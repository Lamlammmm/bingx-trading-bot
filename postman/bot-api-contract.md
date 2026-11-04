# Bot monitoring API

The host runs the existing trading engines as background services. HTTP requests
only read local status; they never start trading or call BingX.

- `GET /health/live`: anonymous, 200 while the HTTP host responds.
- `GET /health/ready`: anonymous, 200 only after trading initialization, successful
  processing within ten minutes, and a closed 5m candle within ten minutes for
  every configured symbol. Returns 503 when disabled, warming up, stale, processing
  has failed, or the application is stopping. Body is only Healthy/Unhealthy.
- `GET /api/bot/status`: requires the single `X-Api-Key` header matching `Api:Key`.
  Returns 401 for a missing, wrong or repeated key; otherwise 200 with bot enablement,
  live enablement, timeframes, processing counters and each symbol's latest closed
  5m candle timestamp. No credentials, balances or order payloads are returned.

`Api:Key` is required before starting the HTTP host, including API-only mode.
`Bot:Enabled=false` starts HTTP without market-data or trading workers, database
access, or exchange access. It reports not ready. Command-line export, journal and
preflight modes retain their existing behavior and do not require an API key.

`bot-api.postman_collection.json` is the executable contract for API-only mode.
Set `baseUrl` and `apiKey` locally. Never save production credentials in this file.
