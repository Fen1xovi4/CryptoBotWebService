# GridFloat monitoring — Iteration 18

**Captured**: 2026-05-15 00:13 UTC (02:13 Warsaw)
**Δ from iteration-17**: ~60 min
**Cron**: `342c898f` fired at 00:07 UTC

## TL;DR

- **13 trades**, **+$2.180 realized this hour**.
- 3 reconcile-DCA warnings (all normal — Bybit + Bitget poll-lag, reconcile caught and adopted).
- 🆕 **BB-SAGA reached level 11** for the first time — deepest the static-range bot has gone.

## Δ Activity since 23:13 UTC

### Trades (13 new)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 23:24:42 | BX-BUSDT (#8) | Buy  | DCA#4 fill          | 21.21 | 0.4713  | — |
| 23:24:46 | BX-BUSDT (#8) | Buy  | DCA#5 (reconcile)   | 21.21 | 0.45526 | — |
| 23:24:59 | BX-BUSDT (#8) | Sell | TakeProfit#5        | 21.21 | 0.4700  | +$0.309 |
| 23:36:09 | BB-SAGA (#4)  | Buy  | DCA#8 fill          | 357.2 | 0.02799 | — |
| 23:42:41 | BG-BUSDT (#5) | Buy  | DCA#4 (reconcile)   | 43    | 0.46402 | — |
| 23:47:16 | BB-JCT (#3)   | Sell | TakeProfit#1        | 2200  | 0.00453 | +$0.287 |
| 23:49:50 | BB-SAGA (#4)  | Buy  | DCA#9 fill          | 371.9 | 0.02688 | — |
| 23:52:01 | BB-SAGA (#4)  | Buy  | DCA#10 fill         | 387.8 | 0.02578 | — |
| **23:52:06** | **BB-SAGA (#4)** | **Buy** | **DCA#11 (reconcile, NEW DEEPEST)** | 387.9 | 0.02467 | — |
| 23:52:15 | BB-SAGA (#4)  | Sell | TakeProfit#11       | 387.9 | 0.02573 | +$0.405 |
| 23:59:13 | BB-SAGA (#4)  | Sell | TakeProfit#10       | 387.8 | 0.02655 | +$0.295 |
| 00:05:28 | BG-BUSDT (#5) | Sell | TakeProfit#4        | 43    | 0.4779  | +$0.589 |
| 00:09:07 | BX-BUSDT (#8) | Sell | TakeProfit#4        | 21.21 | 0.4854  | +$0.295 |

### BB-SAGA deep dive

For the first time since iter-1, BB-SAGA reached level k=11. Static range allows up to k=12 (anchor=0.03683, bound=0.022648):
- k=10 fill at 0.02578 = 0.03683·0.70 ✓
- k=11 fill at 0.02467 = 0.03683·0.67 ✓
- k=12 limit would be 0.03683·0.64 = 0.023571 — still inside bound 0.022648 (not yet hit)

Within 15 seconds of DCA#11 filling, price bounced and TP#11 fired at 0.02573 — netting +$0.405. The "drift in static mode" mechanic the spec mentions in action.

### realizedPnL delta
| Bot | iter-17 | now | Δ |
|---|---|---|---|
| BB-JCT (#3)   | $6.929 | $7.215 | +$0.287 |
| BB-SAGA (#4)  | $9.504 | $10.204 | **+$0.700** |
| BG-BUSDT (#5) | $8.090 | $8.678 | +$0.589 |
| BX-BUSDT (#8) | $3.679 | $4.283 | +$0.604 |
| **Δ this hour** |     |        | **+$2.180** |

### Three reconcile-DCA warnings (all normal)

| Time | Bot | State qty | Exchange qty | Adopted |
|---|---|---|---|---|
| 23:24:46 | BX-BUSDT | 60.34 | 81.55 | DCA#5 (21.21) |
| 23:42:41 | BG-BUSDT | 77 | 120 | DCA#4 (43) |
| 23:52:06 | BB-SAGA | 3558.0 | 3945.9 | DCA#11 (387.9) |

Reconcile design working exactly as intended: Poll missed → defensive backstop caught → adopted with correct LevelIdx and TP.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3) | 2 → 1 | 9 → 10 |
| BB-SAGA (#4) | 8 → 10 | 5 → 2 (multiple DCAs filled, 2 TPs fired) |
| BG-BUSDT (#5) | 4 → 4 | 10 → 10 (DCA fill + TP fill cancel out) |
| BX-BUSDT (#8) | 4 → 4 | 7 → 7 (DCA fill + TP fill cancel out) |

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $0.949 | +$0.664 |
| #2 BB-ZBT   | $1.774 | $5.364 | +$3.589 |
| #3 BB-JCT   | $5.184 | $7.215 | **+$2.032** |
| #4 BB-SAGA  | $6.484 | $10.204 | **+$3.720** |
| #5 BG-BUSDT | $0     | $8.678 | **+$8.678** |
| #6 BG-ZBT   | $0     | $1.173 | +$1.173 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $4.283 | **+$4.283** |
| #9 BX-ZBT   | $0     | $0.591 | +$0.591 |
| **Total Δ from baseline** |  |  | **+$24.731** |

## Verdict for iteration 18

✅ Healthy harvest hour. BB-SAGA's deep-level adoption + immediate TP demonstrates the static-grid mechanic's resilience.

✅ All three reconcile warnings led to clean DCA adoptions with correct TPs placed.

📅 Next cron fire 01:07 UTC.
