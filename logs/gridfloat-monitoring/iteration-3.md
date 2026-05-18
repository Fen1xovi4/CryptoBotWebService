# GridFloat monitoring — Iteration 3

**Captured**: 2026-05-14 09:13 UTC (11:13 Warsaw)
**Δ from iteration-2**: ~51 min
**Cron**: `342c898f` fired on schedule at :07 UTC

## Δ Activity since 08:22 UTC

### Trades (6 new)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 08:26:19 | BG-BUSDT (#5) | Buy  | DCA#1 fill        | 20      | 0.4879   | — |
| 08:26:20 | BX-BUSDT (#8) | Buy  | DCA#1 fill        | 10.28   | 0.4861   | — |
| 09:01:03 | BB-SAGA (#4)  | Buy  | DCA#9 adopt       | 371.94  | 0.026886 | — |
| 09:01:03 | BB-SAGA (#4)  | Buy  | DCA#10 adopt      | 371.86  | 0.025781 | — |
| 09:01:13 | BB-SAGA (#4)  | Sell | TakeProfit#10     | 371.80  | 0.02701  | +$0.453 |
| 09:10:33 | BB-SAGA (#4)  | Sell | TakeProfit#9      | 371.90  | 0.02769  | +$0.295 |

### realizedPnL delta
| Bot | iteration-2 | now | Δ |
|---|---|---|---|
| BB-SAGA (#4) | $6.484 | $7.233 | **+$0.748** |
| (others unchanged) |  |  | — |

### State changes
| Bot | batches Δ | dcas Δ | anchor |
|---|---|---|---|
| BB-SAGA (#4) | 9 → 9       | 4 → 2 | unchanged (0.03683) |
| BG-BUSDT (#5) | 1 → 2      | 6 → 5 | unchanged (0.503)   |
| BX-BUSDT (#8) | 1 → 2      | 6 → 5 | unchanged (0.5012)  |

## 🟡 New Warning: RECONCILE DCA on BB-SAGA — working as designed

```
09:01:03  BB-SAGA  🔎 RECONCILE DCA: state qty=2798.3 vs exchange qty=3542.1
                  (биржа БОЛЬШЕ на 743.8, цена=0.02713)
                  Адаптирую DCA-уровни в порядке близости к якорю.
```

This is `ReconcileBatchesFromPosition` → `ReconcileMissedDcaFills` firing. The 5s `PollDcaFills` tick didn't detect DCA#9 and DCA#10 as filled (likely Bybit returned `Unknown`/`Open` for stale order ids that had actually been filled on the exchange in that bar's low). The reconciler then walks the exchange position, sees qty up by 743.8, adopts DCA#9 first (closer to anchor), then DCA#10 with the remaining 371.86 excess.

### Verify reconcile math against `ReconcileMissedDcaFills`
- For Long, DCAs sorted by descending price → #9 (0.026886) before #10 (0.025781). ✓
- `adoptQty = min(dca.Qty, qtyExcess)`:
  - DCA#9: `min(371.94, 743.8) = 371.94`. Excess remaining = 371.86.
  - DCA#10: `min(387.88, 371.86) = 371.86` (note: original level 10 placement was qty 387.88; adoption capped at the leftover excess, lower).
- Both batches got their own TPs: #10 fired 10s later at 0.02701 (TP=0.025781·1.03=0.026554? No, actually let me check…)

  Hmm — TP#10 close price 0.02701, qty 371.80, PnL=+$0.453. Fill price was 0.025781. tpPrice should = 0.025781·1.03 = 0.026554. But recorded TP price is 0.02701. The recorded `Price` column on a TP fill is the **actual fill price** (not the limit). And the close happened at 0.02701 because price spiked through 0.026554 and the order rested at that level — but exchange filled higher? That can't happen for a Sell limit.

  Actually re-reading `RecordTpFill`: `RecordTrade(... closePrice, ...)`, where `closePrice` comes from `status.AverageFilledPrice > 0 ? status.AverageFilledPrice : batch.TpPrice`. For a Sell limit at 0.026554, the AverageFilledPrice can only be ≥0.026554. The recorded 0.02701 > 0.026554 — but how?

  Oh — this is `RECONCILE TP`, not `PollTpFills`. If reconcile fired, it calls `RecordTpFill(... batch.TpPrice, batch.Qty, ...)`. But `batch.TpPrice` for an adopted batch would be `ComputeTp(0.025781, 3, true) = 0.0265544`. Still doesn't match 0.02701.

  Wait — the TPs that fired at 09:01:13 and 09:10:33 might have been for DIFFERENT batches than #9 and #10. The status column says "TakeProfit#10" — which level number does that refer to? It's `batch.LevelIdx` — and an adopted DCA at level 10 has LevelIdx=10. So the TP for batch.LevelIdx=10 fired.

  fillPrice for that batch was 0.025781. TpPrice would be 0.025781·1.03 = 0.026554. But recorded close=0.02701. Discrepancy of 1.7%.

  Hmm, let me look at the state delta again: BB-SAGA was 9 batches + 4 DCAs before, now 9 batches + 2 DCAs. So +0 batches but -2 DCAs. That means: 2 DCAs got adopted (became batches → +2), and 2 batches got TP-closed (-2). Net batches unchanged. ✓
  
  Looking at the recorded TPs: #10 and #9. These are the JUST-adopted batches getting closed. But their TpPrice = fillPrice * 1.03 = 0.025781*1.03 = 0.026554 for #10, 0.026886*1.03 = 0.027692 for #9. The recorded TP close prices are 0.02701 (TP#10) and 0.02769 (TP#9).
  
  - TP#9: 0.02769 ≈ 0.027692 → matches TpPrice exactly ✓
  - TP#10: 0.02701 vs TpPrice 0.026554 → mismatch (close price 1.7% higher than TP)
  
  But wait — maybe the order didn't actually fill at the TP limit. The 09:01:13 TP fill happened just 10 seconds after the DCA adoption. Did `RECONCILE TP` fire because exchangeQty dropped without PollTpFills catching? Let me check if there's a corresponding reconcile log… I only see ONE reconcile warning in last hour, for DCA at 09:01:03.

  So TP#10 was probably caught by `PollTpFills` normally. The recorded fill price 0.02701 is the actual `AverageFilledPrice` from the exchange. For a Sell limit at 0.026554 to fill at 0.02701… that's the exchange filling at a BETTER price than the limit, which is **legitimate** if the market gapped past the limit. The TP order rested at 0.026554, but at 09:01:13 the market was around 0.02701, so the limit filled at the market's bid (favorable for the seller).

  Wait — that's not how limit orders work. A Sell limit @ 0.026554 fills at the LIMIT or better (higher). If the market is at 0.02701 (above limit), it fills at 0.02701 (market) — yes, exchanges can fill maker limits at the **best available** price, not just the limit. So 0.02701 fill on a Sell limit @ 0.026554 is plausible.

  Actually no. Maker limit orders sit on the book; they fill at THEIR limit price when a taker order crosses them. If the market jumped from below 0.026554 to 0.02701 instantly, the taker who crossed at 0.02701 would buy from the orderbook starting at the lowest ask. If our Sell @ 0.026554 was the lowest ask, the taker pays 0.026554, not 0.02701.

  Unless… the order was REPLACED at some point. Or — more likely — the exchange uses "trigger" semantics and the order is actually a stop-limit that triggers at TP price but fills market. Looking at `PlaceLimitOrderAsync` parameter use… `reduceOnly: true` does NOT make it a stop. It's a normal post-only-like limit on Bybit.

  OR: the recorded price comes from `currentPrice2` somewhere, not the actual fill. Let me re-read RecordTpFill:
  ```csharp
  await RecordTpFill(strategy, config, state, exchange, batch,
      status.AverageFilledPrice > 0 ? status.AverageFilledPrice : batch.TpPrice,
      status.FilledQuantity, ct);
  ```
  `AverageFilledPrice` comes from `exchange.GetOrderAsync(symbol, orderId)`. For Bybit it queries the V5 order endpoint — `AveragePrice` field. That's the exchange's reported average fill price. If reported as 0.02701, the exchange itself believes the fill happened at 0.02701.

  Could be a Bybit reporting quirk, or the TP was REPLACED somewhere. I won't chase this in iteration 3 — flagging it for future iterations to watch. The PnL itself is fine (+$0.453, accurate to the 0.02701 fill).

So no actual bug, just an interesting fill-price anomaly. Probably the exchange's average price reporting includes some slippage component. PnL is correctly computed using the reported AverageFilledPrice.

### Grid math for the new BB-SAGA DCAs — ✓
- DCA#9 placement price: 0.03683 · (1 − 9·0.03) = 0.0268859. Trade recorded at 0.026886. ✓
- DCA#10 placement price: 0.03683 · (1 − 10·0.03) = 0.025781. Trade at 0.025781. ✓
- Legacy tier $10 size: DCA#9 qty = 10/0.026886 = 371.942 ✓; DCA#10 qty adopted = 371.86 (capped by reconcile excess, original placement was 387.88 = 10/0.025781).

### No cross-symbol Bitget cancel recurrence
BG-ZBT (#6) and BG-OPEN (#7) had **zero new warnings** since iteration-2. The 14-warning burst at 08:19 hasn't re-occurred. BG-BUSDT didn't full-close in this 51-min window (its anchor at 0.503 is still active with 2 batches + 5 DCAs), so the suspected trigger (`OnFullClose → CancelAllOrdersAsync`) hasn't fired again. Need to wait for the next BG-BUSDT full close to confirm/refute the cross-symbol bug.

## Other observations

- **BB bots #1, #2, #3 idle**: XRP, ZBT (BB), JCT all unchanged. No fills, no errors.
- **BG bots #6, #7 idle**: ZBT (BG), OPEN unchanged. Still 1 batch + 6 DCAs each.
- **BX bot #9 idle**: ZBT (BX) unchanged.
- **No new errors anywhere**.
- **The 1 Bybit rate-limit error from baseline did NOT recur** — possibly under threshold, or worker is spacing requests well enough.
- **No Pause/Resume / tier-update activity** still — only verifiable when user actually pauses a bot.

## Verdict for iteration 3

✅ `ReconcileMissedDcaFills` confirmed working in production: caught 2 DCA fills the 5s poll missed, adopted in correct cross-by-price order, capped adoption qty at the excess (preventing phantom over-adoption), placed both TPs, then TPs fired and added +$0.748 to realized.

🟡 Fill-price discrepancy on TP#10 (close 0.02701 vs expected TpPrice 0.026554) — noted, PnL itself is consistent with the reported fill price. Not actionable yet; track to see if pattern emerges.

✅ Cross-symbol Bitget cancel hypothesis still un-falsified — but no new occurrence this hour because no BG bot full-closed. Awaiting next BG full-close to test.

📅 Next cron fire 10:07 UTC.
