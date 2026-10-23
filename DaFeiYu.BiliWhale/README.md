# B站养鱼 · Alife 插件

让角色**自己**刷 B 站：挑视频、看懂（字幕 + 抽帧）、写两本日记（公开 / 私密），觉得值得才 `@` 你；
平时零 token 守着评论区，有人回复才叫醒她。

C# 这一层是 Alife 适配层，实际干活的是内置的 Node 工具链（移植自上游 [`dsh-bili-whale`]，MIT，见 `upstream-*`）。

---

## ⚠️ 不维护声明

**这个插件不会被维护。** 不接需求、不跟客户端版本、没有 issue 支持 —— 作者只是把自己机器上跑通的东西丢出来。

**但它天生就是给 agent 改的**，维护成本可以完全丢给 AI：

- 源码就是 `BiliWhale*.cs`（**12 个文件**）+ 一份纯 Node 的 `tools/`，**没有编译步骤**；
  唯一可选的 npm 依赖是「扫码登录」用到的 `qrcode`（`cd tools && npm i` 即可，用 Cookie 登录则完全不需要）；
- 哪里不对，就把**整个插件目录**交给你的 AI agent，说清现象和期望，它改完调一次 `reload_plugin` 就生效；
- 配置项改完**不需要重载**：约 3 秒自己生效（机制见 `ALIFE-PORT.md` 的「配置热更新」一节）；
- 出报错时把「插件目录 + 出错日志（`PluginData\<插件id>\biliwhale.log`）」一起丢给 agent，比等人快得多。

---

## 本次更新（5.0.0）

- **版本号对齐客户端主版本**（客户端 5.0.0 → 插件 5.0.0），并用 5.0.0 的插件编译环境验证：0 error、依赖齐全。
- **依赖清单补齐**：新增 `Alife.Function.QChat`（QQ 通知那一路要用）与 `Alife.Function.AIModelUtility`
  （深读听声音那一路要用）—— 之前没声明，别人装的时候可能因为缺依赖编译不过。
- **源码从 4 个文件长到 12 个**（新增 `BiliWhaleApi` / `Deep` / `Image` / `Login` / `Ops` / `Prompts` / `Tools` / `UI`），
  Runner 与 Clock 也大幅扩充；工具链新增**扫码登录**（`qr-login`）、**自检**（`diagnose`/`selftest`）、
  **检查更新**（`check-update`）、评论区与私信的一批新命令，`whale-night` 的排程逻辑拆进 `lib/night-plan.mjs`。

## 与大肥鱼梗图库的联动

两个插件是**一对**：**大肥鱼梗图库**（`DaFeiYu.MemePack`）是图源，**B站养鱼**负责在 B站 上干活时把图用出去。

- **一步拿图**：`<MemeFetch keyword="委屈"/>` 会把梗图下载到本地并返回路径，直接填进评论/动态的配图参数就行。
- **裸编号也行**：`image="002"` 会去大肥鱼梗图库的缓存目录
  （`Storage\PluginData\DaFeiYu.MemePack\cache`）里找那张，省一次调用。
- **开关**：配置里「评论可以带图」（`BiliCommentImage`）开了才允许把本地图随评论发出去，关掉这个函数直接拒绝。
- **装法**：想用带图就两个都装（市场里搜「大肥鱼」）；只装 B站养鱼也能跑，只是没有梗图配图。

## 5.0 适配说明

- **版本号**：官方清单要求插件主版本不低于客户端（客户端 5.0.0 → 插件 5.0.0）。
- **编译验证**：在 5.0.0 客户端 + `Alife.Function.FunctionCaller` / `AIModelUtility` 5.0.0 环境下编译，0 error。
- **用到的 5.0 接口**：`XmlFunction` / `FunctionMode`、`XmlFunctionCaller.RegisterHandler(handler, DocumentMode, CancellationToken)`、
  `AlifePath` / `AlifeContext`、`IConfigurable<T>` 配置热生效（约 3 秒）。
- **存储不变**：数据都在 `Storage\PluginData\DaFeiYu.BiliWhale\`，升级插件不会丢日记、账本、闸门状态。
- **QQ 通知**：走提示词里的 `<QChat type="Private" targetid="…">`，需要官方 QChat 插件（已在依赖里声明）；
  不想用 QQ 就把通知方式改成「只写日志」。

---

## 需要什么

| 依赖 | 是否必须 | 说明 |
|---|---|---|
| **Node.js 18+** | **必须** | 工具链是纯 Node、零 npm 依赖。装了 DSH 会自动找到它自带的 node；否则放进 PATH，或在配置页手填路径 |
| **B站账号 Cookie** | **必须** | `SESSDATA` + `bili_jct`（`buvid3` 可选）。**强烈建议用小号** —— 它是以这个账号的身份去评论、发动态的 |
| **官方 QChat 插件** | 可选 | 通知默认走「QQ 私聊」；不用 QQ 就把通知方式改成「只写日志」 |
| **ffmpeg** | 可选 | 只影响「抽帧看画面」这一路，不装也能跑（字幕那一路不受影响） |

### 怎么拿 Cookie

1. 浏览器登录 B 站（**建议用小号**）；
2. F12 → Application → Cookies → `bilibili.com`；
3. 复制 `SESSDATA`、`bili_jct`（`SESSDATA` 是 HttpOnly，JS 读不到，只能手抄）；
4. 填进配置页保存 —— **保存即生效，不用重载**。

---

## 装上之后

1. 在角色的**模块列表**里勾选「B站养鱼」；
2. 打开它的配置页，至少填四项：Cookie、**你**的 mid/昵称（主人）、**她**的 mid/昵称（小号）、作息窗口；
3. 保存。剩下的（Node / ffmpeg / 通知方式 / 评分下限 / 作息窗口）都有默认值，可以以后再改。

## 它能做什么

- **生物钟** —— 在配置的作息窗口里随机挑时刻叫醒自己、决定今晚看不看（有「不看概率」）、看几条，到点自动收工；
- **看懂** —— 抓字幕、按需抽帧、打分并给理由；
- **两本日记** —— 一本公开、一本私密，各写各的；
- **分享** —— 达到评分下限才 `@` 主人（默认以评论形式）；
- **守夜（省钱关键）** —— `comment-await` 子进程挂着，有人回复才退出并叫醒她；没人理就零 token 静默；
- **还有** —— 待审回复（草稿箱）、她自己发动态、收藏夹、评论区摘要、私信监听（默认关）。

## 三条护栏

1. **分享闸门** —— 第一次分享**不会自己发**：她会先用 `dry=true` 把原文报备给你，你回「可以」之后她才发；那一条发出去，闸门永久放开。想重置就删掉 `PluginData\DaFeiYu.BiliWhale\share-gate.json`。
2. **一切写入先 dry** —— 评论/回复/动态这些写操作都先出预览。
3. **撞到风控就停** —— 内置额度与退避逻辑，碰到验证码/限流不会硬刚。

---

## 授权与归属

- 本插件自有部分（C# 代码、文档）：MIT，见 `LICENSE`。
- `tools/` 与 `upstream-*`：上游 **dsh-bili-whale** 原样内置（MIT），见 `upstream-LICENSE`、`upstream-NOTICE.md` —— **含第三方与免责声明，请一并阅读**。
- 用你自己账号在 B 站产生的动作，后果自负；不要用于刷量、水军、骚扰或绕风控。