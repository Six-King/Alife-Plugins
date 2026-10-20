using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DaFeiYu.BiliWhale;

/// <summary>一次命令调用的结果。</summary>
internal sealed class BiliRunResult
{
    public bool Started { get; init; }
    public int ExitCode { get; init; }
    public string Out { get; init; } = "";
    public string Err { get; init; } = "";
    public string Failure { get; init; } = "";

    public bool Ok => Started && ExitCode == 0 && Failure.Length == 0;

    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            if (Failure.Length > 0) sb.AppendLine($"[运行失败] {Failure}");
            if (Out.Trim().Length > 0) sb.AppendLine(Out.Trim());
            if (Err.Trim().Length > 0) sb.AppendLine($"[stderr] {Err.Trim()}");
            var s = sb.ToString().Trim();
            return s.Length > 0 ? s : "（命令没有任何输出）";
        }
    }

    public static BiliRunResult Fail(string why) => new() { Started = false, ExitCode = -1, Failure = why };
}

/// <summary>一个长驻子进程（生物钟 / 守夜进程）。</summary>
internal sealed class BiliLongRun
{
    public Process Proc { get; init; } = null!;
    public Task<string> Out { get; init; } = null!;
    public Task<string> Err { get; init; } = null!;
}

/// <summary>
/// 运行环境层：定位 node/ffmpeg、准备状态目录与凭据、把工具链当外部进程调用。
///
/// 关键设计：dsh-bili-whale 工具链的所有状态路径都基于 <c>process.env.USERPROFILE</c>，
/// 配置路径可用 <c>BILI_WHALE_CONFIG</c> 覆盖 —— 所以**不改它一行代码**，
/// 只要给子进程换一套环境变量，就能把它的状态全部关进插件数据目录。
/// </summary>
internal sealed class BiliWhaleRunner
{
    /// <summary>★ 必须是不带 BOM 的 UTF-8：Node 的 JSON.parse 不认 BOM，
    /// 带 BOM 会让 bili-whale.config.json / credentials.json 解析失败并**静默退回默认值**。</summary>
    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    readonly ILogger _logger;
    readonly BiliWhaleConfig _cfg;

    public string PluginDir { get; }
    public string StateRoot { get; }
    public string HomeDir { get; }
    public string BiliAiDir { get; }
    public string DshDir { get; }
    public string ToolsDir { get; }
    public string BiliMjs { get; }
    public string ToolConfigFile { get; }
    public string LogFile { get; }
    public string NotifyLogFile { get; }

    public string NodeExe { get; private set; } = "";
    public string FfmpegPath { get; private set; } = "";

    public BiliWhaleRunner(string storageFolder, BiliWhaleConfig cfg, ILogger logger)
    {
        _logger = logger;
        _cfg = cfg;

        PluginDir = Path.Combine(storageFolder, "Plugins", "DaFeiYu.BiliWhale");
        StateRoot = string.IsNullOrWhiteSpace(cfg.StateRoot)
            ? Path.Combine(storageFolder, "PluginData", "DaFeiYu.BiliWhale")
            : Path.GetFullPath(cfg.StateRoot.Trim().Trim('"'));
        HomeDir = Path.Combine(StateRoot, "home");
        BiliAiDir = Path.Combine(HomeDir, ".bili-ai");
        DshDir = Path.Combine(HomeDir, ".dsh");
        ToolsDir = Path.Combine(PluginDir, "tools");
        BiliMjs = Path.Combine(ToolsDir, "bili.mjs");
        ToolConfigFile = Path.Combine(StateRoot, "bili-whale.config.json");
        LogFile = Path.Combine(StateRoot, "biliwhale.log");
        NotifyLogFile = Path.Combine(DshDir, "whale-notify.log");
    }

    public bool CredentialsPresent =>
        !string.IsNullOrWhiteSpace(_cfg.Sessdata) && !string.IsNullOrWhiteSpace(_cfg.BiliJct);

    public string DiaryFile => Path.Combine(DshDir, "whale-diary.md");
    public string PrivateDiaryFile => Path.Combine(DshDir, "whale-diary-private.md");
    public string NightStateFile => Path.Combine(DshDir, "whale-night.json");
    public string QuotaFile => Path.Combine(BiliAiDir, "quota.json");
    public string FeedbackFile => Path.Combine(BiliAiDir, "feedback.json");
    /// <summary>「第一次分享必须先报备」的闸门状态（主人授权发出过第一条分享 → 永久放开）。</summary>
    public string ShareGateFile => Path.Combine(StateRoot, "share-gate.json");

    // ==================== 探测 ====================

