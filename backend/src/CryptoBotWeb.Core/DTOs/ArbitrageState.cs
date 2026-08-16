namespace CryptoBotWeb.Core.DTOs;

// Which exchange is currently the expensive (short) side for all open levels.
public enum ArbitrageDirection
{
    None = 0,
    PrimaryExpensive = 1,    // short on primary account, long on secondary
    SecondaryExpensive = 2   // short on secondary account, long on primary
}

public class ArbitrageLevelState
{
    // Index into ArbitrageConfig.Levels (after ascending sort by EntrySpreadPercent).
    public int Index { get; set; }
    public bool IsOpen { get; set; }

    // Filled quantities per leg in base units (needed for reduce-only close).
    public decimal ShortQty { get; set; }
    public decimal LongQty { get; set; }
    public decimal ShortEntryPrice { get; set; }
    public decimal LongEntryPrice { get; set; }

    // Actual entry spread at the moment the level opened.
    public decimal EntrySpreadPercent { get; set; }
    public DateTime? OpenedAt { get; set; }
}

public class ArbitrageState
{
    // True only once BOTH legs are pinned to the configured leverage. The per-leg flags let a
    // retry re-attempt just the leg that failed; LeverageRetryAt throttles those retries so a
    // permanently failing leg logs every few minutes instead of on every 5s tick.
    public bool LeverageSet { get; set; }
    public bool LeveragePrimarySet { get; set; }
    public bool LeverageSecondarySet { get; set; }
    public DateTime? LeverageRetryAt { get; set; }
    public ArbitrageDirection Direction { get; set; } = ArbitrageDirection.None;
    public List<ArbitrageLevelState> Levels { get; set; } = new();

    public int ConsecutiveFailures { get; set; }
    public decimal RealizedPnlUsdt { get; set; }

    // Full open→flat round trips (all levels closed).
    public int CompletedCycles { get; set; }

    // Monitoring: last observed entry spread (signed: positive = primary expensive).
    public decimal LastSpreadPercent { get; set; }
    public DateTime? LastCheckAt { get; set; }
}
