// 契约冒烟测试：用与浏览器完全相同的 js/api.js，对着契约 mock 跑全流程。
//
// 用法（二选一）：
//   1) 自带 mock：   WCB_MOCK_SCRIPT=../server/mock/server.js node test/smoke.mjs
//      （脚本会在空闲端口上拉起 mock、跑完即关；server/mock/ 在 A 合主干后存在，
//        此前可从 feat/server-convergence 分支取该文件）
//   2) 外挂 mock：   node server/mock/server.js --port 8901   # 另开一个终端
//      node test/smoke.mjs                                   # 打 config.baseURL
//
// 只测契约面：状态码、字段名、错误信封、排序、幂等。mock 专属端点
// （/mock/reset、/mock/scan）仅在本测试中使用，console 本身不依赖。
import { spawn } from "node:child_process";
import cfg from "../js/config.js";
import wcb from "../js/api.js";

const { ConsoleApi, ApiError, NetworkError } = wcb;

let passed = 0;
let failed = 0;
const failures = [];

function check(name, cond, detail) {
  if (cond) {
    passed++;
    console.log("  ✓ " + name);
  } else {
    failed++;
    failures.push(name + (detail ? " — " + detail : ""));
    console.log("  ✗ " + name + (detail ? " — " + detail : ""));
  }
}

async function expectApiError(name, fn, status, code) {
  try {
    await fn();
    check(name, false, "期望 ApiError " + status + "，实际成功了");
  } catch (err) {
    if (!(err instanceof ApiError)) {
      check(name, false, "期望 ApiError，实际 " + err.name + ": " + err.message);
      return;
    }
    check(name, err.status === status && (!code || err.code === code),
      "实际 " + err.status + " " + err.code + " · " + err.message);
  }
}

