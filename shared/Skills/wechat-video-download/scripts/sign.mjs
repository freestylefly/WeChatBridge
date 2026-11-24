// 为 sph.miuistore.com /sph/public/quick 接口生成 _sign。
// 在 Node vm 沙箱里跑站点原版 enc.js（jsvmp），用法见 save_video.py。
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import crypto from 'node:crypto';
import { performance } from 'node:perf_hooks';
import { TextEncoder, TextDecoder } from 'node:util';
import { fileURLToPath } from 'node:url';

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const ENC_JS = path.join(ROOT, 'enc.js');
const ENC_JS_URL = 'https://sph.miuistore.com/plugins/sph/enc.js';
const UA = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36';

function elStub() {
  return {
    style: {}, appendChild() {}, removeChild() {}, getAttribute() { return null; }, setAttribute() {},
    querySelector() { return null; }, querySelectorAll() { return []; },
    addEventListener() {}, removeEventListener() {}, parentNode: null, childNodes: []
  };
}

function canvasStub() {
  const ctx = {
    fillStyle: '', font: '', textBaseline: '',
    fillRect() {}, strokeRect() {}, fillText() {}, strokeText() {},
    arc() {}, beginPath() {}, closePath() {}, fill() {}, stroke() {},
    getImageData(x, y, w, h) { return { data: new Uint8ClampedArray(w * h * 4), width: w, height: h }; },
    quadraticCurveTo() {}, bezierCurveTo() {}, lineTo() {}, moveTo() {},
    ellipse() {}, rect() {}, clip() {}, rotate() {}, translate() {}, scale() {}, transform() {}, setTransform() {},
    measureText() { return { width: 100 }; },
    createLinearGradient() { return { addColorStop() {} }; },
    createRadialGradient() { return { addColorStop() {} }; },
    drawImage() {}, putImageData() {}, clearRect() {}, save() {}, restore() {}
  };
  return {
    width: 200, height: 40,
    getContext() { return ctx; },
    toDataURL() { return 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=='; }
  };
}

function storage() {
  const s = {};
  return {
    getItem(k) { return k in s ? s[k] : null; },
    setItem(k, v) { s[k] = String(v); },
    removeItem(k) { delete s[k]; },
    clear() { for (const k in s) delete s[k]; },
    key(i) { return Object.keys(s)[i] || null; },
    get length() { return Object.keys(s).length; }
  };
}

function buildSandbox() {
  const sandbox = {
    console,
    setTimeout, setInterval, clearTimeout, clearInterval,
    Date,
    crypto: {
      getRandomValues(arr) { const b = crypto.randomBytes(arr.length); arr.set(b); return arr; },
      subtle: crypto.webcrypto.subtle,
      randomUUID() { return crypto.randomUUID(); }
    },
    performance,
    TextEncoder, TextDecoder,
    atob: (s) => Buffer.from(s, 'base64').toString('binary'),
    btoa: (s) => Buffer.from(s, 'binary').toString('base64'),
    document: {
      querySelector() { return null; }, querySelectorAll() { return []; },
      createElement(tag) { return tag === 'canvas' ? canvasStub() : elStub(); },
      createEvent() { return { initEvent() {} }; },
      documentElement: elStub(), body: elStub(), head: elStub(),
      addEventListener() {}, removeEventListener() {}, cookie: ''
    },
    location: {
      href: 'https://sph.miuistore.com/', protocol: 'https:', hostname: 'sph.miuistore.com',
      host: 'sph.miuistore.com', port: '', origin: 'https://sph.miuistore.com',
      pathname: '/', search: '', hash: '', replace() {}
    },
    navigator: {
      userAgent: UA, platform: 'MacIntel', language: 'zh-CN', languages: ['zh-CN'],
      cookieEnabled: true, hardwareConcurrency: 8, deviceMemory: 8, maxTouchPoints: 0
    },
    localStorage: storage(),
    sessionStorage: storage()
  };
  sandbox.window = sandbox;
  sandbox.globalThis = sandbox;
  sandbox.self = sandbox;
  return sandbox;
}

async function ensureEncJs() {
  if (fs.existsSync(ENC_JS)) return;
  const resp = await fetch(ENC_JS_URL, { headers: { 'User-Agent': UA } });
  const text = await resp.text();
  if (!text.includes('AlgoSign')) throw new Error('enc.js 下载内容异常，源站可能已改版');
  fs.writeFileSync(ENC_JS, text);
}

async function main() {
  const id = process.argv[2];
  if (!id) { console.error('usage: node sign.mjs <content_id>'); process.exit(2); }
  await ensureEncJs();
  const sandbox = buildSandbox();
  vm.createContext(sandbox);
  vm.runInContext(fs.readFileSync(ENC_JS, 'utf8'), sandbox, { filename: 'enc.js' });
  if (typeof sandbox.AlgoSign !== 'function') throw new Error('enc.js 加载失败：未找到 AlgoSign');
  const r = await new sandbox.AlgoSign({ appId: 'sph' }).sign({ id, path: '/sph/public/quick' });
  if (!r || !r._sign) throw new Error('签名生成失败');
  process.stdout.write(r._sign);
}

main().catch(e => { console.error('sign 失败:', e.message); process.exit(1); });
