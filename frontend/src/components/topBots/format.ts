import type { TopBot } from '../../api/topBots';

export type TopLang = 'ru' | 'en';

export const STRATEGY_LABELS: Record<string, string> = {
  MaratG: 'MaratG (EMA Bounce)',
  MaratGGrid: 'MaratG+Grid',
  HuntingFunding: 'HuntingFunding',
  SmaDca: 'SMA DCA',
  GridFloat: 'GridFloat',
  GridHedge: 'GridHedge',
  SmartGridHedge: 'SmartGridHedge',
};

export const STRATEGY_TYPES = Object.keys(STRATEGY_LABELS);

export function strategyLabel(type: string): string {
  return STRATEGY_LABELS[type] ?? type;
}

export function formatPercent(v: number, digits = 2): string {
  const s = Math.abs(v).toFixed(digits);
  return `${v > 0 ? '+' : v < 0 ? '-' : ''}${s}%`;
}

export function formatUsd(v: number, signed = false): string {
  const abs = Math.abs(v);
  const s = abs.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const sign = v < 0 ? '-' : signed && v > 0 ? '+' : '';
  return `${sign}$${s}`;
}

export function pnlColor(v: number): string {
  return v > 0 ? 'text-accent-green' : v < 0 ? 'text-accent-red' : 'text-text-primary';
}

// ---------------------------------------------------------------------------
// Key settings (short, per strategy type)
// ---------------------------------------------------------------------------

export interface SettingItem {
  label: string;
  value: string;
}

const L = {
  ru: {
    indicator: 'Индикатор', timeframe: 'Таймфрейм', tp: 'Тейк-профит', sl: 'Стоп-лосс', orderSize: 'Размер ордера',
    martingale: 'Мартингейл', direction: 'Направление', yes: 'Да', no: 'Нет', long: 'Лонг', short: 'Шорт', both: 'Оба',
    smaPeriod: 'SMA период', positionSize: 'Размер позиции', dcaLevels: 'DCA уровни', tiers: 'Тиры', dcaStep: 'Шаг DCA',
    tpStep: 'Шаг TP', leverage: 'Плечо', levels: 'Уровни', maxCycles: 'Макс. циклов', lot: 'Лот', step: 'Шаг',
    nUp: 'Ячеек вверх', nDown: 'Ячеек вниз', range: 'Диапазон', hedge: 'Хедж', bet: 'Ставка',
    restart: 'Лестница ставок', autoRestart: 'Авто-рестарт', secondsBefore: 'Сек. до фандинга',
    gridStep: 'Шаг сетки', gridMaxLots: 'Макс. докупок',
  },
  en: {
    indicator: 'Indicator', timeframe: 'Timeframe', tp: 'Take profit', sl: 'Stop loss', orderSize: 'Order size',
    martingale: 'Martingale', direction: 'Direction', yes: 'Yes', no: 'No', long: 'Long', short: 'Short', both: 'Both',
    smaPeriod: 'SMA period', positionSize: 'Position size', dcaLevels: 'DCA levels', tiers: 'Tiers', dcaStep: 'DCA step',
    tpStep: 'TP step', leverage: 'Leverage', levels: 'Levels', maxCycles: 'Max cycles', lot: 'Lot', step: 'Step',
    nUp: 'Cells up', nDown: 'Cells down', range: 'Range', hedge: 'Hedge', bet: 'Bet',
    restart: 'Stake ladder', autoRestart: 'Auto-restart', secondsBefore: 'Sec. before funding',
    gridStep: 'Grid step', gridMaxLots: 'Max add-ons',
  },
} as const;

