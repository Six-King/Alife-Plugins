// 扫码登录（⑰-3）—— 主人点配置页「扫码登录」时跑的就是这条
//
//   node bili.mjs qr-login [--out <PNG路径>] [--wait <秒>]
//
// 流程（严格按"先判断、再动作"）：
//   ① 只读验登录态：GET /x/web-interface/nav（带当前 cookie）
//        在线 → **只回一句「账号在线，无需扫码」**：不生成图片、不碰登录接口、不动凭据文件 ✓
//   ② 不在线才走网页扫码：
//        GET /x/passport-login/web/qrcode/generate → data.url + data.qrcode_key
//        把 data.url 画成 PNG 落到桌面（路径由 C# 传 --out；留空则退回 USERPROFILE\Desktop）
//        轮询 /x/passport-login/web/qrcode/poll?qrcode_key=…
//          data.code: 86101 未扫 / 86090 已扫待确认 / 0 成功 / 86038 过期
//   ③ 成功：凭据在**轮询响应的 Set-Cookie** 里（不是 data.url —— 那是别的域的票）——
//        SESSDATA / bili_jct / buvid3（缺了就补一张指纹）+ dedeuserid；有 access_token 也一并存
//        原子写 credentials.json（合并原有键；绝不写半个文件）
//   ④ 用完把二维码图片删掉（成功 / 过期 / 超时 / 错误，只要图生成过就删）
//
// 规矩：
//   · 全程**不打印任何凭据**（SESSDATA / bili_jct / buvid3 / token 一个都不出现在输出里）
//   · 失败**绝不写坏** credentials.json（只有拿到 SESSDATA 才写，且是临时文件 + 改名）
//   · 进度一律走 stderr；stdout 只给"能直接念给人听"的几行 + 最后一条 JSON（C# 解析它）
//   · 不碰额度账本 / 分享闸门 / 日记 / 任何与登录无关的文件
//
// 为什么不用全局 fetch：poll 成功时靠 Set-Cookie 交凭据，而 fetch 的 redirect:'manual'
// 会把响应头吃成 opaque（拿不到 set-cookie）。这里直接走 node:https，原样拿到每个响应头 ✓
//
// 退出码：0=在线跳过或登录成功 · 3=没扫上（过期/超时）· 1=出错（网络/生成/写盘）
import fs from 'node:fs'
import path from 'node:path'
import https from 'node:https'

import { CRED_PATH } from '../lib/cred.mjs'
import { renderQrPng, removeQrFile, DEFAULT_QR_NAME } from '../lib/qr-image.mjs'

const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36'
const NAV = 'https://api.bilibili.com/x/web-interface/nav'
const GEN = 'https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-fe-header'
const SPI = 'https://api.bilibili.com/x/frontend/finger/spi'
const pollUrl = (key) => 'https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=' + encodeURIComponent(key) + '&source=main-fe-header'

// ── 输出：说给人听的走 stdout，进度/诊断走 stderr ──
const say = (msg) => console.log(msg)
const note = (msg) => console.error(msg)

function argValue(name, fallback = '') {
  const i = process.argv.indexOf(name)
  if (i < 0) return fallback
  const v = process.argv[i + 1]
  return v === undefined || String(v).startsWith('--') ? fallback : String(v)
}

/** 桌面兜底路径。注意：插件起的子进程 USERPROFILE 被换成了插件数据目录，
 *  所以正常情况一定是 C# 用 --out 把**真桌面**传进来；这里只是"单跑脚本"时的兜底。 */
function defaultOutPath() {
  const home = process.env.USERPROFILE || process.env.HOME || process.cwd()
  return path.join(home, 'Desktop', DEFAULT_QR_NAME)
}

/** 读凭据：文件不在 / 不是 JSON 都别炸，返回空壳（没凭据=没登录，直接去扫码）。 */
function readCred() {
  try {
    const raw = fs.readFileSync(CRED_PATH, 'utf8').replace(/^\uFEFF/, '')
    const j = JSON.parse(raw)
    const clean = (v) => String(v ?? '').trim()
    return { sessdata: clean(j.sessdata), bili_jct: clean(j.bili_jct), buvid3: clean(j.buvid3) }
  } catch {
    return { sessdata: '', bili_jct: '', buvid3: '' }
  }
}

const cookieHeader = (c) => [
  c.sessdata ? `SESSDATA=${c.sessdata}` : '',
  c.bili_jct ? `bili_jct=${c.bili_jct}` : '',
  c.buvid3 ? `buvid3=${c.buvid3}` : '',
].filter(Boolean).join('; ')

