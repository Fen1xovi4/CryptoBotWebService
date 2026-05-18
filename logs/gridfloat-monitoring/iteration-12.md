# GridFloat monitoring — Iteration 12

**Captured**: 2026-05-14 18:13 UTC (20:13 Warsaw)
**Δ from iteration-11**: ~60 min
**Cron**: `342c898f` fired at 18:07 UTC

## 🎯 TL;DR

**Fix #1 (Stop+Start state preserve) — VERIFIED IN PRODUCTION.**

User restarted BX-BUSDT (#8) at 17:23:18 UTC. With the new code:
- State was **preserved** (not wiped).
- `StateInitialized=false` forced `SyncFromExchangeOnStartup` on the next tick.
- Sync ran, detected the state/exchange qty mismatch, logged the diagnostic warning, **continued operating** instead of failing.
- Reconcile picked up the missing DCA fill within seconds.
- 38 seconds later TP#4 fired → +$0.142.

**End-to-end smooth recovery. No human intervention needed.**

## The Stop+Start moment — second-by-second

```
17:23:18.59  BX-BUSDT  Warning  🔄 RESTART SYNC: state qty=28.88 vs exchange qty=39.13 — расхождение, продолжаю по state
17:23:21.25  BX-BUSDT  Warning  🔎 RECONCILE DCA: state qty=28.88 vs exchange qty=39.13 (биржа БОЛЬШЕ на 10.25, цена=0.4921)
17:23:21.25  BX-BUSDT  Warning  После reconcile DCA остаток qtyExcess=10.25 — нет больше DCA-уровней для адаптации
17:23:34.62  BX-BUSDT  Info     ✅ DCA #3 filled: qty=10.25 @ 0.4873 → batch TP=0.501919  (normal Poll catch)
17:23:43.66  BX-BUSDT  Warning  🔎 RECONCILE DCA: state qty=39.13 vs exchange qty=49.38 (биржа БОЛЬШЕ на 10.25, цена=0.4921)
17:23:43.66  BX-BUSDT  Info     ✅ DCA #4 adopted: qty=10.25 @ 0.4713 → batch TP=0.485429
17:23:56.22  BX-BUSDT  Info     💰 TP #4 filled: qty=10.25 @ 0.4854, PnL=+$0.142
```

### Trace through the new code path
1. User clicked Stop → handler set `Status=Stopped`, state untouched.
2. User clicked Start → my new branch fired:
   ```csharp
   var hasLiveState = prevGfState.Batches.Count > 0 || prevGfState.DcaOrders.Count > 0;  // true
   freshGfState = prevGfState;                  // preserve everything
   freshGfState.StateInitialized = false;       // force restart-sync next tick
   ```
3. Next worker tick at 17:23:18: `state.StateInitialized = false` → entered `SyncFromExchangeOnStartup`:
   - `exchangeHasPosition = true` (BingX had 39.13 USDT pos)
   - `stateHasBatches = true` (we preserved 3 batches summing to 28.88)
   - → hit the "both exist, verify roughly" branch:
     ```csharp
     var qtyDelta = Math.Abs(pos.Quantity - sumQty) / sumQty;  // 10.25/28.88 = 36% > 1%
     Log("🔄 RESTART SYNC: расхождение, продолжаю по state");
     ```
   - Wiped stored TpOrderIds, cleared DcaOrders, set `state.StateInitialized=true`.
4. Same tick: `ReconcileBatchesFromPosition` ran, saw `delta=+10.25` → tried to adopt as DCA. Initially no DCA orders to adopt (just wiped), so warned "нет больше DCA-уровней".
5. Next tick (~10s later): a fresh DCA limit (#3) was placed by `HealMissingDcas`. Exchange already had it filled — `PollDcaFills` caught it.
6. Next tick: reconcile saw another +10.25 delta (DCA#4 also already filled on exchange) → adopted it from the freshly-placed DCA limit.
7. Heal-placed TP for batch #4 → exchange filled in ~10 sec → +$0.142.

**This is exactly the recovery path the fix was designed for.** Pre-fix outcome would have been: state wiped → exchange position lingering → `SyncFromExchangeOnStartup` enters the "Не могу восстановить батчи (нет цен отдельных филлов). Закройте позицию вручную" error branch → bot stuck.

## Δ Activity since 17:13 UTC

### Trades (9 new)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 17:13:54 | BB-ZBT (#2)  | Buy  | DCA#4 fill        | 65    | 0.15364 | — |
| 17:14:47 | BX-ZBT (#9)  | Buy  | DCA#1 (reconcile) | 32.54 | 0.15365 | — |
| **17:23:18** | **BX-BUSDT (#8)** | **—** | **RESTART SYNC (Stop+Start verified)** | — | — | — |
| 17:23:34 | BX-BUSDT (#8)| Buy  | DCA#3 fill        | 10.25 | 0.4873  | — |
| 17:23:43 | BX-BUSDT (#8)| Buy  | DCA#4 (reconcile) | 10.25 | 0.4713  | — |
| 17:23:56 | BX-BUSDT (#8)| Sell | TakeProfit#4      | 10.25 | 0.4854  | +$0.142 |
| 17:35:22 | BG-ZBT (#6)  | Buy  | DCA#1 fill        | 65    | 0.15152 | — |
| 17:48:02 | BB-XRP (#1)  | Sell | TakeProfit#0 (full close) | 6.70  | 1.495   | +$0.095 |
| 17:50:05 | BB-XRP (#1)  | Buy  | Entry (new anchor)| 6.60  | 1.4976  | — |
| 17:57:51 | BB-JCT (#3)  | Sell | TakeProfit#2      | 2300  | 0.0043931 | +$0.290 |

### realizedPnL delta
| Bot | iter-11 | now | Δ |
|---|---|---|---|
| BB-XRP (#1)   | $0.570 | $0.665 | +$0.095 (2nd full cycle in 2h) |
| BB-JCT (#3)   | $6.638 | $6.929 | +$0.290 |
| BX-BUSDT (#8) | $3.241 | $3.383 | +$0.142 |
| **Δ this hour** |     |        | **+$0.527** |

## State delta

| Bot | batches Δ | dcas Δ | anchor |
|---|---|---|---|
| BB-XRP (#1)   | 1 → 1 | 10 → 10 | 1.4802 → **1.4976** (2nd full cycle) |
| BB-ZBT (#2)   | 4 → 5 | 7 → 6 | unchanged |
| BB-JCT (#3)   | 3 → 2 | 8 → 9 | unchanged |
| BG-ZBT (#6)   | 1 → 2 | 6 → 5 | unchanged |
| BX-BUSDT (#8) | 3 → 4 | 8 → 7 | unchanged (after Stop+Start recovery) |
| BX-ZBT (#9)   | 1 → 2 | 6 → 5 | unchanged |

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $0.665 | +$0.380 |
| #2 BB-ZBT   | $1.774 | $5.364 | +$3.589 |
| #3 BB-JCT   | $5.184 | $6.929 | +$1.745 |
| #4 BB-SAGA  | $6.484 | $9.212 | +$2.728 |
| #5 BG-BUSDT | $0     | $7.798 | **+$7.798** |
| #6 BG-ZBT   | $0     | $1.173 | +$1.173 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $3.383 | **+$3.383** |
| #9 BX-ZBT   | $0     | $0.591 | +$0.591 |
| **Total Δ from baseline** |  |  | **+$21.387** |

## Verdict for iteration 12

✅ **Fix #1 verified end-to-end in production**. Stop+Start on BX-BUSDT → state preserved → SyncFromExchangeOnStartup ran → caught mismatch → adopted via reconcile → TP filed within 38 seconds. Pre-fix this would have left the bot stuck in an error loop until manual ClosePosition.

✅ **Fix #2 still holding** (no cross-symbol cancellations).

✅ Both fixes (#1 and #2) are now production-verified. They can be committed.

📅 Next cron fire 19:07 UTC.

## All three open issues from this session — status

| # | Issue | Status |
|---|---|---|
| Fix #1 | Stop+Start wipes state, bot gets stuck if position open | ✅ Fixed + verified |
| Fix #2 | Bitget cross-symbol cancel via CancelAllOrdersAsync | ✅ Fixed + verified (3/3 BG full-closes clean) |
| Fix #3 | Reconcile-TP stale-price false-positive log | 🟡 Cosmetic, not blocking; backlog |