    public void Prepare()
    {
        Directory.CreateDirectory(StateRoot);
        Directory.CreateDirectory(HomeDir);
        Directory.CreateDirectory(BiliAiDir);
        Directory.CreateDirectory(DshDir);

        NodeExe = DetectNode();
        FfmpegPath = DetectFfmpeg();

        if (!File.Exists(BiliMjs))
            Log($"工具链缺失：{BiliMjs}（把 dsh-bili-whale 的 tools/ 放进插件目录）", error: true);
        Log($"运行环境就绪 · node={NodeExe} · ffmpeg={(FfmpegPath.Length > 0 ? FfmpegPath : "（未找到：抽帧看画面这一路会跳过）")}");
        Log($"状态目录：{StateRoot}");

        SyncCredentials();
        WriteToolConfig();
    }

    string DetectNode()
    {
        var cands = new List<string>();
        if (!string.IsNullOrWhiteSpace(_cfg.NodeExe)) cands.Add(_cfg.NodeExe.Trim().Trim('"'));
        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        var dshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrWhiteSpace(dshHome))
            cands.Add(Path.Combine(dshHome, "dsh-runtimes", "dsh-primary-runtime", "dependencies", "node", "bin", "node.exe"));
        if (home.Length > 0)
            cands.Add(Path.Combine(home, ".dsh", "dsh-runtimes", "dsh-primary-runtime", "dependencies", "node", "bin", "node.exe"));
        if (local.Length > 0)
            cands.Add(Path.Combine(local, "Programs", "nodejs", "node.exe"));
        cands.Add(@"C:\Program Files\nodejs\node.exe");
        cands.Add(@"C:\Program Files (x86)\nodejs\node.exe");

        foreach (var c in cands)
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c;

        var onPath = Where("node.exe");
        if (onPath.Length > 0) return onPath;

        // 兜底：插件目录旁的 Runtime 里翻一个
        try
        {
            var storage = Directory.GetParent(Path.GetDirectoryName(Path.GetDirectoryName(PluginDir)!)!)?.FullName;
            if (!string.IsNullOrWhiteSpace(storage))
            {
                var runtime = Path.Combine(storage, "Runtime");
                if (Directory.Exists(runtime))
                {
                    var hit = Directory.EnumerateFiles(runtime, "node.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
            }
        }
        catch { }

        return "";
    }

    string DetectFfmpeg()
    {
        var cands = new List<string>();
        if (!string.IsNullOrWhiteSpace(_cfg.FfmpegPath)) cands.Add(_cfg.FfmpegPath.Trim().Trim('"'));
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        if (local.Length > 0)
        {
            cands.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
            try
            {
                var pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(pkgs))
                    foreach (var dir in Directory.EnumerateDirectories(pkgs, "Gyan.FFmpeg*"))
                        cands.AddRange(Directory.EnumerateFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories));
            }
            catch { }
            try
            {
                var jy = Path.Combine(local, "JianyingPro", "Apps");
                if (Directory.Exists(jy))
                    foreach (var dir in Directory.EnumerateDirectories(jy))
                        cands.Add(Path.Combine(dir, "ffmpeg.exe"));
            }
            catch { }
        }
        cands.Add(Path.Combine(PluginDir, "bin", "ffmpeg.exe"));
        cands.Add(Path.Combine(DshDir, "bili-whale", "bin", "ffmpeg.exe"));

        foreach (var c in cands)
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c;

