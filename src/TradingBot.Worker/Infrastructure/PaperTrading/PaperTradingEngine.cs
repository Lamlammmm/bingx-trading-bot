using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.PaperTrading;

public sealed class PaperTradingEngine(ICandleStore candleStore, ITradingStrategy strategy, IRiskManager riskManager,
    IOpenAiAnalyzer openAiAnalyzer, ITradeStore tradeStore, IContractStore contractStore, IOptions<RiskOptions> riskOptions,
    IOptions<PaperTradingOptions> paperTradingOptions,
    IOptions<OpenAIOptions> openAiOptions, IOptions<StrategyOptions> strategyOptions,
    ILogger<PaperTradingEngine> logger)
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
    private readonly Dictionary<string, DateTimeOffset?> _lastProcessedCandleBySymbol = new();
    private int _totalWins;
    private int _totalLosses;
    private decimal _totalNetPnl;
    private decimal _balance;
    private DateOnly _balanceDate;
    private decimal _realizedPnlToday;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await tradeStore.InitializeAsync(cancellationToken);
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
        var symbol = update.Symbol;
        candleStore.Upsert(symbol, update.TimeFrame, update.Candle);
        if (update.TimeFrame != TimeFrame.FifteenMinutes || !update.Candle.IsClosed) return;
        var lastProcessedCandle = _lastProcessedCandleBySymbol.GetValueOrDefault(symbol);
        if (lastProcessedCandle is not null && update.Candle.OpenTime <= lastProcessedCandle) return;
        _lastProcessedCandleBySymbol[symbol] = update.Candle.OpenTime;
        await ResetDailyStateIfNeededAsync(cancellationToken);

        var position = _positionsBySymbol.GetValueOrDefault(symbol);
        if (position is not null)
        {
            if (await TryClosePositionAsync(symbol, position, update.Candle, cancellationToken)) return;
            return;
        }
        if (update.IsReconciliation)
        {
            logger.LogInformation("Skipping new entry on reconciled closed 15m candle for {Symbol} at {OpenTime}",
                symbol, update.Candle.OpenTime);
            return;
        }

        var technicalSignal = strategy.Evaluate(symbol, candleStore.Get(symbol, TimeFrame.FifteenMinutes),
            candleStore.Get(symbol, TimeFrame.OneHour), candleStore.Get(symbol, TimeFrame.FourHours));
        var signalText = technicalSignal is null ? "none" : technicalSignal.Direction.ToString();
        logger.LogInformation(RuleNoSignalEvent,
            "Evaluated closed 15m candle for {Symbol}: open={Open}, high={High}, low={Low}, close={Close}, volume={Volume}, technicalSignal={TechnicalSignal}",
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
            var result = await openAiAnalyzer.AnalyzeAsync(new AiAnalysisContext(symbol, update.Candle.CloseTime,
                candleStore.Get(symbol, TimeFrame.FifteenMinutes), candleStore.Get(symbol, TimeFrame.OneHour),
                candleStore.Get(symbol, TimeFrame.FourHours), technicalSignal), cancellationToken);
            signal = BuildAiSignal(result, technicalSignal, update.Candle, symbol);
            if (signal is null) return;
        }
        if (signal is null) return;

        var aggregateOpenRisk = _positionsBySymbol.Values.Where(p => p is not null).Sum(p => p!.RiskAmount);
        var contract = contractStore.Get(symbol);
        if (contract is null)
        {
            logger.LogWarning(RuleRejectedEvent,
                "========== TRADE REJECTED ========== {Symbol} | reason=contract metadata is unavailable",
                symbol);
            return;
        }
        var slippageRate = _paperOptions.SlippageBasisPoints / 10_000m;
        var executionEntryPrice = signal.Direction == TradeDirection.Long
            ? signal.EntryPrice * (1m + slippageRate)
            : signal.EntryPrice * (1m - slippageRate);
        var plan = riskManager.CreatePlan(signal with { EntryPrice = executionEntryPrice }, _balance, position,
            _realizedPnlToday, aggregateOpenRisk, contract);
        if (plan is null)
        {
            logger.LogWarning(RuleRejectedEvent,
                "========== TRADE REJECTED BY RISK MANAGER ========== {Symbol} | Direction={Direction} | Entry={Entry} | Source={Source} | Reason={Reason}",
                symbol, signal.Direction, signal.EntryPrice, signal.Source, signal.Reason);
            return;
        }

        _balance -= plan.EntryFee;
        var entryFeatures = BuildEntryFeatures(symbol, signal with { EntryPrice = plan.EntryPrice });
        _positionsBySymbol[symbol] = new PaperPosition(plan.Symbol, plan.Direction, plan.Quantity, plan.EntryPrice, plan.StopLoss,
            plan.TakeProfit, plan.RiskAmount, plan.EntryFee, signal.Time, entryFeatures);
        logger.LogWarning("========== PAPER OPEN {Direction} ========== {Quantity} {Symbol} @ {Entry}; SL={StopLoss}; TP={TakeProfit}; risk={Risk}; source={Source}; reason={Reason}",
            plan.Direction, plan.Quantity, plan.Symbol, plan.EntryPrice, plan.StopLoss, plan.TakeProfit, plan.RiskAmount, signal.Source, plan.Reason);
        await tradeStore.SaveOpenPositionsAsync(_positionsBySymbol.Values.Where(p => p is not null).Select(p => p!).ToArray(), cancellationToken);
        await SaveAccountStateAsync(cancellationToken);
    }

    private Task SaveAccountStateAsync(CancellationToken cancellationToken) =>
        tradeStore.SaveAccountStateAsync(new AccountState(_balance, _balanceDate, _realizedPnlToday,
            _totalWins, _totalLosses, _totalNetPnl), cancellationToken);

    private TradeFeatures BuildEntryFeatures(string symbol, StrategySignal signal)
    {
        // Note: TradeFeatures/DB column names still say 5m/15m/1h for historical reasons, but since the
        // entry/medium/higher timeframes moved to 15m/1h/4h, these now carry that data instead.
        var entryCandles = candleStore.Get(symbol, TimeFrame.FifteenMinutes);
        var mediumTrendCandles = candleStore.Get(symbol, TimeFrame.OneHour);
        var higherTrendCandles = candleStore.Get(symbol, TimeFrame.FourHours);
        var lastCandle = entryCandles.Count > 0 ? entryCandles[^1] : null;
        var averageVolume = TechnicalIndicators.AverageVolume(entryCandles, _strategyOptions.VolumeLookback);
        var volumeRatio = lastCandle is not null && averageVolume > 0m ? lastCandle.Volume / averageVolume : 0m;

        return new TradeFeatures(
            Rsi5m: TechnicalIndicators.Rsi(entryCandles, _strategyOptions.RsiPeriod),
            Rsi15m: TechnicalIndicators.Rsi(mediumTrendCandles, _strategyOptions.RsiPeriod),
            Rsi1h: TechnicalIndicators.Rsi(higherTrendCandles, _strategyOptions.RsiPeriod),
            Atr5m: signal.Atr,
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

        if (technicalSignal is null)
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

        var atr = technicalSignal?.Atr ?? TechnicalIndicators.Atr(candleStore.Get(symbol, TimeFrame.FifteenMinutes), _strategyOptions.AtrPeriod);
        if (atr <= 0m)
        {
            logger.LogWarning("========== AI TRADE BLOCKED | ATR is unavailable ==========");
            return null;
        }
        var direction = decision.Action == "long" ? TradeDirection.Long : TradeDirection.Short;
        return new StrategySignal(symbol, direction, candle.CloseTime, candle.Close, atr,
            $"OpenAI confidence {decision.Confidence:0.##}: {decision.Reason}; invalidation: {decision.Invalidation}",
            decision.StopLoss, decision.TakeProfit, decision.Confidence, "openai");
    }

    private async Task<bool> TryClosePositionAsync(string symbol, PaperPosition position, Candle candle, CancellationToken cancellationToken)
    {
        decimal? exitPrice = null;
        var exitReason = string.Empty;
        if (position.Direction == TradeDirection.Long)
        {
            if (candle.Low <= position.StopLoss) { exitPrice = position.StopLoss; exitReason = "SL"; }
            else if (candle.High >= position.TakeProfit) { exitPrice = position.TakeProfit; exitReason = "TP"; }
        }
        else if (candle.High >= position.StopLoss) { exitPrice = position.StopLoss; exitReason = "SL"; }
        else if (candle.Low <= position.TakeProfit) { exitPrice = position.TakeProfit; exitReason = "TP"; }

        if (exitPrice is null && _paperOptions.MaxHoldingMinutes > 0 &&
            candle.CloseTime >= position.OpenedAt.AddMinutes(_paperOptions.MaxHoldingMinutes))
        {
            exitPrice = candle.Close;
            exitReason = "TIME_EXIT";
        }

        if (exitPrice is null) return false;
        var directionMultiplier = position.Direction == TradeDirection.Long ? 1m : -1m;
        var slippageRate = _paperOptions.SlippageBasisPoints / 10_000m;
        var executionExitPrice = position.Direction == TradeDirection.Long
            ? exitPrice.Value * (1m - slippageRate)
            : exitPrice.Value * (1m + slippageRate);
        var grossPnl = (executionExitPrice - position.EntryPrice) * position.Quantity * directionMultiplier;
        var exitFee = executionExitPrice * position.Quantity * _riskOptions.FeeRate;
        var fundingPeriods = Math.Max(0m, (decimal)Math.Floor((candle.CloseTime - position.OpenedAt).TotalHours / 8d));
        var fundingFee = position.EntryPrice * position.Quantity * _paperOptions.FundingRatePerEightHours * fundingPeriods * directionMultiplier;
        var netPnl = grossPnl - position.EntryFee - exitFee - fundingFee;
        _balance += grossPnl - exitFee - fundingFee;
        _realizedPnlToday += netPnl;
        if (netPnl >= 0m) _totalWins++; else _totalLosses++;
        _totalNetPnl += netPnl;
        logger.LogWarning("PAPER CLOSE {Reason} {Direction} {Quantity} {Symbol} @ {Exit}; funding={Funding}; netPnl={NetPnl}; balance={Balance}",
            exitReason, position.Direction, position.Quantity, position.Symbol, executionExitPrice, fundingFee, netPnl, _balance);

        var totalTrades = _totalWins + _totalLosses;
        var winRate = totalTrades == 0 ? 0m : (decimal)_totalWins / totalTrades;
        logger.LogWarning("PERFORMANCE totalTrades={TotalTrades}; wins={Wins}; losses={Losses}; winRate={WinRate:P0}; totalNetPnl={TotalNetPnl}; balance={Balance}",
            totalTrades, _totalWins, _totalLosses, winRate, _totalNetPnl, _balance);
        _positionsBySymbol[symbol] = null;

        await tradeStore.SaveOpenPositionsAsync(_positionsBySymbol.Values.Where(p => p is not null).Select(p => p!).ToArray(), cancellationToken);
        await tradeStore.RecordClosedTradeAsync(new ClosedTrade(position.Symbol, position.Direction, position.Quantity,
            position.EntryPrice, executionExitPrice, position.StopLoss, position.TakeProfit, netPnl, exitReason,
            "paper", position.OpenedAt, candle.CloseTime, position.EntryFeatures), cancellationToken);
        await SaveAccountStateAsync(cancellationToken);
        return true;
    }

    private async Task ResetDailyStateIfNeededAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today == _balanceDate) return;
        _balanceDate = today;
        _realizedPnlToday = 0m;
        logger.LogInformation("Paper trading daily risk window reset. Start balance={Balance}", _balance);
        await SaveAccountStateAsync(cancellationToken);
    }
}
