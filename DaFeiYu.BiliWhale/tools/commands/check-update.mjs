// 检查更新（⑰-7①）—— 只读比对，**绝不覆盖插件文件**
//
//   node bili.mjs check-update [--json] [--download] [--to <目录>]
//
// 比的是什么：
//   本地 = 插件目录 manifest.json 的 Version
//   上游 = 插件市场里本插件那份包描述（BDFFZI/Alife.PluginMarket 的 DaFeiYu.BiliWhale.json）的 releases 版本号
//          三个镜像依次试：jsDelivr → GitHub API → raw.githubusercontent（本机 raw 常不通，所以放在最后）
//
// 纪律：
//   · 只读：默认一个字都不写盘（--download 才下载，而且只往**备份区**放，永远不碰插件目录）
//   · 不自动升级、不覆盖任何文件 —— 报版本号给人/agent 看，由人决定
//   · 顺带报一句"包内上游工具链版本"（upstream-package.json），只是信息，不参与判定
//
// 退出码：0=已是最新（或本地比市场还新） · 4=上游有新版本 · 1=检查失败（网络/解析）
import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'
import { fileURLToPath } from 'node:url'

import { dshPath } from '../lib/paths.mjs'

const PLUGIN_ID = 'DaFeiYu.BiliWhale'
const MARKET_REPO = 'BDFFZI/Alife.PluginMarket'
const MARKET_PATH = `${PLUGIN_ID}.json`
const MIRRORS = [
  { name: 'jsDelivr', url: `https://cdn.jsdelivr.net/gh/${MARKET_REPO}@main/${MARKET_PATH}` },
  { name: 'GitHub API', url: `https://api.github.com/repos/${MARKET_REPO}/contents/${MARKET_PATH}`, gh: true },
  { name: 'raw.githubusercontent', url: `https://raw.githubusercontent.com/${MARKET_REPO}/main/${MARKET_PATH}` },
]

const argFlag = (name) => process.argv.includes(name)
function argValue(name, fallback = '') {
  const i = process.argv.indexOf(name)
  if (i < 0) return fallback
  const v = process.argv[i + 1]
  return v === undefined || String(v).startsWith('--') ? fallback : String(v)
}

const here = path.dirname(fileURLToPath(import.meta.url))
const PLUGIN_DIR = path.resolve(here, '..', '..')          // tools/commands → 插件根

const out = { ok: false, state: 'error', local: '', upstream: '', mirror: '', file: '', msg: '' }

/** 版本号比较：4.6.10 > 4.6.2（数字段按数值比，非数字段按字典序兜底） */
function cmpVer(a, b) {
  const pa = String(a).split(/[.\-+]/), pb = String(b).split(/[.\-+]/)
  for (let i = 0; i < Math.max(pa.length, pb.length); i += 1) {
    const x = pa[i] ?? '0', y = pb[i] ?? '0'
    const nx = /^\d+$/.test(x), ny = /^\d+$/.test(y)
    if (nx && ny) { const d = Number(x) - Number(y); if (d) return d > 0 ? 1 : -1; continue }
    if (x !== y) return x > y ? 1 : -1
  }
  return 0
}

