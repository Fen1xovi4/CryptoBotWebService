import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import api from '../../api/client';
import { describeError } from './errors';
import { applyParamsToConfig, collectNumericPaths, countRangeValues, suggestRange } from './optimization';
import type {
  OptimizationComboResult,
  OptimizationStatus,
  OptimizeRequest,
  SimulateRequest,
  SimulationSummary,
} from './types';

interface ParamRow {
  path: string;
  from: string;
  to: string;
  step: string;
}

interface Props {
  /** Built from the current form; undefined while the form is invalid. */
  baseConfigJson?: string;
  buildRequestBody: () => { body?: SimulateRequest; error?: string };
  /** Full single simulation of one combination (charts + trades in the regular results area). */
  onSimulateConfig: (configJson: string) => void;
  simulatePending: boolean;
}

const MAX_COMBINATIONS = 3000;
const MAX_TABLE_ROWS = 200;
const ACTIVE_STATUSES = new Set(['Queued', 'Downloading', 'Running']);

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

export default function OptimizationPanel({ baseConfigJson, buildRequestBody, onSimulateConfig, simulatePending }: Props) {
  const [expanded, setExpanded] = useState(false);
  const [rows, setRows] = useState<ParamRow[]>([]);
  const [jobId, setJobId] = useState<string | null>(null);
  const [localError, setLocalError] = useState('');
  const [sortKey, setSortKey] = useState('netPnlUsd');
  const [sortAsc, setSortAsc] = useState(false);
  const queryClient = useQueryClient();

  const numericPaths = useMemo(
    () => (baseConfigJson ? collectNumericPaths(baseConfigJson) : []),
    [baseConfigJson],
  );
  const valueByPath = useMemo(
    () => new Map(numericPaths.map((p) => [p.path, p.value])),
    [numericPaths],
  );

  const { data: status } = useQuery<OptimizationStatus>({
    queryKey: ['tester-optimize', jobId],
    queryFn: () => api.get(`/tester/optimize/${jobId}`).then((r) => r.data),
    enabled: !!jobId,
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
      setJobId(d.jobId);
      queryClient.removeQueries({ queryKey: ['tester-optimize'] });
    },
  });

  const cancelMutation = useMutation({
    mutationFn: () => api.post(`/tester/optimize/${jobId}/cancel`),
  });

  const comboCount = useMemo(() => {
    if (rows.length === 0) return null;
    let total = 1;
    for (const r of rows) {
      const n = countRangeValues(Number(r.from), Number(r.to), Number(r.step));
      if (n === null) return null;
      total *= n;
      if (total > 10 * MAX_COMBINATIONS) return total; // stop multiplying huge numbers
    }
    return total;
  }, [rows]);

  const addRow = () => {
    const used = new Set(rows.map((r) => r.path));
    const next = numericPaths.find((p) => !used.has(p.path));
    if (!next) return;
    setRows((prev) => [...prev, { path: next.path, ...suggestRange(next.value) }]);
  };

  const changeRowPath = (i: number, path: string) => {
    const value = valueByPath.get(path);
    setRows((prev) =>
      prev.map((r, j) => (j === i ? { path, ...(value !== undefined ? suggestRange(value) : { from: r.from, to: r.to, step: r.step }) } : r)),
    );
  };

  const handleStart = () => {
    setLocalError('');
    const req = buildRequestBody();
    if (!req.body) {
      setLocalError(req.error ?? 'Некорректная конфигурация');
      return;
    }
    if (rows.length === 0) {
      setLocalError('Добавьте хотя бы один варьируемый параметр');
      return;
    }
    for (const r of rows) {
      const n = countRangeValues(Number(r.from), Number(r.to), Number(r.step));
      if (n === null) {
        setLocalError(`Параметр ${r.path}: заполните от/до/шаг (шаг > 0, «до» ≥ «от»)`);
        return;
      }
    }
    if (comboCount !== null && comboCount > MAX_COMBINATIONS) {
      setLocalError(`Слишком много комбинаций (${comboCount.toLocaleString('ru-RU')} > ${MAX_COMBINATIONS}) — уменьшите диапазоны или увеличьте шаги`);
      return;
    }
    const body: OptimizeRequest = {
      ...req.body,
      parameters: rows.map((r) => ({ path: r.path, from: Number(r.from), to: Number(r.to), step: Number(r.step) })),
    };
    startMutation.mutate(body);
  };

  const okResults = useMemo(
    () => (status?.results ?? []).filter((r): r is OptimizationComboResult & { summary: SimulationSummary } => !!r.summary),
    [status],
  );
  const failedResults = useMemo(() => (status?.results ?? []).filter((r) => !r.summary), [status]);

  const paramPaths = useMemo(
    () => (okResults.length ? Object.keys(okResults[0].parameters) : []),
    [okResults],
  );

  const sorted = useMemo(() => {
    const metric = (r: OptimizationComboResult & { summary: SimulationSummary }): number => {
      if (sortKey.startsWith('p:')) return r.parameters[sortKey.slice(2)] ?? 0;
      if (sortKey === 'pnlToDd') return pnlToDd(r.summary);
      const v = (r.summary as unknown as Record<string, number>)[sortKey];
      return typeof v === 'number' ? v : 0;
    };
    return [...okResults].sort((a, b) => (metric(a) - metric(b)) * (sortAsc ? 1 : -1));
  }, [okResults, sortKey, sortAsc]);

  const clickSort = (key: string) => {
    if (sortKey === key) setSortAsc((v) => !v);
    else {
      setSortKey(key);
      setSortAsc(false);
    }
  };

  const sortMark = (key: string) => (sortKey === key ? (sortAsc ? ' ▲' : ' ▼') : '');

  const headerCls = 'text-left font-medium pr-3 py-1.5 cursor-pointer select-none whitespace-nowrap hover:text-text-primary';

  const eta =
    status?.status === 'Running' && status.completed > 0
      ? Math.round((status.elapsedSeconds / status.completed) * (status.total - status.completed))
      : null;

  return (
    <div className="bg-bg-secondary rounded-xl border border-border p-4 mb-4 space-y-3">
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        className="flex items-center gap-2 text-xs font-semibold text-text-secondary uppercase tracking-widest hover:text-text-primary transition-colors"
      >
        <span>{expanded ? '▾' : '▸'}</span>
        Оптимизация параметров
        {!expanded && rows.length > 0 && <span className="normal-case tracking-normal">({rows.length} парам.)</span>}
      </button>

      {expanded && (
        <>
          <p className="text-xs text-text-secondary">
            Перебор комбинаций значений выбранных полей конфига по одной и той же истории (grid search).
            История скачивается один раз, дальше прогоны идут параллельно в памяти. Лимит — {MAX_COMBINATIONS} комбинаций.
          </p>

          {!baseConfigJson && (
            <p className="text-xs text-accent-yellow">
              ⚠ Конфигурация стратегии сейчас невалидна — исправьте параметры стратегии выше, чтобы выбрать поля.
            </p>
          )}

          {/* Parameter rows */}
          <div className="space-y-2">
            {rows.map((row, i) => {
              const currentValue = valueByPath.get(row.path);
              const count = countRangeValues(Number(row.from), Number(row.to), Number(row.step));
              const stale = baseConfigJson !== undefined && currentValue === undefined;
              return (
                <div key={i} className="flex flex-wrap items-center gap-2">
                  <select
                    value={row.path}
                    onChange={(e) => changeRowPath(i, e.target.value)}
                    className={`${inputCls} min-w-[260px] font-mono text-xs`}
                  >
                    {stale && <option value={row.path}>{row.path} (нет в конфиге!)</option>}
                    {numericPaths
                      .filter((p) => p.path === row.path || !rows.some((r2) => r2.path === p.path))
                      .map((p) => (
                        <option key={p.path} value={p.path}>
                          {p.path} (= {p.value})
                        </option>
                      ))}
                  </select>
                  <label className="text-xs text-text-secondary">от</label>
                  <input type="number" value={row.from} step="any"
                    onChange={(e) => setRows((prev) => prev.map((r, j) => (j === i ? { ...r, from: e.target.value } : r)))}
                    className={`${inputCls} w-[100px] font-mono`} />
                  <label className="text-xs text-text-secondary">до</label>
                  <input type="number" value={row.to} step="any"
                    onChange={(e) => setRows((prev) => prev.map((r, j) => (j === i ? { ...r, to: e.target.value } : r)))}
                    className={`${inputCls} w-[100px] font-mono`} />
                  <label className="text-xs text-text-secondary">шаг</label>
                  <input type="number" value={row.step} step="any"
                    onChange={(e) => setRows((prev) => prev.map((r, j) => (j === i ? { ...r, step: e.target.value } : r)))}
                    className={`${inputCls} w-[100px] font-mono`} />
                  <span className={`text-xs font-mono ${count === null || stale ? 'text-accent-red' : 'text-text-secondary'}`}>
                    {stale ? 'поле пропало из конфига' : count === null ? '—' : `${count} знач.`}
                  </span>
                  <button
                    type="button"
                    onClick={() => setRows((prev) => prev.filter((_, j) => j !== i))}
                    className="text-xs text-accent-red hover:underline"
                  >
                    убрать
                  </button>
                </div>
              );
            })}
          </div>

          <div className="flex flex-wrap items-center gap-4">
            <button
              type="button"
              onClick={addRow}
              disabled={!baseConfigJson || rows.length >= numericPaths.length}
              className="text-xs text-accent-blue hover:underline disabled:opacity-50 disabled:no-underline"
            >
              + Добавить параметр
            </button>

            {comboCount !== null && (
              <span className={`text-xs font-mono ${comboCount > MAX_COMBINATIONS ? 'text-accent-red' : 'text-text-secondary'}`}>
                Комбинаций: {comboCount.toLocaleString('ru-RU')}
                {comboCount > MAX_COMBINATIONS ? ` (лимит ${MAX_COMBINATIONS})` : ''}
              </span>
            )}

            <button
              onClick={handleStart}
              disabled={!baseConfigJson || rows.length === 0 || isActive || startMutation.isPending}
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

          {(localError || startMutation.isError) && (
            <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg">
              {localError || describeError(startMutation.error)}
            </div>
          )}

          {/* Progress */}
          {status && ACTIVE_STATUSES.has(status.status) && (
            <div className="space-y-1.5">
              <div className="flex items-center gap-3 text-xs text-text-secondary">
                <span>
                  {status.status === 'Downloading' || status.status === 'Queued'
                    ? 'Загрузка 1m-истории…'
                    : `Прогон комбинаций: ${status.completed.toLocaleString('ru-RU')} / ${status.total.toLocaleString('ru-RU')}`}
                </span>
                <span className="font-mono">{Math.round(status.elapsedSeconds)}с</span>
                {eta !== null && <span className="font-mono">≈ ещё {eta}с</span>}
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
                  Готово за {Math.round(status.elapsedSeconds)}с: {status.total.toLocaleString('ru-RU')} комбинаций ·
                  история 1m: {status.history.candlesFromCache.toLocaleString('ru-RU')} из кэша,{' '}
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
              {failedResults.length > 0 && (
                <p className="text-xs text-accent-yellow">
                  ⚠ {failedResults.length} комбинаций упали с ошибкой
                  {failedResults[0]?.error ? ` (первая: ${failedResults[0].error})` : ''}
                </p>
              )}

              {okResults.length === 0 ? (
                <p className="text-xs text-text-secondary">Нет успешных комбинаций.</p>
              ) : (
                <div className="overflow-x-auto">
                  <table className="text-xs w-full">
                    <thead className="text-text-secondary">
                      <tr>
                        <th className="text-left font-medium pr-3 py-1.5">#</th>
                        {paramPaths.map((p) => (
                          <th key={p} className={headerCls} onClick={() => clickSort(`p:${p}`)} title={p}>
                            <span className="font-mono">{p}</span>{sortMark(`p:${p}`)}
                          </th>
                        ))}
                        <th className={headerCls} onClick={() => clickSort('netPnlUsd')}>Net PnL{sortMark('netPnlUsd')}</th>
                        <th className={headerCls} onClick={() => clickSort('maxDrawdownUsd')}>DD ${sortMark('maxDrawdownUsd')}</th>
                        <th className={headerCls} onClick={() => clickSort('maxDrawdownPercent')}>DD %{sortMark('maxDrawdownPercent')}</th>
                        <th className={headerCls} onClick={() => clickSort('pnlToDd')}>PnL/DD{sortMark('pnlToDd')}</th>
                        <th className={headerCls} onClick={() => clickSort('winRate')}>WinRate{sortMark('winRate')}</th>
                        <th className={headerCls} onClick={() => clickSort('totalTrades')}>Сделок{sortMark('totalTrades')}</th>
                        <th className={headerCls} onClick={() => clickSort('feesUsd')}>Комиссии{sortMark('feesUsd')}</th>
                        <th className={headerCls} onClick={() => clickSort('maxNotionalUsd')}>Пик ном.{sortMark('maxNotionalUsd')}</th>
                        <th className={headerCls} onClick={() => clickSort('unrealizedPnlAtEndUsd')}>Незафикс.{sortMark('unrealizedPnlAtEndUsd')}</th>
                        <th />
                      </tr>
                    </thead>
                    <tbody className="text-text-primary">
                      {sorted.slice(0, MAX_TABLE_ROWS).map((r, i) => (
                        <tr key={i} className="border-t border-border/60">
                          <td className="pr-3 py-1.5 text-text-secondary">{i + 1}</td>
                          {paramPaths.map((p) => (
                            <td key={p} className="pr-3 py-1.5 font-mono">{r.parameters[p]}</td>
                          ))}
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
                  {sorted.length > MAX_TABLE_ROWS && (
                    <p className="text-[11px] text-text-secondary mt-1">
                      Показаны первые {MAX_TABLE_ROWS} из {sorted.length.toLocaleString('ru-RU')} по текущей сортировке.
                    </p>
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