/** 一次 GET（不跟跳转，原样拿响应头）。返回 {status, headers, setCookies, text, json} */
function getJson(url, { cookie = '', timeoutMs = 20000 } = {}) {
  return new Promise((resolve, reject) => {
    const headers = { 'User-Agent': UA, Referer: 'https://www.bilibili.com/', Accept: 'application/json, text/plain, */*' }
    if (cookie) headers.Cookie = cookie
    const req = https.request(url, { method: 'GET', headers, timeout: timeoutMs }, (res) => {
      const chunks = []
      res.on('data', (c) => chunks.push(c))
      res.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8')
        let json = null
        try { json = JSON.parse(text) } catch { json = null }
        resolve({ status: res.statusCode, headers: res.headers, setCookies: res.headers['set-cookie'] ?? [], text, json })
      })
    })
    req.on('timeout', () => req.destroy(new Error(`请求超时（${timeoutMs}ms）`)))
    req.on('error', reject)
    req.end()
  })
}

/** ① 只读验登录态。返回 {state:'online'|'offline'|'unknown', uname, mid, why} */
async function checkLogin() {
  const cred = readCred()
  if (!cred.sessdata) return { state: 'offline', why: '本地没有 SESSDATA' }
  try {
    const r = await getJson(NAV, { cookie: cookieHeader(cred) })
    if (!r.json) return { state: 'unknown', why: `nav 返回不是 JSON（HTTP ${r.status}）` }
    const j = r.json
    if (j.code === 0 && j.data && j.data.isLogin === true) {
      return { state: 'online', uname: String(j.data.uname ?? ''), mid: String(j.data.mid ?? '') }
    }
    if (j.code === -101) return { state: 'offline', why: 'SESSDATA 已失效（nav code=-101）' }
    return { state: 'offline', why: `nav code=${j.code}${j.message ? ` ${j.message}` : ''}` }
  } catch (e) {
    return { state: 'unknown', why: `nav 请求失败：${String(e?.message ?? e)}` }
  }
}

/** 从 Set-Cookie 数组里抠出要的几个（成功时凭据就在这里）*/
function pickCookies(list) {
  const jar = {}
  const want = { SESSDATA: 'sessdata', bili_jct: 'bili_jct', DedeUserID: 'dedeuserid', buvid3: 'buvid3' }
  for (const raw of list ?? []) {
    const first = String(raw).split(';')[0] || ''
    const i = first.indexOf('=')
    if (i <= 0) continue
    const name = first.slice(0, i).trim()
    const value = first.slice(i + 1).trim()
    if (want[name] && value) jar[want[name]] = value
  }
  return jar
}

/** 兜底：poll 的 data.url 里也常带着同一批参数（只补 Set-Cookie 里缺的键，不覆盖已有的）*/
function pickFromUrlData(url, jar) {
  try {
    const u = new URL(String(url))
    const map = { sessdata: 'sessdata', bili_jct: 'bili_jct', dedeuserid: 'DedeUserID', buvid3: 'buvid3' }
    for (const [key, field] of Object.entries(map)) {
      const v = u.searchParams.get(key)
      if (v && !jar[field]) jar[field] = v
    }
  } catch { /* 拼不出 URL 就算了 */ }
  return jar
}

/** 拿不到 buvid3 时补一张"设备指纹"（非致命，失败就当空）*/
async function ensureBuvid3(jar) {
  if (jar.buvid3) return jar
  try {
    const r = await getJson(SPI)
    const v = r.json?.data?.b_3
    if (v) jar.buvid3 = String(v)
  } catch { /* 拿不到就算了 */ }
  return jar
}

/** 原子写凭据文件：合并原有键；只有拿到 SESSDATA 才会被调用 ✓ */
function writeCredFile(jar, accessToken) {
  let cur = {}
  try { cur = JSON.parse(fs.readFileSync(CRED_PATH, 'utf8').replace(/^\uFEFF/, '')) } catch { cur = {} }
  const out = {
    ...cur,
    sessdata: jar.sessdata,
    bili_jct: jar.bili_jct || cur.bili_jct || '',
    buvid3: jar.buvid3 || cur.buvid3 || '',
  }
  if (jar.dedeuserid) out.dedeuserid = String(jar.dedeuserid)
  if (accessToken) out.access_token = String(accessToken)
  fs.mkdirSync(path.dirname(CRED_PATH), { recursive: true })
  const tmp = `${CRED_PATH}.tmp-${process.pid}`
  try {
    fs.writeFileSync(tmp, JSON.stringify(out, null, 2) + '\n', 'utf8')
    fs.renameSync(tmp, CRED_PATH)          // 同目录改名=原子：不会留下半个文件 ✗
  } catch (e) {
    try { fs.rmSync(tmp, { force: true }) } catch { /* 清不掉也不影响原文件 */ }
    throw e
  }
  return CRED_PATH
}

// ══════════════════════════ 主流程 ══════════════════════════

