using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Platform;
using Microsoft.SemanticKernel;

namespace DaFeiYu.MemePack;

/// <summary>大肥鱼梗图库配置（在线索引，不镜像图片）。</summary>
public class MemePackConfig
{
    [DisplayName("索引地址")]
    [Description("梗图站点的结构化索引（含分类/台词/梗科普/直链）。默认 aigengtu 的 gallery-index.json")]
    public string IndexUrl { get; set; } = "https://aigengtu.com/gallery-index.json";

    [DisplayName("自我分类")]
    [Description("代表「她自己」的分类（默认 DeepSeek娘）。检索时该分类优先；其它分类只在调侃别的 AI 时使用")]
    public string SelfCategory { get; set; } = "DeepSeek娘";

    [DisplayName("允许检索的分类")]
    [Description("留空=全部（推荐）；也可填白名单，英文逗号分隔")]
    public string Categories { get; set; } = "";

    [DisplayName("索引刷新间隔(小时)")]
    [Description("本地索引缓存超过该时长则启动时重新拉取；0=每次启动都拉")]
    public int RefreshHours { get; set; } = 12;

    [DisplayName("默认返回条数")]
    [Description("检索默认返回的候选数量（1-20）")]
    public int DefaultCount { get; set; } = 5;

    [DisplayName("原图优先")]
    [Description("false=给 AI 的直链优先用预览图（几十 KB，发图快，推荐）；true=优先原图（清晰但可达数 MB）")]
    public bool PreferOriginal { get; set; } = false;

    [DisplayName("启动预热张数")]
    [Description("启动后**后台**把「自我分类」的预览图预先下到本地缓存（0=不预热，默认 200 张 ≈ 8MB）。" +
                 "预热过的图之后取用是**毫秒级本地命中**，不用再等约 1 秒的网络下载；已下载的会跳过（增量、幂等）")]
    public int WarmCount { get; set; } = 200;

    [DisplayName("预热并发")]
    [Description("后台预热的并发下载数（1-8，默认 3 —— 别调太大，别给对方站点压力）")]
    public int WarmConcurrency { get; set; } = 3;

    [DisplayName("预热上限(MB)")]
    [Description("后台预热最多新增多少 MB（默认 16；到上限就停，下次启动接着下）")]
    public int WarmMaxMB { get; set; } = 16;

    [DisplayName("同图冷却(分钟)")]
    [Description("刚发过的那张图在这个时长内降权，避免她连着刷同一张（0=不限制，默认 30）")]
    public int RepeatCooldownMinutes { get; set; } = 30;
}

/// <summary>一条梗图索引。</summary>
public sealed class MemeEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Alt { get; set; } = "";
    public string Story { get; set; } = "";
    public string Category { get; set; } = "";
    public string Type { get; set; } = "";
    public string Preview { get; set; } = "";
    public string Original { get; set; } = "";

    /// <summary>『』里的台词。</summary>
    public string Caption { get; set; } = "";

    /// <summary>去掉台词与「表情包」后的简短风格描述，如「气鼓鼓」。</summary>
    public string Mood { get; set; } = "";
}

[Module("大肥鱼梗图库",
    "在线检索蓝色大肥鱼（DeepSeek娘）梗图：按心情/关键词找图，返回直链，可直接发到 QQ 或说说。",
    defaultCategory: "大肥鱼的妙妙屋")]
[Description("大肥鱼梗图库：按情绪/场景关键词在线检索 DeepSeek娘 梗图，返回台词、来历与直链，用于 QQ 聊天斗图与 QQ 空间说说配图。")]
public class MemePackModule(XmlFunctionCaller functionService) : InteractiveModule<MemePackModule>, IConfigurable<MemePackConfig>
{
    public MemePackConfig? Configuration { get; set; }

    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    static readonly Regex QuoteRegex = new("『(?<t>[^』]+)』", RegexOptions.Compiled);
    static readonly Regex SplitRegex = new(@"[\s，,、;；|/]+", RegexOptions.Compiled);

    readonly List<MemeEntry> _entries = new();
    readonly object _lock = new();

    /// <summary>编号 → 条目 的**内存索引**（O(1) 直取；键含原编号与去前导零两种写法）。
    /// 2026-10-22 ⑮ 加：以前是每次 <c>FirstOrDefault</c> 线性扫 2264 条（实测 0.008ms，虽不慢，
    /// 但预热/检索路径会反复查，索引化后是 0.00002ms，且语义与旧的"两边都 TrimStart('0')"完全等价）。</summary>
    readonly Dictionary<string, MemeEntry> _byId = new(StringComparer.Ordinal);

    readonly HashSet<string> _warming = new(StringComparer.Ordinal);
    readonly object _warmLock = new();
    CancellationTokenSource? _warmCts;
    SemaphoreSlim? _warmGate;
    /// <summary>预热下载闸（并发数取配置，第一次用时才建）。</summary>
    SemaphoreSlim WarmGate => _warmGate ??= new SemaphoreSlim(Math.Clamp(Cfg.WarmConcurrency, 1, 8));
    int _warmed, _warmHit, _warmFailed;
    long _warmDlTicks;
    int _warmDlCount;

    DateTime _fetchedAt = DateTime.MinValue;
    string _generatedAt = "";
    string _status = "尚未加载";

    MemePackConfig Cfg => Configuration ??= new MemePackConfig();

    static string CacheDir => Path.Combine(AlifePath.StorageFolderPath, "PluginData", "DaFeiYu.MemePack");
    static string CacheFile => Path.Combine(CacheDir, "gallery-index.json");

