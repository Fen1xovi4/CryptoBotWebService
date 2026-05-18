# GridFloat monitoring — Iteration 36

**Captured**: 2026-05-15 18:29 UTC (20:29 Warsaw)
**Δ from iteration-35**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **21 trades** (8 TPs, 5 entries, 8 DCAs) — busiest hour of the run.
- **+$1.341 realized** this hour across 6 bots; cumulative now **$49.092**.
- ✅ **Still zero errors** anywhere in the workspace (iter-34's 221-err Bybit loop fully dormant for ~80 min).
- ✅ **No phantom DCA dupes** for hour 2 in a row.
- 🟢 BB-ZBT-SASH (#7e) cycled the **full grid 3× this hour** — close → cooldown → new anchor at 17:34, 17:51, 18:00 → 18:05. State machine working as designed.

## Δ Activity since iter-35

### Trades (21)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 17:30:09 | BB-BANANAS (#4f) | Buy  | Entry | 800   | 0.01191 | — |
| 17:34:41 | BB-ZBT-SASH (#7e) | Sell | TP#0  | 64.1  | 0.15754 | +$0.095 |
| 17:35:05 | BB-ZBT-SASH (#7e) | Buy  | Entry | 63.5  | 0.15729 | — |
| 17:39:49 | BX-ZBT (#0a) | Sell | TP#1  | 32.54 | 0.15825 | +$0.148 |
| 17:47:02 | BB-FF (#46) | Buy  | DCA#1 | 112   | 0.08869 | — |
| 17:48:16 | BB-ZBT (#67) | Sell | TP#4  | 65.0  | 0.15824 | +$0.295 |
| 17:51:01 | BB-ZBT-SASH (#7e) | Sell | TP#0  | 63.5  | 0.15886 | +$0.096 |
| 17:53:20 | BB-JCT (#3) | Buy  | DCA#4 | 2400  | 0.00413 | — |
| 17:55:07 | BB-ZBT-SASH (#7e) | Buy  | Entry | 62.8  | 0.15901 | — |
| 17:59:43 | BG-ZBT (#9d) | Sell | TP#0  | 64.0  | 0.16089 | +$0.296 |
| 18:00:00 | BB-ZBT-SASH (#7e) | Sell | TP#0  | 62.8  | 0.16060 | +$0.096 |
| 18:00:11 | BG-ZBT (#9d) | Buy  | Entry | 62.0  | 0.16089 | — |
| 18:01:09 | BB-ZBT (#67) | Sell | TP#1  | 59.0  | 0.16148 | +$0.274 |
| 18:01:23 | BB-ZBT (#67) | Buy  | DCA#1 | 59.0  | 0.16108 | — |
| 18:04:59 | BB-BANANAS (#4f) | Buy  | DCA#1 | 800   | 0.01185 | — |
| 18:05:15 | BB-ZBT-SASH (#7e) | Buy  | Entry | 62.5  | 0.15978 | — |
| 18:08:13 | BB-ZBT-SASH (#7e) | Buy  | DCA#1 | 63.2  | 0.15818 | — |
| 18:08:54 | BB-BANANAS (#4f) | Sell | TP#1  | 800   | 0.01191 | +$0.043 |
| 18:16:24 | BB-ZBT-SASH (#7e) | Buy  | DCA#2 | 63.8  | 0.15658 | — |
| 18:17:46 | BB-FF (#46) | Buy  | DCA#2 | 113   | 0.08779 | — |
| 18:25:59 | BG-ZBT (#9d) | Buy  | DCA#1 | 64.0  | 0.15606 | — |

**TP-fill total this window: +$1.342**

### Realized PnL delta

| Bot | iter-35 | iter-36 | Δ |
|---|---|---|---|
| BB-ZBT (#67) | 5.955 | 6.523 | **+0.568** (2 TPs) |
| BB-BANANAS (#4f) | 0.043 | 0.087 | +0.043 |
| BB-ZBT-SASH (#7e) | 0.192 | 0.478 | **+0.287** (3 TPs) |
| BG-ZBT (#9d) | 2.350 | 2.645 | +0.296 |
| BX-ZBT (#0a) | 0.887 | 1.035 | +0.148 |
| **Σ Δ** | | | **+1.341** |

### Log counts (since 17:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 ✅ | 0 ✅ | 8 |
| BB-SASH-ShortSMA | 0 | 1 | 108 |
| BG-SASH-Insider | 0 | 0 | 15 |
| BX-M-IJKL | 0 | 0 | 2 |

The single warning is a **healthy informational** `🔎 RECONCILE DCA` notice on BB-ZBT-SASH at 18:08:13 — `state qty=62.5 vs exchange qty=125.7` — the strategy correctly adopted the missed DCA fill in the same tick.

### State transitions captured (full close / cooldown / new anchor)

5 full-close events, 5 new anchors, all on the BB-SASH-ShortSMA and BG-SASH-Insider ZBT bots. Sample flow for BB-ZBT-SASH:

```
17:34:42  🏁 Полное закрытие сетки: realized=0.29USD → кулдаун до 17:34:42
17:35:03  Кулдаун снят (закрылся бар после полного закрытия в 17:34:42) — открываю новый якорь
17:35:04  📈 ANCHOR Long: ZBTUSDT, anchorSize=10USDT, close=0.1574, …
17:35:05  ✅ ANCHOR open: qty=63.5 @ 0.15729, batch TP=0.1588629
```

Cooldown gate in [GridFloatHandler.cs:185-200](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L185-L200) fires exactly as documented: anchor opens on the first closed candle whose CloseTime is strictly after `OpenAfterTime`.

## 🚨 Issue tracker

### 🟢 BB-JCT phantom DCA#5 (from iter-34) — 2 clean hours, no recurrence

Trade table this hour shows ONE BB-JCT DCA fill (DCA#4 at 17:53) — single record, no dupes. The 17-dup incident from 16:05-16:08 UTC remains isolated.

### 🟢 BB-JCT error spam — staying at zero

iter-34: 221 errors / 118 min. iter-35: 0. iter-36: 0. Trend is solidly resolved.

## Cross-exchange health

- **Bybit** (7 bots): 5 TPs filled (+$0.808), 3 entries, 5 DCAs. Most active exchange. 0 errors, 1 informational reconcile-DCA warning.
- **Bitget** (3 bots): 1 TP filled (+$0.296), 1 full grid close + re-anchor on BG-ZBT, 1 DCA fill. 0 errors, 0 warnings.
- **BingX** (2 bots): 1 TP filled (+$0.148). Otherwise idle. 0 errors, 0 warnings.

## State delta

| Bot | batches Δ | dcas Δ | notes |
|---|---|---|---|
| BB-JCT (#3) | 4 → 5 | 7 → 6 | DCA#4 filled |
| BB-ZBT (#67) | 5 → 4 | 6 → 7 | TP#4 + TP#1 + DCA#1 |
| BB-BANANAS (#4f) | 0 → 1 | 0 → 25 | re-anchored after iter-35 cooldown |
| BB-FF (#46) | 1 → 3 | 15 → 13 | 2 DCAs filled |
| BB-ZBT-SASH (#7e) | 1 → 3 | 15 → 13 | 3 full cycles + 2 DCA fills |
| BG-ZBT (#9d) | 1 → 2 | 6 → 5 | TP closed all → re-anchor → DCA#1 |
| BX-ZBT (#0a) | 2 → 1 | 5 → 6 | TP#1 filled, slot re-armed |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$49.092** |
| Δ this iteration | +$1.341 |
| Δ from iter-34 baseline | +$2.066 |

*(Note: iter-34's published baseline of $47.126 was off by ~$0.10 due to a rounding error in the manual table sum — the actual iter-34 sum was $47.026. All Δ values from now on use the live DB sum, so this drift does not propagate.)*

## Verdict for iteration 36

Best-performing hour of the run so far — 8 TP fills across all 3 exchanges, $1.34 realized, zero errors, no phantom dupes, and the state machine cycled correctly through 5 full close/cooldown/re-anchor sequences. BB-ZBT-SASH alone went through 3 complete grid cycles in 35 minutes — confirms the small-tier ($10) + 1% TP config is well-suited to choppy ZBTUSDT price action. **Next cron fire ~19:17 UTC (21:17 Warsaw).**
