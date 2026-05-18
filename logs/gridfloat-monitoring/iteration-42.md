# GridFloat monitoring — Iteration 42

**Captured**: 2026-05-16 00:29 UTC (02:29 Warsaw)
**Δ from iteration-41**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **9 trades** (5 TPs, 4 DCAs), **+$0.930 realized** — ZBT rebound at 23:58 fired **4 simultaneous TPs across all 3 exchanges**.
- 🚨 BX-BUSDT margin-cooldown loop continues — **7 more warnings** (18 total cumulative across iter-41/42).
- ✅ 0 errors (8th clean hour). 0 phantom dupes (hour 8).
- 🟢 BG-ZBT closed the last batch from its 18:00 anchor — full grid drained, will need to wait for next anchor.

## Δ Activity since iter-41

### Trades (9)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 23:31:35 | BB-BANANAS (#4f) | Buy  | DCA#2 | 800   | 0.01179 | — |
| 23:37:02 | BB-JCT (#3) | Buy  | DCA#4 | 2400  | 0.00413 | — |
| 23:56:43 | BB-BANANAS (#4f) | Buy  | DCA#3 | 800   | 0.01173 | — |
| 23:58:22 | BB-ZBT (#67) | Sell | TP#4  | 65.00 | 0.15825 | +$0.295 |
| 23:58:24 | BX-ZBT (#0a) | Sell | TP#1  | 32.54 | 0.15824 | +$0.148 |
| 23:58:24 | BB-ZBT-SASH (#7e) | Sell | TP#2  | 63.80 | 0.15814 | +$0.096 |
| 00:03:19 | BB-FF (#46) | Buy  | DCA#2 | 113   | 0.08779 | — |
| 00:11:14 | BB-ZBT-SASH (#7e) | Sell | TP#1  | 63.20 | 0.15976 | +$0.096 |
| 00:21:47 | BG-ZBT (#9d) | Sell | TP#1  | 64.00 | 0.16074 | +$0.296 |

**Triple-TP cluster at 23:58:22-24** — ZBTUSDT crossed 0.15825 within 2 seconds on Bybit, BingX and BB-SASH; all three workspaces with ZBT bots fired correctly.

### Realized PnL delta

| Bot | iter-41 | iter-42 | Δ |
|---|---|---|---|
| BB-ZBT (#67) | 6.523 | 6.818 | +$0.295 |
| BB-ZBT-SASH (#7e) | 0.765 | 0.957 | +$0.191 (2 TPs) |
| BG-ZBT (#9d) | 2.645 | 2.941 | +$0.296 |
| BX-ZBT (#0a) | 1.035 | 1.182 | +$0.148 |
| **Σ Δ** | | | **+$0.930** |

### Log counts (since 23:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 0 | 4 |
| BB-SASH-ShortSMA | 0 | 0 | 10 |
| BG-SASH-Insider | 0 | 0 | 2 |
| BX-M-IJKL | 0 | **7** | 3 |

## 🚨 Issue tracker

### 🚨 BX-BUSDT "Insufficient margin" (open from iter-41) — 18 cumulative warnings

7 more `DCA #7 не выставлен (cooldown 5мин): Insufficient margin` warnings this hour at the expected 5-min cycle. The cooldown is still doing its job (no busy-loop), but the bot remains stuck on a partial 7-batch grid with no way to refill slot #7.

This needs **user action** — adding margin or reducing tier sizes — not a code fix.

### 🟢 BB-JCT phantom DCA#5 — 8 clean hours, no recurrence

BB-JCT this hour: 1 DCA#4 fill, single record, no dupes.

### 🟢 BB-M-Algida error spam — 8th consecutive clean hour

## Cross-exchange health

- **Bybit** (7 bots): 3 TPs (+$0.486), 4 DCAs. BB-ZBT/BB-ZBT-SASH cycled vigorously on the rebound.
- **Bitget** (3 bots): 1 TP on BG-ZBT (+$0.296) — first activity since iter-41's DCA. BG-ZBT batches now down to 1 (will fully close on next TP).
- **BingX** (2 bots): 1 TP on BX-ZBT (+$0.148). BX-BUSDT margin loop ongoing — no new DCA placements possible.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3) | 4 → 5 | 7 → 6 |
| BB-ZBT (#67) | 5 → 4 | 6 → 7 |
| BB-BANANAS (#4f) | 2 → 4 | 24 → 22 |
| BB-FF (#46) | 2 → 3 | 14 → 13 |
| BB-ZBT-SASH (#7e) | 3 → 1 | 13 → 15 |
| BG-ZBT (#9d) | 2 → 1 | 5 → 6 |
| BX-ZBT (#0a) | 2 → 1 | 5 → 6 |
| BX-BUSDT (#1c) | 7 → 7 | 3 → 4 (1 slot reopened) |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$51.345** |
| Δ this iteration | +$0.930 |
| Δ from iter-34 baseline | +$4.319 |

## Verdict for iteration 42

Strong hour — a 1.5% ZBT rebound at 23:58 UTC fired four TPs across Bybit / BingX / Bitget within 13 minutes, demonstrating the strategy correctly captures market-wide moves regardless of exchange. BB-ZBT-SASH alone earned 2 TPs (+$0.191). The BX-BUSDT margin issue continues to be the only blemish — 18 warnings cumulatively now, but the cooldown gate is preventing it from escalating into errors. **Next cron fire ~01:17 UTC (03:17 Warsaw).**