function num(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

function rows(v: unknown): Record<string, unknown>[] {
  return Array.isArray(v) ? (v as Record<string, unknown>[]) : [];
}

function fmtDir(v: unknown, lang: TopLang): string | null {
  const l = L[lang];
  if (typeof v === 'string') {
    const s = v.toLowerCase();
    if (s === 'long') return l.long;
    if (s === 'short') return l.short;
    if (s === 'both') return l.both;
    return v;
  }
  return null;
}

export function getKeySettings(bot: TopBot, lang: TopLang = 'ru'): SettingItem[] {
  const c = bot.config ?? {};
  const l = L[lang];
  const out: SettingItem[] = [];
  const add = (label: string, value: string | null | undefined) => {
    if (value !== null && value !== undefined && value !== '') out.push({ label, value });
  };
  const pct = (v: unknown) => (num(v) !== null ? `${num(v)}%` : null);
  const usd = (v: unknown) => (num(v) !== null ? `$${num(v)}` : null);
  const lev = (v: unknown) => (num(v) !== null ? `×${num(v)}` : null);
  const plain = (v: unknown) => (num(v) !== null ? String(num(v)) : null);
  const tf = bot.timeframe ?? (typeof c.timeframe === 'string' ? c.timeframe : null);

  switch (bot.strategyType) {
    case 'MaratG':
    case 'MaratGGrid': {
      if (c.indicatorType || c.indicatorLength) {
        add(l.indicator, `${String(c.indicatorType ?? 'EMA')} ${String(c.indicatorLength ?? '')}`.trim());
      }
      add(l.timeframe, tf);
      add(l.tp, pct(c.takeProfitPercent));
      add(l.sl, pct(c.stopLossPercent));
      add(l.orderSize, usd(c.orderSize));
      if (typeof c.useMartingale === 'boolean') {
        add(l.martingale, c.useMartingale ? `${l.yes} ×${num(c.martingaleCoeff) ?? ''}` : l.no);
      }
      add(l.direction, c.onlyLong ? l.long : c.onlyShort ? l.short : l.both);
      if (bot.strategyType === 'MaratGGrid') {
        add(l.gridStep, pct(c.gridStepPercent));
        if ((num(c.gridMaxLots) ?? 0) > 0) add(l.gridMaxLots, plain(c.gridMaxLots));
      }
      break;
    }
    case 'SmaDca': {
      add(l.timeframe, tf);
      add(l.direction, fmtDir(c.direction, lang));
      add(l.smaPeriod, plain(c.smaPeriod));
      add(l.tp, pct(c.takeProfitPercent));
      add(l.positionSize, usd(c.positionSizeUsd));
      const lv = rows(c.levels);
      if (lv.length > 0) {
        add(l.dcaLevels, lv.map((x) => `${String(x.count ?? 1)}×${String(x.stepPercent ?? '?')}%`).join(', '));
      } else {
        add(l.dcaStep, pct(c.dcaStepPercent));
      }
      break;
    }
    case 'GridFloat': {
      add(l.timeframe, tf);
      add(l.direction, fmtDir(c.direction, lang));
      const tiers = rows(c.tiers);
      if (tiers.length > 0) add(l.tiers, String(tiers.length));
      add(l.dcaStep, pct(c.dcaStepPercent));
      add(l.tpStep, pct(c.tpStepPercent));
      add(l.leverage, lev(c.leverage));
      if (c.restartOnRangeExit === true) {
        add(l.restart, `×${num(c.stakeMultiplier) ?? '?'}, ${num(c.maxStakeStages) ?? '?'}`);
      }
      break;
    }
    case 'HuntingFunding': {
      const lv = rows(c.levels);
      if (lv.length > 0) {
        add(l.levels, lv.map((x) => `${String(x.offsetPercent ?? '?')}% / $${String(x.sizeUsdt ?? '?')}`).join(', '));
      }
      add(l.tp, pct(c.takeProfitPercent));
      add(l.sl, pct(c.stopLossPercent));
      add(l.secondsBefore, plain(c.secondsBeforeFunding));
      add(l.maxCycles, plain(c.maxCycles));
      break;
    }
    case 'GridHedge': {
      add(l.range, pct(c.rangePercent));
      add(l.dcaStep, pct(c.dcaStepPercent));
      add(l.tpStep, pct(c.tpStepPercent));
      add(l.bet, usd(c.betUsdt));
      add(l.hedge, usd(c.hedgeNotionalUsdt));
      add(l.leverage, lev(c.gridLeverage));
      break;
    }
    case 'SmartGridHedge': {
      add(l.lot, usd(c.lotUsd));
      add(l.step, plain(c.step));
      add(l.nUp, plain(c.nUp));
      add(l.nDown, plain(c.nDown));
      add(l.leverage, lev(c.leverage));
      if (typeof c.autoRestart === 'boolean') add(l.autoRestart, c.autoRestart ? l.yes : l.no);
      break;
    }
    default: {
      add(l.timeframe, tf);
      add(l.direction, bot.direction);
    }
  }
  return out;
}
