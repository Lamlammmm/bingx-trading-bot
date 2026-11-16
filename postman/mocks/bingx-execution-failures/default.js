const http = require('http');
http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  let state = await pm.state.get('bingx-execution-failures.state') || { scenario: 'success', writes: 0, calls: 0 };
  res.setHeader('Content-Type', 'application/json');
  if (url.pathname === '/__scenario' && req.method === 'POST') {
    state = { scenario: url.searchParams.get('name'), writes: 0, calls: 0 };
    await pm.state.set('bingx-execution-failures.state', state);
    return res.end(JSON.stringify(state));
  }
  if (url.pathname === '/__state') return res.end(JSON.stringify(state));
  if (url.pathname === '/openApi/swap/v2/trade/order' && req.method === 'POST') {
    state.calls++;
    if (state.scenario === 'rate-limit') {
      await pm.state.set('bingx-execution-failures.state', state);
      res.statusCode = 429;
      return res.end(JSON.stringify({ code: 100410, msg: 'Synthetic rate limit' }));
    }
    if (state.scenario === 'business-error') {
      await pm.state.set('bingx-execution-failures.state', state);
      return res.end(JSON.stringify({ code: 100410, msg: 'Synthetic rejection' }));
    }
    if (state.scenario === 'html-error') {
      await pm.state.set('bingx-execution-failures.state', state);
      res.statusCode = 502;
      res.setHeader('Content-Type', 'text/html');
      return res.end('<html>synthetic upstream unavailable</html>');
    }
    state.writes++;
    await pm.state.set('bingx-execution-failures.state', state);
    const body = JSON.stringify({ code: 0, data: { order: { avgPrice: '100', executedQty: '1', status: 'FILLED' } } });
    if (state.scenario === 'timeout-after-write') return setTimeout(() => res.end(body), 1500);
    return res.end(body);
  }
  if (url.pathname === '/openApi/swap/v2/user/positions') {
    return res.end(JSON.stringify({ code: 0, data: [{ symbol: 'BTC-USDT', positionSide: 'LONG', positionAmt: '0.5' }] }));
  }
  res.statusCode = 501;
  res.end(JSON.stringify({ error: 'Endpoint not modeled' }));
}).listen(process.env.PORT || 4500);
