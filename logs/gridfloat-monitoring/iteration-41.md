# GridFloat monitoring — Iteration 41

**Captured**: 2026-05-15 23:29 UTC (01:29 Warsaw, May 16)
**Δ from iteration-40**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **6 trades** (3 TPs, 3 DCAs), **+$0.573 realized** — Bitget and BingX woke up after a sharp **BUSDT downspike around 22:35 UTC**.
- 🚨 **NEW ISSUE — BX-BUSDT Insufficient-margin loop**: 11 placement-cooldown warnings for `DCA #7` over 52 min. Account can no longer extend grid past 7 batches.
- ✅ Both reconcile-DCA paths (BG + BX) fired correctly on the BUSDT spike — adopted missed fills authoritatively from the exchange.
- ✅ 0 errors (7th clean hour). No phantom dupes.

## Δ Activity since iter-40

### Trades (6)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 22:35:33 | BG-BUSDT (#3f) | Buy  | DCA#5 | 45.00 | 0.44293 | — |
| 22:35:34 | BX-BUSDT (#1c) | Buy  | DCA#6 | 22.77 | 0.43919 | — |
| 22:35:35 | BX-BUSDT (#1c) | Buy  | DCA#7 | 22.75 | 0.42312 | — |
| 22:35:49 | BX-BUSDT (#1c) | Sell | TP#7  | 22.75 | 0.44010 | **+$0.382** |
| 23:09:54 | BB-ZBT-SASH (#7e) | Sell | TP#4  | 65.10 | 0.15491 | +$0.096 |
| 23:17:54 | BB-ZBT-SASH (#7e) | Sell | TP#3  | 64.50 | 0.15653 | +$0.096 |

### Realized PnL delta

| Bot | iter-40 | iter-41 | Δ |
|---|---|---|---|
| BB-ZBT-SASH (#7e) | 0.574 | 0.765 | +$0.191 |
| BX-BUSDT (#1c) | 5.159 | 5.541 | **+$0.382** |
| **Σ Δ** | | | **+$0.573** |

### Log counts (since 22:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 0 | 0 |
| BB-SASH-ShortSMA | 0 | 0 | 4 |
| BG-SASH-Insider | 0 | 1 | 2 |
| BX-M-IJKL | 0 | **12** | 5 |

## 🚨 Issue tracker

### 🚨 NEW — BX-BUSDT (#1c) — "Insufficient margin" on DCA #7

11 placement-cooldown warnings every ~5 min from 22:35 to 23:27 UTC:
```
DCA #7 не выставлен (cooldown 5мин): Insufficient margin
```
The 5-minute `PlacementCooldownUntil` at [GridFloatHandler.cs:361](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L361) is doing its job (preventing busy-loop), but the underlying issue is real: the BingX account doesn't have enough free margin to place DCA#7 at qty=22.75 ≈ $10 notional. State shows 7 batches + 3 DCAs = 10 slots out of an expected 10 — so slot #7 (and possibly others) are stuck unplaced.

**Money already locked into 7 batches** (~$70 notional). Any further down-leg of BUSDT cannot be DCA'd. Not blocking PnL accrual on existing batches (TP#7 just filled for +$0.382), but the bot is now operating on a **partial grid**.

**User action needed**: top up BingX account or reduce tier-2 size in BX-BUSDT config.

### 🟢 BB-JCT phantom DCA#5 — 7 clean hours
### 🟢 BB-M-Algida error spam — 7th clean hour
### 🟢 Reconcile-DCA paths — fired correctly on BG and BX at 22:35:33-34 (warnings shown were the *successful* adoption notices, not failures)

## Cross-exchange health

- **Bybit** (7 bots): 2 TPs on BB-ZBT-SASH (+$0.191). All other bots idle.
- **Bitget** (3 bots): 1 DCA on BG-BUSDT (+0 PnL) — first activity in 4 hours. The 22:35 BUSDT spike triggered DCA#5.
- **BingX** (2 bots): full cycle on BX-BUSDT in 16 seconds (DCA#6 → DCA#7 → TP#7 +$0.382), then 52 min of margin-cooldown trying to re-arm DCA#7. **Partial-grid state — flagged above.**

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BG-BUSDT (#3f) | 5 → 6 | 9 → 8 |
| BX-BUSDT (#1c) | 6 → 7 | 5 → 3 |
| BB-ZBT-SASH (#7e) | 5 → 3 | 11 → 13 |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$50.415** |
| Δ this iteration | +$0.573 |
| Δ from iter-34 baseline | +$3.389 |

Workspace crossed **$50 cumulative realized** this hour. 🎯

## Verdict for iteration 41

Sharp BUSDT down-move at 22:35 UTC reactivated both Bitget and BingX after 4 hours of dormancy — confirms the grid configurations on BUSDT are properly tiered for volatility. BX-BUSDT executed a textbook anchor → DCA → DCA → TP cycle in 16 seconds and booked +$0.382 — the largest single-trade PnL of the entire run. **But the margin warning is a real issue**: BingX account can't refill DCA#7's slot after the TP, so the bot is now running an incomplete grid. Not catastrophic (existing batches still trade), but it caps further accumulation. **Next cron fire ~00:17 UTC May 16 (02:17 Warsaw).**
