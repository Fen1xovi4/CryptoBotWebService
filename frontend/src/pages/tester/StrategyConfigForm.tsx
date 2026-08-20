import { useState } from 'react';
import api, { optimizeSmartGridHedge } from '../../api/client';
import type { OptimizeSmartGridHedgeResponse } from '../../api/client';
import type { AllForms } from './formDefaults';
import type { ArbLevel, HFLevel, StrategyType } from './types';
import { gridHedgeRecommendation } from './buildConfig';

const inputCls =
  'w-full bg-bg-tertiary border border-border rounded-lg px-3 py-2 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors';
const labelCls = 'block text-xs font-medium text-text-secondary mb-1';
const TIMEFRAME_OPTIONS = ['1m', '5m', '15m', '30m', '1h', '4h', '1d'];

interface Props {
  strategyType: StrategyType;
  symbol: string;
  accountId: string;
  forms: AllForms;
  setForms: React.Dispatch<React.SetStateAction<AllForms>>;
}

type ObjKey = 'mg' | 'hf' | 'sd' | 'fc' | 'gf' | 'gh' | 'sgh' | 'arb';

/** Renders the strategy-specific config fields for the tester. Field lists,
 * defaults and labels are copied from the real bot-creation form so the
 * produced ConfigJson matches what a live bot stores — see
 * frontend/src/pages/ActiveBotsPage.tsx `AddStrategyModal` (~L4407-L6220). */
