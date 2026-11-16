using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.Live;

public sealed partial class LiveTradingEngine : IDisposable
{
    private readonly ICandleStore _candles;
    private readonly ITradingStrategy _strategy;
    private readonly ILiveExecutor _executor;
    private readonly IContractStore _contracts;
    private readonly LiveOptions _options;
    private readonly ILogger<LiveTradingEngine> _logger;
    private readonly ILiveTradeJournal _journal;
    private readonly StrategyOptions _strategyOptions;
    private readonly RiskOptions _riskOptions;
    private readonly decimal _riskPerTradePercent;
    private readonly RiskManager _riskManager;
    private readonly Dictionary<string, DateTimeOffset> _lastProcessed = new();
    private LiveState _state = new();
    private FileStream? _stateLock;
    private bool _initialized;
    private readonly bool _advanced;
    private readonly RiskOptions _liveRisk;
    private readonly SemaphoreSlim _processing = new(1, 1);
    private bool _exchangeChecked;
    private readonly TradingGovernance? _governance;
    private readonly IBingXMarketClient? _marketClient;

    public bool IsReady => !_options.Enabled || (_initialized && _state.PendingOrder is null &&
        (_state.ApiCircuitUntil is null || DateTimeOffset.UtcNow >= _state.ApiCircuitUntil));

    public sealed record SavedPosition(string Symbol, TradeDirection Direction, decimal Quantity,
        decimal StopLoss, decimal TakeProfit, DateTimeOffset OpenedAt, string? TradeId = null,
        string? RequestedExitReason = null, LivePositionManagement? Management = null);

    public sealed class LiveState
    {
        public int Version { get; set; } = 2;
        public string? PendingOrder { get; set; }
        // Read legacy single-position snapshots, then persist only Positions in version 2.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SavedPosition? Position { get; set; }
        public List<SavedPosition> Positions { get; set; } = new();
        public List<LiveTradeEvent> JournalOutbox { get; set; } = new();
        public DateOnly? RiskDate { get; set; }
        public decimal DayStartBalance { get; set; }
        public int ConsecutiveLosses { get; set; }
        public DateTimeOffset? LossCircuitUntil { get; set; }
        public Dictionary<string, DateTimeOffset> LastExits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int ConsecutiveApiFailures { get; set; }
        public DateTimeOffset? ApiCircuitUntil { get; set; }
        public Dictionary<string, TradingContext> TradeContexts { get; set; } = new();
        public decimal EquityHighWater { get; set; }
        public decimal CashFlowAdjustment { get; set; }
        public decimal LastExternalIncomeToday { get; set; }
    }

    public LiveTradingEngine(ICandleStore candleStore, IOptions<StrategyOptions> strategyOptions,
        IOptions<RiskOptions> riskOptions, IOptions<LiveOptions> liveOptions, ILiveExecutor executor, IContractStore contractStore,
        ILogger<LiveTradingEngine> logger, ILiveTradeJournal journal, TradingGovernance? governance = null, IBingXMarketClient? marketClient = null)
    {
        _candles = candleStore;
        _strategy = strategyOptions.Value.UseMultiTimeframeAnalysis ? new MultiTimeframeStrategy(strategyOptions) : new BreakoutStrategy(strategyOptions);
        _options = liveOptions.Value;
        _executor = executor;
        _contracts = contractStore;
        _logger = logger;
        _journal = journal;
        _strategyOptions = strategyOptions.Value;
        _riskOptions = riskOptions.Value;
        _riskPerTradePercent = _options.RiskPerTradePercent ?? _riskOptions.RiskPerTradePercent;
        _riskManager = new RiskManager(riskOptions);
        _governance = governance; _marketClient = marketClient;
        _advanced = governance is not null || strategyOptions.Value.UseMultiTimeframeAnalysis || riskOptions.Value.UseDynamicPositionManagement;
        _liveRisk = JsonSerializer.Deserialize<RiskOptions>(JsonSerializer.Serialize(_riskOptions))!;
        _liveRisk.RiskPerTradePercent = _riskPerTradePercent;
        _liveRisk.StopAtrMultiplier = _options.StopAtrMultiplier;
        _liveRisk.TakeProfitRiskMultiple = _options.TakeProfitRiskMultiple;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _initialized) return;
        if (_riskPerTradePercent <= 0m || _riskPerTradePercent >= 100m ||
            _options.Leverage < 1m || _options.Leverage != decimal.Truncate(_options.Leverage) ||
            _options.MaxLeverage < 1m || _options.MaxLeverage != decimal.Truncate(_options.MaxLeverage) ||
            _options.MaxOpenPositions < 0 ||
            _options.MaxMarginUsagePercent <= 0m || _options.MaxMarginUsagePercent > 100m ||
            _riskOptions.FeeRate < 0m ||
            _riskOptions.MinimumNetRewardRiskRatio < 0m || _riskOptions.MaximumStopAtrMultiplier <= 0m ||
            _options.StopAtrMultiplier <= 0m || _options.TakeProfitRiskMultiple <= 0m ||
            _options.MaximumConsecutiveApiFailures < 1 || _options.ApiCircuitBreakerMinutes < 1 ||
            _options.MaximumEntryDeviationBasisPoints <= 0m)
            throw new InvalidOperationException("Invalid Live options: requires a nonnegative position limit, positive integer leverage, and valid risk/margin percentages.");

