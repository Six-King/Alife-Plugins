/** 私信 content 解析（2026-10-23 从 dsh-bili-scroll 的 `lib/dm.mjs` 移植「分享卡」支持）
 *
 *  为什么：主人分享视频到私信时，消息是 **msg_type=7（视频卡）/ 8**，
 *  它的 content 里**没有 `content` 字段** —— 只有 `title` / `author` / `jump_url` / `thumb`。
 *  旧实现只认 `content`/`text` ⇒ 卡片被当成"空消息"⇒ 等于**收不到主人的分享** ✗
 *
 *  这里只做**解析**（纯读）：不碰任何写接口、不影响 --peek/--ack、账本、闸门。
 */

/** 把一条私信 content 解析成给人看的一行文字。
 *  @param {unknown} content 原始 content（JSON 字符串或纯文本）
 *  @param {number|string} msgType 该条消息的 msg_type（默认 1=文本）
 *  @returns {string}
 */
export function parseDmContent(content, msgType = 1) {
  try {
    const j = JSON.parse(String(content))
    const mt = Number(msgType) || 1
    if (mt === 7 || mt === 8) {
      if (j.jump_url || j.bvid || j.author || j.title) {
        const bv = String(j.bvid || '') || (String(j.jump_url || '').match(/BV[0-9A-Za-z]{10}/) || [])[0] || ''
        const title = String(j.title || j.headline || j.desc || '').slice(0, 80)
        const who = String(j.author || j.author_name || '').slice(0, 30)
        return '[分享视频] ' + (title || '(无标题)') + (who ? ' / ' + who : '') + (bv ? ' ' + bv : '')
      }
      // 卡片但没有认得出来的字段：别退化成空串（否则又变成"收不到"）
      if (!j.content && !j.text) return '[分享/卡片消息] ' + String(content ?? '').slice(0, 120)
    }
    return String(j.content ?? j.text ?? content)
  } catch { return String(content ?? '') }
}

/** 从一段文案里抠出 BV 号（没有就返回空串）。 */
export function extractBv(text) {
  return (String(text ?? '').match(/BV[0-9A-Za-z]{10}/) || [])[0] ?? ''
}