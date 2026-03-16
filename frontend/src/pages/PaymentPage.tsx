import { useState, useEffect, useCallback } from 'react';
import { useSearchParams, useNavigate } from 'react-router-dom';
import api from '../api/client';
import Header from '../components/Layout/Header';
import { usePaymentStore, type PaymentSession } from '../stores/paymentStore';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface SubscriptionPlan {
  plan: string;
  nameRu: string;
  nameEn: string;
  maxAccounts: number;
  maxActiveBots: number;
  priceMonthly: number;
  priceLabel: string;
}

type Step = 1 | 2 | 3 | 4;

type ResultVariant = 'confirmed' | 'expired';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function formatCountdown(seconds: number): string {
  if (seconds <= 0) return '00:00';
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}

function formatDate(iso: string): string {
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function StepIndicator({ current }: { current: Step }) {
  const steps: { n: Step; label: string }[] = [
    { n: 1, label: 'Plan' },
    { n: 2, label: 'Network' },
    { n: 3, label: 'Payment' },
    { n: 4, label: 'Done' },
  ];

  return (
    <div className="flex items-center gap-0 mb-8">
      {steps.map(({ n, label }, idx) => {
        const done = current > n;
        const active = current === n;
        return (
          <div key={n} className="flex items-center">
            <div className="flex flex-col items-center">
              <div
                className={`w-8 h-8 rounded-full flex items-center justify-center text-xs font-bold transition-colors ${
                  done
                    ? 'bg-accent-green text-white'
                    : active
                    ? 'bg-accent-blue text-white shadow-lg shadow-accent-blue/30'
                    : 'bg-bg-tertiary text-text-secondary border border-border'
                }`}
              >
                {done ? (
                  <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M4.5 12.75l6 6 9-13.5" />
                  </svg>
                ) : (
                  n
                )}
              </div>
              <span
                className={`text-xs mt-1 font-medium ${
                  active ? 'text-accent-blue' : done ? 'text-accent-green' : 'text-text-secondary'
                }`}
              >
                {label}
              </span>
            </div>
            {idx < steps.length - 1 && (
              <div
                className={`w-16 h-0.5 mx-1 mb-5 rounded transition-colors ${
                  current > n ? 'bg-accent-green' : 'bg-border'
                }`}
              />
            )}
          </div>
        );
      })}
    </div>
  );
}

function PlanFeature({ children }: { children: React.ReactNode }) {
  return (
    <li className="flex items-center gap-2 text-sm text-text-secondary">
      <svg className="w-4 h-4 flex-shrink-0 text-accent-green" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
        <path strokeLinecap="round" strokeLinejoin="round" d="M4.5 12.75l6 6 9-13.5" />
      </svg>
      {children}
    </li>
  );
}

function StatusBadge({ status }: { status: string }) {
  const cfg: Record<string, string> = {
    Pending: 'bg-yellow-400/10 text-yellow-400',
    Confirmed: 'bg-accent-green/10 text-accent-green',
    ManuallyConfirmed: 'bg-accent-green/10 text-accent-green',
    Expired: 'bg-bg-tertiary text-text-secondary',
    Cancelled: 'bg-accent-red/10 text-accent-red',
  };
  return (
    <span className={`inline-flex items-center text-xs font-semibold px-2 py-0.5 rounded-full ${cfg[status] ?? 'bg-bg-tertiary text-text-secondary'}`}>
      {status}
    </span>
  );
}

// ---------------------------------------------------------------------------
// Step 1: Plan Selection
// ---------------------------------------------------------------------------

interface Step1Props {
  plans: SubscriptionPlan[];
  loading: boolean;
  selectedPlan: string | null;
  onSelect: (plan: string) => void;
  onNext: () => void;
}

function Step1PlanSelection({ plans, loading, selectedPlan, onSelect, onNext }: Step1Props) {
  const planAccentMap: Record<string, { border: string; badge: string; shadow: string }> = {
    Basic: {
      border: 'border-border',
      badge: 'bg-bg-tertiary text-text-secondary',
      shadow: '',
    },
    Advanced: {
      border: 'border-accent-blue',
      badge: 'bg-accent-blue/15 text-accent-blue',
      shadow: 'shadow-lg shadow-accent-blue/10',
    },
    Pro: {
      border: 'border-accent-green',
      badge: 'bg-accent-green/15 text-accent-green',
      shadow: 'shadow-lg shadow-accent-green/10',
    },
  };

  if (loading) {
    return (
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        {[1, 2, 3].map((i) => (
          <div key={i} className="bg-bg-secondary rounded-xl border border-border p-6 animate-pulse">
            <div className="h-4 w-20 bg-bg-tertiary rounded mb-3" />
            <div className="h-8 w-16 bg-bg-tertiary rounded mb-4" />
            <div className="space-y-2">
              <div className="h-3 w-full bg-bg-tertiary rounded" />
              <div className="h-3 w-3/4 bg-bg-tertiary rounded" />
            </div>
          </div>
        ))}
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        {plans.map((p) => {
          const isSelected = selectedPlan === p.plan;
          const accent = planAccentMap[p.plan] ?? planAccentMap['Basic'];
          return (
            <button
              key={p.plan}
              onClick={() => onSelect(p.plan)}
              className={`relative text-left bg-bg-secondary rounded-xl border-2 p-6 transition-all cursor-pointer ${
                isSelected ? `${accent.border} ${accent.shadow}` : 'border-border hover:border-text-secondary'
              }`}
              aria-pressed={isSelected}
            >
              {p.plan === 'Advanced' && (
                <span className="absolute -top-3 left-1/2 -translate-x-1/2 text-xs font-bold px-3 py-0.5 rounded-full bg-accent-blue text-white shadow shadow-accent-blue/30">
                  Popular
                </span>
              )}

              <div className="flex items-start justify-between mb-4">
                <div>
                  <span className={`inline-block text-xs font-semibold px-2 py-0.5 rounded-full mb-2 ${accent.badge}`}>
                    {p.plan}
                  </span>
                  <p className="text-2xl font-bold text-text-primary">{p.priceLabel}</p>
                </div>
                {isSelected && (
                  <div className="w-6 h-6 rounded-full bg-accent-blue flex items-center justify-center flex-shrink-0">
                    <svg className="w-3.5 h-3.5 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M4.5 12.75l6 6 9-13.5" />
                    </svg>
                  </div>
                )}
              </div>

              <ul className="space-y-2">
                <PlanFeature>Up to {p.maxAccounts} exchange account{p.maxAccounts !== 1 ? 's' : ''}</PlanFeature>
                <PlanFeature>Up to {p.maxActiveBots} active bot{p.maxActiveBots !== 1 ? 's' : ''}</PlanFeature>
                <PlanFeature>All supported exchanges</PlanFeature>
                <PlanFeature>Real-time monitoring</PlanFeature>
              </ul>
            </button>
          );
        })}
      </div>

      <div className="flex justify-end">
        <button
          onClick={onNext}
          disabled={!selectedPlan}
          className="inline-flex items-center gap-2 bg-accent-blue hover:bg-accent-blue/90 disabled:opacity-40 disabled:cursor-not-allowed text-white font-medium py-2.5 px-6 rounded-lg text-sm transition-colors shadow-lg shadow-accent-blue/25"
        >
          Continue
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 4.5L21 12m0 0l-7.5 7.5M21 12H3" />
          </svg>
        </button>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Step 2: Network & Token Selection
// ---------------------------------------------------------------------------

interface Step2Props {
  selectedNetwork: string;
  selectedToken: string;
  onNetworkChange: (v: string) => void;
  onTokenChange: (v: string) => void;
  onBack: () => void;
  onNext: () => void;
  loading: boolean;
  error: string | null;
}

function Step2NetworkToken({
  selectedNetwork,
  selectedToken,
  onNetworkChange,
  onTokenChange,
  onBack,
  onNext,
  loading,
  error,
}: Step2Props) {
  const networks = ['TRC20', 'BEP20'];
  const tokens = ['USDT', 'USDC'];

  function OptionButton({
    value,
    selected,
    onSelect,
    children,
  }: {
    value: string;
    selected: boolean;
    onSelect: (v: string) => void;
    children: React.ReactNode;
  }) {
    return (
      <button
        onClick={() => onSelect(value)}
        className={`px-5 py-2.5 rounded-lg border-2 text-sm font-semibold transition-all ${
          selected
            ? 'border-accent-blue bg-accent-blue/10 text-accent-blue shadow shadow-accent-blue/15'
            : 'border-border bg-bg-tertiary text-text-secondary hover:border-text-secondary hover:text-text-primary'
        }`}
        aria-pressed={selected}
      >
        {children}
      </button>
    );
  }

  return (
    <div className="space-y-6 max-w-lg">
      <div className="bg-bg-secondary rounded-xl border border-border p-6 space-y-6">
        <div>
          <p className="text-sm font-semibold text-text-primary mb-3">Network</p>
          <div className="flex gap-3">
            {networks.map((n) => (
              <OptionButton key={n} value={n} selected={selectedNetwork === n} onSelect={onNetworkChange}>
                {n}
              </OptionButton>
            ))}
          </div>
          <p className="text-xs text-text-secondary mt-2">
            {selectedNetwork === 'TRC20' && 'Tron network — fast, low fees'}
            {selectedNetwork === 'BEP20' && 'BNB Smart Chain — broad exchange support'}
          </p>
        </div>

        <div className="border-t border-border" />

        <div>
          <p className="text-sm font-semibold text-text-primary mb-3">Token</p>
          <div className="flex gap-3">
            {tokens.map((t) => (
              <OptionButton key={t} value={t} selected={selectedToken === t} onSelect={onTokenChange}>
                {t}
              </OptionButton>
            ))}
          </div>
        </div>

        {error && (
          <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-3 rounded-lg">
            {error}
          </div>
        )}
      </div>

      <div className="flex items-center justify-between">
        <button
          onClick={onBack}
          className="inline-flex items-center gap-2 text-sm text-text-secondary hover:text-text-primary transition-colors"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M10.5 19.5L3 12m0 0l7.5-7.5M3 12h18" />
          </svg>
          Back
        </button>
        <button
          onClick={onNext}
          disabled={loading}
          className="inline-flex items-center gap-2 bg-accent-blue hover:bg-accent-blue/90 disabled:opacity-40 disabled:cursor-not-allowed text-white font-medium py-2.5 px-6 rounded-lg text-sm transition-colors shadow-lg shadow-accent-blue/25"
        >
          {loading ? (
            <>
              <svg className="w-4 h-4 animate-spin" fill="none" viewBox="0 0 24 24">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
              </svg>
              Creating session...
            </>
          ) : (
            <>
              Proceed to Payment
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 4.5L21 12m0 0l-7.5 7.5M21 12H3" />
              </svg>
            </>
          )}
        </button>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Step 3: Active Payment
// ---------------------------------------------------------------------------

interface Step3Props {
  session: PaymentSession;
  onCancel: () => void;
}

function Step3ActivePayment({ session, onCancel }: Step3Props) {
  const [countdown, setCountdown] = useState<number>(() => {
    const remaining = Math.floor((new Date(session.expiresAt).getTime() - Date.now()) / 1000);
    return Math.max(0, remaining);
  });
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    const timer = setInterval(() => {
      setCountdown((prev) => {
        const next = Math.max(0, prev - 1);
        return next;
      });
    }, 1000);
    return () => clearInterval(timer);
  }, []);

  // Recalculate when session changes (e.g. after a check returns updated remainingSeconds)
  useEffect(() => {
    const remaining = Math.floor((new Date(session.expiresAt).getTime() - Date.now()) / 1000);
    setCountdown(Math.max(0, remaining));
  }, [session.expiresAt]);

  const handleCopy = () => {
    navigator.clipboard.writeText(session.walletAddress).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    });
  };

  const isUrgent = countdown < 300 && countdown > 0;

  return (
    <div className="max-w-lg space-y-4">
      {/* Status bar */}
      <div className="bg-bg-secondary rounded-xl border border-border p-5">
        <div className="flex items-center justify-between mb-3">
          <div className="flex items-center gap-2">
            <span className="relative flex h-2.5 w-2.5">
              <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-accent-blue opacity-75" />
              <span className="relative inline-flex rounded-full h-2.5 w-2.5 bg-accent-blue" />
            </span>
            <span className="text-sm font-medium text-text-primary">Waiting for payment...</span>
          </div>
          <span
            className={`text-sm font-mono font-bold tabular-nums ${
              isUrgent ? 'text-accent-red' : countdown === 0 ? 'text-text-secondary' : 'text-text-primary'
            }`}
          >
            {countdown === 0 ? 'Expired' : `⏱ ${formatCountdown(countdown)}`}
          </span>
        </div>

        <div className="flex items-center gap-2 flex-wrap">
          <span className="inline-flex items-center text-xs font-semibold px-2.5 py-1 rounded-full bg-bg-tertiary text-text-secondary border border-border">
            {session.network}
          </span>
          <span className="inline-flex items-center text-xs font-semibold px-2.5 py-1 rounded-full bg-accent-blue/10 text-accent-blue">
            {session.token}
          </span>
          <span className="inline-flex items-center text-xs font-semibold px-2.5 py-1 rounded-full bg-bg-tertiary text-text-secondary border border-border capitalize">
            {session.plan}
          </span>
        </div>
      </div>

      {/* Amount */}
      <div className="bg-bg-secondary rounded-xl border border-border p-5">
        <p className="text-xs text-text-secondary mb-1 font-medium uppercase tracking-wide">Send exactly</p>
        <p className="text-3xl font-bold text-text-primary">
          ${session.expectedAmount}{' '}
          <span className="text-accent-blue text-xl">{session.token}</span>
        </p>
        <p className="text-xs text-text-secondary mt-1">
          Any other amount will require manual confirmation
        </p>
      </div>

      {/* Wallet address */}
      <div className="bg-bg-secondary rounded-xl border border-border p-5">
        <p className="text-xs text-text-secondary mb-2 font-medium uppercase tracking-wide">Wallet Address</p>
        <div className="flex items-center gap-3">
          <p className="font-mono text-sm text-text-primary break-all flex-1 select-all bg-bg-primary border border-border rounded-lg px-3 py-2.5">
            {session.walletAddress}
          </p>
          <button
            onClick={handleCopy}
            title="Copy address"
            className={`flex-shrink-0 px-3 py-2.5 rounded-lg text-sm font-medium border transition-all ${
              copied
                ? 'border-accent-green bg-accent-green/10 text-accent-green'
                : 'border-border bg-bg-tertiary text-text-secondary hover:text-text-primary hover:border-accent-blue hover:bg-accent-blue/5'
            }`}
            aria-label="Copy wallet address"
          >
            {copied ? (
              <span className="flex items-center gap-1.5">
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M4.5 12.75l6 6 9-13.5" />
                </svg>
                Copied
              </span>
            ) : (
              <span className="flex items-center gap-1.5">
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M15.666 3.888A2.25 2.25 0 0013.5 2.25h-3c-1.03 0-1.9.693-2.166 1.638m7.332 0c.055.194.084.4.084.612v0a.75.75 0 01-.75.75H9.75a.75.75 0 01-.75-.75v0c0-.212.03-.418.084-.612m7.332 0c.646.049 1.288.11 1.927.184 1.1.128 1.907 1.077 1.907 2.185V19.5a2.25 2.25 0 01-2.25 2.25H6.75A2.25 2.25 0 014.5 19.5V6.257c0-1.108.806-2.057 1.907-2.185a48.208 48.208 0 011.927-.184" />
                </svg>
                Copy
              </span>
            )}
          </button>
        </div>
        <p className="text-xs text-accent-red/80 mt-2">
          Send only {session.token} on the {session.network} network to this address.
        </p>
      </div>

      {/* Cancel */}
      <div className="flex justify-end">
        <button
          onClick={() => {
            if (!confirm('Cancel this payment session?')) return;
            onCancel();
          }}
          className="px-4 py-2 text-sm font-medium text-accent-red bg-accent-red/10 rounded-lg hover:bg-accent-red/20 transition-colors border border-accent-red/20"
        >
          Cancel Payment
        </button>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Step 4: Result
// ---------------------------------------------------------------------------

interface Step4Props {
  variant: ResultVariant;
  session: PaymentSession;
  onTryAgain: () => void;
}

function Step4Result({ variant, session, onTryAgain }: Step4Props) {
  const navigate = useNavigate();

  if (variant === 'confirmed') {
    return (
      <div className="max-w-md text-center space-y-6">
        <div className="flex justify-center">
          <div className="w-20 h-20 rounded-full bg-accent-green/15 flex items-center justify-center shadow-lg shadow-accent-green/20">
            <svg className="w-10 h-10 text-accent-green" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M4.5 12.75l6 6 9-13.5" />
            </svg>
          </div>
        </div>

        <div>
          <h3 className="text-2xl font-bold text-text-primary mb-1">Payment successful!</h3>
          <p className="text-text-secondary text-sm">
            Your <span className="font-semibold text-accent-green">{session.plan}</span> subscription has been activated.
          </p>
        </div>

        {session.txHash && (
          <div className="bg-bg-secondary rounded-xl border border-border p-4 text-left">
            <p className="text-xs text-text-secondary mb-1 font-medium">Transaction Hash</p>
            <p className="font-mono text-xs text-text-primary break-all">{session.txHash}</p>
          </div>
        )}

        {session.confirmedAt && (
          <p className="text-xs text-text-secondary">
            Confirmed at: {formatDate(session.confirmedAt)}
          </p>
        )}

        <button
          onClick={() => navigate('/dashboard')}
          className="w-full bg-accent-blue hover:bg-accent-blue/90 text-white font-medium py-2.5 px-6 rounded-lg text-sm transition-colors shadow-lg shadow-accent-blue/25"
        >
          Go to Dashboard
        </button>
      </div>
    );
  }

  return (
    <div className="max-w-md text-center space-y-6">
      <div className="flex justify-center">
        <div className="w-20 h-20 rounded-full bg-accent-red/15 flex items-center justify-center shadow-lg shadow-accent-red/20">
          <svg className="w-10 h-10 text-accent-red" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
          </svg>
        </div>
      </div>

      <div>
        <h3 className="text-2xl font-bold text-text-primary mb-1">Session expired</h3>
        <p className="text-text-secondary text-sm">
          The payment window has closed. Please start a new payment session.
        </p>
      </div>

      <button
        onClick={onTryAgain}
        className="w-full bg-accent-blue hover:bg-accent-blue/90 text-white font-medium py-2.5 px-6 rounded-lg text-sm transition-colors shadow-lg shadow-accent-blue/25"
      >
        Try Again
      </button>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Payment History
// ---------------------------------------------------------------------------

function PaymentHistory({ history }: { history: PaymentSession[] }) {
  if (history.length === 0) {
    return (
      <div className="bg-bg-secondary rounded-xl border border-border p-8 text-center">
        <p className="text-sm text-text-secondary">No payment history yet.</p>
      </div>
    );
  }

  return (
    <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
      <div className="overflow-x-auto">
      <table className="w-full">
        <thead>
          <tr className="text-xs text-text-secondary border-b border-border">
            <th className="text-left px-5 py-2.5 font-medium">Date</th>
            <th className="text-left px-5 py-2.5 font-medium">Plan</th>
            <th className="text-left px-5 py-2.5 font-medium">Amount</th>
            <th className="text-left px-5 py-2.5 font-medium">Network / Token</th>
            <th className="text-left px-5 py-2.5 font-medium">Status</th>
            <th className="text-left px-5 py-2.5 font-medium">Tx Hash</th>
          </tr>
        </thead>
        <tbody>
          {history.map((s) => (
            <tr key={s.id} className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors">
              <td className="px-5 py-3 text-sm text-text-secondary whitespace-nowrap">
                {formatDate(s.createdAt)}
              </td>
              <td className="px-5 py-3 text-sm font-medium text-text-primary">{s.plan}</td>
              <td className="px-5 py-3 text-sm text-text-primary">
                ${s.receivedAmount ?? s.expectedAmount}{' '}
                <span className="text-text-secondary">{s.token}</span>
              </td>
              <td className="px-5 py-3 text-sm text-text-secondary">
                <span className="inline-flex items-center gap-1">
                  <span className="font-medium text-text-primary">{s.network}</span>
                  <span>/</span>
                  {s.token}
                </span>
              </td>
              <td className="px-5 py-3">
                <StatusBadge status={s.status} />
              </td>
              <td className="px-5 py-3 text-xs text-text-secondary font-mono">
                {s.txHash ? (
                  <span className="truncate max-w-[120px] inline-block" title={s.txHash}>
                    {s.txHash.slice(0, 8)}...{s.txHash.slice(-6)}
                  </span>
                ) : (
                  '—'
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main PaymentPage
// ---------------------------------------------------------------------------

export default function PaymentPage() {
  const [searchParams] = useSearchParams();
  const [step, setStep] = useState<Step>(1);
  const [plans, setPlans] = useState<SubscriptionPlan[]>([]);
  const [plansLoading, setPlansLoading] = useState(true);
  const [selectedPlan, setSelectedPlan] = useState<string | null>(null);
  const [selectedNetwork, setSelectedNetwork] = useState('TRC20');
  const [selectedToken, setSelectedToken] = useState('USDT');
  const [resultVariant, setResultVariant] = useState<ResultVariant>('confirmed');

  const { activeSession, history, loading, error, createSession, checkSession, cancelSession, fetchHistory, reset } =
    usePaymentStore();

  // Fetch plans on mount
  useEffect(() => {
    setPlansLoading(true);
    api
      .get<SubscriptionPlan[]>('/subscriptions/plans')
      .then((r) => setPlans(r.data))
      .catch(() => setPlans([]))
      .finally(() => setPlansLoading(false));
  }, []);

  // Pre-select plan from URL ?plan= param
  useEffect(() => {
    const planParam = searchParams.get('plan');
    if (planParam) {
      setSelectedPlan(planParam);
    }
  }, [searchParams]);

  // Fetch payment history on mount
  useEffect(() => {
    fetchHistory();
  }, [fetchHistory]);

  // Poll session status every 5 seconds while on step 3
  useEffect(() => {
    if (step !== 3 || !activeSession) return;

    const poll = setInterval(() => {
      checkSession(activeSession.id);
    }, 5000);

    return () => clearInterval(poll);
  }, [step, activeSession, checkSession]);

  // React to session status changes — advance or show result
  useEffect(() => {
    if (!activeSession) return;

    if (activeSession.status === 'Confirmed' || activeSession.status === 'ManuallyConfirmed') {
      setResultVariant('confirmed');
      setStep(4);
    } else if (activeSession.status === 'Expired') {
      setResultVariant('expired');
      setStep(4);
    }
  }, [activeSession?.status]);

  const handlePlanSelect = (plan: string) => setSelectedPlan(plan);

  const handleStep1Next = () => {
    if (selectedPlan) setStep(2);
  };

  const handleStep2Next = useCallback(async () => {
    if (!selectedPlan) return;
    await createSession(selectedPlan, selectedNetwork, selectedToken);
    // createSession sets activeSession on success; if no error, advance
    const state = usePaymentStore.getState();
    if (state.activeSession && !state.error) {
      setStep(3);
    }
  }, [selectedPlan, selectedNetwork, selectedToken, createSession]);

  const handleCancel = useCallback(async () => {
    if (!activeSession) return;
    await cancelSession(activeSession.id);
    fetchHistory();
    setStep(1);
  }, [activeSession, cancelSession, fetchHistory]);

  const handleTryAgain = () => {
    reset();
    setStep(1);
  };

  return (
    <div>
      <Header title="Subscription" subtitle="Choose a plan and pay with crypto" />

      {/* Wizard card */}
      <div className="bg-bg-secondary rounded-xl border border-border p-6 mb-6 max-w-2xl">
        <StepIndicator current={step} />

        {step === 1 && (
          <Step1PlanSelection
            plans={plans}
            loading={plansLoading}
            selectedPlan={selectedPlan}
            onSelect={handlePlanSelect}
            onNext={handleStep1Next}
          />
        )}

        {step === 2 && (
          <Step2NetworkToken
            selectedNetwork={selectedNetwork}
            selectedToken={selectedToken}
            onNetworkChange={setSelectedNetwork}
            onTokenChange={setSelectedToken}
            onBack={() => setStep(1)}
            onNext={handleStep2Next}
            loading={loading}
            error={error}
          />
        )}

        {step === 3 && activeSession && (
          <Step3ActivePayment session={activeSession} onCancel={handleCancel} />
        )}

        {step === 4 && activeSession && (
          <Step4Result variant={resultVariant} session={activeSession} onTryAgain={handleTryAgain} />
        )}

        {/* Fallback: session unexpectedly null on step 3 */}
        {step === 3 && !activeSession && (
          <div className="text-center py-8">
            <p className="text-sm text-text-secondary mb-4">Payment session not found.</p>
            <button
              onClick={() => setStep(1)}
              className="px-4 py-2 text-sm font-medium bg-bg-tertiary text-text-secondary rounded-lg hover:bg-border transition-colors"
            >
              Start over
            </button>
          </div>
        )}
      </div>

      {/* Payment History */}
      <div className="max-w-4xl">
        <h3 className="text-sm font-semibold text-text-primary mb-3">Payment History</h3>
        <PaymentHistory history={history} />
      </div>
    </div>
  );
}
