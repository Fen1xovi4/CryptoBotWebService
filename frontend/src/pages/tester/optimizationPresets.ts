/** Per-strategy catalogue of sweepable config fields for the Tester optimizer.
 *
 * Instead of adding parameters one by one, the panel lists every numeric field of the chosen
 * strategy with a human label and a sensible default range; the user only ticks the ones to
 * sweep. Fields inside arrays (DCA levels, grid tiers, funding ladder levels) are described by
 * a template keyed on the array name — `levels[].stepPercent` — and expanded against the actual
 * config so each existing element gets its own row.
 *
 * Config key names mirror buildConfig.ts (which mirrors the live bot form). Keep in sync. */

import { collectNumericPaths, suggestRange } from './optimization';
import type { StrategyType } from './types';

export interface ParamDef {
  /** Config key, or array template such as "levels[].stepPercent". */
  key: string;
  label: string;
  /** Default sweep range; omitted → ±2 steps around the current value (suggestRange). */
  from?: number;
  to?: number;
  step?: number;
  /** Ticked by default when the strategy is first opened. */
  defaultOn?: boolean;
  /** Short explanation shown under the label. */
  hint?: string;
}

const pct = (from: number, to: number, step: number) => ({ from, to, step });

const MARATG_BASE: ParamDef[] = [
  { key: 'indicatorLength', label: 'Период индикатора (EMA/SMA)', ...pct(20, 200, 10), defaultOn: true },
  { key: 'candleCount', label: 'Кол-во свечей для анализа', ...pct(20, 100, 10) },
  { key: 'offsetPercent', label: 'Отступ от индикатора, %', ...pct(0, 2, 0.25), defaultOn: true },
  { key: 'takeProfitPercent', label: 'Take Profit, %', ...pct(1, 6, 0.5), defaultOn: true },
  { key: 'stopLossPercent', label: 'Stop Loss, %', ...pct(1, 6, 0.5), defaultOn: true },
  { key: 'orderSize', label: 'Ставка, USDT', hint: 'Масштабирует PnL линейно — обычно не оптимизируется' },
  { key: 'martingaleCoeff', label: 'Коэф. мартингейла', ...pct(1.5, 3, 0.25), hint: 'Действует только при включённом мартингейле' },
  { key: 'martingaleStep', label: 'Шаг ступенчатого мартингейла', ...pct(1, 5, 1) },
  { key: 'drawdownBalance', label: 'Баланс для масштаба по просадке, USDT' },
  { key: 'drawdownPercent', label: 'Просадка для масштаба, %', ...pct(5, 30, 5) },
  { key: 'drawdownTarget', label: 'Цель масштаба по просадке, %', ...pct(1, 10, 1) },
];

