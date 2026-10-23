using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace DaFeiYu.BiliWhale;

/// <summary>
/// ⑰-7①③ 的两个配置页动作（检查更新 / 导出诊断）。
///
/// 为什么放配置页：这两件事都是"主人想看"的，而主人不一定开终端。
/// 纪律：两个都是**只读**动作 ——
///   · 检查更新：只读比对版本，**绝不下载、绝不覆盖插件文件**（下载要自己去跑带 --download 的命令）；
///   · 导出诊断：只在页面上显示一段**脱敏文本**，**不落盘**（想落盘自己去跑 diagnose --out，那是 .txt）。
/// 工具链那半边（tools/commands/check-update.mjs、diagnose.mjs）负责干活，C# 只翻成人话。
/// </summary>
public partial class BiliWhaleModule
{
    bool _opsBusy;

    /// <summary>配置页「检查更新」：本地版本 vs 插件市场，回一段人话（只读）。</summary>
    public async Task<string> UiCheckUpdateAsync()
    {
        if (_opsBusy) return "另一个动作还在跑，等它结束再点（同一时刻只跑一个）。";
        if (_runner == null) return "模块还没就绪（_runner 为空），先重载一次插件再点。";

        _opsBusy = true;
        try
        {
            var r = await _runner.RunAsync("check-update", new[] { "--json" }, 90, DestroyCancellationToken);
            var stdout = r.Out ?? "";
            var json = LastJsonLine(stdout);

            var state = "";
            var msg = "";
            if (json.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    state = GetStr(root, "state");
                    msg = GetStr(root, "msg");
                }
                catch { /* 解析不了就退回纯文本 */ }
            }

            if (state.Length == 0)
                return r.Failure.Length > 0
                    ? $"检查更新没跑起来：{r.Failure}"
                    : $"没看懂工具链的输出（exit={r.ExitCode}）：\n{Trim(stdout)}";

            // exit=4 表示"上游有新版本" —— 那是**正常结果**不是失败 ✓（这只读命令自己的约定）
            _runner.Log($"[检查更新] {msg}（exit={r.ExitCode} · 只读，没动任何插件文件）", error: state == "error");

            var body = StripJsonLine(stdout);
            return msg + "\n\n" + body.Trim()
                 + "\n（只读检查：不会下载、也不会覆盖你的插件文件 ✓）";
        }
        catch (Exception e)
        {
            try { _runner.Log($"[检查更新] 异常：{e.Message}", error: true); } catch { }
            return "检查更新异常：" + e.Message;
        }
        finally { _opsBusy = false; }
    }

    /// <summary>配置页「导出诊断」：把脱敏诊断文本显示出来（不落盘、不回显凭据）。</summary>
    public async Task<string> UiDiagnoseAsync()
    {
        if (_opsBusy) return "另一个动作还在跑，等它结束再点（同一时刻只跑一个）。";
        if (_runner == null) return "模块还没就绪（_runner 为空），先重载一次插件再点。";

        _opsBusy = true;
        try
        {
            var r = await _runner.RunAsync("diagnose", new[] { "--lines", "120" }, 90, DestroyCancellationToken);
            var text = (r.Out ?? "").Trim();
            if (text.Length == 0)
                return r.Failure.Length > 0
                    ? $"诊断导出没跑起来：{r.Failure}"
                    : $"诊断导出没有任何输出（exit={r.ExitCode}）。\n{r.Err}";

            // 日志里只记一行摘要 —— **绝不把诊断正文回写进日志**（否则日志会自己吃自己越长越大 ✗）
            _runner.Log($"[诊断导出] 已生成脱敏文本 {text.Length} 字（只在配置页显示，未落盘、未回显凭据）");
            return text.Length > 6000
                ? text[..6000] + "\n…（太长已截断；要看全文请在插件目录跑：node tools\\bili.mjs diagnose --out，落 .txt）"
                : text;
        }
        catch (Exception e)
        {
            try { _runner.Log($"[诊断导出] 异常：{e.Message}", error: true); } catch { }
            return "诊断导出异常：" + e.Message;
        }
        finally { _opsBusy = false; }
    }

    /// <summary>把 stdout 里最后那条 JSON 行去掉，只留给人看的正文。</summary>
    static string StripJsonLine(string stdout)
    {
        var lines = (stdout ?? "").Replace("\r\n", "\n").Split('\n');
        var keep = new System.Collections.Generic.List<string>();
        var dropped = false;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var l = lines[i].Trim();
            if (!dropped && l.StartsWith("{", StringComparison.Ordinal)) { dropped = true; continue; }
            keep.Insert(0, lines[i]);
        }
        return string.Join("\n", keep);
    }
}