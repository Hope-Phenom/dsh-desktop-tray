#!/usr/bin/env node
/**
 * check-skin-updates — DSH Web 皮肤/插件更新检查器
 *
 * 覆盖两个更新源：
 *   1. maid-atelier（深海女仆工坊，dsh-deep-whale）——本地 link: 路径安装，
 *      不走 npm，只能对比 GitHub 最新提交。`--update` 会把最新文件重新拉下来。
 *   2. @linxin666/*（dsh-web-ui 全家桶 / dsh-skins）——npm 发布，对比 npm 最新版
 *      与本机 profile 已安装版本。
 *
 * 用法：
 *   node tools/check-skin-updates.mjs           # 只检查，打印是否有更新
 *   node tools/check-skin-updates.mjs --update  # 检查；maid-atelier 有更新时自动重新下载
 *
 * 注意：本地路径安装的皮肤更新后，需要重启 `dsh web`（或 dsh-desktop-tray 托盘）
 * 才会生效；npm 插件更新后同样需要重启。
 */

import { createHash } from 'node:crypto'
import { mkdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join as joinPath } from 'node:path'
import { fileURLToPath } from 'node:url'
import https from 'node:https'

const SCRIPT_DIR = dirname(fileURLToPath(import.meta.url))
const WORKSPACE = joinPath(SCRIPT_DIR, '..')

// --- 配置 ------------------------------------------------------------------

/** dsh-deep-whale 仓库：maid-atelier 皮肤源码（本地 link: 安装指向这里）。 */
const DEEP_WHALE_REPO = 'Small-tailqwq/dsh-deep-whale'
const DEEP_WHALE_DIR = joinPath(WORKSPACE, '_ref', 'dsh-deep-whale')
const DEEP_WHALE_STAMP = joinPath(DEEP_WHALE_DIR, '.last-commit')

/** @linxin666 scope 的 npm 包（dsh-web-ui 全家桶 / 皮肤包）。 */
const LINXIN_PACKAGES = ['dsh-web-ui-all', 'dsh-skins']

/** web profile 目录。 */
const PROFILE_DIR = process.env.DSH_HOME
  ? joinPath(process.env.DSH_HOME, 'profiles', 'web')
  : joinPath(homedir(), '.dsh', 'profiles', 'web')
const PROFILE_MANIFEST = joinPath(PROFILE_DIR, 'package.json')

// --- 网络工具 ----------------------------------------------------------------

function getJson(url, timeoutMs = 20000) {
  return new Promise((resolve, reject) => {
    const req = https.get(url, {
      rejectUnauthorized: false,
      headers: { 'User-Agent': 'Mozilla/5.0 (dsh-skin-update-checker)' },
    }, (res) => {
      if (res.statusCode !== 200) {
        res.resume()
        return reject(new Error(`${url} -> HTTP ${res.statusCode}`))
      }
      let data = ''
      res.setEncoding('utf8')
      res.on('data', (c) => { data += c })
      res.on('end', () => {
        try { resolve(JSON.parse(data)) } catch (e) { reject(new Error(`${url}: JSON parse failed`)) }
      })
    })
    req.on('error', reject)
    req.setTimeout(timeoutMs, () => { req.destroy(new Error(`timeout ${url}`)) })
  })
}

function getBuffer(url, timeoutMs = 60000) {
  return new Promise((resolve, reject) => {
    const req = https.get(url, {
      rejectUnauthorized: false,
      headers: { 'User-Agent': 'Mozilla/5.0 (dsh-skin-update-checker)' },
    }, (res) => {
      if (res.statusCode !== 200) {
        res.resume()
        return reject(new Error(`${url} -> HTTP ${res.statusCode}`))
      }
      const chunks = []
      res.on('data', (c) => chunks.push(c))
      res.on('end', () => resolve(Buffer.concat(chunks)))
    })
    req.on('error', reject)
    req.setTimeout(timeoutMs, () => { req.destroy(new Error(`timeout ${url}`)) })
  })
}

// --- 1. maid-atelier（dsh-deep-whale）-----------------------------------------

function readStamp() {
  try { return readFileSync(DEEP_WHALE_STAMP, 'utf8').trim() } catch { return null }
}

/** 拉取 dsh-deep-whale main 分支最新提交 SHA + 时间。 */
async function latestDeepWhaleCommit() {
  const c = await getJson(`https://api.github.com/repos/${DEEP_WHALE_REPO}/commits/main`)
  return { sha: c.sha, date: c.commit?.committer?.date ?? '' }
}

/** 把仓库全部文件重新下载到 DEEP_WHALE_DIR（并发 8），然后写时间戳。 */
async function refreshDeepWhale() {
  const tree = await getJson(`https://api.github.com/repos/${DEEP_WHALE_REPO}/git/trees/main?recursive=1`)
  const blobs = (tree.tree ?? []).filter((i) => i.type === 'blob')
  if (blobs.length === 0) throw new Error('empty tree from GitHub')
  const base = `https://raw.githubusercontent.com/${DEEP_WHALE_REPO}/main/`
  const results = new Array(blobs.length)
  let next = 0
  const worker = async () => {
    while (next < blobs.length) {
      const idx = next++
      const b = blobs[idx]
      try {
        results[idx] = { ok: true, buf: await getBuffer(base + b.path.split('/').map(encodeURIComponent).join('/')) }
      } catch (e) {
        results[idx] = { ok: false, err: e.message }
      }
    }
  }
  await Promise.all(Array.from({ length: 8 }, worker))
  let ok = 0
  const failed = []
  for (let i = 0; i < blobs.length; i++) {
    const r = results[i]
    if (r.ok) {
      const dest = joinPath(DEEP_WHALE_DIR, blobs[i].path)
      mkdirSync(dirname(dest), { recursive: true })
      writeFileSync(dest, r.buf)
      ok++
    } else {
      failed.push(`${blobs[i].path} (${r.err})`)
    }
  }
  if (ok === 0) throw new Error('download failed entirely: ' + failed.join('; '))
  return { ok, total: blobs.length, failed }
}

