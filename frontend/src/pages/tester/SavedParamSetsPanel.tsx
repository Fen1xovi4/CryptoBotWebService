import { Fragment, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import api from '../../api/client';
import { describeError } from './errors';
import type { StrategyType, TesterParamSet } from './types';

interface Props {
  exchangeType?: number;
  symbol: string;
  strategyType: StrategyType;
  /** Load the set's config into the strategy form (view / edit before re-running). */
  onLoadToForm: (configJson: string) => void;
  /** Full simulation of the saved config on the currently selected period (fresh history). */
  onSimulateConfig: (configJson: string) => void;
  simulatePending: boolean;
}

export const PARAM_SETS_QUERY_KEY = 'tester-param-sets';

function fmtUsd(v: number): string {
  return `${v >= 0 ? '+' : ''}${v.toFixed(2)}$`;
}

function fmtDate(iso?: string | null): string {
  return iso ? iso.slice(0, 10) : '—';
}

export default function SavedParamSetsPanel({ exchangeType, symbol, strategyType, onLoadToForm, onSimulateConfig, simulatePending }: Props) {
  const [expanded, setExpanded] = useState(true);
  const [openId, setOpenId] = useState<string | null>(null);
  const queryClient = useQueryClient();
  const sym = symbol.replace(/\s+/g, '').toUpperCase();
  const enabled = exchangeType !== undefined && sym.length > 0;

  const { data: sets, isLoading } = useQuery<TesterParamSet[]>({
    queryKey: [PARAM_SETS_QUERY_KEY, exchangeType, sym, strategyType],
    queryFn: () => api.get('/tester/param-sets', { params: { exchangeType, symbol: sym, strategyType } }).then((r) => r.data),
    enabled,
    staleTime: 30_000,
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: [PARAM_SETS_QUERY_KEY] });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/tester/param-sets/${id}`),
    onSuccess: invalidate,
  });
  const renameMutation = useMutation({
    mutationFn: ({ id, name, note }: { id: string; name?: string; note?: string }) =>
      api.patch(`/tester/param-sets/${id}`, { name, note }),
    onSuccess: invalidate,
  });

  const rename = (s: TesterParamSet) => {
    const name = window.prompt('Название набора', s.name);
    if (name === null || !name.trim() || name.trim() === s.name) return;
    renameMutation.mutate({ id: s.id, name: name.trim() });
  };
  const editNote = (s: TesterParamSet) => {
    const note = window.prompt('Заметка к набору', s.note ?? '');
    if (note === null) return;
    renameMutation.mutate({ id: s.id, note });
  };

  const count = sets?.length ?? 0;

  return (
    <div className="bg-bg-secondary rounded-xl border border-border p-4 mb-4 space-y-3">
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        className="flex items-center gap-2 text-xs font-semibold text-text-secondary uppercase tracking-widest hover:text-text-primary transition-colors"
      >
        <span>{expanded ? '▾' : '▸'}</span>
        Сохранённые наборы параметров
        <span className="normal-case tracking-normal font-mono">{sym} · {strategyType}</span>
        {count > 0 && <span className="normal-case tracking-normal">({count})</span>}
      </button>

      {expanded && (
        <>
          {!enabled ? (
            <p className="text-xs text-text-secondary">Выберите аккаунт и символ.</p>
          ) : isLoading ? (
            <p className="text-xs text-text-secondary">Загрузка…</p>
          ) : !sets || sets.length === 0 ? (
            <p className="text-xs text-text-secondary">
              Для этого инструмента и стратегии наборов пока нет. Сохраните строку из таблицы оптимизации
              («сохранить») или результат одиночной симуляции («Сохранить набор»).
            </p>
          ) : (
            <div className="overflow-x-auto">
              <table className="text-xs w-full">
                <thead className="text-text-secondary">
                  <tr>
                    <th className="text-left font-medium pr-3 py-1.5">Название</th>
                    <th className="text-left font-medium pr-3 py-1.5">Параметры</th>
                    <th className="text-left font-medium pr-3 py-1.5 whitespace-nowrap">Период теста</th>
                    <th className="text-left font-medium pr-3 py-1.5">ТФ</th>
                    <th className="text-left font-medium pr-3 py-1.5">Net PnL</th>
                    <th className="text-left font-medium pr-3 py-1.5">DD $</th>
                    <th className="text-left font-medium pr-3 py-1.5">DD %</th>
                    <th className="text-left font-medium pr-3 py-1.5">WinRate</th>
                    <th className="text-left font-medium pr-3 py-1.5">Сделок</th>
                    <th className="text-left font-medium pr-3 py-1.5">Сохранён</th>
                    <th />
                  </tr>
                </thead>
                <tbody className="text-text-primary">
                  {sets.map((s) => {
                    const swept = Object.entries(s.sweptParams);
                    const isOpen = openId === s.id;
                    return (
                      <Fragment key={s.id}>
                        <tr className="border-t border-border/60 align-top">
                          <td className="pr-3 py-1.5 max-w-[220px]">
                            <button type="button" onClick={() => rename(s)} className="font-medium hover:underline text-left" title="Переименовать">
                              {s.name}
                            </button>
                            {s.note && <div className="text-[11px] text-text-secondary whitespace-pre-wrap">{s.note}</div>}
                          </td>
                          <td className="pr-3 py-1.5">
                            {swept.length === 0 ? (
                              <span className="text-text-secondary">из формы</span>
                            ) : (
                              <div className="flex flex-wrap gap-1 max-w-[360px]">
                                {swept.map(([k, v]) => (
                                  <span key={k} className="font-mono bg-bg-primary border border-border rounded px-1.5 py-0.5 whitespace-nowrap" title={k}>
                                    {k}={v}
                                  </span>
                                ))}
                              </div>
                            )}
                          </td>
                          <td className="pr-3 py-1.5 font-mono whitespace-nowrap">{fmtDate(s.periodFromUtc)} → {fmtDate(s.periodToUtc)}</td>
                          <td className="pr-3 py-1.5 font-mono">{s.pathTimeframe}</td>
                          <td className={`pr-3 py-1.5 font-mono ${s.netPnlUsd >= 0 ? 'text-green-400' : 'text-red-400'}`}>{fmtUsd(s.netPnlUsd)}</td>
                          <td className="pr-3 py-1.5 font-mono text-red-400">{fmtUsd(-Math.abs(s.maxDrawdownUsd))}</td>
                          <td className="pr-3 py-1.5 font-mono text-red-400">{s.maxDrawdownPercent.toFixed(2)}%</td>
                          <td className="pr-3 py-1.5 font-mono">{s.winRate.toFixed(1)}%</td>
                          <td className="pr-3 py-1.5 font-mono">{s.totalTrades}</td>
                          <td className="pr-3 py-1.5 whitespace-nowrap">{s.createdAt.slice(0, 16).replace('T', ' ')}</td>
                          <td className="py-1.5 whitespace-nowrap">
                            <button
                              type="button"
                              disabled={simulatePending}
                              onClick={() => onSimulateConfig(s.configJson)}
                              className="text-accent-blue hover:underline disabled:opacity-50 mr-3"
                              title="Полная симуляция этого набора на выбранном сейчас периоде — свежая история подгрузится как обычно"
                            >
                              симулировать
                            </button>
                            <button
                              type="button"
                              onClick={() => onLoadToForm(s.configJson)}
                              className="text-accent-blue hover:underline mr-3"
                              title="Подставить параметры набора в форму стратегии выше"
                            >
                              в форму
                            </button>
                            <button
                              type="button"
                              onClick={() => setOpenId(isOpen ? null : s.id)}
                              className="text-text-secondary hover:underline mr-3"
                            >
                              {isOpen ? 'скрыть конфиг' : 'конфиг'}
                            </button>
                            <button type="button" onClick={() => editNote(s)} className="text-text-secondary hover:underline mr-3" title="Заметка">
                              заметка
                            </button>
                            <button
                              type="button"
                              onClick={() => { if (window.confirm(`Удалить набор «${s.name}»?`)) deleteMutation.mutate(s.id); }}
                              className="text-accent-red hover:underline"
                            >
                              удалить
                            </button>
                          </td>
                        </tr>
                        {isOpen && (
                          <tr className="border-t border-border/30">
                            <td colSpan={11} className="py-2">
                              <div className="flex items-start gap-3">
                                <pre className="text-[11px] font-mono bg-bg-primary border border-border rounded-lg p-3 overflow-x-auto flex-1 whitespace-pre-wrap break-all">
                                  {prettyJson(s.configJson)}
                                </pre>
                                <button
                                  type="button"
                                  onClick={() => navigator.clipboard.writeText(s.configJson)}
                                  className="text-xs text-text-secondary hover:underline whitespace-nowrap"
                                >
                                  копировать
                                </button>
                              </div>
                            </td>
                          </tr>
                        )}
                      </Fragment>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
          {(deleteMutation.isError || renameMutation.isError) && (
            <p className="text-xs text-accent-red">{describeError(deleteMutation.error ?? renameMutation.error)}</p>
          )}
        </>
      )}
    </div>
  );
}

function prettyJson(s: string): string {
  try {
    return JSON.stringify(JSON.parse(s), null, 2);
  } catch {
    return s;
  }
}
