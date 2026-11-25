# Phễu tín hiệu Current trong 7 ngày

Tái truy vấn Seq từ **14/11/2026 05:13:06 UTC** đến **21/11/2026 05:13:06 UTC**, cận cuối không tính. Có **33.840 lượt đánh giá Live**, tất cả là các cặp symbol và thời điểm đóng nến 5m khác nhau. **33.838 lượt không phát tín hiệu kỹ thuật**. Hai lượt còn lại phát SHORT LINK-USDT và OPN-USDT ngày 15/11. Đây là hai tín hiệu kỹ thuật phân biệt được. Quan hệ phụ thuộc giữa các tín hiệu và tổng số setup breakout/retest độc lập chưa thể xác định từ log cũ.

Seq có 34.091 nến thị trường không trùng ở 21 symbol. Tất cả lượt đánh giá Live ghép được với nến thị trường. Có 251 nến được ghi ở luồng thị trường nhưng không có event đánh giá Live tương ứng. Các dòng đó nằm ngoài mẫu 33.838 và chưa có đủ dấu vết để quy từng dòng cho một guard. **5m là timeframe quyết định**. 1h, 30m và 15m là timeframe được tham chiếu trong cùng một lượt.

## Lý do đầu tiên dừng đánh giá

Theo số lượt đánh giá 5m, với mẫu số chung là 33.840:

- Regime 1h không phù hợp cho trend entry: **19.365 (57,23%)**. Gồm 17.529 `Sideways` và 1.836 `HighVolatility`.
- Trend hoặc cấu trúc 30m không khớp 1h: **5.464 (16,15%)**. Gồm 5.436 `TrendingDown` và 28 `TrendingUp`.
- Chờ breakout/retest đủ điều kiện hoặc cooldown: **3.496 (10,33%)**. Gồm 3.465 `TrendingDown` và 31 `TrendingUp`. Log cùng một lý do chung cho cả trường hợp chưa từng có breakout và trường hợp một retest đã chờ nhiều nến.
- Momentum 15m chưa đạt hoặc giá vào đã quá xa: **2.875 (8,50%)**. Gồm 2.861 `TrendingDown` và 14 `TrendingUp`.
- Dữ liệu cũ hoặc thiếu nến: **2.638 (7,80%)**, toàn bộ `Unknown` vì nhánh này dừng trước tính regime.

Tổng rejection theo regime là 17.529 `Sideways`, 11.762 `TrendingDown`, 2.638 `Unknown`, 1.836 `HighVolatility`, 73 `TrendingUp` và 0 `LowVolatility`. [Dữ liệu chi tiết](strategy-funnel-validation.json) có 109 tổ hợp thực tế của symbol, 5m, regime và lý do đầu tiên. Trường `bySymbol` có tổng nến, đánh giá, từ chối và số tín hiệu kỹ thuật cho từng symbol. Có 19 symbol không có tín hiệu kỹ thuật và hai symbol có một tín hiệu. Không cộng riêng các lớp xác nhận để suy ra thêm lượt đánh giá.

Tỷ lệ chặn có điều kiện cho thấy khi đã vượt qua kiểm tra dữ liệu, 19.365/31.202 lượt dừng ở 1h. Khi đã qua 1h, 5.464/11.837 dừng ở 30m. Khi đã qua 30m, 2.875/6.373 dừng ở 15m. Còn 3.498 lượt tới breakout/retest 5m và chỉ 2 phát tín hiệu. Đây là tỷ lệ theo **lượt**, không đo tỷ lệ mất setup hoặc hiệu quả của việc bỏ một bộ lọc.

## Đợt phá biên giá dùng để đối chiếu setup tiềm năng

Trong 32.423 nến có đủ 12 nến 5m liên tục liền trước trong log, 4.630 nến đóng cùng hướng thân nến và vượt đỉnh hoặc đáy của 12 nến trước. Gom các nến kề nhau phá biên cùng hướng trên cùng symbol thành một đợt, rồi đối chiếu lý do Live tại nến mở đầu. Có **3.169 đợt**:

- Regime 1h là lý do đầu tiên của **1.826 đợt (57,62%)**. Trong đó 1.209 có ADX dưới 20, với 229 đồng thời không xếp đúng bộ EMA.
- Xung đột 30m là lý do của **509 đợt**. Có 466 đợt thực sự fail điều kiện cấu trúc.
- Nhóm 15m có **335 đợt**. Có 289 đợt đã đạt điều kiện RSI theo hướng trend 1h.
- Nhóm chờ breakout/retest hoặc cooldown có **268 đợt**, dữ liệu cũ hoặc thiếu nến có **206 đợt**, và **25 đợt** thiếu event Live tại nến mở đầu.

Đây là **đại diện dựa trên giá**, chưa áp regime, xác nhận khối lượng, khoảng cách, cooldown hay retest. Hai tín hiệu đã xác nhận xảy ra ở nến retest, nên không xuất hiện như một phát tín hiệu tại nến mở đầu của đợt phá biên. Đợt có thể chịu ảnh hưởng chung giữa các symbol hoặc tái xuất hiện sau một nến nghỉ. Số liệu cho thấy thứ tự các gate chặn nhiều nhất trên tập đợt quan sát được. Nó chưa chứng minh bao nhiêu setup có thể khớp lệnh, hoặc thêm bao nhiêu setup nếu bỏ từng gate.

