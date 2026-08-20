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

    /// <summary>
    /// Cheap config sanity check run by the engine BEFORE the (slow) price-history download, so an
    /// obviously broken config fails fast with a readable message. Throw <see cref="ArgumentException"/>
    /// to reject; the default accepts everything.
    /// </summary>
    void ValidateConfig(string configJson) { }

    SimulationRunResult Run(SimulationContext context);
}