const PRESETS: Record<StrategyType, ParamDef[]> = {
  MaratG: MARATG_BASE,
  MaratGGrid: [
    ...MARATG_BASE,
    { key: 'gridLotSize', label: 'Лот сетки, USDT' },
    { key: 'gridStepPercent', label: 'Шаг сетки, %', ...pct(0.5, 3, 0.25), defaultOn: true },
    { key: 'gridMaxLots', label: 'Макс. лотов сетки (0 = до SL)', ...pct(0, 10, 1) },
  ],
  HuntingFunding: [
    { key: 'takeProfitPercent', label: 'Take Profit, %', ...pct(0.5, 3, 0.25), defaultOn: true },
    { key: 'stopLossPercent', label: 'Stop Loss, %', ...pct(0.25, 2, 0.25), defaultOn: true },
    { key: 'secondsBeforeFunding', label: 'Секунд до фандинга', ...pct(5, 60, 5) },
    { key: 'closeAfterMinutes', label: 'Закрыть через, мин', ...pct(5, 60, 5) },
    { key: 'minFundingLong', label: 'Мин. фандинг для лонга, %', ...pct(0.5, 2, 0.25) },
    { key: 'minFundingShort', label: 'Мин. фандинг для шорта, %', ...pct(0.5, 2, 0.25) },
    { key: 'fundingRateMin', label: 'Фандинг от, % (воркспейс)', ...pct(0.5, 2, 0.25) },
    { key: 'fundingRateMax', label: 'Фандинг до, % (воркспейс)', ...pct(1, 5, 0.5) },
    { key: 'maxCycles', label: 'Макс. циклов (0 = без лимита)' },
    { key: 'levels[].offsetPercent', label: 'Уровень {n}: отступ, %', ...pct(0.5, 3, 0.25) },
    { key: 'levels[].sizeUsdt', label: 'Уровень {n}: размер, USDT' },
  ],
  SmaDca: [
    { key: 'smaPeriod', label: 'Период SMA', ...pct(20, 200, 10), defaultOn: true },
    { key: 'takeProfitPercent', label: 'Take Profit, %', ...pct(0.5, 3, 0.25), defaultOn: true },
    { key: 'positionSizeUsd', label: 'Размер позиции, USDT' },
    { key: 'entryLimitOffsetPercent', label: 'Отступ лимитного входа, %', ...pct(0, 0.2, 0.05) },
    { key: 'entryLimitTimeoutBars', label: 'Таймаут лимитки, баров', ...pct(1, 10, 1) },
    { key: 'levels[].stepPercent', label: 'DCA-ярус {n}: шаг, %', ...pct(1, 6, 0.5), defaultOn: true },
    { key: 'levels[].multiplier', label: 'DCA-ярус {n}: множитель', ...pct(1, 3, 0.5) },
    { key: 'levels[].count', label: 'DCA-ярус {n}: кол-во докупок', ...pct(1, 8, 1) },
    { key: 'takeProfitTierShifts[].fromTier', label: 'Смещение TP {n}: с яруса', ...pct(1, 8, 1) },
    { key: 'takeProfitTierShifts[].takeProfitPercent', label: 'Смещение TP {n}: TP, %', ...pct(0.2, 2, 0.2) },
  ],
  GridFloat: [
    { key: 'dcaStepPercent', label: 'Шаг DCA, %', ...pct(0.5, 3, 0.25), defaultOn: true },
    { key: 'tpStepPercent', label: 'Шаг TP, %', ...pct(0.5, 3, 0.25), defaultOn: true },
    { key: 'leverage', label: 'Плечо', ...pct(1, 5, 1) },
    { key: 'takeProfitTargetUsd', label: 'Цель Take-Profit, USDT' },
    { key: 'stakeMultiplier', label: 'Множитель ставки (лестница)', ...pct(1.5, 3, 0.5), hint: 'Только в режиме рестарта при выходе из диапазона' },
    { key: 'maxStakeStages', label: 'Макс. ступеней ставки', ...pct(2, 8, 1) },
    { key: 'anchorPriceLimit', label: 'Лимит цены якоря' },
    { key: 'tiers[].upToPercent', label: 'Ярус {n}: до, %', ...pct(5, 30, 5), defaultOn: true },
    { key: 'tiers[].sizeUsdt', label: 'Ярус {n}: ставка, USDT' },
    { key: 'tiers[].dcaStepPercent', label: 'Ярус {n}: свой шаг DCA, %', ...pct(0.5, 3, 0.25) },
    { key: 'tiers[].tpStepPercent', label: 'Ярус {n}: свой шаг TP, %', ...pct(0.5, 3, 0.25) },
  ],
  GridHedge: [
    { key: 'rangePercent', label: 'Диапазон вниз, %', ...pct(5, 30, 5), defaultOn: true },
    { key: 'upperExitPercent', label: 'Выход вверх, %', ...pct(5, 30, 5), defaultOn: true },
    { key: 'dcaStepPercent', label: 'Шаг DCA, %', ...pct(0.5, 3, 0.25), defaultOn: true },
    { key: 'tpStepPercent', label: 'Шаг TP, %', ...pct(0.5, 3, 0.25), defaultOn: true },
    { key: 'betUsdt', label: 'Ставка на уровень, USDT' },
    { key: 'hedgeNotionalUsdt', label: 'Размер хеджа, USDT' },
    { key: 'hedgeLeverage', label: 'Плечо хеджа', ...pct(1, 10, 1) },
    { key: 'gridLeverage', label: 'Плечо сетки', ...pct(1, 5, 1) },
  ],
  SpotPingPong: [
    { key: 'levels[].buyPrice', label: 'Уровень {n}: цена покупки', hint: 'Диапазон по умолчанию — ±2 шага вокруг текущего значения' },
    { key: 'levels[].sellPrice', label: 'Уровень {n}: цена продажи' },
    { key: 'levels[].amountUsdt', label: 'Уровень {n}: сумма, USDT' },
  ],
  SmartGridHedge: [
    { key: 'step', label: 'Шаг сетки (дробь: 0.01 = 1%)', ...pct(0.005, 0.03, 0.0025), defaultOn: true },
    { key: 'nUp', label: 'Ячеек вверх (NUp)', ...pct(5, 20, 5), defaultOn: true },
    { key: 'nDown', label: 'Ячеек вниз (NDown)', ...pct(5, 20, 5), defaultOn: true },
    { key: 'lotUsd', label: 'Лот, USDT' },
    { key: 'leverage', label: 'Плечо', ...pct(2, 10, 1) },
    { key: 'qHedgeOverride', label: 'Q_hedge (монет)', hint: 'Пусто/авто в форме → поля нет в конфиге' },
    { key: 'takeProfitTargetUsd', label: 'Цель Take-Profit, USDT' },
  ],
};

