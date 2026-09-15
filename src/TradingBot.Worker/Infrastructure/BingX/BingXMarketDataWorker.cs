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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var activeSymbols = await LoadHistoryAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (activeSymbols.Count == 0)
                {
                    logger.LogError("No valid BingX symbols are available; retrying symbol validation in {DelaySeconds}s", _options.ReconnectDelaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds), stoppingToken);
                    activeSymbols = await LoadHistoryAsync(stoppingToken);
                    continue;
                }

                await marketStream.StreamAsync(activeSymbols, async update =>
                {
                    var closedUpdate = _closedCandleTracker.Observe(update);
                    if (closedUpdate is not null)
                        await PublishClosedCandleAsync(closedUpdate, stoppingToken);
                }, stoppingToken);
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
                    activeSymbols = await LoadHistoryAsync(stoppingToken);
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

    private async Task<IReadOnlyList<string>> LoadHistoryAsync(CancellationToken cancellationToken)
    {
        var activeSymbols = new List<string>();
        foreach (var symbol in _options.Symbols)
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
                logger.LogWarning(exception, "Skipping unavailable BingX symbol {Symbol}; other symbols will continue", symbol);
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
        foreach (var timeFrame in new[] { TimeFrame.FifteenMinutes, TimeFrame.OneHour, TimeFrame.FourHours })
        {
            var candles = await marketClient.GetCandlesAsync(symbol, timeFrame, _options.HistoryLimit, cancellationToken);
            foreach (var candle in candles) candleStore.Upsert(symbol, timeFrame, candle);
            loadedHistory[timeFrame] = candles;
            logger.LogInformation("Loaded {Count} closed candles for {Symbol} {TimeFrame}", candles.Count, symbol, timeFrame);
        }

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

            if (timeFrame == TimeFrame.FifteenMinutes)
            {
                if (_lastPublishedOpenTimes.TryGetValue((symbol, timeFrame), out var lastPublished))
                {
                    var missedCandles = candles.Where(candle => candle.OpenTime > lastPublished).ToArray();
                    if (missedCandles.Length > 0)
                    {
                        logger.LogWarning("BingX reconnect missed {MissedCount} closed 15m candles for {Symbol}; replaying them for paper position protection and skipping new entries during reconciliation",
                            missedCandles.Length, symbol);
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

    private async Task PublishClosedCandleAsync(MarketUpdate update, CancellationToken cancellationToken)
    {
        if (!update.Candle.IsClosed) return;
        var key = (update.Symbol, update.TimeFrame);
        if (_lastPublishedOpenTimes.TryGetValue(key, out var lastPublished) &&
            update.Candle.OpenTime <= lastPublished) return;

        candleStore.Upsert(update.Symbol, update.TimeFrame, update.Candle);
        await updates.Writer.WriteAsync(update, cancellationToken);
        _lastPublishedOpenTimes[key] = update.Candle.OpenTime;

        if (update.TimeFrame == TimeFrame.FifteenMinutes)
        {
            logger.LogInformation("Received closed 15m candle for {Symbol}: openTime={OpenTime}, closeTime={CloseTime}, open={Open}, high={High}, low={Low}, close={Close}, volume={Volume}",
                update.Symbol, update.Candle.OpenTime, update.Candle.CloseTime, update.Candle.Open, update.Candle.High,
                update.Candle.Low, update.Candle.Close, update.Candle.Volume);
        }
    }
}
