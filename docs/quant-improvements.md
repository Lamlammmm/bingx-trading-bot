# Audit và cải tiến bot BingX — 06/11/2026

> Báo cáo giai đoạn nghiên cứu ban đầu. Sau đó người dùng yêu cầu áp dụng cả LIVE và tích hợp adaptive/journal; trạng thái source/config hiện tại được mô tả trong [adaptive-trading.md](adaptive-trading.md). Các câu “paper-only”, “live từ chối” hoặc “không sửa appsettings” dưới đây chỉ mô tả giai đoạn ban đầu; kết quả backtest lịch sử vẫn được giữ nguyên.

Đã sửa trực tiếp source, bổ sung chế độ nghiên cứu paper, test và replay lịch sử. **Chưa có bằng chứng chiến lược mới sinh lợi hơn. Không bật hoặc restart trading worker, không gửi lệnh thật, không sửa `appsettings.json` hay thông số live hiện có.**

## 1. Audit trước thay đổi

Luồng hiện tại: REST tải contract và nến 5m/30m/1h → WebSocket cập nhật → `ClosedCandleTracker` đóng nến trước khi thấy nến kế tiếp → channel một reader → `TradingWorker` xử lý live trước, paper sau → `BreakoutStrategy` → risk gate → executor hoặc sổ paper → reconciliation/journal và persistence.

Điểm tốt được giữ: breakout phải retest, kiểm tra nến đã đóng, context higher timeframe không đi sau thời điểm tín hiệu, cooldown tín hiệu, giới hạn khoảng cách retest, minimum quantity/notional, giới hạn leverage sàn, Hedge mode, market order gắn SL/TP. Live lưu pending ID trước khi gửi lệnh, không retry lệnh ghi mù, kiểm tra quantity/protection và giữ trạng thái không ready khi chưa xác minh; startup dùng file lock và đối chiếu vị thế sàn.

Các vấn đề tìm thấy:

- Paper sizing chia ngân sách cho khoảng cách giá Entry–SL, bỏ qua phí và chi phí thoát lệnh. Live sizing đã tính phí; hai mô hình không nhất quán.
- Partial TP tự dời SL về Entry, chưa hòa vốn sau phí, và xét lại SL mới trên cùng OHLC đã dùng để kích hoạt partial. Không biết thứ tự intrabar nên kết quả có thể sai.
- Paper khớp stop tại trigger khi giá gap qua stop; funding chỉ tính trên quantity còn lại lúc thoát và theo số giờ giữ, bỏ qua quantity trước partial và mốc UTC settlement.
- Daily paper PnL ghi nhận phí Entry muộn, dùng ngày máy khi replay lịch sử; account/position/trade đóng lưu riêng, có cửa sổ mất đồng bộ khi crash.
- Chưa có setup 15m, regime/ADX/rolling structure, trailing có cooldown, loss circuit được lưu qua restart, hoặc backtest có provenance và chia thời gian.
- RSI chuỗi đi ngang tuyệt đối trả 100 thay vì trung tính 50. REST chọn contract đầu tiên trong array thay vì symbol yêu cầu.
- AI paper có thể đề xuất hướng ngược technical signal. Confidence do AI đưa ra chưa phải xác suất thắng đã hiệu chuẩn.
- Không có dữ liệu thực thi để kết luận các lệnh live bị SL do xu hướng, spread, mark-price hay slippage. Live journal giữ history thô nhưng chưa quy kết chính xác PnL/funding/fill cho từng trade.

## 2. Thiết kế và phần đã triển khai

`MultiTimeframeStrategy` là lớp lọc quanh `BreakoutStrategy`, mặc định opt-in cho paper. Live engine từ chối khởi tạo nếu bật chế độ nghiên cứu cùng live.

