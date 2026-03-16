import { useState, useEffect, useCallback } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import api from '../api/client';
import { useGuestPaymentStore, type GuestPaymentSession } from '../stores/guestPaymentStore';

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

// ---------------------------------------------------------------------------
// Logo
// ---------------------------------------------------------------------------

function Logo() {
  return (
    <Link to="/" className="flex items-center gap-2.5">
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
    </Link>
  );
}

// ---------------------------------------------------------------------------
// StepIndicator
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

// ---------------------------------------------------------------------------
// PlanFeature
// ---------------------------------------------------------------------------

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
      <div className="bg-bg-tertiary rounded-xl border border-border p-6 space-y-6">
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
  session: GuestPaymentSession;
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
      setCountdown((prev) => Math.max(0, prev - 1));
    }, 1000);
    return () => clearInterval(timer);
  }, []);

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
      <div className="bg-bg-tertiary rounded-xl border border-border p-5">
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
            {countdown === 0 ? 'Expired' : `${formatCountdown(countdown)}`}
          </span>
        </div>

        <div className="flex items-center gap-2 flex-wrap">
          <span className="inline-flex items-center text-xs font-semibold px-2.5 py-1 rounded-full bg-bg-secondary text-text-secondary border border-border">
            {session.network}
          </span>
          <span className="inline-flex items-center text-xs font-semibold px-2.5 py-1 rounded-full bg-accent-blue/10 text-accent-blue">
            {session.token}
          </span>
          <span className="inline-flex items-center text-xs font-semibold px-2.5 py-1 rounded-full bg-bg-secondary text-text-secondary border border-border capitalize">
            {session.plan}
          </span>
        </div>
      </div>

      {/* Amount */}
      <div className="bg-bg-tertiary rounded-xl border border-border p-5">
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
      <div className="bg-bg-tertiary rounded-xl border border-border p-5">
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
                : 'border-border bg-bg-secondary text-text-secondary hover:text-text-primary hover:border-accent-blue hover:bg-accent-blue/5'
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
  session: GuestPaymentSession;
  onTryAgain: () => void;
}

