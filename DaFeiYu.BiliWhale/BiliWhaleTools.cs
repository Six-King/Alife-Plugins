using System;
using System.Collections.Generic;
using System.Text;

namespace DaFeiYu.BiliWhale;

/// <summary>
/// ⑰-5 的"场景点名表"（配置里那份默认值 + 渲染）。
///
/// 只点名**真实存在**的工具（都在本插件 [XmlFunction] 里注册着，一个没编）；
/// 表里没点到的工具照旧全部可用 —— 这里只负责"先想到哪几个"。
/// </summary>
internal static class SceneToolHintsTable
{
    /// <summary>内置默认表：场景=工具A,工具B（每行一条）。配置留空或全被过滤掉时用它。</summary>
    public const string Default =
        "选题（刷首页/找片子）=BiliFeed,BiliDebrief,BiliSearch\n" +
        "看懂一条视频=BiliDeepRead,BiliSubtitles,BiliVideo\n" +
        "看看谁找过人家=BiliCommentDigest,BiliThread,BiliLikes\n" +
        "分享给主人=BiliComment,BiliDm,BiliRecon,BiliQuotaStatus\n" +
        "回复别人=BiliComment,BiliThread,BiliCheckComment,BiliDraftAdd\n" +
        "评论配图与表情=BiliCommentImage,BiliEmotes\n" +
        "自己的动态=BiliDynamicPost,BiliDynamicImage,BiliDynamicList\n" +
        "日记与收工=BiliDiaryWrite,BiliDiaryLog,BiliDiaryShow,BiliNotify\n" +
        "查登录态/环境=BiliSession,BiliWhaleStatus,BiliDeps\n" +
        "冷门命令兜底=BiliRun";
}

public partial class BiliWhaleModule
{
    /// <summary>上一次真正注入给她的场景点名块（空串=当时没注入）。用来判断"要不要重发一次"。</summary>
    string _sceneHintPublished = "";

    internal bool SceneHintsActive => IsScenedMode(Cfg.ToolExposureMode);

    static bool IsScenedMode(string? mode)
        => string.Equals((mode ?? "").Trim(), "scened", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 组装"注入给她的场景点名"整段文本。
    /// full（默认）→ **返回空串**：注入的东西与以前逐字节一致，行为零变化 ✓
    /// scened → 把配置里那张表渲染成一段"优先用这几个"的提示（工具一个都没摘）。
    /// </summary>
    internal string BuildSceneHintBlock()
    {
        if (!SceneHintsActive) return "";

        var rows = ParseSceneHints(Cfg.SceneToolHints);
        if (rows.Count == 0) rows = ParseSceneHints(SceneToolHintsTable.Default);
        if (rows.Count == 0) return "";     // 表被清空且内置也解析不出来 → 宁可不注入，也不注入半截话

        var sb = new StringBuilder();
        sb.AppendLine("【场景点名·优先工具】（配置里开了 scened 模式；工具**一个都没少**，这张表只说明「先想到这几个」）");
        foreach (var (scene, tools) in rows)
            sb.AppendLine($"   · {scene}：{tools}");
        sb.AppendLine("   ⚠️ 表里没点到 ≠ 不能用：任何工具照旧可用（比如你自己的 <BiliRun command=\"…\"/> 兜底口）。");
        sb.AppendLine("   这张表只是省掉「翻一遍函数清单」的时间；真需要别的工具就直接用，不用请示。");
        return sb.ToString().TrimEnd();
    }

    /// <summary>场景点名块变了吗（配置热更新/主人改了表 → 3 秒内重发一次常驻提示）。</summary>
    internal bool SceneHintChanged()
    {
        var now = BuildSceneHintBlock();
        if (string.Equals(now, _sceneHintPublished, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>给日志用的一句话摘要（不含任何凭据；只说明注入了多少个场景 / 多少字）。</summary>
    internal string SceneHintSummary()
    {
        if (!SceneHintsActive) return "full（不加任何提示，行为与以前一致）";
        var block = BuildSceneHintBlock();
        if (block.Length == 0) return "scened（表是空的 → 什么都没注入）";
        var scenes = 0;
        foreach (var line in block.Split('\n'))
            if (line.TrimStart().StartsWith("·", StringComparison.Ordinal)) scenes++;
        return $"scened · {scenes} 个场景 · {block.Length} 字（只加提示，一个工具都没摘）";
    }

    /// <summary>
    /// 解析点名表：每行 `场景=工具A,工具B`（行内以 # 开头=注释；也容忍中文全角逗号/顿号）。
    /// 解析不出工具名的行直接丢掉 —— 宁可少注入，也不给她一张写着不存在工具的假表 ✗
    /// </summary>
    static List<(string Scene, string Tools)> ParseSceneHints(string? raw)
    {
        var list = new List<(string, string)>();
        foreach (var rawLine in (raw ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) eq = line.IndexOf('：');          // 容忍写成中文冒号
            if (eq <= 0) continue;
            var scene = line[..eq].Trim().Trim('·', '-', ' ');
            var toolsRaw = line[(eq + 1)..];
            var tools = new List<string>();
            foreach (var t in toolsRaw.Split(new[] { ',', '，', '、', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = t.Trim().Trim('<', '>', '/', '"');
                if (name.Length >= 4 && name.StartsWith("Bili", StringComparison.Ordinal) && !tools.Contains(name))
                    tools.Add(name);
            }
            if (scene.Length == 0 || tools.Count == 0) continue;
            list.Add((scene, string.Join(", ", tools)));
        }
        return list;
    }
}