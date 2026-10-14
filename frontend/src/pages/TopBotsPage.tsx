import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery } from '@tanstack/react-query';
import api from '../api/client';
import { copyTopBot, fetchTopBots, type TopBot, type TopBotsPeriod } from '../api/topBots';
import {
  STRATEGY_TYPES,
  formatPercent,
  formatUsd,
  getKeySettings,
  pnlColor,
  strategyLabel,
} from '../components/topBots/format';

interface Account {
  id: string;
  name: string;
  exchangeType: number;
  isActive: boolean;
}

const exchangeNames: Record<number, string> = { 1: 'Bybit', 2: 'Bitget', 3: 'BingX', 4: 'Dzengi' };
const PERIODS: TopBotsPeriod[] = [7, 30, 90];
const LIMIT = 50;

function extractError(err: unknown): string {
  const e = err as { response?: { data?: { message?: string } }; message?: string };
  return e?.response?.data?.message || e?.message || 'Не удалось создать бота';
}

export default function TopBotsPage() {
  const [days, setDays] = useState<TopBotsPeriod>(30);
  const [typeFilter, setTypeFilter] = useState<string>('all');
  const [copyBot, setCopyBot] = useState<TopBot | null>(null);

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['top-bots', days, LIMIT],
    queryFn: () => fetchTopBots(days, LIMIT),
    staleTime: 60_000,
  });

  const bots = (data?.bots ?? []).filter((b) => typeFilter === 'all' || b.strategyType === typeFilter);

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-xl font-bold text-text-primary">Топ ботов</h1>
        <p className="text-sm text-text-secondary mt-1">
          Доходность = реализованная прибыль за {days} дн. / максимальный капитал,
          вложенный за этот период. Боты анонимны — показаны только настройки и результаты.
        </p>
      </div>

      <div className="flex flex-wrap items-center gap-3">
        <div className="flex gap-1 bg-bg-secondary border border-border rounded-lg p-1" role="group" aria-label="Период">
          {PERIODS.map((p) => (
            <button
              key={p}
              type="button"
              onClick={() => setDays(p)}
              aria-pressed={days === p}
              className={`px-3 py-1.5 rounded-md text-sm font-medium transition-colors ${
                days === p ? 'bg-accent-blue text-white' : 'text-text-secondary hover:text-text-primary'
              }`}
            >
              {p} дн.
            </button>
          ))}
        </div>

        <select
          value={typeFilter}
          onChange={(e) => setTypeFilter(e.target.value)}
          aria-label="Тип стратегии"
          className="bg-bg-secondary border border-border rounded-lg px-3 py-2 text-sm text-text-primary focus:outline-none focus:border-accent-blue"
        >
          <option value="all">Все стратегии</option>
          {STRATEGY_TYPES.map((s) => (
            <option key={s} value={s}>
              {strategyLabel(s)}
            </option>
          ))}
        </select>
      </div>

      {isLoading ? (
        <div className="text-center py-12 text-text-secondary text-sm">Загрузка...</div>
      ) : isError ? (
        <div className="text-center py-12 text-sm">
          <p className="text-accent-red mb-3">Не удалось загрузить рейтинг</p>
          <button
            type="button"
            onClick={() => refetch()}
            className="px-4 py-2 rounded-lg text-sm bg-bg-tertiary text-text-primary hover:bg-border transition-colors"
          >
            Повторить
          </button>
        </div>
      ) : bots.length === 0 ? (
        <div className="text-center py-12 text-text-secondary text-sm">Нет ботов для выбранных фильтров</div>
      ) : (
        <div className="space-y-3">
          {bots.map((bot, i) => (
            <TopBotRow key={bot.id} bot={bot} rank={i + 1} onCopy={() => setCopyBot(bot)} />
          ))}
        </div>
      )}

      {copyBot && <CopyBotModal bot={copyBot} onClose={() => setCopyBot(null)} />}
    </div>
  );
}

function Metric({ label, value, valueClass = 'text-text-primary' }: { label: string; value: string; valueClass?: string }) {
  return (
    <div className="flex flex-col gap-0.5 min-w-0">
      <span className="text-[10px] uppercase tracking-wider text-text-secondary font-medium">{label}</span>
      <span className={`text-sm font-semibold truncate ${valueClass}`}>{value}</span>
    </div>
  );
}

