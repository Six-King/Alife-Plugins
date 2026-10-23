// 诊断导出（⑰-7③）—— 把「环境 / 配置 / 状态文件 / 最近日志」打成一页**脱敏文本**
//
//   node bili.mjs diagnose                  → 直接打 stdout（不产生任何文件 ✓ 默认就这样）
//   node bili.mjs diagnose --out            → 写到系统临时目录的 .txt（用完请删）
//   node bili.mjs diagnose --out D:\x.txt   → 写到指定 .txt（**拒绝 .md** —— 主人定的规矩）
//   node bili.mjs diagnose --lines 200      → 日志窗口行数（默认 120）
//   node bili.mjs diagnose --json           → 末尾多打一行 JSON 摘要（给按钮/脚本用）
//
// 脱敏是**兜底式**的：先把真凭据值（credentials.json 里的 sessdata/bili_jct/buvid3/access_token）
// 读进内存，再对整篇报告做逐字替换 + 关键词正则替换 —— 报告里**不可能**出现
// SESSDATA / bili_jct / buvid3 / token / access_key / cookie 的真值 ✓（只在末尾报"替换了几处"）
//
// 只读：不写凭据、不动 share-gate.json / quota.json / dm-quota.json ✓
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { dshPath } from '../lib/paths.mjs'
import { NODE_EXE } from '../lib/nodebin.mjs'

const argFlag = (n) => process.argv.includes(n)
function argValue(n, fb = null) {
  const i = process.argv.indexOf(n)
  if (i < 0) return fb
  const v = process.argv[i + 1]
  return v === undefined || String(v).startsWith('--') ? true : String(v)
}

const HOME = process.env.USERPROFILE || process.env.HOME || '/'
const DSH = process.env.DSH_HOME || dshPath()
const STATE_ROOT = path.dirname(HOME)                    // 插件把 HOME 指到 <插件数据目录>\home
const CFG = process.env.BILI_WHALE_CONFIG || process.env.WHALE_CFG || path.join(DSH, 'bili-whale.config.json')
const CRED = path.join(HOME, '.bili-ai', 'credentials.json')
const BILI_AI = path.join(HOME, '.bili-ai')
const LOG = path.join(STATE_ROOT, 'biliwhale.log')
const PLUGIN_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..')

