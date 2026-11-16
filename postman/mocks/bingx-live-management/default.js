const http = require('http');
http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  let s = await pm.state.get('bingx-live-management.state');
  res.setHeader('Content-Type', 'application/json');
  if (url.pathname === '/__reset') {
    const short = url.searchParams.get('side') === 'SHORT';
    const now = Date.now();
    s = { scenario: url.searchParams.get('scenario') || 'success', side: short ? 'SHORT' : 'LONG',
      positionId: '92233720368547758071', quantity: 1, entry: 100, mark: short ? 88 : 112,
      nextId: 1000, closes: 0, replacements: 0, deletes: 0, orders: [
        {orderId:'sl-original',type:'STOP_MARKET',stopPrice:short?110:90,origQty:1},
        {orderId:'tp-original',type:'TAKE_PROFIT_MARKET',stopPrice:short?70:130,origQty:1}], fills:{}, income:[
        {symbol:'',asset:'USDT',incomeType:'TRANSFER',income:'20',time:now-3000000},
        {symbol:'BTC-USDT',asset:'USDT',incomeType:'TRADING_FEE',income:'-0.05',time:now-2400000},
        {symbol:'BTC-USDT',asset:'USDT',incomeType:'FUNDING_FEE',income:'-0.25',time:now-1200000}] };
    if (s.scenario === 'unknown-order') s.orders.push({orderId:'manual',type:'LIMIT',stopPrice:0,origQty:1});
    await pm.state.set('bingx-live-management.state',s);
    return res.end(JSON.stringify({ok:true}));
  }
  if (!s) { res.statusCode=409; return res.end(JSON.stringify({error:'Reset the fixture first'})); }
  if (url.pathname === '/__state') return res.end(JSON.stringify(s));
  if (url.pathname === '/__mark') { s.mark=Number(url.searchParams.get('price')); await pm.state.set('bingx-live-management.state',s); return res.end('{}'); }
  let data;
  if (url.pathname === '/openApi/swap/v1/positionSide/dual') data={dualSidePosition:true};
  else if (url.pathname === '/openApi/swap/v2/user/positions') data=s.quantity ? [{symbol:'BTC-USDT',positionId:s.positionId,positionSide:s.side,positionAmt:String(s.quantity),avgPrice:String(s.entry)}] : [];
  else if (url.pathname === '/openApi/swap/v2/trade/openOrders') data=s.orders.map(o=>({...o,symbol:'BTC-USDT',positionSide:s.side,side:s.side==='LONG'?'SELL':'BUY',workingType:'MARK_PRICE'}));
  else if (url.pathname === '/openApi/swap/v2/quote/premiumIndex') data={symbol:'BTC-USDT',markPrice:String(s.mark),time:Date.now()};
  else if (url.pathname === '/openApi/swap/v2/user/income') {
    data=s.income.filter(i=>(!url.searchParams.get('symbol')||i.symbol===url.searchParams.get('symbol')) && i.time>=Number(url.searchParams.get('startTime')) && i.time<=Number(url.searchParams.get('endTime')));
  } else if (url.pathname === '/openApi/swap/v3/user/balance') {
    const wallet=1000+s.income.reduce((a,i)=>a+Number(i.income),0);
    data=[{asset:'USDT',balance:String(wallet),equity:String(wallet+(s.mark-s.entry)*s.quantity*(s.side==='LONG'?1:-1)),availableMargin:String(wallet)}];
  } else if (url.pathname === '/openApi/swap/v2/user/commissionRate') data={commission:{takerCommissionRate:0.0005}};
  else if (url.pathname === '/openApi/swap/v1/trade/positionHistory') data={list:s.quantity?[]:[{positionId:s.positionId}]};
  else if (url.pathname === '/openApi/swap/v2/trade/order' && req.method === 'GET') {
    data=s.fills[url.searchParams.get('clientOrderId')];
    if (!data) return res.end(JSON.stringify({code:100400,msg:'Unknown synthetic order'}));
  } else if (url.pathname === '/openApi/swap/v2/trade/order' && req.method === 'POST') {
    const quantity=Number(url.searchParams.get('quantity'));
    if (url.searchParams.has('reduceOnly') || url.searchParams.get('positionSide')!==s.side ||
        url.searchParams.get('side')!==(s.side==='LONG'?'SELL':'BUY') || quantity<=0 || quantity>s.quantity)
      return res.end(JSON.stringify({code:100400,msg:'Invalid hedge close request'}));
    const client=url.searchParams.get('clientOrderId');
    if (!client || s.fills[client]) return res.end(JSON.stringify({code:100400,msg:'Duplicate client id'}));
    s.closes++;
    const actual=s.scenario==='partial-fill' ? quantity/2 : quantity;
    s.quantity-=actual;
    const now=Date.now();
    s.income.push({symbol:'BTC-USDT',asset:'USDT',incomeType:'REALIZED_PNL',income:String((s.mark-s.entry)*actual*(s.side==='LONG'?1:-1)),time:now});
    s.income.push({symbol:'BTC-USDT',asset:'USDT',incomeType:'TRADING_FEE',income:String(-s.mark*actual*0.0005),time:now});
    data=s.fills[client]={orderId:String(++s.nextId),clientOrderId:client,symbol:'BTC-USDT',side:s.side==='LONG'?'SELL':'BUY',positionSide:s.side,
      status:s.scenario==='partial-fill'?'PARTIALLY_FILLED':'FILLED',executedQty:String(actual),avgPrice:String(s.mark)};
    await pm.state.set('bingx-live-management.state',s);
    if (s.scenario==='close-timeout') return setTimeout(()=>res.end(JSON.stringify({code:0,data})),2500);
  } else if (url.pathname === '/openApi/swap/v1/trade/cancelReplace' && req.method==='POST') {
    s.replacements++;
    if (url.searchParams.get('cancelReplaceMode')!=='STOP_ON_FAILURE' || url.searchParams.get('closePosition')!=='true' || url.searchParams.has('reduceOnly'))
      return res.end(JSON.stringify({code:100400,msg:'Unsafe replacement request'}));
    const index=s.orders.findIndex(o=>o.orderId===url.searchParams.get('cancelOrderId'));
    if (index<0) return res.end(JSON.stringify({code:0,data:{cancelResult:'FAILED',newOrderResult:'FAILED'}}));
    const type=url.searchParams.get('type');
    s.orders.splice(index,1);
    if ((s.scenario==='take-rejected' && type==='TAKE_PROFIT_MARKET') || (s.scenario==='stop-rejected' && type==='STOP_MARKET')) {
      await pm.state.set('bingx-live-management.state',s);
      return res.end(JSON.stringify({code:0,data:{cancelResult:'SUCCESS',newOrderResult:'FAILED'}}));
    }
    const order={orderId:String(++s.nextId),type,stopPrice:Number(url.searchParams.get('stopPrice')),origQty:Number(url.searchParams.get('quantity')),closePosition:url.searchParams.get('closePosition')==='true'};
    s.orders.push(order);
    data={cancelResult:'SUCCESS',newOrderResult:'SUCCESS',newOrderId:order.orderId};
    await pm.state.set('bingx-live-management.state',s);
    if (s.scenario==='stop-timeout' && type==='STOP_MARKET') return setTimeout(()=>res.end(JSON.stringify({code:0,data})),2500);
  } else if (url.pathname === '/openApi/swap/v2/trade/order' && req.method==='DELETE') {
    const id=url.searchParams.get('orderId');
    const order=s.orders.find(o=>o.orderId===id);
    if (!order) return res.end(JSON.stringify({code:100400,msg:'Unknown order'}));
    s.orders=s.orders.filter(o=>o.orderId!==id); s.deletes++; data={orderId:id};
  } else { res.statusCode=501; return res.end(JSON.stringify({error:'Endpoint not modeled'})); }
  await pm.state.set('bingx-live-management.state',s);
  res.end(JSON.stringify({code:0,msg:'',data}));
}).listen(process.env.PORT || 4500);
