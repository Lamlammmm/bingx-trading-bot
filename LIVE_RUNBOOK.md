# Live Trading

Live is enabled in appsettings.json. The worker uses USDT perpetual futures in
Hedge mode, one position at a time, 90% of available USDT as the margin budget,
and automatic leverage from 1x up to 20x. The bot rounds the exchange minimum
quantity/value up to the contract precision and selects the lowest integer
leverage that can fund that minimum. It also respects the exchange limit for
the symbol and direction. If the required leverage exceeds either limit, it
skips the entry. Logs include the selected leverage and quantities.
Set Live:AutoLeverage=false to use Live:Leverage (5x) instead.
This is a balance percentage, not a fixed USDT cap. A larger account
balance means a larger next order. Exchange minimum quantity and notional still
apply. Paper risk settings and AI decisions do not control live orders.

## Check Before Restart

Run this from the repository root. It does not start the trading loop or place orders:

```powershell
dotnet artifacts/live-ready/TradingBot.Worker.dll --contentRoot "$PWD/src/TradingBot.Worker" --environment Development --preflight
```

Preflight checks credentials, Hedge mode, available USDT, open positions and open
orders. It does not prove that an actual order or attached SL/TP will be accepted.
The full startup also validates local state and exchange protection.

## Start

Stop the old worker in its terminal/debugger first. Do not run old and new builds
concurrently. The new version locks its state file, but older versions do not.
Then run from the repository root:

```powershell
dotnet artifacts/live-ready/TradingBot.Worker.dll --contentRoot "$PWD/src/TradingBot.Worker" --environment Development
```

Look for `LIVE READY` in Seq (or console if Seq is not configured). Entries wait
for the existing breakout/retest strategy. `LIVE SKIPPED` means the current
balance cannot meet the exchange minimum. `LIVE OPEN` is logged only after the
worker confirms the position and full-quantity SL/TP orders on BingX.

## Recovery And Limits

- Keep `src/TradingBot.Worker/data/live-state.json` across builds and restarts.
  Do not delete it to bypass a halt. Use the same content root on every launch.
- Attached SL/TP is requested on the exchange with each entry. Recovery validates
  the saved symbol, side and quantity against the exchange and checks for SL/TP.
  Manual changes, partial fills, missing protection or unknown positions block
  new entries. Inspect BingX immediately when validation fails.
- Each entry has a unique client order ID, saved before sending. An error or
  timeout leaves `PendingOrder` set and prevents retries and future entries,
  including after restart. This is deliberately conservative even on a rejected
  order. Reconcile that ID and any remaining position/protective orders before
  correcting the saved state. Recovery of an uncertain request is manual.
- Native SL/TP closes are detected on subsequent fresh 5m updates. TIME_EXIT
  uses a Hedge-mode opposite-side market order and waits for exchange confirmation.
  Orphan orders block new entries; the bot never cancels unknown account orders.
- Live entries have no daily loss limit. Position sizing still uses 90% of
  available USDT as the margin budget; exchange SL/TP and the other entry
  checks remain active.
- State corruption, missing credentials or incompatible mode fails startup.
  REST requests are paced and malformed account responses fail closed.
- Stopping the process does not close exchange positions. Native SL/TP remains
  on BingX, but TIME_EXIT requires a running worker. Check positions in BingX
  before disabling live or changing deployments.

The automated tests use mocked exchange responses. Read-only production
preflight is separate from actual entry/exit verification; no real-money test
trade is required by the commands above.

## Trade History For Analysis

Paper stores current positions in open_positions and closed results/features in
closed_trades (PostgreSQL; SQLite uses OpenPositions/ClosedTrades). The current
paper feature names are legacy: Rsi15m stores the 1h input and Rsi1h stores the
4h input after the timeframe migration. Do not infer timeframes from those
column names alone when analyzing older exports.

Live writes a separate append-only live_trade_events table in the configured
persistence provider. Event IDs deduplicate writes across restarts. The journal
contains entry_requested (client ID, signal, reason, candle snapshots at 5m/1h/4h,
strategy/live settings, contract minimums, balance, quantity, leverage, SL/TP),
entry_response (raw exchange receipt, reported price), entry_protected,
close_requested/close_response for TIME_EXIT, closed_observed and
exchange_position_history for detected closures including exchange SL/TP exits.

entry_requested is an intent, not proof of a fill. closed_observed uses detection
time, not actual fill time. Native closure reasons, fees and PnL must be read
from exchange history and matched by position/order IDs; the raw history may
contain other trades for that symbol in the queried time window. No guessed
PnL or win rate is inserted. Empty history is recorded as unavailable before
clearing the saved position when the exchange confirms the account is flat.
Unknown pending requests still require manual reconciliation as described above.

Events are first saved in the JSON state outbox, then inserted into the DB.
An entry is not sent unless its intent has been persisted. DB failure blocks
further processing until recovery/restart, and the outbox is replayed before
pending-order checks. Keep the state file as well as the DB.

Inspect journal counts without starting the trading worker:

```powershell
dotnet artifacts/live-ready/TradingBot.Worker.dll --contentRoot "$PWD/src/TradingBot.Worker" --environment Development --journal-status
```

Export live events as JSONL without starting the worker:

```powershell
dotnet artifacts/live-ready/TradingBot.Worker.dll --contentRoot "$PWD/src/TradingBot.Worker" --environment Development --export-live-journal
```

Export one row per paper trade and one row per live trade for analysis:

```powershell
dotnet artifacts/analysis-ready/TradingBot.Worker.dll --contentRoot "$PWD/src/TradingBot.Worker" --environment Development --export-trade-analysis
```

The command reads the configured DB and writes two JSONL files under `exports`.
Paper rows include recorded net PnL and entry features. Live rows group raw
journal events by trade ID; `NetPnl` stays null and `OutcomeVerified` false until
exchange fills and fees are independently matched to that trade. `HistoryAvailable`
only says whether the exchange history query returned rows, not whether those rows
belong to this trade. Legacy paper feature columns `Rsi15m` and `Rsi1h` changed
meaning after the 5m migration; use trade dates and strategy version before
comparing old and new samples.

The command prints the file path under src/TradingBot.Worker/exports. Payloads
include no BingX credentials or DB connection strings. Exports contain account
balances and trading activity, so treat them as private. New journal coverage
starts after restarting the updated worker. Older signal/candle context cannot
be reconstructed from a current-state JSON file. Legacy saved positions are
identified explicitly; this change does not backfill prior trades or log every
rejected/no-signal candle. Analyze closed trades without treating attempted
entries as executed trades or assuming the sample covers all market opportunities.
