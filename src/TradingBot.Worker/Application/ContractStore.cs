using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed class InMemoryContractStore : IContractStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ContractInfo> _contracts = new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(string symbol, ContractInfo contract)
    {
        lock (_sync) _contracts[symbol] = contract;
    }

    public ContractInfo? Get(string symbol)
    {
        lock (_sync) return _contracts.GetValueOrDefault(symbol);
    }
}
