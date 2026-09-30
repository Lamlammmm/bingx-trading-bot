using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.Live;

namespace TradingBot.Worker.Infrastructure.PaperTrading;

public sealed class TradingWorker(Channel<MarketUpdate> updates, PaperTradingEngine paperTrading,
    LiveTradingEngine liveTrading, ILogger<TradingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await liveTrading.InitializeAsync(stoppingToken);
        await paperTrading.InitializeAsync(stoppingToken);
        await foreach (var update in updates.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await paperTrading.ProcessAsync(update, stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Paper trading processing failed for {Symbol} {TimeFrame} candle at {OpenTime}",
                    update.Symbol, update.TimeFrame, update.Candle.OpenTime);
            }

            try
            {
                await liveTrading.ProcessAsync(update, stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Live trading processing failed for {Symbol} {TimeFrame} candle at {OpenTime}",
                    update.Symbol, update.TimeFrame, update.Candle.OpenTime);
            }
        }
    }
}
