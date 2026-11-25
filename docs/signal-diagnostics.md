# Signal Diagnostics

Paper, Live và Shadow có nhật ký riêng theo từng lượt xử lý nến đóng 5m. 15m, 30m và 1h là dữ liệu xác nhận trong lượt đánh giá 5m. Các timeframe đó không tạo thêm lượt quét entry. Nếu chưa có nến hoặc worker chưa khởi động được, xem `/health/ready`, `/api/bot/status` và log startup. Báo cáo bắt đầu từ khi chạy phiên bản này.

Gửi các yêu cầu sau với header `X-Api-Key` đã cấu hình trên host.

```http
GET /api/diagnostics/signals/overview?window=7d&mode=LIVE
GET /api/diagnostics/signals/overview?window=24h&mode=SHADOW
GET /api/diagnostics/signals/history?window=30d&symbol=BTC-USDT&decision=REJECTED&limit=50&offset=0
GET /api/diagnostics/signals/history?from=2026-11-14T00:00:00Z&to=2026-11-21T00:00:00Z&strategy=Current&timeframe=5m
```

`window` nhận `24h`, `7d` (mặc định) hoặc `30d`. `from` và `to` lọc theo thời điểm quét UTC và nhận ISO 8601. `to` là cận loại trừ. Cũng có thể lọc theo `version`, `regime` (ví dụ `TrendingUp`, `Sideways`, `HighVolatility`, `LowVolatility`) và `mode` (`PAPER`, `LIVE`, `SHADOW`). Giới hạn mỗi trang là 500. Dùng cùng một `to` khi chuyển trang để giữ cửa sổ cố định.

- `overview` trả số lượt quét, số ứng viên có hướng, số quyết định, lần gửi, đặt, khớp và từ chối lệnh. `groups` phân theo lý do chính, symbol, timeframe, strategy/version/mode, regime và giờ hoặc ngày UTC. Dùng `mode` khi cần so sánh theo một chế độ. Một lượt có thể dừng trước khi chạy strategy và hiện `direction=NONE`, `regime=Unknown` hoặc `version=unresolved`.
- `history` trả từng quyết định, thời điểm quét và đóng nến, giá, volume, hướng, chỉ báo đã tính tại các bước đi qua, mức dự kiến, hash cấu hình của phiên bản đã đăng ký và trạng thái lệnh. `primaryReason` là lý do chặn đầu tiên. `secondaryReasons` chỉ ghi các điều kiện thất bại được xác định tại cùng bước, để tránh chạy lại strategy có trạng thái hoặc phát sinh thêm lời gọi sàn.
- `ACCEPTED` nghĩa là kế hoạch entry vượt qua các kiểm tra trước khi gửi. `NOT_ATTEMPTED` vẫn có thể xảy ra nếu đặt leverage hoặc ghi journal thất bại. `REJECTED` ở **trạng thái thực thi** xác nhận BingX trả mã từ chối. `UNKNOWN` bao gồm timeout hoặc phản hồi không thể xác minh. `PLACED` là đã nhận phản hồi thành công và đang chờ xác nhận khớp đầy đủ. `FILLED` là Paper/Shadow đã lưu fill mô phỏng, hoặc Live đã xác minh fill qua sàn. Luồng Live legacy xác minh hướng, số lượng vị thế và SL/TP trước khi ghi `FILLED`. Luồng Live nâng cao xác minh lệnh khớp theo ID và ghi riêng việc xác minh SL/TP ở `protectionConfirmed`. `lastSuccessfulTrade` là lần xác nhận fill mới nhất trong bộ lọc.

Ví dụ tìm nguyên nhân thiếu lệnh Live. Lọc `mode=LIVE` và xem có lượt quét hay không. Có lượt quét nhưng không có ứng viên thì đọc nhóm lý do của data/strategy và các guard chạy trước strategy. Có ứng viên nhưng chưa được duyệt thì xem MACD, rủi ro, vị thế và điều kiện sàn. Nếu đã duyệt, dùng trạng thái thực thi cùng `orderId` để đối chiếu journal. `PLACED` và `UNKNOWN` phải được đối chiếu với BingX trước khi kết luận lệnh chưa khớp.

