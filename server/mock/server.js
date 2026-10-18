#!/usr/bin/env node
// Mock of the converged buyout backend (docs/design/buyout-flow-convergence.zh-CN.md §11.3).
//
// Purpose: unblock the admin-console and client-license-gate branches on day one.
// Every response shape here mirrors the real server byte for byte — success
// payloads are written unwrapped (httpx.OK is c.JSON(payload)), errors wear the
// { error: { code, message, request_id } } envelope with an X-Request-Id header
// (httpx.Fail). When the real activation/admin handlers land on
// feat/server-convergence, this file is deleted; until then it is the contract
// made runnable.
//
// What is real and what is not:
//   real  — every route, field name, status code, error envelope, timestamp
//           layout ("2006-01-02T15:04:05Z" equivalent), base64url licence token
//   mock  — signatures are placeholders (the licence envelope will NOT verify
//           against the client's embedded public key), the QR PNG is a
//           QR-shaped placeholder (valid PNG, not scannable), any non-empty
//           bearer token / admin password is accepted
//
// Usage: node server/mock/server.js [--port 8901]
// No dependencies. Node 18+.

"use strict";

const http = require("http");
const crypto = require("crypto");
const zlib = require("zlib");

const PORT = parsePort(process.argv) ?? Number(process.env.PORT ?? 8901);

function parsePort(argv) {
  const i = argv.indexOf("--port");
  return i >= 0 && argv[i + 1] ? Number(argv[i + 1]) : null;
}

// ---------------------------------------------------------------------------
// Wire helpers — the shapes are contract, copy them exactly.
// ---------------------------------------------------------------------------

const STAMP_LAYOUT_ISO = (t) => new Date(t).toISOString().replace(/\.\d{3}Z$/, "Z");

function requestId() {
  return crypto.randomBytes(12).toString("hex");
}

function sendJSON(res, status, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": Buffer.byteLength(body),
    "X-Request-Id": res.locals?.requestId ?? requestId(),
  });
  res.end(body);
}

function sendError(res, status, code, message) {
  const rid = res.locals?.requestId ?? requestId();
  const body = JSON.stringify({
    error: { code, message, request_id: rid },
  });
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": Buffer.byteLength(body),
    "X-Request-Id": rid,
  });
  res.end(body);
}

// The admin console is a static page opened from file:// or another localhost
// port; without this the browser kills every call before it is made.
function cors(res) {
  res.setHeader("Access-Control-Allow-Origin", "*");
  res.setHeader("Access-Control-Allow-Methods", "GET,POST,DELETE,OPTIONS");
  res.setHeader(
    "Access-Control-Allow-Headers",
    "Content-Type, Authorization, X-Admin-Password, X-Request-Id"
  );
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => {
      const raw = Buffer.concat(chunks).toString("utf8");
      if (!raw) return resolve({});
      try {
        resolve(JSON.parse(raw));
      } catch {
        reject(new Error("malformed json"));
      }
    });
    req.on("error", reject);
  });
}

const hex = (n) => crypto.randomBytes(n).toString("hex");
const now = () => Date.now();
const stamp = (ms) => STAMP_LAYOUT_ISO(ms);

// ---------------------------------------------------------------------------
// In-memory state. Wiped by POST /mock/reset.
// ---------------------------------------------------------------------------

const state = {
  sessions: new Map(), // ticket -> session
  accounts: new Map(), // accountId -> account
  tokens: new Map(), // accessToken -> accountId
  activations: new Map(), // requestId -> activation request
  licences: new Map(), // licenseId -> licence
  requestsByAccount: new Map(), // accountId -> [requestId]
};

