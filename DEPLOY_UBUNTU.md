# Deploy bot trên Ubuntu

Ứng dụng ASP.NET Core .NET 8 chạy market data và giao dịch bằng `BackgroundService`.
Bot tiếp tục chạy khi đóng SSH; HTTP chỉ để theo dõi. Giữ một instance cho mỗi
tài khoản BingX, không scale nhiều replica cùng tài khoản.

## Chuẩn bị

Cài Docker Engine và Compose plugin theo [hướng dẫn Docker cho Ubuntu](https://docs.docker.com/engine/install/ubuntu/).
Clone/copy source và vào thư mục repository. Compose dùng danh sách symbol/strategy
trong `appsettings.json`, biến môi trường ghi đè các mục được chỉ định.

```bash
cp .env.example .env
chmod 600 .env
openssl rand -hex 32
nano .env
```

Điền chuỗi vừa tạo vào `BOT_API_KEY`. `LIVE_ENABLED=false` chạy paper; đổi thành
`true` và điền `BINGX_API_KEY`, `BINGX_API_SECRET` khi bạn chủ động chạy live.
Compose luôn ghi đè `Live:Enabled` trong appsettings bằng biến này.
Để chỉ mở HTTP, không chạy market data/paper/live, đặt `BOT_ENABLED=false`.
Không đưa `.env` vào Git.

Build/publish không đóng gói `data/`, `exports/` hoặc `appsettings.Development.json`.
State và database cần được chuyển riêng theo bước bên dưới.

Mặc định Compose dùng SQLite tại `/app/data/tradingbot.db`. Nếu giữ PostgreSQL
hiện tại, đặt `PERSISTENCE_PROVIDER=Postgres` và điền
`SUPABASE_DB_CONNECTION_STRING`. PostgreSQL vẫn cần volume live state bên dưới.

## Chuyển bot đang chạy sang server

Chỉ chuyển sau khi bạn đã dừng instance cũ. Không chạy đồng thời bản trên máy cũ
và Ubuntu, kể cả khi dùng hai volume riêng: khóa file chỉ bảo vệ cùng một file.
Sao lưu/copy `data/live-state.json` từ content root của bản cũ, giữ nguyên
`PendingOrder`, `Positions` và `JournalOutbox`. Với SQLite, sao lưu DB khi bot đã
dừng; với PostgreSQL, giữ đúng connection string và sao lưu DB theo quy trình cũ.
Không khởi động live bằng state rỗng khi tài khoản còn vị thế do bot quản lý.

Tạo image/volume trước, chưa khởi động bot:

```bash
docker compose build trading-bot
docker compose create trading-bot
```

Nếu cần chuyển dữ liệu cũ, copy từng file còn tồn tại vào container đã tạo:

```bash
docker compose cp ./migration/live-state.json trading-bot:/app/data/live-state.json
docker compose cp ./migration/tradingbot.db trading-bot:/app/data/tradingbot.db
docker compose run --rm --no-deps --user root --entrypoint chown trading-bot -R app:app /app/data
```

`migration/` là thư mục tạm bạn tự chuẩn bị trên server; không commit các file đó.
Nếu bot mới hoàn toàn, bỏ qua copy. PostgreSQL chỉ cần copy live state.

## Chạy nền và kiểm tra

```bash
docker compose up -d --build trading-bot
docker compose ps
docker compose logs -f --tail=100 trading-bot
```

Container chạy bằng user `app`, tự restart khi tiến trình dừng, chờ tối đa 60 giây
khi shutdown và giới hạn log 3 file × 10 MB. Volume `bot-data` giữ SQLite và live
state qua lần tạo lại container. Không dùng `docker compose down -v` nếu cần giữ
dữ liệu. Sao lưu volume và DB trước mỗi lần nâng cấp.

```bash
curl -i http://127.0.0.1:8080/health/live
curl -i http://127.0.0.1:8080/health/ready
read -rs -p 'Bot API key: ' BOT_STATUS_KEY; echo
curl -H "X-Api-Key: $BOT_STATUS_KEY" http://127.0.0.1:8080/api/bot/status
unset BOT_STATUS_KEY
```

`/health/live` báo HTTP host còn đáp ứng. `/health/ready` chỉ trả 200 sau khi
engine khởi tạo xong, không báo lỗi xử lý và có nến 5m trong vòng 10 phút cho mọi
symbol. Khi mới chạy, mất kết nối, symbol chưa tải được lịch sử, lệnh pending
hoặc `BOT_ENABLED=false`, readiness trả 503. Healthy không xác nhận mọi vị thế
trên sàn đã có protection tại thời điểm request; kiểm tra đó vẫn nằm trong live
engine. Restart policy chỉ restart khi tiến trình thoát, readiness 503 không tự
restart container.

Port chỉ bind `127.0.0.1`; truy cập từ máy cá nhân bằng SSH tunnel:

```bash
ssh -L 8080:127.0.0.1:8080 user@your-server
```

Sau đó dùng cùng URL localhost trên máy cá nhân. Nếu cần public API, đặt reverse
proxy HTTPS phía trước và giữ `X-Api-Key`. Không dùng hosting sleep/scale-to-zero
cho bot chạy liên tục.

## Công cụ tùy chọn

Seq và SQLite viewer chỉ chạy khi bật profile `tools`. Đặt `SEQ_URL=http://seq:80`
nếu muốn gửi log vào Seq rồi chạy `docker compose --profile tools up -d`.
Các port 5341 và 8081 chỉ bind localhost; viewer SQLite ở chế độ read-only.
Viewer chỉ dùng với SQLite. Console logs dùng được khi `SEQ_URL` rỗng.

## Logic giao dịch và giới hạn

- Giữ entry 5m, lọc trend 30m/1h, breakout/retest và SL/TP trên sàn.
- Breakout phải đúng chiều; retest cách mức breakout tối đa 1 ATR tính trước breakout.
  Chỉnh `Strategy:MaxRetestEntryDistanceAtr` trong appsettings (`0` tắt giới hạn này).
  Setup bị hủy nếu trend thiếu/cũ hoặc lịch sử có nến đóng phá ngược mức breakout.
  Các snapshot chỉ dùng nến đóng đến thời điểm nến entry, tránh lấy nến mới hơn trong store.
- Hàng đợi chờ khi đầy để không âm thầm bỏ nến đóng. Nếu xử lý chậm, live vẫn bỏ
  tín hiệu quá hạn theo guard hiện có; không vào lệnh từ nến backfill.
- Live xử lý update trước paper để giảm độ trễ khi paper bật AI; paper của update
  trước vẫn có thể làm chậm hàng đợi. Mặc định AI tắt.
- Quantity live tính SL distance cộng phí entry và exit tại SL vào risk budget.
  Đây là ước tính theo `Risk:FeeRate`; slippage/gap/funding có thể làm lỗ thực tế lớn hơn.
- Live chưa có daily-loss cutoff hoặc aggregate-risk cutoff. Hai mục
  `Risk:MaxDailyLossPercent`, `Risk:MaxAggregateOpenRiskPercent` hiện áp dụng cho paper.
  `Live:MaxOpenPositions` giới hạn số vị thế, không thay thế giới hạn tổng lỗ.
- Khóa state, pending-order guard và journal vẫn giữ nguyên. Dừng ứng dụng không
  đóng vị thế trên BingX; đối chiếu sàn trước khi thay cấu hình hay xử lý state.

Chưa có backtest/walk-forward đủ để kết luận tăng win rate hay lợi nhuận. Các
sửa đổi tập trung vào xử lý dữ liệu, ước tính rủi ro và vận hành.

## Kiểm tra API riêng, không khởi động bot

```bash
dotnet publish src/TradingBot.Worker -c Release -o artifacts/ubuntu-entry-filter-ready
Bot__Enabled=false Api__Key=local-api-smoke-key DOTNET_ENVIRONMENT=Production \
  dotnet artifacts/ubuntu-entry-filter-ready/TradingBot.Worker.dll --urls http://127.0.0.1:5087
```

Trong terminal khác:

```bash
postman collection run postman/bot-api.postman_collection.json --no-report-events
```

Collection này chỉ kiểm tra API-only mode. Các lệnh `--preflight`,
`--journal-status`, `--export-live-journal` và `--export-trade-analysis` vẫn chạy
riêng rồi thoát, không mở HTTP/trading loop và không yêu cầu `Api__Key`; giữ content
root/DB đúng như hướng dẫn live hiện có.
