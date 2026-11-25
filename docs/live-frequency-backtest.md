# Replay kiểm chứng tần suất giao dịch của Current

## Thiết kế kiểm chứng

So sánh hai phiên bản Current trên cùng danh sách BNB, BTC, DOGE, ETH, LINK, OPN và SOL USDT perpetual. Danh sách kế thừa tập audit 44 ngày đã tải đầy đủ. Mẫu đại diện 7/21 cặp được audit và chọn từ universe hiện hành, nên còn rủi ro sai lệch chọn mẫu. Giữ nguyên danh sách trong các lần chạy. Mỗi phiên bản dùng một tài khoản Paper chung, bắt đầu với 10.000 USDT. Mỗi cửa sổ khởi tạo trạng thái mới, dùng 10 ngày warm-up và nhận nến khung cao khi đã đóng. Giữ nguyên cùng risk, phí, funding và giả định giá khớp giữa hai phiên bản. Không fit tham số theo kết quả replay.

- Điều kiện tần suất ở kịch bản chuẩn là trung bình ít nhất một entry mô phỏng trên mỗi ngày đủ nến tính theo UTC+7. Tính cả vị thế còn mở cuối kỳ. Số vị thế mở là kết quả quan sát và không tác động đến strategy.
- Điều kiện chất lượng là expectancy ròng dương ở ngoài mẫu 1×, chênh lệch PnL đánh dấu cuối kỳ so với Current không âm, và còn mẫu giao dịch với expectancy không âm trong kịch bản stress 2×. Với nguyên nhân không có mẫu ở 2×, báo riêng tỷ lệ setup bị loại bởi risk.
- Điều kiện rủi ro là drawdown đánh dấu ngoài mẫu dưới 5% và không tăng quá một điểm phần trăm so với Current, cùng giới hạn vị thế, rủi ro tổng, drawdown đã chốt, lỗ ngày, exposure danh nghĩa và vốn khả dụng như baseline Paper. Cần đối chiếu riêng hành vi Live thực tế.
- Đợt so sánh hiện tại có holdout theo thời gian và một cửa sổ ngoài audit cũ. Governance với cấu hình hiện tại cần ít nhất ba fold walk-forward, 30 giao dịch ngoài mẫu và 30 giao dịch Shadow. Mẫu này chưa đủ để ước lượng độ ổn định trên hai hướng trend hoặc độ nhạy chọn symbol.

