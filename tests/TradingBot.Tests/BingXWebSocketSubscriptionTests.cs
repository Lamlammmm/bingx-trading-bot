using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.BingX;

namespace TradingBot.Tests;

public sealed class BingXWebSocketSubscriptionTests
{
    [Fact]
    public async Task AddsRecoveredSymbolOnExistingSocketAndKeepsHeartbeatAndMarketUpdatesWorking()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cancellation.Token;
        var additionalSymbols = Channel.CreateUnbounded<string>();
        var updates = Channel.CreateUnbounded<MarketUpdate>();
        var client = new BingXWebSocketClient(Options.Create(new BingXOptions
        {
            WebSocketUrl = $"ws://localhost:{port}/"
        }), NullLogger<BingXWebSocketClient>.Instance);
        var streamTask = client.StreamAsync(["ETH-USDT"], additionalSymbols.Reader,
            update => updates.Writer.WriteAsync(update, token).AsTask(), token);
        try
        {
            var context = await listener.GetContextAsync().WaitAsync(token);
            var accepted = await context.AcceptWebSocketAsync(null).WaitAsync(token);
            using var socket = accepted.WebSocket;
            foreach (var symbol in new[] { "ETH-USDT", "BTC-USDT" })
            {
                if (symbol == "BTC-USDT") await additionalSymbols.Writer.WriteAsync(symbol, token);
                foreach (var interval in new[] { "5m", "30m", "1h" })
                {
                    using var request = JsonDocument.Parse(await ReadTextAsync(socket, token));
                    Assert.Equal("sub", request.RootElement.GetProperty("reqType").GetString());
                    Assert.Equal($"{symbol}@kline_{interval}", request.RootElement.GetProperty("dataType").GetString());
                }
            }

            // A duplicate recovery must not subscribe the same three feeds again.
            await additionalSymbols.Writer.WriteAsync("BTC-USDT", token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("Ping"), WebSocketMessageType.Text, true, token);
            Assert.Equal("Pong", await ReadTextAsync(socket, token));

            foreach (var symbol in new[] { "ETH-USDT", "BTC-USDT" })
            {
                var payload = $$"""
                    {"dataType":"{{symbol}}@kline_5m","data":[{"T":1787750100000,"o":"100","h":"110","l":"90","c":"105","v":"12"}]}
                    """;
                await socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, token);
                var update = await updates.Reader.ReadAsync(token);
                Assert.Equal(symbol, update.Symbol);
                Assert.Equal(TimeFrame.FiveMinutes, update.TimeFrame);
            }
            Assert.False(streamTask.IsCompleted);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await streamTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
