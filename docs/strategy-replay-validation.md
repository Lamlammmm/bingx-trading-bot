# Kiểm định replay Current và Experimental

Hai phần dùng cùng mã Current và `trend-pullback-v1` từ đợt audit. Không fit tham số hoặc thay đổi strategy, MACD, giới hạn risk hay Live trong đợt kiểm định này.

## Vì sao chi phí cao hơn làm mất giao dịch

`compare 2` nhân đôi ba đầu vào cùng lúc. Phí mỗi lần khớp tăng từ **5 lên 10 bps**, spread giả định tăng từ **4 lên 8 bps**, và slippage tăng từ **2 lên 4 bps**. Paper áp dụng nửa spread cộng slippage, tức **4 lên 8 bps** bất lợi ở **mỗi phía**. Phí được tính trên giá đã khớp. Đây là kiểm tra độ nhạy và khả năng đi qua risk gate, khác với phép tính nhân đôi tiền phí trên danh sách giao dịch đã khớp. [Phí Perpetual Futures BingX](https://bingx.com/en/support/articles/40029373147289)

Với SHORT, engine giảm giá entry theo chi phí thực thi và làm tròn theo tick, tăng giá thực thi ở SL/TP. Buffer bảo vệ theo cấu trúc cũng bao gồm chi phí thực thi, nên mức SL/TP có thể dịch chuyển khi thay đổi giả định. Bên trong `RiskManager`, bước đầu kiểm tra reward/risk ròng với TP cuối. Sau đó, bước sizing tính reward đã trừ phí và chi phí khớp trên cơ cấu partial TP. Với 50% ở 1R, 25% ở 2R và 25% còn lại ở 3R, kỳ vọng **trước chi phí của đường đạt toàn bộ mục tiêu** là 1,75R. Ngưỡng chấp nhận **sau chi phí** là 1,50R. Đây là một phép kiểm tra trước lệnh, không phải kỳ vọng PnL được đo hoặc dự đoán mọi partial đều sẽ khớp.

`--risk-trace` dùng chính `RiskManager` trong mô phỏng. Nếu planner trả loại do min reward/risk, runner đo lại tỷ số bằng một instance tách biệt với ngưỡng tỷ số bằng 0. Candidate và tình trạng tài khoản được giữ nguyên, các gate còn lại vẫn áp dụng. Engine chỉ nhận kết quả planner gốc. Khi các gate khác cũng ngăn tính plan, tỷ số trong trace để trống.

Hai ứng viên SHORT ETH-USDT từ đoạn test ban đầu xuất hiện vào **20:35** và **22:50 UTC ngày 20/11/2026**. Giá đóng tham chiếu lần lượt là **1.587,81** và **1.616,18 USDT**. Cả hai vẫn phát tín hiệu ở chi phí cao. Các tỷ số sau chi phí dưới đây được tính ở bước kiểm tra partial TP tính theo trọng số. Bước kiểm tra TP cuối đã qua.

| Chi phí | Net R/R lúc 20:35 UTC | Net R/R lúc 22:50 UTC | Lượt đánh giá | Nến có tín hiệu kỹ thuật | Chặn bởi weighted R/R | Lệnh đã đóng | PnL ròng (USDT) |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 1× | 1,57338 | 1,50209 | 1.068 | 11 | 9 | 2 | +8,78 |
| 1,25× | 1,53473 | **1,44702** | 1.133 | 14 | 13 | 1 | −49,68 |
| 1,50× | **1,49796** | **1,39435** | 1.152 | 15 | 15 | 0 | 0,00 |
| 1,75× | **1,46295** | **1,34394** | 1.152 | 15 | 15 | 0 | 0,00 |
| 2× | **1,42956** | **1,29562** | 1.152 | 15 | 15 | 0 | 0,00 |

| Tín hiệu (UTC) | Entry / SL / TP ở 1× (USDT) | Entry / SL / TP dự kiến ở 2× (USDT) |
|---|---:|---:|
| 20:35 | 1.587,17 / 1.619,52 / 1.490,12 | 1.586,53 / 1.620,16 / 1.485,64 |
| 22:50 | 1.615,53 / 1.638,34 / 1.547,10 | 1.614,88 / 1.637,69 / 1.546,45 |

Các tỷ số in đậm dưới sàn 1,50. Lệnh lúc 22:50 UTC là lệnh thắng ở 1×. Nó mất ngay ở 1,25×, để lại một lệnh lỗ. Từ 1,50× cả hai ứng viên đã bị chặn trước sizing và không tạo giao dịch. Số nến có tín hiệu kỹ thuật tăng ở chi phí cao vì tài khoản không giữ vị thế hoặc chờ cooldown. Ở 1×, 72 bar dùng quản lý vị thế và 12 bar dừng do cooldown, nên chỉ có 1.068/1.152 bar chạy strategy. Không lấy chênh lệch 11→15 nến làm số setup độc lập.

Kết quả tại 1× và 2× khớp với 56 dòng replay trong báo cáo trước, gồm các quyết định và trade, sau khi bỏ trường bổ sung `RiskTrace`. Trace chỉ thay đổi dữ liệu chẩn đoán.

Tính lại độc lập cho cả 10 quan sát (hai ứng viên ở năm mức chi phí) cho kết quả đúng với trace. Ví dụ ở 2×, tín hiệu 20:35 có reward ròng theo partial **54,51493 USDT/ETH** và lỗ dự kiến tại stop, kể cả phí, **38,13411 USDT/ETH**. Chia hai số được **1,42956**. Tín hiệu 22:50 tương ứng **35,46643 / 27,37403 = 1,29562**.

## Dữ liệu và các cửa sổ kiểm định

Tải nến perpetual 5m qua endpoint công khai BingX. Ở lần kiểm tra, yêu cầu ba ngày bắt đầu 07/10/2026 05:10 UTC chỉ trả 808/864 nến BTC và bắt đầu tại 09:50 UTC. Các yêu cầu mẫu bắt đầu sớm hơn (04/10, 23/08 và 24/06) trả `code=0` với 0 nến. Cả bảy lượt tải 150 ngày đều không qua kiểm tra vùng biên. Vì vậy kết quả mở rộng dùng **44 ngày đầy đủ** thay vì nối dữ liệu thiếu hoặc thay venue. Đây là giới hạn quan sát tại thời điểm tải, không phải cam kết lưu trữ của sàn. [API dữ liệu BingX](https://github.com/BingX-API/api-ai-skills/blob/main/skills/swap-market/api-reference.md)

- **Dữ liệu:** 08/10/2026 05:10 đến 21/11/2026 05:10 UTC, 12.672 nến liên tiếp mỗi cặp, tổng 88.704 nến của BTC, ETH, SOL, BNB, DOGE, LINK và OPN, đều hợp đồng USDT. Tệp đầu vào lưu cùng manifest SHA-256 và metadata contract tại thời điểm tải. Replay từ chối nến thiếu, trùng, lệch khung, OHLCV lỗi hoặc hash sai.
- **Chia theo thời gian:** 10 ngày warm-up, rồi 60/20/20 theo số nến, tương đương khoảng 20,4 / 6,8 / 6,8 ngày đánh giá. Mỗi đoạn có tài khoản và trạng thái strategy mới. Các cửa sổ walk-forward chỉ dịch khoảng đánh giá với tham số cố định, và hai cửa sổ trùng chính xác các đoạn validation và test. Không cộng chúng thành giao dịch độc lập.
- **Ngoài mẫu:** đoạn cuối nằm sau train và validation. Không điều chỉnh mô hình với kết quả trong đoạn đó. Một phần giai đoạn BTC/ETH đã xuất hiện trong replay trước, nên đây là holdout hồi cứu theo thời gian. Cần dữ liệu đến sau khi đóng băng bộ tiêu chí hoặc một tập độc lập chưa xem để xác minh ngoài mẫu độc lập.
- **Mô phỏng:** một tài khoản 10.000 USDT cho từng cặp và mỗi strategy, cùng risk và fill model. Tài khoản độc lập giúp so sánh từng symbol nhưng không đo giới hạn hoặc drawdown danh mục khi các cặp cùng mở lệnh. Nến 15m, 30m và 1h hoàn thành từ các nến 5m trước điểm quyết định, tránh nhìn trước. Fill tại giá đóng + chi phí bất lợi, SL trước nếu cùng chạm SL và TP trong nến. Chưa có độ trễ sang bar sau, bid/ask và market impact lịch sử, funding lịch sử, metadata hợp đồng theo từng ngày hay mô phỏng vòng đời lệnh thật. Dùng funding bằng 0 theo cấu hình replay.
- Trong JSON, `PotentialSignals` là **bar phát tín hiệu kỹ thuật theo replay**, có thể gồm nhiều trigger của cùng một trend/pullback. `TradeCount` là vị thế mô phỏng **đã đóng**. Chỉ số ở từng split được đọc trong phạm vi cửa sổ, symbol và vốn nêu trong dòng kết quả.

## Kết quả mở rộng

Các số cộng dòng giao dịch đã đóng của bảy tài khoản mô phỏng độc lập ở **1×**. Drawdown là mức cao nhất quan sát ở **một symbol**, tính trên equity đánh dấu theo bar đóng. Dấu — biểu thị chỉ số không xác định.

| Đoạn UTC | Strategy | Nến có tín hiệu kỹ thuật | Lệnh đóng | Win rate | Expectancy (USDT/lệnh) | Profit factor | PnL ròng (USDT) | Drawdown tối đa |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| 18/10 05:10 – 07/11 14:45 | Current | 3 | 0 | — | — | — | 0,00 | 0,00% |
| 18/10 05:10 – 07/11 14:45 | Experimental | 448 | 8 | 25% | −11,98 | 0,538 | −95,87 | 1,00% |
| 07/11 14:45 – 14/11 09:55 | Current | 3 | 0 | — | — | — | 0,00 | 0,00% |
| 07/11 14:45 – 14/11 09:55 | Experimental | 218 | 3 | 0% | −49,75 | 0,000 | −149,25 | 1,82% |
| 14/11 09:55 – 21/11 05:10 | Current | 3 | 3 | 0% | −34,43 | 0,000 | −103,30 | 0,66% |
| 14/11 09:55 – 21/11 05:10 | Experimental | 124 | 15 | 40% | +3,48 | 1,167 | +52,17 | 1,83% |

Experimental còn một vị thế mở khi hết đoạn validation. Vị thế này có trong equity cuối cửa sổ và được báo riêng trong JSON. Chi phí trên các lệnh **đã đóng** ở holdout là **6,26 USDT phí và 2,50 USDT slippage ước tính** đối với Current, so với **40,72 USDT phí và 16,29 USDT slippage ước tính** đối với Experimental. Nửa spread có cùng số bps với slippage trong cấu hình đang dùng, nên chi phí spread ước tính có cùng độ lớn. PnL ròng đã bao gồm chi phí mô phỏng. Các dòng còn lại có chi phí chi tiết riêng trong JSON.

| Symbol, holdout 1× | Current (lệnh đóng / PnL USDT) | Experimental (lệnh đóng / PnL USDT) |
|---|---:|---:|
| BNB-USDT | 0 / 0,00 | 1 / −49,97 |
| BTC-USDT | 1 / −3,32 | 0 / 0,00 |
| DOGE-USDT | 0 / 0,00 | 1 / +75,25 |
| ETH-USDT | 0 / 0,00 | 3 / +4,64 |
| LINK-USDT | 1 / −49,98 | 1 / −49,99 |
| OPN-USDT | 1 / −50,00 | 5 / +4,51 |
| SOL-USDT | 0 / 0,00 | 4 / +67,72 |

- Current phát cả hai ứng viên đúng timestamp lỗi Live trong audit, LINK 01:10 UTC và OPN 17:50 UTC ngày 15/11. Cả hai chạm SL trong replay Paper. Đây chỉ là đường đi mô phỏng; audit không ghi nhận lệnh thật.
- Tất cả 26 giao dịch đã đóng của Experimental trong ba đoạn không trùng nhau ở 1× đến từ `TrendingDown`. Dữ liệu có cả `Sideways` và một số lượt `TrendingUp`, nhưng chưa đo được hiệu quả giao dịch ở các regime đó. Hai đoạn trước holdout đều lỗ. Nếu bỏ riêng DOGE hoặc riêng SOL thì PnL Experimental holdout đổi sang âm. Bỏ cả hai, còn −90,80 USDT. PnL cộng dồn của 15 lệnh chưa cho thấy độ ổn định qua symbol hoặc trạng thái thị trường.
- Ở 2×, cả Current và Experimental đều có **0 entry mô phỏng trong toàn bộ replay mở rộng**. Trong holdout, Experimental vẫn phát 147 nến có tín hiệu kỹ thuật. 146 bị chặn bởi weighted R/R và một bị chặn ở R/R ròng với TP cuối. Vì không có fill, không có cơ sở ước lượng win rate, expectancy hay PF trong điều kiện này.

Đã đối chiếu 196 dòng kết quả của hai kịch bản với manifest, SHA-256, thời điểm bắt đầu/kết thúc, số nến, trade đã đóng, vị thế còn mở và PnL cộng lại. Các cặp strategy và cost variant sử dụng đúng cùng cửa sổ dữ liệu; các cửa sổ walk-forward trùng nhau cho cùng số liệu. Báo cáo đầy đủ lưu ở `artifacts/strategy-replay-validation/expanded-cost-1.json` và `expanded-cost-2.json`.

## Chạy lại

Chạy từ thư mục gốc. Sử dụng bản CSV và manifest đã lưu để lặp lại cửa sổ chính xác. Nến cũ sẽ rời phạm vi truy vấn công khai theo thời gian, nên lệnh tải lại có thể thất bại nếu chạy sau.

```powershell
dotnet build tools/TradingBot.Backtest -c Release --no-restore -o artifacts/strategy-replay-validation/bin
foreach ($factor in '1','1.25','1.5','1.75','2') {
    dotnet artifacts/strategy-replay-validation/bin/TradingBot.Backtest.dll artifacts/market-history-20261121 "artifacts/strategy-replay-validation/original-cost-$factor.json" compare $factor --risk-trace
}
$history = 'artifacts/market-history-44d-20261121'
foreach ($factor in '1','2') {
    dotnet artifacts/strategy-replay-validation/bin/TradingBot.Backtest.dll $history "artifacts/strategy-replay-validation/expanded-cost-$factor.json" compare $factor
}
```

Input của nhánh mở rộng được tạo bằng `tools/download-history.ps1` với `-Days 44 -EndUtc '2026-11-21T05:10:00Z'`, các symbol nêu trên và `-OutputDirectory artifacts/market-history-44d-20261121`. Downloader dùng trang tối đa ba ngày và dừng nếu thiếu dữ liệu. Runner đối chiếu lại SHA, số nến và biên thời gian trước khi chạy.
