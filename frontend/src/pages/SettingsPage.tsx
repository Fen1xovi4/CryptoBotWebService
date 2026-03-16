import { useState, useEffect } from 'react';
import { QRCodeSVG } from 'qrcode.react';
import Header from '../components/Layout/Header';
import { useThemeStore } from '../stores/themeStore';
import { useSubscriptionStore } from '../stores/subscriptionStore';
import api from '../api/client';

const planBadgeColors: Record<string, string> = {
  Basic: 'bg-bg-tertiary text-text-secondary',
  Advanced: 'bg-accent-blue/15 text-accent-blue',
  Pro: 'bg-accent-green/15 text-accent-green',
  Admin: 'bg-accent-blue/15 text-accent-blue',
};

const inputClass = 'w-full bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-text-primary text-sm focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all';

function TwoFactorSection() {
  const [status, setStatus] = useState<boolean | null>(null);
  const [setupData, setSetupData] = useState<{ secretKey: string; qrCodeUri: string } | null>(null);
  const [verifyCode, setVerifyCode] = useState('');
  const [disableCode, setDisableCode] = useState('');
  const [showDisable, setShowDisable] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');

  useEffect(() => {
    api.get('/settings/2fa/status').then(({ data }) => setStatus(data.isEnabled));
  }, []);

  const handleSetup = async () => {
    setError('');
    setLoading(true);
    try {
      const { data } = await api.post('/settings/2fa/setup');
      setSetupData(data);
    } catch {
      setError('Failed to start 2FA setup');
    } finally {
      setLoading(false);
    }
  };

  const handleVerify = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);
    try {
      await api.post('/settings/2fa/verify', { code: verifyCode });
      setStatus(true);
      setSetupData(null);
      setVerifyCode('');
      setSuccess('2FA enabled successfully');
      setTimeout(() => setSuccess(''), 3000);
    } catch {
      setError('Invalid verification code');
    } finally {
      setLoading(false);
    }
  };

  const handleDisable = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);
    try {
      await api.post('/settings/2fa/disable', { code: disableCode });
      setStatus(false);
      setShowDisable(false);
      setDisableCode('');
      setSuccess('2FA disabled');
      setTimeout(() => setSuccess(''), 3000);
    } catch {
      setError('Invalid verification code');
    } finally {
      setLoading(false);
    }
  };

  const handleCancel = () => {
    setSetupData(null);
    setVerifyCode('');
    setError('');
  };

  if (status === null) return null;

  return (
    <div className="bg-bg-secondary rounded-xl border border-border p-6 max-w-2xl mt-6">
      <div className="flex items-center justify-between mb-4">
        <h3 className="text-sm font-semibold text-text-primary">Two-Factor Authentication</h3>
        {status && (
          <span className="inline-block text-xs font-semibold px-2 py-1 rounded bg-accent-green/15 text-accent-green">
            Enabled
          </span>
        )}
      </div>

      {error && (
        <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg mb-4">
          {error}
        </div>
      )}
      {success && (
        <div className="bg-accent-green/10 border border-accent-green/20 text-accent-green text-sm px-4 py-2.5 rounded-lg mb-4">
          {success}
        </div>
      )}

      {/* State: 2FA disabled, no setup in progress */}
      {!status && !setupData && (
        <div>
          <p className="text-sm text-text-secondary mb-4">
            Add an extra layer of security to your account using Google Authenticator or any TOTP app.
          </p>
          <button
            onClick={handleSetup}
            disabled={loading}
            className="bg-accent-blue hover:bg-accent-blue/90 text-white font-medium py-2 px-4 rounded-lg text-sm transition-colors disabled:opacity-50"
          >
            {loading ? 'Setting up...' : 'Enable 2FA'}
          </button>
        </div>
      )}

      {/* State: Setup in progress — show QR + verify */}
      {!status && setupData && (
        <div>
          <p className="text-sm text-text-secondary mb-4">
            Scan this QR code with your authenticator app, then enter the 6-digit code to verify.
          </p>
          <div className="flex flex-col items-center gap-4 mb-4">
            <div className="bg-white p-4 rounded-xl">
              <QRCodeSVG value={setupData.qrCodeUri} size={180} />
            </div>
            <div className="text-center">
              <p className="text-xs text-text-secondary mb-1">Or enter this key manually:</p>
              <code className="text-sm font-mono text-text-primary bg-bg-tertiary px-3 py-1.5 rounded select-all">
                {setupData.secretKey}
              </code>
            </div>
          </div>
          <form onSubmit={handleVerify} className="space-y-3">
            <div>
              <label className="block text-sm font-medium text-text-primary mb-1.5">Verification Code</label>
              <input
                type="text"
                value={verifyCode}
                onChange={(e) => setVerifyCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
                className={`${inputClass} text-center text-lg tracking-widest max-w-48 mx-auto block`}
                placeholder="000000"
                maxLength={6}
                pattern="[0-9]{6}"
                autoFocus
                required
              />
            </div>
            <div className="flex gap-3 justify-center">
              <button
                type="button"
                onClick={handleCancel}
                className="px-4 py-2 rounded-lg text-sm font-medium text-text-secondary hover:text-text-primary border border-border hover:border-text-secondary transition-all"
              >
                Cancel
              </button>
              <button
                type="submit"
                disabled={loading || verifyCode.length !== 6}
                className="bg-accent-blue hover:bg-accent-blue/90 text-white font-medium py-2 px-4 rounded-lg text-sm transition-colors disabled:opacity-50"
              >
                {loading ? 'Verifying...' : 'Verify & Activate'}
              </button>
            </div>
          </form>
        </div>
      )}

      {/* State: 2FA enabled */}
      {status && !showDisable && (
        <div>
          <p className="text-sm text-text-secondary mb-4">
            Your account is protected with two-factor authentication.
          </p>
          <button
            onClick={() => { setShowDisable(true); setError(''); }}
            className="px-4 py-2 rounded-lg text-sm font-medium text-accent-red border border-accent-red/30 hover:bg-accent-red/10 transition-all"
          >
            Disable 2FA
          </button>
        </div>
      )}

      {/* State: Confirming disable */}
      {status && showDisable && (
        <form onSubmit={handleDisable} className="space-y-3">
          <p className="text-sm text-text-secondary mb-2">
            Enter your authenticator code to confirm disabling 2FA.
          </p>
          <div>
            <input
              type="text"
              value={disableCode}
              onChange={(e) => setDisableCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
              className={`${inputClass} text-center text-lg tracking-widest max-w-48 mx-auto block`}
              placeholder="000000"
              maxLength={6}
              pattern="[0-9]{6}"
              autoFocus
              required
            />
          </div>
          <div className="flex gap-3 justify-center">
            <button
              type="button"
              onClick={() => { setShowDisable(false); setDisableCode(''); setError(''); }}
              className="px-4 py-2 rounded-lg text-sm font-medium text-text-secondary hover:text-text-primary border border-border hover:border-text-secondary transition-all"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={loading || disableCode.length !== 6}
              className="bg-accent-red hover:bg-accent-red/90 text-white font-medium py-2 px-4 rounded-lg text-sm transition-colors disabled:opacity-50"
            >
              {loading ? 'Disabling...' : 'Confirm Disable'}
            </button>
          </div>
        </form>
      )}
    </div>
  );
}

function ChangePasswordSection() {
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setSuccess('');

    if (newPassword.length < 6) {
      setError('New password must be at least 6 characters');
      return;
    }
    if (newPassword !== confirmPassword) {
      setError('Passwords do not match');
      return;
    }

    setLoading(true);
    try {
      await api.post('/settings/change-password', { currentPassword, newPassword, confirmPassword });
      setSuccess('Password changed successfully');
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
      setTimeout(() => setSuccess(''), 3000);
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'response' in err) {
        const resp = err as { response?: { data?: { message?: string } } };
        setError(resp.response?.data?.message || 'Failed to change password');
      } else {
        setError('Failed to change password');
      }
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="bg-bg-secondary rounded-xl border border-border p-6 max-w-2xl mt-6">
      <h3 className="text-sm font-semibold text-text-primary mb-4">Change Password</h3>

      {error && (
        <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg mb-4">
          {error}
        </div>
      )}
      {success && (
        <div className="bg-accent-green/10 border border-accent-green/20 text-accent-green text-sm px-4 py-2.5 rounded-lg mb-4">
          {success}
        </div>
      )}

      <form onSubmit={handleSubmit} className="space-y-4 max-w-sm">
        <div>
          <label className="block text-sm font-medium text-text-primary mb-1.5">Current Password</label>
          <input
            type="password"
            value={currentPassword}
            onChange={(e) => setCurrentPassword(e.target.value)}
            className={inputClass}
            placeholder="Enter current password"
            required
          />
        </div>

        <div>
          <label className="block text-sm font-medium text-text-primary mb-1.5">New Password</label>
          <input
            type="password"
            value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)}
            className={inputClass}
            placeholder="At least 6 characters"
            required
          />
        </div>

        <div>
          <label className="block text-sm font-medium text-text-primary mb-1.5">Confirm New Password</label>
          <input
            type="password"
            value={confirmPassword}
            onChange={(e) => setConfirmPassword(e.target.value)}
            className={inputClass}
            placeholder="Repeat new password"
            required
          />
        </div>

        <button
          type="submit"
          disabled={loading}
          className="bg-accent-blue hover:bg-accent-blue/90 text-white font-medium py-2 px-4 rounded-lg text-sm transition-colors disabled:opacity-50"
        >
          {loading ? 'Changing...' : 'Change Password'}
        </button>
      </form>
    </div>
  );
}

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

      {/* Two-Factor Authentication */}
      <TwoFactorSection />

      {/* Change Password */}
      <ChangePasswordSection />
    </div>
  );
}