// --- 2. @linxin666/*（npm）-----------------------------------------------------

function readProfileDeps() {
  try {
    const m = JSON.parse(readFileSync(PROFILE_MANIFEST, 'utf8'))
    return { deps: m.dependencies ?? {}, bundles: m.dsh?.profile?.bundles ?? [] }
  } catch {
    return { deps: {}, bundles: [] }
  }
}

/** 读取已安装包的真实版本（从 node_modules 的 package.json）。 */
function installedVersion(pkgName) {
  try {
    const p = joinPath(PROFILE_DIR, 'node_modules', ...pkgName.split('/'))
    const m = JSON.parse(readFileSync(joinPath(p, 'package.json'), 'utf8'))
    return m.version ?? '?'
  } catch {
    return null
  }
}

async function latestNpmVersion(pkgName) {
  const j = await getJson(`https://registry.npmjs.org/${encodeURIComponent(pkgName)}/latest`)
  return j.version
}

// --- 主流程 -------------------------------------------------------------------

const args = new Set(process.argv.slice(2))
const doUpdate = args.has('--update')

let changed = false

console.log('== DSH Web 皮肤 / 插件更新检查 ==\n')

// 1) maid-atelier
console.log('[1] maid-atelier 皮肤（dsh-deep-whale，本地 link: 安装）')
try {
  const latest = await latestDeepWhaleCommit()
  const stamp = readStamp()
  const baseline = stamp ? `本地基线提交 ${stamp.slice(0, 12)}` : '本地基线未知（首次运行将记录）'
  console.log(`    最新提交   ${latest.sha.slice(0, 12)} (${latest.date})`)
  console.log(`    ${baseline}`)
  if (stamp && stamp !== latest.sha) {
    console.log('    → 有更新！')
    changed = true
    if (doUpdate) {
      console.log('    → 正在重新下载最新文件…')
      const r = await refreshDeepWhale()
      writeFileSync(DEEP_WHALE_STAMP, latest.sha + '\n')
      console.log(`    → 已更新 ${r.ok}/${r.total} 个文件（失败 ${r.failed.length}）`)
      if (r.failed.length > 0) console.log('      失败项: ' + r.failed.join('; '))
      console.log('    → 请重启 dsh web 使皮肤更新生效')
    } else {
      console.log('    → 执行 `node tools/check-skin-updates.mjs --update` 拉取最新文件')
    }
  } else if (!stamp) {
    // 首次运行：把当前 HEAD 记为基线（本次检查本身不重下文件）
    writeFileSync(DEEP_WHALE_STAMP, latest.sha + '\n')
    console.log('    → 已记录基线，当前为最新')
  } else {
    console.log('    → 已是最新')
  }
} catch (e) {
  console.log(`    ⚠ 检查失败: ${e.message}`)
}

// 2) @linxin666/*
console.log('\n[2] @linxin666/* 插件（dsh-web-ui 全家桶，npm 发布）')
const { deps, bundles } = readProfileDeps()
for (const short of LINXIN_PACKAGES) {
  const pkg = `@linxin666/${short}`
  try {
    const latest = await latestNpmVersion(pkg)
    const installed = installedVersion(pkg)
    const inProfile = deps[pkg] !== undefined || bundles.includes(pkg)
    if (!inProfile) {
      console.log(`    ${pkg}: 未安装（npm 最新 ${latest}）`)
      continue
    }
    if (installed === latest) {
      console.log(`    ${pkg}: 已安装 ${installed}，已是最新`)
    } else {
      console.log(`    ${pkg}: 已安装 ${installed ?? '?'} → npm 最新 ${latest}，有更新！`)
      changed = true
    }
  } catch (e) {
    console.log(`    ${pkg}: ⚠ 检查失败: ${e.message}`)
  }
}

// 3) dsh 本体
console.log('\n[3] DSH 本体（@deepseek-ai/dsh）')
try {
  const latest = await latestNpmVersion('@deepseek-ai/dsh')
  const { execFileSync } = await import('node:child_process')
  let local = '?'
  try {
    // Windows 上 dsh 是 .ps1/.cmd shim，经 cmd 执行以走 PATH 解析
    const cmd = process.platform === 'win32' ? 'cmd' : 'dsh'
    const argv = process.platform === 'win32' ? ['/c', 'dsh --version'] : ['--version']
    local = execFileSync(cmd, argv, { encoding: 'utf8' }).trim()
  } catch { /* dsh 不在 PATH 时忽略 */ }
  console.log(`    本机 ${local} → npm 最新 ${latest}`)
  if (local !== '?' && local !== latest) { console.log('    → DSH 有新版本！'); changed = true }
} catch (e) {
  console.log(`    ⚠ 检查失败: ${e.message}`)
}

console.log('\n' + (changed ? '结论：有可用更新。' : '结论：全部为最新。'))
console.log('提示：GitHub 上 star/watch 两个仓库（dsh-web-ui、dsh-deep-whale）可收到发布/提交通知；')
console.log('      npm 插件更新后需重启 dsh web 生效。')
