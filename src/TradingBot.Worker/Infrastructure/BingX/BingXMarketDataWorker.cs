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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadHistoryAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await marketStream.StreamAsync(async update =>
                {
                    if (!update.Candle.IsClosed) return;
                    candleStore.Upsert(update.TimeFrame, update.Candle);
                    if (update.TimeFrame == TimeFrame.FifteenMinutes)
                    {
                        logger.LogInformation("Received closed 15m candle for {Symbol}: openTime={OpenTime}, closeTime={CloseTime}, close={Close}, volume={Volume}",
                            _options.Symbol, update.Candle.OpenTime, update.Candle.CloseTime, update.Candle.Close, update.Candle.Volume);
                    }
                    await updates.Writer.WriteAsync(update, stoppingToken);
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
                await LoadHistoryAsync(stoppingToken);
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

        foreach (var timeFrame in new[] { TimeFrame.FifteenMinutes, TimeFrame.OneHour, TimeFrame.FourHours })
        {
            var candles = await marketClient.GetCandlesAsync(timeFrame, _options.HistoryLimit, cancellationToken);
            foreach (var candle in candles) candleStore.Upsert(timeFrame, candle);
            logger.LogInformation("Loaded {Count} closed candles for {TimeFrame}", candles.Count, timeFrame);
        }
    }
}
