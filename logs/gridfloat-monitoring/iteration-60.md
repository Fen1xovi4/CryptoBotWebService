# GridFloat monitoring — Iteration 60 (Phase 2)

**Captured**: 2026-05-17 10:48 UTC (12:48 Warsaw)
**Δ from iteration-59**: ~4 min (cron re-fired or manual trigger — see note)
**Cron**: `17 * * * *` (Warsaw) — job `bb358590`

> **Note**: Iteration 59 was captured at 10:44 UTC and iter-60 fired only 4 min later (likely a queued cron fire from session warm-up). Data window overlaps almost entirely with iter-59. Delta is therefore very small. **From iter-61 onwards the gap should normalize to ~60 min** as the cron settles on its :17 schedule.

## TL;DR

- ✅ Fix #6 dedupe **still working**: identical 2 qtyExcess fires in 60-min window (no escalation).
- 🟢 1 new trade since iter-59: **BB-SASH/FFUSDT DCA#3 fill** at 10:47:20 (qty 111 @ 0.08966) — FF grid now 4 batches / 12 pending DCAs.
- 🟡 0 lot-step / 0 partial-TP fires (no new TP placements this 4-min window — expected).
- 🚨 4 transient API errors unchanged (same set as iter-59) — 60-min rolling window still captures them.
- 🟢 Realized PnL totals unchanged ($93.535 cumulative).

## Δ Activity since iter-59 (4-min sub-window)

### New trade (1)

| Time UTC | Acc | Symbol | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|---|
| 10:47:20 | BB-SASH | FFUSDT | Buy | DCA#3 | 111 | 0.08966 | — (fill, no PnL) |

No new TPs. Realized PnL Δ: **$0.00**.

### Log counts (last 60 min, our workspace) — unchanged

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 4 | 2 |
| BB-SASH-ShortSMA | 0 | 1 | 36 |
| BG-SASH-Insider | 0 | 0 | 0 |
| BX-M-IJKL | 0 | 0 | 0 |

(`BB-SASH-ShortSMA` Info bumped 34 → 36 — the new DCA fill logged 2 info lines.)

## 🚨 Issue tracker

### ✅ BB-M-Algida qtyExcess storm — Fix #6 holding steady

2 qtyExcess fires in 60 min, same 3082.35604418 orphan. Dedupe state in JSONB confirmed unchanged.

### 🟡 Fix #6 PlaceBatchTpLimit / RecordTpFill — still not exercised

No TP placements happened in this 4-min window, so the lot-step floor / partial-fill paths haven't fired. JCT still hasn't recovered to its TP trigger.

### 🟢 Worker errors — same 4 transient API errors as iter-59

Same strategies: 3× Bybit rate-limit (4638fce0/7e848311/995ed77d), 1× Bitget timeout (d1a647d2). Rolling window still includes them. None caused state corruption.

## State delta (vs iter-59)

| Bot | iter-59 bat/dca | iter-60 bat/dca | realized |
|---|---|---|---|
| BB-SASH/FF (#46) | 3/13 | **4/12** | 4.180 (DCA#3 fill — 1 slot moved DCA→batch) |
| All others | (12 unchanged) | (12 unchanged) | — |

Inventory: 88 → **89 batches**, 76 → **75 DCAs** (one slot conversion only).

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$93.535** (unchanged) |
| Δ vs iter-59 | $0.00 |
| Δ vs iter-34 baseline ($47.026) | +$46.509 |

## Verdict for iteration 60

Trivial iteration — only 4 min elapsed since iter-59. **Fix #6 status unchanged and stable**. FF picked up another DCA on a continued drift down (0.08966 = -3.6% from anchor 0.09302). FF inventory now well-positioned for next bounce.

**Real signal will come from iter-61 (~11:17 UTC / 13:17 Warsaw, ~30 min from now)** when the rolling 60-min window finally clears the pre-existing trades and we see a "clean" hour of post-Fix #6 activity.

**22 iterations remain (61-82).**
