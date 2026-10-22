# Live Trading

Live is enabled in appsettings.json. One worker manages multiple USDT perpetual
positions in Hedge mode, with at most one bot position per symbol. `Live:MaxOpenPositions=0`
removes the overall count limit; a positive value sets an optional bot-position limit.
Manual positions and orders on other symbols do not block entries. A symbol with
any existing position or open order is skipped, without modifying manual SL/TP.
Do not manually add to or change a bot-managed symbol: exchange quantity/side
must still match its saved state.

Each entry reads current available USDT margin. Quantity is the smaller of:
- the existing `Risk:RiskPerTradePercent` budget divided by the ATR stop distance;
- the margin-funded quantity, using `Live:MaxMarginUsagePercent` (20% of current
  available margin) and reserving estimated entry/exit fees from `Risk:FeeRate`.

This is dynamic sizing, not a fixed USDT amount or a daily loss cutoff. Leverage
is selected automatically and capped by `Live:MaxLeverage` and the exchange limit;
quantity is reduced when the cap cannot fund the original risk-sized quantity.
Set `Live:AutoLeverage=false` to use `Live:Leverage` instead. Rounded quantities
must meet exchange minimums. TP distance must exceed estimated trading fees;
funding, real slippage and fee-tier differences can still reduce net profit.

Signals require breakout/retest, volume/body/RSI filters, and aligned closed
30m/1h trends on 15m entry candles. Retest reconfirms both trends and candle direction; forming/future
trend candles are excluded. These checks have automated tests, but no measured
win-rate or profitability improvement is claimed. AI decisions do not control live entries.

Time-based exits have been removed from both live and paper execution.
There is no `MaxHoldingMinutes` option; old values are ignored by this build.
Bot-managed live positions remain open until the exchange SL or TP closes them.
Restart the worker manually to load this build. Paper retains its configured
partial-TP behavior and checks protection on 5m candles; only closed 15m candles
can trigger entries. Retest 5 bars is a 75-minute window; cooldown 12 bars is 3 hours.

## Check Before Restart

Run this from the repository root. It does not start the trading loop or place orders:

```powershell
dotnet artifacts/15m-sltp-ready/TradingBot.Worker.dll --contentRoot "$PWD/src/TradingBot.Worker" --environment Development --preflight
```

Preflight checks credentials, Hedge mode, available USDT, open positions and open
orders. It does not prove that an actual order or attached SL/TP will be accepted.
The full startup also validates local state and exchange protection.

## Start

Stop the old worker in its terminal/debugger first. Do not run old and new builds
concurrently. The new version locks its state file, but older versions do not.
Then run from the repository root:

```powershell
dotnet artifacts/15m-sltp-ready/TradingBot.Worker.dll --contentRoot "$PWD/src/TradingBot.Worker" --environment Development
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
  Manual changes to a tracked symbol, partial fills or missing protection fail
  validation for that symbol. Inspect BingX immediately when validation fails.
- Each entry has a unique client order ID, saved before sending. An error or
  timeout leaves `PendingOrder` set and prevents retries and future entries,
  including after restart. This is deliberately conservative even on a rejected
  order. Reconcile that ID and any remaining position/protective orders before
  correcting the saved state. Recovery of an uncertain request is manual.
- Native SL/TP executes on the exchange and closures are detected on subsequent
  fresh 15m updates. The worker does not send time-based closing orders.
  Orphan orders block reuse of their symbol; the bot never cancels unknown orders.
- Live entries have no daily loss limit or aggregate risk cutoff. Several positions
  can lose together. Available margin and exchange SL/TP checks remain active.
- State version 2 persists `Positions` independently. A version-1 `Position` is
  migrated on startup, preserving quantity, side, SL/TP and trade ID. Back up state
  before deployment; the old worker cannot read version 2 for rollback.
- State corruption, missing credentials or incompatible mode fails startup.
  REST requests are paced and malformed account responses fail closed.
- Stopping the process does not close exchange positions. Native SL/TP remains
  on BingX. Check positions in BingX
  before disabling live or changing deployments.

The automated tests use mocked exchange responses. Read-only production
preflight is separate from actual entry/exit verification; no real-money test
trade is required by the commands above.

## Trade History For Analysis

Paper stores current positions in open_positions and closed results/features in
closed_trades (PostgreSQL; SQLite uses OpenPositions/ClosedTrades). The current
paper feature names are legacy. From the 2026-10-21 migration, Rsi5m stores
the entry 15m input, Rsi15m stores the medium 30m input, and Rsi1h stores the
higher 1h input. ATR/EMA/volume/body columns with a 5m suffix also describe the
15m entry input. Schema is preserved; do not combine older/newer trades without
checking their dates and strategy context.

Live writes a separate append-only live_trade_events table in the configured
persistence provider. Event IDs deduplicate writes across restarts. The journal
contains entry_requested (schema version 2, explicit 15m/30m/1h timeframe labels,
client ID, signal, reason, EntryCandles/MediumTrendCandles/HigherTrendCandles snapshots,
strategy/live settings, contract minimums, balance, quantity, leverage, SL/TP),
entry_response (raw exchange receipt, reported price), entry_protected,
closed_observed and
exchange_position_history for detected closures including exchange SL/TP exits. Older journal records may still contain
schema version 1 or TIME_EXIT events; they are retained as history.

entry_requested is an intent, not proof of a fill. closed_observed uses detection
time, not actual fill time. Native closure reasons, fees and PnL must be read
from exchange history and matched by position/order IDs; the raw history may
contain other trades for that symbol in the queried time window. No guessed
PnL or win rate is inserted. Empty history is recorded as unavailable before
clearing the saved position when the exchange confirms that tracked symbol is flat.
Unknown pending requests still require manual reconciliation as described above.

Events are first saved in the JSON state outbox, then inserted into the DB.
An entry is not sent unless its intent has been persisted. DB failure blocks
further processing until recovery/restart, and the outbox is replayed before
pending-order checks. Keep the state file as well as the DB.

Inspect journal counts without starting the trading worker:

```powershell
dotnet artifacts/15m-sltp-ready/TradingBot.Worker.dll --contentRoot "$PWD/src/TradingBot.Worker" --environment Development --journal-status
```

Export live events as JSONL without starting the worker:

```powershell
dotnet artifacts/15m-sltp-ready/TradingBot.Worker.dll --contentRoot "$PWD/src/TradingBot.Worker" --environment Development --export-live-journal
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
belong to this trade. Legacy paper feature columns changed
meaning across timeframe migrations; use trade dates and strategy version before
comparing old and new samples.

The command prints the file path under src/TradingBot.Worker/exports. Payloads
include no BingX credentials or DB connection strings. Exports contain account
balances and trading activity, so treat them as private. New journal coverage
starts after restarting the updated worker. Older signal/candle context cannot
be reconstructed from a current-state JSON file. Legacy saved positions are
identified explicitly; this change does not backfill prior trades or log every
rejected/no-signal candle. Analyze closed trades without treating attempted
entries as executed trades or assuming the sample covers all market opportunities.
