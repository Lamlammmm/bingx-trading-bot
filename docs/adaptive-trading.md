# Adaptive Trading cho PAPER và LIVE — 06/11/2026

Đã tích hợp vào source của cả hai engine, giữ AI disabled. Chưa khởi động/restart worker, deploy hoặc gửi lệnh thật. Các kiểm tra Supabase dùng schema tạm và đã xóa; schema của môi trường chạy thật sẽ được khởi tạo bổ sung khi người vận hành chạy bản mới. Không có candidate nào được promote vào tài khoản thật trong task này.

## Kiến trúc và audit

Trước thay đổi, hai engine dùng chung breakout nhưng khác cách sizing và quản lý vị thế; live journal là event thô, chưa có version, analytics hoặc adaptive rules. PostgreSQL/Supabase đã được nối qua `PostgresTradeStore`; AI chỉ tùy chọn trong paper; channel của `TradingWorker` có một reader, xử lý live trước paper. Các bảng cũ, API theo dõi và cơ chế startup của người vận hành được giữ lại.

Luồng mới: nến đã đóng → `TradingPolicyRuntime` lấy cấu hình APPROVED theo mode → `MultiTimeframeStrategy`/`BreakoutStrategy` → deterministic adaptive decision → cùng `RiskManager`/`Plan` → execution paper hoặc BingX → journal → analytics. Partial targets, break-even và trailing dùng chung `PositionManager`. Live xác nhận fill/position/order thật; paper áp dụng mô phỏng giá, phí, spread, funding và độ chính xác hợp đồng.

`TradingGovernance` đọc active version từ DB trước khi đánh giá entry. Strategy instance riêng theo mode/version giữ trạng thái breakout/retest riêng. Mỗi vị thế mang snapshot cấu hình bất biến; promote/rollback chỉ ảnh hưởng entry mới. Vị thế live cũ thiếu snapshot tiếp tục giữ native protection và chặn thêm exposure cho đến khi đóng/reconcile; không dựng lại dữ liệu lịch sử thiếu.

## Dữ liệu và schema

[SQL bổ sung](schema/20261106-adaptive-trading.sql) tạo 5 bảng:

- `trading_journal`: event append-only, khóa `(trading_mode,event_id)`, trade/signal/version/hash, thời gian UTC, symbol và JSON payload.
- `strategy_versions`: snapshot/hash, trạng thái, người duyệt, version trước và validation evidence; khóa theo mode/version.
- `strategy_active`: version hiện tại riêng PAPER/LIVE.
- `strategy_changes`: audit BOOTSTRAP/APPROVE/ROLLBACK, from/to, người duyệt và thời gian.
- `trading_controls`: kill switch riêng từng mode; thay đổi cũng ghi event.

PostgreSQL bật RLS trên cả 5 bảng, thu hồi quyền `anon`/`authenticated` nếu các role này tồn tại. Backend phải dùng tài khoản chủ bảng/quyền backend phù hợp; không dùng Supabase anon key. SQL không xóa/sửa lịch sử giao dịch cũ. Các store cũ được bổ sung JSON metrics/risk controls cho paper và transaction account + positions + closed trade + unified events. SQLite có cùng repository contract để kiểm thử cô lập.

Journal mới lưu entry/exit, SL/TP, quantity, leverage, order events, fees/funding, net PnL, net R, MFE/MAE, context/regime/timeframe và lý do quyết định. Signal score để `null` vì chưa có mô hình score được hiệu chuẩn. Không tạo confidence giả để làm ngưỡng tối ưu.

Live lưu intent vào state file có flush/atomic replace và outbox trước khi gửi lệnh. Journal insert idempotent; lỗi DB chặn entry. Đây không phải transaction phân tán hoặc bảo đảm exactly-once với sàn. Khi kết quả lệnh chưa xác định, pending ID giữ nguyên, không retry mù, restart bị chặn để đối chiếu. State file và DB đều cần được giữ; một worker quản lý một account, không dùng thêm bot khác chỉnh cùng vị thế.

## Quy tắc Live và cấu hình

