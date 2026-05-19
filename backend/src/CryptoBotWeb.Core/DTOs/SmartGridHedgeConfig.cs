namespace CryptoBotWeb.Core.DTOs;

// SmartGridHedge — port of the GridHedgeSimulator "Symmetric grid + static short hedge" model.
//
// Geometric grid around P0 (cycle anchor):
//   U_k = P0 * (1 + Step)^k, k = 1..NUp     — upper rungs
//   D_k = P0 * (1 - Step)^k, k = 1..NDown   — lower rungs
//   HBreak = U_NUp, LBreak = D_NDown        — hard-close boundaries
//
// At t = 0: initial long qInit = LotUsd / P0 (positionIdx=1) + static short Q_hedge
// (positionIdx=2). DCA layer on the way down (paired buy/sell limits, always recyclable);
// SkimMode controls the upper-cell behavior. On HBreak/LBreak touch — hard-close everything
// and (if AutoRestart) immediately re-anchor at the new market price.
//
// Account must be Bybit, switched to hedge mode (see CLAUDE.md "Exception: SmartGridHedge").
public enum SmartGridSkimMode
{
    // Upper cells fire exactly once each. On U_k cross: sell the excess (qInit * U_k − LotUsd)
    // from the initial long, locking in trim profit. No recycle. Lowest reward in sideways,
    // but no upper-cycle loss on HBreak.
    OneShot = 0,

    // Variant A — recycle paired shorts sized to the excess only (LotUsd*Step / U_k coins).
    // Profit per cell up-down swing = LotUsd*Step²/(1+Step) (small). Loss on HBreak ≈ zero.
    ExcessRecycle = 1,

    // Variant B — recycle paired shorts sized to the FULL lot (LotUsd / U_k coins).
    // Profit per swing = LotUsd*Step/(1+Step) (big). Loss on HBreak is large.
    FullRecycle = 2
}

public class SmartGridHedgeConfig
{
    // Bybit linear-perp symbol, e.g. "BTCUSDT".
    public string Symbol { get; set; } = string.Empty;

    // USDT notional per grid lot (initial long, each DCA buy, each skim short notional in
    // Variant B). qInit_0 = LotUsd / P0.
    public decimal LotUsd { get; set; } = 50m;

    // Geometric step as a fraction (0.01 = 1% per rung).
    public decimal Step { get; set; } = 0.01m;

    // Number of upper rungs (HBreak = P0 * (1+Step)^NUp). Skim fires on k=1..NUp-1.
    public int NUp { get; set; } = 10;

    // Number of lower rungs (LBreak = P0 * (1-Step)^NDown). DCA fires on k=1..NDown-1.
    public int NDown { get; set; } = 10;

    public SmartGridSkimMode SkimMode { get; set; } = SmartGridSkimMode.OneShot;

    public int Leverage { get; set; } = 5;

    // null → handler computes Q_hedge via SymmetricAnalyticHedgeOptimizer at cycle start
    // (re-computed on each auto-restart with the new P0). Otherwise the user-supplied value
    // is used verbatim, in COINS (not USDT).
    public decimal? QHedgeOverride { get; set; }

    // After hard-close (HBreak or LBreak), open a new cycle at the current market price
    // using the same params. Recommended default; the user can flip it off to inspect the
    // closed cycle before re-arming manually.
    public bool AutoRestart { get; set; } = true;

    // Fee tier used by the analytic optimizer. Defaults match Bybit V5 VIP-0 linear perp:
    // maker 2 bps, taker 5.5 bps. UI exposes these as overridable for VIP accounts.
    public decimal MakerFeeBps { get; set; } = 2m;
    public decimal TakerFeeBps { get; set; } = 5.5m;
}
