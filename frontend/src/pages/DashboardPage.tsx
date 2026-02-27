import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import api from '../api/client';
import Header from '../components/Layout/Header';

interface PnlPoint {
  date: string;
  cumPnl: number;
}

interface WorkspaceDashboard {
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
}

export default function DashboardPage() {
  const navigate = useNavigate();

  const { data: workspaces, isLoading } = useQuery<WorkspaceDashboard[]>({
    queryKey: ['dashboard-workspaces'],
    queryFn: () => api.get('/dashboard/workspaces').then((r) => r.data),
    refetchInterval: 15000,
  });

  const totalPnl = workspaces?.reduce((s, w) => s + w.realizedPnl, 0) ?? 0;
  const totalUnrealized = workspaces?.reduce((s, w) => s + w.unrealizedPnl, 0) ?? 0;
  const totalBots = workspaces?.reduce((s, w) => s + w.totalBots, 0) ?? 0;
  const totalInPosition = workspaces?.reduce((s, w) => s + w.botsInPosition, 0) ?? 0;

  return (
    <div>
      <Header title="Dashboard" subtitle="Обзор торговых воркспейсов" />

      {/* Summary row */}
      <div className="grid grid-cols-2 lg:grid-cols-4 gap-3 mb-6">
        <MiniStat
          label="Realized PnL"
          value={`$${totalPnl.toFixed(2)}`}
          color={totalPnl >= 0 ? 'text-accent-green' : 'text-accent-red'}
        />
        <MiniStat
          label="Unrealized"
          value={`$${totalUnrealized.toFixed(2)}`}
          color={totalUnrealized >= 0 ? 'text-accent-green' : 'text-accent-red'}
        />
        <MiniStat label="Всего ботов" value={String(totalBots)} color="text-text-primary" />
        <MiniStat label="В позиции" value={String(totalInPosition)} color="text-accent-yellow" />
      </div>

      {/* Workspace cards */}
      {isLoading ? (
        <div className="text-text-secondary text-sm">Загрузка...</div>
      ) : !workspaces?.length ? (
        <div className="bg-bg-secondary rounded-xl border border-border p-8 text-center text-text-secondary text-sm">
          Нет воркспейсов. Создайте первый на странице «Активные боты».
        </div>
      ) : (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          {workspaces.map((ws) => (
            <WorkspaceCard key={ws.workspaceId} ws={ws} onClick={() => navigate(`/workspace/${ws.workspaceId}`)} />
          ))}
        </div>
      )}
    </div>
  );
}

function WorkspaceCard({ ws, onClick }: { ws: WorkspaceDashboard; onClick: () => void }) {
  const totalPnl = ws.realizedPnl + ws.unrealizedPnl;

  return (
    <div
      onClick={onClick}
      className="bg-bg-secondary rounded-xl border border-border p-5 cursor-pointer hover:border-accent-blue/40 transition-all group"
    >
      {/* Header */}
      <div className="flex items-center justify-between mb-4">
        <h3 className="text-base font-semibold text-text-primary group-hover:text-accent-blue transition-colors">
          {ws.workspaceName}
        </h3>
        <span className={`text-lg font-bold ${totalPnl >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
          {totalPnl >= 0 ? '+' : ''}{totalPnl.toFixed(2)}$
        </span>
      </div>

      {/* Stats row */}
      <div className="flex items-center gap-4 text-xs text-text-secondary mb-3">
        <span>
          <span className="text-text-primary font-medium">{ws.runningBots}</span>/{ws.totalBots} ботов
        </span>
        <span>
          <span className="text-accent-yellow font-medium">{ws.botsInPosition}</span> в позиции
        </span>
        <span>
          <span className="text-text-primary font-medium">{ws.totalTrades}</span> сделок
        </span>
      </div>

      {/* PnL breakdown + Win rate */}
      <div className="flex items-center gap-4 text-xs mb-4">
        <span className={ws.realizedPnl >= 0 ? 'text-accent-green' : 'text-accent-red'}>
          Realized: {ws.realizedPnl >= 0 ? '+' : ''}{ws.realizedPnl.toFixed(2)}$
        </span>
        {ws.unrealizedPnl !== 0 && (
          <span className={ws.unrealizedPnl >= 0 ? 'text-accent-green/70' : 'text-accent-red/70'}>
            Unrealized: {ws.unrealizedPnl >= 0 ? '+' : ''}{ws.unrealizedPnl.toFixed(2)}$
          </span>
        )}
        <span className="ml-auto text-text-secondary">
          WR: <span className="text-text-primary font-medium">{ws.winRate}%</span>
          <span className="text-text-secondary ml-1">({ws.winningTrades}W / {ws.losingTrades}L)</span>
        </span>
      </div>

      {/* Sparkline */}
      {ws.pnlCurve.length > 1 && (
        <Sparkline points={ws.pnlCurve} />
      )}
      {ws.pnlCurve.length <= 1 && (
        <div className="h-10 flex items-center justify-center text-[10px] text-text-secondary">
          Нет данных для графика
        </div>
      )}
    </div>
  );
}

function Sparkline({ points }: { points: PnlPoint[] }) {
  const values = points.map((p) => p.cumPnl);
  const min = Math.min(...values);
  const max = Math.max(...values);
  const range = max - min || 1;

  const w = 300;
  const h = 40;
  const pad = 2;

  const coords = values.map((v, i) => {
    const x = pad + (i / (values.length - 1)) * (w - pad * 2);
    const y = h - pad - ((v - min) / range) * (h - pad * 2);
    return `${x},${y}`;
  });

  const last = values[values.length - 1];
  const color = last >= 0 ? '#22c55e' : '#ef4444';

  // Fill area
  const fillCoords = [
    `${pad},${h - pad}`,
    ...coords,
    `${w - pad},${h - pad}`,
  ].join(' ');

  return (
    <svg viewBox={`0 0 ${w} ${h}`} className="w-full h-10" preserveAspectRatio="none">
      <polygon points={fillCoords} fill={color} fillOpacity={0.08} />
      <polyline
        points={coords.join(' ')}
        fill="none"
        stroke={color}
        strokeWidth={1.5}
        strokeLinejoin="round"
        strokeLinecap="round"
      />
    </svg>
  );
}

function MiniStat({ label, value, color }: { label: string; value: string; color: string }) {
  return (
    <div className="bg-bg-secondary rounded-lg border border-border px-4 py-3">
      <p className={`text-lg font-bold ${color} leading-none`}>{value}</p>
      <p className="text-[11px] text-text-secondary mt-1">{label}</p>
    </div>
  );
}