function seed() {
  state.sessions.clear();
  state.accounts.clear();
  state.tokens.clear();
  state.activations.clear();
  state.licences.clear();
  state.requestsByAccount.clear();

  const demo = {
    id: "acc_demo0001",
    nickname: "演示买家",
    avatarUrl: "",
    plan: "buyout",
    createdAt: now() - 86_400_000,
    lastLoginAt: now() - 3_600_000,
    wechatBound: true,
  };
  state.accounts.set(demo.id, demo);

  // Two pending requests so the admin list has content on first load, and one
  // already-confirmed request so the ledger page is not empty either.
  const pending = [
    { installId: "9f2c1d4e7a3b5c60", anchor: "sha256:8c1f…a31d", fen: 3800, ago: 20 * 60_000 },
    { installId: "4b8e0f2a6d9c1375", anchor: "sha256:55aa…90e2", fen: 3800, ago: 55 * 60_000 },
  ];
  for (const p of pending) {
    const requestId = "act_" + hex(6);
    state.activations.set(requestId, {
      requestId,
      accountId: demo.id,
      installId: p.installId,
      anchorDigest: p.anchor,
      amountFen: p.fen,
      status: "pending",
      createdAt: now() - p.ago,
    });
    pushRequest(demo.id, requestId);
  }

  const confirmedId = "act_" + hex(6);
  const licenseId = "lic_" + hex(6);
  state.activations.set(confirmedId, {
    requestId: confirmedId,
    accountId: demo.id,
    installId: "1c3d5f7a9b0e2d48",
    anchorDigest: "sha256:71bb…4c09",
    amountFen: 3800,
    status: "confirmed",
    createdAt: now() - 3 * 86_400_000,
    licenseId,
  });
  state.licences.set(licenseId, {
    licenseId,
    installId: "1c3d5f7a9b0e2d48",
    requestId: confirmedId,
    revoked: false,
    issuedAt: now() - 3 * 86_400_000,
  });
  pushRequest(demo.id, confirmedId);
}

function pushRequest(accountId, requestId) {
  const list = state.requestsByAccount.get(accountId) ?? [];
  list.push(requestId);
  state.requestsByAccount.set(accountId, list);
}

seed();

// ---------------------------------------------------------------------------
// Auth (frozen contract — internal/auth/http.go). Field names must not drift.
// ---------------------------------------------------------------------------

const SESSION_TTL_MS = 5 * 60_000;
const TOKEN_TTL_MS = 60 * 60_000;

function issueTokens(accountId) {
  const accessToken = "mock_at_" + hex(16);
  const refreshToken = "mock_rt_" + hex(16);
  state.tokens.set(accessToken, accountId);
  return { accessToken, refreshToken, expiresAt: now() + TOKEN_TTL_MS };
}

function accountView(a) {
  return {
    id: a.id,
    nickname: a.nickname,
    avatarUrl: a.avatarUrl,
    plan: a.plan,
    createdAt: stamp(a.createdAt),
    lastLoginAt: a.lastLoginAt ? stamp(a.lastLoginAt) : null,
    wechatBound: a.wechatBound,
  };
}

async function createSession(req, res) {
  const body = await readBody(req).catch(() => ({}));
  const ticket = hex(16);
  state.sessions.set(ticket, {
    ticket,
    installId: body.installId ?? "",
    deviceId: body.deviceId ?? "",
    status: "pending",
    expiresAt: now() + SESSION_TTL_MS,
    accountId: null,
    tokens: null,
  });
  sendJSON(res, 201, {
    ticket,
    authorizeUrl:
      "https://open.weixin.qq.com/connect/qrconnect?appid=mock&redirect_uri=https%3A%2F%2Fmock.invalid%2Fv1%2Fauth%2Fwechat%2Fcallback&response_type=code&scope=snsapi_login&state=" +
      ticket,
    qrImageUrl: "/v1/auth/session/" + ticket + "/qr",
    expiresAt: stamp(now() + SESSION_TTL_MS),
  });
}

async function pollSession(req, res, ticket) {
  const s = state.sessions.get(ticket);
  if (!s) {
    return sendError(res, 500, "internal", "服务器内部错误，请稍后重试。");
  }
  if (s.status === "pending" && now() > s.expiresAt) {
    s.status = "expired";
  }
  if (s.status === "pending") {
    return sendJSON(res, 200, { status: "pending", expiresAt: stamp(s.expiresAt) });
  }
  if (s.status === "expired") {
    return sendJSON(res, 200, { status: "expired", expiresAt: stamp(s.expiresAt) });
  }
  const a = state.accounts.get(s.accountId);
  return sendJSON(res, 200, {
    status: "authorized",
    accessToken: s.tokens.accessToken,
    refreshToken: s.tokens.refreshToken,
    expiresAt: stamp(s.tokens.expiresAt),
    account: accountView(a),
  });
}

