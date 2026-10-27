using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Entities;

/// <summary>
/// A strategy parameter set saved from the Tester (admin backtesting tool) for one instrument.
/// Holds the complete ConfigJson in the same shape as <c>strategies.ConfigJson</c>, so it can be
/// replayed on fresh history or copied into a live bot. Shared by all admins — no owner FK, the
/// creator is kept for the audit trail only.
/// </summary>
public class TesterParamSet
{
    public Guid Id { get; set; }
    public ExchangeType ExchangeType { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string StrategyType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }

    /// <summary>Full strategy config (JSONB) — the optimizer's winning combination applied to the base.</summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>The swept values alone (JSONB, path → value), for compact display. "{}" when saved from a plain run.</summary>
    public string SweptParamsJson { get; set; } = "{}";

    /// <summary>Simulated window at save time (from the run summary).</summary>
    public DateTime? PeriodFromUtc { get; set; }
    public DateTime? PeriodToUtc { get; set; }
    public string PathTimeframe { get; set; } = "1m";

    /// <summary>Headline metrics at save time.</summary>
    public decimal NetPnlUsd { get; set; }
    public decimal MaxDrawdownUsd { get; set; }
    public decimal MaxDrawdownPercent { get; set; }
    public decimal WinRate { get; set; }
    public int TotalTrades { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public User? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
