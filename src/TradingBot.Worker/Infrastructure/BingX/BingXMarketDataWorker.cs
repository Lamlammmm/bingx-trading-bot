using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.BingX;

public sealed class BingXMarketDataWorker(IBingXMarketClient marketClient, IBingXMarketStream marketStream,
    ICandleStore candleStore, Channel<MarketUpdate> updates, IOptions<BingXOptions> options,
    ILogger<BingXMarketDataWorker> logger) : BackgroundService
{
    private readonly BingXOptions _options = options.Value;
    private readonly ClosedCandleTracker _closedCandleTracker = new();
    private readonly Dictionary<TimeFrame, DateTimeOffset> _lastPublishedOpenTimes = new();
    private bool _historyInitialized;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadHistoryAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await marketStream.StreamAsync(async update =>
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
                    await LoadHistoryAsync(stoppingToken);
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

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        var contract = await marketClient.GetContractAsync(cancellationToken);
        if (contract is null || !contract.IsActive)
            throw new InvalidOperationException($"BingX contract {_options.Symbol} is missing or inactive.");
        logger.LogInformation("BingX contract {Symbol}: minQty={MinimumQuantity}, minValue={MinimumOrderValue} USDT, pricePrecision={PricePrecision}",
            contract.Symbol, contract.MinimumQuantity, contract.MinimumOrderValue, contract.PricePrecision);

        var loadedHistory = new Dictionary<TimeFrame, IReadOnlyList<Candle>>();
        foreach (var timeFrame in new[] { TimeFrame.FifteenMinutes, TimeFrame.OneHour, TimeFrame.FourHours })
        {
            var candles = await marketClient.GetCandlesAsync(timeFrame, _options.HistoryLimit, cancellationToken);
            foreach (var candle in candles) candleStore.Upsert(timeFrame, candle);
            loadedHistory[timeFrame] = candles;
            logger.LogInformation("Loaded {Count} closed candles for {TimeFrame}", candles.Count, timeFrame);
        }

        var isInitialLoad = !_historyInitialized;
        _historyInitialized = true;
        foreach (var (timeFrame, candles) in loadedHistory)
        {
            if (candles.Count == 0) continue;
            var latest = candles[^1];
            _closedCandleTracker.DiscardThrough(timeFrame, latest.OpenTime);

            if (isInitialLoad)
            {
                _lastPublishedOpenTimes[timeFrame] = latest.OpenTime;
                continue;
            }

            if (timeFrame == TimeFrame.FifteenMinutes)
            {
                // Reconcile only the newest missed 15m candle. Replaying multiple candles through a
                // shared store would evaluate old updates against future 1h/4h context.
                if (_lastPublishedOpenTimes.TryGetValue(timeFrame, out var lastPublished))
                {
                    var missedCount = candles.Count(candle => candle.OpenTime > lastPublished);
                    if (missedCount > 1)
                    {
                        logger.LogWarning("BingX reconnect missed {MissedCount} closed 15m candles; reconciling only the latest and skipping {SkippedCount} intermediate candles",
                            missedCount, missedCount - 1);
                    }
                }
                await PublishClosedCandleAsync(new MarketUpdate(timeFrame, latest with { IsClosed = true }), cancellationToken);
            }
            else if (!_lastPublishedOpenTimes.TryGetValue(timeFrame, out var lastPublished) || latest.OpenTime > lastPublished)
            {
                _lastPublishedOpenTimes[timeFrame] = latest.OpenTime;
            }
        }
    }

    private async Task PublishClosedCandleAsync(MarketUpdate update, CancellationToken cancellationToken)
    {
        if (!update.Candle.IsClosed) return;
        if (_lastPublishedOpenTimes.TryGetValue(update.TimeFrame, out var lastPublished) &&
            update.Candle.OpenTime <= lastPublished) return;

        candleStore.Upsert(update.TimeFrame, update.Candle);
        await updates.Writer.WriteAsync(update, cancellationToken);
        _lastPublishedOpenTimes[update.TimeFrame] = update.Candle.OpenTime;

        if (update.TimeFrame == TimeFrame.FifteenMinutes)
        {
            logger.LogInformation("Received closed 15m candle for {Symbol}: openTime={OpenTime}, closeTime={CloseTime}, close={Close}, volume={Volume}",
                _options.Symbol, update.Candle.OpenTime, update.Candle.CloseTime, update.Candle.Close, update.Candle.Volume);
        }
    }
}
