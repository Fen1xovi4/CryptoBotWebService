import { useState, useEffect, useCallback, useRef } from 'react';
import api from '../api/client';
import Header from '../components/Layout/Header';

interface PaymentSessionAdminDto {
  id: string;
  userId: string;
  username: string;
  walletId: string;
  plan: string;
  network: string;
  token: string;
  expectedAmount: number;
  walletAddress: string;
  status: string;
  txHash: string | null;
  receivedAmount: number | null;
  createdAt: string;
  expiresAt: string;
  confirmedAt: string | null;
  confirmedByAdmin: string | null;
  remainingSeconds: number;
}

interface ConfirmForm {
  txHash: string;
  receivedAmount: string;
}

type StatusFilter = 'All' | 'Pending' | 'Confirmed' | 'Expired' | 'Cancelled' | 'ManuallyConfirmed';

const STATUS_FILTERS: StatusFilter[] = ['All', 'Pending', 'Confirmed', 'Expired', 'Cancelled', 'ManuallyConfirmed'];

const statusBadgeClass: Record<string, string> = {
  Pending: 'bg-accent-yellow/10 text-accent-yellow',
  Confirmed: 'bg-accent-green/10 text-accent-green',
  ManuallyConfirmed: 'bg-accent-blue/10 text-accent-blue',
  Expired: 'bg-bg-tertiary text-text-secondary',
  Cancelled: 'bg-accent-red/10 text-accent-red',
};

function truncateHash(hash: string | null): string {
  if (!hash) return '—';
  if (hash.length <= 14) return hash;
  return `${hash.slice(0, 8)}...${hash.slice(-6)}`;
}

function truncateAddress(addr: string): string {
  if (!addr || addr.length <= 14) return addr;
  return `${addr.slice(0, 8)}...${addr.slice(-6)}`;
}

