using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.BingX;

var uri = new Uri(args.Length > 0 ? args[0] : "http://127.0.0.1:4918/");
if (!uri.IsLoopback || uri.Scheme != "http") throw new InvalidOperationException("This smoke accepts HTTP loopback only.");
if (args.Contains("--management")) { await LiveManagementSmoke.RunAsync(uri); return; }
using var control = new HttpClient { BaseAddress = uri };
control.DefaultRequestHeaders.Add("x-mock-slug", "bingx");
if (args.Contains("--empty-income"))
{
    using var reset = await control.PostAsync("__reset?scenario=empty-income", null);
    reset.EnsureSuccessStatusCode();
    var executor = new BingXLiveExecutor(control, Options.Create(new BingXOptions
    {
        ApiKey = "synthetic-key", ApiSecret = "synthetic-secret"
    }), NullLogger<BingXLiveExecutor>.Instance);
    var snapshot = await executor.GetAccountSnapshotAsync(default);
    if (snapshot.DayStartBalance != 1000m || snapshot.NetTradingIncomeToday != 0m ||
        snapshot.ExternalIncomeToday != 0m || snapshot.Equity != 1012m || snapshot.TakerFeeRate != 0.0005m ||
        snapshot.AvailableMargin != 992m || snapshot.UsedMargin != 20m || snapshot.FrozenMargin != 0m)
        throw new InvalidOperationException("Empty BingX income response produced an incorrect risk snapshot.");
    Console.WriteLine("PASS HTTP empty-income: confirmed zero cash flows, preserved equity, margin and commission");
    return;
}
foreach (var scenario in new[] { "success", "rate-limit", "business-error", "html-error", "timeout-after-write" })
{
    (await control.PostAsync($"__scenario?name={scenario}", null)).EnsureSuccessStatusCode();
    using var http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromMilliseconds(500) };
    http.DefaultRequestHeaders.Add("x-mock-slug", "bingx");
    var executor = new BingXLiveExecutor(http, Options.Create(new BingXOptions { ApiKey = "synthetic-key", ApiSecret = "synthetic-secret" }), NullLogger<BingXLiveExecutor>.Instance);
    Exception? error = null;
    try { await executor.OpenMarketPositionAsync("BTC-USDT", TradeDirection.Long, 1m, 90m, 120m, "smoke-local", default); }
    catch (Exception exception) { error = exception; }
    if ((scenario == "success") != (error is null)) throw new Exception($"Unexpected outcome: {scenario}: {error?.Message}");
    using var stats = await control.GetFromJsonAsync<JsonDocument>("__state") ?? throw new Exception("No stats");
    var writes = stats.RootElement.GetProperty("writes").GetInt32();
    var calls = stats.RootElement.GetProperty("calls").GetInt32();
    if (calls != 1 || writes != (scenario is "success" or "timeout-after-write" ? 1 : 0))
        throw new Exception($"Duplicate/incorrect write count: {scenario} calls={calls} writes={writes}");
    Console.WriteLine($"PASS {scenario}: requests={calls}, writes={writes}, surfacedError={error?.GetType().Name ?? "none"}");
}
using (var http = new HttpClient { BaseAddress = uri })
{
    http.DefaultRequestHeaders.Add("x-mock-slug", "bingx");
    var executor = new BingXLiveExecutor(http, Options.Create(new BingXOptions { ApiKey = "synthetic-key", ApiSecret = "synthetic-secret" }), NullLogger<BingXLiveExecutor>.Instance);
    try { await executor.ValidateProtectionAsync("BTC-USDT", TradeDirection.Long, 1m, default); }
    catch (InvalidOperationException) { Console.WriteLine("PASS partial quantity: protection validation rejected 0.5 when 1 was requested."); return; }
    throw new Exception("Partial fill incorrectly accepted as fully protected.");
}