        var path = Path.GetFullPath(_options.StatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stateLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(path))
            _state = JsonSerializer.Deserialize<LiveState>(await File.ReadAllTextAsync(path, cancellationToken))
                ?? throw new InvalidOperationException("Live state is empty.");
        if (_state.Version is not (1 or 2) || _state.Positions is null ||
            (_state.Version == 2 && _state.Position is not null))
            throw new InvalidOperationException("Live state is invalid.");
        if (_state.Version == 1 && _state.Position is { } legacy)
        {
            if (_state.Positions.Count != 0)
                throw new InvalidOperationException("Live state contains conflicting legacy and multi-position data.");
            _state.Positions.Add(legacy);
        }
        if (_state.Positions.Any(p => string.IsNullOrWhiteSpace(p.Symbol) || p.Quantity <= 0m) ||
            _state.Positions.Select(p => p.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _state.Positions.Count)
            throw new InvalidOperationException("Live state contains invalid or duplicate positions.");
        if (_state.LastExits is null || _state.ConsecutiveLosses < 0 || _state.ConsecutiveApiFailures < 0 ||
            _state.Positions.Any(p => p.Management is { } m && (string.IsNullOrWhiteSpace(p.TradeId) ||
                string.IsNullOrWhiteSpace(m.PositionId) || m.EntryPrice <= 0m || m.InitialQuantity < p.Quantity ||
                m.InitialStopLoss <= 0m || m.InitialRiskAmount <= 0m || m.Metrics is null ||
                m.Metrics.TakeProfitStage is < 0 or > 2 || string.IsNullOrWhiteSpace(m.StopOrderId) ||
                string.IsNullOrWhiteSpace(m.TakeOrderId) || m.StopOrderId == m.TakeOrderId || m.FeeRate < 0m || m.FeeRate >= 1m)))
            throw new InvalidOperationException("Live management/risk state is invalid.");
        _state.Version = 2;
        _state.Position = null;
        await _journal.InitializeJournalAsync(cancellationToken);
        if (_governance is not null) await _governance.InitializeAsync(TradingMode.LIVE, cancellationToken);
        await SaveAsync(cancellationToken);
        if (_state.PendingOrder is not null)
            throw new InvalidOperationException($"Unresolved live order {_state.PendingOrder}; reconcile it in BingX before restarting.");

        await _executor.ValidatePositionModeAsync(cancellationToken);
        await ReconcileAsync(cancellationToken);
        _initialized = true;
        _logger.LogWarning("LIVE READY | entry=5m | trends=30m/1h | riskPerTrade={RiskPerTrade}% | leverage={Leverage} | autoLeverage={AutoLeverage} | maxLeverage={MaxLeverage} | restoredPositions={PositionCount} | positionLimit={PositionLimit} | state={Path} | multiTimeframe={MultiTimeframe} | dynamicManagement={DynamicManagement}",
            _riskPerTradePercent, _options.Leverage, _options.AutoLeverage, _options.MaxLeverage, _state.Positions.Count, _options.MaxOpenPositions, path,
            _strategyOptions.UseMultiTimeframeAnalysis, _riskOptions.UseDynamicPositionManagement);
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken, string? symbol = null)
    {
        foreach (var position in _state.Positions.ToArray())
        {
            if (symbol is not null && !string.Equals(position.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) continue;
            var exists = await _executor.HasOpenPositionAsync(position.Symbol, cancellationToken);
            _exchangeChecked = true;
            if (exists)
            {
                if (position.Management is { } managed)
                {
                    var snapshot = await _executor.GetManagedPositionAsync(position.Symbol, position.Direction, position.Quantity,
                        position.StopLoss, position.TakeProfit, managed.StopOrderId, managed.TakeOrderId, cancellationToken);
                    if (snapshot.PositionId != managed.PositionId || snapshot.EntryPrice != managed.EntryPrice)
                        throw new InvalidOperationException("Tracked live position was replaced or its entry changed.");
                }
                else await _executor.ValidateProtectionAsync(position.Symbol, position.Direction, position.Quantity, cancellationToken);
                continue;
            }

            var tradeId = position.TradeId ?? $"legacy-{position.Symbol}-{position.OpenedAt.ToUnixTimeMilliseconds()}";
            _state.JournalOutbox.Add(new LiveTradeEvent(tradeId + ":closed", tradeId, position.Symbol,
                "closed_observed", DateTimeOffset.UtcNow, JsonSerializer.Serialize(new
                {
                    Position = position, Observation = "Exchange reports no open position for the tracked symbol; this timestamp is detection time, not fill time.",
                    RequestedExit = position.RequestedExitReason ?? "EXCHANGE",
                    ActualExitReason = (string?)null, NetPnl = (decimal?)null
                })));
            await SaveAsync(cancellationToken);
            var history = await _executor.GetPositionHistoryAsync(position.Symbol, position.OpenedAt.AddMinutes(-1), cancellationToken);
            _state.JournalOutbox.Add(new LiveTradeEvent(tradeId + ":history", tradeId, position.Symbol,
                "exchange_position_history", DateTimeOffset.UtcNow, history));
            await SaveAsync(cancellationToken);
            // Keep tracking until old protective orders are gone; never cancel unknown orders.
            if (await _executor.GetOpenOrderCountAsync(cancellationToken, position.Symbol) != 0)
            {
                if (position.Management is not { } protection)
                    throw new InvalidOperationException($"Outstanding exchange orders for closed bot position {position.Symbol}; inspect them in BingX before new entries.");
                _state.PendingOrder = "cleanup-" + Guid.NewGuid().ToString("N");
                await SaveAsync(cancellationToken);
                await _executor.CancelKnownProtectionAsync(position.Symbol, [protection.StopOrderId, protection.TakeOrderId], cancellationToken);
                if (await _executor.GetOpenOrderCountAsync(cancellationToken, position.Symbol) != 0)
                    throw new InvalidOperationException("Protective order cleanup is not confirmed.");
                _state.PendingOrder = null;
                await SaveAsync(cancellationToken);
            }
            var balance = await _executor.GetAvailableBalanceUsdtAsync(cancellationToken);
            if (position.Management is { } management)
            {
                using var closedHistory = JsonDocument.Parse(history);
                if (!closedHistory.RootElement.GetProperty("Positions").EnumerateArray().Any(p =>
                    p.TryGetProperty("positionId", out var id) && id.ToString() == management.PositionId))
                    throw new InvalidOperationException("Closed position history is not yet matched to the tracked position ID.");
                var income = await _executor.GetIncomeAsync(position.Symbol, position.OpenedAt, cancellationToken);
                if (!income.HasRealizedPnl || !income.HasTradingFee) throw new InvalidOperationException("Closed position PnL/commission income is not yet available.");
                _state.ConsecutiveLosses = income.NetPnl < 0m ? _state.ConsecutiveLosses + 1 : 0;
                if (_riskOptions.MaximumConsecutiveLosses > 0 && _state.ConsecutiveLosses >= _riskOptions.MaximumConsecutiveLosses)
                    _state.LossCircuitUntil = DateTimeOffset.UtcNow.AddMinutes(_riskOptions.CircuitBreakerMinutes);
                _state.JournalOutbox.Add(new LiveTradeEvent(tradeId + ":income", tradeId, position.Symbol,
                    "closed_income_observed", DateTimeOffset.UtcNow, JsonSerializer.Serialize(new { Income = income, Position = position,
                        Attribution = "Observed USDT income in this exclusively tracked symbol's lifetime; late exchange postings can revise it." })));
                if (management.Metrics.Context is { } context)
                {
                    var outcome = new TradingOutcome(TradingMode.LIVE, tradeId, context.SignalId, context.Strategy, position.Symbol,
                        TimeFrame.FiveMinutes, management.Metrics.Regime, position.Direction, management.InitialQuantity, management.EntryPrice,
                        management.EntryPrice + (position.Direction == TradeDirection.Long ? 1m : -1m) * income.RealizedPnl / management.InitialQuantity,
                        management.InitialStopLoss, position.TakeProfit, context.Leverage, -income.TradingFees, -income.FundingIncome,
                        income.NetPnl, income.NetPnl / management.InitialRiskAmount, management.Metrics.Mfe, management.Metrics.Mae,
                        position.OpenedAt, DateTimeOffset.UtcNow, context.EntryReason, position.RequestedExitReason ?? "EXCHANGE",
                        management.Metrics.RequestedEntryPrice > 0m ? (position.Direction == TradeDirection.Long ? 1m : -1m) *
                            (management.EntryPrice - management.Metrics.RequestedEntryPrice) / management.Metrics.RequestedEntryPrice * 10_000m : null,
                        null, false, "5m mark samples; exit VWAP derived from realized income; slippage=entry fill versus planning mark; latency=request/journal to response",
                        context.EquityAtEntry, management.InitialRiskAmount, management.Metrics.EntryLatencyMilliseconds);
                    var journalEvent = TradingGovernance.OutcomeEvent(outcome);
                    _state.JournalOutbox.Add(new LiveTradeEvent(journalEvent.EventId, tradeId, position.Symbol, "trade_closed",
                        journalEvent.OccurredAt, journalEvent.PayloadJson));
                }
            }
            _logger.LogWarning("LIVE position closed on exchange | symbol={Symbol} | availableUSDT={Balance}", position.Symbol, balance);
            _state.Positions.Remove(position);
            _state.LastExits[position.Symbol] = DateTimeOffset.UtcNow;
            await SaveAsync(cancellationToken);
        }
    }

    private async Task ProcessCoreAsync(MarketUpdate update, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        if (!_initialized) throw new InvalidOperationException("Live initialization has not completed.");
        if (_state.PendingOrder is not null) return;
        if (update.TimeFrame != TimeFrame.FiveMinutes || !update.Candle.IsClosed || update.IsReconciliation) return;
        if (DateTimeOffset.UtcNow - update.Candle.CloseTime > TimeSpan.FromMinutes(2)) return;
        if (update.Candle.CloseTime > DateTimeOffset.UtcNow.AddSeconds(10)) return;
        if (_lastProcessed.TryGetValue(update.Symbol, out var last) && update.Candle.OpenTime <= last) return;
        _lastProcessed[update.Symbol] = update.Candle.OpenTime;

        await ReconcileAsync(cancellationToken, update.Symbol);
        if (_advanced)
        {
            await ProcessAdvancedAsync(update, cancellationToken);
            return;
        }
        if (_state.Positions.Any(p => string.Equals(p.Symbol, update.Symbol, StringComparison.OrdinalIgnoreCase))) return;
        if (_options.MaxOpenPositions > 0 && _state.Positions.Count >= _options.MaxOpenPositions) return;
        // The shared store may already contain newer queued bars. Evaluate and journal only this candle's context.
        var entryCandles = _candles.Get(update.Symbol, TimeFrame.FiveMinutes)
            .Where(candle => candle.IsClosed && candle.CloseTime <= update.Candle.CloseTime).ToArray();
        var mediumTrendCandles = _candles.Get(update.Symbol, TimeFrame.ThirtyMinutes)
            .Where(candle => candle.IsClosed && candle.CloseTime <= update.Candle.CloseTime).ToArray();
        var higherTrendCandles = _candles.Get(update.Symbol, TimeFrame.OneHour)
            .Where(candle => candle.IsClosed && candle.CloseTime <= update.Candle.CloseTime).ToArray();
        var signal = ConfirmMacd(_strategy.Evaluate(update.Symbol, entryCandles, mediumTrendCandles, higherTrendCandles), entryCandles, update.TimeFrame);
        if (signal is null) return;
        var contract = _contracts.Get(update.Symbol);
        if (contract is null || !contract.IsActive || signal.EntryPrice <= 0m || signal.Atr <= 0m) return;
        if (await _executor.HasOpenPositionAsync(update.Symbol, cancellationToken) ||
            await _executor.GetOpenOrderCountAsync(cancellationToken, update.Symbol) != 0) return;
        var balance = await _executor.GetAvailableBalanceUsdtAsync(cancellationToken);
        if (balance <= 0m) return;
        if (contract.QuantityPrecision < 0m || contract.QuantityPrecision > 28m ||
            contract.QuantityPrecision != decimal.Truncate(contract.QuantityPrecision))
            throw new InvalidOperationException("Invalid contract quantity precision.");
        var protection = _riskManager.CreateProtection(signal, contract, _options.StopAtrMultiplier,
            _options.TakeProfitRiskMultiple, out var rejectionReason);
        if (protection is null)
        {
            _logger.LogWarning("LIVE SKIPPED {Symbol} | reason={Reason} | entry={Entry} | atr={Atr} | invalidation={Invalidation} | minimumNetRR={MinimumNetRR}",
                update.Symbol, rejectionReason, signal.EntryPrice, signal.Atr, signal.InvalidationPrice,
                _riskOptions.MinimumNetRewardRiskRatio);
            return;
        }
        var (stop, take, lossPerUnitAtStop, netRewardPerUnit) = protection.Value;
        var distance = Math.Abs(signal.EntryPrice - stop);
        var estimatedCostsPerUnit = (signal.EntryPrice + take) * _riskOptions.FeeRate;

        var marginBudget = balance * _options.MaxMarginUsagePercent / 100m;
        var riskBudget = balance * _riskPerTradePercent / 100m;
        var riskQuantity = riskBudget / lossPerUnitAtStop;
        var leverage = _options.AutoLeverage
            ? Math.Max(1m, decimal.Ceiling(riskQuantity * signal.EntryPrice / marginBudget))
            : _options.Leverage;
        var exchangeMaximum = await _executor.GetMaxLeverageAsync(update.Symbol, signal.Direction, cancellationToken);
        var maximum = Math.Min(_options.MaxLeverage, exchangeMaximum);
        if (_options.AutoLeverage)
            leverage = Math.Min(leverage, maximum);
        else if (leverage > maximum)
        {
            _logger.LogWarning("LIVE SKIPPED {Symbol} | configuredLeverage={Required} exceeds maxLeverage={Maximum}",
                update.Symbol, leverage, maximum);
            return;
        }
        // Reserve estimated entry/exit fees in the margin allocation.
        var maximumQuantityForMargin = marginBudget / (signal.EntryPrice / leverage + signal.EntryPrice * _riskOptions.FeeRate * 2m);
        var quantity = Math.Round(Math.Min(riskQuantity, maximumQuantityForMargin),
            (int)contract.QuantityPrecision, MidpointRounding.ToZero);
        if (quantity <= 0m || quantity < contract.MinimumQuantity || quantity * signal.EntryPrice < contract.MinimumOrderValue)
        {
            _logger.LogWarning("LIVE SKIPPED {Symbol} | balance={Balance} | quantity={Quantity} | minQty={MinQty} | minValue={MinValue} | riskBudget={RiskBudget} | marginBudget={MarginBudget} | entry={Entry} | stopDistance={StopDistance} | leverage={Leverage} | riskQuantity={RiskQuantity} | marginQuantity={MarginQuantity} | limitedBy={LimitedBy}",
                update.Symbol, balance, quantity, contract.MinimumQuantity, contract.MinimumOrderValue,
                riskBudget, marginBudget, signal.EntryPrice, distance, leverage, riskQuantity, maximumQuantityForMargin,
                riskQuantity <= maximumQuantityForMargin ? "risk" : "margin");
            return;
        }
        await _executor.ValidatePositionModeAsync(cancellationToken);
        _logger.LogInformation("LIVE SIZING {Symbol} | riskBudget={RiskBudget} | stopDistance={StopDistance} | leverage={Leverage} | quantity={Quantity} | notional={Notional} | availableUSDT={Balance} | marginBudget={MarginBudget} | botPositions={PositionCount} | netRewardRisk={NetRewardRisk}",
            update.Symbol, riskBudget, distance, leverage, quantity, quantity * signal.EntryPrice, balance, marginBudget, _state.Positions.Count,
            netRewardPerUnit / lossPerUnitAtStop);
        if (await _executor.HasOpenPositionAsync(update.Symbol, cancellationToken) ||
            await _executor.GetOpenOrderCountAsync(cancellationToken, update.Symbol) != 0) return;
        await _executor.SetLeverageAsync(update.Symbol, signal.Direction, leverage, cancellationToken);
        _state.PendingOrder = "bot-" + Guid.NewGuid().ToString("N");
        var entryId = _state.PendingOrder;
        _state.JournalOutbox.Add(new LiveTradeEvent(entryId + ":requested", entryId, update.Symbol,
            "entry_requested", DateTimeOffset.UtcNow, JsonSerializer.Serialize(new
            {
                SchemaVersion = 2, EntryTimeFrame = "5m", MediumTrendTimeFrame = "30m", HigherTrendTimeFrame = "1h",
                Signal = signal, Quantity = quantity, Leverage = leverage,
                AvailableBalance = balance, MarginBudget = marginBudget, EstimatedCostsPerUnit = estimatedCostsPerUnit, RiskBudget = riskBudget, StopDistance = distance,
                EstimatedLossAtStop = quantity * lossPerUnitAtStop,
                NetRewardRiskRatio = netRewardPerUnit / lossPerUnitAtStop,
                StopLoss = stop, TakeProfit = take,
                Contract = contract, Strategy = _strategyOptions, Live = _options,
                EntryCandles = entryCandles,
                MediumTrendCandles = mediumTrendCandles,
                HigherTrendCandles = higherTrendCandles
            })));
        await SaveAsync(cancellationToken);
        var entryPrice = await _executor.OpenMarketPositionAsync(update.Symbol, signal.Direction, quantity, stop, take,
            _state.PendingOrder, cancellationToken);
        var opened = new SavedPosition(update.Symbol, signal.Direction, quantity, stop, take, DateTimeOffset.UtcNow, entryId);
        _state.Positions.Add(opened);
        _state.JournalOutbox.Add(new LiveTradeEvent(entryId + ":response", entryId, update.Symbol,
            "entry_response", DateTimeOffset.UtcNow, JsonSerializer.Serialize(new
            { Position = opened, Receipt = entryPrice })));
        await SaveAsync(cancellationToken);
        await _executor.ValidateProtectionAsync(update.Symbol, signal.Direction, quantity, cancellationToken);
        _state.JournalOutbox.Add(new LiveTradeEvent(entryId + ":protected", entryId, update.Symbol,
            "entry_protected", DateTimeOffset.UtcNow, JsonSerializer.Serialize(opened)));
        _state.PendingOrder = null;
        await SaveAsync(cancellationToken);
        _logger.LogWarning("LIVE OPEN {Direction} {Symbol} | quantity={Quantity} | exchange SL={Stop} TP={Take}",
            signal.Direction, update.Symbol, quantity, stop, take);
    }

    private StrategySignal? ConfirmMacd(StrategySignal? signal, IReadOnlyList<Candle> entryCandles, TimeFrame timeFrame)
    {
        if (signal is null || !_options.Macd.Enabled) return signal;
        var macd = MacdEntryFilter.Evaluate(entryCandles, signal, timeFrame, _options.Macd);
        MacdEntryFilter.Log(_logger, "LIVE", signal, macd);
        return macd.Allowed ? signal with { Macd = macd } : null;
    }

    private async Task SaveAsync(CancellationToken cancellationToken, bool flushJournal = true)
    {
        var path = Path.GetFullPath(_options.StatePath);
        try
        {
            // Flush and atomically replace to retain the last complete snapshot on interruption.
            await using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, _state, cancellationToken: cancellationToken);
                file.Flush(true);
            }
            File.Move(path + ".tmp", path, true);
            if (!flushJournal)
            {
                _logger.LogCritical("Emergency protection persisted in local outbox; new entries remain blocked until exchange/database reconciliation");
                return;
            }
            if (_state.JournalOutbox.Count > 0)
            {
                foreach (var entry in _state.JournalOutbox)
                {
                    await _journal.RecordLiveEventAsync(entry, cancellationToken);
                    if (_governance is not null)
                        await _governance.MirrorLiveAsync(entry, _state.TradeContexts.GetValueOrDefault(entry.TradeId), cancellationToken);
                }
                _state.JournalOutbox.Clear();
                foreach (var id in _state.TradeContexts.Keys.ToArray())
                    if (id != _state.PendingOrder && !_state.Positions.Any(p => p.TradeId == id)) _state.TradeContexts.Remove(id);
                await SaveAsync(cancellationToken);
            }
        }
        catch
        {
            _initialized = false;
            throw;
        }
    }

    public void Dispose() => _stateLock?.Dispose();
}
