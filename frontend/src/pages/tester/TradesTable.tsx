import { useMemo, useState } from 'react';
import type { SimulatedTrade } from './types';

const PAGE_SIZE = 100;

function actionColorCls(action: string, reason: string): string {
  const s = `${action} ${reason}`.toLowerCase();
  if (s.includes('takeprofit') || s.includes('take profit') || s.includes(' tp')) return 'text-accent-green';
  if (s.includes('stoploss') || s.includes('stop loss') || s.includes(' sl')) return 'text-accent-red';
  if (s.includes('dca')) return 'text-orange-400';
  if (s.includes('funding')) return 'text-purple-400';
  return 'text-text-primary';
}

function formatDate(iso: string): string {
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  return d.toLocaleString('ru-RU', { year: '2-digit', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

export default function TradesTable({ trades }: { trades: SimulatedTrade[] }) {
  const [page, setPage] = useState(0);

  const pageCount = Math.max(1, Math.ceil(trades.length / PAGE_SIZE));
  const clampedPage = Math.min(page, pageCount - 1);
  const pageTrades = useMemo(
    () => trades.slice(clampedPage * PAGE_SIZE, clampedPage * PAGE_SIZE + PAGE_SIZE),
    [trades, clampedPage],
  );

  if (trades.length === 0) {
    return (
      <div className="bg-bg-secondary rounded-xl border border-border px-4 py-8 text-center text-sm text-text-secondary">
        Сделок не было за выбранный период
      </div>
    );
  }

  return (
    <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
      <div className="flex items-center justify-between px-4 py-3 border-b border-border">
        <h3 className="text-sm font-semibold text-text-primary">Сделки ({trades.length})</h3>
        {pageCount > 1 && (
          <div className="flex items-center gap-2 text-xs text-text-secondary">
            <button
              onClick={() => setPage((p) => Math.max(0, p - 1))}
              disabled={clampedPage === 0}
              className="px-2 py-1 rounded bg-bg-tertiary hover:bg-bg-tertiary/70 disabled:opacity-30 transition-colors"
            >
              ←
            </button>
            <span>{clampedPage + 1} / {pageCount}</span>
            <button
              onClick={() => setPage((p) => Math.min(pageCount - 1, p + 1))}
              disabled={clampedPage >= pageCount - 1}
              className="px-2 py-1 rounded bg-bg-tertiary hover:bg-bg-tertiary/70 disabled:opacity-30 transition-colors"
            >
              →
            </button>
          </div>
        )}
      </div>
      <div className="overflow-x-auto max-h-[480px] overflow-y-auto">
        <table className="w-full text-sm">
          <thead className="sticky top-0 bg-bg-secondary">
            <tr className="border-b border-border text-xs text-text-secondary">
              <th className="px-3 py-2 text-left">Время</th>
              <th className="px-3 py-2 text-left">Сторона</th>
              <th className="px-3 py-2 text-left">Действие</th>
              <th className="px-3 py-2 text-right">Цена</th>
              <th className="px-3 py-2 text-right">Кол-во</th>
              <th className="px-3 py-2 text-right">Notional $</th>
              <th className="px-3 py-2 text-right">Комиссия $</th>
              <th className="px-3 py-2 text-right">PnL $</th>
              <th className="px-3 py-2 text-right">PnL %</th>
              <th className="px-3 py-2 text-left">Причина</th>
            </tr>
          </thead>
          <tbody>
            {pageTrades.map((t, i) => (
              <tr key={i} className="border-b border-border/30 hover:bg-bg-tertiary/50">
                <td className="px-3 py-2 text-text-secondary font-mono text-xs whitespace-nowrap">{formatDate(t.time)}</td>
                <td className="px-3 py-2">
                  <span className={t.side?.toLowerCase().includes('short') || t.side?.toLowerCase().includes('sell') ? 'text-red-400' : 'text-green-400'}>
                    {t.side}
                  </span>
                </td>
                <td className={`px-3 py-2 font-medium ${actionColorCls(t.action, t.reason)}`}>{t.action}</td>
                <td className="px-3 py-2 text-right font-mono text-text-primary">{t.price.toFixed(4)}</td>
                <td className="px-3 py-2 text-right font-mono text-text-secondary">{t.quantity}</td>
                <td className="px-3 py-2 text-right font-mono text-text-secondary">{t.notionalUsd.toFixed(2)}</td>
                <td className="px-3 py-2 text-right font-mono text-text-secondary">{t.feeUsd.toFixed(4)}</td>
                <td className="px-3 py-2 text-right font-mono">
                  {t.pnlUsd != null ? (
                    <span className={t.pnlUsd >= 0 ? 'text-green-400' : 'text-red-400'}>
                      {t.pnlUsd >= 0 ? '+' : ''}{t.pnlUsd.toFixed(2)}
                    </span>
                  ) : <span className="text-text-secondary">—</span>}
                </td>
                <td className="px-3 py-2 text-right font-mono">
                  {t.pnlPercent != null ? (
                    <span className={t.pnlPercent >= 0 ? 'text-green-400' : 'text-red-400'}>
                      {t.pnlPercent >= 0 ? '+' : ''}{t.pnlPercent.toFixed(2)}%
                    </span>
                  ) : <span className="text-text-secondary">—</span>}
                </td>
                <td className="px-3 py-2 text-text-secondary text-xs">{t.reason}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
