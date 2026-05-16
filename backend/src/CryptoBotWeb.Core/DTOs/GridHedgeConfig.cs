namespace CryptoBotWeb.Core.DTOs;

// Grid-with-Hedge strategy. Uniform-step grid below the anchor + a single futures short.
//
// Two operating modes:
//   - SameTicker (1): spot grid + futures short on the SAME symbol (Bybit-only in V1).
//   - CrossTicker (2): futures grid on one symbol + futures short on a correlated symbol.
//
// Grid layout: BetUsdt per limit-buy, levels spaced DcaStepPercent% apart from −DcaStepPercent
// down to −RangePercent. Each fill becomes a batch with its own reduce-only limit sell at
// fill_price × (1 + TpStepPercent/100).
//
// Exit triggers (both close the whole bot — grid + hedge):
//   - price ≥ Anchor × (1 + UpperExitPercent/100) → ExitingUp (take-profit, hedge in loss)
//   - price ≤ Anchor × (1 − RangePercent/100)     → ExitingDown (stop-loss; hedge in profit)
//
// Hedge sizing is configured directly via HedgeNotionalUsdt — the frontend computes a
// recommendation from R, step and bet and pre-fills the field, but the user can override.
// One cycle per Start; Stop → Start re-anchors at current price preserving cumulative stats.
public enum GridHedgeMode
{
    SameTicker = 1,
    CrossTicker = 2
}

public class GridHedgeConfig
{
    public GridHedgeMode Mode { get; set; } = GridHedgeMode.SameTicker;

    public string GridSymbol { get; set; } = string.Empty;
    public string HedgeSymbol { get; set; } = string.Empty;

    // Range BELOW anchor where the grid lives. Crossing this is stop-loss.
    public decimal RangePercent { get; set; } = 10m;

    // Range ABOVE anchor where the whole bot closes in profit.
    public decimal UpperExitPercent { get; set; } = 10m;

    public decimal DcaStepPercent { get; set; } = 1m;
    public decimal TpStepPercent { get; set; } = 1m;

    // Notional in USDT for EVERY grid-level limit buy. Uniform across the whole range.
    public decimal BetUsdt { get; set; } = 100m;

    // Notional in USDT for the single hedge short, opened at start as one market trade.
    // 0 = no hedge (degenerates to a pure long-grid). Otherwise: this is the exact dollar
    // amount sent to OpenShortAsync — the frontend's "recommended" calculation is just a
    // suggestion; backend trusts whatever value the user persisted.
    public decimal HedgeNotionalUsdt { get; set; }

    public int HedgeLeverage { get; set; } = 5;

    // Only meaningful in CrossTicker mode (futures grid). SameTicker grid is spot — no leverage.
    public int GridLeverage { get; set; } = 1;
}
