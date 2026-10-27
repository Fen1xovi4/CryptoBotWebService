import { SPP_MIN_SELL_RATIO, emptySppLevel } from './sppLevelsUtils';
import type { SppLevelForm } from './sppLevelsUtils';

const cellInput =
  'w-full bg-transparent text-text-primary focus:outline-none focus:text-accent-blue';

interface Props {
  levels: SppLevelForm[];
  onChange: (levels: SppLevelForm[]) => void;
}

/** Editable levels table for the SpotPingPong strategy (bot form and tester). */
export function SpotPingPongLevels({ levels, onChange }: Props) {
  const update = (i: number, key: keyof SppLevelForm, value: string) =>
    onChange(levels.map((l, idx) => (idx === i ? { ...l, [key]: value } : l)));
  const remove = (i: number) => onChange(levels.filter((_, idx) => idx !== i));
  const add = () => {
    const last = levels[levels.length - 1];
    onChange([...levels, { ...emptySppLevel(), amountUsdt: last?.amountUsdt || '50' }]);
  };

  return (
    <div>
      <label className="block text-xs font-medium text-text-secondary mb-1">Уровни</label>
      <div className="rounded-lg border border-border overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr className="bg-bg-tertiary text-text-secondary text-xs">
              <th className="px-3 py-2 text-left font-medium">Покупка</th>
              <th className="px-3 py-2 text-left font-medium">Продажа</th>
              <th className="px-3 py-2 text-left font-medium">Сумма USDT</th>
              <th className="px-3 py-2 text-right font-medium">Шаг %</th>
              <th className="px-3 py-2 text-right font-medium">Монет ≈</th>
              <th className="px-3 py-2 w-8" />
            </tr>
          </thead>
          <tbody>
            {levels.map((lvl, i) => {
              const buy = Number(lvl.buyPrice);
              const sell = Number(lvl.sellPrice);
              const amount = Number(lvl.amountUsdt);
              const stepPct = buy > 0 && sell > 0 ? (sell / buy - 1) * 100 : null;
              const coins = buy > 0 && amount > 0 ? amount / buy : null;
              const badStep = stepPct !== null && !(sell > buy * SPP_MIN_SELL_RATIO);
              return (
                <tr key={i} className="border-t border-border/50">
                  <td className="px-3 py-1.5">
                    <input type="number" step="any" min="0" value={lvl.buyPrice} aria-label={`Уровень ${i + 1}: цена покупки`}
                      onChange={(e) => update(i, 'buyPrice', e.target.value)} className={cellInput} />
                  </td>
                  <td className="px-3 py-1.5">
                    <input type="number" step="any" min="0" value={lvl.sellPrice} aria-label={`Уровень ${i + 1}: цена продажи`}
                      onChange={(e) => update(i, 'sellPrice', e.target.value)} className={cellInput} />
                  </td>
                  <td className="px-3 py-1.5">
                    <input type="number" step="any" min="0" value={lvl.amountUsdt} aria-label={`Уровень ${i + 1}: сумма USDT`}
                      onChange={(e) => update(i, 'amountUsdt', e.target.value)} className={cellInput} />
                  </td>
                  <td className={`px-3 py-1.5 text-right tabular-nums ${badStep ? 'text-accent-red' : 'text-text-secondary'}`}>
                    {stepPct !== null ? `${stepPct.toFixed(2)}%` : '—'}
                  </td>
                  <td className="px-3 py-1.5 text-right tabular-nums text-text-secondary">
                    {coins !== null ? Number(coins.toPrecision(4)) : '—'}
                  </td>
                  <td className="px-2 py-1.5 text-center">
                    <button type="button" onClick={() => remove(i)} disabled={levels.length <= 1}
                      className="text-text-secondary/40 hover:text-accent-red transition-colors disabled:opacity-30 disabled:cursor-not-allowed"
                      title="Удалить уровень" aria-label={`Удалить уровень ${i + 1}`}>
                      <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                      </svg>
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <button type="button" onClick={add} className="mt-2 text-xs text-accent-blue hover:text-accent-blue/80 transition-colors">
        + Добавить уровень
      </button>
    </div>
  );
}
