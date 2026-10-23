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
    Task? _mentionTask;

    // 官方表情转义符清单（启动时拉一次，缓存在内存里；拉不到就一直是空串，提示词里就不出现这一行）
    string _emoteHint = "";

    // v0.4.5 私信交付回执：已经 Poke 过、但 --ack 还没成功的那个 receipt。
    // 用途：下次轮询拿到同一条 pending 时**只重试确认，不重复叫她**（避免她答两遍）。
    string _dmPokedReceipt = "";

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
        => $"生物钟={(Cfg.ClockEnabled ? "开" : "关")} 评论区守夜={(Cfg.ListenComment ? "开" : "关")} 私信守夜={(Cfg.ListenDm ? "开" : "关")} @守夜={(Cfg.ListenMention ? "开" : "关")} 分享={(Cfg.ShareEnabled ? "开" : "关")}";

    void StartAutomation()
    {
        StopAutomation();
        _autoCts = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
        var ct = _autoCts.Token;

        if (Cfg.ClockEnabled) _clockTask = Task.Run(() => ClockLoopAsync(ct), CancellationToken.None);
        else _runner.Log("生物钟开关是关的：她只在你叫她时才动");

        if (Cfg.ListenComment) _commentTask = Task.Run(() => CommentLoopAsync(ct), CancellationToken.None);
        if (Cfg.ListenDm) _dmTask = Task.Run(() => DmLoopAsync(ct), CancellationToken.None);
        if (Cfg.ListenMention) _mentionTask = Task.Run(() => MentionLoopAsync(ct), CancellationToken.None);

        // 顺手把"这个号能用的官方表情"拉一份（只读、失败就静默），供评论/回复提示词用
        _ = Task.Run(async () =>
        {
            try
            {
                var emotes = await FetchEmoteCodesAsync(ct);
                _emoteHint = BuildEmoteHint(emotes);
                _runner.Log($"官方表情清单：{emotes.Count} 个转义符" + (_emoteHint.Length > 0 ? "（已注入提示词）" : "（为空，不注入）"));
            }
            catch { }
        }, CancellationToken.None);

        _runner.Log($"自动化已启动：生物钟={Cfg.ClockEnabled} · 评论区守夜={Cfg.ListenComment} · 私信守夜={Cfg.ListenDm} · @守夜={Cfg.ListenMention}");

        // ══════════ 【临时·⑰-6 诊断】把外置读到的东西打出来（长度/哈希/尾字节/冻结词计数），确认后删 ══════════
        if (!_promptCmpDone)
        {
            _promptCmpDone = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    foreach (var p in new[]
                    {
                        ("guide", PromptBook.Text("guide", GuideVarsForCmp())),
                        ("persona", PromptBook.Text("persona", PersonaVars())),
                        ("wake-flow", string.Join("\n", PromptBook.Lines("wake-flow", WakeVars(6, Cfg.MinScore)))),
                        ("wake-rules", string.Join("\n", PromptBook.Lines("wake-rules"))),
                        ("wake-tail", string.Join("\n", PromptBook.Lines("wake-tail"))),
                        ("wake-end", string.Join("\n", PromptBook.Lines("wake-end"))),
                        ("dm-rules", string.Join("\n", PromptBook.Lines("dm-rules"))),
                        ("skip", string.Join("\n", PromptBook.Lines("skip"))),
                        ("mention", string.Join("\n", PromptBook.Lines("mention", new Dictionary<string, string> { ["ownerMid"] = OwnerMidText() }))),
                        ("listen", string.Join("\n", PromptBook.Lines("listen", new Dictionary<string, string> { ["ownerMid"] = OwnerMidText(), ["oncePerPerson"] = Cfg.OncePerPerson ? "（配置开着「每人只回一条」，闸门会自动记 30 天）" : "" }))),
                    })
                        _runner.Log($"[⑰-6块] {p.Item1}: {p.Item2.Length}B/{Sha8(p.Item2)}/尾0x{(p.Item2.Length > 0 ? (int)p.Item2[^1] : -1):X2}");
                    _runner.Log($"[⑰-6原样] guide 整段 {PromptBook.Text("guide", GuideVarsForCmp()).Length}B · persona 整段 {PromptBook.Text("persona", PersonaVars()).Length}B");

                    // 五个冻结锚点的**来源段**（外置文件级）——证明 ③ 落在 guide.txt、④⑤ 落在 guide+wake-rules ✓
                    _runner.Log("[⑰-6来源] 外置文件里的锚点分布：");
                    foreach (var k in new[] { "guide", "persona", "wake-flow", "wake-rules", "wake-tail", "wake-end", "dm-rules", "skip", "mention", "listen" })
                    {
                        var raw = PromptBook.Text(k);
                        _runner.Log($"[⑰-6来源]   {k}.txt: ①对照 {CountOf(raw, "对照］")} · ②4.5 {CountOf(raw, "4.5)")} · ③先分清 {CountOf(raw, "先分清谁是谁再写")} · ④当观众看完了 {CountOf(raw, "当观众看完了")} · ⑤过三个人 {CountOf(raw, "过三个人")} · {raw.Length}B");
                    }

                    var wake = BuildWakePrompt("23:47", 6);
                    foreach (var t in new[]
                    {
                        ("guide", PromptBook.Text("guide", GuideVarsForCmp())), ("wake", wake),
                        ("skip", BuildSkipPrompt("比对")), ("mention", BuildMentionPrompt(new List<BiliMention>(), 0)), ("listen", BuildListenPrompt("比对")),
                    })
                        _runner.Log($"[⑰-6计数] {t.Item1}: 长度 {t.Item2.Length}B/{Sha8(t.Item2)} · ①对照 {CountOf(t.Item2, "对照］")} · ②4.5 {CountOf(t.Item2, "4.5)")} · ③先分清 {CountOf(t.Item2, "先分清谁是谁再写")} · ④当观众看完了 {CountOf(t.Item2, "当观众看完了")} · ⑤过三个人 {CountOf(t.Item2, "过三个人")}");
                }
                catch (Exception e) { _runner.Log("[⑰-6] 诊断异常：" + e.Message, error: true); }
            }, CancellationToken.None);
        }
        // ══════════ 【临时块结束】 ══════════

        // 自检：把工具链读到的作息打出来。配置没被读到（例如 JSON 坏了）时，
        // 它会退回内置默认值（20:00–24:00 / 5 条 / 30%）—— 这里一眼就能看出来。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                var r = await _runner.RunAsync("night", new[] { "status" }, 60, ct);
                _runner.Log($"生物钟自检（工具链读到的作息）：{DescribeNightStatus(r.Text)}");
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

    /// <summary>
    /// 把 <c>night status</c> 的 JSON 压成**一行人话**（2026-10-22 修，v0.3.0 时代就是一行）。
    /// 为什么在这侧压：查过 v0.4.5 的 <c>tools/commands/whale-night.mjs</c> / <c>lib/night-plan.mjs</c> /
    /// <c>lib/cli-args.mjs</c> —— <c>night status</c> 只有 <c>JSON.stringify(…, null, 2)</c> 一种输出，
    /// **没有**「人类可读 / 非 JSON」开关，所以最小改动就是在自检这里解析成一行。
    /// 解析失败 → 退回原始输出的头两行：自检**不许崩**，也不许静默吞掉内容。
    /// </summary>
    static string DescribeNightStatus(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return "（工具链没输出）";
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var cfg = root.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            var st = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;

            // ⚠️ 目标框架的 JsonElement **没有** TryGetValue<T>（实测编译报 CS1061）→ 一律用 TryGetInt32/Double
            static string StrOf(JsonElement o, string k)
                => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var e) ? (e.ToString() ?? "") : "";
            static int IntOf(JsonElement o, string k, int fallback)
                => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var e)
                   && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : fallback;
            static double DblOf(JsonElement o, string k, double fallback)
                => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var e)
                   && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) ? d : fallback;

            var wins = new List<string>();
            var wakeSum = 0;
            if (cfg.ValueKind == JsonValueKind.Object
                && cfg.TryGetProperty("windows", out var ws) && ws.ValueKind == JsonValueKind.Array)
                foreach (var w in ws.EnumerateArray())
                {
                    if (w.ValueKind != JsonValueKind.Object) continue;
                    var n = IntOf(w, "wakes", 1);
                    wakeSum += Math.Max(0, n);
                    wins.Add($"{StrOf(w, "start")}–{StrOf(w, "end")} ×{n}");
                }
            var cap = IntOf(cfg, "maxWakesPerDay", wins.Count > 0 ? wakeSum : 0);
            var skip = DblOf(cfg, "skipChance", 0);
            var mv = IntOf(cfg, "maxVideos", 0);
            var woke = IntOf(st, "wakes", 0);

            var sb = new StringBuilder();
            sb.Append($"配置: 窗口 {(wins.Count > 0 ? string.Join(" / ", wins) : "无")}");
            sb.Append($" | 今天最多醒 {cap} 次");
            sb.Append($" | 不看概率 {skip * 100:0}%");
            sb.Append($" | 一次初筛 {mv} 条");
            sb.Append($" | 今天已醒: {woke}/{cap}");
            if (st.ValueKind == JsonValueKind.Object)
            {
                var at = StrOf(st, "atText");
                var act = StrOf(st, "action");
                var why = StrOf(st, "reason");
                if (at.Length > 0 || act.Length > 0) sb.Append($" · 计划: {at} {act}".TrimEnd());
                if (why.Length > 0) sb.Append($"（{why}）");
            }
            return sb.ToString();
        }
        catch
        {
            return string.Join(" | ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(2));
        }
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

                // ④ v0.4.5 交付回执（2026-10-22）：--peek 让工具链把待交付消息**持久化**并给一个 receipt，
                //    我们**成功 Poke 之后**才 --ack 确认。这样"取到了但没推进她上下文"不会丢消息 ✓
                var r = await _runner.RunAsync("dm-await", new[] { "--once", "--json", "--peek" }, 90, ct);
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
                            var receipt = root.TryGetProperty("receipt", out var rc) ? (rc.GetString() ?? "") : "";
                            if (text != null && text.Length > 500) text = text[..500];

                            if (receipt.Length > 0 && receipt == _dmPokedReceipt)
                            {
                                // 这条上次已经推给她了（只是 ack 没成功）→ 只重试确认，不再叫她 ✗
                                await AckDmAsync(receipt, ct);
                            }
                            else
                            {
                                _runner.Log($"主人私信：{name}（mid {mid}）");
                                var poked = false;
                                try
                                {
                                    interactor.Poke($"{PersonaLine()}\n【主人私信】{name}（mid {mid}）刚给你发私信：\n\n{text}\n\n"
                                        + "按「专属通道」做：\n1) 这是主人 → **直接回，不用审核**：<BiliDm message=\"…\" dry=\"true\"/> → dry=\"false\"\n"
                                        + "2) 短一点、像聊天（一两句就够）\n"
                                        + "3) 🔎 **如果他发来的是 B站 链接或 BV 号** → 先 <BiliDeepRead bvid=\"BV…\"/> **看完**"
                                        + "（有字幕读字幕；没字幕翻弹幕与评论区；再不行靠画面）→ 然后回一句**具体的看法**："
                                        + "点名到片子里的具体内容（谁做的、哪一秒、哪句台词），**别只说「收到」、别复述标题**。"
                                        + "看完觉得值得留 → 可以 <BiliFav bvid=\"BV…\" reason=\"…\"/>（走它自己那五条判据）\n"
                                        + "4) 回完写进私人日记的「偏好校准」，并 <BiliDiaryLog kind=\"dm-reply\" note=\"…\"/>\n"
                                        + "5) 若因未互关等原因**回不了私信** → 用 <QChat type=\"Private\" targetid=\"…\"> 在 QQ 告诉主人你看完了什么");
                                    poked = true;
                                }
                                catch (Exception pe)
                                {
                                    // ★ 绝不吞掉：不 ack → dm-await 的 pending 还在 → 下次重投这条消息
                                    _runner.Log($"私信取到了但推给她失败（**不确认**，下次会重投）：{pe.Message}", error: true);
                                }
                                if (poked)
                                {
                                    if (receipt.Length > 0) { _dmPokedReceipt = receipt; await AckDmAsync(receipt, ct); }
                                    else _runner.Log("私信已推给她，但工具链没给 receipt（旧版 dm-await？）→ 不 ack，等它自己推进", error: true);
                                }
                            }
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

    /// <summary>
    /// 确认一条私信**已经交付到她上下文**（v0.4.5 的 receipt 回执；工具链侧只推进已读水位线，不访问网络）。
    /// 只有 <see cref="DmLoopAsync"/> 成功 Poke 之后才会调它 —— Poke 失败/异常时**绝不**调用，
    /// 这样 <c>dm-await</c> 的 pending 仍在，下次轮询会把同一条消息重投回来（不丢消息）。
    /// </summary>
    async Task AckDmAsync(string receipt, CancellationToken ct)
    {
        try
        {
            var a = await _runner.RunAsync("dm-await", new[] { "--ack", receipt }, 30, ct);
            if (a.ExitCode == 0 && (a.Out ?? "").Contains("私信交付已确认"))
            {
                _dmPokedReceipt = "";
                _runner.Log("私信交付已确认（receipt 已推进已读水位线）");
            }
            else
            {
                _runner.Log($"私信确认未成功（下次会重投这条）：{(a.Text ?? "").Trim()}", error: true);
            }
        }
        catch (Exception e) { _runner.Log($"私信确认异常（下次会重投这条）：{e.Message}", error: true); }
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
        // 身份对照表：只在这里出现 B站昵称与 mid，而本函数被四个叫醒词共用（唤醒/跳过/被@/倾听），
        // 一处改动即可覆盖所有触发场景 —— 治「分不清视频里的人代指谁」。
        // 身份对照表：文本已外置到 Prompts\persona.txt（2026-10-23 任务⑰-6），这里只填占位符 ✓
        var map = "";
        if (!string.IsNullOrWhiteSpace(Cfg.SelfName) || !string.IsNullOrWhiteSpace(Cfg.OwnerName))
        {
            // 文件以换行结尾（文本文件的常态），而原字面量没有 → 这里去掉**恰好一个** ✓（不动用户其它空白）
            map = PromptBook.Text("persona", PersonaVars());
            if (map.EndsWith("\n", StringComparison.Ordinal)) map = map[..^1];
        }
        var head = bits.Count > 0 ? $"［身份］{string.Join("，", bits)}。（以此为准，别用别的称呼）" : "";
        return head + map;
    }

    /// <summary>persona.txt 的占位符（⑰-6）</summary>
    Dictionary<string, string> PersonaVars() => new()
    {
        ["SelfName"] = Cfg.SelfName ?? "", ["SelfMid"] = Cfg.SelfMid ?? "",
        ["OwnerName"] = Cfg.OwnerName ?? "", ["OwnerMid"] = Cfg.OwnerMid ?? "",
    };

    /// <summary>唤醒流程占位符（⑰-6）</summary>
    static Dictionary<string, string> WakeVars(int maxVideos, int minScore) => new()
    {
        ["maxVideos"] = maxVideos.ToString(), ["minScore"] = minScore.ToString(),
    };

    /// <summary>主人 mid 的显示文本（原逻辑：没配就写"（未配置）"）</summary>
    string OwnerMidText() => Cfg.OwnerMid.Length > 0 ? Cfg.OwnerMid : "（未配置）";

    /// <summary>逐行追加外置提示词（⑰-6）—— 空行照旧保留 ✓</summary>
    void AppendLines(StringBuilder sb, string key, IReadOnlyDictionary<string, string>? vars = null)
    {
        foreach (var line in PromptBook.Lines(key, vars)) sb.AppendLine(line);
    }

    // ── 【临时·⑰-6】比对用的小工具（确认零差异后连同 _promptCmpDone 一起删）──
    static bool _promptCmpDone;
    Dictionary<string, string> GuideVarsForCmp() => new()
    {
        ["DiaryDir"] = _runner.DiaryDir, ["DiaryFile"] = _runner.DiaryFile, ["PrivateDiaryFile"] = _runner.PrivateDiaryFile,
        ["OwnerMid"] = Cfg.OwnerMid, ["SelfName"] = Cfg.SelfName, ["SelfMid"] = Cfg.SelfMid, ["OwnerName"] = Cfg.OwnerName,
    };
    static string Sha8(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)))[..8];
    }

    static int CountOf(string s, string sub)
    {
        var c = 0; var i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { c++; i += sub.Length; }
        return c;
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
        // 外置（⑰-6）：wake-flow.txt = 唤醒主流程的静态行；第 0 行在"没 cookie"分支之前，第 1 行起在其后 ✓
        var wakeFlow = PromptBook.Lines("wake-flow", WakeVars(maxVideos, Cfg.MinScore)).ToList();
        if (wakeFlow.Count > 0) sb.AppendLine(wakeFlow[0]);
        if (!CredentialsReady)
        {
            sb.AppendLine("⚠️ 现在**没有配 B站 cookie**：第 1 步体检一定是红的。这趟你只做一件事 —— 报备告诉主人「缺 cookie，配好我才能刷」，"
                        + "**不要尝试任何对外发的东西**，也不用写视频日记（用 <BiliDiaryWrite to=\"me\" text=\"…\"/> 写一段「我还没能出门」的心情就好）。");
        }
        for (var i = 1; i < wakeFlow.Count; i++) sb.AppendLine(wakeFlow[i]);
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
        // ⑥⑦ 私信是**双向通道**（2026-10-22 主人定）：
        //   · 她 → 主人：允许**主动**找他说话（不设条数上限，但有分寸）；也可以私下推视频给他看
        //   · 界线：公开评论/动态 = 走评分线 + 每日分享额度；「跟主人说话」（不带 share）= 不占额度、不用报备
        sb.AppendLine();
        AppendLines(sb, "dm-rules");
        AppendLines(sb, "wake-rules");
        AppendLines(sb, "wake-tail");
        if (faint) sb.AppendLine("5.5) ⛔ 沉默太久 → **今天不分享**（只看 + 两本日记 + 报备一句）");
        sb.AppendLine(Cfg.DiaryPush
            ? "10) <BiliNotify message=\"…\"/>：推「给他的信」里最想对我说的那一段原文 + 一句摘要"
            : "10) ⛔ 主人关掉了日记推送 —— 照写两本日记，但这一步不要发给他");
        AppendLines(sb, "wake-end");
        return sb.ToString().TrimEnd();
    }

    string BuildSkipPrompt(string detail)
    {
        var sb = new StringBuilder();
        var persona = PersonaLine();
        if (persona.Length > 0) sb.AppendLine(persona);
        sb.AppendLine($"【今晚不看】{detail}");
        sb.AppendLine();
        AppendLines(sb, "skip");
        return sb.ToString().TrimEnd();
    }

    // ==================== @我的 守夜 ====================
    // 只用 C# 直读 x/msgfeed/at（只读接口、零 token），命中就当场叫醒她。
    // 水位线存在 mention-state.json：at_time 是主键，SeenIds 兜住同一秒里的多条。

    sealed class MentionMark
    {
        public long LastAt { get; set; }
        public List<string> SeenIds { get; set; } = new();
    }

    MentionMark LoadMentionMark()
    {
        try
        {
            if (File.Exists(_runner.MentionStateFile))
            {
                var m = JsonSerializer.Deserialize<MentionMark>(File.ReadAllText(_runner.MentionStateFile));
                if (m != null)
                {
                    m.SeenIds ??= new List<string>();
                    return m;
                }
            }
        }
        catch (Exception e) { _runner.Log($"@水位线读不出来（当第一次跑）：{e.Message}", error: true); }
        // 第一次跑：按配置往回补看 N 小时（0=只看启动之后的）
        return new MentionMark
        {
            LastAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Math.Max(0, Cfg.MentionCatchUpHours) * 3600L
        };
    }

    void SaveMentionMark(MentionMark mark)
    {
        try
        {
            if (mark.SeenIds.Count > 60) mark.SeenIds = mark.SeenIds.TakeLast(60).ToList();
            File.WriteAllText(_runner.MentionStateFile, JsonSerializer.Serialize(mark), new UTF8Encoding(false));
        }
        catch (Exception e) { _runner.Log($"@水位线写不进去：{e.Message}", error: true); }
    }

    async Task MentionLoopAsync(CancellationToken ct)
    {
        var interval = Math.Max(20, Cfg.MentionIntervalSec);
        var mark = LoadMentionMark();
        var firstPoll = true;
        _runner.Log($"@守夜启动：间隔 {interval} 秒 · 全天盯={Cfg.MentionAllDay} · 只认@她本人={Cfg.MentionOnlySelf} · 启动补看 {Cfg.MentionCatchUpHours} 小时");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Cfg.ListenMention) { await Task.Delay(TimeSpan.FromMinutes(1), ct); continue; }
                if (!CredentialsReady) { await Task.Delay(TimeSpan.FromMinutes(2), ct); continue; }
                if (!Cfg.MentionAllDay && !InWindow(DateTime.Now))
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), ct);
                    continue;
                }

                // 给这次读取单独加超时：网络卡住别把整个循环拖死（超时只跳过本轮，不等于插件在关闭）
                List<BiliMention> all;
                using (var inner = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    inner.CancelAfter(TimeSpan.FromSeconds(interval + 30));
                    try { all = await FetchMentionsAsync(inner.Token, quiet: false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _runner.Log("@收件箱读取超时，本轮跳过");
                        await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                        continue;
                    }
                }
                if (firstPoll)
                {
                    firstPoll = false;
                    _runner.Log($"@收件箱首轮：共 {all.Count} 条，其中 @她本人 {all.Count(x => x.AtMe)} 条（水位线 {mark.LastAt}）");
                }
                var fresh = new List<BiliMention>();
                foreach (var m in all)
                {
                    if (Cfg.MentionOnlySelf && !m.AtMe) continue;          // 收件箱里有别人被 @ 的，不算
                    if (m.AtTime < mark.LastAt) continue;
                    if (m.AtTime == mark.LastAt && mark.SeenIds.Contains(m.Id)) continue;
                    fresh.Add(m);
                }

                if (fresh.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                    continue;
                }

                fresh = fresh.OrderBy(m => m.AtTime).ToList();
                // 补看/积压可能一次涌进很多：只叫醒最近几条，其余记账不叫她（免得一醒来就是几十条）
                var wake = fresh.Take(5).ToList();
                foreach (var m in fresh)
                {
                    mark.LastAt = Math.Max(mark.LastAt, m.AtTime);
                    if (!mark.SeenIds.Contains(m.Id)) mark.SeenIds.Add(m.Id);
                }
                SaveMentionMark(mark);

                _runner.Log($"有人@她：本次 {fresh.Count} 条，叫醒 {wake.Count} 条");
                interactor.Poke(BuildMentionPrompt(wake, fresh.Count));
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _runner.Log($"@守夜循环异常：{e.Message}", error: true);
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch { break; }
            }
        }
    }

    string BuildMentionPrompt(List<BiliMention> items, int total)
    {
        var sb = new StringBuilder();
        var persona = PersonaLine();
        if (persona.Length > 0) sb.AppendLine(persona);
        sb.AppendLine($"【有人 @ 你】{total} 条" + (total > items.Count ? $"（先处理最近 {items.Count} 条，其余留着下轮）" : ""));
        sb.AppendLine();
        foreach (var m in items)
        {
            sb.AppendLine($"· {m.WhoName}（mid {m.WhoMid}）在 {m.Business}「{m.VideoTitle}」里 @ 了你：");
            sb.AppendLine($"    「{Shorten(m.Text, 500)}」");
            var where = new StringBuilder("    怎么回：");
            if (m.Aid > 0 && m.SourceId > 0)
            {
                var rootArg = m.RootId > 0 ? $"root=\"{m.RootId}\"" : $"root=\"{m.SourceId}\"";
                where.Append($"oid={m.Aid} parent=\"{m.SourceId}\" {rootArg}");
                where.Append($" → <BiliComment oid=\"{m.Aid}\" parent=\"{m.SourceId}\" {rootArg} message=\"…\" dry=\"true\"/>");
            }
            else
            {
                if (m.Aid > 0) where.Append($"oid={m.Aid} ");
                if (m.RootId > 0) where.Append($"root={m.RootId} ");
                if (m.SourceId > 0) where.Append($"parent={m.SourceId} ");
            }
            sb.AppendLine(where.ToString().TrimEnd());
            if (m.Uri.Length > 0) sb.AppendLine($"    {m.Uri}");
        }
        sb.AppendLine();
        sb.AppendLine("按「倾听」做（不要等我说话，直接做）：");
        AppendLines(sb, "mention", new Dictionary<string, string> { ["ownerMid"] = OwnerMidText() });
        return sb.ToString().TrimEnd();
    }

    static string Shorten(string? s, int max)
    {
        s ??= "";
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>把表情清单压成一段提示：正文里写转义符，B站服务端自己渲染成官方表情。</summary>
    static string BuildEmoteHint(List<(string Pkg, string Code)> emotes)
    {
        if (emotes == null || emotes.Count == 0) return "";
        var codes = new List<string>();
        foreach (var e in emotes)
        {
            var c = (e.Code ?? "").Trim();
            if (c.Length == 0) continue;
            if (!c.StartsWith("[")) c = "[" + c + "]";
            if (!codes.Contains(c)) codes.Add(c);
            if (string.Join(" ", codes).Length > 600) break;   // 别把提示词撑爆
        }
        if (codes.Count == 0) return "";
        return "【能用的B站官方表情】正文里**直接写转义符**就会显示成表情（服务端自己解析，不需要别的参数）。⚠️ **只写这份清单里确实有的码**——清单外的（如 [害羞]、[笑哭]、[热]、[打call]）不会渲染成表情，会原样变成方括号文字，宁可不用。例如： "
               + string.Join(" ", codes)
               + " —— 只写上面这些（这是本号确实可用的），写别的不会显示。";
    }

    string BuildListenPrompt(string detail)
    {
        var sb = new StringBuilder();
        var persona = PersonaLine();
        if (persona.Length > 0) sb.AppendLine(persona);
        sb.AppendLine($"【有人找你】{detail}");
        sb.AppendLine();
        sb.AppendLine("按「倾听」做（不要等我说话，直接做）：");
        AppendLines(sb, "listen", new Dictionary<string, string>
        {
            ["ownerMid"] = OwnerMidText(),
            ["oncePerPerson"] = Cfg.OncePerPerson ? "（配置开着「每人只回一条」，闸门会自动记 30 天）" : "",
        });
        if (_emoteHint.Length > 0) sb.AppendLine(_emoteHint);
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