#!/usr/bin/env node
/**
 * wechat-article.ts —— 微信公众号文章解析器（单文件、零依赖）
 *
 * 给一个 mp.weixin.qq.com 的完整链接，抓取正文并输出为 Markdown / 纯文本 / JSON。
 *
 * 运行（Node >= 22.18 或 >= 23.6 可直接跑 .ts，无需 ts-node / tsx）:
 *   node wechat-article.ts "https://mp.weixin.qq.com/s/rO_FZWaISOpxMOxqTtK8KQ"
 *   node wechat-article.ts URL1 URL2 -o ./articles --format all --images
 *   node wechat-article.ts URL --stdout --format txt
 *
 * 旧版 Node（22.6 ~ 22.17）加参数: node --experimental-strip-types wechat-article.ts ...
 *
 * 设计要点:
 *   - 微信文章是服务端渲染的，正文原本就在 HTML 源码里，无需执行 JS，一次 fetch 即可。
 *   - 必须伪装手机 UA 并带 Referer，否则会被风控返回「环境异常」验证页。
 *   - 正文区块用 <div> 配对计数定位，非贪婪正则会截断。
 *   - 图片真实地址在 data-src，剥标签前必须先换成占位符，否则图片与图注一起丢失。
 */

import { parseArgs } from 'node:util';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';

const CST_OFFSET_SEC = 8 * 3600;

const UA =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) ' +
  'AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1';

const HEADERS: Record<string, string> = {
  'User-Agent': UA,
  Accept: 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
  'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.8',
  Referer: 'https://mp.weixin.qq.com/',
};

/** 命中任意一条即认为不是正常文章页 */
const ERROR_PATTERNS: Array<[string, string]> = [
  ['环境异常', '触发风控验证页（环境异常），稍后重试或更换网络/出口 IP'],
  ['该内容已被发布者删除', '文章已被发布者删除'],
  ['此内容因违规无法查看', '文章因违规无法查看'],
  ['此内容发送失败无法查看', '内容发送失败，无法查看'],
  ['该公众号已迁移', '该公众号已迁移，请用新链接'],
  ['当前内容已被举报', '内容已被举报，无法查看'],
  ['该内容暂时无法查看', '内容暂时无法查看'],
  ['参数错误', '链接参数错误或已失效'],
];

const IMG_TAG_RE = /<img\b[^>]*>/gi;
const DIV_TAG_RE = /<(\/?)div\b[^>]*>/gi;
const TAG_RE = /<[^>]+>/g;

interface ImageRef {
  index: number;
  url: string;
  type: string;
  local?: string;
}

interface Meta {
  url: string;
  title: string;
  account: string;
  author: string;
  digest: string;
  cover: string;
  publishTime: string;
  createTime: number | null;
  biz: string;
  mid: string;
  idx: string;
  sn: string;
  fetchedAt: string;
}

// --------------------------------------------------------------------------- //
// HTML 实体（零依赖，覆盖微信正文常见实体）
// --------------------------------------------------------------------------- //
const NAMED_ENTITIES: Record<string, string> = {
  amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: '\u00a0',
  mdash: '—', ndash: '–', hellip: '…', ldquo: '“', rdquo: '”',
  lsquo: '‘', rsquo: '’', middot: '·', bull: '•', dagger: '†',
  times: '×', plusmn: '±', minus: '−', deg: '°', frac12: '½',
  sup2: '²', sup3: '³', copy: '©', reg: '®', trade: '™',
  laquo: '«', raquo: '»', sect: '§', para: '¶', micro: 'µ', shy: '\u00ad',
  yen: '¥', euro: '€', pound: '£', cent: '¢', curren: '¤',
  ensp: ' ', emsp: ' ', thinsp: ' ', zwnj: '\u200c', zwj: '\u200d',
  alpha: 'α', beta: 'β', gamma: 'γ', delta: 'δ', epsilon: 'ε', theta: 'θ',
  lambda: 'λ', mu: 'μ', pi: 'π', rho: 'ρ', sigma: 'σ', tau: 'τ', phi: 'φ',
  omega: 'ω', Delta: 'Δ', Sigma: 'Σ', Omega: 'Ω',
  rarr: '→', larr: '←', harr: '↔', uarr: '↑', darr: '↓',
  infin: '∞', ne: '≠', le: '≤', ge: '≥', asymp: '≈', prime: '′',
  eacute: 'é', egrave: 'è', agrave: 'à', ccedil: 'ç',
  uuml: 'ü', ouml: 'ö', auml: 'ä', szlig: 'ß', ntilde: 'ñ',
};