async function fetchJson(url, opt = {}) {
  const r = await fetch(url, {
    headers: { 'User-Agent': 'alife-dafeiyu-biliwhale-checkupdate', Accept: 'application/vnd.github+json, application/json' },
    signal: AbortSignal.timeout(15000),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  if (opt.gh) {
    const j = await r.json()
    if (!j?.content) throw new Error('GitHub API 返回里没有 content')
    return JSON.parse(Buffer.from(String(j.content).replace(/\n/g, ''), 'base64').toString('utf8'))
  }
  return await r.json()
}

const readLocalVersion = () => {
  const m = JSON.parse(fs.readFileSync(path.join(PLUGIN_DIR, 'manifest.json'), 'utf8').replace(/^\uFEFF/, ''))
  return String(m.Version ?? '').trim()
}
const readUpstreamToolVersion = () => {
  try {
    const p = JSON.parse(fs.readFileSync(path.join(PLUGIN_DIR, 'upstream-package.json'), 'utf8').replace(/^\uFEFF/, ''))
    return `${p.name} ${p.version}`.trim()
  } catch { return '（读不到 upstream-package.json）' }
}

try {
  out.local = readLocalVersion()
  if (!out.local) throw new Error('插件目录 manifest.json 里没有 Version')

  let market = null, used = ''
  const failures = []
  for (const m of MIRRORS) {
    try {
      market = await fetchJson(m.url, m)
      used = m.name
      break
    } catch (e) { failures.push(`${m.name}: ${String(e?.message ?? e).slice(0, 60)}`) }
  }
  if (!market) throw new Error(`三个镜像都没拿到市场描述 —— ${failures.join(' | ')}`)

  const releases = market.releases && typeof market.releases === 'object' ? market.releases : {}
  const versions = Object.keys(releases)
  if (versions.length === 0) throw new Error('市场描述里没有 releases')

  let latest = versions[0]
  for (const v of versions) if (cmpVer(v, latest) > 0) latest = v
  const rel = releases[latest] ?? {}

  out.upstream = String(latest)
  out.mirror = used
  out.file = String(rel.file ?? '')
  const diff = cmpVer(out.upstream, out.local)

  console.log('\n══════ 检查更新（只读 · 绝不自动覆盖插件）══════\n')
  console.log(`  本地版本：${out.local}   （${path.join(PLUGIN_DIR, 'manifest.json')}）`)
  console.log(`  上  游  ：${out.upstream}   （市场描述 · 源 ${used} · ${rel.date ? String(rel.date).slice(0, 10) : '日期未标'}）`)
  console.log(`  包内上游工具链：${readUpstreamToolVersion()}（只是信息，不参与比较）`)

  if (diff === 0) {
    out.ok = true; out.state = 'latest'
    out.msg = `已是最新（本地 ${out.local} = 上游 ${out.upstream}）`
    console.log(`\n  结论：${out.msg} ✓\n`)
  } else if (diff > 0) {
    out.ok = true; out.state = 'newer'
    out.msg = `上游有新版本：${out.upstream}（本地 ${out.local}）`
    console.log(`\n  结论：${out.msg}`)
    if (rel.note) console.log(`  更新说明：${String(rel.note).replace(/\s+/g, ' ').slice(0, 200)}`)
    if (out.file) console.log(`  包地址：${out.file}`)
    console.log('  ⚠️ 本命令不会下载、更不会覆盖任何插件文件。')
    console.log('     想留一份包：node bili.mjs check-update --download   （只会放进备份区，不动插件目录）\n')
  } else {
    out.ok = true; out.state = 'ahead'
    out.msg = `本地比市场还新：本地 ${out.local} > 上游 ${out.upstream}（多半是还没发布的改动）`
    console.log(`\n  结论：${out.msg}\n`)
  }

  // 可选：把包下到备份区（**绝不**落进插件目录）
  // 下载单独兜错：本机 github.com 常不通，下不到也**不能**影响"检查结果"这件事本身 ✗
  if (argFlag('--download') && out.state === 'newer') {
    const url = argValue('--url', out.file)
    if (!url) {
      out.download_error = '市场描述里没有 file 地址'
      console.log('  （--download：市场描述里没给包地址，跳过）\n')
    } else {
      try {
        const to = path.resolve(argValue('--to', dshPath('upstream-backup')))
        fs.mkdirSync(to, { recursive: true })
        let name = `${PLUGIN_ID}_${out.upstream}.zip`
        try { name = path.basename(new URL(url).pathname) || name } catch { /* 保持默认名 */ }
        const target = path.join(to, name)
        const r = await fetch(url, { signal: AbortSignal.timeout(60000), redirect: 'follow' })
        if (!r.ok) throw new Error(`HTTP ${r.status}`)
        const buf = Buffer.from(await r.arrayBuffer())
        if (buf.length === 0) throw new Error('下到 0 字节')
        const tmp = `${target}.part`
        fs.writeFileSync(tmp, buf)
        fs.renameSync(tmp, target)          // 原子落地：要么完整文件，要么什么都没有
        const sha = crypto.createHash('sha256').update(buf).digest('hex').slice(0, 16)
        out.downloaded = target
        console.log(`  已下载到备份区：${target}（${buf.length} 字节 · sha256:${sha}…）`)
        console.log('  （备份区，不是插件目录 —— 想升级请自己解压替换，本命令不代劳）\n')
      } catch (e) {
        out.download_error = String(e?.message ?? e).slice(0, 120)
        console.log(`  ⚠️ 下载失败：${out.download_error}`)
        console.log('     （本机 github.com/raw 常不通；检查结果不受影响。可用 --url <镜像地址> 再试，或让 agent 手工下。）\n')
      }
    }
  } else if (argFlag('--download') && out.state !== 'newer') {
    console.log('  （--download：没有更新可下，跳过）\n')
  }
} catch (e) {
  out.state = 'error'
  out.msg = `检查更新失败：${String(e?.message ?? e)}`
  console.log(`\n  ✗ ${out.msg}`)
  console.log('    本地版本照旧能读；上游拿不到通常是网络/镜像问题 —— 不影响插件运行。\n')
  if (argFlag('--json')) console.log(JSON.stringify(out))
  process.exit(1)
}

if (argFlag('--json')) console.log(JSON.stringify(out))
process.exit(out.state === 'newer' ? 4 : 0)