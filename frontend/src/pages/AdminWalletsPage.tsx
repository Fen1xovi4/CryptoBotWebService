import { useState, useEffect, useCallback } from 'react';
import api from '../api/client';
import Header from '../components/Layout/Header';

interface PaymentWalletDto {
  id: string;
  addressTrc20: string;
  addressBep20: string;
  isActive: boolean;
  createdAt: string;
  isLocked: boolean;
}

interface WalletFormState {
  addressTrc20: string;
  addressBep20: string;
}

function truncateAddress(addr: string): string {
  if (!addr || addr.length <= 14) return addr;
  return `${addr.slice(0, 8)}...${addr.slice(-6)}`;
}

function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);

  const handleCopy = () => {
    navigator.clipboard.writeText(text).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  };

  return (
    <button
      onClick={handleCopy}
      aria-label="Copy address"
      className="ml-1.5 p-1 rounded text-text-secondary hover:text-text-primary hover:bg-bg-tertiary transition-colors"
    >
      {copied ? (
        <svg className="w-3.5 h-3.5 text-accent-green" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
        </svg>
      ) : (
        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.8}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
        </svg>
      )}
    </button>
  );
}

export default function AdminWalletsPage() {
  const [wallets, setWallets] = useState<PaymentWalletDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [modalOpen, setModalOpen] = useState(false);
  const [editingWallet, setEditingWallet] = useState<PaymentWalletDto | null>(null);
  const [form, setForm] = useState<WalletFormState>({ addressTrc20: '', addressBep20: '' });
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const [togglingId, setTogglingId] = useState<string | null>(null);

  const fetchWallets = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await api.get<PaymentWalletDto[]>('/paymentwallets');
      setWallets(res.data);
    } catch {
      setError('Failed to load wallets.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchWallets();
  }, [fetchWallets]);

  const openAddModal = () => {
    setEditingWallet(null);
    setForm({ addressTrc20: '', addressBep20: '' });
    setSaveError(null);
    setModalOpen(true);
  };

  const openEditModal = (wallet: PaymentWalletDto) => {
    setEditingWallet(wallet);
    setForm({ addressTrc20: wallet.addressTrc20, addressBep20: wallet.addressBep20 });
    setSaveError(null);
    setModalOpen(true);
  };

  const closeModal = () => {
    setModalOpen(false);
    setEditingWallet(null);
    setSaveError(null);
  };

  const handleSave = async () => {
    if (!form.addressTrc20.trim() || !form.addressBep20.trim()) {
      setSaveError('Both addresses are required.');
      return;
    }
    setSaving(true);
    setSaveError(null);
    try {
      if (editingWallet) {
        await api.put(`/paymentwallets/${editingWallet.id}`, form);
      } else {
        await api.post('/paymentwallets', form);
      }
      closeModal();
      await fetchWallets();
    } catch {
      setSaveError('Failed to save wallet. Please try again.');
    } finally {
      setSaving(false);
    }
  };

  const handleToggleActive = async (wallet: PaymentWalletDto) => {
    if (wallet.isLocked) return;
    setTogglingId(wallet.id);
    try {
      if (wallet.isActive) {
        await api.delete(`/paymentwallets/${wallet.id}`);
      } else {
        await api.put(`/paymentwallets/${wallet.id}`, {
          addressTrc20: wallet.addressTrc20,
          addressBep20: wallet.addressBep20,
        });
      }
      await fetchWallets();
    } catch {
      // silently ignore toggle errors — list will still show current state
    } finally {
      setTogglingId(null);
    }
  };

  return (
    <div>
      <Header title="Payment Wallets" subtitle="Manage crypto addresses used to receive payments">
        <button
          onClick={openAddModal}
          className="bg-accent-blue hover:bg-accent-blue/90 text-white font-medium py-2.5 px-4 rounded-lg text-sm transition-colors shadow-lg shadow-accent-blue/25"
        >
          Add Wallet
        </button>
      </Header>

      {error && (
        <div className="mb-4 px-4 py-3 rounded-lg bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm">
          {error}
        </div>
      )}

      <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
        <div className="overflow-x-auto">
        <table className="w-full">
          <thead>
            <tr className="text-xs text-text-secondary border-b border-border">
              <th className="text-left px-5 py-2.5 font-medium">#</th>
              <th className="text-left px-5 py-2.5 font-medium">TRC20 Address</th>
              <th className="text-left px-5 py-2.5 font-medium">BEP20 Address</th>
              <th className="text-left px-5 py-2.5 font-medium">Active</th>
              <th className="text-left px-5 py-2.5 font-medium">Locked</th>
              <th className="text-left px-5 py-2.5 font-medium">Created</th>
              <th className="text-right px-5 py-2.5 font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            {loading ? (
              <tr>
                <td colSpan={7} className="px-5 py-8 text-center text-text-secondary text-sm">
                  Loading...
                </td>
              </tr>
            ) : wallets.length === 0 ? (
              <tr>
                <td colSpan={7} className="px-5 py-8 text-center text-text-secondary text-sm">
                  No wallets found. Add one to get started.
                </td>
              </tr>
            ) : (
              wallets.map((wallet, idx) => (
                <tr
                  key={wallet.id}
                  className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors"
                >
                  <td className="px-5 py-3 text-sm text-text-secondary">{idx + 1}</td>
                  <td className="px-5 py-3">
                    <div className="flex items-center">
                      <span className="font-mono text-xs text-text-primary">
                        {truncateAddress(wallet.addressTrc20)}
                      </span>
                      <CopyButton text={wallet.addressTrc20} />
                    </div>
                  </td>
                  <td className="px-5 py-3">
                    <div className="flex items-center">
                      <span className="font-mono text-xs text-text-primary">
                        {truncateAddress(wallet.addressBep20)}
                      </span>
                      <CopyButton text={wallet.addressBep20} />
                    </div>
                  </td>
                  <td className="px-5 py-3">
                    {wallet.isActive ? (
                      <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-accent-green/10 text-accent-green">
                        <span className="w-1.5 h-1.5 rounded-full bg-accent-green" />
                        Active
                      </span>
                    ) : (
                      <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-accent-red/10 text-accent-red">
                        <span className="w-1.5 h-1.5 rounded-full bg-accent-red" />
                        Inactive
                      </span>
                    )}
                  </td>
                  <td className="px-5 py-3">
                    {wallet.isLocked ? (
                      <span className="inline-flex items-center text-xs font-medium px-2.5 py-1 rounded-full bg-accent-yellow/10 text-accent-yellow">
                        Locked
                      </span>
                    ) : (
                      <span className="inline-flex items-center text-xs font-medium px-2.5 py-1 rounded-full bg-bg-tertiary text-text-secondary">
                        Free
                      </span>
                    )}
                  </td>
                  <td className="px-5 py-3 text-sm text-text-secondary">
                    {new Date(wallet.createdAt).toLocaleDateString()}
                  </td>
                  <td className="px-5 py-3 text-right">
                    <div className="flex items-center justify-end gap-2">
                      <button
                        onClick={() => openEditModal(wallet)}
                        className="px-3 py-1.5 text-xs font-medium rounded-lg bg-bg-tertiary text-text-secondary hover:text-text-primary hover:bg-border transition-colors"
                      >
                        Edit
                      </button>
                      <button
                        onClick={() => handleToggleActive(wallet)}
                        disabled={wallet.isLocked || togglingId === wallet.id}
                        title={wallet.isLocked ? 'Wallet is locked (in use)' : undefined}
                        className={`px-3 py-1.5 text-xs font-medium rounded-lg transition-colors disabled:opacity-40 disabled:cursor-not-allowed ${
                          wallet.isActive
                            ? 'bg-accent-red/10 text-accent-red hover:bg-accent-red/20'
                            : 'bg-accent-green/10 text-accent-green hover:bg-accent-green/20'
                        }`}
                      >
                        {togglingId === wallet.id
                          ? '...'
                          : wallet.isActive
                          ? 'Deactivate'
                          : 'Activate'}
                      </button>
                    </div>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
        </div>
      </div>

      {/* Add / Edit Modal */}
      {modalOpen && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm"
          onClick={(e) => { if (e.target === e.currentTarget) closeModal(); }}
        >
          <div className="bg-bg-secondary rounded-xl border border-border w-full max-w-md mx-4 shadow-2xl">
            <div className="flex items-center justify-between px-6 py-4 border-b border-border">
              <h3 className="text-base font-semibold text-text-primary">
                {editingWallet ? 'Edit Wallet' : 'Add Wallet'}
              </h3>
              <button
                onClick={closeModal}
                className="p-1.5 rounded-lg text-text-secondary hover:text-text-primary hover:bg-bg-tertiary transition-colors"
                aria-label="Close"
              >
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            </div>

            <div className="px-6 py-5 space-y-4">
              {saveError && (
                <div className="px-3 py-2.5 rounded-lg bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm">
                  {saveError}
                </div>
              )}

              <div>
                <label className="block text-xs font-medium text-text-secondary mb-1.5">
                  TRC20 Address
                </label>
                <input
                  type="text"
                  value={form.addressTrc20}
                  onChange={(e) => setForm((f) => ({ ...f, addressTrc20: e.target.value }))}
                  placeholder="TRON TRC20 wallet address"
                  className="w-full bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-text-primary text-sm font-mono focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue outline-none transition-colors"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-text-secondary mb-1.5">
                  BEP20 Address
                </label>
                <input
                  type="text"
                  value={form.addressBep20}
                  onChange={(e) => setForm((f) => ({ ...f, addressBep20: e.target.value }))}
                  placeholder="BSC BEP20 wallet address"
                  className="w-full bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-text-primary text-sm font-mono focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue outline-none transition-colors"
                />
              </div>
            </div>

            <div className="flex items-center justify-end gap-3 px-6 py-4 border-t border-border">
              <button
                onClick={closeModal}
                className="px-4 py-2.5 text-sm font-medium rounded-lg bg-bg-tertiary text-text-secondary hover:text-text-primary hover:bg-border transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={handleSave}
                disabled={saving}
                className="px-4 py-2.5 text-sm font-medium rounded-lg bg-accent-blue hover:bg-accent-blue/90 text-white transition-colors shadow-lg shadow-accent-blue/25 disabled:opacity-60"
              >
                {saving ? 'Saving...' : 'Save'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
