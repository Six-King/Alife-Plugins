using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DaFeiYu.BiliWhale;

/// <summary>「@我的」收件箱里的一条（<c>x/msgfeed/at</c> 的 items[]）。</summary>
internal sealed class BiliMention
{
    public string Id { get; set; } = "";
    public long AtTime { get; set; }          // unix 秒
    public string WhoName { get; set; } = "";
    public string WhoMid { get; set; } = "";
    public string Text { get; set; } = "";    // item.source_content：那条 @ 她的话
    public string VideoTitle { get; set; } = "";
    public string Uri { get; set; } = "";     // 视频/动态链接
    public string Business { get; set; } = ""; // 「评论」/「动态」…
    public long BusinessId { get; set; } = 1;  // 1=视频 11=动态
    public long Aid { get; set; }              // item.subject_id
    public long SourceId { get; set; }         // item.source_id：说出这句话的那条评论 rpid
    public long RootId { get; set; }           // item.root_id（0=顶层评论）
    public bool AtMe { get; set; }             // at_details 里确实有她自己
    public List<string> AtNames { get; set; } = new();
}

/// <summary>
/// B 站直连层（插件侧）。
///
/// 为什么要有这一层：上游工具链**没有**这些能力 ——
///   · 评论带图（comment.mjs 只发纯文本，没有 pictures 字段）
///   · 「@我的」单独高频轮询（comment-await 把 reply+at 合成一个进程、默认 5 分钟一轮）
///   · 官方表情清单（全库没有任何 /x/emote/… 调用）
///   · 分P/标签/章节（debrief 不取）
/// 这些都只是**只读 GET + 一个上传**，不重写任何上游逻辑，也不改上游一行代码。
///
/// 实测记录（2026-10-21，本机，用她自己的 cookie 逐个验过）：
///   · cookie 名**必须大写** <c>SESSDATA</c>：写成小写 sessdata → <c>code=-101 账号未登录</c>（踩过）
///   · <c>x/msgfeed/at</c> 只读、不需要 wbi；item 的真实结构是 <c>{id,user,item:{…},at_time}</c>，
///     而且 **有 at_details**（谁被 @ 了）—— 上游没读它，所以上游分不出"@的是不是她本人"
///   · <c>x/emote/user/panel/web?business=reply</c> 只读、不需要 wbi，返回的 <c>text</c> 就是能直接写进评论的转义符
///   · WBI 签名（nav → img_key/sub_key → mixinKeyEncTab → md5）可用：<c>x/player/wbi/v2</c> 回 code=0
///   · 图片上传 <c>x/dynamic/feed/draw/upload_bfs</c>（csrf + biz=new_dyn + file_up）→ 回
///     <c>{image_url:"http://i0.hdslb.com/bfs/new_dyn/….jpg", image_width, image_height, img_size}</c>，
///     与真实"带图评论"的 <c>content.pictures[].img_src</c> 同一命名空间
/// </summary>
public partial class BiliWhaleModule
{
    const string BiliUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>上传图片单独用一个大超时客户端（几 MB 的图 + 30 秒不够）。</summary>
    static readonly HttpClient _apiUpload = new() { Timeout = TimeSpan.FromSeconds(180) };