function TopBotRow({ bot, rank, onCopy }: { bot: TopBot; rank: number; onCopy: () => void }) {
  const [open, setOpen] = useState(false);
  const settings = getKeySettings(bot, 'ru');

  return (
    <article className="bg-bg-secondary border border-border rounded-xl p-4 hover:border-text-secondary/30 transition-colors">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex items-center gap-3 min-w-0">
          <span className="inline-flex items-center justify-center w-7 h-7 rounded-full bg-bg-tertiary text-text-secondary text-xs font-bold shrink-0">
            {rank}
          </span>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-sm font-semibold text-text-primary">{strategyLabel(bot.strategyType)}</span>
              <span className="text-xs font-mono bg-bg-tertiary text-text-secondary px-2 py-0.5 rounded">{bot.symbol}</span>
              {bot.timeframe && <span className="text-xs text-text-secondary">{bot.timeframe}</span>}
              <span className="text-[11px] font-medium bg-accent-blue/10 text-accent-blue px-2 py-0.5 rounded-full">
                {bot.exchange}
              </span>
              {bot.isRunning && (
                <span className="text-[11px] font-medium bg-accent-green/10 text-accent-green px-2 py-0.5 rounded-full">
                  Работает
                </span>
              )}
            </div>
          </div>
        </div>

        <div className="text-right">
          <div className={`text-2xl font-extrabold leading-tight ${pnlColor(bot.returnPercent)}`}>
            {formatPercent(bot.returnPercent)}
          </div>
          <div className="text-[10px] uppercase tracking-wider text-text-secondary">доходность</div>
        </div>
      </div>

      <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-3 mt-4">
        <Metric label="Прибыль" value={formatUsd(bot.pnlUsd, true)} valueClass={pnlColor(bot.pnlUsd)} />
        <Metric label="Макс. капитал" value={formatUsd(bot.peakCapitalUsd)} />
        <Metric label="Сделок за период" value={String(bot.periodClosedTrades)} />
        <Metric label="Винрейт" value={`${bot.periodWinRate.toFixed(1)}%`} valueClass="text-accent-blue" />
        <Metric label="Всего сделок" value={String(bot.totalClosedTrades)} />
        <Metric label="Дней работы" value={String(bot.activeDays)} />
      </div>

      {settings.length > 0 && (
        <div className="flex flex-wrap gap-1.5 mt-4">
          {settings.map((s) => (
            <span key={s.label} className="text-[11px] px-2 py-0.5 rounded bg-bg-tertiary text-text-secondary">
              {s.label}: <span className="text-text-primary font-medium">{s.value}</span>
            </span>
          ))}
        </div>
      )}

      <div className="flex flex-wrap items-center justify-between gap-2 mt-4 pt-3 border-t border-border/50">
        <button
          type="button"
          onClick={() => setOpen((v) => !v)}
          aria-expanded={open}
          className="flex items-center gap-1.5 text-xs text-text-secondary hover:text-text-primary transition-colors"
        >
          <svg
            className={`w-3.5 h-3.5 transition-transform duration-200 ${open ? 'rotate-90' : ''}`}
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            strokeWidth={2.5}
          >
            <path strokeLinecap="round" strokeLinejoin="round" d="M8.25 4.5l7.5 7.5-7.5 7.5" />
          </svg>
          Все параметры
        </button>
        <button
          type="button"
          onClick={onCopy}
          className="px-3 py-1.5 rounded-lg text-xs font-semibold bg-accent-blue hover:bg-accent-blue/90 text-white transition-colors"
        >
          Создать такого бота
        </button>
      </div>

      {open && (
        <div className="mt-3">
          <ConfigView value={bot.config} />
        </div>
      )}
    </article>
  );
}

// ---------------------------------------------------------------------------
// Full config view
// ---------------------------------------------------------------------------

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function formatScalar(v: unknown): string {
  if (v === null || v === undefined) return '—';
  if (typeof v === 'boolean') return v ? 'Да' : 'Нет';
  return String(v);
}

function ConfigView({ value }: { value: Record<string, unknown> }) {
  const entries = Object.entries(value ?? {});
  if (entries.length === 0) {
    return <p className="text-xs text-text-secondary">Нет параметров</p>;
  }
  return (
    <dl className="bg-bg-primary/50 border border-border rounded-lg divide-y divide-border/50">
      {entries.map(([k, v]) => (
        <ConfigEntry key={k} name={k} value={v} />
      ))}
    </dl>
  );
}

function ConfigEntry({ name, value }: { name: string; value: unknown }) {
  const isObjectArray = Array.isArray(value) && value.length > 0 && value.every(isPlainObject);

  return (
    <div className="px-3 py-2 text-xs sm:flex sm:gap-4">
      <dt className="text-text-secondary font-mono sm:w-56 shrink-0">{name}</dt>
      <dd className="text-text-primary min-w-0 mt-1 sm:mt-0 flex-1">
        {isObjectArray ? (
          <ObjectTable rows={value as Record<string, unknown>[]} />
        ) : Array.isArray(value) ? (
          value.length === 0 ? '—' : value.map(formatScalar).join(', ')
        ) : isPlainObject(value) ? (
          <ConfigView value={value} />
        ) : (
          formatScalar(value)
        )}
      </dd>
    </div>
  );
}

