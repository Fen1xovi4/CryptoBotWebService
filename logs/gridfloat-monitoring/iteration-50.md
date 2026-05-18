# GridFloat monitoring — Iteration 50

**Captured**: 2026-05-16 08:29 UTC (10:29 Warsaw)
**Δ from iteration-49**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **23 trades** (8 TPs, 15 DCAs), **+$0.907 realized**.
- 🚨 **NEW critical noise: BX-BUSDT 185 warnings** — a persistent `qtyExcess=38.36` mismatch had reconcile-DCA logging 2 warnings every ~13 sec for ~57 min before finally adopting at 08:28:14.
- 🟢 Bitget continued strong — BG-ZBT TP#2 +$0.296, plus 3 BG-BUSDT DCAs accumulating on a continued BUSDT downtrend (lastPrice 0.428 → 0.383).
- ✅ 0 errors (16th clean hour); 0 phantom dupes (hour 16).

## Δ Activity since iter-49

### Trades (23) — TPs and notable DCAs

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 07:31:31 | BX-BUSDT (#1c) | Buy | DCA#7 | 36.37 | 0.41240 | — |
| 07:32:46 | BB-ZBT-SASH (#7e) | Sell | TP#5 | 63.0 | 0.16013 | +$0.096 |
| 07:34:14 | BG-ZBT (#9d) | Sell | TP#2 | 64.0 | 0.16072 | **+$0.296** |
| 07:39:38 | BB-FF (#46) | Sell | TP#3 | 115 | 0.08776 | +$0.095 |
| 07:51:35 | BX-BUSDT (#1c) | Buy | DCA#8 | 37.84 | 0.39634 | — |
| 07:58:17 | BB-BANANAS (#4f) | Sell | TP#6 | 800 | 0.01161 | +$0.042 |
| 08:00:32 | BB-FF (#46) | Sell | TP#2 | 113 | 0.08866 | +$0.094 |
| 08:11:57 | BB-ZBT-SASH (#7e) | Sell | TP#7 | 64.4 | 0.15676 | +$0.095 |
| 08:19:54 | BB-FF (#46) | Sell | TP#2 | 113 | 0.08866 | +$0.094 |
| 08:22:17 | BB-ZBT-SASH (#7e) | Sell | TP#6 | 63.7 | 0.15844 | +$0.095 |
| 08:28:14 | BX-BUSDT (#1c) | Buy | DCA#9 (reconcile, resolved 57-min mismatch) | 38.36 | 0.38028 | — |

15 DCAs across BB-BANANAS, BB-FF, BB-ZBT-SASH, BG-BUSDT (×3), BG-ZBT, BX-BUSDT (×3).

### Realized PnL delta

| Bot | iter-49 | iter-50 | Δ |
|---|---|---|---|
| BB-BANANAS (#4f) | 1.022 | 1.064 | +$0.042 |
| BB-FF (#46) | 1.235 | 1.519 | +$0.284 (3 TPs) |
| BB-ZBT-SASH (#7e) | 2.487 | 2.773 | +$0.286 (3 TPs) |
| BG-ZBT (#9d) | 3.531 | 3.826 | +$0.296 |
| **Σ Δ** | | | **+$0.907** |

### Log counts (since 07:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 0 | 0 |
| BB-SASH-ShortSMA | 0 | 2 | 30 |
| BG-SASH-Insider | 0 | 1 | 10 |
| BX-M-IJKL | 0 | **185** 🚨 | 7 |

## 🚨 Issue tracker

### 🚨 NEW — BX-BUSDT persistent qtyExcess=38.36 reconcile loop (185 warnings)

From 07:31 to 08:28 the bot logged **2 warnings per worker tick (~5-13 sec)** in the form:
```
🔎 RECONCILE DCA: state qty=179.28 vs exchange qty=217.64 (биржа БОЛЬШЕ на 38.36, цена=0.38xx).
После reconcile DCA остаток qtyExcess=38.36 — нет больше DCA-уровней для адаптации.
Возможно ручное открытие извне или повреждение state.
```

Root cause: the exchange had 38.36 extra qty (a DCA had filled exchange-side at ~0.38) but `state.DcaOrders` was **empty** — so `ReconcileMissedDcaFills` at [GridFloatHandler.cs:830-832](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L830-L832) had nothing to adopt. Each tick re-detected the mismatch and re-logged. Finally resolved at 08:28:14 when a fresh DCA placement was made and immediately adopted.

This is a real bug shape worth a Fix #6: when state has zero DCA orders but exchange shows excess qty, the reconcile path needs to *fabricate* a DcaOrder entry from the computed grid level closest to current price, instead of repeatedly logging unrecoverable. Currently the bot spent 57 minutes in a noise-storm.

**Operationally**: not blocking PnL (BX-BUSDT batches still hold and will eventually TP), but a monitoring dashboard would page somebody.

### 🟢 BX-BUSDT margin loop — silenced this hour

The Insufficient-margin warnings stopped completely this hour (drowned out by, but not caused by, the qtyExcess noise). Slots #5/#6/#9 either filled or self-resolved.

### 🟢 BB-JCT phantom DCA#5 — 16 clean hours

### 🟢 BB-M-Algida error spam — 16th clean hour, BB-M-Algida fully silent

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): **idle entire hour** — 0 trades, 0 logs. Anchors well above current price (XRP -0.5%, ZBT -1%, JCT -0.3%, SAGA -1%).
- **Bybit BB-SASH-ShortSMA** (3 bots): 7 TPs (+$0.611), many DCAs as ZBT/BANANAS/FF kept drifting down.
- **Bitget** (3 bots): 1 TP (BG-ZBT +$0.296), 3 BG-BUSDT DCAs deep (DCA#7 / DCA#8 / DCA#9 at 0.406 / 0.390 / 0.385) — accumulating for a recovery.
- **BingX** (2 bots): 3 BX-BUSDT DCAs (same downtrend), 0 TPs. The qtyExcess noise dominated logs.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-BANANAS (#4f) | 7 → 8 | 19 → 18 |
| BB-FF (#46) | 3 → 3 | 13 → 13 (3 TPs + 3 DCAs neutral) |
| BB-ZBT-SASH (#7e) | 6 → 6 | 10 → 10 |
| BG-BUSDT (#3f) | 7 → 10 | 7 → 4 |
| BG-ZBT (#9d) | 3 → 3 | 4 → 4 |
| BX-BUSDT (#1c) | 7 → 10 | 2 → 0 |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$62.084** |
| Δ this iteration | +$0.907 |
| Δ from iter-34 baseline | **+$15.058** |

## Verdict for iteration 50

Active hour for BB-SASH and BG-ZBT but the headline is the **BX-BUSDT log-storm**: 185 warnings over 57 minutes from a single unrecoverable qtyExcess mismatch. The defensive design ("skip rather than corrupt state") prevented any damage to PnL or batch tracking — and the issue self-resolved when the next DCA placement matched up. But this is a real Fix-#6 candidate: the reconcile path needs to either fabricate a DcaOrder entry or cap re-logging to N times. BUSDT continued its downtrend (0.428 → 0.383) so all 3 BUSDT bots stacked deeper inventory; next rally should produce another iter-49-style payoff hour. **Next cron fire ~09:17 UTC (11:17 Warsaw).**
