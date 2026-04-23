namespace CryptoBotWeb.Core.DTOs;

// One tier of the stepped DCA plan. Bot stays on a tier until it has completed `Count` fills,
// then advances to the next tier (which typically widens the step / changes the multiplier).
public class SmaDcaLevel
{
    // Distance the price must move against the position (from the DcaTriggerBase reference)
    // before a DCA fires while this tier is active, in %.
    public decimal StepPercent { get; set; }

    // Size multiplier applied to current total quantity: dcaQty = currentTotalQty * Multiplier.
    public decimal Multiplier { get; set; }

    // Number of DCA fills to perform on this tier before switching to the next one.
    public int Count { get; set; }
}

public class SmaDcaConfig
{
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = "1h";

    // "Long" or "Short" — one bot = one direction
    public string Direction { get; set; } = "Long";

    public int SmaPeriod { get; set; } = 50;

    // TP measured from the current average entry; recomputed after every DCA
    public decimal TakeProfitPercent { get; set; } = 1.0m;

    // Tiered DCA configuration. Each tier has its own step%/multiplier and applies for `Count`
    // fills before the bot advances to the next tier. Total DCA cap = sum of all tier Counts.
    // When all tiers are exhausted no more DCAs are placed — bot waits for TP.
    // If Levels is empty, the handler synthesizes a single tier from the legacy scalar fields
    // (DcaStepPercent/DcaMultiplier/MaxDcaLevels) for backward compatibility.
    public List<SmaDcaLevel> Levels { get; set; } = new();

    // "Average" (default) — triggers when price moves the current tier's step% from the CURRENT average
    //                      (grid tightens as position averages down).
    // "LastFill"          — triggers when price moves the current tier's step% from the LAST FILL price
    //                      (grid stays an even step apart, position accumulates slower).
    // TP is always computed off the average regardless of this setting.
    public string DcaTriggerBase { get; set; } = "Average";

    // Legacy fields — kept for backward compatibility with existing bots stored before tiered
    // levels were introduced. Only used when `Levels` is empty: the handler synthesizes
    // a single tier from these values. New configs should populate `Levels` and leave these at defaults.
    public decimal DcaStepPercent { get; set; } = 3.0m;
    public decimal DcaMultiplier { get; set; } = 3.0m;
    public int MaxDcaLevels { get; set; } = 5;

    // First-entry quote amount in USD
    public decimal PositionSizeUsd { get; set; } = 100m;

    // Controls DCA order placement. FIRST ENTRY IS ALWAYS MARKET regardless of this setting —
    // we need the position open immediately so DCA/TP logic can start working.
    //   "Market" — DCAs go as market orders (taker fees, guaranteed fill, slippage).
    //   "Limit"  — DCAs go as reduce-unaware maker limits placed 'EntryLimitOffsetPercent' away
    //              from candle close in the direction of our trade (may not fill).
    // TP is always a reduce-only limit regardless of this setting.
    public string OrderType { get; set; } = "Market";

    // Offset in % applied to candle.Close when placing DCA limits. For Long Buy we use
    //   close × (1 − offset/100); for Short Sell — close × (1 + offset/100).
    // Small enough (default 0.05%) to fill quickly on liquid pairs while guaranteeing maker status.
    public decimal EntryLimitOffsetPercent { get; set; } = 0.05m;

    // Legacy: bars-timeout for entry limits. First entry is now always Market, so this only still
    // cleans up any leftover entry-limit state from prior config — kept for safe migration.
    public int EntryLimitTimeoutBars { get; set; } = 3;
}

// Stored inside Workspace.ConfigJson (shared across all SmaDca bots in the workspace).
// When TimerEnabled is true and TimerExpiresAt is in the past, bots stop opening
// NEW positions (but DCA fills and take-profit closes on existing positions still run).
public class WorkspaceSmaDcaConfig
{
    public bool TimerEnabled { get; set; } = false;
    public DateTime? TimerExpiresAt { get; set; } // UTC
}