async function waitHealth(baseURL, attempts = 40) {
  for (let i = 0; i < attempts; i++) {
    try {
      const res = await fetch(baseURL + "/healthz");
      if (res.ok) return;
    } catch (_) { /* 还没起来 */ }
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error("mock 未在 " + baseURL + " 就绪");
}

async function main() {
  // ---------- 起服务（或确认外挂 mock 可达） ----------
  let child = null;
  let baseURL = cfg.baseURL;
  if (process.env.WCB_MOCK_SCRIPT) {
    const port = 8909;
    baseURL = "http://127.0.0.1:" + port;
    child = spawn(process.execPath, [process.env.WCB_MOCK_SCRIPT, "--port", String(port)], {
      stdio: ["ignore", "pipe", "pipe"],
    });
    child.stdout.on("data", () => {});
    child.stderr.on("data", (d) => process.stderr.write(d));
    await waitHealth(baseURL);
    console.log("mock 已拉起：" + baseURL);
  } else {
    try {
      await waitHealth(baseURL, 4);
      console.log("使用外挂 mock：" + baseURL);
    } catch (_) {
      console.error("未找到可用的契约 mock。请先启动：node server/mock/server.js --port 8901");
      console.error("或用 WCB_MOCK_SCRIPT=<mock路径> node test/smoke.mjs 让本脚本自行拉起。");
      process.exit(2);
    }
  }

  const api = new ConsoleApi(baseURL);
  const reset = () => fetch(baseURL + "/mock/reset", { method: "POST" }).then((r) => r.json());

  try {
    // ---------- 健康检查与未鉴权 ----------
    console.log("\n[1] 健康检查 / 未携令牌");
    await reset();
    const health = await api.health();
    check("GET /healthz → {status:ok}", health.status === "ok");
    await expectApiError("无令牌调 admin 列表 → 401 unauthorized",
      () => api.admin.listRequests("pending"), 401, "unauthorized");

    // ---------- 扫码登录全流程 ----------
    console.log("\n[2] 微信扫码登录（auth 冻结端点）");
    const sess = await api.auth.createSession("wcbadmin-smoke", "admin-console");
    check("POST /v1/auth/session → ticket",
      typeof sess.ticket === "string" && sess.ticket.length > 0);
    check("响应含 authorizeUrl / qrImageUrl / expiresAt",
      !!sess.authorizeUrl && !!sess.qrImageUrl && !!sess.expiresAt);

    const qr = await api.auth.qrBlob(sess.ticket);
    const magic = new Uint8Array((await qr.arrayBuffer()).slice(0, 4));
    check("QR 端点是合法 PNG", magic[0] === 0x89 && magic[1] === 0x50 && magic[2] === 0x4e && magic[3] === 0x47);

    const pollPending = await api.auth.pollSession(sess.ticket);
    check("轮询 → pending", pollPending.status === "pending");

    // /mock/scan 是 mock 专属：代替「卖家拿起微信扫了一下」。
    const scan = await fetch(baseURL + "/mock/scan/" + sess.ticket, { method: "POST" }).then((r) => r.json());
    check("mock 扫码后会话 authorized", scan.status === "authorized");

    const pollDone = await api.auth.pollSession(sess.ticket);
    check("轮询 → authorized 且带双令牌",
      pollDone.status === "authorized" && !!pollDone.accessToken && !!pollDone.refreshToken && !!pollDone.account);
    api.setTokens({
      accessToken: pollDone.accessToken,
      refreshToken: pollDone.refreshToken,
      expiresAt: pollDone.expiresAt,
      account: pollDone.account,
    });

    // ---------- 待确认列表 ----------
    console.log("\n[3] 待确认列表");
    const pending = await api.admin.listRequests("pending");
    const items = pending.items || [];
    check("种子含 2 条 pending", items.length === 2, "实际 " + items.length);
    check("全部为 pending 且按时间倒序",
      items.every((i) => i.status === "pending") &&
      items.every((i, idx) => idx === 0 || new Date(items[idx - 1].createdAt) >= new Date(i.createdAt)));
    check("item 字段齐全（requestId/installId/anchorDigest/nickname/amountFen/createdAt）",
      items.every((i) => i.requestId && i.installId && "anchorDigest" in i && "nickname" in i && "amountFen" in i && i.createdAt));

    // ---------- 确认签发（含幂等） ----------
    console.log("\n[4] 确认签发");
    const target = items[1];
    const confirmed = await api.admin.confirm(target.requestId);
    check("confirm → licenseId（lic_ + 12 hex）",
      /^lic_[0-9a-f]{12}$/.test(confirmed.licenseId || ""), String(confirmed.licenseId));

    const again = await api.admin.confirm(target.requestId);
    check("重复 confirm 幂等（同一 licenseId）", again.licenseId === confirmed.licenseId);

    await expectApiError("confirm 未知 requestId → 404 not_found",
      () => api.admin.confirm("act_deadbeef"), 404, "not_found");

    // ---------- 换机重签 / 作废 ----------
    console.log("\n[5] 换机重签 / 作废");
    const reissued = await api.admin.reissue(items[0].installId);
    check("reissue → licenseId", /^lic_[0-9a-f]{12}$/.test(reissued.licenseId || ""));

    const revoked = await api.admin.revoke(confirmed.licenseId);
    check("revoke → 200 空 payload", revoked && Object.keys(revoked).length === 0);

    await expectApiError("revoke 未知 licenseId → 404",
      () => api.admin.revoke("lic_deadbeef"), 404, "not_found");

    // ---------- 台账视图 ----------
    console.log("\n[6] 台账与全量列表");
    const ledger = await api.admin.listRequests("confirmed");
    check("confirmed 列表含本次签发",
      (ledger.items || []).some((i) => i.requestId === target.requestId && i.status === "confirmed"));
    const all = await api.admin.listRequests("");
    check("全量列表 3 条（2 种子 + 1 新登记……含已确认）",
      (all.items || []).length === 3, "实际 " + (all.items || []).length);

    // ---------- 错误信封形状 ----------
    console.log("\n[7] 错误信封");
    try {
      await api.admin.confirm("act_deadbeef");
    } catch (err) {
      check("错误信封带 requestId（排查钥匙）", !!err.requestId);
    }

    // ---------- 网络错误态 ----------
    console.log("\n[8] 网络错误态");
    const dead = new ConsoleApi("http://127.0.0.1:9");
    try {
      await dead.admin.listRequests("pending");
      check("死端口 → NetworkError", false, "实际未抛错");
    } catch (err) {
      check("死端口 → NetworkError", err instanceof NetworkError, err.name);
    }

    // ---------- 汇总 ----------
    console.log("\n========================================");
    console.log("通过 " + passed + " 项，失败 " + failed + " 项");
    if (failed) {
      failures.forEach((f) => console.log("  ✗ " + f));
      process.exitCode = 1;
    } else {
      console.log("出口达标：登录 → 列表 → 确认（幂等）→ 重签 → 作废 → 台账 全流程可走");
    }
  } finally {
    if (child) child.kill();
  }
}

main().catch((err) => {
  console.error("冒烟测试自身崩溃：" + (err && err.stack ? err.stack : err));
  process.exit(1);
});