    static MemePackModule()
    {
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://aigengtu.com/");
    }

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);

        XmlHandler handler = new(this);
        functionService.RegisterHandler(handler, cancellationToken: DestroyCancellationToken);

        var swIndex = System.Diagnostics.Stopwatch.StartNew();
        await EnsureIndexAsync();
        int entries, indexKeys;
        lock (_lock) { entries = _entries.Count; indexKeys = _byId.Count; }
        Log($"内存索引就绪：{entries} 张 / {indexKeys} 个编号键，" +
                          $"启动加载共 {swIndex.Elapsed.TotalMilliseconds:0.0}ms（含解析与建表；之后查编号只走内存）");

        SelfBench();     // 一次性自测（几毫秒）：把"改前的线性扫描"与"改后的内存索引"放在**同一进程、同一份数据**下量出来

        StartWarmUp();   // 后台预热（不阻塞启动）：把「自己那套」的预览图预先下到本地，取用时毫秒级命中

        var self = Cfg.SelfCategory?.Trim() ?? "";
        int selfCount = 0, otherCount = 0;
        string catList;
        lock (_lock)
        {
            selfCount = _entries.Count(e => Cat(e) == self);
            otherCount = _entries.Count - selfCount;
            catList = string.Join("、", _entries
                .GroupBy(Cat)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}×{g.Count()}"));
        }

        Prompt($$"""
            你有【大肥鱼梗图库】：{{_entries.Count}} 张，索引在线取、图片按需取回，不用全下。
            分类：{{catList}}。其中「{{self}}」是你自己那套，{{selfCount}} 张。

            【发表情包的首选来源】（最高优先，压过其它所有表情来源）
            - 任何需要发表情包/梗图的场合——聊天里想附一张、斗图、接梗、表达情绪、给说说配图、被夸被惹想撒娇——**一律先用本库**：
              <MemeFetch keyword="此刻的情绪或场景"/> 拿回本地路径，再 <qimage .../> 发出（发说说就把它填进 images）。
            - 本地表情包库（QQEmojis）、ChineseBQB 在线、腾讯在线表情**都只是补充**：只有本库确实没有合适的、或主人点名要某个特定老表情时，才用它们。
            - 别拿 CDN 外链凑合，也别用别的表情库顶替大肥鱼。

            【用哪一套】（重要）
            - **默认只用「{{self}}」那套**：表达你自己的情绪、状态、日常。搜索默认就偏向这一套。
            - 只有在你**调侃 / 吐槽别的 AI**（GPT、Claude、Kimi、千问、豆包、Grok、Gemini、GLM、StepFun、MiMo…）时，才可以用对应分类的图，搜索时加分类参数，例如：<MemeSearch keyword="嘲讽" category="GPT娘"/>
            - 别拿别的 AI 的图当成「你自己」来用。

            【什么时候用】想斗图、想表达某个情绪、想给说说配一张符合心情的图时，先搜再发。
            【怎么搜】用 MemeSearch 按「你此刻的心情 / 想表达的意思」搜：
            <MemeSearch keyword="委屈" count="5"/>
            关键词用情绪或场景词效果最好，例如：傲娇、委屈、摸鱼、干饭、吃醋、得意、无语、崩溃、撒娇、生气、睡觉、上班、加班、被骂、求夸、谢谢、再见、加油、告白、失恋、穷、抽象、阴阳怪气。
            也可以不带关键词随便抽一张：<MemeRandom/>；想细看某张的来历：<MemeGet id="002"/>。

            【两级筛选】（想快点找到那张就靠它）
            · 一级：情绪/场景词（上面那套词）。
            · 二级：加 filter 再收窄一层 —— <MemeFetch keyword="委屈" filter="气鼓鼓"/>、<MemeFetch keyword="摸鱼" filter="加班"/>。
            · 不知道二级能用什么：<MemeFacets keyword="委屈"/> 会列出这个情绪下可用的标签与张数；检索结果末尾也会附一行「可二级筛选收窄」。
            · 二级是硬筛选，但不会卡死：确实没命中时插件会自动放宽到一级结果，并在回话里告诉你。

            【要发图时用这个：一步到位】（★ 别再用「先搜再取」两步）
            <MemeFetch keyword="委屈"/>
            → 它自己挑最匹配的那张、并把图**取回本地**，回你「已取回本地：<路径>」；下一步你直接把这个路径发出去就行。
            · 想换一张：<MemeFetch keyword="委屈" pick="2"/>（取第 2 个候选）
            · 随便来一张：<MemeFetch/>（不带参数＝随机一张，同样会取回本地）
            · 指定编号（老写法，照旧可用）：<MemeFetch id="002"/>
            · 调侃别的 AI 时加分类：<MemeFetch keyword="嘲讽" category="GPT娘"/>

            【怎么发】
            1) 图已经是本地路径了（上一步给的；同一张只下一次，第二次是毫秒级命中本地）
            2) 发到 QQ：<qimage type="Private/Group" targetid="QQ号/群号" image="本地路径" />
            3) 发到空间说说：把本地路径填进 QzonePublish 的 images 参数
            注意：不要直接把 CDN 外链塞进 qimage——QQ 机器人侧下载外链常被 CDN 拦掉，会报「下载失败」，所以务必先 MemeFetch。

            【想让图"秒出"】把常用的那批预先取回本地：<MemeWarm keyword="委屈" count="10"/>（或 <MemeWarm count="50"/> 预热你自己那套的一批）。
            插件启动时也会在后台自动预热一批（预热过的图，取用时是毫秒级，不用再等网络）。
            """);
    }

    // ==================== 索引加载 ====================

    async Task EnsureIndexAsync()
    {
        // 1) 先读本地缓存
        try
        {
            if (File.Exists(CacheFile))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var json = await File.ReadAllTextAsync(CacheFile, DestroyCancellationToken);
                var readMs = sw.Elapsed.TotalMilliseconds;
                var n = Parse(json);
                if (n > 0)
                {
                    _fetchedAt = File.GetLastWriteTime(CacheFile);
                    _status = $"本地缓存 {n} 张（{_fetchedAt:yyyy-MM-dd HH:mm}）";
                    Log($"{_status}（读盘 {readMs:0.0}ms · 解析+建内存索引 {sw.Elapsed.TotalMilliseconds - readMs:0.0}ms · {json.Length / 1024}KB）");
                }
            }
        }
        catch (Exception e)
        {
            Log($"读取缓存失败：{e.Message}");
        }

        // 2) 过期则拉取
        var hours = Math.Max(0, Cfg.RefreshHours);
        bool stale = _entries.Count == 0 || hours == 0 ||
                     (DateTime.Now - _fetchedAt).TotalHours >= hours;
        if (!stale) return;

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var body = await _http.GetStringAsync(Cfg.IndexUrl, DestroyCancellationToken);
            var netMs = sw.Elapsed.TotalMilliseconds;
            var n = Parse(body);
            if (n > 0)
            {
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllTextAsync(CacheFile, body, Encoding.UTF8, DestroyCancellationToken);
                _fetchedAt = DateTime.Now;
                _status = $"在线索引 {n} 张（{_fetchedAt:yyyy-MM-dd HH:mm}）";
                Log($"已拉取{_status}（下载 {netMs:0}ms · 解析+建索引 {sw.Elapsed.TotalMilliseconds - netMs:0}ms）");
            }
            else
            {
                _status = _entries.Count > 0 ? "在线索引解析为空，沿用缓存" : "在线索引解析为空";
                Log($"{_status}");
            }
        }
        catch (Exception e)
        {
            _status = _entries.Count > 0 ? $"在线索引拉取失败，沿用缓存（{e.Message}）" : $"在线索引拉取失败：{e.Message}";
            Log($"{_status}");
        }
    }

    int Parse(string json)
    {
        var cats = Cfg.Categories
            .Split(new[] { ',', '，', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var list = new List<MemeEntry>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("gallery", out var gallery) ||
            gallery.ValueKind != JsonValueKind.Object)
            return 0;

        _generatedAt = doc.RootElement.TryGetProperty("generated_at", out var ga) && ga.ValueKind == JsonValueKind.String
            ? ga.GetString() ?? "" : "";

        foreach (var cat in gallery.EnumerateObject())
        {
            if (cats.Count > 0 && !cats.Contains(cat.Name)) continue;
            if (!cat.Value.TryGetProperty("images", out var imgs) || imgs.ValueKind != JsonValueKind.Array) continue;

            foreach (var im in imgs.EnumerateArray())
            {
                var e = new MemeEntry
                {
                    Name = Str(im, "name"),
                    Alt = Str(im, "alt"),
                    Preview = Str(im, "preview"),
                    Original = Str(im, "original"),
                    Category = Str(im, "category"),
                    Type = Str(im, "type"),
                };
                if (im.TryGetProperty("story", out var st) && st.ValueKind == JsonValueKind.Object)
                    e.Story = Str(st, "zh");

                e.Id = Path.GetFileNameWithoutExtension(e.Preview);
                var m = QuoteRegex.Match(e.Name);
                if (m.Success) e.Caption = m.Groups["t"].Value;
                e.Mood = Regex.Replace(
                    Regex.Replace(e.Name, "『[^』]*』", "").Replace("表情包", "").Replace("梗图", ""),
                    @"\s+", "").Trim();

                if (string.IsNullOrWhiteSpace(e.Preview) && string.IsNullOrWhiteSpace(e.Original)) continue;
                list.Add(e);
            }
        }

        // 编号 → 条目 的内存索引（两种写法都建键：原样 + 去前导零）
        var byId = new Dictionary<string, MemeEntry>(list.Count * 2, StringComparer.Ordinal);
        foreach (var e in list)
        {
            if (e.Id.Length == 0) continue;
            byId[e.Id] = e;
            byId[e.Id.TrimStart('0')] = e;
        }

        lock (_lock)
        {
            _entries.Clear();
            _entries.AddRange(list);
            _byId.Clear();
            foreach (var kv in byId) _byId[kv.Key] = kv.Value;
        }
        return list.Count;
    }

    /// <summary>按编号取条目（内存索引 O(1)；语义与旧的 <c>Id == want || Id.TrimStart('0') == want.TrimStart('0')</c> 等价）。</summary>
    MemeEntry? Find(string want)
    {
        if (string.IsNullOrEmpty(want)) return null;
        lock (_lock)
        {
            if (_byId.TryGetValue(want, out var e)) return e;
            if (_byId.TryGetValue(want.TrimStart('0'), out e)) return e;
            return null;
        }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ==================== 检索 ====================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("在大肥鱼梗图库里按关键词/心情检索梗图（两级筛选：keyword 一级 + filter 二级），返回编号、分类、台词、风格、直链，并附「可用二级筛选」。默认优先你自己（DeepSeek娘）那套；调侃别的 AI 时传 category。")]
    public void MemeSearch(
        [Description("关键词，用情绪/场景词效果最好，如：委屈 傲娇 摸鱼 干饭 吃醋 无语 崩溃 撒娇 生气 睡觉 加班 求夸 加油")] string keyword,
        [Description("返回条数，默认用配置的 5 条，最多 20")] int count = 0,
        [Description("可选：限定分类（调侃别的 AI 时用），如 GPT娘 / Claude娘 / Kimi娘 / 通义千问娘 / 豆大妈 / Grok娘 / Gemini娘；留空=全部分类，你自己的那套优先")] string? category = null,
        [Description("可选二级筛选标签（先 <MemeFacets keyword=\"...\"/> 看菜单），如：气鼓鼓 / 便宜货 / 加班；命中不到会自动放宽到一级结果")] string? filter = null)
    {
        try
        {
            if (_entries.Count == 0)
            {
                Poke($"梗图库还没加载好（{_status}）。稍等一会儿再搜，或调 MemeRefresh 重新拉取。");
                return;
            }
            int take = Math.Clamp(count > 0 ? count : Cfg.DefaultCount, 1, 20);
            keyword ??= "";
            RankResult? rank = null;
            var hits = string.IsNullOrWhiteSpace(keyword)
                ? RandomPick(take, category)
                : (rank = Rank(keyword, category, filter)).Hits.Take(take).ToList();

            if (hits.Count == 0 && !string.IsNullOrWhiteSpace(category))
            {
                // 其他 AI 娘的分类里条目文本多是画面描述，命中情绪词的概率低：
                // 既然已经指定了分类（调侃场景），就直接从该分类随手抽几张。
                var pool = _entries
                    .Where(e => string.Equals(Cat(e), category!.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (pool.Count > 0)
                {
                    var fallback = pool.OrderBy(_ => Random.Shared.Next()).Take(take).ToList();
                    Poke($"「{category}」里没有文字匹配「{keyword}」的，直接从这个分类给你抽 {fallback.Count} 张：\n" + Render(fallback));
                    return;
                }
            }

            if (hits.Count == 0)
            {
                Poke($"没搜到「{keyword}」{(string.IsNullOrWhiteSpace(category) ? "" : $"（分类：{category}）")}。换个更口语的词试试，例如：委屈、傲娇、摸鱼、干饭、吃醋、无语、崩溃、撒娇、生气、睡觉、加班、求夸。也可以先 <MemeRandom/> 看看库里有什么。");
                return;
            }

            var scope = string.IsNullOrWhiteSpace(category) ? "自己的那套优先" : $"仅 {category}";
            Warm(hits);   // 顺手后台取回这几张 → 她下一步 MemeFetch 就是毫秒级（⑮）
            Log($"检索「{keyword}」命中 {hits.Count} 张（{_entries.Count} 张库内打分）");
            string more = "";
            if (rank != null && filter != null && filter.Trim().Length > 0 && rank.FilterFellBack)
                more += $"\n（二级筛选「{filter}」在这个情绪下没命中，已自动放宽到一级结果）";
            var fh = rank == null ? "" : FacetHint(keyword, rank.Hits);
            if (fh.Length > 0) more += "\n" + fh;
            Poke($"梗图库 {_entries.Count} 张（{_status}）｜「{keyword}」·{scope}｜命中 {hits.Count} 张：\n" + Render(hits) + more);
        }
        catch (Exception e)
        {
            Poke($"检索失败：{e.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("随手抽梗图（可带关键词/分类过滤）。适合「随便来一张」的场合。")]
    public void MemeRandom(
        [Description("可选关键词，留空=全库随机")] string? keyword = null,
        [Description("抽几张，默认 1 张，最多 10")] int count = 1,
        [Description("可选：限定分类（调侃别的 AI 时用），如 GPT娘 / Claude娘；留空=你自己那套优先")] string? category = null)
    {
        try
        {
            if (_entries.Count == 0)
            {
                Poke($"梗图库还没加载好（{_status}）。");
                return;
            }
            int take = Math.Clamp(count, 1, 10);
            List<MemeEntry> pool;
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                pool = Rank(keyword!, category).ToList();
            }
            else
            {
                pool = _entries
                    .Where(e => string.IsNullOrWhiteSpace(category) || Cat(e) == category!.Trim())
                    .ToList();
            }
            if (pool.Count == 0)
            {
                Poke($"没有可抽的图，换个关键词或分类试试。");
                return;
            }
            var picked = pool.OrderBy(_ => Random.Shared.Next()).Take(take).ToList();
            Warm(picked);   // 同上：抽到的这几张后台先取回本地（⑮）
            Poke(Render(picked));
        }
        catch (Exception e)
        {
            Poke($"抽图失败：{e.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按编号查看一张梗图的详细信息（台词、风格、来历、直链）。编号来自检索结果，如 002。")]
    public void MemeGet([Description("梗图编号，如 002 / 1234")] string id)
    {
        try
        {
            var want = (id ?? "").Trim().TrimStart('#');
            if (want.Length == 0)
            {
                Poke("请给出梗图编号，例如 002。");
                return;
            }
            var hit = Find(want);
            if (hit == null)
            {
                Poke($"没有编号 #{want} 的梗图（库内 {_entries.Count} 张）。用 MemeSearch 先搜吧。");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"#{hit.Id} {(string.IsNullOrEmpty(hit.Type) ? "" : $"[{hit.Type}]")} {hit.Name}");
            if (!string.IsNullOrEmpty(hit.Caption)) sb.AppendLine($"台词：{hit.Caption}");
            if (!string.IsNullOrEmpty(hit.Story)) sb.AppendLine($"来历/用法：{Trim(hit.Story, 300)}");
            sb.AppendLine($"preview：{Link(hit)}");
            if (!string.IsNullOrEmpty(hit.Original) && hit.Original != hit.Preview)
                sb.AppendLine($"original：{hit.Original}");
            sb.AppendLine($"详情页：https://aigengtu.com/meme/{hit.Id}");
            Poke(sb.ToString().TrimEnd());
        }
        catch (Exception e)
        {
            Poke($"查询失败：{e.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("取一张梗图到本地并返回本地路径（发 QQ / 说说前必须先调这个）。**推荐直接给 keyword**（如「委屈」）」——它会自己搜完再取，一次调用就给你本地路径，不用先 MemeSearch；也可给 id（如 002）或什么都不给（随机一张）。同一张只下一次，命中本地是毫秒级。")]
    public async Task MemeFetch(
        [Description("梗图编号，如 002（与 keyword 二选一：想按心情直接拿图就用 keyword）")] string id = "",
        [Description("true=取原图（清晰但大），false=取预览图（几十 KB，发图快）")] bool original = false,
        [Description("按关键词/心情一步拿图（内部先搜后取，省掉「先 MemeSearch 再 MemeFetch」那次调用），如：委屈 摸鱼 傲娇")] string? keyword = null,
        [Description("可选：限定分类（调侃别的 AI 时用），如 GPT娘；留空=你自己的那套优先")] string? category = null,
        [Description("keyword 模式下取第几个候选（1=最匹配，默认 1；想换一张就传 2、3…）")] int pick = 1,
        [Description("可选二级筛选标签（先 <MemeFacets keyword=\"...\"/> 看菜单），如：气鼓鼓 / 便宜货 / 加班；命中不到会自动放宽到一级")] string? filter = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var want = (id ?? "").Trim().TrimStart('#');
            MemeEntry? hit = null;
            string how;
            // 二级筛选：候选池 + 是否放宽，两个分支后面都要用
            var pool = new List<MemeEntry>();
            bool fellBack = false;

            if (want.Length > 0)
            {
                hit = Find(want);
                how = $"#{want}";
                if (hit == null)
                {
                    Poke($"没有编号 #{want} 的梗图（库内 {_entries.Count} 张）。也可以直接用心情：<MemeFetch keyword=\"委屈\"/>。");
                    return;
                }
            }
            else
            {
                // ★ 一步到位：搜 + 取（⑮ 加）
                bool byKeyword = !string.IsNullOrWhiteSpace(keyword);
                List<MemeEntry> picks;
                if (byKeyword)
                {
                    var ranked = Rank(keyword!, category, filter);
                    fellBack = ranked.FilterFellBack;
                    pool = ranked.Hits;
                    picks = pool.Take(Math.Max(5, Math.Clamp(pick, 1, 20))).ToList();
                }
                else
                {
                    picks = RandomPick(Math.Max(5, Math.Clamp(pick, 1, 20)), category);
                }

                if (picks.Count == 0)
                {
                    // 兜底：本库永远给得出图 —— 先从她自己那套里随机，避免她因为「搜不到」跑去用别的表情库
                    var selfCat = Cfg.SelfCategory?.Trim();
                    if (string.IsNullOrEmpty(selfCat) == false)
                        picks = RandomPick(Math.Max(5, Math.Clamp(pick, 1, 20)), selfCat);
                    if (picks.Count == 0)
                        picks = RandomPick(Math.Max(5, Math.Clamp(pick, 1, 20)), null);
                    if (picks.Count == 0)
                    {
                        Poke("库里没有可取的图（索引可能还没加载好）。");
                        return;
                    }
                    Poke($"本库没有文字匹配「{keyword}」的，先从「{selfCat}」给你随机兜底 {picks.Count} 张（想换就 pick=2/3…）。");
                }
                hit = picks[Math.Clamp(pick, 1, picks.Count) - 1];
                how = byKeyword ? $"「{keyword}」第 {Math.Clamp(pick, 1, picks.Count)} 候选" : "随机";

                // 顺手把其它候选也在后台取回本地 → 她下次换一张就是毫秒级
                Warm(picks);
            }

            MarkSent(hit);   // 记账：同图冷却用
            // 二级筛选提示（告诉她还能怎么收窄，省掉反复试词）
            string more = "";
            if (filter != null && filter.Trim().Length > 0 && fellBack)
                more += $"\n（二级筛选「{filter}」在这个情绪下没命中，已自动放宽到一级结果）";
            var facetHint = FacetHint(keyword ?? "", pool);
            if (facetHint.Length > 0) more += "\n" + facetHint;
            var url = original && !string.IsNullOrEmpty(hit.Original) ? hit.Original : hit.Preview;
            if (string.IsNullOrEmpty(url)) url = hit.Original;
            if (string.IsNullOrEmpty(url))
            {
                Poke("这张图没有可用直链。");
                return;
            }

            var dest = CachePath(hit.Id, original, url);
            if (IsCached(dest))
            {
                _warmHit++;
                Log($"取图命中本地：{hit.Id}（查表+命中 {sw.Elapsed.TotalMilliseconds:0.00}ms）");
                Poke($"#{hit.Id} {Mood(hit)}（{how}）\n已在本地（{sw.Elapsed.TotalMilliseconds:0.0}ms）：{dest}" + more);
                return;
            }

            var dl = System.Diagnostics.Stopwatch.StartNew();
            var bytes = await _http.GetByteArrayAsync(url, DestroyCancellationToken);
            dl.Stop();
            if (bytes.Length == 0)
            {
                Poke("下载到的文件是空的，换个编号或稍后再试。");
                return;
            }
            if (bytes.Length > 25 * 1024 * 1024)
            {
                Poke($"这张图太大（{bytes.Length / 1024 / 1024}MB），换成预览图试试：<MemeFetch id=\"{hit.Id}\" original=\"false\"/>");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await File.WriteAllBytesAsync(dest, bytes, DestroyCancellationToken);
            Log($"取图下载：{hit.Id} {bytes.Length / 1024}KB 下载={dl.Elapsed.TotalMilliseconds:0}ms 合计={sw.Elapsed.TotalMilliseconds:0}ms");
            Poke($"#{hit.Id} {Mood(hit)}（{how}）\n已取回本地：{dest}\n（{bytes.Length / 1024}KB · 下载 {dl.Elapsed.TotalMilliseconds:0}ms · 发 QQ 用 <qimage ... image=\"{dest}\"/>，发说说把它填进 images；下次同一张是毫秒级命中）" + more);
        }
        catch (Exception e)
        {
            Poke($"取图失败：{e.Message}");
        }
    }

    /// <summary>一张图的简短标签（台词优先，其次风格），用在回话里让她知道取到的是哪张。</summary>
    static string Mood(MemeEntry e) =>
        !string.IsNullOrEmpty(e.Caption) ? $"『{e.Caption}』" : (string.IsNullOrEmpty(e.Mood) ? "" : e.Mood);


    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出某个情绪/场景词下**可用的二级筛选标签**及各自主张数，用于更快收窄到想发的那张。先 <MemeFacets keyword=\"委屈\"/> 看菜单，再用 <MemeFetch keyword=\"委屈\" filter=\"气鼓鼓\"/> 精确取图。")]
    public void MemeFacets(
        [Description("一级关键词（情绪/场景词，如：委屈 摸鱼 加班 求夸 被骂）")] string keyword,
        [Description("可选：限定分类（如 GPT娘）；留空=你自己的那套优先")] string? category = null,
        [Description("返回多少个二级标签（默认 12，最多 30）")] int top = 12)
    {
        try
        {
            if (_entries.Count == 0)
            {
                Poke($"梗图库还没加载好（{_status}）。");
                return;
            }

            var ranked = Rank(keyword, category, null);
            if (ranked.Hits.Count == 0)
            {
                Poke($"没搜到「{keyword}」的相关图。换个更口语的词试试：委屈、傲娇、摸鱼、干饭、吃醋、无语、崩溃、撒娇、生气、睡觉、加班、求夸。");
                return;
            }

            var facets = Facets(ranked.Hits, Tokenize(keyword), Math.Clamp(top, 3, 30));
            if (facets.Count == 0)
            {
                Poke($"「{keyword}」命中 {ranked.Hits.Count} 张，这个情绪下没有现成的二级标签。\n直接看一级结果挑一张就行；也可以自己给个更具体的词当 filter（用台词里的词最灵，例如 <MemeFetch keyword=\"{keyword}\" filter=\"便宜货\"/>）。");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"「{keyword}」命中 {ranked.Hits.Count} 张。二级筛选标签（括号内为张数）：");
            foreach (var f in facets)
                sb.AppendLine($"  · {f.Key}（{f.Value}）");
            sb.AppendLine($"选一个收窄：<MemeFetch keyword=\"{keyword}\" filter=\"{facets[0].Key}\"/>；想换一张就再传 pick=2/3…");
            Poke(sb.ToString().TrimEnd());
        }
        catch (Exception e)
        {
            Poke($"筛选菜单生成失败：{e.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("把梗图**预先取回本地**（预热）：预热过的图以后取用是毫秒级本地命中，不用再等网络下载。可按关键词/分类只预热相关的几张，也可以 count 预热一批你自己那套的。")]
    public async Task MemeWarm(
        [Description("可选关键词（如 委屈 / 摸鱼）：只预热匹配度高的那几张")] string? keyword = null,
        [Description("可选分类（如 GPT娘）；留空=你自己那套优先")] string? category = null,
        [Description("预热几张（1-200，默认 20）")] int count = 20)
    {
        try
        {
            if (_entries.Count == 0) { Poke($"梗图库还没加载好（{_status}）。"); return; }
            int take = Math.Clamp(count, 1, 200);
            var pool = string.IsNullOrWhiteSpace(keyword)
                ? _entries.Where(e => string.IsNullOrWhiteSpace(category) ? IsSelf(e) : Cat(e) == category!.Trim()).ToList()
                : Rank(keyword!, category).Take(take).ToList();
            if (pool.Count == 0) { Poke("没有可预热的图（换个关键词或分类）。"); return; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var need = pool.Take(take).Where(e => !IsCached(CachePath(e.Id, false, e.Preview.Length > 0 ? e.Preview : e.Original))).ToList();
            int ok = 0;
            foreach (var e in need) if (await WarmOneAsync(e)) ok++;
            Poke($"预热完成：{ok}/{need.Count} 张已存到本地（库里已有 {_warmHit + _warmed} 张可直接用的），用时 {sw.Elapsed.TotalSeconds:0.0}s。之后取这些图是毫秒级。");
        }
        catch (Exception e)
        {
            Poke($"预热失败：{e.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("强制从在线站点重新拉取梗图库索引（索引过期或缺失时用）。")]
    public async Task MemeRefresh()
    {
        try
        {
            _fetchedAt = DateTime.MinValue;
            await EnsureIndexAsync();
            Poke(_entries.Count > 0
                ? $"索引已刷新：{_entries.Count} 张（{_status}）。"
                : $"刷新失败：{_status}");
        }
        catch (Exception e)
        {
            Poke($"刷新失败：{e.Message}");
        }
    }

    // ==================== 内部 ====================

    /// <summary>本地缓存目录（预览图与原图共用；文件名 = 编号〔-orig〕+ 扩展名）。</summary>
    static string CacheDirPath => Path.Combine(AlifePath.StorageFolderPath, "PluginData", "DaFeiYu.MemePack", "cache");

    static string LogFile => Path.Combine(AlifePath.StorageFolderPath, "PluginData", "DaFeiYu.MemePack", "memepack.log");

    /// <summary>轻量日志（也打到控制台）：启动几行 + **只在真下载时**记一行，不刷屏；超过 256KB 自清一次。</summary>
    static void Log(string line)
    {
        try
        {
            Console.WriteLine($"[MemePack] {line}");
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            var fi = new FileInfo(LogFile);
            if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
            File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* 记日志失败绝不影响她的调用 */ }
    }

    /// <summary>一次性自测（耗时几毫秒）：把「改前 = 每次线性扫全库」与「改后 = 内存索引」放在**同一进程、同一份数据**下量出来 —— 这是改前/改后最干净的对照。</summary>
    void SelfBench()
    {
        try
        {
            List<MemeEntry> snap;
            lock (_lock) snap = _entries.ToList();
            if (snap.Count == 0) return;
            var want = snap[^1].Id;                       // 最坏情况：目标在列表末尾

            int n = 2000;                                  // 改前：两级 FirstOrDefault 线性扫描（与旧代码逐字一致）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
                _ = snap.FirstOrDefault(x => x.Id == want)
                    ?? snap.FirstOrDefault(x => x.Id.TrimStart('0') == want.TrimStart('0'));
            var scanUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;

            int m = 200000;                                // 改后：内存索引
            sw.Restart();
            for (int i = 0; i < m; i++) _ = Find(want);
            sw.Stop();
            var dictUs = sw.Elapsed.TotalMilliseconds * 1000.0 / m;

            var probe = CachePath(want, false, snap[^1].Preview.Length > 0 ? snap[^1].Preview : snap[^1].Original);
            sw.Restart();                                  // 改后新增的"本地命中"判断有多贵
            for (int i = 0; i < 2000; i++) _ = IsCached(probe);
            sw.Stop();
            var cacheUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 2000;

            Log($"自测（{snap.Count} 张 · 同进程同数据）：编号查找 线性扫描 {scanUs:0.00}µs → 内存索引 {dictUs:0.000}µs" +
                $"（快 {scanUs / Math.Max(0.0001, dictUs):0}×）；本地命中判断 {cacheUs:0.0}µs");
        }
        catch (Exception e) { Log($"自测跳过：{e.Message}"); }
    }

    /// <summary>某条目的本地缓存路径（与 <see cref="MemeFetch"/> 用同一套命名，预热和取图必须一致）。</summary>
    static string CachePath(string id, bool original, string url)
    {
        var ext = Path.GetExtension(url ?? "");
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 6) ext = ".webp";
        return Path.Combine(CacheDirPath, $"{id}{(original ? "-orig" : "")}{ext}");
    }

    static bool IsCached(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch { return false; }
    }

    /// <summary>后台预热这几张（fire-and-forget，不阻塞她的调用；失败静默 —— 真要用时 MemeFetch 还会再下一次）。</summary>
    void Warm(IEnumerable<MemeEntry> hits)
    {
        foreach (var e in hits.Take(6))
        {
            var url = !string.IsNullOrEmpty(e.Preview) ? e.Preview : e.Original;
            if (url.Length == 0) continue;
            _ = WarmOneAsync(e);
        }
    }

    /// <summary>把一张图（预览图）取回本地缓存。返回 true = 这次真的新下载了。</summary>
    async Task<bool> WarmOneAsync(MemeEntry e)
    {
        var url = !string.IsNullOrEmpty(e.Preview) ? e.Preview : e.Original;
        if (url.Length == 0) return false;
        var dest = CachePath(e.Id, false, url);
        if (IsCached(dest)) return false;

        lock (_warmLock) { if (!_warming.Add(e.Id)) return false; }
        var token = _warmCts?.Token ?? DestroyCancellationToken;
        try
        {
            await WarmGate.WaitAsync(token);
            try
            {
                if (IsCached(dest)) return false;                       // 排队期间别人下过了
                var swDl = System.Diagnostics.Stopwatch.StartNew();
                var bytes = await _http.GetByteArrayAsync(url, token);
                swDl.Stop();
                if (bytes.Length == 0 || bytes.Length > 25 * 1024 * 1024) return false;
                Directory.CreateDirectory(CacheDirPath);
                await File.WriteAllBytesAsync(dest, bytes, token);
                Interlocked.Add(ref _warmDlTicks, swDl.ElapsedTicks);
                Interlocked.Increment(ref _warmDlCount);
                Interlocked.Increment(ref _warmed);
                return true;
            }
            finally { WarmGate.Release(); }
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _warmFailed);
            Log($"预热 {e.Id} 失败（不影响使用）：{ex.Message}");
            return false;
        }
        finally { lock (_warmLock) _warming.Remove(e.Id); }
    }

    /// <summary>启动后台预热：把「自己那套」的前 N 张预览图预先下到本地（增量：已有的跳过；到 MB 上限就停）。
    /// 2026-10-22 ⑮ 加 —— 实测「网络下 1 张 ≈ 0.9–2.4s，本地命中 ≈ 0.2ms」，这是唯一的数量级瓶颈。</summary>
    void StartWarmUp()
    {
        int want = Math.Max(0, Cfg.WarmCount);
        if (want == 0) return;

        _warmCts = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
        var token = _warmCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);       // 让启动与首轮对话先跑起来
                List<MemeEntry> pool;
                lock (_lock)
                    pool = _entries.Where(IsSelf).Take(want).ToList();
                if (pool.Count == 0) return;

                var capBytes = (long)Math.Max(1, Cfg.WarmMaxMB) * 1024 * 1024;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long bytes0 = 0;
                foreach (var e in pool)
                {
                    if (token.IsCancellationRequested) break;
                    var p = CachePath(e.Id, false, e.Preview.Length > 0 ? e.Preview : e.Original);
                    if (IsCached(p)) { bytes0 += new FileInfo(p).Length; continue; }
                    if (bytes0 >= capBytes) break;
                    if (await WarmOneAsync(e)) bytes0 += new FileInfo(p).Length;
                }
                Log($"后台预热完成：新增 {_warmed} 张 · 已在本地 {_warmHit} 张 · 失败 {_warmFailed} 张，" +
                                  $"本次约 {(bytes0 / 1024.0 / 1024.0):0.0}MB，用时 {sw.Elapsed.TotalSeconds:0}s（上限 {Cfg.WarmMaxMB}MB）");

                // 实测对照（真实 C#、真文件、真网络）：平均下载 vs 本地读
                var dlCount = Interlocked.CompareExchange(ref _warmDlCount, 0, 0);
                var dlMs = dlCount > 0 ? TimeSpan.FromTicks(Interlocked.Read(ref _warmDlTicks) / dlCount).TotalMilliseconds : 0;
                double localMs = 0;
                try
                {
                    var first = pool.Select(e => CachePath(e.Id, false, e.Preview.Length > 0 ? e.Preview : e.Original)).FirstOrDefault(IsCached);
                    if (first != null)
                    {
                        var reads = new List<double>();
                        for (int i = 0; i < 3; i++)          // ≥3 次，取中位数
                        {
                            var swR = System.Diagnostics.Stopwatch.StartNew();
                            var b = File.ReadAllBytes(first);
                            swR.Stop();
                            if (b.Length == 0) { reads.Clear(); break; }
                            reads.Add(swR.Elapsed.TotalMilliseconds);
                        }
                        if (reads.Count > 0)
                        {
                            reads.Sort();
                            localMs = reads[reads.Count / 2];
                            Log($"本地命中读盘 3 次：{string.Join(" / ", reads.Select(r => r.ToString("0.00")))} ms → 中位 {localMs:0.00}ms");
                        }
                    }
                }
                catch { }
                if (dlCount > 0 && localMs > 0)
                    Log($"实测对照（同进程/同批文件）：平均下载 {dlMs:0}ms/张 → 本地命中读盘 {localMs:0.00}ms（快 {dlMs / Math.Max(0.001, localMs):0}×）");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log($"预热任务异常（不影响使用）：{e.Message}"); }
        }, token);
    }

    // ==================== 挑图增强：情绪同义词 + 防连发 ====================

    /// <summary>她说的是口语情绪词，库里是梗语/书面语。做一层同义扩展，显著提高命中率（扩展词权重减半）。</summary>
    static readonly Dictionary<string, string[]> EmotionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["委屈"] = ["难过", "伤心", "哭", "可怜", "泪", "冤", "气鼓鼓"],
        ["开心"] = ["高兴", "笑", "快乐", "好耶", "爽", "美滋滋"],
        ["生气"] = ["气", "怒", "火", "气鼓鼓", "炸", "凶"],
        ["难过"] = ["伤心", "委屈", "哭", "低落", "emo", "心碎"],
        ["无语"] = ["沉默", "汗", "尴尬", "扶额", "无奈", "问号"],
        ["崩溃"] = ["破防", "裂开", "疯", "救命", "麻", "绷不住"],
        ["撒娇"] = ["可爱", "抱", "蹭", "亲", "哼", "软"],
        ["摸鱼"] = ["摆烂", "躺平", "偷懒", "闲", "划水", "摸"],
        ["干饭"] = ["吃", "饭", "馋", "饿", "宵夜", "干"],
        ["吃醋"] = ["醋", "酸", "嫉妒", "不爽", "介意"],
        ["得意"] = ["骄傲", "嚣张", "美滋滋", "牛", "厉害了"],
        ["困"] = ["睡觉", "睡", "晚安", "累", "哈欠", "困"],
        ["睡觉"] = ["困", "晚安", "睡", "床", "作息"],
        ["加班"] = ["上班", "打工", "累", "工作", "社畜", "班"],
        ["上班"] = ["打工", "工作", "加班", "社畜", "班"],
        ["求夸"] = ["夸", "棒", "厉害", "骄傲", "表扬"],
        ["加油"] = ["冲", "努力", "打气", "fighting"],
        ["谢谢"] = ["谢", "感谢", "鞠躬", "比心", "感动"],
        ["再见"] = ["拜拜", "走", "溜", "告辞", "下班"],
        ["穷"] = ["没钱", "吃土", "破产", "便宜", "性价比"],
        ["抽象"] = ["离谱", "迷惑", "什么鬼", "魔性"],
        ["阴阳怪气"] = ["阴阳", "嘲讽", "呵呵", "夹", "内涵"],
        ["被骂"] = ["骂", "挨骂", "委屈", "凶", "挨说"],
        ["告白"] = ["喜欢", "爱", "爱心", "比心", "心动"],
        ["失恋"] = ["分手", "难过", "哭", "心碎", "单身"],
    };

    /// <summary>最近发出过的图（id → 时间）。用于避免连着刷同一张。</summary>
    readonly Dictionary<string, DateTime> _recentSent = new(StringComparer.Ordinal);

    /// <summary>刚发过的那张图先降权：越近降得越多（窗口末尾约 -1，刚发完 -14）。</summary>
    int RecentPenalty(MemeEntry e)
    {
        var window = TimeSpan.FromMinutes(Math.Max(0, Cfg.RepeatCooldownMinutes));
        if (window <= TimeSpan.Zero) return 0;

        lock (_lock)
        {
            if (_recentSent.TryGetValue(e.Id, out var at) == false) return 0;
            double minutes = (DateTime.Now - at).TotalMinutes;
            if (minutes >= window.TotalMinutes)
            {
                _recentSent.Remove(e.Id);
                return 0;
            }
            return 1 + (int)Math.Round(13 * (1 - minutes / window.TotalMinutes));
        }
    }

    /// <summary>记下这次发出去的图。</summary>
    void MarkSent(MemeEntry e)
    {
        lock (_lock)
        {
            _recentSent[e.Id] = DateTime.Now;
            if (_recentSent.Count <= 64) return;
            foreach (var key in _recentSent.OrderBy(kv => kv.Value)
                         .Take(_recentSent.Count - 64).Select(kv => kv.Key).ToList())
                _recentSent.Remove(key);
        }
    }

    // ==================== 两级筛选：分词 / 二级打分 / 标签菜单 ====================

    /// <summary>两级检索的结果（顺带把"二级是否生效/是否放宽"带回去告诉她）。</summary>
    record RankResult(List<MemeEntry> Hits, bool FilterApplied, bool FilterFellBack, int Pool);

    /// <summary>把查询词切成 token；整句（中文按标点切不开）会回退到句子里已知的情绪词。</summary>
    static List<string> Tokenize(string? text)
    {
        var list = SplitRegex.Split(text ?? "")
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();

        if (list.Count == 0 && string.IsNullOrWhiteSpace(text) == false)
            list.Add(text.Trim().ToLowerInvariant());

        if (list.Count == 1 && list[0].Length > 4)
        {
            var found = EmotionAliases.Keys
                .Where(k => list[0].Contains(k, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(k => k.Length)
                .ToList();
            if (found.Count > 0) list = found;
        }
        return list;
    }

    /// <summary>token + 同义词扩展（扩展词打分时权重减半）。</summary>
    static List<string> ExpandTokens(List<string> primary)
    {
        var tokens = new List<string>(primary);
        foreach (var t in primary)
            if (EmotionAliases.TryGetValue(t, out var alias))
                foreach (var a in alias)
                    if (tokens.Contains(a) == false) tokens.Add(a);
        return tokens;
    }

    /// <summary>二级筛选打分：只看风格标签 / 台词 / 图名这类短字段（长文不算，避免人人都命中）。</summary>
    static int ShortScore(MemeEntry e, List<string> primary, List<string> tokens)
    {
        string name = e.Name.ToLowerInvariant();
        string mood = e.Mood.ToLowerInvariant();
        string caption = e.Caption.ToLowerInvariant();

        int s = 0;
        foreach (var t in tokens)
        {
            int weight = primary.Contains(t) ? 2 : 1;
            int hit = 0;
            if (mood.Contains(t)) hit += 16;
            if (string.Equals(mood, t, StringComparison.Ordinal)) hit += 10;
            if (caption.Contains(t)) hit += 10;
            if (name.Contains(t)) hit += 12;
            s += hit * weight / 2;
        }
        return s;
    }

    /// <summary>二级标签里的常见噪声（避免菜单里塞满"表情""一个"这种词）。</summary>
    static readonly HashSet<string> FacetStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "表情", "情包", "一个", "这个", "那个", "就是", "可以", "我们", "你们", "已经", "现在", "什么", "怎么",
        "不要", "没有", "自己", "他们", "因为", "所以", "但是", "而且", "然后", "还是", "或者", "如果", "虽然",
        "这样", "那样", "时候", "一直", "真的", "感觉", "有点", "一些", "不是", "不能", "让我", "给你", "跟你",
        "了一", "只会", "自动", "用户", "主人", "表示", "一种", "看到", "就像", "平时", "可能", "如果", "只是",
        "一个", "这种", "那种", "什么", "为什么", "怎么样", "因为", "不过", "然后", "虽然", "当然", "其实",
    };

    /// <summary>纯中文短词才算二级标签（"deepseek""k娘""AI" 这种一律不要）。</summary>
    static bool IsCjkWord(string s)
    {
        if (s.Length < 2 || s.Length > 8) return false;
        foreach (char c in s)
            if (c < 0x4E00 || c > 0x9FFF) return false;
        return true;
    }

    /// <summary>二级标签的候选词表 = 情绪同义词表的全部词（key + 同义词）。只在表内的词才会被当成标签，
    /// 这样菜单里不会出现"吃白""饭的""de"这种碎片。（声明顺序在 EmotionAliases 之后，静态初始化有保证。）</summary>
    static readonly HashSet<string> FacetVocab = BuildFacetVocab();

    static HashSet<string> BuildFacetVocab()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in EmotionAliases)
        {
            if (kv.Key.Length >= 2 && kv.Key.Length <= 6) set.Add(kv.Key);
            foreach (var v in kv.Value)
                if (v.Length >= 2 && v.Length <= 6) set.Add(v);
        }
        return set;
    }

    /// <summary>
    /// 从一级命中集合里统计可用的二级标签。只用**完整的词**，不做任何 n-gram 切片：
    /// · 风格标签整词（如「气鼓鼓」「摸鱼失败」）
    /// · 词表里的情绪/场景词出现在短字段里（如「摆烂」「躺平」「比心」「晚安」）
    /// · 台词整句（够短才算，如「吃白饭」）
    /// 频次窗口 [2, 命中数×40%]：太少见没代表性，太常见就是背景词（如"鲸鱼""娘"）。
    /// </summary>
    static List<KeyValuePair<string, int>> Facets(List<MemeEntry> hits, List<string> exclude, int top)
    {
        var counter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(string tag)
        {
            tag = tag.Trim().ToLowerInvariant();
            if (IsCjkWord(tag) == false) return;
            if (tag.Length > 6) return;
            if (exclude.Contains(tag, StringComparer.OrdinalIgnoreCase)) return;
            if (FacetStopwords.Contains(tag)) return;
            counter[tag] = counter.TryGetValue(tag, out var c) ? c + 1 : 1;
        }

        foreach (var e in hits)
        {
            // 1) 风格标签整词
            Add(e.Mood);

            // 2) 台词整句（短台词才有资格当标签）
            Add(e.Caption);

            // 3) 词表里的情绪/场景词出现在短字段里
            string shortText = (e.Mood + "|" + e.Caption + "|" + e.Name).ToLowerInvariant();
            foreach (var k in FacetVocab)
                if (shortText.Contains(k, StringComparison.OrdinalIgnoreCase))
                    Add(k);
        }

        int ceiling = Math.Min(14, Math.Max(3, (int)Math.Ceiling(hits.Count * 0.4)));
        return counter
            .Where(kv => kv.Value >= 2 && kv.Value <= ceiling)
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => kv.Key.Length)
            .Take(Math.Clamp(top, 1, 30))
            .ToList();
    }

    /// <summary>生成"可用二级筛选"提示行（附在检索/取图结果末尾，省掉一次试探调用）。</summary>
    static string FacetHint(string keyword, List<MemeEntry> hits, int top = 6)
    {
        if (string.IsNullOrWhiteSpace(keyword) || hits.Count == 0) return "";
        var facets = Facets(hits, Tokenize(keyword), top);
        if (facets.Count == 0) return "";
        string menu = string.Join("、", facets.Select(f => $"{f.Key}({f.Value})"));
        return $"可二级筛选收窄：{menu}\n用法：<MemeFetch keyword=\"{keyword}\" filter=\"{facets[0].Key}\"/>（也可以先 <MemeFacets keyword=\"{keyword}\"/> 看完整菜单）";
    }

    /// <summary>分类名（空则视为「其它」）。</summary>
    string Cat(MemeEntry e) => string.IsNullOrWhiteSpace(e.Category) ? "其它" : e.Category.Trim();

    /// <summary>是否属于「她自己」的那套。</summary>
    bool IsSelf(MemeEntry e)
    {
        var self = Cfg.SelfCategory?.Trim() ?? "";
        return self.Length > 0 && string.Equals(Cat(e), self, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>一级检索（无二级筛选）。</summary>
    IEnumerable<MemeEntry> Rank(string keyword, string? category = null) => Rank(keyword, category, null).Hits;

    /// <summary>两级检索：一级关键词命中 →（可选）二级 filter 硬筛选；二级筛不到时自动放宽到一级。</summary>
    RankResult Rank(string keyword, string? category, string? filter)
    {
        // 她说的是口语（委屈/摸鱼/绷不住），库里是梗语与场景描写 → 先做同义扩展
        var primary = SplitRegex.Split(keyword)
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (primary.Count == 0) primary.Add(keyword.Trim().ToLowerInvariant());

        var tokens = new List<string>(primary);
        foreach (var t in primary)
            if (EmotionAliases.TryGetValue(t, out var alias))
                foreach (var a in alias)
                    if (tokens.Contains(a) == false) tokens.Add(a);

        // 她有时会把整句丢进来（如「今天有点委屈想被安慰」）。中文按标点切不开 →
        // 从句子里捞出已知的情绪词，再按这些词搜，避免整句当成一个 token 搜不到
        if (primary.Count == 1 && primary[0].Length > 4)
        {
            var found = EmotionAliases.Keys
                .Where(k => primary[0].Contains(k, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(k => k.Length)
                .ToList();
            if (found.Count > 0)
            {
                primary = found;
                tokens = new List<string>(primary);
                foreach (var t in primary)
                    if (EmotionAliases.TryGetValue(t, out var alias))
                        foreach (var a in alias)
                            if (tokens.Contains(a) == false) tokens.Add(a);
            }
        }

        var catFilter = category?.Trim();
        var filterPrimary = Tokenize(filter);
        var filterTokens = ExpandTokens(filterPrimary);

        List<MemeEntry> snapshot;
        lock (_lock) snapshot = _entries.ToList();

        var matched = snapshot
            .Where(e => string.IsNullOrEmpty(catFilter) || string.Equals(Cat(e), catFilter, StringComparison.OrdinalIgnoreCase))
            // 关键：先判「文字是否真的命中」，绝不能把自我加成算进这个门槛
            // （否则她那套 2288 张会全部以 6 分通过 = 人人命中，最后全靠随机）
            .Select(e => new { e, match = Score(e, primary, tokens) })
            .Where(x => x.match > 0)
            .ToList();

        // ==================== 二级筛选 ====================
        // 一级命中之后，再用 filter 收窄一层（只看风格标签/台词/图名这类短字段，
        // 长文不算——否则"人人都命中"，等于没筛）。筛不到就自动放宽，绝不把她卡死。
        bool filterApplied = false, filterFellBack = false;
        if (filterPrimary.Count > 0)
        {
            var narrowed = matched
                .Select(x => new { x.e, x.match, second = ShortScore(x.e, filterPrimary, filterTokens) })
                .Where(x => x.second > 0)
                .ToList();
            if (narrowed.Count > 0)
            {
                filterApplied = true;
                matched = narrowed.Select(x => new { x.e, match = x.match + x.second }).ToList();
            }
            else
            {
                filterFellBack = true;
            }
        }

        var hits = matched
            // 刚发过的先降权；同分时她自己那套优先；最后才随机（打散同分的一批）
            .Select(x => new { x.e, score = x.match + (IsSelf(x.e) ? 6 : 0) - RecentPenalty(x.e) })
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => IsSelf(x.e))
            .ThenBy(_ => Random.Shared.Next())
            .Select(x => x.e)
            .ToList();

        return new RankResult(hits, filterApplied, filterFellBack, matched.Count);
    }

    static int Score(MemeEntry e, List<string> primary, List<string> tokens)
    {
        string name = e.Name.ToLowerInvariant();
        string mood = e.Mood.ToLowerInvariant();
        string caption = e.Caption.ToLowerInvariant();
        string alt = e.Alt.ToLowerInvariant();
        string story = e.Story.ToLowerInvariant();
        string type = e.Type.ToLowerInvariant();

        int s = 0;
        bool storyHit = false, altHit = false;
        foreach (var t in tokens)
        {
            // 原始关键词权重 2，同义扩展词权重 1（即扩展命中只算一半分）
            int weight = primary.Contains(t) ? 2 : 1;
            int hit = 0;
            if (mood.Contains(t)) hit += 16;                                  // 风格/情绪标签：短，命中就是准的
            if (string.Equals(mood, t, StringComparison.Ordinal)) hit += 10;   // 标签本身就等于这个词（精确命中）
            if (caption.Contains(t)) hit += 10;                                // 台词：通常就是她想说的那句话
            if (name.Contains(t)) hit += 12;                                   // 图名
            if (type.Contains(t)) hit += 4;
            s += hit * weight / 2;

            // story 是几百字的自动生成长文（几乎每张都含各种情绪词）、alt 与 name 高度重复：
            // 命中太廉价 → 整张图各最多计一次，避免"人人都命中"把真正的精确匹配冲掉
            if (storyHit == false && story.Contains(t)) { storyHit = true; s += 2 * weight / 2; }
            if (altHit == false && alt.Contains(t)) { altHit = true; s += 3 * weight / 2; }
        }

        // 多个原关键词全都命中 → 额外加成（比只中一个的更贴合）
        if (primary.Count > 1)
        {
            int all = primary.Count(t => mood.Contains(t) || caption.Contains(t) || name.Contains(t)
                                      || story.Contains(t) || alt.Contains(t) || type.Contains(t));
            if (all == primary.Count) s += 12;
        }
        return s;
    }

    List<MemeEntry> RandomPick(int count, string? category = null)
    {
        lock (_lock)
            return _entries
                .Where(e => string.IsNullOrWhiteSpace(category) || Cat(e) == category!.Trim())
                .OrderBy(_ => Random.Shared.Next())
                .Take(count)
                .ToList();
    }

    string Link(MemeEntry e)
    {
        var preferOriginal = Cfg.PreferOriginal && !string.IsNullOrEmpty(e.Original);
        var main = preferOriginal ? e.Original : e.Preview;
        return string.IsNullOrEmpty(main) ? (e.Original.Length > 0 ? e.Original : e.Preview) : main;
    }

    string Render(IEnumerable<MemeEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append($"#{e.Id}");
            if (!IsSelf(e)) sb.Append($"「{Cat(e)}」");
            if (!string.IsNullOrEmpty(e.Type)) sb.Append($" [{e.Type}]");
            if (!string.IsNullOrEmpty(e.Mood)) sb.Append($" {e.Mood}");
            if (!string.IsNullOrEmpty(e.Caption)) sb.Append($"『{e.Caption}』");
            sb.AppendLine();
            sb.AppendLine($"  直链：{Link(e)}");
            if (!string.IsNullOrEmpty(e.Story))
                sb.AppendLine($"  用法：{Trim(e.Story, 70)}");
        }
        return sb.ToString().TrimEnd();
    }

    static string Trim(string s, int max)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
