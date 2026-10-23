# Alife 移植版说明 · `DaFeiYu.BiliWhale`

把 [`Alterssss/dsh-bili-whale`](https://github.com/Alterssss/dsh-bili-whale)（MIT）移植成 **Alife 框架的插件模块**：
让她（大肥鱼）像过日子一样刷 B 站 —— 自己按作息醒来、选题、看懂视频、写两本日记、把值得的分享给主人；并监听评论区与待审回复。

> 上游原版是 **DeepSeek Harness（DSH）** 的插件；本移植**不重写任何 B 站接口**，而是把上游的 Node 工具链整体搬过来当外部进程调用（薄封装）。
> 上游代码以 MIT 许可分发，许可原文见 `upstream-LICENSE`，上游 README/CHANGELOG/NOTICE 一并保留。

---

## 1. 目录结构

```
DaFeiYu.BiliWhale/
├─ manifest.json              插件清单（依赖 Alife.Function.FunctionCaller）
├─ BiliWhaleModule.cs         模块本体：35 个 XmlFunction（她调用的工具面）
├─ BiliWhaleClock.cs          生物钟 + 评论区守夜 + 私信守夜（partial）
├─ BiliWhaleRunner.cs         运行环境：定位 node/ffmpeg、写配置与凭据、起子进程
├─ BiliWhaleConfig.cs         插件配置（一部分翻译成上游的统一配置）
├─ tools/                     ★ 上游 Node 工具链（41 个 .mjs，原样搬运）
├─ upstream-*.{md,json}       上游 README / CHANGELOG / NOTICE / LICENSE / package.json
└─ ALIFE-PORT.md              本文件
```

运行时数据（**不在这里**，见 §3）：

```
Storage/PluginData/DaFeiYu.BiliWhale/
├─ bili-whale.config.json     由插件配置翻译生成（喂给工具链）
├─ biliwhale.log              插件自己的日志（含"生物钟自检"，排错先看它）
└─ home/                      ← 被伪装成"家目录"给子进程用
   ├─ .bili-ai/credentials.json   B站凭据（SESSDATA / bili_jct / buvid3）
   ├─ .bili-ai/quota.json         发言额度与白名单
   ├─ .bili-ai/replied.json       "同一个人 30 天只回一条"的记账
   ├─ .bili-ai/feedback.json      主人的表态（沉默档位靠它）
   ├─ .dsh/whale-night.json       今天的作息计划（窗口/时刻/醒了几次）
   ├─ .dsh/whale-diary.md         ★ 两本日记之一：给主人的信
   ├─ .dsh/whale-diary-private.md ★ 两本日记之二：给自己的（不加滤镜）
   ├─ .dsh/whale-log.jsonl        结构化事件流
   ├─ .dsh/whale-notify.log       报备记录
   └─ .dsh/shots/                 抽帧出的九宫格图
```

## 2. 关键设计：**不改上游一行逻辑**，靠环境变量"搬家"

上游每个脚本都从 `process.env.USERPROFILE || process.env.HOME` 推导状态路径，配置路径则可用 `BILI_WHALE_CONFIG` 覆盖。
于是插件给每个子进程换一套环境变量，就把上游的全部状态关进了插件数据目录：

| 环境变量 | 插件给的值 | 作用 |
|---|---|---|
| `USERPROFILE` / `HOME` | `<PluginData>/home` | 凭据、额度、日记、事件、截图全部落在这里 |
| `BILI_WHALE_CONFIG` | `<PluginData>/bili-whale.config.json` | 统一配置（作息/分享/倾听/口味） |
| `DSH_HOME` | `<PluginData>/home/.dsh` | 兜底（部分脚本认它） |
| `FFMPEG` | 探测到的 ffmpeg 全路径 | 抽帧看画面（不装也能跑，只是少一条腿） |

**好处**：上游升级时把 `tools/` 整个换掉即可，不会和本移植的改动打架。

### 对上游做的唯一改动（2 行，纯显示文本）

上游输出里有两处写着 DSH 专属的工具名，会误导她（她没有 `notify_wechat` 这个工具，也不该绕过插件去直接跑 `node bili.mjs`）：

| 文件 | 改动 |
|---|---|
| `tools/commands/diary.mjs:108` | 输出提示里的 `notify_wechat`（+ 指向上游 AGENTS.md 的说明）→ `<BiliNotify message="…"/>` |
| `tools/commands/feed.mjs:210` | 输出提示里的 `node bili.mjs debrief <BV>` → `<BiliDebrief bvid="<BV>"/>` |

不改这两处的风险是实的：她可能去调一个不存在的工具，或用 `ProcessService` 直接跑 `node bili.mjs`（那会绕过环境隔离、把状态写进 Alife 进程自己的家目录）。

### 上游自身的缺陷（3 条命令不可用，**非本移植引入**）

`tools/bili.mjs` 的命令映射表共 38 条，其中 3 条指向**上游仓库里并不存在**的文件：

| 命令 | 映射到 | 影响 |
|---|---|---|
| `auto` | `commands/danmaku-auto.mjs` | 直播间自动回复 —— 不在本次范围 |
| `dyn-keywords` | `diagnostics/dyn-detail-keywords.mjs` | 动态排查工具 —— 不在范围 |
| `dyn-page` | `diagnostics/dyn-page-keywords.mjs` | 动态排查工具 —— 不在范围 |

其余 35 条命令的文件都在。

### 上游配置读取不一致（**本移植已从插件侧绕开**）

`tools/lib/config.mjs` 认 `$BILI_WHALE_CONFIG`（本移植正是靠它把配置搬进 `PluginData`），
但有两个脚本是**自己拼路径**去读 `<HOME>/.dsh/bili-whale.config.json` 的，根本不走那个模块：

| 文件 | 症状 |
|---|---|
| `tools/commands/fav.mjs:20` | 收藏直接 `ENOENT` 崩（实测：2026-10-20 22:00 她第一次收藏就撞上） |
| `tools/commands/selftest.mjs:16` | 配置文件那行永远显示"没有"，自检表第一行红 |

**修法：不改上游源码** —— `BiliWhaleRunner.WriteToolConfig()` 把同一份 JSON **双写**：既写 `$BILI_WHALE_CONFIG` 指向的正式文件，也往 `<HOME>/.dsh/` 补一份同内容副本。
两个脚本照旧能找到配置，而 `tools/` 依然可以整包替换。（`notify.mjs` 只是注释里提到这个路径，不读，无需处理。）

## 3. 与 Alife 的对接

- **工具面**：35 个 `[XmlFunction]`，读命令（`BiliFeed`/`BiliDebrief`/`BiliSearch`/`BiliCommentDigest`/`BiliLikes`/`BiliDiaryShow`…）+ 写命令（`BiliComment`/`BiliDm`/`BiliDynamicPost`/`BiliFav`/`BiliDraftAdd`…）+ 兜底 `BiliRun`。
  所有写操作**必须显式 `dry`**：`dry="true"` 只打印请求，`dry="false"` 才真发。
- **生物钟**：起一个廉价的 `node bili.mjs night clock` 子进程 —— 它在作息窗口内随机挑一刻，睡到点就**退出**（打印 `WHALE_WAKE`）；"今天不想看"则睡到窗口末退出（`WHALE_SKIP`）；次数用完 `WHALE_DONE`。
  插件监听它退出 → `interactor.Poke()` 把"你醒了"的指令推给她。用"进程退出"当闹钟：随机时刻 / 跨午夜 / 次数上限全在上游算，只有一处真相。
- **守夜（零 token）**：`node bili.mjs comment-await --quiet` 常驻守「回复我的」收件箱，**有人找她才退出**并叫醒她；没人理就一直静默（不烧 token）。私信同理（`dm-await`，默认关）。

### 分享闸门（代码强制；主人授权过第一条后**永久放开**，2026-10-22 21:15:29 已放开）

上游那句 `dry="true"` → "确认后 `dry="false"`" 在自主流程里会被她理解成"自己确认"，于是她第一次就直接发了（2026-10-20 21:58 实测踩到）。
所以加了一道**自闭合**的闸门：

- **未放行时**：`share && !dry && auto && !ShareGateOpen()` → **拒绝执行**，并回一条中文说明（2026-10-22 ⑭：不再往提示词里注入「报备流程」那些步骤文字 —— 闸门已永久放开，留着只会让她困惑）。
- **放行判定**：插件看到 `share && !auto && !dry` 且输出含 `评论已发出` → 写 `<StateRoot>/share-gate.json`（`opened:true`）→ **永久放开**，之后她回自主节奏。
- **为什么不用 `quota.json` 判断**：`tools/commands/quota.mjs:165` 是 `auto: !!opt.auto || !!opt.share` —— **任何分享都被记成 `auto:true`**，账本里分不出"自主发的"和"主人授权的"。所以状态必须由插件自己记。
- 只拦「分享型」；回别人评论 / 收藏 / 写日记不受影响。`BiliWhaleStatus` 里能看到闸门开没开。

2026-10-22 21:15:29 她自己真发出第一条分享（私信 `BV1Fr4y1F7P2`）→ 闸门**已永久放开**，她现在按正常节奏自主分享（明细见 §9.6）。

### 配置热更新：设置页改开关 → **立刻生效（不用重载插件）**

**框架事实（读 v4.6.1 源码确认）**：`Alife.Framework.ConfigurationSystem.SetConfiguration()` **只把配置写进文件，不通知活着的模块实例** ——
`ChatActivity` 只在实例创建那一刻赋一次 `Configuration`（`ChatActivity.cs:81-85`）。
所以按常规写法，改任何开关都必须 `reload_plugin` + 重启角色（35–55 秒），这既慢又容易踩激活竞态。

**本插件的做法**：框架的 `ChatActivity.StartTimer(0.3f, …)` 每 0.3 秒遍历所有 `ChatBehaviour` 调一次 `UpdateAsync` →
`ChatBehaviour.OnUpdate()`（`ChatBehaviour.cs:52-82`）。我们**拿它当心跳**：

```
OnUpdate()  →  每 10 个 tick（≈3 秒）比对模块配置文件时间戳
            →  变了就：反序列化新配置 → 重建 runner + Prepare()（重写凭据与工具链配置）
            →  StartAutomation()（内部先 Stop，按新开关起停子进程）
            →  日志一行：⚙ 配置热更新已生效（没有重载插件）：生物钟=开 评论区守夜=开 … → …
```

配置路径按 `ConfigurationSystem.GetConfigurationFilePath` 的同一顺序找：**角色级优先**
（`{StorageFolderPath}\{Character.StorageKey}\Configuration\{模块全名}.json`），回退全局
（`{StorageFolderPath}\Configuration\{模块全名}.json`）。

**实测（2026-10-20 22:48 / 22:49，双向）**：22:48:38 把 `ListenComment` 改 `false` → **22:48:40** 日志出现热更新行且 `自动化已启动：…评论区守夜=False`；
22:49:09 改回 `true` → 22:49:10 生效。全程未重载插件、未重启角色。

**顺带好处**：凭据也走同一条路 —— 在设置页改 `SESSDATA`/`bili_jct` 保存后，`Prepare()` 会重写凭据文件，不必再单独调 `BiliSetCookie`。
**注意**：热更新只在角色**已激活**时生效（`OnUpdate` 得有 tick）；角色没激活时改配置，下次激活自然就是新值。
- **注入**：`OnAwake` 里 `interactor.Prompt()` 注入一段用法（含两本日记的路径）；每次叫醒用 `interactor.Poke()` 推一份分步指令（醒来版 / 不看版 / 有人找你版）。
- **报备**：`BiliNotify` 记进 `whale-notify.log`，并按配置提示她用 `<QChat type="Private" targetid="…">` 私聊发给主人。

## 4. 配置对照（插件配置 → 上游配置）

| 插件配置 | 上游字段 | 说明 |
|---|---|---|
| `NightWindows`（如 `20:00-24:00=1`） | `night.windows[]` | 窗口 = 一次醒来的机会；`=1` 是次数 |
| `SkipChance` | `night.skipChance` | 每次醒来独立掷骰子，"不想看"是合法结果 |
| `MaxVideos` | `night.maxVideos` | 一次初筛几条 |
| `LikeTags` / `DislikeTags` | `preferences.like/dislike` | 选题加权 |
| `ShareEnabled` / `ShareVia` / `MinScore` / `ShareDaily` | `share.*` / `quota.shareDaily` | 分享开关 / 评论还是私信 / 评分阈值 / 每日上限 |
| `SilentAfterDays` / `SilentStopDays` | `share.silentAfterDays/silentStopDays` | 主人几天没表态 → 收敛 / 停发 |
| `ListenComment` / `CommentIntervalSec` / `ListenScope` / `ListenInWindow` / `OncePerPerson` | `listen.*` | 评论监听 |
| `Sessdata` / `BiliJct` / `Buvid3` | —（写进 `credentials.json`） | B站凭据，**不回显** |
| `OwnerMid` / `OwnerName` | `owner.mid/name` | 分享要 @ 的人 |
| `NodeExe` / `FfmpegPath` / `StateRoot` / `CommandTimeoutSec` / `MaxOutputChars` | —（插件自身） | 留空自动探测 |

`NotifyType=file` 是写死的：报备投递交给 Alife 侧的 QChat（不启用上游的 `notify` 后端）。

## 5. 已做的验证

### 5.1 装机与链路（2026-10-20 下午）

| 项 | 结果 |
|---|---|
| 依赖体检 `deps` | ✓ ffmpeg 9.0.2（winget 装）/ node v24.21.0 / 数据目录可写 |
| 环境隔离 | ✓ 凭据与日记全部落在 `PluginData`，没碰真家目录 |
| 工具链调用链 | ✓ 她调 `<BiliDeps/>` → C# 起 node → 结果回到她上下文 → 她按指令报备 |
| 生物钟叫醒 | ✓ 演练窗口 13:29–13:33 内 13:29 准点叫醒，随后正确进入 `done` 退避 |
| 启动自检 | ✓ 日志里"生物钟自检"会打出工具链**实际读到**的作息（防止配置静默失效） |

### 5.2 真实端到端一轮（2026-10-20 21:58，用小号账号实测）

窗口 `21:58–22:03` / 不看概率 0 / 一次 3 条 → **`21:58:39` 她自己醒**（日志 `生物钟叫醒（第 1 次 · 计划 21:58）`），随后：

| 环节 | 结果 |
|---|---|
| 选题 | ✓ 3 条 |
| 看懂 | ✓ 三条全读，含字幕 + ffmpeg 抽帧（`shots/subs/*debrief.txt`、`*-3x3.jpg`、`*-subs.jpg`） |
| 评分 | ✓ 18 / 16 / 14；只发"当天第一且 ≥ 阈值"那条 |
| 分享 | ✓ 评论真发（`rpid=（已抹）`、`@主人`、18 分） |
| 两本日记 | ✓ 公开 + 私人（私人本带「偏好校准」段落） |
| QQ 报备 | ✓ 22:01:31 / 22:01:32 两条私聊到达主人（llbot 日志 `send_private_msg`）+ `whale-notify.log` |
| 收藏 | ✗ → 已修（见 §2 的双写；`fav --list` 与 `selftest` 复验通过） |

**这一轮暴露并修掉的 3 个问题**：

1. **第一次分享没等主人过目就发了** —— 见 §3 的分享闸门。
2. **收藏 `ENOENT`** —— 见 §2 的配置双写。
3. **她看不见按函数的文档** —— Alife 的 `DocumentMode.Implicit` 只把模块的 `Description` / `Explanation` 送进她上下文，**不逐个列函数**；
   她之所以知道 `BiliDeps`/`BiliFeed`，是因为**唤醒提示词里点了名**。结果 20 多个函数（诊断、待审草稿、发动态、贴 cookie……）她根本不知道存在，主人也就无法经她调用（"待审回复""她自己的动态"两个功能等于没通路）。
   → 把完整**可用函数清单**写进 `Explanation`（那段一定会送到她），并注明"这些名字都真实存在，不要自己编"。

**踩过的坑（都已修）**：.NET 的 `Encoding.UTF8` 会写 **BOM**，而 Node 的 `JSON.parse` 不认 BOM ——
导致 `bili-whale.config.json` / `credentials.json` 解析失败并**静默退回内置默认值**（表现是：作息配置怎么改都不生效）。
现在统一用不带 BOM 的 UTF-8，并加了启动自检把"实际读到的作息"打进日志。

**操作注意（血的教训）**：`activate_character` 要 35–55 秒才真正完成，**期间不要发 `chat_with_character`** ——
否则请求撞进正在激活的角色，会把 Alife 的 Web/MCP 宿主带崩（应用日志表现为 `Microsoft.AspNetCore.Components.Web.ErrorBoundary`，18765 端口消失，只能重启 Alife）。
改配置的标准顺序：`set_module_config` → **等 ≥3 秒** → `reload_plugin` → `reload_character_config` → `deactivate_character` → 等 3 秒 → `activate_character` → **轮询到能读到角色上下文，再说话**。

## 6. 已知限制 / 后续可选

- **收藏**：`fav.mjs` 的路径 bug 已修（§2 双写），但日常流程仍未把它排进去（按当初约定算可选功能；她自己想收时可用 `BiliFav`）。
- **未纳入本次范围**：私信监听（默认关，可开）、直播间弹幕（`danmaku`/`await`，上游还缺 `auto` 命令文件）。
- **分享闸门是"一次性"的**：主人点头发出第一条 `auto="false"` 的分享后永久放开，之后她自主发（每日 ≤`ShareDaily`、同视频 7 天一次由上游 quota 强制）。想重新收紧就删 `<StateRoot>/share-gate.json`。
- **上游 3 条命令缺失**（见 §2），都无关本范围。
- **凭据会过期**：写操作前先跑 `BiliSession`；过期后所有写命令会明确报错而不是静默失败。
- **Alife 必须开着**：生物钟/守夜都靠进程退出叫醒，Alife 关了就不会醒（上游 DSH 同理）。
- **配置改动后旧计划会被沿用**：`whale-night.json` 里当天的 `wake`/`skip` 计划是**钉住**的；改了作息窗口要删掉这个文件（或跑 `BiliNightPlan`）才会重排。

## 7. 发布到插件市场（2026-10-20）

- **源码仓库**：<https://github.com/Six-King/Alife-Plugins> —— 两个插件同一个仓库，各占一个目录（`DaFeiYu.BiliWhale/`、`DaFeiYu.MemePack/`）。
- **发行包**：Release `v4.6.1`，资产名 `<插件id>_4.6.1.zip`。zip 内**文件放在根目录**（不套文件夹），解压即落到 `Storage\Plugins\<插件id>\`；`VERSION.txt` 由客户端自己写，包里不放。
- **市场条目**：`DaFeiYu.BiliWhale.json` / `DaFeiYu.MemePack.json`，PR `BDFFZI/Alife.PluginMarket#394` 已由市场自带的 automerge 工作流（新文件免所有权检查）自动 squash 合并。
- **署名与许可**：`author` = `鱼小晚的垃圾堆`；两个插件自有部分 MIT。市场描述与两个 `README.md` 都写明**作者不维护、请把插件目录交给 AI agent 改**。
- **实测验证**：合并后从市场 `install_plugins` 装回来 —— 61 个文件与本地实测版逐文件 SHA256 **零差异**，`QChat`/`FunctionCaller` 依赖未被改动，装完插件立即正常运行。
- **发布前的三处必改（下次发版别忘）**：
  1. `manifest.json` 的 `Version` **首位必须与客户端一致**（客户端 4.6.1 → 写 `4.6.1`；官方清单文档明说「主版本号低于客户端：插件可能不兼容」）；
  2. **隐私脱敏** —— 本文件里曾出现小号的昵称与 mid（已抹），发布前务必再扫一遍（cookie / 账号 mid / QQ / 本机路径）；
  3. 两个插件都要**自带 LICENSE 与 README**（含不维护声明）；内置上游工具链的必须保留 `upstream-LICENSE` 与 `upstream-NOTICE.md`。
- **发新版本流程**：改 `manifest.json` 版本 → 重打 zip（文件在根）→ 新建 tag + Release 并上传资产 → 在市场 JSON 的 `releases` 里加 `"x.y.z"`（`file` 指向新资产）→ 向市场仓库提 PR（同一账号的后续 PR 也能过所有权检查）。

## 8. 日记的落地契约（2026-10-20 改：纯文本 txt）

- 两本日记是**纯文本 txt**，默认落在 `<运行时目录>\RJ`（默认即此位置）：
  `养鱼日记.txt`（给主人的信）、`养鱼日记-私密.txt`（她自己那本，不加滤镜）。
  三个配置项「日记目录 / 公开日记文件名 / 私密日记文件名」都在设置页，**热开关即时生效**；「日记目录」留空 = 运行时目录下的 `RJ`（别写死绝对路径，别人装的 Alife 不在 K 盘）。
- **写日记一律走 `<BiliDiaryWrite to="owner|me" text="…"/>`**：由插件追加 + 盖时间戳（`—— 10月20日 23:44 ——`）。
  ⚠️ 不要让模型"读全文再写回"，也**不要改用文件工具追加** —— `FileService` 只有 `<Write>`（覆盖）和 `<Edit>`（替换），**没有 append**；日记越长，重写的代价越大、越容易截断。
- 格式约定：纯文本，不要 markdown（不要 `#` 标题、`**` 加粗、表格），段落之间空一行。
- 上游 `diary.mjs` 的职责只有两件：写事件流 `whale-log.jsonl`、`show` 读日记。为读新文件，插件给子进程传 `WHALE_DIARY_FILE` —— 这是对上游的**第 3 处**改动（另 2 处是显示文本）。
- `.md` 时代的两本留在 `PluginData\<id>\home\.dsh\` 作留档；搬家由 `Prepare()` 执行一次，标记文件 `PluginData\<id>\diary-migrated.marker`（改了日记目录不会重搬）。

## 9. 2026-10-22 补丁批次（换上游包时的重打清单）

> 本批共 7 处改动（3 个已知问题 + 4 项主人追加）。行号是**改完后**的行号，仅供定位。

### 9.1 `tools/commands/feed.mjs`（改的是上游工具链 ★ 换包必重打）

**根因**：`已看过/已评论过` 恒为 0 个 BV → 选题池回声。
v0.4.5 只从两处收 BV：① 日记里出现的 BV ② `<HOME>/.bili-ai/commented-videos.md`。
② **全仓库没有任何写者**（只有 feed.mjs 自己读它，文件从来不存在）；① 又被"日记搬家"
（`.md` → 纯文本 txt / `WHALE_DIARY_FILE`）绕过去了 → `seen` 恒空。

**改法**：改读真账本（都只有"真发出/真做过"才写）。三个坑按上游 2.0.1 的经验避：
① 日期带时间 → 逐条/逐行解析，坏一条只丢那一条；② 不假设 BV 在第几列 → 对 `target`/`bv` 串整体做 BV 正则；
③ "已发出"不看状态文字 → 只认账本里已落地的记录。

| 行 | 内容 |
|---|---|
| L4–5 | 头部注释：数据来源改成「两本日记 + quota.json 的 log[] + whale-log.jsonl」 |
| L28 / L30 | 日记路径认 `WHALE_DIARY_FILE`（与 diary.mjs 同序）；**新增**私密日记 `WHALE_DIARY_PRIVATE_FILE` |
| L33–95 | 新增 `quotaFile` / `eventLog`；`harvest`/`harvestFields` + 四路来源（日记 / quota 账本 / 事件流 / 老审计表兼容）合进 `seen` |
| L253 | 计数行附来源明细：`（quota 账本 N · 事件流 N · 日记 N）` |

### 9.2 `BiliWhaleRunner.cs`（插件侧，换包不受影响）

| 行 | 内容 |
|---|---|
| L502 | `BuildPsi` 新增 `psi.Environment["WHALE_DIARY_PRIVATE_FILE"] = PrivateDiaryFile`（配合 9.1；上游无此变量，JS 侧是可选读，不传也不报错） |

### 9.3 `BiliWhaleClock.cs`

| 行 | 内容 |
|---|---|
| L41 | 新字段 `_dmPokedReceipt`（同一条 pending 只叫她一次，ack 失败时只重试确认） |
| L140 / L181–230 | 「生物钟自检」从原始 JSON → 一行人话：新增 `DescribeNightStatus()`（解析失败**退回**原始输出头两行）。查过 `whale-night.mjs`/`night-plan.mjs`/`cli-args.mjs`：`night status` 只有 JSON 输出，**没有**人类可读开关，所以只能在这一侧压平。⚠️ 目标框架的 `JsonElement` **没有** `TryGetValue<T>`（编译报 CS1061）→ 用 `TryGetInt32/TryGetDouble` |
| L361 | 私信轮询 `dm-await --once --json` → **`--once --json --peek`**（v0.4.5 交付回执） |
| L378–408 | 拿到 `receipt` 后：**只有成功 Poke 之后才** ack；Poke 抛异常 → 记日志 + **不 ack**（下次重投，不丢消息）。同一 receipt 重复出现时只重试确认、不重复叫她 |
| L431–448 | 新增 `AckDmAsync(receipt)`：`dm-await --ack <receipt>`，只在输出含「私信交付已确认」时清 `_dmPokedReceipt` |
| L392–397 | 私信叫醒词加硬规矩（⑤）：主人私信含链接/BV → 先 `<BiliDeepRead/>` 看完 → 回**具体看法**（点名到谁做的/哪一秒/哪句台词），别只说"收到"；看得上可 `<BiliFav/>`；回不了私信走 `<QChat>` |
| L603–613 | 普通唤醒词加 ⑥⑦ 双向私信许可：**主动找主人说话不限条数**；分寸（同一件事不重复说、没话说别硬找话、先 dry）；**不占**公开分享额度、**不用**报备；私下推视频给他看用 `bvid=… share="true"`（属"分享"，仍受评分线 + 同一视频 7 天一次 + 与评论**共用**每日分享额度）；并写明「公开 vs 私下」的界线 |

### 9.4 `BiliWhaleModule.cs`

| 行 | 内容 |
|---|---|
| L426–442 | **(b) 分享闸门升级为代码硬拦**：`share && !dry && auto && !ShareGateOpen()` → 拒绝执行并返回「先 dry 出原文 → `<BiliNotify>` 报备 → 收工等回话 → 主人点头后用 `auto="false"` 发」的明确指令。判据与 ALIFE-PORT §3 完全一致（**没改判据**） |
| L444–450 | **(a) 同内容 10 分钟硬闸（纯文本）**：`!dry` 时先过 `CommentDupReason` → 命中就拒绝并说明原因（口径与 `BiliWhaleImage.cs` 的带图闸一致） |
| L473–481 | 放行判据认上游原话「评论已发出」（原来只认带图路径的「✅ 已发布，评论 rpid=」，与 `comment.mjs` 实际输出不符 → 闸门永远打不开） |
| L484–540 | 新增 `CommentDupReason()` + `NormComment()`：账本用 `quota.json` 的 `log[]`（`comment.mjs` 只在**发成功之后**才 `quota record` → dry 预演不落账，天然不占闸门）。归一化 = 去掉开头 `@某人` + trim + 压缩空白（`comment.mjs` 记账就是 `message.replace(/\s+/g,' ').slice(0,40)`，分享型还会自动补 `@主人 ` 前缀）；账本只留 40 字 → 「相等」或「短的那侧是长的那侧的前缀（≥12 字）」都算同内容 |
| L116–117 | 常驻守则**新增两行**（⑤2 + ⑥3 + ⑦）：主人私信含 BV → 先深读再回具体看法；她也可以主动找他说话（**不限条数**）。⚠️ 只新增，没改今晚那三条（`PersonaLine` / 4.5 块反套话与点名 / 「先分清谁是谁再写」） |

**状态文件**：(a) 用 `home/.bili-ai/quota.json`（唯一真相，与带图闸同一本账，不另开状态文件）；
(b) 用 `<StateRoot>/share-gate.json`（既有，`BiliWhaleStatus` 里能看到闸门开没开）。

### 9.5 `<BiliDm>` 分享参数转发不全（2026-10-22 主人现场撞到，最高优先）

**症状**：她分享视频到私信 → 报「**分享需要 --reason（至少 8 字）**」；她确实给了 reason，是函数没转发 → 她改用 `BiliRun` 手拼参数**真的发了出去**（20:46:12Z 账本有 `type=dm share=True target=BV:BV1ALnw6YEGK` 一条）。

**真实 CLI 契约**（照 `tools/commands/dm.mjs` 复核，不是猜的）：
- L60-61 `validateShare(config(), opt)` 读 `opt.reason`（≥8 字）+ `opt.score`（0–20 且 ≥ 阈值）+ `opt.rank`（必须 1）→ **三个都要转发**；
- L52 / L106：分享型的 BV **既可从正文里抠，也可走 `--target`**（`SHARE_TARGET = "BV:" + targetBV`）；
- L205 必须给 `--dry` 或 `--go`；
- L208-216：SHARE 走 `gate()`/`record()`（`type:'dm'`，share=true）→ **与评论分享共用同一本每日分享额度**。

| 文件·行 | 改动 |
|---|---|
| `BiliWhaleModule.cs` L556-560 | `<BiliDm>` 的 Description：写明分享型必须 `share=true + bvid + reason≥8字 + score + rank=1`，并点明与评论**共用**每日分享额度、纯文字私信不占额度（修掉"文档和实现打架"） |
| `BiliWhaleModule.cs` L561-570 | 签名新增 `reason` / `score` / `rank` 三个参数 |
| `BiliWhaleModule.cs` L573-601 | 分享型**本地预检**：缺 bvid / reason<8 字 / score 越界 / rank≠1 → 直接 `interactor.Poke` 一段**中文**说明（含完整正确写法），**不调用 CLI**（她不用去读英文参数报错） |
| `BiliWhaleModule.cs` L604-611 | 真正转发：`--reason` / `--score` / `--rank` / `--target BV…`（以前只转了 `--share` + `--target`） |
| `BiliWhaleModule.cs` L62-64 / L75 | Explanation 里「分享 BiliComment 或 BiliDm」补上分享型参数要求与额度界线；写清单里 BiliDm 加注 `(分享型要给 reason/score/rank + bvid)` |
| `BiliWhaleClock.cs` L611-612、`BiliWhaleModule.cs` L117 | ⑦ 的私信分享示例补上 `reason/score/rank`（否则她照抄示例会被新预检拦下），并把界线写准：带 share 的私信**也算分享**，与评论共用额度 |

**`<BiliComment>` 核对结果**：它**本来就能原样转发** `--reason`（`args.Add("--reason"); args.Add(Clean(reason))`）与 `--score`/`--rank`
→ 所以漏参数的**只有 `<BiliDm>`** 一个函数（不是全局性问题）。

**`ShareDaily` 口径**：`dm.mjs:50` 注释里的「分享 3 条/天」是**过时注释**；实际键是配置 `quota.shareDaily`
（`BiliWhaleRunner.cs:368` 由插件 `ShareDaily` 写入；`quota.mjs:24` 读取，缺省才回落 3）。
本机现值 **`quota.shareDaily = 4`**，CLI 实测也打印「分享额度内（2/4）」✓ —— **当前真实上限 4 条/天（评论分享 + 私信分享合计）**。

### 9.6 分享闸门补齐另外两条写通道（2026-10-22 ⑨）

**缺口**：`share-gate.json` 闸门原先只挂在 `<BiliComment>` 上；`<BiliDm share="true">` **完全没过闸**
（现场：20:5x 她直接真发了一条分享型私信 `BV1MXKi6NEbR`，分享额度记到 3/4 ✗）。

**改动（闸门判据、语义、报备话术与 `<BiliComment>` 完全一致）**：

| 文件·行 | 内容 |
|---|---|
| `BiliWhaleModule.cs` L494-500 | 新增 `ShareSentOk()`：**统一**「分享真发出去了」的判据 —— 同时认 `评论已发出`（comment.mjs）／`✅ 已发布，评论 rpid=`（带图）／`✓ 已发送`（dm.mjs:217）。只认一句就会得到一道**永远打不开**的闸门 |
| `BiliWhaleModule.cs` L477-486 | `<BiliComment>` 的放行判据改用它（行为不变，只是收口） |
| `BiliWhaleModule.cs` L582 | `<BiliDm>` 新增 `auto` 参数（默认 true = 自主；主人当场授权用 false） |
| `BiliWhaleModule.cs` L618-636 | `<BiliDm>` **新增同一道分享闸门**：`share && !dry && auto && !ShareGateOpen()` → 拒绝 + 中文报备指令；`dry="true"` 永远不拦 |
| `BiliWhaleModule.cs` L646-652 | `<BiliDm>` 放行：主人授权（`auto="false"`）真发成功 → `OpenShareGate("私信分享 BV…")` |
| `BiliWhaleImage.cs` L33-34 | `<BiliCommentImage>` 的 Description 补上「@主人 且非回评 = 事实上的分享 → 同闸门」 |
| `BiliWhaleImage.cs` L108-126 | `<BiliCommentImage>` 补同一道闸门：`shareLike = LooksLikeShareToOwner(message, parent)`（**正文含 `@主人昵称` 且 parent 为空**）→ 命中即按分享拦 |
| `BiliWhaleImage.cs` L177 | 主人授权那条图评真发出 → `OpenShareGate(...)` |
| `BiliWhaleImage.cs` L195-206 | 新增 `LooksLikeShareToOwner()`（回评 `parent` 非空 → 直接 false，回评不受影响） |

**闸门覆盖清单**：`<BiliComment>` 显式 `share` 判据 ✓ ｜ `<BiliDm>` 显式 `share` 判据 ✓（本轮补）｜
`<BiliCommentImage>` 无 `share` 参数（构造上不是分享型、不占分享额度），用「@主人 + 非回评」的**等价判据**兜住 ✓（本轮补）。
其余写通道（动态 / 收藏 / 日记 / 回复他人评论）按设计**不受**分享闸门约束。

**⑨ 验证**（离线复算，未真发、未创建真实 `share-gate.json`）：
`gateOpen` 五例（不存在 / `opened:false` / `opened:true` / 坏 JSON / 字符串 `"true"`）→ 全部 fail-closed 正确（5/5）；
`ShareSentOk` 六例（dm 成功行 → true、评论成功行 → true、带图成功行 → true；**真实 dry 输出 → false**、发送失败 → false、额度被拒 → false）（6/6）。
**⑨ 追加：生产端到端实测通过**（2026-10-22 21:15:29，主人授权她自己走完整流程）——
日志 `分享闸门已放行：主人授权发出了第一条分享（私信分享 BV1Fr4y1F7P2）→ 以后她可自主分享`，
`share-gate.json` = `{"opened":true,"at":"2026-10-22T21:15:29+08:00","evidence":"私信分享 BV1Fr4y1F7P2"}`，
`quota.json` 新增 `type=dm share=true target=BV:BV1Fr4y1F7P2`，`dm-quota.json` count=9。
→ 说明 ①`<BiliDm>` 的闸门真的生效、②`ShareSentOk` 真的认出了 `dm.mjs` 的「✓ 已发送」文案（判据没写歪，否则就是"永远打不开"）、③授权后放行 + 永久放开都按设计工作 ✓

### 9.7 私信原生分享卡 / 封面卡：**已放弃（主人的决定，2026-10-22）**
- ⚠️ 封面卡（路线 c，评论里带封面截图）：**已整体撤销** —— 主人否掉（他要的是私信原生卡，不是评论封面图）；它曾真发出过一条 `rpid=（已抹）`，实现与参数已全删，**勿再实现**。
- 结论（过程记录：三次/五次实测表、bundle 判读、signWbi 说明、镜像 404 取证 —— 已按主人「把不需要的都删了」删掉）：
  ① `web_im/v1/web_im/send_msg` 的 `msg[msg_type]` 官方**只支持 1/2/5 = 文本 / 图片 / 撤回**（`5` = `EN_MSG_TYPE_DRAW_BACK`，消息列表里被显式跳过）→ **HTTP 发不了卡**（`msg_type=7/4` 的 5 种载荷全 `-400 请求错误` 是必然，与缺字段无关）。
  ② 客户端**发卡走 App gRPC**：`POST https://app.bilibili.com/bilibili.im.interface.v1.ImInterface/SendMsg`（`content-type: application/grpc`），`msg_type=7`，content = `{"id":"<aid 字符串>","title","headline":"","source":5,"thumb","author","author_id"}`。
  ③ gRPC 探针（2 次纯文本：只带 web cookie / 再加真实 buvid + App UA）**两次都返回 `grpc-status: 9` + `grpc-message: request was banned`**（不是 16 UNAUTHENTICATED，网关在鉴权前就 ban）→ **唯一前置 = App 的 `access_key`**；哪天拿到再实现（要点：`tools/commands/dm.mjs` 分享型改走 gRPC、`dry` 只打印帧不发、stdout 仍须含 `✓ 已发送` 以保闸门判据、发完回读 `fetch_session_msgs` 验 `msg_type=7`）。
- 一律保留、不要动（主人指定的清单）：`@主人` 图评按分享型记账 ✓ · `LooksLikeShareToOwner` 判据 ✓ · 分享闸门本体 + **已打开的** `share-gate.json`（**别删**，删了会把她走过的首次分享再拦一次）✓ · ④私信 `--peek/--ack` 回执 ✓ · ⑥主动私信（不限条数）✓ · ⑤主人分享→看完再回 ✓ · ①选题排除修复 ✓ · ②生物钟自检一行人话 ✓ · ③a 评论 10 分钟同内容闸门 ✓ · ③b/⑨ 覆盖 Comment/Dm/CommentImage 的分享闸门 ✓ · 所有配置值 ✓

