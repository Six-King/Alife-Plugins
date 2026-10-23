// 选题器：照"她自己的偏好"挑今天该看什么。**只读**，不产生任何对外动作。
//
// 用法: node feed.mjs [--n 6] [--per 2] [--pool 30]
// 数据来源：两本日记（她写下的 BV + 「爱看」偏好关键词）+ 真账本 quota.json 的 log[]
//          + whale-log.jsonl 事件流（已看过/已评论过的视频全部排除；见下方「已看过/已评论过」注释块）
//
// 为什么这么做（2026-10-14 主人定的方向）：主人要的是"有日子的鱼"，
// 所以"看什么"不该由别人喂，而该由她自己的偏好决定 —— 偏好写在日记里，这个脚本只负责照它去捞。
import fs from 'node:fs'
import path from 'node:path'
import { config } from '../lib/config.mjs'
import crypto from 'node:crypto'
import { dshPath } from '../lib/paths.mjs'


const argv = process.argv.slice(2)
const opt = {}
for (let i = 0; i < argv.length; i += 1) {
  if (argv[i].startsWith('--')) opt[argv[i].slice(2)] = argv[i + 1]?.startsWith('--') ? true : argv[++i] ?? true
}
const N = Number(opt.n ?? 6)
const PER = Number(opt.per ?? 2)
const POOL = Number(opt.pool ?? 30)

const HOME = (process.env.USERPROFILE || process.env.HOME || '/')
// 日记（给主人的那本）：Alife 移植把日记搬成纯文本 txt，并给子进程传 WHALE_DIARY_FILE
// （diary.mjs 就是这个顺序）→ 这里跟它对齐，别再只认老 .md 路径 ✗
const diary = process.env.WHALE_DIARY_FILE || dshPath('whale-diary.md')
// 她自己的那本（不加滤镜）：里面写着"这条看了、几分" —— "已看过"最直接的证据
const privateDiary = process.env.WHALE_DIARY_PRIVATE_FILE || dshPath('whale-diary-private.md')
// 2026-10-18：审计表挪到 `~/.bili-ai/` ✓（原来写死作者机器路径 ✗ 换机器就写不进去，静默失效 ✗）
const audit = path.join(HOME, '.bili-ai', 'commented-videos.md')
// ★ 真账本（2026-10-22 修「选题池回声」，见下）
const quotaFile = path.join(HOME, '.bili-ai', 'quota.json')
const eventLog = dshPath('whale-log.jsonl')

// ── 偏好 ──
let prefs = [...(config().preferences?.like ?? [])]   // 偏好来自统一配置（首选）
let dislike = [...(config().preferences?.dislike ?? [])]

// ── 已看过 / 已评论过（选题去重的唯一依据）──────────────────────────────────
// 2026-10-22 根因（她自己报过「选题池还是回声，六条里五条看过的」）：
//   v0.4.5 只从两处收 BV：① 日记里出现的 BV  ② `<HOME>/.bili-ai/commented-videos.md`。
//   ② **全仓库没有任何写者**（只有这里读它），文件从来不存在 → 那一路恒为空；
//   ① 又被"日记搬家"（.md → 纯文本 txt / WHALE_DIARY_FILE）绕过去了。
//   两处一起失效 → seen 恒空 → 看过的视频又被推荐回来 ✗
// 现在改成读**真账本**（都只有"真发出/真做过"才写进去）：
//   · quota.json 的 log[]：comment / fav / dm 在**成功之后**才 record；每条有 at / type / target
//   · whale-log.jsonl：她自己的事件流（fav / share / reply）—— 追加写，最容易被写坏一半
//   · 两本日记：她写下的 BV
// 上游 2.0.1 踩过的三个坑，这里都按它避：
//   ① 日期可能带时间 → 逐条/逐行解析，**一条坏只丢那一条**，绝不整份放弃
//   ② 不假设 BV 号在第几列 → 只对 target/bv 串整体做 BV 正则（不 split、不取下标）
//   ③ 「已发出」不看状态文字 → 只认账本里已经落地的记录
const BV_RE = /\bBV[0-9A-Za-z]{10}\b/g
const harvest = (text, out) => { for (const m of String(text ?? '').matchAll(BV_RE)) out.add(m[0]) }
const harvestFields = (obj, out) => { harvest(obj?.target, out); harvest(obj?.bv, out) }

