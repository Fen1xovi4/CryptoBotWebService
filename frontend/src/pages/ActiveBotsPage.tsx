import { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import api from '../api/client';
import Header from '../components/Layout/Header';
import StatusBadge from '../components/ui/StatusBadge';
import SearchableSelect from '../components/ui/SearchableSelect';
import CandlestickChart from '../components/Chart/CandlestickChart';
import type { CandleData, IndicatorDataPoint } from '../components/Chart/CandlestickChart';

interface Strategy {
  id: string;
  accountId: string;
  workspaceId: string | null;
  telegramBotId: string | null;
  accountName: string;
  exchange: string;
  name: string;
  type: string;
  configJson: string;
  stateJson: string | null;
  status: string;
  createdAt: string;
  startedAt: string | null;
}

interface TelegramBotOption {
  id: string;
  name: string;
  isActive: boolean;
}

interface Account {
  id: string;
  name: string;
  exchangeType: number;
  isActive: boolean;
}

interface Workspace {
  id: string;
  name: string;
  strategyType: string;
  configJson: string;
  sortOrder: number;
  createdAt: string;
}

interface WorkspaceConfig {
  betAmount: number;
  useMartingale: boolean;
  martingaleCoeff: number;
  useSteppedMartingale: boolean;
  martingaleStep: number;
  onlyLong: boolean;
  onlyShort: boolean;
  useDrawdownScale: boolean;
  drawdownBalance: number;
  drawdownPercent: number;
  drawdownTarget: number;
}

interface WorkspaceStats {
  workspaceId: string | null;
  totalBots: number;
  activeBots: number;
  totalTrades: number;
  pnl: number;
  unrealizedPnl: number;
}

const defaultConfig: WorkspaceConfig = {
  betAmount: 0,
  useMartingale: false,
  martingaleCoeff: 2,
  useSteppedMartingale: false,
  martingaleStep: 3,
  onlyLong: false,
  onlyShort: false,
  useDrawdownScale: false,
  drawdownBalance: 0,
  drawdownPercent: 10,
  drawdownTarget: 5,
};

const exchangeNames: Record<number, string> = { 1: 'Bybit', 2: 'Bitget', 3: 'BingX' };

function parseJson(json: string | null) {
  if (!json) return null;
  try {
    return JSON.parse(json);
  } catch {
    return null;
  }
}

const invalidateAll = (qc: ReturnType<typeof useQueryClient>) => {
  qc.invalidateQueries({ queryKey: ['strategies'] });
  qc.invalidateQueries({ queryKey: ['strategy-stats'] });
  qc.invalidateQueries({ queryKey: ['workspaces'] });
};

export default function ActiveBotsPage() {
  const queryClient = useQueryClient();
  const [showModal, setShowModal] = useState(false);
  const [editingStrategy, setEditingStrategy] = useState<Strategy | null>(null);
  const [chartStrategy, setChartStrategy] = useState<Strategy | null>(null);
  const [logStrategy, setLogStrategy] = useState<Strategy | null>(null);
  const [activeWorkspaceId, setActiveWorkspaceId] = useState<string | null>(null);

  // Local config state for instant UI response
  const [localConfig, setLocalConfig] = useState<WorkspaceConfig>(defaultConfig);
  const configSaveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // ── Queries ──
  const { data: workspaces } = useQuery<Workspace[]>({
    queryKey: ['workspaces'],
    queryFn: () => api.get('/workspaces').then((r) => r.data),
  });

  const { data: strategies, isLoading } = useQuery<Strategy[]>({
    queryKey: ['strategies'],
    queryFn: () => api.get('/strategies').then((r) => r.data),
    refetchInterval: 5000,
  });

  const { data: allStats } = useQuery<WorkspaceStats[]>({
    queryKey: ['strategy-stats'],
    queryFn: () => api.get('/strategies/stats').then((r) => r.data),
  });

  const { data: telegramBots } = useQuery<TelegramBotOption[]>({
    queryKey: ['telegram-bots'],
    queryFn: () => api.get('/telegram-bots').then((r) => r.data),
  });

  const setTelegramBotMutation = useMutation({
    mutationFn: ({ strategyId, telegramBotId }: { strategyId: string; telegramBotId: string | null }) =>
      api.patch(`/strategies/${strategyId}/telegram-bot`, { telegramBotId }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['strategies'] }),
  });

  // Auto-select first workspace
  useEffect(() => {
    if (workspaces?.length && !activeWorkspaceId) {
      setActiveWorkspaceId(workspaces[0].id);
    }
  }, [workspaces, activeWorkspaceId]);

  const activeWorkspace = workspaces?.find((w) => w.id === activeWorkspaceId);

  // Sync local config when active workspace changes
  useEffect(() => {
    if (activeWorkspace) {
      const parsed = parseJson(activeWorkspace.configJson);
      setLocalConfig({ ...defaultConfig, ...parsed });
    }
  }, [activeWorkspace?.id, activeWorkspace?.configJson]);

  // ── Config save with debounce ──
  const saveConfigMutation = useMutation({
    mutationFn: (cfg: WorkspaceConfig) =>
      api.put(`/workspaces/${activeWorkspaceId}/config`, { configJson: JSON.stringify(cfg) }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['workspaces'] }),
  });

  const updateConfig = useCallback(
    (patch: Partial<WorkspaceConfig>) => {
      setLocalConfig((prev) => {
        const next = { ...prev, ...patch };
        if (configSaveTimer.current) clearTimeout(configSaveTimer.current);
        configSaveTimer.current = setTimeout(() => {
          if (activeWorkspaceId) saveConfigMutation.mutate(next);
        }, 500);
        return next;
      });
    },
    [activeWorkspaceId],
  );

  // ── Workspace mutations ──
  const createWorkspaceMutation = useMutation({
    mutationFn: (data: { name: string; strategyType?: string }) =>
      api.post('/workspaces', { ...data, configJson: JSON.stringify(defaultConfig) }),
    onSuccess: (res) => {
      queryClient.invalidateQueries({ queryKey: ['workspaces'] });
      setActiveWorkspaceId(res.data.id);
    },
    onError: (err: unknown) => {
      const e = err as { response?: { data?: { message?: string } }; message?: string };
      alert(e.response?.data?.message || e.message || 'Ошибка создания пространства');
    },
  });

  const updateWorkspaceMutation = useMutation({
    mutationFn: ({ id, ...data }: { id: string; name?: string; strategyType?: string }) =>
      api.put(`/workspaces/${id}`, data),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['workspaces'] }),
  });

  const deleteWorkspaceMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/workspaces/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workspaces'] });
      setActiveWorkspaceId(null);
    },
    onError: (err: unknown) => {
      const e = err as { response?: { data?: { message?: string } } };
      alert(e.response?.data?.message || 'Ошибка удаления');
    },
  });

  // ── Bot mutations ──
  const startMutation = useMutation({
    mutationFn: (id: string) => api.post(`/strategies/${id}/start`),
    onSuccess: () => invalidateAll(queryClient),
  });

  const stopMutation = useMutation({
    mutationFn: (id: string) => api.post(`/strategies/${id}/stop`),
    onSuccess: () => invalidateAll(queryClient),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/strategies/${id}`),
    onSuccess: () => invalidateAll(queryClient),
  });

  const closePositionMutation = useMutation({
    mutationFn: (id: string) => api.post(`/strategies/${id}/close-position`),
    onSuccess: () => invalidateAll(queryClient),
  });

  const resetLossesMutation = useMutation({
    mutationFn: (id: string) => api.post(`/strategies/${id}/reset-losses`),
    onSuccess: () => invalidateAll(queryClient),
  });

  // ── Derived data ──
  const filteredStrategies = strategies?.filter((s) => s.workspaceId === activeWorkspaceId) || [];
  const currentStats = allStats?.find((s) => s.workspaceId === activeWorkspaceId) || {
    totalBots: 0,
    activeBots: 0,
    totalTrades: 0,
    pnl: 0,
    unrealizedPnl: 0,
  };

  const handleCreateWorkspace = () => {
    const n = (workspaces?.length ?? 0) + 1;
    createWorkspaceMutation.mutate({ name: `Пространство ${n}` });
  };

  // ── Empty state ──
  if (workspaces && workspaces.length === 0) {
    return (
      <div>
        <Header title="Активные боты" subtitle="Управление торговыми ботами" />
        <div className="text-center py-16">
          <p className="text-text-secondary mb-4">У вас пока нет рабочих пространств</p>
          <button
            onClick={() => createWorkspaceMutation.mutate({ name: 'Основной', strategyType: 'MaratG' })}
            className="px-6 py-2.5 text-sm font-medium bg-accent-blue hover:bg-accent-blue/90 text-white rounded-lg transition-colors"
          >
            Создать первое пространство
          </button>
        </div>
      </div>
    );
  }

  return (
    <div>
      <Header title="Активные боты" subtitle="Управление торговыми ботами" />

      {/* Workspace Tab Bar */}
      <div className="flex items-center gap-1 mb-6 border-b border-border">
        {workspaces?.map((ws) => (
          <WorkspaceTab
            key={ws.id}
            workspace={ws}
            isActive={ws.id === activeWorkspaceId}
            onClick={() => setActiveWorkspaceId(ws.id)}
            onRename={(name) => updateWorkspaceMutation.mutate({ id: ws.id, name })}
            onDelete={() => {
              if (confirm(`Удалить пространство "${ws.name}"? Боты будут сохранены без привязки.`))
                deleteWorkspaceMutation.mutate(ws.id);
            }}
          />
        ))}

        <button
          onClick={handleCreateWorkspace}
          className="px-3 py-2.5 text-text-secondary hover:text-accent-blue transition-colors"
          title="Добавить пространство"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M12 4.5v15m7.5-7.5h-15" />
          </svg>
        </button>
      </div>

      {/* Stat Cards */}
      <div className="grid grid-cols-2 xl:grid-cols-4 gap-4 mb-6">
        <StatCard label="Активные боты" value={currentStats.activeBots} accent="green" />
        <StatCard label="Всего ботов" value={currentStats.totalBots} accent="blue" />
        <StatCard label="Сделки" value={currentStats.totalTrades} accent="yellow" />
        <StatCard label="P&L" value={`${currentStats.pnl >= 0 ? '+' : ''}$${currentStats.pnl.toFixed(2)}`} accent={currentStats.pnl >= 0 ? 'green' : 'red'} />
      </div>

      {/* Config Panel */}
      <div className="bg-bg-secondary rounded-xl border border-border p-4 mb-6 flex flex-wrap items-center gap-4">
        {/* Strategy type selector */}
        <div className="flex items-center gap-2">
          <span className="text-sm font-medium text-text-secondary">Стратегия:</span>
          <select
            value={activeWorkspace?.strategyType ?? 'MaratG'}
            onChange={(e) => {
              if (activeWorkspaceId)
                updateWorkspaceMutation.mutate({ id: activeWorkspaceId, strategyType: e.target.value });
            }}
            className="bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
          >
            <option value="MaratG">MaratG</option>
            <option value="HuntingFunding">HuntingFunding</option>
          </select>
        </div>

        {activeWorkspace?.strategyType === 'HuntingFunding' ? (
          <p className="text-sm text-text-secondary italic">
            Все настройки задаются индивидуально в каждом боте через уровни ордеров.
          </p>
        ) : (
          <>
        <div className="h-6 w-px bg-border" />

        <div className="flex items-center gap-2">
          <span className="text-sm font-medium text-text-secondary">Сумма ставки (USDT):</span>
          <input
            type="number"
            value={localConfig.betAmount || ''}
            onChange={(e) => updateConfig({ betAmount: Number(e.target.value) })}
            placeholder="100"
            className="w-32 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
          />
        </div>

        <div className="h-6 w-px bg-border" />

        <label className="flex items-center gap-2 cursor-pointer select-none">
          <input
            type="checkbox"
            checked={localConfig.useMartingale}
            onChange={(e) => updateConfig({ useMartingale: e.target.checked })}
            className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer"
          />
          <span className="text-sm font-medium text-text-secondary">Мартингейл</span>
        </label>

        {localConfig.useMartingale && (
          <div className="flex items-center gap-2">
            <span className="text-sm text-text-secondary">x</span>
            <input
              type="number"
              value={localConfig.martingaleCoeff}
              onChange={(e) => updateConfig({ martingaleCoeff: Number(e.target.value) })}
              step="0.1"
              min="1.1"
              className="w-20 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
            />
          </div>
        )}

        {localConfig.useMartingale && (
          <>
            <div className="h-6 w-px bg-border" />

            <label className="flex items-center gap-2 cursor-pointer select-none">
              <input
                type="checkbox"
                checked={localConfig.useSteppedMartingale}
                onChange={(e) => updateConfig({ useSteppedMartingale: e.target.checked })}
                className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer"
              />
              <span className="text-sm font-medium text-text-secondary">Ступенчатый</span>
            </label>

            {localConfig.useSteppedMartingale && (
              <div className="flex items-center gap-2">
                <span className="text-sm text-text-secondary">каждые</span>
                <input
                  type="number"
                  value={localConfig.martingaleStep}
                  onChange={(e) => updateConfig({ martingaleStep: Number(e.target.value) })}
                  min="2"
                  className="w-16 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
                />
                <span className="text-sm text-text-secondary">убытков</span>
              </div>
            )}
          </>
        )}

        <div className="h-6 w-px bg-border" />

        <label className="flex items-center gap-2 cursor-pointer select-none">
          <input
            type="checkbox"
            checked={localConfig.onlyLong}
            onChange={(e) => {
              const patch: Partial<WorkspaceConfig> = { onlyLong: e.target.checked };
              if (e.target.checked) patch.onlyShort = false;
              updateConfig(patch);
            }}
            className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-green focus:ring-accent-green/50 cursor-pointer"
          />
          <span className="text-sm font-medium text-accent-green">Только Long</span>
        </label>

        <label className="flex items-center gap-2 cursor-pointer select-none">
          <input
            type="checkbox"
            checked={localConfig.onlyShort}
            onChange={(e) => {
              const patch: Partial<WorkspaceConfig> = { onlyShort: e.target.checked };
              if (e.target.checked) patch.onlyLong = false;
              updateConfig(patch);
            }}
            className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-red focus:ring-accent-red/50 cursor-pointer"
          />
          <span className="text-sm font-medium text-accent-red">Только Short</span>
        </label>

        <span className="text-xs text-text-secondary">Общая для всех ботов</span>

        {/* Drawdown scale */}
        {localConfig.useMartingale && (
          <div className="w-full border-t border-border/50 mt-1 pt-3 flex flex-wrap items-center gap-4">
            <label className="flex items-center gap-2 cursor-pointer select-none">
              <input
                type="checkbox"
                checked={localConfig.useDrawdownScale}
                onChange={(e) => updateConfig({ useDrawdownScale: e.target.checked })}
                className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer"
              />
              <span className="text-sm font-medium text-text-secondary">По просадке</span>
            </label>

            {localConfig.useDrawdownScale && (
              <>
                <div className="flex items-center gap-2">
                  <span className="text-xs text-text-secondary">Баланс:</span>
                  <input
                    type="number"
                    value={localConfig.drawdownBalance || ''}
                    onChange={(e) => updateConfig({ drawdownBalance: Number(e.target.value) })}
                    placeholder="1000"
                    className="w-24 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
                  />
                  <span className="text-xs text-text-secondary">$</span>
                </div>

                <div className="flex items-center gap-2">
                  <span className="text-xs text-text-secondary">Просадка:</span>
                  <input
                    type="number"
                    value={localConfig.drawdownPercent}
                    onChange={(e) => updateConfig({ drawdownPercent: Number(e.target.value) })}
                    min="1"
                    className="w-16 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
                  />
                  <span className="text-xs text-text-secondary">%</span>
                </div>

                <div className="flex items-center gap-2">
                  <span className="text-xs text-text-secondary">Цель:</span>
                  <input
                    type="number"
                    value={localConfig.drawdownTarget}
                    onChange={(e) => updateConfig({ drawdownTarget: Number(e.target.value) })}
                    min="1"
                    className="w-16 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
                  />
                  <span className="text-xs text-text-secondary">%</span>
                </div>

                {localConfig.drawdownBalance > 0 && (
                  <span className="text-xs text-text-secondary/70">
                    Увеличение при −${(localConfig.drawdownBalance * localConfig.drawdownPercent / 100).toFixed(0)} · Сброс при +${(localConfig.drawdownBalance * localConfig.drawdownTarget / 100).toFixed(0)}
                  </span>
                )}
              </>
            )}
          </div>
        )}
          </>
        )}

        {/* PNL */}
        <div className="w-full border-t border-border/50 mt-1 pt-3 flex flex-wrap items-center gap-4">
          <div className="flex items-center gap-2">
            <span className="text-xs text-text-secondary">Незафикс. PNL:</span>
            <span className={`text-sm font-semibold ${currentStats.unrealizedPnl < 0 ? 'text-accent-red' : currentStats.unrealizedPnl > 0 ? 'text-accent-green' : 'text-text-secondary'}`}>
              {currentStats.unrealizedPnl >= 0 ? '+' : ''}${currentStats.unrealizedPnl.toFixed(2)}
            </span>
          </div>

          <div className="flex items-center gap-2">
            <span className="text-xs text-text-secondary">Реализ. PNL:</span>
            <span className={`text-sm font-semibold ${currentStats.pnl < 0 ? 'text-accent-red' : currentStats.pnl > 0 ? 'text-accent-green' : 'text-text-secondary'}`}>
              {currentStats.pnl >= 0 ? '+' : ''}${currentStats.pnl.toFixed(2)}
            </span>
          </div>
        </div>
      </div>

      {/* Bots Header */}
      <div className="flex items-center justify-between mb-4">
        <h3 className="text-sm font-semibold text-text-secondary uppercase tracking-widest">
          Мои боты
        </h3>
        <button
          onClick={() => setShowModal(true)}
          className="px-4 py-2 text-sm font-medium bg-accent-blue hover:bg-accent-blue/90 text-white rounded-lg transition-colors shadow-md shadow-accent-blue/20"
        >
          + Добавить бота
        </button>
      </div>

      {/* Bots Grid */}
      {isLoading ? (
        <div className="text-center py-12 text-text-secondary text-sm">Загрузка...</div>
      ) : filteredStrategies.length === 0 ? (
        <div className="text-center py-12 text-text-secondary text-sm">
          Нет ботов в этом пространстве
        </div>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-3">
          {filteredStrategies.map((s) => {
            const cfg = parseJson(s.configJson);
            const state = parseJson(s.stateJson);
            const isRunning = s.status === 'Running';

            if (s.type === 'HuntingFunding') {
              return (
                <HuntingFundingCard
                  key={s.id}
                  s={s}
                  cfg={cfg}
                  state={state}
                  isRunning={isRunning}
                  onStart={() => startMutation.mutate(s.id)}
                  onStop={() => stopMutation.mutate(s.id)}
                  onDelete={() => { if (confirm('Удалить этого бота?')) deleteMutation.mutate(s.id); }}
                  onEdit={() => setEditingStrategy(s)}
                  onLogs={() => setLogStrategy(s)}
                  onClosePosition={() => closePositionMutation.mutate(s.id)}
                  closePositionPending={closePositionMutation.isPending}
                  telegramBots={telegramBots}
                  onSetTelegramBot={(botId) => setTelegramBotMutation.mutate({ strategyId: s.id, telegramBotId: botId })}
                />
              );
            }

            const consecutiveLosses = state?.consecutiveLosses ?? 0;
            const pos = state?.openLong || state?.openShort;
            const coinName = cfg?.symbol?.replace(/USDT$/i, '') || '';
            const borderAccent = isRunning
              ? pos ? 'border-l-accent-yellow' : 'border-l-accent-green'
              : 'border-l-border';

            return (
              <div
                key={s.id}
                className={`bg-bg-secondary rounded-xl border border-border border-l-2 ${borderAccent} overflow-hidden transition-colors hover:border-text-secondary/20`}
              >
                {/* Header */}
                <div className="px-4 pt-3 pb-2 flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="text-sm font-mono font-semibold text-text-primary truncate">
                        {cfg?.symbol || '—'}
                      </span>
                      {cfg?.timeframe && (
                        <span className="text-[10px] font-medium px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
                          {cfg.timeframe}
                        </span>
                      )}
                    </div>
                    <div className="text-[11px] text-text-secondary mt-0.5 truncate">
                      {s.name} · {s.accountName}
                    </div>
                  </div>
                  <StatusBadge status={s.status} />
                </div>

                {/* Config chips */}
                {cfg && (
                  <div className="px-4 pb-2 flex flex-wrap gap-1">
                    <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
                      {cfg.indicatorType || 'EMA'}{cfg.indicatorLength || 50}
                    </span>
                    <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
                      {cfg.candleCount || 50} св.
                    </span>
                    <span className="text-[10px] px-1.5 py-0.5 rounded bg-accent-green/10 text-accent-green">
                      TP {cfg.takeProfitPercent ?? 3}%
                    </span>
                    <span className="text-[10px] px-1.5 py-0.5 rounded bg-accent-red/10 text-accent-red">
                      SL {cfg.stopLossPercent ?? 3}%
                    </span>
                    {cfg.offsetPercent > 0 && (
                      <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
                        Off {cfg.offsetPercent}%
                      </span>
                    )}
                  </div>
                )}

                {/* Divider */}
                <div className="border-t border-border/50" />

                {/* Signal / Position */}
                <div className="px-4 py-2.5 min-h-[52px] flex items-center">
                  {isRunning && cfg?.candleCount ? (() => {
                    if (pos) {
                      const isLong = !!state?.openLong;
                      const lastPrice = state?.lastPrice;
                      const tpPct = cfg.takeProfitPercent ?? 3;
                      const slPct = cfg.stopLossPercent ?? 3;

                      let progress = 0;
                      let pnlPct = 0;
                      if (lastPrice && pos.entryPrice) {
                        pnlPct = isLong
                          ? (lastPrice - pos.entryPrice) / pos.entryPrice * 100
                          : (pos.entryPrice - lastPrice) / pos.entryPrice * 100;
                        progress = pnlPct >= 0
                          ? Math.min((pnlPct / tpPct) * 100, 100)
                          : Math.max((pnlPct / slPct) * 100, -100);
                      }

                      const barColor = progress >= 0 ? 'bg-accent-green' : 'bg-accent-red';
                      const barWidth = Math.min(Math.abs(progress), 100);

                      return (
                        <div className="w-full space-y-1.5">
                          <div className="flex items-center justify-between">
                            <div className="flex items-center gap-2">
                              <span className={`text-[10px] font-bold px-1.5 py-0.5 rounded ${isLong ? 'bg-accent-green/15 text-accent-green' : 'bg-accent-red/15 text-accent-red'}`}>
                                {isLong ? 'LONG' : 'SHORT'}
                              </span>
                              <span className="text-xs font-semibold text-text-primary">
                                ${pos.orderSize?.toFixed(2) ?? '—'}
                              </span>
                              <span className="text-[10px] text-text-secondary">
                                {pos.quantity} {coinName}
                              </span>
                            </div>
                            <span className={`text-[10px] font-medium ${consecutiveLosses > 0 ? 'text-accent-red' : 'text-text-secondary'}`}>
                              {consecutiveLosses} loss
                            </span>
                          </div>
                          {lastPrice ? (
                            <div>
                              <div className="relative w-full h-1.5 bg-bg-tertiary rounded-full overflow-hidden">
                                {progress >= 0 ? (
                                  <div
                                    className={`absolute left-0 top-0 h-full rounded-full ${barColor} transition-all`}
                                    style={{ width: `${barWidth}%` }}
                                  />
                                ) : (
                                  <div
                                    className={`absolute right-0 top-0 h-full rounded-full ${barColor} transition-all`}
                                    style={{ width: `${barWidth}%` }}
                                  />
                                )}
                              </div>
                              <div className={`text-[10px] font-semibold mt-0.5 ${pnlPct >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
                                {pnlPct >= 0 ? '+' : ''}{pnlPct.toFixed(2)}% ({progress >= 0 ? '+' : ''}{progress.toFixed(0)}%)
                              </div>
                            </div>
                          ) : (
                            <span className="text-[10px] text-text-secondary">загрузка цены...</span>
                          )}
                        </div>
                      );
                    }

                    {
                      const nextBet = state?.nextOrderSize;
                      const baseBet = cfg.orderSize || localConfig.betAmount;
                      const betIncreased = nextBet && baseBet && nextBet > baseBet;

                      return (
                        <div className="flex items-center gap-3 w-full">
                          {!localConfig.onlyShort && (
                            <div className="flex items-center gap-1">
                              <span className="text-[10px] text-accent-green font-medium">L</span>
                              <span className={`text-sm font-semibold ${(state?.longCounter ?? 0) > 0 ? 'text-accent-green' : 'text-text-secondary'}`}>
                                {state?.longCounter ?? 0}/{cfg.candleCount}
                              </span>
                            </div>
                          )}
                          {!localConfig.onlyLong && (
                            <div className="flex items-center gap-1">
                              <span className="text-[10px] text-accent-red font-medium">S</span>
                              <span className={`text-sm font-semibold ${(state?.shortCounter ?? 0) > 0 ? 'text-accent-red' : 'text-text-secondary'}`}>
                                {state?.shortCounter ?? 0}/{cfg.candleCount}
                              </span>
                            </div>
                          )}
                          {nextBet != null && nextBet > 0 && (
                            <span className={`text-[10px] px-1.5 py-0.5 rounded font-medium ${betIncreased ? 'bg-accent-yellow/10 text-accent-yellow' : 'bg-bg-tertiary text-text-secondary'}`}>
                              ${nextBet.toFixed(2)}
                            </span>
                          )}
                          <div className="flex items-center gap-1 ml-auto">
                            <span className={`text-xs font-semibold ${consecutiveLosses > 0 ? 'text-accent-red' : 'text-text-secondary'}`}>{consecutiveLosses}</span>
                            <span className="text-[10px] text-text-secondary">loss</span>
                            {consecutiveLosses > 0 && (
                              <button
                                onClick={() => {
                                  if (confirm(`Сбросить убытки (${consecutiveLosses}) на 0?`))
                                    resetLossesMutation.mutate(s.id);
                                }}
                                title="Сбросить"
                                className="p-0.5 text-text-secondary hover:text-accent-blue transition-colors"
                              >
                                <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                                  <path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                                </svg>
                              </button>
                            )}
                          </div>
                        </div>
                      );
                    }
                  })() : (
                    <span className="text-text-secondary text-xs">—</span>
                  )}
                </div>

                {/* Divider */}
                <div className="border-t border-border/50" />

                {/* Actions */}
                <div className="px-3 py-2 flex items-center gap-1">
                  <button
                    onClick={() => setChartStrategy(s)}
                    title="График"
                    className="p-1.5 text-text-secondary/60 hover:text-accent-blue rounded-lg hover:bg-accent-blue/10 transition-colors"
                  >
                    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M3 13.125C3 12.504 3.504 12 4.125 12h2.25c.621 0 1.125.504 1.125 1.125v6.75C7.5 20.496 6.996 21 6.375 21h-2.25A1.125 1.125 0 013 19.875v-6.75zM9.75 8.625c0-.621.504-1.125 1.125-1.125h2.25c.621 0 1.125.504 1.125 1.125v11.25c0 .621-.504 1.125-1.125 1.125h-2.25a1.125 1.125 0 01-1.125-1.125V8.625zM16.5 4.125c0-.621.504-1.125 1.125-1.125h2.25C20.496 3 21 3.504 21 4.125v15.75c0 .621-.504 1.125-1.125 1.125h-2.25a1.125 1.125 0 01-1.125-1.125V4.125z" />
                    </svg>
                  </button>
                  <button
                    onClick={() => setLogStrategy(s)}
                    title="Логи"
                    className="p-1.5 text-text-secondary/60 hover:text-accent-yellow rounded-lg hover:bg-accent-yellow/10 transition-colors"
                  >
                    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M19.5 14.25v-2.625a3.375 3.375 0 00-3.375-3.375h-1.5A1.125 1.125 0 0113.5 7.125v-1.5a3.375 3.375 0 00-3.375-3.375H8.25m0 12.75h7.5m-7.5 3H12M10.5 2.25H5.625c-.621 0-1.125.504-1.125 1.125v17.25c0 .621.504 1.125 1.125 1.125h12.75c.621 0 1.125-.504 1.125-1.125V11.25a9 9 0 00-9-9z" />
                    </svg>
                  </button>

                  {/* TG signal toggle */}
                  {telegramBots && telegramBots.length > 0 && (
                    <div className="flex items-center gap-1">
                      <button
                        onClick={() => {
                          if (s.telegramBotId) {
                            setTelegramBotMutation.mutate({ strategyId: s.id, telegramBotId: null });
                          } else if (telegramBots.filter(b => b.isActive).length === 1) {
                            setTelegramBotMutation.mutate({ strategyId: s.id, telegramBotId: telegramBots.filter(b => b.isActive)[0].id });
                          }
                        }}
                        title={s.telegramBotId ? 'Disable TG signals' : 'Enable TG signals'}
                        className={`px-1.5 py-1 text-[10px] font-bold rounded-lg transition-colors ${
                          s.telegramBotId
                            ? 'bg-accent-blue/15 text-accent-blue'
                            : 'bg-bg-tertiary text-text-secondary/40 hover:text-text-secondary'
                        }`}
                      >
                        TG
                      </button>
                      {!s.telegramBotId && telegramBots.filter(b => b.isActive).length > 1 && (
                        <select
                          className="text-[10px] bg-bg-tertiary border border-border rounded px-1 py-0.5 text-text-secondary"
                          value=""
                          onChange={(e) => {
                            if (e.target.value) {
                              setTelegramBotMutation.mutate({ strategyId: s.id, telegramBotId: e.target.value });
                            }
                          }}
                        >
                          <option value="">Select bot...</option>
                          {telegramBots.filter(b => b.isActive).map(b => (
                            <option key={b.id} value={b.id}>{b.name}</option>
                          ))}
                        </select>
                      )}
                      {s.telegramBotId && (
                        <select
                          className="text-[10px] bg-bg-tertiary border border-border rounded px-1 py-0.5 text-accent-blue"
                          value={s.telegramBotId}
                          onChange={(e) => {
                            setTelegramBotMutation.mutate({
                              strategyId: s.id,
                              telegramBotId: e.target.value || null,
                            });
                          }}
                        >
                          {telegramBots.filter(b => b.isActive).map(b => (
                            <option key={b.id} value={b.id}>{b.name}</option>
                          ))}
                        </select>
                      )}
                    </div>
                  )}

                  <div className="flex-1" />

                  {pos && (
                    <button
                      onClick={() => {
                        const dir = state?.openLong ? 'Long' : 'Short';
                        const entry = state?.openLong?.entryPrice ?? state?.openShort?.entryPrice;
                        if (confirm(`Закрыть ${dir} позицию (вход: ${entry}) по рынку?`))
                          closePositionMutation.mutate(s.id);
                      }}
                      disabled={closePositionMutation.isPending}
                      className="px-2 py-1 text-[11px] font-medium bg-accent-yellow/10 text-accent-yellow rounded-lg hover:bg-accent-yellow/20 transition-colors disabled:opacity-50"
                    >
                      {closePositionMutation.isPending ? '...' : 'Закрыть'}
                    </button>
                  )}

                  {isRunning ? (
                    <button
                      onClick={() => stopMutation.mutate(s.id)}
                      className="px-2.5 py-1 text-[11px] font-medium bg-accent-red/10 text-accent-red rounded-lg hover:bg-accent-red/20 transition-colors"
                    >
                      Стоп
                    </button>
                  ) : (
                    <>
                      <button
                        onClick={() => startMutation.mutate(s.id)}
                        className="px-2.5 py-1 text-[11px] font-medium bg-accent-green/10 text-accent-green rounded-lg hover:bg-accent-green/20 transition-colors"
                      >
                        Старт
                      </button>
                      <button
                        onClick={() => setEditingStrategy(s)}
                        title="Редактировать"
                        className="p-1.5 text-text-secondary/60 hover:text-accent-blue rounded-lg hover:bg-accent-blue/10 transition-colors"
                      >
                        <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M16.862 4.487l1.687-1.688a1.875 1.875 0 112.652 2.652L10.582 16.07a4.5 4.5 0 01-1.897 1.13L6 18l.8-2.685a4.5 4.5 0 011.13-1.897l8.932-8.931zm0 0L19.5 7.125M18 14v4.75A2.25 2.25 0 0115.75 21H5.25A2.25 2.25 0 013 18.75V8.25A2.25 2.25 0 015.25 6H10" />
                        </svg>
                      </button>
                    </>
                  )}

                  <button
                    onClick={() => {
                      if (confirm('Удалить этого бота?')) deleteMutation.mutate(s.id);
                    }}
                    title="Удалить"
                    className="p-1.5 text-text-secondary/30 hover:text-accent-red rounded-lg hover:bg-accent-red/10 transition-colors"
                  >
                    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M14.74 9l-.346 9m-4.788 0L9.26 9m9.968-3.21c.342.052.682.107 1.022.166m-1.022-.165L18.16 19.673a2.25 2.25 0 01-2.244 2.077H8.084a2.25 2.25 0 01-2.244-2.077L4.772 5.79m14.456 0a48.108 48.108 0 00-3.478-.397m-12 .562c.34-.059.68-.114 1.022-.165m0 0a48.11 48.11 0 013.478-.397m7.5 0v-.916c0-1.18-.91-2.164-2.09-2.201a51.964 51.964 0 00-3.32 0c-1.18.037-2.09 1.022-2.09 2.201v.916m7.5 0a48.667 48.667 0 00-7.5 0" />
                    </svg>
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      )}

      {showModal && activeWorkspace && (
        <AddStrategyModal
          workspaceId={activeWorkspace.id}
          strategyType={activeWorkspace.strategyType}
          onClose={() => setShowModal(false)}
        />
      )}

      {editingStrategy && (
        <EditStrategyModal strategy={editingStrategy} onClose={() => setEditingStrategy(null)} />
      )}

      {chartStrategy && (
        <ChartModal strategy={chartStrategy} onClose={() => setChartStrategy(null)} />
      )}

      {logStrategy && (
        <LogModal strategy={logStrategy} onClose={() => setLogStrategy(null)} />
      )}
    </div>
  );
}

