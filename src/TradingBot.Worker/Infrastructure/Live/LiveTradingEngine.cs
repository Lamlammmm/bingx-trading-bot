using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.Live;

public sealed class LiveTradingEngine : IDisposable
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
    private readonly Dictionary<string, DateTimeOffset> _lastProcessed = new();
    private LiveState _state = new();
    private FileStream? _stateLock;
    private bool _initialized;

    public bool IsReady => !_options.Enabled || (_initialized && _state.PendingOrder is null);

    public sealed record SavedPosition(string Symbol, TradeDirection Direction, decimal Quantity,
        decimal StopLoss, decimal TakeProfit, DateTimeOffset OpenedAt, string? TradeId = null,
        string? RequestedExitReason = null);

    public sealed class LiveState
    {
        public int Version { get; set; } = 2;
        public string? PendingOrder { get; set; }
        // Read legacy single-position snapshots, then persist only Positions in version 2.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SavedPosition? Position { get; set; }
        public List<SavedPosition> Positions { get; set; } = new();
        public List<LiveTradeEvent> JournalOutbox { get; set; } = new();
    }

    public LiveTradingEngine(ICandleStore candleStore, IOptions<StrategyOptions> strategyOptions,
        IOptions<RiskOptions> riskOptions, IOptions<LiveOptions> liveOptions, ILiveExecutor executor, IContractStore contractStore,
        ILogger<LiveTradingEngine> logger, ILiveTradeJournal journal)
    {
        _candles = candleStore;
        _strategy = new BreakoutStrategy(strategyOptions);
        _options = liveOptions.Value;
        _executor = executor;
        _contracts = contractStore;
        _logger = logger;
        _journal = journal;
        _strategyOptions = strategyOptions.Value;
        _riskOptions = riskOptions.Value;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _initialized) return;
        if (_riskOptions.RiskPerTradePercent <= 0m || _riskOptions.RiskPerTradePercent >= 100m ||
            _options.Leverage < 1m || _options.Leverage != decimal.Truncate(_options.Leverage) ||
            _options.MaxLeverage < 1m || _options.MaxLeverage != decimal.Truncate(_options.MaxLeverage) ||
            _options.MaxOpenPositions < 0 ||
            _options.MaxMarginUsagePercent <= 0m || _options.MaxMarginUsagePercent > 100m ||
            _riskOptions.FeeRate < 0m ||
            _options.StopAtrMultiplier <= 0m || _options.TakeProfitRiskMultiple <= 0m)
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
        _state.Version = 2;
        _state.Position = null;
        await _journal.InitializeJournalAsync(cancellationToken);
        await SaveAsync(cancellationToken);
        if (_state.PendingOrder is not null)
            throw new InvalidOperationException($"Unresolved live order {_state.PendingOrder}; reconcile it in BingX before restarting.");

        await _executor.ValidatePositionModeAsync(cancellationToken);
        await ReconcileAsync(cancellationToken);
        _initialized = true;
        _logger.LogWarning("LIVE READY | entry=5m | trends=30m/1h | exits=SL/TP only | riskPerTrade={RiskPerTrade}% | leverage={Leverage} | autoLeverage={AutoLeverage} | maxLeverage={MaxLeverage} | restoredPositions={PositionCount} | positionLimit={PositionLimit} | state={Path}",
            _riskOptions.RiskPerTradePercent, _options.Leverage, _options.AutoLeverage, _options.MaxLeverage, _state.Positions.Count, _options.MaxOpenPositions, path);
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken, string? symbol = null)
    {
        foreach (var position in _state.Positions.ToArray())
        {
            if (symbol is not null && !string.Equals(position.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (await _executor.HasOpenPositionAsync(position.Symbol, cancellationToken))
            {
                await _executor.ValidateProtectionAsync(position.Symbol, position.Direction, position.Quantity, cancellationToken);
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
                throw new InvalidOperationException($"Outstanding exchange orders for closed bot position {position.Symbol}; inspect them in BingX before new entries.");
            var balance = await _executor.GetAvailableBalanceUsdtAsync(cancellationToken);
            _logger.LogWarning("LIVE position closed on exchange | symbol={Symbol} | availableUSDT={Balance}", position.Symbol, balance);
            _state.Positions.Remove(position);
            await SaveAsync(cancellationToken);
        }
    }

    public async Task ProcessAsync(MarketUpdate update, CancellationToken cancellationToken)
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
        if (_state.Positions.Any(p => string.Equals(p.Symbol, update.Symbol, StringComparison.OrdinalIgnoreCase))) return;
        if (_options.MaxOpenPositions > 0 && _state.Positions.Count >= _options.MaxOpenPositions) return;
        // The shared store may already contain newer queued bars. Evaluate and journal only this candle's context.
        var entryCandles = _candles.Get(update.Symbol, TimeFrame.FiveMinutes)
            .Where(candle => candle.IsClosed && candle.CloseTime <= update.Candle.CloseTime).ToArray();
        var mediumTrendCandles = _candles.Get(update.Symbol, TimeFrame.ThirtyMinutes)
            .Where(candle => candle.IsClosed && candle.CloseTime <= update.Candle.CloseTime).ToArray();
        var higherTrendCandles = _candles.Get(update.Symbol, TimeFrame.OneHour)
            .Where(candle => candle.IsClosed && candle.CloseTime <= update.Candle.CloseTime).ToArray();
        var signal = _strategy.Evaluate(update.Symbol, entryCandles, mediumTrendCandles, higherTrendCandles);
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
        var distance = signal.Atr * _options.StopAtrMultiplier;
        var sign = signal.Direction == TradeDirection.Long ? 1m : -1m;
        var stop = Math.Round(signal.EntryPrice - sign * distance, (int)contract.PricePrecision);
        var take = Math.Round(signal.EntryPrice + sign * distance * _options.TakeProfitRiskMultiple, (int)contract.PricePrecision);
        distance = Math.Abs(signal.EntryPrice - stop);
        if (stop <= 0m || take <= 0m || distance <= 0m || sign * (take - signal.EntryPrice) <= 0m) return;
        var estimatedCostsPerUnit = (signal.EntryPrice + take) * _riskOptions.FeeRate;
        if (Math.Abs(take - signal.EntryPrice) <= estimatedCostsPerUnit) return;

        var marginBudget = balance * _options.MaxMarginUsagePercent / 100m;
        var riskBudget = balance * _riskOptions.RiskPerTradePercent / 100m;
        var lossPerUnitAtStop = distance + (signal.EntryPrice + stop) * _riskOptions.FeeRate;
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
            _logger.LogWarning("LIVE SKIPPED {Symbol} | balance={Balance} | quantity={Quantity} | minQty={MinQty} | minValue={MinValue}",
                update.Symbol, balance, quantity, contract.MinimumQuantity, contract.MinimumOrderValue);
            return;
        }
        await _executor.ValidatePositionModeAsync(cancellationToken);
        _logger.LogInformation("LIVE SIZING {Symbol} | riskBudget={RiskBudget} | stopDistance={StopDistance} | leverage={Leverage} | quantity={Quantity} | notional={Notional} | availableUSDT={Balance} | marginBudget={MarginBudget} | botPositions={PositionCount}",
            update.Symbol, riskBudget, distance, leverage, quantity, quantity * signal.EntryPrice, balance, marginBudget, _state.Positions.Count);
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

    private async Task SaveAsync(CancellationToken cancellationToken)
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
            if (_state.JournalOutbox.Count > 0)
            {
                foreach (var entry in _state.JournalOutbox)
                    await _journal.RecordLiveEventAsync(entry, cancellationToken);
                _state.JournalOutbox.Clear();
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
