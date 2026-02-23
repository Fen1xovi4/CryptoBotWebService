import { useState, useEffect, useMemo } from 'react';
import { useQuery, useMutation } from '@tanstack/react-query';
import api from '../api/client';
import Header from '../components/Layout/Header';
import CandlestickChart from '../components/Chart/CandlestickChart';
import type { CandleData, ChartMarker, IndicatorDataPoint } from '../components/Chart/CandlestickChart';

interface Account {
  id: string;
  name: string;
  exchangeType: number;
  isActive: boolean;
}

interface SimulatedTrade {
  side: string;
  action: string;
  price: number;
  time: string;
  reason: string;
  pnlPercent: number | null;
  orderSize: number;
  pnlDollar: number | null;
}

interface SimulationResult {
  trades: SimulatedTrade[];
  indicatorValues: { time: string; value: number }[];
  summary: {
    totalTrades: number;
    winningTrades: number;
    losingTrades: number;
    totalPnlPercent: number;
    totalPnlDollar: number;
    winRate: number;
    averagePnlPercent: number;
    openPositions: number;
    maxOrderSize: number;
  };
}

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

export default function TesterPage() {
  const [accountId, setAccountId] = useState('');
  const [symbol, setSymbol] = useState('BTCUSDT');
  const [timeframe, setTimeframe] = useState('1h');
  const [showSimPanel, setShowSimPanel] = useState(false);
  const [simConfig, setSimConfig] = useState({
    indicatorType: 'EMA',
    indicatorLength: 50,
    candleCount: 50,
    offsetPercent: 0,
    takeProfitPercent: 3,
    stopLossPercent: 3,
    orderSize: 100,
  });
  const [martingale, setMartingale] = useState({
    useMartingale: false,
    martingaleCoeff: 2,
    useSteppedMartingale: false,
    martingaleStep: 3,
    useDrawdownScale: false,
    drawdownBalance: 0,
    drawdownPercent: 10,
    drawdownTarget: 5,
  });

  const { data: accounts } = useQuery<Account[]>({
    queryKey: ['accounts'],
    queryFn: () => api.get('/accounts').then((r) => r.data),
  });

  useEffect(() => {
    if (accounts?.length && !accountId) {
      const active = accounts.find((a) => a.isActive);
      setAccountId((active ?? accounts[0]).id);
    }
  }, [accounts, accountId]);

  const canFetch = !!accountId && !!symbol.trim();

  const {
    data: candles,
    isLoading,
    error,
  } = useQuery<CandleData[]>({
    queryKey: ['tester-klines', accountId, symbol, timeframe],
    queryFn: () =>
      api
        .get('/tester/klines', {
          params: { accountId, symbol: symbol.trim().toUpperCase(), timeframe, limit: 300 },
        })
        .then((r) => r.data),
    enabled: canFetch,
    refetchInterval: canFetch ? (POLL_MS[timeframe] ?? 60000) : false,
  });

  const simulateMutation = useMutation({
    mutationFn: () =>
      api
        .post<SimulationResult>('/tester/simulate', {
          accountId,
          strategyType: 'MaratG',
          symbol: symbol.trim().toUpperCase(),
          timeframe,
          candleLimit: 300,
          ...simConfig,
          ...martingale,
        })
        .then((r) => r.data),
  });

  // Convert simulation results to chart markers
  const chartMarkers: ChartMarker[] = useMemo(() => {
    if (!simulateMutation.data?.trades) return [];
    return simulateMutation.data.trades.map((t) => {
      const isOpen = t.action === 'Open';
      const isLong = t.side === 'Long';
      let color: string;
      if (isOpen) {
        color = '#3b82f6'; // blue
      } else {
        color = t.reason === 'TakeProfit' ? '#22c55e' : '#ef4444'; // green / red
      }
      return {
        time: new Date(t.time).getTime() / 1000,
        position: (isOpen ? 'belowBar' : 'aboveBar') as 'belowBar' | 'aboveBar',
        shape: (isLong
          ? isOpen
            ? 'arrowUp'
            : 'arrowDown'
          : isOpen
            ? 'arrowDown'
            : 'arrowUp') as 'arrowUp' | 'arrowDown',
        color,
        text: isOpen
          ? `${t.side} $${t.orderSize}`
          : `${t.reason === 'TakeProfit' ? 'TP' : 'SL'} ${t.pnlDollar != null ? (t.pnlDollar >= 0 ? '+' : '') + t.pnlDollar.toFixed(1) + '$' : ''}`,
      };
    });
  }, [simulateMutation.data]);

  // Convert indicator values for chart overlay
  const indicatorData: IndicatorDataPoint[] = useMemo(() => {
    if (!simulateMutation.data?.indicatorValues) return [];
    return simulateMutation.data.indicatorValues.map((p) => ({
      time: new Date(p.time).getTime() / 1000,
      value: p.value,
    }));
  }, [simulateMutation.data]);

  const summary = simulateMutation.data?.summary;
  const trades = simulateMutation.data?.trades ?? [];

  const inputCls =
    'bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all';

  const updateConfig = (key: string, value: string | number) => {
    setSimConfig((prev) => ({ ...prev, [key]: value }));
  };

  const updateMartingale = (key: string, value: string | number | boolean) => {
    setMartingale((prev) => ({ ...prev, [key]: value }));
  };

  return (
    <div>
      <Header title="Tester" subtitle="Real-time exchange chart + strategy simulation" />

      {/* Controls */}
      <div className="flex flex-wrap items-end gap-4 mb-4">
        <div className="flex flex-col gap-1.5">
          <label className="text-xs font-medium text-text-secondary">Account</label>
          <select
            value={accountId}
            onChange={(e) => setAccountId(e.target.value)}
            className={`${inputCls} min-w-[200px]`}
          >
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
          <label className="text-xs font-medium text-text-secondary">Timeframe</label>
          <div className="flex rounded-lg overflow-hidden border border-border">
            {TIMEFRAMES.map((tf) => (
              <button
                key={tf}
                onClick={() => setTimeframe(tf)}
                className={`px-3 py-2.5 text-xs font-medium transition-colors ${
                  timeframe === tf
                    ? 'bg-accent-blue text-white'
                    : 'bg-bg-primary text-text-secondary hover:bg-bg-tertiary hover:text-text-primary'
                }`}
              >
                {tf.toUpperCase()}
              </button>
            ))}
          </div>
        </div>

        <button
          onClick={() => setShowSimPanel((v) => !v)}
          className={`px-4 py-2.5 rounded-lg text-sm font-medium transition-colors ${
            showSimPanel
              ? 'bg-accent-blue text-white'
              : 'bg-bg-tertiary text-text-secondary hover:text-text-primary border border-border'
          }`}
        >
          Симуляция
        </button>
      </div>

      {/* Simulation Config Panel */}
      {showSimPanel && (
        <div className="bg-bg-secondary rounded-xl border border-border p-4 mb-4 space-y-4">
          {/* Strategy params */}
          <div>
            <div className="flex items-center justify-between mb-3">
              <h3 className="text-sm font-semibold text-text-primary">Параметры стратегии MaratG</h3>
              {simulateMutation.isPending && (
                <span className="text-xs text-accent-blue">Загрузка...</span>
              )}
              {simulateMutation.isError && (
                <span className="text-xs text-accent-red">
                  Ошибка: {(simulateMutation.error as Error)?.message ?? 'unknown'}
                </span>
              )}
            </div>

            <div className="grid grid-cols-2 sm:grid-cols-4 lg:grid-cols-8 gap-3">
              <div className="flex flex-col gap-1">
                <label className="text-xs text-text-secondary">Индикатор</label>
                <select
                  value={simConfig.indicatorType}
                  onChange={(e) => updateConfig('indicatorType', e.target.value)}
                  className={inputCls}
                >
                  <option value="EMA">EMA</option>
                  <option value="SMA">SMA</option>
                </select>
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-xs text-text-secondary">Длина</label>
                <input
                  type="number"
                  value={simConfig.indicatorLength}
                  onChange={(e) => updateConfig('indicatorLength', Number(e.target.value))}
                  className={`${inputCls} font-mono`}
                  min={2}
                  max={500}
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-xs text-text-secondary">Свечей</label>
                <input
                  type="number"
                  value={simConfig.candleCount}
                  onChange={(e) => updateConfig('candleCount', Number(e.target.value))}
                  className={`${inputCls} font-mono`}
                  min={1}
                  max={500}
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-xs text-text-secondary">Оффсет %</label>
                <input
                  type="number"
                  step="0.1"
                  value={simConfig.offsetPercent}
                  onChange={(e) => updateConfig('offsetPercent', Number(e.target.value))}
                  className={`${inputCls} font-mono`}
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-xs text-text-secondary">TP %</label>
                <input
                  type="number"
                  step="0.1"
                  value={simConfig.takeProfitPercent}
                  onChange={(e) => updateConfig('takeProfitPercent', Number(e.target.value))}
                  className={`${inputCls} font-mono`}
                  min={0.1}
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-xs text-text-secondary">SL %</label>
                <input
                  type="number"
                  step="0.1"
                  value={simConfig.stopLossPercent}
                  onChange={(e) => updateConfig('stopLossPercent', Number(e.target.value))}
                  className={`${inputCls} font-mono`}
                  min={0.1}
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-xs text-text-secondary">Ордер $</label>
                <input
                  type="number"
                  value={simConfig.orderSize}
                  onChange={(e) => updateConfig('orderSize', Number(e.target.value))}
                  className={`${inputCls} font-mono`}
                  min={1}
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-xs text-text-secondary">&nbsp;</label>
                <button
                  onClick={() => simulateMutation.mutate()}
                  disabled={!canFetch || simulateMutation.isPending}
                  className="bg-accent-blue hover:bg-accent-blue/80 disabled:opacity-50 text-white px-4 py-2.5 rounded-lg text-sm font-medium transition-colors"
                >
                  Запустить
                </button>
              </div>
            </div>
          </div>

          {/* Martingale section */}
          <div className="border-t border-border/50 pt-4">
            <div className="flex flex-wrap items-center gap-4">
              {/* Martingale toggle */}
              <label className="flex items-center gap-2 cursor-pointer select-none">
                <input
                  type="checkbox"
                  checked={martingale.useMartingale}
                  onChange={(e) => updateMartingale('useMartingale', e.target.checked)}
                  className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue"
                />
                <span className="text-sm font-medium text-text-secondary">Мартингейл</span>
              </label>

              {/* Coefficient */}
              {martingale.useMartingale && (
                <div className="flex items-center gap-2">
                  <span className="text-sm text-text-secondary">x</span>
                  <input
                    type="number"
                    value={martingale.martingaleCoeff}
                    onChange={(e) => updateMartingale('martingaleCoeff', Number(e.target.value))}
                    step="0.1"
                    min="1.1"
                    className={`${inputCls} w-20 font-mono`}
                  />
                </div>
              )}

              {/* Stepped */}
              {martingale.useMartingale && (
                <>
                  <div className="h-6 w-px bg-border" />

                  <label className="flex items-center gap-2 cursor-pointer select-none">
                    <input
                      type="checkbox"
                      checked={martingale.useSteppedMartingale}
                      onChange={(e) => updateMartingale('useSteppedMartingale', e.target.checked)}
                      className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue"
                    />
                    <span className="text-sm font-medium text-text-secondary">Ступенчатый</span>
                  </label>

                  {martingale.useSteppedMartingale && (
                    <div className="flex items-center gap-2">
                      <span className="text-sm text-text-secondary">каждые</span>
                      <input
                        type="number"
                        value={martingale.martingaleStep}
                        onChange={(e) => updateMartingale('martingaleStep', Number(e.target.value))}
                        min="2"
                        className={`${inputCls} w-16 font-mono`}
                      />
                      <span className="text-sm text-text-secondary">убытков</span>
                    </div>
                  )}
                </>
              )}
            </div>

            {/* Drawdown scaling */}
            {martingale.useMartingale && (
              <div className="mt-3 pt-3 border-t border-border/30 flex flex-wrap items-center gap-4">
                <label className="flex items-center gap-2 cursor-pointer select-none">
                  <input
                    type="checkbox"
                    checked={martingale.useDrawdownScale}
                    onChange={(e) => updateMartingale('useDrawdownScale', e.target.checked)}
                    className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue"
                  />
                  <span className="text-sm font-medium text-text-secondary">По просадке</span>
                </label>

                {martingale.useDrawdownScale && (
                  <>
                    <div className="flex items-center gap-2">
                      <span className="text-xs text-text-secondary">Баланс:</span>
                      <input
                        type="number"
                        value={martingale.drawdownBalance || ''}
                        onChange={(e) => updateMartingale('drawdownBalance', Number(e.target.value))}
                        placeholder="1000"
                        className={`${inputCls} w-24 font-mono`}
                      />
                      <span className="text-xs text-text-secondary">$</span>
                    </div>

                    <div className="flex items-center gap-2">
                      <span className="text-xs text-text-secondary">Просадка:</span>
                      <input
                        type="number"
                        value={martingale.drawdownPercent}
                        onChange={(e) => updateMartingale('drawdownPercent', Number(e.target.value))}
                        min="1"
                        className={`${inputCls} w-16 font-mono`}
                      />
                      <span className="text-xs text-text-secondary">%</span>
                    </div>

                    <div className="flex items-center gap-2">
                      <span className="text-xs text-text-secondary">Цель:</span>
                      <input
                        type="number"
                        value={martingale.drawdownTarget}
                        onChange={(e) => updateMartingale('drawdownTarget', Number(e.target.value))}
                        min="1"
                        className={`${inputCls} w-16 font-mono`}
                      />
                      <span className="text-xs text-text-secondary">%</span>
                    </div>

                    {martingale.drawdownBalance > 0 && (
                      <span className="text-xs text-text-secondary/70">
                        Увеличение при &minus;${(martingale.drawdownBalance * martingale.drawdownPercent / 100).toFixed(0)}
                        {' · '}
                        Сброс при +${(martingale.drawdownBalance * martingale.drawdownTarget / 100).toFixed(0)}
                      </span>
                    )}
                  </>
                )}
              </div>
            )}
          </div>
        </div>
      )}

      {/* Error */}
      {error && (
        <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg mb-4">
          Failed to load chart data. Check that the symbol is correct for the selected exchange.
        </div>
      )}

      {/* Chart */}
      <div className="bg-bg-secondary rounded-xl border border-border p-1">
        {!canFetch ? (
          <div className="flex items-center justify-center h-[500px] text-sm text-text-secondary">
            Select an account and enter a symbol to load chart
          </div>
        ) : (
          <CandlestickChart
            data={candles ?? []}
            isLoading={isLoading}
            markers={chartMarkers}
            indicatorData={indicatorData}
          />
        )}
      </div>

      {/* Info */}
      <div className="flex items-center gap-4 mt-3 text-xs text-text-secondary">
        <span>Auto-refresh: every {(POLL_MS[timeframe] ?? 60000) / 1000}s</span>
        {candles?.length ? <span>{candles.length} candles loaded</span> : null}
      </div>

      {/* Simulation Results */}
      {summary && (
        <div className="mt-6 space-y-4">
          {/* Summary cards */}
          <div className="grid grid-cols-2 sm:grid-cols-4 lg:grid-cols-8 gap-3">
            <StatCard label="Всего сделок" value={summary.totalTrades} />
            <StatCard label="Win Rate" value={`${summary.winRate.toFixed(1)}%`} />
            <StatCard
              label="PnL %"
              value={`${summary.totalPnlPercent >= 0 ? '+' : ''}${summary.totalPnlPercent.toFixed(2)}%`}
              color={summary.totalPnlPercent >= 0 ? 'green' : 'red'}
            />
            <StatCard
              label="PnL $"
              value={`${summary.totalPnlDollar >= 0 ? '+' : ''}${summary.totalPnlDollar.toFixed(2)}`}
              color={summary.totalPnlDollar >= 0 ? 'green' : 'red'}
            />
            <StatCard
              label="Средний PnL"
              value={`${summary.averagePnlPercent >= 0 ? '+' : ''}${summary.averagePnlPercent.toFixed(2)}%`}
              color={summary.averagePnlPercent >= 0 ? 'green' : 'red'}
            />
            <StatCard label="Прибыльных" value={summary.winningTrades} color="green" />
            <StatCard label="Убыточных" value={summary.losingTrades} color="red" />
            {summary.maxOrderSize > simConfig.orderSize && (
              <StatCard label="Макс. ордер" value={`$${summary.maxOrderSize.toFixed(0)}`} color="red" />
            )}
          </div>

          {/* Trade list */}
          {trades.length > 0 && (
            <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
              <div className="px-4 py-3 border-b border-border">
                <h3 className="text-sm font-semibold text-text-primary">
                  Сделки ({trades.length})
                </h3>
              </div>
              <div className="overflow-x-auto max-h-[400px] overflow-y-auto">
                <table className="w-full text-sm">
                  <thead className="sticky top-0 bg-bg-secondary">
                    <tr className="border-b border-border text-xs text-text-secondary">
                      <th className="px-4 py-2 text-left">Время</th>
                      <th className="px-4 py-2 text-left">Сторона</th>
                      <th className="px-4 py-2 text-left">Действие</th>
                      <th className="px-4 py-2 text-right">Цена</th>
                      <th className="px-4 py-2 text-right">Ордер $</th>
                      <th className="px-4 py-2 text-left">Причина</th>
                      <th className="px-4 py-2 text-right">PnL %</th>
                      <th className="px-4 py-2 text-right">PnL $</th>
                    </tr>
                  </thead>
                  <tbody>
                    {trades.map((t, i) => (
                      <tr key={i} className="border-b border-border/30 hover:bg-bg-tertiary/50">
                        <td className="px-4 py-2 text-text-secondary font-mono text-xs">
                          {formatDate(t.time)}
                        </td>
                        <td className="px-4 py-2">
                          <span
                            className={
                              t.side === 'Long' ? 'text-green-400' : 'text-red-400'
                            }
                          >
                            {t.side}
                          </span>
                        </td>
                        <td className="px-4 py-2 text-text-primary">{t.action === 'Open' ? 'Открытие' : 'Закрытие'}</td>
                        <td className="px-4 py-2 text-right font-mono text-text-primary">
                          {t.price.toFixed(2)}
                        </td>
                        <td className="px-4 py-2 text-right font-mono text-text-secondary">
                          {t.orderSize.toFixed(0)}
                        </td>
                        <td className="px-4 py-2 text-text-secondary">{translateReason(t.reason)}</td>
                        <td className="px-4 py-2 text-right font-mono">
                          {t.pnlPercent != null ? (
                            <span
                              className={t.pnlPercent >= 0 ? 'text-green-400' : 'text-red-400'}
                            >
                              {t.pnlPercent >= 0 ? '+' : ''}
                              {t.pnlPercent.toFixed(2)}%
                            </span>
                          ) : (
                            <span className="text-text-secondary">—</span>
                          )}
                        </td>
                        <td className="px-4 py-2 text-right font-mono">
                          {t.pnlDollar != null ? (
                            <span
                              className={t.pnlDollar >= 0 ? 'text-green-400' : 'text-red-400'}
                            >
                              {t.pnlDollar >= 0 ? '+' : ''}
                              {t.pnlDollar.toFixed(2)}
                            </span>
                          ) : (
                            <span className="text-text-secondary">—</span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function StatCard({
  label,
  value,
  color,
}: {
  label: string;
  value: string | number;
  color?: 'green' | 'red';
}) {
  const colorCls =
    color === 'green'
      ? 'text-green-400'
      : color === 'red'
        ? 'text-red-400'
        : 'text-text-primary';

  return (
    <div className="bg-bg-secondary rounded-xl border border-border px-4 py-3">
      <div className="text-xs text-text-secondary mb-1">{label}</div>
      <div className={`text-lg font-semibold font-mono ${colorCls}`}>{value}</div>
    </div>
  );
}

function formatDate(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString('ru-RU', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function translateReason(reason: string): string {
  switch (reason) {
    case 'Entry':
      return 'Вход';
    case 'TakeProfit':
      return 'Take Profit';
    case 'StopLoss':
      return 'Stop Loss';
    default:
      return reason;
  }
}
