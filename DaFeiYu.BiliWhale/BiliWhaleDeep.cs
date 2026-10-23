using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using Alife.Function.AIModelUtility;
using Alife.Framework;
using Alife.Function.FunctionCaller;

namespace DaFeiYu.BiliWhale;

/// <summary>
/// B站视频深读：「真正看懂」的那一步。
///
/// 上游 debrief 的做事方式：字幕全文**写进文件、一行不印**；标签/分P/章节根本不取；
/// 抽帧只拼 9 格 480px 小图、时间戳也不传；从不送视觉模型 —— "看画面"全靠她手动调 read_image。
///
/// 这一层由插件补齐：
///   · 完整字幕（从 debrief 写好的文件里读，不额外调上游）
///   · 标签 / 分P / 章节（直连 B站 view + tag API，只读）
///   · 简介全文（上游截到 300 字，这里不截）
///   · 关键帧逐帧抽原图（复用 watch-video 下载的 mp4 + 场景吸附时间点，自己抽）
///   · 逐帧 + 封面 送视觉模型（插件自己调，不用她手动 read_image）
/// </summary>
public partial class BiliWhaleModule
{
    // ==================== BiliDeepRead ====================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("深读一条视频（真正看懂的那一步）：完整字幕 + 标签/分P/章节 + 简介全文 + 弹幕热区 + 热评 + 关键帧画面（插件自动送视觉模型，不用你再调视觉工具）+ 音轨流式转写（本机 SenseVoice，没字幕的视频也听得懂，台词按时间戳对齐到画面上，音画同步）。比 BiliDebrief 慢很多（几分钟），只用来深读从初筛里挑出来的那 2-3 条。")]
    public async Task BiliDeepRead(
        [Description("视频 BV 号")] string bvid,
        [Description("抽几帧看画面，-1=用配置默认，0=不看（便宜很多；注意：音轨转写也跟着一起跳过）")] int frames = -1,
        [Description("关掉=只给图片路径、不送视觉模型（配合配置开关共同决定）")] bool vision = true,
        [Description("带几条热评，默认 8")] int comments = 8)
    {
        var bv = Bv(bvid);
        if (bv.Length == 0) { Poke("把 BV 号给我。"); return; }

        var wantFrames = frames < 0 ? Math.Clamp(Cfg.DeepReadFrames, 0, 16) : Math.Clamp(frames, 0, 16);
        var wantVision = vision && Cfg.DeepReadVision && _vision != null;
        var ncom = Math.Clamp(comments, 0, 20);
        _runner.Log($"深读 {bv} 开始（帧={wantFrames} 视觉={(wantVision ? "开" : "关")} 热评={ncom}）");

        var parts = new List<string>();
        var ct = DestroyCancellationToken;

        // ① 元信息（标签 / 分P / 章节 / 简介全文）—— 直连 B站 API，都是只读 GET
        var meta = await BuildMetaAsync(bv, ct);
        parts.Add(meta);

        // ② debrief 文字腿（弹幕热区 + 热评 + 封面）—— 复用上游，加 --score 出评分卡
        string title = _deepLastTitle, coverPath = "";
        var debArgs = new List<string> { bv, "--no-frames", "--comments", ncom.ToString(CultureInfo.InvariantCulture), "--score" };
        var deb = await _runner.RunAsync("debrief", debArgs, 300, ct);
        if (deb.Ok) parts.Add(DebriefTextSections(deb.Out));
        else { Poke($"深读 {bv} 中断：debrief 跑失败了。{deb.Text}"); return; }

        // 从 debrief 输出里把封面路径摘出来（debrief.mjs:91 打印的是绝对路径）
        coverPath = Regex.Match(deb.Out, @"(?m)shorts[\\/]covers[\\/].*?\.jpg").Value;
        if (coverPath.Length > 0 && !Path.IsPathRooted(coverPath))
            coverPath = Path.Combine(_runner.ShotsCoversDir, Path.GetFileName(coverPath));

        // ③ 字幕全文 —— 从 debrief 写好的文件里读（不额外调 subtitles）
        var transcriptFile = Path.Combine(_runner.ShotsSubsDir, $"{bv}-debrief.txt");
        if (File.Exists(transcriptFile))
        {
            var transcript = File.ReadAllText(transcriptFile, Encoding.UTF8);
            if (transcript.Trim().Length > 0)
            {
                parts.Add(FormatTranscript(transcript, bv));
            }
        }

        // ④ 画面（抽帧 + 视觉）—— 只在 wantFrames > 0 时走这一步
        if (wantFrames > 0)
        {
            var frameParts = await BuildFrameReportAsync(bv, title, coverPath, wantFrames, wantVision, ct);
            if (frameParts != null) parts.Add(frameParts);
        }

        // ⑤ 输出：分片 poke（每片 ≤7000 字，避免硬截断把字幕砍掉尾巴）
        var sb = new StringBuilder();
        sb.AppendLine($"【深读 · {bv}】{(title.Length > 0 ? title : "（标题稍后补上）")}");
        Poke(sb.ToString().TrimEnd());

        var budget = Math.Max(7000, Cfg.MaxOutputChars / 2);
        foreach (var part in parts)
        {
            if (part.Length <= budget) Poke(part);
            else Poke(part[..budget] + $"\n…（剩余 {part.Length - budget} 字，用专项函数细看：BiliSubtitles / BiliCommentDigest）…");
        }

        Poke("—— 深读收工：按标尺打分（每项 0-4，满分 20），值得再决定分享/收藏/静静写进日记。");
        _runner.Log($"深读 {bv} 结束（{parts.Count + 1} 片）");
    }

