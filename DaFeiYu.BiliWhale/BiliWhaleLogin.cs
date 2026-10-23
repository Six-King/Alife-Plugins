using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Alife.Platform;

namespace DaFeiYu.BiliWhale;

/// <summary>
/// ⑰-3 扫码登录（配置页动作按钮的执行体）。
///
/// 分工：流程全在 <c>tools/commands/qr-login.mjs</c>（先只读验登录态 → 在线就跳过；
/// 不在线才生成二维码 PNG → 轮询 → 原子写 credentials.json → 删图）。
/// C# 这半边只做三件事：
///   ① 决定二维码落到哪（**真桌面** —— 子进程的 USERPROFILE 被换成了插件数据目录，
///      所以必须由这里把绝对路径传进去，不能让 JS 自己推）；
///   ② 把结果翻译成一句人话回给配置页；
///   ③ 登录成功后**把凭据同步进模块配置**并重写 credentials.json/tool 配置 ——
///      否则下次 Prepare() 的 SyncCredentials() 会拿配置里的旧（空）值把新凭据覆盖掉 ✗。
///
/// 铁律：不回显任何凭据；失败不写盘；不碰额度账本 / 分享闸门 / 日记。
/// </summary>
public partial class BiliWhaleModule
{
    bool _qrLoginBusy;

