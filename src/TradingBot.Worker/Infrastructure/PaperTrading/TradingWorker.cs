using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.PaperTrading;

public sealed class TradingWorker(Channel<MarketUpdate> updates, PaperTradingEngine paperTrading,
    IOptions<BingXOptions> options, ILogger<TradingWorker> logger) : BackgroundService
{
    private readonly BingXOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        paperTrading.Initialize();
        await foreach (var update in updates.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await paperTrading.ProcessAsync(update, _options.Symbol, stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Paper trading processing failed for {TimeFrame} candle at {OpenTime}",
                    update.TimeFrame, update.Candle.OpenTime);
            }
        }
    }
}