function qrPNG(ticket) {
  // A QR-shaped placeholder: three finder patterns, timing rows, and modules
  // seeded from the ticket. Valid PNG bytes — the client's magic-byte check
  // passes — but it does not scan. The real QR is rendered by the real server
  // from the WeChat authorize URL.
  const modules = 25;
  const scale = 8;
  const quiet = 2;
  const size = (modules + quiet * 2) * scale;
  const px = Buffer.alloc(size * size * 4, 0xff);

  let seed = 0;
  for (const b of Buffer.from(ticket)) seed = (seed * 31 + b) >>> 0;
  const rnd = () => {
    seed = (seed * 1664525 + 1013904223) >>> 0;
    return seed / 0xffffffff;
  };

  const at = (x, y, dark) => {
    if (x < 0 || y < 0 || x >= modules || y >= modules) return;
    const v = dark ? 0x00 : 0xff;
    for (let dy = 0; dy < scale; dy++) {
      for (let dx = 0; dx < scale; dx++) {
        const cx = (x + quiet) * scale + dx;
        const cy = (y + quiet) * scale + dy;
        const i = (cy * size + cx) * 4;
        px[i] = v;
        px[i + 1] = v;
        px[i + 2] = v;
        px[i + 3] = 0xff;
      }
    }
  };

  const inFinder = (x, y) =>
    (x < 7 && y < 7) || (x >= modules - 7 && y < 7) || (x < 7 && y >= modules - 7);

  for (let y = 0; y < modules; y++) {
    for (let x = 0; x < modules; x++) {
      if (inFinder(x, y)) continue;
      at(x, y, rnd() > 0.52);
    }
  }
  for (const [ox, oy] of [
    [0, 0],
    [modules - 7, 0],
    [0, modules - 7],
  ]) {
    for (let y = 0; y < 7; y++) {
      for (let x = 0; x < 7; x++) {
        const edge = x === 0 || y === 0 || x === 6 || y === 6;
        const core = x >= 2 && x <= 4 && y >= 2 && y <= 4;
        at(ox + x, oy + y, edge || core);
      }
    }
  }
  for (let i = 8; i < modules - 8; i++) {
    at(i, 6, i % 2 === 0);
    at(6, i, i % 2 === 0);
  }

  // PNG: signature + IHDR + IDAT(zlib of raw scanlines) + IEND.
  const raw = Buffer.alloc(size * (size + 1));
  for (let y = 0; y < size; y++) {
    raw[y * (size + 1)] = 0;
    px.copy(raw, y * (size + 1) + 1, y * size * 4, (y + 1) * size * 4);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // colour type RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    pngChunk("IHDR", ihdr),
    pngChunk("IDAT", zlib.deflateSync(raw)),
    pngChunk("IEND", Buffer.alloc(0)),
  ]);
}

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (const b of buf) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([len, body, crc]);
}

async function revokeSession(req, res) {
  await readBody(req).catch(() => ({}));
  res.writeHead(204);
  res.end();
}

async function refreshSession(req, res) {
  const body = await readBody(req).catch(() => ({}));
  // The mock has no refresh-token chain to rotate; it hands out a fresh pair
  // the way RefreshRotated does, which is what the client's happy path needs.
  const accessToken = "mock_at_" + hex(16);
  const accountId = state.accounts.keys().next().value;
  state.tokens.set(accessToken, accountId);
  sendJSON(res, 200, {
    status: "authorized",
    accessToken,
    refreshToken: body.refreshToken ?? "mock_rt_" + hex(16),
    expiresAt: stamp(now() + TOKEN_TTL_MS),
    account: accountView(state.accounts.get(accountId)),
  });
}

// ---------------------------------------------------------------------------
// Activation (new contract — §11.3).
// ---------------------------------------------------------------------------

function bearerAccount(req) {
  const raw = req.headers.authorization ?? "";
  const m = /^bearer\s+(.+)$/i.exec(raw);
  if (!m) return null;
  return state.tokens.get(m[1].trim()) ?? null;
}

function latestRequest(accountId) {
  const list = state.requestsByAccount.get(accountId) ?? [];
  for (let i = list.length - 1; i >= 0; i--) {
    return state.activations.get(list[i]);
  }
  return null;
}

