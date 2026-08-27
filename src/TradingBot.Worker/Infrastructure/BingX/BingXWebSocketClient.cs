using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.BingX;

public sealed class BingXWebSocketClient(IOptions<BingXOptions> options, ILogger<BingXWebSocketClient> logger) : IBingXMarketStream
{
    private readonly BingXOptions _options = options.Value;
    private DateTimeOffset _nextParseWarningAt;

    public async Task StreamAsync(IReadOnlyList<string> symbols, Func<MarketUpdate, Task> onUpdate, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_options.WebSocketUrl), cancellationToken);
        foreach (var symbol in symbols)
        {
            foreach (var interval in new[] { "15m", "1h", "4h" })
            {
                var request = JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString(), reqType = "sub", dataType = $"{symbol}@kline_{interval}" });
                await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, cancellationToken);
            }
        }

        logger.LogInformation("Connected to BingX perpetual market WebSocket for {Symbols}", string.Join(", ", symbols));
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(socket, cancellationToken);
            if (message is null) break;
            if (message.Equals("Ping", StringComparison.OrdinalIgnoreCase))
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes("Pong"), WebSocketMessageType.Text, true, cancellationToken);
                continue;
            }

            var parsedUpdates = ParseUpdates(message);
            if (parsedUpdates.Count == 0 && message.Contains("@kline_", StringComparison.Ordinal))
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= _nextParseWarningAt)
                {
                    logger.LogWarning("Received an unrecognized BingX K-line payload; suppressing repeated warnings for one minute");
                    _nextParseWarningAt = now.AddMinutes(1);
                }
            }

            foreach (var update in parsedUpdates)
            {
                if (update.TimeFrame == TimeFrame.FifteenMinutes && logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("BingX raw price update: {Symbol} {TimeFrame} open={Open} high={High} low={Low} close={Close} volume={Volume} openTime={OpenTime} closed={IsClosed}",
                        update.Symbol, update.TimeFrame, update.Candle.Open, update.Candle.High, update.Candle.Low,
                        update.Candle.Close, update.Candle.Volume, update.Candle.OpenTime, update.Candle.IsClosed);
                }
                await onUpdate(update);
            }
        }
    }

    private static async Task<string?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var bytes = message.ToArray();
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var compressed = new MemoryStream(bytes);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            gzip.CopyTo(decompressed);
            bytes = decompressed.ToArray();
        }
        return Encoding.UTF8.GetString(bytes);
    }

    internal static IReadOnlyList<MarketUpdate> ParseUpdates(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("dataType", out var dataTypeProperty)) return Array.Empty<MarketUpdate>();
            var dataType = dataTypeProperty.GetString() ?? string.Empty;
            if (!dataType.Contains("@kline_", StringComparison.Ordinal)) return Array.Empty<MarketUpdate>();
            var separatorIndex = dataType.IndexOf("@kline_", StringComparison.Ordinal);
            var symbol = dataType[..separatorIndex];
            var interval = dataType[(separatorIndex + 7)..];
            var timeFrame = interval switch
            {
                "15m" => TimeFrame.FifteenMinutes,
                "1h" => TimeFrame.OneHour,
                "4h" => TimeFrame.FourHours,
                _ => (TimeFrame?)null
            };
            if (timeFrame is null || !root.TryGetProperty("data", out var data)) return Array.Empty<MarketUpdate>();

            var updates = new List<MarketUpdate>();
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var update = ParseKline(symbol, timeFrame.Value, item);
                    if (update is not null) updates.Add(update);
                }
            }
            else
            {
                var update = ParseKline(symbol, timeFrame.Value, data);
                if (update is not null) updates.Add(update);
            }

            return updates;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Array.Empty<MarketUpdate>();
        }
    }

    private static MarketUpdate? ParseKline(string symbol, TimeFrame timeFrame, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;

        // BingX currently sends a flat array item where T is the candle open time.
        // The documented payload is an object with a nested K containing t/T.
        var isNested = data.TryGetProperty("K", out var nestedKline);
        var kline = isNested ? nestedKline : data;
        if (kline.ValueKind != JsonValueKind.Object) return null;

        var hasExplicitOpenTime = kline.TryGetProperty("t", out _);
        var openTime = hasExplicitOpenTime
            ? DateTimeOffset.FromUnixTimeMilliseconds(GetLong(kline, "t"))
            : DateTimeOffset.FromUnixTimeMilliseconds(GetLong(kline, "T"));
        var closeTime = hasExplicitOpenTime && kline.TryGetProperty("T", out _)
            ? DateTimeOffset.FromUnixTimeMilliseconds(GetLong(kline, "T"))
            : openTime.Add(GetDuration(timeFrame));
        var candle = new Candle(openTime, closeTime, GetDecimal(kline, "o"), GetDecimal(kline, "h"),
            GetDecimal(kline, "l"), GetDecimal(kline, "c"), GetDecimal(kline, "v"), false);
        return new MarketUpdate(symbol, timeFrame, candle);
    }

    private static TimeSpan GetDuration(TimeFrame timeFrame) => timeFrame switch
    {
        TimeFrame.FifteenMinutes => TimeSpan.FromMinutes(15),
        TimeFrame.OneHour => TimeSpan.FromHours(1),
        TimeFrame.FourHours => TimeSpan.FromHours(4),
        _ => throw new ArgumentOutOfRangeException(nameof(timeFrame))
    };

    private static decimal GetDecimal(JsonElement parent, string name) => parent.GetProperty(name).ValueKind == JsonValueKind.String
        ? decimal.Parse(parent.GetProperty(name).GetString()!, System.Globalization.CultureInfo.InvariantCulture)
        : parent.GetProperty(name).GetDecimal();

    private static long GetLong(JsonElement parent, string name) => parent.GetProperty(name).ValueKind == JsonValueKind.String
        ? long.Parse(parent.GetProperty(name).GetString()!, System.Globalization.CultureInfo.InvariantCulture)
        : parent.GetProperty(name).GetInt64();
}
