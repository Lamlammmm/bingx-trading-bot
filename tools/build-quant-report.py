"""Render measured replay summaries as one portable HTML report; no external packages."""
import hashlib
import html
import json
from datetime import datetime, timezone
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
validation = root / 'artifacts/quant-validation'
variants = {
    'baseline-backtest': 'Baseline trước sửa',
    'corrected-legacy': 'Legacy + sửa mô phỏng/risk',
    'multi-only': 'Chỉ multi-timeframe',
    'management-only': 'Chỉ dynamic management',
    'research': 'Nghiên cứu kết hợp',
    'research-cost-stress': 'Stress phí/slippage ×2 + spread/funding',
    'research-parameters-08': 'Sensitivity ADX/trailing ×0.8',
    'research-parameters-12': 'Sensitivity ADX/trailing ×1.2',
}
rows = []
sources = []
for name, label in variants.items():
    path = validation / f'{name}.json'
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    sources.append({'file': str(path.relative_to(root)), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                    'assumptions': data['Assumptions']})
    for result in data['Results']:
        counts = result.get('DecisionCounts', {})
        rows.append({
            'variant': label, 'symbol': result['Symbol'], 'split': result['Split'],
            'from': result['From'], 'until': result['Until'], 'bars': result['Bars'],
            'signals': counts.get('RuleSignal', 0) if 'DecisionCounts' in result else None,
            'rejected': counts.get('RuleRejected', 0) if 'DecisionCounts' in result else None,
            'trades': result['TradeCount'], 'winRate': result['WinRatePercent'],
            'averageWin': result['AverageWin'], 'averageLoss': result['AverageLoss'],
            'profitFactor': result['ProfitFactor'], 'expectancy': result['Expectancy'],
            'netPnl': result['ClosedNetPnl'], 'drawdown': result['MaximumDrawdownPercent'],
            'maximumConsecutiveLosses': result['MaximumConsecutiveLosses'],
            'realizedR': result.get('AverageRealizedR'), 'openAtEnd': len(result['OpenPositionsAtEnd']),
            'reasons': {k: v for k, v in counts.items() if k.startswith('Rejected: ')},
        })
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
trx = ET.parse(validation / 'final.trx').find('.//t:Counters', ns).attrib
manifests = [json.loads(path.read_text(encoding='utf-8-sig')) for path in sorted((root / 'artifacts/market-history').glob('*-manifest.json'))]
binary_hashes = {}
for name in ['quant-baseline', 'quant-final']:
    path = root / f'artifacts/{name}/TradingBot.Worker.dll'
    binary_hashes[name] = hashlib.sha256(path.read_bytes()).hexdigest()
report = {'generatedAt': datetime.now(timezone.utc).isoformat(), 'tests': trx, 'manifests': manifests,
          'binarySha256': binary_hashes, 'sources': sources, 'rows': rows}
