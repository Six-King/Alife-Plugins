// 启动测试（2026-10-20 加 —— 装完插件先跑这个，一步确认能不能用）
//
//   node bili.mjs selftest
//
// 为什么需要它：很多人装完插件卡在两件事上 ——
//   ① 插件新开的会话在「工作区 → 未分组」里，不点开那个文件夹看不见
//   ② 那个新会话默认没有完全权限，agent 起来后什么都干不了
// 这个命令把"该确认的事"列成一张表，缺哪项就直说缺哪项。
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import crypto from 'node:crypto'
import { execFileSync } from '../lib/capture.mjs'
import { fileURLToPath } from 'node:url'
import { dshPath } from '../lib/paths.mjs'
import { NODE_EXE } from '../lib/nodebin.mjs'   // 2026-10-21：别用 process.execPath（Electron 里它是主程序 ✗）


const HOME = (process.env.USERPROFILE || process.env.HOME || '/')
const DSH = dshPath()
const CFG = path.join(DSH, 'bili-whale.config.json')
const CRED = path.join(HOME, '.bili-ai', 'credentials.json')
const OK = '✓'
const NO = '✗'
const WARN = '!'

let bad = 0
const line = (mark, label, detail) => {
  if (mark === NO) bad += 1
  console.log(`  ${mark} ${label}${detail ? '  —— ' + detail : ''}`)
}

console.log('\n══════════ 启动测试（装完插件跑这一条）══════════\n')

// ① 配置文件
try {
  const c = JSON.parse(fs.readFileSync(CFG, 'utf8'))
  line(OK, '配置文件', CFG)
  line(c.self?.mid ? OK : NO, '她的身份', c.self?.name ? `${c.self.name}（${c.self.mid}）` : '设置页 →「她是谁」还没填')
  line(c.owner?.mid ? OK : NO, '主人的身份', c.owner?.name ? `${c.owner.name}（${c.owner.mid}）` : '设置页 →「她是谁」里填主人 mid')
  line(c.share?.via ? OK : NO, '分享方式', c.share?.via === 'dm' ? '私信（不发公开评论）' : '评论区 @')
  line(c.notify?.type ? OK : NO, '消息推送', String(c.notify?.type ?? '未设置'))
} catch {
  line(NO, '配置文件', '没有 ' + CFG + ' —— 先去设置页保存一次')
}

// ② 凭据
try {
  const cred = JSON.parse(fs.readFileSync(CRED, 'utf8'))
  const has = !!cred.sessdata && !!cred.bili_jct
  line(has ? OK : NO, 'B站凭据', has ? '已保存（cookie）' : '缺 sessdata / bili_jct')
  line(cred.buvid3 ? OK : WARN, 'buvid3', cred.buvid3 ? '有' : '缺 —— 不影响评论/私信；个别接口可能被风控。想要就 F12 → Cookies 复制 buvid3 再写入')
  // 2026-10-21：bili_jct 是"能不能发东西"的关键 —— 只读全绿但一发就 code=-111，最常见就是它 ✗
  const jct = String(cred.bili_jct ?? '')
  if (!jct) line(NO, 'bili_jct（写操作）', '空着 —— 发私信/评论/收藏都会失败（code=-111）→ 设置页重新粘贴一次')
  else if (!/^[0-9a-fA-F]{32}$/.test(jct)) line(WARN, 'bili_jct（写操作）', '格式不像（长度 ' + jct.length + '，正常 32 位十六进制）—— 可能复制时截断了')
  else line(OK, 'bili_jct（写操作）', '格式正常（和 SESSDATA 是否同一账号，只能靠一次真实写入验证）')
} catch {
  line(NO, 'B站凭据', '没有 ' + CRED + ' —— 设置页粘贴 cookie 后保存')
}

// ③ 登录态
try {
  const BILI0 = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'bili.mjs')
  const out = execFileSync(NODE_EXE, [BILI0, 'session'], { encoding: 'utf8', timeout: 30000 })
  const who = out.match(/账号\s*:\s*(.+)/) ?? []
  const midM = out.match(/mid\s*:\s*(\d+)/) ?? []
  line(who[1] ? OK : NO, '登录态', who[1] ? `${who[1].trim()}（mid ${midM[1] ?? '?'}）` : String(out).trim().slice(0, 80))
} catch (e) {
  line(NO, '登录态', '查不到（cookie 可能过期）—— ' + String(e.message).slice(0, 60))
}

// ④ 数据目录
for (const d of ['.bili-ai', '.dsh']) {
  const p = path.join(HOME, d)
  line(fs.existsSync(p) ? OK : NO, '目录 ' + d, p)
}
const DIARY = path.join(DSH, 'whale-diary.md')
line(fs.existsSync(DIARY) ? OK : WARN, '日记', fs.existsSync(DIARY) ? '已存在' : '还没建（她第一次醒来会写）')

