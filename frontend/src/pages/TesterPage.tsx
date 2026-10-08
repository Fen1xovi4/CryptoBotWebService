import { useEffect, useMemo, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import api from '../api/client';
import Header from '../components/Layout/Header';
import CandlestickChart from '../components/Chart/CandlestickChart';
import type { CandleData, ChartMarker, IndicatorDataPoint } from '../components/Chart/CandlestickChart';
import StrategyConfigForm from './tester/StrategyConfigForm';
import EquityChart from './tester/EquityChart';
import TradesTable from './tester/TradesTable';
import OptimizationPanel from './tester/OptimizationPanel';
import { describeError } from './tester/errors';
import { buildSimConfig } from './tester/buildConfig';
import { makeDefaultForms } from './tester/formDefaults';
import type { AllForms } from './tester/formDefaults';
import { STRATEGY_LABELS, STRATEGY_TYPES } from './tester/types';
import type { Account, KlineCacheEntry, SimulateRequest, SimulationResult, StrategyType } from './tester/types';

const EXCHANGE_NAMES: Record<number, string> = { 1: 'Bybit', 2: 'Bitget', 3: 'BingX', 4: 'Dzengi' };
// SimulationEngine downloads kline history via GetKlinesRangeAsync, which only Bybit/Bitget/BingX
// implement — Dzengi accounts would 400 on /tester/simulate, so they are not offered here.
const SIM_SUPPORTED_EXCHANGES = new Set([1, 2, 3]);
const TIMEFRAMES = ['1m', '5m', '15m', '1h', '4h', '1d'] as const;
const POLL_MS: Record<string, number> = {
  '1m': 5000,
  '5m': 10000,
  '15m': 30000,
  '1h': 60000,
  '4h': 120000,
  '1d': 300000,
};
const DAY_PRESETS = [7, 30, 90, 180, 365, 730, 1095, 1460];
// Timeframe of the downloaded price path — coarser bars make long windows cheap
// (1h is 60× fewer candles than 1m) at the cost of intrabar fill fidelity.
const PATH_TIMEFRAMES = ['1m', '5m', '15m', '30m', '1h', '4h', '1d'];
const PATH_TF_MINUTES: Record<string, number> = {
  '1m': 1, '5m': 5, '15m': 15, '30m': 30, '1h': 60, '4h': 240, '1d': 1440,
};
// Mirrors SimulationEngine.MaxPathCandles — the backend rejects windows above this many bars.
const MAX_PATH_BARS = 530_000;
// Rough history download speed per exchange (candles/sec), from measured paging:
// Bybit/BingX 1000 per page ≈ 2 pages/s; Bitget 200 per page but 8 parallel slice workers (measured: 365d of 1m ≈ 3.7 min).
const DOWNLOAD_CANDLES_PER_SEC: Record<number, number> = { 1: 2000, 2: 2300, 3: 2000 };
const SIMULATE_TIMEOUT_MS = 30 * 60 * 1000; // paginated kline download: Bybit ~4 min per 500k bars, Bitget (200/page) ~20 min; nginx /api/tester/ allows 1800s

export default function TesterPage() {
  const [accountId, setAccountId] = useState('');
  const [symbol, setSymbol] = useState('BTCUSDT');
  const [strategyType, setStrategyType] = useState<StrategyType>('MaratG');
  const [secondAccountId, setSecondAccountId] = useState('');
  const [previewTimeframe, setPreviewTimeframe] = useState('1h');

  const [days, setDays] = useState(30);
  const [fromDate, setFromDate] = useState('');
  const [toDate, setToDate] = useState('');
  const [pathTimeframe, setPathTimeframe] = useState('1m');
  const [makerFeePercent, setMakerFeePercent] = useState('');
  const [takerFeePercent, setTakerFeePercent] = useState('');
  const [bypassCache, setBypassCache] = useState(false);
  const [showCache, setShowCache] = useState(false);
  const queryClient = useQueryClient();

  const [forms, setForms] = useState<AllForms>(() => makeDefaultForms());
  const [formError, setFormError] = useState('');

  const { data: accounts } = useQuery<Account[]>({
    queryKey: ['accounts'],
    queryFn: () => api.get('/accounts').then((r) => r.data),
  });

  const { data: supportedStrategies } = useQuery<string[]>({
    queryKey: ['tester-strategies'],
    queryFn: () => api.get('/tester/strategies').then((r) => r.data),
    staleTime: 5 * 60 * 1000,
  });

  const simAccounts = useMemo(
    () => (accounts ?? []).filter((a) => SIM_SUPPORTED_EXCHANGES.has(a.exchangeType)),
    [accounts],
  );
  const hiddenAccountCount = (accounts?.length ?? 0) - simAccounts.length;

  // The path-timeframe default follows the strategy: indicator strategies model on their own
  // timeframe (downloading finer bars would be wasted), everything else (grids/funding/arbitrage)
  // is tick-driven and defaults to 1m. The user can still override until the strategy TF changes.
  const strategyDefaultPathTf = useMemo(() => {
    if (strategyType === 'MaratG') return forms.mg.timeframe;
    if (strategyType === 'SmaDca') return forms.sd.timeframe;
    if (strategyType === 'GridFloat') return forms.gf.timeframe;
    return '1m';
  }, [strategyType, forms.mg.timeframe, forms.sd.timeframe, forms.gf.timeframe]);
  useEffect(() => {
    setPathTimeframe(PATH_TIMEFRAMES.includes(strategyDefaultPathTf) ? strategyDefaultPathTf : '1m');
  }, [strategyDefaultPathTf]);

  // Window length in days — explicit date range overrides the preset, same as in handleSimulate.
  const windowDays = useMemo(() => {
    if (fromDate && toDate) {
      const ms = new Date(`${toDate}T23:59:59Z`).getTime() - new Date(`${fromDate}T00:00:00Z`).getTime();
      return ms > 0 ? ms / 86_400_000 : null;
    }
    return days;
  }, [days, fromDate, toDate]);

  // Bars per price series at the chosen path timeframe — shown next to the run button and
  // checked against the backend limit.
  const pathBars = useMemo(() => {
    if (windowDays == null) return null;
    return Math.round((windowDays * 1440) / (PATH_TF_MINUTES[pathTimeframe] ?? 1));
  }, [windowDays, pathTimeframe]);

  // Rough wall-clock estimate for the history download so a long run on Bitget doesn't look hung.
  const estimatedMinutes = useMemo(() => {
    const acc = simAccounts.find((a) => a.id === accountId);
    if (!acc || pathBars == null) return null;
    const rate = DOWNLOAD_CANDLES_PER_SEC[acc.exchangeType] ?? 1000;
    // FuturesArbitrage / CrossTicker GridHedge pull a second series; ballpark ×2.
    const series = strategyType === 'FuturesArbitrage' || (strategyType === 'GridHedge' && forms.gh.mode === 2) ? 2 : 1;
    return Math.max(1, Math.round((pathBars * series) / rate / 60));
  }, [simAccounts, accountId, pathBars, strategyType, forms.gh.mode]);

  useEffect(() => {
    if (simAccounts.length && !accountId) {
      const active = simAccounts.find((a) => a.isActive);
      setAccountId((active ?? simAccounts[0]).id);
    }
  }, [simAccounts, accountId]);

  const canFetch = !!accountId && !!symbol.trim();

  // Live klines preview — shown as the "no results yet" state.
  const {
    data: candles,
    isLoading: previewLoading,
    error: previewError,
  } = useQuery<CandleData[]>({
    queryKey: ['tester-klines', accountId, symbol, previewTimeframe],
    queryFn: () =>
      api
        .get('/tester/klines', {
          params: { accountId, symbol: symbol.trim().toUpperCase(), timeframe: previewTimeframe, limit: 300 },
        })
        .then((r) => r.data),
    enabled: canFetch,
    refetchInterval: canFetch ? (POLL_MS[previewTimeframe] ?? 60000) : false,
  });

  const simulateMutation = useMutation({
    mutationFn: (body: SimulateRequest) =>
      api.post<SimulationResult>('/tester/simulate', body, { timeout: SIMULATE_TIMEOUT_MS }).then((r) => r.data),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tester-cache'] }),
  });

  // Backend kline cache — which exchange/symbol windows are already on disk.
  const { data: cacheEntries } = useQuery<KlineCacheEntry[]>({
    queryKey: ['tester-cache'],
    queryFn: () => api.get('/tester/cache').then((r) => r.data),
    enabled: showCache,
  });
  const clearCacheMutation = useMutation({
    mutationFn: (key: { exchangeType?: number; symbol?: string; timeframe?: string } | null) =>
      api.delete('/tester/cache', { params: key ?? {} }).then((r) => r.data as { removedCandles: number }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tester-cache'] }),
  });

  // Elapsed-time ticker while a simulation is running.
  const [elapsedSec, setElapsedSec] = useState(0);
  useEffect(() => {
    if (!simulateMutation.isPending) {
      setElapsedSec(0);
      return;
    }
    const start = Date.now();
    const id = setInterval(() => setElapsedSec(Math.floor((Date.now() - start) / 1000)), 1000);
    return () => clearInterval(id);
  }, [simulateMutation.isPending]);

  // Base config built from the current form — used by the simulate button and as the
  // template the optimization panel discovers sweepable numeric fields in.
  const baseBuild = useMemo(() => buildSimConfig(strategyType, symbol, forms), [strategyType, symbol, forms]);

  const buildRequestBody = (): { body?: SimulateRequest; error?: string } => {
    if (!accountId || !symbol.trim()) return { error: 'Выберите аккаунт и укажите символ' };
    if (strategyType === 'FuturesArbitrage' && !secondAccountId)
      return { error: 'Выберите второй аккаунт (другая биржа) для арбитража' };
    const build = buildSimConfig(strategyType, symbol, forms);
    if (!build.ok || !build.configJson) return { error: build.error ?? 'Некорректная конфигурация' };
    const useExplicitRange = !!fromDate && !!toDate;
    return {
      body: {
        accountId,
        strategyType,
        symbol: symbol.replace(/\s+/g, '').toUpperCase(),
        secondSymbol: build.secondSymbol ?? null,
        secondAccountId: strategyType === 'FuturesArbitrage' ? secondAccountId : null,
        fromUtc: useExplicitRange ? new Date(`${fromDate}T00:00:00Z`).toISOString() : null,
        toUtc: useExplicitRange ? new Date(`${toDate}T23:59:59Z`).toISOString() : null,
        days,
        pathTimeframe,
        configJson: build.configJson,
        makerFeeRate: makerFeePercent.trim() === '' ? null : Number(makerFeePercent) / 100,
        takerFeeRate: takerFeePercent.trim() === '' ? null : Number(takerFeePercent) / 100,
        bypassCache,
      },
    };
  };

  const handleSimulate = () => {
    setFormError('');
    const req = buildRequestBody();
    if (!req.body) {
      setFormError(req.error ?? 'Некорректная конфигурация');
      return;
    }
    simulateMutation.mutate(req.body);
  };

  /** Full simulation of one optimizer combination — its configJson replaces the form-built one. */
  const handleSimulateConfig = (configJson: string) => {
    setFormError('');
    const req = buildRequestBody();
    if (!req.body) {
      setFormError(req.error ?? 'Некорректная конфигурация');
      return;
    }
    simulateMutation.mutate({ ...req.body, configJson });
  };

  const simResult = simulateMutation.data;

  const chartMarkers: ChartMarker[] = useMemo(() => {
    if (!simResult?.trades) return [];
    return simResult.trades.map((t) => {
      const tag = `${t.action} ${t.reason}`.toLowerCase();
      let color = '#3b82f6'; // default — entry/open
      if (tag.includes('takeprofit') || tag.includes('take profit') || / tp\b/.test(tag)) color = '#22c55e';
      else if (tag.includes('stoploss') || tag.includes('stop loss') || / sl\b/.test(tag)) color = '#ef4444';
      else if (tag.includes('dca')) color = '#f59e0b';
      else if (tag.includes('funding')) color = '#a855f7';

      const sideLower = (t.side || '').toLowerCase();
      const isBuy = sideLower.includes('long') || sideLower.includes('buy');
      return {
        time: new Date(t.time).getTime() / 1000,
        position: (isBuy ? 'belowBar' : 'aboveBar') as 'belowBar' | 'aboveBar',
        shape: (isBuy ? 'arrowUp' : 'arrowDown') as 'arrowUp' | 'arrowDown',
        color,
        text: `${t.action}${t.pnlUsd != null ? ` ${t.pnlUsd >= 0 ? '+' : ''}${t.pnlUsd.toFixed(1)}$` : ''}`,
      };
    });
  }, [simResult]);

  const indicatorData: IndicatorDataPoint[] = useMemo(() => {
    if (!simResult?.indicatorValues) return [];
    return simResult.indicatorValues.map((p) => ({ time: new Date(p.time).getTime() / 1000, value: p.value }));
  }, [simResult]);

  const inputCls =
    'bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all';

  return (
    <div>
      <Header title="Tester" subtitle="Real-time exchange chart + strategy backtesting" />

      {/* Top controls */}
      <div className="flex flex-wrap items-end gap-4 mb-4">
        <div className="flex flex-col gap-1.5">
          <label className="text-xs font-medium text-text-secondary">Account</label>
          <select value={accountId} onChange={(e) => setAccountId(e.target.value)} className={`${inputCls} min-w-[200px]`}>
            <option value="">Select account...</option>
            {simAccounts.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name} ({EXCHANGE_NAMES[a.exchangeType]})
              </option>
            ))}
          </select>
          {hiddenAccountCount > 0 && (
            <span className="text-[11px] text-text-secondary">
              Скрыто {hiddenAccountCount}: симулятор работает только с Bybit / Bitget / BingX
            </span>
          )}
        </div>

        <div className="flex flex-col gap-1.5">
          <label className="text-xs font-medium text-text-secondary">Symbol</label>
          <input
            type="text"
            value={symbol}
            onChange={(e) => setSymbol(e.target.value.toUpperCase())}
            placeholder="BTCUSDT"
            className={`${inputCls} w-[160px] font-mono`}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <label className="text-xs font-medium text-text-secondary">Стратегия</label>
          <select
            value={strategyType}
            onChange={(e) => setStrategyType(e.target.value as StrategyType)}
            className={`${inputCls} min-w-[260px]`}
          >
            {STRATEGY_TYPES.map((st) => {
              const supported = !supportedStrategies || supportedStrategies.includes(st);
              return (
                <option key={st} value={st} disabled={!supported}>
                  {STRATEGY_LABELS[st]}{!supported ? ' (недоступно)' : ''}
                </option>
              );
            })}
          </select>
        </div>

        {strategyType === 'FuturesArbitrage' && (
          <>
            <div className="flex flex-col gap-1.5">
              <label className="text-xs font-medium text-text-secondary">Account 2 (другая биржа) *</label>
              <select
                value={secondAccountId}
                onChange={(e) => setSecondAccountId(e.target.value)}
                className={`${inputCls} min-w-[200px]`}
              >
                <option value="">Select account...</option>
                {simAccounts.filter((a) => a.id !== accountId).map((a) => (
                  <option key={a.id} value={a.id}>
                    {a.name} ({EXCHANGE_NAMES[a.exchangeType]})
                  </option>
                ))}
              </select>
              {secondAccountId && (() => {
                const acc1 = simAccounts.find((a) => a.id === accountId);
                const acc2 = simAccounts.find((a) => a.id === secondAccountId);
                if (acc1 && acc2 && acc1.exchangeType === acc2.exchangeType) {
                  return <p className="text-xs text-accent-yellow">⚠ Второй аккаунт должен быть на другой бирже</p>;
                }
                return null;
              })()}
            </div>

            <div className="flex flex-col gap-1.5">
              <label className="text-xs font-medium text-text-secondary">Symbol B (опционально)</label>
              <input
                type="text"
                value={forms.arb.secondSymbol}
                onChange={(e) => setForms((f) => ({ ...f, arb: { ...f.arb, secondSymbol: e.target.value.toUpperCase() } }))}
                placeholder="пусто = тот же символ"
                className={`${inputCls} w-[160px] font-mono`}
              />
            </div>
          </>
        )}
      </div>

      {/* Period + fees */}
      <div className="bg-bg-secondary rounded-xl border border-border p-4 mb-4 space-y-3">
        <div className="flex flex-wrap items-end gap-4">
          <div className="flex flex-col gap-1.5">
            <label className="text-xs font-medium text-text-secondary">Период</label>
            <div className="flex rounded-lg overflow-hidden border border-border">
              {DAY_PRESETS.map((d) => (
                <button
                  key={d}
                  onClick={() => { setDays(d); setFromDate(''); setToDate(''); }}
                  className={`px-3 py-2.5 text-xs font-medium transition-colors ${
                    days === d && !fromDate && !toDate
                      ? 'bg-accent-blue text-white'
                      : 'bg-bg-primary text-text-secondary hover:bg-bg-tertiary hover:text-text-primary'
                  }`}
                >
                  {d >= 365 ? `${Math.round(d / 365)}г` : `${d}д`}
                </button>
              ))}
            </div>
          </div>

          <div className="flex flex-col gap-1.5">
            <label className="text-xs font-medium text-text-secondary" title="Таймфрейм скачиваемых баров: крупнее ТФ — в разы меньше данных и быстрее загрузка, но ниже точность внутрибарных заполнений (4 тика на бар)">
              ТФ данных
            </label>
            <select value={pathTimeframe} onChange={(e) => setPathTimeframe(e.target.value)} className={`${inputCls} w-[90px]`}>
              {PATH_TIMEFRAMES.map((tf) => (
                <option key={tf} value={tf}>{tf}</option>
              ))}
            </select>
          </div>

          <div className="flex flex-col gap-1.5">
            <label className="text-xs font-medium text-text-secondary">С даты (UTC, опционально)</label>
            <input type="date" value={fromDate} onChange={(e) => setFromDate(e.target.value)} className={inputCls} />
          </div>
          <div className="flex flex-col gap-1.5">
            <label className="text-xs font-medium text-text-secondary">По дату (UTC, опционально)</label>
            <input type="date" value={toDate} onChange={(e) => setToDate(e.target.value)} className={inputCls} />
          </div>

          <div className="flex flex-col gap-1.5">
            <label className="text-xs font-medium text-text-secondary">Maker fee, %</label>
            <input
              type="number" step="0.001" placeholder="авто"
              value={makerFeePercent} onChange={(e) => setMakerFeePercent(e.target.value)}
              className={`${inputCls} w-[110px] font-mono`}
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <label className="text-xs font-medium text-text-secondary">Taker fee, %</label>
            <input
              type="number" step="0.001" placeholder="авто"
              value={takerFeePercent} onChange={(e) => setTakerFeePercent(e.target.value)}
              className={`${inputCls} w-[110px] font-mono`}
            />
          </div>

          <button
            onClick={handleSimulate}
            disabled={!canFetch || simulateMutation.isPending}
            className="bg-accent-blue hover:bg-accent-blue/80 disabled:opacity-50 text-white px-5 py-2.5 rounded-lg text-sm font-medium transition-colors shadow-lg shadow-accent-blue/25"
          >
            {simulateMutation.isPending ? `Симуляция... ${elapsedSec}с` : 'Запустить симуляцию'}
          </button>
          {pathBars !== null && pathBars > MAX_PATH_BARS ? (
            <span className="text-[11px] text-accent-red self-center">
              ~{pathBars.toLocaleString('ru-RU')} баров на {pathTimeframe} — больше лимита {MAX_PATH_BARS.toLocaleString('ru-RU')}. Возьмите крупнее ТФ данных или короче период.
            </span>
          ) : estimatedMinutes !== null && pathBars !== null ? (
            <span className="text-[11px] text-text-secondary self-center" title="Оценка времени загрузки истории с биржи, если её ещё нет в кэше; повторные прогоны по тому же окну — секунды">
              ~{pathBars.toLocaleString('ru-RU')} баров ({pathTimeframe}) · ≈ {estimatedMinutes} мин, если истории нет в кэше
            </span>
          ) : null}
        </div>

        <div className="flex flex-wrap items-center gap-4 text-xs">
          <label className="flex items-center gap-2 cursor-pointer select-none text-text-secondary">
            <input type="checkbox" checked={bypassCache} onChange={(e) => setBypassCache(e.target.checked)}
              className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer" />
            Перекачать историю с биржи (не использовать кэш)
          </label>
          <button type="button" onClick={() => setShowCache((v) => !v)} className="text-accent-blue hover:underline">
            {showCache ? 'Скрыть кэш истории' : 'Показать кэш истории'}
          </button>
        </div>

        {showCache && (
          <div className="border border-border rounded-lg p-3 space-y-2">
            <div className="flex items-center justify-between">
              <span className="text-xs font-medium text-text-secondary">
                Кэш истории в БД — скачанные окна (по биржа/символ/ТФ), повторные симуляции по ним идут без обращения к бирже
              </span>
              {cacheEntries && cacheEntries.length > 0 && (
                <button
                  type="button"
                  onClick={() => { if (window.confirm('Очистить весь кэш истории?')) clearCacheMutation.mutate(null); }}
                  className="text-xs text-accent-red hover:underline"
                >
                  Очистить всё
                </button>
              )}
            </div>
            {!cacheEntries ? (
              <p className="text-xs text-text-secondary">Загрузка…</p>
            ) : cacheEntries.length === 0 ? (
              <p className="text-xs text-text-secondary">Кэш пуст — первая симуляция по символу скачает историю и сохранит её.</p>
            ) : (
              <div className="overflow-x-auto">
                <table className="text-xs w-full">
                  <thead className="text-text-secondary">
                    <tr>
                      <th className="text-left font-medium pr-4 py-1">Биржа</th>
                      <th className="text-left font-medium pr-4 py-1">Символ</th>
                      <th className="text-left font-medium pr-4 py-1">TF</th>
                      <th className="text-left font-medium pr-4 py-1">Окно (UTC)</th>
                      <th className="text-right font-medium pr-4 py-1">Свечей</th>
                      <th className="text-left font-medium pr-4 py-1">Скачано</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody className="text-text-primary">
                    {cacheEntries.map((e, i) => (
                      <tr key={i} className="border-t border-border/60">
                        <td className="pr-4 py-1">{EXCHANGE_NAMES[e.exchangeType] ?? e.exchangeType}</td>
                        <td className="pr-4 py-1 font-mono">{e.symbol}</td>
                        <td className="pr-4 py-1">{e.timeframe}</td>
                        <td className="pr-4 py-1 font-mono whitespace-nowrap">{e.fromUtc.slice(0, 16).replace('T', ' ')} → {e.toUtc.slice(0, 16).replace('T', ' ')}</td>
                        <td className="pr-4 py-1 text-right font-mono">{e.candles.toLocaleString('ru-RU')}</td>
                        <td className="pr-4 py-1 whitespace-nowrap">{e.downloadedAt.slice(0, 16).replace('T', ' ')}</td>
                        <td className="py-1">
                          <button
                            type="button"
                            onClick={() => clearCacheMutation.mutate({ exchangeType: e.exchangeType, symbol: e.symbol, timeframe: e.timeframe })}
                            className="text-accent-red hover:underline"
                          >
                            удалить
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}

        {fromDate && toDate && (
          <p className="text-xs text-text-secondary">
            Явный диапазон {fromDate} → {toDate} переопределяет пресет периода.
          </p>
        )}

        {(formError || simulateMutation.isError) && (
          <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg">
            {formError || describeError(simulateMutation.error)}
          </div>
        )}

        {/* Strategy-specific config */}
        <div className="border-t border-border pt-3 space-y-3">
          <h3 className="text-xs font-semibold text-text-secondary uppercase tracking-widest">
            Параметры стратегии {strategyType}
          </h3>
          <StrategyConfigForm
            strategyType={strategyType}
            symbol={symbol}
            accountId={accountId}
            forms={forms}
            setForms={setForms}
          />
        </div>
      </div>

      {/* Parameter optimization (grid search over config fields) */}
      <OptimizationPanel
        baseConfigJson={baseBuild.ok ? baseBuild.configJson : undefined}
        buildRequestBody={buildRequestBody}
        onSimulateConfig={handleSimulateConfig}
        simulatePending={simulateMutation.isPending}
      />

      {/* Results or live preview */}
      {simResult ? (
        <SimulationResults result={simResult} chartMarkers={chartMarkers} indicatorData={indicatorData} />
      ) : (
        <>
          <div className="flex items-center gap-2 mb-3">
            <span className="text-xs font-medium text-text-secondary">Таймфрейм превью:</span>
            <div className="flex rounded-lg overflow-hidden border border-border">
              {TIMEFRAMES.map((tf) => (
                <button
                  key={tf}
                  onClick={() => setPreviewTimeframe(tf)}
                  className={`px-3 py-2 text-xs font-medium transition-colors ${
                    previewTimeframe === tf
                      ? 'bg-accent-blue text-white'
                      : 'bg-bg-primary text-text-secondary hover:bg-bg-tertiary hover:text-text-primary'
                  }`}
                >
                  {tf.toUpperCase()}
                </button>
              ))}
            </div>
          </div>

          {previewError && (
            <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg mb-4">
              Не удалось загрузить график: {describeError(previewError)}. Проверьте, что символ существует на выбранной бирже.
            </div>
          )}

          <div className="bg-bg-secondary rounded-xl border border-border p-1">
            {!canFetch ? (
              <div className="flex items-center justify-center h-[500px] text-sm text-text-secondary">
                Select an account and enter a symbol to load chart
              </div>
            ) : (
              <CandlestickChart data={candles ?? []} isLoading={previewLoading} />
            )}
          </div>

          <div className="flex items-center gap-4 mt-3 text-xs text-text-secondary">
            <span>Нет результатов симуляции — превью живого графика</span>
            {candles?.length ? <span>{candles.length} candles loaded</span> : null}
          </div>
        </>
      )}
    </div>
  );
}

function SimulationResults({
  result,
  chartMarkers,
  indicatorData,
}: {
  result: SimulationResult;
  chartMarkers: ChartMarker[];
  indicatorData: IndicatorDataPoint[];
}) {
  const s = result.summary;
  const h = result.history;
  return (
    <div className="space-y-4">
      {h && (
        <p className="text-[11px] text-text-secondary">
          История: {h.candlesFromCache.toLocaleString('ru-RU')} свечей из кэша
          {h.cacheReadSeconds > 0 ? ` (${h.cacheReadSeconds}с)` : ''}, {h.candlesDownloaded.toLocaleString('ru-RU')} скачано с биржи
          {h.downloadSeconds > 0 ? ` (${h.downloadSeconds}с)` : ''}
          {h.gapsFilled > 0 ? `, докачано дыр: ${h.gapsFilled}` : ''}
          {!h.cacheUsed ? ' — кэш не использовался' : ''}
        </p>
      )}
      {result.warnings.length > 0 && (
        <div className="bg-accent-yellow/10 border border-accent-yellow/30 rounded-lg px-4 py-3 space-y-1">
          {result.warnings.map((w, i) => (
            <p key={i} className="text-xs text-accent-yellow">⚠ {w}</p>
          ))}
        </div>
      )}

      {/* Candlestick chart with trade markers + indicator overlay */}
      <div className="bg-bg-secondary rounded-xl border border-border p-1">
        <CandlestickChart data={result.chartCandles} isLoading={false} markers={chartMarkers} indicatorData={indicatorData} />
      </div>

      {/* Equity curve */}
      <div className="bg-bg-secondary rounded-xl border border-border p-3">
        <h3 className="text-sm font-semibold text-text-primary mb-2">Кривая эквити</h3>
        <EquityChart data={result.equityCurve} />
      </div>

      {/* Summary cards */}
      <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-3">
        <StatCard label="Net PnL" value={fmtUsd(s.netPnlUsd)} color={s.netPnlUsd >= 0 ? 'green' : 'red'} />
        <StatCard label="Gross PnL" value={fmtUsd(s.grossPnlUsd)} color={s.grossPnlUsd >= 0 ? 'green' : 'red'} />
        <StatCard label="Комиссии" value={fmtUsd(-Math.abs(s.feesUsd))} color="red" />
        <StatCard label="Funding PnL" value={fmtUsd(s.fundingPnlUsd)} color={s.fundingPnlUsd >= 0 ? 'green' : 'red'} />
        <StatCard label="Win Rate" value={`${s.winRate.toFixed(1)}%`} />
        <StatCard label="Сделок" value={`${s.totalTrades} (${s.winningTrades}W / ${s.losingTrades}L)`} />
        <StatCard label="Макс. просадка $" value={fmtUsd(-Math.abs(s.maxDrawdownUsd))} color="red" />
        <StatCard label="Макс. просадка %" value={`${s.maxDrawdownPercent.toFixed(2)}%`} color="red" />
        <StatCard label="Пик номинала" value={fmtUsd(s.maxNotionalUsd)} />
        <StatCard label="Циклов" value={s.completedCycles} />
        <StatCard label="Откр. позиций" value={s.openPositionsAtEnd} />
        <StatCard label="Незафикс. PnL" value={fmtUsd(s.unrealizedPnlAtEndUsd)} color={s.unrealizedPnlAtEndUsd >= 0 ? 'green' : 'red'} />
      </div>

      <p className="text-xs text-text-secondary">
        Период: {new Date(s.startTime).toLocaleString('ru-RU')} — {new Date(s.endTime).toLocaleString('ru-RU')} · обработано свечей: {s.pathCandlesProcessed}
      </p>

      {/* Trades table */}
      <TradesTable trades={result.trades} />
    </div>
  );
}

function fmtUsd(v: number): string {
  return `${v >= 0 ? '+' : ''}${v.toFixed(2)}$`;
}

function StatCard({ label, value, color }: { label: string; value: string | number; color?: 'green' | 'red' }) {
  const colorCls = color === 'green' ? 'text-green-400' : color === 'red' ? 'text-red-400' : 'text-text-primary';
  return (
    <div className="bg-bg-secondary rounded-xl border border-border px-4 py-3">
      <div className="text-xs text-text-secondary mb-1">{label}</div>
      <div className={`text-base font-semibold font-mono ${colorCls}`}>{value}</div>
    </div>
  );
}
