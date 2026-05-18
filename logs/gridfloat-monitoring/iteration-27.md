# GridFloat monitoring — Iteration 27

**Captured**: 2026-05-15 09:13 UTC (11:13 Warsaw)
**Δ from iteration-26**: ~60 min
**Cron**: `342c898f` fired at 09:07 UTC

## TL;DR

- **1 trade**, **+$0.295 realized**, 0 errors.

## Δ Activity since 08:13 UTC

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 09:07:07 | BG-ZBT (#6) | Sell | TakeProfit#2 | 68 | 0.15123 | +$0.295 |

BG-ZBT batch #2 (fillPrice 0.14683 from iter-22 DCA fill) closed at TP price 0.14683·1.03 = 0.151235 ✓ (recorded 0.15123).

### realizedPnL delta
| Bot | iter-26 | now | Δ |
|---|---|---|---|
| BG-ZBT (#6) | $1.468 | $1.763 | +$0.295 |
| **Δ this hour** |     |        | **+$0.295** |

### State delta
| Bot | batches Δ | dcas Δ |
|---|---|---|
| BG-ZBT (#6) | 3 → 2 | 4 → 5 (slot 2 re-armed) |

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $1.043 | +$0.758 |
| #2 BB-ZBT   | $1.774 | $5.659 | +$3.885 |
| #3 BB-JCT   | $5.184 | $7.794 | +$2.610 |
| #4 BB-SAGA  | $6.484 | $10.682 | +$4.198 |
| #5 BG-BUSDT | $0     | $9.549 | **+$9.549** |
| #6 BG-ZBT   | $0     | $1.763 | +$1.763 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $4.864 | **+$4.864** |
| #9 BX-ZBT   | $0     | $0.739 | +$0.739 |
| **Total Δ from baseline** |  |  | **+$28.366** |

## Verdict for iteration 27

✅ Clean micro-hour. TP fill landed exactly at computed price.

📅 Next cron fire 10:07 UTC.
