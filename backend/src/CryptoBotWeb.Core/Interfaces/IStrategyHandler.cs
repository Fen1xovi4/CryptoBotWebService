using CryptoBotWeb.Core.Entities;

namespace CryptoBotWeb.Core.Interfaces;

public interface IStrategyHandler
{
    string StrategyType { get; }
    Task ProcessAsync(Strategy strategy, IFuturesExchangeService exchange, CancellationToken ct);
}