const outPath = argValue('--out', defaultOutPath())
const waitSec = Math.max(15, Number(argValue('--wait', '150')) || 150)
const result = { ok: false, state: 'error', msg: '', png: '', png_removed: false, waited: 0, access_token: false }

function finish(code) {
  if (result.png && !result.png_removed) result.png_removed = removeQrFile(result.png)
  say(JSON.stringify(result))
  process.exit(code)
}

try {
  note(`二维码将存到：${outPath}`)
  const chk = await checkLogin()
  note(`登录态：${chk.state}${chk.why ? `（${chk.why}）` : ''}`)

  // ① 在线 → 只回一句，什么都不做
  if (chk.state === 'online') {
    result.ok = true
    result.state = 'online'
    result.msg = '账号在线，无需扫码'
    say(`账号在线，无需扫码${chk.uname ? `（${chk.uname}${chk.mid ? ` · mid ${chk.mid}` : ''}）` : ''}`)
    finish(0)
  }

  // 验不了就别乱动（不生成图、不碰登录接口）
  if (chk.state === 'unknown') {
    result.state = 'error'
    result.msg = `验不了登录态：${chk.why} —— 没生成二维码，也没动任何凭据。网络好了再点一次。`
    say(result.msg)
    finish(1)
  }

  // ② 不在线 → 拿登录 URL + 画二维码
  say(`当前未登录（${chk.why}），开始扫码登录。`)
  const gr = await getJson(GEN)
  const gj = gr.json
  const data = gj?.data
  if (!data?.qrcode_key || !data?.url) {
    result.state = 'error'
    result.msg = `生成二维码失败：${gj?.message || `HTTP ${gr.status}，返回里没有 qrcode_key`}`
    say(result.msg)
    finish(1)
  }

  const png = await renderQrPng(data.url, outPath)
  result.png = png
  say(`二维码已生成：${png}`)
  say(`请用手机 B站 App 扫码（最多等 ${waitSec} 秒；扫完这张图会自动删掉）。`)
  note('轮询扫码结果……')

  // ③ 轮询
  const startedAt = Date.now()
  const deadline = startedAt + waitSec * 1000
  let intervalMs = 2000
  let lastCode = null
  let scanSaid = false
  while (Date.now() < deadline) {
    const res = await getJson(pollUrl(data.qrcode_key), { timeoutMs: 15000 })
    const body = res.json
    const code = body?.data?.code
    result.waited = Math.round((Date.now() - startedAt) / 1000)

    if (code === 0) {
      const jar = await ensureBuvid3(pickFromUrlData(body?.data?.url, pickCookies(res.setCookies)))
      const accessToken = String(body?.data?.access_token ?? '')
      if (!jar.sessdata) {
        result.state = 'error'
        result.msg = '登录成功但响应里没有 SESSDATA —— 没写凭据文件（怕写坏它），请再点一次；连续两次都这样就告诉主人。'
        say(result.msg)
        finish(1)
      }
      const file = writeCredFile(jar, accessToken)
      result.ok = true
      result.state = 'ok'
      result.access_token = !!accessToken
      result.msg = '扫码登录成功，凭据已写入（不回显）'
      say('扫码登录成功 ✓ 凭据已写入（不回显）。')
      say(`  凭据文件：${file}`)
      say(`  bili_jct：${jar.bili_jct ? '已拿到' : '这次响应里没带 —— 写操作可能还不通'}`)
      say(`  buvid3：${jar.buvid3 ? '已拿到' : '没有（不影响评论/私信）'}`)
      say(`  access_token：${accessToken ? '已一并保存' : '这次响应里没有'}`)
      finish(0)
    }

    if (code === 86038) {
      result.state = 'expired'
      result.msg = '二维码已过期（超过 3 分钟没扫）。图片已删除，再点一次「扫码登录」即可。'
      say(result.msg)
      finish(3)
    }

    if (code === 86090 && !scanSaid) {
      scanSaid = true
      say('已扫码，请在手机上点确认。')
      note('poll: 已扫码待确认')
    }
    if (code !== lastCode) {
      note(`poll code=${code ?? '?'}${body?.message ? ` ${body.message}` : ''}`)
      lastCode = code
    }

    if (code === 86090) intervalMs = 1000     // 扫上了就快一点，别让主人多等
    await new Promise((r) => setTimeout(r, intervalMs))
  }

  result.state = 'timeout'
  result.msg = `等了 ${waitSec} 秒没等到扫码确认。图片已删除，可再点一次「扫码登录」。`
  say(result.msg)
  finish(3)
} catch (e) {
  result.state = 'error'
  result.msg = `扫码登录出错：${String(e?.message ?? e)}`
  say(result.msg)
  note(`异常：${e?.stack ?? e}`)
  finish(1)
}