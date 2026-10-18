// admin-console 的契约客户端（设计文档 §11.3 + §11.7）。
//
// 这个文件是 UMD 的，为两个消费者服务：
//   - 浏览器：<script src="js/api.js"> 后挂到 window.WCB_CONSOLE；
//   - Node：require("./js/api.js")，让 test/smoke.mjs 用同一份代码验证契约。
// 选 UMD 而不是 ES module，是为了 index.html 能直接双击（file://）打开——
// module script 在 file:// 下会被 CORS 拦掉，而卖家不该为了开一个页面先跑服务。
//
// 两个客户端分工：
//   AuthClient  微信开放平台扫码登录（冻结的 auth 端点，买家端同一套）
//   AdminClient 管理操作（§11.3 四个 admin 端点，Bearer 鉴权见 §11.7）
// 令牌的存取、刷新时机由调用方（app.js）决定，这里只提供机制——
// 存储是 sessionStorage 还是内存，是产品决策，不是网络层的。
(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.WCB_CONSOLE = api;
  }
})(typeof self !== "undefined" ? self : globalThis, function () {
  "use strict";

  // 访问令牌到期前多少毫秒就开始静默续期。卖家一开就是一整天，
  // 等到 401 再续会让一次操作莫名其妙失败半秒。
  const REFRESH_MARGIN_MS = 60 * 1000;

  // ApiError 是「服务端回答了，但答案是不」。status/code/message/requestId
  // 四件套直接来自错误信封 { error: { code, message, request_id } }，
  // requestId 要亮给卖家看——排查时那是服务端日志的钥匙。
  class ApiError extends Error {
    constructor(status, code, message, requestId) {
      super(message || "服务端返回错误。");
      this.name = "ApiError";
      this.status = status;
      this.code = code || ("http_" + status);
      this.requestId = requestId || null;
    }
  }

  // NetworkError 是「服务端没回答」：mock 没起、域名写错、网线掉了。
  // 它与 ApiError 必须分开，因为卖家要做的动作完全不同：
  // 一个是去看服务端日志，一个是去检查服务起没起。
  class NetworkError extends Error {
    constructor(baseURL, cause) {
      super("无法连接服务端 " + baseURL + (cause ? "：" + cause : ""));
      this.name = "NetworkError";
      this.baseURL = baseURL;
      this.cause = cause || null;
    }
  }

  // HttpClient 是全文件唯一碰 fetch 的地方。所有错误在这里归成两类，
  // 所有成功响应在这里解析，上层永远拿到干净的数据或干净的异常。
  class HttpClient {
    constructor(baseURL) {
      this.baseURL = String(baseURL || "").replace(/\/+$/, "");
    }

    async json(method, path, body, headers) {
      const url = this.baseURL + path;
      let res;
      try {
        res = await fetch(url, {
          method: method,
          headers: Object.assign({ "Content-Type": "application/json" }, headers || {}),
          body: body === undefined ? undefined : JSON.stringify(body),
        });
      } catch (err) {
        // fetch 只在网络层失败时 throw；HTTP 错误状态码走下面的 !res.ok。
        throw new NetworkError(this.baseURL, err && err.message ? err.message : String(err));
      }
      const requestId = res.headers.get("X-Request-Id");
      const text = await res.text();
      const payload = text ? safeParse(text) : {};
      if (!res.ok) {
        const env = payload && payload.error ? payload.error : {};
        throw new ApiError(res.status, env.code, env.message, env.request_id || requestId);
      }
      return payload;
    }

    // QR 是二进制 PNG，不走 json()。
    async png(path, headers) {
      const url = this.baseURL + path;
      let res;
      try {
        res = await fetch(url, { method: "GET", headers: headers || {} });
      } catch (err) {
        throw new NetworkError(this.baseURL, err && err.message ? err.message : String(err));
      }
      if (!res.ok) {
        const requestId = res.headers.get("X-Request-Id");
        let message = "服务端返回 " + res.status + "。";
        try {
          const payload = await res.json();
          if (payload && payload.error && payload.error.message) message = payload.error.message;
        } catch (_) {
          /* 非 JSON 的错误体就用默认文案 */
        }
        throw new ApiError(res.status, null, message, requestId);
      }
      return await res.blob();
    }
  }

  function safeParse(text) {
    try {
      return JSON.parse(text);
    } catch (_) {
      return {};
    }
  }

  // AuthClient — 微信开放平台网站应用扫码登录。
  //
  // 流程（与买家端完全同一套冻结端点）：建会话拿 ticket → 展示 QR →
  // 轮询 ticket → authorized 时拿双令牌。installId 是这台控制台自己的
  // 匿名身份，由调用方生成后持久化，与服务端登记的买家机器码无关。
  class AuthClient {
    constructor(http) {
      this.http = http;
    }

    createSession(installId, deviceId) {
      return this.http.json("POST", "/v1/auth/session", {
        installId: installId,
        deviceId: deviceId || "admin-console",
      });
    }

    pollSession(ticket) {
      return this.http.json("GET", "/v1/auth/session/" + encodeURIComponent(ticket));
    }

    qrBlob(ticket) {
      return this.http.png("/v1/auth/session/" + encodeURIComponent(ticket) + "/qr");
    }

    refresh(refreshToken) {
      return this.http.json("POST", "/v1/auth/refresh", { refreshToken: refreshToken });
    }

    // revoke 必须带 refreshToken，否则服务端认不出要作废哪条会话链。
    // 服务端对空凭证照样回 204（handler 把它当成「没有链可作废」），
    // 前端因此看不出自己登出失败了——令牌还在服务端活着。
    revoke(refreshToken) {
      // 204 无 body；json() 对空 body 返回 {}，ok 即可。
      return this.http.json("DELETE", "/v1/auth/session", refreshToken ? { refreshToken: refreshToken } : undefined);
    }
  }

  // AdminClient — §11.3 的四个管理端点。
  //
  // 鉴权（§11.7，2026-10-18 变更）：Authorization: Bearer <accessToken>，
  // 即卖家本人微信扫码拿到的令牌。旧 D5 的 X-Admin-Password 已作废。
  // 令牌由调用方经 setTokens 注入；到期前自动续，401 自动续后重试一次，
  // 两层都失败才把错误抛给卖家——那意味着真的要重新扫码了。
  //
  // 光有 Bearer 不够：服务端还要求签入账号在 WCB_ADMIN_OPENIDS 白名单里，
  // 不在名单上是 403 而不是 401——凭证本身有效，只是背后的身份不是卖家。
  // 所以 403 不能当作「令牌过期」处理，续期多少次都不会让它变成 200。
  class AdminClient {
    constructor(http) {
      this.http = http;
      this._tokens = null; // { accessToken, refreshToken, expiresAt }
      this._onTokens = null;
    }

    setTokens(tokens) {
      this._tokens = tokens || null;
    }

    onTokensChanged(fn) {
      this._onTokens = fn;
    }

    listRequests(status) {
      const q = status ? "?status=" + encodeURIComponent(status) : "";
      return this._withAuth("GET", "/v1/admin/requests" + q);
    }

    confirm(requestId) {
      return this._withAuth("POST", "/v1/admin/confirm", { requestId: requestId });
    }

    reissue(installId) {
      return this._withAuth("POST", "/v1/admin/reissue", { installId: installId });
    }

    revoke(licenseId) {
      return this._withAuth("POST", "/v1/admin/revoke", { licenseId: licenseId });
    }

    // _withAuth 是四个管理端点共用的外壳：带头请求 → 必要时续期 →
    // 401 续期重试一次。续期本身用 AuthClient 的端点，这里内联一份 http
    // 调用即可，不必让 AdminClient 依赖 AuthClient 实例。
    async _withAuth(method, path, body) {
      await this._ensureFresh();
      try {
        return await this._send(method, path, body);
      } catch (err) {
        if (err instanceof ApiError && err.status === 401 && this._tokens && this._tokens.refreshToken) {
          // 服务端说令牌过期/失效：续一次，重试一次。重试再失败就把
          // 续期后的错误抛出去，由调用方决定回登录页。
          await this._refresh();
          return await this._send(method, path, body);
        }
        throw err;
      }
    }

    async _send(method, path, body) {
      const headers = {};
      if (this._tokens && this._tokens.accessToken) {
        headers["Authorization"] = "Bearer " + this._tokens.accessToken;
      }
      return this.http.json(method, path, body, headers);
    }

    async _ensureFresh() {
      if (!this._tokens || !this._tokens.refreshToken || !this._tokens.expiresAt) return;
      const left = Date.parse(this._tokens.expiresAt) - Date.now();
      if (left < REFRESH_MARGIN_MS) {
        await this._refresh();
      }
    }

    async _refresh() {
      const res = await this.http.json("POST", "/v1/auth/refresh", {
        refreshToken: this._tokens.refreshToken,
      });
      this._apply(res);
    }

    _apply(res) {
      if (!res || !res.accessToken) return;
      this._tokens = {
        accessToken: res.accessToken,
        refreshToken: res.refreshToken || (this._tokens && this._tokens.refreshToken),
        expiresAt: res.expiresAt,
        account: res.account || (this._tokens && this._tokens.account),
      };
      if (this._onTokens) this._onTokens(this._tokens);
    }
  }

  // ConsoleApi 是门面：一个 baseURL，两个客户端，一个令牌槽。
  // app.js 只跟它说话；测试也只跟它说话。
  class ConsoleApi {
    constructor(baseURL) {
      this.http = new HttpClient(baseURL);
      this.auth = new AuthClient(this.http);
      this.admin = new AdminClient(this.http);
    }

    get baseURL() {
      return this.http.baseURL;
    }

    setTokens(tokens) {
      this.admin.setTokens(tokens);
    }

    onTokensChanged(fn) {
      this.admin.onTokensChanged(fn);
    }

    health() {
      return this.http.json("GET", "/healthz");
    }
  }

  return {
    ConsoleApi: ConsoleApi,
    HttpClient: HttpClient,
    AuthClient: AuthClient,
    AdminClient: AdminClient,
    ApiError: ApiError,
    NetworkError: NetworkError,
  };
});
