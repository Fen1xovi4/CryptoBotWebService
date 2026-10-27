import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import api from '../../api/client';
import { describeError } from './errors';
import { applyParamsToConfig, countRangeValues } from './optimization';
import { buildPresetRows } from './optimizationPresets';
import type { PresetRow } from './optimizationPresets';
import { PARAM_SETS_QUERY_KEY } from './SavedParamSetsPanel';
import type {
  OptimizationComboResult,
  OptimizationStatus,
  OptimizeRequest,
  SaveTesterParamSetRequest,
  SimulateRequest,
  SimulationSummary,
  StrategyType,
} from './types';

/** Per-parameter sweep settings the user edited; persisted per strategy in localStorage. */
interface RowState {
  on: boolean;
  from: string;
  to: string;
  step: string;
}

interface Props {
  strategyType: StrategyType;
  /** Built from the current form; undefined while the form is invalid. */
  baseConfigJson?: string;
  buildRequestBody: () => { body?: SimulateRequest; error?: string };
  /** Full single simulation of one combination (charts + trades in the regular results area). */
  onSimulateConfig: (configJson: string) => void;
  simulatePending: boolean;
}

// Mirrors ConfigJsonMutator.MaxCombinations on the backend.
const MAX_COMBINATIONS = 100_000;
// Above this the sweep is likely to take tens of minutes — warn, don't block.
const SLOW_COMBINATIONS = 20_000;
const PAGE_SIZES = [100, 200, 500, 1000, 2000];
const OOS_OPTIONS = [0, 20, 30, 40, 50];
const ACTIVE_STATUSES = new Set(['Queued', 'Downloading', 'Running']);
const oosStorageKey = 'tester.optimizer.oos';
const minTradesStorageKey = 'tester.optimizer.minTrades';

function loadNumber(key: string, fallback: number): number {
  try {
    const v = Number(localStorage.getItem(key));
    return Number.isFinite(v) && localStorage.getItem(key) !== null ? v : fallback;
  } catch {
    return fallback;
  }
}
function storeNumber(key: string, v: number) {
  try {
    localStorage.setItem(key, String(v));
  } catch {
    /* ignore */
  }
}

const inputCls =
  'bg-bg-primary border border-border rounded-lg px-3 py-2 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all';

function pnlToDd(s: SimulationSummary): number {
  const dd = Math.abs(s.maxDrawdownUsd);
  if (dd < 1e-9) return s.netPnlUsd > 0 ? Number.POSITIVE_INFINITY : 0;
  return s.netPnlUsd / dd;
}

function fmtRatio(v: number): string {
  return v === Number.POSITIVE_INFINITY ? '∞' : v.toFixed(2);
}

function fmtUsd(v: number): string {
  return `${v >= 0 ? '+' : ''}${v.toFixed(2)}$`;
}

const storageKey = (st: StrategyType) => `tester.optimizer.${st}`;

function loadRowState(st: StrategyType): Record<string, RowState> {
  try {
    const raw = localStorage.getItem(storageKey(st));
    return raw ? (JSON.parse(raw) as Record<string, RowState>) : {};
  } catch {
    return {};
  }
}

function saveRowState(st: StrategyType, state: Record<string, RowState>) {
  try {
    localStorage.setItem(storageKey(st), JSON.stringify(state));
  } catch {
    /* storage unavailable — settings just won't persist */
  }
}