(root / 'docs/quant-comparison.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
options = ''.join(f'<option>{html.escape(value)}</option>' for value in variants.values())
payload = json.dumps(rows, ensure_ascii=False).replace('<', '\\u003c')
page = '''<!doctype html>
<html lang="vi"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>BingX · Kết quả kiểm tra 06/11/2026</title>
<style>
:root{color-scheme:light dark;font-family:system-ui,sans-serif;background:#f8f9fb;color:#17212b}
body{max-width:1300px;margin:auto;padding:32px 24px}h1{font-size:26px;margin:8px 0 16px}h2{font-size:18px;margin-top:30px}
p{max-width:960px;line-height:1.65}strong{color:#125f64}.facts{display:flex;gap:30px;flex-wrap:wrap;padding:20px 0;border-block:1px solid #ccd2d8}
.facts b{display:block;font-size:23px}label{display:inline-block;margin:10px 18px 10px 0}select{margin-left:8px;padding:6px}
.scroll{overflow-x:auto}table{border-collapse:collapse;width:100%;font-size:13px}th,td{padding:11px 9px;border-bottom:1px solid #d8dde2;text-align:right;white-space:nowrap}th:first-child,td:first-child,td:nth-child(2),td:nth-child(3){text-align:left}th{font-weight:600}
details{padding:14px 0;border-bottom:1px solid #d8dde2}summary{cursor:pointer}small{line-height:1.6;display:block;color:#586675}a{color:#125f64}code{font-size:12px;overflow-wrap:anywhere}
@media(prefers-color-scheme:dark){:root{background:#15191e;color:#e4e8ed}strong,a{color:#8bc6c8}small{color:#aab4c0}th,td,.facts,details{border-color:#3a424b}}
</style>
<main><small>BINGX / AUDIT · IMPLEMENTATION · MEASURED REPLAY / 06 NOV 2026</small>
<h1>Chưa có bằng chứng lợi thế giao dịch</h1>
<p>Source đã được cải tiến và kiểm thử. <strong>Baseline và các biến thể đều không mở/đóng giao dịch trên mẫu này</strong>: tín hiệu còn lại không đạt Risk/Reward sau phí. Các chỉ số lợi nhuận ghi “—” khi không thể tính, không coi mẫu rỗng là tỷ lệ thắng 0% hay bằng chứng an toàn.</p>
<div class="facts"><div><b>17.280</b>nến 5m công khai</div><div><b>2 symbols</b>BTC-USDT · ETH-USDT</div><div><b>30 ngày</b>07/10–06/11/2026</div><div><b>TEST_RESULT</b>unit/integration tests</div><div><b>12 + 6</b>API assertions + execution scenarios</div></div>
<h2>So sánh kết quả đã chạy</h2>
<p>Warmup 10 ngày; phần còn lại chia 60/20/20 theo thời gian. Ba cửa sổ walk-forward dùng tham số cố định và có vùng chồng lấn: không cộng các hàng này vào train/validation/test. Mỗi symbol là một tài khoản riêng. Baseline chưa thu bộ đếm candidate nên hiển thị “—”.</p>
<label>Split<select id="split"><option value="test">test</option><option>validation</option><option>train</option><option>walk-forward-1</option><option>walk-forward-2</option><option>walk-forward-3</option><option value="all">Tất cả</option></select></label>
<label>Biến thể<select id="variant"><option value="all">Tất cả</option>VARIANT_OPTIONS</select></label>
<div class="scroll"><table><thead><tr><th>Biến thể</th><th>Symbol</th><th>Split</th><th>Tín hiệu</th><th>Từ chối</th><th>Lệnh đóng</th><th>Net PnL (USDT)</th><th>Win rate (%)</th><th>Profit factor</th><th>Expectancy (USDT/lệnh)</th><th>Max DD (%)</th></tr></thead><tbody id="body"></tbody></table></div>
<small>Phí cơ sở 0,05%/chiều, slippage 2 bps/chiều, spread/funding cơ sở 0 (giả định). Stress tăng gấp đôi phí/slippage, thêm spread 4 bps và funding cố định 0,0001/8h; không phải funding hoặc spread lịch sử. Sensitivity thay ADX/trailing cùng ±20%; không có giao dịch nên chưa đánh giá được độ bền tham số.</small>
<h2>Vì sao không giao dịch?</h2><div id="reasons"></div>
<h2>Cách đọc bằng chứng</h2><p>0 drawdown do không có vị thế không chứng minh mức rủi ro thấp. Các split đã được xem trong phát triển, cần holdout mới sau khi freeze. OHLC không cho biết thứ tự intrabar: stop được xét trước; SL mới chỉ hiệu lực từ bar kế tiếp; stop gap khớp ở open bất lợi. Chưa mô hình liquidation, market impact, mark-price và latency thực tế.</p>
<p><a href="quant-improvements.md">Đọc audit, công thức, giới hạn live và lệnh chạy paper</a> · <a href="quant-comparison.json">Tải số đo và fingerprint JSON</a></p>
<small>Không bật/restart trading worker, không sửa cấu hình live, không gửi lệnh thật. Research được chặn khi Live enabled. Kiểm tra HTTP dùng API-only host và mock loopback; PostgreSQL thật và workflow partial/trailing trên sàn chưa được xác minh.</small></main>
<script>
const rows=REPORT_DATA;
const fmt=v=>v===null?'—':Number(v).toLocaleString('vi-VN',{maximumFractionDigits:3});
const esc=s=>String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function render(){const shown=rows.filter(r=>(split.value==='all'||r.split===split.value)&&(variant.value==='all'||r.variant===variant.value));
document.getElementById('body').innerHTML=shown.map(r=>'<tr>'+[esc(r.variant),esc(r.symbol),esc(r.split),fmt(r.signals),fmt(r.rejected),fmt(r.trades),fmt(r.netPnl),fmt(r.winRate),fmt(r.profitFactor),fmt(r.expectancy),fmt(r.drawdown)].map(v=>'<td>'+v+'</td>').join('')+'</tr>').join('');
document.getElementById('reasons').innerHTML=shown.filter(r=>Object.keys(r.reasons).length).map(r=>'<details><summary>'+esc(r.variant+' · '+r.symbol+' · '+r.split)+'</summary>'+Object.entries(r.reasons).map(([k,v])=>'<p>'+esc(k.replace('Rejected: ',''))+' — '+v+'</p>').join('')+'</details>').join('');}
const split=document.getElementById('split'),variant=document.getElementById('variant');split.onchange=render;variant.onchange=render;render();
</script></html>'''
page = page.replace('TEST_RESULT', f"{trx['passed']}/{trx['total']}").replace('VARIANT_OPTIONS', options).replace('REPORT_DATA', payload)
(root / 'docs/quant-comparison.html').write_text(page, encoding='utf-8')
print(f"Wrote {len(rows)} measured rows; tests {trx['passed']}/{trx['total']}.")
