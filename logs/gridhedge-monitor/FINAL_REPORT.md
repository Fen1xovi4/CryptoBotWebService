# GridHedge мониторинг — финальный отчёт

**Окно:** 2026-05-17 15:32 UTC → 2026-05-18 08:14 UTC (~16ч42м)
**Workspace:** `Grid+Hadge` (id `39b74b39-46e7-4870-8de3-4af800a2daf3`), Bybit, локальный стек
**Итераций:** 8 (cron `ad49cc8b` остановлен)
**Артефакты:** `logs/gridhedge-monitor/*.md`

---

## 1. Что тестировалось

3 стратегии типа GridHedge, по одной на каждый поддерживаемый режим:

| Slot | Strategy Id | Mode | PositionMode | Grid leg | Hedge leg | Аккаунт |
|---|---|---|---|---|---|---|
| **S1** | `8a7e7536` | CrossTicker (2) | OneWay | XRPUSDT futures long | ETHUSDT futures short | BB-SASH-ShortSMA |
| **S2** | `76946830` | SameTicker (1) | OneWay (1) | XRPUSDT **spot** long | XRPUSDT futures short | BB-SASHA-General |
| **S3** | `8d51e9c4` | SameTicker (1) | Hedge (2) | XRPUSDT futures long (posIdx=1) | XRPUSDT futures short (posIdx=2) | BB-M-Byglo_Headge |

Параметры (Range/UpperExit/DcaStep/TpStep/Bet/HedgeNotional/HedgeLev):
- S1: 10 / 10 / 1 / 1 / $10 / $84 / 5x
- S2: 5 / 5 / 1 / 1 / $10 / $40 / 5x
- S3: 5 / 5 / 0.5 / 0.5 / $10 / $70 / 10x

---

## 2. Финальный snapshot (08:14 UTC)

| | S1 | S2 | S3 |
|---|---|---|---|
| Status / Phase | 1 / Active | 1 / Active | 1 / Active |
| Anchor | 1.4261 | 1.4215 | 1.4251 |
| StartAnchor | **1.4118** ⚠ | 1.4215 | 1.4107 |
| LastPrice | 1.3862 | 1.3867 | 1.3862 |
| CompletedCycles | 2 | 0 | 0 |
| Batches открыто | 3 | 2 | 4 |
| Pending limits | 6 | 2 | 3 |
| GridPnl (state, всё время) | +$0.285 | +$0.159 | +$0.271 |
| HedgePnl (state, всё время) | −$0.598 | 0 | 0 |
| ArmFails / Cooldown | 0 / — | 0 / — | 0 / — |

XRP за окно сходил с 1.4120 до низов ~1.375, сейчас 1.386 (-1.8% от старта окна).

---

## 3. Активность за окно мониторинга

**Логи (strategy_logs):** 66 записей, **все Info, ноль Warning, ноль Error**.

| | Info | Warning | Error |
|---|---|---|---|
| S1 | 8 | 0 | 0 |
| S2 | 8 | 0 | 0 |
| S3 | 50 | 0 | 0 |

**Трейды (trades, 25 шт):**

| | Trades | MarketEntry | DCA fills | TP closes | HedgeOpen | HedgeClose | Realized PnL | Commission |
|---|---|---|---|---|---|---|---|---|
| S1 | 5 | 0 | 3 | 2 | 0 | 0 | +$0.191 | $0.008 |
| S2 | 5 | 0 | 3 | 2 | 0 | 0 | +$0.159 | $0.040 |
| S3 | 15 | 2 | 7 | 6 | 0 | 0 | +$0.271 | $0.024 |
| **Σ** | **25** | **2** | **13** | **10** | **0** | **0** | **+$0.620** | **$0.072** |

Хедж ни разу не открывался/не закрывался за окно — все цели были внутри active-цикла. Только S3 закрывал level-0 TP **дважды** (ladder-up х2) → market entries +2.

---

## 4. Подтверждённые сценарии state-machine

