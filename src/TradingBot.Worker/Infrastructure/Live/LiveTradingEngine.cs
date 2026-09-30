using System.Text.Json;
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
    private bool _externalPositionDetected;

    public sealed record SavedPosition(string Symbol, TradeDirection Direction, decimal Quantity,
        decimal StopLoss, decimal TakeProfit, DateTimeOffset OpenedAt, string? TradeId = null,
        string? RequestedExitReason = null);

    public sealed class LiveState
    {
        public int Version { get; set; } = 1;
        public string? PendingOrder { get; set; }
        public SavedPosition? Position { get; set; }
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
            _options.MaxOpenPositions != 1 ||
            _options.StopAtrMultiplier <= 0m || _options.TakeProfitRiskMultiple <= 0m || _options.MaxHoldingMinutes < 0)
            throw new InvalidOperationException("Invalid Live options: requires one position, positive integer leverage, and percentages between 0 and 100.");

        var path = Path.GetFullPath(_options.StatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stateLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(path))
            _state = JsonSerializer.Deserialize<LiveState>(await File.ReadAllTextAsync(path, cancellationToken))
                ?? throw new InvalidOperationException("Live state is empty.");
        if (_state.Version != 1)
            throw new InvalidOperationException("Live state is invalid.");
        await _journal.InitializeJournalAsync(cancellationToken);
        await SaveAsync(cancellationToken);
        if (_state.PendingOrder is not null)
            throw new InvalidOperationException($"Unresolved live order {_state.PendingOrder}; reconcile it in BingX before restarting.");

        await _executor.ValidatePositionModeAsync(cancellationToken);
        await ReconcileAsync(cancellationToken);
        _initialized = true;
        _logger.LogWarning("LIVE READY | riskPerTrade={RiskPerTrade}% | leverage={Leverage} | autoLeverage={AutoLeverage} | maxLeverage={MaxLeverage} | restoredPosition={Symbol} | state={Path}",
            _riskOptions.RiskPerTradePercent, _options.Leverage, _options.AutoLeverage, _options.MaxLeverage, _state.Position?.Symbol, path);
    }

    private async Task<bool> ReconcileAsync(CancellationToken cancellationToken)
    {
        var count = await _executor.GetOpenPositionCountAsync(cancellationToken);
        if (count > 0)
        {
            if (count != 1 || _state.Position is not { } position)
            {
                if (!_externalPositionDetected)
                    _logger.LogWarning("LIVE PAUSED | detected {PositionCount} exchange position(s) not fully tracked by the bot; manual positions are left untouched and new bot entries are blocked",
                        count);
                _externalPositionDetected = true;
                return true;
            }
            await _executor.ValidateProtectionAsync(position.Symbol, position.Direction, position.Quantity, cancellationToken);
            _externalPositionDetected = false;
            return false;
        }

        if (_externalPositionDetected)
        {
            _externalPositionDetected = false;
            _logger.LogWarning("LIVE RESUMED | no external exchange positions remain");
        }

        if (_state.Position is { } closed)
        {
            var tradeId = closed.TradeId ?? $"legacy-{closed.Symbol}-{closed.OpenedAt.ToUnixTimeMilliseconds()}";
            _state.JournalOutbox.Add(new LiveTradeEvent(tradeId + ":closed", tradeId, closed.Symbol,
                "closed_observed", DateTimeOffset.UtcNow, JsonSerializer.Serialize(new
                {
                    Position = closed, Observation = "Exchange reports no open positions; this timestamp is detection time, not fill time.",
                    RequestedExit = closed.RequestedExitReason ?? "EXCHANGE",
                    ActualExitReason = (string?)null, NetPnl = (decimal?)null
                })));
            await SaveAsync(cancellationToken);
            var history = await _executor.GetPositionHistoryAsync(closed.Symbol, closed.OpenedAt.AddMinutes(-1), cancellationToken);
            _state.JournalOutbox.Add(new LiveTradeEvent(tradeId + ":history", tradeId, closed.Symbol,
                "exchange_position_history", DateTimeOffset.UtcNow, history));
            await SaveAsync(cancellationToken);
        }
        // Never cancel unknown orders or let old protective orders affect a new position.
        if (await _executor.GetOpenOrderCountAsync(cancellationToken) != 0)
            throw new InvalidOperationException("Outstanding exchange orders while flat: inspect them in BingX before new entries.");
        if (_state.Position is not null)
        {
            var balance = await _executor.GetAvailableBalanceUsdtAsync(cancellationToken);
            _logger.LogWarning("LIVE position closed on exchange | symbol={Symbol} | availableUSDT={Balance}", _state.Position.Symbol, balance);
        }
        _state.Position = null;
        await SaveAsync(cancellationToken);
        return false;
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

        if (await ReconcileAsync(cancellationToken)) return;
        if (_state.Position is { } held)
        {
            if (held.Symbol != update.Symbol || _options.MaxHoldingMinutes == 0 ||
                DateTimeOffset.UtcNow < held.OpenedAt.AddMinutes(_options.MaxHoldingMinutes)) return;
            _state.PendingOrder = "close-" + Guid.NewGuid().ToString("N");
            _state.Position = held with { RequestedExitReason = "TIME_EXIT" };
            var tradeId = held.TradeId ?? $"legacy-{held.Symbol}-{held.OpenedAt.ToUnixTimeMilliseconds()}";
            _state.JournalOutbox.Add(new LiveTradeEvent(_state.PendingOrder, tradeId, held.Symbol,
                "close_requested", DateTimeOffset.UtcNow, JsonSerializer.Serialize(new { Position = held, Reason = "TIME_EXIT" })));
            await SaveAsync(cancellationToken);
            var closePrice = await _executor.CloseMarketPositionAsync(held.Symbol, held.Direction, held.Quantity, cancellationToken);
            _state.JournalOutbox.Add(new LiveTradeEvent(_state.PendingOrder + ":response", tradeId, held.Symbol,
                "close_response", DateTimeOffset.UtcNow, JsonSerializer.Serialize(closePrice)));
            await SaveAsync(cancellationToken);
            if (await _executor.HasOpenPositionAsync(held.Symbol, cancellationToken))
                throw new InvalidOperationException("Close not confirmed; check live position in BingX.");
            _state.PendingOrder = null;
            await SaveAsync(cancellationToken);
            await ReconcileAsync(cancellationToken);
            return;
        }
        var signal = _strategy.Evaluate(update.Symbol, _candles.Get(update.Symbol, TimeFrame.FiveMinutes),
            _candles.Get(update.Symbol, TimeFrame.OneHour), _candles.Get(update.Symbol, TimeFrame.FourHours));
        if (signal is null) return;
        var contract = _contracts.Get(update.Symbol);
        if (contract is null || !contract.IsActive || signal.EntryPrice <= 0m || signal.Atr <= 0m) return;
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

        var riskBudget = balance * _riskOptions.RiskPerTradePercent / 100m;
        var riskQuantity = riskBudget / distance;
        var leverage = _options.AutoLeverage
            ? Math.Max(1m, decimal.Ceiling(riskQuantity * signal.EntryPrice / balance))
            : _options.Leverage;
        var exchangeMaximum = await _executor.GetMaxLeverageAsync(update.Symbol, signal.Direction, cancellationToken);
        var maximum = Math.Min(_options.MaxLeverage, exchangeMaximum);
        if (leverage > maximum)
        {
            _logger.LogWarning("LIVE SKIPPED {Symbol} | requiredLeverage={Required} exceeds maxLeverage={Maximum} | availableUSDT={Balance}",
                update.Symbol, leverage, maximum, balance);
            return;
        }
        var maximumQuantityForMargin = balance * leverage / signal.EntryPrice;
        var quantity = Math.Round(Math.Min(riskQuantity, maximumQuantityForMargin),
            (int)contract.QuantityPrecision, MidpointRounding.ToZero);
        if (quantity <= 0m || quantity < contract.MinimumQuantity || quantity * signal.EntryPrice < contract.MinimumOrderValue)
        {
            _logger.LogWarning("LIVE SKIPPED {Symbol} | balance={Balance} | quantity={Quantity} | minQty={MinQty} | minValue={MinValue}",
                update.Symbol, balance, quantity, contract.MinimumQuantity, contract.MinimumOrderValue);
            return;
        }
        await _executor.ValidatePositionModeAsync(cancellationToken);
        _logger.LogInformation("LIVE SIZING {Symbol} | riskBudget={RiskBudget} | stopDistance={StopDistance} | leverage={Leverage} | quantity={Quantity} | notional={Notional}",
            update.Symbol, riskBudget, distance, leverage, quantity, quantity * signal.EntryPrice);
        await _executor.SetLeverageAsync(update.Symbol, signal.Direction, leverage, cancellationToken);
        if (await _executor.GetOpenPositionCountAsync(cancellationToken) != 0 ||
            await _executor.GetOpenOrderCountAsync(cancellationToken) != 0) return;
        _state.PendingOrder = "bot-" + Guid.NewGuid().ToString("N");
        var entryId = _state.PendingOrder;
        _state.JournalOutbox.Add(new LiveTradeEvent(entryId + ":requested", entryId, update.Symbol,
            "entry_requested", DateTimeOffset.UtcNow, JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, Signal = signal, Quantity = quantity, Leverage = leverage,
                AvailableBalance = balance, RiskBudget = riskBudget, StopDistance = distance,
                StopLoss = stop, TakeProfit = take,
                Contract = contract, Strategy = _strategyOptions, Live = _options,
                EntryCandles = _candles.Get(update.Symbol, TimeFrame.FiveMinutes),
                HourCandles = _candles.Get(update.Symbol, TimeFrame.OneHour),
                FourHourCandles = _candles.Get(update.Symbol, TimeFrame.FourHours)
            })));
        await SaveAsync(cancellationToken);
        var entryPrice = await _executor.OpenMarketPositionAsync(update.Symbol, signal.Direction, quantity, stop, take,
            _state.PendingOrder, cancellationToken);
        _state.Position = new SavedPosition(update.Symbol, signal.Direction, quantity, stop, take, DateTimeOffset.UtcNow, entryId);
        _state.JournalOutbox.Add(new LiveTradeEvent(entryId + ":response", entryId, update.Symbol,
            "entry_response", DateTimeOffset.UtcNow, JsonSerializer.Serialize(new
            { Position = _state.Position, Receipt = entryPrice })));
        await SaveAsync(cancellationToken);
        await _executor.ValidateProtectionAsync(update.Symbol, signal.Direction, quantity, cancellationToken);
        _state.JournalOutbox.Add(new LiveTradeEvent(entryId + ":protected", entryId, update.Symbol,
            "entry_protected", DateTimeOffset.UtcNow, JsonSerializer.Serialize(_state.Position)));
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
