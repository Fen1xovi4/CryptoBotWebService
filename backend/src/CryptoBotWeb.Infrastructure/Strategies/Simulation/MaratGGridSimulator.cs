using CryptoBotWeb.Core.Constants;

namespace CryptoBotWeb.Infrastructure.Strategies.Simulation;

/// <summary>
/// Backtest simulator for MaratG+Grid: the MaratG simulator with grid lots on top of the base
/// (starter lot, levels against the base entry, per-lot take-profits, everything closed with the
/// base). The grid half lives in <see cref="MaratGSimulator"/> behind <c>WithGrid</c>.
/// </summary>
public class MaratGGridSimulator : MaratGSimulator
{
    public override string StrategyType => StrategyTypes.MaratGGrid;

    protected override bool WithGrid => true;
}
