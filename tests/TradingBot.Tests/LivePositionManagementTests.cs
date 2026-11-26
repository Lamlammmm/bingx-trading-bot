using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.Live;
using TradingBot.Worker.Infrastructure;
using TradingBot.Worker.Infrastructure.BingX;
using TradingBot.Worker.Infrastructure.PaperTrading;

namespace TradingBot.Tests;

public sealed class LivePositionManagementTests
{
    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public async Task PartialTargetsResizeProtectionAndRestartDoesNotRepeatFill(TradeDirection direction)
    {
        using var f = new Fixture(direction);
        using (var engine = f.Create())
        {
            await engine.InitializeAsync(default);
            await engine.ProcessAsync(f.Bar(0, 112m), default);
            Assert.Equal(0.5m, f.Exchange.Quantity);
            Assert.Equal(1, f.State().Positions[0].Management!.Metrics.TakeProfitStage);
            Assert.Equal(new[] { "replace-stop", "close", "replace-stop", "replace-take" }, f.Exchange.Writes);
            Assert.Equal(0.5m, f.Exchange.ProtectedQuantity);
            Assert.Null(f.State().PendingOrder);
        }
        using (var restarted = f.Create())
        {
            await restarted.InitializeAsync(default);
            await restarted.ProcessAsync(f.Bar(0, 112m), default);
            Assert.Equal(4, f.Exchange.Writes.Count);
            await restarted.ProcessAsync(f.Bar(1, 121m), default);
            Assert.Equal(0.25m, f.Exchange.Quantity);
            Assert.Equal(2, f.State().Positions[0].Management!.Metrics.TakeProfitStage);
            Assert.Equal(0.25m, f.Exchange.ProtectedQuantity);
            Assert.True(direction == TradeDirection.Long ? f.Exchange.Stop > 100m : f.Exchange.Stop < 100m);
            Assert.Null(f.State().PendingOrder);
        }
    }

