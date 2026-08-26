# BingX Trading Bot - Phase 1

Bot .NET Worker cho BingX USDT-M Perpetual Futures. Giai đoạn này có thể dùng OpenAI để phân tích quyết định, nhưng chỉ paper trade; không có code đặt, sửa, hủy lệnh thật.

## Phạm vi giai đoạn 1

- REST backfill nến đóng cho `15m`, `1h`, `4h`.
- WebSocket market data chính thức cho ba kênh K-line.
- Strategy mẫu: trend 4H + 1H bằng EMA 20/50, breakout 15m, volume xác nhận.
- Signal engine và risk manager: rủi ro 0.5%/lệnh, dừng ngày ở mức lỗ 2%, leverage paper tối đa 3x, SL theo ATR 1.5 lần, TP 2R.
- Paper execution: một vị thế tại một thời điểm, mô phỏng phí, SL/TP và log JSON.
- OpenAI tùy chọn: trả về `long`, `short` hoặc `no_trade` bằng Structured Outputs; Risk Gate luôn kiểm tra lại SL/TP, confidence, entry deviation, daily loss và leverage.
- Khi OpenAI bật nhưng API key thiếu, lỗi hoặc response sai schema, bot fail-closed về `no_trade`.
- BingX API key/secret chưa được sử dụng vì Phase 1 chỉ dùng public market data và chưa có live executor.

## Endpoint BingX đang dùng

Các endpoint được kiểm tra thực tế ngày 2026-08-26:

| Mục đích | Endpoint |
| --- | --- |
| Server time | `GET /openApi/swap/v2/server/time` |
| Contract info | `GET /openApi/swap/v2/quote/contracts?symbol=BTC-USDT` |
| K-line | `GET /openApi/swap/v3/quote/klines?symbol=BTC-USDT&interval=15m&limit=250` |
| WebSocket | `wss://open-api-swap.bingx.com/swap-market` |

WebSocket subscribe theo dạng `{symbol}@kline_{interval}`, ví dụ `BTC-USDT@kline_15m`. Client xử lý cả payload GZIP và heartbeat `Ping`/`Pong`, đồng thời nhận được cả dạng `data` object và array đang xuất hiện trong runtime BingX.

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

File này đã được `.gitignore`, không đưa lên GitHub. API key/secret BingX hiện chỉ được lưu cho bước tích hợp tài khoản về sau; Phase 1 vẫn chỉ dùng market data công khai và paper trading.

### Bật OpenAI phân tích paper trading

Tạo API key của chính bạn trong OpenAI Platform, sau đó truyền qua environment variable. Không gửi key trong chat và không ghi key vào file cấu hình:

```powershell
$env:OPENAI_API_KEY = "your-key-from-openai-platform"
$env:TRADINGBOT__OPENAI__ENABLED = "true"
$env:TRADINGBOT__OPENAI__MODEL = "gpt-5.2"
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

Mỗi nến 15m đã đóng, bot gửi dữ liệu nến gần nhất của 15m/1h/4h cùng technical context lên OpenAI Responses API với `store=false`. AI chỉ trả về quyết định có schema cố định. Paper engine dùng giá thị trường hiện tại, còn SL/TP của AI phải vượt qua Risk Gate trước khi mô phỏng lệnh.

Có thể cấu hình bằng biến môi trường với prefix `TRADINGBOT__`, ví dụ:

```powershell
$env:TRADINGBOT__BINGX__SYMBOL = "BTC-USDT"
$env:TRADINGBOT__RISK__STARTINGBALANCE = "10000"
$env:TRADINGBOT__RISK__RISKPERTRADEPERCENT = "0.5"
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

Hoặc sao chép `.env.example` thành `.env` khi chạy qua Docker Compose. Không đưa API secret vào `appsettings.json`, `.env.example` hoặc Git.

## Chạy Docker

```powershell
docker compose up --build
```

Log cần quan sát:

- `Loaded ... closed candles` - REST backfill thành công.
- `Connected to BingX perpetual market WebSocket` - stream thành công.
- `PAPER OPEN` / `PAPER CLOSE` - paper engine đã mô phỏng một giao dịch.

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

Paper engine vào lệnh tại giá đóng cửa của nến tín hiệu. Nếu cùng một nến chạm cả SL và TP, engine xử lý SL trước để mô phỏng thận trọng. State hiện chỉ nằm trong memory, chưa có database, backtest, dashboard hay cơ chế khôi phục vị thế sau restart.

Live trading chưa được đăng ký vào DI và không có executor gọi endpoint order. Khi chuyển giai đoạn, cần thêm adapter riêng dùng `BINGX_API_KEY` và `BINGX_API_SECRET` từ environment variables, test trên demo/sandbox, bổ sung idempotency, position reconciliation, kill switch, persistence và kiểm tra quyền API không bao gồm rút tiền. OpenAI chỉ tạo quyết định có cấu trúc; tuyệt đối không đưa BingX secret vào prompt hoặc cho model tự giữ credential.
