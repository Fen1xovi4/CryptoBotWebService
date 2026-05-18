# GridFloat monitoring — Iteration 13

**Captured**: 2026-05-14 19:13 UTC (21:13 Warsaw)
**Δ from iteration-12**: ~60 min
**Cron**: `342c898f` fired at 19:07 UTC

## TL;DR

- **12 trades**, **+$0.872 realized this hour**.
- 🚀 **BB-XRP (#1) crushed it**: three full cycles in 52 minutes (+$0.284). Dynamic-range bot benefiting from XRP volatility.
- **Zero warnings, zero errors** — second clean hour in a row.
- Both fixes still holding.

## Δ Activity since 18:13 UTC

### Trades (12 new)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 18:18:25 | BX-BUSDT (#8) | Sell | TakeProfit#2 | 9.93   | 0.51850 | +$0.148 |
| 18:18:25 | BX-BUSDT (#8) | Sell | TakeProfit#3 | 10.25  | 0.50190 | +$0.148 |
| 18:18:37 | BG-BUSDT (#5) | Sell | TakeProfit#2 | 20     | 0.51040 | +$0.292 |
| **18:19:59** | **BB-XRP (#1)** | **Sell** | **TakeProfit#0 FULL CLOSE #1** | 6.60 | 1.5125 | +$0.094 |
| 18:20:09 | BB-XRP (#1)   | Buy  | Entry (new anchor) | 6.50 | 1.5154 | — |
| 18:21:01 | BX-BUSDT (#8) | Buy  | DCA#2 re-fill | 9.93   | 0.50340 | — |
| 18:27:51 | BG-BUSDT (#5) | Buy  | DCA#2 re-fill | 20     | 0.49560 | — |
| **18:29:14** | **BB-XRP (#1)** | **Sell** | **TakeProfit#0 FULL CLOSE #2** | 6.50 | 1.5305 | +$0.094 |
| 18:30:02 | BB-XRP (#1)   | Buy  | Entry (new anchor) | 6.50 | 1.5342 | — |
| 18:51:18 | BX-ZBT (#9)   | Buy  | DCA#2 fill | 33.58  | 0.14889 | — |
| 19:04:37 | BX-BUSDT (#8) | Buy  | DCA#3 re-fill | 10.25  | 0.4873  | — |
| **19:12:00** | **BB-XRP (#1)** | **Sell** | **TakeProfit#0 FULL CLOSE #3** | 6.50 | 1.5495 | +$0.095 |

### BB-XRP triple-cycle visualization

```
18:20  ────●─────●─────●─────●─────●─────●─────  anchor=1.5154 → 10 DCAs
            ↓
18:29  ●●●●●●●●●●●●●●● TP#0 (+0.094, ~10min cycle)
            ↓
18:30  ────●─────●─────●─────●─────●─────●─────  anchor=1.5342 → 10 DCAs
            ↓
19:12  ●●●●●●●●●●●●●●● TP#0 (+0.095, ~42min cycle)
            ↓ (now in 1-bar cooldown)
```

3 cycles × $0.095 ≈ +$0.284 in one hour from a single bot, on a $10-anchor and 1% TP step. The dynamic-range mechanic shines when the symbol has fast 1% oscillations.

### realizedPnL delta
| Bot | iter-12 | now | Δ |
|---|---|---|---|
| BB-XRP (#1)   | $0.665 | $0.949 | **+$0.284** (3 full cycles) |
| BG-BUSDT (#5) | $7.798 | $8.090 | +$0.292 |
| BX-BUSDT (#8) | $3.383 | $3.679 | +$0.296 |
| **Δ this hour** |     |        | **+$0.872** |

## Grid math — ✓ all entries land on tier formula

BB-XRP cycle 2 anchor 1.5154, step 1%, tier1 $10/anchor base:
- TP price for anchor: 1.5154·1.01 = **1.530554** → recorded TP fill at 1.5305 ✓ (matches)

BB-XRP cycle 3 anchor 1.5342, step 1%:
- TP: 1.5342·1.01 = **1.549542** → recorded TP fill at 1.5495 ✓

BX-BUSDT TP#3 at 0.5019 vs computed: batch #3 (DCA fill at 0.48739 from iter-11 trace) TP=0.48739·1.03 = **0.501912**. Recorded TP fill 0.50190. ✓

All on-formula.

## State delta

| Bot | batches Δ | dcas Δ | anchor |
|---|---|---|---|
| BB-XRP (#1)   | 1 → 0 | 10 → 0 | post-FULL-CLOSE cooldown |
| BG-BUSDT (#5) | 3 → 3 | 11 → 11 | unchanged |
| BX-BUSDT (#8) | 4 → 4 | 7 → 7  | unchanged |
| BX-ZBT (#9)   | 1 → 3 | 6 → 4  | unchanged (2 DCAs filled) |

BB-XRP at iter-13 close: `batches=0, dcaOrders=0, anchorPrice=0` — `OnFullClose` was called for the 19:12 TP and the bot is in the 1-bar cooldown waiting to open a fresh anchor on the next 5m close.

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $0.949 | **+$0.664** (5 full cycles since baseline) |
| #2 BB-ZBT   | $1.774 | $5.364 | +$3.589 |
| #3 BB-JCT   | $5.184 | $6.929 | +$1.745 |
| #4 BB-SAGA  | $6.484 | $9.212 | +$2.728 |
| #5 BG-BUSDT | $0     | $8.090 | **+$8.090** |
| #6 BG-ZBT   | $0     | $1.173 | +$1.173 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $3.679 | **+$3.679** |
| #9 BX-ZBT   | $0     | $0.591 | +$0.591 |
| **Total Δ from baseline** |  |  | **+$22.259** |

## Verdict for iteration 13

✅ Calmest data hour to date (0 warnings, 0 errors) — both fixes' steady-state behavior is silent and clean.

✅ BB-XRP demonstrates that dynamic-range mode is the right tooling for fast-oscillating symbols. Got 3 cycles for $0.284 with $10 commitment — that's ~3%/hour return on margin.

📅 Next cron fire 20:07 UTC.
