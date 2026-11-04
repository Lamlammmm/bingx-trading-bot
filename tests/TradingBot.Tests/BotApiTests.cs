using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingBot.Worker.Application;
using TradingBot.Worker.Domain;

namespace TradingBot.Tests;

public sealed class BotApiTests
{
    [Fact]
    public async Task ApiOnlyHostDoesNotStartTradingAndRequiresKey()
    {
        await using var factory = new BotApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/bot/status")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/bot/status")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", BotApiFactory.ApiKey);
        using var response = await client.GetAsync("/api/bot/status");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.False(body.GetProperty("liveTradingEnabled").GetBoolean());
        Assert.False(body.GetProperty("processing").GetProperty("initialized").GetBoolean());
        Assert.Equal("5m", body.GetProperty("entryTimeFrame").GetString());
        Assert.Equal(new[] { "30m", "1h" }, body.GetProperty("trendTimeFrames").EnumerateArray().Select(x => x.GetString()));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain(BotApiFactory.ApiKey, body.GetRawText());
        Assert.DoesNotContain(factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            service => service is TradingBot.Worker.Infrastructure.PaperTrading.TradingWorker or
                TradingBot.Worker.Infrastructure.BingX.BingXMarketDataWorker);
        client.DefaultRequestHeaders.Add("X-Api-Key", BotApiFactory.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/bot/status")).StatusCode);
    }

    [Fact]
    public async Task MissingApiKeyPreventsHostStartup()
    {
        await using var factory = new BotApiFactory("");
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Api:Key", error.Message);
    }

    [Fact]
    public async Task ReadinessRequiresFreshDataForEverySymbolAndSuccessfulProcessing()
    {
        await using var factory = new BotApiFactory();
        using var client = factory.CreateClient();
        // Enable readiness only; workers remain unregistered in this isolated HTTP test.
        factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TradingBot.Worker.Configuration.BotOptions>>()
            .Value.Enabled = true;
        var state = factory.Services.GetRequiredService<BotRuntimeState>();
        var candles = factory.Services.GetRequiredService<ICandleStore>();
        var now = DateTimeOffset.UtcNow;
        state.Snapshot = new BotSnapshot(Initialized: true, LastProcessedAt: now);
        candles.Upsert("BTC-USDT", TimeFrame.FiveMinutes, new Candle(now.AddMinutes(-5), now, 1, 1, 1, 1, 1, true));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        candles.Upsert("ETH-USDT", TimeFrame.FiveMinutes, new Candle(now.AddMinutes(-5), now, 1, 1, 1, 1, 1, true));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        candles.Upsert("ETH-USDT", TimeFrame.FiveMinutes, new Candle(now.AddMinutes(-5), now.AddMinutes(-11), 1, 1, 1, 1, 1, true));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        candles.Upsert("ETH-USDT", TimeFrame.FiveMinutes, new Candle(now.AddMinutes(-5), now.AddMinutes(1), 1, 1, 1, 1, 1, true));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        candles.Upsert("ETH-USDT", TimeFrame.FiveMinutes, new Candle(now.AddMinutes(-5), now, 1, 1, 1, 1, 1, true));
        state.Snapshot = state.Snapshot with { HasProcessingError = true };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        state.Snapshot = state.Snapshot with { HasProcessingError = false, LastProcessedAt = now.AddMinutes(-11) };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task FullQueueWaitsWithoutDroppingProtectionCandles()
    {
        await using var factory = new BotApiFactory();
        var queue = factory.Services.GetRequiredService<Channel<MarketUpdate>>();
        var now = DateTimeOffset.UtcNow;
        var first = new MarketUpdate("BTC-USDT", TimeFrame.FiveMinutes,
            new Candle(now.AddMinutes(-5), now, 100, 105, 90, 100, 1, true));
        Assert.True(queue.Writer.TryWrite(first));
        for (var index = 1; index < 1000; index++)
            Assert.True(queue.Writer.TryWrite(first with { Symbol = "ETH-USDT" }));
        var pending = queue.Writer.WriteAsync(first with { Symbol = "SOL-USDT" }).AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Same(first, await queue.Reader.ReadAsync());
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 1; index < 1000; index++)
            Assert.Equal("ETH-USDT", (await queue.Reader.ReadAsync()).Symbol);
        Assert.Equal("SOL-USDT", (await queue.Reader.ReadAsync()).Symbol);
    }

    private sealed class BotApiFactory(string apiKey = BotApiFactory.ApiKey) : WebApplicationFactory<Program>
    {
        public const string ApiKey = "local-api-test-key";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Bot:Enabled", "false");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bot:Enabled"] = "false",
                ["Api:Key"] = apiKey,
                ["Live:Enabled"] = "true", // Disabled bot must still never trade.
                ["Seq:Url"] = ""
            }));
            builder.ConfigureServices(services => services.PostConfigure<TradingBot.Worker.Configuration.BingXOptions>(options =>
                options.Symbols = new() { "BTC-USDT", "ETH-USDT" }));
        }
    }
}
