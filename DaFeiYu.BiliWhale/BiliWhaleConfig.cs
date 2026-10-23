using System.ComponentModel;

namespace DaFeiYu.BiliWhale;

/// <summary>
/// B站养鱼配置。
/// 一部分喂给 dsh-bili-whale 工具链（写成 bili-whale.config.json），一部分是本插件自己的运行参数。
/// </summary>
public class BiliWhaleConfig
{
    // ==================== 运行环境 ====================

    [DisplayName("Node 可执行文件")]
    [Description("留空=自动探测（DSH 运行时 → Program Files → PATH）。DSH 自带的那个最稳")]
    public string NodeExe { get; set; } = "";

    [DisplayName("ffmpeg 可执行文件")]
    [Description("留空=自动探测（PATH → winget Links → 插件 bin → 剪映自带）。只影响「抽帧看画面」这一路，不装也能跑")]
    public string FfmpegPath { get; set; } = "";

    [DisplayName("状态目录")]
    [Description("留空=Storage\\PluginData\\DaFeiYu.BiliWhale。凭据、额度、日记、截图、看过的视频全在这里")]
    public string StateRoot { get; set; } = "";

    [DisplayName("命令超时(秒)")]
    [Description("单条工具链命令的等待上限（抽帧较慢，默认 300）")]
    public int CommandTimeoutSec { get; set; } = 300;

    [DisplayName("回显给AI的输出上限(字符)")]
    [Description("工具链输出很长（字幕/弹幕），超过就截断，避免吃掉上下文。默认 8000")]
    public int MaxOutputChars { get; set; } = 8000;

    // ==================== 账号（她的 B 站号）====================

    [DisplayName("SESSDATA")]
    [Description("B站登录凭据之一。F12 → Application → Cookies → bilibili.com 里手动复制（HttpOnly，脚本读不到）。不会回显")]
    public string Sessdata { get; set; } = "";

    [DisplayName("bili_jct")]
    [Description("B站登录凭据之二（CSRF token）。不会回显")]
    public string BiliJct { get; set; } = "";

    [DisplayName("buvid3")]
    [Description("B站设备指纹（可选，建议填）。不会回显")]
    public string Buvid3 { get; set; } = "";

    [DisplayName("access_token（扫码登录自动写入）")]
    [Description("点配置页「扫码登录」成功后由插件自动写入（有的返回里带、有的不带）。手填也不冲突；不会回显")]
    public string AccessToken { get; set; } = "";

    [DisplayName("扫码登录·二维码路径")]
    [Description("点配置页「扫码登录」时那张二维码 PNG 存到哪。留空=桌面 bili-login-qr.png。扫完（或过期/超时）会自动删掉")]
    public string QrImagePath { get; set; } = "";

    [DisplayName("扫码登录·等待秒数")]
    [Description("二维码最多等你扫多久，默认 150 秒。期间每 2 秒问一次；超时/过期就把图片删掉，可再点一次")]
    public int QrLoginWaitSec { get; set; } = 150;

    [DisplayName("她自己 mid（只读）")]
    [Description("由 BiliSession 自动写入，用来认「谁回了我 / 谁点了赞」。留空即可")]
    public string SelfMid { get; set; } = "";

    [DisplayName("她自己昵称（只读）")]
    [Description("由 BiliSession 自动写入。留空即可")]
    public string SelfName { get; set; } = "";

    // ==================== 主人（分享要 @ 的人）====================

    [DisplayName("主人 mid")]
    [Description("你的 B站 UID（数字）。分享评论要 @ 的人、私信唯一收件人、点赞/回复识别都靠它")]
    public string OwnerMid { get; set; } = "";

    [DisplayName("主人昵称")]
    [Description("写进分享正文里的 @昵称（改过名就用 BiliWhois 核一下）")]
    public string OwnerName { get; set; } = "";

    [DisplayName("角色名")]
    [Description("她怎么被称呼。留空=不注入身份提示")]
    public string PersonaName { get; set; } = "";

    [DisplayName("她怎么称呼你")]
    [Description("例如「主人」")]
    public string OwnerTitle { get; set; } = "主人";

    [DisplayName("她怎么自称")]
    [Description("例如「我」/「人家」")]
    public string SelfClaim { get; set; } = "我";

    // ==================== 作息（生物钟）====================

    [DisplayName("生物钟开关")]
    [Description("开=她自己按作息醒来看视频、写日记、分享；关=只在你叫她时才动")]
    public bool ClockEnabled { get; set; } = true;

    [DisplayName("作息窗口")]
    [Description("格式：开始-结束=醒来次数，多个用逗号分隔。例如 07:00-09:00=1,20:00-24:00=1。窗口内随机一个时刻醒来")]
    public string NightWindows { get; set; } = "20:00-24:00=1";

    [DisplayName("不看概率")]
    [Description("每次醒来掷骰子，这个概率下她今天不想看（0.3 = 三成）。她也有权利不看，但照样写日记")]
    public double SkipChance { get; set; } = 0.3;

