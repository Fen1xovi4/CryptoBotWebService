# GridFloat monitoring — Iteration 46

**Captured**: 2026-05-16 04:29 UTC (06:29 Warsaw)
**Δ from iteration-45**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **16 trades** (7 TPs, 6 DCAs, 3 Entries), **+$0.562 realized**.
- 🟢 **BB-ZBT-SASH cycled twice** in 10 min (Entry @ 04:15 → TP @ 04:22 → Entry @ 04:25) — full close + cooldown + re-anchor flow back-to-back.
- 🟢 **BX-ZBT cycled** (TP#0 at 04:22 → Entry at 04:25, new anchor 0.16222).
- 🚨 **BX-BUSDT margin issue MOVED to slot #4** — after 3 silent hours, 1 new "Insufficient margin" warning on DCA#4 at 04:25 (different slot than iter-41/42's #7, which actually filled via reconcile this hour).
- ✅ 0 errors (12th clean hour); 0 phantom dupes (hour 12).

## Δ Activity since iter-45

### Trades (16)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 03:30:34 | BB-FF (#46) | Buy  | DCA#4 | 116 | 0.08600 | — |
| 03:38:18 | BB-BANANAS (#4f) | Buy  | DCA#7 | 800 | 0.01150 | — |
| 03:52:36 | BB-FF (#46) | Buy  | DCA#5 | 117 | 0.08511 | — |
| 03:55:57 | BB-BANANAS (#4f) | Sell | TP#7  | 800 | 0.01155 | +$0.042 |
| 04:00:13 | BB-BANANAS (#4f) | Sell | TP#6  | 800 | 0.01161 | +$0.042 |
| 04:04:29 | BB-FF (#46) | Sell | TP#5  | 117 | 0.08596 | +$0.095 |
| 04:07:09 | BX-BUSDT (#1c) | Buy  | DCA#7 (reconcile) | 35.45 | 0.41241 | — |
| 04:14:40 | BB-ZBT-SASH (#7e) | Sell | TP#0  | 62.3 | 0.16208 | +$0.096 |
| 04:15:09 | BB-ZBT-SASH (#7e) | Buy  | **Entry** | 61.8 | 0.16174 | — |
| 04:17:32 | BB-BANANAS (#4f) | Sell | TP#5  | 800 | 0.01167 | +$0.043 |
| 04:21:40 | BB-BANANAS (#4f) | Buy  | DCA#5 | 800 | 0.01162 | — |
| 04:22:52 | BX-ZBT (#0a) | Sell | TP#0  | 31.57 | 0.16315 | +$0.148 |
| 04:22:52 | BB-ZBT-SASH (#7e) | Sell | TP#0  | 61.8 | 0.16335 | +$0.096 |
| 04:24:31 | BG-BUSDT (#3f) | Buy  | DCA#7 | 72   | 0.41650 | — |
| 04:25:15 | BB-ZBT-SASH (#7e) | Buy  | **Entry** | 61.6 | 0.16213 | — |
| 04:25:15 | BX-ZBT (#0a) | Buy  | **Entry** | 30.82 | 0.16222 | — |

### Realized PnL delta

| Bot | iter-45 | iter-46 | Δ |
|---|---|---|---|
| BB-BANANAS (#4f) | 0.556 | 0.682 | +$0.126 (3 TPs) |
| BB-FF (#46) | 0.567 | 0.663 | +$0.095 |
| BB-ZBT-SASH (#7e) | 1.434 | 1.626 | +$0.191 (2 TPs) |
| BX-ZBT (#0a) | 1.182 | 1.330 | +$0.148 |
| **Σ Δ** | | | **+$0.562** |

### Log counts (since 03:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 0 | 0 |
| BB-SASH-ShortSMA | 0 | 0 | 60 |
| BG-SASH-Insider | 0 | 0 | 2 |
| BX-M-IJKL | 0 | **2** | 12 |

The 2 BX warnings:
- 04:07:09 `🔎 RECONCILE DCA` — adopted slot #7 fill (the slot that's been "Insufficient margin" since iter-41!). It seems either margin was added externally, the price moved enough to make the limit cheaper, or BingX freed some collateral. Either way, **slot #7 cleared this hour**.
- 04:25:21 `DCA #4 не выставлен (cooldown 5мин): Insufficient margin` — **margin issue migrated to slot #4** after slot #7 filled. New cooldown loop possible.

## 🚨 Issue tracker

### 🚨 BX-BUSDT (#1c) margin loop — re-opened on slot #4 after slot #7 cleared

The original iter-41 issue (DCA#7 margin) is **resolved** — slot #7 filled at 04:07:09 via reconcile-DCA. But now slot #4 is hitting the same wall. State shows 8 batches + 3 DCAs = 11 slots tracked out of typical 11 — but slot #4 is one of the missing 3. Pattern suggests the BingX account is *just barely* under-margined; each successful DCA frees a slot but pushes another into "Insufficient margin" territory.

### 🟢 BB-JCT phantom DCA#5 — 12 clean hours

### 🟢 BB-M-Algida error spam — 12th clean hour

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): **idle 2nd consecutive hour** (no trades, no logs).
- **Bybit BB-SASH-ShortSMA** (3 bots): 6 TPs (+$0.414), 5 DCAs, 2 entries. Drove most of the action.
- **Bitget** (3 bots): 1 DCA (BG-BUSDT), 0 TPs. Realized unchanged.
- **BingX** (2 bots): 1 reconcile-DCA, 1 TP (+$0.148), 1 entry. **Re-activated** after 3 idle hours.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-BANANAS (#4f) | 7 → 6 | 19 → 20 |
| BB-FF (#46) | 4 → 5 | 12 → 11 |
| BB-ZBT-SASH (#7e) | 1 → 1 | 15 → 15 (2 cycles, both back to start) |
| BG-BUSDT (#3f) | 7 → 8 | 7 → 6 |
| BX-BUSDT (#1c) | 7 → 8 | 4 → 3 |
| BX-ZBT (#0a) | 1 → 1 | 6 → 3 (cycle: TP cleared 0, new anchor placed fresh 3-DCA ladder) |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$53.939** |
| Δ this iteration | +$0.562 |
| Δ from iter-34 baseline | +$6.913 |

## Verdict for iteration 46

Action returned to Bybit BB-SASH and BingX after a few sleepy hours. Two clean full-close → cooldown → re-anchor cycles on BB-ZBT-SASH (occurred entirely within 10 min around 04:15-04:25), and the long-stuck BX-BUSDT DCA#7 finally cleared via reconcile-DCA — but the margin headache just migrated to slot #4. The account is genuinely short on free margin. Next iteration should show whether the new #4 cooldown loop will continue or self-resolve like #7 did. **Next cron fire ~05:17 UTC (07:17 Warsaw).**
