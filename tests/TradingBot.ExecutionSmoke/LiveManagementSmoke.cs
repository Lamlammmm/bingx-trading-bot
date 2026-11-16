using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure;
using TradingBot.Worker.Infrastructure.BingX;
using TradingBot.Worker.Infrastructure.Live;

internal static class LiveManagementSmoke
{
    public static async Task RunAsync(Uri uri)
    {
        using var control = new HttpClient { BaseAddress = uri };
        control.DefaultRequestHeaders.Add("x-mock-slug", "bingx");
        foreach (var scenario in new[] { "success-long", "success-short", "close-timeout", "partial-fill", "stop-timeout", "take-rejected", "stop-rejected", "unknown-order" })
        {
            var direction = scenario == "success-short" ? TradeDirection.Short : TradeDirection.Long;
            var side = direction == TradeDirection.Long ? "LONG" : "SHORT";
            (await control.PostAsync($"__reset?scenario={(scenario.StartsWith("success") ? "success" : scenario)}&side={side}", null)).EnsureSuccessStatusCode();
            var directory = Path.Combine(Path.GetTempPath(), "live-http-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using var http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromMilliseconds(700) };
                http.DefaultRequestHeaders.Add("x-mock-slug", "bingx");
                var exchange = new BingXLiveExecutor(http, Options.Create(new BingXOptions { ApiKey = "synthetic-key", ApiSecret = "synthetic-secret" }), NullLogger<BingXLiveExecutor>.Instance);
                var account = await exchange.GetAccountSnapshotAsync(default);
                if (account.WalletBalance != 1019.7m || account.DayStartBalance != 1000m || account.NetTradingIncomeToday != -0.3m || account.ExternalIncomeToday != 20m)
                    throw new Exception("Account cash-flow attribution failed");
                var statePath = Path.Combine(directory, "state.json");
                var now = DateTimeOffset.UtcNow.AddSeconds(-30);
                var stop = direction == TradeDirection.Long ? 90m : 110m;
                var take = direction == TradeDirection.Long ? 130m : 70m;
                var management = new LivePositionManagement("92233720368547758071", 100m, 1m, stop, 10m, "sl-original", "tp-original", new PositionMetrics());
                await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new LiveTradingEngine.LiveState { Positions = [
                    new("BTC-USDT", direction, 1m, stop, take, now.AddHours(-1), "synthetic-http", Management: management) ] }));
                var candles = new InMemoryCandleStore();
                for (var i = 70; i > 0; i--)
                    candles.Upsert("BTC-USDT", TimeFrame.FiveMinutes, new(now.AddMinutes(-5*i), now.AddMinutes(-5*(i-1)), 100m, 101m, 99m, 100m, 100m, true));
                var contracts = new InMemoryContractStore(); contracts.Upsert("BTC-USDT", new("BTC-USDT", 2, 2, 0.01m, 1m, true));
                var risk = Options.Create(new RiskOptions { UseDynamicPositionManagement = true, TargetProfitUsdt = 0m, TakeProfitRiskMultiple = 3m });
                var options = Options.Create(new LiveOptions
                {
                    Enabled = true, StatePath = statePath, TakeProfitRiskMultiple = 3m,
                    Macd = new() { Enabled = true } // The position must still be managed with only 70 candles.
                });
                var journal = new MemoryJournal();
                var bar = new Candle(now.AddMinutes(-5), now, 100m, 113m, 87m, direction == TradeDirection.Long ? 112m : 88m, 100m, true);
                var update = new MarketUpdate("BTC-USDT", TimeFrame.FiveMinutes, bar);
                Exception? error = null;
                using (var engine = new LiveTradingEngine(candles, Options.Create(new StrategyOptions()), risk, options, exchange, contracts, NullLogger<LiveTradingEngine>.Instance, journal))
                {
                    try { await engine.InitializeAsync(default); await engine.ProcessAsync(update, default); }
                    catch (Exception e) { error = e; }
                    var success = scenario.StartsWith("success");
                    if (success && error is not null) throw new Exception($"Unexpected {scenario}: {error}");
                    if (!success && scenario != "stop-rejected" && error is null) throw new Exception($"Failure was not surfaced: {scenario}");
                    using var state = await control.GetFromJsonAsync<JsonDocument>("__state") ?? throw new Exception("Missing mock state");
                    var closes = state.RootElement.GetProperty("closes").GetInt32();
                    var saved = JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(await File.ReadAllTextAsync(statePath))!;
                    if (success)
                    {
                        if (closes != 1 || state.RootElement.GetProperty("quantity").GetDecimal() != 0.5m || saved.PendingOrder is not null)
                            throw new Exception("Confirmed partial or protection mismatch");
                    }
                    else if (scenario == "unknown-order")
                    {
                        if (closes != 0 || state.RootElement.GetProperty("deletes").GetInt32() != 0) throw new Exception("Unknown order was touched");
                    }
                    else if (saved.PendingOrder is null || engine.IsReady || closes != (scenario == "stop-timeout" ? 0 : 1))
                        throw new Exception("Uncertain operation did not halt safely");
                    if (scenario == "stop-rejected" && state.RootElement.GetProperty("quantity").GetDecimal() != 0m)
                        throw new Exception("Confirmed missing SL did not close the exposed position");
                }
                using (var restarted = new LiveTradingEngine(candles, Options.Create(new StrategyOptions()), risk, options, exchange, contracts, NullLogger<LiveTradingEngine>.Instance, journal))
                {
                    if (scenario.StartsWith("success"))
                    {
                        await restarted.InitializeAsync(default);
                        await restarted.ProcessAsync(update, default); // Persisted LastEvaluatedAt suppresses this duplicate after restart.
                        using var duplicate = await control.GetFromJsonAsync<JsonDocument>("__state");
                        if (duplicate!.RootElement.GetProperty("closes").GetInt32() != 1) throw new Exception("Restart duplicated partial close");
                        var price = direction == TradeDirection.Long ? 121m : 79m;
                        (await control.PostAsync($"__mark?price={price}", null)).EnsureSuccessStatusCode();
                        var next = bar with { OpenTime = bar.OpenTime.AddSeconds(5), CloseTime = bar.CloseTime.AddSeconds(5), Close = price };
                        candles.Upsert("BTC-USDT", TimeFrame.FiveMinutes, next);
                        await restarted.ProcessAsync(new("BTC-USDT", TimeFrame.FiveMinutes, next), default);
                        using var state = await control.GetFromJsonAsync<JsonDocument>("__state");
                        if (state!.RootElement.GetProperty("closes").GetInt32() != 2 || state.RootElement.GetProperty("quantity").GetDecimal() != 0.25m)
                            throw new Exception("Second partial failed");
                    }
                    else
                    {
                        var blocked = false;
                        try { await restarted.InitializeAsync(default); } catch (InvalidOperationException) { blocked = true; }
                        if (!blocked) throw new Exception("Unresolved state was accepted at restart");
                    }
                }
                Console.WriteLine($"PASS HTTP {scenario}: real executor + live engine, synthetic exchange only");
            }
            finally { Directory.Delete(directory, true); }
        }
    }

    private sealed class MemoryJournal : ILiveTradeJournal
    {
        public Task InitializeJournalAsync(CancellationToken token) => Task.CompletedTask;
        public Task RecordLiveEventAsync(LiveTradeEvent entry, CancellationToken token) => Task.CompletedTask;
        public async IAsyncEnumerable<LiveTradeEvent> ReadLiveEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        { await Task.CompletedTask; yield break; }
    }
}
