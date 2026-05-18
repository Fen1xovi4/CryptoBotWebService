# GridFloat monitoring — Iteration 11

**Captured**: 2026-05-14 17:13 UTC (19:13 Warsaw)
**Δ from iteration-10**: ~60 min
**Cron**: `342c898f` fired at 17:07 UTC

## TL;DR

- **10 trades**, **+$1.097 realized this hour** — quiet recovery hour.
- **Zero warnings, zero errors** across all 9 bots — first warning-free hour of the session.
- 🔄 **BB-XRP (#1) first full cycle since baseline** — TP#0 fired at 16:59:41, new anchor at 1.4802 opened immediately after (dynamic range bot, so anchor recenters).
- 🔄 BX-ZBT (#9) full cycle again (third running).

## Δ Activity since 16:13 UTC

### Trades (10 new)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 16:18:29 | BB-ZBT (#2)  | Sell | TakeProfit#1   | 59       | 0.15673 | +$0.265 |
| 16:18:40 | BB-ZBT (#2)  | Buy  | DCA#1 re-fill  | 59       | 0.15678 | — |
| 16:25:08 | BB-ZBT (#2)  | Sell | TakeProfit#4   | 65       | 0.15755 | +$0.294 |
| 16:25:22 | BX-ZBT (#9)  | Sell | TakeProfit#0   | 32.647   | 0.15774 | +$0.148 (full close) |
| 16:29:46 | BB-JCT (#3)  | Sell | TakeProfit#3   | 2400     | 0.0042529 | +$0.293 |
| 16:30:05 | BX-ZBT (#9)  | Buy  | Entry (new)    | 31.565   | 0.1584  | — |
| 16:36:47 | BX-BUSDT (#8)| Buy  | DCA#2 fill     | 9.93     | 0.50340 | — |
| 16:47:53 | BG-BUSDT (#5)| Buy  | DCA#2 fill     | 20       | 0.49560 | — |
| **16:59:41** | **BB-XRP (#1)** | **Sell** | **TakeProfit#0 (FULL CLOSE)** | 6.8 | 1.4798 | +$0.095 |
| 17:00:03 | BB-XRP (#1)  | Buy  | Entry (new anchor) | 6.7  | 1.4802  | — |

### realizedPnL delta
| Bot | iter-10 | now | Δ |
|---|---|---|---|
| BB-XRP (#1)   | $0.474 | $0.570 | +$0.095 |
| BB-ZBT (#2)   | $4.804 | $5.364 | +$0.559 |
| BB-JCT (#3)   | $6.345 | $6.638 | +$0.293 |
| BX-ZBT (#9)   | $0.444 | $0.591 | +$0.148 |
| **Δ this hour** |     |        | **+$1.097** |

## 🔄 BB-XRP first full cycle

This was the bot's first complete cycle (anchor → flat → cooldown → new anchor) since the iter-1 baseline. As a **dynamic-range** bot (`useStaticRange=false`), the new anchor 1.4802 re-centered the grid → 10 fresh DCAs (k=1..10 since maxTier=10%, step=1%):
- k=1: 1.4802·0.99 = **1.465398**
- k=10: 1.4802·0.90 = **1.33218**

State shows 1 batch (anchor) + 10 DCAs — matches the dynamic-range slot computation `floor(maxTierPct/step) = floor(10/1) = 10`. ✓

## State delta
| Bot | batches Δ | dcas Δ | anchor |
|---|---|---|---|
| BB-XRP (#1)   | 1 → 1 | 10 → 10 | 1.4652 → **1.4802** (new cycle) |
| BB-ZBT (#2)   | 5 → 4 | 6 → 7 | unchanged |
| BB-JCT (#3)   | 4 → 3 | 7 → 8 | unchanged |
| BG-BUSDT (#5) | 2 → 3 | 12 → 11 | unchanged |
| BX-BUSDT (#8) | 2 → 3 | 9 → 8  | unchanged |
| BX-ZBT (#9)   | 1 → 1 | 6 → 6 | 0.15315 → **0.1584** (new cycle) |

## Bot health snapshot

- **All 9 bots Status=Running**, no Stops/Pauses since deploy. Fix #1 still untested in real conditions.
- **Zero warnings**: no reconcile-DCA, no reconcile-TP, no cancel-symbol issues. Quietest hour to date.
- The 3 BG bots and BG-OPEN especially have been clean: zero anomalous logs since the fix went live ~3h45m ago.

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $0.570 | +$0.285 |
| #2 BB-ZBT   | $1.774 | $5.364 | **+$3.589** |
| #3 BB-JCT   | $5.184 | $6.638 | +$1.454 |
| #4 BB-SAGA  | $6.484 | $9.212 | +$2.728 |
| #5 BG-BUSDT | $0     | $7.798 | **+$7.798** |
| #6 BG-ZBT   | $0     | $1.173 | +$1.173 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $3.241 | **+$3.241** |
| #9 BX-ZBT   | $0     | $0.591 | +$0.591 |
| **Total Δ from baseline** |  |  | **+$20.859** |

## Verdict for iteration 11

✅ Cleanest hour yet (0 warnings / 0 errors / 10 trades / all bots healthy).

✅ Dynamic-range mechanics validated on BB-XRP: anchor re-center → fresh 10-slot DCA ladder.

🟡 BB-SAGA, BG-BUSDT, BG-OPEN, BX-BUSDT haven't done full closes recently — each is sitting on a deep DCA ladder waiting for the symbol to reverse up.

📅 Next cron fire 18:07 UTC.