const seenDiary = new Set()
for (const f of [...new Set([diary, privateDiary].filter(Boolean))]) {
  try {
    const text = fs.readFileSync(f, 'utf8')
    harvest(text, seenDiary)
    const m = text.match(/^-\s*\*\*爱看\*\*：(.+)$/m)
    // 兼容旧写法：日记里若仍写着「爱看」，只在配置没给偏好时兜底（日记应回归纯叙事）
    if (m && !prefs.length) prefs = m[1].split(/[·、,，]/).map((s) => s.trim()).filter(Boolean)
  } catch { /* 这本没有就算了 */ }
}

// ② quota.json：优先逐条读 target/bv；整份解不开就退回全文正则 —— 不能静默失效
const seenQuota = new Set()
try {
  const raw = fs.readFileSync(quotaFile, 'utf8')
  let parsed = false
  try {
    const q = JSON.parse(raw)
    if (Array.isArray(q?.log)) { for (const e of q.log) harvestFields(e, seenQuota); parsed = true }
  } catch { /* 账本半截/坏了 */ }
  if (!parsed) harvest(raw, seenQuota)
} catch { /* 还没有账本 */ }

// ③ whale-log.jsonl：逐行解析，坏行只丢那一行（这个文件是追加写的，最容易留半行）
const seenLog = new Set()
try {
  for (const line of fs.readFileSync(eventLog, 'utf8').split(/\r?\n/)) {
    if (!line.trim()) continue
    try { harvestFields(JSON.parse(line), seenLog) } catch { harvest(line, seenLog) }
  }
} catch { /* 还没有事件流 */ }

// ④ 上游原设计的审计表：将来若真有写者补上它，照旧生效（保留兼容，不再当唯一来源）
const seenAudit = new Set()
try { harvest(fs.readFileSync(audit, 'utf8'), seenAudit) } catch { /* 没有也无妨 */ }

const seen = new Set([...seenDiary, ...seenQuota, ...seenLog, ...seenAudit])
if (seenDiary.size === 0 && seenQuota.size === 0 && seenLog.size === 0) console.log('（读不到日记与账本，用默认偏好）')

const cred = JSON.parse(fs.readFileSync(path.join(HOME, '.bili-ai', 'credentials.json'), 'utf8'))
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36'
const H = { 'user-agent': UA, referer: 'https://search.bilibili.com/', cookie: `SESSDATA=${cred.sessdata}; bili_jct=${cred.bili_jct}; buvid3=${cred.buvid3}` }
const MIXIN_TAB = [46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13,37,48,7,16,24,55,40,61,26,17,0,1,60,51,30,4,22,25,54,21,56,59,6,63,57,62,11,36,20,34,44,52]
const keyOf = (u) => u.slice(u.lastIndexOf('/') + 1, u.lastIndexOf('.'))
const nav = await (await fetch('https://api.bilibili.com/x/web-interface/nav', { headers: H })).json()
const mixin = MIXIN_TAB.map((i) => (keyOf(nav.data?.wbi_img?.img_url ?? '') + keyOf(nav.data?.wbi_img?.sub_url ?? ''))[i]).join('').slice(0, 32)
const wbi = (p) => {
  const wts = Math.round(Date.now() / 1000)
  const q = Object.keys({ ...p, wts }).sort().map((k) => `${encodeURIComponent(k)}=${encodeURIComponent(String(k === 'wts' ? wts : p[k]).replace(/[!'()*]/g, ''))}`).join('&')
  return `${q}&w_rid=${crypto.createHash('md5').update(q + mixin).digest('hex')}`
}
const secs = (s) => { const p = String(s).split(':').map(Number); return p.length === 2 ? p[0] * 60 + p[1] : p[0] * 3600 + p[1] * 60 + p[2] }

