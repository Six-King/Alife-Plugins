# Alife 插件 · 鱼小晚的垃圾堆

两个给 [Alife](https://github.com/BDFFZI/Alife) 用的插件。

| 插件 | 是什么 |
|---|---|
| [DaFeiYu.BiliWhale](DaFeiYu.BiliWhale) | **B站养鱼** —— 让角色自己刷 B 站：挑视频、看懂（字幕 + 抽帧）、写两本日记（公开 / 私密），觉得值得才 `@` 你；平时零 token 守着评论区，有人回复才叫醒她 |
| [DaFeiYu.MemePack](DaFeiYu.MemePack) | **大肥鱼梗图库** —— 按情绪 / 场景在线检索 DeepSeek娘梗图，返回台词、来历与图片直链 |

## ⚠️ 不维护声明

**这两个插件都不会被维护。** 不接需求、不跟客户端版本、没有 issue 支持 —— 作者只是把自己机器上跑通的东西丢出来，仅此而已。

**但它们天生就是给 agent 改的**，维护成本可以完全丢给 AI：

- 源码就是若干 `.cs` + （B站养鱼另带一份）纯 Node 工具链，**没有编译步骤、没有 npm 依赖**；
- 哪里不对，就把**整个插件目录**交给你的 AI agent，说清现象和期望，它改完调一次 `reload_plugin` 就生效；
- B站养鱼的配置项改完**不需要重载**，约 3 秒自己生效（机制见该目录 `ALIFE-PORT.md` 的「配置热更新」一节）；
- 出报错时把「插件目录 + 出错日志（`Storage\PluginData\<插件id>\biliwhale.log`）」一起丢给 agent，比等人快得多。

## 安装

**推荐**：在 Alife 插件市场里搜「B站养鱼」/「大肥鱼梗图库」直接安装。

**手动**：到 [Releases](../../releases) 下载对应 zip，解压到 `Storage\Plugins\<插件id>\` ——
zip 里的文件**直接放进该目录**（`manifest.json` 与该目录同级），不要再套一层文件夹。

各插件的运行前提（Node.js、B 站 Cookie、QChat、ffmpeg 等）见各自目录的 `README.md`。

## 授权

两个插件的自有部分均为 **MIT**，见各自目录的 `LICENSE`。

`DaFeiYu.BiliWhale/tools/` 与同目录的 `upstream-*` 文件来自上游 **dsh-bili-whale**（MIT），原样内置，
其版权与免责声明见 `DaFeiYu.BiliWhale/upstream-LICENSE` 与 `upstream-NOTICE.md`（**请一并阅读**）。

用你自己的账号在 B 站产生的动作，后果自负；不要用于刷量、水军、骚扰或绕过风控。