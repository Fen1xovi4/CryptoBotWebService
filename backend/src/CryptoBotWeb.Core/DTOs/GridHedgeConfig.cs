namespace CryptoBotWeb.Core.DTOs;

// Grid-with-Hedge strategy.
//
// Two operating modes:
//   - SameTicker (1): spot grid + futures short on the SAME symbol. Net delta = (spot long −
//     futures short notional). Hedge sized to match the FULL planned grid notional so the
//     position is delta-flat when the grid is fully loaded (range bottom).
//   - CrossTicker (2): futures grid on one symbol + futures short on a CORRELATED symbol
//     (e.g. ETH grid hedged by BTC short). Hedge notional = grid_notional × HedgeRatio × β.
//
// The grid lives strictly BELOW the anchor (entry) price, spanning RangePercent. Limit buys are
// placed at every grid level; on fill each batch gets its own reduce-only limit sell at
// fill_price × (1 + TpStep%) — per-batch TPs, not pooled (same model as GridFloat).
//
// Exit triggers — both close the WHOLE bot (grid + hedge):
//   - price ≥ Anchor × (1 + UpperExitPercent/100) → ExitingUp (grid winners locked, hedge in loss)
//   - price ≤ Anchor × (1 − RangePercent/100)     → ExitingDown (stop-loss; hedge in profit)
//
// One cycle per Start. After Done, user clicks Stop+Start to begin a fresh cycle at the
// current market price; cumulative cycle stats (HedgeRealizedPnl, GridRealizedPnl,
// CompletedCycles) persist across cycles.
public enum GridHedgeMode
{
    SameTicker = 1, // spot grid + same-ticker futures short (Bybit-only in V1)
    CrossTicker = 2 // futures grid + different-ticker futures short
}

public class GridHedgeConfig
{
    public GridHedgeMode Mode { get; set; } = GridHedgeMode.SameTicker;

    // Long-grid leg symbol. SameTicker → spot symbol (ETHUSDT). CrossTicker → futures symbol.
    public string GridSymbol { get; set; } = string.Empty;

    // Futures short leg symbol. SameTicker → mirror of GridSymbol (auto-filled on start if blank).
    // CrossTicker → user-supplied correlated futures symbol (e.g. BTCUSDT).
    public string HedgeSymbol { get; set; } = string.Empty;

    // Range BELOW anchor where the grid lives. Lower bound = anchor × (1 − RangePercent/100).
    // Crossing this bound triggers stop-loss exit (ExitingDown).
    public decimal RangePercent { get; set; } = 10m;

    // Take-profit trigger above anchor — when price climbs this far the whole bot closes:
    // grid (mostly in TP profit already) + hedge (in loss). Net = grid PnL − hedge loss.
    public decimal UpperExitPercent { get; set; } = 2m;

    // Tier list shared with GridFloat for the exact same per-tier override semantics:
    // UpToPercent (offset from anchor where the tier ends), SizeUsdt (per-fill notional),
    // optional per-tier DcaStepPercent / TpStepPercent overrides.
    public List<GridFloatTier> Tiers { get; set; } = new();

    // Default DCA spacing (% of anchor) — applies to any tier without its own override.
    public decimal DcaStepPercent { get; set; } = 1m;

    // Default per-batch TP offset (% of fill price) — applies to any tier without its own override.
    public decimal TpStepPercent { get; set; } = 1m;

    // Fraction of the planned grid notional to short on the hedge leg. 1.0 = full coverage,
    // 0.0 = no hedge. SameTicker typically uses 1.0; CrossTicker is the configurable knob.
    public decimal HedgeRatio { get; set; } = 1.0m;

    // Correlation coefficient — multiplies the hedge notional in CrossTicker mode to compensate
    // for differing β between the grid and hedge tickers (e.g. ETH ≈ 1.2× BTC volatility).
    // SameTicker ignores this; effective hedge = grid × 1.0 × 1.0.
    public decimal Beta { get; set; } = 1.0m;

    // Exchange leverage applied to each leg before the first trade.
    public int HedgeLeverage { get; set; } = 5;

    // Only meaningful in CrossTicker mode (futures grid). SameTicker grid is spot — no leverage.
    public int GridLeverage { get; set; } = 1;
}
