// admin-console 配置（合并后改这一行即可切真服务端）。
//
// 这个文件是 UMD 的：浏览器里挂在 window.WCB_CONFIG，Node 里可 require，
// 这样 test/smoke.mjs 能直接读同一份配置跑契约冒烟。
//
// baseURL 的优先级：页面「设置」里的运行时覆盖（localStorage） > 本文件。
// 运行时覆盖只是开发期便利，交付形态永远以本文件为准。
(function (root, factory) {
  const config = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = config;
  } else {
    root.WCB_CONFIG = config;
  }
})(typeof self !== "undefined" ? self : globalThis, function () {
  return {
    // 契约 mock（server/mock/server.js）默认监听 127.0.0.1:8901。
    // 切到真实服务端时只改这一个值，例如 "https://license.example.com"。
    baseURL: "http://127.0.0.1:8901",

    // 待确认列表自动刷新间隔（毫秒）。卖家盯着页面等买家登记，10 秒一拍。
    autoRefreshMs: 10000,

    // 本地操作日志上限（条）。只存本浏览器产生的动作，超过后丢最旧的。
    actionLogLimit: 200,
  };
});
