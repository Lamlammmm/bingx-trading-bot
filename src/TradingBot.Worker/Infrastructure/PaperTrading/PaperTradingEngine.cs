using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure;

namespace TradingBot.Worker.Infrastructure.PaperTrading;

public sealed class PaperTradingEngine(ICandleStore candleStore, ITradingStrategy strategy, IRiskManager riskManager,
    IOpenAiAnalyzer openAiAnalyzer, ITradeStore tradeStore, IContractStore contractStore, IOptions<RiskOptions> riskOptions,
    IOptions<PaperTradingOptions> paperTradingOptions,
    IOptions<OpenAIOptions> openAiOptions, IOptions<StrategyOptions> strategyOptions,
    ILogger<PaperTradingEngine> logger, TradingGovernance? governance = null, IBingXMarketClient? marketClient = null,
    SignalDiagnostics? diagnostics = null, string? shadowVersion = null, string? shadowConfigurationHash = null,
    bool enforcePortfolioLimits = false, Func<PaperPosition, Candle, decimal>? replayFunding = null)
{
    private static readonly EventId AiTradeEvent = new(2_100, "AiTrade");
    private static readonly EventId AiNoTradeEvent = new(2_101, "AiNoTrade");
    private static readonly EventId AiErrorEvent = new(2_102, "AiError");
    private static readonly EventId RuleSignalEvent = new(2_200, "RuleSignal");
    private static readonly EventId RuleRejectedEvent = new(2_201, "RuleRejected");
    private static readonly EventId RuleNoSignalEvent = new(2_202, "RuleNoSignal");
    private readonly RiskOptions _riskOptions = riskOptions.Value;
    private readonly PaperTradingOptions _paperOptions = paperTradingOptions.Value;
    private readonly OpenAIOptions _openAiOptions = openAiOptions.Value;
    private readonly StrategyOptions _strategyOptions = strategyOptions.Value;
    private readonly Dictionary<string, PaperPosition?> _positionsBySymbol = new();
    private readonly Dictionary<(string Symbol, TimeFrame TimeFrame), DateTimeOffset> _lastProcessedCandles = new();
    private readonly Dictionary<string, DateTimeOffset> _lastPositionExitTimes = new();
    private int _totalWins;
    private int _totalLosses;
    private decimal _totalNetPnl;
    private decimal _balance;
    private DateOnly _balanceDate;
    private decimal _realizedPnlToday;
    private RiskControlState _controls = new(0m);
    private bool _persistenceFailed;
    private readonly List<TradingJournalEvent> _journalEvents = new();
    private readonly PositionManager _positionManager = new(riskOptions.Value, paperTradingOptions.Value);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_paperOptions.SlippageBasisPoints < 0m || _paperOptions.SpreadBasisPoints < 0m ||
            _paperOptions.SlippageBasisPoints + _paperOptions.SpreadBasisPoints / 2m >= 10_000m ||
            _riskOptions.MaxOpenPositions < 0 || _riskOptions.ExitCooldownBars < 0 ||
            _riskOptions.MaximumConsecutiveLosses < 0 || _riskOptions.CircuitBreakerMinutes <= 0 ||
            (enforcePortfolioLimits && (_riskOptions.StartingBalance <= 0m || _riskOptions.MaxDrawdownPercent <= 0m ||
                _riskOptions.MaxAggregateExposurePercent <= 0m)) ||
            (_riskOptions.UseDynamicPositionManagement && (_riskOptions.BreakEvenTriggerR <= 0m ||
                _riskOptions.TrailingStartR <= 0m || _riskOptions.TrailingAtrMultiplier <= 0m ||
                _riskOptions.MinimumTrailingStepAtr < 0m || _riskOptions.StopAdjustmentCooldownBars < 0)))
            throw new InvalidOperationException("Invalid paper execution or position management options.");
        await tradeStore.InitializeAsync(cancellationToken);
        if (governance is not null) await governance.InitializeAsync(TradingMode.PAPER, cancellationToken);
        var persisted = await tradeStore.LoadAccountStateAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (persisted is null)
        {
            _balance = _riskOptions.StartingBalance;
            _balanceDate = today;
            _realizedPnlToday = 0m;
            _totalWins = 0;
            _totalLosses = 0;
            _totalNetPnl = 0m;
            logger.LogInformation("Paper trading initialized with balance {Balance} USDT (no persisted state found)", _balance);
        }
        else
        {
            _balance = persisted.Balance;
            _balanceDate = today;
            _realizedPnlToday = persisted.BalanceDate == today ? persisted.RealizedPnlToday : 0m;
            _totalWins = persisted.TotalWins;
            _totalLosses = persisted.TotalLosses;
            _totalNetPnl = persisted.TotalNetPnl;
            logger.LogInformation("Paper trading resumed from persisted state: balance={Balance} USDT, realizedPnlToday={RealizedPnlToday}",
                _balance, _realizedPnlToday);
        }

        _controls = persisted?.RiskControls ?? new RiskControlState(_balance - _realizedPnlToday);
        if (persisted is not null && persisted.BalanceDate != today) _controls = _controls with { DayStartBalance = _balance };
        if (_controls.LastExits is not null)
            foreach (var pair in _controls.LastExits) _lastPositionExitTimes[pair.Key] = pair.Value;
        var performance = await tradeStore.LoadTradePerformanceAsync(cancellationToken);
        if (performance is not null)
        {
            _totalWins = performance.Wins;
            _totalLosses = performance.Losses;
            _totalNetPnl = performance.TotalNetPnl;
            logger.LogInformation("Paper performance reconciled from closed trades: wins={Wins}, losses={Losses}, totalNetPnl={TotalNetPnl}",
                _totalWins, _totalLosses, _totalNetPnl);
        }

        foreach (var position in await tradeStore.LoadOpenPositionsAsync(cancellationToken))
        {
            _positionsBySymbol[position.Symbol] = position;
            logger.LogWarning("PAPER POSITION RESTORED {Direction} {Quantity} {Symbol} @ {Entry}; SL={StopLoss}; TP={TakeProfit}",
                position.Direction, position.Quantity, position.Symbol, position.EntryPrice, position.StopLoss, position.TakeProfit);
        }

        await SaveAccountStateAsync(cancellationToken);
    }

    public async Task ProcessAsync(MarketUpdate update, CancellationToken cancellationToken)
    {
        var scan = diagnostics?.Begin(update, shadowVersion is null ? "PAPER" : "SHADOW",
            shadowVersion is null ? "Current" : "Experimental",
            shadowVersion ?? (governance is not null ? "unresolved" : _strategyOptions.UseAdaptiveCurrentEntries
                ? MultiTimeframeStrategy.AdaptiveVersion : _strategyOptions.UseMultiTimeframeAnalysis ? "mtf-v1" : "breakout-v1"));
        if (scan is not null) scan.ConfigurationHash = shadowConfigurationHash;
        try
        {
            await ProcessCoreAsync(update, scan, cancellationToken);
        }
        catch (Exception exception)
        {
            scan?.Fail(exception);
            throw;
        }
        finally
        {
            if (scan is not null) diagnostics!.Record(scan);
        }
    }

    private async Task ProcessCoreAsync(MarketUpdate update, SignalScan? scan, CancellationToken cancellationToken)
    {
        if (_persistenceFailed) throw new InvalidOperationException("Paper persistence failed; reload persisted state before processing more bars.");
        var symbol = update.Symbol;
        candleStore.Upsert(symbol, update.TimeFrame, update.Candle);
        if (update.TimeFrame != TimeFrame.FiveMinutes || !update.Candle.IsClosed) return;
        var key = (symbol, update.TimeFrame);
        if (_lastProcessedCandles.TryGetValue(key, out var lastProcessedCandle) && update.Candle.OpenTime <= lastProcessedCandle)
        {
            scan?.Reject("DUPLICATE_CANDLE");
            return;
        }
        _lastProcessedCandles[key] = update.Candle.OpenTime;
        await ResetDailyStateIfNeededAsync(update.Candle.CloseTime, cancellationToken);

        var position = _positionsBySymbol.GetValueOrDefault(symbol);
        if (position is not null)
        {
            if (scan is not null) scan.Stage = "POSITION_MANAGEMENT";
            scan?.Reject("POSITION_ALREADY_OPEN");
            // Never replay pre-entry prices against a new position.
            if (update.Candle.CloseTime > position.OpenedAt)
                await TryClosePositionAsync(symbol, position, update.Candle, cancellationToken);
            return;
        }
        var active = governance is null ? null : await governance.ActiveAsync(TradingMode.PAPER, cancellationToken);
        if (scan is not null && active is not null)
        {
            scan.Version = active.Snapshot.Version;
            scan.ConfigurationHash = active.Snapshot.ConfigurationHash;
        }
        var guardRisk = active?.Configuration.Risk ?? _riskOptions;
        var drawdownLimitReached = enforcePortfolioLimits && _controls.PeakClosedEquity is { } peak &&
            peak - (_riskOptions.StartingBalance + _totalNetPnl) >= peak * _riskOptions.MaxDrawdownPercent / 100m;
        if (_lastPositionExitTimes.TryGetValue(symbol, out var lastExit) && update.Candle.CloseTime <= lastExit)
        {
            scan?.Reject("PREVIOUS_POSITION_CANDLE");
            return;
        }
        if (_paperOptions.MaximumEntryAgeSeconds > 0 &&
            (DateTimeOffset.UtcNow - update.Candle.CloseTime > TimeSpan.FromSeconds(_paperOptions.MaximumEntryAgeSeconds) ||
             update.Candle.CloseTime > DateTimeOffset.UtcNow.AddSeconds(10)))
        {
            scan?.Reject("STALE_MARKET_DATA");
            return;
        }
        var guardReason = _lastPositionExitTimes.TryGetValue(symbol, out var exitTime) &&
            update.Candle.OpenTime - exitTime < TimeSpan.FromMinutes(5 * guardRisk.ExitCooldownBars) ? "post-exit cooldown"
            : _controls.CircuitOpenUntil is { } circuitUntil && update.Candle.CloseTime < circuitUntil ? "consecutive-loss circuit breaker"
            : _riskOptions.MaxOpenPositions > 0 && _positionsBySymbol.Values.Count(p => p is not null) >= _riskOptions.MaxOpenPositions ? "position limit reached"
            : _realizedPnlToday <= -_controls.DayStartBalance * _riskOptions.MaxDailyLossPercent / 100m ? "daily loss budget exhausted"
            : drawdownLimitReached ? "drawdown limit reached" : null;
        if (guardReason is not null)
        {
            scan?.Reject(guardReason switch
            {
                "post-exit cooldown" => "COOLDOWN_ACTIVE",
                "consecutive-loss circuit breaker" => "LOSS_CIRCUIT_OPEN",
                "position limit reached" => "MAX_POSITION_REACHED",
                "drawdown limit reached" => "DRAWDOWN_LIMIT",
                _ => "DAILY_LOSS_LIMIT"
            });
            logger.LogInformation(RuleRejectedEvent, "PAPER NO TRADE {Symbol} | dataTime={DataTime} | reason={Reason}", symbol, update.Candle.CloseTime, guardReason);
            return;
        }
        if (update.IsReconciliation)
        {
            scan?.Reject("RECONCILIATION_ONLY");
            logger.LogInformation("Skipping new entry on reconciled closed 5m candle for {Symbol} at {OpenTime}",
                symbol, update.Candle.OpenTime);
            return;
        }

        // Freeze one closed-candle context for strategy, optional AI and persisted entry features.
        var entryCandles = candleStore.Get(symbol, TimeFrame.FiveMinutes)
            .Where(candle => candle.IsClosed && candle.CloseTime <= update.Candle.CloseTime).ToArray();
        var mediumTrendCandles = candleStore.Get(symbol, TimeFrame.ThirtyMinutes)
            .Where(candle => candle.IsClosed && candle.CloseTime <= update.Candle.CloseTime).ToArray();
        var higherTrendCandles = candleStore.Get(symbol, TimeFrame.OneHour)
            .Where(candle => candle.IsClosed && candle.CloseTime <= update.Candle.CloseTime).ToArray();
        var signalStrategy = active?.Strategy ?? strategy;
        if (scan is not null) scan.Stage = "STRATEGY";
        var technicalSignal = signalStrategy.Evaluate(symbol, entryCandles, mediumTrendCandles, higherTrendCandles);
        scan?.Observe(technicalSignal, signalStrategy.LastEvaluation);
        if (signalStrategy.LastEvaluation is { } evaluation)
            logger.LogInformation("SIGNAL EVALUATION {Symbol} | timeframe=5m | dataTime={DataTime} | regime={Regime} | ADX1h={Adx} | ATR1hPercent={AtrPercent} | EMAfast1h={EmaFast} | EMAslow1h={EmaSlow} | EMAprimary1h={EmaPrimary} | RSI15m={Rsi} | structure30m={Structure} | reason={Reason}",
                symbol, evaluation.Time, evaluation.Regime, evaluation.Adx, evaluation.AtrPercent,
                evaluation.Ema20, evaluation.Ema50, evaluation.Ema200, evaluation.Rsi15m, evaluation.Structure, evaluation.Reason);
        var signalText = technicalSignal is null ? "none" : technicalSignal.Direction.ToString();
        logger.LogInformation(RuleNoSignalEvent,
            "Evaluated closed 5m candle for {Symbol}: open={Open}, high={High}, low={Low}, close={Close}, volume={Volume}, technicalSignal={TechnicalSignal}",
            symbol, update.Candle.Open, update.Candle.High, update.Candle.Low, update.Candle.Close, update.Candle.Volume, signalText);

        if (technicalSignal is not null)
        {
            var (estimatedStopLoss, estimatedTakeProfit) = EstimateLevels(technicalSignal);
            logger.LogWarning(RuleSignalEvent,
                "========== RULE SIGNAL {Direction} ========== {Symbol} | entry={Entry} | estSL={EstimatedStopLoss} | estTP={EstimatedTakeProfit} | atr={Atr} | reason={Reason}",
                technicalSignal.Direction, symbol, technicalSignal.EntryPrice, estimatedStopLoss, estimatedTakeProfit,
                technicalSignal.Atr, technicalSignal.Reason);
        }

        var signal = technicalSignal;
        if (openAiAnalyzer.Enabled)
        {
            if (scan is not null) scan.Stage = "AI_CONFIRMATION";
            var result = await openAiAnalyzer.AnalyzeAsync(new AiAnalysisContext(symbol, update.Candle.CloseTime,
                entryCandles, mediumTrendCandles, higherTrendCandles, technicalSignal), cancellationToken);
            signal = BuildAiSignal(result, technicalSignal, update.Candle, symbol);
            if (signal is null)
            {
                if (result.Decision is null) scan?.Fail("AI_ANALYSIS_ERROR");
                else scan?.Reject("AI_FILTER_REJECTED");
                return;
            }
            scan?.Observe(signal, signalStrategy.LastEvaluation);
        }
        if (signal is null) return;
        if (_paperOptions.Macd.Enabled)
        {
            if (scan is not null) scan.Stage = "MACD_CONFIRMATION";
            var macd = MacdEntryFilter.Evaluate(entryCandles, signal, update.TimeFrame, _paperOptions.Macd);
            if (scan is not null) scan.Macd = macd;
            MacdEntryFilter.Log(logger, "PAPER", signal, macd);
            if (!macd.Allowed)
            {
                scan?.Reject("MACD_FILTER_REJECTED");
                return;
            }
            signal = signal with { Macd = macd };
        }

        if (scan is not null) scan.Stage = "ACCOUNT_AND_RISK";
        var decision = new AdaptiveDecision(true, 1m, "configured risk policy");
        var signalId = "sig-" + TradingPolicyRuntime.Hash($"{symbol}|5m|{signal.Time.ToUnixTimeMilliseconds()}|{active?.Snapshot.ConfigurationHash}")[..32];
        if (active is not null)
        {
            var spread = marketClient is null ? null : await marketClient.GetSpreadBasisPointsAsync(symbol, cancellationToken);
            decision = await governance!.DecideAsync(active, signal.Evaluation?.Regime ?? MarketRegime.Unknown, spread, cancellationToken);
            await governance.SignalAsync(active.Snapshot, signalId, signal, decision, cancellationToken);
            if (!decision.AllowEntry)
            {
                scan?.Reject("ADAPTIVE_GUARD");
                if (scan is not null) scan.RejectionDetails = decision.Reason;
                return;
            }
        }

        var aggregateOpenRisk = _positionsBySymbol.Values.Where(p => p is not null).Sum(p => p!.RiskAmount);
        var contract = contractStore.Get(symbol);
        if (contract is null)
        {
            scan?.Reject("CONTRACT_UNAVAILABLE");
            logger.LogWarning(RuleRejectedEvent,
                "========== TRADE REJECTED ========== {Symbol} | reason=contract metadata is unavailable",
                symbol);
            return;
        }
        var entryRisk = active?.Configuration.Risk ?? _riskOptions;
        var entryCosts = active?.Configuration.Costs ?? _paperOptions;
        var slippageRate = (entryCosts.SlippageBasisPoints + entryCosts.SpreadBasisPoints / 2m) / 10_000m;
        var executionEntryPrice = signal.Direction == TradeDirection.Long
            ? signal.EntryPrice * (1m + slippageRate)
            : signal.EntryPrice * (1m - slippageRate);
        var usedMargin = _positionsBySymbol.Values.Where(p => p is not null)
            .Sum(p => p!.EntryPrice * p.Quantity / (p.Metrics?.Context?.Leverage ?? _riskOptions.MaxLeverage));
        var aggregateNotional = _positionsBySymbol.Values.Where(p => p is not null).Sum(p => p!.EntryPrice * p.Quantity);
        var plan = active is null ? riskManager.CreatePlan(signal with { EntryPrice = executionEntryPrice }, _balance, position,
            _realizedPnlToday, aggregateOpenRisk, contract) : active.Plan(signal with { EntryPrice = executionEntryPrice }, _balance,
                Math.Max(0m, _balance - usedMargin), aggregateOpenRisk, aggregateNotional, contract, entryRisk.MaxLeverage, decision.RiskMultiplier);
        if (plan is null)
        {
            scan?.Reject("RISK_PLAN_REJECTED");
            if (scan is not null && active is null) scan.RejectionDetails = riskManager.LastRejectionReason;
            logger.LogWarning(RuleRejectedEvent,
                "========== TRADE REJECTED BY RISK MANAGER ========== {Symbol} | Direction={Direction} | Entry={Entry} | Source={Source} | Reason={Reason}",
                symbol, signal.Direction, signal.EntryPrice, signal.Source, riskManager.LastRejectionReason ?? signal.Reason);
            return;
        }
        if (enforcePortfolioLimits &&
            aggregateNotional + plan.EntryPrice * plan.Quantity > _balance * _riskOptions.MaxAggregateExposurePercent / 100m)
        {
            scan?.Reject("EXPOSURE_LIMIT");
            logger.LogInformation(RuleRejectedEvent, "PAPER NO TRADE {Symbol} | reason={Reason}", symbol, "portfolio exposure limit reached");
            return;
        }
        if (plan.EntryPrice * plan.Quantity / entryRisk.MaxLeverage + plan.EntryFee > _balance - usedMargin)
        {
            scan?.Reject("INSUFFICIENT_MARGIN");
            logger.LogWarning(RuleRejectedEvent, "PAPER NO TRADE {Symbol} | reason={Reason}", symbol, "insufficient free margin after existing paper positions");
            return;
        }

        var context = active is null ? null : new TradingContext(active.Snapshot, signalId, "paper-" + Guid.NewGuid().ToString("N"),
            entryRisk.MaxLeverage, _balance, signal.Reason);
        scan?.Accept(plan.EntryPrice, plan.StopLoss, plan.TakeProfit, plan.NetRewardRiskRatio);
        if (scan is not null)
        {
            scan.OrderId = context?.TradeId ?? (shadowVersion is not null ? "shadow-" + scan.Id : null);
            scan.ExecutionStatus = "ATTEMPTED";
            scan.Stage = "SIMULATED_FILL";
        }
        if (context is not null)
            _journalEvents.Add(new(TradingMode.PAPER, context.TradeId + ":open", context.TradeId, signalId, active!.Snapshot.Version,
                active.Snapshot.ConfigurationHash, symbol, "entry_filled", signal.Time, System.Text.Json.JsonSerializer.Serialize(new { Context = context, Plan = plan })));
        _balance -= plan.EntryFee;
        _realizedPnlToday -= plan.EntryFee;
        var entryFeatures = BuildEntryFeatures(signal with { EntryPrice = plan.EntryPrice },
            entryCandles, mediumTrendCandles, higherTrendCandles);
        _positionsBySymbol[symbol] = new PaperPosition(plan.Symbol, plan.Direction, plan.Quantity, plan.EntryPrice, plan.StopLoss,
            plan.TakeProfit, plan.RiskAmount, plan.EntryFee, signal.Time, entryFeatures,
            InitialStopLoss: plan.StopLoss, InitialQuantity: plan.Quantity,
            Metrics: new PositionMetrics(RequestedEntryPrice: signal.EntryPrice,
                Regime: signal.Evaluation?.Regime ?? MarketRegime.Unknown,
                RiskTimeFrame: signal.Evaluation is null ? TimeFrame.FiveMinutes : TimeFrame.FifteenMinutes, Context: context, Macd: signal.Macd));
        logger.LogWarning("========== PAPER OPEN {Direction} ========== {Quantity} {Symbol} @ {Entry}; SL={StopLoss}; TP={TakeProfit}; risk={Risk}; netRewardRisk={NetRewardRisk}; source={Source}; reason={Reason}",
            plan.Direction, plan.Quantity, plan.Symbol, plan.EntryPrice, plan.StopLoss, plan.TakeProfit, plan.RiskAmount, plan.NetRewardRiskRatio, signal.Source, plan.Reason);
        await SaveAccountStateAsync(cancellationToken);
        if (scan is not null)
        {
            scan.ExecutionStatus = "FILLED";
            scan.FilledAt = signal.Time.ToUniversalTime();
        }
    }

    private async Task SaveAccountStateAsync(CancellationToken cancellationToken, ClosedTrade? closedTrade = null)
    {
        try
        {
            if (enforcePortfolioLimits)
                _controls = _controls with { PeakClosedEquity = Math.Max(_controls.PeakClosedEquity ?? _riskOptions.StartingBalance,
                    _riskOptions.StartingBalance + _totalNetPnl) };
            var account = new AccountState(_balance, _balanceDate, _realizedPnlToday, _totalWins, _totalLosses, _totalNetPnl,
                _controls with { LastExits = new(_lastPositionExitTimes) });
            var positions = _positionsBySymbol.Values.Where(p => p is not null).Select(p => p!).ToArray();
            if (governance is null) await tradeStore.CommitPaperStateAsync(account, positions, closedTrade, cancellationToken);
            else
            {
                await tradeStore.CommitPaperStateWithJournalAsync(account, positions, closedTrade, _journalEvents, cancellationToken);
                _journalEvents.Clear();
                if (closedTrade is not null) await governance.AnalyzeAsync(TradingMode.PAPER, cancellationToken);
            }
        }
        catch
        {
            _persistenceFailed = true;
            throw;
        }
    }

    private TradeFeatures BuildEntryFeatures(StrategySignal signal, IReadOnlyList<Candle> entryCandles,
        IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles)
    {
        var lastCandle = entryCandles.Count > 0 ? entryCandles[^1] : null;
        var averageVolume = TechnicalIndicators.AverageVolume(entryCandles, _strategyOptions.VolumeLookback);
        var volumeRatio = lastCandle is not null && averageVolume > 0m ? lastCandle.Volume / averageVolume : 0m;

        // Preserve legacy storage names: these contain entry 5m / medium 30m / higher 1h indicators.
        return new TradeFeatures(
            Rsi5m: TechnicalIndicators.Rsi(entryCandles, _strategyOptions.RsiPeriod),
            Rsi15m: TechnicalIndicators.Rsi(mediumTrendCandles, _strategyOptions.RsiPeriod),
            Rsi1h: TechnicalIndicators.Rsi(higherTrendCandles, _strategyOptions.RsiPeriod),
            Atr5m: TechnicalIndicators.Atr(entryCandles, _strategyOptions.AtrPeriod),
            EmaFast5m: TechnicalIndicators.Ema(entryCandles, _strategyOptions.FastEmaPeriod),
            EmaSlow5m: TechnicalIndicators.Ema(entryCandles, _strategyOptions.SlowEmaPeriod),
            VolumeRatio5m: volumeRatio,
            BodyRatio5m: lastCandle is not null ? TechnicalIndicators.BodyRatio(lastCandle) : 0m,
            Confidence: signal.Confidence,
            HourOfDayUtc: signal.Time.UtcDateTime.Hour,
            DayOfWeekUtc: (int)signal.Time.UtcDateTime.DayOfWeek);
    }

    private (decimal StopLoss, decimal TakeProfit) EstimateLevels(StrategySignal signal)
    {
        var stopDistance = signal.Atr * _riskOptions.StopAtrMultiplier;
        var stopLoss = signal.Direction == TradeDirection.Long
            ? signal.EntryPrice - stopDistance
            : signal.EntryPrice + stopDistance;
        var takeProfit = signal.Direction == TradeDirection.Long
            ? signal.EntryPrice + stopDistance * _riskOptions.TakeProfitRiskMultiple
            : signal.EntryPrice - stopDistance * _riskOptions.TakeProfitRiskMultiple;
        return (stopLoss, takeProfit);
    }

    private StrategySignal? BuildAiSignal(AiAnalysisResult result, StrategySignal? technicalSignal, Candle candle, string symbol)
    {
        var decision = result.Decision;
        if (decision is null)
        {
            logger.LogError(AiErrorEvent,
                "========== AI RESULT: ERROR ========== {Symbol} | candle={CandleTime} | code={ErrorCode} | HTTP={HttpStatusCode} | requestId={RequestId}",
                symbol, candle.CloseTime, result.ErrorCode ?? "unknown", result.HttpStatusCode?.ToString() ?? "n/a",
                result.RequestId ?? "unavailable");
            return null;
        }

        if (decision.Action == "no_trade")
        {
            logger.LogInformation(AiNoTradeEvent,
                "========== AI RESULT: NO_TRADE ========== {Symbol} | confidence={Confidence:P0} | reason={Reason} | invalidation={Invalidation}",
                symbol, decision.Confidence, decision.Reason, decision.Invalidation);
            return null;
        }

        if (technicalSignal is null || decision.Action is not ("long" or "short") ||
            decision.Confidence < _openAiOptions.MinimumConfidence ||
            (decision.Action == "long") != (technicalSignal.Direction == TradeDirection.Long))
        {
            logger.LogInformation(AiNoTradeEvent,
                "========== AI RESULT: NO_TRADE ========== {Symbol} | reason=technical signal gate did not pass",
                symbol);
            return null;
        }

        logger.LogWarning(AiTradeEvent,
            "========== AI RESULT: TRADE {Action} ========== {Symbol} | confidence={Confidence:P0} | entry={Entry} | SL={StopLoss} | TP={TakeProfit} | reason={Reason} | invalidation={Invalidation}",
            decision.Action.ToUpperInvariant(), symbol, decision.Confidence, decision.EntryPrice,
            decision.StopLoss, decision.TakeProfit, decision.Reason, decision.Invalidation);

        var entryDeviation = Math.Abs(decision.EntryPrice - candle.Close) / candle.Close * 100m;
        if (entryDeviation > _openAiOptions.MaximumEntryDeviationPercent)
        {
            logger.LogWarning("========== AI TRADE BLOCKED | entry deviation {DeviationPercent}% exceeds {MaximumPercent}% ==========",
                entryDeviation, _openAiOptions.MaximumEntryDeviationPercent);
            return null;
        }

        var atr = technicalSignal?.Atr ?? TechnicalIndicators.Atr(candleStore.Get(symbol, TimeFrame.FiveMinutes), _strategyOptions.AtrPeriod);
        if (atr <= 0m)
        {
            logger.LogWarning("========== AI TRADE BLOCKED | ATR is unavailable ==========");
            return null;
        }
        var direction = decision.Action == "long" ? TradeDirection.Long : TradeDirection.Short;
        return new StrategySignal(symbol, direction, candle.CloseTime, candle.Close, atr,
            $"OpenAI confidence {decision.Confidence:0.##}: {decision.Reason}; invalidation: {decision.Invalidation}",
            decision.StopLoss, decision.TakeProfit, decision.Confidence, "openai", technicalSignal?.InvalidationPrice, technicalSignal?.Evaluation);
    }

    private async Task<bool> TryClosePositionAsync(string symbol, PaperPosition position, Candle candle, CancellationToken cancellationToken)
    {
        var closedCandles = candleStore.Get(symbol, TimeFrame.FiveMinutes)
            .Where(c => c.IsClosed && c.CloseTime <= candle.CloseTime).ToArray();
        var atr = TechnicalIndicators.Atr(position.Metrics?.RiskTimeFrame == TimeFrame.FifteenMinutes
            ? TechnicalIndicators.AggregateClosed(closedCandles, 15) : closedCandles, _strategyOptions.AtrPeriod);
        var pinned = position.Metrics?.Context?.Strategy is { } snapshot ? governance?.Pinned(snapshot) : null;
        var manager = pinned is null ? _positionManager : new PositionManager(pinned.Configuration.Risk, pinned.Configuration.Costs);
        var transition = manager.Evaluate(position, candle, atr, contractStore.Get(symbol), replayFunding?.Invoke(position, candle));
        _balance += transition.CashDelta;
        _realizedPnlToday += transition.CashDelta;
        _positionsBySymbol[symbol] = transition.IsClosed ? null : transition.Position;
        ClosedTrade? closedTrade = null;
        if (transition.IsClosed)
        {
            if (transition.TradeNetPnl >= 0m) _totalWins++; else _totalLosses++;
            _totalNetPnl += transition.TradeNetPnl;
            var losses = transition.TradeNetPnl < 0m ? _controls.ConsecutiveLosses + 1 : 0;
            _controls = _controls with { ConsecutiveLosses = losses };
            if (_riskOptions.MaximumConsecutiveLosses > 0 && losses >= _riskOptions.MaximumConsecutiveLosses)
                _controls = _controls with { CircuitOpenUntil = candle.CloseTime.AddMinutes(_riskOptions.CircuitBreakerMinutes) };
            _lastPositionExitTimes[symbol] = candle.CloseTime;
            closedTrade = new ClosedTrade(position.Symbol, position.Direction,
                position.InitialQuantity > 0m ? position.InitialQuantity : position.Quantity,
                position.EntryPrice, transition.ExitPrice, position.InitialStopLoss > 0m ? position.InitialStopLoss : position.StopLoss,
                position.TakeProfit, transition.TradeNetPnl, transition.ExitReason,
                "paper", position.OpenedAt, candle.CloseTime, position.EntryFeatures, transition.Metrics);
            if (transition.Metrics?.Context is { } context)
                _journalEvents.Add(TradingGovernance.OutcomeEvent(new(TradingMode.PAPER, context.TradeId, context.SignalId, context.Strategy,
                    position.Symbol, TimeFrame.FiveMinutes, position.Metrics!.Regime, position.Direction,
                    position.InitialQuantity > 0m ? position.InitialQuantity : position.Quantity, position.EntryPrice,
                    transition.Metrics.AverageExitPrice, closedTrade.StopLoss, position.TakeProfit, context.Leverage,
                    transition.Metrics.TotalFees, transition.Metrics.FundingPaid, transition.TradeNetPnl,
                    transition.Metrics.InitialRiskAmount > 0m ? transition.TradeNetPnl / transition.Metrics.InitialRiskAmount : null,
                    transition.Metrics.Mfe, transition.Metrics.Mae, position.OpenedAt, candle.CloseTime, context.EntryReason,
                    transition.ExitReason, transition.Metrics.EntrySlippageBasisPoints,
                    null, true, "closed-bar estimate; exit slippage is the final reduction only", context.EquityAtEntry, transition.Metrics.InitialRiskAmount,
                    ExitSlippageBasisPoints: transition.Metrics.FinalExitSlippageBasisPoints)));
            logger.LogWarning("PAPER CLOSE {Symbol} | reason={Reason} | price={Price} | netPnl={Pnl} | balance={Balance} | metrics={@Metrics}",
                symbol, transition.ExitReason, transition.ExitPrice, transition.TradeNetPnl, _balance, transition.Metrics);
        }
        else if (transition.Position.StopLoss != position.StopLoss || transition.Position.Quantity != position.Quantity)
        {
            if (position.Metrics?.Context is { } context)
                _journalEvents.Add(new(TradingMode.PAPER, context.TradeId + ":managed:" + candle.CloseTime.ToUnixTimeMilliseconds(),
                    context.TradeId, context.SignalId, context.Strategy.Version, context.Strategy.ConfigurationHash, position.Symbol,
                    "position_update", candle.CloseTime, System.Text.Json.JsonSerializer.Serialize(transition)));
            logger.LogInformation("PAPER MANAGEMENT {Symbol} | remainingQty={Quantity} | nextBarStop={Stop} | stage={Stage} | cashDelta={CashDelta}",
                symbol, transition.Position.Quantity, transition.Position.StopLoss, transition.Position.Metrics?.TakeProfitStage, transition.CashDelta);
        }
        await SaveAccountStateAsync(cancellationToken, closedTrade);
        return transition.IsClosed;
    }

    private async Task ResetDailyStateIfNeededAsync(DateTimeOffset candleTime, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(candleTime.UtcDateTime);
        if (_controls.TradingDateTime is { } previousTime && today < DateOnly.FromDateTime(previousTime.UtcDateTime)) return;
        _controls = _controls with { TradingDateTime = candleTime };
        if (today == _balanceDate) return;
        _balanceDate = today;
        _realizedPnlToday = 0m;
        _controls = _controls with { DayStartBalance = _balance };
        logger.LogInformation("Paper trading daily risk window reset. Start balance={Balance}", _balance);
        await SaveAccountStateAsync(cancellationToken);
    }
}