export default function OptimizationPanel({ strategyType, baseConfigJson, buildRequestBody, onSimulateConfig, simulatePending }: Props) {
  const [expanded, setExpanded] = useState(false);
  const [rowState, setRowState] = useState<Record<string, RowState>>(() => loadRowState(strategyType));
  const [jobId, setJobId] = useState<string | null>(null);
  const [localError, setLocalError] = useState('');
  const [sortKey, setSortKey] = useState('netPnlUsd');
  const [sortAsc, setSortAsc] = useState(false);
  const [pageSize, setPageSize] = useState(200);
  const [oosPercent, setOosPercent] = useState(() => loadNumber(oosStorageKey, 30));
  const [minTrades, setMinTrades] = useState(() => loadNumber(minTradesStorageKey, 0));
  const [savedIds, setSavedIds] = useState<Set<number>>(new Set());
  const queryClient = useQueryClient();

  // Switching strategy swaps the catalogue and the remembered tick/range state.
  useEffect(() => {
    setRowState(loadRowState(strategyType));
  }, [strategyType]);

  const presetRows: PresetRow[] = useMemo(
    () => (baseConfigJson ? buildPresetRows(strategyType, baseConfigJson) : []),
    [strategyType, baseConfigJson],
  );

  /** Effective row: user state when present, catalogue defaults otherwise. */
  const effective = (r: PresetRow): RowState =>
    rowState[r.path] ?? { on: r.defaultOn, from: r.from, to: r.to, step: r.step };

  const patchRow = (r: PresetRow, p: Partial<RowState>) =>
    setRowState((prev) => {
      const next = { ...prev, [r.path]: { ...effective(r), ...p } };
      saveRowState(strategyType, next);
      return next;
    });

  const resetRow = (r: PresetRow) =>
    setRowState((prev) => {
      const next = { ...prev };
      delete next[r.path];
      saveRowState(strategyType, next);
      return next;
    });

  const setAll = (on: boolean) =>
    setRowState((prev) => {
      const next = { ...prev };
      for (const r of presetRows) next[r.path] = { ...(prev[r.path] ?? effective(r)), on };
      saveRowState(strategyType, next);
      return next;
    });

  const activeRows = presetRows.filter((r) => effective(r).on);

  const { data: status } = useQuery<OptimizationStatus>({
    queryKey: ['tester-optimize', jobId, sortKey, sortAsc, pageSize, minTrades],
    queryFn: () =>
      api
        .get(`/tester/optimize/${jobId}`, { params: { sortBy: sortKey, desc: !sortAsc, limit: pageSize, minTrades } })
        .then((r) => r.data),
    enabled: !!jobId,
    // Sorting is server-side; keep the previous page on screen while the re-sorted one loads.
    placeholderData: (prev) => prev,
    refetchInterval: (query) => {
      const s = query.state.data?.status;
      return s && !ACTIVE_STATUSES.has(s) ? false : 1500;
    },
  });
  const isActive = !!jobId && (!status || ACTIVE_STATUSES.has(status.status));

  const startMutation = useMutation({
    mutationFn: (body: OptimizeRequest) =>
      api.post('/tester/optimize', body).then((r) => r.data as { jobId: string; totalCombinations: number }),
    onSuccess: (d) => {
      queryClient.removeQueries({ queryKey: ['tester-optimize'] });
      setSavedIds(new Set());
      setJobId(d.jobId);
    },
  });

  const cancelMutation = useMutation({
    mutationFn: () => api.post(`/tester/optimize/${jobId}/cancel`),
  });

  const saveMutation = useMutation({
    mutationFn: ({ body }: { body: SaveTesterParamSetRequest; rowIndex: number }) =>
      api.post('/tester/param-sets', body).then((r) => r.data),
    onSuccess: (_d, vars) => {
      setSavedIds((prev) => new Set(prev).add(vars.rowIndex));
      queryClient.invalidateQueries({ queryKey: [PARAM_SETS_QUERY_KEY] });
    },
  });

  const comboCount = useMemo(() => {
    if (activeRows.length === 0) return null;
    let total = 1;
    for (const r of activeRows) {
      const e = effective(r);
      const n = countRangeValues(Number(e.from), Number(e.to), Number(e.step));
      if (n === null) return null;
      total *= n;
      if (total > 10 * MAX_COMBINATIONS) return total; // stop multiplying huge numbers
    }
    return total;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeRows, rowState]);

  const handleStart = () => {
    setLocalError('');
    const req = buildRequestBody();
    if (!req.body) {
      setLocalError(req.error ?? 'Некорректная конфигурация');
      return;
    }
    if (activeRows.length === 0) {
      setLocalError('Отметьте хотя бы один параметр для перебора');
      return;
    }
    for (const r of activeRows) {
      const e = effective(r);
      const n = countRangeValues(Number(e.from), Number(e.to), Number(e.step));
      if (n === null) {
        setLocalError(`«${r.label}»: заполните от/до/шаг (шаг > 0, «до» ≥ «от»)`);
        return;
      }
    }
    if (comboCount !== null && comboCount > MAX_COMBINATIONS) {
      setLocalError(`Слишком много комбинаций (${comboCount.toLocaleString('ru-RU')} > ${MAX_COMBINATIONS.toLocaleString('ru-RU')}) — снимите галочки или увеличьте шаги`);
      return;
    }
    const body: OptimizeRequest = {
      ...req.body,
      parameters: activeRows.map((r) => {
        const e = effective(r);
        return { path: r.path, from: Number(e.from), to: Number(e.to), step: Number(e.step) };
      }),
      outOfSamplePercent: oosPercent,
    };
    // With a hold-out the default ranking is the robust one: the worse of the two PnLs.
    setSortKey(oosPercent > 0 ? 'minNetPnlUsd' : 'netPnlUsd');
    setSortAsc(false);
    startMutation.mutate(body);
  };

  const handleSave = (r: OptimizationComboResult & { summary: SimulationSummary }, rowIndex: number) => {
    const req = buildRequestBody();
    if (!req.body || !baseConfigJson) {
      setLocalError(req.error ?? 'Некорректная конфигурация');
      return;
    }
    const paramsText = Object.entries(r.parameters).map(([k, v]) => `${k.split('.').pop()}=${v}`).join(' ');
    const defaultName = `${req.body.symbol} ${strategyType} ${paramsText}`.slice(0, 200);
    const name = window.prompt('Название набора параметров', defaultName);
    if (name === null) return;
    const oos = r.oosSummary;
    const note = oos
      ? `In-sample ${fmtUsd(r.summary.netPnlUsd)} (${r.summary.totalTrades} сд., DD ${fmtUsd(-Math.abs(r.summary.maxDrawdownUsd))}) · ` +
        `out-of-sample ${status?.outOfSamplePercent ?? ''}%: ${fmtUsd(oos.netPnlUsd)} (${oos.totalTrades} сд., DD ${fmtUsd(-Math.abs(oos.maxDrawdownUsd))})`
      : null;
    saveMutation.mutate({
      rowIndex,
      body: {
        accountId: req.body.accountId,
        symbol: req.body.symbol,
        strategyType,
        name: name.trim() || defaultName,
        note,
        configJson: applyParamsToConfig(baseConfigJson, r.parameters),
        sweptParams: r.parameters,
        pathTimeframe: req.body.pathTimeframe,
        summary: r.summary,
      },
    });
  };

  const okResults = useMemo(
    () => (status?.results ?? []).filter((r): r is OptimizationComboResult & { summary: SimulationSummary } => !!r.summary),
    [status],
  );

  const paramPaths = useMemo(
    () => (okResults.length ? Object.keys(okResults[0].parameters) : []),
    [okResults],
  );

  const clickSort = (key: string) => {
    if (sortKey === key) setSortAsc((v) => !v);
    else {
      setSortKey(key);
      setSortAsc(false);
    }
  };

  const sortMark = (key: string) => (sortKey === key ? (sortAsc ? ' ▲' : ' ▼') : '');

  const headerCls = 'text-left font-medium pr-3 py-1.5 cursor-pointer select-none whitespace-nowrap hover:text-text-primary';
  const oosHeaderCls = `${headerCls} text-accent-blue/80`;
  const hasOos = (status?.outOfSamplePercent ?? 0) > 0;
  const fmtDay = (iso?: string | null) => (iso ? iso.slice(0, 10) : '?');

  const eta =
    status?.status === 'Running' && status.completed > 0
      ? Math.round((status.elapsedSeconds / status.completed) * (status.total - status.completed))
      : null;

  const fmtEta = (sec: number) => (sec >= 120 ? `${Math.round(sec / 60)} мин` : `${sec}с`);

  return (
    <div className="bg-bg-secondary rounded-xl border border-border p-4 mb-4 space-y-3">
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        className="flex items-center gap-2 text-xs font-semibold text-text-secondary uppercase tracking-widest hover:text-text-primary transition-colors"
      >
        <span>{expanded ? '▾' : '▸'}</span>
        Оптимизация параметров
        {!expanded && activeRows.length > 0 && (
          <span className="normal-case tracking-normal">
            ({activeRows.length} парам.{comboCount !== null ? `, ${comboCount.toLocaleString('ru-RU')} комб.` : ''})
          </span>
        )}
      </button>

      {expanded && (
        <>
          <p className="text-xs text-text-secondary">
            Отметьте параметры стратегии, которые нужно перебрать, и задайте диапазоны. Остальные берутся из формы как есть.
            История скачивается один раз, комбинации считаются параллельно в памяти (grid search).
            Лимит — {MAX_COMBINATIONS.toLocaleString('ru-RU')} комбинаций; настройки галочек запоминаются для каждой стратегии.
          </p>

          {!baseConfigJson && (
            <p className="text-xs text-accent-yellow">
              ⚠ Конфигурация стратегии сейчас невалидна — исправьте параметры стратегии выше, чтобы выбрать поля.
            </p>
          )}

          {presetRows.length > 0 && (
            <div className="flex items-center gap-3 text-xs">
              <button type="button" onClick={() => setAll(true)} className="text-accent-blue hover:underline">отметить все</button>
              <button type="button" onClick={() => setAll(false)} className="text-accent-blue hover:underline">снять все</button>
              <button
                type="button"
                onClick={() => { setRowState({}); saveRowState(strategyType, {}); }}
                className="text-text-secondary hover:underline"
                title="Вернуть галочки и диапазоны по умолчанию для этой стратегии"
              >
                сбросить к умолчаниям
              </button>
            </div>
          )}

          {/* Parameter catalogue */}
          <div className="overflow-x-auto">
            <table className="text-xs w-full">
              <thead className="text-text-secondary">
                <tr>
                  <th className="w-8" />
                  <th className="text-left font-medium pr-3 py-1.5">Параметр</th>
                  <th className="text-left font-medium pr-3 py-1.5">Сейчас</th>
                  <th className="text-left font-medium pr-3 py-1.5">От</th>
                  <th className="text-left font-medium pr-3 py-1.5">До</th>
                  <th className="text-left font-medium pr-3 py-1.5">Шаг</th>
                  <th className="text-left font-medium pr-3 py-1.5">Значений</th>
                  <th />
                </tr>
              </thead>
              <tbody className="text-text-primary">
                {presetRows.map((r) => {
                  const e = effective(r);
                  const count = countRangeValues(Number(e.from), Number(e.to), Number(e.step));
                  const edited = !!rowState[r.path];
                  return (
                    <tr key={r.path} className={`border-t border-border/60 ${e.on ? '' : 'opacity-60'}`}>
                      <td className="py-1.5">
                        <input
                          type="checkbox"
                          checked={e.on}
                          onChange={(ev) => patchRow(r, { on: ev.target.checked })}
                          className="w-4 h-4 rounded border-border bg-bg-tertiary text-accent-blue focus:ring-accent-blue/50 cursor-pointer"
                        />
                      </td>
                      <td className="pr-3 py-1.5">
                        <div className={r.unknown ? 'font-mono' : ''}>{r.label}</div>
                        <div className="text-[11px] text-text-secondary font-mono">
                          {r.unknown ? 'нет в каталоге' : r.path}
                          {r.hint ? <span className="font-sans"> · {r.hint}</span> : null}
                        </div>
                      </td>
                      <td className="pr-3 py-1.5 font-mono">{r.current}</td>
                      <td className="pr-3 py-1.5">
                        <input type="number" value={e.from} step="any" disabled={!e.on}
                          onChange={(ev) => patchRow(r, { from: ev.target.value })}
                          className={`${inputCls} w-[96px] font-mono py-1`} />
                      </td>
                      <td className="pr-3 py-1.5">
                        <input type="number" value={e.to} step="any" disabled={!e.on}
                          onChange={(ev) => patchRow(r, { to: ev.target.value })}
                          className={`${inputCls} w-[96px] font-mono py-1`} />
                      </td>
                      <td className="pr-3 py-1.5">
                        <input type="number" value={e.step} step="any" disabled={!e.on}
                          onChange={(ev) => patchRow(r, { step: ev.target.value })}
                          className={`${inputCls} w-[96px] font-mono py-1`} />
                      </td>
                      <td className={`pr-3 py-1.5 font-mono ${count === null && e.on ? 'text-accent-red' : 'text-text-secondary'}`}>
                        {count === null ? '—' : count}
                      </td>
                      <td className="py-1.5">
                        {edited && (
                          <button type="button" onClick={() => resetRow(r)} className="text-[11px] text-text-secondary hover:underline" title="Вернуть диапазон по умолчанию">
                            сброс
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          <div className="flex flex-wrap items-center gap-4">
            <label className="flex items-center gap-2 text-xs text-text-secondary" title="Защита от переобучения: хвост периода не участвует в подборе и прогоняется отдельно. Набор, который зарабатывает только на обучающей части, подогнан под шум.">
              Проверочный период (out-of-sample)
              <select
                value={oosPercent}
                onChange={(e) => { const v = Number(e.target.value); setOosPercent(v); storeNumber(oosStorageKey, v); }}
                className={`${inputCls} py-1 px-2`}
              >
                {OOS_OPTIONS.map((p) => (
                  <option key={p} value={p}>{p === 0 ? 'выкл' : `${p}% в конце`}</option>
                ))}
              </select>
              {oosPercent > 0 && <span className="font-mono">×2 прогонов</span>}
            </label>

            {comboCount !== null && (
              <span className={`text-xs font-mono ${comboCount > MAX_COMBINATIONS ? 'text-accent-red' : comboCount > SLOW_COMBINATIONS ? 'text-accent-yellow' : 'text-text-secondary'}`}>
                Комбинаций: {comboCount.toLocaleString('ru-RU')}
                {comboCount > MAX_COMBINATIONS
                  ? ` (лимит ${MAX_COMBINATIONS.toLocaleString('ru-RU')})`
                  : comboCount > SLOW_COMBINATIONS
                    ? ' — большой перебор, может занять десятки минут'
                    : ''}
              </span>
            )}

            <button
              onClick={handleStart}
              disabled={!baseConfigJson || activeRows.length === 0 || isActive || startMutation.isPending}
              className="bg-accent-blue hover:bg-accent-blue/80 disabled:opacity-50 text-white px-4 py-2 rounded-lg text-sm font-medium transition-colors"
            >
              {isActive ? 'Оптимизация…' : 'Запустить оптимизацию'}
            </button>

            {isActive && (
              <button
                type="button"
                onClick={() => cancelMutation.mutate()}
                className="text-xs text-accent-red hover:underline"
              >
                Отменить
              </button>
            )}
          </div>

          {(localError || startMutation.isError || saveMutation.isError) && (
            <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg">
              {localError || (startMutation.isError ? describeError(startMutation.error) : describeError(saveMutation.error))}
            </div>
          )}

          {/* Progress */}
          {status && ACTIVE_STATUSES.has(status.status) && (
            <div className="space-y-1.5">
              <div className="flex items-center gap-3 text-xs text-text-secondary">
                <span>
                  {status.status === 'Downloading' || status.status === 'Queued'
                    ? 'Загрузка истории…'
                    : `Прогон комбинаций: ${status.completed.toLocaleString('ru-RU')} / ${status.total.toLocaleString('ru-RU')}`}
                </span>
                <span className="font-mono">{Math.round(status.elapsedSeconds)}с</span>
                {eta !== null && <span className="font-mono">≈ ещё {fmtEta(eta)}</span>}
              </div>
              <div className="h-2 rounded-full bg-bg-primary border border-border overflow-hidden">
                <div
                  className="h-full bg-accent-blue transition-all"
                  style={{ width: `${status.status === 'Running' && status.total > 0 ? Math.round((status.completed / status.total) * 100) : 3}%` }}
                />
              </div>
            </div>
          )}

          {status?.status === 'Failed' && (
            <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg">
              Оптимизация упала: {status.error ?? 'неизвестная ошибка'}
            </div>
          )}
          {status?.status === 'Cancelled' && (
            <p className="text-xs text-text-secondary">Оптимизация отменена.</p>
          )}

          {status?.status === 'Done' && (
            <div className="space-y-2">
              {status.history && (
                <p className="text-[11px] text-text-secondary">
                  Готово за {fmtEta(Math.round(status.elapsedSeconds))}: {status.total.toLocaleString('ru-RU')} комбинаций ·
                  история: {status.history.candlesFromCache.toLocaleString('ru-RU')} из кэша,{' '}
                  {status.history.candlesDownloaded.toLocaleString('ru-RU')} скачано
                </p>
              )}
              {status.warnings.length > 0 && (
                <div className="bg-accent-yellow/10 border border-accent-yellow/30 rounded-lg px-4 py-2 space-y-1">
                  {status.warnings.map((w, i) => (
                    <p key={i} className="text-xs text-accent-yellow">⚠ {w}</p>
                  ))}
                </div>
              )}
              {status.failedCount > 0 && (
                <p className="text-xs text-accent-yellow">
                  ⚠ {status.failedCount.toLocaleString('ru-RU')} комбинаций упали с ошибкой
                  {status.firstError ? ` (первая: ${status.firstError})` : ''}
                </p>
              )}

              {hasOos && (
                <p className="text-[11px] text-text-secondary">
                  Обучение (in-sample): {fmtDay(status.inSampleFromUtc)} → {fmtDay(status.inSampleToUtc)} · проверка (out-of-sample, {status.outOfSamplePercent}%): {fmtDay(status.outOfSampleFromUtc)} → {fmtDay(status.outOfSampleToUtc)}.
                  Колонки OOS (синие) — результат на проверочном периоде; «min PnL» — худший из двух, надёжный набор хорош по обоим.
                </p>
              )}

              {okResults.length === 0 && status.okCount === 0 ? (
                <p className="text-xs text-text-secondary">Нет успешных комбинаций.</p>
              ) : (
                <div className="overflow-x-auto">
                  <div className="flex flex-wrap items-center gap-3 text-[11px] text-text-secondary mb-1">
                    <span>
                      Показаны {okResults.length.toLocaleString('ru-RU')} из {(status.okCount - status.filteredOutCount).toLocaleString('ru-RU')} по сортировке «{sortKey.startsWith('p:') ? sortKey.slice(2) : sortKey}» — сортировка на сервере, клик по заголовку пересортирует весь набор.
                      {status.filteredOutCount > 0 ? ` Скрыто фильтром по сделкам: ${status.filteredOutCount.toLocaleString('ru-RU')}.` : ''}
                    </span>
                    <label className="flex items-center gap-1">
                      строк:
                      <select value={pageSize} onChange={(e) => setPageSize(Number(e.target.value))} className={`${inputCls} py-0.5 px-2 text-[11px]`}>
                        {PAGE_SIZES.map((n) => <option key={n} value={n}>{n}</option>)}
                      </select>
                    </label>
                    <label className="flex items-center gap-1" title="Комбинации с меньшим числом сделок (на обучении и на проверке) скрываются — на нескольких сделках статистики нет">
                      мин. сделок:
                      <input
                        type="number" min={0} step={1} value={minTrades}
                        onChange={(e) => { const v = Math.max(0, Math.floor(Number(e.target.value) || 0)); setMinTrades(v); storeNumber(minTradesStorageKey, v); }}
                        className={`${inputCls} py-0.5 px-2 text-[11px] w-[64px] font-mono`}
                      />
                    </label>
                  </div>
                  {okResults.length === 0 ? (
                    <p className="text-xs text-text-secondary">Все комбинации скрыты фильтром «мин. сделок».</p>
                  ) : (
                  <table className="text-xs w-full">
                    <thead className="text-text-secondary">
                      <tr>
                        <th className="text-left font-medium pr-3 py-1.5">#</th>
                        {paramPaths.map((p) => (
                          <th key={p} className={headerCls} onClick={() => clickSort(`p:${p}`)} title={p}>
                            <span className="font-mono">{p}</span>{sortMark(`p:${p}`)}
                          </th>
                        ))}
                        {hasOos && (
                          <th className={headerCls} onClick={() => clickSort('minNetPnlUsd')} title="Худший из in-sample и out-of-sample Net PnL">min PnL{sortMark('minNetPnlUsd')}</th>
                        )}
                        <th className={headerCls} onClick={() => clickSort('netPnlUsd')}>Net PnL{sortMark('netPnlUsd')}</th>
                        <th className={headerCls} onClick={() => clickSort('maxDrawdownUsd')}>DD ${sortMark('maxDrawdownUsd')}</th>
                        <th className={headerCls} onClick={() => clickSort('maxDrawdownPercent')}>DD %{sortMark('maxDrawdownPercent')}</th>
                        <th className={headerCls} onClick={() => clickSort('pnlToDd')}>PnL/DD{sortMark('pnlToDd')}</th>
                        <th className={headerCls} onClick={() => clickSort('winRate')}>WinRate{sortMark('winRate')}</th>
                        <th className={headerCls} onClick={() => clickSort('totalTrades')}>Сделок{sortMark('totalTrades')}</th>
                        <th className={headerCls} onClick={() => clickSort('feesUsd')}>Комиссии{sortMark('feesUsd')}</th>
                        <th className={headerCls} onClick={() => clickSort('maxNotionalUsd')}>Пик ном.{sortMark('maxNotionalUsd')}</th>
                        <th className={headerCls} onClick={() => clickSort('unrealizedPnlAtEndUsd')}>Незафикс.{sortMark('unrealizedPnlAtEndUsd')}</th>
                        {hasOos && (
                          <>
                            <th className={oosHeaderCls} onClick={() => clickSort('oos:netPnlUsd')}>OOS PnL{sortMark('oos:netPnlUsd')}</th>
                            <th className={oosHeaderCls} onClick={() => clickSort('oos:maxDrawdownUsd')}>OOS DD ${sortMark('oos:maxDrawdownUsd')}</th>
                            <th className={oosHeaderCls} onClick={() => clickSort('oos:pnlToDd')}>OOS PnL/DD{sortMark('oos:pnlToDd')}</th>
                            <th className={oosHeaderCls} onClick={() => clickSort('oos:winRate')}>OOS WinRate{sortMark('oos:winRate')}</th>
                            <th className={oosHeaderCls} onClick={() => clickSort('oos:totalTrades')}>OOS сделок{sortMark('oos:totalTrades')}</th>
                          </>
                        )}
                        <th />
                      </tr>
                    </thead>
                    <tbody className="text-text-primary">
                      {okResults.map((r, i) => (
                        <tr key={i} className="border-t border-border/60">
                          <td className="pr-3 py-1.5 text-text-secondary">{i + 1}</td>
                          {paramPaths.map((p) => (
                            <td key={p} className="pr-3 py-1.5 font-mono">{r.parameters[p]}</td>
                          ))}
                          {hasOos && (
                            <td className={`pr-3 py-1.5 font-mono font-semibold ${Math.min(r.summary.netPnlUsd, r.oosSummary?.netPnlUsd ?? 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                              {fmtUsd(Math.min(r.summary.netPnlUsd, r.oosSummary?.netPnlUsd ?? 0))}
                            </td>
                          )}
                          <td className={`pr-3 py-1.5 font-mono ${r.summary.netPnlUsd >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                            {fmtUsd(r.summary.netPnlUsd)}
                          </td>
                          <td className="pr-3 py-1.5 font-mono text-red-400">{fmtUsd(-Math.abs(r.summary.maxDrawdownUsd))}</td>
                          <td className="pr-3 py-1.5 font-mono text-red-400">{r.summary.maxDrawdownPercent.toFixed(2)}%</td>
                          <td className="pr-3 py-1.5 font-mono">{fmtRatio(pnlToDd(r.summary))}</td>
                          <td className="pr-3 py-1.5 font-mono">{r.summary.winRate.toFixed(1)}%</td>
                          <td className="pr-3 py-1.5 font-mono">{r.summary.totalTrades}</td>
                          <td className="pr-3 py-1.5 font-mono">{r.summary.feesUsd.toFixed(2)}$</td>
                          <td className="pr-3 py-1.5 font-mono">{r.summary.maxNotionalUsd.toFixed(0)}$</td>
                          <td className={`pr-3 py-1.5 font-mono ${r.summary.unrealizedPnlAtEndUsd >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                            {fmtUsd(r.summary.unrealizedPnlAtEndUsd)}
                          </td>
                          {hasOos && (r.oosSummary ? (
                            <>
                              <td className={`pr-3 py-1.5 font-mono bg-accent-blue/5 ${r.oosSummary.netPnlUsd >= 0 ? 'text-green-400' : 'text-red-400'}`}>{fmtUsd(r.oosSummary.netPnlUsd)}</td>
                              <td className="pr-3 py-1.5 font-mono bg-accent-blue/5 text-red-400">{fmtUsd(-Math.abs(r.oosSummary.maxDrawdownUsd))}</td>
                              <td className="pr-3 py-1.5 font-mono bg-accent-blue/5">{fmtRatio(pnlToDd(r.oosSummary))}</td>
                              <td className="pr-3 py-1.5 font-mono bg-accent-blue/5">{r.oosSummary.winRate.toFixed(1)}%</td>
                              <td className="pr-3 py-1.5 font-mono bg-accent-blue/5">{r.oosSummary.totalTrades}</td>
                            </>
                          ) : (
                            <td colSpan={5} className="pr-3 py-1.5 text-text-secondary">—</td>
                          ))}
                          <td className="py-1.5 whitespace-nowrap">
                            <button
                              type="button"
                              disabled={!baseConfigJson || simulatePending}
                              onClick={() => baseConfigJson && onSimulateConfig(applyParamsToConfig(baseConfigJson, r.parameters))}
                              className="text-accent-blue hover:underline disabled:opacity-50 mr-3"
                              title="Полная симуляция этой комбинации — график, сделки и эквити в блоке результатов"
                            >
                              симулировать
                            </button>
                            <button
                              type="button"
                              disabled={!baseConfigJson || saveMutation.isPending}
                              onClick={() => handleSave(r, i)}
                              className="text-accent-blue hover:underline disabled:opacity-50 mr-3"
                              title="Сохранить этот набор параметров для инструмента и стратегии"
                            >
                              {savedIds.has(i) ? 'сохранено ✓' : 'сохранить'}
                            </button>
                            <button
                              type="button"
                              disabled={!baseConfigJson}
                              onClick={() => baseConfigJson && navigator.clipboard.writeText(applyParamsToConfig(baseConfigJson, r.parameters))}
                              className="text-text-secondary hover:underline disabled:opacity-50"
                              title="Скопировать полный configJson этой комбинации"
                            >
                              копировать
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  )}
                </div>
              )}
            </div>
          )}
        </>
      )}
    </div>
  );
}
