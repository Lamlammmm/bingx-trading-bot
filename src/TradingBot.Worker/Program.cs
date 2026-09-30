using System.Threading.Channels;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.BingX;
using TradingBot.Worker.Infrastructure.Live;
using TradingBot.Worker.Infrastructure.OpenAI;
using TradingBot.Worker.Infrastructure.PaperTrading;
using TradingBot.Worker.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);
var seqUrl = builder.Configuration["Seq:Url"];
builder.Logging.ClearProviders();
var loggerConfiguration = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext();
if (!string.IsNullOrWhiteSpace(seqUrl))
{
    loggerConfiguration.WriteTo.Seq(seqUrl);
}
else
{
    loggerConfiguration.WriteTo.Console(
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
        theme: ConsoleTheme.None);
}
Log.Logger = loggerConfiguration.CreateLogger();
builder.Logging.AddSerilog(Log.Logger, dispose: true);
builder.Services.Configure<BingXOptions>(builder.Configuration.GetSection(BingXOptions.SectionName));
builder.Services.Configure<StrategyOptions>(builder.Configuration.GetSection(StrategyOptions.SectionName));
builder.Services.Configure<RiskOptions>(builder.Configuration.GetSection(RiskOptions.SectionName));
builder.Services.Configure<PaperTradingOptions>(builder.Configuration.GetSection(PaperTradingOptions.SectionName));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection(OpenAIOptions.SectionName));
builder.Services.Configure<PersistenceOptions>(builder.Configuration.GetSection(PersistenceOptions.SectionName));
builder.Services.Configure<LiveOptions>(builder.Configuration.GetSection(LiveOptions.SectionName));
builder.Services.PostConfigure<LiveOptions>(options =>
    options.StatePath = Path.GetFullPath(options.StatePath, builder.Environment.ContentRootPath));
builder.Services.PostConfigure<BingXOptions>(options =>
{
    var apiKey = Environment.GetEnvironmentVariable("BINGX_API_KEY");
    if (!string.IsNullOrWhiteSpace(apiKey)) options.ApiKey = apiKey;

    var apiSecret = Environment.GetEnvironmentVariable("BINGX_API_SECRET");
    if (!string.IsNullOrWhiteSpace(apiSecret)) options.ApiSecret = apiSecret;

    options.Symbols = options.Symbols
        .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
        .Select(symbol => symbol.Trim().ToUpperInvariant())
        .Distinct()
        .ToList();
    if (options.Symbols.Count == 0)
    {
        options.Symbols.Add("BTC-USDT");
    }
});
builder.Services.PostConfigure<OpenAIOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.ApiKey))
    {
        options.ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    }
});
builder.Services.PostConfigure<PersistenceOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.ConnectionString))
    {
        options.ConnectionString = Environment.GetEnvironmentVariable("SUPABASE_DB_CONNECTION_STRING");
    }
});

builder.Services.AddSingleton<HttpClient>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<BingXOptions>>().Value;
    return new HttpClient
    {
        BaseAddress = new Uri(options.RestBaseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds)
    };
});
builder.Services.AddSingleton<IBingXMarketClient, BingXRestClient>();
builder.Services.AddSingleton<IOpenAiAnalyzer>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpenAIOptions>>();
    var httpClient = new HttpClient
    {
        BaseAddress = new Uri(options.Value.BaseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(options.Value.RequestTimeoutSeconds)
    };
    return new OpenAiAnalysisClient(httpClient, options,
        serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OpenAiAnalysisClient>>());
});
builder.Services.AddSingleton<Channel<MarketUpdate>>(_ => Channel.CreateBounded<MarketUpdate>(new BoundedChannelOptions(1_000)
{
    FullMode = BoundedChannelFullMode.DropOldest,
    SingleReader = true,
    SingleWriter = true
}));
builder.Services.AddSingleton<ICandleStore, InMemoryCandleStore>();
builder.Services.AddSingleton<IContractStore, InMemoryContractStore>();
builder.Services.AddSingleton<ITradeStore>(serviceProvider =>
{
    var persistenceOptions = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PersistenceOptions>>();
    return persistenceOptions.Value.Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
        ? new PostgresTradeStore(persistenceOptions)
        : new SqliteTradeStore(persistenceOptions);
});
builder.Services.AddSingleton<ITradingStrategy, BreakoutStrategy>();
builder.Services.AddSingleton<ILiveTradeJournal>(provider => (ILiveTradeJournal)provider.GetRequiredService<ITradeStore>());
builder.Services.AddSingleton<IRiskManager, RiskManager>();
builder.Services.AddSingleton<IBingXMarketStream, BingXWebSocketClient>();
builder.Services.AddSingleton<PaperTradingEngine>();
builder.Services.AddSingleton<ILiveExecutor, BingXLiveExecutor>();
builder.Services.AddSingleton<LiveTradingEngine>();
builder.Services.AddHostedService<BingXMarketDataWorker>();
builder.Services.AddHostedService<TradingWorker>();

