using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Core.Interfaces;

/// <summary>
/// Backtest counterpart of IStrategyHandler. One implementation per strategy type,
/// resolved by matching <see cref="StrategyType"/> against the request — same registry
/// pattern the Worker uses for live handlers.
/// Implementations must be pure/deterministic: no I/O, everything comes from the context.
/// </summary>
public interface IStrategySimulator
{
    /// <summary>Must equal one of <see cref="Constants.StrategyTypes"/>.</summary>
    string StrategyType { get; }

    SimulationRunResult Run(SimulationContext context);
}