/* ── Workspace Tab ────────────────────────────────────── */

function WorkspaceTab({
  workspace,
  isActive,
  onClick,
  onRename,
  onDelete,
}: {
  workspace: Workspace;
  isActive: boolean;
  onClick: () => void;
  onRename: (name: string) => void;
  onDelete: () => void;
}) {
  const [editing, setEditing] = useState(false);
  const [editName, setEditName] = useState(workspace.name);
  const [showMenu, setShowMenu] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    setEditName(workspace.name);
  }, [workspace.name]);

  useEffect(() => {
    if (!showMenu) return;
    const handler = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setShowMenu(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [showMenu]);

  const commitRename = () => {
    const trimmed = editName.trim();
    if (trimmed && trimmed !== workspace.name) onRename(trimmed);
    else setEditName(workspace.name);
    setEditing(false);
  };

  if (editing) {
    return (
      <input
        autoFocus
        value={editName}
        onChange={(e) => setEditName(e.target.value)}
        onBlur={commitRename}
        onKeyDown={(e) => {
          if (e.key === 'Enter') commitRename();
          if (e.key === 'Escape') { setEditName(workspace.name); setEditing(false); }
        }}
        className="bg-transparent border-b-2 border-accent-blue px-2 py-2 text-sm font-medium text-text-primary outline-none w-36"
      />
    );
  }

  return (
    <div className="relative group">
      <button
        onClick={onClick}
        onDoubleClick={() => setEditing(true)}
        className={`px-4 py-2.5 text-sm font-medium transition-colors relative ${
          isActive ? 'text-accent-blue' : 'text-text-secondary hover:text-text-primary'
        }`}
      >
        {workspace.name}
        {isActive && (
          <span className="absolute bottom-0 left-0 right-0 h-0.5 bg-accent-blue rounded-t" />
        )}
      </button>

      {isActive && (
        <div className="absolute -top-0.5 -right-1" ref={menuRef}>
          <button
            onClick={(e) => { e.stopPropagation(); setShowMenu(!showMenu); }}
            className="p-0.5 opacity-0 group-hover:opacity-100 text-text-secondary hover:text-text-primary transition-all"
          >
            <svg className="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 20 20">
              <path d="M10 6a2 2 0 110-4 2 2 0 010 4zm0 6a2 2 0 110-4 2 2 0 010 4zm0 6a2 2 0 110-4 2 2 0 010 4z" />
            </svg>
          </button>

          {showMenu && (
            <div className="absolute top-6 right-0 z-50 bg-bg-secondary border border-border rounded-lg shadow-xl py-1 min-w-[140px]">
              <button
                onClick={() => { setShowMenu(false); setEditing(true); }}
                className="w-full text-left px-3 py-1.5 text-sm text-text-primary hover:bg-bg-tertiary transition-colors"
              >
                Переименовать
              </button>
              <button
                onClick={() => { setShowMenu(false); onDelete(); }}
                className="w-full text-left px-3 py-1.5 text-sm text-accent-red hover:bg-bg-tertiary transition-colors"
              >
                Удалить
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/* ── HuntingFunding Card ───────────────────────────────── */

interface HFConfig {
  symbol: string;
  levels: { offsetPercent: number; sizeUsdt: number }[];
  takeProfitPercent: number;
  stopLossPercent: number;
  secondsBeforeFunding: number;
  closeAfterMinutes: number;
  maxCycles: number;
  enableLong: boolean;
  minFundingLong: number;
  enableShort: boolean;
  minFundingShort: number;
}

interface HFStateData {
  phase: number;
  direction: string | null;
  currentFundingRate: number | null;
  nextFundingTime: string | null;
  placedOrders: { orderId: string; levelIndex: number; side: string; price: number; quantity: number; isFilled: boolean }[];
  avgEntryPrice: number | null;
  totalFilledQuantity: number | null;
  totalFilledUsdt: number | null;
  takeProfit: number | null;
  stopLoss: number | null;
  positionOpenedAt: string | null;
  cycleCount: number;
  cycleTotalPnl: number;
  lastPrice: number | null;
}

function useCountdown(targetIso: string | null | undefined): string {
  const [display, setDisplay] = useState('--:--:--');

  useEffect(() => {
    if (!targetIso) return;
    const update = () => {
      const diff = new Date(targetIso).getTime() - Date.now();
      if (diff <= 0) { setDisplay('00:00:00'); return; }
      const h = Math.floor(diff / 3600000);
      const m = Math.floor((diff % 3600000) / 60000);
      const s = Math.floor((diff % 60000) / 1000);
      setDisplay(`${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`);
    };
    update();
    const id = setInterval(update, 1000);
    return () => clearInterval(id);
  }, [targetIso]);

  return display;
}

function HuntingFundingCard({
  s,
  cfg,
  state,
  isRunning,
  onStart,
  onStop,
  onDelete,
  onEdit,
  onLogs,
  onClosePosition,
  closePositionPending,
  telegramBots,
  onSetTelegramBot,
}: {
  s: Strategy;
  cfg: HFConfig | null;
  state: HFStateData | null;
  isRunning: boolean;
  onStart: () => void;
  onStop: () => void;
  onDelete: () => void;
  onEdit: () => void;
  onLogs: () => void;
  onClosePosition: () => void;
  closePositionPending: boolean;
  telegramBots: TelegramBotOption[] | undefined;
  onSetTelegramBot: (botId: string | null) => void;
}) {
  const phase = state?.phase ?? 0;
  const levels = cfg?.levels ?? [];

  // Compute close-position countdown: positionOpenedAt + closeAfterMinutes
  const closeTarget = state?.positionOpenedAt && cfg?.closeAfterMinutes
    ? new Date(new Date(state.positionOpenedAt).getTime() + cfg.closeAfterMinutes * 60000).toISOString()
    : null;
  const fundingCountdown = useCountdown(state?.nextFundingTime);
  const closeCountdown = useCountdown(closeTarget);

  const borderAccent = isRunning
    ? phase === 2 ? 'border-l-accent-yellow' : 'border-l-accent-green'
    : 'border-l-border';

  const phaseLabels = ['Ожидание', 'Ордера', 'В позиции', 'Кулдаун'];

  // PnL calculation for phase 2
  let pnlUsd = 0;
  let pnlPct = 0;
  if (phase === 2 && state?.avgEntryPrice && state?.lastPrice && state?.totalFilledUsdt) {
    const isLong = state.direction === 'Long';
    pnlPct = isLong
      ? (state.lastPrice - state.avgEntryPrice) / state.avgEntryPrice * 100
      : (state.avgEntryPrice - state.lastPrice) / state.avgEntryPrice * 100;
    pnlUsd = (pnlPct / 100) * state.totalFilledUsdt;
  }

  const filledCount = state?.placedOrders?.filter((o) => o.isFilled).length ?? 0;
  const totalOrderCount = state?.placedOrders?.length ?? 0;

  return (
    <div className={`bg-bg-secondary rounded-xl border border-border border-l-2 ${borderAccent} overflow-hidden transition-colors hover:border-text-secondary/20`}>
      {/* Header */}
      <div className="px-4 pt-3 pb-2 flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="text-sm font-mono font-semibold text-text-primary truncate">
              {cfg?.symbol || '—'}
            </span>
            <span className="text-[10px] font-bold px-1.5 py-0.5 rounded bg-indigo-500/15 text-indigo-400">
              HF
            </span>
          </div>
          <div className="text-[11px] text-text-secondary mt-0.5 truncate">
            {s.name} · {s.accountName}
          </div>
        </div>
        <StatusBadge status={s.status} />
      </div>

      {/* Config chips */}
      {cfg && (
        <div className="px-4 pb-2 flex flex-wrap gap-1">
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            {levels.length} уровней
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-accent-green/10 text-accent-green">
            TP {cfg.takeProfitPercent}%
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-accent-red/10 text-accent-red">
            SL {cfg.stopLossPercent}%
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            {cfg.secondsBeforeFunding}s
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            {cfg.closeAfterMinutes}min
          </span>
        </div>
      )}

      {/* Divider */}
      <div className="border-t border-border/50" />

      {/* Phase content */}
      <div className="px-4 py-2.5 min-h-[52px] flex items-center">
        {!isRunning ? (
          <span className="text-text-secondary text-xs">{phaseLabels[phase] ?? '—'}</span>
        ) : phase === 0 ? (
          <div className="w-full space-y-1">
            <div className="flex items-center gap-2">
              {state?.currentFundingRate != null && (
                <span className={`text-xs font-semibold ${state.currentFundingRate >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
                  Funding: {state.currentFundingRate >= 0 ? '+' : ''}{(state.currentFundingRate * 100).toFixed(4)}%
                </span>
              )}
            </div>
            {state?.nextFundingTime && (
              <div className="text-[11px] text-text-secondary">
                До фандинга: <span className="font-mono text-text-primary">{fundingCountdown}</span>
              </div>
            )}
          </div>
        ) : phase === 1 ? (
          <div className="flex items-center gap-2 flex-wrap">
            <span className="text-[10px] font-medium px-1.5 py-0.5 rounded bg-accent-blue/10 text-accent-blue">
              Ордера выставлены
            </span>
            {totalOrderCount > 0 && (
              <span className="text-[10px] text-text-secondary">
                Filled: {filledCount}/{totalOrderCount}
              </span>
            )}
            {state?.direction && (
              <span className={`text-[10px] font-bold px-1.5 py-0.5 rounded ${state.direction === 'Long' ? 'bg-accent-green/15 text-accent-green' : 'bg-accent-red/15 text-accent-red'}`}>
                {state.direction.toUpperCase()}
              </span>
            )}
          </div>
        ) : phase === 2 ? (
          <div className="w-full space-y-1">
            <div className="flex items-center gap-2">
              {state?.direction && (
                <span className={`text-[10px] font-bold px-1.5 py-0.5 rounded ${state.direction === 'Long' ? 'bg-accent-green/15 text-accent-green' : 'bg-accent-red/15 text-accent-red'}`}>
                  {state.direction.toUpperCase()}
                </span>
              )}
              {state?.avgEntryPrice != null && (
                <span className="text-[10px] text-text-secondary">
                  Avg: <span className="text-text-primary">${state.avgEntryPrice.toFixed(2)}</span>
                </span>
              )}
              {state?.lastPrice != null && state?.totalFilledUsdt != null && (
                <span className={`text-[10px] font-semibold ${pnlUsd >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
                  {pnlUsd >= 0 ? '+' : ''}${pnlUsd.toFixed(2)} ({pnlPct >= 0 ? '+' : ''}{pnlPct.toFixed(2)}%)
                </span>
              )}
            </div>
            {(state?.takeProfit != null || state?.stopLoss != null) && (
              <div className="text-[10px] text-text-secondary">
                {state?.takeProfit != null && <span>TP: ${state.takeProfit.toFixed(2)}</span>}
                {state?.takeProfit != null && state?.stopLoss != null && <span> | </span>}
                {state?.stopLoss != null && <span>SL: ${state.stopLoss.toFixed(2)}</span>}
              </div>
            )}
            {closeTarget && (
              <div className="text-[10px] text-text-secondary">
                Закрытие через: <span className="font-mono text-text-primary">{closeCountdown}</span>
              </div>
            )}
          </div>
        ) : phase === 3 ? (
          <div className="w-full space-y-1">
            <span className="text-[10px] text-text-secondary">Ожидание следующего фандинга</span>
            {state?.nextFundingTime && (
              <div className="text-[11px] text-text-secondary">
                До фандинга: <span className="font-mono text-text-primary">{fundingCountdown}</span>
              </div>
            )}
          </div>
        ) : (
          <span className="text-text-secondary text-xs">{phaseLabels[phase] ?? '—'}</span>
        )}
      </div>

      {/* Bottom stats row */}
      <div className="px-4 pb-2 flex items-center gap-3">
        {cfg && (
          <span className="text-[10px] text-text-secondary">
            Цикл: <span className="text-text-primary font-medium">{state?.cycleCount ?? 0}/{cfg.maxCycles || '∞'}</span>
          </span>
        )}
        {state != null && state.cycleTotalPnl != null && (
          <span className={`text-[10px] font-medium ${state.cycleTotalPnl >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
            PnL: {state.cycleTotalPnl >= 0 ? '+' : ''}${state.cycleTotalPnl.toFixed(2)}
          </span>
        )}
      </div>

      {/* Divider */}
      <div className="border-t border-border/50" />

      {/* Actions */}
      <div className="px-3 py-2 flex items-center gap-1">
        <button
          onClick={onLogs}
          title="Логи"
          className="p-1.5 text-text-secondary/60 hover:text-accent-yellow rounded-lg hover:bg-accent-yellow/10 transition-colors"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M19.5 14.25v-2.625a3.375 3.375 0 00-3.375-3.375h-1.5A1.125 1.125 0 0113.5 7.125v-1.5a3.375 3.375 0 00-3.375-3.375H8.25m0 12.75h7.5m-7.5 3H12M10.5 2.25H5.625c-.621 0-1.125.504-1.125 1.125v17.25c0 .621.504 1.125 1.125 1.125h12.75c.621 0 1.125-.504 1.125-1.125V11.25a9 9 0 00-9-9z" />
          </svg>
        </button>

        {/* TG signal toggle */}
        {telegramBots && telegramBots.length > 0 && (
          <div className="flex items-center gap-1">
            <button
              onClick={() => {
                if (s.telegramBotId) {
                  onSetTelegramBot(null);
                } else if (telegramBots.filter((b) => b.isActive).length === 1) {
                  onSetTelegramBot(telegramBots.filter((b) => b.isActive)[0].id);
                }
              }}
              title={s.telegramBotId ? 'Disable TG signals' : 'Enable TG signals'}
              className={`px-1.5 py-1 text-[10px] font-bold rounded-lg transition-colors ${
                s.telegramBotId
                  ? 'bg-accent-blue/15 text-accent-blue'
                  : 'bg-bg-tertiary text-text-secondary/40 hover:text-text-secondary'
              }`}
            >
              TG
            </button>
            {!s.telegramBotId && telegramBots.filter((b) => b.isActive).length > 1 && (
              <select
                className="text-[10px] bg-bg-tertiary border border-border rounded px-1 py-0.5 text-text-secondary"
                value=""
                onChange={(e) => { if (e.target.value) onSetTelegramBot(e.target.value); }}
              >
                <option value="">Select bot...</option>
                {telegramBots.filter((b) => b.isActive).map((b) => (
                  <option key={b.id} value={b.id}>{b.name}</option>
                ))}
              </select>
            )}
            {s.telegramBotId && (
              <select
                className="text-[10px] bg-bg-tertiary border border-border rounded px-1 py-0.5 text-accent-blue"
                value={s.telegramBotId}
                onChange={(e) => onSetTelegramBot(e.target.value || null)}
              >
                {telegramBots.filter((b) => b.isActive).map((b) => (
                  <option key={b.id} value={b.id}>{b.name}</option>
                ))}
              </select>
            )}
          </div>
        )}

        <div className="flex-1" />

        {phase === 2 && isRunning && (
          <button
            onClick={() => { if (confirm('Закрыть позицию по рынку?')) onClosePosition(); }}
            disabled={closePositionPending}
            className="px-2 py-1 text-[11px] font-medium bg-accent-yellow/10 text-accent-yellow rounded-lg hover:bg-accent-yellow/20 transition-colors disabled:opacity-50"
          >
            {closePositionPending ? '...' : 'Закрыть'}
          </button>
        )}

        {isRunning ? (
          <button
            onClick={onStop}
            className="px-2.5 py-1 text-[11px] font-medium bg-accent-red/10 text-accent-red rounded-lg hover:bg-accent-red/20 transition-colors"
          >
            Стоп
          </button>
        ) : (
          <>
            <button
              onClick={onStart}
              className="px-2.5 py-1 text-[11px] font-medium bg-accent-green/10 text-accent-green rounded-lg hover:bg-accent-green/20 transition-colors"
            >
              Старт
            </button>
            <button
              onClick={onEdit}
              title="Редактировать"
              className="p-1.5 text-text-secondary/60 hover:text-accent-blue rounded-lg hover:bg-accent-blue/10 transition-colors"
            >
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M16.862 4.487l1.687-1.688a1.875 1.875 0 112.652 2.652L10.582 16.07a4.5 4.5 0 01-1.897 1.13L6 18l.8-2.685a4.5 4.5 0 011.13-1.897l8.932-8.931zm0 0L19.5 7.125M18 14v4.75A2.25 2.25 0 0115.75 21H5.25A2.25 2.25 0 013 18.75V8.25A2.25 2.25 0 015.25 6H10" />
              </svg>
            </button>
          </>
        )}

        <button
          onClick={onDelete}
          title="Удалить"
          className="p-1.5 text-text-secondary/30 hover:text-accent-red rounded-lg hover:bg-accent-red/10 transition-colors"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M14.74 9l-.346 9m-4.788 0L9.26 9m9.968-3.21c.342.052.682.107 1.022.166m-1.022-.165L18.16 19.673a2.25 2.25 0 01-2.244 2.077H8.084a2.25 2.25 0 01-2.244-2.077L4.772 5.79m14.456 0a48.108 48.108 0 00-3.478-.397m-12 .562c.34-.059.68-.114 1.022-.165m0 0a48.11 48.11 0 013.478-.397m7.5 0v-.916c0-1.18-.91-2.164-2.09-2.201a51.964 51.964 0 00-3.32 0c-1.18.037-2.09 1.022-2.09 2.201v.916m7.5 0a48.667 48.667 0 00-7.5 0" />
          </svg>
        </button>
      </div>
    </div>
  );
}

/* ── Stat Card ─────────────────────────────────────────── */

function StatCard({
  label,
  value,
  accent,
}: {
  label: string;
  value: number | string;
  accent: 'green' | 'blue' | 'yellow' | 'red';
}) {
  const colors: Record<string, string> = {
    green: 'bg-accent-green/15 text-accent-green',
    blue: 'bg-accent-blue/15 text-accent-blue',
    yellow: 'bg-accent-yellow/15 text-accent-yellow',
    red: 'bg-accent-red/15 text-accent-red',
  };

  return (
    <div className="bg-bg-secondary rounded-xl border border-border p-4">
      <div className={`inline-block px-2 py-0.5 rounded text-[10px] font-medium mb-3 ${colors[accent]}`}>
        {label}
      </div>
      <p className="text-2xl font-bold text-text-primary leading-none">{value}</p>
    </div>
  );
}

/* ── Hunting Funding Level row type ───────────────────── */

interface HFLevel {
  offsetPercent: number;
  sizeUsdt: number;
}

/* ── Add Bot Modal ─────────────────────────────────────── */

function AddStrategyModal({
  workspaceId,
  strategyType,
  onClose,
}: {
  workspaceId: string;
  strategyType: string;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();

  const { data: accounts } = useQuery<Account[]>({
    queryKey: ['accounts'],
    queryFn: () => api.get('/accounts').then((r) => r.data),
  });

  // Common fields
  const [accountId, setAccountId] = useState('');
  const [name, setName] = useState('');
  const [symbol, setSymbol] = useState('');

  // MaratG fields
  const [mgForm, setMgForm] = useState({
    timeframe: '1h',
    indicatorType: 'EMA',
    indicatorLength: '50',
    candleCount: '50',
    offsetPercent: '0',
    takeProfitPercent: '3',
    stopLossPercent: '3',
  });

  // HuntingFunding fields
  const [hfLevels, setHfLevels] = useState<HFLevel[]>([{ offsetPercent: 1.5, sizeUsdt: 50 }]);
  const [hfForm, setHfForm] = useState({
    takeProfitPercent: '1.0',
    stopLossPercent: '0.5',
    secondsBeforeFunding: '10',
    closeAfterMinutes: '10',
    maxCycles: '0',
    enableLong: true,
    minFundingLong: '1.0',
    enableShort: true,
    minFundingShort: '1.0',
  });

  const { data: symbolsData, isLoading: symbolsLoading } = useQuery<{ symbol: string }[]>({
    queryKey: ['symbols', accountId],
    queryFn: () => api.get(`/exchange/${accountId}/symbols`).then((r) => r.data),
    enabled: !!accountId,
    staleTime: 5 * 60 * 1000,
  });

  const symbolOptions = useMemo(
    () => (symbolsData || []).map((s) => s.symbol),
    [symbolsData],
  );

  const [error, setError] = useState('');

  const mutation = useMutation({
    mutationFn: (data: { accountId: string; workspaceId: string; name: string; type: string; configJson: string }) =>
      api.post('/strategies', data),
    onSuccess: () => {
      invalidateAll(queryClient);
      onClose();
    },
    onError: (err: unknown) => {
      const e = err as { response?: { data?: { message?: string } } };
      setError(e.response?.data?.message || 'Не удалось создать бота');
    },
  });

  const handleSubmit = () => {
    if (!accountId || !name || !symbol) {
      setError('Заполните все обязательные поля');
      return;
    }

    let configJson: string;
    if (strategyType === 'HuntingFunding') {
      if (hfLevels.length === 0) {
        setError('Добавьте хотя бы один уровень');
        return;
      }
      configJson = JSON.stringify({
        symbol: symbol.replace(/\s+/g, '').toUpperCase(),
        levels: hfLevels,
        takeProfitPercent: Number(hfForm.takeProfitPercent),
        stopLossPercent: Number(hfForm.stopLossPercent),
        secondsBeforeFunding: Number(hfForm.secondsBeforeFunding),
        closeAfterMinutes: Number(hfForm.closeAfterMinutes),
        maxCycles: Number(hfForm.maxCycles),
        enableLong: hfForm.enableLong,
        minFundingLong: Number(hfForm.minFundingLong),
        enableShort: hfForm.enableShort,
        minFundingShort: Number(hfForm.minFundingShort),
      });
    } else {
      configJson = JSON.stringify({
        indicatorType: mgForm.indicatorType,
        indicatorLength: Number(mgForm.indicatorLength),
        candleCount: Number(mgForm.candleCount),
        offsetPercent: Number(mgForm.offsetPercent),
        takeProfitPercent: Number(mgForm.takeProfitPercent),
        stopLossPercent: Number(mgForm.stopLossPercent),
        symbol: symbol.replace(/\s+/g, '').toUpperCase(),
        timeframe: mgForm.timeframe,
      });
    }

    mutation.mutate({ accountId, workspaceId, name, type: strategyType, configJson });
  };

  const activeAccounts = accounts?.filter((a) => a.isActive) || [];
  const inputCls =
    'w-full bg-bg-tertiary border border-border rounded-lg px-3 py-2 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors';
  const labelCls = 'block text-xs font-medium text-text-secondary mb-1';

  const addHfLevel = () =>
    setHfLevels((prev) => [...prev, { offsetPercent: 1.5, sizeUsdt: 50 }]);
  const removeHfLevel = (i: number) =>
    setHfLevels((prev) => prev.filter((_, idx) => idx !== i));
  const updateHfLevel = (i: number, field: keyof HFLevel, value: number) =>
    setHfLevels((prev) => prev.map((l, idx) => idx === i ? { ...l, [field]: value } : l));

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm">
      <div className="bg-bg-secondary rounded-xl border border-border w-full max-w-lg max-h-[90vh] overflow-y-auto shadow-2xl">
        <div className="flex items-center justify-between px-6 py-4 border-b border-border">
          <div>
            <h2 className="text-base font-semibold text-text-primary">Добавить бота</h2>
            <p className="text-xs text-text-secondary mt-0.5">Стратегия: {strategyType}</p>
          </div>
          <button
            onClick={onClose}
            className="text-text-secondary hover:text-text-primary transition-colors"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <div className="px-6 py-4 space-y-4">
          {error && (
            <div className="text-sm text-accent-red bg-accent-red/10 rounded-lg px-3 py-2">
              {error}
            </div>
          )}

          {/* Account */}
          <div>
            <label className={labelCls}>Аккаунт *</label>
            <select
              value={accountId}
              onChange={(e) => { setAccountId(e.target.value); setSymbol(''); }}
              className={inputCls}
            >
              <option value="">Выберите аккаунт...</option>
              {activeAccounts.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name} ({exchangeNames[a.exchangeType]})
                </option>
              ))}
            </select>
          </div>

          {/* Name */}
          <div>
            <label className={labelCls}>Название бота *</label>
            <input
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder={`Мой ${strategyType} бот`}
              className={inputCls}
            />
          </div>

          <div className="border-t border-border pt-4">
            <p className="text-xs font-semibold text-text-secondary uppercase tracking-widest mb-3">
              Параметры торговли
            </p>
          </div>

          {/* Symbol */}
          <div>
            <label className={labelCls}>Символ *</label>
            <SearchableSelect
              value={symbol}
              onChange={(val) => setSymbol(val)}
              options={symbolOptions}
              placeholder="Выберите символ"
              isLoading={symbolsLoading}
              disabled={!accountId}
              className={inputCls}
            />
          </div>

          {strategyType === 'HuntingFunding' ? (
            <>
              {/* Levels table */}
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
                      {hfLevels.map((lvl, i) => (
                        <tr key={i} className="border-t border-border/50">
                          <td className="px-3 py-1.5">
                            <input
                              type="number"
                              step="0.1"
                              value={lvl.offsetPercent}
                              onChange={(e) => updateHfLevel(i, 'offsetPercent', Number(e.target.value))}
                              className="w-full bg-transparent text-text-primary focus:outline-none focus:text-accent-blue"
                            />
                          </td>
                          <td className="px-3 py-1.5">
                            <input
                              type="number"
                              step="1"
                              value={lvl.sizeUsdt}
                              onChange={(e) => updateHfLevel(i, 'sizeUsdt', Number(e.target.value))}
                              className="w-full bg-transparent text-text-primary focus:outline-none focus:text-accent-blue"
                            />
                          </td>
                          <td className="px-2 py-1.5 text-center">
                            <button
                              onClick={() => removeHfLevel(i)}
                              className="text-text-secondary/40 hover:text-accent-red transition-colors"
                              title="Удалить уровень"
                            >
                              <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                                <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                              </svg>
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                <button
                  onClick={addHfLevel}
                  className="mt-2 text-xs text-accent-blue hover:text-accent-blue/80 transition-colors"
                >
                  + Добавить уровень
                </button>
              </div>

              {/* TP / SL */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Take Profit %</label>
                  <input
                    type="number"
                    step="0.1"
                    value={hfForm.takeProfitPercent}
                    onChange={(e) => setHfForm({ ...hfForm, takeProfitPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
                <div>
                  <label className={labelCls}>Stop Loss %</label>
                  <input
                    type="number"
                    step="0.1"
                    value={hfForm.stopLossPercent}
                    onChange={(e) => setHfForm({ ...hfForm, stopLossPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* Timing */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Секунд до фандинга</label>
                  <input
                    type="number"
                    value={hfForm.secondsBeforeFunding}
                    onChange={(e) => setHfForm({ ...hfForm, secondsBeforeFunding: e.target.value })}
                    className={inputCls}
                  />
                </div>
                <div>
                  <label className={labelCls}>Закрыть через (мин)</label>
                  <input
                    type="number"
                    value={hfForm.closeAfterMinutes}
                    onChange={(e) => setHfForm({ ...hfForm, closeAfterMinutes: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* Cycles */}
              <div>
                <label className={labelCls}>Макс. циклов <span className="font-normal">(0 = бесконечно)</span></label>
                <input
                  type="number"
                  min="0"
                  value={hfForm.maxCycles}
                  onChange={(e) => setHfForm({ ...hfForm, maxCycles: e.target.value })}
                  className={inputCls}
                />
              </div>

              {/* Direction thresholds */}
              <div className="col-span-2 border border-dark-border rounded-lg p-3 space-y-3">
                <p className="text-xs text-text-secondary font-medium uppercase tracking-wide">Направления торговли</p>
                <div className="grid grid-cols-2 gap-4">
                  <div>
                    <label className="flex items-center gap-2 mb-1">
                      <input
                        type="checkbox"
                        checked={hfForm.enableLong}
                        onChange={(e) => setHfForm({ ...hfForm, enableLong: e.target.checked })}
                        className="rounded border-dark-border"
                      />
                      <span className="text-sm text-text-primary font-medium">Long</span>
                      <span className="text-xs text-text-secondary">(фандинг &lt; 0)</span>
                    </label>
                    <input
                      type="number"
                      step="0.1"
                      min="0"
                      placeholder="Мин. фандинг %"
                      value={hfForm.minFundingLong}
                      onChange={(e) => setHfForm({ ...hfForm, minFundingLong: e.target.value })}
                      disabled={!hfForm.enableLong}
                      className={inputCls + (!hfForm.enableLong ? ' opacity-50' : '')}
                    />
                    <p className="text-xs text-text-secondary mt-0.5">Торгуем если фандинг ≤ −{hfForm.minFundingLong}%</p>
                  </div>
                  <div>
                    <label className="flex items-center gap-2 mb-1">
                      <input
                        type="checkbox"
                        checked={hfForm.enableShort}
                        onChange={(e) => setHfForm({ ...hfForm, enableShort: e.target.checked })}
                        className="rounded border-dark-border"
                      />
                      <span className="text-sm text-text-primary font-medium">Short</span>
                      <span className="text-xs text-text-secondary">(фандинг &gt; 0)</span>
                    </label>
                    <input
                      type="number"
                      step="0.1"
                      min="0"
                      placeholder="Мин. фандинг %"
                      value={hfForm.minFundingShort}
                      onChange={(e) => setHfForm({ ...hfForm, minFundingShort: e.target.value })}
                      disabled={!hfForm.enableShort}
                      className={inputCls + (!hfForm.enableShort ? ' opacity-50' : '')}
                    />
                    <p className="text-xs text-text-secondary mt-0.5">Торгуем если фандинг ≥ +{hfForm.minFundingShort}%</p>
                  </div>
                </div>
              </div>
            </>
          ) : (
            <>
              {/* Timeframe */}
              <div>
                <label className={labelCls}>Таймфрейм</label>
                <select
                  value={mgForm.timeframe}
                  onChange={(e) => setMgForm({ ...mgForm, timeframe: e.target.value })}
                  className={inputCls}
                >
                  <option value="1m">1m</option>
                  <option value="5m">5m</option>
                  <option value="15m">15m</option>
                  <option value="30m">30m</option>
                  <option value="1h">1h</option>
                  <option value="4h">4h</option>
                  <option value="1d">1D</option>
                </select>
              </div>

              {/* Indicator Type + Length */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Индикатор</label>
                  <select
                    value={mgForm.indicatorType}
                    onChange={(e) => setMgForm({ ...mgForm, indicatorType: e.target.value })}
                    className={inputCls}
                  >
                    <option value="EMA">EMA</option>
                    <option value="SMA">SMA</option>
                  </select>
                </div>
                <div>
                  <label className={labelCls}>Период</label>
                  <input
                    type="number"
                    value={mgForm.indicatorLength}
                    onChange={(e) => setMgForm({ ...mgForm, indicatorLength: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* Candle Count + Offset */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Кол-во свечей</label>
                  <input
                    type="number"
                    value={mgForm.candleCount}
                    onChange={(e) => setMgForm({ ...mgForm, candleCount: e.target.value })}
                    className={inputCls}
                  />
                </div>
                <div>
                  <label className={labelCls}>Offset %</label>
                  <input
                    type="number"
                    value={mgForm.offsetPercent}
                    onChange={(e) => setMgForm({ ...mgForm, offsetPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* TP / SL */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Take Profit %</label>
                  <input
                    type="number"
                    value={mgForm.takeProfitPercent}
                    onChange={(e) => setMgForm({ ...mgForm, takeProfitPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
                <div>
                  <label className={labelCls}>Stop Loss %</label>
                  <input
                    type="number"
                    value={mgForm.stopLossPercent}
                    onChange={(e) => setMgForm({ ...mgForm, stopLossPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>
            </>
          )}
        </div>

        <div className="flex justify-end gap-3 px-6 py-4 border-t border-border">
          <button
            onClick={onClose}
            className="px-4 py-2 text-sm font-medium text-text-secondary hover:text-text-primary transition-colors"
          >
            Отмена
          </button>
          <button
            onClick={handleSubmit}
            disabled={mutation.isPending}
            className="px-4 py-2 text-sm font-medium bg-accent-blue hover:bg-accent-blue/90 text-white rounded-lg transition-colors shadow-md shadow-accent-blue/20 disabled:opacity-50 disabled:shadow-none"
          >
            {mutation.isPending ? 'Создание...' : 'Создать бота'}
          </button>
        </div>
      </div>
    </div>
  );
}

/* ── Edit Bot Modal ─────────────────────────────────────── */

function EditStrategyModal({
  strategy,
  onClose,
}: {
  strategy: Strategy;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const cfg = parseJson(strategy.configJson) || {};
  const isHF = strategy.type === 'HuntingFunding';

  const [name, setName] = useState(strategy.name);
  const [symbol, setSymbol] = useState(cfg.symbol || 'BTCUSDT');

  // MaratG form state
  const [mgForm, setMgForm] = useState({
    timeframe: cfg.timeframe || '1h',
    indicatorType: cfg.indicatorType || 'EMA',
    indicatorLength: String(cfg.indicatorLength ?? 50),
    candleCount: String(cfg.candleCount ?? 50),
    offsetPercent: String(cfg.offsetPercent ?? 0),
    takeProfitPercent: String(cfg.takeProfitPercent ?? 3),
    stopLossPercent: String(cfg.stopLossPercent ?? 3),
    onlyLong: cfg.onlyLong ?? false,
    onlyShort: cfg.onlyShort ?? false,
  });

  // HuntingFunding form state
  const [hfLevels, setHfLevels] = useState<HFLevel[]>(
    Array.isArray(cfg.levels) && cfg.levels.length > 0
      ? cfg.levels
      : [{ offsetPercent: 1.5, sizeUsdt: 50 }],
  );
  const [hfForm, setHfForm] = useState({
    takeProfitPercent: String(cfg.takeProfitPercent ?? 1.0),
    stopLossPercent: String(cfg.stopLossPercent ?? 0.5),
    secondsBeforeFunding: String(cfg.secondsBeforeFunding ?? 10),
    closeAfterMinutes: String(cfg.closeAfterMinutes ?? 10),
    maxCycles: String(cfg.maxCycles ?? 0),
    enableLong: cfg.enableLong ?? true,
    minFundingLong: String(cfg.minFundingLong ?? 1.0),
    enableShort: cfg.enableShort ?? true,
    minFundingShort: String(cfg.minFundingShort ?? 1.0),
  });

  const { data: symbolsData, isLoading: symbolsLoading } = useQuery<{ symbol: string }[]>({
    queryKey: ['symbols', strategy.accountId],
    queryFn: () => api.get(`/exchange/${strategy.accountId}/symbols`).then((r) => r.data),
    staleTime: 5 * 60 * 1000,
  });

  const symbolOptions = useMemo(
    () => (symbolsData || []).map((s) => s.symbol),
    [symbolsData],
  );

  const [error, setError] = useState('');

  const mutation = useMutation({
    mutationFn: (data: { name: string; configJson: string }) =>
      api.put(`/strategies/${strategy.id}`, data),
    onSuccess: () => {
      invalidateAll(queryClient);
      onClose();
    },
    onError: (err: unknown) => {
      const e = err as { response?: { data?: { message?: string } } };
      setError(e.response?.data?.message || 'Не удалось обновить бота');
    },
  });

  const handleSubmit = () => {
    if (!name || !symbol) {
      setError('Заполните все обязательные поля');
      return;
    }

    let configJson: string;
    if (isHF) {
      if (hfLevels.length === 0) {
        setError('Добавьте хотя бы один уровень');
        return;
      }
      configJson = JSON.stringify({
        symbol: symbol.replace(/\s+/g, '').toUpperCase(),
        levels: hfLevels,
        takeProfitPercent: Number(hfForm.takeProfitPercent),
        stopLossPercent: Number(hfForm.stopLossPercent),
        secondsBeforeFunding: Number(hfForm.secondsBeforeFunding),
        closeAfterMinutes: Number(hfForm.closeAfterMinutes),
        maxCycles: Number(hfForm.maxCycles),
        enableLong: hfForm.enableLong,
        minFundingLong: Number(hfForm.minFundingLong),
        enableShort: hfForm.enableShort,
        minFundingShort: Number(hfForm.minFundingShort),
      });
    } else {
      configJson = JSON.stringify({
        indicatorType: mgForm.indicatorType,
        indicatorLength: Number(mgForm.indicatorLength),
        candleCount: Number(mgForm.candleCount),
        offsetPercent: Number(mgForm.offsetPercent),
        takeProfitPercent: Number(mgForm.takeProfitPercent),
        stopLossPercent: Number(mgForm.stopLossPercent),
        symbol: symbol.replace(/\s+/g, '').toUpperCase(),
        timeframe: mgForm.timeframe,
        onlyLong: mgForm.onlyLong,
        onlyShort: mgForm.onlyShort,
      });
    }

    mutation.mutate({ name, configJson });
  };

  const inputCls =
    'w-full bg-bg-tertiary border border-border rounded-lg px-3 py-2 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors';
  const labelCls = 'block text-xs font-medium text-text-secondary mb-1';

  const addHfLevel = () =>
    setHfLevels((prev) => [...prev, { offsetPercent: 1.5, sizeUsdt: 50 }]);
  const removeHfLevel = (i: number) =>
    setHfLevels((prev) => prev.filter((_, idx) => idx !== i));
  const updateHfLevel = (i: number, field: keyof HFLevel, value: number) =>
    setHfLevels((prev) => prev.map((l, idx) => idx === i ? { ...l, [field]: value } : l));

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm">
      <div className="bg-bg-secondary rounded-xl border border-border w-full max-w-lg max-h-[90vh] overflow-y-auto shadow-2xl">
        <div className="flex items-center justify-between px-6 py-4 border-b border-border">
          <div>
            <h2 className="text-base font-semibold text-text-primary">Редактировать бота</h2>
            <p className="text-xs text-text-secondary mt-0.5">
              {strategy.accountName} ({strategy.exchange}) · {strategy.type}
            </p>
          </div>
          <button
            onClick={onClose}
            className="text-text-secondary hover:text-text-primary transition-colors"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <div className="px-6 py-4 space-y-4">
          {error && (
            <div className="text-sm text-accent-red bg-accent-red/10 rounded-lg px-3 py-2">
              {error}
            </div>
          )}

          {/* Name */}
          <div>
            <label className={labelCls}>Название бота *</label>
            <input
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              className={inputCls}
            />
          </div>

          <div className="border-t border-border pt-4">
            <p className="text-xs font-semibold text-text-secondary uppercase tracking-widest mb-3">
              Параметры торговли
            </p>
          </div>

          {/* Symbol */}
          <div>
            <label className={labelCls}>Символ *</label>
            <SearchableSelect
              value={symbol}
              onChange={(val) => setSymbol(val)}
              options={symbolOptions}
              placeholder="Выберите символ"
              isLoading={symbolsLoading}
              className={inputCls}
            />
          </div>

          {isHF ? (
            <>
              {/* Levels table */}
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
                      {hfLevels.map((lvl, i) => (
                        <tr key={i} className="border-t border-border/50">
                          <td className="px-3 py-1.5">
                            <input
                              type="number"
                              step="0.1"
                              value={lvl.offsetPercent}
                              onChange={(e) => updateHfLevel(i, 'offsetPercent', Number(e.target.value))}
                              className="w-full bg-transparent text-text-primary focus:outline-none focus:text-accent-blue"
                            />
                          </td>
                          <td className="px-3 py-1.5">
                            <input
                              type="number"
                              step="1"
                              value={lvl.sizeUsdt}
                              onChange={(e) => updateHfLevel(i, 'sizeUsdt', Number(e.target.value))}
                              className="w-full bg-transparent text-text-primary focus:outline-none focus:text-accent-blue"
                            />
                          </td>
                          <td className="px-2 py-1.5 text-center">
                            <button
                              onClick={() => removeHfLevel(i)}
                              className="text-text-secondary/40 hover:text-accent-red transition-colors"
                              title="Удалить уровень"
                            >
                              <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                                <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                              </svg>
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                <button
                  onClick={addHfLevel}
                  className="mt-2 text-xs text-accent-blue hover:text-accent-blue/80 transition-colors"
                >
                  + Добавить уровень
                </button>
              </div>

              {/* TP / SL */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Take Profit %</label>
                  <input
                    type="number"
                    step="0.1"
                    value={hfForm.takeProfitPercent}
                    onChange={(e) => setHfForm({ ...hfForm, takeProfitPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
                <div>
                  <label className={labelCls}>Stop Loss %</label>
                  <input
                    type="number"
                    step="0.1"
                    value={hfForm.stopLossPercent}
                    onChange={(e) => setHfForm({ ...hfForm, stopLossPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* Timing */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Секунд до фандинга</label>
                  <input
                    type="number"
                    value={hfForm.secondsBeforeFunding}
                    onChange={(e) => setHfForm({ ...hfForm, secondsBeforeFunding: e.target.value })}
                    className={inputCls}
                  />
                </div>
                <div>
                  <label className={labelCls}>Закрыть через (мин)</label>
                  <input
                    type="number"
                    value={hfForm.closeAfterMinutes}
                    onChange={(e) => setHfForm({ ...hfForm, closeAfterMinutes: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* Cycles */}
              <div>
                <label className={labelCls}>Макс. циклов <span className="font-normal">(0 = бесконечно)</span></label>
                <input
                  type="number"
                  min="0"
                  value={hfForm.maxCycles}
                  onChange={(e) => setHfForm({ ...hfForm, maxCycles: e.target.value })}
                  className={inputCls}
                />
              </div>

              {/* Direction thresholds */}
              <div className="col-span-2 border border-dark-border rounded-lg p-3 space-y-3">
                <p className="text-xs text-text-secondary font-medium uppercase tracking-wide">Направления торговли</p>
                <div className="grid grid-cols-2 gap-4">
                  <div>
                    <label className="flex items-center gap-2 mb-1">
                      <input
                        type="checkbox"
                        checked={hfForm.enableLong}
                        onChange={(e) => setHfForm({ ...hfForm, enableLong: e.target.checked })}
                        className="rounded border-dark-border"
                      />
                      <span className="text-sm text-text-primary font-medium">Long</span>
                      <span className="text-xs text-text-secondary">(фандинг &lt; 0)</span>
                    </label>
                    <input
                      type="number"
                      step="0.1"
                      min="0"
                      placeholder="Мин. фандинг %"
                      value={hfForm.minFundingLong}
                      onChange={(e) => setHfForm({ ...hfForm, minFundingLong: e.target.value })}
                      disabled={!hfForm.enableLong}
                      className={inputCls + (!hfForm.enableLong ? ' opacity-50' : '')}
                    />
                    <p className="text-xs text-text-secondary mt-0.5">Торгуем если фандинг ≤ −{hfForm.minFundingLong}%</p>
                  </div>
                  <div>
                    <label className="flex items-center gap-2 mb-1">
                      <input
                        type="checkbox"
                        checked={hfForm.enableShort}
                        onChange={(e) => setHfForm({ ...hfForm, enableShort: e.target.checked })}
                        className="rounded border-dark-border"
                      />
                      <span className="text-sm text-text-primary font-medium">Short</span>
                      <span className="text-xs text-text-secondary">(фандинг &gt; 0)</span>
                    </label>
                    <input
                      type="number"
                      step="0.1"
                      min="0"
                      placeholder="Мин. фандинг %"
                      value={hfForm.minFundingShort}
                      onChange={(e) => setHfForm({ ...hfForm, minFundingShort: e.target.value })}
                      disabled={!hfForm.enableShort}
                      className={inputCls + (!hfForm.enableShort ? ' opacity-50' : '')}
                    />
                    <p className="text-xs text-text-secondary mt-0.5">Торгуем если фандинг ≥ +{hfForm.minFundingShort}%</p>
                  </div>
                </div>
              </div>
            </>
          ) : (
            <>
              {/* Timeframe */}
              <div>
                <label className={labelCls}>Таймфрейм</label>
                <select
                  value={mgForm.timeframe}
                  onChange={(e) => setMgForm({ ...mgForm, timeframe: e.target.value })}
                  className={inputCls}
                >
                  <option value="1m">1m</option>
                  <option value="5m">5m</option>
                  <option value="15m">15m</option>
                  <option value="30m">30m</option>
                  <option value="1h">1h</option>
                  <option value="4h">4h</option>
                  <option value="1d">1D</option>
                </select>
              </div>

              {/* Indicator Type + Length */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Индикатор</label>
                  <select
                    value={mgForm.indicatorType}
                    onChange={(e) => setMgForm({ ...mgForm, indicatorType: e.target.value })}
                    className={inputCls}
                  >
                    <option value="EMA">EMA</option>
                    <option value="SMA">SMA</option>
                  </select>
                </div>
                <div>
                  <label className={labelCls}>Период</label>
                  <input
                    type="number"
                    value={mgForm.indicatorLength}
                    onChange={(e) => setMgForm({ ...mgForm, indicatorLength: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* Candle Count + Offset */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Кол-во свечей</label>
                  <input
                    type="number"
                    value={mgForm.candleCount}
                    onChange={(e) => setMgForm({ ...mgForm, candleCount: e.target.value })}
                    className={inputCls}
                  />
                </div>
                <div>
                  <label className={labelCls}>Offset %</label>
                  <input
                    type="number"
                    value={mgForm.offsetPercent}
                    onChange={(e) => setMgForm({ ...mgForm, offsetPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* TP / SL */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Take Profit %</label>
                  <input
                    type="number"
                    value={mgForm.takeProfitPercent}
                    onChange={(e) => setMgForm({ ...mgForm, takeProfitPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
                <div>
                  <label className={labelCls}>Stop Loss %</label>
                  <input
                    type="number"
                    value={mgForm.stopLossPercent}
                    onChange={(e) => setMgForm({ ...mgForm, stopLossPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* Direction filter */}
              <div className="flex items-center gap-4">
                <label className="flex items-center gap-2 cursor-pointer select-none">
                  <input
                    type="checkbox"
                    checked={mgForm.onlyLong}
                    onChange={(e) => setMgForm({ ...mgForm, onlyLong: e.target.checked, onlyShort: e.target.checked ? false : mgForm.onlyShort })}
                    className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-green focus:ring-accent-green/50 cursor-pointer"
                  />
                  <span className="text-sm font-medium text-accent-green">Только Long</span>
                </label>
                <label className="flex items-center gap-2 cursor-pointer select-none">
                  <input
                    type="checkbox"
                    checked={mgForm.onlyShort}
                    onChange={(e) => setMgForm({ ...mgForm, onlyShort: e.target.checked, onlyLong: e.target.checked ? false : mgForm.onlyLong })}
                    className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-red focus:ring-accent-red/50 cursor-pointer"
                  />
                  <span className="text-sm font-medium text-accent-red">Только Short</span>
                </label>
              </div>
            </>
          )}
        </div>

        <div className="flex justify-end gap-3 px-6 py-4 border-t border-border">
          <button
            onClick={onClose}
            className="px-4 py-2 text-sm font-medium text-text-secondary hover:text-text-primary transition-colors"
          >
            Отмена
          </button>
          <button
            onClick={handleSubmit}
            disabled={mutation.isPending}
            className="px-4 py-2 text-sm font-medium bg-accent-blue hover:bg-accent-blue/90 text-white rounded-lg transition-colors shadow-md shadow-accent-blue/20 disabled:opacity-50 disabled:shadow-none"
          >
            {mutation.isPending ? 'Сохранение...' : 'Сохранить'}
          </button>
        </div>
      </div>
    </div>
  );
}

/* ── Chart Modal ───────────────────────────────────────── */

function ChartModal({
  strategy,
  onClose,
}: {
  strategy: Strategy;
  onClose: () => void;
}) {
  const cfg = parseJson(strategy.configJson) || {};

  const { data: chartData, isLoading } = useQuery<{
    candles: CandleData[];
    indicatorValues: IndicatorDataPoint[];
  }>({
    queryKey: ['strategy-chart', strategy.id],
    queryFn: () =>
      api.get(`/strategies/${strategy.id}/chart?limit=300`).then((r) => r.data),
    refetchInterval: cfg.timeframe === '1m' ? 5000 : cfg.timeframe === '5m' ? 10000 : 60000,
  });

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm">
      <div className="bg-bg-secondary rounded-xl border border-border w-full max-w-5xl shadow-2xl">
        <div className="flex items-center justify-between px-6 py-4 border-b border-border">
          <div>
            <h2 className="text-base font-semibold text-text-primary">
              {cfg.symbol || strategy.name}
              <span className="ml-2 text-xs text-text-secondary font-normal">
                {cfg.timeframe} · {cfg.indicatorType || 'EMA'}{cfg.indicatorLength || 50}
              </span>
            </h2>
            <p className="text-xs text-text-secondary mt-0.5">
              {strategy.accountName} ({strategy.exchange})
            </p>
          </div>
          <button
            onClick={onClose}
            className="text-text-secondary hover:text-text-primary transition-colors"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <div className="p-4">
          <CandlestickChart
            data={chartData?.candles || []}
            isLoading={isLoading}
            indicatorData={chartData?.indicatorValues}
            indicatorColor={cfg.indicatorType === 'SMA' ? '#3b82f6' : '#f59e0b'}
          />
        </div>
      </div>
    </div>
  );
}

/* ── Log Modal ────────────────────────────────────────── */

interface StrategyLogEntry {
  id: string;
  level: string;
  message: string;
  createdAt: string;
}

function LogModal({
  strategy,
  onClose,
}: {
  strategy: Strategy;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const logEndRef = useRef<HTMLDivElement>(null);

  const { data: logs, isLoading } = useQuery<StrategyLogEntry[]>({
    queryKey: ['strategy-logs', strategy.id],
    queryFn: () =>
      api.get(`/strategies/${strategy.id}/logs?limit=500`).then((r) => r.data),
    refetchInterval: 3000,
  });

  const clearMutation = useMutation({
    mutationFn: () => api.delete(`/strategies/${strategy.id}/logs`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['strategy-logs', strategy.id] }),
  });

  // Auto-scroll to bottom when new logs appear
  useEffect(() => {
    if (logEndRef.current) {
      logEndRef.current.scrollIntoView({ behavior: 'smooth' });
    }
  }, [logs?.length]);

  const levelColors: Record<string, string> = {
    Info: 'text-accent-blue',
    Warning: 'text-accent-yellow',
    Error: 'text-accent-red',
  };

  const levelBg: Record<string, string> = {
    Info: 'bg-accent-blue/10',
    Warning: 'bg-accent-yellow/10',
    Error: 'bg-accent-red/10',
  };

  // Show logs oldest first (API returns newest first)
  const sortedLogs = logs ? [...logs].reverse() : [];

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm">
      <div className="bg-bg-secondary rounded-xl border border-border w-full max-w-4xl shadow-2xl flex flex-col max-h-[85vh]">
        <div className="flex items-center justify-between px-6 py-4 border-b border-border shrink-0">
          <div>
            <h2 className="text-base font-semibold text-text-primary">
              Логи: {strategy.name}
            </h2>
            <p className="text-xs text-text-secondary mt-0.5">
              {strategy.accountName} ({strategy.exchange})
              {logs && <span className="ml-2">{logs.length} записей</span>}
            </p>
          </div>
          <div className="flex items-center gap-3">
            <button
              onClick={() => {
                if (confirm('Очистить все логи этого бота?'))
                  clearMutation.mutate();
              }}
              disabled={clearMutation.isPending || !logs?.length}
              className="px-3 py-1.5 text-xs font-medium bg-accent-red/10 text-accent-red rounded-lg hover:bg-accent-red/20 transition-colors disabled:opacity-50"
            >
              {clearMutation.isPending ? 'Очистка...' : 'Очистить'}
            </button>
            <button
              onClick={onClose}
              className="text-text-secondary hover:text-text-primary transition-colors"
            >
              <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto p-4 font-mono text-xs">
          {isLoading ? (
            <div className="text-center py-8 text-text-secondary">Загрузка логов...</div>
          ) : sortedLogs.length === 0 ? (
            <div className="text-center py-8 text-text-secondary">Нет логов. Запустите бота для начала записи.</div>
          ) : (
            <div className="space-y-0.5">
              {sortedLogs.map((log) => (
                <div
                  key={log.id}
                  className={`flex gap-2 px-2 py-1 rounded ${levelBg[log.level] || ''}`}
                >
                  <span className="text-text-secondary shrink-0 w-[140px]">
                    {new Date(log.createdAt).toLocaleString('ru-RU', {
                      day: '2-digit',
                      month: '2-digit',
                      hour: '2-digit',
                      minute: '2-digit',
                      second: '2-digit',
                    })}
                  </span>
                  <span className={`shrink-0 w-[52px] font-semibold ${levelColors[log.level] || 'text-text-secondary'}`}>
                    [{log.level}]
                  </span>
                  <span className="text-text-primary break-all">{log.message}</span>
                </div>
              ))}
              <div ref={logEndRef} />
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