`Strategy.UseMultiTimeframeAnalysis=true`, `Risk.UseDynamicPositionManagement=true`: 1h EMA/ADX/ATR regime, 30m structure, setup 15m tổng hợp từ 5m, entry breakout/retest 5m; SL theo ATR + cấu trúc, TP 1R/2R/3R với giảm từng phần, break-even sau chi phí và trailing chỉ siết SL. Không cập nhật stop mới ngược về quá khứ trong paper.

Giữ các tỷ lệ đã có: paper risk 0.5%, live override 2%, leverage/margin và `Live.Enabled` không tự chuyển. Bổ sung `MaxDrawdownPercent=5`, `MaxAggregateExposurePercent=300`, tối đa 3 vị thế, aggregate initial risk 2%, daily loss 2%, cooldown 6 nến và circuit sau 3 lệnh thua. Aggregate risk có thể khiến số vị thế thực tế thấp hơn giới hạn count. Không martingale hoặc tăng risk khi thắng/thua.

`Adaptive`: enabled, lookback 30 ngày, tối thiểu 30 kết quả đã reconcile, chu kỳ 5 phút, spread tối đa 20 bps, degraded multiplier 0.5, PF threshold 1, bootstrap version `mtf-v1`. Khi mẫu cùng mode/config/regime có expectancy không dương và PF dưới ngưỡng, chỉ giảm size; thiếu spread hoặc spread quá cao thì chặn. Volatility/regime không phù hợp bị strategy từ chối. Kill switch, daily loss và drawdown vẫn chặn entry. AI không được tự bật.

Live đối chiếu position ID, quantity, entry và đúng hai protective IDs; manual/untracked exposure chặn entry mới. Native SL/TP đặt trên sàn và xác nhận. Trước partial close, đổi SL thành `closePosition=true` và xác nhận để bảo vệ toàn bộ phần còn lại, sau đó kiểm tra fill, remaining quantity và SL/TP mới. Không suy ra fill từ high/low cũ. Timeout, partial fill hoặc trạng thái lệnh không chắc chắn giữ pending và ngừng ghi lệnh tiếp.

Nếu sàn xác nhận cancel SL thành công nhưng tạo SL mới thất bại, engine xác minh đúng vị thế rồi emergency close với client ID duy nhất. Intent/kết quả này được lưu outbox cục bộ ngay cả khi DB vừa mất kết nối; entry vẫn bị chặn đến khi đối chiếu. Trường hợp timeout không rõ cancel/replace có thành công không không được coi là “SL đã mất”.

API circuit mặc định 3 lỗi/5 phút; native protection tiếp tục tồn tại khi tiến trình/DB lỗi. Drawdown live dùng equity peak lấy mẫu lúc xử lý vị thế/tín hiệu, điều chỉnh các cash flow đã quan sát; không phải giám sát tick liên tục và không tự đóng mọi vị thế khi chạm ngưỡng. Daily ledger có realized, fees, funding và unrealized. Income response đạt giới hạn 1000 bản ghi hoặc dữ liệu lạ thì fail closed để tránh dùng ledger thiếu.

## Analytics và đối soát

PAPER/LIVE tính riêng: win rate, PF, expectancy, average net R, closed-equity drawdown, loss streak, fees/funding, holding time, entry slippage, latency và MFE/MAE/profit capture. Có nhóm strategy, regime, symbol/timeframe và exit reason để kiểm tra entry/SL/TP/trailing. Mẫu rỗng hoặc không có gross loss trả PF `null`, không tuyên bố PF vô hạn.

Live close ghi income đã quan sát trước, chưa đưa vào thống kê chính thức. Worker đợi ít nhất 5 phút và cần một lần quan sát ledger ổn định tiếp theo để đánh dấu reconciled; resync 24 giờ đầu cho cả kết quả đã reconcile. Phí muộn tạo revision mới, tạm loại kết quả chưa ổn định, không cộng trùng trade. Query bị chặn trong `[OpenedAt,ClosedAt]` để không hút PnL của lệnh tiếp theo cùng symbol.

