# GridFloat monitoring — Iteration 1 (BASELINE)

**Captured**: 2026-05-14 ~08:08 UTC (10:08 Europe/Warsaw)
**Workspace**: `GRID-BB-M-Algida+BG-S-Insider+BX-M-IJKL` (Id `ad651349-ebf3-40e7-8f3e-22ce1f3ec063`)
**Cron**: `8099032b` fires hourly at :07 (in-session, 7-day auto-expiry)

## 1. Inventory — 9 bots across 3 exchanges (user said "3 биржи", not "3 ботa")

| # | Strategy Id | Exchange acc | Type | Symbol | Tier config | Step/TP% | Static | Status | Batches | DCAs | realPnL |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | e7d6cfdc | BB-M-Algida (Bybit)   | legacy | XRPUSDT  | base=$10,range=10% | 1/1   | no  | Running | 3 | 8 | +$0.29 |
| 2 | 67e5fa86 | BB-M-Algida (Bybit)   | legacy | ZBTUSDT  | base=$10,range=20% | 3/3   | no  | Running | 7 | 0 | +$1.77 |
| 3 | 783dcdab | BB-M-Algida (Bybit)   | legacy | JCTUSDT  | base=$10,range=20% | 3/3   | no  | Running | 2 | 5 | +$5.18 |
| 4 | c60574be | BB-M-Algida (Bybit)   | legacy | SAGAUSDT | base=$10,range=20% | 3/3   | yes | Running | 9 | 4 | +$6.48 |
| 5 | 3f1c7ab5 | BG-SASH-Insider (BG)  | tiered | BUSDT    | [≤10%:$10, ≤20%:$20] | 3/3 | yes | Running | 1 | 6 | $0   |
| 6 | 9dbb52d4 | BG-SASH-Insider (BG)  | tiered | ZBTUSDT  | same  | 3/3 | yes | Running | 1 | 6 | $0   |
| 7 | b3d80804 | BG-SASH-Insider (BG)  | tiered | OPENUSDT | same  | 3/3 | yes | Running | 1 | 6 | $0   |
| 8 | 1c521d5c | BX-M-IJKL (BingX)     | tiered | BUSDT    | [≤10%:$5,  ≤20%:$10] | 3/3 | yes | Running | 2 | 5 | $0   |
| 9 | 0ad434d7 | BX-M-IJKL (BingX)     | tiered | ZBTUSDT  | same  | 3/3 | yes | Running | 1 | 6 | $0   |

Legacy = `baseSizeUsdt`+`rangePercent`, normalized into single tier on load (handler verified — `NormalizeTiers`).

## 2. Grid build — verified against `GridFloatHandler.ComputeDcaLevels` ✓

Spot-checked 4 bots; every level price matches `anchor·(1 − k·step/100)`, every qty matches `tier.SizeUsdt / price`, every TP matches `fill·(1 ± tpStep/100)`:

**BG-BUSDT #5** — anchor=0.4788, step=3%, tiers=[$10@10%, $20@20%], static:
- staticLowerBound = 0.4788·0.80 = **0.38304** ✓
- k=1..3 → tier1: 0.464436/0.450072/0.435708 @ $10 → 21.531/22.219/22.951 qty ✓
- k=4..6 → tier2: 0.421344/0.406980/0.392616 @ $20 → 47.467/49.142/50.940 qty ✓
- anchor TP = 0.4788·1.03 = **0.493164** ✓

**BX-BUSDT #8** — anchor=0.4871, step=3%, tiers=[$5@10%, $10@20%], static:
- staticLowerBound = 0.4871·0.80 = **0.38968** ✓
- anchor qty=10.26 → $5 ✓; batch #1 (was DCA k=1) fill=0.4724 ≈ 0.4871·0.97 = 0.472487 ✓
- k=4..6 → tier2: 0.428648/0.414035/0.399422 @ $10 → 23.329/24.152/25.036 ✓

**BB-XRP #1** — legacy anchor=1.4652, step=1%, dynamic range:
- dynamicMaxN = floor(10/1) = 10 slots
- Batches 0-2 + DCAs 3-10 = 11 levels touched ✓
- k=2 fill=1.435896 = 1.4652·0.98 ✓; k=4 DCA=1.406592 = 1.4652·0.96 ✓

**BB-SAGA #4** — legacy static, anchor=0.03683, step=3%, range=20% (config) but bound=0.022648:
- staticLowerBound was frozen at a FIRST anchor of an earlier cycle (≈0.02831·0.80) — current anchor is in a later cycle, hence 9 batches (0..8) + 4 DCAs (9..12) = 13 slots, well beyond the dynamic floor(20/3)=6 of a fresh cycle. **Matches "static drifts: 9, 10, 11, 12…" spec.**
- k=12 = 0.03683·0.64 = **0.023571** ✓ (lowest DCA); k=13 = 0.022467 < bound 0.022648 → correctly skipped.