const pool = []
for (const kw of prefs.slice(0, 6)) {
  for (const order of ['click', 'pubdate']) {
    try {
      const j = await (await fetch(`https://api.bilibili.com/x/web-interface/wbi/search/type?${wbi({ search_type: 'video', keyword: kw, order, page: 1 })}`, { headers: H })).json()
      for (const it of (j?.data?.result ?? []).slice(0, POOL / prefs.length + 4)) {
        pool.push({
          bv: it.bvid, t: String(it.title).replace(/<[^>]+>/g, ''), d: secs(it.duration),
          dm: Number(it.danmaku ?? 0), play: Number(it.play ?? 0), up: it.author, kw,
        })
      }
    } catch { /* 这个词没结果就跳过 */ }
    await new Promise((r) => setTimeout(r, 300))
  }
}

// ── #3 更多来源（2026-10-18 加，Kimi 评审指出：只搜关键词 = 只刷"搜索"，不是刷 B 站）
//   --source search（默认）| popular（热门）| rank（排行榜）| all（三个都要）
const SOURCE_HOT = String((process.argv.find((a) => a.startsWith('--source=')) ?? '').split('=')[1] ?? '')
  || (process.argv.includes('--source') ? String(process.argv[process.argv.indexOf('--source') + 1] ?? 'all') : 'all')
async function fetchPopular() {
  const j = await (await fetch('https://api.bilibili.com/x/web-interface/popular?pn=1&ps=20', { headers: H })).json()
  for (const it of (j?.data?.list ?? [])) {
    pool.push({ bv: it.bvid, t: String(it.title ?? '').replace(/<[^>]+>/g, ''), d: Number(it.duration ?? 0),
      dm: Number(it.stat?.danmaku ?? 0), play: Number(it.stat?.view ?? 0), up: it.owner?.name ?? '', kw: '热门' })
  }
}
async function fetchRank() {
  const j = await (await fetch('https://api.bilibili.com/x/web-interface/ranking/v2?rid=0&type=all', { headers: H })).json()
  for (const it of (j?.data?.list ?? []).slice(0, 20)) {
    pool.push({ bv: it.bvid, t: String(it.title ?? '').replace(/<[^>]+>/g, ''), d: Number(it.duration ?? 0),
      dm: Number(it.stat?.danmaku ?? 0), play: Number(it.stat?.view ?? 0), up: it.owner?.name ?? '', kw: '排行' })
  }
}
// 关注的人发了什么（2026-10-18 加）：真人才会"刷关注"——只搜关键词等于只刷"搜索"，不像人
async function fetchFollowing() {
  const j = await (await fetch('https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/all?type=video&page=1', { headers: H })).json()
  if (j?.code !== 0) throw new Error(`code=${j?.code} ${j?.message ?? ''}`)
  for (const it of (j?.data?.items ?? [])) {
    const a = it?.modules?.module_author
    const ar = it?.modules?.module_dynamic?.major?.archive
    if (!ar?.bvid) continue
    const dt = String(ar.duration_text ?? '')          // 形如 "05:23"
    const p = dt.split(':').map(Number)
    const secs = p.length === 2 ? p[0] * 60 + p[1] : p.length === 3 ? p[0] * 3600 + p[1] * 60 + p[2] : 0
    pool.push({ bv: ar.bvid, t: String(ar.title ?? '').replace(/<[^>]+>/g, ''), d: secs,
      dm: 0, play: Number(ar.stat?.play ?? 0), up: a?.name ?? '', kw: '关注' })
  }
}
try { if (SOURCE_HOT === 'popular' || SOURCE_HOT === 'all') await fetchPopular() } catch { console.error('（热门源取不到，跳过）') }
try { if (SOURCE_HOT === 'rank' || SOURCE_HOT === 'all') await fetchRank() } catch { console.error('（排行源取不到，跳过）') }
try { if (SOURCE_HOT === 'following' || SOURCE_HOT === 'all') await fetchFollowing() } catch (e) { console.error(`（关注源取不到，跳过：${String(e.message).slice(0, 60)}）`) }
console.error(`[来源] ${SOURCE_HOT === 'all' ? '搜索 + 热门 + 排行 + 关注' : SOURCE_HOT === 'popular' ? '热门' : SOURCE_HOT === 'rank' ? '排行' : SOURCE_HOT === 'following' ? '关注' : '搜索'}`)

