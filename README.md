# BingX Trading Bot - Phase 1

Bot .NET Worker cho BingX USDT-M Perpetual Futures, gồm paper trading và live trading. `Live:Enabled=true` cho phép đặt lệnh thật. Xem [hướng dẫn chạy live](LIVE_RUNBOOK.md) trước khi khởi động.

## Phạm vi giai đoạn 1

- REST backfill nến đóng cho `15m`, `30m`, `1h`; thêm `5m` để kiểm tra SL/TP paper.
- WebSocket market data chính thức cho các kênh K-line.
- Strategy mẫu: breakout/retest 15m, xác nhận xu hướng 30m và lọc xu hướng 1h bằng EMA 20/50, volume xác nhận.
- Signal engine và risk manager: target gross 1 USDT/lệnh, rủi ro tối đa 0.5%/lệnh, dừng ngày ở mức lỗ 2%, leverage paper tối đa 20x, SL theo ATR 1.5 lần, TP 2R.
- Paper execution: một vị thế trên mỗi symbol, mô phỏng phí, slippage, funding tùy cấu hình, SL/TP và log JSON.
- Persistence: lưu account state, thống kê và vị thế đang mở để khôi phục sau restart.
- OpenAI tùy chọn: trả về `long`, `short` hoặc `no_trade` bằng Structured Outputs; Risk Gate luôn kiểm tra lại SL/TP, confidence, entry deviation, daily loss và leverage.
- Khi OpenAI bật nhưng API key thiếu, lỗi hoặc response sai schema, bot fail-closed về `no_trade`.
- BingX API key/secret được dùng cho nhánh live; không dùng trong prompt OpenAI.

## Endpoint BingX đang dùng

Các endpoint được kiểm tra thực tế ngày 2026-08-26:

| Mục đích | Endpoint |
| --- | --- |
| Server time | `GET /openApi/swap/v2/server/time` |
| Contract info | `GET /openApi/swap/v2/quote/contracts?symbol=BTC-USDT` |
| K-line | `GET /openApi/swap/v3/quote/klines?symbol=BTC-USDT&interval=15m&limit=250` |
| WebSocket | `wss://open-api-swap.bingx.com/swap-market` |

WebSocket subscribe theo dạng `{symbol}@kline_{interval}`, ví dụ `BTC-USDT@kline_15m`. Client xử lý cả payload GZIP và heartbeat `Ping`/`Pong`, đồng thời nhận được cả dạng `data` object và array đang xuất hiện trong runtime BingX.
Payload array hiện tại dùng `T` làm thời gian mở nến và không có cờ đóng. Bot giữ snapshot mới nhất theo từng timeframe, rồi chỉ phát nến đã đóng một lần khi nhận timestamp của nến kế tiếp. Khi reconnect, REST đối chiếu các nến đóng bị mất. Các nến `5m` được replay tuần tự để bảo vệ vị thế paper, nến `15m` được đối chiếu theo khung vào lệnh; trong lúc replay bot không tạo entry mới để tránh dùng context tương lai.