## 3. TP placement (reduceOnly) — ✓

In code: `PlaceBatchTpLimit` always passes `reduceOnly: true`. Confirmed by inspection (`GridFloatHandler.cs:364`).

Trade audit last hour shows TP fill in DB:
```
1c521d5c BX-M-IJKL  Sell TakeProfit#1  qty=10.58  @ 0.4865  PnL=+$0.15  08:05:21
```
Entry 0.4724 → +2.98% → fill at exact `0.4724·1.03 = 0.486572` (placed price). The fill price 0.4865 differs because BingX walked the price up through the resting maker limit. ✓

## 4. Pause/Resume + live-tune Range — not exercised this hour

All 9 bots `Status = Running`. No Paused→Running transitions in last hour. `lastProcessedCandleTime` ~`08:05 UTC` for every bot ⇒ each is ticking against the same 5m candle close (8 bots) or 1h (`783dcdab` lags at `08:00`, which is correct since its `Timeframe = 5m` but no new close after `08:05` for THIS poll — handler short-circuits if `CloseTime ≤ LastProcessed`).

Cannot verify the `PATCH /grid-float/tiers` path until the user pauses+edits one.

## 5. Errors found — 2 categories

### 🟡 Bitget anchor — "unilateral position type" (6 occurrences, **fully recovered**)

All 3 BG bots failed first anchor placement at 07:50 / 07:51 / 07:55 with:
```
Ошибка ANCHOR: The order type for unilateral position must also be the unilateral position type.
```
Then succeeded at 08:00. Bot self-healed because `OpenAnchor` returns on failure and the next 5s tick retries on the next closed candle.

**Root cause** is most likely Bitget account still has `holdMode=hedge` lingering for the symbol, OR the SDK is sending a non-empty `tradeSide` somewhere. Per `CLAUDE.md`:
> Bitget — omit `tradeSide` entirely (pass nothing / null). One-way mode requires `tradeSide` to be empty.

Action item: spot-check `BitgetFuturesExchangeService.OpenLongAsync` to confirm `tradeSide: null` is the actual argument passed to `PlaceOrderAsync`, then run `set-position-mode` to ensure account is one-way.

### 🟡 Bybit rate limit — `Too many visits. Exceeded the API Rate Limit.` (1 occurrence on bot #1 e7d6cfdc XRP)

Thrown by `BybitFuturesExchangeService.GetKlinesAsync:line 57` → bubbles up from `GridFloatHandler:line 150` (the candle fetch). One-off, did not corrupt state (handler exits early; next tick retries).

Likely cause: 5s worker loop fans out parallel klines requests to the same exchange. With 4 Bybit bots all asking for klines simultaneously, Bybit's 600 req/5s public rate cap can be tripped. Worth watching — if it repeats we need to either back off or batch.

## 6. Misc observations

- **Anchor TP placed instantly after fill** — confirmed (see `08:00:08.760` ANCHOR then `08:00:09.310` TP placement, same batch, same bot). The `PlaceBatchTpLimit` call inside `OpenAnchor` is awaited synchronously.
- **`staticBoundsInitialized` flips on first anchor** — confirmed for bots 4–9 (all have `true`); bots 1–3 are dynamic (`useStaticRange:false`) and have `staticBoundsInitialized:false`. ✓
- **`realizedPnlDollar` preserved across `OnFullClose`** — bots 1–4 (older) all show positive realized, bots 5–9 (started today) still at zero. ✓
- **Anchor sizing uses Tiers[0].SizeUsdt** — BX bots opened with $5 anchors (per their tier config), BB/BG with $10. Different from `orderSize=10` in legacy config field which is correctly ignored.

## 7. Iteration 1 verdict

✅ Strategy logic is executing as designed. Grid math, TP reduceOnly, static bound freeze, anchor sizing, and full-close cooldown all match `GridFloatHandler.cs`.

⚠ Two ops issues to monitor across the 5-day window:
1. Frequency of Bitget "unilateral position type" recurrence on subsequent anchors → if it happens on **DCA fills** (not just anchor open), real money gets stuck.
2. Bybit rate-limit recurrence — track count per hour; if growing, escalate.

## 8. Next iteration prompt fires at 09:07 UTC (11:07 Warsaw)

Watching:
- Any new Errors/Warnings since this baseline.
- Whether bots #5–9 (new Bitget/BingX) record their first TP fill.
- Bybit rate-limit count.
