using System.Threading.Channels;
using Microsoft.Extensions.Logging.Console;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.BingX;
using TradingBot.Worker.Infrastructure.OpenAI;
using TradingBot.Worker.Infrastructure.PaperTrading;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.ColorBehavior = LoggerColorBehavior.Enabled;
        options.SingleLine = true;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
    });
}
else
{
    builder.Logging.AddJsonConsole(options => options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz");
}
builder.Services.Configure<BingXOptions>(builder.Configuration.GetSection(BingXOptions.SectionName));
builder.Services.Configure<StrategyOptions>(builder.Configuration.GetSection(StrategyOptions.SectionName));
builder.Services.Configure<RiskOptions>(builder.Configuration.GetSection(RiskOptions.SectionName));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection(OpenAIOptions.SectionName));
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
});
builder.Services.PostConfigure<OpenAIOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.ApiKey))
    {
        options.ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
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
builder.Services.AddSingleton<ITradingStrategy, BreakoutStrategy>();
builder.Services.AddSingleton<IRiskManager, RiskManager>();
builder.Services.AddSingleton<IBingXMarketStream, BingXWebSocketClient>();
builder.Services.AddSingleton<PaperTradingEngine>();
builder.Services.AddHostedService<BingXMarketDataWorker>();
builder.Services.AddHostedService<TradingWorker>();

await builder.Build().RunAsync();
