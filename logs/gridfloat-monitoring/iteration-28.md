# GridFloat monitoring — Iteration 28

**Captured**: 2026-05-15 10:13 UTC (12:13 Warsaw)
**Δ from iteration-27**: ~60 min
**Cron**: `342c898f` fired at 10:07 UTC

## TL;DR

- **2 TPs**, **+$0.887 realized**, 0 errors.
- 🏆 BB-SAGA TP#11 (tier-2 batch) — biggest single-trade today.

## Δ Activity since 09:13 UTC

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 09:32:26 | BB-SAGA (#4)  | Sell | TakeProfit#11 | 810.5 | 0.02541 | **+$0.592** |
| 10:12:48 | BX-BUSDT (#8) | Sell | TakeProfit#5  | 21.96 | 0.4688  | +$0.295 |

### Grid math
- **BB-SAGA TP#11**: batch fillPrice 0.02467, expected TpPrice = 0.02467·1.03 = **0.0254101** → recorded 0.02541 ✓. Tier-2 size ($20) → qty 20/0.02467 = 810.7 ≈ 810.5 ✓.
- **BX-BUSDT TP#5**: batch fillPrice 0.4552, expected TpPrice = 0.4552·1.03 = **0.468856** → recorded 0.4688 ✓.

### realizedPnL delta
| Bot | iter-27 | now | Δ |
|---|---|---|---|
| BB-SAGA (#4)  | $10.682 | $11.274 | **+$0.592** |
| BX-BUSDT (#8) | $4.864  | $5.159  | +$0.295 |
| **Δ this hour** |     |        | **+$0.887** |

### State delta
| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-SAGA (#4)  | 12 → 11 | 10 → 11 (slot 11 re-armed) |
| BX-BUSDT (#8) | 6 → 5   | 5 → 6 (slot 5 re-armed) |

## Note on BB-SAGA's expanded ladder

BB-SAGA still has 11 DCAs sitting at k=12..22 from the Fix #4 widening (one slot taken back as batch when k=11 fired via the re-arm). Those deeper levels (k=13..21) at $20 size each are dormant until price drops further (currently 0.02541 vs deepest DCA #21 at 0.013627 — needs another 46% drop to fully fill).

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $1.043 | +$0.758 |
| #2 BB-ZBT   | $1.774 | $5.659 | +$3.885 |
| #3 BB-JCT   | $5.184 | $7.794 | +$2.610 |
| #4 BB-SAGA  | $6.484 | $11.274 | **+$4.790** |
| #5 BG-BUSDT | $0     | $9.549 | **+$9.549** |
| #6 BG-ZBT   | $0     | $1.763 | +$1.763 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $5.159 | **+$5.159** |
| #9 BX-ZBT   | $0     | $0.739 | +$0.739 |
| **Total Δ from baseline** |  |  | **+$29.253** |

## Verdict for iteration 28

✅ Clean hour. Both TPs landed exactly on formula. BB-SAGA's tier-2 anchor batch contributed its first $0.59 TP.

📅 Next cron fire 11:07 UTC.
