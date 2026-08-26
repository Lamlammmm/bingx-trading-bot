using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.BingX;

public sealed class BingXRestClient(HttpClient httpClient, IOptions<BingXOptions> options,
    ILogger<BingXRestClient> logger) : IBingXMarketClient
{
    private readonly BingXOptions _options = options.Value;

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(TimeFrame timeFrame, int limit, CancellationToken cancellationToken)
    {
        var interval = timeFrame switch
        {
            TimeFrame.FifteenMinutes => "15m",
            TimeFrame.OneHour => "1h",
            TimeFrame.FourHours => "4h",
            _ => throw new ArgumentOutOfRangeException(nameof(timeFrame))
        };
        var url = $"openApi/swap/v3/quote/klines?symbol={Uri.EscapeDataString(_options.Symbol)}&interval={interval}&limit={Math.Clamp(limit, 1, 1440)}";
        using var document = await GetDocumentAsync(url, cancellationToken);
        var candles = new List<Candle>();
        foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
        {
            var candle = ParseCandle(item, timeFrame);
            if (candle is not null && candle.IsClosed) candles.Add(candle);
        }
        candles.Sort((left, right) => left.OpenTime.CompareTo(right.OpenTime));
        logger.LogDebug("Loaded {Count} closed {TimeFrame} candles for {Symbol}", candles.Count, timeFrame, _options.Symbol);
        return candles;
    }

    public async Task<ContractInfo?> GetContractAsync(CancellationToken cancellationToken)
    {
        var url = $"openApi/swap/v2/quote/contracts?symbol={Uri.EscapeDataString(_options.Symbol)}";
        using var document = await GetDocumentAsync(url, cancellationToken);
        var item = document.RootElement.GetProperty("data");
        if (item.ValueKind == JsonValueKind.Array) item = item.EnumerateArray().FirstOrDefault();
        if (item.ValueKind != JsonValueKind.Object) return null;
        return new ContractInfo(item.GetProperty("symbol").GetString() ?? _options.Symbol,
            GetDecimal(item, "quantityPrecision"), GetDecimal(item, "pricePrecision"),
            GetDecimal(item, "tradeMinQuantity"), GetDecimal(item, "tradeMinUSDT"), GetDecimal(item, "status") == 1m);
    }

    private async Task<JsonDocument> GetDocumentAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(relativeUrl, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        var document = JsonDocument.Parse(body);
        var code = document.RootElement.GetProperty("code").GetInt32();
        if (code != 0)
            throw new InvalidOperationException($"BingX REST error {code}: {document.RootElement.GetProperty("msg").GetString()}");
        return document;
    }

    private static Candle? ParseCandle(JsonElement item, TimeFrame timeFrame)
    {
        if (item.ValueKind == JsonValueKind.Array)
        {
            var values = item.EnumerateArray().ToArray();
            if (values.Length < 6) return null;
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(GetLong(values[0]));
            var closeTime = values.Length > 6
                ? DateTimeOffset.FromUnixTimeMilliseconds(GetLong(values[6]))
                : openTime.Add(GetTimeSpan(timeFrame));
            return new Candle(openTime, closeTime, GetDecimal(values[1]), GetDecimal(values[2]), GetDecimal(values[3]),
                GetDecimal(values[4]), GetDecimal(values[5]), closeTime <= DateTimeOffset.UtcNow);
        }

        var openTimeFromObject = DateTimeOffset.FromUnixTimeMilliseconds(GetLong(item, "time", "openTime"));
        var objectCloseTime = item.TryGetProperty("closeTime", out var closeProperty)
            ? DateTimeOffset.FromUnixTimeMilliseconds(GetLong(closeProperty))
            : openTimeFromObject.Add(GetTimeSpan(timeFrame));
        return new Candle(openTimeFromObject, objectCloseTime, GetDecimal(item, "open"), GetDecimal(item, "high"),
            GetDecimal(item, "low"), GetDecimal(item, "close"), GetDecimal(item, "volume"), objectCloseTime <= DateTimeOffset.UtcNow);
    }

    private static TimeSpan GetTimeSpan(TimeFrame timeFrame) => timeFrame switch
    {
        TimeFrame.FifteenMinutes => TimeSpan.FromMinutes(15),
        TimeFrame.OneHour => TimeSpan.FromHours(1),
        TimeFrame.FourHours => TimeSpan.FromHours(4),
        _ => throw new ArgumentOutOfRangeException(nameof(timeFrame))
    };

    private static decimal GetDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value)) return GetDecimal(value);
        throw new JsonException($"Missing numeric property: {string.Join('/', names)}");
    }

    private static decimal GetDecimal(JsonElement element) => element.ValueKind == JsonValueKind.String
        ? decimal.Parse(element.GetString()!, CultureInfo.InvariantCulture)
        : element.GetDecimal();

    private static long GetLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value)) return GetLong(value);
        throw new JsonException($"Missing timestamp property: {string.Join('/', names)}");
    }

    private static long GetLong(JsonElement element) => element.ValueKind == JsonValueKind.String
        ? long.Parse(element.GetString()!, CultureInfo.InvariantCulture)
        : element.GetInt64();
}