export default function AdminPaymentsPage() {
  const [sessions, setSessions] = useState<PaymentSessionAdminDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('All');

  const [confirmModalOpen, setConfirmModalOpen] = useState(false);
  const [confirmingSession, setConfirmingSession] = useState<PaymentSessionAdminDto | null>(null);
  const [confirmForm, setConfirmForm] = useState<ConfirmForm>({ txHash: '', receivedAmount: '' });
  const [confirming, setConfirming] = useState(false);
  const [confirmError, setConfirmError] = useState<string | null>(null);

  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const fetchSessions = useCallback(async () => {
    setError(null);
    try {
      const res = await api.get<PaymentSessionAdminDto[]>('/paymentsessions');
      setSessions(res.data);
    } catch {
      setError('Failed to load payment sessions.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchSessions();
  }, [fetchSessions]);

  // Auto-refresh every 15 seconds for pending sessions
  useEffect(() => {
    intervalRef.current = setInterval(() => {
      fetchSessions();
    }, 15000);

    return () => {
      if (intervalRef.current) clearInterval(intervalRef.current);
    };
  }, [fetchSessions]);

  const filteredSessions =
    statusFilter === 'All' ? sessions : sessions.filter((s) => s.status === statusFilter);

  const openConfirmModal = (session: PaymentSessionAdminDto) => {
    setConfirmingSession(session);
    setConfirmForm({ txHash: session.txHash ?? '', receivedAmount: session.receivedAmount?.toString() ?? '' });
    setConfirmError(null);
    setConfirmModalOpen(true);
  };

  const closeConfirmModal = () => {
    setConfirmModalOpen(false);
    setConfirmingSession(null);
    setConfirmError(null);
  };

  const handleConfirm = async () => {
    if (!confirmingSession) return;
    setConfirming(true);
    setConfirmError(null);
    try {
      const body: { txHash?: string; receivedAmount?: number } = {};
      if (confirmForm.txHash.trim()) body.txHash = confirmForm.txHash.trim();
      if (confirmForm.receivedAmount.trim()) {
        const parsed = parseFloat(confirmForm.receivedAmount);
        if (!isNaN(parsed)) body.receivedAmount = parsed;
      }
      await api.post(`/paymentsessions/${confirmingSession.id}/confirm`, body);
      closeConfirmModal();
      await fetchSessions();
    } catch {
      setConfirmError('Failed to confirm payment. Please try again.');
    } finally {
      setConfirming(false);
    }
  };

  const hasPending = sessions.some((s) => s.status === 'Pending');

  return (
    <div>
      <Header title="Payment Sessions" subtitle="View and manually confirm user payment sessions">
        {hasPending && (
          <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-accent-yellow/10 text-accent-yellow">
            <span className="w-1.5 h-1.5 rounded-full bg-accent-yellow animate-pulse" />
            {sessions.filter((s) => s.status === 'Pending').length} Pending
          </span>
        )}
      </Header>

      {error && (
        <div className="mb-4 px-4 py-3 rounded-lg bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm">
          {error}
        </div>
      )}

      {/* Filter row */}
      <div className="flex items-center gap-2 mb-4 flex-wrap">
        <span className="text-xs text-text-secondary font-medium">Status:</span>
        {STATUS_FILTERS.map((f) => (
          <button
            key={f}
            onClick={() => setStatusFilter(f)}
            className={`text-xs font-medium px-3 py-1.5 rounded-lg transition-colors ${
              statusFilter === f
                ? 'bg-accent-blue text-white shadow-md shadow-accent-blue/25'
                : 'bg-bg-secondary border border-border text-text-secondary hover:text-text-primary hover:bg-bg-tertiary'
            }`}
          >
            {f}
          </button>
        ))}
      </div>

      <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
        <table className="w-full">
          <thead>
            <tr className="text-xs text-text-secondary border-b border-border">
              <th className="text-left px-5 py-2.5 font-medium">User</th>
              <th className="text-left px-5 py-2.5 font-medium">Plan</th>
              <th className="text-left px-5 py-2.5 font-medium">Amount</th>
              <th className="text-left px-5 py-2.5 font-medium">Network/Token</th>
              <th className="text-left px-5 py-2.5 font-medium">Status</th>
              <th className="text-left px-5 py-2.5 font-medium">Wallet Address</th>
              <th className="text-left px-5 py-2.5 font-medium">TxHash</th>
              <th className="text-left px-5 py-2.5 font-medium">Created</th>
              <th className="text-right px-5 py-2.5 font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            {loading ? (
              <tr>
                <td colSpan={9} className="px-5 py-8 text-center text-text-secondary text-sm">
                  Loading...
                </td>
              </tr>
            ) : filteredSessions.length === 0 ? (
              <tr>
                <td colSpan={9} className="px-5 py-8 text-center text-text-secondary text-sm">
                  {statusFilter === 'All' ? 'No payment sessions found.' : `No ${statusFilter} sessions.`}
                </td>
              </tr>
            ) : (
              filteredSessions.map((session) => (
                <tr
                  key={session.id}
                  className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors"
                >
                  <td className="px-5 py-3">
                    <div className="flex items-center gap-2">
                      <div className="w-7 h-7 rounded-full bg-bg-tertiary flex items-center justify-center text-[10px] font-bold text-text-primary uppercase shrink-0">
                        {session.username[0]}
                      </div>
                      <span className="text-sm font-medium text-text-primary">{session.username}</span>
                    </div>
                  </td>
                  <td className="px-5 py-3">
                    <span className="text-sm text-text-primary font-medium">{session.plan}</span>
                  </td>
                  <td className="px-5 py-3">
                    <span className="text-sm font-medium text-text-primary">
                      {session.expectedAmount} {session.token}
                    </span>
                  </td>
                  <td className="px-5 py-3">
                    <span className="text-xs font-medium px-2 py-1 rounded bg-bg-tertiary text-text-secondary">
                      {session.network}
                    </span>
                    <span className="ml-1 text-xs text-text-secondary">{session.token}</span>
                  </td>
                  <td className="px-5 py-3">
                    <span
                      className={`inline-flex items-center text-xs font-medium px-2.5 py-1 rounded-full ${
                        statusBadgeClass[session.status] ?? 'bg-bg-tertiary text-text-secondary'
                      }`}
                    >
                      {session.status}
                    </span>
                  </td>
                  <td className="px-5 py-3">
                    <span className="font-mono text-xs text-text-secondary" title={session.walletAddress}>
                      {truncateAddress(session.walletAddress)}
                    </span>
                  </td>
                  <td className="px-5 py-3">
                    <span className="font-mono text-xs text-text-secondary" title={session.txHash ?? undefined}>
                      {truncateHash(session.txHash)}
                    </span>
                  </td>
                  <td className="px-5 py-3 text-sm text-text-secondary">
                    {new Date(session.createdAt).toLocaleDateString()}
                  </td>
                  <td className="px-5 py-3 text-right">
                    {session.status === 'Pending' && (
                      <button
                        onClick={() => openConfirmModal(session)}
                        className="px-3 py-1.5 text-xs font-medium rounded-lg bg-accent-green/10 text-accent-green hover:bg-accent-green/20 transition-colors"
                      >
                        Confirm
                      </button>
                    )}
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {/* Confirm Modal */}
      {confirmModalOpen && confirmingSession && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm"
          onClick={(e) => { if (e.target === e.currentTarget) closeConfirmModal(); }}
        >
          <div className="bg-bg-secondary rounded-xl border border-border w-full max-w-md mx-4 shadow-2xl">
            <div className="flex items-center justify-between px-6 py-4 border-b border-border">
              <h3 className="text-base font-semibold text-text-primary">Confirm Payment</h3>
              <button
                onClick={closeConfirmModal}
                className="p-1.5 rounded-lg text-text-secondary hover:text-text-primary hover:bg-bg-tertiary transition-colors"
                aria-label="Close"
              >
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            </div>

            <div className="px-6 py-5 space-y-4">
              {/* Session summary */}
              <div className="rounded-lg bg-bg-tertiary/50 border border-border px-4 py-3 text-sm space-y-1">
                <div className="flex justify-between">
                  <span className="text-text-secondary">User</span>
                  <span className="text-text-primary font-medium">{confirmingSession.username}</span>
                </div>
                <div className="flex justify-between">
                  <span className="text-text-secondary">Plan</span>
                  <span className="text-text-primary font-medium">{confirmingSession.plan}</span>
                </div>
                <div className="flex justify-between">
                  <span className="text-text-secondary">Expected</span>
                  <span className="text-text-primary font-medium">
                    {confirmingSession.expectedAmount} {confirmingSession.token} ({confirmingSession.network})
                  </span>
                </div>
              </div>

              {confirmError && (
                <div className="px-3 py-2.5 rounded-lg bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm">
                  {confirmError}
                </div>
              )}

              <div>
                <label className="block text-xs font-medium text-text-secondary mb-1.5">
                  TxHash <span className="text-text-secondary/50 font-normal">(optional)</span>
                </label>
                <input
                  type="text"
                  value={confirmForm.txHash}
                  onChange={(e) => setConfirmForm((f) => ({ ...f, txHash: e.target.value }))}
                  placeholder="Transaction hash"
                  className="w-full bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-text-primary text-sm font-mono focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue outline-none transition-colors"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-text-secondary mb-1.5">
                  Received Amount <span className="text-text-secondary/50 font-normal">(optional)</span>
                </label>
                <input
                  type="number"
                  min="0"
                  step="any"
                  value={confirmForm.receivedAmount}
                  onChange={(e) => setConfirmForm((f) => ({ ...f, receivedAmount: e.target.value }))}
                  placeholder={`${confirmingSession.expectedAmount}`}
                  className="w-full bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-text-primary text-sm focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue outline-none transition-colors"
                />
              </div>
            </div>

            <div className="flex items-center justify-end gap-3 px-6 py-4 border-t border-border">
              <button
                onClick={closeConfirmModal}
                className="px-4 py-2.5 text-sm font-medium rounded-lg bg-bg-tertiary text-text-secondary hover:text-text-primary hover:bg-border transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={handleConfirm}
                disabled={confirming}
                className="px-4 py-2.5 text-sm font-medium rounded-lg bg-accent-green hover:bg-accent-green/90 text-white transition-colors shadow-lg shadow-accent-green/25 disabled:opacity-60"
              >
                {confirming ? 'Confirming...' : 'Confirm Payment'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
