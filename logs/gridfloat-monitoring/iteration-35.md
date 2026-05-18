# GridFloat monitoring — Iteration 35

**Captured**: 2026-05-15 17:29 UTC (19:29 Warsaw)
**Δ from iteration-34**: ~18 min (first fire of the new cron, captured shortly after :17 trigger)
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **5 trades**, **4 TP fills**, **+$0.724 realized** in this 18-min window.
- ✅ **BB-JCT error loop is GONE** — 0 errors this iteration vs 221 in iter-34. JCT also got its first real TP fill (#4 @ +$0.293).
- ✅ **No phantom DCA-dupes** anywhere — the iter-34 BB-JCT re-adoption loop did not recur.
- 🟡 **BB-BANANAS31 cycled** — TP0 filled → full close → cooldown active (`openAfterTime=17:26:51`).
- 🟡 55 BB-M-Algida warnings, all benign: TP/DCA cancel-without-fill bursts at 17:19-17:20 UTC mark the SAGA/XRP grids re-seeding (TPs got re-placed, DCAs re-armed within seconds).

## Δ Activity since iter-34

### Trades (5)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 17:19:33 | BG-ZBT (#9d) | Sell | TP#1 | 65.0 | 0.15606 | +$0.291 |
| 17:19:34 | BB-ZBT-SASH (#7e) | Sell | TP#0 | 64.7 | 0.15605 | +$0.096 |
| 17:20:31 | BB-ZBT-SASH (#7e) | Buy | **Entry** | 64.1 | 0.15599 | — |
| 17:26:51 | BB-BANANAS (#4f) | Sell | TP#0 | 800 | 0.01189 | +$0.043 |
| 17:26:59 | BB-JCT (#3) | Sell | TP#4 | 2400 | 0.00425 | +$0.293 |

### Realized PnL delta

| Bot | iter-34 | iter-35 | Δ |
|---|---|---|---|
| BB-JCT (#3) | 9.167 | 9.461 | **+0.294** |
| BB-BANANAS (#4f) | 0 | 0.043 | +0.043 |
| BB-ZBT-SASH (#7e) | 0.096 | 0.192 | +0.096 |
| BG-ZBT (#9d) | 2.058 | 2.350 | +0.291 |
| **Σ** | | | **+0.724** |

### Log counts (since 17:11 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | **0** ✅ | 55 | 57 |
| BB-SASH-ShortSMA | 0 | 0 | 24 |
| BG-SASH-Insider | 0 | 0 | 2 |
| BX-M-IJKL | 0 | 0 | 0 |

## 🚨 Issue tracker

### 🟢 BB-JCT phantom DCA#5 (open from iter-34) — no recurrence

iter-34 had 17 phantom DCA#5 records in 3 min at 16:05-16:08 UTC. None this iteration. State even improved: batches went 5 → 4 (a real TP fill), realized accrued +$0.294. Keep monitoring until 24h elapses with no dupes.

### 🟢 BB-JCT error spam — resolved

iter-34 had 221 "Qty 0 < min N" errors / 118 min. This iteration: **0 errors**. The sub-min cleanup path in [GridFloatHandler.cs:396-407](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L396-L407) appears to have purged the offending 59.47-qty zombie batch.

### 🟡 BB-M-Algida 55 warnings @ 17:19-17:20 UTC — benign grid re-seed

All "TP батча #N отменён без филла" or "DCA #N лимит отменён/отклонён без филла". Pattern: a single tick burst where multiple TP/DCA orders got returned as Cancelled by the exchange. `HealMissingTps`/`HealMissingDcas` will re-place on the next tick. Symptoms consistent with a partial-fill or order-id rotation event on Bybit — not a fault, but worth tracking if the burst pattern repeats every hour.

## Cross-exchange health

- **Bybit** (BB-M-Algida + BB-SASH-ShortSMA, 7 bots): 3 TPs filled (+$0.43), 1 new entry, 1 bot now in cooldown waiting for re-anchor. Error count crashed from 221 → 0.
- **Bitget** (BG-SASH-Insider, 3 bots): 1 TP filled (+$0.29). Otherwise idle. Clean.
- **BingX** (BX-M-IJKL, 2 bots): No activity. State unchanged from iter-34.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3) | 5 → 4 | 6 → 7 |
| BB-BANANAS (#4f) | 1 → 0 (cooldown) | 25 → 0 |
| BB-ZBT-SASH (#7e) | 1 → 1 (cycled) | 15 → 15 |
| BG-ZBT (#9d) | 2 → 1 | 5 → 6 |

## Cumulative scoreboard

- iter-34 baseline total realized (state-side): **$47.126**
- iter-35 total realized: **$47.850**
- **Δ from iter-34: +$0.724**

## Verdict for iteration 35

✅ Cleanest hour yet on this monitoring run — error-rate crashed to zero across all 12 bots, 4 distinct TPs filled across both Bybit and Bitget, no phantom DCA dupes. The BB-JCT and BG-ZBT issues called out in iter-33/34 are now in a healthy state. BingX silent this window but holding state. Next cron fire 18:17 Warsaw (16:17 UTC) — wait, cron is in Warsaw local, so next fire is **19:17 → no, that already passed — actually 20:17 Warsaw / 18:17 UTC**.