- **1h:** EMA fast/slow/primary (mặc định 20/50/200), ADX Wilder và ATR/giá. Regime gồm TrendingUp, TrendingDown, Sideways, HighVolatility, LowVolatility và Unknown khi thiếu dữ liệu.
- **30m:** EMA cùng hướng; hai cửa sổ quá khứ có high và low cùng tăng (HH/HL) hoặc cùng giảm (LH/LL). Đây là rolling structure, không dùng pivot có nến tương lai.
- **15m:** tổng hợp từ đúng 3 nến 5m UTC liên tiếp đã đóng; bỏ bucket thiếu, trùng hoặc đang hình thành. RSI và giá so với EMA xác nhận setup; chặn giá quá xa EMA theo ATR15m.
- **5m:** giữ điều kiện breakout, volume/body/RSI, retest và cooldown hiện có. Invalidation lấy biên xa hơn của retest và swing setup 15m. Risk ATR và trailing ATR của nghiên cứu dùng 15m.
- Bỏ qua regime không phù hợp, context cũ/thiếu, xung đột cấu trúc hoặc entry kéo xa. Không thêm MACD hay score 0–100 chưa hiệu chuẩn.

Các ngưỡng mới là **giả thuyết nghiên cứu có cấu hình**, chưa được tối ưu hay chứng minh bằng backtest có giao dịch. Giảm số tín hiệu không tự chứng minh tăng chất lượng.

## 3. Công thức Entry, SL, TP và size

Áp dụng cho USDT perpetual tuyến tính, quantity là đơn vị tài sản cơ sở; không hỗ trợ công thức hợp đồng inverse.

Ký hiệu `s=+1` cho Long, `s=-1` cho Short; `E` là Entry mô phỏng, `A` là ATR của khung risk, `f` là fee mỗi chiều, `c=(slippageBps+spreadBps/2)/10000`.

- Entry dự kiến là close của nến xác nhận; Entry paper dịch bất lợi `close*(1+s*c)`, sau đó theo price precision. Đây là giả định khớp ở close cộng chi phí, không phải mô hình next-open/latency thực tế.
- Long SL = min(`E-k*A`, `swingLow-buffer`); Short SL = max(`E+k*A`, `swingHigh+buffer`). Buffer gồm ATR cấu hình, chi phí giá cấu hình và ít nhất một tick. Stop vượt `MaximumStopAtrMultiplier*A` bị từ chối.
- `D=abs(E-SL)`; TP cuối = `E+s*D*TakeProfitRiskMultiple`, hoặc target được đề xuất và qua risk gate. TP1/TP2 dùng R cấu hình, phần dư giữ đến TP cuối hoặc trailing. TP target được làm tròn theo price precision.
- Giá thoát mô hình tại stop `Sfill=SL*(1-s*c)`; loss/unit = `s*(E-Sfill)+(E+Sfill)*f`.
- Size = floor_quantity_precision(min(`capital*riskPercent/lossPerUnit`, giới hạn margin/leverage, profit cap nếu bật)). Từ chối dưới minimum quantity/notional, vượt tổng risk hoặc thiếu free margin.
- Với dynamic partial, tính cả payoff của từng phần: `averageExit=sum(weight_i*TP_i)*(1-s*c)`; net reward/unit = `s*(averageExit-E)-(E+averageExit)*f`. Phải đạt `MinimumNetRewardRiskRatio*lossPerUnit`. Đây là payoff có điều kiện đạt các TP, **không phải expectancy**.
- Break-even chỉ xét khi close đạt R cấu hình. Long BE fill = `(E*(1+f)+fundingPerUnit)/(1-f)`; Short = `(E*(1-f)-fundingPerUnit)/(1+f)`. Đổi fill thành trigger có tính `c`, làm tròn và chỉ siết stop. Không mặc định dời ngay khi TP1 khớp trong dynamic mode.
- Trailing từ close: `close-s*ATR*multiplier`, có minimum step/cooldown; chỉ có hiệu lực từ nến tiếp theo, không bao giờ nới stop.
- Khi SL và TP cùng chạm một bar, SL được xử lý trước. Stop gap lấy open bất lợi hơn trigger. Partial tôn trọng precision và minimum của phần đóng lẫn phần còn lại.

