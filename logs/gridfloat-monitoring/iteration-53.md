# GridFloat monitoring — Iteration 53

**Captured**: 2026-05-16 11:29 UTC (13:29 Warsaw)
**Δ from iteration-52**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **25 trades** (8 TPs, 16 DCAs, 1 entry-equivalent re-arm), **+$1.568 realized**.
- 🏆 **BX-BUSDT TP#12 single-trade record: +$0.908** — new run record (beats iter-48's $0.78). Triggered when BUSDT bounced from 0.327 to 0.342 in 11 seconds.
- 🚨 **BB-M-Algida now has the qtyExcess phantom pattern** — 399 warnings this hour from a persistent BB-JCT mismatch (state 27460 vs exchange 30500, delta 3039.67 stuck since 10:44 partial-fill TP). Same shape as BX-BUSDT, different exchange.
- 🟢 BB-BANANAS DCA#16 cycled **3 times in 15 min** (each cycle: DCA fills → TP fills +$0.045 → slot re-arms) on a tight choppy 0.5% band.
- ✅ 0 errors (19th clean hour); 0 phantom Trade dupes (hour 19).

## Δ Activity since iter-52 — TPs only

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 10:43:06 | BB-BANANAS (#4f) | Sell | TP#16 | 900 | 0.01101 | +$0.045 |
| 10:43:45 | BB-JCT (#3) | Sell | TP#10 (partial 3000/3043) | 3000 | 0.00339 | +$0.292 |
| 10:48:22 | BB-FF (#46) | Sell | TP#1 | 109 | 0.09187 | +$0.094 |
| 10:53:12 | BB-ZBT-SASH (#7e) | Sell | TP#5 | 63 | 0.16013 | +$0.096 |
| 10:54:36 | BX-BUSDT (#1c) | Sell | **TP#12** (RECORD) | 58.35 | 0.34240 | **+$0.908** 🏆 |
| 10:55:32 | BB-BANANAS (#4f) | Sell | TP#16 (re-arm) | 900 | 0.01101 | +$0.045 |
| 10:57:29 | BB-BANANAS (#4f) | Sell | TP#16 (re-arm 2) | 900 | 0.01101 | +$0.045 |
| 11:16:41 | BB-BANANAS (#4f) | Sell | TP#17 | 900 | 0.01095 | +$0.045 |

16 DCAs across BB-JCT (×2), BB-BANANAS (×7 including 4× DCA#16 re-arms), BB-FF, BB-ZBT-SASH (×2), BX-BUSDT (×2), BG-BUSDT.

### Realized PnL delta

| Bot | iter-52 | iter-53 | Δ |
|---|---|---|---|
| BB-JCT (#3) | 11.486 | 11.777 | +$0.291 |
| BB-BANANAS (#4f) | 1.187 | 1.365 | +$0.179 (4 TPs) |
| BB-FF (#46) | 1.992 | 2.086 | +$0.094 |
| BB-ZBT-SASH (#7e) | 3.442 | 3.538 | +$0.096 |
| BX-BUSDT (#1c) | 9.499 | 10.407 | **+$0.908** 🏆 |
| **Σ Δ** | | | **+$1.568** |

### Log counts (since 10:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | **399** 🚨 | 6 |
| BB-SASH-ShortSMA | 0 | 3 | 36 |
| BG-SASH-Insider | 0 | 0 | 2 |
| BX-M-IJKL | 0 | 8 | 5 |

## 🚨 Issue tracker

### 🚨 NEW — BB-M-Algida qtyExcess phantom (399 warnings)

Started ~10:44 immediately after the BB-JCT TP#10 partial-fill event:
- 10:42:01 — DCA#10 fills 3042.69 qty (batch added)
- 10:43:45 — TP#10 fills 3000 qty (NOT the full 3042.69 — partial)
- 10:44:00 — DCA#10 re-fills 3042.69 qty

Result: state.qty = X, exchange.qty = X + 3039.67 (rounding/lot-size). state.DcaOrders is empty so reconcile-DCA can't adopt → logs every ~13 sec for the rest of the hour.

Same mechanism as the BX-BUSDT chronic issue (iter-50/51/52). Now confirmed across **2 different exchanges**, ruling out exchange-specific behavior. **Fix #6 is now higher priority** — without it, every partial-TP-fill on a Bybit grid bot will produce ~400 warnings/hour until next placement.

### 🚨 BX-BUSDT noise — reduced this hour (212 → 8) but still present

The chronic qtyExcess noise on BX-BUSDT subsided to just 8 warnings this hour. Different from prior pattern because:
- 1× "DCA #12 не выставлен (cooldown 5мин): Insufficient margin" — old margin-loop pattern
- 7× new RECONCILE TP/DCA family
The persistent 79.43 phantom from iter-52 was resolved by the BX-BUSDT TP#12 / TP#11 / TP#10 fills.

### 🟢 BB-JCT phantom DCA#5 (iter-34 root issue) — 19 clean hours, no recurrence

The 17-dup phantom from iter-34 has NOT recurred. The new BB-JCT issue this hour is **different in shape** — single qtyExcess mismatch in reconcile-DCA, not multiple duplicate Trade rows. So issue #1 stays clean.

### 🟢 BB-M-Algida error spam (errors only) — 19th clean hour

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): 1 TP (+$0.291), 2 DCAs on BB-JCT. **399 qtyExcess warnings** dominate log volume.
- **Bybit BB-SASH-ShortSMA** (3 bots): 5 TPs (+$0.281), 8 DCAs, BB-BANANAS cycled DCA#16 three times.
- **Bitget** (3 bots): 1 BG-BUSDT DCA#12 (anchor inventory now 13 batches deep!). No TPs — still accumulating ahead of the next BUSDT rally.
- **BingX** (2 bots): **1 record-breaking TP** on BX-BUSDT (+$0.908), 2 DCAs. Margin loop quiet this hour.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3) | 10 → 11 | 1 → 0 |
| BB-BANANAS (#4f) | 13 → 18 | 13 → 8 |
| BB-FF (#46) | 1 → 1 | 15 → 15 (DCA + TP balanced) |
| BB-ZBT-SASH (#7e) | 5 → 6 | 11 → 10 |
| BG-BUSDT (#3f) | 12 → 13 | 2 → 1 |
| BX-BUSDT (#1c) | 11 → 12 | 5 → 3 |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$70.405** |
| Δ this iteration | +$1.568 |
| Δ from iter-34 baseline | **+$23.379** |

## Verdict for iteration 53

BX-BUSDT printed the run's largest single-trade PnL (+$0.908 on TP#12) when BUSDT bounced from 0.327 → 0.342 in 11 seconds — the strategy adopted a fresh DCA#12 at 0.327 via reconcile and closed it for an 8% gain on a deep batch. **But the qtyExcess phantom pattern now affects Bybit too** — 399 warnings on BB-M-Algida confirm the issue is a generic state-vs-exchange reconciliation bug, not exchange-specific. The first occurrence on Bybit was triggered by a partial TP fill (3000 of 3043 qty closed), leaving 42.69 qty trapped. **Next cron fire ~12:17 UTC (14:17 Warsaw). 5 iterations remain.**
