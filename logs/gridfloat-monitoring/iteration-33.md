# GridFloat monitoring — Iteration 33

**Captured**: 2026-05-15 15:13 UTC (17:13 Warsaw)
**Δ from iteration-32**: ~60 min
**Cron**: `342c898f` fired at 15:07 UTC

## TL;DR

- **2 trades**, **+$0.295 realized**.
- 🚨 **BB-JCT still stuck on TP#5 partial-batch** — 310 errors this hour (Fix #5 candidate, unchanged from iter-32).
- 🟡 **BG-ZBT had a 100+ warning burst** of reconcile-TP false-positives (Fix #3 manifesting). Resolved naturally when PollTpFills caught the real TP fill.

## Δ Activity since 14:13 UTC

### Trades (2 new)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 14:44:16 | BB-JCT (#3) | Buy  | DCA#4 fill   | 2400 | 0.00413 | — |
| 14:48:45 | BG-ZBT (#6) | Sell | TakeProfit#2 | 68   | 0.15123 | +$0.295 |

### realizedPnL delta
| Bot | iter-32 | now | Δ |
|---|---|---|---|
| BG-ZBT (#6) | $1.763 | $2.058 | +$0.295 |
| **Δ this hour** |     |        | **+$0.295** |

## 🚨 Issue tracker

### BB-JCT (#3) — TP#5 sub-min batch loop (310 errors / hr)

Same error from iter-32 has been firing at ~12-second intervals for >1 hour:
```
TP батча #5 не выставлен: Qty 0 < min 100 for JCTUSDT
```
The batch with `qty=59.47` (from the iter-32 partial-fill reconcile adoption) can't have its TP placed. The bot is **stuck spamming this error** until either:
- The user manually closes the bot, or
- Fix #5 is deployed to clean up sub-minimum batches.

**Other BB-JCT batches keep working** (DCA#4 just filled at 14:44, contributing to position). PnL accrual isn't blocked — just one dead 59.47 batch logging endlessly.

### BG-ZBT (#6) — Reconcile-TP stale-price loop (~100 warnings 14:47-14:48)

12-13 reconcile-TP cycles in ~1 minute, each logging:
```
🔎 RECONCILE TP: state qty=197 vs exchange qty=130 (дельта=67, цена=0.15049). 
Reconcile TP: батч #0/#1/#2 TP=… не пересечён ценой 0.15049 — частичное закрытие извне, пропускаю.
После reconcile TP остаток qtyDelta=67 — Возможно ручное частичное закрытие извне.
```

This is **Fix #3** (in backlog) manifesting at scale:
- Exchange's TP fill for batch #2 happened at 14:48 at price 0.15123.
- `state.LastPrice` was 0.15049 (from the previous closed candle).
- Reconcile detected the qty drop (state 197 → exchange 130) but couldn't identify the closed batch because the stale LastPrice (0.15049) was below all batch TpPrices (0.15123 / 0.1561 / 0.1609).
- The "exchangeIsFlat" branch didn't fire either (130 > 0.001·197 = 0.197).
- So reconcile correctly chose to do nothing on each tick — but it logged 5 warnings per attempt = ~60 lines of noise.
- At 14:48:45 PollTpFills finally returned the TP order as Filled → proper recording → loop ended.

The defensive design (skip rather than close) prevented any wrong action. **No money lost.** But the log noise is significant — this is what would trip a monitoring dashboard.

### Total log noise this hour
- 310 errors (BB-JCT loop)
- 105 warnings (BG-ZBT burst)
- Both are non-blocking but worth fixing for log hygiene.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3) | 5 → 6 | 6 → 5 |
| BG-ZBT (#6) | 3 → 2 | 4 → 5 |

## Cumulative scoreboard

**Total Δ from baseline: +$31.497**

## Verdict for iteration 33

✅ PnL accrual continues — bots are functional even with the noisy logs.

🚨 **Fix #5 (BB-JCT sub-min batch)**: high-priority — it's an active error producer and represents trapped inventory.

🟡 **Fix #3 (BG-ZBT reconcile-TP false-positive log)**: medium-priority — false alarms make real issues harder to spot in dashboards.

📅 Next cron fire 16:07 UTC.