Đây là kiểm tra độ ổn định ledger, không phải chứng nhận settlement cuối cùng. Live exit VWAP suy ra từ realized income của symbol được quản lý độc quyền; native exit reason có thể vẫn là `EXCHANGE`. MFE/MAE live lấy mẫu mark 5m, paper là ước lượng OHLC; không coi là full tick excursion. Entry slippage hai mode so với giá quyết định; exit slippage paper chỉ là lần giảm cuối, live chưa có quote thoát nên để null. Latency live gồm journal/request đến response, không phải latency thuần của sàn. Không tự kết luận nguyên nhân live kém chỉ bằng tương quan; đối chiếu raw events và mẫu cùng thời kỳ.

## Candidate → validation → phê duyệt → rollback

Bootstrap chỉ nhập cấu hình người vận hành đã chọn khi mode chưa có active; đây không phải kết quả chiến lược đã được chứng minh sinh lợi. Đổi appsettings sau đó không ghi đè version đang active trong DB.

Degradation đủ mẫu tự tạo CANDIDATE tăng signal cooldown, giữ active hiện tại. Người vận hành có thể nộp candidate khác trong allowlist: ATR stop/trailing, break-even/trailing triggers, TP R multiples, ADX/ATR regime thresholds, setup/body/volume/retest confirmation và cooldown. Candidate không được thay risk %, leverage, exposure caps, operating flags hay execution costs. Hash/version không được ghi đè bằng config khác.

Validation yêu cầu hash artifact khớp file, chronology train → validation → test → shadow không chồng lấn, ≥3 walk-forward folds, ≥30 OOS trades và ≥30 shadow/paper trades, expectancy OOS dương và tốt hơn active, PF >1, drawdown không vượt policy, backtest/shadow passed. Mẫu thiếu/0 trades không đủ điều kiện. VALIDATED vẫn chưa là ACTIVE: cần lệnh promote có người duyệt và expected-active version; CAS tránh đè lên phê duyệt đồng thời. Rollback chỉ dùng version đã APPROVED trước đó.

**Giới hạn validation:** CLI kiểm tra artifact checksum và các chỉ tiêu của bản khai evidence để người vận hành duyệt; nó không tự chứng minh artifact đó trung thực, không tự chạy tối ưu toàn bộ tham số hoặc tự xác thực một shadow service bên ngoài. Backtest executable bên dưới tạo kết quả đo thực tế theo cấu hình, gồm 60/20/20 và 3 frozen-parameter walk-forward windows. Shadow phải có kết quả paper/shadow độc lập sau OOS; người duyệt cần kiểm tra nguồn dữ liệu, chi phí, mẫu và artifact. Các guard này không biến 30 trades thành bằng chứng thống kê chắc chắn.

## Lệnh vận hành (không khởi động worker)

Chạy DLL đã build với `--contentRoot` và environment phù hợp. Ví dụ từ repo, dùng `artifacts/adaptive-final/TradingBot.Worker.dll`; không đưa secret vào command line/log. Các lệnh sẽ khởi tạo schema/initial version nếu còn thiếu.

```powershell
dotnet artifacts/adaptive-final/TradingBot.Worker.dll --contentRoot "$PWD/src/TradingBot.Worker" --environment Development --trading-report --mode=LIVE
dotnet artifacts/adaptive-final/TradingBot.Worker.dll --contentRoot "$PWD/src/TradingBot.Worker" --environment Development --trading-compare
dotnet artifacts/adaptive-final/TradingBot.Worker.dll --contentRoot "$PWD/src/TradingBot.Worker" --environment Development --strategy-show --mode=LIVE
```

Các command khác: `--trading-export=<jsonl>`, `--trading-halt=true|false --reason=<reason>`, `--strategy-candidate=<TradingConfiguration.json>`, `--strategy-validate=<evidence.json>`, `--strategy-promote=<version>` hoặc `--strategy-rollback=<version>`. Hai lệnh cuối bắt buộc `--approved-by=<reviewer> --expected-active=<version>` và `--mode=LIVE` khi thực sự muốn đổi Live. Task này không chạy các lệnh đó trên DB thật.

