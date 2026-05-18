# GridFloat monitoring — Iteration 30

**Captured**: 2026-05-15 12:13 UTC (14:13 Warsaw)
**Δ from iteration-29**: ~60 min
**Cron**: `342c898f` fired at 12:07 UTC

## TL;DR

- **3 trades on BB-JCT**, **+$0.177 realized**.
- 2 reconcile-DCA warnings (normal), 0 errors.
- 💰 **Crossed $30 cumulative? Not quite — at $29.525.**

## Δ Activity since 11:13 UTC

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 11:57:10 | BB-JCT (#3) | Buy  | DCA#2 (reconcile) | 800  | 0.00441 | — |
| 11:57:22 | BB-JCT (#3) | Buy  | DCA#3 (reconcile) | 1400 | 0.00427 | — |
| 11:58:09 | BB-JCT (#3) | Sell | TakeProfit#3      | 1400 | 0.00440 | +$0.177 |

### Partial fills handled correctly by Reconcile

Both DCA-adoption events were partial fills on Bybit:
- DCA#2 placement qty was likely ~2300 (= $10/0.00441 rounded), but only **800** got filled. Reconcile adopted that exact 800 via `adoptQty = min(dca.Qty, qtyExcess)`.
- DCA#3 placement qty was ~2300, but only **1400** got filled. Reconcile adopted 1400.

The TP#3 immediately fired on the newly-adopted batch (#3) at TpPrice 0.00427254·1.03 = **0.00440072** → recorded 0.00440070 ✓. PnL = 1400·(0.00440-0.00427) - commission ≈ $0.177 ✓.

Grid prices (anchor 0.0046951, step 3%):
- k=2: 0.0046951·0.94 = **0.0044134** → recorded 0.00441 ✓
- k=3: 0.0046951·0.91 = **0.0042725** → recorded 0.00427 ✓

### realizedPnL delta
| Bot | iter-29 | now | Δ |
|---|---|---|---|
| BB-JCT (#3) | $7.794 | $7.971 | +$0.177 |
| **Δ this hour** |     |        | **+$0.177** |

### State delta
| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3) | 2 → 3 | 9 → 8 |

## Cumulative scoreboard

**Total Δ from baseline: +$29.525**

## Verdict for iteration 30

✅ Reconcile-DCA's `adoptQty = min(placed.Qty, qtyExcess)` invariant verified again. Partial fills don't create phantom over-adoption.

📅 Next cron fire 13:07 UTC.
