# Tần suất giao dịch Live của Current Strategy

**No-Go cho bản adaptive trên Live.** Logic mới nằm trong đường thực thi Current và được bảo vệ bằng cờ mặc định tắt. Chỉ xem xét kích hoạt sau khi có bằng chứng ngoài mẫu, kết quả Paper tiến về phía trước và phê duyệt vận hành cho đúng binary, cấu hình và tài khoản.

## Vì sao tần suất thấp

Trong audit 14–21/11/2026 UTC, 33.838 trong 33.840 lượt đánh giá 5m dừng trước khi phát tín hiệu kỹ thuật. Regime 1h là lý do đầu tiên của 19.365 lượt (57,23%), tiếp theo là trend hoặc cấu trúc 30m ở 5.464 lượt (16,15%). Có 5.003 lượt bị chặn ở 30m không đạt điều kiện cấu trúc. Kiểm tra EMA lặp bên trong breakout đã được wrapper xác nhận cho cùng hướng, nên phần lặp tự nó không giải thích thêm một lượt từ chối. Hai tín hiệu còn lại, LINK-USDT và OPN-USDT, dừng ở parser income Live khi BingX trả `code: 0, data: null`. Lỗi parser đã được sửa trong source trước đợt này. Audit ghi nhận **0 vị thế Live được mở** trong cửa sổ đó. [Phễu tín hiệu](strategy-funnel-validation.md), [audit đặt lệnh](trading-pipeline-audit.md).

## Logic đưa vào Current

- Dùng nến 5m, 30m và 1h đã đóng. Chặn dữ liệu thiếu, cũ hoặc đứt quãng và bỏ pending setup tương ứng.
- Dùng EMA 20/50/200 trên 1h cho hướng. Trend đã xác lập dùng ADX tối thiểu 20 theo cấu hình hiện tại. Vùng ADX đang tăng từ 75% ngưỡng trở lên chỉ cho breakout/retest nếu giá 1h lấy lại EMA nhanh và cấu trúc 30m cùng hướng. Trend đã xác lập vẫn từ chối cấu trúc 30m ngược chiều hoặc giá 30m nằm sai phía EMA chậm. Sideways và ATR 1h ngoài dải tiếp tục bị chặn.
- Giữ breakout/retest 5m, volume, thân nến và dải RSI. Kiểm tra lại động lượng, khoảng cách tới EMA và ATR từ các nến 15m đóng trước nến trigger. Thêm pullback theo EMA 5m cho trend đã xác lập, cần giá reclaim pivot, RSI cải thiện theo đúng hướng, volume và khoảng cách theo ATR. Swing pullback được dùng cho giá vô hiệu hóa setup.
- Dùng lại khối tính SL, TP, net R/R và độ lớn vị thế. Giữ cấu hình symbol và danh sách loại trừ. Quy tắc hợp đồng hoạt động, minimum order, spread và exposure tiếp tục áp dụng. Dữ liệu quan sát chưa đủ cơ sở để mở rộng danh sách hoặc chọn symbol dựa trên PnL của cùng mẫu.