export default function StrategyConfigForm({ strategyType, symbol, accountId, forms, setForms }: Props) {
  const patch = <K extends ObjKey>(key: K, p: Partial<AllForms[K]>) =>
    setForms((f) => ({ ...f, [key]: { ...f[key], ...p } }));

  const [sghOptResult, setSghOptResult] = useState<OptimizeSmartGridHedgeResponse | null>(null);
  const [sghOptLoading, setSghOptLoading] = useState(false);
  const [sghOptError, setSghOptError] = useState('');
  const [sghAdvanced, setSghAdvanced] = useState(false);

  const handleSghOptimize = async () => {
    if (!symbol || !accountId) {
      setSghOptError('Выберите аккаунт и символ перед расчётом Q_hedge');
      return;
    }
    setSghOptLoading(true);
    setSghOptError('');
    setSghOptResult(null);
    try {
      const tickerRes = await api.get(`/exchange/${accountId}/ticker`, {
        params: { symbol: symbol.trim().toUpperCase() },
      });
      const p0: number = tickerRes.data.price;
      const result = await optimizeSmartGridHedge({
        p0,
        step: Number(forms.sgh.stepPct) / 100,
        nUp: Number(forms.sgh.nUp),
        nDown: Number(forms.sgh.nDown),
        lotUsd: Number(forms.sgh.lotUsd),
        skimMode: forms.sgh.skimMode,
        makerFeeBps: Number(forms.sgh.makerFeeBps),
        takerFeeBps: Number(forms.sgh.takerFeeBps),
      });
      setSghOptResult(result);
      patch('sgh', { qHedgeOverride: parseFloat(result.qHedgeCoins.toFixed(6)).toString() });
    } catch (err) {
      const e = err as { response?: { data?: { message?: string } } };
      setSghOptError(e.response?.data?.message ?? 'Не удалось рассчитать Q_hedge');
    } finally {
      setSghOptLoading(false);
    }
  };

  const addHfLevel = () =>
    setForms((f) => ({ ...f, hfLevels: [...f.hfLevels, { offsetPercent: 1.5, sizeUsdt: 50 }] }));
  const removeHfLevel = (i: number) =>
    setForms((f) => ({ ...f, hfLevels: f.hfLevels.filter((_, idx) => idx !== i) }));
  const updateHfLevel = (i: number, field: keyof HFLevel, value: number) =>
    setForms((f) => ({ ...f, hfLevels: f.hfLevels.map((l, idx) => (idx === i ? { ...l, [field]: value } : l)) }));

  const addArbLevel = () =>
    setForms((f) => ({
      ...f,
      arbLevels: [...f.arbLevels, { entrySpreadPercent: '1', exitSpreadPercent: '0', notionalUsdt: '100' }],
    }));
  const removeArbLevel = (i: number) =>
    setForms((f) => ({ ...f, arbLevels: f.arbLevels.filter((_, idx) => idx !== i) }));
  const updateArbLevel = (i: number, field: keyof ArbLevel, value: string) =>
    setForms((f) => ({ ...f, arbLevels: f.arbLevels.map((l, idx) => (idx === i ? { ...l, [field]: value } : l)) }));

  if (strategyType === 'SmaDca') {
    const sd = forms.sd;
    return (
      <>
        <div>
          <label className={labelCls}>Таймфрейм</label>
          <select value={sd.timeframe} onChange={(e) => patch('sd', { timeframe: e.target.value })} className={inputCls}>
            {TIMEFRAME_OPTIONS.map((tf) => (
              <option key={tf} value={tf}>{tf === '1d' ? '1D' : tf}</option>
            ))}
          </select>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Направление</label>
            <select value={sd.direction} onChange={(e) => patch('sd', { direction: e.target.value })} className={inputCls}>
              <option value="Long">Long</option>
              <option value="Short">Short</option>
            </select>
          </div>
          <div>
            <label className={labelCls}>Период SMA</label>
            <input type="number" min="2" value={sd.smaPeriod} onChange={(e) => patch('sd', { smaPeriod: e.target.value })} className={inputCls} />
          </div>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Размер входа (USD)</label>
            <input type="number" step="1" min="1" value={sd.positionSizeUsd} onChange={(e) => patch('sd', { positionSizeUsd: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>Take Profit %</label>
            <input type="number" step="0.1" min="0.01" value={sd.takeProfitPercent} onChange={(e) => patch('sd', { takeProfitPercent: e.target.value })} className={inputCls} />
          </div>
        </div>

        <div>
          <label className={labelCls}>Уровни DCA</label>
          <div className="space-y-2">
            {forms.sdLevels.map((lvl, i) => (
              <div key={i} className="grid grid-cols-[1fr_1fr_1fr_auto] gap-2 items-end">
                <div>
                  <label className={labelCls}>DCA шаг, %</label>
                  <input type="number" step="0.1" min="0.01" value={lvl.stepPercent}
                    onChange={(e) => setForms((f) => ({ ...f, sdLevels: f.sdLevels.map((l, idx) => idx === i ? { ...l, stepPercent: e.target.value } : l) }))}
                    className={inputCls} />
                </div>
                <div>
                  <label className={labelCls}>Множитель</label>
                  <input type="number" step="0.1" min="0.1" value={lvl.multiplier}
                    onChange={(e) => setForms((f) => ({ ...f, sdLevels: f.sdLevels.map((l, idx) => idx === i ? { ...l, multiplier: e.target.value } : l) }))}
                    className={inputCls} />
                </div>
                <div>
                  <label className={labelCls}>Доборов</label>
                  <input type="number" min="1" value={lvl.count}
                    onChange={(e) => setForms((f) => ({ ...f, sdLevels: f.sdLevels.map((l, idx) => idx === i ? { ...l, count: e.target.value } : l) }))}
                    className={inputCls} />
                </div>
                <button type="button" disabled={forms.sdLevels.length <= 1}
                  onClick={() => setForms((f) => ({ ...f, sdLevels: f.sdLevels.filter((_, idx) => idx !== i) }))}
                  className="pb-0.5 text-accent-red/70 hover:text-accent-red disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
                  title="Удалить уровень">
                  <XIcon />
                </button>
              </div>
            ))}
          </div>
          <button type="button"
            onClick={() => setForms((f) => ({ ...f, sdLevels: [...f.sdLevels, { stepPercent: '5.0', multiplier: '2.0', count: '2' }] }))}
            className="mt-2 text-xs text-accent-blue hover:text-accent-blue/80 transition-colors">
            + Добавить уровень
          </button>
        </div>

        <div>
          <label className="flex items-center gap-2 cursor-pointer">
            <input type="checkbox" checked={sd.takeProfitTierShiftEnabled}
              onChange={(e) => patch('sd', { takeProfitTierShiftEnabled: e.target.checked })}
              className="rounded border-border" />
            <span className="text-sm text-text-primary">Двигать TP вместе с ярусами</span>
          </label>
          {sd.takeProfitTierShiftEnabled && (
            <div className="mt-2 space-y-2">
              {forms.sdTpShifts.map((s, i) => (
                <div key={i} className="grid grid-cols-[1fr_1fr_auto] gap-2 items-end">
                  <div>
                    <label className={labelCls}>С яруса ≥</label>
                    <input type="number" min="1" value={s.fromTier}
                      onChange={(e) => setForms((f) => ({ ...f, sdTpShifts: f.sdTpShifts.map((x, idx) => idx === i ? { ...x, fromTier: e.target.value } : x) }))}
                      className={inputCls} />
                  </div>
                  <div>
                    <label className={labelCls}>Take Profit %</label>
                    <input type="number" step="0.01" min="0.01" value={s.takeProfitPercent}
                      onChange={(e) => setForms((f) => ({ ...f, sdTpShifts: f.sdTpShifts.map((x, idx) => idx === i ? { ...x, takeProfitPercent: e.target.value } : x) }))}
                      className={inputCls} />
                  </div>
                  <button type="button" disabled={forms.sdTpShifts.length <= 1}
                    onClick={() => setForms((f) => ({ ...f, sdTpShifts: f.sdTpShifts.filter((_, idx) => idx !== i) }))}
                    className="pb-0.5 text-accent-red/70 hover:text-accent-red disabled:opacity-30 disabled:cursor-not-allowed transition-colors">
                    <XIcon />
                  </button>
                </div>
              ))}
              <button type="button"
                onClick={() => setForms((f) => ({ ...f, sdTpShifts: [...f.sdTpShifts, { fromTier: '5', takeProfitPercent: '0.3' }] }))}
                className="text-xs text-accent-blue hover:text-accent-blue/80 transition-colors">
                + Добавить ступень
              </button>
            </div>
          )}
        </div>

        <div>
          <label className={labelCls}>База расчёта шага DCA</label>
          <select value={sd.dcaTriggerBase} onChange={(e) => patch('sd', { dcaTriggerBase: e.target.value })} className={inputCls}>
            <option value="Average">От средней цены входа (сетка сжимается)</option>
            <option value="LastFill">От последней докупки (сетка равномерная)</option>
          </select>
        </div>

        <div>
          <label className={labelCls}>Тип ордеров для DCA (докупок)</label>
          <select value={sd.orderType} onChange={(e) => patch('sd', { orderType: e.target.value })} className={inputCls}>
            <option value="Market">Market — рыночные (taker, гарантированный фил)</option>
            <option value="Limit">Limit — лимитные maker (экономия комиссии)</option>
          </select>
        </div>

        {sd.orderType === 'Limit' && (
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className={labelCls}>Оффсет лимитки, %</label>
              <input type="number" step="0.01" min="0.01" value={sd.entryLimitOffsetPercent} onChange={(e) => patch('sd', { entryLimitOffsetPercent: e.target.value })} className={inputCls} />
            </div>
            <div>
              <label className={labelCls}>Таймаут entry, свечей</label>
              <input type="number" min="1" value={sd.entryLimitTimeoutBars} onChange={(e) => patch('sd', { entryLimitTimeoutBars: e.target.value })} className={inputCls} />
            </div>
          </div>
        )}
      </>
    );
  }

  if (strategyType === 'FundingClaim') {
    const fc = forms.fc;
    return (
      <>
        <div className="rounded-lg border border-border/60 bg-bg-tertiary/40 px-3 py-2 text-xs text-text-secondary">
          Автообновление тикера недоступно — в симуляции символ фиксирован.
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Проверка за (мин)</label>
            <input type="number" min="1" value={fc.checkBeforeFundingMinutes} onChange={(e) => patch('fc', { checkBeforeFundingMinutes: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>Макс. циклов <span className="font-normal">(0 = ∞)</span></label>
            <input type="number" min="0" value={fc.maxCycles} onChange={(e) => patch('fc', { maxCycles: e.target.value })} className={inputCls} />
          </div>
        </div>

        <div className="border-t border-border pt-3">
          <p className="text-xs font-semibold text-text-secondary uppercase tracking-widest mb-3">
            Workspace-окно фандинга
          </p>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Размер позиции, USDT</label>
            <input type="number" min="1" value={fc.fcSizeUsdt} onChange={(e) => patch('fc', { fcSizeUsdt: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>Плечо</label>
            <input type="number" min="1" value={fc.fcLeverage} onChange={(e) => patch('fc', { fcLeverage: e.target.value })} className={inputCls} />
          </div>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Мин. фандинг, %</label>
            <input type="number" step="0.01" min="0" value={fc.fcMinFundingRatePercent} onChange={(e) => patch('fc', { fcMinFundingRatePercent: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>Макс. фандинг, %</label>
            <input type="number" step="0.01" min="0" value={fc.fcMaxFundingRatePercent} onChange={(e) => patch('fc', { fcMaxFundingRatePercent: e.target.value })} className={inputCls} />
          </div>
        </div>
        <div className="grid grid-cols-3 gap-3">
          <div>
            <label className={labelCls}>Stop Loss, %</label>
            <input type="number" step="0.1" min="0" value={fc.fcStopLossPercent} onChange={(e) => patch('fc', { fcStopLossPercent: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>SL grace, мин</label>
            <input type="number" min="0" value={fc.fcSlGraceMinutes} onChange={(e) => patch('fc', { fcSlGraceMinutes: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>SL cooldown, ч</label>
            <input type="number" min="0" value={fc.fcSlCooldownHours} onChange={(e) => patch('fc', { fcSlCooldownHours: e.target.value })} className={inputCls} />
          </div>
        </div>

        <p className="text-xs text-text-secondary italic">
          Бот автоматически определяет направление по знаку фандинга: шорт при положительном, лонг при отрицательном.
          Позиция открывается маркет-ордером и держится для сбора фандинговых выплат.
        </p>
      </>
    );
  }

  if (strategyType === 'GridFloat') {
    const gf = forms.gf;
    return (
      <>
        <div>
          <label className={labelCls}>Таймфрейм</label>
          <select value={gf.timeframe} onChange={(e) => patch('gf', { timeframe: e.target.value })} className={inputCls}>
            {TIMEFRAME_OPTIONS.map((tf) => (
              <option key={tf} value={tf}>{tf === '1d' ? '1D' : tf}</option>
            ))}
          </select>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Направление</label>
            <select value={gf.direction} onChange={(e) => patch('gf', { direction: e.target.value })} className={inputCls}>
              <option value="Long">Long</option>
              <option value="Short">Short</option>
            </select>
          </div>
          <div>
            <label className={labelCls}>Плечо</label>
            <input type="number" min="1" step="1" value={gf.leverage} onChange={(e) => patch('gf', { leverage: e.target.value })} className={inputCls} />
          </div>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Шаг TP, % (по умолчанию)</label>
            <input type="number" step="0.1" min="0.01" value={gf.tpStepPercent} onChange={(e) => patch('gf', { tpStepPercent: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>Шаг DCA, % (по умолчанию)</label>
            <input type="number" step="0.1" min="0.01" value={gf.dcaStepPercent} onChange={(e) => patch('gf', { dcaStepPercent: e.target.value })} className={inputCls} />
          </div>
        </div>

        <div>
          <label className={labelCls}>Ярусы сетки</label>
          <div className="rounded-lg border border-border overflow-hidden">
            <table className="w-full text-xs">
              <thead>
                <tr className="bg-bg-tertiary text-text-secondary">
                  <th className="px-2 py-2 text-left font-medium w-8">#</th>
                  <th className="px-2 py-2 text-left font-medium w-10">От %</th>
                  <th className="px-2 py-2 text-left font-medium">До %</th>
                  <th className="px-2 py-2 text-left font-medium">$</th>
                  <th className="px-2 py-2 text-left font-medium" title="Шаг DCA в этом ярусе (пусто = глобальный)">DCA %</th>
                  <th className="px-2 py-2 text-left font-medium" title="Шаг TP в этом ярусе (пусто = глобальный)">TP %</th>
                  <th className="px-2 py-2 w-6" />
                </tr>
              </thead>
              <tbody>
                {forms.gfTiers.map((t, i) => {
                  const prevUp = i === 0 ? '0' : forms.gfTiers[i - 1].upTo || '?';
                  return (
                    <tr key={i} className="border-t border-border/50">
                      <td className="px-2 py-1.5 text-text-secondary">T{i + 1}</td>
                      <td className="px-2 py-1.5 text-text-secondary">{prevUp}</td>
                      <td className="px-2 py-1.5">
                        <input type="number" step="0.5" min="0.1" value={t.upTo}
                          onChange={(e) => setForms((f) => ({ ...f, gfTiers: f.gfTiers.map((row, idx) => idx === i ? { ...row, upTo: e.target.value } : row) }))}
                          className="w-16 bg-bg-tertiary border border-border rounded px-1.5 py-1 text-text-primary" />
                      </td>
                      <td className="px-2 py-1.5">
                        <input type="number" step="1" min="1" value={t.size}
                          onChange={(e) => setForms((f) => ({ ...f, gfTiers: f.gfTiers.map((row, idx) => idx === i ? { ...row, size: e.target.value } : row) }))}
                          className="w-16 bg-bg-tertiary border border-border rounded px-1.5 py-1 text-text-primary" />
                      </td>
                      <td className="px-2 py-1.5">
                        <input type="number" step="0.1" min="0.01" value={t.dca} placeholder={gf.dcaStepPercent}
                          onChange={(e) => setForms((f) => ({ ...f, gfTiers: f.gfTiers.map((row, idx) => idx === i ? { ...row, dca: e.target.value } : row) }))}
                          className="w-14 bg-bg-tertiary border border-border rounded px-1.5 py-1 text-text-primary placeholder:text-text-secondary/40" />
                      </td>
                      <td className="px-2 py-1.5">
                        <input type="number" step="0.1" min="0.01" value={t.tp} placeholder={gf.tpStepPercent}
                          onChange={(e) => setForms((f) => ({ ...f, gfTiers: f.gfTiers.map((row, idx) => idx === i ? { ...row, tp: e.target.value } : row) }))}
                          className="w-14 bg-bg-tertiary border border-border rounded px-1.5 py-1 text-text-primary placeholder:text-text-secondary/40" />
                      </td>
                      <td className="px-2 py-1.5 text-right">
                        {forms.gfTiers.length > 1 && (
                          <button type="button"
                            onClick={() => setForms((f) => ({ ...f, gfTiers: f.gfTiers.filter((_, idx) => idx !== i) }))}
                            className="px-1.5 py-0.5 text-accent-red hover:bg-accent-red/10 rounded" title="Удалить ярус">
                            ×
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <button type="button"
            onClick={() => setForms((f) => {
              const last = f.gfTiers[f.gfTiers.length - 1];
              const nextUp = last && Number(last.upTo) > 0 ? Number(last.upTo) * 2 : 10;
              const nextSize = last && Number(last.size) > 0 ? Number(last.size) * 2 : 100;
              return { ...f, gfTiers: [...f.gfTiers, { upTo: String(nextUp), size: String(nextSize), dca: '', tp: '' }] };
            })}
            className="mt-2 px-3 py-1.5 text-xs font-medium bg-bg-tertiary text-text-secondary rounded-lg hover:bg-bg-tertiary/70 transition-colors">
            + Добавить ярус
          </button>
        </div>

        <label className="flex items-start gap-2 cursor-pointer select-none">
          <input type="checkbox" checked={gf.useStaticRange} onChange={(e) => patch('gf', { useStaticRange: e.target.checked })}
            className="w-4 h-4 mt-0.5 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer" />
          <div>
            <span className="text-sm font-medium text-text-primary">Статический диапазон</span>
            <p className="text-xs text-text-secondary mt-0.5">
              Граница фиксируется на первом якоре. По умолчанию (выключено) — динамический диапазон.
            </p>
          </div>
        </label>

        <div>
          <label className="flex items-center gap-2 cursor-pointer select-none">
            <input type="checkbox" checked={gf.takeProfitEnabled} onChange={(e) => patch('gf', { takeProfitEnabled: e.target.checked })}
              className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer" />
            <span className="text-sm text-text-primary">Зафиксировать при достижении прибыли</span>
          </label>
          {gf.takeProfitEnabled && (
            <div className="mt-2 flex items-center gap-2">
              <input type="number" step="1" min="0" value={gf.takeProfitTargetUsd} onChange={(e) => patch('gf', { takeProfitTargetUsd: e.target.value })} className={inputCls + ' max-w-[140px]'} />
              <span className="text-xs text-text-secondary">USDT (realized + unrealized, останавливает бота)</span>
            </div>
          )}
        </div>
      </>
    );
  }

  if (strategyType === 'GridHedge') {
    const gh = forms.gh;
    const rec = gridHedgeRecommendation(
      Number(gh.rangePercent), Number(gh.upperExitPercent), Number(gh.dcaStepPercent), Number(gh.tpStepPercent), Number(gh.betUsdt),
    );
    return (
      <>
        <div>
          <label className={labelCls}>Режим</label>
          <select value={gh.mode} onChange={(e) => patch('gh', { mode: Number(e.target.value) as 1 | 2 })} className={inputCls}>
            <option value={1}>SameTicker — Spot grid + same-symbol futures short (Bybit-only)</option>
            <option value={2}>CrossTicker — Futures grid + correlated-ticker futures short</option>
          </select>
        </div>

        <div>
          <label className={labelCls}>Маржинальный режим (Position Mode)</label>
          <select value={gh.positionMode} onChange={(e) => patch('gh', { positionMode: Number(e.target.value) as 1 | 2 })} className={inputCls}>
            <option value={1}>OneWay — спот grid + фьючерс short (разные продукты)</option>
            <option value={2}>Hedge — обе ноги на одном фьючерсном аккаунте (только Bybit)</option>
          </select>
        </div>

        {gh.mode === 2 && (
          <div>
            <label className={labelCls}>Второй тикер (secondSymbol) *</label>
            <input type="text" value={gh.secondSymbol} onChange={(e) => patch('gh', { secondSymbol: e.target.value })} placeholder="BTCUSDT" className={inputCls} />
            <p className="text-xs text-text-secondary mt-0.5">
              Коррелированный тикер для шорт-хеджа (например BTCUSDT при гриде на ETHUSDT).
            </p>
          </div>
        )}

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Диапазон вниз %</label>
            <input type="number" step="0.5" min="0.1" value={gh.rangePercent} onChange={(e) => patch('gh', { rangePercent: e.target.value })} className={inputCls} />
            <p className="text-xs text-text-secondary mt-0.5">Стоп-лосс при цене ниже anchor × (1 − R%)</p>
          </div>
          <div>
            <label className={labelCls}>Диапазон вверх %</label>
            <input type="number" step="0.5" min="0.1" value={gh.upperExitPercent} onChange={(e) => patch('gh', { upperExitPercent: e.target.value })} className={inputCls} />
            <p className="text-xs text-text-secondary mt-0.5">TP всего бота при цене выше anchor × (1 + U%)</p>
          </div>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Шаг DCA %</label>
            <input type="number" step="0.1" min="0.01" value={gh.dcaStepPercent} onChange={(e) => patch('gh', { dcaStepPercent: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>Шаг TP %</label>
            <input type="number" step="0.1" min="0.01" value={gh.tpStepPercent} onChange={(e) => patch('gh', { tpStepPercent: e.target.value })} className={inputCls} />
          </div>
        </div>

        <div>
          <label className={labelCls}>Ставка на уровень, USDT</label>
          <input type="number" step="1" min="1" value={gh.betUsdt} onChange={(e) => patch('gh', { betUsdt: e.target.value })} className={inputCls} />
        </div>

        {rec && (
          <div className="rounded-lg border border-accent-blue/30 bg-accent-blue/5 p-3 text-xs space-y-1">
            <div className="font-medium text-accent-blue">Рекомендуемый хедж: ~${Math.round(rec.hedge)}</div>
            <div className="text-text-secondary">
              Одинаковый убыток на резком +{gh.upperExitPercent}% и резком −{gh.rangePercent}%: ~${Math.round(Math.abs(rec.equalLoss))}
            </div>
          </div>
        )}

        <div>
          <label className={labelCls}>Размер хеджа, USDT</label>
          <div className="flex gap-2">
            <input type="number" step="1" min="0" value={gh.hedgeNotionalUsdt} onChange={(e) => patch('gh', { hedgeNotionalUsdt: e.target.value })} className={inputCls + ' flex-1'} />
            {rec && (
              <button type="button" onClick={() => patch('gh', { hedgeNotionalUsdt: String(Math.round(rec.hedge)) })}
                className="px-3 py-2 text-xs font-medium bg-accent-blue/15 text-accent-blue rounded-lg hover:bg-accent-blue/25 transition-colors whitespace-nowrap">
                Применить рекомендацию
              </button>
            )}
          </div>
          <p className="text-xs text-text-secondary mt-0.5">0 = без хеджа.</p>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Плечо хеджа</label>
            <input type="number" step="1" min="1" value={gh.hedgeLeverage} onChange={(e) => patch('gh', { hedgeLeverage: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>Плечо грида (CrossTicker)</label>
            <input type="number" step="1" min="1" value={gh.gridLeverage} disabled={gh.mode === 1}
              onChange={(e) => patch('gh', { gridLeverage: e.target.value })}
              className={inputCls + (gh.mode === 1 ? ' opacity-40' : '')} />
          </div>
        </div>
      </>
    );
  }

  if (strategyType === 'SmartGridHedge') {
    const sgh = forms.sgh;
    return (
      <>
        <div>
          <label className={labelCls}>Лот USDT</label>
          <input type="number" step="1" min="1" value={sgh.lotUsd} onChange={(e) => patch('sgh', { lotUsd: e.target.value })} className={inputCls} />
        </div>

        <div>
          <label className={labelCls}>Шаг сетки, %</label>
          <input type="number" step="0.01" min="0.05" max="10" value={sgh.stepPct} onChange={(e) => patch('sgh', { stepPct: e.target.value })} className={inputCls} />
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>NDown (нижних уровней)</label>
            <input type="number" step="1" min="1" max="50" value={sgh.nDown} onChange={(e) => patch('sgh', { nDown: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>NUp (верхних уровней)</label>
            <input type="number" step="1" min="1" max="50" value={sgh.nUp} onChange={(e) => patch('sgh', { nUp: e.target.value })} className={inputCls} />
          </div>
        </div>

        <div>
          <label className={labelCls}>SkimMode (поведение верхних ячеек)</label>
          <div className="space-y-1.5 mt-1">
            {([
              [0, 'OneShot', 'Однократно обрезает лонг при достижении U_k. Нет повторного цикла.'],
              [1, 'ExcessRecycle (A)', 'Парный шорт только на «избыток» лота.'],
              [2, 'FullRecycle (B)', 'Парный шорт на полный лот.'],
            ] as const).map(([val, label, hint]) => (
              <label key={val} className="flex items-start gap-2 cursor-pointer">
                <input type="radio" name="sghSkimMode" value={val} checked={sgh.skimMode === val}
                  onChange={() => patch('sgh', { skimMode: val })}
                  className="mt-0.5 w-3.5 h-3.5 border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer" />
                <span className="text-sm">
                  <span className="font-medium text-text-primary">{label}</span>
                  <span className="text-xs text-text-secondary block">{hint}</span>
                </span>
              </label>
            ))}
          </div>
        </div>

        <div>
          <label className={labelCls}>Плечо</label>
          <input type="number" step="1" min="1" max="50" value={sgh.leverage} onChange={(e) => patch('sgh', { leverage: e.target.value })} className={inputCls} />
        </div>

        <label className="flex items-center gap-2 cursor-pointer select-none">
          <input type="checkbox" checked={sgh.autoRestart} onChange={(e) => patch('sgh', { autoRestart: e.target.checked })}
            className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer" />
          <span className="text-sm text-text-primary">Auto-restart после закрытия цикла</span>
        </label>

        <div>
          <label className="flex items-center gap-2 cursor-pointer select-none">
            <input type="checkbox" checked={sgh.takeProfitEnabled} onChange={(e) => patch('sgh', { takeProfitEnabled: e.target.checked })}
              className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer" />
            <span className="text-sm text-text-primary">Зафиксировать при достижении прибыли</span>
          </label>
          {sgh.takeProfitEnabled && (
            <div className="mt-2 flex items-center gap-2">
              <input type="number" step="1" min="0" value={sgh.takeProfitTargetUsd} onChange={(e) => patch('sgh', { takeProfitTargetUsd: e.target.value })} className={inputCls + ' max-w-[140px]'} />
              <span className="text-xs text-text-secondary">USDT</span>
            </div>
          )}
        </div>

        <div>
          <label className={labelCls}>Q_hedge (монет) — пустое = авто</label>
          <div className="flex gap-2 items-stretch">
            <input type="number" step="0.000001" min="0" value={sgh.qHedgeOverride}
              onChange={(e) => { patch('sgh', { qHedgeOverride: e.target.value }); setSghOptResult(null); }}
              placeholder="авто" className={inputCls + ' flex-1'} />
            <button type="button" onClick={handleSghOptimize} disabled={sghOptLoading || !symbol || !accountId}
              className="px-3 py-2 text-xs font-medium bg-accent-blue/15 text-accent-blue rounded-lg hover:bg-accent-blue/25 transition-colors disabled:opacity-40 whitespace-nowrap">
              {sghOptLoading ? 'Расчёт...' : 'Рассчитать Q_hedge*'}
            </button>
          </div>
          {sghOptError && <p className="text-xs text-accent-red mt-1">{sghOptError}</p>}
          {sghOptResult && (
            <div className="mt-1.5 flex items-center gap-2 flex-wrap">
              <span className="px-2 py-0.5 rounded-full text-[11px] font-medium bg-accent-green/10 text-accent-green">
                Worst-case loss: ${sghOptResult.worstCaseLoss.toFixed(2)}
              </span>
            </div>
          )}
        </div>

        <div>
          <button type="button" onClick={() => setSghAdvanced((v) => !v)}
            className="text-xs text-text-secondary hover:text-text-primary transition-colors">
            {sghAdvanced ? '▾' : '▸'} Дополнительно (тарифы комиссий)
          </button>
          {sghAdvanced && (
            <div className="mt-2 grid grid-cols-2 gap-3">
              <div>
                <label className={labelCls}>Maker fee, bps</label>
                <input type="number" step="0.1" min="0" value={sgh.makerFeeBps} onChange={(e) => patch('sgh', { makerFeeBps: e.target.value })} className={inputCls} />
              </div>
              <div>
                <label className={labelCls}>Taker fee, bps</label>
                <input type="number" step="0.1" min="0" value={sgh.takerFeeBps} onChange={(e) => patch('sgh', { takerFeeBps: e.target.value })} className={inputCls} />
              </div>
            </div>
          )}
        </div>
      </>
    );
  }

  if (strategyType === 'HuntingFunding') {
    const hf = forms.hf;
    return (
      <>
        <div className="rounded-lg border border-border/60 bg-bg-tertiary/40 px-3 py-2 text-xs text-text-secondary">
          Автообновление тикера недоступно — в симуляции символ фиксирован.
        </div>

        <div>
          <label className={labelCls}>Уровни ордеров</label>
          <div className="rounded-lg border border-border overflow-hidden">
            <table className="w-full text-sm">
              <thead>
                <tr className="bg-bg-tertiary text-text-secondary text-xs">
                  <th className="px-3 py-2 text-left font-medium">Offset %</th>
                  <th className="px-3 py-2 text-left font-medium">Size USDT</th>
                  <th className="px-3 py-2 w-8" />
                </tr>
              </thead>
              <tbody>
                {forms.hfLevels.map((lvl, i) => (
                  <tr key={i} className="border-t border-border/50">
                    <td className="px-3 py-1.5">
                      <input type="number" step="0.1" value={lvl.offsetPercent}
                        onChange={(e) => updateHfLevel(i, 'offsetPercent', Number(e.target.value))}
                        className="w-full bg-transparent text-text-primary focus:outline-none focus:text-accent-blue" />
                    </td>
                    <td className="px-3 py-1.5">
                      <input type="number" step="1" value={lvl.sizeUsdt}
                        onChange={(e) => updateHfLevel(i, 'sizeUsdt', Number(e.target.value))}
                        className="w-full bg-transparent text-text-primary focus:outline-none focus:text-accent-blue" />
                    </td>
                    <td className="px-2 py-1.5 text-center">
                      <button onClick={() => removeHfLevel(i)} className="text-text-secondary/40 hover:text-accent-red transition-colors" title="Удалить уровень">
                        <XIcon />
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <button onClick={addHfLevel} className="mt-2 text-xs text-accent-blue hover:text-accent-blue/80 transition-colors">
            + Добавить уровень
          </button>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Take Profit %</label>
            <input type="number" step="0.1" value={hf.takeProfitPercent} onChange={(e) => patch('hf', { takeProfitPercent: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>Stop Loss %</label>
            <input type="number" step="0.1" value={hf.stopLossPercent} onChange={(e) => patch('hf', { stopLossPercent: e.target.value })} className={inputCls} />
          </div>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Секунд до фандинга</label>
            <input type="number" value={hf.secondsBeforeFunding} onChange={(e) => patch('hf', { secondsBeforeFunding: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>Закрыть через (мин)</label>
            <input type="number" value={hf.closeAfterMinutes} onChange={(e) => patch('hf', { closeAfterMinutes: e.target.value })} className={inputCls} />
          </div>
        </div>

        <div>
          <label className={labelCls}>Макс. циклов <span className="font-normal">(0 = бесконечно)</span></label>
          <input type="number" min="0" value={hf.maxCycles} onChange={(e) => patch('hf', { maxCycles: e.target.value })} className={inputCls} />
        </div>

        <div className="border border-border rounded-lg p-3 space-y-3">
          <p className="text-xs text-text-secondary font-medium uppercase tracking-wide">Направления торговли</p>
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="flex items-center gap-2 mb-1">
                <input type="checkbox" checked={hf.enableLong} onChange={(e) => patch('hf', { enableLong: e.target.checked })} className="rounded border-border" />
                <span className="text-sm text-text-primary font-medium">Long</span>
                <span className="text-xs text-text-secondary">(фандинг &lt; 0)</span>
              </label>
              <input type="number" step="0.1" min="0" placeholder="Мин. фандинг %" value={hf.minFundingLong}
                onChange={(e) => patch('hf', { minFundingLong: e.target.value })} disabled={!hf.enableLong}
                className={inputCls + (!hf.enableLong ? ' opacity-50' : '')} />
            </div>
            <div>
              <label className="flex items-center gap-2 mb-1">
                <input type="checkbox" checked={hf.enableShort} onChange={(e) => patch('hf', { enableShort: e.target.checked })} className="rounded border-border" />
                <span className="text-sm text-text-primary font-medium">Short</span>
                <span className="text-xs text-text-secondary">(фандинг &gt; 0)</span>
              </label>
              <input type="number" step="0.1" min="0" placeholder="Мин. фандинг %" value={hf.minFundingShort}
                onChange={(e) => patch('hf', { minFundingShort: e.target.value })} disabled={!hf.enableShort}
                className={inputCls + (!hf.enableShort ? ' opacity-50' : '')} />
            </div>
          </div>
        </div>

        <div className="border-t border-border pt-3">
          <p className="text-xs font-semibold text-text-secondary uppercase tracking-widest mb-3">
            Workspace-окно фандинга
          </p>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Мин. фандинг окна, %</label>
            <input type="number" step="0.1" min="0" value={hf.fundingRateMin} onChange={(e) => patch('hf', { fundingRateMin: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>Макс. фандинг окна, %</label>
            <input type="number" step="0.1" min="0" value={hf.fundingRateMax} onChange={(e) => patch('hf', { fundingRateMax: e.target.value })} className={inputCls} />
          </div>
        </div>
      </>
    );
  }

  if (strategyType === 'FuturesArbitrage') {
    const arb = forms.arb;
    return (
      <>
        <div className="rounded-lg border border-border/60 bg-bg-tertiary/40 px-3 py-2 text-xs text-text-secondary">
          Символ и аккаунт второй биржи выбираются в блоке над стратегией (Account 2 / Symbol B).
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Плечо</label>
            <input type="number" min="1" step="1" value={arb.leverage} onChange={(e) => patch('arb', { leverage: e.target.value })} className={inputCls} />
          </div>
          <div>
            <label className={labelCls}>Макс. подряд ошибок</label>
            <input type="number" min="1" step="1" value={arb.maxConsecutiveFailures} onChange={(e) => patch('arb', { maxConsecutiveFailures: e.target.value })} className={inputCls} />
          </div>
        </div>

        <label className="flex items-center gap-2 cursor-pointer select-none">
          <input type="checkbox" checked={arb.allowBothDirections} onChange={(e) => patch('arb', { allowBothDirections: e.target.checked })}
            className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer" />
          <span className="text-sm text-text-primary">Разрешить обе стороны (шорт любой из бирж — не только фиксированное направление)</span>
        </label>

        <div>
          <label className={labelCls}>Уровни арбитража</label>
          <div className="rounded-lg border border-border overflow-hidden">
            <table className="w-full text-sm">
              <thead>
                <tr className="bg-bg-tertiary text-text-secondary text-xs">
                  <th className="px-3 py-2 text-left font-medium">Вход %</th>
                  <th className="px-3 py-2 text-left font-medium">Выход %</th>
                  <th className="px-3 py-2 text-left font-medium">Объём USDT</th>
                  <th className="px-3 py-2 w-8" />
                </tr>
              </thead>
              <tbody>
                {forms.arbLevels.map((lvl, i) => (
                  <tr key={i} className="border-t border-border/50">
                    <td className="px-3 py-1.5">
                      <input type="number" step="0.01" min="0.01" value={lvl.entrySpreadPercent}
                        onChange={(e) => updateArbLevel(i, 'entrySpreadPercent', e.target.value)}
                        className="w-full bg-transparent text-text-primary focus:outline-none focus:text-accent-blue" />
                    </td>
                    <td className="px-3 py-1.5">
                      <input type="number" step="0.01" min="0" value={lvl.exitSpreadPercent}
                        onChange={(e) => updateArbLevel(i, 'exitSpreadPercent', e.target.value)}
                        className="w-full bg-transparent text-text-primary focus:outline-none focus:text-accent-blue" />
                    </td>
                    <td className="px-3 py-1.5">
                      <input type="number" step="1" min="1" value={lvl.notionalUsdt}
                        onChange={(e) => updateArbLevel(i, 'notionalUsdt', e.target.value)}
                        className="w-full bg-transparent text-text-primary focus:outline-none focus:text-accent-blue" />
                    </td>
                    <td className="px-2 py-1.5 text-center">
                      <button type="button" disabled={forms.arbLevels.length <= 1} onClick={() => removeArbLevel(i)}
                        className="text-text-secondary/40 hover:text-accent-red disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
                        title="Удалить уровень">
                        <XIcon />
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <button type="button" onClick={addArbLevel} className="mt-2 text-xs text-accent-blue hover:text-accent-blue/80 transition-colors">
            + Добавить уровень
          </button>
        </div>

        <p className="text-xs text-text-secondary italic">
          Уровень открывается, когда спред между биржами ≥ «Вход %» — шорт дорогой ноги и лонг дешёвой на «Объём USDT» на каждую ногу;
          закрывается, когда спред ≤ «Выход %».
        </p>
      </>
    );
  }

  // MaratG (default)
  const mg = forms.mg;
  return (
    <>
      <div>
        <label className={labelCls}>Таймфрейм</label>
        <select value={mg.timeframe} onChange={(e) => patch('mg', { timeframe: e.target.value })} className={inputCls}>
          {TIMEFRAME_OPTIONS.map((tf) => (
            <option key={tf} value={tf}>{tf === '1d' ? '1D' : tf}</option>
          ))}
        </select>
      </div>

      <div className="grid grid-cols-2 gap-3">
        <div>
          <label className={labelCls}>Индикатор</label>
          <select value={mg.indicatorType} onChange={(e) => patch('mg', { indicatorType: e.target.value })} className={inputCls}>
            <option value="EMA">EMA</option>
            <option value="SMA">SMA</option>
          </select>
        </div>
        <div>
          <label className={labelCls}>Период</label>
          <input type="number" value={mg.indicatorLength} onChange={(e) => patch('mg', { indicatorLength: e.target.value })} className={inputCls} />
        </div>
      </div>

      <div className="grid grid-cols-2 gap-3">
        <div>
          <label className={labelCls}>Кол-во свечей</label>
          <input type="number" value={mg.candleCount} onChange={(e) => patch('mg', { candleCount: e.target.value })} className={inputCls} />
        </div>
        <div>
          <label className={labelCls}>Offset %</label>
          <input type="number" value={mg.offsetPercent} onChange={(e) => patch('mg', { offsetPercent: e.target.value })} className={inputCls} />
        </div>
      </div>

      <div className="grid grid-cols-2 gap-3">
        <div>
          <label className={labelCls}>Take Profit %</label>
          <input type="number" value={mg.takeProfitPercent} onChange={(e) => patch('mg', { takeProfitPercent: e.target.value })} className={inputCls} />
        </div>
        <div>
          <label className={labelCls}>Stop Loss %</label>
          <input type="number" value={mg.stopLossPercent} onChange={(e) => patch('mg', { stopLossPercent: e.target.value })} className={inputCls} />
        </div>
      </div>

      {/* Workspace-level settings. Live bots take these from the workspace
          (WorkspaceConfig → MergeWorkspaceConfig at start); the sim has no
          workspace, so they are entered here and flattened into configJson. */}
      <div className="border-t border-border pt-3 mt-1">
        <p className="text-xs uppercase tracking-wide text-text-secondary mb-2">Настройки воркспейса (для симуляции)</p>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={labelCls}>Сумма ставки (USDT)</label>
            <input type="number" min="0" step="1" value={mg.orderSize} onChange={(e) => patch('mg', { orderSize: e.target.value })} className={inputCls} />
          </div>
          <div className="flex flex-col justify-end gap-2 pb-1">
            <label className="flex items-center gap-2 cursor-pointer select-none">
              <input type="checkbox" checked={mg.onlyLong}
                onChange={(e) => patch('mg', { onlyLong: e.target.checked, onlyShort: e.target.checked ? false : mg.onlyShort })}
                className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer" />
              <span className="text-sm font-medium text-accent-green">Только Long</span>
            </label>
            <label className="flex items-center gap-2 cursor-pointer select-none">
              <input type="checkbox" checked={mg.onlyShort}
                onChange={(e) => patch('mg', { onlyShort: e.target.checked, onlyLong: e.target.checked ? false : mg.onlyLong })}
                className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer" />
              <span className="text-sm font-medium text-accent-red">Только Short</span>
            </label>
          </div>
        </div>

        <div className="mt-3">
          <label className="flex items-center gap-2 cursor-pointer select-none">
            <input type="checkbox" checked={mg.useMartingale} onChange={(e) => patch('mg', { useMartingale: e.target.checked })}
              className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer" />
            <span className="text-sm text-text-primary">Мартингейл</span>
          </label>
          {mg.useMartingale && (
            <div className="mt-2 grid grid-cols-2 gap-3">
              <div>
                <label className={labelCls}>Коэффициент (x)</label>
                <input type="number" step="0.1" min="0" value={mg.martingaleCoeff} onChange={(e) => patch('mg', { martingaleCoeff: e.target.value })} className={inputCls} />
              </div>
              <div>
                <label className="flex items-center gap-2 cursor-pointer select-none mb-1">
                  <input type="checkbox" checked={mg.useSteppedMartingale} onChange={(e) => patch('mg', { useSteppedMartingale: e.target.checked })}
                    className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer" />
                  <span className="text-xs font-medium text-text-secondary">Ступенчатый — каждые N убытков</span>
                </label>
                <input type="number" step="1" min="1" value={mg.martingaleStep} disabled={!mg.useSteppedMartingale}
                  onChange={(e) => patch('mg', { martingaleStep: e.target.value })} className={inputCls + (mg.useSteppedMartingale ? '' : ' opacity-50')} />
              </div>
            </div>
          )}
        </div>

        {mg.useMartingale && (
          <div className="mt-3">
            <label className="flex items-center gap-2 cursor-pointer select-none">
              <input type="checkbox" checked={mg.useDrawdownScale} onChange={(e) => patch('mg', { useDrawdownScale: e.target.checked })}
                className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer" />
              <span className="text-sm text-text-primary">Масштабировать по просадке</span>
            </label>
            {mg.useDrawdownScale && (
              <div className="mt-2 grid grid-cols-3 gap-3">
                <div>
                  <label className={labelCls}>Баланс $</label>
                  <input type="number" step="1" min="0" value={mg.drawdownBalance} onChange={(e) => patch('mg', { drawdownBalance: e.target.value })} className={inputCls} />
                </div>
                <div>
                  <label className={labelCls}>Просадка %</label>
                  <input type="number" step="0.1" min="0" value={mg.drawdownPercent} onChange={(e) => patch('mg', { drawdownPercent: e.target.value })} className={inputCls} />
                </div>
                <div>
                  <label className={labelCls}>Цель %</label>
                  <input type="number" step="0.1" min="0" value={mg.drawdownTarget} onChange={(e) => patch('mg', { drawdownTarget: e.target.value })} className={inputCls} />
                </div>
              </div>
            )}
          </div>
        )}
      </div>
    </>
  );
}

function XIcon() {
  return (
    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
    </svg>
  );
}