// Discriminators and fee inputs — sweeping them is meaningless or misleading.
const EXCLUDED_KEYS = new Set(['mode', 'positionMode', 'skimMode', 'makerFeeBps', 'takerFeeBps']);

export interface PresetRow {
  path: string;
  label: string;
  hint?: string;
  current: number;
  from: string;
  to: string;
  step: string;
  defaultOn: boolean;
  /** True when the field is not in the catalogue (new/unknown config key). */
  unknown: boolean;
}

const fmt = (v: number) => String(Number(v.toPrecision(10)));

/** Catalogue rows for the strategy, expanded against the current configJson (array elements become
 * numbered rows; fields missing from the config, e.g. optional nulls, are skipped). Numeric config
 * fields not in the catalogue are appended as "unknown" rows so a new config key is still sweepable. */
export function buildPresetRows(strategyType: StrategyType, configJson: string): PresetRow[] {
  const numeric = collectNumericPaths(configJson).filter((p) => !EXCLUDED_KEYS.has(p.path.split('.').pop()?.replace(/\[\d+\]$/, '') ?? ''));
  const byPath = new Map(numeric.map((p) => [p.path, p.value]));
  const used = new Set<string>();
  const rows: PresetRow[] = [];

  for (const def of PRESETS[strategyType] ?? []) {
    const isTemplate = def.key.includes('[]');
    const matches = isTemplate
      ? numeric.filter((p) => normalizePath(p.path) === def.key)
      : byPath.has(def.key)
        ? [{ path: def.key, value: byPath.get(def.key)! }]
        : [];
    for (const m of matches) {
      used.add(m.path);
      const n = isTemplate ? Number(/\[(\d+)\]/.exec(m.path)?.[1] ?? 0) + 1 : 0;
      const range =
        def.from !== undefined && def.to !== undefined && def.step !== undefined
          ? { from: fmt(def.from), to: fmt(def.to), step: fmt(def.step) }
          : suggestRange(m.value);
      rows.push({
        path: m.path,
        label: def.label.replace('{n}', String(n)),
        hint: def.hint,
        current: m.value,
        ...range,
        defaultOn: !!def.defaultOn,
        unknown: false,
      });
    }
  }

  for (const p of numeric) {
    if (used.has(p.path)) continue;
    rows.push({ path: p.path, label: p.path, current: p.value, ...suggestRange(p.value), defaultOn: false, unknown: true });
  }
  return rows;
}

/** "levels[2].stepPercent" → "levels[].stepPercent" */
function normalizePath(path: string): string {
  return path.replace(/\[\d+\]/g, '[]');
}
