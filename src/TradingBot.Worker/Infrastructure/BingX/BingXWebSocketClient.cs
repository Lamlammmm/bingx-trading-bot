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

    public async Task StreamAsync(Func<MarketUpdate, Task> onUpdate, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_options.WebSocketUrl), cancellationToken);
        foreach (var interval in new[] { "15m", "1h", "4h" })
        {
            var request = JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString(), reqType = "sub", dataType = $"{_options.Symbol}@kline_{interval}" });
            await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, cancellationToken);
        }

        logger.LogInformation("Connected to BingX perpetual market WebSocket for {Symbol}", _options.Symbol);
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(socket, cancellationToken);
            if (message is null) break;
            if (message.Equals("Ping", StringComparison.OrdinalIgnoreCase))
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes("Pong"), WebSocketMessageType.Text, true, cancellationToken);
                continue;
            }

            var update = ParseUpdate(message);
            if (update is not null) await onUpdate(update);
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

    private static MarketUpdate? ParseUpdate(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("dataType", out var dataTypeProperty)) return null;
            var dataType = dataTypeProperty.GetString() ?? string.Empty;
            if (!dataType.Contains("@kline_", StringComparison.Ordinal)) return null;
            var interval = dataType[(dataType.IndexOf("@kline_", StringComparison.Ordinal) + 7)..];
            var timeFrame = interval switch
            {
                "15m" => TimeFrame.FifteenMinutes,
                "1h" => TimeFrame.OneHour,
                "4h" => TimeFrame.FourHours,
                _ => (TimeFrame?)null
            };
            if (timeFrame is null || !root.TryGetProperty("data", out var data)) return null;
            if (data.ValueKind == JsonValueKind.Array) data = data.EnumerateArray().FirstOrDefault();
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("K", out var kline)) return null;
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(GetLong(kline, "t"));
            var closeTime = DateTimeOffset.FromUnixTimeMilliseconds(GetLong(kline, "T"));
            var candle = new Candle(openTime, closeTime, GetDecimal(kline, "o"), GetDecimal(kline, "h"),
                GetDecimal(kline, "l"), GetDecimal(kline, "c"), GetDecimal(kline, "v"), closeTime <= DateTimeOffset.UtcNow);
            return new MarketUpdate(timeFrame.Value, candle);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static decimal GetDecimal(JsonElement parent, string name) => parent.GetProperty(name).ValueKind == JsonValueKind.String
        ? decimal.Parse(parent.GetProperty(name).GetString()!, System.Globalization.CultureInfo.InvariantCulture)
        : parent.GetProperty(name).GetDecimal();

    private static long GetLong(JsonElement parent, string name) => parent.GetProperty(name).ValueKind == JsonValueKind.String
        ? long.Parse(parent.GetProperty(name).GetString()!, System.Globalization.CultureInfo.InvariantCulture)
        : parent.GetProperty(name).GetInt64();
}
