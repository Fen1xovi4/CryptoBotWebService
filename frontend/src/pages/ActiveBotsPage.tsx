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
  // HuntingFunding workspace settings
  fundingRateMin: number;
  fundingRateMax: number;
  // SmaDca workspace settings
  timerEnabled: boolean;
  timerExpiresAt: string | null; // ISO UTC
  // FundingClaim workspace settings
  fcSizeUsdt: number;
  fcMinFundingRatePercent: number;
  fcMaxFundingRatePercent: number;
  fcStopLossPercent: number;
  fcLeverage: number;
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
  fundingRateMin: 1.0,
  fundingRateMax: 2.0,
  timerEnabled: false,
  timerExpiresAt: null,
  fcSizeUsdt: 100,
  fcMinFundingRatePercent: 0.3,
  fcMaxFundingRatePercent: 2.0,
  fcStopLossPercent: 1.5,
  fcLeverage: 3,
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

function formatRemaining(iso: string | null, nowMs: number): { text: string; expired: boolean } {
  if (!iso) return { text: 'не установлен', expired: true };
  const t = new Date(iso).getTime();
  if (isNaN(t)) return { text: 'не установлен', expired: true };
  let diff = Math.floor((t - nowMs) / 1000);
  if (diff <= 0) return { text: 'истёк — новые позиции не открываются', expired: true };
  const h = Math.floor(diff / 3600); diff -= h * 3600;
  const m = Math.floor(diff / 60); const s = diff - m * 60;
  return { text: `осталось ${h}ч ${m}м ${s}с`, expired: false };
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

  // Timer UI (SmaDca): duration inputs + live countdown
  const [timerHours, setTimerHours] = useState(24);
  const [timerMinutes, setTimerMinutes] = useState(0);
  const [timerSeconds, setTimerSeconds] = useState(0);
  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNowMs(Date.now()), 1000);
    return () => clearInterval(id);
  }, []);

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

  const pauseMutation = useMutation({
    mutationFn: (id: string) => api.post(`/strategies/${id}/pause`),
    onSuccess: () => invalidateAll(queryClient),
    onError: (err: unknown) => {
      const e = err as { response?: { data?: { message?: string } } };
      alert(e.response?.data?.message || 'Не удалось поставить на паузу');
    },
  });

  const resumeMutation = useMutation({
    mutationFn: (id: string) => api.post(`/strategies/${id}/resume`),
    onSuccess: () => invalidateAll(queryClient),
    onError: (err: unknown) => {
      const e = err as { response?: { data?: { message?: string } } };
      alert(e.response?.data?.message || 'Не удалось возобновить');
    },
  });

  const updateGridFloatTiersMutation = useMutation({
    mutationFn: ({ id, tiers }: { id: string; tiers: GridFloatTier[] }) =>
      api.patch(`/strategies/${id}/grid-float/tiers`, { tiers }),
    onSuccess: () => invalidateAll(queryClient),
    onError: (err: unknown) => {
      const e = err as { response?: { data?: { message?: string } } };
      alert(e.response?.data?.message || 'Не удалось обновить ярусы');
    },
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
            <option value="SmaDca">SMA DCA</option>
            <option value="FundingClaim">FundingClaim</option>
            <option value="GridFloat">Grid Float</option>
            <option value="GridHedge">Grid Hedge</option>
          </select>
        </div>

        {activeWorkspace?.strategyType === 'SmaDca' ? (
          <>
            <div className="h-6 w-px bg-border" />
            <label className="flex items-center gap-2 cursor-pointer select-none">
              <input
                type="checkbox"
                checked={localConfig.timerEnabled}
                onChange={(e) => updateConfig({ timerEnabled: e.target.checked })}
                className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer"
              />
              <span className="text-sm font-medium text-text-secondary">Таймер</span>
            </label>
            {localConfig.timerEnabled && (() => {
              const { text, expired } = formatRemaining(localConfig.timerExpiresAt, nowMs);
              return (
                <div className="flex items-center gap-2">
                  <input
                    type="number"
                    min="0"
                    value={timerHours}
                    onChange={(e) => setTimerHours(Math.max(0, Number(e.target.value)))}
                    className="w-16 bg-bg-tertiary border border-border rounded-lg px-2 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
                  />
                  <span className="text-xs text-text-secondary">ч</span>
                  <input
                    type="number"
                    min="0"
                    max="59"
                    value={timerMinutes}
                    onChange={(e) => setTimerMinutes(Math.max(0, Math.min(59, Number(e.target.value))))}
                    className="w-16 bg-bg-tertiary border border-border rounded-lg px-2 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
                  />
                  <span className="text-xs text-text-secondary">м</span>
                  <input
                    type="number"
                    min="0"
                    max="59"
                    value={timerSeconds}
                    onChange={(e) => setTimerSeconds(Math.max(0, Math.min(59, Number(e.target.value))))}
                    className="w-16 bg-bg-tertiary border border-border rounded-lg px-2 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
                  />
                  <span className="text-xs text-text-secondary">с</span>
                  <button
                    onClick={() => {
                      const totalSec = timerHours * 3600 + timerMinutes * 60 + timerSeconds;
                      if (totalSec <= 0) return;
                      const iso = new Date(Date.now() + totalSec * 1000).toISOString();
                      updateConfig({ timerExpiresAt: iso });
                    }}
                    className="px-3 py-1.5 text-xs font-medium bg-accent-blue hover:bg-accent-blue/90 text-white rounded-lg transition-colors"
                  >
                    Установить
                  </button>
                  <span className={`text-xs italic ${expired ? 'text-accent-red' : 'text-text-secondary'}`}>
                    {text}
                  </span>
                </div>
              );
            })()}
            <div className="h-6 w-px bg-border" />
            <p className="text-sm text-text-secondary italic">
              Параметры SMA, DCA-шаг, множитель и размер входа задаются индивидуально в каждом боте.
            </p>
          </>
        ) : activeWorkspace?.strategyType === 'HuntingFunding' ? (
          <>
            <div className="h-6 w-px bg-border" />
            <div className="flex items-center gap-2">
              <span className="text-sm font-medium text-text-secondary">Диапазон фандинга %:</span>
              <input
                type="number"
                step="0.1"
                min="0"
                value={localConfig.fundingRateMin}
                onChange={(e) => updateConfig({ fundingRateMin: Number(e.target.value) })}
                className="w-20 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
              />
              <span className="text-sm text-text-secondary">—</span>
              <input
                type="number"
                step="0.1"
                min="0"
                value={localConfig.fundingRateMax}
                onChange={(e) => updateConfig({ fundingRateMax: Number(e.target.value) })}
                className="w-20 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
              />
              <span className="text-xs text-text-secondary italic">
                включительно. Тикеры с |funding| вне диапазона игнорируются.
              </span>
            </div>
            <div className="h-6 w-px bg-border" />
            <p className="text-sm text-text-secondary italic">
              Остальные настройки задаются индивидуально в каждом боте через уровни ордеров.
            </p>
          </>
        ) : activeWorkspace?.strategyType === 'FundingClaim' ? (
          <>
            <div className="h-6 w-px bg-border" />
            <div className="flex items-center gap-2">
              <span className="text-sm font-medium text-text-secondary">Размер позиции:</span>
              <input
                type="number"
                step="1"
                min="1"
                value={localConfig.fcSizeUsdt}
                onChange={(e) => updateConfig({ fcSizeUsdt: Number(e.target.value) })}
                className="w-24 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
              />
              <span className="text-xs text-text-secondary">USDT</span>
            </div>
            <div className="h-6 w-px bg-border" />
            <div className="flex items-center gap-2">
              <span className="text-sm font-medium text-text-secondary">Плечо:</span>
              <input
                type="number"
                step="1"
                min="1"
                max="125"
                value={localConfig.fcLeverage}
                onChange={(e) => updateConfig({ fcLeverage: Number(e.target.value) })}
                className="w-16 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
              />
              <span className="text-xs text-text-secondary">x</span>
            </div>
            <div className="h-6 w-px bg-border" />
            <div className="flex items-center gap-2">
              <span className="text-sm font-medium text-text-secondary">Фандинг:</span>
              <input
                type="number"
                step="0.01"
                min="0.01"
                value={localConfig.fcMinFundingRatePercent}
                onChange={(e) => updateConfig({ fcMinFundingRatePercent: Number(e.target.value) })}
                className="w-20 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
              />
              <span className="text-xs text-text-secondary">..</span>
              <input
                type="number"
                step="0.1"
                min="0"
                value={localConfig.fcMaxFundingRatePercent}
                onChange={(e) => updateConfig({ fcMaxFundingRatePercent: Number(e.target.value) })}
                className="w-20 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
              />
              <span className="text-xs text-text-secondary">%</span>
              <span className="text-xs text-text-secondary italic">
                диапазон |funding|. 0 = без верхней границы.
              </span>
            </div>
            <div className="h-6 w-px bg-border" />
            <div className="flex items-center gap-2">
              <span className="text-sm font-medium text-text-secondary">Стоп-лосс:</span>
              <input
                type="number"
                step="0.1"
                min="0"
                value={localConfig.fcStopLossPercent}
                onChange={(e) => updateConfig({ fcStopLossPercent: Number(e.target.value) })}
                className="w-20 bg-bg-tertiary border border-border rounded-lg px-3 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue transition-colors"
              />
              <span className="text-xs text-text-secondary">%</span>
              <span className="text-xs text-text-secondary italic">
                закрыть позицию при просадке. 0 = выключен.
              </span>
            </div>
            <div className="h-6 w-px bg-border" />
            <p className="text-sm text-text-secondary italic">
              Остальные настройки (авторотация, макс. циклов, проверка перед фандингом) задаются в каждом боте.
            </p>
          </>
        ) : activeWorkspace?.strategyType === 'GridFloat' ? (
          <>
            <div className="h-6 w-px bg-border" />
            <p className="text-sm text-text-secondary italic">
              Размер батча, диапазон, шаги DCA/TP, плечо и режим диапазона задаются в каждом боте.
            </p>
          </>
        ) : activeWorkspace?.strategyType === 'GridHedge' ? (
          <>
            <div className="h-6 w-px bg-border" />
            <p className="text-sm text-text-secondary italic">
              Режим (Spot+Futures vs Cross-Ticker), диапазон, шаги DCA/TP, размер хеджа (ratio × β) и плечи задаются в каждом боте.
            </p>
          </>
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

            if (s.type === 'SmaDca') {
              return (
                <SmaDcaCard
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

            if (s.type === 'FundingClaim') {
              return (
                <FundingClaimCard
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

            if (s.type === 'GridFloat') {
              return (
                <GridFloatCard
                  key={s.id}
                  s={s}
                  cfg={cfg}
                  state={state}
                  isRunning={isRunning}
                  onStart={() => startMutation.mutate(s.id)}
                  onStop={() => stopMutation.mutate(s.id)}
                  onPause={() => pauseMutation.mutate(s.id)}
                  onResume={() => resumeMutation.mutate(s.id)}
                  onUpdateTiers={(tiers) => updateGridFloatTiersMutation.mutate({ id: s.id, tiers })}
                  updateTiersPending={updateGridFloatTiersMutation.isPending}
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

            if (s.type === 'GridHedge') {
              return (
                <GridHedgeCard
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
  autoRotateTicker?: boolean;
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

/* ── FundingClaim types ──────────────────────────────────── */

interface FCConfig {
  symbol: string;
  maxCycles: number;
  autoRotateTicker: boolean;
  checkBeforeFundingMinutes: number;
}

interface FCStateData {
  phase: number; // 0=Idle, 1=InPosition
  direction: string | null;
  symbol: string | null;
  currentFundingRate: number | null;
  nextFundingTime: string | null;
  entryPrice: number | null;
  entryQuantity: number | null;
  entrySizeUsdt: number | null;
  positionOpenedAt: string | null;
  cycleCount: number;
  cycleTotalPnl: number;
  cycleTotalFundingPnl: number;
  currentCycleFundingPnl: number;
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
            {(cfg?.autoRotateTicker !== false) && (
              <span className="text-[10px] font-bold px-1.5 py-0.5 rounded bg-emerald-500/15 text-emerald-400">
                AUTO
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

/* ── FundingClaim Card ──────────────────────────────────── */

function FundingClaimCard({
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
  cfg: FCConfig | null;
  state: FCStateData | null;
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
  const fundingCountdown = useCountdown(state?.nextFundingTime);

  const borderAccent = isRunning
    ? phase === 1 ? 'border-l-accent-yellow' : 'border-l-accent-green'
    : 'border-l-border';

  // PnL calculation for InPosition phase
  let pnlUsd = 0;
  let pnlPct = 0;
  if (phase === 1 && state?.entryPrice && state?.lastPrice && state?.entrySizeUsdt) {
    const isLong = state.direction === 'Long';
    pnlPct = isLong
      ? (state.lastPrice - state.entryPrice) / state.entryPrice * 100
      : (state.entryPrice - state.lastPrice) / state.entryPrice * 100;
    pnlUsd = (pnlPct / 100) * state.entrySizeUsdt;
  }

  return (
    <div className={`bg-bg-secondary rounded-xl border border-border border-l-2 ${borderAccent} overflow-hidden transition-colors hover:border-text-secondary/20`}>
      {/* Header */}
      <div className="px-4 pt-3 pb-2 flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="text-sm font-mono font-semibold text-text-primary truncate">
              {state?.symbol || cfg?.symbol || '—'}
            </span>
            <span className="text-[10px] font-bold px-1.5 py-0.5 rounded bg-yellow-500/15 text-yellow-400">
              FC
            </span>
            {cfg?.autoRotateTicker && (
              <span className="text-[10px] font-bold px-1.5 py-0.5 rounded bg-emerald-500/15 text-emerald-400">
                AUTO
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
            -{cfg.checkBeforeFundingMinutes}мин
          </span>
          {cfg.maxCycles > 0 && (
            <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
              макс {cfg.maxCycles} цикл.
            </span>
          )}
        </div>
      )}

      {/* Divider */}
      <div className="border-t border-border/50" />

      {/* Phase content */}
      <div className="px-4 py-2.5 min-h-[52px] flex items-center">
        {!isRunning ? (
          <span className="text-text-secondary text-xs">Остановлен</span>
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
          <div className="w-full space-y-1">
            <div className="flex items-center gap-2 flex-wrap">
              {state?.direction && (
                <span className={`text-[10px] font-bold px-1.5 py-0.5 rounded ${state.direction === 'Long' ? 'bg-accent-green/15 text-accent-green' : 'bg-accent-red/15 text-accent-red'}`}>
                  {state.direction.toUpperCase()}
                </span>
              )}
              {state?.entryPrice != null && (
                <span className="text-[10px] text-text-secondary">
                  Вход: <span className="text-text-primary">${state.entryPrice.toFixed(2)}</span>
                </span>
              )}
              {state?.lastPrice != null && (
                <span className="text-[10px] text-text-secondary">
                  Цена: <span className="text-text-primary">${state.lastPrice.toFixed(2)}</span>
                </span>
              )}
              {state?.entryPrice != null && state?.lastPrice != null && state?.entrySizeUsdt != null && (
                <span className={`text-[10px] font-semibold ${pnlUsd >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
                  {pnlUsd >= 0 ? '+' : ''}${pnlUsd.toFixed(2)} ({pnlPct >= 0 ? '+' : ''}{pnlPct.toFixed(2)}%)
                </span>
              )}
            </div>
            {state?.currentCycleFundingPnl != null && (
              <div className={`text-[10px] font-medium ${state.currentCycleFundingPnl >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
                Funding PnL: {state.currentCycleFundingPnl >= 0 ? '+' : ''}${state.currentCycleFundingPnl.toFixed(4)}
              </div>
            )}
            {state?.currentFundingRate != null && (
              <div className="text-[11px] text-text-secondary flex items-center gap-2">
                <span>
                  Текущий funding: <span className={`font-mono font-semibold ${state.currentFundingRate >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
                    {state.currentFundingRate >= 0 ? '+' : ''}{(state.currentFundingRate * 100).toFixed(4)}%
                  </span>
                </span>
              </div>
            )}
            {state?.nextFundingTime && (
              <div className="text-[11px] text-text-secondary">
                До фандинга: <span className="font-mono text-text-primary">{fundingCountdown}</span>
              </div>
            )}
          </div>
        ) : (
          <span className="text-text-secondary text-xs">—</span>
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
        {state != null && (state.cycleTotalFundingPnl != null || state.currentCycleFundingPnl != null) && (() => {
          const totalF = (state.cycleTotalFundingPnl ?? 0) + (state.currentCycleFundingPnl ?? 0);
          return (
            <span className={`text-[10px] font-medium ${totalF >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
              Funding: {totalF >= 0 ? '+' : ''}${totalF.toFixed(4)}
            </span>
          );
        })()}
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

        {phase === 1 && isRunning && (
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

/* ── SMA DCA Card ──────────────────────────────────────── */

interface SmaDcaLevel {
  stepPercent: number;
  multiplier: number;
  count: number;
}

interface SmaDcaCfg {
  symbol: string;
  timeframe: string;
  direction: string;
  smaPeriod: number;
  takeProfitPercent: number;
  // legacy scalar fields — kept for backward compat (server synthesises a tier from these when levels is empty)
  dcaStepPercent: number;
  dcaTriggerBase?: string;
  dcaMultiplier: number;
  maxDcaLevels: number;
  positionSizeUsd: number;
  // tiered DCA model
  levels?: SmaDcaLevel[];
}

interface SmaDcaStateData {
  inPosition: boolean;
  isLong: boolean;
  totalQuantity: number;
  totalCost: number;
  averageEntryPrice: number;
  currentTakeProfit: number;
  dcaLevel: number;
  lastDcaPrice: number;
  skipNextCandle: boolean;
  lastPrice: number | null;
  positionOpenedAt: string | null;
  realizedPnlDollar: number;
  dcaCooldownUntil: string | null;
}

function SmaDcaCard({
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
  cfg: SmaDcaCfg | null;
  state: SmaDcaStateData | null;
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
  const inPos = state?.inPosition === true;
  const isLong = state?.isLong === true;
  const borderAccent = isRunning
    ? inPos ? 'border-l-accent-yellow' : 'border-l-accent-green'
    : 'border-l-border';

  // Resolve tiered DCA config (supports both new levels[] and legacy scalar fields)
  const cfgTiersForCard: SmaDcaLevel[] = (cfg?.levels && cfg.levels.length > 0)
    ? cfg.levels
    : [{ stepPercent: cfg?.dcaStepPercent ?? 0, multiplier: cfg?.dcaMultiplier ?? 1, count: cfg?.maxDcaLevels ?? 0 }];
  const totalMax = cfgTiersForCard.reduce((s, t) => s + t.count, 0);

  // PnL vs avg entry, using lastPrice
  let pnlPct = 0;
  let pnlUsd = 0;
  let progress = 0;
  let nextDcaPrice: number | null = null;
  if (inPos && state?.averageEntryPrice && state?.lastPrice) {
    const avg = state.averageEntryPrice;
    pnlPct = isLong
      ? (state.lastPrice - avg) / avg * 100
      : (avg - state.lastPrice) / avg * 100;
    pnlUsd = (state.totalCost ?? 0) * pnlPct / 100;

    // Next DCA trigger (mirrors SmaDcaHandler.cs trigger logic)
    const dcaLevel = state.dcaLevel ?? 0;
    // Walk tiers to find which tier is active at dcaLevel
    let cumulative = 0;
    let currentTierStep = 0;
    for (const tier of cfgTiersForCard) {
      if (dcaLevel < cumulative + tier.count) {
        currentTierStep = tier.stepPercent;
        break;
      }
      cumulative += tier.count;
    }
    if (dcaLevel < totalMax && currentTierStep > 0) {
      const triggerBase = (cfg?.dcaTriggerBase === 'LastFill' && (state.lastDcaPrice ?? 0) > 0)
        ? state.lastDcaPrice
        : avg;
      nextDcaPrice = isLong
        ? triggerBase * (1 - currentTierStep / 100)
        : triggerBase * (1 + currentTierStep / 100);
    }

    if (pnlPct >= 0) {
      // Green: progress from avg to TP
      const tp = cfg?.takeProfitPercent ?? 1;
      progress = Math.min((pnlPct / tp) * 100, 100);
    } else if (nextDcaPrice !== null) {
      // Red: progress from avg to next DCA trigger
      const dist = Math.abs(avg - nextDcaPrice);
      const moved = Math.abs(avg - state.lastPrice);
      progress = dist > 0 ? -Math.min((moved / dist) * 100, 100) : 0;
    }
    // else: max DCA reached and we're in drawdown — leave progress at 0 (no red bar)
  }

  const barColor = progress >= 0 ? 'bg-accent-green' : 'bg-accent-red';
  const barWidth = Math.min(Math.abs(progress), 100);

  return (
    <div className={`bg-bg-secondary rounded-xl border border-border border-l-2 ${borderAccent} overflow-hidden transition-colors hover:border-text-secondary/20`}>
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
            <span className="text-[10px] font-bold px-1.5 py-0.5 rounded bg-purple-500/15 text-purple-400">
              DCA
            </span>
            {cfg?.direction && (
              <span className={`text-[10px] font-bold px-1.5 py-0.5 rounded ${cfg.direction === 'Long' ? 'bg-accent-green/15 text-accent-green' : 'bg-accent-red/15 text-accent-red'}`}>
                {cfg.direction.toUpperCase()}
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
            SMA{cfg.smaPeriod}
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-accent-green/10 text-accent-green">
            TP {cfg.takeProfitPercent}%
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            {cfgTiersForCard.map((t, i) => `T${i + 1}: ${t.stepPercent}% ×${t.multiplier} ·${t.count}`).join('  |  ')}
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            Max {totalMax}
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            ${cfg.positionSizeUsd}
          </span>
        </div>
      )}

      {/* Divider */}
      <div className="border-t border-border/50" />

      {/* Signal / Position */}
      <div className="px-4 py-2.5 min-h-[52px] flex items-center">
        {!isRunning ? (
          <span className="text-text-secondary text-xs">—</span>
        ) : inPos ? (
          <div className="w-full space-y-1.5">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <span className={`text-[10px] font-bold px-1.5 py-0.5 rounded ${isLong ? 'bg-accent-green/15 text-accent-green' : 'bg-accent-red/15 text-accent-red'}`}>
                  {isLong ? 'LONG' : 'SHORT'}
                </span>
                <span className="text-xs font-semibold text-text-primary">
                  ${state?.totalCost?.toFixed(2) ?? '—'}
                </span>
                <span className="text-[10px] text-text-secondary">
                  avg {state?.averageEntryPrice?.toFixed(6) ?? '—'}
                </span>
              </div>
              <span className={`text-[10px] font-medium ${(state?.dcaLevel ?? 0) > 0 ? 'text-accent-yellow' : 'text-text-secondary'}`}>
                DCA {state?.dcaLevel ?? 0}/{totalMax}
              </span>
            </div>
            {state?.lastPrice ? (
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
                <div className="flex items-center justify-between">
                  <div className={`text-[10px] font-semibold mt-0.5 ${pnlPct >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
                    {pnlPct >= 0 ? '+' : ''}{pnlPct.toFixed(2)}% ({pnlUsd >= 0 ? '+' : ''}${pnlUsd.toFixed(2)})
                  </div>
                  <div className="text-[10px] text-text-secondary mt-0.5">
                    {pnlPct >= 0 || nextDcaPrice === null
                      ? <>TP {state?.currentTakeProfit?.toFixed(6) ?? '—'}</>
                      : <>DCA @ {nextDcaPrice.toFixed(6)}</>}
                  </div>
                </div>
              </div>
            ) : (
              <span className="text-[10px] text-text-secondary">загрузка цены...</span>
            )}
          </div>
        ) : (
          <div className="flex items-center gap-3 w-full">
            <span className="text-xs text-text-secondary">
              Ожидание сигнала SMA{cfg?.smaPeriod}
            </span>
            {state?.lastPrice != null && (
              <span className="text-[10px] text-text-secondary ml-auto">
                {state.lastPrice}
              </span>
            )}
          </div>
        )}
      </div>

      {/* Bottom stats row */}
      <div className="px-4 pb-2 flex items-center gap-3">
        {state != null && state.realizedPnlDollar != null && (
          <span className={`text-[10px] font-medium ${state.realizedPnlDollar >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
            PnL: {state.realizedPnlDollar >= 0 ? '+' : ''}${state.realizedPnlDollar.toFixed(2)}
          </span>
        )}
        {state?.dcaCooldownUntil && new Date(state.dcaCooldownUntil).getTime() > Date.now() && (
          <span className="text-[10px] text-accent-yellow">
            DCA cooldown
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

        {inPos && isRunning && (
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

/* ── Grid Float Card ───────────────────────────────────── */

interface GridFloatTier {
  upToPercent: number;
  sizeUsdt: number;
  // Optional per-tier overrides. When null/undefined, the bot uses the global
  // dcaStepPercent / tpStepPercent from the root config.
  dcaStepPercent?: number | null;
  tpStepPercent?: number | null;
}

interface GridFloatCfg {
  symbol: string;
  timeframe: string;
  direction: string;
  // New-format expanding tiers. Auto-derived from legacy baseSizeUsdt/rangePercent if missing.
  tiers: GridFloatTier[];
  dcaStepPercent: number;
  tpStepPercent: number;
  leverage: number;
  useStaticRange: boolean;
  // Legacy fields (read-only, kept for back-compat when reading old strategies from API).
  baseSizeUsdt?: number;
  rangePercent?: number;
}

// Normalize a raw config payload — server may send pre-tier (baseSizeUsdt + rangePercent) or
// new (tiers) shape. Always returns a populated `tiers` array. Mutates a shallow copy.
function normalizeGridFloatCfg(raw: Partial<GridFloatCfg> & Record<string, unknown>): GridFloatCfg {
  const cfg = { ...raw } as GridFloatCfg;
  const rawTiers = Array.isArray(raw.tiers) ? raw.tiers : [];
  let tiers: GridFloatTier[] = rawTiers
    .filter((t) => t && typeof t === 'object'
      && typeof (t as GridFloatTier).upToPercent === 'number' && (t as GridFloatTier).upToPercent > 0
      && typeof (t as GridFloatTier).sizeUsdt === 'number' && (t as GridFloatTier).sizeUsdt > 0)
    .map((t) => {
      const tier = t as GridFloatTier;
      return {
        upToPercent: tier.upToPercent,
        sizeUsdt: tier.sizeUsdt,
        dcaStepPercent: typeof tier.dcaStepPercent === 'number' && tier.dcaStepPercent > 0
          ? tier.dcaStepPercent : null,
        tpStepPercent: typeof tier.tpStepPercent === 'number' && tier.tpStepPercent > 0
          ? tier.tpStepPercent : null,
      };
    });

  if (tiers.length === 0
    && typeof raw.baseSizeUsdt === 'number' && raw.baseSizeUsdt > 0
    && typeof raw.rangePercent === 'number' && raw.rangePercent > 0) {
    tiers = [{ upToPercent: raw.rangePercent, sizeUsdt: raw.baseSizeUsdt }];
  }

  tiers.sort((a, b) => a.upToPercent - b.upToPercent);
  cfg.tiers = tiers;
  return cfg;
}

interface GridFloatBatchData {
  levelIdx: number;
  fillPrice: number;
  qty: number;
  tpPrice: number;
  tpOrderId: string | null;
}

interface GridFloatDcaOrderData {
  levelIdx: number;
  price: number;
  qty: number;
  orderId: string;
}

interface GridFloatStateData {
  isLong: boolean;
  anchorPrice: number;
  staticLowerBound: number;
  staticUpperBound: number;
  staticBoundsInitialized: boolean;
  batches: GridFloatBatchData[];
  dcaOrders: GridFloatDcaOrderData[];
  openAfterTime: string | null;
  lastPrice: number | null;
  realizedPnlDollar: number;
}

function GridFloatCard({
  s,
  cfg,
  state,
  isRunning,
  onStart,
  onStop,
  onPause,
  onResume,
  onUpdateTiers,
  updateTiersPending,
  onDelete,
  onEdit,
  onLogs,
  onClosePosition,
  closePositionPending,
  telegramBots,
  onSetTelegramBot,
}: {
  s: Strategy;
  cfg: GridFloatCfg | null;
  state: GridFloatStateData | null;
  isRunning: boolean;
  onStart: () => void;
  onStop: () => void;
  onPause: () => void;
  onResume: () => void;
  onUpdateTiers: (tiers: GridFloatTier[]) => void;
  updateTiersPending: boolean;
  onDelete: () => void;
  onEdit: () => void;
  onLogs: () => void;
  onClosePosition: () => void;
  closePositionPending: boolean;
  telegramBots: TelegramBotOption[] | undefined;
  onSetTelegramBot: (botId: string | null) => void;
}) {
  const isPaused = s.status.toLowerCase() === 'paused';
  const batches = state?.batches ?? [];
  const dcas = state?.dcaOrders ?? [];
  const hasPosition = batches.length > 0;
  const totalQty = batches.reduce((sum, b) => sum + b.qty, 0);
  const totalCost = batches.reduce((sum, b) => sum + b.fillPrice * b.qty, 0);
  const avgEntry = totalQty > 0 ? totalCost / totalQty : 0;

  // Normalize raw config — legacy bots (baseSizeUsdt + rangePercent) get auto-converted to a
  // single-tier list so the UI doesn't have to branch on shape.
  const normalizedCfg = useMemo(
    () => cfg ? normalizeGridFloatCfg(cfg as unknown as Partial<GridFloatCfg> & Record<string, unknown>) : null,
    [cfg],
  );
  const tiers = normalizedCfg?.tiers ?? [];
  const maxRangePct = tiers.length > 0 ? tiers[tiers.length - 1].upToPercent : 0;
  const anchorSize = tiers.length > 0 ? tiers[0].sizeUsdt : 0;

  // Inline Tiers editor — visible only when paused. Each tier row has its own upToPercent,
  // sizeUsdt, and optional per-tier dcaStep / tpStep overrides (blank = use global default).
  // Add/remove tier buttons mutate the list locally; "Apply" sends the full sorted list to
  // the API.
  type TierDraftRow = { upTo: string; size: string; dca: string; tp: string };
  const [tierDraft, setTierDraft] = useState<TierDraftRow[]>([]);
  // Build a stable signature of the persisted tiers so the effect only re-syncs the draft
  // when the actual tier values change (not on every refetch's new object identity).
  const persistedSig = useMemo(
    () => tiers.map((t) => `${t.upToPercent}:${t.sizeUsdt}:${t.dcaStepPercent ?? ''}:${t.tpStepPercent ?? ''}`).join('|'),
    [tiers],
  );
  useEffect(() => {
    if (isPaused) {
      setTierDraft(tiers.map((t) => ({
        upTo: String(t.upToPercent),
        size: String(t.sizeUsdt),
        dca: t.dcaStepPercent != null && t.dcaStepPercent > 0 ? String(t.dcaStepPercent) : '',
        tp: t.tpStepPercent != null && t.tpStepPercent > 0 ? String(t.tpStepPercent) : '',
      })));
    }
  }, [isPaused, persistedSig]); // eslint-disable-line react-hooks/exhaustive-deps

  const parsedDraft = tierDraft.map((t) => ({
    upToPercent: parseFloat(t.upTo),
    sizeUsdt: parseFloat(t.size),
    dcaStepPercent: t.dca.trim() === '' ? null : parseFloat(t.dca),
    tpStepPercent: t.tp.trim() === '' ? null : parseFloat(t.tp),
  }));
  // Each tier with an override must parse to a positive number. Blank → null is valid (uses global).
  const overridesValid = parsedDraft.every((t) =>
    (t.dcaStepPercent === null || (!isNaN(t.dcaStepPercent) && t.dcaStepPercent > 0))
    && (t.tpStepPercent === null || (!isNaN(t.tpStepPercent) && t.tpStepPercent > 0)));
  const effectiveDca = (t: { dcaStepPercent: number | null }) =>
    t.dcaStepPercent ?? normalizedCfg?.dcaStepPercent ?? 0;
  const draftValid = parsedDraft.length > 0
    && parsedDraft.every((t) => !isNaN(t.upToPercent) && t.upToPercent > 0 && !isNaN(t.sizeUsdt) && t.sizeUsdt > 0)
    && normalizedCfg != null
    && overridesValid
    // Outermost tier must accommodate at least one DCA at its effective step.
    && (() => {
      const outer = parsedDraft[parsedDraft.length - 1];
      const prevUp = parsedDraft.length > 1 ? parsedDraft[parsedDraft.length - 2].upToPercent : 0;
      return outer.upToPercent - prevUp >= effectiveDca(outer);
    })()
    && parsedDraft.every((t, i, a) => i === 0 || t.upToPercent > a[i - 1].upToPercent);
  // Send each tier's payload — omit null overrides so the backend reads the global default.
  const payloadDraft = parsedDraft.map((t) => ({
    upToPercent: t.upToPercent,
    sizeUsdt: t.sizeUsdt,
    ...(t.dcaStepPercent != null ? { dcaStepPercent: t.dcaStepPercent } : {}),
    ...(t.tpStepPercent != null ? { tpStepPercent: t.tpStepPercent } : {}),
  }));
  const draftSig = tierDraft.map((t) => `${t.upTo}:${t.size}:${t.dca}:${t.tp}`).join('|');
  const draftChanged = draftValid && draftSig !== persistedSig;
  // Per-tier walk preview — matches GridFloatHandler.ComputeDcaLevels exactly.
  const countSlots = (parsed: typeof parsedDraft, fallbackStep: number) => {
    let total = 0;
    let prev = 0;
    for (const t of parsed) {
      const step = t.dcaStepPercent ?? fallbackStep;
      if (step > 0) total += Math.floor((t.upToPercent - prev) / step);
      prev = t.upToPercent;
    }
    return total;
  };
  const previewSlots = draftValid && normalizedCfg
    ? countSlots(parsedDraft, normalizedCfg.dcaStepPercent)
    : 0;
  const currentSlots = normalizedCfg
    ? countSlots(
        tiers.map((t) => ({
          upToPercent: t.upToPercent,
          sizeUsdt: t.sizeUsdt,
          dcaStepPercent: t.dcaStepPercent ?? null,
          tpStepPercent: t.tpStepPercent ?? null,
        })),
        normalizedCfg.dcaStepPercent)
    : 0;

  const addTierRow = () => {
    setTierDraft((prev) => {
      const last = prev[prev.length - 1];
      const nextUpTo = last && parseFloat(last.upTo) > 0 ? parseFloat(last.upTo) * 2 : 10;
      const nextSize = last && parseFloat(last.size) > 0 ? parseFloat(last.size) * 2 : 100;
      return [...prev, { upTo: String(nextUpTo), size: String(nextSize), dca: '', tp: '' }];
    });
  };
  const removeTierRow = (i: number) => setTierDraft((prev) => prev.filter((_, idx) => idx !== i));
  const updateTierRow = (i: number, field: keyof TierDraftRow, value: string) =>
    setTierDraft((prev) => prev.map((t, idx) => idx === i ? { ...t, [field]: value } : t));

  // Mark-to-market unrealized PnL — uses state.lastPrice (refreshed by handler each tick).
  let unrealUsd = 0;
  let unrealPct = 0;
  if (hasPosition && state?.lastPrice && state.lastPrice > 0 && avgEntry > 0) {
    unrealPct = state.isLong
      ? (state.lastPrice - avgEntry) / avgEntry * 100
      : (avgEntry - state.lastPrice) / avgEntry * 100;
    unrealUsd = (unrealPct / 100) * totalCost;
  }

  const borderAccent = isPaused
    ? 'border-l-accent-yellow'
    : isRunning
      ? hasPosition
        ? state?.isLong ? 'border-l-accent-green' : 'border-l-accent-red'
        : 'border-l-accent-yellow'
      : 'border-l-border';

  return (
    <div className={`bg-bg-secondary rounded-xl border border-border border-l-2 ${borderAccent} overflow-hidden transition-colors hover:border-text-secondary/20`}>
      {/* Header */}
      <div className="px-4 pt-3 pb-2 flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <span className="text-sm font-mono font-semibold text-text-primary truncate">
              {cfg?.symbol || '—'}
            </span>
            <span className="text-[10px] font-bold px-1.5 py-0.5 rounded bg-indigo-500/15 text-indigo-400">
              GF
            </span>
            {cfg && (
              <span className={`text-[10px] font-bold px-1.5 py-0.5 rounded ${cfg.direction === 'Long' ? 'bg-accent-green/15 text-accent-green' : 'bg-accent-red/15 text-accent-red'}`}>
                {cfg.direction.toUpperCase()}
              </span>
            )}
            {cfg?.useStaticRange && (
              <span className="text-[10px] font-bold px-1.5 py-0.5 rounded bg-sky-500/15 text-sky-400">
                STATIC
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
      {normalizedCfg && (
        <div className="px-4 pb-2 flex flex-wrap gap-1">
          {tiers.length === 1 ? (
            <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
              ${anchorSize}/батч, range ±{maxRangePct}%
            </span>
          ) : (
            tiers.map((t, i) => {
              const prevUp = i === 0 ? 0 : tiers[i - 1].upToPercent;
              const dcaOverride = typeof t.dcaStepPercent === 'number' && t.dcaStepPercent > 0;
              const tpOverride = typeof t.tpStepPercent === 'number' && t.tpStepPercent > 0;
              const effDca = dcaOverride ? t.dcaStepPercent : normalizedCfg.dcaStepPercent;
              const effTp = tpOverride ? t.tpStepPercent : normalizedCfg.tpStepPercent;
              return (
                <span key={i} className="text-[10px] px-1.5 py-0.5 rounded bg-indigo-500/10 text-indigo-300">
                  T{i + 1}: {prevUp}%–{t.upToPercent}% · ${t.sizeUsdt}
                  {(dcaOverride || tpOverride) && (
                    <span className="ml-1 text-indigo-200/80">
                      ·dca{effDca}%·tp{effTp}%
                    </span>
                  )}
                </span>
              );
            })
          )}
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            step {normalizedCfg.dcaStepPercent}%
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            tp +{normalizedCfg.tpStepPercent}%
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            ×{normalizedCfg.leverage}
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            {normalizedCfg.timeframe}
          </span>
        </div>
      )}

      {/* Divider */}
      <div className="border-t border-border/50" />

      {/* State block */}
      <div className="px-4 py-2.5 min-h-[52px] flex items-center">
        {!isRunning && !isPaused ? (
          <span className="text-text-secondary text-xs">Остановлен</span>
        ) : !hasPosition ? (
          <div className="text-xs text-text-secondary">
            {isPaused
              ? 'Пауза (нет позиции)'
              : state?.openAfterTime
                ? 'Кулдаун (ждём следующий бар)'
                : 'Ожидание входа на закрытии бара'}
          </div>
        ) : (
          <div className="w-full space-y-1">
            <div className="flex items-center gap-2 flex-wrap">
              <span className="text-[10px] text-text-secondary">
                Якорь: <span className="text-text-primary">{state?.anchorPrice ? state.anchorPrice.toFixed(6) : '—'}</span>
              </span>
              <span className="text-[10px] text-text-secondary">
                Avg: <span className="text-text-primary">{avgEntry ? avgEntry.toFixed(6) : '—'}</span>
              </span>
              {state?.lastPrice != null && (
                <span className="text-[10px] text-text-secondary">
                  Цена: <span className="text-text-primary">{state.lastPrice.toFixed(6)}</span>
                </span>
              )}
              {avgEntry > 0 && state?.lastPrice != null && (
                <span className={`text-[10px] font-semibold ${unrealUsd >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
                  {unrealUsd >= 0 ? '+' : ''}${unrealUsd.toFixed(2)} ({unrealPct >= 0 ? '+' : ''}{unrealPct.toFixed(2)}%)
                </span>
              )}
            </div>
            <div className="text-[10px] text-text-secondary flex items-center gap-2 flex-wrap">
              <span>
                Батчей: <span className="text-text-primary font-medium">{batches.length}</span>
              </span>
              <span>
                Живых DCA: <span className="text-text-primary font-medium">{dcas.length}</span>
              </span>
              <span>
                Qty: <span className="text-text-primary font-mono">{totalQty.toFixed(6)}</span>
              </span>
            </div>
          </div>
        )}
      </div>

      {/* Bottom realized PnL */}
      {state != null && state.realizedPnlDollar != null && (
        <div className="px-4 pb-2 flex items-center gap-3">
          <span className={`text-[10px] font-medium ${state.realizedPnlDollar >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
            Realized: {state.realizedPnlDollar >= 0 ? '+' : ''}${state.realizedPnlDollar.toFixed(2)}
          </span>
        </div>
      )}

      {/* Inline Tiers editor — only when paused */}
      {isPaused && normalizedCfg && (
        <>
          <div className="border-t border-border/50" />
          <div className="px-4 py-2.5 bg-accent-yellow/5">
            <div className="text-[10px] font-medium text-accent-yellow mb-1.5">
              Расширить сетку (ярусы)
            </div>
            <div className="space-y-1.5">
              {tierDraft.map((t, i) => {
                const prevUp = i === 0 ? 0 : parseFloat(tierDraft[i - 1].upTo);
                return (
                  <div key={i} className="flex items-center gap-1.5 flex-wrap">
                    <span className="text-[10px] text-text-secondary w-7 shrink-0">T{i + 1}</span>
                    <span className="text-[10px] text-text-secondary">от {isNaN(prevUp) ? '?' : prevUp}% до</span>
                    <input
                      type="number"
                      step="0.5"
                      min="0.1"
                      value={t.upTo}
                      onChange={(e) => updateTierRow(i, 'upTo', e.target.value)}
                      className="w-14 text-[11px] bg-bg-tertiary border border-border rounded px-1.5 py-0.5 text-text-primary"
                    />
                    <span className="text-[10px] text-text-secondary">%, $</span>
                    <input
                      type="number"
                      step="1"
                      min="1"
                      value={t.size}
                      onChange={(e) => updateTierRow(i, 'size', e.target.value)}
                      className="w-16 text-[11px] bg-bg-tertiary border border-border rounded px-1.5 py-0.5 text-text-primary"
                    />
                    <span className="text-[10px] text-text-secondary">dca</span>
                    <input
                      type="number"
                      step="0.1"
                      min="0.01"
                      value={t.dca}
                      placeholder={String(normalizedCfg.dcaStepPercent)}
                      onChange={(e) => updateTierRow(i, 'dca', e.target.value)}
                      className="w-12 text-[11px] bg-bg-tertiary border border-border rounded px-1.5 py-0.5 text-text-primary placeholder:text-text-secondary/40"
                    />
                    <span className="text-[10px] text-text-secondary">% tp</span>
                    <input
                      type="number"
                      step="0.1"
                      min="0.01"
                      value={t.tp}
                      placeholder={String(normalizedCfg.tpStepPercent)}
                      onChange={(e) => updateTierRow(i, 'tp', e.target.value)}
                      className="w-12 text-[11px] bg-bg-tertiary border border-border rounded px-1.5 py-0.5 text-text-primary placeholder:text-text-secondary/40"
                    />
                    <span className="text-[10px] text-text-secondary">%</span>
                    {tierDraft.length > 1 && (
                      <button
                        onClick={() => removeTierRow(i)}
                        className="ml-auto px-1.5 py-0.5 text-[10px] text-accent-red hover:bg-accent-red/10 rounded"
                        title="Удалить ярус"
                      >
                        ×
                      </button>
                    )}
                  </div>
                );
              })}
            </div>
            <div className="flex items-center gap-2 mt-2 flex-wrap">
              <button
                onClick={addTierRow}
                className="px-2 py-1 text-[10px] font-medium bg-bg-tertiary text-text-secondary rounded-md hover:bg-bg-tertiary/70 transition-colors"
              >
                + Ярус
              </button>
              <span className="text-[10px] text-text-secondary">
                {draftValid
                  ? <>будет <span className="text-text-primary font-medium">{previewSlots}</span> уровней (сейчас {currentSlots})</>
                  : <span className="text-accent-red">upTo возрастает, ширина внешнего тира ≥ его шага DCA, шаги &gt; 0</span>}
              </span>
              <button
                onClick={() => { if (draftChanged) onUpdateTiers(payloadDraft as GridFloatTier[]); }}
                disabled={!draftChanged || updateTiersPending}
                className="ml-auto px-2 py-1 text-[10px] font-medium bg-accent-yellow/15 text-accent-yellow rounded-md hover:bg-accent-yellow/25 transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
              >
                {updateTiersPending ? '...' : 'Применить'}
              </button>
            </div>
            <div className="text-[10px] text-text-secondary/70 mt-1.5 leading-tight">
              После «Применить» нажми ▶ Возобновить — HealMissingDcas поставит лимиты на новые свободные уровни с размером своего яруса. Уже стоящие лимитки не пересчитываются. Пустые dca/tp = глобальный шаг ({normalizedCfg.dcaStepPercent}% / {normalizedCfg.tpStepPercent}%).
            </div>
          </div>
        </>
      )}

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

        {hasPosition && (isRunning || isPaused) && (
          <button
            onClick={() => { if (confirm('Закрыть всю позицию по рынку?')) onClosePosition(); }}
            disabled={closePositionPending}
            className="px-2 py-1 text-[11px] font-medium bg-accent-yellow/10 text-accent-yellow rounded-lg hover:bg-accent-yellow/20 transition-colors disabled:opacity-50"
          >
            {closePositionPending ? '...' : 'Закрыть'}
          </button>
        )}

        {isPaused ? (
          <>
            <button
              onClick={onResume}
              title="Возобновить"
              className="px-2.5 py-1 text-[11px] font-medium bg-accent-green/10 text-accent-green rounded-lg hover:bg-accent-green/20 transition-colors"
            >
              ▶ Возобновить
            </button>
            <button
              onClick={onStop}
              className="px-2.5 py-1 text-[11px] font-medium bg-accent-red/10 text-accent-red rounded-lg hover:bg-accent-red/20 transition-colors"
            >
              Стоп
            </button>
          </>
        ) : isRunning ? (
          <>
            <button
              onClick={onPause}
              title="Пауза — остановить тик handler'а, но оставить позицию и лимиты живыми. На паузе можно расширить Range."
              className="px-2.5 py-1 text-[11px] font-medium bg-accent-yellow/10 text-accent-yellow rounded-lg hover:bg-accent-yellow/20 transition-colors"
            >
              ⏸ Пауза
            </button>
            <button
              onClick={onStop}
              className="px-2.5 py-1 text-[11px] font-medium bg-accent-red/10 text-accent-red rounded-lg hover:bg-accent-red/20 transition-colors"
            >
              Стоп
            </button>
          </>
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

/* ── Grid Hedge Card ─────────────────────────────────────── */

interface GridHedgeCfg {
  mode: 1 | 2;
  gridSymbol: string;
  hedgeSymbol: string;
  rangePercent: number;
  upperExitPercent: number;
  dcaStepPercent: number;
  tpStepPercent: number;
  betUsdt: number;
  hedgeNotionalUsdt: number;
  hedgeLeverage: number;
  gridLeverage: number;
}

interface GridHedgeBatchData {
  buyOrderId: string;
  tpOrderId: string | null;
  levelPercent: number;
  filledPrice: number;
  filledQty: number;
  tpPrice: number;
  closed: boolean;
  realizedPnl: number;
  filledAt: string;
}

interface GridHedgePendingBuyData {
  orderId: string;
  price: number;
  qty: number;
  levelPercent: number;
}

// phase: 0=NotStarted, 1=HedgeOpening, 2=GridArming, 3=Active,
//        4=ExitingUp, 5=ExitingDown, 6=Done
// Backend serializes this enum as a number (not a string).
interface GridHedgeStateData {
  phase: number;
  anchor: number;
  hedgeAnchor: number;
  hedgeQty: number;
  hedgeAvgEntry: number;
  hedgeOpenOrderId: string | null;
  batches: GridHedgeBatchData[];
  pendingBuys: GridHedgePendingBuyData[];
  gridRealizedPnl: number;
  hedgeRealizedPnl: number;
  completedCycles: number;
  lastPrice: number | null;
  placementCooldownUntil: string | null;
}

function gridHedgeRecommendation(R: number, step: number, G: number): { hedge: number; maxLoss: number } | null {
  if (!(R > 0) || !(step > 0) || !(G > 0)) return null;
  const COEF = 0.88;
  const executions = (2 * R - 1) / step;
  const gridWorstLoss = G * executions * (R / 100) * COEF;
  const hedge = gridWorstLoss / (2 * R / 100);
  const maxLoss = gridWorstLoss / 2;
  return { hedge, maxLoss };
}

const GRID_HEDGE_PHASE_LABELS: Record<number, string> = {
  0: 'NotStarted',
  1: 'HedgeOpening',
  2: 'GridArming',
  3: 'Active',
  4: 'ExitingUp',
  5: 'ExitingDown',
  6: 'Done',
};

function gridHedgePhaseColor(phase: number): string {
  if (phase === 3) return 'bg-accent-green/15 text-accent-green';
  if (phase === 4) return 'bg-accent-blue/15 text-accent-blue';
  if (phase === 5) return 'bg-accent-red/15 text-accent-red';
  if (phase === 6) return 'bg-bg-tertiary text-text-secondary';
  return 'bg-accent-yellow/15 text-accent-yellow';
}

function GridHedgeCard({
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
  cfg: GridHedgeCfg | null;
  state: GridHedgeStateData | null;
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
  const batches = state?.batches ?? [];
  const pendingBuys = state?.pendingBuys ?? [];
  const openBatches = batches.filter((b) => !b.closed);
  const hasPosition = openBatches.length > 0 || (state?.hedgeQty ?? 0) > 0;

  // Border accent based on phase / running state
  const borderAccent = !isRunning
    ? 'border-l-border'
    : phase === 3
      ? 'border-l-accent-green'
      : phase === 4
        ? 'border-l-accent-blue'
        : phase === 5
          ? 'border-l-accent-red'
          : phase === 6
            ? 'border-l-border'
            : 'border-l-accent-yellow';

  // Price-in-band visualisation
  const anchor = state?.anchor ?? 0;
  const lastPrice = state?.lastPrice ?? null;
  const rangePercent = cfg?.rangePercent ?? 0;
  const upperExitPercent = cfg?.upperExitPercent ?? 0;
  let bandPct: number | null = null;
  if (anchor > 0 && lastPrice != null && (rangePercent + upperExitPercent) > 0) {
    const lowerBound = anchor * (1 - rangePercent / 100);
    const upperBound = anchor * (1 + upperExitPercent / 100);
    const total = upperBound - lowerBound;
    bandPct = total > 0 ? Math.max(0, Math.min(100, ((lastPrice - lowerBound) / total) * 100)) : null;
  }

  const modeLabel = cfg
    ? cfg.mode === 1
      ? 'Spot+Futures'
      : `Cross: ${cfg.gridSymbol?.replace(/USDT$/i, '')}/${cfg.hedgeSymbol?.replace(/USDT$/i, '')}`
    : null;

  return (
    <div className={`bg-bg-secondary rounded-xl border border-border border-l-2 ${borderAccent} overflow-hidden transition-colors hover:border-text-secondary/20`}>
      {/* Header */}
      <div className="px-4 pt-3 pb-2 flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <span className="text-sm font-mono font-semibold text-text-primary truncate">
              {cfg?.gridSymbol || '—'}
            </span>
            <span className="text-[10px] font-bold px-1.5 py-0.5 rounded bg-violet-500/15 text-violet-400">
              GH
            </span>
            {modeLabel && (
              <span className="text-[10px] font-medium px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
                {modeLabel}
              </span>
            )}
            <span className={`text-[10px] font-bold px-1.5 py-0.5 rounded ${gridHedgePhaseColor(phase)}`}>
              {GRID_HEDGE_PHASE_LABELS[phase] ?? String(phase)}
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
            range −{cfg.rangePercent}% ↔ +{cfg.upperExitPercent}%
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            step dca {cfg.dcaStepPercent}% tp {cfg.tpStepPercent}%
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            ${cfg.betUsdt}/уровень
          </span>
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
            hedge ×{cfg.hedgeLeverage}
          </span>
          {cfg.mode === 2 && (
            <span className="text-[10px] px-1.5 py-0.5 rounded bg-bg-tertiary text-text-secondary">
              grid ×{cfg.gridLeverage}
            </span>
          )}
        </div>
      )}

      {/* Divider */}
      <div className="border-t border-border/50" />

      {/* State block */}
      <div className="px-4 py-2.5 min-h-[52px]">
        {!isRunning ? (
          <span className="text-text-secondary text-xs">Остановлен</span>
        ) : state == null ? (
          <span className="text-text-secondary text-xs">Ожидание состояния...</span>
        ) : (
          <div className="w-full space-y-1.5">
            {/* Anchor + band */}
            {anchor > 0 && (
              <div className="flex items-center gap-2 flex-wrap">
                <span className="text-[10px] text-text-secondary">
                  Якорь: <span className="text-text-primary font-mono">{anchor}</span>
                </span>
                <span className="text-[10px] text-text-secondary">
                  Диапазон: −{cfg?.rangePercent}% ↔ +{cfg?.upperExitPercent}%
                </span>
                {lastPrice != null && (
                  <span className="text-[10px] text-text-secondary">
                    Цена: <span className="text-text-primary font-mono">{lastPrice}</span>
                  </span>
                )}
              </div>
            )}

            {/* Price band visualisation */}
            {bandPct != null && (
              <div className="relative h-1.5 rounded-full bg-bg-tertiary overflow-hidden">
                <div
                  className="absolute top-0 bottom-0 w-0.5 bg-accent-blue rounded-full"
                  style={{ left: `${bandPct}%`, transform: 'translateX(-50%)' }}
                />
                <div
                  className="absolute top-0 bottom-0 w-px bg-text-secondary/40"
                  style={{ left: `${(cfg!.rangePercent / (cfg!.rangePercent + cfg!.upperExitPercent)) * 100}%` }}
                />
              </div>
            )}

            {/* Hedge row */}
            <div className="text-[10px] text-text-secondary">
              {state.hedgeQty > 0
                ? (
                  <span>
                    HEDGE SHORT {cfg?.hedgeSymbol || cfg?.gridSymbol}:{' '}
                    <span className="text-accent-red font-mono">{state.hedgeQty}</span>
                    {' @ '}
                    <span className="text-text-primary font-mono">{state.hedgeAvgEntry}</span>
                  </span>
                )
                : 'Hedge: —'
              }
            </div>

            {/* Grid summary */}
            <div className="text-[10px] text-text-secondary flex items-center gap-2 flex-wrap">
              <span>
                Лимитов: <span className="text-text-primary font-medium">{pendingBuys.length}</span>
              </span>
              <span>
                Открытых батчей: <span className="text-text-primary font-medium">{openBatches.length}</span>
              </span>
              <span>
                Циклов: <span className="text-text-primary font-medium">{state.completedCycles}</span>
              </span>
            </div>

            {/* PnL */}
            <div className="flex items-center gap-3 flex-wrap">
              <span className={`text-[10px] font-medium ${state.gridRealizedPnl >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
                Grid: {state.gridRealizedPnl >= 0 ? '+' : ''}${state.gridRealizedPnl.toFixed(2)}
              </span>
              <span className={`text-[10px] font-medium ${state.hedgeRealizedPnl >= 0 ? 'text-accent-green' : 'text-accent-red'}`}>
                Hedge: {state.hedgeRealizedPnl >= 0 ? '+' : ''}${state.hedgeRealizedPnl.toFixed(2)}
              </span>
            </div>

            {/* Done hint */}
            {phase === 6 && (
              <div className="text-[10px] italic text-text-secondary/70">
                Цикл завершён. Stop → Start чтобы начать новый.
              </div>
            )}
          </div>
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

        {hasPosition && isRunning && (
          <button
            onClick={() => { if (confirm('Закрыть все позиции по рынку?')) onClosePosition(); }}
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
  const [autoRotateTicker, setAutoRotateTicker] = useState(true);
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

  // SMA DCA fields
  const [sdForm, setSdForm] = useState({
    timeframe: '1h',
    direction: 'Long',
    smaPeriod: '50',
    takeProfitPercent: '1.0',
    positionSizeUsd: '100',
    dcaTriggerBase: 'Average',
    orderType: 'Market',
    entryLimitOffsetPercent: '0.05',
    entryLimitTimeoutBars: '3',
  });
  const [sdLevels, setSdLevels] = useState<Array<{ stepPercent: string; multiplier: string; count: string }>>(
    [{ stepPercent: '3.0', multiplier: '3.0', count: '5' }],
  );

  // FundingClaim fields
  const [fcForm, setFcForm] = useState({
    maxCycles: '0',
    checkBeforeFundingMinutes: '10',
  });
  const [fcAutoRotate, setFcAutoRotate] = useState(true);

  // GridFloat fields. `tiers` is the dynamic expanding-grid list; start with one tier so the
  // default config matches the legacy single-zone behaviour ($100 in the first 10%). Per-tier
  // dca/tp inputs default empty — empty falls back to the global step from gfForm.
  const [gfForm, setGfForm] = useState({
    timeframe: '1h',
    direction: 'Long',
    dcaStepPercent: '1',
    tpStepPercent: '1',
    leverage: '1',
    useStaticRange: false,
  });
  const [gfTiers, setGfTiers] = useState<Array<{ upTo: string; size: string; dca: string; tp: string }>>(
    [{ upTo: '10', size: '100', dca: '', tp: '' }],
  );

  // GridHedge fields
  const [ghForm, setGhForm] = useState({
    mode: 1 as 1 | 2,
    hedgeSymbol: '',
    rangePercent: '10',
    upperExitPercent: '10',
    dcaStepPercent: '1',
    tpStepPercent: '1',
    betUsdt: '100',
    hedgeNotionalUsdt: '836',
    hedgeLeverage: '5',
    gridLeverage: '1',
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
        autoRotateTicker,
      });
    } else if (strategyType === 'SmaDca') {
      configJson = JSON.stringify({
        symbol: symbol.replace(/\s+/g, '').toUpperCase(),
        timeframe: sdForm.timeframe,
        direction: sdForm.direction,
        smaPeriod: Number(sdForm.smaPeriod),
        takeProfitPercent: Number(sdForm.takeProfitPercent),
        positionSizeUsd: Number(sdForm.positionSizeUsd),
        dcaTriggerBase: sdForm.dcaTriggerBase,
        orderType: sdForm.orderType,
        entryLimitOffsetPercent: Number(sdForm.entryLimitOffsetPercent),
        entryLimitTimeoutBars: Number(sdForm.entryLimitTimeoutBars),
        levels: sdLevels.map((l) => ({
          stepPercent: Number(l.stepPercent),
          multiplier: Number(l.multiplier),
          count: Number(l.count),
        })),
      });
    } else if (strategyType === 'FundingClaim') {
      configJson = JSON.stringify({
        symbol: symbol.replace(/\s+/g, '').toUpperCase(),
        maxCycles: Number(fcForm.maxCycles),
        autoRotateTicker: fcAutoRotate,
        checkBeforeFundingMinutes: Number(fcForm.checkBeforeFundingMinutes),
      });
    } else if (strategyType === 'GridFloat') {
      const tiers = gfTiers
        .map((t) => {
          const upToPercent = Number(t.upTo);
          const sizeUsdt = Number(t.size);
          const dcaStepPercent = t.dca.trim() === '' ? null : Number(t.dca);
          const tpStepPercent = t.tp.trim() === '' ? null : Number(t.tp);
          return { upToPercent, sizeUsdt, dcaStepPercent, tpStepPercent };
        })
        .filter((t) => t.upToPercent > 0 && t.sizeUsdt > 0)
        .sort((a, b) => a.upToPercent - b.upToPercent);
      if (tiers.length === 0) {
        setError('Добавьте хотя бы один ярус с upTo% > 0 и ставкой > 0');
        return;
      }
      for (let i = 1; i < tiers.length; i++) {
        if (tiers[i].upToPercent <= tiers[i - 1].upToPercent) {
          setError('Ярусы должны идти со строго возрастающим upTo%');
          return;
        }
      }
      // Per-tier overrides, when provided, must be strictly positive.
      for (const t of tiers) {
        if (t.dcaStepPercent !== null && !(t.dcaStepPercent > 0)) {
          setError('Per-tier шаг DCA должен быть > 0 (или оставьте пустым)');
          return;
        }
        if (t.tpStepPercent !== null && !(t.tpStepPercent > 0)) {
          setError('Per-tier шаг TP должен быть > 0 (или оставьте пустым)');
          return;
        }
      }
      // Send each tier — omit null overrides so the bot falls back to the global default.
      const tiersPayload = tiers.map((t) => ({
        upToPercent: t.upToPercent,
        sizeUsdt: t.sizeUsdt,
        ...(t.dcaStepPercent !== null ? { dcaStepPercent: t.dcaStepPercent } : {}),
        ...(t.tpStepPercent !== null ? { tpStepPercent: t.tpStepPercent } : {}),
      }));
      configJson = JSON.stringify({
        symbol: symbol.replace(/\s+/g, '').toUpperCase(),
        timeframe: gfForm.timeframe,
        direction: gfForm.direction,
        tiers: tiersPayload,
        dcaStepPercent: Number(gfForm.dcaStepPercent),
        tpStepPercent: Number(gfForm.tpStepPercent),
        leverage: Number(gfForm.leverage),
        useStaticRange: gfForm.useStaticRange,
      });
    } else if (strategyType === 'GridHedge') {
      if (Number(ghForm.rangePercent) <= 0 || Number(ghForm.upperExitPercent) <= 0) {
        setError('Диапазоны вниз/вверх должны быть > 0');
        return;
      }
      if (Number(ghForm.dcaStepPercent) <= 0 || Number(ghForm.tpStepPercent) <= 0) {
        setError('Шаги DCA / TP должны быть > 0');
        return;
      }
      if (Number(ghForm.betUsdt) <= 0) {
        setError('Ставка на уровень должна быть > 0');
        return;
      }
      if (Number(ghForm.hedgeNotionalUsdt) < 0) {
        setError('Размер хеджа не может быть отрицательным');
        return;
      }
      if (ghForm.mode === 2 && !ghForm.hedgeSymbol.trim()) {
        setError('Cross-Ticker режим требует hedgeSymbol (например BTCUSDT)');
        return;
      }
      configJson = JSON.stringify({
        mode: ghForm.mode,
        gridSymbol: symbol.replace(/\s+/g, '').toUpperCase(),
        hedgeSymbol: ghForm.mode === 2 ? ghForm.hedgeSymbol.replace(/\s+/g, '').toUpperCase() : '',
        rangePercent: Number(ghForm.rangePercent),
        upperExitPercent: Number(ghForm.upperExitPercent),
        dcaStepPercent: Number(ghForm.dcaStepPercent),
        tpStepPercent: Number(ghForm.tpStepPercent),
        betUsdt: Number(ghForm.betUsdt),
        hedgeNotionalUsdt: Number(ghForm.hedgeNotionalUsdt),
        hedgeLeverage: Number(ghForm.hedgeLeverage),
        gridLeverage: Number(ghForm.gridLeverage),
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
            {strategyType === 'HuntingFunding' && activeAccounts.find((a) => a.id === accountId)?.exchangeType === 3 && (
              <div className="flex items-start gap-2 mt-2 bg-blue-500/10 border border-blue-500/20 rounded-lg px-3 py-2">
                <svg className="w-4 h-4 text-blue-400 mt-0.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M11.25 11.25l.041-.02a.75.75 0 011.063.852l-.708 2.836a.75.75 0 001.063.853l.041-.021M21 12a9 9 0 11-18 0 9 9 0 0118 0zm-9-3.75h.008v.008H12V8.25z" />
                </svg>
                <p className="text-xs text-blue-400">BingX блокирует выставление ордеров за <span className="font-semibold">60 секунд до фандинга</span> (settlement). Установите <span className="font-semibold">SecondsBeforeFunding ≥ 65</span>.</p>
              </div>
            )}
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

          {strategyType === 'SmaDca' ? (
            <>
              {/* Timeframe */}
              <div>
                <label className={labelCls}>Таймфрейм</label>
                <select
                  value={sdForm.timeframe}
                  onChange={(e) => setSdForm({ ...sdForm, timeframe: e.target.value })}
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

              {/* Direction + SMA period */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Направление</label>
                  <select
                    value={sdForm.direction}
                    onChange={(e) => setSdForm({ ...sdForm, direction: e.target.value })}
                    className={inputCls}
                  >
                    <option value="Long">Long</option>
                    <option value="Short">Short</option>
                  </select>
                </div>
                <div>
                  <label className={labelCls}>Период SMA</label>
                  <input
                    type="number"
                    min="2"
                    value={sdForm.smaPeriod}
                    onChange={(e) => setSdForm({ ...sdForm, smaPeriod: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* Position size + TP */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Размер входа (USD)</label>
                  <input
                    type="number"
                    step="1"
                    min="1"
                    value={sdForm.positionSizeUsd}
                    onChange={(e) => setSdForm({ ...sdForm, positionSizeUsd: e.target.value })}
                    className={inputCls}
                  />
                </div>
                <div>
                  <label className={labelCls}>Take Profit %</label>
                  <input
                    type="number"
                    step="0.1"
                    min="0.01"
                    value={sdForm.takeProfitPercent}
                    onChange={(e) => setSdForm({ ...sdForm, takeProfitPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* DCA tiers */}
              <div>
                <label className={labelCls}>Уровни DCA</label>
                <div className="space-y-2">
                  {sdLevels.map((lvl, i) => (
                    <div key={i} className="grid grid-cols-[1fr_1fr_1fr_auto] gap-2 items-end">
                      <div>
                        <label className={labelCls}>DCA шаг, %</label>
                        <input
                          type="number"
                          step="0.1"
                          min="0.01"
                          value={lvl.stepPercent}
                          onChange={(e) => setSdLevels(sdLevels.map((l, idx) => idx === i ? { ...l, stepPercent: e.target.value } : l))}
                          className={inputCls}
                        />
                      </div>
                      <div>
                        <label className={labelCls}>Множитель</label>
                        <input
                          type="number"
                          step="0.1"
                          min="0.1"
                          value={lvl.multiplier}
                          onChange={(e) => setSdLevels(sdLevels.map((l, idx) => idx === i ? { ...l, multiplier: e.target.value } : l))}
                          className={inputCls}
                        />
                      </div>
                      <div>
                        <label className={labelCls}>Доборов</label>
                        <input
                          type="number"
                          min="1"
                          value={lvl.count}
                          onChange={(e) => setSdLevels(sdLevels.map((l, idx) => idx === i ? { ...l, count: e.target.value } : l))}
                          className={inputCls}
                        />
                      </div>
                      <button
                        type="button"
                        disabled={sdLevels.length <= 1}
                        onClick={() => setSdLevels(sdLevels.filter((_, idx) => idx !== i))}
                        className="pb-0.5 text-accent-red/70 hover:text-accent-red disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
                        title="Удалить уровень"
                      >
                        <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                        </svg>
                      </button>
                    </div>
                  ))}
                </div>
                <button
                  type="button"
                  onClick={() => setSdLevels([...sdLevels, { stepPercent: '5.0', multiplier: '2.0', count: '2' }])}
                  className="mt-2 text-xs text-accent-blue hover:text-accent-blue/80 transition-colors"
                >
                  + Добавить уровень
                </button>
              </div>

              {/* DCA trigger base */}
              <div>
                <label className={labelCls}>База расчёта шага DCA</label>
                <select
                  value={sdForm.dcaTriggerBase}
                  onChange={(e) => setSdForm({ ...sdForm, dcaTriggerBase: e.target.value })}
                  className={inputCls}
                >
                  <option value="Average">От средней цены входа (сетка сжимается)</option>
                  <option value="LastFill">От последней докупки (сетка равномерная)</option>
                </select>
              </div>

              {/* Order type: market vs limit (DCA only — first entry is always market) */}
              <div>
                <label className={labelCls}>Тип ордеров для DCA (докупок)</label>
                <select
                  value={sdForm.orderType}
                  onChange={(e) => setSdForm({ ...sdForm, orderType: e.target.value })}
                  className={inputCls}
                >
                  <option value="Market">Market — рыночные (taker, гарантированный фил)</option>
                  <option value="Limit">Limit — лимитные maker (экономия комиссии)</option>
                </select>
                <p className="text-xs text-text-secondary mt-1 italic">
                  Первый вход в позицию всегда выполняется рыночным ордером.
                </p>
              </div>

              {sdForm.orderType === 'Limit' && (
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label className={labelCls}>Оффсет лимитки, %</label>
                    <input
                      type="number"
                      step="0.01"
                      min="0.01"
                      value={sdForm.entryLimitOffsetPercent}
                      onChange={(e) => setSdForm({ ...sdForm, entryLimitOffsetPercent: e.target.value })}
                      className={inputCls}
                    />
                  </div>
                  <div>
                    <label className={labelCls}>Таймаут entry, свечей</label>
                    <input
                      type="number"
                      min="1"
                      value={sdForm.entryLimitTimeoutBars}
                      onChange={(e) => setSdForm({ ...sdForm, entryLimitTimeoutBars: e.target.value })}
                      className={inputCls}
                    />
                  </div>
                </div>
              )}

              <p className="text-xs text-text-secondary italic">
                {`Бот проходит уровни по очереди: сначала выполняет «Доборов» докупок первого уровня с его шагом и множителем, затем переходит к следующему. Когда все уровни исчерпаны — новых DCA нет, бот ждёт TP. Итого докупок: ${sdLevels.reduce((s, l) => s + (Number(l.count) || 0), 0)}.`}
                {sdForm.orderType === 'Limit' && (
                  <>
                    <br />
                    Лимитки ставятся на {sdForm.entryLimitOffsetPercent}% {sdForm.direction === 'Long' ? 'ниже' : 'выше'} цены закрытия свечи (всегда maker). Entry-лимит отменяется через {sdForm.entryLimitTimeoutBars} свечей если не исполнился; DCA-лимит висит до исполнения или пока TP не закроет позицию.
                  </>
                )}
              </p>
            </>
          ) : strategyType === 'FundingClaim' ? (
            <>
              {/* Auto-rotate ticker */}
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={fcAutoRotate}
                  onChange={(e) => setFcAutoRotate(e.target.checked)}
                  className="rounded border-dark-border"
                />
                <span className="text-sm text-text-primary">Автообновление тикера</span>
                <span className="text-xs text-text-secondary">(по макс. фандингу)</span>
              </label>

              {/* Check before + Max cycles */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Проверка за (мин)</label>
                  <input
                    type="number"
                    min="1"
                    value={fcForm.checkBeforeFundingMinutes}
                    onChange={(e) => setFcForm({ ...fcForm, checkBeforeFundingMinutes: e.target.value })}
                    className={inputCls}
                  />
                  <p className="text-xs text-text-secondary mt-0.5">Минут до фандинга для проверки ставки</p>
                </div>
                <div>
                  <label className={labelCls}>Макс. циклов <span className="font-normal">(0 = ∞)</span></label>
                  <input
                    type="number"
                    min="0"
                    value={fcForm.maxCycles}
                    onChange={(e) => setFcForm({ ...fcForm, maxCycles: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              <p className="text-xs text-text-secondary italic">
                Бот автоматически определяет направление по знаку фандинга: шорт при положительном, лонг при отрицательном.
                Позиция открывается маркет-ордером и держится для сбора фандинговых выплат.
              </p>
            </>
          ) : strategyType === 'GridFloat' ? (
            <>
              {/* Timeframe */}
              <div>
                <label className={labelCls}>Таймфрейм</label>
                <select
                  value={gfForm.timeframe}
                  onChange={(e) => setGfForm({ ...gfForm, timeframe: e.target.value })}
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

              {/* Direction + Leverage */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Направление</label>
                  <select
                    value={gfForm.direction}
                    onChange={(e) => setGfForm({ ...gfForm, direction: e.target.value })}
                    className={inputCls}
                  >
                    <option value="Long">Long</option>
                    <option value="Short">Short</option>
                  </select>
                </div>
                <div>
                  <label className={labelCls}>Плечо</label>
                  <input
                    type="number"
                    min="1"
                    step="1"
                    value={gfForm.leverage}
                    onChange={(e) => setGfForm({ ...gfForm, leverage: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* TP step + DCA step — defaults for tiers that don't override */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Шаг TP, % (по умолчанию)</label>
                  <input
                    type="number"
                    step="0.1"
                    min="0.01"
                    value={gfForm.tpStepPercent}
                    onChange={(e) => setGfForm({ ...gfForm, tpStepPercent: e.target.value })}
                    className={inputCls}
                  />
                  <p className="text-xs text-text-secondary mt-0.5">Используется для тиров без своего TP-шага</p>
                </div>
                <div>
                  <label className={labelCls}>Шаг DCA, % (по умолчанию)</label>
                  <input
                    type="number"
                    step="0.1"
                    min="0.01"
                    value={gfForm.dcaStepPercent}
                    onChange={(e) => setGfForm({ ...gfForm, dcaStepPercent: e.target.value })}
                    className={inputCls}
                  />
                  <p className="text-xs text-text-secondary mt-0.5">Используется для тиров без своего DCA-шага</p>
                </div>
              </div>

              {/* Tiers — expanding zones, each can override DCA/TP step */}
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
                      {gfTiers.map((t, i) => {
                        const prevUp = i === 0 ? '0' : gfTiers[i - 1].upTo || '?';
                        return (
                          <tr key={i} className="border-t border-border/50">
                            <td className="px-2 py-1.5 text-text-secondary">T{i + 1}</td>
                            <td className="px-2 py-1.5 text-text-secondary">{prevUp}</td>
                            <td className="px-2 py-1.5">
                              <input
                                type="number"
                                step="0.5"
                                min="0.1"
                                value={t.upTo}
                                onChange={(e) => setGfTiers((prev) =>
                                  prev.map((row, idx) => idx === i ? { ...row, upTo: e.target.value } : row))}
                                className="w-16 bg-bg-tertiary border border-border rounded px-1.5 py-1 text-text-primary"
                              />
                            </td>
                            <td className="px-2 py-1.5">
                              <input
                                type="number"
                                step="1"
                                min="1"
                                value={t.size}
                                onChange={(e) => setGfTiers((prev) =>
                                  prev.map((row, idx) => idx === i ? { ...row, size: e.target.value } : row))}
                                className="w-16 bg-bg-tertiary border border-border rounded px-1.5 py-1 text-text-primary"
                              />
                            </td>
                            <td className="px-2 py-1.5">
                              <input
                                type="number"
                                step="0.1"
                                min="0.01"
                                value={t.dca}
                                placeholder={gfForm.dcaStepPercent}
                                onChange={(e) => setGfTiers((prev) =>
                                  prev.map((row, idx) => idx === i ? { ...row, dca: e.target.value } : row))}
                                className="w-14 bg-bg-tertiary border border-border rounded px-1.5 py-1 text-text-primary placeholder:text-text-secondary/40"
                              />
                            </td>
                            <td className="px-2 py-1.5">
                              <input
                                type="number"
                                step="0.1"
                                min="0.01"
                                value={t.tp}
                                placeholder={gfForm.tpStepPercent}
                                onChange={(e) => setGfTiers((prev) =>
                                  prev.map((row, idx) => idx === i ? { ...row, tp: e.target.value } : row))}
                                className="w-14 bg-bg-tertiary border border-border rounded px-1.5 py-1 text-text-primary placeholder:text-text-secondary/40"
                              />
                            </td>
                            <td className="px-2 py-1.5 text-right">
                              {gfTiers.length > 1 && (
                                <button
                                  type="button"
                                  onClick={() => setGfTiers((prev) => prev.filter((_, idx) => idx !== i))}
                                  className="px-1.5 py-0.5 text-accent-red hover:bg-accent-red/10 rounded"
                                  title="Удалить ярус"
                                >
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
                <button
                  type="button"
                  onClick={() => setGfTiers((prev) => {
                    const last = prev[prev.length - 1];
                    const nextUp = last && Number(last.upTo) > 0 ? Number(last.upTo) * 2 : 10;
                    const nextSize = last && Number(last.size) > 0 ? Number(last.size) * 2 : 100;
                    return [...prev, { upTo: String(nextUp), size: String(nextSize), dca: '', tp: '' }];
                  })}
                  className="mt-2 px-3 py-1.5 text-xs font-medium bg-bg-tertiary text-text-secondary rounded-lg hover:bg-bg-tertiary/70 transition-colors"
                >
                  + Добавить ярус
                </button>
                <p className="text-xs text-text-secondary mt-1.5">
                  Каждый ярус — отдельный участок: внутри него DCA расставляются с шагом этого яруса
                  (или глобального, если поле пустое), а TP каждого фила = его цена ± шаг_TP_яруса.
                  Якорь использует параметры первого яруса.
                </p>
              </div>

              {/* Static range toggle */}
              <label className="flex items-start gap-2 cursor-pointer select-none">
                <input
                  type="checkbox"
                  checked={gfForm.useStaticRange}
                  onChange={(e) => setGfForm({ ...gfForm, useStaticRange: e.target.checked })}
                  className="w-4 h-4 mt-0.5 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer"
                />
                <div>
                  <span className="text-sm font-medium text-text-primary">Статический диапазон</span>
                  <p className="text-xs text-text-secondary mt-0.5">
                    Граница фиксируется на первом якоре. На новых якорях число DCA-уровней
                    меняется (например 9, 10, 11) — сетка не "плавает" вместе с ценой. По
                    умолчанию (выключено) — динамический диапазон: каждый новый якорь пересчитывает сетку.
                  </p>
                </div>
              </label>

              <p className="text-xs text-text-secondary italic">
                Уровней DCA на старте: ~{(() => {
                  const fallback = Math.max(0.0001, Number(gfForm.dcaStepPercent));
                  let total = 0;
                  let prev = 0;
                  for (const row of gfTiers) {
                    const upTo = Number(row.upTo);
                    if (!(upTo > 0)) continue;
                    const tierStep = row.dca.trim() === '' ? fallback : Number(row.dca);
                    if (tierStep > 0 && upTo > prev) total += Math.floor((upTo - prev) / tierStep);
                    prev = upTo;
                  }
                  return total > 0 ? total : '?';
                })()}.
                Каждый фил (якорь и доборы) получает свой собственный reduce-only лимит на закрытие
                в плюс шаг_TP того яруса, в который попадает offset от якоря. Размер фила и шаг DCA
                тоже берутся из соответствующего яруса. Когда срабатывает TP — DCA-слот на этом
                уровне переустанавливается. Полное закрытие всех батчей → отмена всех лимиток
                → ожидание следующего бара → новый якорь. Стоп-лосса нет.
              </p>
            </>
          ) : strategyType === 'GridHedge' ? (
            <>
              {/* Mode selector */}
              <div>
                <label className={labelCls}>Режим</label>
                <select
                  value={ghForm.mode}
                  onChange={(e) => setGhForm({ ...ghForm, mode: Number(e.target.value) as 1 | 2 })}
                  className={inputCls}
                >
                  <option value={1}>SameTicker — Spot grid + same-symbol futures short (Bybit-only)</option>
                  <option value={2}>CrossTicker — Futures grid + correlated-ticker futures short</option>
                </select>
              </div>

              {/* HedgeSymbol — only shown for CrossTicker */}
              {ghForm.mode === 2 && (
                <div>
                  <label className={labelCls}>Тикер хеджа</label>
                  <input
                    type="text"
                    value={ghForm.hedgeSymbol}
                    onChange={(e) => setGhForm({ ...ghForm, hedgeSymbol: e.target.value })}
                    placeholder="BTCUSDT"
                    className={inputCls}
                  />
                  <p className="text-xs text-text-secondary mt-0.5">
                    Коррелированный тикер для шорт-хеджа (например BTCUSDT при гриде на ETHUSDT).
                  </p>
                </div>
              )}

              {/* Диапазон вниз / вверх */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Диапазон вниз %</label>
                  <input type="number" step="0.5" min="0.1"
                    value={ghForm.rangePercent}
                    onChange={(e) => setGhForm({ ...ghForm, rangePercent: e.target.value })}
                    className={inputCls} />
                  <p className="text-xs text-text-secondary mt-0.5">Стоп-лосс при цене ниже anchor × (1 − R%)</p>
                </div>
                <div>
                  <label className={labelCls}>Диапазон вверх %</label>
                  <input type="number" step="0.5" min="0.1"
                    value={ghForm.upperExitPercent}
                    onChange={(e) => setGhForm({ ...ghForm, upperExitPercent: e.target.value })}
                    className={inputCls} />
                  <p className="text-xs text-text-secondary mt-0.5">TP всего бота при цене выше anchor × (1 + U%)</p>
                </div>
              </div>

              {/* DCA + TP steps */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Шаг DCA %</label>
                  <input type="number" step="0.1" min="0.01"
                    value={ghForm.dcaStepPercent}
                    onChange={(e) => setGhForm({ ...ghForm, dcaStepPercent: e.target.value })}
                    className={inputCls} />
                </div>
                <div>
                  <label className={labelCls}>Шаг TP %</label>
                  <input type="number" step="0.1" min="0.01"
                    value={ghForm.tpStepPercent}
                    onChange={(e) => setGhForm({ ...ghForm, tpStepPercent: e.target.value })}
                    className={inputCls} />
                </div>
              </div>

              {/* Ставка на уровень */}
              <div>
                <label className={labelCls}>Ставка на уровень, USDT</label>
                <input type="number" step="1" min="1"
                  value={ghForm.betUsdt}
                  onChange={(e) => setGhForm({ ...ghForm, betUsdt: e.target.value })}
                  className={inputCls} />
              </div>

              {/* Recommendation panel */}
              {(() => {
                const R = Number(ghForm.rangePercent);
                const step = Number(ghForm.dcaStepPercent);
                const G = Number(ghForm.betUsdt);
                const rec = gridHedgeRecommendation(R, step, G);
                if (!rec) return null;
                const hedgeR = Math.round(rec.hedge);
                const maxL = Math.round(rec.maxLoss);
                return (
                  <div className="rounded-lg border border-accent-blue/30 bg-accent-blue/5 p-3 text-xs space-y-1">
                    <div className="font-medium text-accent-blue">
                      Рекомендуемый хедж: ~${hedgeR}
                    </div>
                    <div className="text-text-secondary">
                      Максимальный убыток (при просадке до −{R}%): ~${maxL}
                    </div>
                    <div className="text-text-secondary/70 italic">
                      Расчёт: ставка ${G} × ({(2*R-1).toFixed(0)}/{step}) исполнений × коэф. 0.88. При меньшем шаге уровней больше — хедж и убыток растут пропорционально.
                    </div>
                  </div>
                );
              })()}

              {/* Размер хеджа */}
              <div>
                <label className={labelCls}>Размер хеджа, USDT</label>
                <div className="flex gap-2">
                  <input
                    type="number"
                    step="1"
                    min="0"
                    value={ghForm.hedgeNotionalUsdt}
                    onChange={(e) => setGhForm({ ...ghForm, hedgeNotionalUsdt: e.target.value })}
                    className={inputCls + ' flex-1'}
                  />
                  <button
                    type="button"
                    onClick={() => {
                      const rec = gridHedgeRecommendation(Number(ghForm.rangePercent), Number(ghForm.dcaStepPercent), Number(ghForm.betUsdt));
                      if (rec) setGhForm({ ...ghForm, hedgeNotionalUsdt: String(Math.round(rec.hedge)) });
                    }}
                    className="px-3 py-2 text-xs font-medium bg-accent-blue/15 text-accent-blue rounded-lg hover:bg-accent-blue/25 transition-colors whitespace-nowrap"
                  >
                    Применить рекомендацию
                  </button>
                </div>
                <p className="text-xs text-text-secondary mt-0.5">
                  0 = без хеджа. Бот откроет SHORT на эту сумму одной маркет-сделкой при старте.
                </p>
              </div>

              {/* Leverages */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Плечо хеджа</label>
                  <input type="number" step="1" min="1"
                    value={ghForm.hedgeLeverage}
                    onChange={(e) => setGhForm({ ...ghForm, hedgeLeverage: e.target.value })}
                    className={inputCls} />
                </div>
                <div>
                  <label className={labelCls}>Плечо грида (CrossTicker)</label>
                  <input type="number" step="1" min="1"
                    value={ghForm.gridLeverage}
                    disabled={ghForm.mode === 1}
                    onChange={(e) => setGhForm({ ...ghForm, gridLeverage: e.target.value })}
                    className={inputCls + (ghForm.mode === 1 ? ' opacity-40' : '')} />
                  <p className="text-xs text-text-secondary mt-0.5">Игнорируется в SameTicker (грид на споте — без плеча)</p>
                </div>
              </div>

              <p className="text-xs text-text-secondary italic">
                Сетка лонгов ниже якоря. Хедж SHORT на фьючерсах того же или коррелированного тикера, открывается одной маркет-сделкой.
                Каждый филл сетки имеет свой reduce-only TP. Триггеры верх/низ закрывают весь бот.
                После Done — Stop → Start чтобы начать новый цикл.
              </p>
            </>
          ) : strategyType === 'HuntingFunding' ? (
            <>
              {/* Auto-rotate ticker */}
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={autoRotateTicker}
                  onChange={(e) => setAutoRotateTicker(e.target.checked)}
                  className="rounded border-dark-border"
                />
                <span className="text-sm text-text-primary">Автообновление тикера</span>
                <span className="text-xs text-text-secondary">(по макс. фандингу)</span>
              </label>

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
  const isSD = strategy.type === 'SmaDca';
  const isFC = strategy.type === 'FundingClaim';
  const isGF = strategy.type === 'GridFloat';
  const isGH = strategy.type === 'GridHedge';

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

  // SMA DCA form state
  const [sdForm, setSdForm] = useState({
    timeframe: cfg.timeframe || '1h',
    direction: cfg.direction || 'Long',
    smaPeriod: String(cfg.smaPeriod ?? 50),
    takeProfitPercent: String(cfg.takeProfitPercent ?? 1.0),
    positionSizeUsd: String(cfg.positionSizeUsd ?? 100),
    dcaTriggerBase: cfg.dcaTriggerBase || 'Average',
    orderType: cfg.orderType || 'Market',
    entryLimitOffsetPercent: String(cfg.entryLimitOffsetPercent ?? 0.05),
  });
  // Migrate legacy scalar fields → tiered levels on load
  const [sdLevels, setSdLevels] = useState<Array<{ stepPercent: string; multiplier: string; count: string }>>(
    Array.isArray(cfg.levels) && cfg.levels.length > 0
      ? cfg.levels.map((l: SmaDcaLevel) => ({
          stepPercent: String(l.stepPercent),
          multiplier: String(l.multiplier),
          count: String(l.count),
        }))
      : [{ stepPercent: String(cfg.dcaStepPercent ?? 3.0), multiplier: String(cfg.dcaMultiplier ?? 3.0), count: String(cfg.maxDcaLevels ?? 5) }],
  );

  // HuntingFunding form state
  const [hfLevels, setHfLevels] = useState<HFLevel[]>(
    Array.isArray(cfg.levels) && cfg.levels.length > 0
      ? cfg.levels
      : [{ offsetPercent: 1.5, sizeUsdt: 50 }],
  );
  const [autoRotateTicker, setAutoRotateTicker] = useState(cfg.autoRotateTicker !== false);
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

  // FundingClaim form state
  const [fcForm, setFcForm] = useState({
    maxCycles: String(cfg.maxCycles ?? 0),
    checkBeforeFundingMinutes: String(cfg.checkBeforeFundingMinutes ?? 10),
  });
  const [fcAutoRotate, setFcAutoRotate] = useState(cfg.autoRotateTicker !== false);

  // GridFloat form state. Auto-migrate legacy single-zone config (baseSizeUsdt+rangePercent)
  // into a single-tier list so the editor always works against the new shape.
  const [gfForm, setGfForm] = useState({
    timeframe: cfg.timeframe || '1h',
    direction: cfg.direction || 'Long',
    dcaStepPercent: String(cfg.dcaStepPercent ?? 1),
    tpStepPercent: String(cfg.tpStepPercent ?? 1),
    leverage: String(cfg.leverage ?? 1),
    useStaticRange: cfg.useStaticRange ?? false,
  });
  const [gfTiers, setGfTiers] = useState<Array<{ upTo: string; size: string; dca: string; tp: string }>>(() => {
    if (Array.isArray(cfg.tiers) && cfg.tiers.length > 0) {
      const sorted = [...cfg.tiers]
        .filter((t: { upToPercent: number; sizeUsdt: number }) =>
          Number(t.upToPercent) > 0 && Number(t.sizeUsdt) > 0)
        .sort((a: { upToPercent: number }, b: { upToPercent: number }) => a.upToPercent - b.upToPercent);
      if (sorted.length > 0) {
        return sorted.map((t: { upToPercent: number; sizeUsdt: number; dcaStepPercent?: number | null; tpStepPercent?: number | null }) => ({
          upTo: String(t.upToPercent),
          size: String(t.sizeUsdt),
          dca: typeof t.dcaStepPercent === 'number' && t.dcaStepPercent > 0 ? String(t.dcaStepPercent) : '',
          tp: typeof t.tpStepPercent === 'number' && t.tpStepPercent > 0 ? String(t.tpStepPercent) : '',
        }));
      }
    }
    // Legacy bot — synthesise a single tier from the old fields.
    return [{
      upTo: String(cfg.rangePercent ?? 10),
      size: String(cfg.baseSizeUsdt ?? 100),
      dca: '',
      tp: '',
    }];
  });

  // GridHedge form state — initialise from existing configJson
  const [ghForm, setGhForm] = useState({
    mode: (cfg.mode ?? 1) as 1 | 2,
    hedgeSymbol: cfg.hedgeSymbol || '',
    rangePercent: String(cfg.rangePercent ?? 10),
    upperExitPercent: String(cfg.upperExitPercent ?? 10),
    dcaStepPercent: String(cfg.dcaStepPercent ?? 1),
    tpStepPercent: String(cfg.tpStepPercent ?? 1),
    betUsdt: String(cfg.betUsdt ?? 100),
    hedgeNotionalUsdt: String(cfg.hedgeNotionalUsdt ?? 836),
    hedgeLeverage: String(cfg.hedgeLeverage ?? 5),
    gridLeverage: String(cfg.gridLeverage ?? 1),
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
        autoRotateTicker,
      });
    } else if (isSD) {
      configJson = JSON.stringify({
        symbol: symbol.replace(/\s+/g, '').toUpperCase(),
        timeframe: sdForm.timeframe,
        direction: sdForm.direction,
        smaPeriod: Number(sdForm.smaPeriod),
        takeProfitPercent: Number(sdForm.takeProfitPercent),
        positionSizeUsd: Number(sdForm.positionSizeUsd),
        dcaTriggerBase: sdForm.dcaTriggerBase,
        orderType: sdForm.orderType,
        entryLimitOffsetPercent: Number(sdForm.entryLimitOffsetPercent),
        levels: sdLevels.map((l) => ({
          stepPercent: Number(l.stepPercent),
          multiplier: Number(l.multiplier),
          count: Number(l.count),
        })),
      });
    } else if (isFC) {
      configJson = JSON.stringify({
        symbol: symbol.replace(/\s+/g, '').toUpperCase(),
        maxCycles: Number(fcForm.maxCycles),
        autoRotateTicker: fcAutoRotate,
        checkBeforeFundingMinutes: Number(fcForm.checkBeforeFundingMinutes),
      });
    } else if (isGF) {
      const tiers = gfTiers
        .map((t) => {
          const upToPercent = Number(t.upTo);
          const sizeUsdt = Number(t.size);
          const dcaStepPercent = t.dca.trim() === '' ? null : Number(t.dca);
          const tpStepPercent = t.tp.trim() === '' ? null : Number(t.tp);
          return { upToPercent, sizeUsdt, dcaStepPercent, tpStepPercent };
        })
        .filter((t) => t.upToPercent > 0 && t.sizeUsdt > 0)
        .sort((a, b) => a.upToPercent - b.upToPercent);
      if (tiers.length === 0) {
        setError('Добавьте хотя бы один ярус с upTo% > 0 и ставкой > 0');
        return;
      }
      for (let i = 1; i < tiers.length; i++) {
        if (tiers[i].upToPercent <= tiers[i - 1].upToPercent) {
          setError('Ярусы должны идти со строго возрастающим upTo%');
          return;
        }
      }
      for (const t of tiers) {
        if (t.dcaStepPercent !== null && !(t.dcaStepPercent > 0)) {
          setError('Per-tier шаг DCA должен быть > 0 (или оставьте пустым)');
          return;
        }
        if (t.tpStepPercent !== null && !(t.tpStepPercent > 0)) {
          setError('Per-tier шаг TP должен быть > 0 (или оставьте пустым)');
          return;
        }
      }
      const tiersPayload = tiers.map((t) => ({
        upToPercent: t.upToPercent,
        sizeUsdt: t.sizeUsdt,
        ...(t.dcaStepPercent !== null ? { dcaStepPercent: t.dcaStepPercent } : {}),
        ...(t.tpStepPercent !== null ? { tpStepPercent: t.tpStepPercent } : {}),
      }));
      configJson = JSON.stringify({
        symbol: symbol.replace(/\s+/g, '').toUpperCase(),
        timeframe: gfForm.timeframe,
        direction: gfForm.direction,
        tiers: tiersPayload,
        dcaStepPercent: Number(gfForm.dcaStepPercent),
        tpStepPercent: Number(gfForm.tpStepPercent),
        leverage: Number(gfForm.leverage),
        useStaticRange: gfForm.useStaticRange,
      });
    } else if (isGH) {
      if (Number(ghForm.rangePercent) <= 0 || Number(ghForm.upperExitPercent) <= 0) {
        setError('Диапазоны вниз/вверх должны быть > 0');
        return;
      }
      if (Number(ghForm.dcaStepPercent) <= 0 || Number(ghForm.tpStepPercent) <= 0) {
        setError('Шаги DCA / TP должны быть > 0');
        return;
      }
      if (Number(ghForm.betUsdt) <= 0) {
        setError('Ставка на уровень должна быть > 0');
        return;
      }
      if (Number(ghForm.hedgeNotionalUsdt) < 0) {
        setError('Размер хеджа не может быть отрицательным');
        return;
      }
      if (ghForm.mode === 2 && !ghForm.hedgeSymbol.trim()) {
        setError('Cross-Ticker режим требует hedgeSymbol (например BTCUSDT)');
        return;
      }
      configJson = JSON.stringify({
        mode: ghForm.mode,
        gridSymbol: symbol.replace(/\s+/g, '').toUpperCase(),
        hedgeSymbol: ghForm.mode === 2 ? ghForm.hedgeSymbol.replace(/\s+/g, '').toUpperCase() : '',
        rangePercent: Number(ghForm.rangePercent),
        upperExitPercent: Number(ghForm.upperExitPercent),
        dcaStepPercent: Number(ghForm.dcaStepPercent),
        tpStepPercent: Number(ghForm.tpStepPercent),
        betUsdt: Number(ghForm.betUsdt),
        hedgeNotionalUsdt: Number(ghForm.hedgeNotionalUsdt),
        hedgeLeverage: Number(ghForm.hedgeLeverage),
        gridLeverage: Number(ghForm.gridLeverage),
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

          {isSD ? (
            <>
              <div>
                <label className={labelCls}>Таймфрейм</label>
                <select
                  value={sdForm.timeframe}
                  onChange={(e) => setSdForm({ ...sdForm, timeframe: e.target.value })}
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

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Направление</label>
                  <select
                    value={sdForm.direction}
                    onChange={(e) => setSdForm({ ...sdForm, direction: e.target.value })}
                    className={inputCls}
                  >
                    <option value="Long">Long</option>
                    <option value="Short">Short</option>
                  </select>
                </div>
                <div>
                  <label className={labelCls}>Период SMA</label>
                  <input
                    type="number"
                    min="2"
                    value={sdForm.smaPeriod}
                    onChange={(e) => setSdForm({ ...sdForm, smaPeriod: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Размер входа (USD)</label>
                  <input
                    type="number"
                    step="1"
                    min="1"
                    value={sdForm.positionSizeUsd}
                    onChange={(e) => setSdForm({ ...sdForm, positionSizeUsd: e.target.value })}
                    className={inputCls}
                  />
                </div>
                <div>
                  <label className={labelCls}>Take Profit %</label>
                  <input
                    type="number"
                    step="0.1"
                    min="0.01"
                    value={sdForm.takeProfitPercent}
                    onChange={(e) => setSdForm({ ...sdForm, takeProfitPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* DCA tiers */}
              <div>
                <label className={labelCls}>Уровни DCA</label>
                <div className="space-y-2">
                  {sdLevels.map((lvl, i) => (
                    <div key={i} className="grid grid-cols-[1fr_1fr_1fr_auto] gap-2 items-end">
                      <div>
                        <label className={labelCls}>DCA шаг, %</label>
                        <input
                          type="number"
                          step="0.1"
                          min="0.01"
                          value={lvl.stepPercent}
                          onChange={(e) => setSdLevels(sdLevels.map((l, idx) => idx === i ? { ...l, stepPercent: e.target.value } : l))}
                          className={inputCls}
                        />
                      </div>
                      <div>
                        <label className={labelCls}>Множитель</label>
                        <input
                          type="number"
                          step="0.1"
                          min="0.1"
                          value={lvl.multiplier}
                          onChange={(e) => setSdLevels(sdLevels.map((l, idx) => idx === i ? { ...l, multiplier: e.target.value } : l))}
                          className={inputCls}
                        />
                      </div>
                      <div>
                        <label className={labelCls}>Доборов</label>
                        <input
                          type="number"
                          min="1"
                          value={lvl.count}
                          onChange={(e) => setSdLevels(sdLevels.map((l, idx) => idx === i ? { ...l, count: e.target.value } : l))}
                          className={inputCls}
                        />
                      </div>
                      <button
                        type="button"
                        disabled={sdLevels.length <= 1}
                        onClick={() => setSdLevels(sdLevels.filter((_, idx) => idx !== i))}
                        className="pb-0.5 text-accent-red/70 hover:text-accent-red disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
                        title="Удалить уровень"
                      >
                        <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                        </svg>
                      </button>
                    </div>
                  ))}
                </div>
                <button
                  type="button"
                  onClick={() => setSdLevels([...sdLevels, { stepPercent: '5.0', multiplier: '2.0', count: '2' }])}
                  className="mt-2 text-xs text-accent-blue hover:text-accent-blue/80 transition-colors"
                >
                  + Добавить уровень
                </button>
              </div>

              <div>
                <label className={labelCls}>База расчёта шага DCA</label>
                <select
                  value={sdForm.dcaTriggerBase}
                  onChange={(e) => setSdForm({ ...sdForm, dcaTriggerBase: e.target.value })}
                  className={inputCls}
                >
                  <option value="Average">От средней цены входа (сетка сжимается)</option>
                  <option value="LastFill">От последней докупки (сетка равномерная)</option>
                </select>
              </div>

              {/* Order type: market vs limit (DCA only — first entry is always market) */}
              <div>
                <label className={labelCls}>Тип ордеров для DCA (докупок)</label>
                <select
                  value={sdForm.orderType}
                  onChange={(e) => setSdForm({ ...sdForm, orderType: e.target.value })}
                  className={inputCls}
                >
                  <option value="Market">Market — рыночные (taker, гарантированный фил)</option>
                  <option value="Limit">Limit — лимитные maker (экономия комиссии)</option>
                </select>
                <p className="text-xs text-text-secondary mt-1 italic">
                  Первый вход в позицию всегда выполняется рыночным ордером.
                </p>
              </div>

              {sdForm.orderType === 'Limit' && (
                <div>
                  <label className={labelCls}>Оффсет лимитки DCA, %</label>
                  <input
                    type="number"
                    step="0.01"
                    min="0.01"
                    value={sdForm.entryLimitOffsetPercent}
                    onChange={(e) => setSdForm({ ...sdForm, entryLimitOffsetPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
              )}
            </>
          ) : isFC ? (
            <>
              {/* Auto-rotate ticker */}
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={fcAutoRotate}
                  onChange={(e) => setFcAutoRotate(e.target.checked)}
                  className="rounded border-dark-border"
                />
                <span className="text-sm text-text-primary">Автообновление тикера</span>
                <span className="text-xs text-text-secondary">(по макс. фандингу)</span>
              </label>

              {/* Check before + Max cycles */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Проверка за (мин)</label>
                  <input
                    type="number"
                    min="1"
                    value={fcForm.checkBeforeFundingMinutes}
                    onChange={(e) => setFcForm({ ...fcForm, checkBeforeFundingMinutes: e.target.value })}
                    className={inputCls}
                  />
                  <p className="text-xs text-text-secondary mt-0.5">Минут до фандинга для проверки ставки</p>
                </div>
                <div>
                  <label className={labelCls}>Макс. циклов <span className="font-normal">(0 = ∞)</span></label>
                  <input
                    type="number"
                    min="0"
                    value={fcForm.maxCycles}
                    onChange={(e) => setFcForm({ ...fcForm, maxCycles: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              <p className="text-xs text-text-secondary italic">
                Бот автоматически определяет направление по знаку фандинга: шорт при положительном, лонг при отрицательном.
                Позиция открывается маркет-ордером и держится для сбора фандинговых выплат.
              </p>
            </>
          ) : isGF ? (
            <>
              <div>
                <label className={labelCls}>Таймфрейм</label>
                <select
                  value={gfForm.timeframe}
                  onChange={(e) => setGfForm({ ...gfForm, timeframe: e.target.value })}
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

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Направление</label>
                  <select
                    value={gfForm.direction}
                    onChange={(e) => setGfForm({ ...gfForm, direction: e.target.value })}
                    className={inputCls}
                  >
                    <option value="Long">Long</option>
                    <option value="Short">Short</option>
                  </select>
                </div>
                <div>
                  <label className={labelCls}>Плечо</label>
                  <input
                    type="number"
                    min="1"
                    step="1"
                    value={gfForm.leverage}
                    onChange={(e) => setGfForm({ ...gfForm, leverage: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Шаг TP, % (по умолчанию)</label>
                  <input
                    type="number"
                    step="0.1"
                    min="0.01"
                    value={gfForm.tpStepPercent}
                    onChange={(e) => setGfForm({ ...gfForm, tpStepPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
                <div>
                  <label className={labelCls}>Шаг DCA, % (по умолчанию)</label>
                  <input
                    type="number"
                    step="0.1"
                    min="0.01"
                    value={gfForm.dcaStepPercent}
                    onChange={(e) => setGfForm({ ...gfForm, dcaStepPercent: e.target.value })}
                    className={inputCls}
                  />
                </div>
              </div>

              {/* Tiers — expanding zones, each can override DCA/TP step */}
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
                      {gfTiers.map((t, i) => {
                        const prevUp = i === 0 ? '0' : gfTiers[i - 1].upTo || '?';
                        return (
                          <tr key={i} className="border-t border-border/50">
                            <td className="px-2 py-1.5 text-text-secondary">T{i + 1}</td>
                            <td className="px-2 py-1.5 text-text-secondary">{prevUp}</td>
                            <td className="px-2 py-1.5">
                              <input
                                type="number"
                                step="0.5"
                                min="0.1"
                                value={t.upTo}
                                onChange={(e) => setGfTiers((prev) =>
                                  prev.map((row, idx) => idx === i ? { ...row, upTo: e.target.value } : row))}
                                className="w-16 bg-bg-tertiary border border-border rounded px-1.5 py-1 text-text-primary"
                              />
                            </td>
                            <td className="px-2 py-1.5">
                              <input
                                type="number"
                                step="1"
                                min="1"
                                value={t.size}
                                onChange={(e) => setGfTiers((prev) =>
                                  prev.map((row, idx) => idx === i ? { ...row, size: e.target.value } : row))}
                                className="w-16 bg-bg-tertiary border border-border rounded px-1.5 py-1 text-text-primary"
                              />
                            </td>
                            <td className="px-2 py-1.5">
                              <input
                                type="number"
                                step="0.1"
                                min="0.01"
                                value={t.dca}
                                placeholder={gfForm.dcaStepPercent}
                                onChange={(e) => setGfTiers((prev) =>
                                  prev.map((row, idx) => idx === i ? { ...row, dca: e.target.value } : row))}
                                className="w-14 bg-bg-tertiary border border-border rounded px-1.5 py-1 text-text-primary placeholder:text-text-secondary/40"
                              />
                            </td>
                            <td className="px-2 py-1.5">
                              <input
                                type="number"
                                step="0.1"
                                min="0.01"
                                value={t.tp}
                                placeholder={gfForm.tpStepPercent}
                                onChange={(e) => setGfTiers((prev) =>
                                  prev.map((row, idx) => idx === i ? { ...row, tp: e.target.value } : row))}
                                className="w-14 bg-bg-tertiary border border-border rounded px-1.5 py-1 text-text-primary placeholder:text-text-secondary/40"
                              />
                            </td>
                            <td className="px-2 py-1.5 text-right">
                              {gfTiers.length > 1 && (
                                <button
                                  type="button"
                                  onClick={() => setGfTiers((prev) => prev.filter((_, idx) => idx !== i))}
                                  className="px-1.5 py-0.5 text-accent-red hover:bg-accent-red/10 rounded"
                                  title="Удалить ярус"
                                >
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
                <button
                  type="button"
                  onClick={() => setGfTiers((prev) => {
                    const last = prev[prev.length - 1];
                    const nextUp = last && Number(last.upTo) > 0 ? Number(last.upTo) * 2 : 10;
                    const nextSize = last && Number(last.size) > 0 ? Number(last.size) * 2 : 100;
                    return [...prev, { upTo: String(nextUp), size: String(nextSize), dca: '', tp: '' }];
                  })}
                  className="mt-2 px-3 py-1.5 text-xs font-medium bg-bg-tertiary text-text-secondary rounded-lg hover:bg-bg-tertiary/70 transition-colors"
                >
                  + Добавить ярус
                </button>
                <p className="text-xs text-text-secondary mt-1.5">
                  Каждый ярус — отдельный участок: внутри него DCA расставляются с шагом этого яруса
                  (или глобального, если поле пустое), а TP каждого фила = его цена ± шаг_TP_яруса.
                  Якорь использует параметры первого яруса.
                </p>
              </div>

              <label className="flex items-start gap-2 cursor-pointer select-none">
                <input
                  type="checkbox"
                  checked={gfForm.useStaticRange}
                  onChange={(e) => setGfForm({ ...gfForm, useStaticRange: e.target.checked })}
                  className="w-4 h-4 mt-0.5 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer"
                />
                <div>
                  <span className="text-sm font-medium text-text-primary">Статический диапазон</span>
                  <p className="text-xs text-text-secondary mt-0.5">
                    Граница фиксируется на первом якоре после старта (число DCA-уровней дрейфует на новых якорях).
                    По умолчанию (выключено) — динамический диапазон: каждый новый якорь пересчитывает сетку.
                  </p>
                </div>
              </label>
            </>
          ) : isGH ? (
            <>
              {/* Mode selector */}
              <div>
                <label className={labelCls}>Режим</label>
                <select
                  value={ghForm.mode}
                  onChange={(e) => setGhForm({ ...ghForm, mode: Number(e.target.value) as 1 | 2 })}
                  className={inputCls}
                >
                  <option value={1}>SameTicker — Spot grid + same-symbol futures short (Bybit-only)</option>
                  <option value={2}>CrossTicker — Futures grid + correlated-ticker futures short</option>
                </select>
              </div>

              {/* HedgeSymbol — only for CrossTicker */}
              {ghForm.mode === 2 && (
                <div>
                  <label className={labelCls}>Тикер хеджа</label>
                  <input
                    type="text"
                    value={ghForm.hedgeSymbol}
                    onChange={(e) => setGhForm({ ...ghForm, hedgeSymbol: e.target.value })}
                    placeholder="BTCUSDT"
                    className={inputCls}
                  />
                  <p className="text-xs text-text-secondary mt-0.5">
                    Коррелированный тикер для шорт-хеджа (например BTCUSDT при гриде на ETHUSDT).
                  </p>
                </div>
              )}

              {/* Диапазон вниз / вверх */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Диапазон вниз %</label>
                  <input type="number" step="0.5" min="0.1"
                    value={ghForm.rangePercent}
                    onChange={(e) => setGhForm({ ...ghForm, rangePercent: e.target.value })}
                    className={inputCls} />
                  <p className="text-xs text-text-secondary mt-0.5">Стоп-лосс при цене ниже anchor × (1 − R%)</p>
                </div>
                <div>
                  <label className={labelCls}>Диапазон вверх %</label>
                  <input type="number" step="0.5" min="0.1"
                    value={ghForm.upperExitPercent}
                    onChange={(e) => setGhForm({ ...ghForm, upperExitPercent: e.target.value })}
                    className={inputCls} />
                  <p className="text-xs text-text-secondary mt-0.5">TP всего бота при цене выше anchor × (1 + U%)</p>
                </div>
              </div>

              {/* DCA + TP steps */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Шаг DCA %</label>
                  <input type="number" step="0.1" min="0.01"
                    value={ghForm.dcaStepPercent}
                    onChange={(e) => setGhForm({ ...ghForm, dcaStepPercent: e.target.value })}
                    className={inputCls} />
                </div>
                <div>
                  <label className={labelCls}>Шаг TP %</label>
                  <input type="number" step="0.1" min="0.01"
                    value={ghForm.tpStepPercent}
                    onChange={(e) => setGhForm({ ...ghForm, tpStepPercent: e.target.value })}
                    className={inputCls} />
                </div>
              </div>

              {/* Ставка на уровень */}
              <div>
                <label className={labelCls}>Ставка на уровень, USDT</label>
                <input type="number" step="1" min="1"
                  value={ghForm.betUsdt}
                  onChange={(e) => setGhForm({ ...ghForm, betUsdt: e.target.value })}
                  className={inputCls} />
              </div>

              {/* Recommendation panel */}
              {(() => {
                const R = Number(ghForm.rangePercent);
                const step = Number(ghForm.dcaStepPercent);
                const G = Number(ghForm.betUsdt);
                const rec = gridHedgeRecommendation(R, step, G);
                if (!rec) return null;
                const hedgeR = Math.round(rec.hedge);
                const maxL = Math.round(rec.maxLoss);
                return (
                  <div className="rounded-lg border border-accent-blue/30 bg-accent-blue/5 p-3 text-xs space-y-1">
                    <div className="font-medium text-accent-blue">
                      Рекомендуемый хедж: ~${hedgeR}
                    </div>
                    <div className="text-text-secondary">
                      Максимальный убыток (при просадке до −{R}%): ~${maxL}
                    </div>
                    <div className="text-text-secondary/70 italic">
                      Расчёт: ставка ${G} × ({(2*R-1).toFixed(0)}/{step}) исполнений × коэф. 0.88. При меньшем шаге уровней больше — хедж и убыток растут пропорционально.
                    </div>
                  </div>
                );
              })()}

              {/* Размер хеджа */}
              <div>
                <label className={labelCls}>Размер хеджа, USDT</label>
                <div className="flex gap-2">
                  <input
                    type="number"
                    step="1"
                    min="0"
                    value={ghForm.hedgeNotionalUsdt}
                    onChange={(e) => setGhForm({ ...ghForm, hedgeNotionalUsdt: e.target.value })}
                    className={inputCls + ' flex-1'}
                  />
                  <button
                    type="button"
                    onClick={() => {
                      const rec = gridHedgeRecommendation(Number(ghForm.rangePercent), Number(ghForm.dcaStepPercent), Number(ghForm.betUsdt));
                      if (rec) setGhForm({ ...ghForm, hedgeNotionalUsdt: String(Math.round(rec.hedge)) });
                    }}
                    className="px-3 py-2 text-xs font-medium bg-accent-blue/15 text-accent-blue rounded-lg hover:bg-accent-blue/25 transition-colors whitespace-nowrap"
                  >
                    Применить рекомендацию
                  </button>
                </div>
                <p className="text-xs text-text-secondary mt-0.5">
                  0 = без хеджа. Бот откроет SHORT на эту сумму одной маркет-сделкой при старте.
                </p>
              </div>

              {/* Leverages */}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className={labelCls}>Плечо хеджа</label>
                  <input type="number" step="1" min="1"
                    value={ghForm.hedgeLeverage}
                    onChange={(e) => setGhForm({ ...ghForm, hedgeLeverage: e.target.value })}
                    className={inputCls} />
                </div>
                <div>
                  <label className={labelCls}>Плечо грида (CrossTicker)</label>
                  <input type="number" step="1" min="1"
                    value={ghForm.gridLeverage}
                    disabled={ghForm.mode === 1}
                    onChange={(e) => setGhForm({ ...ghForm, gridLeverage: e.target.value })}
                    className={inputCls + (ghForm.mode === 1 ? ' opacity-40' : '')} />
                  <p className="text-xs text-text-secondary mt-0.5">Игнорируется в SameTicker (грид на споте — без плеча)</p>
                </div>
              </div>

              <p className="text-xs text-text-secondary italic">
                Сетка лонгов ниже якоря. Хедж SHORT на фьючерсах того же или коррелированного тикера, открывается одной маркет-сделкой.
                Каждый филл сетки имеет свой reduce-only TP. Триггеры верх/низ закрывают весь бот.
                После Done — Stop → Start чтобы начать новый цикл.
              </p>
            </>
          ) : isHF ? (
            <>
              {/* Auto-rotate ticker */}
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={autoRotateTicker}
                  onChange={(e) => setAutoRotateTicker(e.target.checked)}
                  className="rounded border-dark-border"
                />
                <span className="text-sm text-text-primary">Автообновление тикера</span>
                <span className="text-xs text-text-secondary">(по макс. фандингу)</span>
              </label>

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
