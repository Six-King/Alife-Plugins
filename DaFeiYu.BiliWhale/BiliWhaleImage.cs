using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;

namespace DaFeiYu.BiliWhale;

/// <summary>
/// 评论配图（梗图）：把本地图片上传到 B站图床，然后发一条带 <c>pictures</c> 的评论。
///
/// 上游 comment.mjs 只发纯文本 —— pictures 字段、图片上传/转码、带图评论的 WBI 签名，这一层补齐。
///
/// 梗图怎么进来：
///   · 直接用大肥鱼梗图库拿（**一步**）：<c>&lt;MemeFetch keyword="委屈" /&gt;</c>（返回本地路径）
///     → 把路径填进这里 <c>image="…"</c>
///   · 填裸编号 <c>image="002"</c> 也会试着从 MemePack 缓存里找（省一次 MemeFetch，但不保证）
///   · 填绝对路径也行（任何本地 jpg/png/gif/webp）
/// </summary>
public partial class BiliWhaleModule
{
    [XmlFunction(FunctionMode.OneShot)]
    [Description("发一条**带图**的视频评论（梗图配文）。本地图片 → 自动上传 B站图床 → 塞进评论 pictures。webp 自动转 jpg。"
               + "写操作：先 dry=\"true\" 看一枪（会真上传图换 img_src，但不发评论），确认后 dry=\"false\"。"
               + "⚠️ 若正文里 @主人 且不是回评（parent 空）= 分享型（占每日分享额度，与评论/私信共用同一本）。")]
    public async Task BiliCommentImage(
        [Description("视频 BV 号")] string bvid,
        [Description("图片路径（大肥鱼梗图可直接给编号如 002，或用 <MemeFetch keyword=\"委屈\"/> **一步**拿到的本地路径填进来）")] string image,
        [Description("评论文案（配图说的话；要带表情就写转义符如 [tv_doge]）")] string message = "",
        [Description("要回复的评论 rpid（如果这是回复）")] string parent = "",
        [Description("线程根评论 rpid")] string root = "",
        [Description("true=自主发言（走限额闸）；主人当场授权的用 false")] bool auto = true,
        [Description("true=只打印不发")] bool dry = true)
    {
        if (!Cfg.AllowImageComment) { Poke("评论配图功能被配置关掉了（「允许评论配图」= false）。开一下或者改用纯文案 BiliComment。"); return; }
        if (!HasBiliCookie) { Poke("还没配 B站 cookie：先 <BiliSetCookie …> 填上 SESSDATA / bili_jct / buvid3。"); return; }

        var bv = Bv(bvid);
        if (bv.Length == 0) { Poke("给我 BV 号。"); return; }

        // 同一轮重复标签去重（2026-10-23 补）：与 <BiliComment> 共用同一把事前幂等键
        // （同 BV + 同归一化正文 5 秒内只放行第一次；账本在 HTTP 成功之后才写，事后闸拦不住同轮重复）
        if (IsRecentDuplicateComment(bv, message)) { Poke("同一条刚发过，已忽略重复标签"); return; }

        var ct = DestroyCancellationToken;

        // 1) 解析图片路径
        var imgPath = ResolveImagePath(image);
        if (imgPath == null)
        {
            Poke($"找不到图片：「{image}」\n"
              + "试试：<MemeFetch keyword=\"委屈\"/> **一步**拿到本地路径 → 把路径填进 image。\n"
              + "或者直接填绝对路径（K:\\…\\002.webp）。");
            return;
        }

        // 2) 格式：B站图床只收 png/jpg/gif —— webp 必须转成 jpg
        var ext = Path.GetExtension(imgPath).ToLowerInvariant();
        string uploadPath = imgPath;
        bool needsCleanup = false;
        if (ext is ".webp" or ".bmp" or ".tiff" or ".tif")
        {
            if (!Cfg.ConvertImageForUpload) { Poke($"格式 {ext} 不被 B站图床接受（只收 png/jpg/gif）。把「配图自动转码」打开，或者先自己转成 jpg。"); return; }
            var jpg = Path.Combine(_runner.TempDir, $"cvt_{Path.GetFileNameWithoutExtension(imgPath)}.jpg");
            Directory.CreateDirectory(_runner.TempDir);
            var r = await _runner.RunFfmpegAsync(new[] { "-y", "-loglevel", "error", "-i", imgPath, "-frames:v", "1", jpg }, 120, ct);
            if (!r.Ok || !File.Exists(jpg)) { Poke($"ffmpeg 转码失败（{r.Text}）—— 可能没装 ffmpeg，也可能文件坏了。"); return; }
            uploadPath = jpg;
            needsCleanup = true;
            _runner.Log($"配图转码：{imgPath} → {jpg}");
        }

        // 3) 上传到 B站图床
        string imgSrc; int w, h; double kb;
        try
        {
            var up = await UploadImageAsync(uploadPath, ct);
            if (up == null) { Poke("上传失败。B站图床返回空。看看配置里 cookie 是不是过期了。"); return; }
            imgSrc = up.Value.Url; w = up.Value.W; h = up.Value.H; kb = up.Value.Kb;
            _runner.Log($"图片已上传：{imgSrc}（{w}×{h} {kb:0.#}KB）");
        }
        catch (Exception e) { Poke($"上传失败：{e.Message}"); return; }
        finally { if (needsCleanup) try { File.Delete(uploadPath); } catch { } }

        // 4) 列请求
        var pics = new Dictionary<string, object>
        {
            ["img_src"] = imgSrc, ["img_width"] = w, ["img_height"] = h, ["img_size"] = kb,
        };
        var picturesJson = JsonSerializer.Serialize(new[] { pics });

        var sb = new StringBuilder();
        sb.AppendLine("══ 带图评论（预览）══");
        sb.AppendLine($"  视频：https://www.bilibili.com/video/{bv}/");
        sb.AppendLine($"  文案：{(message.Length > 0 ? message : "（无文案）")}");
        sb.AppendLine($"  图片：{imgSrc}（{w}×{h} {kb:0.#}KB）");
        sb.AppendLine($"  dry={(dry ? "true" : "false")}　auto={(auto ? "true" : "false")}");

        if (dry)
        {
            sb.AppendLine("\n[dry] 图已上传到 B站图床，但**没有发评论**。文案和图片确认无误后，把 dry=\"true\" 换成 dry=\"false\" 再跑一次。");
            Poke(sb.ToString().TrimEnd());
            return;
        }

        // 5) 分享闸门（2026-10-22 ⑨ 补漏洞）────────────────────────────────────
        // 这条路径**没有 share 参数**（构造上不是分享型、也不占分享额度），
        // 但「正文 @主人 + 不是回评」事实上就是"把好东西 @ 给他" —— 即是分享。
        // 所以按**与 <BiliComment>/<BiliDm> 同一道闸门**处理，免得它变成绕过报备的通道。
        var shareLike = LooksLikeShareToOwner(message, parent);
        if (shareLike && auto && !_runner.ShareGateOpen())
        {
            sb.AppendLine();
            sb.AppendLine("[分享闸门] ⛔ 这条图评在 @主人 = 事实上的「分享」，而闸门当前未放行 → 拒绝发送。");
            sb.AppendLine("（只想闲聊/回评不受影响：回评带 parent 不算分享；不 @ 他也不是分享。）");
            Poke(sb.ToString().TrimEnd());
            return;
        }

        // 5) 限额闸（用上游工具链的 quota check，和 comment.mjs 同一套账本）
        //    2026-10-22 ⑩：@主人的图评（shareLike）按**分享型**记账 —— 以前它只走 --auto，
        //    等于"发分享型评论却不占分享额度"（⑨ 报告里点过的那个残留漏洞），现在对齐。
        var quotaArgs = shareLike
            ? new[] { "check", "--type", "comment", "--target", $"BV:{bv}", "--share" }
            : new[] { "check", "--type", "comment", "--target", $"BV:{bv}", auto ? "--auto" : "" };
        var qt = await _runner.RunAsync("quota", quotaArgs, 60, ct);
        if (!qt.Ok || qt.ExitCode != 0)
        {
            sb.AppendLine($"\n[限额闸] {qt.Text}");
            sb.AppendLine("→ 拒绝发送。这是硬约束，不要绕过。");
            Poke(sb.ToString().TrimEnd());
            return;
        }

        // 6) 防重复硬闸（2026-10-21 加：她曾把同一条带图评论连发 3 次）
        // 账本 quota.json 是唯一真相：同一视频 + 同一文案 10 分钟内已记录 → 拒绝，不再上传也不发。
        try
        {
            var qf = Path.Combine(_runner.StateRoot, "home", ".bili-ai", "quota.json");
            if (File.Exists(qf))
            {
                using var qd = JsonDocument.Parse(File.ReadAllText(qf));
                if (qd.RootElement.TryGetProperty("log", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    // 口径与纯文本闸 CommentDupReason **完全一致**（两边都过 NormComment + ≥12 字前缀包容）；
                    // 记账侧仍用 QuotaNote，写进账本的字符串一个字节都没改 ✓
                    foreach (var e in arr.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.Object) continue;
                        if (!e.TryGetProperty("target", out var tg) || tg.GetString() != $"BV:{bv}") continue;
                        var had = e.TryGetProperty("note", out var nt) ? (nt.GetString() ?? "") : "";
                        if (!SameComment(message, had)) continue;
                        if (!e.TryGetProperty("at", out var atEl) || !DateTimeOffset.TryParse(atEl.GetString(), out var when)) continue;
                        if ((DateTimeOffset.UtcNow - when).TotalMinutes > 10) continue;
                        if (IsDeletedExempt(bv, had, when)) continue;   // 这条对应的评论已被删（豁免晚于该行）→ 不拦
                        Poke($"🛑 拒绝重复发送：这条视频 + 这段文案在 {when.ToLocalTime():HH:mm:ss} 刚发过一次（同一内容 10 分钟内只准一次）。别再重发——表情/错字不对就下次注意，非要改就先 <BiliCommentDel bvid=\"{bv}\"/> 删掉旧的再发新的。");
                        return;
                    }
                }
            }
        }
        catch { /* 账本读不到就不拦，免得把正常发布卡死 */ }

        // 7) 真发
        var oid = await ResolveAidAsync(bv, ct);
        if (oid.Length == 0) { Poke($"拿不到 {bv} 的 aid，发不了。先 <BiliRecon bvid=\"{bv}\"/> 看看这条视频还在不在。"); return; }

        var (code, msg, rpid) = await PostCommentAsync(oid, message, picturesJson,
            Clean(parent).Length > 0 ? Clean(parent) : null,
            Clean(root).Length > 0 ? Clean(root) : null, bv, ct);

        sb.AppendLine($"\n  POST → code={code} message={msg}");
        if (code == 0)
        {
            sb.AppendLine($"  ✅ 已发布，评论 rpid={rpid}");
            sb.AppendLine($"  https://www.bilibili.com/video/{bv}/#reply{rpid}");

            // 主人当场授权（auto=false）的那条「@主人」图评真发出 → 永久放行（与另外两条路径同一道闸门）
            if (shareLike && !auto) _runner.OpenShareGate($"带图评论分享 rpid={rpid}");

            // 7) 记账（note 口径与上面查重共用 QuotaNote，两处必须一致）
            var note = QuotaNote(message);
            var rec = await _runner.RunAsync("quota", new[] { "record", "--type", "comment",
                "--target", $"BV:{bv}", "--note", note, shareLike ? "--share" : (auto ? "--auto" : "") }, 60, ct);
            sb.AppendLine($"  [限额闸] {rec.Text}");

            // 发成功 → 记一条 rpid→原始正文（纯附加：删评时零 HTTP 查得到；不改上面任何判定）
            RecordSentComment(bv, message ?? "", rpid);
        }
        else
        {
            var hint = CommentFailHint(code);
            if (hint.Length > 0) sb.AppendLine($"  提示：{hint}");
        }

        Poke(sb.ToString().TrimEnd());
    }