Worker tạo bảng phụ `signal_scans` và index `ix_signal_scans_time` thông qua kết nối Postgres/Supabase hoặc SQLite đã cấu hình. Bảng chứa các cột lọc và payload JSON, với ID dùng để ghi lặp an toàn khi DB trả kết quả không rõ. Schema này độc lập với trade history và không cần đổi schema của bảng giao dịch. Worker dùng một queue 2.048 mục, ghi tối đa 4 batch 64 mục mỗi chu kỳ 2 giây. Nếu DB lỗi, worker thử lại theo lô. Nếu queue đầy hoặc có bản ghi chưa lưu lúc tắt, sự kiện được ghi đầy đủ ra sink log dưới `SIGNAL DIAGNOSTICS FALLBACK`, đồng thời tăng `fallbackCount`. Giữ và theo dõi sink log để tìm các sự kiện này, vì API chỉ đọc các bản ghi đã lưu. Việc mất tiến trình đột ngột vẫn có thể mất phần queue chưa lưu. Live order journal hiện có tiếp tục cung cấp bằng chứng độc lập về lệnh.

`SignalDiagnostics__RetentionDays` mặc định 35, giới hạn 1–90 ngày. Mỗi đợt dọn chỉ xóa tối đa 5.000 dòng cũ trong bảng diagnostics. Dùng `SignalDiagnostics__Enabled=false` để dừng thu thập và retention. API vẫn có thể đọc dữ liệu đã lưu. Khi bảng hoặc kết nối chưa sẵn sàng, API trả 503. Trên Postgres, quá trình khởi tạo bật RLS và thu hồi quyền của `PUBLIC`, `anon` và `authenticated` trong cùng transaction với DDL. Dùng tài khoản backend như với trading journal.

## Theo dõi tần suất Live

```http
GET /api/bot/live/performance
GET /api/bot/live/performance?date=2026-11-24
```

Endpoint yêu cầu `X-Api-Key`. Ngày mặc định là hôm nay theo `TradingFrequency:TimeZone`. Có thể chọn một ngày trong 30 ngày vừa qua. `TradingFrequency` chỉ cấu hình báo cáo. Giữ `Enabled=true`, `TargetEntriesPerDay=1`, `TimeZone="SE Asia Standard Time"`, `CountOnlyFilledEntries=true` và `ForceEntryToMeetTarget=false`. Cấu hình ép lệnh khiến báo cáo trả 503. Tắt `Enabled` tắt chỉ tiêu trong báo cáo và vẫn hiển thị số liệu giao dịch.

- `dailyFilledEntries` đếm mỗi `TradeId` Live đã xác nhận một lần theo ngày quan sát xác nhận. Journal dùng `entry_filled` cho bản ghi mới và `entry_protected` cho lịch sử có xác minh vị thế. Dữ liệu lịch sử còn phụ thuộc thời điểm bắt đầu lưu journal. `frequencyWindow` tính trung bình trên 30 ngày lịch đã kết thúc, gồm ngày báo cáo nếu ngày đó đã kết thúc.
- `dailyNetPnlUsdt` và `dailyTradingFeesUsdt` dùng các vị thế bot đã đóng và đối soát, theo ngày quan sát đóng. Phí và funding tính theo cả vòng đời vị thế. Báo cáo dùng `null` khi chưa có mẫu đã đối soát hoặc ngày còn vị thế đóng chưa đối soát. Để xem dòng tiền hay PnL theo ngày ghi sổ, đối chiếu sổ cái tài khoản BingX.
- `performance` trả win rate theo tỷ lệ 0–1 và expectancy USDT trên mẫu đã đối soát của 30 ngày. Drawdown tính trên vốn thực hiện từ các vị thế đóng. Drawdown và chuỗi thua trả `null` khi mẫu còn lệnh đóng chưa đối soát hoặc chưa có lệnh đóng được xác nhận.
- `exposure` chụp vị thế bot đang theo dõi lúc truy vấn và định giá theo giá đóng nến 5m tối đa 10 phút tuổi. Exposure tài khoản bên ngoài bot và lệnh đang chờ không nằm trong phép tính. Tổng exposure trả `null` khi không có trạng thái sẵn sàng hoặc chưa có nến đủ mới cho một vị thế.
- Khi ngày dưới mục tiêu, xem `signalDiagnostics.rejectionReasons` theo bước và `followUp`. Với `ADAPTIVE_CURRENT_LIVE_NOT_ENABLED`, kiểm tra cờ Live và phiên bản chiến lược đang được phê duyệt. Dùng dữ liệu replay ngoài mẫu để đánh giá điều chỉnh khi nguyên nhân chính nằm ở strategy.
