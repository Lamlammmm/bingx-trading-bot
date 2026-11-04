using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.BingX;

// Instantiate only market data: no live executor, trading engine, credentials or persistence.
using var http = new HttpClient { BaseAddress = new Uri(args[0].TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(3) };
var rest = new BingXRestClient(http, NullLogger<BingXRestClient>.Instance);
var stream = new HealthyStream();
var updates = Channel.CreateUnbounded<MarketUpdate>();
var candles = new InMemoryCandleStore();
using var worker = new BingXMarketDataWorker(rest, stream, candles, new InMemoryContractStore(),
    updates, Options.Create(new BingXOptions { Symbols = ["BTC-USDT", "ETH-USDT"], SymbolRetrySeconds = 1 }),
    NullLogger<BingXMarketDataWorker>.Instance);
try
{
    await worker.StartAsync(default);
    await stream.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(15));
}
finally
{
    await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
}
using var stats = await http.GetFromJsonAsync<JsonDocument>("__state") ?? throw new Exception("Missing simulation stats");
var calls = stats.RootElement.GetProperty("calls");
var ethLoads = calls.GetProperty("/openApi/swap/v2/quote/contracts:ETH-USDT:").GetInt32();
var btcLoads = calls.GetProperty("/openApi/swap/v2/quote/contracts:BTC-USDT:").GetInt32();
var failures = stats.RootElement.GetProperty("failures").GetInt32();
var ethUpdates = 0;
while (updates.Reader.TryRead(out var update))
    if (update.Symbol == "ETH-USDT") ethUpdates++;
if (stream.Connections != 1 || ethLoads != 1 || btcLoads != 3 || failures != 2 || ethUpdates < 2)
    throw new Exception($"Recovery failed: connections={stream.Connections}, ETH loads={ethLoads}, BTC loads={btcLoads}, failures={failures}, ETH updates={ethUpdates}");
foreach (var symbol in new[] { "BTC-USDT", "ETH-USDT" })
{
    foreach (var (interval, frame, minutes) in new[] { ("5m", TimeFrame.FiveMinutes, 5),
        ("30m", TimeFrame.ThirtyMinutes, 30), ("1h", TimeFrame.OneHour, 60) })
    {
        var loads = calls.GetProperty($"/openApi/swap/v3/quote/klines:{symbol}:{interval}").GetInt32();
        if (loads != (symbol == "BTC-USDT" && interval == "5m" ? 3 : 1))
            throw new Exception($"Unexpected history loads: {symbol} {interval} = {loads}");
        var history = candles.Get(symbol, frame);
        if (history.Count < 2 || history.Any(c => !c.IsClosed || c.CloseTime - c.OpenTime != TimeSpan.FromMinutes(minutes)))
            throw new Exception($"Invalid closed candle history: {symbol} {interval}");
    }
    if (calls.TryGetProperty($"/openApi/swap/v3/quote/klines:{symbol}:15m", out _) ||
        candles.Get(symbol, TimeFrame.FifteenMinutes).Count != 0)
        throw new Exception($"Unexpected 15m history for {symbol}");
}
Console.WriteLine($"PASS: two HTTP-200/code-100410 failures recovered; BTC loaded {btcLoads} times, ETH once; {ethUpdates} ETH candles processed on one stream connection.");
Console.WriteLine("PASS: only native 5m/30m/1h history requested; closed candle durations match each interval; no 15m history loaded.");

sealed class HealthyStream : IBingXMarketStream
{
    public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Connections { get; private set; }

    public async Task StreamAsync(IReadOnlyList<string> symbols, ChannelReader<string> additionalSymbols,
        Func<MarketUpdate, Task> onUpdate, CancellationToken cancellationToken)
    {
        Connections++;
        if (!symbols.SequenceEqual(new[] { "ETH-USDT" })) throw new Exception("Unexpected initial subscriptions");
        var subscription = additionalSymbols.ReadAsync(cancellationToken).AsTask();
        var openTime = DateTimeOffset.UtcNow.AddHours(-12);
        while (!cancellationToken.IsCancellationRequested)
        {
            var tick = Task.Delay(100, cancellationToken);
            if (await Task.WhenAny(subscription, tick) == subscription)
            {
                if (await subscription != "BTC-USDT") throw new Exception("Unexpected recovered symbol");
                Recovered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            await tick;
            var candle = new Candle(openTime, openTime.AddMinutes(5), 100, 102, 99, 101, 10, false);
            await onUpdate(new MarketUpdate("ETH-USDT", TimeFrame.FiveMinutes, candle));
            openTime = candle.CloseTime;
        }
    }
}