// ⑤ 依赖体检（直接调现成的 deps，别在这里重复实现 ✗）
try {
  const BILI2 = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'bili.mjs')
  const out = execFileSync(NODE_EXE, [BILI2, 'deps'], { encoding: 'utf8', timeout: 60000 })
  const reds = (String(out).match(/✗/g) ?? []).length
  line(reds === 0 ? OK : NO, '依赖体检（deps）', reds === 0 ? '全绿' : `${reds} 项红 —— 详见 \`node bili.mjs deps\``)
} catch (e) {
  line(WARN, '依赖体检（deps）', '没跑起来：' + String(e.message).slice(0, 60))
}

// ⑥ 今晚新加的能力（⑰ 系列）：分享卡解析 / UID 缓存 / 账本归档 / 提示词外置 + sha 账本
//    纪律：这一段**全只读** —— 不写凭据、不动账本（归档只跑 --dry）、不碰 share-gate / quota / dm-quota ✓
//    断言的写法：真跑真解析（不是"文件存在就算过"）✓
console.log('')

// ⑥-1 分享卡解析：主人把视频分享到私信时 msg_type=7/8，content 里**没有 content 字段**
//      （旧实现把它当空消息 ⇒ 等于收不到主人的分享 ✗）—— 这里用纯函数验四种形态
try {
  const { parseDmContent, extractBv } = await import('../lib/dm-content.mjs')
  const BV = 'BV1xx411c7mD'
  const cases = [
    ['视频卡(7)', parseDmContent(JSON.stringify({ title: '某段视频', author: '某UP', jump_url: `https://www.bilibili.com/video/${BV}` }), 7),
      (s) => s.startsWith('[分享视频]') && s.includes(BV)],
    ['纯文本(1)', parseDmContent(JSON.stringify({ content: '在吗' }), 1), (s) => s === '在吗'],
    ['认不出字段的卡(8)', parseDmContent(JSON.stringify({ foo: 'bar' }), 8), (s) => s.length > 0 && s.startsWith('[')],
    ['非 JSON 原文', parseDmContent('就是一句普通的话', 1), (s) => s === '就是一句普通的话'],
    ['抠 BV', extractBv(`看看这个 https://www.bilibili.com/video/${BV}/ 挺好`), (s) => s === BV],
  ]
  const failed = cases.filter(([, got, ok]) => !ok(String(got)))
  line(failed.length === 0 ? OK : NO, '分享卡解析（主人分享视频别再丢）',
    failed.length ? `${failed.length}/${cases.length} 项不对：` + failed.map(([n, got]) => `${n}→「${String(got).slice(0, 24)}」`).join('、')
                  : `${cases.length}/${cases.length} 通过（视频卡 / 纯文本 / 怪卡片 / 非 JSON / 抠 BV）`)
} catch (e) {
  line(NO, '分享卡解析', '模块加载失败：' + String(e.message).slice(0, 70))
}

// ⑥-2 UID 缓存：owner.mid 没填时靠昵称查一次、之后走缓存（不然每轮都打接口）
//      验法：临时目录里放一条缓存 → resolveUid 必须**不发网络**就返回它 ✓
try {
  const { resolveUid } = await import('../lib/uid.mjs')
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'bw-selftest-'))
  try {
    fs.writeFileSync(path.join(tmp, 'uid-cache.json'), JSON.stringify({ __selftest__: 424242 }), 'utf8')
    const hit = await resolveUid('__selftest__', { stateDir: tmp, cache: false })
    line(hit === 424242 ? OK : NO, 'UID 缓存（owner.mid 空着也能收到私信）',
      hit === 424242 ? '缓存命中即返回（0 次网络）' : `缓存没命中：返回 ${hit}（应当 424242）`)
  } finally { fs.rmSync(tmp, { recursive: true, force: true }) }

  const real = path.join(HOME, '.bili-ai', 'uid-cache.json')
  if (fs.existsSync(real)) {
    try {
      const c = JSON.parse(fs.readFileSync(real, 'utf8'))
      const entries = Object.entries(c ?? {})
      const badV = entries.filter(([, v]) => !(Number(v) > 0)).map(([k]) => k)
      line(badV.length === 0 ? OK : WARN, 'UID 缓存文件', `已缓存 ${entries.length} 条 · ${badV.length ? `${badV.length} 条 mid 不合法（${badV.slice(0, 3).join('、')}）` : '全部合法'}`)
    } catch (e2) { line(WARN, 'UID 缓存文件', '坏了（会自动重建）：' + String(e2.message).slice(0, 50)) }
  }
} catch (e) {
  line(NO, 'UID 缓存', '模块加载失败：' + String(e.message).slice(0, 70))
}

