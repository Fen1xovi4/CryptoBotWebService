# GridFloat monitoring — Iteration 2

**Captured**: 2026-05-14 ~08:22 UTC (10:22 Europe/Warsaw)
**Δ from baseline**: ~14 minutes
**Cron**: `342c898f` (fixed — bare prompt, won't recurse `/loop`)

## Δ Activity since 08:08 UTC

### Trades (8 new):

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 08:09:49 | BG-BUSDT (#5) | Sell | TakeProfit#0 | 20    | 0.4931  | +$0.282 |
| 08:10:01 | BG-BUSDT (#5) | Buy  | Entry        | 20    | 0.4933  |   —     |
| 08:11:06 | BX-BUSDT (#8) | Sell | TakeProfit#0 | 10.26 | 0.5017  | +$0.148 |
| 08:15:06 | BX-BUSDT (#8) | Buy  | Entry        | 10.11 | 0.4941  |   —     |
| 08:19:04 | BX-BUSDT (#8) | Sell | TakeProfit#0 | 10.11 | 0.5089  | +$0.148 |
| 08:19:12 | BG-BUSDT (#5) | Sell | TakeProfit#0 | 20    | 0.508   | +$0.290 |
| 08:20:05 | BG-BUSDT (#5) | Buy  | Entry        | 19    | 0.503   |   —     |
| 08:20:06 | BX-BUSDT (#8) | Buy  | Entry        | 9.97  | 0.5012  |   —     |

**Two full cycles completed** on each of BG-BUSDT and BX-BUSDT. Cycle = TP fill → flat → cooldown 1 bar → new anchor → DCA ladder placed.

### realized PnL delta
| Bot | Baseline | Now | Δ |
|---|---|---|---|
| BG-BUSDT (#5) | $0     | $0.572 | **+$0.572** |
| BX-BUSDT (#8) | $0     | $0.443 | **+$0.443** |
| (all others)  | unchanged | — | — |

### Cooldown spec — ✓ confirmed
Worker log at 08:20:04: `"Кулдаун снят (закрылся бар после полного закрытия в 08:19:12) — открываю новый якорь"` — exactly one closed bar (5m TF) after the full close, the next anchor opened. Spec match: `OnFullClose` sets `OpenAfterTime = UtcNow`; next handler tick gates on `lastClosed.CloseTime > OpenAfterTime`.

### Grid build at new anchor — ✓
BX-BUSDT new anchor 0.5012, anchorSize=$5, tiers=[≤10%:$5, ≤20%:$10], step=3%, dynamic check:
- DCA #1: 0.486164 = 0.5012·0.97 ✓, qty=10.284 = 5/0.486164 ✓
- DCA #2: 0.471128 = 0.5012·0.94 ✓
- DCA #3: 0.456092 = 0.5012·0.91 ✓
- DCA #4: 0.441056 = 0.5012·0.88 ✓ (tier2 starts: qty=22.67 = 10/0.441056 ✓)
- DCA #5: 0.426020 = 0.5012·0.85 ✓
- DCA #6: 0.410984 = 0.5012·0.82 ✓
All matches `ComputeDcaLevels` formula. ✓

### Anchor TP price uses placed limit, not fill price — ✓
BX-BUSDT new anchor: fill=0.5012, TP placed @ 0.516236 = 0.5012·1.03 — matches `ComputeTp(fillPrice, tpStepPercent=3, isLong=true)`. TP order id present. reduceOnly=true (from code).

## 🚨 SUSPICIOUS: cross-symbol DCA cancellation on Bitget

At **08:19:20–21 UTC** (8 seconds after BG-BUSDT full-closed and called `CancelAllOrdersAsync(symbol="BUSDT")` in `OnFullClose`):

- **BG-ZBT (#6)** — all 6 DCAs detected as `Cancelled` by PollDcaFills:
  ```
  DCA #1..#6 лимит отменён/отклонён без филла — слот переустановится на следующем тике
  ```
- **BG-OPEN (#7)** — same: all 6 DCAs cancelled at 08:19:20–21.

Then at 08:19:23–25 the heal flow re-placed all 12 DCAs successfully. **State recovered** — both bots now show 1 batch + 6 DCAs (same as baseline).

### Why this is suspicious

The cancellation pattern matches exactly the moment BG-BUSDT's `OnFullClose` called `CancelAllOrdersAsync`. Code path:
```csharp
// GridFloatHandler.cs:791
try { await exchange.CancelAllOrdersAsync(config.Symbol); } catch { }
```
and
```csharp
// BitgetFuturesExchangeService.cs:426
var result = await _client.FuturesApiV2.Trading.CancelAllOrdersAsync(
    BitgetProductTypeV2.UsdtFutures, bitgetSymbol, "USDT");
```

The SDK DOES pass `bitgetSymbol="BUSDT"` as a per-symbol filter. **In theory** it should only cancel BUSDT orders. But the observation says otherwise — ZBT and OPEN orders disappeared at the same instant.

### Possible explanations (in order of likelihood)
1. **JK.Bitget.Net 3.6.0 SDK bug / Bitget V2 API quirk**: when both `productType` and `symbol` are passed to `cancel-all-orders`, the API may ignore the symbol filter and cancel everything for the productType (USDT-FUTURES). Worth a one-off integration test.
2. **Order TTL on Bitget**: by coincidence the 12 DCAs from 07:51 hit a server-side TTL/expiry exactly at 08:19, unrelated to bot #5's cancel. Bitget has no documented public TTL on GTC limits, so unlikely.
3. **Bitget cross-margin auto-cancel** triggered by margin shift when BG-BUSDT closed. Possible if account is on cross-margin and the freed margin caused a re-calc. Worth checking account margin mode.

### Risk
If this is bug #1, every time ANY BG bot full-closes its grid, **all DCAs for ALL other BG bots on the same account get nuked**. They recover via heal, but for ~2-3 seconds none of them have resting DCAs — if price gaps through a level in that window, the fill is missed and the grid runs without that batch.

### Suggested action
- Reproduce in isolation: with a single BG bot live, call `BitgetFuturesExchangeService.CancelAllOrdersAsync("FOOUSDT")` while another symbol has DCAs resting. See if they disappear.
- If yes: switch to per-order cancel (loop `CancelOrderAsync(symbol, orderId)` over state.DcaOrders + state.Batches.TpOrderId) in `OnFullClose` and `SyncFromExchangeOnStartup` for the Bitget path.

## Other observations

- **No new Errors** since baseline. The 6 "unilateral position type" errors are stale from before 08:00 (start-of-day anchor failures).
- **No Pause/Resume / tier-update activity** still — can't exercise those paths yet.
- **Bybit bots #1–4 totally idle this 14-min window** — no fills, no errors, no rate-limit recurrence.
- **BX-ZBT (#9) idle** — no fills since 07:56 anchor open.

## Verdict for iteration 2

✅ Two clean full cycles on BG-BUSDT and BX-BUSDT prove the spec end-to-end: TP → flat → 1-bar cooldown → new anchor → re-armed DCA ladder. PnL accrual correct.

🚨 Cross-symbol DCA cancellation on Bitget is the first real bug-candidate found. Needs reproduction.

📅 Next cron fire 09:07 UTC.