    static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
        26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36,
        20, 34, 44, 52,
    };

    // ==================== 凭据 / 基础请求 ====================

    internal bool HasBiliCookie =>
        Clean(Cfg.Sessdata).Length > 0 && Clean(Cfg.BiliJct).Length > 0;

    /// <summary>★ cookie 名必须大写 SESSDATA —— 小写会被 B站当成没登录（实测 -101）。</summary>
    string CookieHeader()
    {
        var parts = new List<string>();
        if (Clean(Cfg.Sessdata).Length > 0) parts.Add($"SESSDATA={Cfg.Sessdata.Trim()}");
        if (Clean(Cfg.BiliJct).Length > 0) parts.Add($"bili_jct={Cfg.BiliJct.Trim()}");
        if (Clean(Cfg.Buvid3).Length > 0) parts.Add($"buvid3={Cfg.Buvid3.Trim()}");
        return string.Join("; ", parts);
    }

    async Task<JsonDocument?> ApiGetAsync(string url, CancellationToken ct, bool quiet = false, bool withCookie = true)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (withCookie) req.Headers.TryAddWithoutValidation("Cookie", CookieHeader());
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(text);
        }
        catch (Exception e)
        {
            if (!quiet) _runner.Log($"B站接口失败（{url.Split('?')[0]}）：{e.Message}", error: true);
            return null;
        }
    }

    static string Md5Hex(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>与 JS <c>encodeURIComponent</c> 完全一致的转义（wbi 签名要按它算，差一个字符 w_rid 就不对）。</summary>
    static string JsEnc(string s) => Uri.EscapeDataString(s ?? "")
        .Replace("%21", "!").Replace("%2A", "*").Replace("%27", "'").Replace("%28", "(").Replace("%29", ")");

    string _wbiImgKey = "";
    string _wbiSubKey = "";
    DateTime _wbiFetchedUtc = DateTime.MinValue;

    async Task<(string Img, string Sub)?> WbiKeysAsync(CancellationToken ct)
    {
        if (_wbiImgKey.Length == 32 && (DateTime.UtcNow - _wbiFetchedUtc).TotalHours < 6)
            return (_wbiImgKey, _wbiSubKey);
        using var doc = await ApiGetAsync("https://api.bilibili.com/x/web-interface/nav", ct, quiet: true);
        try
        {
            if (doc != null && doc.RootElement.TryGetProperty("data", out var d)
                           && d.TryGetProperty("wbi_img", out var w))
            {
                static string Stem(string? u) => (u ?? "").Split('/').Last().Split('.')[0];
                var img = Stem(w.TryGetProperty("img_url", out var a) ? a.GetString() : "");
                var sub = Stem(w.TryGetProperty("sub_url", out var b) ? b.GetString() : "");
                if (img.Length == 32 && sub.Length == 32)
                {
                    _wbiImgKey = img;
                    _wbiSubKey = sub;
                    _wbiFetchedUtc = DateTime.UtcNow;
                    return (img, sub);
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>把参数集算成 wbi 签名串（返回 "k=v&amp;…&amp;w_rid=…"，已含 wts）。失败返回 null（调用方走不签名那条路）。</summary>
    async Task<string?> WbiQueryAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var keys = await WbiKeysAsync(ct);
        if (keys == null) return null;
        var raw = keys.Value.Img + keys.Value.Sub;
        var mixin = new string(MixinKeyEncTab.Select(i => raw[i]).ToArray())[..32];

        var p = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in parameters) p[kv.Key] = kv.Value ?? "";
        p["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var query = string.Join("&", p.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{JsEnc(kv.Key)}={JsEnc(kv.Value)}"));
        return query + "&w_rid=" + Md5Hex(query + mixin);
    }

    // ==================== 官方表情 ====================

    internal string EmoteCacheFile => Path.Combine(_runner.StateRoot, "bilibili-emotes.json");

    /// <summary>拉一份"这个号能用的官方表情"（只读）。返回 (包名, 转义符) 列表；失败回退到本地缓存。</summary>
    internal async Task<List<(string Pkg, string Code)>> FetchEmoteCodesAsync(CancellationToken ct)
    {
        var list = new List<(string, string)>();
        using (var doc = await ApiGetAsync(
                   "https://api.bilibili.com/x/emote/user/panel/web?business=reply", ct, quiet: true))
        {
            try
            {
                if (doc != null && doc.RootElement.TryGetProperty("data", out var d)
                               && d.TryGetProperty("packages", out var pkgs) && pkgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pkg in pkgs.EnumerateArray())
                    {
                        var name = pkg.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
                        if (!pkg.TryGetProperty("emote", out var em) || em.ValueKind != JsonValueKind.Array) continue;
                        foreach (var e in em.EnumerateArray())
                        {
                            var code = e.TryGetProperty("text", out var c) ? (c.GetString() ?? "") : "";
                            if (code.Length > 0) list.Add((name, code));
                        }
                    }
                }
            }
            catch { }
        }

        if (list.Count > 0)
        {
            try
            {
                File.WriteAllText(EmoteCacheFile,
                    JsonSerializer.Serialize(list.Select(x => new[] { x.Item1, x.Item2 })),
                    new UTF8Encoding(false));
            }
            catch { }
            return list;
        }

        try
        {
            if (File.Exists(EmoteCacheFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(EmoteCacheFile));
                foreach (var row in doc.RootElement.EnumerateArray())
                    list.Add((row[0].GetString() ?? "", row[1].GetString() ?? ""));
            }
        }
        catch { }
        return list;
    }

    // ==================== @我的 ====================

    /// <summary>读「@我的」收件箱（只读）。失败返回空表。</summary>
    internal async Task<List<BiliMention>> FetchMentionsAsync(CancellationToken ct, bool quiet = true)
    {
        var outList = new List<BiliMention>();
        using var doc = await ApiGetAsync(
            "https://api.bilibili.com/x/msgfeed/at?platform=web&build=0&mobi_app=web", ct, quiet);
        if (doc == null) return outList;
        try
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var d)) return outList;
            if (!d.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return outList;
            foreach (var it in items.EnumerateArray())
            {
                var m = new BiliMention();
                m.Id = it.TryGetProperty("id", out var id) ? id.ToString() : "";
                m.AtTime = it.TryGetProperty("at_time", out var at) && at.TryGetInt64(out var ts) ? ts : 0;
                if (it.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    m.WhoName = u.TryGetProperty("nickname", out var n) ? (n.GetString() ?? "") : "";
                    m.WhoMid = u.TryGetProperty("mid", out var mid) ? mid.ToString() : "";
                }
                if (it.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                {
                    m.Text = item.TryGetProperty("source_content", out var sc) ? (sc.GetString() ?? "") : "";
                    m.VideoTitle = item.TryGetProperty("title", out var ti) ? (ti.GetString() ?? "") : "";
                    m.Uri = item.TryGetProperty("uri", out var uri) ? (uri.GetString() ?? "") : "";
                    m.Business = item.TryGetProperty("business", out var biz) ? (biz.GetString() ?? "") : "";
                    m.Aid = item.TryGetProperty("subject_id", out var sid) && sid.TryGetInt64(out var a) ? a : 0;
                    m.SourceId = item.TryGetProperty("source_id", out var src) && src.TryGetInt64(out var s) ? s : 0;
                    m.RootId = item.TryGetProperty("root_id", out var rid) && rid.TryGetInt64(out var r) ? r : 0;
                    m.BusinessId = item.TryGetProperty("business_id", out var bid) && bid.TryGetInt64(out var bv) ? bv : 1;
                    if (item.TryGetProperty("at_details", out var ad) && ad.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var one in ad.EnumerateArray())
                        {
                            var who = one.TryGetProperty("nickname", out var nn) ? (nn.GetString() ?? "") : "";
                            var whoMid = one.TryGetProperty("mid", out var mm) ? mm.ToString() : "";
                            if (who.Length > 0) m.AtNames.Add(who);
                            if (Clean(Cfg.SelfMid).Length > 0 && whoMid == Clean(Cfg.SelfMid)) m.AtMe = true;
                        }
                    }
                }
                outList.Add(m);
            }
        }
        catch (Exception e) { if (!quiet) _runner.Log($"解析 @收件箱 失败：{e.Message}", error: true); }
        return outList;
    }

    // ==================== 图片上传 ====================

    static string MimeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    /// <summary>把本地图丢进 B站图床，换一个能塞进评论 pictures 的 img_src。</summary>
    internal async Task<(string Url, int W, int H, double Kb)?> UploadImageAsync(string file, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(file, ct);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Clean(Cfg.BiliJct)), "csrf");
        form.Add(new StringContent("new_dyn"), "biz");
        form.Add(new StringContent("daily"), "category");
        var img = new ByteArrayContent(bytes);
        img.Headers.ContentType = new MediaTypeHeaderValue(MimeOf(file));
        form.Add(img, "file_up", Path.GetFileName(file));

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api.bilibili.com/x/dynamic/feed/draw/upload_bfs") { Content = form };
        req.Headers.TryAddWithoutValidation("Cookie", CookieHeader());
        req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        req.Headers.TryAddWithoutValidation("User-Agent", BiliUa);

        using var res = await _apiUpload.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        var msg = root.TryGetProperty("message", out var ms) ? (ms.GetString() ?? "") : "";
        if (code != 0)
        {
            _runner.Log($"图片上传被拒：code={code} message={msg}", error: true);
            throw new InvalidOperationException($"B站图床拒绝（code={code} {msg}）");
        }
        var d = root.GetProperty("data");
        var url = d.TryGetProperty("image_url", out var iu) ? (iu.GetString() ?? "") : "";
        var w = d.TryGetProperty("image_width", out var iw) && iw.TryGetInt32(out var wv) ? wv : 0;
        var h = d.TryGetProperty("image_height", out var ih) && ih.TryGetInt32(out var hv) ? hv : 0;
        var kb = d.TryGetProperty("img_size", out var isz) && isz.TryGetDouble(out var kv) ? kv : 0;
        if (url.Length == 0) throw new InvalidOperationException("图床没返回 image_url");
        return (url, w, h, kb);
    }

    // ==================== 发评论（带图）====================

    /// <summary>
    /// 直接发一条评论（表单 + WBI 签名），可带 pictures。
    /// 与上游 <c>comment.mjs:201-208</c> 的请求形状逐字对齐：**参数集一起签名**，w_rid/wts 放 query，参数放 body。
    /// </summary>
    internal async Task<(int Code, string Message, string Rpid)> PostCommentAsync(
        string aid, string message, string? picturesJson, string? root, string? parent, string bvid,
        CancellationToken ct)
    {
        var p = new Dictionary<string, string>
        {
            ["type"] = "1",
            ["oid"] = aid,
            ["message"] = message ?? "",
            ["plat"] = "1",
            ["csrf"] = Clean(Cfg.BiliJct),
        };
        if (!string.IsNullOrWhiteSpace(root)) p["root"] = root!.Trim();
        if (!string.IsNullOrWhiteSpace(parent)) p["parent"] = parent!.Trim();
        if (!string.IsNullOrWhiteSpace(picturesJson)) p["pictures"] = picturesJson!;

        var query = await WbiQueryAsync(p, ct);
        var url = query == null
            ? "https://api.bilibili.com/x/v2/reply/add"
            : "https://api.bilibili.com/x/v2/reply/add?" + query;

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(p),
        };
        req.Headers.TryAddWithoutValidation("Cookie", CookieHeader());
        req.Headers.TryAddWithoutValidation("User-Agent", BiliUa);
        req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        req.Headers.TryAddWithoutValidation("Referer",
            Clean(bvid).Length > 0 ? $"https://www.bilibili.com/video/{Clean(bvid)}/" : "https://www.bilibili.com/");

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        var rootEl = doc.RootElement;
        var code = rootEl.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        var msgTxt = rootEl.TryGetProperty("message", out var ms) ? (ms.GetString() ?? "") : "";
        var rpid = "";
        if (code == 0 && rootEl.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("rpid_str", out var rs) && rs.ValueKind == JsonValueKind.String) rpid = rs.GetString() ?? "";
            else if (data.TryGetProperty("rpid", out var rp)) rpid = rp.ToString();
        }
        return (code, msgTxt, rpid);
    }

    /// <summary>上游 comment.mjs:292-302 的那张"失败提示表"，搬过来让报错能看懂。</summary>
    internal static string CommentFailHint(int code) => code switch
    {
        -101 => "账号未登录：cookie 失效（SESSDATA 必须是这个名字）",
        -102 => "账号被封停",
        -111 => "csrf 校验失败：bili_jct 不对或与 SESSDATA 不是同一个号",
        -509 => "请求过于频繁",
        12015 => "需要评论验证码（被风控盯上了，停手）",
        12016 => "内容含敏感信息",
        12035 => "被 UP 主列入评论黑名单",
        12051 => "重复评论被判刷屏",
        12002 => "评论区已关闭",
        _ => "",
    };
}