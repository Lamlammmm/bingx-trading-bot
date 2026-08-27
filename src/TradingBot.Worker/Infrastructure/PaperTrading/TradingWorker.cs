using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.PaperTrading;

public sealed class TradingWorker(Channel<MarketUpdate> updates, PaperTradingEngine paperTrading,
    ILogger<TradingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        paperTrading.Initialize();
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
        }
    }
}
