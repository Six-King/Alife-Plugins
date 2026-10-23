// 删除自己发的一条评论。用法:
//   node comment-del.mjs --oid <aid> --rpid <rpid> --bvid <BV> --dry | --go
// 文档: docs/comment-add.md「删除评论」（x/v2/reply/del，只能删自己的）
//
// 2026-10-23 加「删评豁免」（方案 0b，主人批）：删成功后写一条豁免，让"先删旧的再发新的"在 10 分钟内可用。
//   · 正文必须在**删之前**取（删完这条就查不到了 ✗）—— 不能放到 del 后面
//   · 只读账本、**绝不改账本** ✓；豁免文件坏 / 取不到正文 → 不写豁免（fail-safe ✓，重复闸照旧拦）
import fs from 'node:fs'
import path from 'node:path'

const argv = process.argv.slice(2)
const args = new Map()
for (let i = 0; i < argv.length; i += 1) {
  const a = argv[i]
  if (a.startsWith('--')) args.set(a.slice(2), argv[i + 1]?.startsWith('--') ? true : argv[++i] ?? true)
}
const oid = args.get('oid')
const rpid = args.get('rpid')
const bvid = args.get('bvid')
const isDry = args.has('dry')
const isGo = args.has('go')
if (!oid || !rpid) { console.error('需要 --oid <aid> --rpid <rpid>'); process.exit(2) }
if (isDry === isGo) { console.error('必须且只能给一个 --dry 或 --go'); process.exit(2) }

const HOME = process.env.USERPROFILE || process.env.HOME || '/'
const cred = JSON.parse(fs.readFileSync(path.join(HOME, '.bili-ai', 'credentials.json'), 'utf8'))
const cookie = `SESSDATA=${cred.sessdata}; bili_jct=${cred.bili_jct}; buvid3=${cred.buvid3}`
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36'
const REF = bvid ? `https://www.bilibili.com/video/${bvid}/` : 'https://www.bilibili.com/'
const EXEMPT_FILE = path.join(HOME, '.bili-ai', 'deleted-comments.json')

console.log('=== 将删除 ===')
console.log(`  POST https://api.bilibili.com/x/v2/reply/del`)
console.log(`  type=1  oid=${oid}  rpid=${rpid}  csrf=***csrf***   cookie=SESSDATA=***`)
if (bvid) console.log(`  referer=https://www.bilibili.com/video/${bvid}/`)
if (isDry) { console.log('\n[dry] 未删除。'); process.exit(0) }

// ── 删之前把这条的**原始正文**取回来（存豁免用；不截断不归一，归一交给 C# 的 SameComment）──
//   ① rpid 是根评论（一级评论/分享）：reply/reply?root=<rpid> 会回 data.root
//   ② 兜底：评论区主列表（时间倒序）前 3 页里找 rpid
//   二级回复（挂在别人楼下）这两个接口都够不着 → 取不到就不写豁免（fail-safe ✓，宁可不豁免）
async function fetchCommentText() {
  const headers = { 'user-agent': UA, referer: REF, cookie }
  try {
    const j = await (await fetch(`https://api.bilibili.com/x/v2/reply/reply?type=1&oid=${oid}&root=${rpid}&pn=1&ps=1`, { headers })).json()
    const root = j?.data?.root
    if (root && String(root.rpid) === String(rpid)) {
      const t = String(root.content?.message ?? '')
      if (t) return t
    }
  } catch { /* 取不到就走兜底 */ }
  try {
    for (let pn = 1; pn <= 3; pn += 1) {
      const j = await (await fetch(`https://api.bilibili.com/x/v2/reply?type=1&oid=${oid}&pn=${pn}&ps=20&sort=2`, { headers })).json()
      const list = j?.data?.replies ?? []
      if (!list.length) break
      const hit = list.find((x) => String(x.rpid) === String(rpid))
      if (hit) {
        const t = String(hit.content?.message ?? '')
        if (t) return t
      }
    }
  } catch { /* 兜底失败 */ }
  return ''
}

// ── 先按 rpid 查 sidecar（"发的时候"记下的原始正文 → 零额外 HTTP ✓，二级回复也够得着）；
//    查不到再退回 HTTP 通路（老评论 / 没记过的情况）──────────────────────────
function lookupSentText(id) {
  try {
    const j = JSON.parse(fs.readFileSync(EXEMPT_FILE, 'utf8'))
    if (!Array.isArray(j)) return ''
    const hit = j.filter((x) => x && String(x.rpid ?? '') === String(id) && x.text).pop()
    return String(hit?.text ?? '')
  } catch { return '' }   // 文件不存在/坏 → 退回 HTTP（fail-safe ✓）
}

const text = lookupSentText(rpid) || await fetchCommentText()

const r = await fetch('https://api.bilibili.com/x/v2/reply/del', {
  method: 'POST',
  headers: {
    'user-agent': UA,
    referer: REF,
    origin: 'https://www.bilibili.com',
    'content-type': 'application/x-www-form-urlencoded',
    cookie,
  },
  body: new URLSearchParams({ type: '1', oid: String(oid), rpid: String(rpid), csrf: String(cred.bili_jct) }).toString(),
})
const out = await r.json()
const hints = { 0: '成功', '-101': '未登录', '-111': 'csrf 不对', '-403': '权限不足（不是自己的评论）', '-404': '无此项', 12022: '已经被删除了' }
console.log(`\n[go] HTTP ${r.status}  code=${out.code} ${out.message ?? ''}   ${hints[out.code] ?? ''}`)

// ── 删成功（或 12022「本来就已经没了」，语义同样是"这条已不存在"）→ 写一条豁免 ──
//    只读账本，绝不写/重排/删账本行 ✓；退出码逻辑不变 ✗
if (out.code === 0 || out.code === 12022) {
  if (!text) {
    console.log('   [豁免] 没取到正文（多半是二级回复）→ 不写豁免，重复闸照旧生效')
  } else {
    try {
      let arr = []
      try {
        const j = JSON.parse(fs.readFileSync(EXEMPT_FILE, 'utf8'))
        if (Array.isArray(j)) arr = j
      } catch { arr = [] }
      const target = bvid ? `BV:${bvid}` : `oid:${oid}`
      const mine = { at: new Date().toISOString(), target, text, rpid: String(rpid), kind: 'deleted' }
      // 去重只在自己这一类里做（kind=deleted）；"发时记录"(kind=sent) 同 rpid 也不冲突
      const sameDeletion = (x) => x && x.kind === 'deleted'
        && ((x.rpid && String(x.rpid) === String(rpid)) || (x.target === target && x.text === text))
      if (!arr.some(sameDeletion)) arr.push(mine)
      const cutoff = Date.now() - 24 * 3600 * 1000   // 只留 24 小时内的（自然过期）
      arr = arr
        .filter((x) => x && typeof x.at === 'string' && typeof x.text === 'string' && x.text && Date.parse(x.at) >= cutoff)
        .slice(-50)                                  // 上限 50 条，文件不会无限长
      fs.mkdirSync(path.dirname(EXEMPT_FILE), { recursive: true })
      fs.writeFileSync(EXEMPT_FILE, JSON.stringify(arr, null, 2) + '\n')
      console.log(`   [豁免] 已写删评豁免（${target}）→ 10 分钟内同一条不再被重复闸拦 ✓`)
    } catch (e) {
      console.log(`   [豁免] 写入失败（不影响删评）：${e.message}`)
    }
  }
}
process.exit(out.code === 0 ? 0 : 1)