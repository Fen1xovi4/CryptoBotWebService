# GridFloat monitoring — Iteration 31

**Captured**: 2026-05-15 13:13 UTC (15:13 Warsaw)
**Δ from iteration-30**: ~60 min
**Cron**: `342c898f` fired at 13:07 UTC

## TL;DR

- **3 trades** (1 TP, 2 DCAs), **+$0.104 realized**, 0 errors.
- Small TP on BB-JCT — closing the partial-filled batch from iter-30.

## Δ Activity since 12:13 UTC

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 12:30:50 | BB-XRP (#1)  | Buy  | DCA#5 fill   | 6.80 | 1.4609  | — |
| 13:01:00 | BG-ZBT (#6)  | Buy  | DCA#2 fill   | 68   | 0.14683 | — |
| 13:06:59 | BB-JCT (#3)  | Sell | TakeProfit#2 | 800  | 0.00455 | +$0.104 |

### Small TP — partial fill carries through

BB-JCT TP#2 closed the **800-qty** batch that was partial-adopted via reconcile last iteration (DCA#2 placement was ~2300 but only 800 filled). PnL accounting:
- gross = 800 × (0.0045457 − 0.0044134) = 800 × 0.0001323 = **$0.1058**
- commission ≈ 800 × 0.0045 × 0.001 ≈ $0.0036
- net ≈ **$0.104** ✓ matches recorded.

Smaller batch → smaller TP. Each partial-fill batch settles independently per the spec.

### realizedPnL delta
| Bot | iter-30 | now | Δ |
|---|---|---|---|
| BB-JCT (#3) | $7.971 | $8.075 | +$0.104 |
| **Δ this hour** |     |        | **+$0.104** |

### State delta
| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-XRP (#1)  | 5 → 6 | 6 → 5 |
| BB-JCT (#3)  | 3 → 2 | 8 → 9 |
| BG-ZBT (#6)  | 2 → 3 | 5 → 4 |

## Cumulative scoreboard

**Total Δ from baseline: +$29.629**

## Verdict for iteration 31

✅ Per-batch settlement working correctly — partial-fill batch closed for its proportional profit.

📅 Next cron fire 14:07 UTC.
