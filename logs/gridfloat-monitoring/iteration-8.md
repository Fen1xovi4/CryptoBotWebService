# GridFloat monitoring — Iteration 8

**Captured**: 2026-05-14 14:13 UTC (16:13 Warsaw)
**Δ from iteration-7**: ~42 min
**Cron**: `342c898f` fired at 14:07 UTC
**Special**: first full post-deploy hour (deploy was at 13:24:30)

## 🎯 TL;DR

**Fix #2 (Bitget cross-symbol cancel) — VERIFIED IN PRODUCTION.**

At 14:02:09 UTC, **BG-ZBT (#6) full-closed** (TP#0 fill +$0.293) and called `CancelAllOrdersAsync("ZBTUSDT")`. With the new per-order cancellation loop:
- **Zero warnings** on BG-BUSDT (#5) or BG-OPEN (#7) in the entire 5-minute window after.
- DCAs and TPs on the other two BG bots remain alive on the exchange.
- Compare to iter-2 / iter-6 where this exact event caused 12-13 cross-symbol cancellations.

The behavioral signature has flipped from "13 cancellations" to "0 cancellations" — clean before/after.

## Δ Activity since 13:31 UTC

### Trades (13 new)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 13:35:06 | BX-BUSDT (#8) | Buy  | **Entry (new anchor)** | 9.33  | 0.5356  | — |
| 13:37:18 | BB-SAGA (#4)  | Buy  | DCA#9 fill             | 371.9 | 0.02688 | — |
| 14:01:45 | BB-ZBT (#2)   | Sell | TakeProfit#6           | 69.8  | 0.14746 | +$0.295 |
| **14:02:09** | **BG-ZBT (#6)** | **Sell** | **TakeProfit#0 (FULL CLOSE)** | **69** | **0.14825** | **+$0.293** |
| 14:05:07 | BG-ZBT (#6)   | Buy  | Entry (new anchor)     | 67    | 0.14714 | — |
| 14:05:55 | BX-BUSDT (#8) | Buy  | DCA#1 fill             | 9.62  | 0.51950 | — |
| 14:06:08 | BG-BUSDT (#5) | Buy  | DCA#1 fill             | 19    | 0.51140 | — |
| 14:08:27 | BX-BUSDT (#8) | Buy  | DCA#2 (reconcile)      | 9.93  | 0.50346 | — |
| 14:11:30 | BX-BUSDT (#8) | Sell | TakeProfit#2           | 9.93  | 0.51850 | +$0.147 |
| 14:11:39 | BB-ZBT (#2)   | Sell | TakeProfit#1           | 59    | 0.14783 | +$0.250 |
| 14:11:40 | BG-BUSDT (#5) | Sell | TakeProfit#1           | 19    | 0.52670 | +$0.287 |
| 14:11:51 | BB-ZBT (#2)   | Buy  | DCA#1 re-fill          | 59    | 0.14791 | — |
| 14:12:09 | BX-BUSDT (#8) | Sell | TakeProfit#1           | 9.62  | 0.535   | +$0.147 |

### realizedPnL delta
| Bot | iter-7 | now | Δ |
|---|---|---|---|
| BB-ZBT (#2)   | $2.856 | $3.401 | **+$0.545** (2 TPs) |
| BG-BUSDT (#5) | $6.362 | $6.649 | +$0.287 |
| BG-ZBT (#6)   | $0.293 | $0.586 | +$0.293 (full cycle) |
| BX-BUSDT (#8) | $2.503 | $2.797 | +$0.294 (2 TPs) |
| **Δ this hour** |     |        | **+$1.419** |

## Cross-symbol cancel test — PASS ✅

Timeline at 14:02:09:
```
14:02:09  BG-ZBT (#6)   💰 TP #0 filled → full close → CancelAllOrdersAsync("ZBTUSDT")
14:02:09 — 14:07:00     (5-minute window)
                        Expected pre-fix: 12-13 cross-symbol cancellation warnings on BG-BUSDT/BG-OPEN
                        Actual post-fix: 0 warnings
```

Before the fix, the Bitget V2 cancel-all endpoint nuked ALL the account's USDT-futures orders regardless of the symbol filter. With the new code (`BitgetFuturesExchangeService.CancelAllOrdersAsync` → enumerate `GetOpenOrdersAsync(symbol)` → cancel each by id), only the 7 orders that actually belonged to ZBTUSDT got cancelled.

**BG-BUSDT (#5)** state survived through the event: 1 batch + 13 DCAs (same as iter-7). **BG-OPEN (#7)** state survived: 1 batch + 6 DCAs (same as iter-7). No heal-cycle thrash needed.

## BX-BUSDT — clean new cycle after auto-recovery

After iter-7's reconcile-driven recovery at 13:30:46, the bot opened a fresh anchor at 13:35:06 (qty=9.33 @ 0.5356) and went through a full short cycle:
- 14:05:55 DCA#1 fill at 0.5195 (0.5356·0.97 = 0.51953 ✓ on grid)
- 14:08:27 DCA#2 adopted via Reconcile-DCA at 0.50346 (0.5356·0.94 = 0.50346 ✓)
- 14:11:30 TP#2 fill at 0.5185 (TP at 0.50346·1.03 = 0.51857 ✓)
- 14:12:09 TP#1 fill at 0.535 (TP at 0.51953·1.03 = 0.53512 ✓)

All four prices match the grid formula exactly. ✓ Net +$0.294 realized in 37 minutes.

## State delta
| Bot | batches Δ | dcas Δ | anchor |
|---|---|---|---|
| BB-ZBT (#2)   | 7 → 6   | 4 → 5 | unchanged |
| BB-SAGA (#4)  | 9 → 10  | 4 → 3 | unchanged (DCA#9 became batch) |
| BG-BUSDT (#5) | 1 → 1   | 13 → 13 | unchanged (DCA fill + TP fill cancelled out) |
| BG-ZBT (#6)   | 1 → 1   | 6 → 6 | 0.14394 → **0.14714** (new cycle) |
| BX-BUSDT (#8) | 0 → 1   | 0 → 10 | (was 0) → **0.5356** (new anchor opened) |

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $0.380 | +$0.095 |
| #2 BB-ZBT   | $1.774 | $3.401 | **+$1.627** |
| #3 BB-JCT   | $5.184 | $6.050 | +$0.866 |
| #4 BB-SAGA  | $6.484 | $7.233 | +$0.748 |
| #5 BG-BUSDT | $0     | $6.649 | **+$6.649** |
| #6 BG-ZBT   | $0     | $0.586 | +$0.586 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $2.797 | **+$2.797** |
| #9 BX-ZBT   | $0     | $0.148 | +$0.148 |
| **Total Δ from baseline** |  |  | **+$13.516** |

## Verdict for iteration 8

✅ **Fix #2 (Bitget per-order cancel) verified in production**. The behavioral signature of a BG full-close changed from "12-13 cross-symbol cancellations" to "zero cross-symbol cancellations" — confirmed end-to-end in a real production cycle.

🟡 Fix #1 (Stop+Start state preserve) **still not exercised** — no user-triggered Stop+Start has occurred since the deploy.

✅ Grid math holds on the post-recovery BX-BUSDT cycle (4 grid-derived fill prices, all on-formula).

✅ BG-OPEN (#7) remains the only bot at $0 — its symbol simply hasn't moved enough to trigger any DCA fill since baseline.

📅 Next cron fire 15:07 UTC.