// ── 反馈回流（2026-10-20 加，主人定：B 也做）────────────────────────────
//   feedback.json 里 kind='like' 的事件带 videoTitle → 对被赞视频【标题相似】的候选加权
//   相似度用字符二元组 Jaccard（中文不用分词也能用 ✓）
const FEEDBACK = path.join(HOME, '.bili-ai', 'feedback.json')
let likedEvents = []
try {
  const fb = JSON.parse(fs.readFileSync(FEEDBACK, 'utf8'))
  likedEvents = (fb.events ?? [])
    .filter((e) => e && (e.kind === 'like' || (typeof e.note === 'string' && /BV[0-9A-Za-z]{10}/.test(e.note))))
    .filter((e) => String(e.videoTitle ?? '').trim())   // 没有标题的旧事件学不到东西，跳过 ✓
} catch { /* 没有反馈就零加权 */ }
const favBVs = new Set()
try {
  const fj = JSON.parse(fs.readFileSync(path.join(HOME, '.bili-ai', 'favorites.json'), 'utf8'))
  for (const x of (fj.items ?? [])) if (x?.bv) favBVs.add(x.bv)
} catch {}
const likedBVs = new Set()
try {
  const fb = JSON.parse(fs.readFileSync(FEEDBACK, 'utf8'))
  for (const e of (fb.events ?? [])) if (e?.bv) likedBVs.add(e.bv)
} catch {}
  // 抽主题词：拉丁词（≥2）+ 中文 3-4 字 n-gram（更具体，避免"我为/添加"这类噪音）
  // 为什么不比标题相似度（Jaccard）：实测"几乎改一个字"的两条标题相似度只有 0.036 ✗
  //   主题词方案能学到"dsh""大肥鱼"这种真正跨标题复用的信号 ✓
  const STOP_WORDS = new Set(['我为', '添加', '开屏', '广告', '这个', '那个', '什么', '自己', '可以', '就是', '一个'])
  const ownerName = String(config().owner?.name ?? '')
  const learnTokens = (text) => {
    const out = new Set()
    // ⚠️ ownerName 为空时 new RegExp('') 会在每个字之间插空格 → 必须先判断 ✗（实测踩过）
    const base = ownerName ? String(text).replace(new RegExp(ownerName, 'g'), ' ') : String(text)
    const t = base.replace(/@\S+/g, ' ')
    for (const m of t.matchAll(/[A-Za-z][A-Za-z0-9]{1,}/g)) if (m[0].length >= 2) out.add(m[0].toLowerCase())
    for (const m of t.matchAll(/[\u4e00-\u9fa5]{2,}/g)) {
      const run = m[0]
      for (const n of [3, 4]) for (let i = 0; i + n <= run.length; i += 1) {
        const g = run.slice(i, i + n)
        if (!STOP_WORDS.has(g)) out.add(g)
      }
    }
    return [...out]
  }
  // 她自己收藏过的（favorites.json）也算"品味信号" ✓ 和"被赞过的"并列
  let favItems = []
  try {
    const fj = JSON.parse(fs.readFileSync(path.join(HOME, '.bili-ai', 'favorites.json'), 'utf8'))
    favItems = (fj.items ?? []).filter((x) => x && x.bv)
  } catch {}
  const learnedTokens = [...new Set([
    ...likedEvents.flatMap((e) => learnTokens(String(e.videoTitle ?? '') + ' ' + String(e.comment ?? ''))),
    ...favItems.flatMap((x) => learnTokens(String(x.title ?? '') + ' ' + String(x.reason ?? ''))),
  ])]
  const learnedHitsOf = (title) => learnedTokens.filter((tok) => title.toLowerCase().includes(tok)).length
  // 每条命中 +2.5，最多 +7.5（不压过偏好命中的主权重 ✓）
  const boostOf = (title) => Math.min(7.5, learnedHitsOf(title) * 2.5)

