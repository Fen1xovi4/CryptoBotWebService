import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/client';
import { useAuthStore } from '../stores/authStore';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type Lang = 'ru' | 'en';

interface Plan {
  id: string;
  name: string;
  priceMonthly: number;
  maxExchangeAccounts: number;
  maxActiveBots: number;
  supportLevel: string;
}

interface TopBotConfig {
  indicatorType: string;
  indicatorLength: number;
  takeProfitPercent: number;
  stopLossPercent: number;
  orderSize: number;
  useMartingale: boolean;
  martingaleCoeff: number;
  onlyLong: boolean;
  onlyShort: boolean;
}

interface TopBot {
  id: string;
  name: string;
  symbol: string;
  exchange: string;
  strategyType: string;
  timeframe: string;
  realizedPnlPercent: number;
  runningDays: number;
  totalTrades: number;
  winningTrades: number;
  winRate: number;
  config: TopBotConfig | null;
}

interface ApiPlan {
  plan: string;
  nameRu: string;
  nameEn: string;
  maxAccounts: number;
  maxActiveBots: number;
  priceMonthly: number;
  priceLabel: string;
}

// ---------------------------------------------------------------------------
// Translations
// ---------------------------------------------------------------------------

const t = {
  ru: {
    navSignIn: 'Войти',
    navGetStarted: 'Начать',
    navDashboard: 'Панель управления',

    heroHeading: 'Автоматизированная крипто-торговля',
    heroSubtitle:
      'Подключите биржу, настройте стратегию, и бот будет торговать за вас 24/7',
    heroCta: 'Начать',

    featuresHeading: 'Возможности платформы',
    feature1Title: 'Поддержка бирж',
    feature1Desc: 'Подключайте Bybit, Bitget и BingX через API. Несколько аккаунтов одновременно.',
    feature2Title: 'Умные стратегии',
    feature2Desc: 'EMA Bounce и Martingale с настраиваемыми параметрами и стоп-лоссом.',
    feature3Title: 'Панель управления',
    feature3Desc: 'PnL в реальном времени, история сделок и живые графики по каждому воркспейсу.',

    topBotsHeading: 'Лучшие боты',
    topBotsProfit: 'Прибыль',
    topBotsRunning: 'Работает',
    topBotsDays: 'дн.',
    topBotsTrades: 'Сделок',
    topBotsWinRate: 'Винрейт',
    topBotsSettings: 'Настройки',
    topBotsStatistics: 'Статистика',
    topBotsIndicator: 'Индикатор',
    topBotsTakeProfit: 'Тейк-профит',
    topBotsStopLoss: 'Стоп-лосс',
    topBotsOrderSize: 'Размер ордера',
    topBotsMartingale: 'Мартингейл',
    topBotsDirection: 'Направление',
    topBotsMartingaleYes: 'Да',
    topBotsMartingaleNo: 'Нет',
    topBotsDirectionLong: 'Только лонг',
    topBotsDirectionShort: 'Только шорт',
    topBotsDirectionBoth: 'Оба направления',

    pricingHeading: 'Выберите тариф',
    pricingPopular: 'Популярный',
    pricingPerMonth: '/мес',
    pricingExchangeAccounts: 'биржевых аккаунта',
    pricingActiveBots: 'активных ботов',
    pricingSupport: 'поддержка',
    pricingBasicSupport: 'Базовая',
    pricingPrioritySupport: 'Приоритетная',
    pricingPremiumSupport: 'Премиум',
    pricingCta: 'Начать',

    footerText: '© 2026 CryptoBot. Все права защищены.',
  },
  en: {
    navSignIn: 'Sign In',
    navGetStarted: 'Get Started',
    navDashboard: 'Dashboard',

    heroHeading: 'Automated Crypto Trading',
    heroSubtitle:
      'Connect your exchange, set up a strategy, and let the bot trade for you 24/7',
    heroCta: 'Get Started',

    featuresHeading: 'Platform Features',
    feature1Title: 'Multi-Exchange Support',
    feature1Desc: 'Connect Bybit, Bitget and BingX via API. Manage multiple accounts at once.',
    feature2Title: 'Smart Strategies',
    feature2Desc: 'EMA Bounce and Martingale with configurable parameters and stop-loss.',
    feature3Title: 'Real-time Dashboard',
    feature3Desc: 'Live PnL tracking, trade history and real-time charts per workspace.',

    topBotsHeading: 'Top Bots',
    topBotsProfit: 'Profit',
    topBotsRunning: 'Running',
    topBotsDays: 'd',
    topBotsTrades: 'Trades',
    topBotsWinRate: 'Win Rate',
    topBotsSettings: 'Settings',
    topBotsStatistics: 'Statistics',
    topBotsIndicator: 'Indicator',
    topBotsTakeProfit: 'Take Profit',
    topBotsStopLoss: 'Stop Loss',
    topBotsOrderSize: 'Order Size',
    topBotsMartingale: 'Martingale',
    topBotsDirection: 'Direction',
    topBotsMartingaleYes: 'Yes',
    topBotsMartingaleNo: 'No',
    topBotsDirectionLong: 'Long only',
    topBotsDirectionShort: 'Short only',
    topBotsDirectionBoth: 'Both',

    pricingHeading: 'Choose Your Plan',
    pricingPopular: 'Popular',
    pricingPerMonth: '/mo',
    pricingExchangeAccounts: 'Exchange Accounts',
    pricingActiveBots: 'Active Bots',
    pricingSupport: 'Support',
    pricingBasicSupport: 'Basic',
    pricingPrioritySupport: 'Priority',
    pricingPremiumSupport: 'Premium',
    pricingCta: 'Get Started',

    footerText: '© 2026 CryptoBot. All rights reserved.',
  },
} as const;