### ✅ S3 — два полноценных ladder-up'а
1. `19:40:56` TP level-0 закрылся @ 1.4176 ($0.045) → ⬆️ `LadderUpAsync` → отменил 10 pending → Phase=GridArming → следующий тик: новый market entry @ 1.4181 + 10 лимиток за 8с → Phase=Active.
2. `21:36:22` повторил тот же путь @ 1.4251 → ещё 10 лимиток за 7с.

Anchor мигрировал 1.4107 → 1.4176 → 1.4251. StartAnchor остался пинён на 1.4107 — корректно по коду `OpenHedgeAsync`.

### ✅ S3 — каскад DCA fills на падении (22:00–23:42)
7 уровней подряд: −0.5%, −1.0%, −1.5%, −2.0%, −2.5%, −3.0%, −3.5%. Каждый fill моментально (≤500мс) получал TP. **HedgedFuturesGridLeg** (positionIdx=1 Long, reduceOnly=true sell) отработал безошибочно — короткий хедж positionIdx=2 не задет.

### ✅ S3 — TP-каскад на отскоке (23:42–23:54)
4 батча подряд закрылись по TP: −3.5%, −3.0%, −2.0%, −2.5%, по +$0.045 каждый.

### ✅ S2 — TP-placement на спот-leg
**4 TP placement'а за окно, ноль "Insufficient balance"**. Это критический тест: ранее на старте 11:26–11:33 17 мая был баг на 28 повторных отказов. После сессионного фикса (буфер `SellQtyAfterBuyFee = grossFillQty × (1 − takerFeeRate − 0.0001m)` в [GridHedgeHandler.cs:1026-1027](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridHedgeHandler.cs#L1026-L1027)) **проблема не воспроизвелась**.

### ✅ S1 — DCA + TP в Active без ladder-up
3 DCA fills (-2/-3/-4%), 2 TP закрытия (-3/-4%). Уровень 0 не закрывался → нет ladder-up → anchor стабилен.

### ✅ Throughput placement без задержек
S3 второй раз поставил 11 ордеров (1 market + 10 limits) за 7 секунд → `InterOrderDelayMs = 75ms` (~13 req/s) выдерживается, rate-limit Bybit не задет.

### ✅ Slot consistency
У всех трёх ботов сумма (batches + pending + закрытые-по-TP) всегда равна `1 + Range/DcaStep`. Ни одного потерянного слота за 16 часов и 25 fills.

---

## 5. Аномалии и риски

### 🚨 A3 — критичный latent-баг: `Trade.Status varchar(20)` overflow

**Что наблюдалось:** worker в окно iter #5 трижды упал с `Npgsql.PostgresException 22001: value too long for type character varying(20)`. **Источник в этих хитах** — стратегия `105dc7c0 "Test3"` из другого workspace, НЕ наш Grid+Hadge.

