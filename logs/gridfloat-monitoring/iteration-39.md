# GridFloat monitoring — Iteration 39

**Captured**: 2026-05-15 21:29 UTC (23:29 Warsaw)
**Δ from iteration-38**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **9 trades** (5 TPs, 4 DCAs), **+$0.621 realized** — Bybit back to active.
- ✅ **0 errors, 0 warnings** (5th consecutive clean hour).
- 🟢 **BB-FF (#46) earned first PnL** of the run (+$0.189 across 2 TPs).
- 🟢 **BB-JCT TP#4 re-armed and re-filled** (+$0.293) — same level cycled cleanly, no dupes.
- 🟡 Bitget + BingX **idle 3rd consecutive hour**.

## Δ Activity since iter-38

### Trades (9)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 20:32:06 | BB-BANANAS (#4f) | Buy  | DCA#1 | 800  | 0.01185 | — |
| 20:35:03 | BB-JCT (#3) | Sell | TP#4  | 2400 | 0.00426 | +$0.293 |
| 20:42:15 | BB-ZBT-SASH (#7e) | Sell | TP#4  | 65.1 | 0.15492 | +$0.096 |
| 20:44:50 | BB-BANANAS (#4f) | Buy  | DCA#2 | 800  | 0.01179 | — |
| 20:48:59 | BB-BANANAS (#4f) | Buy  | DCA#3 | 800  | 0.01173 | — |
| 20:59:23 | BB-FF (#46) | Sell | TP#3  | 115  | 0.08776 | +$0.095 |
| 21:18:25 | BB-ZBT-SASH (#7e) | Buy  | DCA#4 | 65.1 | 0.15338 | — |
| 21:25:44 | BB-FF (#46) | Sell | TP#2  | 113  | 0.08866 | +$0.094 |
| 21:25:48 | BB-BANANAS (#4f) | Sell | TP#3  | 800  | 0.01179 | +$0.043 |

### Realized PnL delta

| Bot | iter-38 | iter-39 | Δ |
|---|---|---|---|
| BB-JCT (#3) | 9.461 | 9.754 | +$0.293 |
| BB-BANANAS (#4f) | 0.173 | 0.216 | +$0.043 |
| BB-FF (#46) | 0 | 0.189 | **+$0.189** (first PnL) |
| BB-ZBT-SASH (#7e) | 0.478 | 0.574 | +$0.096 |
| **Σ Δ** | | | **+$0.621** |

### Log counts (since 20:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 0 | 2 |
| BB-SASH-ShortSMA | 0 | 0 | 16 |
| BG-SASH-Insider | 0 | 0 | 0 |
| BX-M-IJKL | 0 | 0 | 0 |

## 🚨 Issue tracker

### 🟢 BB-JCT phantom DCA#5 — 5 clean hours, no recurrence

BB-JCT this hour: 1 TP fill (single trade record), no dupes. The 17-dup incident at 16:05-16:08 UTC remains isolated.

### 🟢 BB-M-Algida error spam — 5th consecutive clean hour

## Cross-exchange health

- **Bybit** (7 bots): 5 TPs (+$0.621), 4 DCAs across 4 bots. BB-FF activated this hour (first TPs).
- **Bitget** (3 bots): **idle 3rd consecutive hour**. All grids holding inventory between 0.5-3% from anchor.
- **BingX** (2 bots): **idle 3rd consecutive hour**.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3) | 5 → 4 | 6 → 7 |
| BB-BANANAS (#4f) | 1 → 3 | 25 → 23 |
| BB-FF (#46) | 4 → 2 | 12 → 14 |
| BB-ZBT-SASH (#7e) | 5 → 5 | 11 → 11 (DCA filled, slot re-armed) |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$49.799** |
| Δ this iteration | +$0.621 |
| Δ from iter-34 baseline | +$2.773 |

## Verdict for iteration 39

Bybit-driven hour — 5 TPs across 4 bots, including BB-FF's first realized PnL of the run. The level-4 batch on BB-JCT cycled cleanly: TP fills → slot re-arms → DCA fills → TP fills again — exactly the floating-grid behavior described in the handler docstring at [GridFloatHandler.cs:25-29](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L25-L29). Bitget and BingX have now been quiet 3 hours; their anchors sit comfortably and inventory holds steady — waiting for either a rebound to fire TPs or another drop to fire DCAs. **Next cron fire ~22:17 UTC (00:17 Warsaw, May 16).**
