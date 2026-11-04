using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Worker.Application;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.Live;

namespace TradingBot.Worker.Infrastructure.PaperTrading;

public sealed class TradingWorker(Channel<MarketUpdate> updates, PaperTradingEngine paperTrading,
    LiveTradingEngine liveTrading, BotRuntimeState state, ILogger<TradingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await liveTrading.InitializeAsync(stoppingToken);
        await paperTrading.InitializeAsync(stoppingToken);
        state.Snapshot = new BotSnapshot(Initialized: true);
        await foreach (var update in updates.Reader.ReadAllAsync(stoppingToken))
        {
            var failed = false;
            // Live protection and entries must not wait for optional paper AI analysis.
            try
            {
                await liveTrading.ProcessAsync(update, stoppingToken);
                failed = !liveTrading.IsReady;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                failed = true;
                logger.LogError(exception, "Live trading processing failed for {Symbol} {TimeFrame} candle at {OpenTime}",
                    update.Symbol, update.TimeFrame, update.Candle.OpenTime);
            }

            try
            {
                await paperTrading.ProcessAsync(update, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                failed = true;
                logger.LogError(exception, "Paper trading processing failed for {Symbol} {TimeFrame} candle at {OpenTime}",
                    update.Symbol, update.TimeFrame, update.Candle.OpenTime);
            }
            state.Snapshot = state.Snapshot with
            {
                ProcessedUpdates = state.Snapshot.ProcessedUpdates + 1,
                LastProcessedAt = DateTimeOffset.UtcNow,
                LastCandleCloseTime = update.Candle.CloseTime,
                LastSymbol = update.Symbol,
                HasProcessingError = failed ||
                    (update.TimeFrame != TimeFrame.FiveMinutes && state.Snapshot.HasProcessingError)
            };
        }
    }
}
