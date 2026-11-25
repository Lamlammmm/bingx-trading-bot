using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.Persistence;

namespace TradingBot.Worker.Infrastructure.PaperTrading;

public sealed class ShadowTradingWorker(ICandleStore marketCandles, IContractStore contracts,
    IOptions<ExperimentalStrategyOptions> options, IOptions<StrategyOptions> strategyOptions, IOptions<RiskOptions> risk,
    IOptions<PaperTradingOptions> costs, IOptions<PersistenceOptions> persistence, ILoggerFactory loggerFactory,
    SignalDiagnostics diagnostics)
    : BackgroundService
{
    private readonly Channel<ShadowBar> _updates = Channel.CreateBounded<ShadowBar>(new BoundedChannelOptions(256)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
    });
    private volatile bool _accepting = options.Value.Enabled;
    private volatile string? _fault;
    private volatile bool _ready;
    private long _processed;
    private SqliteTradeStore? _store;
    private string? _configurationHash;

    public static void Add(IServiceCollection services)
    {
        services.AddOptions<ExperimentalStrategyOptions>().BindConfiguration(ExperimentalStrategyOptions.SectionName);
        services.AddSingleton<ShadowTradingWorker>();
        services.AddHostedService<ShadowTradingWorker>(provider => provider.GetRequiredService<ShadowTradingWorker>());
    }

    public void Observe(MarketUpdate update)
    {
        if (!_accepting || update.TimeFrame != TimeFrame.FiveMinutes || !update.Candle.IsClosed) return;
        var snapshot = new ShadowBar(update, marketCandles.Get(update.Symbol, TimeFrame.FiveMinutes),
            marketCandles.Get(update.Symbol, TimeFrame.ThirtyMinutes), marketCandles.Get(update.Symbol, TimeFrame.OneHour));
        if (_updates.Writer.TryWrite(snapshot) || !_accepting) return;

        _fault = "Shadow queue overflow. Processing stopped to preserve the simulation chronology.";
        _accepting = false;
        _updates.Writer.TryComplete();
        loggerFactory.CreateLogger<ShadowTradingWorker>().LogError("{ShadowFault}", _fault);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        await Task.Yield();
        var logger = loggerFactory.CreateLogger<ShadowTradingWorker>();
        try
        {
            _configurationHash = TradingPolicyRuntime.Hash(JsonSerializer.Serialize(new
            {
                ExperimentalStrategy.Version, Indicators = strategyOptions.Value,
                Strategy = options.Value, Risk = risk.Value, Costs = costs.Value, costs.Value.Macd
            }));
            var path = Path.GetFullPath(persistence.Value.DatabasePath) +
                $".shadow-{ExperimentalStrategy.Version}-{_configurationHash[..16]}.db";
            _store = new SqliteTradeStore(Options.Create(new PersistenceOptions { DatabasePath = path }));
            var candleSnapshot = new ShadowCandleStore();
            var engine = new PaperTradingEngine(candleSnapshot,
                new ExperimentalStrategy(strategyOptions, options), new RiskManager(risk), new DisabledAnalyzer(),
                _store, contracts, risk, costs, Options.Create(new OpenAIOptions()), strategyOptions,
                loggerFactory.CreateLogger<PaperTradingEngine>(), diagnostics: diagnostics, shadowVersion: ExperimentalStrategy.Version,
                shadowConfigurationHash: _configurationHash, enforcePortfolioLimits: true);
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["TradingMode"] = "SHADOW" });
            await engine.InitializeAsync(stoppingToken);
            var lastClose = (await _store.LoadOpenPositionsAsync(stoppingToken)).ToDictionary(position => position.Symbol,
                position => position.Metrics?.LastEvaluatedAt ?? position.OpenedAt, StringComparer.OrdinalIgnoreCase);
            _ready = true;
            await foreach (var snapshot in _updates.Reader.ReadAllAsync(stoppingToken))
            {
                if (!_accepting) break;
                var bar = snapshot.Update;
                if (lastClose.TryGetValue(bar.Symbol, out var previousClose))
                {
                    if (bar.Candle.CloseTime <= previousClose) continue;
                    if (bar.Candle.OpenTime != previousClose)
                    {
                        _fault = "Missing shadow candles. Restart from a consecutive feed or archive the prior shadow session.";
                        logger.LogError("{ShadowFault} {Symbol} previous close {PreviousClose:o}, next open {NextOpen:o}",
                            _fault, bar.Symbol, previousClose, bar.Candle.OpenTime);
                        break;
                    }
                }
                candleSnapshot.Current = snapshot;
                await engine.ProcessAsync(bar, stoppingToken);
                lastClose[bar.Symbol] = bar.Candle.CloseTime;
                Interlocked.Increment(ref _processed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            _fault = "Shadow simulation failed during initialization or processing.";
            logger.LogError("Shadow simulation stopped due to {ErrorType}", error.GetType().Name);
        }
        finally
        {
            _accepting = false;
            _updates.Writer.TryComplete();
            // Release candle snapshots after the optional simulation stops.
            while (_updates.Reader.TryRead(out _)) { }
        }
    }

    public static void MapPerformance(WebApplication app)
    {
        app.MapGet("/api/bot/shadow/performance", async (HttpContext context, int? days,
            Func<SignalScanStore> scans, SignalDiagnostics diagnostics, CancellationToken token) =>
        {
            var shadow = context.RequestServices.GetService<ShadowTradingWorker>();
            if (shadow is null || !shadow._ready || shadow._store is not { } store)
                return Results.Ok(new { Ready = false, Fault = shadow?._fault });
            if (days is < 1 or > 90) return Results.BadRequest("days must be between 1 and 90.");

            var until = DateTimeOffset.UtcNow;
            var since = until.AddDays(-(days ?? 7));
            var baseline = context.RequestServices.GetRequiredService<ITradeStore>();
            Dictionary<DateOnly, (long Scans, long Setups)>? currentDays = null;
            Dictionary<DateOnly, (long Scans, long Setups)>? experimentalDays = null;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    currentDays = await scans().SetupDaysAsync(since, until, "PAPER", null, deadline.Token);
                    experimentalDays = await scans().SetupDaysAsync(since, until, "SHADOW", shadow._configurationHash, deadline.Token);
                }
                catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException ||
                    (error is OperationCanceledException && !token.IsCancellationRequested))
                {
                    // Trade history remains available even if scan storage is temporarily offline.
                }
            }
            return Results.Ok(new
            {
                Ready = true, Running = shadow._accepting, Fault = shadow._fault, MarketBarsProcessed = Interlocked.Read(ref shadow._processed),
                ExperimentalStrategy.Version, ConfigurationHash = shadow._configurationHash, SinceUtc = since, UntilUtc = until,
                DiagnosticsEnabled = diagnostics.Enabled, DiagnosticsFallbackCount = diagnostics.FallbackCount,
                Current = await ReadPerformanceAsync(baseline, currentDays, since, until, token),
                Experimental = await ReadPerformanceAsync(store, experimentalDays, since, until, token)
            });
        });
    }

    private static async Task<object> ReadPerformanceAsync(ITradeStore store,
        Dictionary<DateOnly, (long Scans, long Setups)>? setupDays, DateTimeOffset since,
        DateTimeOffset until, CancellationToken token)
    {
        var trades = new List<ClosedTrade>();
        await foreach (var row in store.ReadClosedTradesAsync(token))
            if (row.Trade.ClosedAt >= since && row.Trade.ClosedAt < until) trades.Add(row.Trade);

        static object Summarize(DateOnly? period, IReadOnlyCollection<ClosedTrade> rows,
            IReadOnlyCollection<(long Scans, long Setups)> days)
        {
            var wins = 0;
            decimal pnl = 0, peak = 0, drawdown = 0, grossWin = 0, grossLoss = 0, fees = 0, funding = 0, friction = 0;
            var knownCosts = 0;
            // Simultaneous settlements have no observable ordering at candle resolution.
            foreach (var close in rows.GroupBy(t => t.ClosedAt).OrderBy(g => g.Key))
            {
                pnl += close.Sum(t => t.NetPnl);
                peak = Math.Max(peak, pnl);
                drawdown = Math.Max(drawdown, peak - pnl);
                foreach (var trade in close)
                {
                    if (trade.NetPnl > 0m) { wins++; grossWin += trade.NetPnl; }
                    else grossLoss -= trade.NetPnl;
                    if (trade.Metrics is not { } metrics) continue;
                    var sign = trade.Direction == TradeDirection.Long ? 1m : -1m;
                    var entryRate = metrics.EntrySlippageBasisPoints / 10_000m;
                    var exitRate = metrics.FinalExitSlippageBasisPoints / 10_000m;
                    if (1m + sign * entryRate <= 0m || 1m - sign * exitRate <= 0m || metrics.AverageExitPrice <= 0m) continue;
                    fees += metrics.TotalFees;
                    funding += metrics.FundingPaid;
                    friction += sign * trade.Quantity * (trade.EntryPrice - trade.EntryPrice / (1m + sign * entryRate)
                        + metrics.AverageExitPrice / (1m - sign * exitRate) - metrics.AverageExitPrice);
                    knownCosts++;
                }
            }
            return new
            {
                PeriodStartUtc = period, Trades = rows.Count, NetPnl = pnl,
                WinRatePercent = rows.Count > 0 ? wins * 100m / rows.Count : (decimal?)null,
                ProfitFactor = grossLoss > 0m ? grossWin / grossLoss : (decimal?)null,
                Expectancy = rows.Count > 0 ? pnl / rows.Count : (decimal?)null,
                ClosedTradeDrawdownUsdt = drawdown,
                ObservedDays = days.Count, MarketScans = days.Count > 0 ? days.Sum(d => d.Scans) : (long?)null,
                SetupObservations = days.Count > 0 ? days.Sum(d => d.Setups) : (long?)null,
                TradesWithCostMetrics = knownCosts,
                FeesUsdt = knownCosts == rows.Count ? fees : (decimal?)null,
                FundingPaidUsdt = knownCosts == rows.Count ? funding : (decimal?)null,
                EstimatedSpreadAndSlippageUsdt = knownCosts == rows.Count ? friction : (decimal?)null
            };
        }

        setupDays ??= [];
        var tradesByDay = trades.ToLookup(t => DateOnly.FromDateTime(t.ClosedAt.UtcDateTime));
        var observedDates = tradesByDay.Select(g => g.Key).Union(setupDays.Keys);
        object[] Periods(Func<DateOnly, DateOnly> bucket) => observedDates.GroupBy(bucket).OrderBy(g => g.Key)
            .Select(g => Summarize(g.Key, g.SelectMany(day => tradesByDay[day]).ToArray(),
                g.Where(setupDays.ContainsKey).Select(day => setupDays[day]).ToArray())).ToArray();

        return new
        {
            OpenPositions = (await store.LoadOpenPositionsAsync(token)).Count,
            ClosedTrades = Summarize(null, trades, setupDays.Values),
            Daily = Periods(day => day),
            Weekly = Periods(day => day.AddDays(-((int)day.DayOfWeek + 6) % 7)),
            Monthly = Periods(day => new DateOnly(day.Year, day.Month, 1))
        };
    }

    private sealed record ShadowBar(MarketUpdate Update, IReadOnlyList<Candle> Entry,
        IReadOnlyList<Candle> Medium, IReadOnlyList<Candle> Higher);

    private sealed class ShadowCandleStore : ICandleStore
    {
        public ShadowBar? Current { get; set; }
        public void Upsert(string symbol, TimeFrame timeFrame, Candle candle) { }

        public IReadOnlyList<Candle> Get(string symbol, TimeFrame timeFrame) =>
            Current is { } snapshot && snapshot.Update.Symbol == symbol ? timeFrame switch
            {
                TimeFrame.FiveMinutes => snapshot.Entry,
                TimeFrame.ThirtyMinutes => snapshot.Medium,
                TimeFrame.OneHour => snapshot.Higher,
                _ => []
            } : [];
    }

    private sealed class DisabledAnalyzer : IOpenAiAnalyzer
    {
        public bool Enabled => false;
        public Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Shadow trading uses only technical signals.");
    }
}
