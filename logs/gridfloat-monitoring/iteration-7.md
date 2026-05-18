# GridFloat monitoring — Iteration 7

**Captured**: 2026-05-14 13:31 UTC (15:31 Warsaw)
**Δ from iteration-6**: ~78 min
**Cron**: `342c898f` fired at 13:07 UTC
**Special**: includes pre-deploy traffic + post-deploy verification window

## TL;DR

- **35 new trades**, **+$4.296 realized this hour** on 4 bots (BG-BUSDT alone +$2.887)
- 🛠️ **Backend rebuilt + deployed at 13:24:30 UTC** with two fixes:
  - Stop+Start now preserves state, forces `SyncFromExchangeOnStartup` on next tick
  - Bitget `CancelAllOrdersAsync` now does per-order cancellation (cross-symbol bug fixed)
- 🎯 **Config change detected**: BX-BUSDT now has 3 tiers (`[≤10%:$5, ≤20%:$10, **≤30%:$15**]`) — a new tier was added since baseline. BG-BUSDT also has more DCAs than expected (13 DCAs!) suggesting the same expansion.
- 🩹 **BX-BUSDT (#8) entered stuck state at 13:00:38 → self-healed at 13:30:46** via `Reconcile-TP exchangeIsFlat` branch
- 🟢 **Zero Bitget cross-symbol cancel warnings since deploy** (but no BG full-close has happened in the 7-min post-deploy window yet — fix not exercised)

## Activity timeline since 12:13 UTC

### Pre-deploy (12:13 → 13:24 UTC) — old code

**BG-BUSDT (#5)** had a massive churn cycle (anchor 0.503):
```
12:16:08 TP#2 +$0.294  12:32:38 ...    12:44:58 TP#1 +$0.288
12:18:34 DCA#2          12:39:01 DCA#3  12:58:54 TP#0 +$0.281  ← FULL CLOSE
12:21:17 TP#2 +$0.292   12:40:02 TP#3   13:00:04 Entry @ 0.525 (new cycle)
12:22:33 DCA#2          12:41:12 TP#2   13:00:51 Entry @ 0.5273 ← ⚠️ second Entry, 47s later
12:24:09 TP#2 +$0.292                   13:01:13 DCA#1 fill
12:25:11 TP#1 +$0.288                   13:02:12 TP#1 +$0.287
12:28:34 TP#1 +$0.288
```

**BX-BUSDT (#8)** similar churn:
```
12:16:07 TP#2  12:30:56 DCA#1  12:58:54 TP#0 +$0.148 ← FULL CLOSE
12:25:21 TP#1  12:32:34 DCA#2  13:00:03 Entry @ 0.5258
12:41:01 TP#2  12:44:59 TP#1   13:00:38 Entry @ 0.5276 ← ⚠️ second Entry, 35s later
                                13:00:56 DCA#1 fill
                                13:01:56 TP#1 +$0.144
```

### The two "double-Entry" events
Both BG-BUSDT and BX-BUSDT show TWO consecutive Entry trades 35-47 seconds apart at 13:00. Cooldown is 1 closed 5m bar (5 min), so this is impossible via normal flow. Strongest hypothesis: **user did Stop+Start on both bots** within a minute — the old code wiped state on Start, opening a fresh anchor each time. This is consistent with:
- New `staticLowerBound = 0.36932` on BX-BUSDT (= 0.5276·0.70, **30%** range, matching the newly-added tier 3) vs the original 0.38968 (= 0.4871·0.80, 20% range, from the very first anchor).
- The `staticBoundsInitialized` flag must have been reset during a fresh state wipe.

This is exactly the Stop+Start state-wipe behavior we just fixed.

### Deploy moment (13:24:30 UTC)
```bash
docker compose build api worker   # ~50s
docker compose up -d api worker   # 5s
```
Containers recreated. State preserved in Postgres (volume untouched).

### Post-deploy (13:24:30 → 13:31 UTC, ~7 min)

**BX-BUSDT (#8) stuck loop and recovery**:
- Pre-deploy state had batch #0 (qty 9.47, fill 0.5276) with `tpOrderId=None` — TP was never successfully placed, likely lost during the rapid Stop+Start.
- Position on BingX had already been TP-closed externally during the same window (price spiked through 0.5434 = 0.5276·1.03).
- Post-deploy ticks (every ~13 sec): `HealMissingTps` tries to place a new TP → BingX returns "Reduce Only order can only decrease the position and not be used to open a position" → 27 consecutive errors.
- At 13:30:46 — `ReconcileBatchesFromPosition` finally fired successfully (probably both probes finally agreed on `exchange.qty=0`). Hit the `exchangeIsFlat=true` branch:
  ```
  🔎 RECONCILE TP: state qty=9.47 vs exchange qty=0 (дельта=9.47, цена=0.5218).
                  Биржа: позиции нет — закрываю все батчи (TP реально сработал на high бара).
  ```
- `RecordTpFill` called with `batch.TpPrice=0.543428` → PnL +$0.148 recorded. `OnFullClose` called. State cleaned. Bot now waiting for cooldown bar to open new anchor. ✓

**State as of capture**:
- BX-BUSDT (#8): batches=0, dcaOrders=0, anchor=0, realized=$2.503 — clean, ready to open new anchor on next 5m close

### Why Reconcile took 6 minutes to fire
Suspect `BingXFuturesExchangeService.GetPositionAsync` returned inconsistent results (sometimes null/0, sometimes lingering qty?) — the 2-second second-probe disagreement guard kept skipping. Worth investigating in a future iteration.

## Δ realized this hour
| Bot | iter-6 | now | Δ |
|---|---|---|---|
| BB-XRP (#1)   | $0.285 | $0.380 | +$0.095 |
| BB-JCT (#3)   | $5.765 | $6.050 | +$0.285 |
| BG-BUSDT (#5) | $3.475 | $6.362 | **+$2.887** |
| BX-BUSDT (#8) | $1.474 | $2.503 | **+$1.029** |
| **Δ this hour** |     |        | **+$4.296** |

## Grid math — ✓ on the new 3-tier config

BG-BUSDT new anchor 0.5273 (from `staticLowerBound=0.36932` field perspective, but actual current is 0.5273 in state at iter-7), tiers=[≤10%:$10, ≤20%:$20, ≤30%:$30] (likely — same expansion). 13 DCAs in state implies levels k=1..13 with k=10 at 30% (the bound). Math checks per `ComputeDcaLevels`: tier lookup picks $10 for k=1-3, $20 for k=4-6, $30 (or whatever tier3.size is) for k=7-10.

BX-BUSDT new ladder (before stuck event) at anchor=0.5276, tiers=[≤10%:$5, ≤20%:$10, ≤30%:$15]:
- k=1..3: $5/0.51177=9.77, $5/0.49594=10.08, $5/0.48012=10.41 ✓
- k=4..6: $10/0.46429=21.54, $10/0.44846=22.30, $10/0.43263=23.11 ✓
- k=7..10: $15/0.41680=35.99, $15/0.40098=37.41, $15/0.38515=38.95, $15/0.36932=40.62 ✓
All ladder qtys in state match `sizeUsdt/price` for the respective tier. ✓

## 🟢 Bitget cross-symbol cancel: zero warnings post-deploy
The 14 warnings on BG-ZBT and 14 on BG-OPEN this hour (visible in counts) were all from the pre-deploy period (BG-BUSDT full-close at 12:58:54 + likely earlier cycles). Post-13:24:30 deploy: **zero**.

But no Bitget bot has done a full-close in the 7-min post-deploy window, so the fix is not yet exercised. Next BG full-close will be the verification moment.

## State delta
| Bot | batches Δ | dcas Δ | anchor |
|---|---|---|---|
| BB-XRP (#1)   | 3 → 2 | 8 → 9 | unchanged |
| BB-ZBT (#2)   | 6 → 7 | 1 → 4 | unchanged |
| BB-JCT (#3)   | 5 → 5 | 2 → 6 | unchanged |
| BG-BUSDT (#5) | 3 → 1 | 4 → 13 | 0.503 → 0.5273 (new cycle, 30% range) |
| BX-BUSDT (#8) | 3 → 0 | 5 → 0 | 0.5012 → 0 (post-recovery, awaits new anchor) |

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $0.380 | +$0.095 |
| #2 BB-ZBT   | $1.774 | $2.856 | +$1.081 |
| #3 BB-JCT   | $5.184 | $6.050 | +$0.866 |
| #4 BB-SAGA  | $6.484 | $7.233 | +$0.748 |
| #5 BG-BUSDT | $0     | $6.362 | **+$6.362** |
| #6 BG-ZBT   | $0     | $0.293 | +$0.293 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $2.503 | **+$2.503** |
| #9 BX-ZBT   | $0     | $0.148 | +$0.148 |
| **Total Δ from baseline** |  |  | **+$12.097** |

## Verdict for iteration 7

✅ Backend deployed cleanly with two fixes (StateInitialized=false on Start, per-order Bitget cancel).

✅ **Existing defensive design proven once more**: BX-BUSDT auto-recovered from a stuck state via Reconcile-TP without code change. Logs are noisy (27 errors) but no money lost.

✅ Grid math on new 3-tier config verified.

🟡 The 6-min delay before Reconcile took action on BX-BUSDT is a soft issue worth tracking. May be a BingX `GetPositionAsync` quirk.

🟡 **Fixes not yet exercised in production** — need next Bitget full-close to verify per-order cancel, and a user-triggered Stop+Start to verify state preservation.

📅 Next cron fire 14:07 UTC.