Evidence file có dạng `{ "Version": "candidate-...", "ArtifactPath": "...", "Evidence": { ...ValidationEvidence fields... } }`; schema typed ở `Domain/ResearchModels.cs`. Artifact checksum SHA256, dates UTC; fixtures trong tests chỉ chứng minh gate, không dùng để duyệt giao dịch.

Replay cấu hình đầy đủ mà không tạo exchange executor/host:

```powershell
dotnet artifacts/adaptive-final/TradingBot.Backtest.dll artifacts/market-history artifacts/adaptive-validation/current-live-replay.json registered --strategy-config=research/current-live-config.json
```

`research/current-paper-config.json` và `current-live-config.json` là bản freeze config hiện tại, không chứa credentials. Replay dùng shared planner, mô phỏng single-symbol account, funding kịch bản; không có order-book lịch sử, queue/market-impact/liquidation hoặc entry liquidity partial fills. Không dùng kết quả mô phỏng thay cho xác nhận thực thi live.

## Kiểm chứng task

- Solution build thành công; NU1900 chỉ ở vulnerability feed của NuGet không truy cập được.
- 217 unit/integration tests: shared formulas, hai engine thật với SQLite/governance và AI disabled, mode isolation, atomic rollback, DB lỗi chặn entry, approve/rollback, late fees, risk/circuit, spread và duplicate protection.
- 8 HTTP management scenarios chạy production executor + live engine qua Postman localhost: long/short partial TP + restart, close-timeout, partial-fill, stop-timeout, take-rejected, confirmed stop-rejected/emergency close, unknown-order. Cộng 6 execution failure checks của giai đoạn trước.
- Supabase đã cấu hình: 11 kiểm tra journal/idempotency/mode/performance/approval/rollback/RLS trong schema `codex_validation_<random>`; tự xóa schema. Không sửa schema public hoặc lịch sử thật.
- Replay config live trên 30 ngày BTC/ETH, 12 windows: **0 trades**. Không có bằng chứng tăng lợi nhuận hoặc đủ điều kiện promote. Dữ liệu này đã được xem khi phát triển, không còn là holdout chưa đụng tới.

## Các file chính

Application: `TradingGovernance.cs`, `TradingPolicyRuntime.cs`, `PerformanceAnalyzer.cs`, `TradingResearchCommands.cs`, `MultiTimeframeStrategy.cs`, `PositionManager.cs`, `RiskManager.cs`, `Interfaces.cs`, `TechnicalIndicators.cs`, chỉnh `BreakoutStrategy.cs`.

Domain/config: `ResearchModels.cs`, `TradingModels.cs`, `LiveTradeEvent.cs`, `Configuration/Options.cs`, `appsettings.json`, `Program.cs`.

Infrastructure: `LiveTradingEngine.cs` + `.Advanced.cs`, `BingXLiveExecutor.cs` + `.Management.cs`, `BingXRestClient.cs`, `PaperTradingEngine.cs`, `TradingAnalyticsWorker.cs`, `SqlTradingResearchStore.cs`, `PostgresTradeStore.cs`, `SqliteTradeStore.cs`.

Kiểm chứng: `TradingGovernanceTests.cs`, `LivePositionManagementTests.cs` và các test strategy/risk/recovery/persistence; `tests/TradingBot.ExecutionSmoke`, `tests/TradingBot.ResearchSmoke`, `postman/mocks/bingx-live-management`, simulation/context; `tools/TradingBot.Backtest`, research configs và tài liệu/schema. Những thay đổi triển khai WSL/CI đã có sẵn trong working tree không thuộc phần adaptive này.

Tham chiếu hợp đồng sàn: [BingX official trade reference](https://raw.githubusercontent.com/BingX-API/api-ai-skills/main/skills/swap-trade/api-reference.md), [account/income reference](https://raw.githubusercontent.com/BingX-API/api-ai-skills/main/skills/swap-account/api-reference.md). HTTP mock chứng minh xử lý các response được mô hình hóa, chưa xác minh hành vi/race thực tế trên tài khoản BingX.
