using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure;

public sealed class TradingAnalyticsWorker(TradingGovernance governance, ILiveExecutor exchange,
    IOptions<AdaptiveOptions> options, IOptions<LiveOptions> live, ILogger<TradingAnalyticsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Trading analytics failed; trading entries still require a healthy journal and approved policy"); }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.Value.AnalysisIntervalMinutes)), stoppingToken);
        }
    }

    public async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        foreach (var mode in new[] { TradingMode.PAPER, TradingMode.LIVE })
        {
            await governance.InitializeAsync(mode, stoppingToken);
            if (mode == TradingMode.LIVE && live.Value.Enabled)
            {
                var rows = await governance.OutcomesAsync(mode, DateTimeOffset.UtcNow.AddDays(-options.Value.LookbackDays), stoppingToken);
                foreach (var row in rows.Where(r => DateTimeOffset.UtcNow - r.ClosedAt >= TimeSpan.FromMinutes(5) &&
                    (!r.Reconciled || DateTimeOffset.UtcNow - r.ClosedAt < TimeSpan.FromHours(24))))
                {
                    var income = await exchange.GetIncomeAsync(row.Symbol, row.OpenedAt, row.ClosedAt, stoppingToken);
                    if (!income.HasRealizedPnl || !income.HasTradingFee) continue;
                    // Require a second stable ledger observation after the settlement delay.
                    var stable = row.NetPnl == income.NetPnl && row.TradingFees == -income.TradingFees && row.FundingFees == -income.FundingIncome;
                    var updated = row with { NetPnl = income.NetPnl, TradingFees = -income.TradingFees, FundingFees = -income.FundingIncome,
                        RealizedR = row.InitialRiskAmount > 0m ? income.NetPnl / row.InitialRiskAmount : null,
                        ExitPrice = row.EntryPrice + (row.Direction == TradeDirection.Long ? 1m : -1m) * income.RealizedPnl / row.Quantity,
                        Reconciled = stable };
                    await governance.Store.AppendAsync(TradingGovernance.OutcomeEvent(updated), stoppingToken);
                }
            }
            await governance.AnalyzeAsync(mode, stoppingToken);
        }
    }
}
