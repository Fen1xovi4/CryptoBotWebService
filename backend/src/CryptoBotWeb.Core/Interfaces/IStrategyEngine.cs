using CryptoBotWeb.Core.Entities;

namespace CryptoBotWeb.Core.Interfaces;

public interface IStrategyEngine
{
    Task StartAsync(Strategy strategy, CancellationToken ct);
    Task StopAsync(Guid strategyId);
}