`PositionManager` là logic thuần dùng chung cho paper và replay. Funding hiện là **kịch bản rate cố định** tại UTC 00/08/16, theo quantity đang giữ trước các lần thoát của bar; không phải funding lịch sử của BingX. Risk budget là ước tính tới stop với chi phí cấu hình, không bảo đảm mức lỗ tối đa khi gap, funding kéo dài hoặc thị trường thiếu thanh khoản.

## 4. Quản trị rủi ro, log và recovery

Paper có position cap, risk tổng, free-margin check, daily realized loss theo ngày nến UTC và vốn đầu ngày, cooldown sau thoát và circuit sau chuỗi thua. Không tăng size để gỡ lỗ, không DCA/Martingale. Các guard mới không tự bật trên cấu hình hiện có; profile nghiên cứu cấu hình riêng.

SQLite/Postgres thêm cột JSON tương thích dữ liệu cũ cho controls và metrics. Account, open positions và closed trade được commit trong cùng transaction; lỗi commit khóa xử lý tiếp của instance đến khi restart từ state. Controls lưu cooldown, loss streak, circuit expiry. Position metrics lưu partial stage, quantity còn lại, funding đã tính, timestamp xử lý và lần đổi SL; replay lại bar đã xử lý không tính lại.

Log nghiên cứu có symbol, 5m data time, regime, EMA/ADX/ATR1h, RSI15m, rolling structure30m và lý do chờ/từ chối. Trade plan có Entry, SL, TP, quantity, fee-inclusive risk và net reward/risk. Trade đóng có phí/funding, average exit, entry/final-exit slippage mô hình, holding time và MFE/MAE theo giá. MFE/MAE từ OHLC là ước tính/bounds, không phải excursion chính xác từ tick. Cột legacy `Rsi15m` trong feature storage vẫn chứa RSI30m theo schema cũ; RSI15m thực được giải thích riêng trong signal evaluation log.

## 5. Đo thực tế và giới hạn bằng chứng

Dữ liệu public BingX: BTC-USDT và ETH-USDT, mỗi symbol **8.640 nến 5m**, tổng **17.280**, từ 07/10/2026 15:30 đến 06/11/2026 15:30 giờ Việt Nam. Mỗi file có manifest SHA256, start/end và provenance; runner từ chối sai checksum, thiếu/trùng/gap OHLCV. Contract specifications là snapshot ngày tải, chưa có lịch sử thay đổi precision/minimum.

Warmup 10 ngày; phần 20 ngày còn lại chia theo thứ tự 60/20/20: train 12 ngày, validation 4 ngày, test 4 ngày. Thêm 3 cửa sổ walk-forward với tham số cố định, state mới ở mỗi cửa sổ và history chỉ đi đến thời điểm xử lý. Không nối/dồn kết quả các cửa sổ chồng lấn. Mỗi symbol là một tài khoản riêng, chưa phải portfolio backtest.

Baseline chạy từ DLL được lưu **trước sửa logic** trong `artifacts/quant-baseline/`; giữ cả các thay đổi chưa commit có sẵn lúc bắt đầu. Kết quả và fingerprint nằm trong báo cáo so sánh. Bốn biến thể mới tách corrected legacy, multi-timeframe, management và kết hợp; thêm chi phí gấp đôi + spread/funding scenario và sensitivity ADX/trailing ±20%.

**Kết quả chính:** baseline và tất cả biến thể đều 0 lệnh đóng trên các split này. Corrected legacy tạo 11 technical candidates và bị fee-inclusive reward/risk gate từ chối; nghiên cứu tạo 2 candidates, bị weighted partial reward/risk gate từ chối. Không có Win Rate, Profit Factor, Average Win/Loss, expectancy hay realized R có ý nghĩa; không gán 0% Win Rate cho mẫu rỗng. Drawdown 0 do không có giao dịch không chứng minh chiến lược tốt. Sensitivity cũng không có giao dịch nên không chứng minh độ bền tham số.