    [Theory]
    [InlineData("close-timeout")]
    [InlineData("partial-fill")]
    [InlineData("stop-timeout")]
    [InlineData("take-rejected")]
    [InlineData("position-changed")]
    public async Task UncertainManagementBlocksRetryAndRestart(string failure)
    {
        using var f = new Fixture(TradeDirection.Long);
        f.Exchange.Failure = failure;
        using (var engine = f.Create())
        {
            await engine.InitializeAsync(default);
            await Assert.ThrowsAnyAsync<Exception>(() => engine.ProcessAsync(f.Bar(0, 112m), default));
            Assert.NotNull(f.State().PendingOrder);
            Assert.False(engine.IsReady);
            var writes = f.Exchange.Writes.Count;
            await engine.ProcessAsync(f.Bar(1, 121m), default);
            Assert.Equal(writes, f.Exchange.Writes.Count);
            if (failure is "close-timeout" or "partial-fill" or "position-changed")
                Assert.Equal(new[] { "replace-stop", "close" }, f.Exchange.Writes);
            if (failure == "stop-timeout") Assert.DoesNotContain("replace-take", f.Exchange.Writes);
        }
        using var restarted = f.Create();
        var count = f.Exchange.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.InitializeAsync(default));
        Assert.Equal(count, f.Exchange.Writes.Count);
    }

    [Fact]
    public async Task ConcurrentDuplicateUpdatesProduceOnlyOnePartialClose()
    {
        using var f = new Fixture(TradeDirection.Long);
        using var engine = f.Create();
        await engine.InitializeAsync(default);
        var bar = f.Bar(0, 112m);
        await Task.WhenAll(engine.ProcessAsync(bar, default), engine.ProcessAsync(bar, default));
        Assert.Single(f.Exchange.Writes.Where(w => w == "close"));
        Assert.Equal(0.5m, f.Exchange.Quantity);
    }

    [Fact]
    public async Task HighEarlierInBarDoesNotCreateALivePartialAfterPriceRetraces()
    {
        using var f = new Fixture(TradeDirection.Long);
        using var engine = f.Create();
        await engine.InitializeAsync(default);
        var update = f.Bar(0, 105m);
        await engine.ProcessAsync(update with { Candle = update.Candle with { High = 125m } }, default);
        Assert.Empty(f.Exchange.Writes);
        Assert.Equal(1m, f.Exchange.Quantity);
    }

    [Fact]
    public async Task ApiCircuitStopsEntryButTracksExistingProtectionWhenTheExchangeRecovers()
    {
        using var f = new Fixture(TradeDirection.Long);
        using var engine = f.Create();
        await engine.InitializeAsync(default);
        f.Exchange.Failure = "query";
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ProcessAsync(f.Bar(i, 105m), default));
            await engine.ProcessAsync(f.Bar(i, 105m) with { TimeFrame = TimeFrame.ThirtyMinutes }, default);
            Assert.Equal(i + 1, f.State().ConsecutiveApiFailures);
        }
        Assert.False(engine.IsReady);
        Assert.NotNull(f.State().ApiCircuitUntil);
        var queries = f.Exchange.ManagedQueries;
        await engine.ProcessAsync(f.Bar(4, 112m) with { Symbol = "ETH-USDT" }, default);
        Assert.Equal(queries, f.Exchange.ManagedQueries);
        Assert.Empty(f.Exchange.Writes);

        f.Exchange.Failure = null;
        await engine.ProcessAsync(f.Bar(5, 112m), default);
        Assert.True(f.Exchange.ManagedQueries > queries);
        Assert.Equal(new[] { "replace-stop", "close", "replace-stop", "replace-take" }, f.Exchange.Writes);
        Assert.Equal(0.5m, f.Exchange.ProtectedQuantity);
        Assert.False(engine.IsReady); // Entry backoff still applies after a successful management read.
        Assert.Null(f.State().PendingOrder);
    }

    [Fact]
    public async Task OpenApiCircuitBlocksAnOtherwiseValidEntryWithoutQueryingLeverage()
    {
        using var f = new Fixture(TradeDirection.Long, held: false);
        File.WriteAllText(f.PathName, JsonSerializer.Serialize(new LiveTradingEngine.LiveState
        {
            ApiCircuitUntil = DateTimeOffset.UtcNow.AddMinutes(5)
        }));
        using var engine = f.Create();
        await engine.InitializeAsync(default);
        await f.Entry(engine);
        Assert.False(engine.IsReady);
        Assert.Equal(0, f.Exchange.LeverageLimitReads);
        Assert.Equal(0, f.Exchange.LeverageWrites);
        Assert.Empty(f.Exchange.Writes);
    }

    [Fact]
    public async Task ExchangeClosureCleansOnlyKnownOrdersAndPersistsLossCircuit()
    {
        using var f = new Fixture(TradeDirection.Long);
        f.Risk.MaximumConsecutiveLosses = 1;
        using var engine = f.Create();
        await engine.InitializeAsync(default);
        f.Exchange.Quantity = 0m;
        f.Exchange.Income = new(-10.1m, -0.05m, -0.1m, 3, true, true);
        await engine.ProcessAsync(f.Bar(0, 90m), default);
        var state = f.State();
        Assert.Empty(state.Positions);
        Assert.Equal(1, state.ConsecutiveLosses);
        Assert.NotNull(state.LossCircuitUntil);
        Assert.True(state.LastExits.ContainsKey("BTC-USDT"));
        Assert.Equal(new[] { "cleanup" }, f.Exchange.Writes);
        Assert.Contains(f.Journal.Events, e => e.Kind == "closed_income_observed");
    }

    [Theory]
    [InlineData(-30, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, true)]
    public async Task DailyLossUnknownExposureAndAggregateRiskBlockEntry(int dailyLoss, bool manualPosition, bool aggregateFull)
    {
        using var f = new Fixture(TradeDirection.Long, held: false);
        f.Exchange.Account = new(1000m + dailyLoss, 1000m + dailyLoss, 1000m + dailyLoss, 1000m, dailyLoss, 0m);
        f.Exchange.ManualPosition = manualPosition;
        if (aggregateFull)
        {
            f.Risk.MaxAggregateOpenRiskPercent = 0.1m; // Budget 1 USDT vs a 10 USDT proposed stop risk.
        }
        using var engine = f.Create();
        await engine.InitializeAsync(default);
        await f.Entry(engine);
        Assert.DoesNotContain("open", f.Exchange.Writes);
    }

    [Theory]
    [InlineData("null", 1000, 1000, true, true)]
    [InlineData("[]", 1000, 1000, true, true)]
    [InlineData("null", 980, 1000, false, false)] // Unrealized loss reaches the 2% daily limit.
    [InlineData("null", 1000, 0, false, false)] // No available margin.
    [InlineData("null", 1000, 0.1, true, false)] // Positive margin, but rounded size is below the contract minimum.
    public async Task SuccessfulEmptyBingXIncomeStillRequiresLiveRiskApproval(
        string ledger, int equity, double availableMargin, bool reachesSizing, bool opens)
    {
        using var f = new Fixture(TradeDirection.Long, held: false);
        using var db = new ResearchFixture(new StrategyOptions { RsiOverbought = 101m, MaxRetestEntryDistanceAtr = 0m }, f.Risk);
        using var handler = new IncomeHttpHandler("{\"code\":0,\"data\":" + ledger + "}", equity, (decimal)availableMargin);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://exchange.invalid") };
        var accountApi = new BingXLiveExecutor(http, Options.Create(new BingXOptions { ApiKey = "fixture", ApiSecret = "synthetic" }),
            NullLogger<BingXLiveExecutor>.Instance);
        f.Exchange.AccountSource = accountApi.GetAccountSnapshotAsync;

        using var engine = f.Create(db.Governance, new Quotes());
        await engine.InitializeAsync(default);
        await f.Entry(engine);

        Assert.Equal(new[] { IncomeHttpHandler.Balance, IncomeHttpHandler.Income, IncomeHttpHandler.Commission }, handler.Requests);
        Assert.Equal(1000m, f.State().DayStartBalance);
        Assert.Equal(equity, f.State().EquityHighWater);
        Assert.Equal(reachesSizing ? 1 : 0, f.Exchange.LeverageLimitReads);
        Assert.Equal(opens ? 1 : 0, f.Exchange.LeverageWrites);
        if (opens)
        {
            Assert.Equal(new[] { "open" }, f.Exchange.Writes);
            var management = Assert.Single(f.State().Positions).Management!;
            Assert.InRange(management.InitialRiskAmount, 0.0001m, 10m);
            Assert.Equal(0.0005m, management.FeeRate);
            Assert.Contains(f.Journal.Events, entry => entry.Kind == "entry_protected");
        }
        else
        {
            Assert.Empty(f.Exchange.Writes);
            Assert.Empty(f.State().Positions);
            Assert.DoesNotContain(f.Journal.Events, entry => entry.Kind == "entry_requested");
        }
        Assert.Null(f.State().PendingOrder);
        Assert.True(engine.IsReady);
    }

    [Theory]
    [InlineData(200, 0, false)]
    [InlineData(150, 50, false)]
    [InlineData(190, 5, true)]
    public async Task LiveEntryUsesOnlyTheRemainingAccountMarginBudget(int used, int frozen, bool opens)
    {
        using var f = new Fixture(TradeDirection.Long, held: false);
        using var db = new ResearchFixture(new StrategyOptions { RsiOverbought = 101m, MaxRetestEntryDistanceAtr = 0m }, f.Risk);
        using var handler = new IncomeHttpHandler("""{"code":0,"data":null}""", margin: 1000m - used - frozen,
            usedMargin: used, frozenMargin: frozen);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://exchange.invalid") };
        var accountApi = new BingXLiveExecutor(http, Options.Create(new BingXOptions { ApiKey = "fixture", ApiSecret = "synthetic" }),
            NullLogger<BingXLiveExecutor>.Instance);
        f.Exchange.AccountSource = accountApi.GetAccountSnapshotAsync;
        using var engine = f.Create(db.Governance, new Quotes(), marginUsagePercent: 20m);
        await engine.InitializeAsync(default);
        await f.Entry(engine);

        if (opens)
        {
            Assert.Equal(new[] { "open" }, f.Exchange.Writes);
            Assert.InRange(f.Exchange.EntryPrice * f.Exchange.Quantity * (1m / 5m + 2m * 0.0005m), 0.01m, 200m - used - frozen);
        }
        else
        {
            Assert.Empty(f.Exchange.Writes);
            Assert.Equal(0, f.Exchange.LeverageLimitReads);
            Assert.Equal(0, f.Exchange.LeverageWrites);
        }
        Assert.Null(f.State().PendingOrder);
    }

    [Fact]
    public async Task OperatorHaltSetDuringFinalValidationBlocksBothLeverageAndEntryWrites()
    {
        using var f = new Fixture(TradeDirection.Long, held: false);
        using var db = new ResearchFixture(new StrategyOptions { RsiOverbought = 101m, MaxRetestEntryDistanceAtr = 0m }, f.Risk);
        using var engine = f.Create(db.Governance, new Quotes());
        await engine.InitializeAsync(default);
        f.Exchange.PositionModeSource = token => db.Governance.Store.SetHaltAsync(TradingMode.LIVE, true, "test halt during validation", token);
        await f.Entry(engine);

        Assert.True(await db.Governance.Store.IsHaltedAsync(TradingMode.LIVE, default));
        Assert.Equal(1, f.Exchange.LeverageLimitReads);
        Assert.Equal(0, f.Exchange.LeverageWrites);
        Assert.Empty(f.Exchange.Writes);
        Assert.DoesNotContain(f.Journal.Events, entry => entry.Kind == "entry_requested");
        Assert.Null(f.State().PendingOrder);
    }

    [Fact]
    public async Task OperatorHaltPreservesThePinnedManagementOfAnExistingPosition()
    {
        using var f = new Fixture(TradeDirection.Long);
        using var db = new ResearchFixture(new StrategyOptions(), f.Risk);
        await db.Governance.InitializeAsync(TradingMode.LIVE, default);
        var policy = await db.Governance.ActiveAsync(TradingMode.LIVE, default);
        await db.Governance.Store.SetHaltAsync(TradingMode.LIVE, true, "test operator halt", default);
        var state = f.State();
        var position = Assert.Single(state.Positions);
        var management = position.Management!;
        var context = new TradingContext(policy.Snapshot, "signal-fixture", position.TradeId!, 5m, 1000m, "fixture");
        state.Positions[0] = position with { Management = management with { Metrics = management.Metrics with { Context = context } } };
        state.TradeContexts[context.TradeId] = context;
        File.WriteAllText(f.PathName, JsonSerializer.Serialize(state));

        using var engine = f.Create(db.Governance, new Quotes());
        await engine.InitializeAsync(default);
        await engine.ProcessAsync(f.Bar(0, 112m), default);

        Assert.Equal(new[] { "replace-stop", "close", "replace-stop", "replace-take" }, f.Exchange.Writes);
        Assert.Equal(0.5m, Assert.Single(f.State().Positions).Quantity);
        Assert.Equal(0.5m, f.Exchange.ProtectedQuantity);
        Assert.Equal(0, f.Exchange.LeverageWrites);
        Assert.Null(f.State().PendingOrder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovedAdaptiveCurrentStillRequiresTheLiveFeatureFlag(bool allowAdaptiveCurrent)
    {
        using var f = new Fixture(TradeDirection.Long, held: false);
        using var db = new ResearchFixture(new StrategyOptions { UseMultiTimeframeAnalysis = true }, f.Risk);
        await db.Governance.InitializeAsync(TradingMode.LIVE, default);
        var legacy = await db.Governance.ActiveAsync(TradingMode.LIVE, default);
        var configuration = JsonSerializer.Deserialize<TradingConfiguration>(legacy.Snapshot.ConfigurationJson)!;
        configuration.Strategy.UseAdaptiveCurrentEntries = true;
        var json = JsonSerializer.Serialize(configuration);
        var candidate = legacy.Snapshot with
        {
            Version = "adaptive-entry-fixture", ConfigurationJson = json,
            ConfigurationHash = TradingPolicyRuntime.Hash(json), ApprovalStatus = "CANDIDATE"
        };
        await db.Store.RegisterAsync(new(candidate, "CANDIDATE", PreviousVersion: legacy.Snapshot.Version), false, default);
        await db.Governance.ValidateCandidateAsync(TradingMode.LIVE, candidate.Version,
            TradingGovernanceTests.Evidence(candidate.ConfigurationHash), default);
        await db.Store.PromoteAsync(TradingMode.LIVE, candidate.Version, legacy.Snapshot.Version, "synthetic reviewer", "APPROVE", default);

        using var engine = f.Create(db.Governance, new Quotes(), allowAdaptiveCurrent: allowAdaptiveCurrent);
        await engine.InitializeAsync(default);
        await f.Entry(engine);

        var active = await db.Governance.ActiveAsync(TradingMode.LIVE, default);
        Assert.Equal(allowAdaptiveCurrent, active.Strategy.LastEvaluation is not null);
        Assert.Empty(f.Exchange.Writes);
        Assert.Equal(0, f.Exchange.LeverageWrites);
        Assert.Null(f.State().PendingOrder);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"data":null}""", typeof(InvalidOperationException))]
    [InlineData(HttpStatusCode.OK, """{"code":"0","data":null}""", typeof(InvalidOperationException))]
    [InlineData(HttpStatusCode.OK, """{"code":0}""", typeof(InvalidOperationException))]
    [InlineData(HttpStatusCode.OK, """{"code":0,"data":[null]}""", typeof(InvalidOperationException))]
    [InlineData(HttpStatusCode.OK, """{"code":100400,"data":null}""", typeof(InvalidOperationException))]
    [InlineData(HttpStatusCode.OK, """{"code":0,"data":""", typeof(JsonException))]
    [InlineData(HttpStatusCode.TooManyRequests, """{"code":0,"data":null}""", typeof(InvalidOperationException))]
    [InlineData(HttpStatusCode.BadGateway, "<html>not JSON</html>", typeof(InvalidOperationException))]
    [InlineData(HttpStatusCode.RequestTimeout, "", typeof(InvalidOperationException))]
    [InlineData(null, "", typeof(HttpRequestException))]
    public async Task FailedBingXIncomeStopsAdvancedEntryBeforeAnyWrite(HttpStatusCode? status, string body, Type failureType)
    {
        using var f = new Fixture(TradeDirection.Long, held: false);
        using var db = new ResearchFixture(new StrategyOptions { RsiOverbought = 101m, MaxRetestEntryDistanceAtr = 0m }, f.Risk);
        using var handler = new IncomeHttpHandler(body, incomeStatus: status);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://exchange.invalid") };
        var accountApi = new BingXLiveExecutor(http, Options.Create(new BingXOptions { ApiKey = "fixture", ApiSecret = "synthetic" }),
            NullLogger<BingXLiveExecutor>.Instance);
        f.Exchange.AccountSource = accountApi.GetAccountSnapshotAsync;

        using var engine = f.Create(db.Governance, new Quotes());
        await engine.InitializeAsync(default);
        var error = await Record.ExceptionAsync(() => f.Entry(engine));

        Assert.IsAssignableFrom(failureType, error);
        Assert.Equal(new[] { IncomeHttpHandler.Balance, IncomeHttpHandler.Income }, handler.Requests);
        Assert.Equal(1, f.State().ConsecutiveApiFailures);
        Assert.Equal(0, f.Exchange.LeverageLimitReads);
        Assert.Equal(0, f.Exchange.LeverageWrites);
        Assert.Empty(f.Exchange.Writes);
        Assert.DoesNotContain(f.Journal.Events, entry => entry.Kind == "entry_requested");
        Assert.Null(f.State().PendingOrder);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task EmptyBingXIncomeDoesNotConfirmReconciledPositionClosure(string ledger)
    {
        using var f = new Fixture(TradeDirection.Long);
        using var handler = new IncomeHttpHandler("{\"code\":0,\"data\":" + ledger + "}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://exchange.invalid") };
        var accountApi = new BingXLiveExecutor(http, Options.Create(new BingXOptions { ApiKey = "fixture", ApiSecret = "synthetic" }),
            NullLogger<BingXLiveExecutor>.Instance);
        f.Exchange.PositionIncomeSource = accountApi.GetIncomeAsync;
        using var engine = f.Create();
        await engine.InitializeAsync(default);
        f.Exchange.Quantity = 0;
        f.Exchange.Orders = 0;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ProcessAsync(f.Bar(0, 90m), default));

        Assert.Contains("PnL/commission income is not yet available", error.Message);
        Assert.Equal(IncomeHttpHandler.Income, Assert.Single(handler.Requests));
        Assert.Single(f.State().Positions);
        Assert.Empty(f.State().LastExits);
        Assert.DoesNotContain(f.Journal.Events, entry => entry.Kind is "closed_income_observed" or "trade_closed");
        Assert.Empty(f.Exchange.Writes);
    }

    [Fact]
    public async Task AdvancedEntryUsesConfirmedFillAndStoresManagementIdentity()
    {
        using var f = new Fixture(TradeDirection.Long, held: false);
        using var engine = f.Create();
        await engine.InitializeAsync(default);
        await f.Entry(engine);
        var position = Assert.Single(f.State().Positions);
        Assert.Equal("92233720368547758071", position.Management!.PositionId);
        Assert.Equal(112m, position.Management.EntryPrice);
        Assert.Equal(position.Quantity, position.Management.InitialQuantity);
        Assert.InRange(position.Management.InitialRiskAmount, 0.0001m, 10m);
        Assert.True(engine.IsReady);
        Assert.Null(f.State().PendingOrder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothEnginesUseApprovedPoliciesAndLiveCanRejectOnAccountRisk(bool liveDailyLoss)
    {
        using var f = new Fixture(TradeDirection.Long, held: false);
        var settings = new StrategyOptions { RsiOverbought = 101m, MaxRetestEntryDistanceAtr = 0m };
        using var db = new ResearchFixture(settings, f.Risk);
        var market = new Quotes(); var ai = new DisabledAi();
        var contracts = new InMemoryContractStore(); contracts.Upsert("BTC-USDT", new("BTC-USDT", 2, 2, 0.01m, 1m, true));
        var paper = new PaperTradingEngine(f.Candles, new BreakoutStrategy(Options.Create(settings)), new RiskManager(Options.Create(f.Risk)),
            ai, db.Trades, contracts, Options.Create(f.Risk), Options.Create(new PaperTradingOptions()), Options.Create(new OpenAIOptions()),
            Options.Create(settings), NullLogger<PaperTradingEngine>.Instance, db.Governance, market);
        await paper.InitializeAsync(default);
        // Both cases exceed the former drawdown limit. Daily loss still rejects the second case.
        File.WriteAllText(f.PathName, JsonSerializer.Serialize(new LiveTradingEngine.LiveState { EquityHighWater = 1500m }));
        using var live = f.Create(db.Governance, market); await live.InitializeAsync(default);
        if (liveDailyLoss) f.Exchange.Account = new(970m, 970m, 970m, 1000m, -30m, 0m);
        await f.Entry(live, paper);
        var paperPosition = Assert.Single(await db.Trades.LoadOpenPositionsAsync(default));
        Assert.Equal(TradingMode.PAPER, paperPosition.Metrics!.Context!.Strategy.Mode);
        Assert.Equal(0, ai.Calls);
        var paperEvents = new List<TradingJournalEvent>();
        await foreach (var e in db.Store.ReadAsync(TradingMode.PAPER, DateTimeOffset.MinValue, default)) paperEvents.Add(e);
        Assert.Contains(paperEvents, e => e.Kind == "entry_filled" && e.StrategyVersion == "mtf-v1" &&
            e.TradeId == paperPosition.Metrics.Context.TradeId);
        if (liveDailyLoss)
        {
            Assert.Empty(f.Exchange.Writes);
            Assert.Empty(f.State().Positions);
            Assert.Null(f.State().PendingOrder);
            return;
        }
        var livePosition = Assert.Single(f.State().Positions);
        Assert.Equal(TradingMode.LIVE, livePosition.Management!.Metrics.Context!.Strategy.Mode);
        var liveEvents = new List<TradingJournalEvent>();
        await foreach (var e in db.Store.ReadAsync(TradingMode.LIVE, DateTimeOffset.MinValue, default)) liveEvents.Add(e);
        Assert.Contains(liveEvents, e => e.Kind == "entry_requested" && e.StrategyVersion == "mtf-v1");
        Assert.Contains(liveEvents, e => e.Kind == "entry_filled" && e.TradeId == livePosition.TradeId);
        Assert.Contains(liveEvents, e => e.Kind == "entry_protected" && e.TradeId == livePosition.TradeId);
        f.Exchange.Quantity = 0m;
        f.Exchange.Income = new(-10.1m, -0.05m, -0.1m, 3, true, true, -9.95m);
        await live.ProcessAsync(f.Bar(1, 90m) with { Candle = f.Bar(1, 90m).Candle with { OpenTime = DateTimeOffset.UtcNow.AddSeconds(1), CloseTime = DateTimeOffset.UtcNow.AddSeconds(2) } }, default);
        var outcome = Assert.Single(await db.Governance.OutcomesAsync(TradingMode.LIVE, DateTimeOffset.MinValue, default));
        Assert.Equal(-10.1m, outcome.NetPnl); Assert.False(outcome.Reconciled);
        Assert.Empty(await db.Governance.OutcomesAsync(TradingMode.PAPER, DateTimeOffset.MinValue, default));
    }

    [Theory]
    [InlineData(true, true, 113, true, true, true)]
    [InlineData(true, true, 111, false, false, true)]
    [InlineData(false, true, 111, true, false, true)]
    [InlineData(true, false, 111, false, true, true)]
    [InlineData(true, true, 113, false, false, false)]
    public async Task MacdConfirmsTheSameEntryInBothModesAndCanBeDisabledSeparately(
        bool paperEnabled, bool liveEnabled, int retestPrice, bool opensPaper, bool opensLive, bool priorBreakout)
    {
        const string symbol = "BTC-USDT";
        using var f = new Fixture(TradeDirection.Long, held: false);
        var settings = new StrategyOptions { RsiOverbought = 101m, MaxRetestEntryDistanceAtr = 0m };
        using var db = new ResearchFixture(settings, f.Risk);
        var contracts = new InMemoryContractStore();
        contracts.Upsert(symbol, new(symbol, 2, 2, 0.01m, 1m, true));
        var risk = Options.Create(f.Risk);
        var strategyOptions = Options.Create(settings);
        var ai = new DisabledAi();
        var market = new Quotes();
        var paper = new PaperTradingEngine(f.Candles, new BreakoutStrategy(strategyOptions), new RiskManager(risk), ai,
            db.Trades, contracts, risk, Options.Create(new PaperTradingOptions
            {
                Macd = new() { Enabled = paperEnabled, Fast = 1, Slow = 2, Signal = 2 }
            }), Options.Create(new OpenAIOptions()), strategyOptions, NullLogger<PaperTradingEngine>.Instance, db.Governance, market);
        using var live = f.Create(db.Governance, market, new() { Enabled = liveEnabled, Fast = 1, Slow = 2, Signal = 2 });
        await paper.InitializeAsync(default);
        await live.InitializeAsync(default);

        var lastBars = f.Candles.Get(symbol, TimeFrame.FiveMinutes).TakeLast(6).ToArray();
        decimal[] prices = [103m, 106m, 109m, 110m, 110.7m, retestPrice];
        var open = 100m;
        for (var i = 0; i < prices.Length; i++)
        {
            var bar = lastBars[i];
            if (i == prices.Length - 1) open = 109.9m;
            f.Candles.Upsert(symbol, TimeFrame.FiveMinutes, bar with
            {
                Open = open, High = prices[i] + 0.1m, Low = open - 0.1m, Close = prices[i], Volume = i == 4 ? 200m : 100m
            });
            open = prices[i];
        }

        var bars = f.Candles.Get(symbol, TimeFrame.FiveMinutes);
        var breakout = bars[^2];
        var retest = bars[^1];
        foreach (var frame in new[] { TimeFrame.ThirtyMinutes, TimeFrame.OneHour })
        {
            var minutes = frame == TimeFrame.ThirtyMinutes ? 30 : 60;
            for (var i = 0; i < 60; i++)
            {
                var time = breakout.CloseTime.AddMinutes((i - 60) * minutes);
                var close = 100m + i;
                f.Candles.Upsert(symbol, frame, new(time, time.AddMinutes(minutes), close - 0.9m, close + 0.1m, close - 1m, close, 100m, true));
            }
        }

        // The previous 5m breakout is older than Live's 2m limit. Prime its strategy as a prior scan would.
        if (priorBreakout)
        {
            foreach (var mode in new[] { TradingMode.PAPER, TradingMode.LIVE })
            {
                var policy = await db.Governance.ActiveAsync(mode, default);
                Assert.Null(policy.Strategy.Evaluate(symbol, bars.SkipLast(1).ToArray(),
                    f.Candles.Get(symbol, TimeFrame.ThirtyMinutes), f.Candles.Get(symbol, TimeFrame.OneHour)));
            }
        }
        f.Candles.Upsert(symbol, TimeFrame.FiveMinutes, new(retest.CloseTime, retest.CloseTime.AddMinutes(5),
            retestPrice, 200m, retestPrice, 200m, 100m, false));
        f.Exchange.Mark = retestPrice;
        var update = new MarketUpdate(symbol, TimeFrame.FiveMinutes, retest);
        await live.ProcessAsync(update, default);
        await paper.ProcessAsync(update, default);
        await live.ProcessAsync(update, default);
        await paper.ProcessAsync(update, default);

        var paperPositions = await db.Trades.LoadOpenPositionsAsync(default);
        Assert.Equal(opensPaper ? 1 : 0, paperPositions.Count);
        Assert.Equal(opensLive ? 1 : 0, f.Exchange.Writes.Count(write => write == "open"));
        Assert.Equal(0, ai.Calls);
        if (opensPaper && opensLive)
        {
            var paperPosition = Assert.Single(paperPositions);
            var paperMacd = paperPosition.Metrics!.Macd;
            var liveRequest = Assert.Single(f.Journal.Events.Where(e => e.Kind == "entry_requested"));
            using var json = JsonDocument.Parse(liveRequest.PayloadJson);
            var liveMacd = json.RootElement.GetProperty("Signal").GetProperty("Macd").Deserialize<MacdEntryDecision>();
            Assert.NotNull(paperMacd);
            Assert.Equal(paperMacd, liveMacd);
            Assert.Equal(retest.CloseTime, liveMacd!.CandleCloseTime);
            Assert.True(liveMacd.PreviousHistogram <= 0m && liveMacd.Histogram > 0m);

            // The next bar hits the existing stop even though it cannot confirm another long entry.
            var stop = paperPosition.StopLoss;
            await paper.ProcessAsync(new(symbol, TimeFrame.FiveMinutes,
                new(retest.CloseTime, retest.CloseTime.AddMinutes(5), retest.Close, retest.Close, stop, stop, 100m, true)), default);
            var closed = new List<StoredPaperTrade>();
            await foreach (var trade in db.Trades.ReadClosedTradesAsync(default)) closed.Add(trade);
            Assert.Equal("SL", Assert.Single(closed).Trade.ExitReason);
            Assert.Equal(paperMacd, closed[0].Trade.Metrics!.Macd);
        }
    }

    [Fact]
    public async Task UnifiedJournalFailureBlocksLiveEntryBeforeExchangeWrite()
    {
        using var f = new Fixture(TradeDirection.Long, held: false);
        using var db = new ResearchFixture(new StrategyOptions { RsiOverbought = 101m, MaxRetestEntryDistanceAtr = 0m }, f.Risk);
        using var live = f.Create(db.Governance, new Quotes()); await live.InitializeAsync(default);
        await using (var connection = new SqliteConnection(db.Connection))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_live_entry BEFORE INSERT ON trading_journal WHEN NEW.kind='entry_requested' BEGIN SELECT RAISE(ABORT,'injected journal failure'); END";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => f.Entry(live));
        Assert.DoesNotContain("open", f.Exchange.Writes);
        Assert.NotNull(f.State().PendingOrder); Assert.False(live.IsReady);
    }

    [Fact]
    public async Task AnalyticsBoundsIncomeToTradeAndReconcilesLateFeeWithoutDoubleCounting()
    {
        using var f = new Fixture(TradeDirection.Long, held: false);
        using var db = new ResearchFixture();
        await db.Governance.InitializeAsync(TradingMode.LIVE, default);
        var active = await db.Governance.ActiveAsync(TradingMode.LIVE, default);
        var row = TradingGovernanceTests.Outcome(active.Snapshot, "late-fee", 10m) with { Reconciled = false };
        await db.Store.AppendAsync(TradingGovernance.OutcomeEvent(row), default);
        f.Exchange.Income = new(10m, -0.1m, -1m, 3, true, true, 11.1m);
        var worker = new TradingAnalyticsWorker(db.Governance, f.Exchange, Options.Create(new AdaptiveOptions()),
            Options.Create(new LiveOptions { Enabled = true }), NullLogger<TradingAnalyticsWorker>.Instance);
        await worker.RunCycleAsync(default);
        Assert.True(Assert.Single(await db.Governance.OutcomesAsync(TradingMode.LIVE, DateTimeOffset.MinValue, default)).Reconciled);
        f.Exchange.Income = new(9m, -0.1m, -2m, 4, true, true, 11.1m);
        await worker.RunCycleAsync(default);
        Assert.False(Assert.Single(await db.Governance.OutcomesAsync(TradingMode.LIVE, DateTimeOffset.MinValue, default)).Reconciled);
        Assert.Equal(0, (await db.Governance.PerformanceAsync(TradingMode.LIVE, default))[0].TradeCount);
        await worker.RunCycleAsync(default);
        var report = (await db.Governance.PerformanceAsync(TradingMode.LIVE, default))[0];
        Assert.Equal(1, report.TradeCount); Assert.Equal(9m, report.NetPnl); Assert.Equal(2m, report.Fees);
        Assert.Equal(3, f.Exchange.IncomeWindows.Count);
        Assert.All(f.Exchange.IncomeWindows, window => { Assert.Equal(row.OpenedAt, window.From); Assert.Equal(row.ClosedAt, window.Until); });
        Assert.Empty(f.Exchange.Writes);
    }

    [Fact]
    public async Task ConfirmedMissingStopCanEmergencyCloseEvenWhenDatabaseFails()
    {
        using var f = new Fixture(TradeDirection.Long);
        f.Exchange.Failure = "stop-rejected";
        f.Exchange.OnStopFailure = () => f.Journal.Fail = true;
        using var engine = f.Create(); await engine.InitializeAsync(default);
        await engine.ProcessAsync(f.Bar(0, 112m), default);
        Assert.Equal(0m, f.Exchange.Quantity); Assert.False(engine.IsReady);
        Assert.StartsWith("emergency-", f.State().PendingOrder);
        Assert.Contains(f.State().JournalOutbox, e => e.Kind == "emergency_close_confirmed");
        Assert.Equal(new[] { "replace-stop", "close" }, f.Exchange.Writes);
    }

    private sealed class IncomeHttpHandler(string ledger, decimal equity = 1000m, decimal margin = 1000m,
        HttpStatusCode? incomeStatus = HttpStatusCode.OK, decimal usedMargin = 0m, decimal frozenMargin = 0m) : HttpMessageHandler
    {
        public const string Balance = "/openApi/swap/v3/user/balance";
        public const string Income = "/openApi/swap/v2/user/income";
        public const string Commission = "/openApi/swap/v2/user/commissionRate";
        public readonly List<string> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (path == Income && incomeStatus is null)
                throw new HttpRequestException("Synthetic transport failure before receiving the income response.");
            var content = path switch
            {
                Balance => JsonSerializer.Serialize(new { code = 0, data = new[] { new { asset = "USDT", balance = 1000m, equity,
                    availableMargin = margin, usedMargin, frozenMargin } } }),
                Income => ledger,
                Commission => """{"code":0,"data":{"commission":{"takerCommissionRate":"0.0005"}}}""",
                _ => throw new ArgumentException($"Unexpected fixture path: {path}")
            };
            return Task.FromResult(new HttpResponseMessage(path == Income ? incomeStatus!.Value : HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }

    private sealed class Quotes : IBingXMarketClient
    {
        public Task<decimal?> GetSpreadBasisPointsAsync(string symbol, CancellationToken token) => Task.FromResult<decimal?>(1m);
        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, TimeFrame frame, int limit, CancellationToken token) => throw new NotSupportedException();
        public Task<ContractInfo?> GetContractAsync(string symbol, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class DisabledAi : IOpenAiAnalyzer
    {
        public bool Enabled => false;
        public int Calls;
        public Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisContext context, CancellationToken token) { Calls++; throw new Exception("AI must remain unused"); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "live-managed-" + Guid.NewGuid().ToString("N"));
        private readonly DateTimeOffset _time = DateTimeOffset.UtcNow.AddSeconds(-40);
        private readonly TradeDirection _direction;
        public readonly Exchange Exchange = new();
        public readonly Journal Journal = new();
        public readonly InMemoryCandleStore Candles = new();
        public readonly RiskOptions Risk = new() { UseDynamicPositionManagement = true, RiskPerTradePercent = 1m,
            TakeProfitRiskMultiple = 3m, FeeRate = 0m, TargetProfitUsdt = 0m, ExitCooldownBars = 6, MaximumConsecutiveLosses = 3 };
        public string PathName => Path.Combine(_directory, "state.json");
        public Fixture(TradeDirection direction, bool held = true)
        {
            Directory.CreateDirectory(_directory);
            _direction = direction;
            Exchange.Direction = direction;
            Exchange.Quantity = held ? 1m : 0m;
            Exchange.Orders = held ? 2 : 0;
            Exchange.Stop = direction == TradeDirection.Long ? 90m : 110m;
            Exchange.Take = direction == TradeDirection.Long ? 130m : 70m;
            if (held)
            {
                var management = new LivePositionManagement(Exchange.PositionId, 100m, 1m, Exchange.Stop, 10m,
                    Exchange.StopId, Exchange.TakeId, new PositionMetrics());
                File.WriteAllText(PathName, JsonSerializer.Serialize(new LiveTradingEngine.LiveState { Positions = [
                    new("BTC-USDT", direction, 1m, Exchange.Stop, Exchange.Take, _time.AddHours(-1), "trade-test", Management: management) ] }));
            }
            for (var i = 70; i > 0; i--)
                Candles.Upsert("BTC-USDT", TimeFrame.FiveMinutes, new(_time.AddMinutes(-i * 5), _time.AddMinutes((-i + 1) * 5), 100m, 101m, 99m, 100m, 100m, true));
        }
        public LiveTradingEngine Create(TradingGovernance? governance = null, IBingXMarketClient? market = null, MacdOptions? macd = null,
            decimal marginUsagePercent = 100m, bool allowAdaptiveCurrent = false)
        {
            var contracts = new InMemoryContractStore();
            contracts.Upsert("BTC-USDT", new("BTC-USDT", 2, 2, 0.01m, 1m, true));
            return new(Candles, Options.Create(new StrategyOptions { RsiOverbought = 101m, MaxRetestEntryDistanceAtr = 0m }),
                Options.Create(Risk), Options.Create(new LiveOptions { Enabled = true, AllowAdaptiveCurrentEntries = allowAdaptiveCurrent,
                    StatePath = PathName, TakeProfitRiskMultiple = 3m,
                    MaxMarginUsagePercent = marginUsagePercent, MaximumConsecutiveApiFailures = 3, Macd = macd ?? new() }),
                Exchange, contracts, NullLogger<LiveTradingEngine>.Instance, Journal, governance, market);
        }
        public LiveTradingEngine.LiveState State() => JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(File.ReadAllText(PathName))!;
        public MarketUpdate Bar(int index, decimal longPrice)
        {
            var price = _direction == TradeDirection.Long ? longPrice : 200m - longPrice;
            Exchange.Mark = price;
            var time = _time.AddSeconds(index * 5);
            var candle = new Candle(time.AddMinutes(-5), time, price, price + 1m, price - 1m, price, 100m, true);
            Candles.Upsert("BTC-USDT", TimeFrame.FiveMinutes, candle);
            return new("BTC-USDT", TimeFrame.FiveMinutes, candle);
        }
        public async Task Entry(LiveTradingEngine engine, PaperTradingEngine? paper = null)
        {
            var end = DateTimeOffset.UtcNow.AddMinutes(-1);
            var close = 100m;
            for (var i = 0; i < 80; i++)
            {
                var open = close;
                close += i % 3 == 2 ? -0.6m : 0.5m;
                var time = end.AddMinutes(-5 * (80 - i));
                Candles.Upsert("BTC-USDT", TimeFrame.FiveMinutes, i == 79
                    ? new(end.AddMinutes(-1), end, 129m, 131m, 129m, 130m, 200m, true)
                    : new(time, time.AddMinutes(5), open, Math.Max(open, close) + 0.5m, Math.Min(open, close) - 0.5m, close, 100m, true));
                foreach (var frame in new[] { TimeFrame.ThirtyMinutes, TimeFrame.OneHour })
                {
                    var trend = 100m + i * 2m;
                    var interval = frame == TimeFrame.ThirtyMinutes ? 30 : 60;
                    var trendTime = end.AddMinutes(-interval * (80 - i));
                    Candles.Upsert("BTC-USDT", frame, new(trendTime, trendTime.AddMinutes(interval), trend - 2m, trend + 0.5m, trend - 0.5m, trend, 100m, true));
                }
            }
            Exchange.Mark = 112m;
            await engine.ProcessAsync(new("BTC-USDT", TimeFrame.FiveMinutes, Candles.Get("BTC-USDT", TimeFrame.FiveMinutes)[^1]), default);
            if (paper is not null) await paper.ProcessAsync(new("BTC-USDT", TimeFrame.FiveMinutes, Candles.Get("BTC-USDT", TimeFrame.FiveMinutes)[^1]), default);
            var retest = new Candle(end, DateTimeOffset.UtcNow, 110.1m, 112.5m, 110m, 112m, 100m, true);
            Candles.Upsert("BTC-USDT", TimeFrame.FiveMinutes, retest);
            await engine.ProcessAsync(new("BTC-USDT", TimeFrame.FiveMinutes, retest), default);
            if (paper is not null) await paper.ProcessAsync(new("BTC-USDT", TimeFrame.FiveMinutes, retest), default);
        }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }

    private sealed class Journal : ILiveTradeJournal
    {
        public readonly List<LiveTradeEvent> Events = [];
        public bool Fail;
        public Task InitializeJournalAsync(CancellationToken token) => Task.CompletedTask;
        public Task RecordLiveEventAsync(LiveTradeEvent entry, CancellationToken token)
        { if (Fail) throw new InvalidOperationException("Synthetic DB unavailable"); Events.Add(entry); return Task.CompletedTask; }
        public async IAsyncEnumerable<LiveTradeEvent> ReadLiveEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        { await Task.CompletedTask; foreach (var e in Events) yield return e; }
    }

    private sealed class Exchange : ILiveExecutor
    {
        public string PositionId = "92233720368547758071";
        public TradeDirection Direction;
        public decimal Quantity, Stop, Take, Mark = 100m, EntryPrice = 100m, ProtectedQuantity = 1m;
        public string StopId = "sl-original", TakeId = "tp-original";
        public int Orders, ManagedQueries;
        public bool ManualPosition;
        public string? Failure;
        public Action? OnStopFailure;
        public readonly List<string> Writes = [];
        public int LeverageLimitReads, LeverageWrites;
        public LiveAccountSnapshot Account = new(1000m, 1000m, 1000m, 1000m, 0m, 0m);
        public LiveIncomeSnapshot Income = new(0m, 0m, 0m, 2, true, true);
        public Func<CancellationToken, Task<LiveAccountSnapshot>>? AccountSource;
        public Func<CancellationToken, Task>? PositionModeSource;
        public Func<string, DateTimeOffset, CancellationToken, Task<LiveIncomeSnapshot>>? PositionIncomeSource;
        public readonly List<(DateTimeOffset From, DateTimeOffset Until)> IncomeWindows = [];
        private readonly Dictionary<string, LiveOrderFill> _fills = new();
        public Task<LiveAccountSnapshot> GetAccountSnapshotAsync(CancellationToken token) => AccountSource?.Invoke(token) ?? Task.FromResult(Account);
        public Task<LiveIncomeSnapshot> GetIncomeAsync(string symbol, DateTimeOffset since, CancellationToken token)
            => PositionIncomeSource?.Invoke(symbol, since, token) ?? Task.FromResult(Income);
        public Task<LiveIncomeSnapshot> GetIncomeAsync(string symbol, DateTimeOffset since, DateTimeOffset until, CancellationToken token)
        { IncomeWindows.Add((since, until)); return Task.FromResult(Income); }
        public Task<decimal> GetMarkPriceAsync(string symbol, CancellationToken token) => Task.FromResult(Mark);
        public Task<LivePositionSnapshot> GetPositionAsync(string symbol, CancellationToken token) => Task.FromResult(new LivePositionSnapshot(PositionId, symbol, Direction, Quantity, EntryPrice));
        public Task ValidatePositionModeAsync(CancellationToken token) => PositionModeSource?.Invoke(token) ?? Task.CompletedTask;
        public Task<int> GetOpenPositionCountAsync(CancellationToken token) => Task.FromResult((Quantity > 0m ? 1 : 0) + (ManualPosition ? 1 : 0));
        public Task<int> GetOpenOrderCountAsync(CancellationToken token, string? symbol = null) => Task.FromResult(Orders);
        public Task<bool> HasOpenPositionAsync(string symbol, CancellationToken token) => Task.FromResult(Quantity > 0m);
        public Task<decimal> GetAvailableBalanceUsdtAsync(CancellationToken token) => Task.FromResult(Account.AvailableMargin);
        public Task<decimal> GetMaxLeverageAsync(string symbol, TradeDirection direction, CancellationToken token)
        { LeverageLimitReads++; return Task.FromResult(5m); }
        public Task SetLeverageAsync(string symbol, TradeDirection direction, decimal leverage, CancellationToken token)
        { LeverageWrites++; return Task.CompletedTask; }
        public Task ValidateProtectionAsync(string symbol, TradeDirection direction, decimal quantity, CancellationToken token) => Task.CompletedTask;
        public Task<string> GetPositionHistoryAsync(string symbol, DateTimeOffset since, CancellationToken token) =>
            Task.FromResult(JsonSerializer.Serialize(new { Positions = new[] { new { positionId = PositionId } } }));
        public Task<LiveManagedPosition> GetManagedPositionAsync(string symbol, TradeDirection direction, decimal quantity, decimal stopLoss,
            decimal takeProfit, string? stopOrderId, string? takeOrderId, CancellationToken token, bool allowOversizedProtection = false)
        {
            ManagedQueries++;
            if (Failure == "query" || direction != Direction || quantity != Quantity || stopLoss != Stop || takeProfit != Take ||
                (stopOrderId is not null && stopOrderId != StopId) || (takeOrderId is not null && takeOrderId != TakeId))
                throw new InvalidOperationException("Synthetic snapshot mismatch");
            return Task.FromResult(new LiveManagedPosition(PositionId, symbol, direction, quantity, EntryPrice, StopId, TakeId, true));
        }
        public Task<LiveOrderReceipt> OpenMarketPositionAsync(string symbol, TradeDirection direction, decimal quantity,
            decimal stopLoss, decimal takeProfit, string clientOrderId, CancellationToken token)
        {
            Writes.Add("open"); Direction = direction; Quantity = quantity; Stop = stopLoss; Take = takeProfit; Orders = 2; EntryPrice = Mark;
            _fills[clientOrderId] = new("entry-order", clientOrderId, symbol, direction == TradeDirection.Long ? "BUY" : "SELL",
                direction == TradeDirection.Long ? "LONG" : "SHORT", "FILLED", quantity, Mark);
            return Task.FromResult(new LiveOrderReceipt(Mark, "{}"));
        }
        public Task<LiveOrderReceipt> CloseMarketPositionAsync(string symbol, TradeDirection direction, decimal quantity, CancellationToken token) => throw new NotSupportedException();
        public Task<LiveOrderReceipt> CloseMarketPositionAsync(string symbol, TradeDirection direction, decimal quantity, string clientOrderId, CancellationToken token)
        {
            Writes.Add("close");
            var actual = Failure == "partial-fill" ? quantity / 2m : quantity;
            Quantity -= actual;
            _fills[clientOrderId] = new("partial-order", clientOrderId, symbol, direction == TradeDirection.Long ? "SELL" : "BUY",
                direction == TradeDirection.Long ? "LONG" : "SHORT", Failure == "partial-fill" ? "PARTIALLY_FILLED" : "FILLED", actual, Mark);
            if (Failure == "position-changed") PositionId = "another-position";
            if (Failure == "close-timeout") throw new TimeoutException();
            return Task.FromResult(new LiveOrderReceipt(Mark, "{}"));
        }
        public Task<LiveOrderFill> GetOrderFillAsync(string symbol, string clientOrderId, CancellationToken token) => Task.FromResult(_fills[clientOrderId]);
        public Task<string> ReplaceProtectionAsync(string symbol, TradeDirection direction, string orderId, string orderType, decimal quantity, decimal triggerPrice, CancellationToken token)
        {
            if (orderType == "STOP_MARKET")
            {
                Writes.Add("replace-stop"); Stop = triggerPrice; StopId += "-new"; ProtectedQuantity = quantity;
                if (Failure == "stop-rejected") { OnStopFailure?.Invoke(); throw new LiveProtectionReplacementException("Synthetic confirmed missing stop"); }
                if (Failure == "stop-timeout") throw new TimeoutException();
                return Task.FromResult(StopId);
            }
            Writes.Add("replace-take");
            if (Failure == "take-rejected") throw new InvalidOperationException("Synthetic take replacement rejected");
            Take = triggerPrice; TakeId += "-new";
            return Task.FromResult(TakeId);
        }
        public Task CancelKnownProtectionAsync(string symbol, IReadOnlyCollection<string> orderIds, CancellationToken token)
        {
            Assert.Contains(StopId, orderIds); Assert.Contains(TakeId, orderIds);
            Writes.Add("cleanup"); Orders = 0; return Task.CompletedTask;
        }
    }
}
