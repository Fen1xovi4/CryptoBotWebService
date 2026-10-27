export interface SppLevelForm {
  buyPrice: string;
  sellPrice: string;
  amountUsdt: string;
}

/** Sell must clear buy by two 0.1% fees plus a small buffer (mirrors the backend check). */
export const SPP_MIN_SELL_RATIO = 1.0021;

export const emptySppLevel = (): SppLevelForm => ({ buyPrice: '', sellPrice: '', amountUsdt: '50' });

/** Returns a Russian error message, or null when the levels are valid. */
export function validateSppLevels(levels: SppLevelForm[]): string | null {
  if (levels.length === 0) return 'Добавьте хотя бы один уровень';
  for (let i = 0; i < levels.length; i++) {
    const buy = Number(levels[i].buyPrice);
    const sell = Number(levels[i].sellPrice);
    const amount = Number(levels[i].amountUsdt);
    const n = i + 1;
    if (!(buy > 0)) return `Уровень ${n}: цена покупки должна быть больше 0`;
    if (!(sell > 0)) return `Уровень ${n}: цена продажи должна быть больше 0`;
    if (!(amount > 0)) return `Уровень ${n}: сумма должна быть больше 0`;
    if (!(sell > buy * SPP_MIN_SELL_RATIO))
      return `Уровень ${n}: цена продажи должна быть выше цены покупки минимум на 0.21% (комиссии 2×0.1% + запас)`;
  }
  return null;
}
