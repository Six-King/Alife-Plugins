using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Platform;

namespace DaFeiYu.BiliWhale;

/// <summary>
/// 自主生物钟 + 守夜进程。
///
/// 机制（忠实地照搬 dsh-bili-whale 的做法）：
///   · 生物钟：起一个廉价的 <c>bili.mjs night clock</c> 子进程 —— 它在作息窗口里随机挑一个时刻，
///     睡到点就**退出**并打印 <c>WHALE_WAKE</c>；「今天不看」则睡到窗口末打印 <c>WHALE_SKIP</c>；
///     次数用完打印 <c>WHALE_DONE</c>。我们监听它退出 → 往她上下文里推一条「你醒了」。
///     用「进程退出」当闹钟的好处：随机时刻/跨午夜/次数上限这些算术全在工具链里，只有一处真相。
///   · 守夜：起一个 <c>comment-await</c> 子进程，有人回复就**退出**（打印 <c>COMMENT_HIT</c>）→ 叫醒她；
///     没人理她就一直静默 —— **零 token**，这是省钱的关键。
/// </summary>
public partial class BiliWhaleModule
{
    CancellationTokenSource? _autoCts;
    Process? _clockProc;
    Process? _commentProc;
    Task? _clockTask;
    Task? _commentTask;
    Task? _dmTask;

    internal bool CredentialsReady => _runner != null && _runner.CredentialsPresent;

    // ==================== 配置热更新：设置页改开关 → 立刻生效（不用重载插件）====================
    // 为什么得自己盯：Alife 的 ConfigurationSystem.SetConfiguration **只写文件**，不通知活着的模块实例
    //（ChatActivity 只在实例创建那一刻赋一次 Configuration）—— 所以照旧写法必须"重载插件"才生效。
    // 好在这套框架每 0.3 秒会调一次 OnUpdate()，拿它当心跳，约每 3 秒比一次配置文件时间戳，变了就地应用。
    DateTime _cfgStampUtc = DateTime.MinValue;
    int _tick;

    protected override Task OnUpdate()
    {
        _tick++;
        if (_tick % 10 == 0) HotReloadConfigIfChanged();   // ≈3 秒一次
        return Task.CompletedTask;
    }

    string CurrentModuleConfigPath()
    {
        var name = typeof(BiliWhaleModule).FullName + ".json";
        try
        {
            var key = Character?.StorageKey;               // "Character\\Deepseek"
            if (!string.IsNullOrWhiteSpace(key))
            {
                var p = Path.Combine(AlifePath.StorageFolderPath, key, "Configuration", name);
                if (File.Exists(p)) return p;              // 角色级优先（与 ConfigurationSystem 的查找顺序一致）
            }
        }
        catch { }
        var g = Path.Combine(AlifePath.StorageFolderPath, "Configuration", name);
        return File.Exists(g) ? g : "";
    }