        return Where("ffmpeg.exe");
    }

    static string Where(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return text.Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0 && File.Exists(s)) ?? "";
        }
        catch { return ""; }
    }

    // ==================== 配置与凭据同步 ====================

    /// <summary>凭据单独落盘（工具链只认这个文件）；只写不读回，任何地方都不回显。</summary>
    public void SyncCredentials()
    {
        try
        {
            Directory.CreateDirectory(BiliAiDir);
            var obj = new Dictionary<string, string>
            {
                ["sessdata"] = _cfg.Sessdata?.Trim() ?? "",
                ["bili_jct"] = _cfg.BiliJct?.Trim() ?? "",
                ["buvid3"] = _cfg.Buvid3?.Trim() ?? "",
            };
            File.WriteAllText(Path.Combine(BiliAiDir, "credentials.json"),
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }) + "\n", Utf8NoBom);
        }
        catch (Exception e) { Log($"写凭据失败：{e.Message}", error: true); }
    }

    /// <summary>把插件配置翻译成工具链的统一配置（bili-whale.config.json）。</summary>
    public void WriteToolConfig()
    {
        try
        {
            var windows = ParseWindows(_cfg.NightWindows, Math.Max(1, _cfg.MaxVideos));
            var cfg = new Dictionary<string, object?>
            {
                ["persona"] = new Dictionary<string, object?>
                {
                    ["name"] = _cfg.PersonaName ?? "",
                    ["ownerTitle"] = _cfg.OwnerTitle ?? "主人",
                    ["selfClaim"] = _cfg.SelfClaim ?? "我",
                },
                ["owner"] = new Dictionary<string, object?>
                {
                    ["mid"] = _cfg.OwnerMid?.Trim() ?? "",
                    ["name"] = _cfg.OwnerName?.Trim() ?? "",
                },
                ["self"] = new Dictionary<string, object?>
                {
                    ["mid"] = _cfg.SelfMid?.Trim() ?? "",
                    ["name"] = _cfg.SelfName?.Trim() ?? "",
                },
                ["preferences"] = new Dictionary<string, object?>
                {
                    ["like"] = SplitTags(_cfg.LikeTags),
                    ["dislike"] = SplitTags(_cfg.DislikeTags),
                },
                ["night"] = new Dictionary<string, object?>
                {
                    ["windows"] = windows,
                    ["skipChance"] = Clamp(_cfg.SkipChance, 0, 0.95),
                    ["maxVideos"] = Clamp(_cfg.MaxVideos, 1, 20),
                    ["maxWakesPerDay"] = windows.Count == 0 ? 1 : windows.Sum(w => (int)w["wakes"]!),
                },
                ["quota"] = new Dictionary<string, object?> { ["shareDaily"] = Clamp(_cfg.ShareDaily, 0, 20) },
                ["share"] = new Dictionary<string, object?>
                {
                    ["enabled"] = _cfg.ShareEnabled,
                    ["via"] = NormalizeVia(_cfg.ShareVia),
                    ["minScore"] = Clamp(_cfg.MinScore, 0, 20),
                    ["silentAfterDays"] = Math.Max(1, _cfg.SilentAfterDays),
                    ["silentStopDays"] = Math.Max(2, _cfg.SilentStopDays),
                    ["decayPerDay"] = 1,
                },
                ["diary"] = new Dictionary<string, object?> { ["push"] = _cfg.DiaryPush, ["enabled"] = true },
                ["listen"] = new Dictionary<string, object?>
                {
                    ["comment"] = _cfg.ListenComment,
                    ["commentIntervalSec"] = Math.Max(60, _cfg.CommentIntervalSec),
                    ["scope"] = NormalizeScope(_cfg.ListenScope),
                    ["oncePerPerson"] = _cfg.OncePerPerson,
                    ["allDay"] = !_cfg.ListenInWindow,
                    ["dm"] = _cfg.ListenDm,
                    ["live"] = false,
                    ["liveRoom"] = "",
                    ["liveScope"] = "owner",
                },
                ["notify"] = new Dictionary<string, object?>
                {
                    ["type"] = "file",
                    ["path"] = NotifyLogFile,
                },
            };
            var toolJson = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }) + "\n";
            File.WriteAllText(ToolConfigFile, toolJson, Utf8NoBom);
            // 上游 commands/fav.mjs 与 commands/selftest.mjs 是**自己拼** <HOME>/.dsh/bili-whale.config.json 读的，
            // 没走 lib/config.mjs（那个才认 BILI_WHALE_CONFIG）→ 只写上面那份它们会 ENOENT。
            // 这里补一份同内容副本喂给它们；**不改上游源码**，升级时整包替换即可。
            File.WriteAllText(Path.Combine(DshDir, "bili-whale.config.json"), toolJson, Utf8NoBom);
        }
        catch (Exception e) { Log($"写工具链配置失败：{e.Message}", error: true); }
    }

    // ==================== 分享闸门（第一次必须先报备） ====================
    // ⚠️ 判断依据不能用 quota.json：commands/quota.mjs:165 把**任何分享**都记成 auto:true
    //    （auto: !!opt.auto || !!opt.share）→ 账本里分不出「自主发的」和「主人授权的」。
    //    所以闸门状态由插件自己记：主人用 auto=false（主人当场授权）发出过第一条分享 → 写这个文件 → 永久放开。

    public bool ShareGateOpen()
    {
        try
        {
            if (!File.Exists(ShareGateFile)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(ShareGateFile));
            return doc.RootElement.TryGetProperty("opened", out var o) && o.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    public void OpenShareGate(string evidence)
    {
        try
        {
            File.WriteAllText(ShareGateFile,
                "{\"opened\":true,\"at\":\"" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz") + "\",\"evidence\":\""
                + (evidence ?? "?").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ") + "\"}\n", Utf8NoBom);
            Log($"分享闸门已放行：主人授权发出了第一条分享（{evidence}）→ 以后她可自主分享");
        }
        catch (Exception e) { Log($"写分享闸门状态失败：{e.Message}", error: true); }
    }

    static List<Dictionary<string, object?>> ParseWindows(string raw, int maxVideos)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var part in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = part.Trim();
            var wakes = 1;
            var eq = text.IndexOf('=');
            if (eq > 0)
            {
                if (int.TryParse(text[(eq + 1)..].Trim(), out var w)) wakes = Math.Clamp(w, 0, 12);
                text = text[..eq].Trim();
            }
            var dash = text.IndexOf('-');
            if (dash <= 0) continue;
            var start = text[..dash].Trim();
            var end = text[(dash + 1)..].Trim();
            if (!IsHHMM(start) || !IsHHMM(end)) continue;
            list.Add(new Dictionary<string, object?> { ["start"] = start, ["end"] = end, ["wakes"] = wakes });
        }
        if (list.Count == 0)
            list.Add(new Dictionary<string, object?> { ["start"] = "20:00", ["end"] = "24:00", ["wakes"] = 1 });
        return list;
    }

    static bool IsHHMM(string s)
    {
        var bits = s.Split(':');
        return bits.Length == 2 && int.TryParse(bits[0], out var h) && int.TryParse(bits[1], out var m)
               && h is >= 0 and <= 24 && m is >= 0 and <= 59;
    }

    static string[] SplitTags(string raw) =>
        (raw ?? "").Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    static string NormalizeVia(string v) => string.Equals(v?.Trim(), "dm", StringComparison.OrdinalIgnoreCase) ? "dm" : "comment";
    static string NormalizeScope(string v) => string.Equals(v?.Trim(), "reply", StringComparison.OrdinalIgnoreCase) ? "reply" : "both";

    // ==================== 进程调用 ====================

    ProcessStartInfo BuildPsi(string exe, IEnumerable<string> args, bool longRunning = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = ToolsDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // ★ 换掉"家目录"：工具链的凭据/额度/日记/截图全部落进插件数据目录
        psi.Environment["USERPROFILE"] = HomeDir;
        psi.Environment["HOME"] = HomeDir;
        psi.Environment["BILI_WHALE_CONFIG"] = ToolConfigFile;   // lib/config.mjs 认它
        psi.Environment["WHALE_CFG"] = ToolConfigFile;           // 部分脚本认它
        psi.Environment["DSH_HOME"] = DshDir;
        psi.Environment["NODE_NO_WARNINGS"] = "1";
        if (FfmpegPath.Length > 0) psi.Environment["FFMPEG"] = FfmpegPath;
        _ = longRunning;
        return psi;
    }

    /// <summary>跑一条工具链命令（node bili.mjs &lt;cmd&gt; …），等它结束。</summary>
    public async Task<BiliRunResult> RunAsync(string cmd, IReadOnlyList<string>? args = null, int? timeoutSec = null, CancellationToken ct = default)
    {
        if (NodeExe.Length == 0) return BiliRunResult.Fail("找不到 node.exe（请在配置里填「Node 可执行文件」）");
        if (!File.Exists(BiliMjs)) return BiliRunResult.Fail($"找不到工具链入口：{BiliMjs}");

        var list = new List<string> { BiliMjs, cmd };
        if (args != null) list.AddRange(args.Where(a => !string.IsNullOrEmpty(a)));

        var timeout = Math.Max(15, timeoutSec ?? _cfg.CommandTimeoutSec);
        try
        {
            using var p = new Process { StartInfo = BuildPsi(NodeExe, list) };
            var so = new StringBuilder();
            var se = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
            if (!p.Start()) return BiliRunResult.Fail("进程启动失败");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new BiliRunResult
                {
                    Started = true,
                    ExitCode = -1,
                    Out = Lock(so),
                    Failure = $"命令超时（{timeout}s）：{cmd}",
                };
            }
            try { p.WaitForExit(); } catch { }

            return new BiliRunResult
            {
                Started = true,
                ExitCode = p.ExitCode,
                Out = Lock(so),
                Err = Lock(se),
            };
        }
        catch (Exception e)
        {
            Log($"运行 {cmd} 异常：{e.Message}", error: true);
            return BiliRunResult.Fail(e.Message);
        }
    }

    static string Lock(StringBuilder sb) { lock (sb) return sb.ToString(); }

    /// <summary>起一个长驻子进程（生物钟 / 守夜进程），由调用方负责 Kill。</summary>
    public BiliLongRun StartCommand(string cmd, IReadOnlyList<string>? args = null)
    {
        var list = new List<string> { BiliMjs, cmd };
        if (args != null) list.AddRange(args.Where(a => !string.IsNullOrEmpty(a)));
        var p = new Process { StartInfo = BuildPsi(NodeExe, list, longRunning: true) };
        p.Start();
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        return new BiliLongRun { Proc = p, Out = so, Err = se };
    }

    // ==================== 日志 ====================

    public void Log(string message, bool error = false)
    {
        try
        {
            Directory.CreateDirectory(StateRoot);
            File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(error ? "[错误] " : "")}{message}\n", Utf8NoBom);
        }
        catch { }
        if (error) _logger.LogWarning("[BiliWhale] {Message}", message);
        else _logger.LogInformation("[BiliWhale] {Message}", message);
    }
}