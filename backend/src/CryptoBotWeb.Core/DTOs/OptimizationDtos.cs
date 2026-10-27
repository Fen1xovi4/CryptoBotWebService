namespace CryptoBotWeb.Core.DTOs;

/// <summary>
/// Request for the parameter optimizer (POST /api/tester/optimize): a regular simulation
/// request plus the set of config fields to sweep. The optimizer builds the price path once
/// and re-runs the pure simulator for every combination of the swept values.
/// </summary>
public class OptimizationRunRequest : SimulationRunRequest
{
    public List<OptimizationParameterSpec> Parameters { get; set; } = new();

    /// <summary>
    /// Overfitting guard: share of the window (by time, from the end) held out as the
    /// out-of-sample check. 0 = off. With e.g. 30 the first 70% of candles is the in-sample
    /// period every combination is scored on (<see cref="OptimizationComboResult.Summary"/>), and
    /// the last 30% is replayed separately (<see cref="OptimizationComboResult.OosSummary"/>) —
    /// a set whose edge vanishes there was fitted to noise.
    /// </summary>
    public int OutOfSamplePercent { get; set; }
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

    /// <summary>In-sample result (the whole window when no out-of-sample split was requested).</summary>
    public SimulationRunSummary? Summary { get; set; }

    /// <summary>Out-of-sample result; null when the split is off.</summary>
    public SimulationRunSummary? OosSummary { get; set; }

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

    /// <summary>Echo of the request's split; 0 = no out-of-sample check.</summary>
    public int OutOfSamplePercent { get; set; }

    /// <summary>In-sample / out-of-sample windows actually used (Done only, split on).</summary>
    public DateTime? InSampleFromUtc { get; set; }
    public DateTime? InSampleToUtc { get; set; }
    public DateTime? OutOfSampleFromUtc { get; set; }
    public DateTime? OutOfSampleToUtc { get; set; }

    /// <summary>Number of combinations that produced a summary (Done only).</summary>
    public int OkCount { get; set; }

    /// <summary>Combinations hidden from the page by the minTrades filter (Done only).</summary>
    public int FilteredOutCount { get; set; }

    /// <summary>Number of combinations that failed validation/run (Done only).</summary>
    public int FailedCount { get; set; }

    /// <summary>Error message of the first failed combination, if any.</summary>
    public string? FirstError { get; set; }

    /// <summary>
    /// Filled only when the job reached Done: the successful combinations sorted by the requested
    /// metric and cut to the requested page size. Sweeps can be 100k runs — the whole set is never
    /// serialized; the client re-queries with a different sort key instead of sorting locally.
    /// </summary>
    public List<OptimizationComboResult>? Results { get; set; }
}

/// <summary>
/// A parameter set the admin saved from the Tester for one instrument + strategy: the full
/// ConfigJson of the winning combination, the swept values (for display), the test window and
/// the headline metrics at save time. Replayed later on fresh history via /tester/simulate.
/// </summary>
public class TesterParamSetDto
{
    public Guid Id { get; set; }
    public int ExchangeType { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string StrategyType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public Dictionary<string, decimal> SweptParams { get; set; } = new();
    public DateTime? PeriodFromUtc { get; set; }
    public DateTime? PeriodToUtc { get; set; }
    public string PathTimeframe { get; set; } = "1m";
    public decimal NetPnlUsd { get; set; }
    public decimal MaxDrawdownUsd { get; set; }
    public decimal MaxDrawdownPercent { get; set; }
    public decimal WinRate { get; set; }
    public int TotalTrades { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SaveTesterParamSetRequest
{
    /// <summary>Exchange account the test ran on — resolves the exchange the set belongs to.</summary>
    public Guid AccountId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string StrategyType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public Dictionary<string, decimal>? SweptParams { get; set; }
    public string PathTimeframe { get; set; } = "1m";

    /// <summary>Summary of the run being saved — the window and metrics are copied from it.</summary>
    public SimulationRunSummary? Summary { get; set; }
}

public class UpdateTesterParamSetRequest
{
    public string? Name { get; set; }
    public string? Note { get; set; }
}