function ObjectTable({ rows }: { rows: Record<string, unknown>[] }) {
  const cols = Array.from(new Set(rows.flatMap((r) => Object.keys(r))));
  return (
    <div className="overflow-x-auto">
      <table className="text-[11px] border-collapse">
        <thead>
          <tr>
            <th className="text-left text-text-secondary font-medium pr-3 pb-1">#</th>
            {cols.map((c) => (
              <th key={c} className="text-left text-text-secondary font-mono font-medium pr-3 pb-1">
                {c}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i} className="border-t border-border/40">
              <td className="pr-3 py-1 text-text-secondary">{i + 1}</td>
              {cols.map((c) => (
                <td key={c} className="pr-3 py-1 text-text-primary">
                  {isPlainObject(r[c]) || Array.isArray(r[c]) ? JSON.stringify(r[c]) : formatScalar(r[c])}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Copy modal
// ---------------------------------------------------------------------------

function CopyBotModal({ bot, onClose }: { bot: TopBot; onClose: () => void }) {
  const [accountId, setAccountId] = useState('');
  const needsBybit = bot.strategyType === 'SmartGridHedge';

  const { data: accounts, isLoading } = useQuery<Account[]>({
    queryKey: ['accounts'],
    queryFn: () => api.get('/accounts').then((r) => r.data),
  });

  const available = (accounts ?? []).filter((a) => a.isActive && (!needsBybit || a.exchangeType === 1));

  const mutation = useMutation({
    mutationFn: () => copyTopBot(bot.id, accountId),
  });

  return (
    <div
      className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50 p-4"
      onClick={onClose}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-label="Создать такого бота"
        className="bg-bg-secondary border border-border rounded-xl p-6 w-full max-w-md"
        onClick={(e) => e.stopPropagation()}
      >
        {mutation.isSuccess ? (
          <div className="space-y-4">
            <h2 className="text-lg font-semibold text-text-primary">Бот создан</h2>
            <p className="text-sm text-text-secondary">
              Копия добавлена в новое рабочее пространство в остановленном состоянии. Проверьте настройки перед запуском.
            </p>
            <div className="flex justify-end gap-2">
              <button
                type="button"
                onClick={onClose}
                className="px-4 py-2 rounded-lg text-sm text-text-secondary hover:text-text-primary transition-colors"
              >
                Закрыть
              </button>
              <Link
                to="/active-bots"
                className="px-4 py-2 rounded-lg text-sm font-semibold bg-accent-blue hover:bg-accent-blue/90 text-white transition-colors"
              >
                Перейти к ботам
              </Link>
            </div>
          </div>
        ) : (
          <div className="space-y-4">
            <div>
              <h2 className="text-lg font-semibold text-text-primary">Создать такого бота</h2>
              <p className="text-xs text-text-secondary mt-1">
                {strategyLabel(bot.strategyType)} · {bot.symbol}
              </p>
            </div>

            <div className="bg-accent-yellow/10 border border-accent-yellow/20 text-accent-yellow text-xs px-3 py-2 rounded-lg">
              Бот будет создан остановленным в новом рабочем пространстве. Проверьте настройки и размеры позиций перед
              запуском — прошлые результаты не гарантируют будущую доходность.
            </div>

            <div>
              <label htmlFor="copy-account" className="block text-sm font-medium text-text-primary mb-1.5">
                Биржевой аккаунт
              </label>
              {isLoading ? (
                <p className="text-sm text-text-secondary">Загрузка...</p>
              ) : available.length === 0 ? (
                <p className="text-sm text-text-secondary">
                  {needsBybit
                    ? 'Для SmartGridHedge нужен активный аккаунт Bybit (в режиме хеджирования). '
                    : 'Нет активных аккаунтов. '}
                  <Link to="/accounts" className="text-accent-blue underline" onClick={onClose}>
                    Добавить аккаунт
                  </Link>
                </p>
              ) : (
                <select
                  id="copy-account"
                  value={accountId}
                  onChange={(e) => setAccountId(e.target.value)}
                  className="w-full bg-bg-tertiary border border-border rounded-lg px-3 py-2 text-sm text-text-primary focus:outline-none focus:border-accent-blue"
                >
                  <option value="">Выберите аккаунт</option>
                  {available.map((a) => (
                    <option key={a.id} value={a.id}>
                      {a.name} ({exchangeNames[a.exchangeType] ?? a.exchangeType})
                    </option>
                  ))}
                </select>
              )}
            </div>

            {mutation.isError && (
              <div role="alert" className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-3 py-2 rounded-lg">
                {extractError(mutation.error)}
              </div>
            )}

            <div className="flex justify-end gap-2">
              <button
                type="button"
                onClick={onClose}
                className="px-4 py-2 rounded-lg text-sm text-text-secondary hover:text-text-primary transition-colors"
              >
                Отмена
              </button>
              <button
                type="button"
                onClick={() => mutation.mutate()}
                disabled={!accountId || mutation.isPending}
                className="px-4 py-2 rounded-lg text-sm font-semibold bg-accent-blue hover:bg-accent-blue/90 text-white disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
              >
                {mutation.isPending ? 'Создание...' : 'Создать'}
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
