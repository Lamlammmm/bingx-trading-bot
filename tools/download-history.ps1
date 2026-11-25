param(
    [string[]]$Symbols = @('BTC-USDT', 'ETH-USDT'),
    [int]$Days = 30,
    [string]$OutputDirectory = 'artifacts/market-history',
    [DateTimeOffset]$EndUtc = [DateTimeOffset]::UtcNow
)
$ErrorActionPreference = 'Stop'
if ($Days -lt 15 -or $Days -gt 3650) { throw 'Days must be between 15 and 3650.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$fetchedAt = [DateTimeOffset]::UtcNow
if ($EndUtc -gt $fetchedAt) { throw 'EndUtc cannot be in the future.' }
$end = [long]([Math]::Floor($EndUtc.ToUnixTimeMilliseconds() / 300000) * 300000)
$start = $end - [long]$Days * 86400000
foreach ($symbol in $Symbols) {
    if ($symbol -notmatch '^[A-Z0-9]+-USDT$') { throw "Invalid symbol: $symbol" }
    $contract = Invoke-RestMethod -Uri "https://open-api.bingx.com/openApi/swap/v2/quote/contracts?symbol=$symbol" -TimeoutSec 30
    if ($contract.code -ne 0) { throw "Contract request failed: $($contract.code)" }
    $contract | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory "$symbol-contract.json") -Encoding utf8
    $rows = [System.Collections.Generic.SortedDictionary[long, object]]::new()
    $cursor = $end - 1
    while ($cursor -ge $start) {
        Start-Sleep -Milliseconds 1100
        # Three days fit below the API's 1,000-row limit at 5m (864 rows).
        $pageStart = [Math]::Max($start, $cursor - 259200000L + 1)
        $uri = "https://open-api.bingx.com/openApi/swap/v3/quote/klines?symbol=$symbol&interval=5m&startTime=$pageStart&endTime=$cursor&limit=1000"
        $result = $null
        for ($attempt = 0; $attempt -lt 4; $attempt++) {
            try {
                $result = Invoke-RestMethod -Uri $uri -TimeoutSec 30
                if ($result.code -eq 0 -and $result.data.Count -gt 0) { break }
                throw "Kline response code=$($result.code) or empty data at page start=$pageStart end=$cursor"
            } catch {
                if ($attempt -eq 3) { throw }
                Start-Sleep -Seconds ([Math]::Pow(2, $attempt + 1))
            }
        }
        $expectedFirst = $pageStart
        foreach ($row in $result.data) {
            $time = [long]$row.time
            if ($time -gt $cursor -or $time -lt $pageStart) { throw 'API returned candles outside requested page.' }
            if ($time -ge $start -and $time -lt $end) { $rows[$time] = $row }
        }
        if (-not $rows.ContainsKey($expectedFirst)) { throw "Incomplete page for $symbol at $expectedFirst." }
        $cursor = $pageStart - 1
    }
    if ($rows.Count -ne $Days * 288) { throw "Expected $($Days * 288) consecutive candles for $symbol, received $($rows.Count)." }
    $path = Join-Path $OutputDirectory "$symbol-5m.csv"
    $rows.Values | Select-Object time,open,high,low,close,volume | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding utf8
    $manifest = @{
        source = 'https://open-api.bingx.com/openApi/swap/v3/quote/klines'
        symbol = $symbol; interval = '5m'; fetchedAt = $fetchedAt.ToString('O')
        startInclusive = $start; endExclusive = $end; candles = $rows.Count
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        funding = 'Not downloaded; backtest uses an explicit cost scenario.'
        spread = 'No historical quotes; backtest uses an explicit cost scenario.'
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory "$symbol-manifest.json") -Encoding utf8
    Write-Output "$symbol : $($rows.Count) closed 5m candles downloaded."
}