    // ==================== 元信息（直连 B站）====================

    string _deepLastTitle = "";

    async Task<string> BuildMetaAsync(string bv, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("══════ ① 元信息（插件直连 B站，比上游多标签/分P/简介全文）══════");

        // view API（不用 wbi）
        using (var doc = await ApiGetAsync(
                   $"https://api.bilibili.com/x/web-interface/view?bvid={Uri.EscapeDataString(bv)}", ct))
        {
            try
            {
                if (doc != null && doc.RootElement.TryGetProperty("data", out var d))
                {
                    var title = DStr(d, "title");
                    _deepLastTitle = title;
                    sb.AppendLine($"标题：{title}");
                    if (d.TryGetProperty("owner", out var ow) && ow.ValueKind == JsonValueKind.Object)
                        sb.AppendLine($"UP：{DStr(ow, "name")}（mid {DStr(ow, "mid")}）");
                    var dur = d.TryGetProperty("duration", out var du) ? du.GetInt32() : 0;
                    sb.AppendLine($"时长：{fmt(dur)}　分区：{DStr(d, "tname")}　发布时间：{Dto(d, "pubdate", "ctime")}");
                    if (d.TryGetProperty("stat", out var s) && s.ValueKind == JsonValueKind.Object)
                        sb.AppendLine(fmtStat(s));

                    var desc = DStr(d, "desc").Trim();
                    if (desc.Length > 0 && desc != "-")
                        sb.AppendLine($"简介：{desc}");
                    else sb.AppendLine("简介：（无）");

                    // desc_v2 → 章节/分段的弱信号（type=1 是"时间+文本"，常对应"片头/片尾/分段"）
                    if (d.TryGetProperty("desc_v2", out var dv) && dv.ValueKind == JsonValueKind.Array)
                    {
                        var chunks = new List<string>();
                        foreach (var e in dv.EnumerateArray())
                        {
                            if (e.TryGetProperty("type", out var et) && et.GetInt32() == 1
                                && e.TryGetProperty("raw_text", out var rt))
                            {
                                var t = (rt.GetString() ?? "").Trim();
                                if (t.Length > 0 && t != "-") chunks.Add(t);
                            }
                        }
                        if (chunks.Count > 0) sb.AppendLine($"章节提示：{string.Join(" · ", chunks)}");
                    }

                    // 分P（多P=合集）
                    if (d.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
                    {
                        var pl = new List<string>();
                        foreach (var pg in pages.EnumerateArray())
                            pl.Add($"{(pg.TryGetProperty("part", out var pt) ? (pt.GetString() ?? "") : "?")}（{fmt((pg.TryGetProperty("duration", out var pd) ? pd.GetInt32() : 0))}）");
                        if (pl.Count > 1) sb.AppendLine($"分P（{pl.Count} 集）：{string.Join("  |  ", pl)}");
                    }

                    if (d.TryGetProperty("honor_reply", out var hr) && hr.ValueKind == JsonValueKind.Object)
                    {
                        var honor = DStr(hr, "honor_reply");
                        if (honor.Length > 0) sb.AppendLine($"荣誉：{honor}");
                    }

                    if (d.TryGetProperty("dynamic", out var dyn) && dyn.ValueKind == JsonValueKind.String)
                        sb.AppendLine($"动态：{dyn.GetString()}");
                }
            }
            catch (Exception e) { sb.AppendLine($"（元信息解析失败：{e.Message}）"); }
        }

        // 标签（x/tag/archive/tags —— 不用 wbi，已验证 code=0）
        sb.AppendLine();
        using (var td = await ApiGetAsync(
                   $"https://api.bilibili.com/x/tag/archive/tags?bvid={Uri.EscapeDataString(bv)}", ct, quiet: true))
        {
            try
            {
                if (td != null && td.RootElement.TryGetProperty("data", out var tags) && tags.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>();
                    foreach (var t in tags.EnumerateArray())
                    {
                        var tn = t.TryGetProperty("tag_name", out var tnn) ? (tnn.GetString() ?? "").Trim() : "";
                        if (tn.Length > 0) names.Add(tn);
                    }
                    if (names.Count > 0) sb.AppendLine($"标签：{string.Join(" · ", names)}（共 {names.Count} 个）");
                }
            }
            catch { }
        }

        return sb.ToString().TrimEnd();
    }

    // ==================== 字幕 ====================

    string FormatTranscript(string text, string bv)
    {
        var max = Math.Max(2000, Cfg.DeepReadTranscriptChars);
        if (text.Length <= max)
            return $"══════ ② 字幕全文（{text.Split('\n').Length} 行，带时间戳）══════\n{text.TrimEnd()}";

        // 截取：头 60% + 尾 40%
        var head = (int)(max * 0.6);
        var tail = max - head - 90;
        if (tail < 0) { head = max - 20; tail = 0; }
        return $"══════ ② 字幕全文（{text.Split('\n').Length} 行，含时间戳；头 {head} 字 + 尾 {tail} 字）══════\n"
               + text[..head]
               + $"\n…（中间省略 {text.Length - head - tail} 字；完整版在 {_runner.ShotsSubsDir}\\{bv}-debrief.txt）…\n"
               + (tail > 0 ? text[^tail..].TrimEnd() : "");
    }

    // ==================== 声音（音轨流式转写）====================

    /// <summary>
    /// 听视频：把音轨按 20 秒一段流式喂给本机识别器（SenseVoice），每段用完即删 ——
    /// 全程不把视频读进内存；每段识别结果钉在这段的起始时刻上，台词与画面天然对齐。
    /// </summary>
    async Task<string> BuildAudioTimelineAsync(string mp4, List<string> picked, CancellationToken ct)
    {
        if (!Cfg.DeepReadAudio) { _runner.Log("深读听声音：配置里关着，跳过"); return ""; }
        if (mp4.Length == 0) return "";

        // 优先开「独立 VAD 会话」；拿不到就退回共享的流式识别器（同一台本机模型，效果一样，只是共用 VAD）
        var own = _audio?.CreateAudioRecognizer();
        var sink = (IAuditoryModel?)own ?? _hearing;
        if (sink == null)
        {
            _runner.Log("深读听声音：没拿到本机识别器（SenseVoice / AIModelUtility 没加载？），跳过");
            return "";
        }

        var cap = Cfg.DeepReadAudioSeconds;
        _runner.Log($"深读听声音：开始（上限 {cap} 秒，20 秒一段流式喂识别器 · {(own != null ? "独立会话" : "共享会话")}）");
        var sb = new StringBuilder();
        sb.AppendLine("══════ ③.5 声音（音轨流式转写 · 本机 SenseVoice · 20 秒一段 · 不进内存）══════");

        var tmpDir = Path.Combine(_runner.TempDir, "audio");
        try { Directory.CreateDirectory(tmpDir); } catch { }

        var lines = new List<(int Start, string Text)>();
        var cursor = 0;
        var covered = 0;
        void OnHeard(string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) lines.Add((cursor, text.Trim()));
        }
        try
        {
            sink.Recognized += OnHeard;

            for (var start = 0; cap <= 0 || start < cap; start += 20)
            {
                if (ct.IsCancellationRequested) break;
                cursor = start;
                var f32 = Path.Combine(tmpDir, $"a_{start:D5}.f32");
                var r = await _runner.RunFfmpegAsync(new[]
                {
                    "-y", "-loglevel", "error",
                    "-ss", start.ToString(CultureInfo.InvariantCulture),
                    "-t", "20", "-i", mp4,
                    "-vn", "-f", "f32le", "-acodec", "pcm_f32le", "-ac", "1", "-ar", "16000", f32,
                }, 180, ct);
                if (!r.Ok || !File.Exists(f32)) { try { File.Delete(f32); } catch { } break; }

                byte[] bytes;
                try { bytes = await File.ReadAllBytesAsync(f32, ct); }
                finally { try { File.Delete(f32); } catch { } }
                if (bytes.Length < 6400) break;      // 不足 0.1 秒 = 音轨到头了

                var samples = new float[bytes.Length / 4];
                Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
                sink.AcceptWaveform(samples, samples.Length);   // 同步处理并顺手切句
                covered = start + 20;
            }

            if (sink is IAudioRecognizer rec) rec.Flush();
            _runner.Log($"深读听声音：完成 0–{covered} 秒，识别出 {lines.Count} 段人声");
            sb.AppendLine($"[范围] 已转写 0–{covered} 秒（上限 {cap} 秒，配置里「深读听多久」可调）");
        }
        catch (Exception e) { sb.AppendLine($"（音轨转写出问题：{e.Message}）"); }
        finally
        {
            try { sink.Recognized -= OnHeard; } catch { }     // 共享会话必须摘钩子，否则它会继续听别人的音频
            if (own != null) { try { own.Dispose(); } catch { } }
        }

        if (lines.Count == 0)
        {
            sb.AppendLine("（这一段没识别出人声：可能是纯音乐 / 没有解说 / 识别器没打开）");
            return sb.ToString().TrimEnd();
        }

        foreach (var (start, text) in lines)
        {
            var near = picked.FirstOrDefault(p => Math.Abs(TsSeconds(p) - start) <= 20);
            sb.AppendLine($"[{MmSs(start)}] {text}" + (near != null ? $"　（这时刻附近有画面 {near}）" : ""));
        }
        return sb.ToString().TrimEnd();
    }

