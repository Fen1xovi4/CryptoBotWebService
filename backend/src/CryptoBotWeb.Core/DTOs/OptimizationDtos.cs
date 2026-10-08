namespace CryptoBotWeb.Core.DTOs;

/// <summary>
/// Request for the parameter optimizer (POST /api/tester/optimize): a regular simulation
/// request plus the set of config fields to sweep. The optimizer builds the price path once
/// and re-runs the pure simulator for every combination of the swept values.
/// </summary>
public class OptimizationRunRequest : SimulationRunRequest
{
    public List<OptimizationParameterSpec> Parameters { get; set; } = new();
}

/// <summary>
/// One swept config field. <see cref="Path"/> addresses a numeric value inside ConfigJson using
/// dot/index notation (e.g. "takeProfitPercent", "levels[0].entrySpreadPercent").
/// Either an explicit <see cref="Values"/> list or a From/To/Step range must be given.
/// </summary>
public class OptimizationParameterSpec
{
    public string Path { get; set; } = string.Empty;
    public decimal? From { get; set; }
    public decimal? To { get; set; }
    public decimal? Step { get; set; }
    public List<decimal>? Values { get; set; }
}

public class OptimizationStartResponse
{
    public Guid JobId { get; set; }
    public int TotalCombinations { get; set; }
}

/// <summary>
/// Result of one parameter combination — summary only. Trades/equity of a winner are obtained
/// by re-running a single simulation with the combination applied to the base config.
/// </summary>
public class OptimizationComboResult
{
    public Dictionary<string, decimal> Parameters { get; set; } = new();
    public SimulationRunSummary? Summary { get; set; }

    /// <summary>Set when this combination failed (invalid config etc.); Summary is null then.</summary>
    public string? Error { get; set; }
}

public class OptimizationJobStatusDto
{
    public Guid JobId { get; set; }

    /// <summary>Queued | Downloading | Running | Done | Failed | Cancelled.</summary>
    public string Status { get; set; } = "Queued";

    public int Completed { get; set; }
    public int Total { get; set; }
    public double ElapsedSeconds { get; set; }

    public string? Error { get; set; }
    public List<string> Warnings { get; set; } = new();
    public SimulationHistoryStats? History { get; set; }

    /// <summary>Filled only when the job reached Done.</summary>
    public List<OptimizationComboResult>? Results { get; set; }
}
