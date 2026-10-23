/** 用昵称查 B 站 UID（2026-10-23 从 dsh-bili-scroll 的 `lib/uid.mjs` 移植）
 *
 *  为什么：配置里 `owner.mid` 空/非数字时，`dm-await.mjs` 原来直接 `exit 2`
 *  ⇒ 一条主人的私信都收不到。而配置里通常有 `owner.name` —— 用昵称查一次就够。
 *
 *  策略（照参照实现）：先精确匹配 `uname`，退到第一个结果；结果缓存进 `<stateDir>/uid-cache.json`，
 *  避免每轮都打接口。**同时只读**：只调搜索接口，不写任何内容。
 *  ⚠️ 只在 `owner.mid` 不是数字时调用 —— **绝不覆盖已填好的 mid**。
 */
import fs from 'node:fs'
import path from 'node:path'

const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131 Safari/537.36'

/** 默认状态目录：`<home>/.bili-ai`（与 credentials.json / dm-await-state.json 同处）。 */
export const stateDirOf = (home = process.env.USERPROFILE || process.env.HOME || '/') => path.join(home, '.bili-ai')

function loadCred(dir) {
  try {
    const c = JSON.parse(fs.readFileSync(path.join(dir, 'credentials.json'), 'utf8'))
    return c?.sessdata ? c : null
  } catch { return null }
}
function readCache(dir) {
  try { return JSON.parse(fs.readFileSync(path.join(dir, 'uid-cache.json'), 'utf8')) || {} } catch { return {} }
}
function writeCache(dir, obj) {
  try { fs.writeFileSync(path.join(dir, 'uid-cache.json'), JSON.stringify(obj, null, 2) + '\n', 'utf8') } catch { /* 写不了就算了 */ }
}

/** 昵称 → mid。
 *  @returns {Promise<number>} 0 = 没查到
 */
export async function resolveUid(name, { stateDir = stateDirOf(), log = () => {}, cache = true } = {}) {
  const q = String(name || '').trim()
  if (!q) return 0
  const cached = readCache(stateDir)
  if (Number(cached[q]) > 0) return Number(cached[q])

  const cred = loadCred(stateDir)
  if (!cred) { log('查 UID 失败：还没有凭据（credentials.json）'); return 0 }

  const url = 'https://api.bilibili.com/x/web-interface/search/type?search_type=bili_user&page=1&keyword='
    + encodeURIComponent(q)
  try {
    const r = await fetch(url, {
      headers: {
        'user-agent': UA,
        referer: 'https://search.bilibili.com/',
        cookie: `SESSDATA=${cred.sessdata}; bili_jct=${cred.bili_jct}; buvid3=${cred.buvid3}`,
      },
    })
    const j = await r.json().catch(() => null)
    if (j?.code !== 0) { log(`查 UID 失败：搜索接口 code=${j?.code ?? '?'} ${j?.message ?? ''}`); return 0 }
    const list = j?.data?.result ?? []
    if (!list.length) { log(`查 UID：「${q}」没有搜索结果`); return 0 }
    const exact = list.find((u) => String(u.uname) === q)
    const pick = exact || list[0]
    const mid = Number(pick.mid) || 0
    if (mid > 0 && cache) { cached[q] = mid; writeCache(stateDir, cached) }
    if (mid > 0) log(`查 UID：「${q}」→ ${mid}（${pick.uname}${exact ? '，精确匹配' : '，第一个结果'}）`)
    return mid
  } catch (e) {
    log('查 UID 失败：' + String(e?.message ?? e))
    return 0
  }
}