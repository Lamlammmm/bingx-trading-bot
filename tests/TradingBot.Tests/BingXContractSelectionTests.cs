using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Worker.Infrastructure.BingX;

namespace TradingBot.Tests;

public sealed class BingXContractSelectionTests
{
    [Theory]
    [InlineData("ETH-USDT", 3)]
    [InlineData("DOGE-USDT", null)]
    public async Task ContractMetadataMustBelongToRequestedSymbol(string symbol, int? expectedPrecision)
    {
        using var http = new HttpClient(new ContractsHandler()) { BaseAddress = new Uri("https://example.test") };
        var client = new BingXRestClient(http, NullLogger<BingXRestClient>.Instance);
        var contract = await client.GetContractAsync(symbol, default);
        if (expectedPrecision is null) Assert.Null(contract);
        else
        {
            Assert.Equal(symbol, contract!.Symbol);
            Assert.Equal(expectedPrecision.Value, contract.QuantityPrecision);
        }
    }

    private sealed class ContractsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"code":0,"data":[
                  {"symbol":"BTC-USDT","quantityPrecision":4,"pricePrecision":1,"tradeMinQuantity":0.0001,"tradeMinUSDT":2,"status":1},
                  {"symbol":"ETH-USDT","quantityPrecision":3,"pricePrecision":2,"tradeMinQuantity":0.001,"tradeMinUSDT":2,"status":1}
                ]}
                """)
        });
    }

    [Theory]
    [InlineData(0, false, 200)]
    [InlineData(90, false, null)]
    [InlineData(0, true, null)]
    public async Task SpreadUsesApprovedDepthFieldsAndRejectsStaleOrCrossedBook(int ageSeconds, bool crossed, int? expected)
    {
        using var http = new HttpClient(new DepthHandler(ageSeconds, crossed)) { BaseAddress = new Uri("https://example.test") };
        var client = new BingXRestClient(http, NullLogger<BingXRestClient>.Instance);
        var spread = await client.GetSpreadBasisPointsAsync("BTC-USDT", default);
        Assert.Equal(expected is null ? null : (decimal?)expected.Value, spread);
    }

    private sealed class DepthHandler(int ageSeconds, bool crossed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Contains("quote/depth?symbol=BTC-USDT&limit=5", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { code = 0, data = new { T = DateTimeOffset.UtcNow.AddSeconds(-ageSeconds).ToUnixTimeMilliseconds(),
                bids = new[] { new[] { "99", "1" } }, asks = new[] { new[] { crossed ? "98" : "101", "1" } } } })) });
        }
    }
}