function Step4Result({ variant, session, onTryAgain }: Step4Props) {
  const [codeCopied, setCodeCopied] = useState(false);

  const handleCopyCode = () => {
    if (!session.inviteCode) return;
    navigator.clipboard.writeText(session.inviteCode).then(() => {
      setCodeCopied(true);
      setTimeout(() => setCodeCopied(false), 2500);
    });
  };

  if (variant === 'confirmed') {
    return (
      <div className="max-w-md text-center space-y-6">
        {/* Success icon */}
        <div className="flex justify-center">
          <div className="w-20 h-20 rounded-full bg-accent-green/15 flex items-center justify-center shadow-lg shadow-accent-green/20">
            <svg className="w-10 h-10 text-accent-green" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M4.5 12.75l6 6 9-13.5" />
            </svg>
          </div>
        </div>

        <div>
          <h3 className="text-2xl font-bold text-text-primary mb-1">Payment confirmed!</h3>
          <p className="text-text-secondary text-sm">
            Your <span className="font-semibold text-accent-green">{session.plan}</span> plan payment was received.
          </p>
        </div>

        {/* Invite code — prominent display */}
        {session.inviteCode ? (
          <div className="bg-accent-blue/5 border-2 border-accent-blue/40 rounded-2xl p-6 space-y-4">
            <div className="flex items-center justify-center gap-2 text-accent-blue">
              <svg className="w-5 h-5 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M15.75 5.25a3 3 0 013 3m3 0a6 6 0 01-7.029 5.912c-.563-.097-1.159.026-1.563.43L10.5 17.25H8.25v2.25H6v2.25H2.25v-2.818c0-.597.237-1.17.659-1.591l6.499-6.499c.404-.404.527-1 .43-1.563A6 6 0 1121.75 8.25z" />
              </svg>
              <p className="text-sm font-semibold uppercase tracking-wide">Your Invite Code</p>
            </div>

            <div className="flex items-center gap-3">
              <p className="flex-1 font-mono text-2xl font-extrabold text-text-primary tracking-[0.2em] text-center bg-bg-primary border border-border rounded-xl px-4 py-3 select-all">
                {session.inviteCode}
              </p>
              <button
                onClick={handleCopyCode}
                title="Copy invite code"
                className={`flex-shrink-0 px-4 py-3 rounded-xl text-sm font-semibold border-2 transition-all ${
                  codeCopied
                    ? 'border-accent-green bg-accent-green/15 text-accent-green'
                    : 'border-accent-blue/40 bg-accent-blue/10 text-accent-blue hover:bg-accent-blue/20'
                }`}
                aria-label="Copy invite code"
              >
                {codeCopied ? (
                  <span className="flex items-center gap-1.5">
                    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M4.5 12.75l6 6 9-13.5" />
                    </svg>
                    Copied!
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

            <p className="text-sm text-text-secondary leading-relaxed">
              Use this code to create your account. It is single-use — do not share it until you are ready to register.
            </p>

            <Link
              to={`/register?code=${encodeURIComponent(session.inviteCode)}`}
              className="flex items-center justify-center gap-2 w-full bg-accent-blue hover:bg-accent-blue/90 text-white font-semibold py-3 px-6 rounded-xl text-sm transition-colors shadow-lg shadow-accent-blue/25"
            >
              Create Your Account
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 4.5L21 12m0 0l-7.5 7.5M21 12H3" />
              </svg>
            </Link>
          </div>
        ) : (
          <div className="bg-accent-yellow/5 border border-accent-yellow/30 rounded-xl p-4 text-sm text-accent-yellow">
            Your invite code is being generated. Please check back shortly or contact support.
          </div>
        )}

        {session.txHash && (
          <div className="bg-bg-tertiary rounded-xl border border-border p-4 text-left">
            <p className="text-xs text-text-secondary mb-1 font-medium">Transaction Hash</p>
            <p className="font-mono text-xs text-text-primary break-all">{session.txHash}</p>
          </div>
        )}

        <Link
          to="/"
          className="inline-flex items-center gap-1.5 text-sm text-text-secondary hover:text-text-primary transition-colors"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M10.5 19.5L3 12m0 0l7.5-7.5M3 12h18" />
          </svg>
          Back to home
        </Link>
      </div>
    );
  }

  // Expired
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

      <Link
        to="/"
        className="inline-flex items-center gap-1.5 text-sm text-text-secondary hover:text-text-primary transition-colors"
      >
        <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M10.5 19.5L3 12m0 0l7.5-7.5M3 12h18" />
        </svg>
        Back to home
      </Link>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main GuestPaymentPage
// ---------------------------------------------------------------------------

export default function GuestPaymentPage() {
  const [searchParams] = useSearchParams();
  const [step, setStep] = useState<Step>(1);
  const [plans, setPlans] = useState<SubscriptionPlan[]>([]);
  const [plansLoading, setPlansLoading] = useState(true);
  const [selectedPlan, setSelectedPlan] = useState<string | null>(null);
  const [selectedNetwork, setSelectedNetwork] = useState('TRC20');
  const [selectedToken, setSelectedToken] = useState('USDT');
  const [resultVariant, setResultVariant] = useState<ResultVariant>('confirmed');

  const { session, loading, error, createSession, checkSession, cancelSession, reset } =
    useGuestPaymentStore();

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

  // Poll session status every 5 seconds while on step 3
  useEffect(() => {
    if (step !== 3 || !session) return;

    const poll = setInterval(() => {
      checkSession();
    }, 5000);

    return () => clearInterval(poll);
  }, [step, session, checkSession]);

  // React to session status changes
  useEffect(() => {
    if (!session) return;

    if (session.status === 'Confirmed' || session.status === 'ManuallyConfirmed') {
      setResultVariant('confirmed');
      setStep(4);
    } else if (session.status === 'Expired') {
      setResultVariant('expired');
      setStep(4);
    }
  }, [session?.status]);

  const handlePlanSelect = (plan: string) => setSelectedPlan(plan);

  const handleStep1Next = () => {
    if (selectedPlan) setStep(2);
  };

  const handleStep2Next = useCallback(async () => {
    if (!selectedPlan) return;
    await createSession(selectedPlan, selectedNetwork, selectedToken);
    const state = useGuestPaymentStore.getState();
    if (state.session && !state.error) {
      setStep(3);
    }
  }, [selectedPlan, selectedNetwork, selectedToken, createSession]);

  const handleCancel = useCallback(async () => {
    await cancelSession();
    reset();
    setStep(1);
  }, [cancelSession, reset]);

  const handleTryAgain = () => {
    reset();
    setStep(1);
  };

  return (
    <div className="min-h-screen bg-bg-primary text-text-primary">
      {/* Minimal header */}
      <header className="border-b border-border bg-bg-primary/80 backdrop-blur-md sticky top-0 z-50">
        <div className="max-w-5xl mx-auto px-4 sm:px-6 h-14 flex items-center justify-between">
          <Logo />
          <Link
            to="/login"
            className="text-sm font-medium text-text-secondary hover:text-text-primary transition-colors"
          >
            Sign In
          </Link>
        </div>
      </header>

      {/* Page content */}
      <main className="max-w-5xl mx-auto px-4 sm:px-6 py-10">
        {/* Page heading */}
        <div className="mb-8">
          <h1 className="text-2xl font-bold text-text-primary">Buy a Subscription</h1>
          <p className="text-sm text-text-secondary mt-1">
            Pay with crypto and receive an invite code to create your account
          </p>
        </div>

        {/* Wizard card */}
        <div className="bg-bg-secondary rounded-2xl border border-border p-6 sm:p-8 max-w-3xl">
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

          {step === 3 && session && (
            <Step3ActivePayment session={session} onCancel={handleCancel} />
          )}

          {step === 4 && session && (
            <Step4Result variant={resultVariant} session={session} onTryAgain={handleTryAgain} />
          )}

          {/* Fallback: session unexpectedly null on step 3 */}
          {step === 3 && !session && (
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

        {/* Trust note */}
        <div className="mt-6 max-w-3xl flex flex-wrap items-center gap-x-6 gap-y-2">
          <span className="flex items-center gap-1.5 text-xs text-text-secondary">
            <svg className="w-3.5 h-3.5 text-accent-green" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M9 12.75L11.25 15 15 9.75m-3-7.036A11.959 11.959 0 013.598 6 11.99 11.99 0 003 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285z" />
            </svg>
            Secure payment
          </span>
          <span className="flex items-center gap-1.5 text-xs text-text-secondary">
            <svg className="w-3.5 h-3.5 text-accent-blue" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M12 6v6h4.5m4.5 0a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            Auto-detected on-chain
          </span>
          <span className="flex items-center gap-1.5 text-xs text-text-secondary">
            <svg className="w-3.5 h-3.5 text-accent-yellow" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M15.75 5.25a3 3 0 013 3m3 0a6 6 0 01-7.029 5.912c-.563-.097-1.159.026-1.563.43L10.5 17.25H8.25v2.25H6v2.25H2.25v-2.818c0-.597.237-1.17.659-1.591l6.499-6.499c.404-.404.527-1 .43-1.563A6 6 0 1121.75 8.25z" />
            </svg>
            Invite code delivered instantly
          </span>
        </div>
      </main>
    </div>
  );
}
