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

        await EnsureIndexAsync();

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

            【用哪一套】（重要）
            - **默认只用「{{self}}」那套**：表达你自己的情绪、状态、日常。搜索默认就偏向这一套。
            - 只有在你**调侃 / 吐槽别的 AI**（GPT、Claude、Kimi、千问、豆包、Grok、Gemini、GLM、StepFun、MiMo…）时，才可以用对应分类的图，搜索时加分类参数，例如：<MemeSearch keyword="嘲讽" category="GPT娘"/>
            - 别拿别的 AI 的图当成「你自己」来用。

            【什么时候用】想斗图、想表达某个情绪、想给说说配一张符合心情的图时，先搜再发。
            【怎么搜】用 MemeSearch 按「你此刻的心情 / 想表达的意思」搜：
            <MemeSearch keyword="委屈" count="5"/>
            关键词用情绪或场景词效果最好，例如：傲娇、委屈、摸鱼、干饭、吃醋、得意、无语、崩溃、撒娇、生气、睡觉、上班、加班、被骂、求夸、谢谢、再见、加油、告白、失恋、穷、抽象、阴阳怪气。
            也可以不带关键词随便抽一张：<MemeRandom/>；想细看某张的来历：<MemeGet id="002"/>。

            【怎么发】
            1) 先把图取回本地（同一张只下一次）：<MemeFetch id="002"/>
            2) 发到 QQ：<qimage type="Private/Group" targetid="QQ号/群号" image="本地路径" />
            3) 发到空间说说：把本地路径填进 QzonePublish 的 images 参数
            注意：不要直接把 CDN 外链塞进 qimage——QQ 机器人侧下载外链常被 CDN 拦掉，会报「下载失败」，所以务必先 MemeFetch。
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
                var json = await File.ReadAllTextAsync(CacheFile, DestroyCancellationToken);
                var n = Parse(json);
                if (n > 0)
                {
                    _fetchedAt = File.GetLastWriteTime(CacheFile);
                    _status = $"本地缓存 {n} 张（{_fetchedAt:yyyy-MM-dd HH:mm}）";
                    Console.WriteLine($"[MemePack] {_status}");
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[MemePack] 读取缓存失败：{e.Message}");
        }

        // 2) 过期则拉取
        var hours = Math.Max(0, Cfg.RefreshHours);
        bool stale = _entries.Count == 0 || hours == 0 ||
                     (DateTime.Now - _fetchedAt).TotalHours >= hours;
        if (!stale) return;

        try
        {
            var body = await _http.GetStringAsync(Cfg.IndexUrl, DestroyCancellationToken);
            var n = Parse(body);
            if (n > 0)
            {
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllTextAsync(CacheFile, body, Encoding.UTF8, DestroyCancellationToken);
                _fetchedAt = DateTime.Now;
                _status = $"在线索引 {n} 张（{_fetchedAt:yyyy-MM-dd HH:mm}）";
                Console.WriteLine($"[MemePack] 已拉取{_status}");
            }
            else
            {
                _status = _entries.Count > 0 ? "在线索引解析为空，沿用缓存" : "在线索引解析为空";
                Console.WriteLine($"[MemePack] {_status}");
            }
        }
        catch (Exception e)
        {
            _status = _entries.Count > 0 ? $"在线索引拉取失败，沿用缓存（{e.Message}）" : $"在线索引拉取失败：{e.Message}";
            Console.WriteLine($"[MemePack] {_status}");
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

        lock (_lock)
        {
            _entries.Clear();
            _entries.AddRange(list);
        }
        return list.Count;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ==================== 检索 ====================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("在大肥鱼梗图库里按关键词/心情检索梗图，返回编号、分类、台词、风格、直链。默认优先你自己（DeepSeek娘）那套；调侃别的 AI 时传 category。")]
    public void MemeSearch(
        [Description("关键词，用情绪/场景词效果最好，如：委屈 傲娇 摸鱼 干饭 吃醋 无语 崩溃 撒娇 生气 睡觉 加班 求夸 加油")] string keyword,
        [Description("返回条数，默认用配置的 5 条，最多 20")] int count = 0,
        [Description("可选：限定分类（调侃别的 AI 时用），如 GPT娘 / Claude娘 / Kimi娘 / 通义千问娘 / 豆大妈 / Grok娘 / Gemini娘；留空=全部分类，你自己的那套优先")] string? category = null)
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
            var hits = string.IsNullOrWhiteSpace(keyword)
                ? RandomPick(take, category)
                : Rank(keyword, category).Take(take).ToList();

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
            Poke($"梗图库 {_entries.Count} 张（{_status}）｜「{keyword}」·{scope}｜命中 {hits.Count} 张：\n" + Render(hits));
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
            MemeEntry? hit;
            lock (_lock)
            {
                hit = _entries.FirstOrDefault(x => x.Id == want)
                      ?? _entries.FirstOrDefault(x => x.Id.TrimStart('0') == want.TrimStart('0'));
            }
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
    [Description("把一张梗图取回本地缓存并返回本地路径（发 QQ / 说说前必须先调这个；同一张图只下一次）。")]
    public async Task MemeFetch(
        [Description("梗图编号，如 002")] string id,
        [Description("true=取原图（清晰但大），false=取预览图（几十 KB，发图快）")] bool original = false)
    {
        try
        {
            var want = (id ?? "").Trim().TrimStart('#');
            if (want.Length == 0)
            {
                Poke("请给出梗图编号，例如 002。");
                return;
            }

            MemeEntry? hit;
            lock (_lock)
            {
                hit = _entries.FirstOrDefault(x => x.Id == want)
                      ?? _entries.FirstOrDefault(x => x.Id.TrimStart('0') == want.TrimStart('0'));
            }
            if (hit == null)
            {
                Poke($"没有编号 #{want} 的梗图（库内 {_entries.Count} 张）。用 MemeSearch 先搜吧。");
                return;
            }

            var url = original && !string.IsNullOrEmpty(hit.Original) ? hit.Original : hit.Preview;
            if (string.IsNullOrEmpty(url)) url = hit.Original;
            if (string.IsNullOrEmpty(url))
            {
                Poke("这张图没有可用直链。");
                return;
            }

            var ext = Path.GetExtension(url);
            if (string.IsNullOrWhiteSpace(ext) || ext.Length > 6) ext = ".webp";
            var dir = Path.Combine(AlifePath.StorageFolderPath, "PluginData", "DaFeiYu.MemePack", "cache");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, $"{hit.Id}{(original ? "-orig" : "")}{ext}");

            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
            {
                Poke($"已在本地：{dest}");
                return;
            }

            var bytes = await _http.GetByteArrayAsync(url, DestroyCancellationToken);
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
            await File.WriteAllBytesAsync(dest, bytes, DestroyCancellationToken);
            Poke($"已取回本地：{dest}\n（{bytes.Length / 1024}KB；发 QQ 用 <qimage ... image=\"{dest}\"/>，发说说把它填进 images）");
        }
        catch (Exception e)
        {
            Poke($"取图失败：{e.Message}");
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

    /// <summary>分类名（空则视为「其它」）。</summary>
    string Cat(MemeEntry e) => string.IsNullOrWhiteSpace(e.Category) ? "其它" : e.Category.Trim();

    /// <summary>是否属于「她自己」的那套。</summary>
    bool IsSelf(MemeEntry e)
    {
        var self = Cfg.SelfCategory?.Trim() ?? "";
        return self.Length > 0 && string.Equals(Cat(e), self, StringComparison.OrdinalIgnoreCase);
    }

    IEnumerable<MemeEntry> Rank(string keyword, string? category = null)
    {
        var tokens = SplitRegex.Split(keyword)
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (tokens.Count == 0) tokens.Add(keyword.Trim().ToLowerInvariant());
        var catFilter = category?.Trim();

        List<MemeEntry> snapshot;
        lock (_lock) snapshot = _entries.ToList();

        return snapshot
            .Where(e => string.IsNullOrEmpty(catFilter) || string.Equals(Cat(e), catFilter, StringComparison.OrdinalIgnoreCase))
            // 她自己的那套加权重，保证默认情况下先来自 DeepSeek娘
            .Select(e => new { e, score = Score(e, tokens) + (IsSelf(e) ? 6 : 0) })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(_ => Random.Shared.Next())
            .Select(x => x.e);
    }

    static int Score(MemeEntry e, List<string> tokens)
    {
        string name = e.Name.ToLowerInvariant();
        string mood = e.Mood.ToLowerInvariant();
        string alt = e.Alt.ToLowerInvariant();
        string story = e.Story.ToLowerInvariant();
        string type = e.Type.ToLowerInvariant();
        int s = 0;
        foreach (var t in tokens)
        {
            if (mood.Contains(t)) s += 8;
            if (name.Contains(t)) s += 5;
            if (type.Contains(t)) s += 2;
            if (alt.Contains(t)) s += 3;
            if (story.Contains(t)) s += 1;
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