    [DisplayName("单次初筛条数")]
    [Description("每次醒来先粗筛几条候选（只看文字，便宜），再从里面挑 2-3 条深读")]
    public int MaxVideos { get; set; } = 5;

    // ==================== 口味 ====================

    [DisplayName("爱看")]
    [Description("逗号分隔。选题会按这些关键词加权（命中越多排越前）")]
    public string LikeTags { get; set; } = "AI 工具与插件,鬼畜与怀旧向剪辑,猫咪,Live2D/模型,B站生态八卦";

    [DisplayName("不爱看")]
    [Description("逗号分隔。命中就降权（带货/擦边/标题党/纯搬运）")]
    public string DislikeTags { get; set; } = "带货,擦边,标题党,纯搬运";

    // ==================== 分享（主动 @ 你）====================

    [DisplayName("启用分享")]
    [Description("关掉=她只看不说。开着也受每日上限与评分下限约束，不值得就不发")]
    public bool ShareEnabled { get; set; } = true;

    [DisplayName("分享方式")]
    [Description("comment=在视频评论区发一条带 @你 的公开评论；dm=B站私信发给你（不吵别人，带视频链接）")]
    public string ShareVia { get; set; } = "comment";

    [DisplayName("分享评分下限")]
    [Description("满分 20。不到这个分就是「今天没有值得发的」——少发、不发都是合法结果。默认 13")]
    public int MinScore { get; set; } = 13;

    [DisplayName("每日分享上限")]
    [Description("硬限制，写进代码由闸门强制。默认 3（调高容易被判刷屏）")]
    public int ShareDaily { get; set; } = 3;

    [DisplayName("沉默几天后收敛")]
    [Description("你连续这么多天没任何表态（点赞/回复都算），她开始主动少说")]
    public int SilentAfterDays { get; set; } = 3;

    [DisplayName("沉默几天后停止分享")]
    [Description("到这个天数她当天不再分享，但两本日记照写")]
    public int SilentStopDays { get; set; } = 7;

    // ==================== 日记与通知 ====================

    [DisplayName("推送日记摘要")]
    [Description("关掉=她照写两本日记，但不推给你（日记本身不能关）")]
    public bool DiaryPush { get; set; } = true;

    [DisplayName("日记目录")]
    [Description("两本日记 txt 放在哪。留空 = 运行时目录下的 RJ（默认 K:\\AI\\Alife\\Runtime\\RJ）")]
    public string DiaryDir { get; set; } = "";

    [DisplayName("公开日记文件名")]
    [Description("给主人看的那本 txt。默认 养鱼日记.txt（没写 .txt 会自动补）")]
    public string DiaryName { get; set; } = "养鱼日记.txt";

    [DisplayName("私密日记文件名")]
    [Description("她自己那本 txt（不加滤镜）。默认 养鱼日记-私密.txt")]
    public string PrivateDiaryName { get; set; } = "养鱼日记-私密.txt";

    [DisplayName("通知方式")]
    [Description("qq=让她用 QChat 私聊发给你（推荐）；file=只写进通知日志，不发消息")]
    public string NotifyType { get; set; } = "qq";

    [DisplayName("通知目标QQ")]
    [Description("报备要发到哪个 QQ。留空=BiliNotify 只记日志、不发消息")]
    public string NotifyQq { get; set; } = "";

    // ==================== 倾听（评论区）====================

    [DisplayName("评论区监听")]
    [Description("开=有个零 token 的守夜进程盯着「回复我的」，有人找她就叫醒她；没人找就完全静默")]
    public bool ListenComment { get; set; } = true;

    [DisplayName("监听间隔(秒)")]
    [Description("多久看一次收件箱。别低于 60，B站接口经不起频繁锤（默认 300）")]
    public int CommentIntervalSec { get; set; } = 300;

    [DisplayName("监听范围")]
    [Description("both=回复我的+@我的；reply=只看「回复我的」")]
    public string ListenScope { get; set; } = "both";

    [DisplayName("只在作息窗口内听")]
    [Description("开=只在她醒着的窗口里守（省 token、更自然）；关=全天守")]
    public bool ListenInWindow { get; set; } = true;

    [DisplayName("同一个人只回一条")]
    [Description("开=同一个人 30 天内最多回一条（只对「回复某人」生效）")]
    public bool OncePerPerson { get; set; } = false;

    [DisplayName("私信监听")]
    [Description("开=每 20 秒看一次你的私信，有新消息才叫醒她（只认你，陌生人一律忽略）")]
    public bool ListenDm { get; set; } = false;

    // ==================== @我 立刻通知 ====================
    // 为什么单独一条腿：上游的 comment-await 是"回复我的 + @我的"合成一个进程、默认 5 分钟一轮、
    // 而且受作息窗口限制 —— 被人 @ 了要等最多 5 分钟、夜里还可能等到第二天。
    // @是"有人当面喊她"，所以这里自己拿 msgfeed/at 高频轮询（只读、零 token），命中立刻叫醒。