    static double TsSeconds(string ts)
    {
        var parts = (ts ?? "").Split(':');
        try
        {
            if (parts.Length == 3) return int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + double.Parse(parts[2], CultureInfo.InvariantCulture);
            if (parts.Length == 2) return int.Parse(parts[0]) * 60 + double.Parse(parts[1], CultureInfo.InvariantCulture);
        }
        catch { }
        return double.NaN;
    }

    static string MmSs(int seconds) => $"{seconds / 60:D2}:{seconds % 60:D2}";

    // ==================== 画面（抽帧 + 视觉）====================

    async Task<string?> BuildFrameReportAsync(string bv, string title, string coverPath,
        int wantFrames, bool wantVision, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("══════ ③ 画面（插件逐帧抽原图，已送视觉模型读画面）══════");

        // Step 1: watch-video → mp4 + 场景吸附时间点
        var wv = await _runner.RunAsync("watch-video", new[] { bv, "3x3" }, 600, ct);
        var mp4 = "";
        var matchMp4 = Regex.Match(wv.Out, @"已下载:\s*(.+?\.mp4)\s*\(");
        if (matchMp4.Success) mp4 = matchMp4.Groups[1].Value.Trim();
        else
        {
            // fallback: 去 ShotsVideoDir 找最新的匹配 mp4
            try
            {
                var cand = Directory.GetFiles(_runner.ShotsVideoDir, $"{bv}*.mp4")
                                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (cand != null) mp4 = cand;
            }
            catch { }
        }
        if (mp4.Length == 0 || !File.Exists(mp4))
        {
            sb.AppendLine("（没能拿到视频文件：可能是下载失败 / 没登号 / ffmpeg 路径不对。下面只有 debrief 的九宫格可看。）");
            return sb.ToString().TrimEnd();
        }

        // Step 2: 解析时间点（watch-video 的 "时间点  : 1=00:05  2=00:12  …"）
        var times = new List<string>();
        var matchTimes = Regex.Match(wv.Out, @"时间点\s*:\s*(.+)");
        if (matchTimes.Success)
        {
            foreach (var tok in matchTimes.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = tok.IndexOf('=');
                if (eq < 0) continue;
                var ts = tok[(eq + 1)..].Trim();
                if (ts.Length >= 4 && (ts.Contains(':') || ts.Contains('.'))) times.Add(ts);
            }
        }
        if (times.Count == 0)
        {
            // fallback: 均匀取 wantFrames 个时间点，粗暴但可用
            var durMatch = Regex.Match(wv.Out, @"时长:\s*(\d+)s");
            var dur = durMatch.Success ? double.TryParse(durMatch.Groups[1].Value, out var d) && d > 0 ? d : 60 : 60;
            for (int i = 0; i < wantFrames; i++)
                times.Add(fmt(dur * (i + 0.5) / wantFrames));
        }

        // Step 3: 从 mp4 里抽帧（挑均匀覆盖全片的 wantFrames 个时间点）
        var frameDir = Path.Combine(_runner.FramesDir, bv);
        Directory.CreateDirectory(frameDir);
        var picked = PickEvenly(times, wantFrames);
        var frameFiles = new List<(string tLabel, string file)>();
        for (int i = 0; i < picked.Count; i++)
        {
            var file = Path.Combine(frameDir, $"frame_{i:D2}.jpg");
            var r = await _runner.RunFfmpegAsync(
                new[] { "-y", "-loglevel", "error", "-ss", picked[i], "-i", mp4, "-frames:v", "1", file }, 120, ct);
            if (r.Ok && File.Exists(file)) frameFiles.Add((picked[i], file));
        }

        // Step 4: 封面也要（如果有）
        if (coverPath.Length > 0 && File.Exists(coverPath))
            frameFiles.Insert(0, ("封面", coverPath));

        if (frameFiles.Count == 0)
        {
            sb.AppendLine("（没能抽出任何帧：可能是 ffmpeg 有问题。看 debrief 的九宫格和封面凑合一下吧。）");
            return sb.ToString().TrimEnd();
        }

        // Step 5: 逐帧视觉描述
        if (wantVision)
        {
            for (int i = 0; i < frameFiles.Count; i++)
            {
                var (label, file) = frameFiles[i];
                var q = label switch
                {
                    "封面" => $"这是B站视频《{title}》的封面。用两三句话描述封面上有什么、画风/色调、有没有标题字/人物/头像等信息。只描述你看到的，不要评价。",
                    _ => $"这是B站视频《{title}》第 {label} 的画面（第 {i}/{frameFiles.Count} 张，按时间顺序）。描述画面里有什么、在做什么、屏幕上有没有关键文字或字幕。两到三句话，只描述看到的内容。",
                };
                var tokens = Math.Max(60, Cfg.DeepReadVisionTokens);
                string desc;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(90));
                    try { desc = await _vision!.QueryAsync(file, q, tokens, cts.Token); }
                    catch (Exception e) { desc = $"（视觉模型没读出东西：{e.Message}）"; }
                }
                sb.AppendLine($"[{label}] {desc}");
            }
        }
        else
        {
            sb.AppendLine("（插件端「视觉自动看画面」关闭了：下面只有图片路径，想看图自己调视觉工具）");
            foreach (var (label, file) in frameFiles)
                sb.AppendLine($"[{label}] {file}");
        }

        // Step 5.5: 听声音 —— 音轨流式转写，把台词钉在同一条时间轴上（音画同步）
        var audioReport = await BuildAudioTimelineAsync(mp4, picked, ct);
        if (audioReport.Length > 0) sb.AppendLine().AppendLine(audioReport);

        // Step 6: 附图路径（她随时可以 read_image 复核）
        if (wantVision)
        {
            sb.AppendLine($"\n单帧原图（共 {frameFiles.Count} 张，交给 read_image 看细节）：");
            foreach (var (_, file) in frameFiles) sb.AppendLine($"  {file}");
        }

        sb.AppendLine($"\n九宫格 / 字幕条 / 原片 mp4 看上游 debrief（--no-frames 时它不打印这些 —— 这里补上）：{mp4}");

        return sb.ToString().TrimEnd();
    }

    static List<string> PickEvenly(List<string> times, int want)
    {
        if (times.Count <= want) return times.ToList();
        var outList = new List<string>();
        for (int i = 0; i < want; i++)
            outList.Add(times[Math.Min(times.Count - 1, i * times.Count / want + times.Count / (2 * want))]);
        return outList;
    }

    // ==================== BiliEmotes ====================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出你这个 B站号能用的官方表情（返回能直接写进评论正文的转义符，如 [tv_doge]）。评论里直接写转义符就行，不用别的参数。")]
    public async Task BiliEmotes(
        [Description("按关键词过滤（如 doge、小电视、点赞、颜文字），留空=全部")] string keyword = "",
        [Description("最多列几个，默认 60")] int count = 60)
    {
        if (!HasBiliCookie) { Poke("还没配 B站 cookie：先 <BiliSetCookie …> 填上 SESSDATA / bili_jct / buvid3。"); return; }

        var all = await FetchEmoteCodesAsync(DestroyCancellationToken);
        if (all.Count == 0) { Poke($"没拿到官方表情列表（cookie 过期？用 <BiliSession/> 核实一下）\n本地缓存：{EmoteCacheFile}"); return; }

        var kw = Clean(keyword).ToLowerInvariant();
        if (kw.Length > 0)
            all = all.Where(x => x.Code.ToLowerInvariant().Contains(kw) || x.Pkg.ToLowerInvariant().Contains(kw)).ToList();

        var limit = Math.Clamp(count, 10, 150);
        var sb = new StringBuilder();
        var pkgNames = all.Select(x => x.Pkg).Distinct().ToList();
        var taken = 0;
        foreach (var pkg in pkgNames)
        {
            if (taken >= limit) break;
            var codes = all.Where(x => x.Pkg == pkg).Select(x => x.Code).Distinct().ToList();
            if (codes.Count == 0) continue;
            var slice = codes.Take(Math.Min(codes.Count, limit - taken)).ToList();
            taken += slice.Count;
            sb.AppendLine($"【{pkg}】（{codes.Count} 个）");
            sb.AppendLine(string.Join("  ", slice));
            sb.AppendLine();
        }
        if (taken == 0 && kw.Length > 0)
            sb.AppendLine($"没有找到含「{kw}」的表情。换个关键词试试。");

        sb.Insert(0, $"【B站官方表情 · 直接写进 comment 的 message 里】：\n（你的号共有 {all.Select(x => x.Code).Distinct().Count()} 个表情，{pkgNames.Count} 个包）\n\n");
        Poke(sb.ToString().TrimEnd());
    }

    // ==================== 小工具 ====================

    /// <summary>debrief 输出太长，只取 ④弹幕热区 ⑤热评。</summary>
    static string DebriefTextSections(string text)
    {
        var sb = new StringBuilder();
        // 从 ④ 开始（跳过 ① 元信息、② 封面、③ 字幕 —— 这些深读自己补了更好的）
        var idx = text.IndexOf("弹幕热区", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var segment = text[idx..];
            // 截掉 ⑥/⑥b 画面段落（帧的描述我们自己做，上游只剩九宫格路径 & 评分卡保留）
            var cut = segment.IndexOf("══ ⑥ 画面", StringComparison.Ordinal);
            if (cut > 0) segment = segment[..cut];
            sb.AppendLine(segment.TrimEnd());
        }

        // 评分卡（如果有 --score 输出）
        var si = text.IndexOf("评分卡", StringComparison.Ordinal);
        if (si >= 0)
        {
            var scard = text[si..];
            var next = scard.IndexOf("══════ 下一步", StringComparison.Ordinal);
            if (next > 0) scard = scard[..(next + 30)];
            sb.AppendLine(scard.TrimEnd());
        }
        return sb.ToString().TrimEnd();
    }

    static string DStr(JsonElement e, string key)
    {
        if (e.TryGetProperty(key, out var v))
            return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString();
        return "";
    }

    static string Dto(JsonElement e, string key1, string key2)
    {
        var s = DStr(e, key1);
        if (s.Length == 0) s = DStr(e, key2);
        return s.Length > 0 ? FormatUnixTime(s) : "";
    }

    static string FormatUnixTime(string ts)
    {
        if (long.TryParse(ts, out var unix) && unix > 0)
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
            catch { }
        }
        return ts;
    }

    static string fmt(double seconds) => $"{Math.Floor(seconds / 60):0}:{seconds % 60:00}";
    static string fmt(int seconds) => fmt((double)seconds);

    static string fmtStat(JsonElement s) =>
        $"播放 {FmtNum(s, "view")}  ·  赞 {FmtNum(s, "like")}  ·  硬币 {FmtNum(s, "coin")}  ·  收藏 {FmtNum(s, "favorite")}  ·  弹幕 {FmtNum(s, "danmaku")}  ·  评论 {FmtNum(s, "reply")}  ·  分享 {FmtNum(s, "share")}";

    static string FmtNum(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return "?";
        var n = v.TryGetInt64(out var l) ? l : (long)v.GetDouble();
        return n >= 10_000_000 ? $"{n / 1_000_000d:F1}千万"
             : n >= 1_000_000 ? $"{n / 1_000_000d:F1}万"
             : n >= 10_000 ? $"{n / 10_000d:F1}万" : n.ToString();
    }

    static string Bv(string raw) => (raw ?? "").Trim();

    void Poke(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        interactor.Poke(text.Trim());
    }
}