async function activationRequest(req, res) {
  const accountId = bearerAccount(req);
  if (!accountId) {
    return sendError(res, 401, "unauthorized", "缺少访问令牌。");
  }
  const body = await readBody(req).catch(() => ({}));
  if (!body.installId) {
    return sendError(res, 400, "bad_request", "请求体格式不正确。");
  }
  const requestId = "act_" + hex(6);
  state.activations.set(requestId, {
    requestId,
    accountId,
    installId: body.installId,
    anchorDigest: body.anchorDigest ?? "",
    amountFen: body.amountFen ?? null,
    status: "pending",
    createdAt: now(),
  });
  pushRequest(accountId, requestId);
  sendJSON(res, 201, { requestId, status: "pending" });
}

async function activationStatus(req, res) {
  const accountId = bearerAccount(req);
  if (!accountId) {
    return sendError(res, 401, "unauthorized", "缺少访问令牌。");
  }
  const r = latestRequest(accountId);
  if (!r) {
    return sendError(res, 404, "not_found", "请求的内容不存在。");
  }
  const out = { status: r.status };
  if (r.licenseId) out.licenseId = r.licenseId;
  sendJSON(res, 200, out);
}

async function activationLicence(req, res) {
  const accountId = bearerAccount(req);
  if (!accountId) {
    return sendError(res, 401, "unauthorized", "缺少访问令牌。");
  }
  const r = latestRequest(accountId);
  if (!r || r.status !== "confirmed" || !r.licenseId) {
    // Mock-chosen status for "not yet confirmed": the real handler's choice is
    // branch 1's to make; 409 keeps the client from caching a licence it can
    // never get.
    return sendError(res, 409, "conflict", "授权尚未确认，暂时无法获取许可证。");
  }
  const envelope = {
    v: 1,
    sub: r.installId,
    plan: "buyout",
    iat: Math.floor(now() / 1000),
    nbf: null,
    exp: null, // buy-out: no expiry, and that absence is the product
    ent: ["pro"],
    sig: Buffer.from(crypto.randomBytes(64)).toString("base64"),
  };
  sendJSON(res, 200, {
    token: Buffer.from(JSON.stringify(envelope)).toString("base64url"),
    envelope,
  });
}

// ---------------------------------------------------------------------------
// Admin (new contract — §11.3).
// ---------------------------------------------------------------------------

function adminOK(req) {
  return Boolean(req.headers["x-admin-password"]);
}

async function adminRequests(req, res, url) {
  if (!adminOK(req)) {
    return sendError(res, 401, "unauthorized", "缺少访问令牌。");
  }
  const want = url.searchParams.get("status");
  const items = [...state.activations.values()]
    .filter((r) => !want || r.status === want)
    .sort((a, b) => b.createdAt - a.createdAt)
    .map((r) => {
      const a = state.accounts.get(r.accountId);
      return {
        requestId: r.requestId,
        installId: r.installId,
        anchorDigest: r.anchorDigest,
        nickname: a ? a.nickname : "",
        amountFen: r.amountFen,
        createdAt: stamp(r.createdAt),
        status: r.status,
      };
    });
  sendJSON(res, 200, { items });
}

async function adminConfirm(req, res) {
  if (!adminOK(req)) {
    return sendError(res, 401, "unauthorized", "缺少访问令牌。");
  }
  const body = await readBody(req).catch(() => ({}));
  const r = body.requestId ? state.activations.get(body.requestId) : null;
  if (!r) {
    return sendError(res, 404, "not_found", "请求的内容不存在。");
  }
  if (r.status === "confirmed") {
    return sendJSON(res, 200, { licenseId: r.licenseId });
  }
  const licenseId = "lic_" + hex(6);
  r.status = "confirmed";
  r.licenseId = licenseId;
  state.licences.set(licenseId, {
    licenseId,
    installId: r.installId,
    requestId: r.requestId,
    revoked: false,
    issuedAt: now(),
  });
  sendJSON(res, 200, { licenseId });
}

async function adminReissue(req, res) {
  if (!adminOK(req)) {
    return sendError(res, 401, "unauthorized", "缺少访问令牌。");
  }
  const body = await readBody(req).catch(() => ({}));
  const old = [...state.licences.values()].find((l) => l.installId === body.installId && !l.revoked);
  if (old) old.revoked = true; // reissue revokes the previous binding first
  const licenseId = "lic_" + hex(6);
  state.licences.set(licenseId, {
    licenseId,
    installId: body.installId,
    requestId: old ? old.requestId : null,
    revoked: false,
    issuedAt: now(),
  });
  sendJSON(res, 200, { licenseId });
}

