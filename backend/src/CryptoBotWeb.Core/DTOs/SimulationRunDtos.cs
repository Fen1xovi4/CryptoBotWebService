using CryptoBotWeb.Core.Constants;

namespace CryptoBotWeb.Core.DTOs;

/// <summary>
/// Request for the multi-strategy simulator (POST /api/tester/simulate).
/// ConfigJson carries the strategy config in the exact same JSON shape as the
/// strategies.ConfigJson column, so a live bot's config can be replayed as-is.
/// </summary>
public class SimulationRunRequest
{
    public Guid AccountId { get; set; }
    public string StrategyType { get; set; } = StrategyTypes.MaratG;
    public string Symbol { get; set; } = "BTCUSDT";

    /// <summary>Second price series for GridHedge CrossTicker mode; null otherwise.</summary>
    public string? SecondSymbol { get; set; }

    /// <summary>Explicit UTC window. When FromUtc is null, the window is the last <see cref="Days"/> days.</summary>
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public int Days { get; set; } = 30;

    public string ConfigJson { get; set; } = "{}";

    /// <summary>Fee overrides as raw fractions (0.0006 = 0.06%). Null → exchange defaults.</summary>
    public decimal? MakerFeeRate { get; set; }
    public decimal? TakerFeeRate { get; set; }
}

/// <summary>
/// Everything a strategy simulator needs to run: the price path, funding history
/// and fees. Built by the SimulationEngine, consumed by IStrategySimulator.Run.
/// </summary>
public class SimulationContext
{
    public string StrategyType { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string ConfigJson { get; set; } = "{}";

    /// <summary>
    /// 1-minute candles over the whole simulation window, ascending by OpenTime.
    /// This is the price path: simulators walk it candle-by-candle (see CandlePathHelper
    /// for the intrabar tick convention) and aggregate to their strategy timeframe via
    /// CandleAggregator when they need indicator candles.
    /// </summary>
    public List<CandleDto> PathCandles { get; set; } = new();

    public string? SecondSymbol { get; set; }
    public List<CandleDto>? SecondSymbolPathCandles { get; set; }

    /// <summary>Historical funding settlements for Symbol, ascending. Empty when not needed/unavailable.</summary>
    public List<FundingEventDto> FundingEvents { get; set; } = new();

    /// <summary>Raw fee fractions (0.0002 = 0.02%). Applied by simulators on every simulated fill.</summary>
    public decimal MakerFeeRate { get; set; } = 0.0002m;
    public decimal TakerFeeRate { get; set; } = 0.0006m;

    /// <summary>Warnings surfaced to the result (e.g. truncated funding history). Simulators may append.</summary>
    public List<string> Warnings { get; set; } = new();
}

public class SimulationRunResult
{
    public List<SimTrade> Trades { get; set; } = new();

    /// <summary>Indicator overlay (MA line etc.) at strategy-timeframe resolution. Optional.</summary>
    public List<IndicatorPoint> IndicatorValues { get; set; } = new();

    /// <summary>Equity samples (realized + unrealized, net of fees/funding) over time.</summary>
    public List<EquityPoint> EquityCurve { get; set; } = new();

    /// <summary>Candles for charting, aggregated to the strategy timeframe. Filled by the engine.</summary>
    public List<CandleDto> ChartCandles { get; set; } = new();

    public SimulationRunSummary Summary { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class SimTrade
{
    public DateTime Time { get; set; }

    /// <summary>Long | Short — the position side this fill belongs to.</summary>
    public string Side { get; set; } = string.Empty;

    /// <summary>Open | Close | Dca | TakeProfit | StopLoss | Skim | HedgeOpen | HedgeClose | Funding | ...</summary>
    public string Action { get; set; } = string.Empty;

    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal NotionalUsd { get; set; }
    public decimal FeeUsd { get; set; }

    /// <summary>Realized PnL of this fill (net of its fee), null for pure opens.</summary>
    public decimal? PnlUsd { get; set; }
    public decimal? PnlPercent { get; set; }

    public string Reason { get; set; } = string.Empty;
}

public class EquityPoint
{
    public DateTime Time { get; set; }
    public decimal RealizedPnlUsd { get; set; }
    public decimal UnrealizedPnlUsd { get; set; }

    /// <summary>RealizedPnlUsd + UnrealizedPnlUsd (fees and funding already included in realized).</summary>
    public decimal EquityUsd { get; set; }
}

public class SimulationRunSummary
{
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public decimal WinRate { get; set; }

    /// <summary>Realized PnL before fees and funding.</summary>
    public decimal GrossPnlUsd { get; set; }
    public decimal FeesUsd { get; set; }
    public decimal FundingPnlUsd { get; set; }

    /// <summary>GrossPnlUsd - FeesUsd + FundingPnlUsd.</summary>
    public decimal NetPnlUsd { get; set; }

    public decimal MaxDrawdownUsd { get; set; }
    public decimal MaxDrawdownPercent { get; set; }

    /// <summary>Peak simultaneous position notional — shows margin/exposure requirements.</summary>
    public decimal MaxNotionalUsd { get; set; }

    public int OpenPositionsAtEnd { get; set; }
    public decimal UnrealizedPnlAtEndUsd { get; set; }
    public int CompletedCycles { get; set; }

    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int PathCandlesProcessed { get; set; }
}
