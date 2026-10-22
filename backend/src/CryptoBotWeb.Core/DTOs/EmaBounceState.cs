namespace CryptoBotWeb.Core.DTOs;

public class EmaBounceState
{
    public int LongCounter { get; set; }
    public int ShortCounter { get; set; }
    public OpenPositionInfo? OpenLong { get; set; }
    public OpenPositionInfo? OpenShort { get; set; }
    public DateTime? LastProcessedCandleTime { get; set; }
    public bool WaitNextCandleAfterLongClose { get; set; }
    public bool WaitNextCandleAfterShortClose { get; set; }

    // Martingale state
    public int ConsecutiveLosses { get; set; }
    public decimal RunningPnlDollar { get; set; }

    // Last fetched market price (for frontend progress display)
    public decimal? LastPrice { get; set; }

    // Computed next order size (for frontend display)
    public decimal NextOrderSize { get; set; }

    // Intrabar entry cooldown — timestamp of last intrabar entry attempt (success or fail).
    // Used to throttle retries so failed orders don't cascade every 5s poll.
    public DateTime? LastIntrabarAttemptAt { get; set; }

    // ── Grid (MaratGGrid only) ──
    // Open grid lots on top of the base position. On the exchange they are one net one-way position
    // together with the base; the bot tracks which quantity belongs to which lot.
    public List<GridLotInfo> GridLots { get; set; } = new();

    // Resting buy limits that become lots when they fill: at most one level order (the nearest
    // free level) plus the starter-reopen order. All cancelled before the base closes.
    public List<GridPendingOrder> GridPendingOrders { get; set; } = new();

    // When the starter lot (level 0) took profit, its reopen limit is placed once this moment
    // (the open of the next candle) has passed — as long as the base position still exists.
    public DateTime? GridStarterReopenAt { get; set; }

    // Net USDT realized by grid lots over the bot's life, and how many lots took profit.
    public decimal GridRealizedPnl { get; set; }
    public int GridLotsClosed { get; set; }

    // Martingale-scaled lot size for the next grid open (for frontend display).
    public decimal NextGridLotSize { get; set; }

    // Lot size fixed when the base opened, so drawdown-scaled martingale can't resize lots mid-cycle,
    // and the net grid PnL of the current cycle (for the cycle-close log).
    public decimal CycleGridLotSize { get; set; }
    public decimal CycleGridPnl { get; set; }

    // After a failed grid order, no grid order is sent before this moment (no 5s retry hammering).
    public DateTime? GridRetryAfter { get; set; }

    /// <summary>Open grid lots marked to LastPrice (lots follow the base's direction).</summary>
    public decimal GridUnrealizedPnl()
    {
        if (GridLots.Count == 0 || LastPrice is not > 0m) return 0m;
        var mark = LastPrice.Value;
        var isLong = OpenShort == null;
        return GridLots.Sum(l => isLong ? l.Quantity * (mark - l.EntryPrice) : l.Quantity * (l.EntryPrice - mark));
    }
}

public class GridLotInfo
{
    // 0 = starter lot opened with the base; k >= 1 = k-th level against the base entry.
    public int Level { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal OrderSize { get; set; }
    public decimal TakeProfit { get; set; }
    public DateTime OpenedAt { get; set; }
    // Fee the exchange reported for the opening fill; null = estimate at the taker rate on close.
    public decimal? EntryFee { get; set; }
    public string? ExchangeOrderId { get; set; }
    // Resting reduce-only limit at TakeProfit for exactly this lot's quantity; null = not placed yet.
    public string? TpOrderId { get; set; }
    public DateTime? TpPlacedAt { get; set; }
}

/// <summary>What cancelling a MaratGGrid's resting orders found: TP fills to book, orders left unconfirmed.</summary>
public class GridOrdersCancelResult
{
    public List<(GridLotInfo Part, decimal Price, decimal Fee, string OrderId)> TpFills { get; } = new();
    public int Unsettled { get; set; }
}

public class GridPendingOrder
{
    // 0 = starter reopen, k >= 1 = level k against the base entry.
    public int Level { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal OrderSize { get; set; }
    public DateTime PlacedAt { get; set; }
    // Starter reopen only: still unfilled at this moment (next candle open) → re-placed at the current price.
    public DateTime? RepriceAt { get; set; }
}

public class OpenPositionInfo
{
    public string Direction { get; set; } = string.Empty;
    public decimal EntryPrice { get; set; }
    public decimal Quantity { get; set; }
    public DateTime OpenedAt { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal StopLoss { get; set; }
    public string? ExchangeOrderId { get; set; }
    public decimal OrderSize { get; set; }
    // Fee the exchange reported for the opening fill (MaratGGrid reads it back); null = estimate.
    public decimal? EntryFee { get; set; }
}
