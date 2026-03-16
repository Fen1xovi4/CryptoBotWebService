import Header from '../components/Layout/Header';
import { useThemeStore } from '../stores/themeStore';
import { useSubscriptionStore } from '../stores/subscriptionStore';

const planBadgeColors: Record<string, string> = {
  Basic: 'bg-bg-tertiary text-text-secondary',
  Advanced: 'bg-accent-blue/15 text-accent-blue',
  Pro: 'bg-accent-green/15 text-accent-green',
  Admin: 'bg-accent-blue/15 text-accent-blue',
};

export default function SettingsPage() {
  const { theme, setTheme } = useThemeStore();
  const { plan, maxAccounts, maxActiveBots, currentAccounts, currentActiveBots, expiresAt, isAdmin } = useSubscriptionStore();

  return (
    <div>
      <Header title="Settings" subtitle="Configure your preferences" />

      <div className="bg-bg-secondary rounded-xl border border-border p-6 max-w-2xl">
        <h3 className="text-sm font-semibold text-text-primary mb-4">Appearance</h3>

        <div className="flex gap-4">
          <button
            onClick={() => setTheme('dark')}
            className={`flex-1 rounded-xl border-2 p-4 transition-all ${
              theme === 'dark'
                ? 'border-accent-blue shadow-lg shadow-accent-blue/20'
                : 'border-border hover:border-text-secondary'
            }`}
          >
            {/* Dark preview */}
            <div className="rounded-lg overflow-hidden mb-3">
              <div className="bg-[#1a1d2e] p-3 space-y-2">
                <div className="flex gap-2">
                  <div className="w-16 h-2 rounded bg-[#333a50]" />
                  <div className="w-10 h-2 rounded bg-[#333a50]" />
                </div>
                <div className="bg-[#242838] rounded p-2 space-y-1.5">
                  <div className="w-full h-1.5 rounded bg-[#333a50]" />
                  <div className="w-3/4 h-1.5 rounded bg-[#333a50]" />
                  <div className="w-1/2 h-1.5 rounded bg-[#333a50]" />
                </div>
              </div>
            </div>
            <p className="text-sm font-medium text-text-primary">Dark</p>
            <p className="text-xs text-text-secondary mt-0.5">Easy on the eyes</p>
          </button>

          <button
            onClick={() => setTheme('light')}
            className={`flex-1 rounded-xl border-2 p-4 transition-all ${
              theme === 'light'
                ? 'border-accent-blue shadow-lg shadow-accent-blue/20'
                : 'border-border hover:border-text-secondary'
            }`}
          >
            {/* Light preview */}
            <div className="rounded-lg overflow-hidden mb-3">
              <div className="bg-[#f5f7fa] p-3 space-y-2">
                <div className="flex gap-2">
                  <div className="w-16 h-2 rounded bg-[#d1d5db]" />
                  <div className="w-10 h-2 rounded bg-[#d1d5db]" />
                </div>
                <div className="bg-white rounded p-2 space-y-1.5">
                  <div className="w-full h-1.5 rounded bg-[#d1d5db]" />
                  <div className="w-3/4 h-1.5 rounded bg-[#d1d5db]" />
                  <div className="w-1/2 h-1.5 rounded bg-[#d1d5db]" />
                </div>
              </div>
            </div>
            <p className="text-sm font-medium text-text-primary">Light</p>
            <p className="text-xs text-text-secondary mt-0.5">Classic bright look</p>
          </button>
        </div>
      </div>

      {/* Subscription */}
      {plan && (
        <div className="bg-bg-secondary rounded-xl border border-border p-6 max-w-2xl mt-6">
          <h3 className="text-sm font-semibold text-text-primary mb-4">Subscription</h3>

          <div className="space-y-4">
            <div className="flex items-center gap-3">
              <span className="text-sm text-text-secondary">Current plan:</span>
              <span className={`inline-block text-xs font-semibold px-2 py-1 rounded ${planBadgeColors[plan] || ''}`}>
                {plan}
              </span>
            </div>

            <div className="grid grid-cols-2 gap-4">
              <div className="bg-bg-tertiary rounded-lg p-4">
                <p className="text-xs text-text-secondary mb-1">Exchange Accounts</p>
                <p className="text-lg font-bold text-text-primary">
                  {currentAccounts} {isAdmin
                    ? <span className="text-sm font-normal text-text-secondary">/ Unlimited</span>
                    : <span className="text-sm font-normal text-text-secondary">/ {maxAccounts}</span>}
                </p>
                {!isAdmin && (
                  <div className="mt-2 h-1.5 bg-bg-primary rounded-full overflow-hidden">
                    <div
                      className="h-full rounded-full bg-accent-blue transition-all"
                      style={{ width: `${Math.min((currentAccounts / maxAccounts) * 100, 100)}%` }}
                    />
                  </div>
                )}
              </div>

              <div className="bg-bg-tertiary rounded-lg p-4">
                <p className="text-xs text-text-secondary mb-1">Active Bots</p>
                <p className="text-lg font-bold text-text-primary">
                  {currentActiveBots} {isAdmin
                    ? <span className="text-sm font-normal text-text-secondary">/ Unlimited</span>
                    : <span className="text-sm font-normal text-text-secondary">/ {maxActiveBots}</span>}
                </p>
                {!isAdmin && (
                  <div className="mt-2 h-1.5 bg-bg-primary rounded-full overflow-hidden">
                    <div
                      className="h-full rounded-full bg-accent-green transition-all"
                      style={{ width: `${Math.min((currentActiveBots / maxActiveBots) * 100, 100)}%` }}
                    />
                  </div>
                )}
              </div>
            </div>

            {!isAdmin && (
              <div className="bg-bg-tertiary rounded-lg p-4">
                <p className="text-xs text-text-secondary mb-1">Subscription Period</p>
                {expiresAt ? (() => {
                  const exp = new Date(expiresAt);
                  const now = new Date();
                  const daysLeft = Math.ceil((exp.getTime() - now.getTime()) / (1000 * 60 * 60 * 24));
                  const isExpired = daysLeft <= 0;
                  return (
                    <div>
                      <p className={`text-lg font-bold ${isExpired ? 'text-accent-red' : daysLeft <= 7 ? 'text-yellow-400' : 'text-text-primary'}`}>
                        {isExpired
                          ? 'Expired'
                          : `${daysLeft} ${daysLeft === 1 ? 'day' : 'days'} left`}
                      </p>
                      <p className="text-xs text-text-secondary mt-1">
                        {isExpired ? 'Expired' : 'Expires'}: {exp.toLocaleDateString()}
                      </p>
                    </div>
                  );
                })() : (
                  <p className="text-lg font-bold text-accent-green">Permanent</p>
                )}
              </div>
            )}

            {!isAdmin && plan !== 'Pro' && (
              <p className="text-xs text-text-secondary">
                Contact admin to upgrade your plan.
              </p>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