    [DisplayName("@我 立刻通知")]
    [Description("开=有个小进程高频盯「@我的」，有人 @ 她立刻叫醒她（没人 @ 就完全不花钱）。关=退回上游那个 5 分钟一轮的合成守夜")]
    public bool ListenMention { get; set; } = true;

    [DisplayName("@我 轮询间隔(秒)")]
    [Description("多久看一次「@我的」。默认 45，最低 20；这是只读接口，但也别太频繁")]
    public int MentionIntervalSec { get; set; } = 45;

    [DisplayName("@我 全天盯")]
    [Description("开=不管作息窗口，有人 @ 就立刻叫（推荐：@ 是有人当面喊她）；关=只在作息窗口里盯")]
    public bool MentionAllDay { get; set; } = true;

    [DisplayName("@我 启动补看(小时)")]
    [Description("启动时往回补这么多小时里漏掉的 @（Alife 关着的时候也不漏）。默认 6，0=只看启动之后的新 @")]
    public int MentionCatchUpHours { get; set; } = 6;

    [DisplayName("@我 只认@她本人的")]
    [Description("开=只叫醒「at_details 里确实有她 mid」的（推荐，最准）；关=只要进了 @收件箱 就算")]
    public bool MentionOnlySelf { get; set; } = true;

    // ==================== 深读：真正看懂一条视频 ====================
    // 上游 debrief 把字幕写进文件却不打印、标签/分P/章节根本不取、抽帧只是 9 格拼图且从不送给视觉模型。
    // 所以"看懂"这条腿由插件补齐：完整字幕 + 标签/分P/章节 + 关键帧逐帧喂视觉模型。

    [DisplayName("深读抽帧数")]
    [Description("BiliDeepRead 抽几张关键帧交给视觉模型看画面。0=不看画面（便宜很多）。默认 8")]
    public int DeepReadFrames { get; set; } = 8;

    [DisplayName("深读自动看画面")]
    [Description("开=插件自己把关键帧和封面送进视觉模型，把画面描述并进深读结果（不用她再手动调视觉工具）；关=只给图片路径")]
    public bool DeepReadVision { get; set; } = true;

    [DisplayName("深读每帧描述字数")]
    [Description("单帧画面描述的字数上限（默认 220）。调大更细但更慢")]
    public int DeepReadVisionTokens { get; set; } = 220;

    [DisplayName("深读字幕上限(字符)")]
    [Description("整篇字幕喂给她的字符上限，超出就取头+尾。默认 9000")]
    public int DeepReadTranscriptChars { get; set; } = 9000;

    [DisplayName("深读听声音(音轨转写)")]
    [Description("开=用本机 SenseVoice 把视频音轨流式转写成带时间戳的台词（没字幕的视频也能听懂）。关=只听字幕。本地模型有开销，所以有秒数上限，且只在深读时按需跑")]
    public bool DeepReadAudio { get; set; } = true;

    [DisplayName("深读听多久(秒)")]
    [Description("音轨最多转写多少秒。默认 360（6 分钟），按 20 秒一段流式喂给识别器、每段用完即删，不把视频读进内存。0=不限（不建议）")]
    public int DeepReadAudioSeconds { get; set; } = 360;

    // ==================== 评论配图（梗图）与官方表情 ====================
    // 官方表情：评论正文里直接写转义符 [tv_doge] 就行（B站服务端自己解析）。
    // 梗图：B站评论支持 pictures，但要先把本地图上传到 B站图床换一个 img_src。

    [DisplayName("允许评论配图")]
    [Description("开=可以用 BiliCommentImage 把本地图片（含大肥鱼梗图库 MemeFetch 下来的梗图）随评论发出去。关=这个函数直接拒绝")]
    public bool AllowImageComment { get; set; } = true;

    [DisplayName("配图自动转码")]
    [Description("梗图多是 webp，B站图床只收 png/jpg/gif；开=用 ffmpeg 自动转 jpg 再传。关=只接受本来就合规的图片")]
    public bool ConvertImageForUpload { get; set; } = true;

    // ==================== ⑰-5 每轮工具点名（默认不改行为） ====================
    // 只影响"注入给她的常驻提示里多不多一段场景点名"，**绝不从工具注册表里摘任何工具**：
    // 摘了会挡她自助（她本来能自己绕路解决的事会变成做不到）。
    // full（默认）= 一个字都不加，与以前完全一致 ✓

    [DisplayName("工具暴露模式")]
    [Description("full=现状（不加任何东西，全凭她自己挑工具）；scened=在常驻提示里按场景点名「优先用哪几个」。注意：scened 也**不会摘掉任何工具**，只是多一段提示")]
    public string ToolExposureMode { get; set; } = "full";

    [DisplayName("场景点名表（只在 scened 模式生效）")]
    [Description("每行一条：场景=工具A,工具B。只在 scened 模式下追加进常驻提示；full 模式完全不出现。留空/乱填=退回内置那张表")]
    public string SceneToolHints { get; set; } = SceneToolHintsTable.Default;
}