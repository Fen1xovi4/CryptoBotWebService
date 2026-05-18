# GridFloat monitoring — Iteration 43

**Captured**: 2026-05-16 01:29 UTC (03:29 Warsaw)
**Δ from iteration-42**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **15 trades** (4 TPs, 11 DCAs), **+$0.327 realized**.
- 🟢 **Fix #5 sub-min batch cleanup fired live on BB-JCT (#3)** — adopted phantom DCA#6 qty=53.73 (< 100 min), instantly dropped it. **This is exactly the condition that caused iter-34's 17-trade dup-loop, now structurally prevented.**
- 🟢 **BX-BUSDT margin loop went silent** — 0 warnings this hour vs 7 in iter-42. Worker uptime unchanged (8h), so cooldown extended past 1h window, OR free margin was restored externally.
- 🟢 1 clean state transition (BB-ZBT-SASH full close → cooldown → re-anchor at 00:55).
- ✅ 0 errors (9th clean hour); 0 phantom dupes (hour 9).

## Δ Activity since iter-42

### Trades (15 — 4 TPs, 11 DCAs)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 00:31:36 | BG-BUSDT (#3f) | Buy  | DCA#6 | 46.00 | 0.43230 | — |
| 00:32:56 | BB-FF (#46) | Sell | TP#2  | 113   | 0.08866 | +$0.094 |
| 00:41:56 | BB-FF (#46) | Sell | TP#1  | 112   | 0.08957 | +$0.094 |
| 00:44:41 | BB-BANANAS (#4f) | Sell | TP#3  | 800   | 0.01179 | +$0.043 |
| 00:48:53 | BB-FF (#46) | Buy  | DCA#1 | 112   | 0.08869 | — |
| 00:50:29 | BB-ZBT-SASH (#7e) | Sell | TP#0  | 62.5  | 0.16137 | +$0.095 |
| 00:51:19 | BB-BANANAS (#4f) | Buy  | DCA#3 | 800   | 0.01173 | — |
| 00:55:08 | BB-ZBT-SASH (#7e) | Buy  | **Entry** | 62.3 | 0.16048 | — |
| 01:11:18 | BB-JCT (#3) | Buy  | **DCA#5 (reconcile-adopted)** | 2505.74 | 0.00399 | — |
| 01:11:19 | BB-JCT (#3) | Buy  | DCA#6 (dropped sub-min) | 53.73 | 0.00385 | — |
| 01:11:24 | BB-BANANAS (#4f) | Buy  | DCA#4 | 800   | 0.01168 | — |
| 01:25:01 | BB-ZBT-SASH (#7e) | Buy  | DCA#1 | 62.9  | 0.15888 | — |
| 01:26:59 | BB-ZBT-SASH (#7e) | Buy  | DCA#2 | 63.58 | 0.15727 | — |
| 01:26:59 | BG-ZBT (#9d) | Buy  | DCA#1 | 64.0  | 0.15606 | — |
| 01:27:00 | BB-ZBT-SASH (#7e) | Buy  | DCA#3 | 64.12 | 0.15567 | — |

### Realized PnL delta

| Bot | iter-42 | iter-43 | Δ |
|---|---|---|---|
| BB-FF (#46) | 0.189 | 0.378 | +$0.189 (2 TPs) |
| BB-BANANAS (#4f) | 0.259 | 0.302 | +$0.043 |
| BB-ZBT-SASH (#7e) | 0.957 | 1.052 | +$0.095 |
| **Σ Δ** | | | **+$0.327** |

### Log counts (since 00:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 2 | 4 |
| BB-SASH-ShortSMA | 0 | 3 | 40 |
| BG-SASH-Insider | 0 | 1 | 4 |
| BX-M-IJKL | 0 | **0** | 0 |

All warnings except one were `🔎 RECONCILE DCA` adoption notices (healthy). The notable one:
```
01:11:19  🧹 Удаляю sub-min батч #6: qty=53.7311… < биржевой минимум 100.
          Это легаси из partial-fill reconcile до Fix #5.
```

## 🚨 Issue tracker

### 🟢 BB-JCT phantom DCA#5 (open from iter-34) — Fix #5 verified live

A new partial DCA fill on BB-JCT (qty=53.73 < min=100) was caught at 01:11:19 by the sub-min batch cleanup at [GridFloatHandler.cs:396-407](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L396-L407) — dropped on first detection instead of looping. Without this fix this would have re-spawned the iter-34 17-dup-trade situation. **The Fix #5 mechanism is now confirmed operational.**

I'd argue this issue can be closed — but per the instructions I'll keep it open until 24h elapses with no new phantom-DCA patterns. Hour 9 of 24 clean.

### 🟢 BX-BUSDT margin-cooldown loop (open from iter-41) — silent this hour

7 warnings in iter-42, **0 this hour**. State unchanged (still 7 batches / 4 DCAs / realized $5.541), worker uptime 8h (no restart). Two hypotheses:
1. PlacementCooldownUntil extended past the hourly snapshot window (5-min cooldown × N retries pushes the next attempt past 01:29).
2. BingX account got margin top-up externally (silent fix).

The state.dcaOrders count went 3 → 4 in iter-42 already (a slot was re-armed), then stayed at 4 here — consistent with hypothesis (1). Worth re-checking next iteration; if warnings resume, the underlying issue is unresolved.

### 🟢 BB-M-Algida error spam — 9th clean hour

## Cross-exchange health

- **Bybit** (7 bots): 4 TPs (+$0.327), 9 DCAs, 1 full close + re-anchor on BB-ZBT-SASH. Fix #5 fired live on BB-JCT.
- **Bitget** (3 bots): 2 DCAs (BG-BUSDT, BG-ZBT), 0 TPs. Reconcile-DCA fired correctly on BG-ZBT.
- **BingX** (2 bots): No activity, no logs — margin loop silent this hour.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3) | 5 → 6 | 6 → 5 (DCA#5 adopted, DCA#6 dropped sub-min) |
| BB-BANANAS (#4f) | 4 → 5 | 22 → 21 |
| BB-FF (#46) | 3 → 2 | 13 → 14 |
| BB-ZBT-SASH (#7e) | 1 → 4 | 15 → 12 (cycle + 3 DCAs after re-anchor) |
| BG-BUSDT (#3f) | 6 → 7 | 8 → 7 |
| BG-ZBT (#9d) | 1 → 2 | 6 → 5 |
| BX-ZBT (#0a) | 1 → 1 | 6 → 6 (unchanged) |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$51.672** |
| Δ this iteration | +$0.327 |
| Δ from iter-34 baseline | +$4.646 |

## Verdict for iteration 43

The most diagnostically-valuable hour so far. Fix #5 caught a real sub-min DCA adoption on BB-JCT live and prevented what would have been a repeat of iter-34's 17-dup loop — confirms the cleanup mechanism is the correct guard at [GridFloatHandler.cs:396-407](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L396-L407). Separately, BX-BUSDT's margin-warning stream went silent this hour — could be cooldown extension or external margin top-up; will re-check iter-44. **Next cron fire ~02:17 UTC (04:17 Warsaw).**
