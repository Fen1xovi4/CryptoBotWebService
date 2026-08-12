import { useEffect, useMemo, useState } from 'react';
import { useQuery, useMutation } from '@tanstack/react-query';
import api from '../api/client';
import Header from '../components/Layout/Header';
import CandlestickChart from '../components/Chart/CandlestickChart';
import type { CandleData, ChartMarker, IndicatorDataPoint } from '../components/Chart/CandlestickChart';
import StrategyConfigForm from './tester/StrategyConfigForm';
import EquityChart from './tester/EquityChart';
import TradesTable from './tester/TradesTable';
import { buildSimConfig } from './tester/buildConfig';
import { makeDefaultForms } from './tester/formDefaults';
import type { AllForms } from './tester/formDefaults';
import { STRATEGY_LABELS, STRATEGY_TYPES } from './tester/types';
import type { Account, SimulateRequest, SimulationResult, StrategyType } from './tester/types';

const EXCHANGE_NAMES: Record<number, string> = { 1: 'Bybit', 2: 'Bitget', 3: 'BingX' };
const TIMEFRAMES = ['1m', '5m', '15m', '1h', '4h', '1d'] as const;
const POLL_MS: Record<string, number> = {
  '1m': 5000,
  '5m': 10000,
  '15m': 30000,
  '1h': 60000,
  '4h': 120000,
  '1d': 300000,
};
const DAY_PRESETS = [7, 30, 90, 180];
const SIMULATE_TIMEOUT_MS = 10 * 60 * 1000; // paginated 1m-kline download can take a while

export default function TesterPage() {
  const [accountId, setAccountId] = useState('');
  const [symbol, setSymbol] = useState('BTCUSDT');
  const [strategyType, setStrategyType] = useState<StrategyType>('MaratG');
  const [secondAccountId, setSecondAccountId] = useState('');
  const [previewTimeframe, setPreviewTimeframe] = useState('1h');

  const [days, setDays] = useState(30);
  const [fromDate, setFromDate] = useState('');
  const [toDate, setToDate] = useState('');
  const [makerFeePercent, setMakerFeePercent] = useState('');
  const [takerFeePercent, setTakerFeePercent] = useState('');

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

  useEffect(() => {
    if (accounts?.length && !accountId) {
      const active = accounts.find((a) => a.isActive);
      setAccountId((active ?? accounts[0]).id);
    }
  }, [accounts, accountId]);

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

  const handleSimulate = () => {
    setFormError('');
    if (!accountId || !symbol.trim()) {
      setFormError('Выберите аккаунт и укажите символ');
      return;
    }
    if (strategyType === 'FuturesArbitrage' && !secondAccountId) {
      setFormError('Выберите второй аккаунт (другая биржа) для арбитража');
      return;
    }
    const build = buildSimConfig(strategyType, symbol, forms);
    if (!build.ok || !build.configJson) {
      setFormError(build.error ?? 'Некорректная конфигурация');
      return;
    }
    const useExplicitRange = !!fromDate && !!toDate;
    const body: SimulateRequest = {
      accountId,
      strategyType,
      symbol: symbol.replace(/\s+/g, '').toUpperCase(),
      secondSymbol: build.secondSymbol ?? null,
      secondAccountId: strategyType === 'FuturesArbitrage' ? secondAccountId : null,
      fromUtc: useExplicitRange ? new Date(`${fromDate}T00:00:00Z`).toISOString() : null,
      toUtc: useExplicitRange ? new Date(`${toDate}T23:59:59Z`).toISOString() : null,
      days,
      configJson: build.configJson,
      makerFeeRate: makerFeePercent.trim() === '' ? null : Number(makerFeePercent) / 100,
      takerFeeRate: takerFeePercent.trim() === '' ? null : Number(takerFeePercent) / 100,
    };
    simulateMutation.mutate(body);
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
            {accounts?.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name} ({EXCHANGE_NAMES[a.exchangeType]})
              </option>
            ))}
          </select>
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
                {accounts?.filter((a) => a.id !== accountId).map((a) => (
                  <option key={a.id} value={a.id}>
                    {a.name} ({EXCHANGE_NAMES[a.exchangeType]})
                  </option>
                ))}
              </select>
              {secondAccountId && accounts && (() => {
                const acc1 = accounts.find((a) => a.id === accountId);
                const acc2 = accounts.find((a) => a.id === secondAccountId);
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
                  {d}д
                </button>
              ))}
            </div>
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
        </div>

        {fromDate && toDate && (
          <p className="text-xs text-text-secondary">
            Явный диапазон {fromDate} → {toDate} переопределяет пресет периода.
          </p>
        )}

        {(formError || simulateMutation.isError) && (
          <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg">
            {formError || (simulateMutation.error as Error)?.message || 'Ошибка симуляции'}
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
              Failed to load chart data. Check that the symbol is correct for the selected exchange.
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
  return (
    <div className="space-y-4">
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