using var host = builder.Build();
if (args.Contains("--export-trade-analysis", StringComparer.OrdinalIgnoreCase))
{
    var store = host.Services.GetRequiredService<ITradeStore>();
    var journal = host.Services.GetRequiredService<ILiveTradeJournal>();
    var exportDirectory = Path.Combine(builder.Environment.ContentRootPath, "exports");
    Directory.CreateDirectory(exportDirectory);
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
    var paperPath = Path.Combine(exportDirectory, $"paper-trades-{stamp}.jsonl");
    var livePath = Path.Combine(exportDirectory, $"live-trades-{stamp}.jsonl");
    var paperCount = 0;
    await using (var writer = new StreamWriter(new FileStream(paperPath, FileMode.CreateNew, FileAccess.Write)))
    {
        await foreach (var row in store.ReadClosedTradesAsync(CancellationToken.None))
        {
            await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, Dataset = "paper", row.Id,
                row.Trade.Symbol, Direction = row.Trade.Direction.ToString(), row.Trade.Quantity,
                row.Trade.EntryPrice, row.Trade.ExitPrice, row.Trade.StopLoss,
                row.Trade.TakeProfit, row.Trade.NetPnl, row.Trade.ExitReason,
                row.Trade.Source, row.Trade.OpenedAt, row.Trade.ClosedAt,
                EntryFeatures = row.Features
            }));
            paperCount++;
        }
    }
    var eventsByTrade = new Dictionary<string, List<LiveTradeEvent>>();
    await foreach (var entry in journal.ReadLiveEventsAsync(CancellationToken.None))
    {
        if (!eventsByTrade.TryGetValue(entry.TradeId, out var events))
            eventsByTrade[entry.TradeId] = events = new List<LiveTradeEvent>();
        events.Add(entry);
    }
    await using (var writer = new StreamWriter(new FileStream(livePath, FileMode.CreateNew, FileAccess.Write)))
    {
        foreach (var (tradeId, events) in eventsByTrade.OrderBy(pair => pair.Value[0].OccurredAt))
        {
            var history = events.LastOrDefault(entry => entry.Kind == "exchange_position_history");
            bool? historyAvailable = null;
            if (history is not null)
            {
                using var payload = System.Text.Json.JsonDocument.Parse(history.PayloadJson);
                if (payload.RootElement.TryGetProperty("HistoryAvailable", out var available))
                    historyAvailable = available.GetBoolean();
                else if (payload.RootElement.TryGetProperty("Positions", out var positions))
                    historyAvailable = positions.GetArrayLength() > 0;
            }
            var eventRows = events.Select(entry =>
            {
                using var payload = System.Text.Json.JsonDocument.Parse(entry.PayloadJson);
                return new { entry.EventId, entry.Kind, entry.OccurredAt, Payload = payload.RootElement.Clone() };
            }).ToArray();
            await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, Dataset = "live", TradeId = tradeId, Symbol = events[0].Symbol,
                EntryRequestedAt = events.FirstOrDefault(entry => entry.Kind == "entry_requested")?.OccurredAt,
                EntryProtected = events.Any(entry => entry.Kind == "entry_protected"),
                ClosedObservedAt = events.FirstOrDefault(entry => entry.Kind == "closed_observed")?.OccurredAt,
                HistoryAvailable = historyAvailable, OutcomeVerified = false, NetPnl = (decimal?)null,
                Events = eventRows
            }));
        }
    }
    Console.WriteLine($"TRADE ANALYSIS | paper={paperCount} | live={eventsByTrade.Count}");
    Console.WriteLine($"PAPER {paperPath}");
    Console.WriteLine($"LIVE {livePath}");
    return;
}
if (args.Contains("--journal-status", StringComparer.OrdinalIgnoreCase) ||
    args.Contains("--export-live-journal", StringComparer.OrdinalIgnoreCase))
{
    var journal = host.Services.GetRequiredService<ILiveTradeJournal>();
    await journal.InitializeJournalAsync(CancellationToken.None);
    var counts = new Dictionary<string, int>();
    var export = args.Contains("--export-live-journal", StringComparer.OrdinalIgnoreCase);
    var exportPath = Path.Combine(builder.Environment.ContentRootPath, "exports", $"live-journal-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.jsonl");
    if (export) Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
    await using var writer = export ? new StreamWriter(new FileStream(exportPath, FileMode.CreateNew, FileAccess.Write)) : null;
    await foreach (var entry in journal.ReadLiveEventsAsync(CancellationToken.None))
    {
        counts[entry.Kind] = counts.GetValueOrDefault(entry.Kind) + 1;
        if (writer is not null)
        {
            using var payload = System.Text.Json.JsonDocument.Parse(entry.PayloadJson);
            await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new
            { entry.EventId, entry.TradeId, entry.Symbol, entry.Kind, entry.OccurredAt, Payload = payload.RootElement }));
        }
    }
    Console.WriteLine("LIVE JOURNAL " + System.Text.Json.JsonSerializer.Serialize(counts));
    if (export) Console.WriteLine("EXPORTED " + exportPath);
    else
    {
        var store = host.Services.GetRequiredService<ITradeStore>();
        var performance = await store.LoadTradePerformanceAsync(CancellationToken.None);
        var positions = await store.LoadOpenPositionsAsync(CancellationToken.None);
        Console.WriteLine($"PAPER DB | closedTrades={(performance?.Wins ?? 0) + (performance?.Losses ?? 0)} | openPositions={positions.Count}");
    }
    return;
}
if (args.Contains("--preflight", StringComparer.OrdinalIgnoreCase))
{
    var executor = host.Services.GetRequiredService<ILiveExecutor>();
    await executor.ValidatePositionModeAsync(CancellationToken.None);
    var balance = await executor.GetAvailableBalanceUsdtAsync(CancellationToken.None);
    var positions = await executor.GetOpenPositionCountAsync(CancellationToken.None);
    var orders = await executor.GetOpenOrderCountAsync(CancellationToken.None);
    Console.WriteLine($"PREFLIGHT READ-ONLY | USDT={balance} | positions={positions} | openOrders={orders} | Hedge mode verified");
    return;
}
await host.RunAsync();