function decodeEntities(input: string): string {
  return input
    .replace(/&#x([0-9a-f]+);/gi, (m, hex: string) => {
      const cp = Number.parseInt(hex, 16);
      return Number.isFinite(cp) ? String.fromCodePoint(cp) : m;
    })
    .replace(/&#(\d+);/g, (m, dec: string) => {
      const cp = Number.parseInt(dec, 10);
      return Number.isFinite(cp) ? String.fromCodePoint(cp) : m;
    })
    .replace(/&([a-z][a-z0-9]*);/gi, (m, name: string) => NAMED_ENTITIES[name] ?? m);
}

// --------------------------------------------------------------------------- //
// 抓取
// --------------------------------------------------------------------------- //
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

async function fetchText(url: string, retries: number, timeoutSec: number): Promise<string> {
  let lastErr: unknown = null;
  for (let attempt = 1; attempt <= retries; attempt++) {
    try {
      const res = await fetch(url, {
        headers: HEADERS,
        redirect: 'follow',
        signal: AbortSignal.timeout(timeoutSec * 1000),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status} ${res.statusText}`);
      return await res.text();
    } catch (err) {
      lastErr = err;
      if (attempt < retries) await sleep(1500 * attempt);
    }
  }
  throw new Error(`下载失败（已重试 ${retries} 次）：${String(lastErr)}`);
}

function checkPageError(html: string): string | null {
  for (const [needle, message] of ERROR_PATTERNS) {
    if (html.includes(needle)) return message;
  }
  return null;
}

// --------------------------------------------------------------------------- //
// 元信息
// --------------------------------------------------------------------------- //
function firstMatch(html: string, ...patterns: RegExp[]): string {
  for (const pat of patterns) {
    const m = html.match(pat);
    if (m?.[1]) {
      const val = decodeEntities(m[1]).replace(TAG_RE, '').replace(/\s+/g, ' ').trim();
      if (val) return val;
    }
  }
  return '';
}

function formatCST(ts: number): string {
  const d = new Date((ts + CST_OFFSET_SEC) * 1000);
  const p = (n: number) => String(n).padStart(2, '0');
  return (
    `${d.getUTCFullYear()}-${p(d.getUTCMonth() + 1)}-${p(d.getUTCDate())} ` +
    `${p(d.getUTCHours())}:${p(d.getUTCMinutes())}:${p(d.getUTCSeconds())}`
  );
}

function parseMeta(html: string, url: string): Meta {
  const parsed = new URL(url);
  const q = (key: string) => parsed.searchParams.get(key) ?? '';

  const title = firstMatch(
    html,
    /<meta\s+property="og:title"\s+content="([^"]*)"/i,
    /var\s+msg_title\s*=\s*['"]([\s\S]*?)['"]\s*;/i,
    /<h1[^>]*class="rich_media_title"[^>]*>([\s\S]*?)<\/h1>/i,
    /<h1[^>]*id="activity-name"[^>]*>([\s\S]*?)<\/h1>/i,
  );
  const account = firstMatch(
    html,
    /id="js_name"[^>]*>([\s\S]*?)</i,
    /var\s+nickname\s*=\s*['"]([^'"]*)['"]/i,
    /<meta\s+property="og:article:author"\s+content="([^"]*)"/i,
  );
  const author = firstMatch(html, /var\s+author\s*=\s*['"]([^'"]*)['"]/i) || account;
  const digest = firstMatch(
    html,
    /var\s+msg_desc\s*=\s*['"]([\s\S]*?)['"]\s*;/i,
    /<meta\s+name="description"\s+content="([^"]*)"/i,
    /<meta\s+property="og:description"\s+content="([^"]*)"/i,
  );
  const cover = firstMatch(
    html,
    /var\s+msg_cdn_url\s*=\s*['"]([^'"]*)['"]/i,
    /<meta\s+property="og:image"\s+content="([^"]*)"/i,
  );

  const tsRaw = firstMatch(
    html,
    /var\s+create_time\s*=\s*['"](\d+)['"]/i,
    /var\s+ct\s*=\s*['"](\d+)['"]/i,
  );
  const createTime = /^\d+$/.test(tsRaw) ? Number.parseInt(tsRaw, 10) : null;
  const publishTime = createTime
    ? formatCST(createTime)
    : firstMatch(html, /id="publish_time"[^>]*>([\s\S]*?)</i);

  const biz = firstMatch(html, /var\s+biz\s*=\s*['"]([^'"]+)['"]/i) || q('__biz');

  return {
    url,
    title,
    account,
    author,
    digest,
    cover,
    publishTime,
    createTime,
    biz,
    mid: q('mid'),
    idx: q('idx'),
    sn: q('sn'),
    fetchedAt: formatCST(Math.floor(Date.now() / 1000)),
  };
}

// --------------------------------------------------------------------------- //
// 正文
// --------------------------------------------------------------------------- //
/** 取出 id="js_content" 这个 div 的完整内容（按 div 配对计数，避免正则截断） */
function extractContentHtml(html: string): string {
  const open = /<div[^>]*id="js_content"/i.exec(html);
  if (open) {
    const start = open.index;
    let depth = 0;
    DIV_TAG_RE.lastIndex = start;
    let m: RegExpExecArray | null;
    while ((m = DIV_TAG_RE.exec(html)) !== null) {
      if (m[1] === '/') {
        depth -= 1;
        if (depth === 0) return html.slice(start, m.index + m[0].length);
      } else {
        depth += 1;
      }
    }
    return html.slice(start);
  }

  // 兜底：老版本页面结构
  const fallback = html.match(
    /<div[^>]*id="js_content"[^>]*>([\s\S]*?)<\/div>\s*(?:<script|<div)/i,
  );
  return fallback?.[1] ?? '';
}

function attrValue(tag: string, name: string): string {
  const m = tag.match(new RegExp(`${name}\\s*=\\s*"([^"]+)"`, 'i'));
  return m?.[1] ? decodeEntities(m[1].trim()) : '';
}

/** 把正文 HTML 转成纯文本，同时按出现顺序收集图片 */
function htmlToText(contentHtml: string): { text: string; images: ImageRef[] } {
  const images: ImageRef[] = [];

  let text = contentHtml.replace(IMG_TAG_RE, (tag) => {
    const src =
      attrValue(tag, 'data-src') || attrValue(tag, 'src') || attrValue(tag, 'data-original');
    if (!src) return '';
    images.push({ index: images.length + 1, url: src, type: attrValue(tag, 'data-type').toLowerCase() });
    return `\n\u0000IMG${images.length}\u0000\n`;
  });

  text = text
    .replace(/<br\s*\/?>/gi, '\n')
    .replace(/<\/(?:p|section|h[1-6]|li|blockquote|pre|tr|div|figure)\s*>/gi, '\n')
    .replace(/<hr\s*\/?>/gi, '\n')
    .replace(TAG_RE, '');
  text = decodeEntities(text)
    .replace(/[\u200b\ufeff]/g, '')
    .replace(/[ \t\u00a0]+/g, ' ');
  text = text
    .split('\n')
    .map((line) => line.replace(/\s+$/, ''))
    .join('\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();

  return { text, images };
}

// --------------------------------------------------------------------------- //
// 输出
// --------------------------------------------------------------------------- //
function sanitize(name: string, limit = 60): string {
  const cleaned = name
    .replace(/[\\/:*?"<>|\r\n\t]+/g, '_')
    .replace(/\s+/g, ' ')
    .replace(/_{2,}/g, '_')
    .replace(/^[ ._]+|[ ._]+$/g, '');
  return cleaned.slice(0, limit).replace(/[ ._]+$/g, '') || 'article';
}

function toMarkdown(meta: Meta, text: string, images: ImageRef[]): string {
  const lines: string[] = [`# ${meta.title || '(无标题)'}`, ''];
  if (meta.account) lines.push(`> **公众号**：${meta.account}`);
  if (meta.author && meta.author !== meta.account) lines.push(`> **作者**：${meta.author}`);
  if (meta.publishTime) lines.push(`> **发布时间**：${meta.publishTime}`);
  lines.push(`> **原文链接**：${meta.url}`);
  if (meta.digest) lines.push('', `> ${meta.digest}`);
  lines.push('', '---', '');

  let body = text;
  for (const img of images) {
    body = body.split(`\u0000IMG${img.index}\u0000`).join(`![图${img.index}](${img.local ?? img.url})`);
  }
  lines.push(body);
  return lines.join('\n').trim() + '\n';
}

const EXT_BY_TYPE: Record<string, string> = {
  png: '.png', gif: '.gif', webp: '.webp', jpeg: '.jpg', jpg: '.jpg',
  bmp: '.bmp', svg: '.svg', tiff: '.tiff',
};

async function downloadImage(url: string, dir: string, index: number): Promise<string | null> {
  try {
    const res = await fetch(url, {
      headers: { 'User-Agent': UA, Referer: 'https://mp.weixin.qq.com/' },
      signal: AbortSignal.timeout(20000),
    });
    if (!res.ok) return null;
    const ctype = (res.headers.get('content-type') ?? '').toLowerCase();
    const ext =
      Object.entries(EXT_BY_TYPE).find(([key]) => ctype.includes(key))?.[1] ?? '.jpg';
    const file = path.join(dir, `img_${String(index).padStart(2, '0')}${ext}`);
    await writeFile(file, Buffer.from(await res.arrayBuffer()));
    return file;
  } catch {
    return null;
  }
}

interface ParseResult {
  meta: Meta;
  text: string;
  images: ImageRef[];
  markdown: string;
  files: string[];
}

interface Options {
  outdir: string;
  format: string;
  images: boolean;
  stdout: boolean;
  retries: number;
  timeout: number;
}

async function parseOne(url: string, opts: Options): Promise<ParseResult> {
  const html = await fetchText(url, opts.retries, opts.timeout);

  const err = checkPageError(html);
  if (err) throw new Error(err);

  const meta = parseMeta(html, url);
  const contentHtml = extractContentHtml(html);
  if (!contentHtml) {
    throw new Error('页面里没找到 id="js_content" 正文区块，可能链接已失效或是非图文页面');
  }
  const { text, images } = htmlToText(contentHtml);
  if (!text.trim()) throw new Error('正文解析结果为空');

  const base = sanitize(meta.title || 'article');
  const outDir = path.join(opts.outdir, base);
  await mkdir(outDir, { recursive: true });

  if (opts.images && images.length) {
    const imgDir = path.join(outDir, 'images');
    await mkdir(imgDir, { recursive: true });
    for (const img of images) {
      const saved = await downloadImage(img.url, imgDir, img.index);
      if (saved) img.local = path.join('images', path.basename(saved));
    }
  }

  const markdown = toMarkdown(meta, text, images);
  const formats = opts.format === 'all' ? ['md', 'txt', 'json'] : [opts.format];
  const files: string[] = [];

  for (const fmt of formats) {
    const target = path.join(outDir, `${base}.${fmt}`);
    if (fmt === 'md') {
      await writeFile(target, markdown, 'utf-8');
    } else if (fmt === 'txt') {
      await writeFile(target, text, 'utf-8');
    } else if (fmt === 'json') {
      const payload = {
        ...meta,
        imageCount: images.length,
        images,
        contentText: text,
        contentMarkdown: markdown,
      };
      await writeFile(target, JSON.stringify(payload, null, 2), 'utf-8');
    } else {
      continue;
    }
    files.push(target);
  }

  return { meta, text, images, markdown, files };
}

// --------------------------------------------------------------------------- //
// CLI
// --------------------------------------------------------------------------- //
const HELP = `wechat-article.ts —— 微信公众号文章解析器

用法:
  node wechat-article.ts <链接> [更多链接...] [选项]

选项:
  -o, --outdir <dir>    输出目录（默认 ./articles）
  -f, --format <fmt>    md | txt | json | all（默认 md；all 表示三种全出）
      --images          额外下载正文图片到 images/ 并在 Markdown 中本地引用
      --stdout          同时把结果打印到终端
      --retries <n>     抓取失败重试次数（默认 3）
      --timeout <s>     单次请求超时秒数（默认 20）
  -h, --help            显示本帮助

示例:
  node wechat-article.ts "https://mp.weixin.qq.com/s/xxxx"
  node wechat-article.ts URL1 URL2 -o ./articles --format all --images
`;

async function main(): Promise<number> {
  let parsed;
  try {
    parsed = parseArgs({
      args: process.argv.slice(2),
      allowPositionals: true,
      options: {
        outdir: { type: 'string', short: 'o', default: './articles' },
        format: { type: 'string', short: 'f', default: 'md' },
        images: { type: 'boolean', default: false },
        stdout: { type: 'boolean', default: false },
        retries: { type: 'string', default: '3' },
        timeout: { type: 'string', default: '20' },
        help: { type: 'boolean', short: 'h', default: false },
      },
    });
  } catch (err) {
    process.stderr.write(`${String(err)}\n\n${HELP}`);
    return 2;
  }

  const { values, positionals } = parsed;
  if (values.help || positionals.length === 0) {
    process.stdout.write(HELP);
    return values.help ? 0 : 2;
  }

  const format = values.format as string;
  if (!['md', 'txt', 'json', 'all'].includes(format)) {
    process.stderr.write(`不支持的 --format: ${format}（可选 md | txt | json | all）\n`);
    return 2;
  }

  const opts: Options = {
    outdir: values.outdir as string,
    format,
    images: Boolean(values.images),
    stdout: Boolean(values.stdout),
    retries: Math.max(1, Number.parseInt(values.retries as string, 10) || 3),
    timeout: Math.max(1, Number.parseInt(values.timeout as string, 10) || 20),
  };
  await mkdir(opts.outdir, { recursive: true });

  let failures = 0;
  const urls = positionals as string[];
  for (let i = 0; i < urls.length; i++) {
    const url = urls[i];
    process.stderr.write(`[${i + 1}/${urls.length}] ${url}\n`);
    try {
      const result = await parseOne(url, opts);
      const { meta } = result;
      process.stderr.write(
        `    ✓ ${meta.title || '(无标题)'}\n` +
          `      公众号 ${meta.account || '未知'} | 作者 ${meta.author || '未知'} | ` +
          `时间 ${meta.publishTime || '未知'}\n` +
          `      正文 ${result.text.length} 字 | 图片 ${result.images.length} 张\n` +
          result.files.map((f) => `      → ${f}\n`).join(''),
      );
      if (opts.stdout) {
        process.stdout.write(opts.format === 'txt' ? result.text + '\n' : result.markdown);
      }
    } catch (err) {
      failures += 1;
      process.stderr.write(`    ✗ 失败：${err instanceof Error ? err.message : String(err)}\n`);
    }
  }

  return failures > 0 ? 1 : 0;
}

main().then(
  (code) => process.exit(code),
  (err) => {
    process.stderr.write(`意外错误：${err instanceof Error ? err.stack : String(err)}\n`);
    process.exit(1);
  },
);
