using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.PaperTrading;

public sealed class PaperTradingEngine(ICandleStore candleStore, ITradingStrategy strategy, IRiskManager riskManager,
    IOpenAiAnalyzer openAiAnalyzer, IOptions<RiskOptions> riskOptions, IOptions<OpenAIOptions> openAiOptions,
    IOptions<StrategyOptions> strategyOptions, ILogger<PaperTradingEngine> logger)
{
    private static readonly EventId AiTradeEvent = new(2_100, "AiTrade");
    private static readonly EventId AiNoTradeEvent = new(2_101, "AiNoTrade");
    private static readonly EventId AiErrorEvent = new(2_102, "AiError");
    private static readonly EventId RuleSignalEvent = new(2_200, "RuleSignal");
    private static readonly EventId RuleRejectedEvent = new(2_201, "RuleRejected");
    private static readonly EventId RuleNoSignalEvent = new(2_202, "RuleNoSignal");
    private readonly RiskOptions _riskOptions = riskOptions.Value;
    private readonly OpenAIOptions _openAiOptions = openAiOptions.Value;
    private readonly StrategyOptions _strategyOptions = strategyOptions.Value;
    private readonly Dictionary<string, PaperPosition?> _positionsBySymbol = new();
    private readonly Dictionary<string, DateTimeOffset?> _lastProcessedCandleBySymbol = new();
    private decimal _balance;
    private DateOnly _balanceDate;
    private decimal _realizedPnlToday;

    public void Initialize()
    {
        _balance = _riskOptions.StartingBalance;
        _balanceDate = DateOnly.FromDateTime(DateTime.UtcNow);
        logger.LogInformation("Paper trading initialized with balance {Balance} USDT", _balance);
    }

    public async Task ProcessAsync(MarketUpdate update, CancellationToken cancellationToken)
    {
        var symbol = update.Symbol;
        candleStore.Upsert(symbol, update.TimeFrame, update.Candle);
        if (update.TimeFrame != TimeFrame.FifteenMinutes || !update.Candle.IsClosed) return;
        var lastProcessedCandle = _lastProcessedCandleBySymbol.GetValueOrDefault(symbol);
        if (lastProcessedCandle is not null && update.Candle.OpenTime <= lastProcessedCandle) return;
        _lastProcessedCandleBySymbol[symbol] = update.Candle.OpenTime;
        ResetDailyStateIfNeeded();

        var position = _positionsBySymbol.GetValueOrDefault(symbol);
        if (position is not null)
        {
            if (TryClosePosition(symbol, position, update.Candle)) return;
            return;
        }

        var technicalSignal = strategy.Evaluate(symbol, candleStore.Get(symbol, TimeFrame.FifteenMinutes),
            candleStore.Get(symbol, TimeFrame.OneHour), candleStore.Get(symbol, TimeFrame.FourHours));
        if (technicalSignal is null)
        {
            logger.LogInformation(RuleNoSignalEvent, "Evaluated closed 15m candle for {Symbol}: close={Close}, technicalSignal=none",
                symbol, update.Candle.Close);
        }
        else
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

        var plan = riskManager.CreatePlan(signal, _balance, position, _realizedPnlToday);
        if (plan is null)
        {
            logger.LogWarning(RuleRejectedEvent,
                "========== TRADE REJECTED BY RISK MANAGER ========== {Symbol} | Direction={Direction} | Entry={Entry} | Source={Source} | Reason={Reason}",
                symbol, signal.Direction, signal.EntryPrice, signal.Source, signal.Reason);
            return;
        }

        _balance -= plan.EntryFee;
        _positionsBySymbol[symbol] = new PaperPosition(plan.Symbol, plan.Direction, plan.Quantity, plan.EntryPrice, plan.StopLoss,
            plan.TakeProfit, plan.RiskAmount, plan.EntryFee, signal.Time);
        logger.LogWarning("========== PAPER OPEN {Direction} ========== {Quantity} {Symbol} @ {Entry}; SL={StopLoss}; TP={TakeProfit}; risk={Risk}; source={Source}; reason={Reason}",
            plan.Direction, plan.Quantity, plan.Symbol, plan.EntryPrice, plan.StopLoss, plan.TakeProfit, plan.RiskAmount, signal.Source, plan.Reason);
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

    private bool TryClosePosition(string symbol, PaperPosition position, Candle candle)
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

        if (exitPrice is null) return false;
        var directionMultiplier = position.Direction == TradeDirection.Long ? 1m : -1m;
        var grossPnl = (exitPrice.Value - position.EntryPrice) * position.Quantity * directionMultiplier;
        var exitFee = exitPrice.Value * position.Quantity * _riskOptions.FeeRate;
        var netPnl = grossPnl - position.EntryFee - exitFee;
        _balance += grossPnl - exitFee;
        _realizedPnlToday += netPnl;
        logger.LogInformation("PAPER CLOSE {Reason} {Direction} {Quantity} {Symbol} @ {Exit}; netPnl={NetPnl}; balance={Balance}",
            exitReason, position.Direction, position.Quantity, position.Symbol, exitPrice.Value, netPnl, _balance);
        _positionsBySymbol[symbol] = null;
        return true;
    }

    private void ResetDailyStateIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today == _balanceDate) return;
        _balanceDate = today;
        _realizedPnlToday = 0m;
        logger.LogInformation("Paper trading daily risk window reset. Start balance={Balance}", _balance);
    }
}