    /// <summary>配置页「扫码登录」按钮的入口：跑一次扫码登录，返回给人看的一段话。</summary>
    public async Task<string> UiQrLoginAsync()
    {
        if (_qrLoginBusy) return "正在扫码登录中，等这一轮结束再点（同一个时刻只跑一次）。";
        if (_runner == null) return "模块还没就绪（_runner 为空），先重载一次插件再点。";

        _qrLoginBusy = true;
        try
        {
            var png = ResolveQrImagePath();
            var wait = Math.Clamp(Cfg.QrLoginWaitSec <= 0 ? 150 : Cfg.QrLoginWaitSec, 15, 600);
            _runner.Log($"[扫码登录] 开始：二维码={png} · 等待 {wait}s（先只读验登录态，在线就跳过，不生成图片）");

            var r = await _runner.RunAsync("qr-login",
                new[] { "--out", png, "--wait", wait.ToString() },
                wait + 90, DestroyCancellationToken);

            var state = "error";
            var msg = "";
            var hasToken = false;
            var stdout = r.Out ?? "";
            var jsonLine = LastJsonLine(stdout);
            if (jsonLine.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(jsonLine);
                    var root = doc.RootElement;
                    state = GetStr(root, "state");
                    msg = GetStr(root, "msg");
                    hasToken = root.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.True;
                }
                catch { /* 解析不了就退回纯文本 */ }
            }

            if (state == "online")
            {
                // 在线：只回一句 —— 不生成图片、不打登录接口（JS 侧就是照这个顺序做的）
                var who = Cfg.SelfName.Length > 0 ? Cfg.SelfName : "";
                return who.Length > 0 ? $"账号在线，无需扫码（{who}）" : "账号在线，无需扫码";
            }

            if (state == "ok")
            {
                var applied = ApplyFreshCredentials();
                _runner.Log($"[扫码登录] 成功：凭据已写入 {applied.Summary} · access_token={(hasToken ? "有" : "无")}");
                var s = await _runner.RunAsync("session", null, 60, DestroyCancellationToken);
                TryCaptureSelf(s.Text);
                PersistCredsToModuleConfig();    // 把 SelfMid/SelfName 也一起落盘
                return "扫码登录成功 ✓ 凭据已写入并同步进配置（重启也不会丢；不回显）。\n"
                     + "  access_token：" + (hasToken ? "已一并保存" : "这次返回里没有") + "\n"
                     + "  " + applied.Summary + "\n"
                     + Trim(s.Text);
            }

            if (msg.Length == 0)
            {
                msg = r.Failure.Length > 0
                    ? $"没跑起来：{r.Failure}"
                    : $"没看懂工具链的输出（exit={r.ExitCode}）。\n{Trim(r.Text)}";
            }
            _runner.Log($"[扫码登录] 未完成（state={state}）：{msg}", error: state == "error");
            return msg;
        }
        catch (Exception e)
        {
            try { _runner.Log($"[扫码登录] 异常：{e.Message}", error: true); } catch { }
            return $"扫码登录异常：{e.Message}";
        }
        finally { _qrLoginBusy = false; }
    }

    // ==================== 二维码路径 ====================

    /// <summary>二维码 PNG 落到哪：配置留空 → **真桌面**（不是被换过的 USERPROFILE\Desktop）。</summary>
    string ResolveQrImagePath()
    {
        var raw = (Cfg.QrImagePath ?? "").Trim().Trim('"');
        if (raw.Length > 0) return Path.GetFullPath(raw);        // 配了就用配的（相对路径按 Alife 进程 cwd 解析）
        string desktop;
        try { desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        catch { desktop = ""; }
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        return Path.Combine(desktop, "bili-login-qr.png");
    }

    // ==================== 凭据落盘（不回显） ====================

    /// <summary>把扫码登录刚写进 credentials.json 的凭据搬到内存配置里（**不打印任何值**）。</summary>
    (bool Ok, string Summary) ApplyFreshCredentials()
    {
        try
        {
            var file = Path.Combine(_runner.BiliAiDir, "credentials.json");
            if (!File.Exists(file)) return (false, "凭据文件不存在（工具链没写成功）");
            using var doc = JsonDocument.Parse(File.ReadAllText(file).Replace("\uFEFF", ""));
            var root = doc.RootElement;
            var sessdata = GetStr(root, "sessdata");
            var jct = GetStr(root, "bili_jct");
            var buvid = GetStr(root, "buvid3");
            var token = GetStr(root, "access_token");
            if (sessdata.Length == 0) return (false, "凭据文件里没有 SESSDATA（没敢覆盖配置）");

            Cfg.Sessdata = sessdata;
            Cfg.BiliJct = jct;
            if (buvid.Length > 0) Cfg.Buvid3 = buvid;
            if (token.Length > 0) Cfg.AccessToken = token;

            _runner.SyncCredentials();     // 让 credentials.json / 配置两边的键一致（含 access_token）
            _runner.WriteToolConfig();
            var saved = PersistCredsToModuleConfig();
            return (true, $"SESSDATA/bili_jct 已更新 · buvid3={(buvid.Length > 0 ? "有" : "无")} · "
                        + $"配置文件={(saved.Length > 0 ? "已更新" : "没找到（下次保存配置时会带上）")}");
        }
        catch (Exception e)
        {
            return (false, "读凭据失败：" + e.Message);
        }
    }

    /// <summary>
    /// 把凭据（与她自己是谁）写回**模块配置文件**。为什么必须做：
    /// 模块每次 Prepare()/热更新都会用配置里的 cookie 重写 credentials.json，
    /// 不落盘的扫码结果会在下一次重启时被旧值覆盖掉 ✗。
    /// 只改这几个键，其余键原样保留；临时文件 + 替换，绝不留下半个文件。
    /// 返回写好的文件路径（找不到配置位置就返回空串）。
    /// </summary>
    string PersistCredsToModuleConfig()
    {
        try
        {
            var path = ModuleConfigPath();
            if (path.Length == 0) return "";
            JsonNode? root;
            if (File.Exists(path))
            {
                root = JsonNode.Parse(File.ReadAllText(path).Replace("\uFEFF", ""));
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                root = new JsonObject();
            }
            if (root is not JsonObject obj) return "";
            SetIfNotEmpty(obj, "Sessdata", Cfg.Sessdata);
            SetIfNotEmpty(obj, "BiliJct", Cfg.BiliJct);
            SetIfNotEmpty(obj, "Buvid3", Cfg.Buvid3);
            SetIfNotEmpty(obj, "AccessToken", Cfg.AccessToken);
            SetIfNotEmpty(obj, "SelfMid", Cfg.SelfMid);
            SetIfNotEmpty(obj, "SelfName", Cfg.SelfName);

            var tmp = path + ".tmp-" + Environment.ProcessId;
            File.WriteAllText(tmp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception e)
        {
            try { _runner.Log($"[扫码登录] 写模块配置失败（凭据已在 credentials.json 里，不影响本次可用）：{e.Message}", error: true); } catch { }
            return "";
        }
    }

    static void SetIfNotEmpty(JsonObject obj, string key, string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length > 0) obj[key] = v;
    }

    /// <summary>模块配置文件的落地位置：角色级优先（与框架 ConfigurationSystem 的查找顺序一致）。</summary>
    string ModuleConfigPath()
    {
        var name = typeof(BiliWhaleModule).FullName + ".json";
        try
        {
            var key = Character?.StorageKey;
            if (!string.IsNullOrWhiteSpace(key))
                return Path.Combine(AlifePath.StorageFolderPath, key, "Configuration", name);
        }
        catch { }
        return Path.Combine(AlifePath.StorageFolderPath, "Configuration", name);
    }

    static string GetStr(JsonElement root, string key)
        => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    /// <summary>C# 只认 stdout 里**最后一条以 { 开头的行**（和工具链其它命令一个规矩）。</summary>
    static string LastJsonLine(string stdout)
    {
        var lines = (stdout ?? "").Replace("\r\n", "\n").Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var l = lines[i].Trim();
            if (l.StartsWith("{", StringComparison.Ordinal)) return l;
        }
        return "";
    }
}