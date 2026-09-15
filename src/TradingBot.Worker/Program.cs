using System.Threading.Channels;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.BingX;
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
    // Seq is the primary log sink when configured; console is skipped to avoid duplicate I/O.
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
builder.Services.PostConfigure<BingXOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.ApiKey))
    {
        options.ApiKey = Environment.GetEnvironmentVariable("BINGX_API_KEY");
    }

    if (string.IsNullOrWhiteSpace(options.ApiSecret))
    {
        options.ApiSecret = Environment.GetEnvironmentVariable("BINGX_API_SECRET");
    }

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
builder.Services.AddSingleton<IRiskManager, RiskManager>();
builder.Services.AddSingleton<IBingXMarketStream, BingXWebSocketClient>();
builder.Services.AddSingleton<PaperTradingEngine>();
builder.Services.AddHostedService<BingXMarketDataWorker>();
builder.Services.AddHostedService<TradingWorker>();

await builder.Build().RunAsync();
