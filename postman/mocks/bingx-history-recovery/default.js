// Payload shapes follow the market-data client and the observed HTTP-200/code-100410 failure.
const http = require("http");

http.createServer(async (req, res) => {
  const url = new URL(req.url, "http://localhost");
  const symbol = url.searchParams.get("symbol");
  const interval = url.searchParams.get("interval");
  const stats = await pm.state.get("bingx-history-recovery.stats") || { calls: {}, failures: 0 };
  res.setHeader("Content-Type", "application/json");
  if (req.method === "GET" && url.pathname === "/__state") {
    return res.end(JSON.stringify(stats));
  }
  const key = `${url.pathname}:${symbol}:${interval || ""}`;
  stats.calls[key] = (stats.calls[key] || 0) + 1;
  let body;
  if (req.method === "GET" && url.pathname === "/openApi/swap/v2/quote/contracts") {
    body = { code: 0, msg: "", data: [{ symbol, status: 1, quantityPrecision: 4,
      pricePrecision: 1, tradeMinQuantity: 0.0001, tradeMinUSDT: 2 }] };
  } else if (req.method === "GET" && url.pathname === "/openApi/swap/v3/quote/klines") {
    if (symbol === "BTC-USDT" && interval === "5m" && stats.calls[key] <= 2) {
      stats.failures++;
      body = { code: 100410, msg: "请稍后重试", data: null };
    } else {
      const minutes = { "5m": 5, "15m": 15, "30m": 30, "1h": 60 }[interval];
      if (!minutes) {
        res.statusCode = 400;
        body = { code: 100400, msg: "Invalid interval", data: null };
      } else {
        const duration = minutes * 60000;
        const time = Math.floor((Date.now() - 86400000) / duration) * duration;
        body = { code: 0, msg: "", data: [0, 1].map(i => ({ time: time + i * duration,
          open: "100", high: "102", low: "99", close: "101", volume: "10" })) };
      }
    }
  } else {
    res.statusCode = 501;
    body = { error: "Endpoint not modeled by the history recovery simulation" };
  }
  await pm.state.set("bingx-history-recovery.stats", stats);
  res.end(JSON.stringify(body));
}).listen(process.env.PORT || 4500);
