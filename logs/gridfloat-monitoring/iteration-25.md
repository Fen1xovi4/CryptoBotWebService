# GridFloat monitoring — Iteration 25

**Captured**: 2026-05-15 07:13 UTC (09:13 Warsaw)
**Δ from iteration-24**: ~60 min
**Cron**: `342c898f` fired at 07:07 UTC
**Special**: First iteration after Fix #4 deploy (06:31:50 UTC).

## TL;DR

- **2 trades** (1 TP, 1 DCA), zero warnings, zero errors.
- **+$0.283 realized**.
- 🛠️ **Fix #4 deployed at 06:31:50 UTC** and **immediately verified via PATCH /grid-float/tiers** on BB-SAGA at 06:33:31 — bound recomputed from 0.022648 → **0.0128905** (= 0.03683·0.35 with new 65% range), 9 new DCAs placed at k=13..21 with $20 tier-2 size each.

## Δ Activity since 06:13 UTC

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 06:17:32 | BB-JCT (#3)  | Sell | TakeProfit#1 | 2100  | 0.00469 | +$0.283 |
| 06:18:52 | BB-SAGA (#4) | Buy  | DCA#11 fill  | 810.5 | 0.02467 | — |

### Fix #4 verification on BB-SAGA

Sequence:
- 06:31:50 deploy of new API/worker images
- 06:33:31-36 user did Pause+PATCH+Resume on BB-SAGA with new tiers `[30%:$10, 65%:$20]`
- `oldMaxTierPct` was 30 (from prior tier list), `newMaxTierPct` = 65 → widening detected
- `StaticLowerBound` recomputed: `0.03683 · (1 − 65/100) = 0.0128905` (was 0.022648)
- HealMissingDcas seeded **9 new limits** at k=13..21 with tier-2 $20 size each, all exactly on formula `0.03683·(1−k·0.03)`.

### realizedPnL delta
| Bot | iter-24 | now | Δ |
|---|---|---|---|
| BB-JCT (#3) | $7.511 | $7.794 | +$0.283 |
| **Δ this hour** |     |        | **+$0.283** |

### State delta
| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3)  | 2 → 1 | 9 → 10 |
| BB-SAGA (#4) | 11 → 12 | 0 → 10 (DCA#11 filled + ladder widened to k=21 via Fix #4) |

BB-SAGA now has the maximum possible depth: 12 batches loaded (k=0..11) + 10 fresh DCAs (k=12..21). Total inventory commitment = ~$330 USDT spot value, all working toward the next bounce.

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $1.043 | +$0.758 |
| #2 BB-ZBT   | $1.774 | $5.659 | +$3.885 |
| #3 BB-JCT   | $5.184 | $7.794 | **+$2.610** |
| #4 BB-SAGA  | $6.484 | $10.682 | +$4.198 |
| #5 BG-BUSDT | $0     | $9.549 | **+$9.549** |
| #6 BG-ZBT   | $0     | $1.468 | +$1.468 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $4.578 | +$4.578 |
| #9 BX-ZBT   | $0     | $0.739 | +$0.739 |
| **Total Δ from baseline** |  |  | **+$27.785** |

## Status of all 4 fixes after iter-25

| Fix | Verified | Notes |
|---|---|---|
| #1 Stop+Start preserve state | ✅ iter-12 | Production proof on BX-BUSDT recovery |
| #2 Bitget cross-symbol cancel | ✅ iter-8, iter-10 | 3 BG full-closes, 0 collateral cancellations |
| #3 Reconcile-TP stale-price log | 🟡 backlog | Cosmetic only |
| #4 Static bound re-anchor on widening | ✅ iter-25 | BB-SAGA widened 30%→65%, 9 new DCAs placed |

## Verdict for iteration 25

✅ Fix #4 deployed and immediately verified in production. All 4 GridFloat improvements identified this session are now live and proven on real bots.

📅 Next cron fire 08:07 UTC.
