# Replay Current và Experimental

Replay sử dụng nến đã đóng tải từ BingX tại 05:11 UTC ngày 21/11/2026. Mỗi cặp BTC-USDT và ETH-USDT có 8.640 nến 5m liên tục, từ 05:10 UTC ngày 22/10 đến 05:10 UTC ngày 21/11. Runner kiểm tra SHA-256, số lượng, phạm vi, thứ tự và tính liên tục của dữ liệu trước khi chạy. Dữ liệu 15m, 30m và 1h được gộp từ nến 5m, chỉ đưa vào strategy khi đã đóng. [BingX Market Data](https://bingx-api.github.io/docs/)

Mỗi chiến lược dùng tài khoản mô phỏng độc lập 10.000 USDT cho từng symbol, cùng bộ nến, metadata contract, các giới hạn risk theo cấu hình Paper và cùng engine khớp lệnh. Cả hai áp dụng giới hạn drawdown trên vốn đã chốt và giới hạn phơi nhiễm danh nghĩa như Shadow. Kịch bản chuẩn dùng phí 5 bps, spread 4 bps và trượt giá 2 bps mỗi lần khớp. Mức funding 0 là giả định. Kịch bản stress nhân đôi fee, spread và slippage đồng thời. Entry mô phỏng market fill tại giá đóng tham chiếu của nến cộng chi phí bất lợi. Mô hình có thể đánh giá quá cao chất lượng khớp vì chưa đo được độ trễ hoặc gap sau khi đóng nến. Nếu SL và TP cùng chạm trong một nến, SL được ưu tiên. Vị thế chưa đóng được đánh dấu theo giá thị trường sau chi phí ước tính và tách khỏi PnL giao dịch đã đóng.

## Kết quả

Sau 10 ngày lấy lịch sử cho indicator, runner chia 20 ngày còn lại thành các đoạn 12, 4 và 4 ngày. Mỗi đoạn khởi tạo tài khoản mới và dùng cùng tham số cố định. Ba cửa sổ walk-forward chỉ dịch thời điểm đánh giá, không fit tham số. Hai cửa sổ cuối trùng với validation và test nên không thể cộng chúng như mẫu độc lập. Cửa sổ test là đoạn cuối theo thời gian, không phải một bộ dữ liệu đánh giá độc lập đã khóa trước khi phát triển chiến lược.

Đoạn test từ 05:10 UTC ngày 17/11 đến 05:10 UTC ngày 21/11 ở chi phí cấu hình:

| Symbol | Chiến lược | Giao dịch đã đóng | Win rate | Profit factor | Expectancy (USDT/lệnh) | PnL ròng (USDT) | Drawdown tối đa | Lệnh/ngày |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| BTC-USDT | Current | 0 | — | — | — | 0 | 0% | 0 |
| BTC-USDT | Experimental | 0 | — | — | — | 0 | 0% | 0 |
| ETH-USDT | Current | 0 | — | — | — | 0 | 0% | 0 |
| ETH-USDT | Experimental | 2 | 50% | 1,18 | +4,39 | +8,78 | 0,76% | 0,50 |

Dấu — biểu thị chỉ số không xác định khi không có giao dịch. Hai lệnh Experimental trong test đều là Short ở regime TrendingDown. Thời gian giữ trung bình 180 phút, gross reward/risk dự kiến 3,00, R thực hiện trung bình +0,09 và chuỗi lỗ dài nhất 1. Tổng phí 5,48 USDT và trượt giá ước tính 2,19 USDT. Con số trượt giá chưa bao gồm spread. Tỷ lệ return/drawdown là 0,115.

Đoạn validation trước test có một giao dịch ở Current BTC-USDT lỗ 3,32 USDT và một giao dịch ở Experimental ETH-USDT lỗ 4,14 USDT. Kịch bản stress 2× không có giao dịch ở cả hai chiến lược trên hai cặp trong tất cả các đoạn.

Trong cửa sổ 7 ngày cuối từ 05:10 UTC ngày 14/11 đến 05:10 UTC ngày 21/11, Current ghi 1 setup trên 4.032 nến giữa hai symbol, khớp 1 giao dịch mô phỏng BTC-USDT và đóng với PnL −3,32 USDT. Các nhánh loại bỏ nhiều nhất khi strategy đã chạy là regime không cho phép giao dịch trend (2.136), trend hoặc cấu trúc 30m xung đột với 1h (1.066), chờ breakout/retest hoặc cooldown (435) và momentum 15m hoặc entry đã quá xa (369). Experimental có 28 setup BTC-USDT và cả 28 đều bị chặn do reward/risk sau chi phí. Trên ETH-USDT, 22 trong 25 setup bị chặn bởi cùng giới hạn. Ba giao dịch mô phỏng đã đóng có tổng PnL +4,64 USDT. Cửa sổ này chồng lên đoạn test nên không cộng các giao dịch hai lần.

Mẫu có hai symbol với số giao dịch rất thấp. Thử nghiệm cho thấy nhiều setup hơn đi tới risk gate, nhưng chưa đủ dữ liệu để đánh giá expectancy hay đề xuất tăng kích thước vị thế. Đây là bằng chứng tái dựng điều kiện thị trường, không xác nhận worker đã quét các nến đó hay trạng thái thực tế của lệnh BingX. Replay không có active policy trong database, trạng thái adaptive, số dư thực, spread/taker fee/funding lịch sử, fill tại sàn hoặc hiệu ứng danh mục đa symbol.

## Chạy lại và đọc dữ liệu

Chạy từ thư mục gốc của repo sau khi có quyền truy cập endpoint market data công khai. Hai lệnh replay tạo báo cáo JSON chứa cửa sổ, hash đầu vào, lý do strategy và risk, regime, vị thế đang mở, giao dịch đã đóng và equity curve theo từng symbol và chiến lược. Hai biến thể đều dùng cùng một file cấu hình. Chạy với các tệp manifest đã lưu để tái hiện đúng khoảng thời gian của báo cáo này.

```powershell
.\tools\download-history.ps1 -Symbols BTC-USDT,ETH-USDT -Days 30 -OutputDirectory artifacts/market-history-20261121
dotnet run --project tools/TradingBot.Backtest -- artifacts/market-history-20261121 artifacts/trading-audit-backtest/comparison-20261121.json compare
dotnet run --project tools/TradingBot.Backtest -- artifacts/market-history-20261121 artifacts/trading-audit-backtest/comparison-20261121-cost-2x.json compare 2
```

Lệnh tải lại luôn lấy dữ liệu đến hiện tại. Lưu bộ nến đã tải nếu cần kiểm toán lại cùng cửa sổ. SHA-256 của hai CSV gốc:

- BTC-USDT `7D2528EF83D4BE8893A9A209A369CED53113D61CF13A2CC24218F6AE4103E584`
- ETH-USDT `8836917ACFA99CC0E7827CA6AE7EC5698CC93360B2BC8A35B24FEE1D5B6BFF66`

Build runner thành công. Đã kiểm tra 56 kết quả của hai kịch bản, gồm hash và cửa sổ trùng khớp giữa hai strategy, số lượng scan/setup/khớp/đóng nhất quán và chỉ số không xác định để trống. Runner từ chối manifest sai hash trước khi tạo báo cáo.