Giả thuyết là một xác nhận 30m bớt trùng lặp và hai loại trigger có thể tăng lượt khớp mà vẫn giữ expectancy dương sau chi phí. Nghiên cứu time-series momentum của Moskowitz, Ooi và Pedersen đo sự tiếp diễn từ một đến mười hai tháng trên futures truyền thống. Đây là cơ sở để kiểm tra cơ chế trend, còn kết quả 5m phải dựa trên replay và Paper của BingX. [Nghiên cứu gốc](https://www.sciencedirect.com/science/article/pii/S0304405X11002613).

## Kết quả backtest

- **33 ngày hồi cứu hoàn chỉnh, 7 hợp đồng.** Ở chi phí cơ sở, Current cũ có 9 tín hiệu và 3 lượt khớp (0,09/ngày). Bản adaptive có 270 tín hiệu và 12 lượt khớp (0,36/ngày). Các đoạn được chạy với tài khoản mới, nên số lượt khớp có thể cộng để đọc tần suất nhưng mỗi đoạn có đường vốn riêng.
- **Đoạn test hồi cứu 14–20/11 theo ngày Việt Nam.** Current cũ có 3 lượt khớp, expectancy −34,17 USDT và max drawdown theo vốn đánh dấu 1,61%. Bản adaptive có 7 lượt khớp, expectancy +13,89 USDT và drawdown 1,95%. 7 lượt đó tập trung trong 3/7 ngày. Đoạn này chồng lấn audit đã biết.
- **Dữ liệu mới hơn 22–24/11 theo ngày Việt Nam.** Current cũ có 0 tín hiệu, bản adaptive có 5 pullback SHORT. Mọi tín hiệu mới đều có net R/R dưới 1,5. Cả hai phiên bản có 0 lượt khớp, nên không có mẫu để ước tính expectancy.
- **Chi phí stress 2×.** Cả hai phiên bản không có lượt khớp ở các cửa sổ đã chạy. Giả định cơ sở là phí 5 bps mỗi lần khớp, spread toàn phần 4 bps và slippage 2 bps mỗi phía. Stress nhân đôi ba khoản đó và giữ các khoản funding lịch sử. Khớp theo giá đóng 5m đã điều chỉnh, với SL trước khi cùng nến có thể chạm SL và TP.

Replay dùng một tài khoản Paper chung cho bảy hợp đồng và cùng rule sizing ở cả hai phiên bản. Risk mỗi lệnh mô phỏng là 0,5%. Kết quả không đủ để suy ra fill, PnL hay rủi ro trên tài khoản Live. [Dữ liệu, chi phí, exposure và lệnh tái lập](live-frequency-backtest.md).

## Cấu hình và phê duyệt

```json
{
  "Strategy": {
    "UseMultiTimeframeAnalysis": true,
    "UseAdaptiveCurrentEntries": false
  },
  "Live": {
    "AllowAdaptiveCurrentEntries": false
  },
  "TradingFrequency": {
    "Enabled": true,
    "TargetEntriesPerDay": 1,
    "TimeZone": "SE Asia Standard Time",
    "CountOnlyFilledEntries": true,
    "ForceEntryToMeetTarget": false
  }
}
```

Paper có thể dùng cờ Strategy với một DB mới để thử nghiệm. Live đọc snapshot được phê duyệt trong DB. Bootstrap Live vẫn tạo Current cũ ngay cả khi cờ Strategy được đặt ở cấu hình tiến trình. Để đưa bản adaptive vào luồng entry Live cần snapshot MTF đã đăng ký, bằng chứng mang đúng hash cấu hình và phiên bản đối chứng còn active, kiểm định walk-forward/out-of-sample và Shadow, reviewer phê duyệt promotion, và `Live__AllowAdaptiveCurrentEntries=true`. Trạng thái halt của Live vẫn có ưu tiên trước các ghi lệnh. Cấu hình tần suất chỉ được dùng bởi endpoint giám sát. Khi đặt `ForceEntryToMeetTarget=true`, endpoint báo cấu hình không hợp lệ.

`Live:Enabled=true` đã có trong cấu hình nguồn trước đợt này. Gói staging đặt `Bot:Enabled=false` và `Live:Enabled=false`. Khi chạy Paper từ source, đặt riêng `Live__Enabled=false` và đường dẫn DB thử nghiệm.

Giới hạn trong cấu hình đã audit gồm rủi ro tối đa 2% mỗi lệnh Live, 2% tổng open stop risk, 2% lỗ ngày, 5% drawdown và 300% tổng danh nghĩa theo giá entry, tối đa 3 vị thế theo policy và trần margin 20%. Leverage dự phòng 5× và auto leverage tối đa 20× hoặc trần hợp đồng nếu thấp hơn. SL dùng cấu trúc và 1,5 ATR, với trần 3 ATR. Net R/R tối thiểu 1,5. Dừng entry 60 phút sau 3 lệnh lỗ liên tiếp. Ngày kiểm soát lỗ Live vẫn theo UTC. Kỳ KPI dùng ngày Việt Nam. Các con số cần đối chiếu với snapshot đang active trước khi duyệt vận hành. [Chi tiết risk và execution](live-trading-readiness.md).

## Theo dõi Live

Gọi `GET /api/bot/live/performance` với `X-Api-Key`. Endpoint đếm mỗi TradeId đã có bằng chứng fill một lần, báo tiến độ mục tiêu theo ngày địa phương và trả lý do reject theo stage. Các chỉ số PnL, phí, tỷ lệ thắng, expectancy và drawdown dựa trên các khoản đóng vị thế đã đối soát. Exposure theo các vị thế bot quan sát và giá nến còn mới. Chỉ số thiếu dữ liệu cần được đọc cùng độ phủ journal và diagnostics.

Nếu chưa đạt mục tiêu, ưu tiên nguồn dữ liệu hoặc lỗi sàn khi không đủ độ phủ hay có giao dịch chưa rõ kết quả. Với ngày đủ dữ liệu, dùng lý do reject ở đầu phễu để xây replay đối chứng. Đánh giá trên nhiều ngày đầy đủ và theo dõi tỷ lệ ngày có lệnh cùng trung bình fills/ngày.

## Kiểm thử và quyết định triển khai

- **294/294 regression tests Release** đã qua. Các fixture bao gồm chặn bootstrap adaptive trên Live, yêu cầu evidence và approval, khớp đúng baseline active, cờ Live, rollback, daily loss và giới hạn vị thế, halt trước ghi lệnh, và quản lý vị thế đang mở khi halt.
- **Postman loopback** dùng khóa giả và kiểm tra HTTP thành công, rate limit, lỗi nghiệp vụ, HTML lỗi, timeout sau khi mock ghi lệnh, khớp một phần, income trống và đối soát bảo vệ vị thế LONG/SHORT. Trường hợp timeout sau khi ghi lệnh có đúng một request và một lần ghi vào mock. Khi chưa xác định kết quả lệnh, engine giữ trạng thái chặn.
- **Paper trên feed công khai ngày 25/11.** Phiên SQLite riêng nhận đủ nến 5m đóng lúc 08:20 giờ Việt Nam của bảy hợp đồng. Snapshot `current-adaptive-v1` xử lý 7/7 lượt không lỗi và 0 setup được chấp nhận. Có hai lượt dừng ở regime, hai lượt xung đột trend, hai lượt thiếu setup và một lượt vượt trần volume. Phiên đã dừng sau lượt kiểm tra. Live tắt trong suốt phiên. Đây là kiểm tra đường dữ liệu tiến về phía trước trên một mốc nến. [Kết quả Paper](../artifacts/live-frequency-20261125/paper-forward-final/paper-forward-summary.json).
- **IIS staging** gồm 58 tệp với Bot, Live và cả hai cờ entry adaptive đều tắt. Gói không mang credentials, state hay cấu hình Development. Smoke loopback nhận HTTP 200 ở liveness, 503 ở readiness khi worker tắt và 401 khi gọi API performance thiếu key. [ZIP staging](../artifacts/live-frequency-20261125/iis-candidate.zip), [SHA-256 từng tệp](../artifacts/live-frequency-20261125/iis-candidate-sha256.json).
- **IIS đích** vẫn có quyền Modify kế thừa cho Authenticated Users trên thư mục deploy, DLL, `web.config` và thư mục state. `web.config` có biến môi trường mang thông tin xác thực và override bật/tắt Live. AppCmd không đọc được identity app pool do quyền truy cập. IP allowlist của key từng thiếu trong lần audit trước và cần xác nhận lại. [Điều kiện triển khai IIS](live-trading-readiness.md).

Tần suất 1,00/ngày trong một đoạn test hồi cứu không lặp lại trên đoạn mới hơn và biến mất ở giả định chi phí stress. Mẫu cũng chưa đủ số fold hoặc số lệnh Paper tiến về phía trước cho governance. Các điều kiện thực thi đã qua trong fixture. Các quyết định triển khai và mở entry còn cần dữ liệu tài khoản, khả năng đối soát và quyền host phù hợp. Trạng thái của bản adaptive là **No-Go**.

## Rollback và emergency stop

1. Đặt halt cho đúng mode và DB bằng quy trình trong runbook, rồi xác nhận hiệu lực. Đối chiếu pending intent theo client order ID và kiểm tra lệnh bảo vệ đang có trên sàn trước khi thay tiến trình. Halt chặn entry mới, còn các yêu cầu đã gửi cần được đối soát. [Tài liệu lệnh BingX](https://github.com/BingX-API/api-ai-skills/blob/main/skills/swap-trade/api-reference.md#9-query-order-details).
2. Tắt `Live__AllowAdaptiveCurrentEntries`. Nếu snapshot adaptive đã active, dùng `--strategy-rollback=<legacy-version> --mode=LIVE --expected-active=<adaptive-version> --approved-by=<operator>` theo quy trình triển khai đã duyệt. Xác nhận version/hash legacy và bảo toàn journal, state, pending order và outbox. Các vị thế đã mở tiếp tục dùng snapshot đã gắn khi vào lệnh. Khi cài lần đầu, xác nhận binary mới đã chạy trong trạng thái halt trước khi promotion snapshot adaptive.
3. Đối chiếu các lệnh và vị thế tại BingX, xác nhận readiness và feed, rồi chỉ gỡ halt sau phê duyệt vận hành. Nếu cần rollback binary, dùng bản đã kiểm tra tương thích state và có bản sửa income null. [Runbook Live](../LIVE_RUNBOOK.md).