## Đối chiếu chỉ báo và trùng lặp

- **EMA và MTF.** Khi hướng signal trùng với trend đã qua wrapper, kiểm tra EMA nhanh/chậm và close so với EMA nhanh ở 30m/1h trong `BreakoutStrategy` lặp lại các điều kiện đã qua của `MultiTimeframeStrategy` trên cùng nến đã đóng. Wrapper còn yêu cầu EMA200 ở 1h và cấu trúc ở 30m. Phép lặp không tạo thêm rejection cho một hướng đã qua đầy đủ wrapper trên cùng lượt. Điều kiện 1h và 30m vẫn có thể xung đột trong pullback. Trong 5.464 lượt dừng ở 30m, 5.003 không đạt cấu trúc, còn 461 đạt cấu trúc và phải fail EMA hoặc close. Log thiếu EMA và close 30m nên chưa tách được phần giao nhau.
- **ADX, EMA và ATR 1h.** Trong 19.365 lượt dừng ở 1h, có 1.836 ATR vượt trần 3% và 0 dưới sàn 0,1%. Trong nhóm còn lại, 10.369 có ADX dưới 20 dù xếp đúng bộ EMA, và 2.344 đồng thời có ADX dưới 20 và bộ EMA không cùng hướng. Có 1.697 lượt ADX đạt nhưng bộ EMA không cùng hướng. Ở 3.119 lượt còn lại, theo thứ tự điều kiện trong source, close 1h không ở đúng phía EMA nhanh. Giá đóng 1h không có trong event, nên kết luận cuối được suy ra bằng loại trừ. Chưa phân rã được đóng góp riêng của từng chỉ báo khi chúng đồng thời fail.
- **RSI và khoảng cách.** Ở 15m, RSI cần ở phía 50 theo hướng trend. Chỉ 420/2.875 lượt thuộc nhánh 15m thực sự sai phía RSI. 2.455 lượt đã qua RSI và fail vị trí so với EMA, ATR dương hoặc khoảng cách giá đến EMA theo ATR. RSI 5m dùng ngưỡng 70/30 ở breakout/retest để loại quá mua hoặc quá bán. Hai quy tắc áp trên timeframe khác nhau và có thể cùng đạt. ATR% 1h chọn regime, ATR 15m đo extension và cấp độ rủi ro, ATR 5m trước breakout giới hạn độ xa khi retest. Chúng đều nhạy với biến động nhưng kiểm tra những điều kiện riêng.
- **Volume.** Breakout yêu cầu volume ít nhất bằng trung bình 20 nến trước và dưới ba lần mức đó theo cấu hình đã audit. Retest dùng trần volume và cần có trung bình hợp lệ. Các ngưỡng tạo một khoảng có thể đạt. Giá phá biên nhanh vẫn có thể đồng thời fail trần volume hoặc mức extension 15m. Log cũ không lưu bước pending, phiên bản setup, các giá trị ngưỡng thực tế tại retest hoặc từng điều kiện fail, nên không quy 3.496 lượt trong nhóm 5m cho volume, RSI, thiếu retest hay cooldown.
- **MACD.** Cấu hình Live/Paper đã audit tắt MACD và truy vấn cùng cửa sổ không có event quyết định MACD. Nếu được bật, bộ lọc yêu cầu histogram cắt qua 0 đúng hướng ngay trên nến tín hiệu. Điều kiện đó có thể không trùng thời điểm retest dù xu hướng cùng hướng, nhưng không giải thích rejection lịch sử.

Phễu xác nhận regime 1h đứng đầu về số lần dừng và các điều kiện trend có phần lặp. Chưa có bằng chứng hiệu quả sau chi phí để đổi bất kỳ bộ lọc nào. Nếu cần đo ở lần quan sát sau, ghi khóa `strategy/version + symbol + hướng + thời điểm breakout đầu tiên`, trạng thái `detected/waiting/confirmed/invalidated/expired` và các điều kiện fail đồng thời. Theo dõi cả số nến được đánh giá lẫn vòng đời theo khóa. Với các gate trước breakout, dùng một đánh giá đối chứng ngoài luồng đặt lệnh để ghi nhận ứng viên. Việc đánh giá đối chứng cần trạng thái riêng để lượt đo không tiến hoặc reset pending của Current.

## Tái lập

`python tools/analyze-live-funnel.py` truy vấn GET tới Seq loopback. Script chỉ đọc trường tổng hợp và OHLCV đã ghi, chia kết quả theo ngày để tải và đối chiếu số dòng với `count(*)` của toàn cửa sổ. Nó báo lỗi khi một khóa có hai phiên bản khác nhau, hoặc tổng phân nhóm không khớp. JSON lưu các số tổng hợp và hai timestamp tín hiệu, không lưu raw logs hoặc thông tin tài khoản. Cấu hình đã audit và nguồn gốc hai tín hiệu xem trong [trading-pipeline-audit.md](trading-pipeline-audit.md). [Seq Query Syntax](https://docs.datalust.co/docs/the-seq-query-language).
