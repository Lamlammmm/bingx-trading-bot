using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure;
using TradingBot.Worker.Infrastructure.PaperTrading;
using TradingBot.Worker.Infrastructure.Persistence;

namespace TradingBot.Tests;

public sealed class BotApiTests
{
    [Fact]
    public async Task ShadowOnlyHostClosesVirtualTradeAndReportsIsolatedPeriods()
    {
        var folder = Directory.CreateTempSubdirectory("trading-shadow-test-");
        var prefix = $"TRADINGBOT_TEST_{Guid.NewGuid():N}_";
        var flag = prefix + "ExperimentalStrategy__Enabled";
        try
        {
            Environment.SetEnvironmentVariable(flag, "true");
            await using var baseFactory = new BotApiFactory(overrides: new()
            {
                ["Live:Enabled"] = "false", ["PaperTrading:MaximumEntryAgeSeconds"] = "0"
            }, shadowTestPath: folder.FullName);
            await using var factory = baseFactory.WithWebHostBuilder(host => host.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddEnvironmentVariables(prefix)));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", BotApiFactory.ApiKey);
            var hosted = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToArray();
            Assert.Single(hosted.OfType<ShadowTradingWorker>());
            Assert.DoesNotContain(hosted, worker => worker is TradingWorker or TradingAnalyticsWorker or
                TradingBot.Worker.Infrastructure.BingX.BingXMarketDataWorker);
            var baseline = factory.Services.GetRequiredService<ITradeStore>();
            await baseline.InitializeAsync(default);
            var now = DateTimeOffset.UtcNow;
            var account = new AccountState(10000m, DateOnly.FromDateTime(now.UtcDateTime), 0m);
            var first = new ClosedTrade("ETH-USDT", TradeDirection.Long, 1m, 100m, 112m, 95m, 110m, 12m,
                "FIXTURE", "paper", now.AddDays(-36), now.AddDays(-35),
                Metrics: new TradeMetrics(5m, 0m, 0m, 0.1m, 1m, 0m, 0m, 112m, 1440m, MarketRegime.TrendingUp));
            await baseline.CommitPaperStateAsync(account, [], first, default);
            await baseline.CommitPaperStateAsync(account, [], first with
            {
                NetPnl = -5m, OpenedAt = now.AddDays(-35), ClosedAt = now.AddDays(-34),
            }, default);

            var marketCandles = factory.Services.GetRequiredService<ICandleStore>();
            factory.Services.GetRequiredService<IContractStore>().Upsert("BTC-USDT", new("BTC-USDT", 4, 2, 0.0001m, 2m, true));
            var before = now.AddMinutes(-12);
            var end = new DateTimeOffset(before.Year, before.Month, before.Day, before.Hour, 0, 0, TimeSpan.Zero);
            var (entry, medium, higher) = ExperimentalStrategyTests.Pullback(closeTime: end);
            foreach (var candle in entry) marketCandles.Upsert("BTC-USDT", TimeFrame.FiveMinutes, candle);
            foreach (var candle in medium) marketCandles.Upsert("BTC-USDT", TimeFrame.ThirtyMinutes, candle);
            foreach (var candle in higher) marketCandles.Upsert("BTC-USDT", TimeFrame.OneHour, candle);
            var trigger = new MarketUpdate("BTC-USDT", TimeFrame.FiveMinutes, entry[^1]);
            var exit = new Candle(end, end.AddMinutes(5), 106m, 106m, 90m, 95m, 100m, true);
            var diagnostics = factory.Services.GetRequiredService<SignalDiagnostics>();
            var signal = new StrategySignal("BTC-USDT", TradeDirection.Long, end, 106m, 1m, "fixture");
            for (var i = 0; i < 2; i++)
            {
                var duplicate = diagnostics.Begin(trigger, "PAPER", "Current", "test")!;
                duplicate.Observe(signal, null);
                diagnostics.Record(duplicate);
            }
            var oldHash = diagnostics.Begin(trigger, "SHADOW", "Experimental", ExperimentalStrategy.Version)!;
            oldHash.ConfigurationHash = "different-session";
            oldHash.Observe(signal, null);
            diagnostics.Record(oldHash);

            var shadow = factory.Services.GetRequiredService<ShadowTradingWorker>();
            shadow.Observe(trigger);
            marketCandles.Upsert("BTC-USDT", TimeFrame.FiveMinutes, exit);
            shadow.Observe(trigger with { Candle = exit });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            JsonElement response;
            JsonElement experimentalTotal;
            do
            {
                await Task.Delay(100, deadline.Token);
                response = await client.GetFromJsonAsync<JsonElement>("/api/bot/shadow/performance?days=90", deadline.Token);
                experimentalTotal = default;
            } while (!response.TryGetProperty("experimental", out var experimental) ||
                !experimental.TryGetProperty("closedTrades", out experimentalTotal) ||
                experimentalTotal.GetProperty("setupObservations").ValueKind == JsonValueKind.Null ||
                experimentalTotal.GetProperty("trades").GetInt32() != 1);

            Assert.True(response.GetProperty("running").GetBoolean());
            Assert.Equal(2, response.GetProperty("marketBarsProcessed").GetInt64());
            Assert.Single(Directory.GetFiles(folder.FullName, "paper.db.shadow-*.db"));
            var baselineReport = response.GetProperty("current");
            var baselineTotal = baselineReport.GetProperty("closedTrades");
            Assert.Equal(2, baselineTotal.GetProperty("trades").GetInt32());
            Assert.Equal(2, baselineTotal.GetProperty("marketScans").GetInt64());
            Assert.Equal(1, baselineTotal.GetProperty("setupObservations").GetInt64());
            Assert.Equal(5m, baselineTotal.GetProperty("closedTradeDrawdownUsdt").GetDecimal());
            Assert.Equal(2m, baselineTotal.GetProperty("feesUsdt").GetDecimal());
            Assert.Equal(0.2m, baselineTotal.GetProperty("fundingPaidUsdt").GetDecimal());
            Assert.Equal(3.5m, baselineTotal.GetProperty("expectancy").GetDecimal());
            Assert.Equal(2.4m, baselineTotal.GetProperty("profitFactor").GetDecimal());
            Assert.Equal(50m, baselineTotal.GetProperty("winRatePercent").GetDecimal());
            Assert.Contains(baselineReport.GetProperty("daily").EnumerateArray(),
                day => day.GetProperty("trades").GetInt32() > 0 && day.GetProperty("setupObservations").ValueKind == JsonValueKind.Null);
            Assert.Equal(1, experimentalTotal.GetProperty("setupObservations").GetInt64());
            Assert.Equal(2, experimentalTotal.GetProperty("marketScans").GetInt64());
            Assert.True(experimentalTotal.GetProperty("estimatedSpreadAndSlippageUsdt").GetDecimal() > 0m);
            Assert.True(experimentalTotal.GetProperty("closedTradeDrawdownUsdt").GetDecimal() > 0m);
            Assert.Contains(response.GetProperty("experimental").GetProperty("weekly").EnumerateArray(),
                week => week.GetProperty("trades").GetInt32() == 1);
            Assert.Contains(response.GetProperty("experimental").GetProperty("monthly").EnumerateArray(),
                month => month.GetProperty("trades").GetInt32() == 1);

            var gapBar = exit with { OpenTime = exit.OpenTime.AddMinutes(10), CloseTime = exit.CloseTime.AddMinutes(10) };
            marketCandles.Upsert("BTC-USDT", TimeFrame.FiveMinutes, gapBar);
            shadow.Observe(trigger with { Candle = gapBar });
            do
            {
                await Task.Delay(100, deadline.Token);
                response = await client.GetFromJsonAsync<JsonElement>("/api/bot/shadow/performance", deadline.Token);
            } while (response.GetProperty("running").GetBoolean());
            Assert.Contains("Missing shadow candles", response.GetProperty("fault").GetString());
            Assert.Equal(2, response.GetProperty("marketBarsProcessed").GetInt64());
        }
        finally
        {
            Environment.SetEnvironmentVariable(flag, null);
            foreach (var database in folder.EnumerateFiles("*.db"))
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.FullName }.ToString());
                SqliteConnection.ClearPool(connection);
            }
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SignalDiagnosticsReportsPrimaryReasonsAndExecutionOutcomes()
    {
        await using var factory = new BotApiFactory();
        using var client = factory.CreateClient();
        await using var connection = new SqliteConnection("Data Source=signal-api-test;Mode=Memory;Cache=Shared;Pooling=false");
        await connection.OpenAsync();
        var store = factory.Services.GetRequiredService<Func<SignalScanStore>>()();
        await store.InitializeAsync(default);
        await store.InitializeAsync(default); // The additive schema is safe to initialize again.
        var now = DateTimeOffset.UtcNow;
        var bar = new Candle(now.AddMinutes(-5), now, 95, 110, 90, 100, 1, true);
        var update = new MarketUpdate("BTC-USDT", TimeFrame.FiveMinutes, bar);
        var rejected = new SignalScan(update, "LIVE", "Current", "v1");
        rejected.Reject("LOW_VOLUME", "RSI_FILTER_REJECTED");
        var timeout = new SignalScan(update, "LIVE", "Current", "v1");
        timeout.Accept(100, 95, 110, 2);
        timeout.ExecutionStatus = "ATTEMPTED";
        timeout.Fail(new TimeoutException("private request"));
        var refused = new SignalScan(update, "LIVE", "Current", "v1");
        refused.Accept(100, 95, 110, 2);
        refused.ExecutionStatus = "ATTEMPTED";
        refused.Fail(new BingXOrderRejected(123, "private response"));
        var filled = new SignalScan(update, "SHADOW", "Experimental", "v2");
        filled.Accept(100, 95, 110, 2);
        filled.ExecutionStatus = "FILLED";
        filled.FilledAt = now;
        await store.AppendAsync([rejected, timeout, refused, filled], default);
        await store.AppendAsync([rejected], default); // Retry with the same ID stays idempotent.

        const string overview = "/api/diagnostics/signals/overview?window=24h&mode=live";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(overview)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", BotApiFactory.ApiKey);
        var report = (await client.GetFromJsonAsync<JsonElement>(overview)).GetProperty("report");
        var total = report.GetProperty("total");
        Assert.Equal(3, total.GetProperty("totalMarketScans").GetInt32());
        Assert.Equal(2, total.GetProperty("orderAttempts").GetInt32());
        Assert.Equal(1, total.GetProperty("rejectedOrders").GetInt32());
        Assert.Equal(1, total.GetProperty("unknownOrderOutcomes").GetInt32());
        Assert.Equal(0, total.GetProperty("filledOrders").GetInt32());
        Assert.Single(report.GetProperty("groups").GetProperty("byReason").EnumerateArray());
        Assert.Equal("LOW_VOLUME", report.GetProperty("topRejection").GetString());
        var history = (await client.GetFromJsonAsync<JsonElement>("/api/diagnostics/signals/history?symbol=btc-usdt&mode=LIVE"))
            .GetProperty("items");
        Assert.Equal(3, history.GetArrayLength());
        Assert.Contains(history.EnumerateArray(), scan => scan.GetProperty("executionStatus").GetString() == "REJECTED" &&
            scan.GetProperty("errorDetails").GetString() == "BingXOrderRejected:123");
        Assert.DoesNotContain("private", history.GetRawText());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/diagnostics/signals/history?limit=501")).StatusCode);
        await store.RetainAsync(now.AddHours(1), default);
        Assert.Empty(await store.HistoryAsync(new(now.AddHours(-1), now.AddHours(1), null, null, null, null, null, null, null), 100, 0, default));
    }

    [Fact]
    public async Task LivePerformanceUsesLocalDayUniqueConfirmedEntriesAndReconciledOutcomes()
    {
        const string database = "Data Source=live-frequency-api;Mode=Memory;Cache=Shared;Pooling=false";
        await using var connection = new SqliteConnection(database);
        await connection.OpenAsync();
        var journal = new SqlTradingResearchStore(() => new SqliteConnection(database));
        var signalStore = new SignalScanStore(() => new SqliteConnection(database));
        await journal.InitializeAsync(default);
        await signalStore.InitializeAsync(default);
        await using var baseline = new BotApiFactory();
        await using var factory = baseline.WithWebHostBuilder(host => host.ConfigureServices(services =>
        {
            services.AddSingleton<ITradingResearchStore>(journal);
            services.AddSingleton<Func<SignalScanStore>>(_ => () => signalStore);
            services.AddSingleton<ILiveExecutor>(_ => throw new InvalidOperationException("Metrics must not resolve exchange execution."));
        }));
        using var client = factory.CreateClient();
        const string route = "/api/bot/live/performance";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", BotApiFactory.ApiKey);

        var now = DateTimeOffset.UtcNow;
        var vietnamOffset = TimeSpan.FromHours(7);
        var yesterday = new DateTimeOffset(now.ToOffset(vietnamOffset).Date.AddDays(-1), vietnamOffset);
        var snapshot = new StrategySnapshot(TradingMode.LIVE, "v1", "cfg", yesterday.AddDays(-1), "APPROVED", "{}");
        await journal.AppendAsync(new(TradingMode.LIVE, "cross-fill", "cross", null, "v1", "cfg", "BTC-USDT", "entry_filled", yesterday.AddSeconds(-1), "{}"), default);
        await journal.AppendAsync(new(TradingMode.LIVE, "cross-protected", "cross", null, "v1", "cfg", "BTC-USDT", "entry_protected", yesterday.AddSeconds(1), "{}"), default);
        await journal.AppendAsync(new(TradingMode.LIVE, "fill", "one", null, "v1", "cfg", "BTC-USDT", "entry_filled", yesterday.AddSeconds(2), "{}"), default);
        await journal.AppendAsync(new(TradingMode.LIVE, "fill-retry", "one", null, "v1", "cfg", "BTC-USDT", "entry_filled", yesterday.AddSeconds(3), "{}"), default);
        await journal.AppendAsync(new(TradingMode.LIVE, "legacy", "two", null, "v1", "cfg", "ETH-USDT", "entry_protected", yesterday.AddSeconds(4), "{}"), default);
        await journal.AppendAsync(new(TradingMode.LIVE, "response", "unconfirmed", null, "v1", "cfg", "BTC-USDT", "entry_response", yesterday.AddSeconds(5), "{}"), default);
        await journal.AppendAsync(new(TradingMode.PAPER, "paper", "paper", null, "v1", "cfg", "BTC-USDT", "entry_filled", yesterday.AddSeconds(6), "{}"), default);

        var win = new TradingOutcome(TradingMode.LIVE, "one", "signal", snapshot, "BTC-USDT", TimeFrame.FiveMinutes,
            MarketRegime.TrendingUp, TradeDirection.Long, 1m, 100m, 110m, 95m, 110m, 1m, 1m, 0m, 9m, 1.8m, 10m, 0m,
            yesterday, yesterday.AddHours(1), "test", "TP", null, null, true, "test", 100m);
        await journal.AppendAsync(TradingGovernance.OutcomeEvent(win), default);
        await journal.AppendAsync(TradingGovernance.OutcomeEvent(win with
        {
            TradeId = "two", ClosedAt = yesterday.AddHours(2), NetPnl = -4m, TradingFees = 0.5m
        }), default);
        await journal.AppendAsync(new(TradingMode.LIVE, "observed-close", "unverified", null, "v1", "cfg", "BTC-USDT",
            "closed_observed", yesterday.AddDays(1), "{}"), default);
        var update = new MarketUpdate("BTC-USDT", TimeFrame.FiveMinutes, new Candle(now.AddMinutes(-5), now, 95, 110, 90, 100, 1, true));
        var liveScan = new SignalScan(update, "LIVE", "Current", "v1") { Stage = "STRATEGY" };
        liveScan.Reject("LOW_VOLUME");
        var paperScan = new SignalScan(update, "PAPER", "Current", "v1");
        paperScan.Reject("PAPER_ONLY");
        await signalStore.AppendAsync([liveScan, paperScan], default);

        var previous = await client.GetFromJsonAsync<JsonElement>($"{route}?date={yesterday:yyyy-MM-dd}");
        Assert.Equal(yesterday, previous.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(2, previous.GetProperty("dailyFilledEntries").GetInt32());
        Assert.True(previous.GetProperty("dailyTradingTargetAchievement").GetProperty("met").GetBoolean());
        Assert.Equal(0.1m, previous.GetProperty("frequencyWindow").GetProperty("averageFilledEntriesPerCalendarDay").GetDecimal());
        Assert.Equal(5m, previous.GetProperty("dailyNetPnlUsdt").GetDecimal());
        Assert.Equal(1.5m, previous.GetProperty("dailyTradingFeesUsdt").GetDecimal());
        var performance = previous.GetProperty("performance");
        Assert.Equal(0.5m, performance.GetProperty("winRate").GetDecimal());
        Assert.Equal(2.5m, performance.GetProperty("netExpectancyUsdt").GetDecimal());
        Assert.Equal(4m, performance.GetProperty("maxRealizedDrawdownUsdt").GetDecimal());
        Assert.Equal(1, performance.GetProperty("consecutiveLosses").GetInt32());

        var today = await client.GetFromJsonAsync<JsonElement>(route);
        Assert.False(today.GetProperty("liveEnabled").GetBoolean());
        Assert.Equal(0, today.GetProperty("dailyFilledEntries").GetInt32());
        Assert.Equal(1, today.GetProperty("dailyUnreconciledCloses").GetInt32());
        Assert.Equal(JsonValueKind.Null, today.GetProperty("dailyNetPnlUsdt").ValueKind);
        Assert.Equal(JsonValueKind.Null, today.GetProperty("performance").GetProperty("maxRealizedDrawdownUsdt").ValueKind);
        Assert.Equal(JsonValueKind.Null, today.GetProperty("exposure").GetProperty("totalGrossNotionalUsdt").ValueKind);
        Assert.Equal("LOW_VOLUME", Assert.Single(today.GetProperty("signalDiagnostics").GetProperty("rejectionReasons").EnumerateArray()).GetProperty("reason").GetString());
        Assert.Contains("out of sample", today.GetProperty("followUp").GetString());
    }

    [Fact]
    public async Task LivePerformanceRejectsForcingEntriesWithoutBlockingTheHost()
    {
        await using var factory = new BotApiFactory(overrides: new() { ["TradingFrequency:ForceEntryToMeetTarget"] = "true" });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", BotApiFactory.ApiKey);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/bot/live/performance")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/bot/status")).StatusCode);
    }

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
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/bot/shadow/performance")).GetProperty("ready").GetBoolean());
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

    [Theory]
    [InlineData("PaperTrading:Macd:Fast", "0", "PaperTrading:Macd")]
    [InlineData("Live:Macd:Slow", "12", "Live:Macd")]
    [InlineData("Live:Macd:Signal", "0", "Live:Macd")]
    public async Task InvalidMacdOptionsPreventStartup(string key, string value, string section)
    {
        await using var factory = new BotApiFactory(overrides: new Dictionary<string, string?> { [key] = value });
        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(section, error.Message);
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

    private sealed class BotApiFactory(string apiKey = BotApiFactory.ApiKey, Dictionary<string, string?>? overrides = null,
        string? shadowTestPath = null)
        : WebApplicationFactory<Program>
    {
        public const string ApiKey = "local-api-test-key";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Bot:Enabled", "false");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Bot:Enabled"] = "false",
                    ["Api:Key"] = apiKey,
                    ["Live:Enabled"] = "true", // Disabled bot must still never trade.
                    ["Seq:Url"] = ""
                });
                if (overrides is not null) configuration.AddInMemoryCollection(overrides);
            });
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<TradingBot.Worker.Configuration.BingXOptions>(options =>
                    options.Symbols = new() { "BTC-USDT", "ETH-USDT" });
                if (shadowTestPath is null)
                {
                    services.AddSingleton<Func<SignalScanStore>>(_ => () => new(() =>
                        new SqliteConnection("Data Source=signal-api-test;Mode=Memory;Cache=Shared;Pooling=false")));
                    return;
                }
                services.PostConfigure<PersistenceOptions>(options =>
                {
                    options.Provider = "Sqlite";
                    options.DatabasePath = Path.Combine(shadowTestPath, "paper.db");
                });
                services.AddSingleton<ILiveExecutor>(_ => throw new InvalidOperationException("Live executor may not be resolved in a Shadow-only test."));
                services.AddSingleton<IBingXMarketClient>(_ => throw new InvalidOperationException("Only injected market data may be used in this test."));
                services.AddHostedService(provider => provider.GetRequiredService<SignalDiagnostics>());
                ShadowTradingWorker.Add(services);
            });
        }
    }
}