async function adminRevoke(req, res) {
  if (!adminOK(req)) {
    return sendError(res, 401, "unauthorized", "缺少访问令牌。");
  }
  const body = await readBody(req).catch(() => ({}));
  const l = body.licenseId ? state.licences.get(body.licenseId) : null;
  if (!l) {
    return sendError(res, 404, "not_found", "请求的内容不存在。");
  }
  l.revoked = true;
  sendJSON(res, 200, {});
}

// ---------------------------------------------------------------------------
// Mock-only helpers — namespaced so they can never collide with the contract.
// ---------------------------------------------------------------------------

function mockScan(res, ticket) {
  const s = state.sessions.get(ticket);
  if (!s) {
    return sendError(res, 404, "not_found", "请求的内容不存在。");
  }
  const accountId = "acc_" + hex(5);
  const account = {
    id: accountId,
    nickname: "微信用户" + accountId.slice(-6),
    avatarUrl: "",
    plan: "buyout",
    createdAt: now(),
    lastLoginAt: now(),
    wechatBound: true,
  };
  state.accounts.set(accountId, account);
  s.status = "authorized";
  s.accountId = accountId;
  s.tokens = issueTokens(accountId);
  sendJSON(res, 200, { status: "authorized", account: accountView(account) });
}

function mockReset(res) {
  seed();
  sendJSON(res, 200, { status: "reset" });
}

// ---------------------------------------------------------------------------
// Router
// ---------------------------------------------------------------------------

const server = http.createServer(async (req, res) => {
  res.locals = { requestId: requestId() };
  cors(res);
  if (req.method === "OPTIONS") {
    res.writeHead(204);
    return res.end();
  }
  const url = new URL(req.url, "http://mock.invalid");
  const path = url.pathname;

  try {
    if (req.method === "GET" && path === "/healthz") {
      return sendJSON(res, 200, { status: "ok" });
    }

    // Auth (frozen)
    if (req.method === "POST" && path === "/v1/auth/session") return await createSession(req, res);
    let m = /^\/v1\/auth\/session\/([0-9a-f]{32})\/qr$/.exec(path);
    if (req.method === "GET" && m) {
      const png = qrPNG(m[1]);
      res.writeHead(200, { "Content-Type": "image/png", "Content-Length": png.length });
      return res.end(png);
    }
    m = /^\/v1\/auth\/session\/([0-9a-f]{32})$/.exec(path);
    if (req.method === "GET" && m) return await pollSession(req, res, m[1]);
    if (req.method === "DELETE" && path === "/v1/auth/session") return await revokeSession(req, res);
    if (req.method === "POST" && path === "/v1/auth/refresh") return await refreshSession(req, res);

    // Activation (new)
    if (req.method === "POST" && path === "/v1/activation/request") return await activationRequest(req, res);
    if (req.method === "GET" && path === "/v1/activation/status") return await activationStatus(req, res);
    if (req.method === "GET" && path === "/v1/activation/licence") return await activationLicence(req, res);

    // Admin (new)
    if (req.method === "GET" && path === "/v1/admin/requests") return await adminRequests(req, res, url);
    if (req.method === "POST" && path === "/v1/admin/confirm") return await adminConfirm(req, res);
    if (req.method === "POST" && path === "/v1/admin/reissue") return await adminReissue(req, res);
    if (req.method === "POST" && path === "/v1/admin/revoke") return await adminRevoke(req, res);

    // Mock-only
    m = /^\/mock\/scan\/([0-9a-f]{32})$/.exec(path);
    if (req.method === "POST" && m) return mockScan(res, m[1]);
    if (req.method === "POST" && path === "/mock/reset") return mockReset(res);

    sendError(res, 404, "not_found", "请求的内容不存在。");
  } catch (err) {
    sendError(res, 500, "internal", "服务器内部错误，请稍后重试。");
  }
});

server.listen(PORT, "127.0.0.1", () => {
  console.log(`mock server listening on http://127.0.0.1:${PORT}`);
  console.log("contract: docs/design/buyout-flow-convergence.zh-CN.md §11.3");
});