const L = []                                             // 报告行（最后统一过一遍脱敏）
const push = (s = '') => L.push(String(s))
const localStamp = () => { const d = new Date(); const p = (n) => String(n).padStart(2, '0'); return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}` }
const flat = (o, prefix = '') => {
  const out = []
  for (const [k, v] of Object.entries(o ?? {})) {
    const q = prefix ? `${prefix}.${k}` : k
    if (v && typeof v === 'object' && !Array.isArray(v)) { out.push(...flat(v, q)); continue }
    const s = Array.isArray(v) ? JSON.stringify(v) : String(v)
    out.push(`    ${q.padEnd(30)} = ${s.length > 120 ? s.slice(0, 120) + '…' : s}`)
  }
  return out
}

// ── 凭据真值（只在内存里，用来做兜底替换；从不打印）──
let SECRETS = []
try {
  const c = JSON.parse(fs.readFileSync(CRED, 'utf8'))
  SECRETS = ['sessdata', 'bili_jct', 'buvid3', 'buvid4', 'access_token', 'access_key', 'refresh_token']
    .map((k) => c?.[k]).filter((v) => typeof v === 'string' && v.length >= 6)
} catch { /* 没凭据就没得替换 */ }

// ── 报告 ──────────────────────────────────────────────
push('══════════ B站养鱼 · 诊断报告（脱敏文本）══════════')
push(`  生成时间：${new Date().toLocaleString('zh-CN')}（本机时区）`)
push('  说明：这是一份**脱敏**摘要 —— cookie / SESSDATA / bili_jct / buvid3 / token / access_key')
push('        的真值不会出现在里面（结尾会告诉你替换了几处）。看完请删掉本文件（如果用 --out 落过盘）。')

push('\n【1】环境')
const env = [
  ['node 可执行文件', NODE_EXE],
  ['node 版本', process.version],
  ['平台', `${process.platform} ${process.arch}`],
  ['插件目录', PLUGIN_DIR],
  ['HOME（被插件改过）', HOME],
  ['DSH_HOME', DSH],
  ['插件数据目录', STATE_ROOT],
  ['工具链配置', CFG],
  ['ffmpeg', process.env.FFMPEG || '（没设 → 只影响抽帧看画面）'],
  ['日记文件', process.env.WHALE_DIARY_FILE || '（没设）'],
]
for (const [k, v] of env) push(`    ${k.padEnd(22)} ${v}`)
try {
  const m = JSON.parse(fs.readFileSync(path.join(PLUGIN_DIR, 'manifest.json'), 'utf8'))
  push(`    ${'插件版本'.padEnd(22)} ${m.Version}（manifest.json）`)
} catch { push(`    ${'插件版本'.padEnd(22)} 读不到 manifest.json`) }

push('\n【2】配置（工具链读到的那份，本身就是非敏感项）')
try {
  const c = JSON.parse(fs.readFileSync(CFG, 'utf8').replace(/^\uFEFF/, ''))
  for (const line of flat(c)) push(line)
} catch (e) { push('    ✗ 读不到：' + String(e.message).slice(0, 80)) }

// 模块配置（角色级；里面有 cookie 字段 —— 只报"在不在 + 多长"，绝不报值）
push('\n【2b】模块配置里的敏感字段（只报存在性/长度）')
try {
  const storage = path.resolve(STATE_ROOT, '..', '..')             // ...\Storage
  const charDir = path.join(storage, 'Character')
  const found = []
  if (fs.existsSync(charDir)) {
    for (const ch of fs.readdirSync(charDir)) {
      const p = path.join(charDir, ch, 'Configuration', 'DaFeiYu.BiliWhale.BiliWhaleModule.json')
      if (fs.existsSync(p)) found.push(p)
    }
  }
  const g = path.join(storage, 'Configuration', 'DaFeiYu.BiliWhale.BiliWhaleModule.json')
  if (fs.existsSync(g)) found.push(g)
  if (!found.length) push('    （没找到模块配置文件）')
  for (const p of found) {
    try {
      const j = JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''))
      const sens = ['Sessdata', 'BiliJct', 'Buvid3', 'AccessToken', 'Cookie'].filter((k) => k in j)
      const shape = sens.map((k) => `${k}(len=${String(j[k] ?? '').length})`).join(' · ') || '没有敏感字段'
      push(`    ${path.basename(path.dirname(path.dirname(p)))}/${path.basename(p)}`)
      push(`        ${shape}`)
      const keep = ['SelfMid', 'SelfName', 'OwnerMid', 'OwnerName', 'ToolExposureMode']
      push(`        ${keep.filter((k) => k in j).map((k) => `${k}=${String(j[k]).slice(0, 40)}`).join(' · ')}`)
    } catch (e) { push(`    ${p} 读不动：` + String(e.message).slice(0, 60)) }
  }
} catch (e) { push('    ✗ ' + String(e.message).slice(0, 80)) }

push('\n【3】凭据（只看"有哪些键 + 多长"，不看值）')
try {
  const c = JSON.parse(fs.readFileSync(CRED, 'utf8'))
  push(`    文件：${CRED}`)
  push('    ' + Object.entries(c).map(([k, v]) => `${k}(len=${String(v ?? '').length})`).join(' · '))
} catch { push(`    ✗ 没有/读不到 ${CRED}`) }

push('\n【4】状态文件（全部只读看一眼，不动）')
const peekJson = (file, label, fn) => {
  try {
    const j = JSON.parse(fs.readFileSync(file, 'utf8'))
    push(`    ${label.padEnd(18)} ${fn(j)}`)
  } catch { push(`    ${label.padEnd(18)} （没有/读不到，正常）`) }
}
peekJson(path.join(STATE_ROOT, 'share-gate.json'), 'share-gate.json', (j) => `opened=${j.opened === true}${j.at ? ` · at=${String(j.at).slice(0, 19)}` : ''}${j.bvid ? ' · bvid=' + j.bvid : ''}`)
peekJson(path.join(BILI_AI, 'quota.json'), 'quota.json', (j) => {
  const log = Array.isArray(j.log) ? j.log : []
  const last = log.length ? String(log[log.length - 1]?.at ?? '').slice(0, 19) : ''
  return `当期 ${log.length} 条${last ? ` · 最近一条 ${last}` : ''}`
})
peekJson(path.join(BILI_AI, 'dm-quota.json'), 'dm-quota.json', (j) => `键 ${Object.keys(j).length} 个${Array.isArray(j.log) ? ` · 当期 ${j.log.length} 条` : ''}`)
peekJson(path.join(BILI_AI, 'uid-cache.json'), 'uid-cache.json', (j) => `缓存 ${Object.keys(j).length} 条昵称→mid`)
peekJson(path.join(BILI_AI, 'selftest-passed.json'), 'selftest-passed', (j) => `上次全绿 ${String(j.at ?? '').slice(0, 19)}`)
try {
  const arc = path.join(BILI_AI, 'Archive')
  const files = fs.existsSync(arc) ? fs.readdirSync(arc).filter((f) => f.endsWith('.json')) : []
  push(`    ${'账本归档 Archive/'.padEnd(18)} ${files.length ? files.join(' · ') : '（还没归档过）'}`)
} catch { /* ignore */ }
try {
  const envDiary = process.env.WHALE_DIARY_FILE || ''
  const legacy = [path.join(DSH, 'whale-diary.md'), path.join(DSH, 'whale-diary-private.md')]
    .filter((f) => fs.existsSync(f))
  const stat = (f) => `${fs.statSync(f).size} 字节 · 改于 ${fs.statSync(f).mtime.toLocaleString('zh-CN')}`
  if (envDiary && fs.existsSync(envDiary)) push(`    ${'日记（当前）'.padEnd(18)} ${stat(envDiary)}（${envDiary}）`)
  else if (envDiary) push(`    ${'日记（当前）'.padEnd(18)} 还没建：${envDiary}`)
  if (legacy.length) push(`    ${'日记（旧版）'.padEnd(18)} ${legacy.map((f) => `${path.basename(f)} ${stat(f)}`).join(' · ')}（4.6.2 起改 txt，旧文件留档）`)
  if (!envDiary && !legacy.length) push(`    ${'日记'.padEnd(18)} （env 没给 WHALE_DIARY_FILE，DSH 下也没有旧版文件）`)
} catch { /* ignore */ }
try {
  const dir = path.join(STATE_ROOT, 'Prompts')
  const mf = path.join(dir, '.manifest.json')
  if (fs.existsSync(mf)) {
    const man = JSON.parse(fs.readFileSync(mf, 'utf8'))
    const txts = fs.readdirSync(dir).filter((x) => x.endsWith('.txt')).length
    const news = fs.readdirSync(dir).filter((x) => x.endsWith('.txt.new')).length
    push(`    ${'提示词外置'.padEnd(18)} ${Object.keys(man).length} 个键 · ${txts} 个 txt · sha 账本 OK${news ? ` · ${news} 个 *.txt.new 待对比` : ''}`)
  } else push(`    ${'提示词外置'.padEnd(18)} （目录/账本还没生成）`)
} catch { /* ignore */ }

push('\n【5】最近日志（脱敏；窗口 = 最后 N 行 + 最近一次"运行环境就绪"至今）')
const linesRaw = argValue('--lines', 120)
const lines = linesRaw === true || !Number.isFinite(Number(linesRaw))
  ? 120 : Math.max(10, Math.min(2000, Number(linesRaw)))
try {
  const all = fs.readFileSync(LOG, 'utf8').replace(/\r\n/g, '\n').split('\n')
  const idx = all.map((l, i) => (l.includes('运行环境就绪') ? i : -1)).filter((i) => i >= 0).pop() ?? -1
  const block = idx >= 0 ? all.slice(idx) : []
  const tail = all.slice(-lines)
  const errIn = (arr) => arr.filter((l) => /错误|异常|失败|Exception|error:/.test(l)).length
  push(`    日志文件：${LOG}`)
  push(`    总行数 ${all.length} · 最近一次启动（运行环境就绪）在 ${idx >= 0 ? all[idx].slice(1, 20) : '（本次日志里没有）'}`)
  push(`    自那次启动至今 ${block.length} 行 · 其中"错误/异常/失败/Exception" ${errIn(block)} 行 ✓`)
  push(`    下面贴最后 ${tail.length} 行：`)
  for (const l of tail) if (l.trim()) push('    | ' + l)
} catch (e) { push('    ✗ 读不到日志：' + String(e.message).slice(0, 80)) }

// ── 脱敏兜底：① 逐个真凭据值整串替换 ② 关键词正则 ③ 长 hex 一律挡掉 ──
let hits = 0
const sub = (s, re, rep) => s.replace(re, (...a) => {
  hits += 1
  if (typeof rep === 'function') return rep(...a)
  // 函数式替换不会自动展开 $1 —— 手动来（要保留字段名用得上）
  return rep.replace(/\$(\d)/g, (_, d) => a[Number(d)] ?? '')
})
function redact(text) {
  let s = text
  for (const v of SECRETS) {
    const parts = s.split(v)
    if (parts.length > 1) { hits += parts.length - 1; s = parts.join('<已脱敏:凭据值>') }
  }
  s = sub(s, /(SESSDATA|sessdata)\s*[=:]\s*[^\s;&"',}]+/g, 'SESSDATA=<已脱敏>')
  s = sub(s, /(bili_jct|biliJct|csrf|csrf_token)\s*[=:]\s*[^\s;&"',}]+/gi, 'bili_jct=<已脱敏>')
  s = sub(s, /(buvid3|buvid4)\s*[=:]\s*[^\s;&"',}]+/gi, 'buvid3=<已脱敏>')
  s = sub(s, /(access_token|access_key|refresh_token|authorization|bearer)\s*[=:]?\s*[^\s;&"',}]{6,}/gi, '<已脱敏:token>')
  s = sub(s, /"(sessdata|bili_jct|buvid3|access_token|access_key|refresh_token|token|cookie)"\s*:\s*"[^"]*"/gi, '"$1":"<已脱敏>"')
  s = sub(s, /\b[0-9a-fA-F]{32,}\b/g, '<已脱敏:hex>')          // 32 位十六进制 = bili_jct 那种；sha256 一起挡掉，安全优先
  return s
}
const report = L.map(redact).join('\n') + '\n'

console.log(report)
console.log(`────────── 脱敏兜底：命中并替换 ${hits} 处（真凭据值 + 关键词 + 长 hex）· 报告里没有 cookie 真值 ✓`)

// 可选落盘（默认不落盘；拒绝 .md）
const outArg = argValue('--out', null)
if (outArg) {
  try {
    let target = typeof outArg === 'string' ? path.resolve(outArg) : path.join(os.tmpdir(), `bili-whale-diagnose-${localStamp()}.txt`)
    if (/\.md$/i.test(target)) {
      console.log(`✗ 拒绝写 .md（${target}）—— 主人定过规矩：诊断只出 .txt，不新建 .md`)
      process.exit(1)
    }
    if (!/\.txt$/i.test(target)) target += '.txt'
    fs.mkdirSync(path.dirname(target), { recursive: true })
    fs.writeFileSync(target, report, 'utf8')
    console.log(`已写入：${target}（${Buffer.byteLength(report, 'utf8')} 字节）`)
    console.log('⚠️ 这是临时文本，看完请删掉它（命令不会替你留）。')
  } catch (e) {
    console.log('✗ 落盘失败：' + String(e?.message ?? e).slice(0, 100))
    process.exit(1)
  }
}
if (argFlag('--json')) console.log(JSON.stringify({ ok: true, state: 'ok', redacted: hits, bytes: Buffer.byteLength(report, 'utf8'), out: outArg ? String(outArg) : '' }))