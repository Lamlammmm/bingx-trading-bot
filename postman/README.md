# BingX history recovery simulation

The local mock models the public contracts and kline response shapes used by
`BingXRestClient`, including the observed HTTP 200 response with business code
`100410`. BTC's first two 5m requests fail; ETH always succeeds. Other endpoints
return 501. Restart the simulation before each smoke run to reset its state.

Start it in one terminal:

```powershell
postman simulation run postman/simulations/bingx-history-recovery.sim.yaml --port 4907 --no-history --output ndjson
```

Run the production REST client and market-data worker against it in another:

```powershell
dotnet run --project tests/TradingBot.SimulationSmoke -p:OutputPath=D:\referenced-chatgpt-conversation-this-is-an\artifacts\symbol-retry-smoke\ -- http://127.0.0.1:4907/bingx/
```

The smoke checks recovery after both failures, only one ETH history load,
continued ETH candle processing, and one stream connection. It also checks that
only native 5m/30m/1h history is requested and closed candle durations match those
intervals; 15m history is not loaded. The stream is a
test implementation; `BingXWebSocketSubscriptionTests` separately exercises the
real WebSocket client against a local WebSocket server. No trading engine or
live executor is instantiated. No API key, BingX access or Postman login is needed.

## Bot monitoring API

See [the API contract](bot-api-contract.md) for the read-only monitoring endpoints.
With the published HTTP host running in `Bot__Enabled=false` mode, run:

```powershell
postman collection run postman/bot-api.postman_collection.json --no-report-events
```

This local smoke covers liveness, disabled readiness, API-key rejection and
authorized status. It does not start workers or call BingX. Ubuntu setup and the
host command are in [DEPLOY_UBUNTU.md](../DEPLOY_UBUNTU.md).