    // ==================== 图片路径解析 ====================

    /// <summary>
    /// 「这条带图评论其实是在**分享给主人**」的判据（2026-10-22 ⑨ 补）。
    /// <c>BiliCommentImage</c> 没有 <c>share</c> 参数（构造上不是分享型、也不占分享额度），
    /// 但正文里 <c>@主人昵称</c> 且**不是回评**（parent 空）= 事实上就是"把好东西 @ 给他" → 按分享闸门处理。
    /// 回评（parent 非空）与不 @ 他的评论都不受影响 ✓
    /// </summary>
    bool LooksLikeShareToOwner(string message, string parent)
    {
        if (Clean(parent).Length > 0) return false;
        var name = (Cfg.OwnerName ?? "").Trim();
        if (name.Length == 0) return false;
        return (message ?? "").Contains("@" + name, StringComparison.Ordinal);
    }

    /// <summary>账本 note 的**唯一口径**（查重与记账必须用同一个函数）：换行/回车 → 空格，截断 40 字。</summary>
    static string QuotaNote(string message)
    {
        var t = (message ?? "").Replace('\n', ' ').Replace('\r', ' ');
        return t.Length > 40 ? t[..40] : t;
    }

    /// <summary>把用户给的 image 解析成实际文件路径。
    /// 支持：绝对路径、相对路径、裸编号（002 → 去 MemePack 缓存找）。</summary>
    string? ResolveImagePath(string raw)
    {
        raw = (raw ?? "").Trim().Trim('"');
        if (raw.Length == 0) return null;

        // 绝对路径
        if (Path.IsPathRooted(raw) && File.Exists(raw)) return raw;

        // 编号 / 文件名 → 先看 MemePack 缓存
        var id = raw.TrimStart('0', '#', ' ');
        if (id.All(char.IsDigit) && id.Length <= 5)
        {
            var cacheDir = Path.Combine(AlifePath.StorageFolderPath, "PluginData", "DaFeiYu.MemePack", "cache");
            if (Directory.Exists(cacheDir))
            {
                var n = int.Parse(id);
                // 缓存里的文件名是零填充的（如 002.webp），所以 2 / 002 / 原样 三种写法都要认
                var names = new[] { n.ToString(), n.ToString("D3"), n + "-orig", n.ToString("D3") + "-orig", raw.TrimStart('#', ' ').Trim() };
                foreach (var ext in new[] { ".webp", ".jpg", ".png", ".gif" })
                    foreach (var nm in names)
                    {
                        var f = Path.Combine(cacheDir, nm + ext);
                        if (File.Exists(f)) return f;
                    }
            }
        }

        // 相对路径
        if (raw.Length > 0 && File.Exists(raw)) return Path.GetFullPath(raw);

        // 整句塞进来也救回来：从文本里抠出第一个像路径的片段
        // （MemeFetch 的输出形如「已取回本地：K:\…\002.webp」，模型很容易连前缀一起填进来）
        var hit = Regex.Match(raw, @"[A-Za-z]:\\[^""\r\n]*?\.(?:webp|jpg|jpeg|png|gif|bmp)", RegexOptions.IgnoreCase);
        if (hit.Success && File.Exists(hit.Value.Trim())) return hit.Value.Trim();

        return null;
    }
}