**Почему критично для GridHedge:** колонка `Trade.Status` ограничена 20 символами ([backend/src/CryptoBotWeb.Infrastructure/Data/AppDbContext.cs:130](backend/src/CryptoBotWeb.Infrastructure/Data/AppDbContext.cs#L130)). Хендлер `GridHedgeHandler.RecordTrade` пишет статусы из `RecordBatchClosure`:

| Статус | Длина | OK? |
|---|---|---|
| `MarketEntry@0%` | 14 | ✓ |
| `HedgeOpen` / `HedgeClose` | 9–10 | ✓ |
| `GridFill@-3.5%` | 14 | ✓ |
| `TpFill@-3.5%` | 12 | ✓ |
| **`ForceMarketClose@-3.5%`** | **22** | **❌ overflow** |
| **`TpCancelledPartial@-3.5%`** | **24** | **❌ overflow** |

**Когда стрельнёт у наших:**
- `ForceMarketClose@-X%` — в `CloseEverythingAsync` на каждом батче при stop-loss или upper-exit.
- `TpCancelledPartial@-X%` — в `PollTpFillsAsync`, если биржа отменит TP с частичным исполнением.

**Эффект:** транзакция тика откатится (`SaveChangesAsync` в `finally`-блоке), state не сохранится, trade не запишется. Бот будет крутиться в Phase=Exiting* в бесконечном цикле падений. **Грозит потерей трейл-записей и заморозкой цикла на закрытии.**

**Рекомендация (фикс одной правкой, без миграции):**

```csharp
// GridHedgeHandler.cs ~744 и ~842
// было:
RecordTrade(..., $"{reason}@-{batch.LevelPercent}%", ...);
// стало:
var shortReason = reason switch {
    "ForceMarketClose"    => "ForceClose",       // 10 chars
    "TpCancelledPartial"  => "TpCancelPart",     // 12 chars
    _ => reason
};
RecordTrade(..., $"{shortReason}@-{batch.LevelPercent}%", ...);
```
"ForceClose@-3.5%" = 16 chars ✓, "TpCancelPart@-3.5%" = 18 chars ✓.

Альтернатива — миграция `ALTER TABLE trades ALTER COLUMN "Status" TYPE varchar(40)`.

### ⚠ A1 — S1.StartAnchor=1.4118 ≠ S1.Anchor=1.4261

**Что:** StartAnchor S1 не совпадает с anchor цикла. По коду `OpenHedgeAsync` StartAnchor пинится один раз при `Anchor == 0` и переживает ladder-up; `CloseEverythingAsync` обнуляет в конце цикла.

**Подозрение:** между ladder-up'ом 17 мая 10:12 UTC и DCA fill в 14:16 UTC пользователь, вероятно, делал Stop→Start. При этом state не прошёл через `CloseEverythingAsync` (нет инкремента CompletedCycles), но StartAnchor каким-то путём переписался на текущую цену 1.4118.

**Эффект на риск:** триггеры S1 считаются от 1.4118, не от ~1.412. Разница в нижнем триггере: 1.4118×0.9 = 1.2706 vs 1.412×0.9 = 1.2708. **0.014% — практически ноль.**

**Самокоррекция:** только после полного цикла Close → reset → новый Start.

### ⚠ A2 — фоновый Bybit GetKlines rate-limit (не наш)

Стратегия GridFloat `995ed77d` из workspace `GRID-BB-M-Algida+...` стабильно ловит `Too many visits. Exceeded the API Rate Limit.` на `GetKlines`. **На Grid+Hadge не задело ни разу** за 16ч и ~30 placement'ов. Однако это занимает общий Bybit IP-квоту — теоретический фоновый риск.

---

## 6. Что подтверждено как НЕ баг

- ✅ **Hedge mode GridHedge** работает: 11 placement + 11 fill + 4 close на S3 без единой ошибки. `HedgedFuturesGridLeg.PlaceLimitSellAsync(..., reduceOnly: true)` корректно закрывает только Long-сторону (positionIdx=1), не задевая хедж.
- ✅ **Bybit spot buy-fee discount** (`SpotGridLeg.SellQtyAfterBuyFee`) теперь держит — за весь окно ноль "Insufficient balance".
- ✅ **CrossTicker** (XRP grid + ETH hedge): корректно разнесённые legs, разные тикеры, разные anchors (`Anchor` для grid leg, `HedgeAnchor` для hedge leg).
- ✅ **Ladder-up** дважды на S3, оба раза с полным переоткрытием 10/10 limits — корректное состояние.
- ✅ **Slot accounting**: batches + pending + закрытые = const = `1 + Range/DcaStep`.
- ✅ **State persistence** через `SaveChangesAsync` в `finally`-блоке: ни одного потерянного fill за 25 трейдов.
- ✅ **No orphan TP**: каждый fill сопровождается TP placement за ≤500ms.
- ✅ **No stuck cooldown**: `placementCooldownUntil` ни разу не задержался дольше нужного.
- ✅ **No reverse position**: ни одной попытки продать больше, чем хранится в batch.
- ✅ **No invalid state transition**: все переходы шли по схеме Active → (level-0 TP) → GridArming → Active.

---

## 7. Итог по PnL

| | Realized Grid PnL | Realized Hedge PnL | Net | Commission |
|---|---|---|---|---|
| S1 (cross XRP+ETH) | +$0.285 | −$0.598 | **−$0.313** | $0.168 (всё время) |
| S2 (same OneWay) | +$0.159 | 0 | **+$0.159** | $0.040 |
| S3 (same Hedge) | +$0.271 | 0 | **+$0.271** | $0.024 |
| **Σ** | **+$0.715** | **−$0.598** | **+$0.117** | **$0.232** |

**S1 общий минус** — наследие старых циклов, когда ETH-хедж закрывался в минус (накопилось −$0.598 в state). Сами grid-операции прибыльны во всех трёх ботах.

**S2 hedge unrealized** ≈ (1.4206 − 1.3867) × 28.1 = **+$0.953** — компенсирует просадку спот-батчей. Реализуется только на выходе цикла.

**S3 hedge unrealized** ≈ (1.4106 − 1.3862) × 49.6 = **+$1.210** — аналогично, компенсирует.

Если сейчас закрыть всё (стрельнёт A3 на ForceMarketClose!), нетто S2/S3 был бы в плюс по марк-ту-маркет, S1 — около нуля минус накопленный hedge loss.

---

## 8. Рекомендации (по приоритету)

1. **🔴 P0 — Починить A3.** Один patch в `GridHedgeHandler` (см. блок выше). Это блокер для безопасного экзита цикла любого из 3 ботов. Аналогично проверить остальные strategy handlers — где они формируют `Trade.Status`.
2. **🟡 P1 — Разобраться с A1.** Найти в `StrategyController` (или где обрабатывается Start) ветку, которая может перезаписать StartAnchor у не-Done стратегии. Если такая ветка есть — пометить как "только новый цикл".
3. **🟡 P1 — Решить с rate-limit от GridFloat (A2).** Не наш сервис, но если он съест квоту на пиковом placement в Grid+Hadge — это станет нашим. Кандидаты: разделение по аккаунтам, SOCKS5-прокси, либо backoff в `BybitFuturesExchangeService.GetKlinesAsync`.
4. **🟢 P2 — UX:** имя workspace `Grid+Hadge` (опечатка) — мелочь, но раздражает в логах.

---

## 9. Список итераций и отчётов

| # | Время UTC | Δ от предыдущей | Размер | Главное |
|---|---|---|---|---|
| 1 | 16:27 17.05 | (baseline) | 7.1KB | Установлен baseline post-reset, тихо |
| 2 | 18:27 17.05 | 2ч | 5.7KB | Первый DCA fill S2 после фикса, TP без "Insufficient balance" |
| 3 | 02:51 18.05 | 8ч25м | 7.5KB | Большой каскад: 2 ladder-up S3, 13 DCA, 10 TP closes |
| 4 | 02:55 18.05 | 4мин | 2.6KB | Sanity tick после iter #3 — ничего не залипло |
| 5 | 04:40 18.05 | 1ч45м | 5.3KB | **🆕 Обнаружен A3 varchar(20) overflow (у Test3)** |
| 6 | 04:42 18.05 | 2.5мин | 3.5KB | Sanity tick, рекомендация по фиксу A3 |
| 7 | 07:53 18.05 | 3ч10м | 4.0KB | Тихий час, A3 хитов нет, sanity check distance |
| 8 | 07:56 18.05 | 3мин | 1.7KB | Sanity tick, нано-боковик XRP |

Все отчёты: `logs/gridhedge-monitor/`.

---

## 10. Что осталось неразрешённым (open questions)

1. **A3 не воспроизвёлся на наших** — потому что ни один бот не доходил до Phase=Exiting* в окно мониторинга. **Потенциально стрельнёт мгновенно** если XRP резко уйдёт ниже −4.20% от S2.startAnchor или ниже −4.88% от S3.startAnchor (сейчас оба ~2.5% над триггером).
2. **Hedge close**: ни один хедж не закрывался — поведение `CloseEverythingAsync` для futures-хеджа на спот-leg counterpart (S2) и для дополнительной хедж-позиции (S3 в Hedge mode) **не протестировано** на боевых данных в эту сессию. Это станет следующим важным сценарием.
3. **CrossTicker hedge с разными движениями цен** — S1.HedgePnl стабильно отрицателен, но ETH/XRP корреляция не сверялась — может, базовая идея хеджа здесь неоптимальна, или просто рынок сложился неудачно.