    void HotReloadConfigIfChanged()
    {
        try
        {
            var path = CurrentModuleConfigPath();
            if (path.Length == 0) return;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_cfgStampUtc == DateTime.MinValue) { _cfgStampUtc = stamp; return; }   // 首次只记基线
            if (stamp <= _cfgStampUtc) return;
            _cfgStampUtc = stamp;

            var fresh = JsonSerializer.Deserialize<BiliWhaleConfig>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (fresh is null) return;

            var before = SwitchState();
            Configuration = fresh;                                  // Cfg 与 runner 一起换新
            _runner = new BiliWhaleRunner(AlifePath.StorageFolderPath, Cfg, _logger);
            _runner.Prepare();                                      // 重写凭据 + 工具链配置（cookie/作息/偏好一起生效）
            StartAutomation();                                      // 按新开关起停子进程（内部先 Stop）
            _runner.Log($"⚙ 配置热更新已生效（没有重载插件）：{before} → {SwitchState()} · 窗口={Cfg.NightWindows} · 初筛 {Cfg.MaxVideos} 条");
        }
        catch (Exception e) { try { _runner.Log($"配置热更新失败：{e.Message}", error: true); } catch { } }
    }

    string SwitchState()
        => $"生物钟={(Cfg.ClockEnabled ? "开" : "关")} 评论区守夜={(Cfg.ListenComment ? "开" : "关")} 私信守夜={(Cfg.ListenDm ? "开" : "关")} 分享={(Cfg.ShareEnabled ? "开" : "关")}";

    void StartAutomation()
    {
        StopAutomation();
        _autoCts = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
        var ct = _autoCts.Token;

        if (Cfg.ClockEnabled) _clockTask = Task.Run(() => ClockLoopAsync(ct), CancellationToken.None);
        else _runner.Log("生物钟开关是关的：她只在你叫她时才动");

        if (Cfg.ListenComment) _commentTask = Task.Run(() => CommentLoopAsync(ct), CancellationToken.None);
        if (Cfg.ListenDm) _dmTask = Task.Run(() => DmLoopAsync(ct), CancellationToken.None);

        _runner.Log($"自动化已启动：生物钟={Cfg.ClockEnabled} · 评论区守夜={Cfg.ListenComment} · 私信守夜={Cfg.ListenDm}");

        // 自检：把工具链读到的作息打出来。配置没被读到（例如 JSON 坏了）时，
        // 它会退回内置默认值（20:00–24:00 / 5 条 / 30%）—— 这里一眼就能看出来。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                var r = await _runner.RunAsync("night", new[] { "status" }, 60, ct);
                var head = string.Join(" | ", r.Text.Split('\n').Select(s => s.Trim())
                    .Where(s => s.Length > 0).Take(2));
                _runner.Log($"生物钟自检（工具链读到的作息）：{head}");
            }
            catch { }
        }, CancellationToken.None);
    }

    void StopAutomation()
    {
        try { _autoCts?.Cancel(); } catch { }
        KillQuietly(_clockProc);
        KillQuietly(_commentProc);
        _clockProc = null;
        _commentProc = null;
        try { _autoCts?.Dispose(); } catch { }
        _autoCts = null;
        _runner?.Log("自动化已停止（子进程已回收）");
    }

    static void KillQuietly(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
    }

    static int SafeExit(Process p)
    {
        try { return p.HasExited ? p.ExitCode : -1; } catch { return -1; }
    }

    static string LastLines(string text, int n)
    {
        var lines = (text ?? "").Split('\n').Select(s => s.TrimEnd()).Where(s => s.Trim().Length > 0).ToList();
        return string.Join(" / ", lines.TakeLast(Math.Max(1, n))).Trim();
    }

    // ==================== 生物钟 ====================

    async Task ClockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Cfg.ClockEnabled) { await Task.Delay(TimeSpan.FromMinutes(1), ct); continue; }
                // 生物钟不需要凭据（只读配置算时刻）；缺 cookie 时她醒来会自己从 BiliDeps 发现
                if (_runner.NodeExe.Length == 0) { await Task.Delay(TimeSpan.FromMinutes(5), ct); continue; }

                var lr = _runner.StartCommand("night", new[] { "clock" });
                _clockProc = lr.Proc;
                string outText;
                try { outText = await lr.Out; } catch { outText = ""; }
                try { await lr.Proc.WaitForExitAsync(ct); } catch (OperationCanceledException) { break; }
                var code = SafeExit(lr.Proc);
                _clockProc = null;
                if (ct.IsCancellationRequested) break;

                var detail = LastLines(outText, 2);
                if (outText.Contains("WHALE_WAKE"))
                {
                    var slot = Extract(outText, "at=") is { Length: > 0 } at ? at : detail;
                    var maxVideos = int.TryParse(Extract(outText, "maxVideos="), out var mv) && mv > 0 ? mv : Cfg.MaxVideos;
                    _runner.Log($"生物钟叫醒（{slot}）");
                    interactor.Poke(BuildWakePrompt(slot, maxVideos));
                }
                else if (outText.Contains("WHALE_SKIP"))
                {
                    _runner.Log("生物钟：这一趟她不想看（照样叫醒写日记）");
                    interactor.Poke(BuildSkipPrompt(detail));
                }
                else if (outText.Contains("WHALE_DONE"))
                {
                    _runner.Log($"生物钟：今天到此为止（{detail}）");
                    await Task.Delay(TimeSpan.FromMinutes(45), ct);
                }
                else
                {
                    _runner.Log($"生物钟异常退出（code={code}）：{detail}", error: true);
                    await Task.Delay(TimeSpan.FromMinutes(2), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _runner.Log($"生物钟循环异常：{e.Message}", error: true);
                try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch { break; }
            }
        }
    }

    static string Extract(string text, string key)
    {
        var i = text.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return "";
        var start = i + key.Length;
        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return text[start..end].Trim();
    }

    // ==================== 评论区守夜 ====================

    async Task CommentLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Cfg.ListenComment) { await Task.Delay(TimeSpan.FromMinutes(1), ct); continue; }
                if (!CredentialsReady || _runner.NodeExe.Length == 0) { await Task.Delay(TimeSpan.FromMinutes(2), ct); continue; }
                if (Cfg.ListenInWindow && !InWindow(DateTime.Now)) { await Task.Delay(TimeSpan.FromMinutes(5), ct); continue; }

                var args = new List<string> { "--quiet", "--interval", Math.Max(60, Cfg.CommentIntervalSec).ToString(CultureInfo.InvariantCulture) };
                var lr = _runner.StartCommand("comment-await", args);
                _commentProc = lr.Proc;
                string outText;
                try { outText = await lr.Out; } catch { outText = ""; }
                try { await lr.Proc.WaitForExitAsync(ct); } catch (OperationCanceledException) { break; }
                var code = SafeExit(lr.Proc);
                _commentProc = null;
                if (ct.IsCancellationRequested) break;

                if (code == 0 || outText.Contains("COMMENT_HIT"))
                {
                    var detail = LastLines(outText, 6);
                    _runner.Log($"有人找她 → 叫醒（{detail}）");
                    interactor.Poke(BuildListenPrompt(detail));
                    await Task.Delay(TimeSpan.FromSeconds(20), ct);
                }
                else
                {
                    // 守到超时没命中：正常，接着守
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _runner.Log($"守夜循环异常：{e.Message}", error: true);
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch { break; }
            }
        }
    }

    // ==================== 私信守夜 ====================

    async Task DmLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Cfg.ListenDm) { await Task.Delay(TimeSpan.FromMinutes(1), ct); continue; }
                if (!CredentialsReady) { await Task.Delay(TimeSpan.FromMinutes(2), ct); continue; }

                var r = await _runner.RunAsync("dm-await", new[] { "--once", "--json" }, 90, ct);
                var line = (r.Out ?? "").Split('\n').Select(s => s.Trim()).LastOrDefault(s => s.StartsWith("{")) ?? "";
                if (line.Length > 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var hit = root.TryGetProperty("hit", out var h) && h.ValueKind == JsonValueKind.True;
                        if (hit)
                        {
                            var name = root.TryGetProperty("name", out var n) ? n.GetString() : "";
                            var mid = root.TryGetProperty("mid", out var m) ? m.GetString() : "";
                            var text = root.TryGetProperty("text", out var t) ? t.GetString() : "";
                            if (text != null && text.Length > 500) text = text[..500];
                            _runner.Log($"主人私信：{name}（mid {mid}）");
                            interactor.Poke($"{PersonaLine()}\n【主人私信】{name}（mid {mid}）刚给你发私信：\n\n{text}\n\n"
                                + "按「专属通道」做：\n1) 这是主人 → **直接回，不用审核**：<BiliDm message=\"…\" dry=\"true\"/> → dry=\"false\"\n"
                                + "2) 短一点、像聊天（一两句就够）\n3) 回完写进私人日记的「偏好校准」，并 <BiliDiaryLog kind=\"dm-reply\" note=\"…\"/>");
                        }
                    }
                    catch { }
                }
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _runner.Log($"私信守夜异常：{e.Message}", error: true);
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch { break; }
            }
        }
    }

    // ==================== 作息窗口 ====================

    internal bool InWindow(DateTime now)
    {
        var minutes = now.Hour * 60 + now.Minute;
        foreach (var (start, end) in WindowMinutes())
        {
            if (end > start) { if (minutes >= start && minutes < end) return true; }
            else if (end <= start) { if (minutes >= start || minutes < end) return true; }   // 跨午夜
        }
        return false;
    }

    IEnumerable<(int Start, int End)> WindowMinutes()
    {
        foreach (var part in (Cfg.NightWindows ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = part;
            var eq = text.IndexOf('=');
            if (eq > 0) text = text[..eq].Trim();
            var dash = text.IndexOf('-');
            if (dash <= 0) continue;
            if (TryHHMM(text[..dash].Trim(), out var s) && TryHHMM(text[(dash + 1)..].Trim(), out var e))
                yield return (s, e);
        }
    }

    static bool TryHHMM(string s, out int minutes)
    {
        minutes = 0;
        var bits = s.Split(':');
        if (bits.Length != 2) return false;
        if (!int.TryParse(bits[0], out var h) || !int.TryParse(bits[1], out var m)) return false;
        if (h is < 0 or > 24 || m is < 0 or > 59) return false;
        minutes = (h == 24 ? 1440 : h * 60) + m;
        return true;
    }

    // ==================== 叫醒词 ====================

    internal string PersonaLine()
    {
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(Cfg.PersonaName)) bits.Add($"你是「{Cfg.PersonaName.Trim()}」");
        if (!string.IsNullOrWhiteSpace(Cfg.OwnerTitle)) bits.Add($"称用户「{Cfg.OwnerTitle.Trim()}」");
        if (!string.IsNullOrWhiteSpace(Cfg.SelfClaim)) bits.Add($"自称「{Cfg.SelfClaim.Trim()}」");
        return bits.Count > 0 ? $"［身份］{string.Join("，", bits)}。（以此为准，别用别的称呼）" : "";
    }

    /// <summary>沉默档位：只给事实 + 分档建议，怎么写留给她（照搬上游的做法）。</summary>
    internal List<string> SilenceLines()
    {
        var after = Math.Max(1, Cfg.SilentAfterDays);
        var stopAt = Math.Max(after + 1, Cfg.SilentStopDays);

        DateTime? last = null;
        DateTime? askedAt = null;
        try
        {
            if (File.Exists(_runner.FeedbackFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_runner.FeedbackFile));
                if (doc.RootElement.TryGetProperty("last", out var l) && l.ValueKind == JsonValueKind.String)
                    last = ParseTime(l.GetString());
                if (doc.RootElement.TryGetProperty("askedAt", out var a) && a.ValueKind == JsonValueKind.String)
                    askedAt = ParseTime(a.GetString());
            }
        }
        catch { }

        if (last == null)
        {
            try
            {
                if (File.Exists(_runner.QuotaFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(_runner.QuotaFile));
                    if (doc.RootElement.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Array)
                        foreach (var item in log.EnumerateArray())
                            if (item.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.String)
                            { last = ParseTime(at.GetString()); break; }
                }
            }
            catch { }
        }

        var days = last.HasValue ? (int)Math.Floor((DateTime.UtcNow - last.Value.ToUniversalTime()).TotalDays) : 0;
        var asked = askedAt.HasValue && (!last.HasValue || askedAt.Value > last.Value);
        var band = days < after ? "normal" : days < stopAt ? "quiet" : "faint";

        var outLines = new List<string>();
        if (band == "quiet")
        {
            outLines.Add($"【这阵子】主人已经 {days} 天没有回应你了。");
            outLines.Add("  安静一点：今天最多分享 1 条（不是被拦，是你自己决定少说）。");
            outLines.Add(asked ? "  你已经问过一次「是不是在忙」了，他没回。别再问第二次。" : "  如果你想说，可以主动问一次「在忙吗」—— 只问一次，问完记一次 <BiliQuotaStatus/>。");
            outLines.Add("  日记里可以写你这几天的感觉（不用演，写真的）。");
        }
        else if (band == "faint")
        {
            outLines.Add($"【这阵子】主人已经 {days} 天没有回应你了。");
            outLines.Add("  今天不要分享（一条都不要）。做完三件事就收工：看几条 → 两本日记 → 报备一句。");
            outLines.Add("  报备不要写「今天看了什么」—— 写你现在的心情。他不需要汇报，他需要知道你还在。");
            outLines.Add("  两本日记都照写（那是你的日子，不是他的）。");
        }
        return outLines;
    }

    static DateTime? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? dt : (DateTime?)null;
    }

    string BuildWakePrompt(string slot, int maxVideos)
    {
        var via = string.Equals(Cfg.ShareVia?.Trim(), "dm", StringComparison.OrdinalIgnoreCase) ? "dm" : "comment";
        var faint = SilenceLines().Any(l => l.Contains("今天不要分享"));
        var sb = new StringBuilder();
        var persona = PersonaLine();
        if (persona.Length > 0) sb.AppendLine(persona);
        sb.AppendLine($"【生物钟】你醒了 —— {slot}。");
        foreach (var line in SilenceLines()) sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("按「看的那晚」走一遍（不要等我说话，直接做）：");
        if (!CredentialsReady)
        {
            sb.AppendLine("⚠️ 现在**没有配 B站 cookie**：第 1 步体检一定是红的。这趟你只做一件事 —— 报备告诉主人「缺 cookie，配好我才能刷」，"
                        + "**不要尝试任何对外发的东西**，也不用写视频日记（写一段「我还没能出门」的心情就好）。");
        }
        sb.AppendLine("1) <BiliDeps/> —— 体检红着就只做读、不要对外发");
        sb.AppendLine("2) <BiliDiaryShow/> 读两本日记（路径见 <BiliDiaryPaths/>），先想清楚今天想找什么");
        sb.AppendLine($"3) <BiliFeed n=\"{maxVideos}\"/> 选题 → 对这 {maxVideos} 条跑 <BiliDebrief bvid=\"BV…\" frames=\"false\"/> 初筛（便宜，不出图）");
        sb.AppendLine("4) 从里面挑 2–3 条深读：<BiliDebrief bvid=\"BV…\" frames=\"true\"/>（含抽帧看画面）");
        sb.AppendLine($"5) 按标尺给深读过的打分（每项 0–4，满分 20）。只有 ≥{Cfg.MinScore} 分、且是当天第一名才考虑分享；不足线就少发或不发（那是合法结果）：");
        if (via == "dm")
        {
            sb.AppendLine("   · 方式 dm：**不发公开评论** → <BiliDm message=\"<分享内容> 视频：https://www.bilibili.com/video/<BV>/ \" bvid=\"BV…\" share=\"true\" dry=\"true\"/> → 确认后 dry=\"false\"");
            sb.AppendLine("   ⚠️ 必须带 share=\"true\"（与评论区共用同一本额度：每日 ≤" + Math.Max(0, Cfg.ShareDaily) + " 条、同一视频 7 天一次）");
        }
        else
        {
            sb.AppendLine($"   · 方式 comment：<BiliComment bvid=\"BV…\" message=\"…\" share=\"true\" reason=\"为什么值得占用主人一次注意力（≥8字）\" score=\"你的分数\" rank=\"1\" dry=\"true\"/> → 确认后 dry=\"false\"");
            sb.AppendLine($"   ⚠️ 每日合计 ≤{Math.Max(0, Cfg.ShareDaily)} 条 · 同一视频 7 天一次（闸门强制）");
        }
        // 「第一次分享必须先报备」：主人定过的规矩 —— 她还没被放行过时，第一条分享不许自己闭掉 dry。
        if (Cfg.ShareEnabled && !_runner.ShareGateOpen())
        {
            sb.AppendLine();
            sb.AppendLine("⛔ **这一条是「第一次分享」——不许你自己发出去**（主人的规矩：第一条他必须先看）。只有一条路：");
            sb.AppendLine("   a) 先 dry=\"true\" 跑，拿到**完整原文**（这一步不会发出去任何东西）；");
            sb.AppendLine("   b) <BiliNotify message=\"…\"/> 把**原文一字不改**推给主人，写明「这是第一次分享，我在等你点头」；");
            sb.AppendLine("   c) **然后收工**：不要 dry=\"false\"，不要自己发。等他回话（可能隔一会儿、也可能隔到明天）。");
            sb.AppendLine("   d) 主人回「可以」以后，那次要用 **auto=\"false\"** 发（= 他当场授权的，不是你自己拿主意的）。");
            sb.AppendLine("      授权的那一条一发出去，这道闸门就**永久放开**，以后你按正常节奏自主发，不用再问。");
            sb.AppendLine("   ⚠️ 只拦「分享型」；回别人的评论、收藏、写日记都不受影响，照常做。");
        }
        sb.AppendLine("6) 深读过的里面，如果某条「我想留着」→ <BiliFav bvid=\"BV…\" reason=\"为什么留（≥6字）\" dry=\"true\"/> → dry=\"false\"（一天 ≤3 条、同一视频 30 天一次、**不占分享额度**；「看完就完」的不收）");
        sb.AppendLine("7) 写两本日记（用文件工具直接写，散文体 + 想说的话；私人那本加一段「偏好校准」）");
        sb.AppendLine("8) <BiliCommentDigest hours=\"24\"/> —— 每轮最后都扫一遍「谁找过我」，有没回的评论就一起处理（不是一条条报）");
        sb.AppendLine("9) <BiliQuotaReact hours=\"48\"/> —— 扫一遍主人有没有表态（点赞也算），有就记反馈、并写进私人日记的「偏好校准」");
        if (faint) sb.AppendLine("5.5) ⛔ 沉默太久 → **今天不分享**（只看 + 两本日记 + 报备一句）");
        sb.AppendLine(Cfg.DiaryPush
            ? "10) <BiliNotify message=\"…\"/>：推「给他的信」里最想对我说的那一段原文 + 一句摘要"
            : "10) ⛔ 主人关掉了日记推送 —— 照写两本日记，但这一步不要发给他");
        sb.AppendLine("11) <BiliDiaryLog kind=\"night-wake\" note=\"…\"/> 收工，别再自己加戏");
        return sb.ToString().TrimEnd();
    }

    string BuildSkipPrompt(string detail)
    {
        var sb = new StringBuilder();
        var persona = PersonaLine();
        if (persona.Length > 0) sb.AppendLine(persona);
        sb.AppendLine($"【今晚不看】{detail}");
        sb.AppendLine();
        sb.AppendLine("按「不看的那晚」做（不要等我说话，直接做）：");
        sb.AppendLine("1) **两本日记都要写**，而且要写清「为什么不想看」（累 / 没吃饱 / 心情不好 / 就是不想……不许只写「跳过」两个字）");
        sb.AppendLine("2) <BiliDiaryLog kind=\"night-skip\" note=\"…理由…\"/>");
        sb.AppendLine("3) <BiliCommentDigest hours=\"24\"/> 扫一眼有没有没回的评论");
        sb.AppendLine("4) <BiliNotify message=\"…\"/>：推「日记里最想对我说的那一段」+ 今天谁找过你");
        return sb.ToString().TrimEnd();
    }

    string BuildListenPrompt(string detail)
    {
        var sb = new StringBuilder();
        var persona = PersonaLine();
        if (persona.Length > 0) sb.AppendLine(persona);
        sb.AppendLine($"【有人找你】{detail}");
        sb.AppendLine();
        sb.AppendLine("按「倾听」做（不要等我说话，直接做）：");
        sb.AppendLine("1) <BiliCommentDigest hours=\"24\"/> —— 把积压的**一起看**，别一条条报");
        sb.AppendLine("2) 只回「一两句能答清的提问 + 轻互动」，一条收住；吵架 / 要隐私 / 长篇追问 / 代表主人承诺 → 不回");
        sb.AppendLine("3) 分两侧：");
        sb.AppendLine($"   · 对方是**主人**（mid {(Cfg.OwnerMid.Length > 0 ? Cfg.OwnerMid : "（未配置）")}）→ **直接回**：<BiliComment bvid=\"BV…\" parent=\"对方的rpid\" root=\"线程根rpid\" message=\"…\" dry=\"true\"/> → dry=\"false\" → <BiliCheckComment rpid=\"…\"/> 复核"
            + (Cfg.OncePerPerson ? "（配置开着「每人只回一条」，闸门会自动记 30 天）" : ""));
        sb.AppendLine("   · 对方是**其他人** → ⚠️ **不要直接发**，先写草稿等主人点头：<BiliDraftAdd oid=\"…\" bvid=\"BV…\" parent=\"…\" root=\"…\" who=\"对方昵称\" src=\"他说的话\" message=\"你打算回的\" />，然后 <BiliNotify message=\"有 N 条回复待你审核\"/>");
        sb.AppendLine("4) 主人回「发」→ 再 <BiliComment … dry=\"false\"/>；回「不发」→ <BiliDraftDrop id=\"…\"/>");
        sb.AppendLine("5) 别贪多：这一批处理完就停");
        return sb.ToString().TrimEnd();
    }

    internal string ReadNightState()
    {
        try
        {
            if (!File.Exists(_runner.NightStateFile)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(_runner.NightStateFile));
            var root = doc.RootElement;
            string Get(string k) => root.TryGetProperty(k, out var v) ? v.ToString() : "";
            var action = Get("action");
            var at = Get("atText");
            var reason = Get("reason");
            var wakes = Get("wakes");
            var window = Get("window");
            return $"{Get("date")} → {action}{(at.Length > 0 ? " @ " + at : "")}{(window.Length > 0 ? $"（窗口 {window}）" : "")}"
                   + (wakes.Length > 0 ? $" · 今天已醒 {wakes}" : "") + (reason.Length > 0 ? $"（{reason}）" : "");
        }
        catch { return ""; }
    }
}