// ---------------------------------------------------------------------------
// Fallback plan data
// ---------------------------------------------------------------------------

const FALLBACK_PLANS: Plan[] = [
  {
    id: 'basic',
    name: 'Basic',
    priceMonthly: 9,
    maxExchangeAccounts: 2,
    maxActiveBots: 5,
    supportLevel: 'basic',
  },
  {
    id: 'advanced',
    name: 'Advanced',
    priceMonthly: 29,
    maxExchangeAccounts: 5,
    maxActiveBots: 15,
    supportLevel: 'priority',
  },
  {
    id: 'pro',
    name: 'Pro',
    priceMonthly: 99,
    maxExchangeAccounts: 20,
    maxActiveBots: 50,
    supportLevel: 'premium',
  },
];

// ---------------------------------------------------------------------------
// Helper: resolve support label from supportLevel key
// ---------------------------------------------------------------------------

function supportLabel(level: string, tr: (typeof t)['ru'] | (typeof t)['en']): string {
  const map: Record<string, string> = {
    basic: tr.pricingBasicSupport,
    priority: tr.pricingPrioritySupport,
    premium: tr.pricingPremiumSupport,
  };
  return map[level.toLowerCase()] ?? level;
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function Logo() {
  return (
    <div className="flex items-center gap-2.5">
      <div className="w-9 h-9 rounded-lg bg-accent-blue flex items-center justify-center shrink-0">
        <svg
          className="w-5 h-5 text-white"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          strokeWidth={2}
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            d="M12 6v12m-3-2.818l.879.659c1.171.879 3.07.879 4.242 0 1.172-.879 1.172-2.303 0-3.182C13.536 12.219 12.768 12 12 12c-.725 0-1.45-.22-2.003-.659-1.106-.879-1.106-2.303 0-3.182s2.9-.879 4.006 0l.415.33M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
          />
        </svg>
      </div>
      <div>
        <p className="text-sm font-bold text-text-primary leading-tight">CryptoBot</p>
        <p className="text-[10px] text-text-secondary leading-tight">Trading Platform</p>
      </div>
    </div>
  );
}

interface FeatureCardProps {
  icon: React.ReactNode;
  title: string;
  description: string;
}

function FeatureCard({ icon, title, description }: FeatureCardProps) {
  return (
    <div className="bg-bg-secondary border border-border rounded-2xl p-6 flex flex-col gap-4 hover:border-accent-blue/40 transition-colors">
      <div className="w-12 h-12 rounded-xl bg-accent-blue/10 flex items-center justify-center text-accent-blue">
        {icon}
      </div>
      <div>
        <h3 className="text-base font-semibold text-text-primary mb-1.5">{title}</h3>
        <p className="text-sm text-text-secondary leading-relaxed">{description}</p>
      </div>
    </div>
  );
}

interface PricingCardProps {
  plan: Plan;
  isPopular: boolean;
  tr: (typeof t)['ru'] | (typeof t)['en'];
}

function PricingCard({ plan, isPopular, tr }: PricingCardProps) {
  const cardBase =
    'relative flex flex-col rounded-2xl border p-6 transition-transform duration-200';
  const cardStyle = isPopular
    ? `${cardBase} border-accent-blue bg-accent-blue/5 scale-105`
    : `${cardBase} border-border bg-bg-secondary`;

  return (
    <div className={cardStyle}>
      {isPopular && (
        <span className="absolute -top-3.5 left-1/2 -translate-x-1/2 bg-accent-blue text-white text-[11px] font-bold px-3 py-1 rounded-full uppercase tracking-wide">
          {tr.pricingPopular}
        </span>
      )}

      <div className="mb-5">
        <p className="text-sm font-semibold text-text-secondary uppercase tracking-wider mb-2">
          {plan.name}
        </p>
        <div className="flex items-end gap-1">
          <span className="text-4xl font-extrabold text-text-primary">${plan.priceMonthly}</span>
          <span className="text-text-secondary text-sm mb-1">{tr.pricingPerMonth}</span>
        </div>
      </div>

      <ul className="flex flex-col gap-3 mb-8 flex-1">
        <PricingFeature
          label={`${plan.maxExchangeAccounts} ${tr.pricingExchangeAccounts}`}
        />
        <PricingFeature
          label={`${plan.maxActiveBots} ${tr.pricingActiveBots}`}
        />
        <PricingFeature
          label={`${supportLabel(plan.supportLevel, tr)} ${tr.pricingSupport}`}
        />
      </ul>

      <Link
        to={`/buy?plan=${encodeURIComponent(plan.name)}`}
        className={`block text-center py-2.5 px-4 rounded-lg text-sm font-semibold transition-colors ${
          isPopular
            ? 'bg-accent-blue hover:bg-accent-blue/90 text-white shadow-lg shadow-accent-blue/25'
            : 'bg-bg-tertiary hover:bg-border text-text-primary'
        }`}
      >
        {tr.pricingCta}
      </Link>
    </div>
  );
}

function PricingFeature({ label }: { label: string }) {
  return (
    <li className="flex items-center gap-2 text-sm text-text-secondary">
      <svg
        className="w-4 h-4 text-accent-green shrink-0"
        fill="none"
        viewBox="0 0 24 24"
        stroke="currentColor"
        strokeWidth={2.5}
      >
        <path strokeLinecap="round" strokeLinejoin="round" d="M4.5 12.75l6 6 9-13.5" />
      </svg>
      {label}
    </li>
  );
}

// ---------------------------------------------------------------------------
// TopBotCard
// ---------------------------------------------------------------------------

const RANK_COLORS = [
  'from-yellow-500/20 via-yellow-500/5 to-transparent border-yellow-500/30',
  'from-slate-400/15 via-slate-400/5 to-transparent border-slate-400/25',
  'from-amber-700/15 via-amber-700/5 to-transparent border-amber-700/25',
];

function RankBadge({ rank }: { rank: number }) {
  if (rank === 1) {
    return (
      <span
        aria-label="Rank 1"
        className="inline-flex items-center justify-center w-6 h-6 rounded-full bg-yellow-500/20 text-yellow-400 font-bold text-xs"
      >
        1
      </span>
    );
  }
  if (rank === 2) {
    return (
      <span
        aria-label="Rank 2"
        className="inline-flex items-center justify-center w-6 h-6 rounded-full bg-slate-400/20 text-slate-300 font-bold text-xs"
      >
        2
      </span>
    );
  }
  if (rank === 3) {
    return (
      <span
        aria-label="Rank 3"
        className="inline-flex items-center justify-center w-6 h-6 rounded-full bg-amber-700/20 text-amber-500 font-bold text-xs"
      >
        3
      </span>
    );
  }
  return (
    <span
      aria-label={`Rank ${rank}`}
      className="inline-flex items-center justify-center w-6 h-6 rounded-full bg-bg-tertiary text-text-secondary font-bold text-xs"
    >
      {rank}
    </span>
  );
}

interface TopBotCardProps {
  bot: TopBot;
  rank: number;
  tr: (typeof t)['ru'] | (typeof t)['en'];
}

function TopBotCard({ bot, rank, tr }: TopBotCardProps) {
  const [expanded, setExpanded] = useState(false);

  const gradientClass =
    rank <= 3
      ? `bg-gradient-to-b ${RANK_COLORS[rank - 1]}`
      : 'bg-bg-secondary border-border';

  const pnlFormatted =
    bot.realizedPnlPercent >= 0
      ? `+${bot.realizedPnlPercent.toFixed(2)}%`
      : `${bot.realizedPnlPercent.toFixed(2)}%`;

  const winRateFormatted = `${bot.winRate.toFixed(1)}%`;

  function directionLabel(cfg: TopBotConfig): string {
    if (cfg.onlyLong) return tr.topBotsDirectionLong;
    if (cfg.onlyShort) return tr.topBotsDirectionShort;
    return tr.topBotsDirectionBoth;
  }

  return (
    <article
      className={`relative flex flex-col rounded-2xl border p-5 transition-colors hover:border-accent-blue/40 ${gradientClass}`}
    >
      {/* Header row */}
      <div className="flex items-start justify-between gap-2 mb-3">
        <div className="flex items-center gap-2 min-w-0">
          <RankBadge rank={rank} />
          <span className="text-sm font-semibold text-text-primary truncate">{bot.name}</span>
        </div>
        <span className="shrink-0 text-[11px] font-medium bg-accent-blue/10 text-accent-blue px-2 py-0.5 rounded-full">
          {bot.exchange}
        </span>
      </div>

      {/* Symbol + timeframe */}
      <div className="flex items-center gap-2 mb-4">
        <span className="text-xs text-text-secondary font-mono bg-bg-tertiary px-2 py-0.5 rounded">
          {bot.symbol}
        </span>
        <span className="text-xs text-text-secondary">{bot.timeframe}</span>
      </div>

      {/* Stats row */}
      <div className="grid grid-cols-4 gap-2 mb-4">
        {/* PnL */}
        <div className="flex flex-col gap-0.5">
          <span className="text-[10px] uppercase tracking-wider text-text-secondary font-medium">
            {tr.topBotsProfit}
          </span>
          <span
            className={`text-base font-bold leading-tight ${
              bot.realizedPnlPercent >= 0 ? 'text-accent-green' : 'text-accent-red'
            }`}
          >
            {pnlFormatted}
          </span>
        </div>

        {/* Running time */}
        <div className="flex flex-col gap-0.5">
          <span className="text-[10px] uppercase tracking-wider text-text-secondary font-medium">
            {tr.topBotsRunning}
          </span>
          <span className="text-base font-bold text-text-primary leading-tight">
            {bot.runningDays}{tr.topBotsDays}
          </span>
        </div>

        {/* Trades */}
        <div className="flex flex-col gap-0.5">
          <span className="text-[10px] uppercase tracking-wider text-text-secondary font-medium">
            {tr.topBotsTrades}
          </span>
          <span className="text-base font-bold text-text-primary leading-tight">
            {bot.totalTrades}
          </span>
        </div>

        {/* Win rate */}
        <div className="flex flex-col gap-0.5">
          <span className="text-[10px] uppercase tracking-wider text-text-secondary font-medium">
            {tr.topBotsWinRate}
          </span>
          <span className="text-base font-bold text-accent-blue leading-tight">
            {winRateFormatted}
          </span>
        </div>
      </div>

      {/* Expandable config */}
      {bot.config && (
        <div>
          <button
            type="button"
            onClick={() => setExpanded((v) => !v)}
            aria-expanded={expanded}
            className="flex items-center gap-1.5 text-xs text-text-secondary hover:text-text-primary transition-colors w-full"
          >
            <svg
              className={`w-3.5 h-3.5 shrink-0 transition-transform duration-200 ${expanded ? 'rotate-90' : ''}`}
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              strokeWidth={2.5}
            >
              <path strokeLinecap="round" strokeLinejoin="round" d="M8.25 4.5l7.5 7.5-7.5 7.5" />
            </svg>
            <span>{tr.topBotsSettings}</span>
          </button>

          {expanded && (
            <div className="mt-3 pt-3 border-t border-border grid grid-cols-2 gap-x-4 gap-y-2">
              <ConfigRow
                label={tr.topBotsIndicator}
                value={`${bot.config.indicatorType} ${bot.config.indicatorLength}`}
              />
              <ConfigRow
                label={tr.topBotsTakeProfit}
                value={`${bot.config.takeProfitPercent}%`}
              />
              <ConfigRow
                label={tr.topBotsStopLoss}
                value={`${bot.config.stopLossPercent}%`}
              />
              <ConfigRow
                label={tr.topBotsOrderSize}
                value={`$${bot.config.orderSize}`}
              />
              <ConfigRow
                label={tr.topBotsMartingale}
                value={
                  bot.config.useMartingale
                    ? `${tr.topBotsMartingaleYes} ×${bot.config.martingaleCoeff}`
                    : tr.topBotsMartingaleNo
                }
              />
              <ConfigRow
                label={tr.topBotsDirection}
                value={directionLabel(bot.config)}
              />
            </div>
          )}
        </div>
      )}
    </article>
  );
}

function ConfigRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-[10px] uppercase tracking-wider text-text-secondary">{label}</span>
      <span className="text-xs font-medium text-text-primary">{value}</span>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function LandingPage() {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const [lang, setLang] = useState<Lang>(() => {
    const stored = localStorage.getItem('landing-lang');
    return stored === 'en' || stored === 'ru' ? stored : 'ru';
  });

  const [plans, setPlans] = useState<Plan[]>(FALLBACK_PLANS);
  const [topBots, setTopBots] = useState<TopBot[]>([]);

  const tr = t[lang];

  // Persist language choice
  useEffect(() => {
    localStorage.setItem('landing-lang', lang);
  }, [lang]);

  // Fetch top bots
  useEffect(() => {
    api
      .get<TopBot[]>('/strategies/top')
      .then((res) => {
        if (Array.isArray(res.data) && res.data.length > 0) {
          setTopBots(res.data.slice(0, 6));
        }
      })
      .catch(() => {
        // silently hide the section on failure
      });
  }, []);

  // Fetch subscription plans
  useEffect(() => {
    api
      .get<ApiPlan[]>('/subscriptions/plans')
      .then((res) => {
        if (Array.isArray(res.data) && res.data.length > 0) {
          const supportLevels = ['basic', 'priority', 'premium'];
          const mapped: Plan[] = res.data.map((p, i) => ({
            id: p.plan.toLowerCase(),
            name: p.plan,
            priceMonthly: p.priceMonthly,
            maxExchangeAccounts: p.maxAccounts,
            maxActiveBots: p.maxActiveBots,
            supportLevel: supportLevels[i] || 'basic',
          }));
          setPlans(mapped);
        }
      })
      .catch(() => {
        // silently fall back to hardcoded data
      });
  }, []);

  const toggleLang = () => setLang((prev) => (prev === 'ru' ? 'en' : 'ru'));

  return (
    <div className="min-h-screen bg-bg-primary text-text-primary">
      {/* ------------------------------------------------------------------ */}
      {/* Navbar */}
      {/* ------------------------------------------------------------------ */}
      <header className="fixed top-0 inset-x-0 z-50 bg-bg-primary/80 backdrop-blur-md border-b border-border">
        <div className="max-w-6xl mx-auto px-4 sm:px-6 h-16 flex items-center justify-between gap-4">
          <Logo />

          <nav className="flex items-center gap-2 sm:gap-3">
            {/* Language toggle */}
            <button
              onClick={toggleLang}
              aria-label="Toggle language"
              className="px-3 py-1.5 rounded-lg text-xs font-semibold text-text-secondary border border-border hover:border-accent-blue/40 hover:text-text-primary transition-colors"
            >
              {lang === 'ru' ? 'EN' : 'RU'}
            </button>

            {isAuthenticated ? (
              <Link
                to="/dashboard"
                className="px-4 py-2 rounded-lg text-sm font-semibold bg-accent-blue hover:bg-accent-blue/90 text-white shadow-lg shadow-accent-blue/20 transition-colors"
              >
                {tr.navDashboard}
              </Link>
            ) : (
              <>
                <Link
                  to="/login"
                  className="px-3 py-2 rounded-lg text-sm font-medium text-text-secondary hover:text-text-primary transition-colors"
                >
                  {tr.navSignIn}
                </Link>

                <Link
                  to="/register"
                  className="px-4 py-2 rounded-lg text-sm font-semibold bg-accent-blue hover:bg-accent-blue/90 text-white shadow-lg shadow-accent-blue/20 transition-colors"
                >
                  {tr.navGetStarted}
                </Link>
              </>
            )}
          </nav>
        </div>
      </header>

      {/* ------------------------------------------------------------------ */}
      {/* Hero */}
      {/* ------------------------------------------------------------------ */}
      <section className="pt-36 pb-24 px-4 sm:px-6 text-center">
        <div className="max-w-3xl mx-auto">
          {/* Decorative pill */}
          <span className="inline-flex items-center gap-1.5 bg-accent-blue/10 text-accent-blue text-xs font-semibold px-3 py-1 rounded-full mb-6">
            <span className="w-1.5 h-1.5 rounded-full bg-accent-green animate-pulse" />
            {lang === 'ru' ? 'Торговля 24/7' : '24/7 Trading'}
          </span>

          <h1 className="text-4xl sm:text-5xl lg:text-6xl font-extrabold text-text-primary leading-tight mb-6">
            {tr.heroHeading}
          </h1>

          <p className="text-lg sm:text-xl text-text-secondary max-w-xl mx-auto mb-10 leading-relaxed">
            {tr.heroSubtitle}
          </p>

          <Link
            to="/register"
            className="inline-flex items-center gap-2 bg-accent-blue hover:bg-accent-blue/90 text-white font-semibold px-8 py-3.5 rounded-xl text-base shadow-xl shadow-accent-blue/25 transition-colors"
          >
            {tr.heroCta}
            <svg
              className="w-4 h-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              strokeWidth={2.5}
            >
              <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 4.5L21 12m0 0l-7.5 7.5M21 12H3" />
            </svg>
          </Link>
        </div>

        {/* Subtle gradient glow behind hero */}
        <div
          aria-hidden
          className="pointer-events-none absolute left-1/2 top-24 -translate-x-1/2 w-[600px] h-[300px] bg-accent-blue/5 rounded-full blur-3xl -z-10"
        />
      </section>

      {/* ------------------------------------------------------------------ */}
      {/* Features */}
      {/* ------------------------------------------------------------------ */}
      <section className="py-20 px-4 sm:px-6 bg-bg-secondary border-y border-border">
        <div className="max-w-6xl mx-auto">
          <h2 className="text-2xl sm:text-3xl font-bold text-text-primary text-center mb-12">
            {tr.featuresHeading}
          </h2>

          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            {/* Card 1 — Exchanges */}
            <FeatureCard
              title={tr.feature1Title}
              description={tr.feature1Desc}
              icon={
                <svg
                  className="w-6 h-6"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                  strokeWidth={1.8}
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M12 21a9.004 9.004 0 008.716-6.747M12 21a9.004 9.004 0 01-8.716-6.747M12 21c2.485 0 4.5-4.03 4.5-9S14.485 3 12 3m0 18c-2.485 0-4.5-4.03-4.5-9S9.515 3 12 3m0 0a8.997 8.997 0 017.843 4.582M12 3a8.997 8.997 0 00-7.843 4.582m15.686 0A11.953 11.953 0 0112 10.5c-2.998 0-5.74-1.1-7.843-2.918m15.686 0A8.959 8.959 0 0121 12c0 .778-.099 1.533-.284 2.253m0 0A17.919 17.919 0 0112 16.5c-3.162 0-6.133-.815-8.716-2.247m0 0A9.015 9.015 0 013 12c0-1.605.42-3.113 1.157-4.418"
                  />
                </svg>
              }
            />

            {/* Card 2 — Strategies */}
            <FeatureCard
              title={tr.feature2Title}
              description={tr.feature2Desc}
              icon={
                <svg
                  className="w-6 h-6"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                  strokeWidth={1.8}
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M10.5 6a7.5 7.5 0 107.5 7.5h-7.5V6z"
                  />
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M13.5 10.5H21A7.5 7.5 0 0013.5 3v7.5z"
                  />
                </svg>
              }
            />

            {/* Card 3 — Dashboard */}
            <FeatureCard
              title={tr.feature3Title}
              description={tr.feature3Desc}
              icon={
                <svg
                  className="w-6 h-6"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                  strokeWidth={1.8}
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M3 13.125C3 12.504 3.504 12 4.125 12h2.25c.621 0 1.125.504 1.125 1.125v6.75C7.5 20.496 6.996 21 6.375 21h-2.25A1.125 1.125 0 013 19.875v-6.75zM9.75 8.625c0-.621.504-1.125 1.125-1.125h2.25c.621 0 1.125.504 1.125 1.125v11.25c0 .621-.504 1.125-1.125 1.125h-2.25a1.125 1.125 0 01-1.125-1.125V8.625zM16.5 4.125c0-.621.504-1.125 1.125-1.125h2.25C20.496 3 21 3.504 21 4.125v15.75c0 .621-.504 1.125-1.125 1.125h-2.25a1.125 1.125 0 01-1.125-1.125V4.125z"
                  />
                </svg>
              }
            />
          </div>
        </div>
      </section>

      {/* ------------------------------------------------------------------ */}
      {/* Top Bots */}
      {/* ------------------------------------------------------------------ */}
      {topBots.length > 0 && (
        <section className="py-20 px-4 sm:px-6">
          <div className="max-w-6xl mx-auto">
            <div className="flex items-center justify-center gap-3 mb-12">
              {/* Trophy icon */}
              <svg
                className="w-7 h-7 text-yellow-400 shrink-0"
                fill="currentColor"
                viewBox="0 0 24 24"
                aria-hidden
              >
                <path d="M11.049 2.927c.3-.921 1.603-.921 1.902 0l1.519 4.674a1 1 0 00.95.69h4.915c.969 0 1.371 1.24.588 1.81l-3.976 2.888a1 1 0 00-.363 1.118l1.518 4.674c.3.922-.755 1.688-1.538 1.118l-3.976-2.888a1 1 0 00-1.176 0l-3.976 2.888c-.783.57-1.838-.197-1.538-1.118l1.518-4.674a1 1 0 00-.363-1.118l-3.976-2.888c-.784-.57-.38-1.81.588-1.81h4.914a1 1 0 00.951-.69l1.519-4.674z" />
              </svg>
              <h2 className="text-2xl sm:text-3xl font-bold text-text-primary text-center">
                {tr.topBotsHeading}
              </h2>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-5">
              {topBots.map((bot, index) => (
                <TopBotCard key={bot.id} bot={bot} rank={index + 1} tr={tr} />
              ))}
            </div>
          </div>
        </section>
      )}

      {/* ------------------------------------------------------------------ */}
      {/* Pricing */}
      {/* ------------------------------------------------------------------ */}
      <section className="py-20 px-4 sm:px-6">
        <div className="max-w-6xl mx-auto">
          <h2 className="text-2xl sm:text-3xl font-bold text-text-primary text-center mb-14">
            {tr.pricingHeading}
          </h2>

          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6 items-center">
            {plans.map((plan, index) => {
              // Treat middle plan as "popular"
              const isPopular = index === 1;
              return (
                <PricingCard key={plan.id} plan={plan} isPopular={isPopular} tr={tr} />
              );
            })}
          </div>
        </div>
      </section>

      {/* ------------------------------------------------------------------ */}
      {/* Footer */}
      {/* ------------------------------------------------------------------ */}
      <footer className="border-t border-border py-8 px-4 sm:px-6 text-center">
        <p className="text-sm text-text-secondary">{tr.footerText}</p>
      </footer>
    </div>
  );
}