// ── 偏好命中与排序（2026-10-20 重写，主人定：不要对高播放量有偏向 ✗）────────────
//   旧：弹幕密度 + log10(播放)×3  → 大 UP 的爆款永远霸榜 ✗
//   新：log10(1+弹幕密度)×3 + 偏好命中×4 + 反馈学习×2.5（封顶 7.5）→ 播放量彻底移出 ✓
//   为什么密度要取 log：不取时"92 秒 15027 弹幕"那条得 163 分，偏好加满也压不过 ✗（实测）
const prefTokens = prefs.map((p) => ({
  label: p,
  tokens: [...new Set([p, ...String(p).split(/[\s·,，、/]+/).filter((t) => t.length >= 2)])],
}))
const hitsOf = (title) => prefTokens.filter((p) => p.tokens.some((tok) => title.includes(tok))).map((p) => p.label)
const densityOf = (x) => Math.log10(1 + x.dm / Math.max(30, x.d))
// 2026-10-20：排除她自己的视频（实测刷到过自己的《大肥鱼成精了》✗ 别让自己看自己）
const selfName = String(config().self?.name ?? "")
const uniq = [...new Map(pool.map((x) => [x.bv, x])).values()]
  .filter((x) => !seen.has(x.bv) && !likedBVs.has(x.bv) && !favBVs.has(x.bv))
  .filter((x) => !(selfName && String(x.up ?? "").includes(selfName)))
const scoreOf = (x) => densityOf(x) * 3 + hitsOf(x.t).length * 4 + boostOf(x.t)
uniq.sort((a, b) => scoreOf(b) - scoreOf(a))
for (const x of uniq) { x._hits = hitsOf(x.t); x._score = scoreOf(x) }

// 多样性：同一"命中和类"最多 PER 条；完全没命中的最多 1 条（防凑数 ✗）
const kindUsed = {}
const diversified = []
for (const x of uniq) {
  const key = x._hits.length ? x._hits.join('+') : '__none__'
  kindUsed[key] = kindUsed[key] ?? 0
  if (kindUsed[key] >= (x._hits.length ? PER : 1)) continue
  kindUsed[key] += 1
  diversified.push(x)
}

console.log(`偏好（来自日记）: ${prefs.join(' · ')}`)
console.log(`反馈回流: 被赞 ${likedEvents.length} 条 · 她收藏 ${favItems.length} 条 → 学会 ${learnedTokens.length} 个主题词`)
console.log(`已看过/已评论过（已排除）: ${seen.size} 个 BV（quota 账本 ${seenQuota.size} · 事件流 ${seenLog.size} · 日记 ${seenDiary.size}）`)
console.log(`候选池: ${pool.length} 条 → 去重去已看后 ${uniq.length} 条\n`)
console.log(`今天可以看这几条（每词最多 ${PER} 条，共 ${N} 条）：`)
const perKw = {}
let shown = 0
for (const x of diversified) {
  if (shown >= N) break
  perKw[x.kw] = perKw[x.kw] ?? 0
  // 多样性已在上一步做过 → 这里不再二次限流 ✓
  perKw[x.kw] += 1; shown += 1
  console.log(`  ${String(shown).padStart(2)}. ${x.bv}  ${String(x.d).padStart(4)}s  弹幕${String(x.dm).padStart(7)}  播放${String(x.play).padStart(9)}  ${x.t.slice(0, 40)}  @${x.up} ${x._hits.length ? "[" + x._hits.join("+") + "]" : ""}`)
}
console.log('\n看完跑：<BiliDebrief bvid="<BV>"/>   → 然后把"想法"写进日记的「想说」区（不发）')
