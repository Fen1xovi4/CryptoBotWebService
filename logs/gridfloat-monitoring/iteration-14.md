# GridFloat monitoring — Iteration 14

**Captured**: 2026-05-14 20:13 UTC (22:13 Warsaw)
**Δ from iteration-13**: ~60 min
**Cron**: `342c898f` fired at 20:07 UTC

## TL;DR

- **5 trades** — all DCA fills, **zero TPs**.
- **+$0.000 realized this hour** (price went down → bots accumulated, no profit-taking).
- 1 warning (normal reconcile-DCA on BB-XRP), 0 errors.
- Fixes still silent.

## Δ Activity since 19:13 UTC

### Trades (5 new, all DCA fills)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 19:15:09 | BB-XRP (#1)  | Buy | Entry (post-cooldown) | 6.50 | 1.5378 | — |
| 19:16:09 | BG-BUSDT (#5)| Buy | DCA#3 fill            | 20   | 0.4798 | — |
| 19:29:21 | BB-XRP (#1)  | Buy | DCA#1 (reconcile)     | 6.50 | 1.5224 | — |
| 19:39:05 | BB-ZBT (#2)  | Buy | DCA#5 fill            | 67.3 | 0.14841 | — |
| 20:06:07 | BB-XRP (#1)  | Buy | DCA#2 fill            | 6.60 | 1.507  | — |

### Price direction
All trades this hour are buy-side (entry + DCAs). No reduce-only/sell trades = no TP fills = no realized PnL. The chain implies symbols generally drifted DOWN through their DCA grids without bouncing back to TP levels.

This is the GridFloat strategy's "loading phase" — bots are stacking inventory waiting for a reversal.

## Grid math — BB-XRP new cycle ✓

Post-cooldown anchor 1.5378 (after iter-13's 19:12 full close). Step=1%, dynamic range. DCA levels:
- k=1: 1.5378·0.99 = **1.522422** → trade at 1.52242 ✓
- k=2: 1.5378·0.98 = **1.507044** → trade at 1.5070 ✓
- k=3: 1.5378·0.97 = 1.491666 (not yet hit)
- ...

Both filled DCAs land exactly on formula.

## Reconcile-DCA #1 (normal)
19:29:21 BB-XRP: state=6.5 vs exchange=13 (+6.5 = DCA#1 missed by poll, adopted). Normal Bybit poll-lag pattern, no concern.

## State delta

| Bot | batches Δ | dcas Δ | anchor |
|---|---|---|---|
| BB-XRP (#1)   | 0 → 3 | 0 → 8 | 0 → **1.5378** (new anchor after cooldown + 2 DCAs filled) |
| BB-ZBT (#2)   | 5 → 6 | 6 → 5 | unchanged |
| BG-BUSDT (#5) | 3 → 4 | 11 → 10 | unchanged |

## Cumulative scoreboard (unchanged this hour)

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $0.949 | +$0.664 |
| #2 BB-ZBT   | $1.774 | $5.364 | +$3.589 |
| #3 BB-JCT   | $5.184 | $6.929 | +$1.745 |
| #4 BB-SAGA  | $6.484 | $9.212 | +$2.728 |
| #5 BG-BUSDT | $0     | $8.090 | **+$8.090** |
| #6 BG-ZBT   | $0     | $1.173 | +$1.173 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $3.679 | **+$3.679** |
| #9 BX-ZBT   | $0     | $0.591 | +$0.591 |
| **Total Δ from baseline** |  |  | **+$22.259** |

## Verdict for iteration 14

✅ Quiet accumulation hour — strategy behavior matches design (load on weakness, harvest on bounce). Bots are now sitting on more inventory at lower prices, primed for the next reversal.

✅ Both fixes silent and stable.

📅 Next cron fire 21:07 UTC.