Nguồn tham khảo chính thức: [BingX API Docs](https://bingx-api.github.io/docs-v3/), [BingX Swap Market API Reference](https://github.com/BingX-API/api-ai-skills/blob/main/skills/swap-market/api-reference.md), [BingX Swap WebSocket Market Reference](https://github.com/BingX-API/api-ai-skills/blob/main/skills/swap-ws-market/api-reference.md).

## Chạy local

Cần .NET 8 SDK.

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

### Cấu hình key khi chạy local

Mở file `src/TradingBot.Worker/appsettings.Development.json` và điền:

```json
{
  "BingX": {
    "ApiKey": "your-bingx-api-key",
    "ApiSecret": "your-bingx-api-secret"
  },
  "OpenAI": {
    "Enabled": true,
    "ApiKey": "your-openai-api-key",
    "Model": "gpt-5.2"
  }
}
```

File này đã được `.gitignore`, không đưa lên GitHub. Nhánh live sử dụng key/secret BingX; biến môi trường `BINGX_API_KEY` và `BINGX_API_SECRET` được ưu tiên.

### Bật OpenAI phân tích paper trading

Tạo API key của chính bạn trong OpenAI Platform, sau đó truyền qua environment variable. Không gửi key trong chat và không ghi key vào file cấu hình:

```powershell
$env:OPENAI_API_KEY = "your-key-from-openai-platform"
$env:OPENAI__ENABLED = "true"
$env:OPENAI__MODEL = "gpt-5.2"
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

Mỗi nến 15m đã đóng, bot gửi dữ liệu nến gần nhất của 15m/30m/1h cùng technical context lên OpenAI Responses API với `store=false`. AI chỉ trả về quyết định có schema cố định. Paper engine dùng giá thị trường hiện tại, còn SL/TP của AI phải vượt qua Risk Gate trước khi mô phỏng lệnh.

**Khuyến nghị: giữ `OPENAI__ENABLED=false` (mặc định).** Dùng LLM để ra quyết định vào lệnh theo thời gian thực chưa được kiểm chứng có edge thống kê thật sự, kết quả không nhất quán giữa các lần gọi nên khó backtest, và tốn chi phí/độ trễ mỗi 15 phút. Rule-engine (`BreakoutStrategy` + `RiskManager`) là nguồn quyết định chính; code AI vẫn giữ lại để thử nghiệm có kiểm soát khi cần.

Biến môi trường phải trùng tên section trong `appsettings.json` (`BingX` / `Strategy` / `Risk` / `OpenAI`) — **không có** tiền tố `TradingBot`. Ví dụ cấu hình nhiều symbol cùng lúc:

```powershell
$env:BINGX__SYMBOLS__0 = "BTC-USDT"
$env:BINGX__SYMBOLS__1 = "ETH-USDT"
$env:RISK__STARTINGBALANCE = "10000"
$env:RISK__RISKPERTRADEPERCENT = "0.5"
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

Hoặc sao chép `.env.example` thành `.env` khi chạy qua Docker Compose. Không đưa API secret vào `appsettings.json`, `.env.example` hoặc Git.

### Mô phỏng paper

`Risk:TargetProfitUsdt` mặc định là `1` và là target gross trước phí/slippage. Quantity sẽ không vượt risk budget `Risk:RiskPerTradePercent`; nếu vốn hoặc min quantity không cho phép, bot sẽ bỏ qua lệnh. Live và paper không còn tự đóng theo thời gian; chỉ thoát theo SL/TP. Paper kiểm tra SL/TP trên nến 5m và giữ cách chốt từng phần đã cấu hình. Tín hiệu mới chỉ xét trên nến 15m đã đóng. `PaperTrading:SlippageBasisPoints` mặc định là `2` (0.02%) và được áp dụng theo hướng bất lợi ở entry/exit. `PaperTrading:FundingRatePerEightHours` mặc định là `0`; nếu muốn mô phỏng funding, đặt một tỷ lệ ước tính dương/âm theo từng 8 giờ. Đây không phải funding rate live từ BingX.

## Chạy Docker

```powershell
docker compose up --build
```

Log cần quan sát:

- `Loaded ... closed candles` - REST backfill thành công.
- `Connected to BingX perpetual market WebSocket` - stream thành công.
- `Received closed 15m candle` - WebSocket đã chuyển sang kỳ mới và bot đã chốt nến 15m trước đó.
- `Evaluated closed 15m candle` - paper engine đã đánh giá nến 15m (khi không có vị thế đang mở).
- `AI RESULT: TRADE LONG/SHORT` - OpenAI đề xuất giao dịch; log Warning màu vàng ở console Development.
- `AI RESULT: NO_TRADE` - OpenAI chủ động quyết định không giao dịch, kèm confidence và lý do.
- `AI RESULT: ERROR` - request hoặc response OpenAI bị lỗi; log gồm mã lỗi, HTTP status và request ID, không bị hiểu nhầm là quyết định `no_trade`.
- `PAPER OPEN` / `PAPER CLOSE` - paper engine đã mô phỏng một giao dịch.
- `PAPER POSITION RESTORED` - vị thế paper đã được khôi phục sau restart.

Khi chạy local với `DOTNET_ENVIRONMENT=Development`, console dùng định dạng một dòng có màu để dễ tập trung vào các banner `AI ...`. Các môi trường khác vẫn giữ log JSON để máy thu thập log xử lý ổn định.

## Cấu trúc module

```text
src/TradingBot.Worker
├── Domain                         # Candle, signal, plan, paper position
├── Application                    # Candle store, indicators, strategy, risk manager
├── Infrastructure/BingX           # REST client, WebSocket client, market worker
├── Infrastructure/OpenAI          # Responses API + structured AI decision
├── Infrastructure/PaperTrading    # Paper execution và worker xử lý update
└── Configuration                  # Options từ appsettings/environment
```

## Giới hạn và bước tiếp theo

Paper engine vào lệnh tại giá đóng cửa của nến tín hiệu, cộng slippage bất lợi theo cấu hình. Nếu cùng một nến chạm cả SL và TP, engine xử lý SL trước để mô phỏng thận trọng. Vị thế, account state và thống kê được lưu trong SQLite/Postgres; dữ liệu market candle và backtest/dashboard chưa có.

Live trading đã được đăng ký vào DI, sử dụng chiến lược kỹ thuật riêng, không dùng quyết định AI của paper engine. Lệnh mở kèm SL/TP trên sàn; trạng thái live lưu riêng trong `data/live-state.json`. Bot đối chiếu vị thế sau restart và chặn giao dịch khi kết quả lệnh chưa rõ. Xem các giới hạn vận hành trong [LIVE_RUNBOOK.md](LIVE_RUNBOOK.md). Không đưa BingX secret vào prompt.
