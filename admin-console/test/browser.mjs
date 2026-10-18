// 浏览器级全流程验证（可选工具，console 本身零依赖）。
//
// 依赖：playwright-core（已装于本机 node workspace）+ 系统 Chrome。
// 运行：先起契约 mock（node server/mock/server.js --port 8901），再：
//   NODE_PATH=C:/Users/Admin/.workbuddy/binaries/node/workspace/node_modules \
//   node test/browser.mjs
//
// 验证的是 smoke.mjs 覆盖不到的另一半：DOM 接线、渲染、弹窗、三态 toast。
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { createRequire } from "node:module";

// ESM import 不认 NODE_PATH，用 createRequire 走 CJS 解析找到 playwright-core。
const require = createRequire(import.meta.url);
const { chromium } = require("playwright-core");

const here = path.dirname(fileURLToPath(import.meta.url));
const consoleDir = path.resolve(here, "..");
const pageURL = "file:///" + consoleDir.replace(/\\/g, "/") + "/index.html";
const MOCK = "http://127.0.0.1:8901";

let passed = 0, failed = 0;
function check(name, cond, detail) {
  if (cond) { passed++; console.log("  ✓ " + name); }
  else { failed++; console.log("  ✗ " + name + (detail ? " — " + detail : "")); }
}

async function main() {
  // mock 必须已在 8901 就绪（脚本不打 /mock/reset 之外的假设）。
  const health = await fetch(MOCK + "/healthz").then((r) => r.json()).catch(() => null);
  if (!health || health.status !== "ok") {
    console.error("契约 mock 未就绪，请先：node server/mock/server.js --port 8901");
    process.exit(2);
  }
  await fetch(MOCK + "/mock/reset", { method: "POST" });

  const browser = await chromium.launch({ channel: "chrome", headless: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });

  const pageErrors = [];
  page.on("pageerror", (e) => pageErrors.push(String(e)));

  try {
    console.log("[1] 登录视图（微信扫码）");
    await page.goto(pageURL);
    await page.waitForSelector("#login-qr:not([hidden])", { timeout: 8000 });
    const qrSrc = await page.getAttribute("#login-qr", "src");
    check("二维码已加载（QR 端点）", /\/v1\/auth\/session\/[0-9a-f]+\/qr$/.test(qrSrc || ""), qrSrc);
    check("登录状态提示等待扫码", (await page.textContent("#login-status")).includes("等待微信扫码"));
    check("安装标识已生成本地", /^wcbadmin-[0-9a-f]{32}$/.test(await page.textContent("#login-install-id")));
    await page.screenshot({ path: path.join(here, "shot-login.png") });

    console.log("\n[2] 模拟卖家扫码 → 进入应用");
    const ticket = /\/v1\/auth\/session\/([0-9a-f]+)\/qr$/.exec(qrSrc)[1];
    const scan = await fetch(MOCK + "/mock/scan/" + ticket, { method: "POST" }).then((r) => r.json());
    check("mock 扫码授权成功", scan.status === "authorized");
    await page.waitForSelector("#view-app:not([hidden])", { timeout: 8000 });
    check("应用视图已展示", await page.isVisible("#view-app"));
    check("当前卖家身份已显示", (await page.textContent("#current-account")).includes("已登录"));

    console.log("\n[3] 待确认列表");
    await page.waitForFunction(() => document.querySelectorAll("#pending-body tr:not(.row-empty)").length > 0, null, { timeout: 8000 });
    check("徽标计数 = 2", (await page.textContent("#pending-count")) === "2");
    check("表格 2 行", (await page.locator("#pending-body tr:not(.row-empty)").count()) === 2);
    const firstRowText = await page.textContent("#pending-body tr:not(.row-empty)");
    check("行内含机器码/金额/操作", firstRowText.includes("9f2c1d4e7a3b5c60") && firstRowText.includes("¥38.00"));
    await page.screenshot({ path: path.join(here, "shot-pending.png") });

    console.log("\n[4] 确认签发（弹窗防误点 → 成功态）");
    await page.locator("#pending-body tr:not(.row-empty)").first().getByRole("button", { name: "确认签发" }).click();
    await page.waitForSelector("#modal-mask:not([hidden])", { timeout: 4000 });
    check("确认弹窗展示目标事实", (await page.textContent("#modal-body")).includes("9f2c1d4e7a3b5c60"));
    await page.click("#modal-ok");
    await page.waitForSelector(".toast-ok", { timeout: 8000 });
    const toastText = await page.textContent(".toast-ok");
    check("成功 toast 含许可证号", /lic_[0-9a-f]{12}/.test(toastText), toastText);
    await page.waitForFunction(() => document.getElementById("pending-count").textContent === "1", null, { timeout: 8000 });
    check("确认后列表自动收敛为 1", (await page.textContent("#pending-count")) === "1");

    console.log("\n[5] 换机重签（剩余登记）");
    await page.locator("#pending-body tr:not(.row-empty)").first().getByRole("button", { name: "换机重签" }).click();
    await page.waitForSelector("#modal-mask:not([hidden])", { timeout: 4000 });
    await page.click("#modal-ok");
    await page.waitForSelector(".toast-ok >> nth=1", { timeout: 8000 });
    check("重签成功 toast", (await page.textContent(".toast-ok >> nth=1")).includes("已换机重签"));

    console.log("\n[6] 已签发台账（本地日志补许可证号列）");
    await page.click("#tab-ledger");
    await page.waitForFunction(() => document.querySelectorAll("#ledger-body tr:not(.row-empty)").length > 0, null, { timeout: 8000 });
    const ledgerText = await page.textContent("#ledger-table");
    check("台账含本机签发的许可证号", /lic_[0-9a-f]{12}/.test(ledgerText), ledgerText.slice(0, 200));
    check("本地操作日志已记录 2 条（确认 + 重签）", (await page.locator("#action-log li").count()) === 2);
    check("日志含两类动作标签",
      (await page.textContent("#action-log")).includes("确认签发") &&
      (await page.textContent("#action-log")).includes("换机重签"));
    await page.screenshot({ path: path.join(here, "shot-ledger.png") });

    console.log("\n[7] 页面无 JS 异常");
    check("无 pageerror", pageErrors.length === 0, pageErrors.join("; "));
  } finally {
    await browser.close();
  }

  console.log("\n========================================");
  console.log("通过 " + passed + " 项，失败 " + failed + " 项");
  if (failed) process.exitCode = 1;
}

main().catch((err) => {
  console.error("浏览器验证崩溃：" + (err && err.stack ? err.stack : err));
  process.exit(1);
});
