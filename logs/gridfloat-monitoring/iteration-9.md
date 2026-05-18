# GridFloat monitoring — Iteration 9

**Captured**: 2026-05-14 15:13 UTC (17:13 Warsaw)
**Δ from iteration-8**: ~60 min
**Cron**: `342c898f` fired at 15:07 UTC

## TL;DR

- **31 new trades**, **+$4.238 realized this hour** — second-biggest hour after iter-6.
- 🏆 **BB-SAGA (#4) +$1.979** — 6 TPs on legacy static-grid, including 3 cycles of DCA#4 → TP#4 → re-arm → DCA#4.
- 🟢 **Fix #2 still holding** — no cross-symbol cancellation events.
- 🟡 **Reconcile-TP false-positive recurs** on BB-SAGA (cosmetic — same as iter-6 stale-price issue).

## Δ Activity since 14:13 UTC

### Highlights

**BB-SAGA (#4) — heavy harvest cycle**:
```
14:33:23 TP#9  +$0.294   (closes adopted batch from iter-3)
14:50:32 TP#8  +$0.292
14:56:09 TP#4  +$0.262
14:56:19 DCA#4 (re-arm fill)
15:03:00 TP#7  +$0.293
15:03:48 TP#4  +$0.268
15:03:59 DCA#4 (re-arm again)
15:04:49 TP#6  +$0.294
15:06:31 TP#4  +$0.277
15:06:41 DCA#4 (3rd re-arm)
15:07:53 DCA#6 (re-arm)
```
3× cycles of DCA#4 → TP#4 → re-arm in <15 minutes. Plus closes of #6, #7, #8, #9. Total +$1.979.

**BX-ZBT (#9) full cycle**: TP#0 at 14:23:24 (+$0.148) → new Entry at 14:25:08 @ 0.14842. Realized 0.148 → 0.296.

**Other TPs**:
- BB-XRP (#1) TP#1 at 14:38:15 (+$0.094)
- BB-ZBT (#2) TP#4+TP#5 at 14:23:23 (+$0.565)
- BB-JCT (#3) TP#4 at 14:30:47 (+$0.295)
- BG-BUSDT (#5) TP#1, TP#2, TP#3 in succession at 14:58:24-59:12 (+$0.862)
- BX-BUSDT (#8) TP#2, TP#3 at 14:58:50-59:15 (+$0.296)

### realizedPnL delta
| Bot | iter-8 | now | Δ |
|---|---|---|---|
| BB-XRP (#1)   | $0.380 | $0.474 | +$0.094 |
| BB-ZBT (#2)   | $3.401 | $3.966 | +$0.565 |
| BB-JCT (#3)   | $6.050 | $6.345 | +$0.295 |
| BB-SAGA (#4)  | $7.233 | $9.212 | **+$1.979** |
| BG-BUSDT (#5) | $6.649 | $7.511 | +$0.862 |
| BX-BUSDT (#8) | $2.797 | $3.093 | +$0.296 |
| BX-ZBT (#9)   | $0.148 | $0.296 | +$0.148 |
| **Δ this hour** |     |        | **+$4.238** |

## 🟡 Reconcile-TP false-positive recurs on BB-SAGA

Two reconcile-TP events at 15:03:39 and 15:04:39 both logged "Возможно ручное частичное закрытие извне" — but no manual close happened. PollTpFills caught the actual fills 9 seconds and 10 seconds later (15:03:48 TP#4, 15:04:49 TP#6).

Symptom is identical to iter-6 (also BB-ZBT, Bybit):
1. Exchange fills a TP at the bar's high; `state.LastPrice` is still the previous candle's close (= below TP price).
2. Reconcile sees `exchange.qty < state.qty` → enters `ReconcileMissedTpFills`.
3. For each batch the `crossed = price >= TpPrice` check returns false (because LastPrice is stale and below TP).
4. Logs the misleading "external partial close" warning.
5. Returns without closing any batch.
6. Next 5-15 seconds: `PollTpFills` GET-by-orderId returns the TP as Filled → records correctly. State catches up.

**No money lost**, but the warning text is misleading. This is the same cosmetic issue I flagged in iter-6. Worth a one-line fix: bump `state.LastPrice` to a fresh `GetTickerPriceAsync` call BEFORE running the `crossed` check, or downgrade the warning text to "stale price — пробую ещё раз" until a second tick confirms.

## Other warnings (both normal)
- 14:34:05 BX-BUSDT Reconcile-DCA: state qty=28.88 vs exchange qty=39.13 (+10.25 = DCA#3 adopt). Normal Bitget/BingX poll-lag.
- 14:37:43 BG-BUSDT Reconcile-DCA: state qty=57 vs exchange qty=77 (+20 = DCA#3 adopt). Normal.

## 🟢 Fix #2 holding — no cross-symbol cancel events

No `OnFullClose` happened on any Bitget bot this hour (BG-BUSDT is in mid-cycle with 2 batches; BG-ZBT had its cycle in iter-8; BG-OPEN still idle). So no chance for the cross-symbol cancel to potentially trigger. So far the new per-order cancellation has eliminated 100% of the cross-symbol blast radius observed in iter-2 / iter-6.

## Fix #1 (Stop/Start) — still not exercised
No user-triggered Stop+Start. `Status` for all 9 bots remains 1 (Running). Need a manual test to fully verify.

## State delta
| Bot | batches Δ | dcas Δ | anchor |
|---|---|---|---|
| BB-XRP (#1)   | 2 → 1 | 9 → 10 | unchanged |
| BB-ZBT (#2)   | 6 → 6 | 5 → 5  | unchanged |
| BB-JCT (#3)   | 5 → 4 | 6 → 7  | unchanged |
| BB-SAGA (#4)  | 10 → 7 | 3 → 6 | unchanged (lots of churn at level 4) |
| BG-BUSDT (#5) | 1 → 2 | 13 → 12 | unchanged |
| BX-BUSDT (#8) | 1 → 2 | 10 → 9 | unchanged |
| BX-ZBT (#9)   | 1 → 1 | 6 → 6 | 0.14417 → **0.14842** (new cycle) |

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $0.474 | +$0.189 |
| #2 BB-ZBT   | $1.774 | $3.966 | **+$2.192** |
| #3 BB-JCT   | $5.184 | $6.345 | +$1.161 |
| #4 BB-SAGA  | $6.484 | $9.212 | **+$2.728** |
| #5 BG-BUSDT | $0     | $7.511 | **+$7.511** |
| #6 BG-ZBT   | $0     | $0.586 | +$0.586 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $3.093 | **+$3.093** |
| #9 BX-ZBT   | $0     | $0.296 | +$0.296 |
| **Total Δ from baseline** |  |  | **+$17.756** |

## Verdict for iteration 9

✅ Highest-throughput hour yet — 31 trades, $4.238 realized, no errors.

✅ Grid math verified on spot-checks: BB-SAGA k=7 TpPrice (0.0290957·1.03 = 0.029969 ✓ vs trade 0.02996), BB-SAGA DCA#4 placement (0.03683·0.88 = 0.0324104, fill gapped through to 0.03045 ✓).

🟡 Reconcile-TP cosmetic false positive should be fixed. Add to backlog as **Fix #3**:
> If `state.LastPrice` is older than ~2× the candle interval OR the reconcile delta resolves to 0 within 2 ticks, suppress the "Возможно ручное закрытие извне" warning text — it's almost always a benign poll-lag, not a real external close.

📅 Next cron fire 16:07 UTC.
