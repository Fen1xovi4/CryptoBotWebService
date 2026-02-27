import { useEffect, useRef } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { createChart, LineSeries, ColorType } from 'lightweight-charts';
import type { IChartApi, UTCTimestamp } from 'lightweight-charts';
import api from '../api/client';
import Header from '../components/Layout/Header';

interface PnlPoint {
  date: string;
  cumPnl: number;
}

interface TradeDto {
  id: string;
  symbol: string;
  side: string;
  price: number;
  quantity: number;
  pnlDollar: number | null;
  status: string | null;
  executedAt: string;
}

interface BotSummary {
  strategyId: string;
  name: string;
  symbol: string;
  status: string;
  hasPosition: boolean;
  positionDirection: string | null;
  realizedPnl: number;
  totalTrades: number;
}

interface WorkspaceDetail {
  workspaceId: string;
  workspaceName: string;
  totalBots: number;
  runningBots: number;
  botsInPosition: number;
  realizedPnl: number;
  unrealizedPnl: number;
  totalTrades: number;
  winningTrades: number;
  losingTrades: number;
  winRate: number;
  pnlCurve: PnlPoint[];
  avgTradePnl: number;
  maxDrawdown: number;
  recentTrades: TradeDto[];
  bots: BotSummary[];
}

export default function WorkspaceDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();

  const { data: ws, isLoading } = useQuery<WorkspaceDetail>({
    queryKey: ['workspace-detail', id],
    queryFn: () => api.get(`/dashboard/workspaces/${id}`).then((r) => r.data),
    refetchInterval: 15000,
    enabled: !!id,
  });

  if (isLoading) return <div className="text-text-secondary text-sm p-4">Загрузка...</div>;
  if (!ws) return <div className="text-text-secondary text-sm p-4">Воркспейс не найден</div>;

  const totalPnl = ws.realizedPnl + ws.unrealizedPnl;

  return (
    <div>
      <Header title={ws.workspaceName} subtitle="Детальная статистика воркспейса" />

      {/* Back button */}
      <button
        onClick={() => navigate('/')}
        className="flex items-center gap-1.5 text-xs text-text-secondary hover:text-accent-blue transition-colors mb-5"
      >
        <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M15.75 19.5L8.25 12l7.5-7.5" />
        </svg>
        Назад к дашборду
      </button>

      {/* Stats cards */}
      <div className="grid grid-cols-2 lg:grid-cols-4 xl:grid-cols-6 gap-3 mb-6">
        <StatBox label="Total PnL" value={`${totalPnl >= 0 ? '+' : ''}${totalPnl.toFixed(2)}$`} color={totalPnl >= 0 ? 'text-accent-green' : 'text-accent-red'} />
        <StatBox label="Realized" value={`${ws.realizedPnl >= 0 ? '+' : ''}${ws.realizedPnl.toFixed(2)}$`} color={ws.realizedPnl >= 0 ? 'text-accent-green' : 'text-accent-red'} />
        <StatBox label="Unrealized" value={`${ws.unrealizedPnl >= 0 ? '+' : ''}${ws.unrealizedPnl.toFixed(2)}$`} color={ws.unrealizedPnl >= 0 ? 'text-accent-green' : 'text-accent-red'} />
        <StatBox label="Win Rate" value={`${ws.winRate}%`} color="text-text-primary" sub={`${ws.winningTrades}W / ${ws.losingTrades}L`} />
        <StatBox label="Avg Trade" value={`${ws.avgTradePnl >= 0 ? '+' : ''}${ws.avgTradePnl.toFixed(2)}$`} color={ws.avgTradePnl >= 0 ? 'text-accent-green' : 'text-accent-red'} />
        <StatBox label="Max Drawdown" value={`-${ws.maxDrawdown.toFixed(2)}$`} color="text-accent-red" />
      </div>

      {/* PnL chart */}
      <div className="bg-bg-secondary rounded-xl border border-border p-4 mb-6">
        <h3 className="text-sm font-semibold text-text-primary mb-3">Кривая доходности</h3>
        {ws.pnlCurve.length > 1 ? (
          <PnlChart points={ws.pnlCurve} />
        ) : (
          <div className="h-[300px] flex items-center justify-center text-text-secondary text-sm">
            Недостаточно данных для графика
          </div>
        )}
      </div>

      {/* Bots table */}
      <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden mb-6">
        <div className="px-5 py-3.5 border-b border-border">
          <h3 className="text-sm font-semibold text-text-primary">
            Боты ({ws.runningBots}/{ws.totalBots} активных, {ws.botsInPosition} в позиции)
          </h3>
        </div>
        <table className="w-full">
          <thead>
            <tr className="text-xs text-text-secondary border-b border-border">
              <th className="text-left px-5 py-2.5 font-medium">Имя</th>
              <th className="text-left px-5 py-2.5 font-medium">Символ</th>
              <th className="text-left px-5 py-2.5 font-medium">Статус</th>
              <th className="text-left px-5 py-2.5 font-medium">Позиция</th>
              <th className="text-right px-5 py-2.5 font-medium">Сделки</th>
              <th className="text-right px-5 py-2.5 font-medium">PnL</th>
            </tr>
          </thead>
          <tbody>
            {ws.bots.map((bot) => (
              <tr key={bot.strategyId} className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors">
                <td className="px-5 py-2.5 text-sm font-medium">{bot.name || bot.symbol}</td>
                <td className="px-5 py-2.5 text-sm text-text-secondary">{bot.symbol}</td>
                <td className="px-5 py-2.5">
                  <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${
                    bot.status === 'Running' ? 'bg-accent-green/15 text-accent-green' :
                    bot.status === 'Error' ? 'bg-accent-red/15 text-accent-red' :
                    'bg-bg-tertiary text-text-secondary'
                  }`}>
                    {bot.status}
                  </span>
                </td>
                <td className="px-5 py-2.5 text-sm">
                  {bot.hasPosition ? (
                    <span className={bot.positionDirection === 'Long' ? 'text-accent-green' : 'text-accent-red'}>
                      {bot.positionDirection}
                    </span>
                  ) : (
                    <span className="text-text-secondary">—</span>
                  )}
                </td>
                <td className="px-5 py-2.5 text-sm text-text-secondary text-right">{bot.totalTrades}</td>
                <td className={`px-5 py-2.5 text-sm font-medium text-right ${bot.realizedPnl >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
                  {bot.realizedPnl >= 0 ? '+' : ''}{bot.realizedPnl.toFixed(2)}$
                </td>
              </tr>
            ))}
            {!ws.bots.length && (
              <tr><td colSpan={6} className="px-5 py-8 text-center text-text-secondary text-sm">Нет ботов</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {/* Recent trades */}
      <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
        <div className="px-5 py-3.5 border-b border-border">
          <h3 className="text-sm font-semibold text-text-primary">Последние сделки ({ws.totalTrades} всего)</h3>
        </div>
        <table className="w-full">
          <thead>
            <tr className="text-xs text-text-secondary border-b border-border">
              <th className="text-left px-5 py-2.5 font-medium">Дата</th>
              <th className="text-left px-5 py-2.5 font-medium">Символ</th>
              <th className="text-left px-5 py-2.5 font-medium">Сторона</th>
              <th className="text-right px-5 py-2.5 font-medium">Цена</th>
              <th className="text-right px-5 py-2.5 font-medium">Кол-во</th>
              <th className="text-right px-5 py-2.5 font-medium">PnL</th>
            </tr>
          </thead>
          <tbody>
            {ws.recentTrades.map((t) => (
              <tr key={t.id} className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors">
                <td className="px-5 py-2.5 text-xs text-text-secondary">
                  {new Date(t.executedAt).toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })}
                </td>
                <td className="px-5 py-2.5 text-sm">{t.symbol}</td>
                <td className="px-5 py-2.5">
                  <span className={`text-xs font-medium ${t.side === 'Buy' ? 'text-accent-green' : 'text-accent-red'}`}>
                    {t.side}
                  </span>
                </td>
                <td className="px-5 py-2.5 text-sm text-text-secondary text-right">{t.price}</td>
                <td className="px-5 py-2.5 text-sm text-text-secondary text-right">{t.quantity}</td>
                <td className={`px-5 py-2.5 text-sm font-medium text-right ${
                  t.pnlDollar == null ? 'text-text-secondary' :
                  t.pnlDollar >= 0 ? 'text-accent-green' : 'text-accent-red'
                }`}>
                  {t.pnlDollar != null ? `${t.pnlDollar >= 0 ? '+' : ''}${t.pnlDollar.toFixed(2)}$` : '—'}
                </td>
              </tr>
            ))}
            {!ws.recentTrades.length && (
              <tr><td colSpan={6} className="px-5 py-8 text-center text-text-secondary text-sm">Нет сделок</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function PnlChart({ points }: { points: PnlPoint[] }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);

  useEffect(() => {
    if (!containerRef.current) return;

    const chart = createChart(containerRef.current, {
      layout: {
        background: { type: ColorType.Solid, color: '#242838' },
        textColor: '#7a8299',
      },
      grid: {
        vertLines: { color: '#2d3148' },
        horzLines: { color: '#2d3148' },
      },
      rightPriceScale: { borderColor: '#333a50' },
      timeScale: {
        borderColor: '#333a50',
        timeVisible: true,
        secondsVisible: false,
      },
      crosshair: { mode: 0 },
      width: containerRef.current.clientWidth,
      height: 300,
    });

    const lastValue = points[points.length - 1]?.cumPnl ?? 0;
    const lineColor = lastValue >= 0 ? '#22c55e' : '#ef4444';

    const series = chart.addSeries(LineSeries, {
      color: lineColor,
      lineWidth: 2,
      priceFormat: { type: 'custom', formatter: (v: number) => `$${v.toFixed(2)}` },
    });

    const data = points.map((p) => ({
      time: Math.floor(new Date(p.date).getTime() / 1000) as UTCTimestamp,
      value: p.cumPnl,
    }));

    series.setData(data);
    chart.timeScale().fitContent();
    chartRef.current = chart;

    const handleResize = () => {
      if (containerRef.current) chart.applyOptions({ width: containerRef.current.clientWidth });
    };
    window.addEventListener('resize', handleResize);

    return () => {
      window.removeEventListener('resize', handleResize);
      chart.remove();
      chartRef.current = null;
    };
  }, [points]);

  return <div ref={containerRef} />;
}

function StatBox({ label, value, color, sub }: { label: string; value: string; color: string; sub?: string }) {
  return (
    <div className="bg-bg-secondary rounded-lg border border-border px-4 py-3">
      <p className={`text-lg font-bold ${color} leading-none`}>{value}</p>
      <p className="text-[11px] text-text-secondary mt-1">{label}</p>
      {sub && <p className="text-[10px] text-text-secondary mt-0.5">{sub}</p>}
    </div>
  );
}
