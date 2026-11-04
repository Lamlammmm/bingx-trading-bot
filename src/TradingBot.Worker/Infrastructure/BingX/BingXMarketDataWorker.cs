using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.BingX;

public sealed class BingXMarketDataWorker(IBingXMarketClient marketClient, IBingXMarketStream marketStream,
    ICandleStore candleStore, IContractStore contractStore, Channel<MarketUpdate> updates, IOptions<BingXOptions> options,
    ILogger<BingXMarketDataWorker> logger) : BackgroundService
{
    private readonly BingXOptions _options = options.Value;
    private readonly ClosedCandleTracker _closedCandleTracker = new();
    private readonly Dictionary<(string Symbol, TimeFrame TimeFrame), DateTimeOffset> _lastPublishedOpenTimes = new();
    private readonly HashSet<string> _historyInitializedSymbols = new();
    private readonly SemaphoreSlim _candleStateLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(Math.Max(1, _options.SymbolRetrySeconds));
        var activeSymbols = await LoadHistoryAsync(_options.Symbols, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (activeSymbols.Count == 0)
                {
                    logger.LogError("No BingX symbols have loaded history; retrying in {DelaySeconds}s", retryDelay.TotalSeconds);
                    await Task.Delay(retryDelay, stoppingToken);
                    activeSymbols = await LoadHistoryAsync(_options.Symbols, stoppingToken);
                    continue;
                }

                using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var additionalSymbols = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true
                });
                var streamTask = marketStream.StreamAsync(activeSymbols.ToArray(), additionalSymbols.Reader, async update =>
                {
                    await _candleStateLock.WaitAsync(streamCancellation.Token);
                    try
                    {
                        var closedUpdate = _closedCandleTracker.Observe(update);
                        if (closedUpdate is not null)
                            await PublishClosedCandleAsync(closedUpdate, streamCancellation.Token);
                    }
                    finally
                    {
                        _candleStateLock.Release();
                    }
                }, streamCancellation.Token);
                try
                {
                    while (activeSymbols.Count < _options.Symbols.Count)
                    {
                        var retryTask = Task.Delay(retryDelay, streamCancellation.Token);
                        if (await Task.WhenAny(streamTask, retryTask) == streamTask) break;
                        await retryTask;

                        var recoveredSymbols = await LoadHistoryAsync(_options.Symbols.Except(activeSymbols).ToArray(),
                            streamCancellation.Token);
                        foreach (var symbol in recoveredSymbols)
                        {
                            activeSymbols.Add(symbol);
                            await additionalSymbols.Writer.WriteAsync(symbol, streamCancellation.Token);
                            logger.LogInformation("BingX history recovered for {Symbol}; adding WebSocket subscriptions", symbol);
                        }
                    }
                    await streamTask;
                }
                finally
                {
                    await streamCancellation.CancelAsync();
                    try { await streamTask; }
                    catch (OperationCanceledException) when (streamCancellation.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "BingX market stream failed; retrying in {DelaySeconds}s", _options.ReconnectDelaySeconds);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds), stoppingToken);
                try
                {
                    activeSymbols = await LoadHistoryAsync(_options.Symbols, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "BingX REST reconciliation failed; reconnecting market stream without backfill");
                }
            }
        }
    }

    private async Task<List<string>> LoadHistoryAsync(IEnumerable<string> symbols, CancellationToken cancellationToken)
    {
        var activeSymbols = new List<string>();
        foreach (var symbol in symbols)
        {
            try
            {
                await LoadHistoryForSymbolAsync(symbol, cancellationToken);
                activeSymbols.Add(symbol);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "BingX history load failed for {Symbol}; keeping it pending for retry in {DelaySeconds}s while other symbols continue",
                    symbol, Math.Max(1, _options.SymbolRetrySeconds));
            }
        }
        return activeSymbols;
    }

    private async Task LoadHistoryForSymbolAsync(string symbol, CancellationToken cancellationToken)
    {
        var contract = await marketClient.GetContractAsync(symbol, cancellationToken);
        if (contract is null || !contract.IsActive)
            throw new InvalidOperationException($"BingX contract {symbol} is missing or inactive.");
        logger.LogInformation("BingX contract {Symbol}: minQty={MinimumQuantity}, minValue={MinimumOrderValue} USDT, pricePrecision={PricePrecision}",
            contract.Symbol, contract.MinimumQuantity, contract.MinimumOrderValue, contract.PricePrecision);
        contractStore.Upsert(symbol, contract);

        var loadedHistory = new Dictionary<TimeFrame, IReadOnlyList<Candle>>();
        foreach (var timeFrame in new[] { TimeFrame.FiveMinutes, TimeFrame.ThirtyMinutes, TimeFrame.OneHour })
        {
            var candles = await marketClient.GetCandlesAsync(symbol, timeFrame, _options.HistoryLimit, cancellationToken);
            foreach (var candle in candles) candleStore.Upsert(symbol, timeFrame, candle);
            loadedHistory[timeFrame] = candles;
            logger.LogInformation("Loaded {Count} closed candles for {Symbol} {TimeFrame}", candles.Count, symbol, timeFrame);
        }

        // REST fetches run alongside the stream; serialize only candle tracking and publication.
        await _candleStateLock.WaitAsync(cancellationToken);
        try
        {
            var isInitialLoad = !_historyInitializedSymbols.Contains(symbol);
            _historyInitializedSymbols.Add(symbol);
            foreach (var (timeFrame, candles) in loadedHistory)
            {
                if (candles.Count == 0) continue;
                var latest = candles[^1];
                _closedCandleTracker.DiscardThrough(symbol, timeFrame, latest.OpenTime);

                if (isInitialLoad)
                {
                    _lastPublishedOpenTimes[(symbol, timeFrame)] = latest.OpenTime;
                    continue;
                }

                if (timeFrame == TimeFrame.FiveMinutes)
                {
                    if (_lastPublishedOpenTimes.TryGetValue((symbol, timeFrame), out var lastPublished))
                    {
                        var missedCandles = candles.Where(candle => candle.OpenTime > lastPublished).ToArray();
                        if (missedCandles.Length > 0)
                        {
                            logger.LogWarning("BingX reconnect missed {MissedCount} closed {TimeFrame} candles for {Symbol}; replaying them for paper position protection and skipping new entries during reconciliation",
                                missedCandles.Length, timeFrame, symbol);
                            foreach (var missedCandle in missedCandles)
                            {
                                await PublishClosedCandleAsync(new MarketUpdate(symbol, timeFrame,
                                    missedCandle with { IsClosed = true }, IsReconciliation: true), cancellationToken);
                            }
                        }
                    }
                }
                else if (!_lastPublishedOpenTimes.TryGetValue((symbol, timeFrame), out var lastPublished) || latest.OpenTime > lastPublished)
                {
                    _lastPublishedOpenTimes[(symbol, timeFrame)] = latest.OpenTime;
                }
            }
        }
        finally
        {
            _candleStateLock.Release();
        }
    }

    private async Task PublishClosedCandleAsync(MarketUpdate update, CancellationToken cancellationToken)
    {
        if (!update.Candle.IsClosed) return;
        var key = (update.Symbol, update.TimeFrame);
        if (_lastPublishedOpenTimes.TryGetValue(key, out var lastPublished) &&
            update.Candle.OpenTime <= lastPublished) return;

        candleStore.Upsert(update.Symbol, update.TimeFrame, update.Candle);
        await updates.Writer.WriteAsync(update, cancellationToken);
        _lastPublishedOpenTimes[key] = update.Candle.OpenTime;

        if (update.TimeFrame == TimeFrame.FiveMinutes)
        {
            logger.LogInformation("Received closed 5m candle for {Symbol}: openTime={OpenTime}, closeTime={CloseTime}, open={Open}, high={High}, low={Low}, close={Close}, volume={Volume}",
                update.Symbol, update.Candle.OpenTime, update.Candle.CloseTime, update.Candle.Open, update.Candle.High,
                update.Candle.Low, update.Candle.Close, update.Candle.Volume);
        }
    }
}
