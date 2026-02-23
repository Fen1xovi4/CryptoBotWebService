import { useState, useEffect, useRef, useCallback } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import api from '../api/client';
import Header from '../components/Layout/Header';
import StatusBadge from '../components/ui/StatusBadge';

interface Strategy {
  id: string;
  accountId: string;
  workspaceId: string | null;
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
  drawdownFixedPnl: number;
  drawdownRealizedPnl: number;
}

interface WorkspaceStats {
  workspaceId: string | null;
  totalBots: number;
  activeBots: number;
  totalTrades: number;
  pnl: number;
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
  drawdownFixedPnl: 0,
  drawdownRealizedPnl: 0,
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
  });

  const { data: allStats } = useQuery<WorkspaceStats[]>({
    queryKey: ['strategy-stats'],
    queryFn: () => api.get('/strategies/stats').then((r) => r.data),
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
        <StatCard label="P&L" value={`$${currentStats.pnl.toFixed(2)}`} accent="red" />
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
          </select>
        </div>

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

        {/* PNL */}
        <div className="w-full border-t border-border/50 mt-1 pt-3 flex flex-wrap items-center gap-4">
          <div className="flex items-center gap-2">
            <span className="text-xs text-text-secondary">Зафикс. PNL:</span>
            <span className={`text-sm font-semibold ${localConfig.drawdownFixedPnl < 0 ? 'text-accent-red' : 'text-accent-green'}`}>
              ${localConfig.drawdownFixedPnl.toFixed(2)}
            </span>
          </div>

          <div className="flex items-center gap-2">
            <span className="text-xs text-text-secondary">Реализ. PNL:</span>
            <span className={`text-sm font-semibold ${localConfig.drawdownRealizedPnl < 0 ? 'text-accent-red' : 'text-accent-green'}`}>
              ${localConfig.drawdownRealizedPnl.toFixed(2)}
            </span>
          </div>

          <button
            onClick={() => {
              if (confirm('Сбросить все PNL значения на 0?')) {
                updateConfig({ drawdownFixedPnl: 0, drawdownRealizedPnl: 0 });
              }
            }}
            title="Сбросить PNL"
            className="px-2.5 py-1 text-xs font-medium bg-bg-tertiary text-text-secondary border border-border rounded-lg hover:text-text-primary hover:border-accent-blue transition-colors"
          >
            Сбросить PNL
          </button>
        </div>
      </div>

      {/* Bots Table Header */}
      <div className="flex items-center justify-between mb-3">
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

      {/* Bots Table */}
      <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
        <table className="w-full">
          <thead>
            <tr className="text-xs text-text-secondary border-b border-border">
              <th className="text-left px-5 py-2.5 font-medium">Название</th>
              <th className="text-left px-5 py-2.5 font-medium">Пара</th>
              <th className="text-left px-5 py-2.5 font-medium">Аккаунт</th>
              <th className="text-left px-5 py-2.5 font-medium">Конфиг</th>
              <th className="text-center px-5 py-2.5 font-medium">Прогресс</th>
              <th className="text-center px-5 py-2.5 font-medium">Убытки подряд</th>
              <th className="text-left px-5 py-2.5 font-medium">Статус</th>
              <th className="text-right px-5 py-2.5 font-medium">Действия</th>
            </tr>
          </thead>
          <tbody>
            {isLoading ? (
              <tr>
                <td colSpan={8} className="px-5 py-8 text-center text-text-secondary text-sm">
                  Загрузка...
                </td>
              </tr>
            ) : filteredStrategies.length === 0 ? (
              <tr>
                <td colSpan={8} className="px-5 py-8 text-center text-text-secondary text-sm">
                  Нет ботов в этом пространстве
                </td>
              </tr>
            ) : (
              filteredStrategies.map((s) => {
                const cfg = parseJson(s.configJson);
                const state = parseJson(s.stateJson);
                const consecutiveLosses = state?.consecutiveLosses ?? 0;
                return (
                  <tr
                    key={s.id}
                    className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors"
                  >
                    <td className="px-5 py-3">
                      <div className="text-sm font-medium">{s.name}</div>
                    </td>
                    <td className="px-5 py-3 text-sm font-mono">
                      {cfg?.symbol || '—'}
                      {cfg?.timeframe && (
                        <span className="ml-1.5 text-text-secondary">{cfg.timeframe}</span>
                      )}
                    </td>
                    <td className="px-5 py-3 text-sm text-text-secondary">
                      {s.accountName} ({s.exchange})
                    </td>
                    <td className="px-5 py-3">
                      {cfg && (
                        <div className="text-xs text-text-secondary space-y-0.5">
                          <div>
                            {cfg.indicatorType || 'EMA'}
                            {cfg.indicatorLength || 50} / {cfg.candleCount || 50} свечей
                          </div>
                          <div>
                            TP {cfg.takeProfitPercent ?? 3}% / SL {cfg.stopLossPercent ?? 3}%
                          </div>
                          {cfg.offsetPercent > 0 && <div>Offset {cfg.offsetPercent}%</div>}
                        </div>
                      )}
                    </td>
                    <td className="px-5 py-3 text-center">
                      {s.status === 'Running' && cfg?.candleCount ? (
                        <div className="inline-flex flex-col items-center gap-0.5">
                          {!state?.openLong && !localConfig.onlyShort && (
                            <div className="flex items-center gap-1">
                              <span className="text-[10px] text-accent-green font-medium">L</span>
                              <span className={`text-sm font-semibold ${(state?.longCounter ?? 0) > 0 ? 'text-accent-green' : 'text-text-secondary'}`}>
                                {state?.longCounter ?? 0}/{cfg.candleCount}
                              </span>
                            </div>
                          )}
                          {!state?.openShort && !localConfig.onlyLong && (
                            <div className="flex items-center gap-1">
                              <span className="text-[10px] text-accent-red font-medium">S</span>
                              <span className={`text-sm font-semibold ${(state?.shortCounter ?? 0) > 0 ? 'text-accent-red' : 'text-text-secondary'}`}>
                                {state?.shortCounter ?? 0}/{cfg.candleCount}
                              </span>
                            </div>
                          )}
                          {state?.openLong && (
                            <span className="text-[10px] text-accent-green font-medium">Long открыт</span>
                          )}
                          {state?.openShort && (
                            <span className="text-[10px] text-accent-red font-medium">Short открыт</span>
                          )}
                        </div>
                      ) : (
                        <span className="text-text-secondary text-xs">—</span>
                      )}
                    </td>
                    <td className="px-5 py-3 text-center">
                      <div className="inline-flex items-center gap-1.5">
                        <span className={`text-sm font-semibold ${consecutiveLosses > 0 ? 'text-accent-red' : 'text-text-secondary'}`}>
                          {consecutiveLosses}
                        </span>
                        {consecutiveLosses > 0 && (
                          <button
                            onClick={() => {
                              if (confirm(`Сбросить убытки (${consecutiveLosses}) на 0?`))
                                resetLossesMutation.mutate(s.id);
                            }}
                            title="Сбросить убытки"
                            className="p-0.5 text-text-secondary hover:text-accent-blue transition-colors"
                          >
                            <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                            </svg>
                          </button>
                        )}
                      </div>
                    </td>
                    <td className="px-5 py-3">
                      <StatusBadge status={s.status} />
                    </td>
                    <td className="px-5 py-3 text-right">
                      <div className="inline-flex items-center gap-2 flex-wrap justify-end">
                        {s.status === 'Running' ? (
                          <button
                            onClick={() => stopMutation.mutate(s.id)}
                            className="px-3 py-1.5 text-xs font-medium bg-accent-red/10 text-accent-red rounded-lg hover:bg-accent-red/20 transition-colors"
                          >
                            Стоп
                          </button>
                        ) : (
                          <button
                            onClick={() => startMutation.mutate(s.id)}
                            className="px-3 py-1.5 text-xs font-medium bg-accent-green/10 text-accent-green rounded-lg hover:bg-accent-green/20 transition-colors"
                          >
                            Старт
                          </button>
                        )}
                        {s.status !== 'Running' && (
                          <button
                            onClick={() => setEditingStrategy(s)}
                            className="px-3 py-1.5 text-xs font-medium bg-accent-blue/10 text-accent-blue rounded-lg hover:bg-accent-blue/20 transition-colors"
                          >
                            Ред.
                          </button>
                        )}
                        {(state?.openLong || state?.openShort) && (
                          <button
                            onClick={() => {
                              const dir = state?.openLong ? 'Long' : 'Short';
                              const entry = state?.openLong?.entryPrice ?? state?.openShort?.entryPrice;
                              if (confirm(`Закрыть ${dir} позицию (вход: ${entry}) по рынку?`))
                                closePositionMutation.mutate(s.id);
                            }}
                            disabled={closePositionMutation.isPending}
                            className="px-3 py-1.5 text-xs font-medium bg-accent-yellow/10 text-accent-yellow rounded-lg hover:bg-accent-yellow/20 transition-colors disabled:opacity-50"
                          >
                            {closePositionMutation.isPending ? '...' : 'Закрыть позицию'}
                          </button>
                        )}
                        <button
                          onClick={() => {
                            if (confirm('Удалить этого бота?')) deleteMutation.mutate(s.id);
                          }}
                          className="px-3 py-1.5 text-xs font-medium bg-accent-red/10 text-accent-red rounded-lg hover:bg-accent-red/20 transition-colors"
                        >
                          Удалить
                        </button>
                      </div>
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>

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

  const [form, setForm] = useState({
    accountId: '',
    name: '',
    symbol: 'BTCUSDT',
    timeframe: '1h',
    indicatorType: 'EMA',
    indicatorLength: '50',
    candleCount: '50',
    offsetPercent: '0',
    takeProfitPercent: '3',
    stopLossPercent: '3',
  });

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
    if (!form.accountId || !form.name || !form.symbol) {
      setError('Заполните все обязательные поля');
      return;
    }

    const configJson = JSON.stringify({
      indicatorType: form.indicatorType,
      indicatorLength: Number(form.indicatorLength),
      candleCount: Number(form.candleCount),
      offsetPercent: Number(form.offsetPercent),
      takeProfitPercent: Number(form.takeProfitPercent),
      stopLossPercent: Number(form.stopLossPercent),
      symbol: form.symbol.toUpperCase(),
      timeframe: form.timeframe,
    });

    mutation.mutate({
      accountId: form.accountId,
      workspaceId,
      name: form.name,
      type: strategyType,
      configJson,
    });
  };

  const activeAccounts = accounts?.filter((a) => a.isActive) || [];
  const inputCls =
    'w-full bg-bg-tertiary border border-border rounded-lg px-3 py-2 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors';
  const labelCls = 'block text-xs font-medium text-text-secondary mb-1';

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
            <svg
              className="w-5 h-5"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              strokeWidth={2}
            >
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
              value={form.accountId}
              onChange={(e) => setForm({ ...form, accountId: e.target.value })}
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
              value={form.name}
              onChange={(e) => setForm({ ...form, name: e.target.value })}
              placeholder={`Мой ${strategyType} бот`}
              className={inputCls}
            />
          </div>

          <div className="border-t border-border pt-4">
            <p className="text-xs font-semibold text-text-secondary uppercase tracking-widest mb-3">
              Параметры торговли
            </p>
          </div>

          {/* Symbol + Timeframe */}
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className={labelCls}>Символ *</label>
              <input
                type="text"
                value={form.symbol}
                onChange={(e) => setForm({ ...form, symbol: e.target.value })}
                placeholder="BTCUSDT"
                className={inputCls}
              />
            </div>
            <div>
              <label className={labelCls}>Таймфрейм</label>
              <select
                value={form.timeframe}
                onChange={(e) => setForm({ ...form, timeframe: e.target.value })}
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
          </div>

          {/* Indicator Type + Length */}
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className={labelCls}>Индикатор</label>
              <select
                value={form.indicatorType}
                onChange={(e) => setForm({ ...form, indicatorType: e.target.value })}
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
                value={form.indicatorLength}
                onChange={(e) => setForm({ ...form, indicatorLength: e.target.value })}
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
                value={form.candleCount}
                onChange={(e) => setForm({ ...form, candleCount: e.target.value })}
                className={inputCls}
              />
            </div>
            <div>
              <label className={labelCls}>Offset %</label>
              <input
                type="number"
                value={form.offsetPercent}
                onChange={(e) => setForm({ ...form, offsetPercent: e.target.value })}
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
                value={form.takeProfitPercent}
                onChange={(e) => setForm({ ...form, takeProfitPercent: e.target.value })}
                className={inputCls}
              />
            </div>
            <div>
              <label className={labelCls}>Stop Loss %</label>
              <input
                type="number"
                value={form.stopLossPercent}
                onChange={(e) => setForm({ ...form, stopLossPercent: e.target.value })}
                className={inputCls}
              />
            </div>
          </div>

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

  const [form, setForm] = useState({
    name: strategy.name,
    symbol: cfg.symbol || 'BTCUSDT',
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
    if (!form.name || !form.symbol) {
      setError('Заполните все обязательные поля');
      return;
    }

    const configJson = JSON.stringify({
      indicatorType: form.indicatorType,
      indicatorLength: Number(form.indicatorLength),
      candleCount: Number(form.candleCount),
      offsetPercent: Number(form.offsetPercent),
      takeProfitPercent: Number(form.takeProfitPercent),
      stopLossPercent: Number(form.stopLossPercent),
      symbol: form.symbol.toUpperCase(),
      timeframe: form.timeframe,
      onlyLong: form.onlyLong,
      onlyShort: form.onlyShort,
    });

    mutation.mutate({ name: form.name, configJson });
  };

  const inputCls =
    'w-full bg-bg-tertiary border border-border rounded-lg px-3 py-2 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors';
  const labelCls = 'block text-xs font-medium text-text-secondary mb-1';

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm">
      <div className="bg-bg-secondary rounded-xl border border-border w-full max-w-lg max-h-[90vh] overflow-y-auto shadow-2xl">
        <div className="flex items-center justify-between px-6 py-4 border-b border-border">
          <div>
            <h2 className="text-base font-semibold text-text-primary">Редактировать бота</h2>
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
              value={form.name}
              onChange={(e) => setForm({ ...form, name: e.target.value })}
              className={inputCls}
            />
          </div>

          <div className="border-t border-border pt-4">
            <p className="text-xs font-semibold text-text-secondary uppercase tracking-widest mb-3">
              Параметры торговли
            </p>
          </div>

          {/* Symbol + Timeframe */}
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className={labelCls}>Символ *</label>
              <input
                type="text"
                value={form.symbol}
                onChange={(e) => setForm({ ...form, symbol: e.target.value })}
                className={inputCls}
              />
            </div>
            <div>
              <label className={labelCls}>Таймфрейм</label>
              <select
                value={form.timeframe}
                onChange={(e) => setForm({ ...form, timeframe: e.target.value })}
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
          </div>

          {/* Indicator Type + Length */}
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className={labelCls}>Индикатор</label>
              <select
                value={form.indicatorType}
                onChange={(e) => setForm({ ...form, indicatorType: e.target.value })}
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
                value={form.indicatorLength}
                onChange={(e) => setForm({ ...form, indicatorLength: e.target.value })}
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
                value={form.candleCount}
                onChange={(e) => setForm({ ...form, candleCount: e.target.value })}
                className={inputCls}
              />
            </div>
            <div>
              <label className={labelCls}>Offset %</label>
              <input
                type="number"
                value={form.offsetPercent}
                onChange={(e) => setForm({ ...form, offsetPercent: e.target.value })}
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
                value={form.takeProfitPercent}
                onChange={(e) => setForm({ ...form, takeProfitPercent: e.target.value })}
                className={inputCls}
              />
            </div>
            <div>
              <label className={labelCls}>Stop Loss %</label>
              <input
                type="number"
                value={form.stopLossPercent}
                onChange={(e) => setForm({ ...form, stopLossPercent: e.target.value })}
                className={inputCls}
              />
            </div>
          </div>

          {/* Direction filter */}
          <div className="flex items-center gap-4">
            <label className="flex items-center gap-2 cursor-pointer select-none">
              <input
                type="checkbox"
                checked={form.onlyLong}
                onChange={(e) => setForm({ ...form, onlyLong: e.target.checked, onlyShort: e.target.checked ? false : form.onlyShort })}
                className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-green focus:ring-accent-green/50 cursor-pointer"
              />
              <span className="text-sm font-medium text-accent-green">Только Long</span>
            </label>
            <label className="flex items-center gap-2 cursor-pointer select-none">
              <input
                type="checkbox"
                checked={form.onlyShort}
                onChange={(e) => setForm({ ...form, onlyShort: e.target.checked, onlyLong: e.target.checked ? false : form.onlyLong })}
                className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-red focus:ring-accent-red/50 cursor-pointer"
              />
              <span className="text-sm font-medium text-accent-red">Только Short</span>
            </label>
          </div>
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
