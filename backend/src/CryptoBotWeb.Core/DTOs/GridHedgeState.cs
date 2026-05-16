namespace CryptoBotWeb.Core.DTOs;

public enum GridHedgePhase
{
    NotStarted = 0,    // freshly created or freshly Start'd; next tick opens the hedge
    HedgeOpening = 1,  // hedge short placement in progress (transient — retried on failure)
    GridArming = 2,    // hedge open, laying down grid limit buys
    Active = 3,        // grid is live; poll fills, place TPs, watch exit triggers
    ExitingUp = 4,     // upper trigger crossed — closing hedge + cleaning up grid
    ExitingDown = 5,   // lower (stop-loss) trigger — closing everything in loss
    Done = 6           // cycle complete; user must Stop+Start to begin a new one
}

// One filled grid level. The buy fill has already happened on the exchange; this row also
// holds the reduce-only TP order placed against it. Mirrors GridFloatBatch except we store
// the offset% so PnL accounting and exit logic stay accurate even if the anchor shifts.
public class GridHedgeBatch
{
    // Original limit-buy order ID that filled into this batch.
    public string BuyOrderId { get; set; } = string.Empty;

    // Reduce-only limit-sell ID for the per-batch TP. Null until placed / cleared on cancel.
    public string? TpOrderId { get; set; }

    // Offset% from Anchor at fill time (used to select the tier whose TpStep applies).
    public decimal LevelPercent { get; set; }

    public decimal FilledPrice { get; set; }
    public decimal FilledQty { get; set; }
    public decimal TpPrice { get; set; }

    // True once the TP filled (or we force-closed via market on exit). PnL is recorded once.
    public bool Closed { get; set; }
    public decimal RealizedPnl { get; set; }

    public DateTime FilledAt { get; set; }
}

// A grid-leg limit buy that has been placed but is not yet filled. Tracked separately from
// Batches so we can cancel it during Exit*. Once filled, it migrates into Batches.
public class GridHedgePendingBuy
{
    public string OrderId { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Qty { get; set; }
    public decimal LevelPercent { get; set; }
}

public class GridHedgeState
{
    public GridHedgePhase Phase { get; set; } = GridHedgePhase.NotStarted;

    // Captured market price of GridSymbol at activation. Drives every grid-level calculation
    // and the exit triggers. 0 = not yet anchored.
    public decimal Anchor { get; set; }

    // Captured market price of HedgeSymbol at activation (= Anchor when Mode==SameTicker).
    // Used to compute hedge PnL on close.
    public decimal HedgeAnchor { get; set; }

    // Open hedge short on the futures leg. 0 when not yet opened or after close.
    public decimal HedgeQty { get; set; }
    public decimal HedgeAvgEntry { get; set; }
    public string? HedgeOpenOrderId { get; set; }

    // Filled grid levels with their dedicated TPs.
    public List<GridHedgeBatch> Batches { get; set; } = new();

    // Limit buys resting on the exchange (not yet filled).
    public List<GridHedgePendingBuy> PendingBuys { get; set; } = new();

    // Cumulative across cycles — preserved when the user does Stop+Start on a finished bot.
    public decimal GridRealizedPnl { get; set; }
    public decimal HedgeRealizedPnl { get; set; }
    public int CompletedCycles { get; set; }

    // Last price we observed — used for diagnostics / restart resync.
    public decimal? LastPrice { get; set; }

    // On placement failure, throttle the next attempt to avoid hammering the exchange.
    public DateTime? PlacementCooldownUntil { get; set; }

    // Counts consecutive GridArming ticks where placement failed. Reset to 0 when a tick
    // successfully places at least one limit. When this hits the limit (see handler), the
    // hedge is rolled back via a forced ExitingDown — prevents a naked hedge from sitting on
    // the exchange when the grid leg refuses (e.g. Bybit Spot regulatory restriction).
    public int GridArmingFailureCount { get; set; }
}