Cấu hình chuẩn dùng 5 bps phí taker mỗi lần khớp, 2 bps slippage mỗi phía và spread toàn phần 4 bps. Mỗi phía chịu 2 bps từ spread. Kịch bản stress nhân đôi fee, spread và slippage. Quy tắc khớp vào tại giá đóng tham chiếu đã điều chỉnh chi phí. Quản lý vị thế ưu tiên SL khi nến đồng thời chạm SL và TP, với gap qua SL khớp theo giá mở nếu bất lợi hơn. Funding đã settle dùng mark price và rate của BingX tại timestamp tương ứng, nhân khối lượng trước các exit nội nến. Funding giữ nguyên trong stress. Replay đóng dấu lệnh vào theo timestamp đóng nến. Các cặp trong cùng timestamp xử lý theo thứ tự ký hiệu cố định. [Phí BingX Perpetual Futures](https://bingx.com/en/support/articles/40029373147289), [BingX Market API](https://github.com/BingX-API/api-ai-skills/blob/main/skills/swap-market/api-reference.md).

Hai cửa sổ dữ liệu đầu vào đã chốt là 44 ngày kết thúc 21/11/2026 05:10 UTC và 15 ngày kết thúc 24/11/2026 17:00 UTC. Trên bộ đầu tiên, các ngày đầy đủ sau warm-up chia theo thời gian theo tỷ lệ xấp xỉ 60/20/20. Bộ 15 ngày chỉ đánh giá các ngày địa phương 22–24/11, sau cận cuối của audit trước và trước thời điểm đóng băng candidate. Dùng riêng cửa sổ này để kiểm tra dữ liệu chưa xuất hiện trong audit, chưa tính là quan sát tiến cứu sau phát triển.

## Kết quả 33 ngày đánh giá hồi cứu

- **19/10–06/11, 19 ngày ở 1×.** Current phát 3 tín hiệu và không mở vị thế. Current Adaptive phát 146 tín hiệu, mở và đóng 4 vị thế trong 3 ngày, trung bình 0,21 vị thế/ngày. PnL ròng +73,74 USDT, expectancy +18,44 USDT, tỷ lệ thắng 50%, profit factor 2,37, drawdown 1,05%.
- **07–13/11, 7 ngày ở 1×.** Current phát 3 tín hiệu và không mở vị thế. Current Adaptive phát 62 tín hiệu, mở và đóng 1 vị thế OPN short, trung bình 0,14 vị thế/ngày. PnL ròng và expectancy −49,84 USDT. Drawdown 0,50%.
- **14–20/11, 7 ngày test ở 1×.** Current phát 3 tín hiệu, mở và đóng 3 vị thế trong 2 ngày, trung bình 0,43 vị thế/ngày. PnL ròng −102,51 USDT, expectancy −34,17 USDT, tỷ lệ thắng 0%, profit factor 0, drawdown 1,61%. Current Adaptive phát 62 tín hiệu, mở và đóng 7 vị thế trong 3 ngày, trung bình 1,00 vị thế/ngày. PnL ròng +97,20 USDT, expectancy +13,89 USDT, tỷ lệ thắng 42,86%, profit factor 1,90, drawdown 1,95%. Chênh lệch PnL đánh dấu cuối test là +199,70 USDT. Candidate có 6 vị thế pullback, tổng +147,78 USDT, và một vị thế breakout/retest, −50,58 USDT.
- **Tổng tần suất qua ba cửa sổ.** Current mở 3 vị thế trong 33 ngày, tương đương 0,09/ngày. Current Adaptive mở 12 vị thế trong 33 ngày, tương đương 0,36/ngày. Các cửa sổ không chồng lấn và mỗi cửa sổ khởi tạo lại tài khoản.
- **Risk và chi phí ở đoạn test 1×.** Current có tối đa 2 vị thế đồng thời, peak marked notional 4.130,97 USDT và peak open risk 1,00% số dư. Current Adaptive có tối đa 3 vị thế, peak marked notional 6.501,66 USDT, peak open risk 1,50%, và lỗ thực hiện trong ngày tối đa 1,04% theo ngày UTC của risk engine. Candidate chặn 48 lượt theo giới hạn vị thế và 67 lượt theo circuit breaker. Chuỗi lỗ đã đóng dài nhất là 3 ở Current và 4 ở candidate. Circuit breaker giới hạn entry trong khoảng nghỉ, các vị thế đã mở có thể tiếp tục chốt lỗ. Phí của các vị thế đã đóng ở Current và candidate lần lượt là 6,25 và 20,31 USDT. Slippage ước tính là 2,50 và 8,12 USDT, với chi phí nửa spread cùng độ lớn. Funding ròng lần lượt là credit 0,52 và 0,28 USDT.
- **Stress 2×.** Cả hai phía có 0 vị thế trong cả ba đoạn. Ở đoạn test, cả 63 tín hiệu candidate dừng ở R/R có trọng số sau chi phí, dưới sàn 1,50. Khi không có fill, expectancy, win rate và profit factor chưa xác định.

Toàn bộ 12 giao dịch candidate là short khi regime được gắn `TrendingDown`. Cửa sổ đầu có lượt đánh giá `TrendingUp` nhưng không có fill long. Ở đoạn test, DOGE và OPN đóng góp +75,82 và +75,13 USDT. Bỏ hai khoản đóng góp khỏi kết quả ghi nhận thì PnL còn −53,75 USDT. Phép tính này đo độ tập trung theo symbol, chưa tái chạy danh mục với cặp bị loại. Số giao dịch quá ít để ước lượng độ ổn định theo regime hoặc khoảng bất định có ý nghĩa.

## Kết quả trên ba ngày sau audit trước

- Có 6.048 lượt đánh giá mỗi phía. Current có 0 tín hiệu. Current Adaptive có 5 tín hiệu short pullback-reclaim trên BTC, ETH và DOGE. Chưa thấy tín hiệu breakout/retest ở cửa sổ này. Nhánh 1h loại 5.676 lượt ở Current và 4.650 ở Current Adaptive. Candidate vẫn loại 948 lượt do bối cảnh 30m ngược xu hướng.
- Cả hai phía có 0 vị thế mở ở 1× và 2×. Risk trace cho thấy cả năm tín hiệu candidate hụt sàn R/R có trọng số sau chi phí là 1,50. Khoảng R/R là 1,194–1,297 ở 1× và 0,826–0,981 ở 2×. Tần suất đạt 0/ngày trên cả ba ngày UTC+7.
- Không có mẫu để tính tỷ lệ thắng, expectancy hoặc profit factor. PnL, drawdown, exposure, phí và funding phát sinh đều bằng 0 vì không có vị thế. Kết quả xác nhận thêm tín hiệu kỹ thuật và chưa đạt gate về tần suất hoặc chất lượng để mở Live.

**No-Go.** Candidate đạt trung bình 1 vị thế/ngày trong một cửa sổ hồi cứu ngắn ở 1×. Tần suất giảm còn 0 ở ba ngày sau audit và trong stress 2×. Mẫu chưa xác nhận hiệu quả đi qua nhiều chế độ thị trường và điều kiện chi phí. Giữ candidate sau feature flag để theo dõi Paper/Shadow trên dữ liệu đến sau thời điểm đóng băng. Lần thử kế tiếp nên kiểm định setup có payoff vượt chi phí ở R/R hiện tại, đo theo nhánh entry và nhóm thanh khoản đã chốt trước. Ưu tiên hash snapshot và tránh chọn lại symbol từ PnL quan sát.

## Tái lập

Sử dụng snapshot OHLCV cũ trong `artifacts/market-history-44d-20261121`, snapshot mới trong `artifacts/live-frequency-backtest/fresh-history` và funding trong `artifacts/live-frequency-backtest/funding`. Mỗi kết quả JSON có SHA-256 của nến, metadata hợp đồng và funding. Runner kiểm tra số nến, biên thời gian, hash và khoảng cách giữa các nến. Hai phiên bản dùng dữ liệu đồng bộ đủ bảy cặp. Đối chiếu riêng xác nhận OHLCV giống nhau trên 3.314 nến chồng lấp của từng cặp. Metadata precision và số lượng tối thiểu là snapshot lúc tải, chưa phục dựng được thay đổi lịch sử. Thư mục 150 ngày của audit trước chỉ có metadata.

Funding lấy qua endpoint công khai `GET /openApi/swap/v2/quote/fundingRate` với các trang 14 ngày, cận biên truy vấn 08/10/2026 05:10 đến 24/11/2026 17:00 UTC. Sáu cặp có 143 settlement, cách nhau đúng 8 giờ. OPN có 285 settlement, cách nhau đúng 4 giờ. Runner từ chối sự kiện trùng, lệch cặp, nằm ngoài phạm vi, lệch khung 5m, mất coverage quá tám giờ hoặc mark price không dương. Khoản thanh toán có dấu được chuyển vào Paper, bao gồm credit khi rate và chiều lệnh tạo funding nhận về. Đã chạy 13/13 test `PositionManagerTests`, gồm các trường hợp funding có dấu, khối lượng tại thời điểm settle và nến trùng.

Đã đối chiếu độc lập 16 dòng kết quả về hash, cận cửa sổ UTC+7, lượt đánh giá, fill theo ngày, symbol và setup, PnL, exposure, phí, funding và số vị thế. Ví dụ lệnh short OPN ở đoạn validation nhận funding 0,16173045552 USDT, khớp settled rate 0,00005, mark 0,03558 và số lượng 90.910,88. Kết quả sử dụng risk Paper với 0,5% mỗi vị thế, tối đa 3 vị thế, giới hạn lỗ ngày và open risk 2%, drawdown đã chốt 5% và exposure danh nghĩa 300%. Mô phỏng chưa có giá bid/ask lịch sử, hàng đợi lệnh, thông số hợp đồng theo từng ngày, latency, market impact, liquidation, phí cá nhân hoặc vòng đời executor Live.

```powershell
dotnet build tools/TradingBot.Backtest -c Release --no-restore -o artifacts/live-frequency-backtest/final-bin
.\tools\download-history.ps1 -Symbols BNB-USDT,BTC-USDT,DOGE-USDT,ETH-USDT,LINK-USDT,OPN-USDT,SOL-USDT -Days 15 -EndUtc '2026-11-24T17:00:00Z' -OutputDirectory artifacts/live-frequency-backtest/fresh-history
$runner = 'artifacts/live-frequency-backtest/final-bin/TradingBot.Backtest.dll'
$funding = '--funding-directory=artifacts/live-frequency-backtest/funding'
foreach ($factor in 1,2) {
    dotnet $runner artifacts/market-history-44d-20261121 "artifacts/live-frequency-backtest/final-retrospective-${factor}x.json" compare-current $factor $funding --risk-trace
    dotnet $runner artifacts/live-frequency-backtest/fresh-history "artifacts/live-frequency-backtest/final-holdout-${factor}x.json" compare-current $factor $funding --risk-trace --evaluate-from=2026-11-21T17:00:00Z --evaluate-until=2026-11-24T17:00:00Z
}
```

Bản chạy kết quả dùng .NET SDK 11.0.100, Worker và replay target .NET 8. Candidate `current-adaptive-v1` có SHA-256 `13761A442244D20E64AE6C02AA50DA2889FA24E23000B5F886736B508326F56D` ở `AdaptiveCurrentEntryPolicy.cs`. Trial sơ bộ lưu trong `artifacts/live-frequency-backtest/pre-fix` và loại khỏi đánh giá chính thức vì một fixture tổng hợp phát hiện nến phá biên có thể được gán loại pullback trước retest. Nhóm sửa lỗi cấu trúc trước khi đánh giá kết quả replay, rồi chạy lại cả hai kịch bản chi phí với cùng bộ dữ liệu và thông số.