Chi tiết từng symbol/split/variant, nguyên nhân từ chối và chi phí: [bảng so sánh](quant-comparison.html), [dữ liệu tổng hợp](quant-comparison.json). Các JSON replay đầy đủ nằm trong `artifacts/quant-validation/`.

Các split đã được xem trong quá trình phát triển; **không coi test hiện tại là holdout hoàn toàn chưa từng được xem**. Không dùng kết quả rỗng để chọn tham số thắng. Cần một giai đoạn dữ liệu mới sau khi đóng băng logic trước khi xác nhận out-of-sample thật. Các test runtime kiểm tra không dùng nến tương lai; điều đó khác với tính độc lập của holdout nghiên cứu.

## 6. Kiểm thử và cách chạy

Kết quả thực thi được ghi trong `artifacts/quant-validation/final.trx`; báo cáo so sánh lấy số test từ TRX này. Baseline ban đầu 157/157 pass. Test mới bao phủ ATR/ADX/RSI, aggregate không leak/không thiếu, nhánh entry hợp lệ, SL/TP, phí/size, daily budget, partial quantity, gap, funding, trailing một chiều, transaction rollback, recovery circuit/cooldown và chặn research+live.

Postman API-only host: **5 request / 12 assertion pass**; xác nhận Bot/Live disabled, không có worker khởi tạo, auth/readiness đúng và không trả key. Executor thật qua mock HTTP local: **6 scenario pass** (success, 429, business error, HTML 502, timeout sau write, quantity khớp thiếu). Mock state xác nhận một request, không duplicate write. Đây là bằng chứng local, chưa phải execution trên sàn.

Build source/test thành công. Build solution có cảnh báo NU1900 do feed vulnerability NuGet không truy cập được trong sandbox; không tắt audit. PostgreSQL DDL/transaction đã build nhưng chưa chạy với PostgreSQL thật; SQLite có integration test thực. Không chạy preflight có khóa, không gửi lệnh test/live lên BingX. Postman CLI 1.73.0 không có lệnh `status`, chưa đăng nhập; không tìm owner mock hoặc publish cloud. Orbit trả provider khác nên không dùng thay BingX.

Từ repository, chỉ build và test:

```powershell
dotnet build BingXTradingBot.sln -m:1 -p:UseSharedCompilation=false -p:OutputPath=D:/referenced-chatgpt-conversation-this-is-an/artifacts/quant-final/
dotnet vstest artifacts/quant-final/TradingBot.Tests.dll
```

Tải public history và replay, không tạo trading host:

```powershell
pwsh -File tools/download-history.ps1 -Days 30
dotnet artifacts/quant-final/TradingBot.Backtest.dll artifacts/market-history artifacts/quant-validation/research.json research
dotnet artifacts/quant-final/TradingBot.Backtest.dll artifacts/market-history artifacts/quant-validation/research-cost-stress.json research 2 1 4 0.0001
```

Chạy paper khi **bạn chủ động muốn khởi động worker**:

```powershell
pwsh -File tools/run-paper.ps1 -Port 5099
```

Script chưa được chạy trong phiên này. Nó đọc `research/paper-profile.json`, ép Live/OpenAI false bằng command line, dùng SQLite riêng `data/paper-research.db`, build riêng và bind HTTP vào loopback. API key `paper-local-development-only` chỉ dành cho host localhost nghiên cứu. Khác biệt với replay so sánh mặc định: profile paper thêm spread 4 bps; các con số này là giả định, cần hiệu chuẩn bằng dữ liệu thực tế.

Mock execution có thể chạy lại:

```powershell
postman simulation run postman/simulations/bingx-execution-failures.sim.yaml --port 4918 --no-history --output ndjson
# Terminal khác:
dotnet artifacts/quant-final/TradingBot.ExecutionSmoke.dll http://127.0.0.1:4918/
```

## 7. Phần chưa đủ điều kiện đưa vào live

- Cần tập dữ liệu dài hơn, nhiều coin/regime và giai đoạn holdout mới sau khi freeze; đủ giao dịch để đánh giá expectancy/PF/DD, rủi ro thống kê và chi phí. Hiện chưa tìm được bộ tham số có bằng chứng hiệu quả.
- Cần lịch sử bid/ask, mark-price/funding, thực thi partial/latency, phí tài khoản, market impact và mô hình margin/liquidation. SL live dùng mark price, trong khi OHLC hiện là trade price.
- Live hiện giữ protection toàn bộ quantity. Chưa triển khai multi-level partial/trailing cancel–replace trên sàn; không giả định sửa protective orders là atomic.
- Live chưa có ledger PnL/funding quy kết đầy đủ theo position/fill cho daily loss/loss streak, hoặc portfolio risk reconciliation bao gồm vị thế thủ công. Các guard paper mới không phải bảo đảm cho live.
- Pending order chưa rõ và mismatched/partial position vẫn fail closed, cần reconcile rõ ràng; không tự gửi lại lệnh để “sửa”.
- Chỉ cân nhắc tích hợp và bật live sau khi khắc phục các phần trên, paper forward test ổn định và có phê duyệt của bạn. Không có cam kết lợi nhuận.

API được kiểm tra đối chiếu với tài liệu chính thức: [BingX swap market](https://github.com/BingX-API/api-ai-skills/blob/main/skills/swap-market/api-reference.md), [BingX swap trade](https://github.com/BingX-API/api-ai-skills/blob/main/skills/swap-trade/api-reference.md). Tài liệu xác nhận attached SL/TP, trailing order types và quantity theo coin; hỗ trợ loại lệnh không tự chứng minh workflow sửa protective orders an toàn.

## 8. File thay đổi trong phiên này

- Signal/position: `Application/MultiTimeframeStrategy.cs` (mới), `Application/PositionManager.cs` (mới), `Application/TechnicalIndicators.cs`, `Application/RiskManager.cs`, `Application/Interfaces.cs`.
- State/config/wiring: `Domain/TradingModels.cs`, `Configuration/Options.cs`, `Program.cs`, `Infrastructure/PaperTrading/PaperTradingEngine.cs`, `Infrastructure/Live/LiveTradingEngine.cs` (chặn research+live).
- Exchange/persistence: `Infrastructure/BingX/BingXRestClient.cs`, `Infrastructure/Persistence/SqliteTradeStore.cs`, `Infrastructure/Persistence/PostgresTradeStore.cs`.
- Tests: `MultiTimeframeTests.cs`, `PositionManagerTests.cs`, `RiskBudgetTests.cs`, `PaperRecoveryTests.cs`, `BingXContractSelectionTests.cs` (mới); cập nhật `TradingLogicTests.cs`, `PersistenceTests.cs`, `PaperTradingEngineTests.cs`, `OpenAiDecisionLoggingTests.cs`, `LiveTradingEngineTests.cs`; thêm project `tests/TradingBot.ExecutionSmoke`.
- Tooling/tài liệu: `tools/TradingBot.Backtest`, `tools/download-history.ps1`, `tools/run-paper.ps1`, `research/paper-profile.json`, mock/simulation `bingx-execution-failures`, solution và các file `docs/quant-*`.

Các thay đổi có sẵn ở `BreakoutStrategy`, `BingXLiveExecutor`, `appsettings.json`, deployment docs và một số test được giữ nguyên. Không commit/push/deploy. Hash `appsettings.json` trước/sau giống nhau: `8BCC7C04E54BE6D5F8F95359A8840D7C91D0C277DD21FA88C1CD511DECA5EE58`.