// ⑥-3 账本归档（quota archive）：只跑 --dry，只算不动 —— 验"30 天前的行能按月归进 Archive/ 且可合并回读"
try {
  const BILI3 = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'bili.mjs')
  const out = execFileSync(NODE_EXE, [BILI3, 'quota', 'archive', '--dry', '--json'], { encoding: 'utf8', timeout: 30000 })
  const j = JSON.parse(String(out).trim().split(/\r?\n/).pop() ?? '{}')
  const shaped = typeof j.moved === 'number' || typeof j.skipped === 'string'
  const detail = j.skipped ? `跳过：${j.skipped}`
    : j.moved === 0 ? `无需归档（当期 ${j.total} 条都还新，阈值 ${j.days} 天）`
    : `${j.dry ? '（dry-run）计划归档' : '将归档'} ${j.moved} 条 → ${(j.files ?? []).map((f) => `${f.file}+${f.added}`).join(' / ')}；当期剩 ${j.kept} 条`
  line(shaped ? OK : NO, '账本归档（quota archive --dry，只算不动）', shaped ? detail : '返回形状不对：' + detail)
} catch (e) {
  line(WARN, '账本归档', '没跑起来：' + String(e.message).slice(0, 60))
}

// ⑥-4 提示词外置 + sha256 升级账本：Prompts\*.txt + Prompts\.manifest.json（shipped/user 两个 sha）
//      "文件比账本新"不是错 —— 那是主人刚改完还没被插件读到（下次启动会认成"你改过"→ 保留你的版本）✓
try {
  const STATE_ROOT = path.dirname(HOME)                 // 插件把 HOME 指到 <插件数据目录>\home
  const DIR = path.join(STATE_ROOT, 'Prompts')
  const mf = path.join(DIR, '.manifest.json')
  if (!fs.existsSync(mf)) {
    line(WARN, '提示词外置', `还没 ${mf} —— 插件启动过一次后就会自动落地`)
  } else {
    const man = JSON.parse(fs.readFileSync(mf, 'utf8'))
    const keys = Object.keys(man ?? {})
    const hex64 = (v) => /^[0-9a-fA-F]{64}$/.test(String(v ?? ''))
    const badSha = keys.filter((k) => !hex64(man[k]?.shipped) || !hex64(man[k]?.user))
    const missing = keys.filter((k) => !fs.existsSync(path.join(DIR, k + '.txt')))
    const drift = keys.filter((k) => {
      try { return crypto.createHash('sha256').update(fs.readFileSync(path.join(DIR, k + '.txt'), 'utf8')).digest('hex') !== String(man[k].user) } catch { return true }
    })
    const txts = fs.readdirSync(DIR).filter((f) => f.endsWith('.txt')).length
    const news = fs.readdirSync(DIR).filter((f) => f.endsWith('.txt.new')).length
    const detail = `${keys.length} 个键（${txts} 个 txt）· sha 两个都在=${badSha.length === 0}`
      + (missing.length ? ` · ✗ 账本里有 ${missing.length} 个键没有对应文件（${missing.slice(0, 3).join('、')}）` : ' · 账本与文件对得上')
      + (drift.length ? ` · ${drift.length} 个文件比账本新（你改过，下次启动保留你的版本）` : '')
      + (news ? ` · ${news} 个新版待你对比（*.txt.new）` : '')
    line(badSha.length === 0 && missing.length === 0 && keys.length >= 10 ? OK : NO, '提示词外置 + sha256 账本', detail)
  }
} catch (e) {
  line(NO, '提示词外置 + sha256 账本', String(e.message).slice(0, 70))
}

// ⑦ 告诉她/他 下一步该做什么（这一步是很多人卡住的地方）
console.log('\n────────── 接下来要做的两件事（很多人卡在这）──────────\n')
console.log('  ① 【找到她的会话】')
console.log('     插件启动时会自动开一个会话，位置在：')
console.log('       左侧「工作区」→ 展开「未分组」文件夹 → 里面那个 bili-whale')
console.log('     ⚠️ 不点开「未分组」是看不见它的 —— 这一步最容易漏 ✗')
console.log('')
console.log('  ② 【给她完全权限】')
console.log('     打开那个会话 → 权限设置 → 选「完全访问 / danger-full-access」')
console.log('     否则她起来后跑不了命令、发不了评论、写不了日记 ✗')
console.log('')
// 2026-10-20：全绿时落个标记 —— 设置页和她的唤醒提示会用它在"还没测过"时提醒
if (bad === 0) {
  try {
    fs.mkdirSync(path.join(HOME, '.bili-ai'), { recursive: true })
    fs.writeFileSync(path.join(HOME, '.bili-ai', 'selftest-passed.json'), JSON.stringify({ at: new Date().toISOString() }, null, 2))
  } catch {}
  console.log('══════════ 全部通过 ✓ 可以用了 ══════════')
  console.log('  想立刻看她跑一轮：设置页 →「生物钟」→ 把窗口改成现在之后 2 分钟 → 等她自己醒\n')
} else {
  console.log(`══════════ ${bad} 项没通过 ✗ 按上面的提示补一下 ══════════\n`)
}
process.exit(bad === 0 ? 0 : 1)
