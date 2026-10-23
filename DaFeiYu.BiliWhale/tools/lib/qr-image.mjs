// 二维码图片（⑰-3 扫码登录用）
//
// 为什么单独一层：登录命令（commands/qr-login.mjs）只关心"流程"，
// 画图这件事被 C# 配置页、登录流程、以及验证脚本各用一次 —— 放一处，谁都不会写歪。
//
// 用的是 npm 包 qrcode（与参考项目 dsh-bili-scroll 同款做法）：
//   · 装在 tools/node_modules（只有工具链用，插件本体是 C#，不引任何 C# 依赖）
//   · 纠错级别固定 M（字节模式自动选版本）—— 和手机端扫码的默认容错一致
//   · 纯本地生成，绝不调任何在线服务
import fs from 'node:fs'
import path from 'node:path'
import QRCode from 'qrcode'

/** 默认文件名（配置页留空时用的就是它） */
export const DEFAULT_QR_NAME = 'bili-login-qr.png'

/**
 * 把一段文本画成 PNG 二维码。
 * @param {string} text 内容（登录 URL / 任意文本）
 * @param {string} outPath PNG 落地路径（绝对路径优先；相对路径按 cwd 解析）
 * @param {{ecc?: 'L'|'M'|'Q'|'H', margin?: number, width?: number}} [opt]
 * @returns {Promise<string>} 写好的 PNG 绝对路径
 */
export async function renderQrPng(text, outPath, opt = {}) {
  const content = String(text ?? '')
  if (!content) throw new Error('二维码内容为空 —— 不生成图片')
  const file = path.resolve(String(outPath || path.join(process.cwd(), DEFAULT_QR_NAME)))
  fs.mkdirSync(path.dirname(file), { recursive: true })
  await QRCode.toFile(file, content, {
    type: 'png',
    errorCorrectionLevel: opt.ecc || 'M',
    margin: Number.isFinite(opt.margin) ? opt.margin : 2,
    width: Number.isFinite(opt.width) ? opt.width : 512,
    color: { dark: '#000000ff', light: '#ffffffff' },
  })
  return file
}

/** 单次渲染的"结构自检"：返回版本号与边长（模块数），出错了就抛。 */
export function inspectQr(text, opt = {}) {
  const q = QRCode.create(String(text ?? ''), { errorCorrectionLevel: opt.ecc || 'M' })
  return { version: q.version, size: q.modules.size, ecc: opt.ecc || 'M' }
}

/** 删掉二维码图片（不存在就静默）。 */
export function removeQrFile(outPath) {
  try {
    const file = path.resolve(String(outPath || ''))
    if (file && fs.existsSync(file)) { fs.rmSync(file, { force: true }); return true }
  } catch { /* 删不掉不影响主流程 */ }
  return false
}

/** 图片在不在 + 多大（只给"我确认它真写出来了"用，不读内容） */
export function qrFileInfo(outPath) {
  try {
    const file = path.resolve(String(outPath || ''))
    const st = fs.statSync(file)
    return { path: file, bytes: st.size, exists: true }
  } catch {
    return { path: path.resolve(String(outPath || '')), bytes: 0, exists: false }
  }
}