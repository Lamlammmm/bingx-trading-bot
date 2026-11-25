using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.Live;

public sealed partial class LiveTradingEngine
{
    public async Task ProcessAsync(MarketUpdate update, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        await _processing.WaitAsync(cancellationToken);
        var scan = _diagnostics?.Begin(update, "LIVE", "Current",
            _governance is null ? (_strategyOptions.UseMultiTimeframeAnalysis ? "mtf-v1" : "breakout-v1") : "unresolved");
        try
        {
            try
            {
                _exchangeChecked = false;
                await ProcessCoreAsync(update, scan, cancellationToken);
                if (_exchangeChecked && _state.ConsecutiveApiFailures != 0)
                {
                    _state.ConsecutiveApiFailures = 0;
                    await SaveAsync(cancellationToken);
                }
            }
            catch (Exception exception) when (_advanced && _initialized &&
                (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
            {
                _state.ConsecutiveApiFailures++;
                if (_state.ConsecutiveApiFailures >= _options.MaximumConsecutiveApiFailures)
                    _state.ApiCircuitUntil = DateTimeOffset.UtcNow.AddMinutes(_options.ApiCircuitBreakerMinutes);
                await SaveAsync(CancellationToken.None);
                throw;
            }
        }
        catch (Exception exception)
        {
            scan?.Fail(exception);
            throw;
        }
        finally
        {
            _processing.Release();
            if (scan is not null) _diagnostics!.Record(scan);
        }
    }

    private async Task ProcessAdvancedAsync(MarketUpdate update, SignalScan? scan, CancellationToken token)
    {
        var held = _state.Positions.SingleOrDefault(p => p.Symbol == update.Symbol);
        if (held is not null)
        {
            if (scan is not null) scan.Stage = "POSITION_MANAGEMENT";
            scan?.Reject("POSITION_ALREADY_OPEN");
            // Keep the account peak while positions are held, not just when an entry signal appears.
            await ObserveAccountAsync(token);
            if (held.Management is not null && (_governance is null || held.Management.Metrics.Context is not null))
                await ManagePositionAsync(held, update.Candle, token);
            return;
        }
        // Back off new entries during exchange failures, but keep checking known protection.
        if (_state.ApiCircuitUntil is { } until && DateTimeOffset.UtcNow < until)
        {
            scan?.Reject("API_CIRCUIT_OPEN");
            return;
        }
        // Pre-upgrade positions keep their original exchange protection. Their risk is unknown,
        // so do not open more positions until they have closed/reconciled.
        if (_state.Positions.Any(p => p.Management is null || (_governance is not null && p.Management.Metrics.Context is null)))
        {
            scan?.Reject("UNTRACKED_POSITION_RISK");
            return;
        }
        var active = _governance is null ? null : await _governance.ActiveAsync(TradingMode.LIVE, token);
        if (scan is not null && active is not null)
        {
            scan.Version = active.Snapshot.Version;
            scan.ConfigurationHash = active.Snapshot.ConfigurationHash;
        }
        if ((active is null && _strategyOptions.UseAdaptiveCurrentEntries) ||
            (active?.Configuration.Strategy.UseAdaptiveCurrentEntries == true && !_options.AllowAdaptiveCurrentEntries))
        {
            scan?.Reject("ADAPTIVE_CURRENT_LIVE_NOT_ENABLED");
            return;
        }
        var riskPolicy = active?.Configuration.Risk ?? _liveRisk;
        if (_state.LastExits.TryGetValue(update.Symbol, out var lastExit) &&
            update.Candle.OpenTime - lastExit < TimeSpan.FromMinutes(5 * riskPolicy.ExitCooldownBars))
        {
            scan?.Reject("COOLDOWN_ACTIVE");
            return;
        }
        if (_state.LossCircuitUntil is { } lossUntil && DateTimeOffset.UtcNow < lossUntil)
        {
            scan?.Reject("LOSS_CIRCUIT_OPEN");
            return;
        }
        var positionLimit = _options.MaxOpenPositions > 0 ? _options.MaxOpenPositions : riskPolicy.MaxOpenPositions;
        if (riskPolicy.MaxOpenPositions > 0 && positionLimit > 0) positionLimit = Math.Min(positionLimit, riskPolicy.MaxOpenPositions);
        if (positionLimit > 0 && _state.Positions.Count >= positionLimit)
        {
            scan?.Reject("MAX_POSITION_REACHED");
            return;
        }
        var entry = _candles.Get(update.Symbol, TimeFrame.FiveMinutes).Where(c => c.IsClosed && c.CloseTime <= update.Candle.CloseTime).ToArray();
        var medium = _candles.Get(update.Symbol, TimeFrame.ThirtyMinutes).Where(c => c.IsClosed && c.CloseTime <= update.Candle.CloseTime).ToArray();
        var higher = _candles.Get(update.Symbol, TimeFrame.OneHour).Where(c => c.IsClosed && c.CloseTime <= update.Candle.CloseTime).ToArray();
        var signalStrategy = active?.Strategy ?? _strategy;
        if (scan is not null) scan.Stage = "STRATEGY";
        var signal = signalStrategy.Evaluate(update.Symbol, entry, medium, higher);
        scan?.Observe(signal, signalStrategy.LastEvaluation);
        _logger.LogInformation("LIVE SIGNAL EVALUATION {Symbol} | dataTime={Time} | evaluation={@Evaluation} | signal={Direction}",
            update.Symbol, update.Candle.CloseTime, signalStrategy.LastEvaluation, signal?.Direction);
        signal = ConfirmMacd(signal, entry, update.TimeFrame, scan);
        if (signal is null) return;
        if (scan is not null) scan.Stage = "EXCHANGE_EXPOSURE";
        var contract = _contracts.Get(update.Symbol);
        if (contract is null || !contract.IsActive) { scan?.Reject("CONTRACT_UNAVAILABLE"); return; }
        await ReconcileAsync(token); // Validate every tracked position before allocating more portfolio risk.
        var exchangeCount = await _executor.GetOpenPositionCountAsync(token);
        _exchangeChecked = true;
        if (exchangeCount != _state.Positions.Count || await _executor.GetOpenOrderCountAsync(token) != 2 * exchangeCount)
        {
            scan?.Reject("UNTRACKED_EXCHANGE_EXPOSURE");
            _logger.LogWarning("LIVE NO TRADE {Symbol} | reason=untracked exchange positions/orders prevent aggregate risk calculation", update.Symbol);
            return;
        }
        if (await _executor.HasOpenPositionAsync(update.Symbol, token) || await _executor.GetOpenOrderCountAsync(token, update.Symbol) != 0)
        {
            scan?.Reject("EXCHANGE_EXPOSURE");
            return;
        }
        if (scan is not null) scan.Stage = "ACCOUNT_AND_RISK";
        var account = await ObserveAccountAsync(token);
        var feeRate = Math.Max(riskPolicy.FeeRate, account.TakerFeeRate);
        var adjustedEquity = account.Equity - _state.CashFlowAdjustment;
        if (_state.EquityHighWater > 0m && (_state.EquityHighWater - adjustedEquity) / _state.EquityHighWater * 100m >= riskPolicy.MaxDrawdownPercent)
        {
            scan?.Reject("DRAWDOWN_LIMIT");
            return;
        }
        var dailyPnlIncludingUnrealized = Math.Min(account.NetTradingIncomeToday + account.Equity - account.WalletBalance,
            account.Equity - _state.DayStartBalance - account.ExternalIncomeToday);
        if (_state.DayStartBalance <= 0m || dailyPnlIncludingUnrealized <= -_state.DayStartBalance * riskPolicy.MaxDailyLossPercent / 100m)
        {
            scan?.Reject("DAILY_LOSS_LIMIT");
            _logger.LogWarning("LIVE NO TRADE {Symbol} | reason=daily loss budget exhausted or unavailable | dailyPnl={Pnl}", update.Symbol, dailyPnlIncludingUnrealized);
            return;
        }
        var capital = Math.Min(account.WalletBalance, account.Equity);
        if (capital <= 0m || account.AvailableMargin <= 0m) { scan?.Reject("INSUFFICIENT_MARGIN"); return; }
        var marginFraction = _options.MaxMarginUsagePercent / 100m;
        var marginBudget = Math.Min(account.AvailableMargin * marginFraction,
            capital * marginFraction - account.UsedMargin - account.FrozenMargin);
        if (marginBudget <= 0m) { scan?.Reject("ACCOUNT_MARGIN_LIMIT"); return; }
        var decision = new AdaptiveDecision(true, 1m, "configured risk policy");
        var signalId = "sig-" + TradingPolicyRuntime.Hash($"{update.Symbol}|5m|{signal.Time.ToUnixTimeMilliseconds()}|{active?.Snapshot.ConfigurationHash}")[..32];
        if (active is not null)
        {
            var spread = _marketClient is null ? null : await _marketClient.GetSpreadBasisPointsAsync(update.Symbol, token);
            decision = await _governance!.DecideAsync(active, signal.Evaluation?.Regime ?? MarketRegime.Unknown, spread, token);
            await _governance.SignalAsync(active.Snapshot, signalId, signal, decision, token);
            if (!decision.AllowEntry)
            {
                scan?.Reject("ADAPTIVE_GUARD");
                if (scan is not null) scan.RejectionDetails = decision.Reason;
                return;
            }
        }
        var mark = await _executor.GetMarkPriceAsync(update.Symbol, token);
        var deviation = Math.Abs(mark - signal.EntryPrice) / signal.EntryPrice * 10_000m;
        if (deviation > _options.MaximumEntryDeviationBasisPoints) { scan?.Reject("ENTRY_PRICE_DEVIATION"); return; }
        var sign = signal.Direction == TradeDirection.Long ? 1m : -1m;
        // Reserve adverse entry execution before sizing; levels are still anchored in the setup.
        signal = signal with { EntryPrice = mark * (1m + sign * riskPolicy.ExecutionCostBasisPoints / 10_000m) };
        var protection = new RiskManager(Options.Create(riskPolicy)).CreateProtection(signal, contract,
            riskPolicy.StopAtrMultiplier, riskPolicy.TakeProfitRiskMultiple, out var rejection);
        if (protection is null)
        {
            scan?.Reject("PROTECTION_REJECTED");
            if (scan is not null) scan.RejectionDetails = rejection;
            _logger.LogInformation("LIVE NO TRADE {Symbol} | reason={Reason}", update.Symbol, rejection);
            return;
        }
        var riskBudget = capital * riskPolicy.RiskPerTradePercent / 100m * decision.RiskMultiplier;
        var requestedRiskQuantity = riskBudget / protection.Value.LossPerUnitAtStop;
        var maximum = Math.Min(_options.MaxLeverage, await _executor.GetMaxLeverageAsync(update.Symbol, signal.Direction, token));
        var leverage = _options.AutoLeverage ? Math.Min(maximum, Math.Max(1m, decimal.Ceiling(requestedRiskQuantity * signal.EntryPrice / marginBudget))) : _options.Leverage;
        if (leverage < 1m || leverage > maximum) { scan?.Reject("LEVERAGE_UNAVAILABLE"); return; }
        _liveRisk.MaxLeverage = leverage;
        var manager = new RiskManager(Options.Create(_liveRisk));
        var aggregateRisk = _state.Positions.Sum(p => p.Management!.InitialRiskAmount);
        var aggregateNotional = _state.Positions.Sum(p => p.Quantity * p.Management!.EntryPrice);
        var plan = active is null ? manager.CreatePlan(signal, capital, null, 0m, aggregateRisk, contract)
            : active.Plan(signal, capital, marginBudget, aggregateRisk, aggregateNotional, contract, leverage, decision.RiskMultiplier, feeRate);
        if (plan is null)
        {
            scan?.Reject("RISK_PLAN_REJECTED");
            if (scan is not null && active is null) scan.RejectionDetails = manager.LastRejectionReason;
            _logger.LogInformation("LIVE NO TRADE {Symbol} | reason={Reason}", update.Symbol, manager.LastRejectionReason);
            return;
        }
        var maximumMarginQuantity = marginBudget / (plan.EntryPrice / leverage + plan.EntryPrice * _liveRisk.FeeRate * 2m);
        var quantity = decimal.Round(Math.Min(plan.Quantity, maximumMarginQuantity), (int)contract.QuantityPrecision, MidpointRounding.ToZero);
        if (quantity <= 0m || quantity < contract.MinimumQuantity || quantity * plan.EntryPrice < contract.MinimumOrderValue)
        {
            scan?.Reject("CONTRACT_MINIMUM");
            return;
        }
        await _executor.ValidatePositionModeAsync(token);
        if (await _executor.HasOpenPositionAsync(update.Symbol, token) || await _executor.GetOpenOrderCountAsync(token, update.Symbol) != 0)
        {
            scan?.Reject("EXCHANGE_EXPOSURE");
            return;
        }
        // The operator may have halted entries while the exchange checks above were in progress.
        if (_governance is not null && await _governance.Store.IsHaltedAsync(TradingMode.LIVE, token))
        {
            scan?.Reject("OPERATOR_HALT");
            return;
        }
        scan?.Accept(plan.EntryPrice, plan.StopLoss, plan.TakeProfit, plan.NetRewardRiskRatio);
        if (scan is not null) scan.Stage = "EXECUTION_PREPARATION";
        await _executor.SetLeverageAsync(update.Symbol, signal.Direction, leverage, token);
        _state.PendingOrder = "bot-" + Guid.NewGuid().ToString("N");
        var id = _state.PendingOrder;
        var requestedAt = DateTimeOffset.UtcNow;
        var context = active is null ? null : new TradingContext(active.Snapshot, signalId, id, leverage, account.Equity, signal.Reason);
        if (context is not null) _state.TradeContexts[id] = context;
        _state.JournalOutbox.Add(new LiveTradeEvent(id + ":requested", id, update.Symbol, "entry_requested", requestedAt,
            JsonSerializer.Serialize(new { SchemaVersion = 3, Signal = signal, Plan = plan, Quantity = quantity, Leverage = leverage,
                Account = account, AggregateRisk = aggregateRisk, RiskBudget = riskBudget, MarginBudget = marginBudget,
                EntryCandles = entry, MediumTrendCandles = medium, HigherTrendCandles = higher })));
        await SaveAsync(token);
        if (scan is not null)
        {
            scan.OrderId = id;
            scan.ExecutionStatus = "ATTEMPTED";
            scan.Stage = "ORDER_SUBMISSION";
        }
        var receipt = await _executor.OpenMarketPositionAsync(update.Symbol, signal.Direction, quantity, plan.StopLoss, plan.TakeProfit, id, token);
        if (scan is not null)
        {
            scan.ExecutionStatus = "PLACED";
            scan.Stage = "FILL_VERIFICATION";
        }
        var entryLatency = (decimal)(DateTimeOffset.UtcNow - requestedAt).TotalMilliseconds;
        var opened = new SavedPosition(update.Symbol, signal.Direction, quantity, plan.StopLoss, plan.TakeProfit, requestedAt, id);
        _state.Positions.Add(opened);
        _state.JournalOutbox.Add(new LiveTradeEvent(id + ":response", id, update.Symbol, "entry_response", DateTimeOffset.UtcNow, JsonSerializer.Serialize(receipt)));
        await SaveAsync(token);
        var fill = await _executor.GetOrderFillAsync(update.Symbol, id, token);
        ValidateFill(fill, id, update.Symbol, signal.Direction, quantity, closing: false);
        var confirmedAt = DateTimeOffset.UtcNow;
        _state.JournalOutbox.Add(new LiveTradeEvent(id + ":filled", id, update.Symbol, "entry_filled", confirmedAt, JsonSerializer.Serialize(fill)));
        if (scan is not null)
        {
            scan.ExecutionStatus = "FILLED";
            scan.FilledAt = confirmedAt; // Order fill response does not expose the exchange fill time.
            scan.Stage = "PROTECTION_VERIFICATION";
        }
        if (sign * (fill.AveragePrice - plan.StopLoss) <= 0m || sign * (plan.TakeProfit - fill.AveragePrice) <= 0m)
            throw new InvalidOperationException("Confirmed entry invalidates the planned SL/TP geometry.");
        var snapshot = await _executor.GetManagedPositionAsync(update.Symbol, signal.Direction, quantity, plan.StopLoss, plan.TakeProfit, null, null, token);
        if (snapshot.EntryPrice != fill.AveragePrice) throw new InvalidOperationException("Position entry differs from confirmed order average.");
        if (scan is not null) scan.ProtectionConfirmed = true;
        var stopFill = plan.StopLoss * (1m - sign * riskPolicy.ExecutionCostBasisPoints / 10_000m);
        var actualRisk = quantity * (sign * (fill.AveragePrice - stopFill) + (fill.AveragePrice + stopFill) * feeRate);
        var managed = new LivePositionManagement(snapshot.PositionId, snapshot.EntryPrice, quantity, plan.StopLoss, actualRisk,
            snapshot.StopOrderId, snapshot.TakeOrderId, new PositionMetrics(RequestedEntryPrice: mark,
                Regime: signal.Evaluation?.Regime ?? MarketRegime.Unknown,
                RiskTimeFrame: signal.Evaluation is null ? TimeFrame.FiveMinutes : TimeFrame.FifteenMinutes, Context: context,
                EntryLatencyMilliseconds: entryLatency, Macd: signal.Macd), feeRate);
        _state.Positions[_state.Positions.IndexOf(opened)] = opened with { Management = managed };
        await SaveAsync(token);
        if (actualRisk <= 0m || actualRisk > riskBudget || aggregateRisk + actualRisk > capital * riskPolicy.MaxAggregateOpenRiskPercent / 100m)
            throw new InvalidOperationException("Confirmed fill exceeds the planned risk budget; protection retained and new orders blocked.");
        _state.JournalOutbox.Add(new LiveTradeEvent(id + ":protected", id, update.Symbol, "entry_protected", DateTimeOffset.UtcNow, JsonSerializer.Serialize(managed)));
        _state.PendingOrder = null;
        await SaveAsync(token);
    }

    private async Task<LiveAccountSnapshot> ObserveAccountAsync(CancellationToken token)
    {
        var account = await _executor.GetAccountSnapshotAsync(token);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_state.RiskDate != today)
        {
            _state.RiskDate = today;
            _state.DayStartBalance = account.DayStartBalance;
            _state.LastExternalIncomeToday = 0m;
        }
        _state.CashFlowAdjustment += account.ExternalIncomeToday - _state.LastExternalIncomeToday;
        _state.LastExternalIncomeToday = account.ExternalIncomeToday;
        _state.EquityHighWater = Math.Max(_state.EquityHighWater, account.Equity - _state.CashFlowAdjustment);
        await SaveAsync(token);
        return account;
    }

    private async Task ManagePositionAsync(SavedPosition position, Candle candle, CancellationToken token)
    {
        var management = position.Management!;
        var pinned = management.Metrics.Context is { } context ? _governance?.Pinned(context.Strategy) : null;
        var risk = pinned is null ? JsonSerializer.Deserialize<RiskOptions>(JsonSerializer.Serialize(_liveRisk))! : pinned.Configuration.Risk;
        risk.FeeRate = Math.Max(risk.FeeRate, management.FeeRate);
        if (candle.OpenTime < position.OpenedAt || management.Metrics.LastEvaluatedAt >= candle.CloseTime) return;
        var contract = _contracts.Get(position.Symbol);
        if (contract is null || !contract.IsActive) return;
        if (contract.QuantityPrecision < 0m || contract.QuantityPrecision > 28m || contract.QuantityPrecision != decimal.Truncate(contract.QuantityPrecision) ||
            contract.PricePrecision < 0m || contract.PricePrecision > 28m || contract.PricePrecision != decimal.Truncate(contract.PricePrecision))
            throw new InvalidOperationException("Invalid contract precision for live management.");
        var mark = await _executor.GetMarkPriceAsync(position.Symbol, token);
        var sign = position.Direction == TradeDirection.Long ? 1m : -1m;
        // If the exchange trigger should already have fired, only reconcile; never act on a phantom position.
        if (sign * (mark - position.StopLoss) <= 0m || sign * (position.TakeProfit - mark) <= 0m) return;
        var quantity = position.Quantity;
        var mathInput = new PaperPosition(position.Symbol, position.Direction, quantity, management.EntryPrice,
            position.StopLoss, position.TakeProfit, management.InitialRiskAmount, 0m, position.OpenedAt,
            InitialStopLoss: management.InitialStopLoss, InitialQuantity: management.InitialQuantity,
            PartialTaken: management.Metrics.TakeProfitStage > 0, Metrics: management.Metrics);
        var policy = new PositionManager(risk, new PaperTradingOptions { SlippageBasisPoints = risk.ExecutionCostBasisPoints });
        var partials = policy.PartialTargets(mathInput, sign > 0m ? Math.Min(mark, candle.Close) : Math.Max(mark, candle.Close), contract);
        var partialQuantity = partials.Sum(p => p.Quantity);
        var stage = partials.LastOrDefault()?.Stage ?? management.Metrics.TakeProfitStage;
        var income = await _executor.GetIncomeAsync(position.Symbol, position.OpenedAt, token);
        var metrics = management.Metrics with { TakeProfitStage = stage, FundingPaid = -income.FundingIncome,
            LastEvaluatedAt = candle.CloseTime, Mfe = Math.Max(management.Metrics.Mfe, Math.Max(0m, sign * (mark - management.EntryPrice))),
            Mae = Math.Max(management.Metrics.Mae, Math.Max(0m, -sign * (mark - management.EntryPrice))) };
        var bars = _candles.Get(position.Symbol, TimeFrame.FiveMinutes).Where(c => c.IsClosed && c.CloseTime <= candle.CloseTime).ToArray();
        var atr = TechnicalIndicators.Atr(metrics.RiskTimeFrame == TimeFrame.FifteenMinutes ? TechnicalIndicators.AggregateClosed(bars, 15) : bars, _strategyOptions.AtrPeriod);
        var mathPosition = new PaperPosition(position.Symbol, position.Direction, quantity - partialQuantity, management.EntryPrice,
            position.StopLoss, position.TakeProfit, management.InitialRiskAmount, 0m, position.OpenedAt,
            InitialStopLoss: management.InitialStopLoss, PartialTaken: stage > 0, InitialQuantity: management.InitialQuantity, Metrics: metrics);
        var proposed = policy.TightenStop(mathPosition, candle.CloseTime, sign > 0m ? Math.Min(mark, candle.Close) : Math.Max(mark, candle.Close), atr, contract);
        var next = position with { Management = management with { Metrics = proposed.Metrics! } };
        if (partialQuantity == 0m && proposed.StopLoss == position.StopLoss)
        {
            _state.Positions[_state.Positions.FindIndex(p => p.Symbol == position.Symbol)] = next;
            await SaveAsync(token);
            return;
        }
        _state.PendingOrder = "manage-" + Guid.NewGuid().ToString("N");
        var operation = _state.PendingOrder;
        _state.JournalOutbox.Add(new LiveTradeEvent(operation + ":requested", position.TradeId!, position.Symbol,
            "management_requested", DateTimeOffset.UtcNow, JsonSerializer.Serialize(new { Position = position, PartialQuantity = partialQuantity, NextStop = proposed.StopLoss, NextStage = stage })));
        await SaveAsync(token);
        if (partialQuantity > 0m)
        {
            var preparedStop = await ReplaceStopAsync(position, position.StopLoss, token);
            if (preparedStop is null) return;
            management = management with { StopOrderId = preparedStop };
            position = position with { Management = management };
            next = next with { Management = next.Management! with { StopOrderId = preparedStop } };
            _state.Positions[_state.Positions.FindIndex(p => p.Symbol == position.Symbol)] = position;
            await SaveAsync(token);
            var prepared = await _executor.GetManagedPositionAsync(position.Symbol, position.Direction, position.Quantity,
                position.StopLoss, position.TakeProfit, preparedStop, management.TakeOrderId, token);
            if (prepared.PositionId != management.PositionId || !prepared.StopClosesPosition)
                throw new InvalidOperationException("Close-entire-position SL is not confirmed; partial close was not sent.");
            await _executor.CloseMarketPositionAsync(position.Symbol, position.Direction, partialQuantity, operation, token);
            var fill = await _executor.GetOrderFillAsync(position.Symbol, operation, token);
            ValidateFill(fill, operation, position.Symbol, position.Direction, partialQuantity, closing: true);
            var actual = await _executor.GetManagedPositionAsync(position.Symbol, position.Direction, quantity - partialQuantity,
                position.StopLoss, position.TakeProfit, management.StopOrderId, management.TakeOrderId, token, allowOversizedProtection: true);
            if (actual.PositionId != management.PositionId || actual.EntryPrice != management.EntryPrice) throw new InvalidOperationException("Partial close changed position identity.");
            next = next with { Quantity = quantity - partialQuantity };
            _state.JournalOutbox.Add(new LiveTradeEvent(operation + ":fill", position.TradeId!, position.Symbol,
                "partial_fill_confirmed", DateTimeOffset.UtcNow, JsonSerializer.Serialize(fill)));
            _state.Positions[_state.Positions.FindIndex(p => p.Symbol == position.Symbol)] = next;
            await SaveAsync(token);
        }
        var stopId = await ReplaceStopAsync(next, proposed.StopLoss, token);
        if (stopId is null) return;
        next = next with { StopLoss = proposed.StopLoss, Management = next.Management! with { StopOrderId = stopId } };
        _state.Positions[_state.Positions.FindIndex(p => p.Symbol == position.Symbol)] = next;
        await SaveAsync(token);
        if (partialQuantity > 0m)
        {
            var takeId = await _executor.ReplaceProtectionAsync(position.Symbol, position.Direction, management.TakeOrderId,
                "TAKE_PROFIT_MARKET", next.Quantity, position.TakeProfit, token);
            next = next with { Management = next.Management! with { TakeOrderId = takeId } };
            _state.Positions[_state.Positions.FindIndex(p => p.Symbol == position.Symbol)] = next;
            await SaveAsync(token);
        }
        var confirmed = await _executor.GetManagedPositionAsync(position.Symbol, position.Direction, next.Quantity, next.StopLoss,
            next.TakeProfit, next.Management!.StopOrderId, next.Management.TakeOrderId, token);
        if (confirmed.PositionId != management.PositionId) throw new InvalidOperationException("Protection confirmation changed position identity.");
        _state.JournalOutbox.Add(new LiveTradeEvent(operation + ":confirmed", position.TradeId!, position.Symbol,
            "management_confirmed", DateTimeOffset.UtcNow, JsonSerializer.Serialize(next)));
        _state.PendingOrder = null;
        await SaveAsync(token);
    }

    private async Task<string?> ReplaceStopAsync(SavedPosition position, decimal trigger, CancellationToken token)
    {
        try
        {
            return await _executor.ReplaceProtectionAsync(position.Symbol, position.Direction, position.Management!.StopOrderId,
                "STOP_MARKET", position.Quantity, trigger, token);
        }
        catch (LiveProtectionReplacementException)
        {
            var exposed = await _executor.GetPositionAsync(position.Symbol, token);
            if (exposed.PositionId != position.Management!.PositionId || exposed.Direction != position.Direction || exposed.Quantity != position.Quantity)
                throw new InvalidOperationException("Unprotected position identity is uncertain; emergency close was not sent.");
            var emergencyId = "emergency-" + Guid.NewGuid().ToString("N")[..30];
            _state.PendingOrder = emergencyId;
            _state.JournalOutbox.Add(new LiveTradeEvent(emergencyId + ":requested", position.TradeId!, position.Symbol,
                "emergency_close_requested", DateTimeOffset.UtcNow, JsonSerializer.Serialize(exposed)));
            // Restoring safety must still work if the DB fails after cancellation removed the SL.
            // The local durable outbox and pending ID remain mandatory; this never authorizes a new entry.
            await SaveAsync(token, flushJournal: false);
            await _executor.CloseMarketPositionAsync(position.Symbol, position.Direction, exposed.Quantity, emergencyId, token);
            var fill = await _executor.GetOrderFillAsync(position.Symbol, emergencyId, token);
            ValidateFill(fill, emergencyId, position.Symbol, position.Direction, exposed.Quantity, closing: true);
            if (await _executor.HasOpenPositionAsync(position.Symbol, token)) throw new InvalidOperationException("Emergency close quantity is not confirmed flat.");
            _state.JournalOutbox.Add(new LiveTradeEvent(emergencyId + ":filled", position.TradeId!, position.Symbol,
                "emergency_close_confirmed", DateTimeOffset.UtcNow, JsonSerializer.Serialize(fill)));
            await SaveAsync(token, flushJournal: false);
            return null;
        }
    }

    private static void ValidateFill(LiveOrderFill fill, string clientId, string symbol, TradeDirection direction, decimal quantity, bool closing)
    {
        var expectedSide = (direction == TradeDirection.Long) != closing ? "BUY" : "SELL";
        if (string.IsNullOrWhiteSpace(fill.OrderId) || fill.ClientOrderId != clientId || fill.Symbol != symbol ||
            fill.Side != expectedSide || fill.PositionSide != (direction == TradeDirection.Long ? "LONG" : "SHORT") ||
            fill.Status != "FILLED" || fill.ExecutedQuantity != quantity || fill.AveragePrice <= 0m)
            throw new InvalidOperationException("Order fill identity/status/quantity is not confirmed; no further order writes are allowed.");
    }
}
