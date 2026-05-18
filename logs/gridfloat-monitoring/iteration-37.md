# GridFloat monitoring — Iteration 37

**Captured**: 2026-05-15 19:29 UTC (21:29 Warsaw)
**Δ from iteration-36**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **7 trades, all DCAs, no TPs** — quiet hour after iter-36's burst. Realized PnL **unchanged** at $49.092.
- 🔻 **Workspace-wide downtick** in last hour: ZBT –1.7%, JCT –2.1%, SAGA –0.75%, XRP –1.0% (lastPrice deltas). Grids accumulated inventory instead of cycling.
- ✅ **0 errors** for hour 3 in a row. 4 warnings all benign `RECONCILE DCA` adoption notices.
- ✅ **No phantom DCA dupes** — BB-JCT regression from iter-34 still dormant.

## Δ Activity since iter-36

### Trades (7, all DCA fills)

| Time UTC | Bot | Status | Qty | Price |
|---|---|---|---|---|
| 18:35:08 | BB-BANANAS (#4f) | DCA#1 | 800 | 0.01185 |
| 18:40:34 | BB-ZBT-SASH (#7e) | DCA#3 | 64.5 | 0.15499 |
| 18:54:32 | BB-BANANAS (#4f) | DCA#2 | 800 | 0.01179 |
| 19:05:02 | BB-FF (#46) | DCA#3 | 115 | 0.08690 |
| 19:11:03 | BB-ZBT (#67) | DCA#4 | 65.0 | 0.15365 |
| 19:11:04 | BB-ZBT-SASH (#7e) | DCA#4 | 65.1 | 0.15339 |
| 19:11:12 | BX-ZBT (#0a) | DCA#1 | 32.54 | 0.15364 |

### Realized PnL delta: **+$0.000** (no TPs this hour)

### Log counts (since 18:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 ✅ | 1 | 2 |
| BB-SASH-ShortSMA | 0 | 3 | 10 |
| BG-SASH-Insider | 0 | 0 | 0 |
| BX-M-IJKL | 0 | 0 | 2 |

All 4 warnings are `🔎 RECONCILE DCA` adoption notices — exchange showed +1 DCA fill that `PollDcaFills` had not yet captured, reconcile adopted it. This is the **expected backstop path** in [GridFloatHandler.cs:817-861](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L817-L861) — log says "Адаптирую DCA-уровни" and the matching DCA fill trade is recorded the same second. Net effect: same as a clean Poll path.

## 🚨 Issue tracker

### 🟢 BB-JCT phantom DCA#5 — 3 clean hours, no recurrence (need 21 more)

### 🟢 BB-M-Algida error spam — sustained 0 errors / hour 3

## Cross-exchange health

- **Bybit** (7 bots): 6 DCAs filled across 5 bots, no TPs. Price drift pushed bots deeper into their grids.
- **Bitget** (3 bots): **No activity** this hour — all 3 grids holding inventory, lastPrice within ±0.5% of previous.
- **BingX** (2 bots): 1 DCA on BX-ZBT. BX-BUSDT idle.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-ZBT (#67) | 4 → 5 | 7 → 6 |
| BB-BANANAS (#4f) | 1 → 3 | 25 → 23 |
| BB-FF (#46) | 3 → 4 | 13 → 12 |
| BB-ZBT-SASH (#7e) | 3 → 5 | 13 → 11 |
| BX-ZBT (#0a) | 1 → 2 | 6 → 5 |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$49.092** |
| Δ this iteration | +$0.000 |
| Δ from iter-34 baseline | +$2.066 |

## Verdict for iteration 37

Calm hour — bots correctly **accumulated** inventory (7 DCA fills) as prices drifted lower across multiple symbols, instead of cycling TPs. No errors, no phantom dupes, all reconciles healthy. Bitget completely idle this window; BingX one DCA; Bybit absorbing the move. Wait for the bounce: TPs sit 1-2% above current price on the newer batches, so even a modest rebound should flip several batches into profit next iteration. **Next cron fire ~20:17 UTC (22:17 Warsaw).**
