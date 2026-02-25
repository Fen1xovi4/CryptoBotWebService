import { useState, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, Link } from 'react-router-dom';
import api from '../api/client';
import Header from '../components/Layout/Header';
import { useAuthStore } from '../stores/authStore';

interface ExchangeAccount {
  id: string;
  name: string;
  exchangeType: number;
  proxyId: string | null;
  proxyName: string | null;
  isActive: boolean;
  createdAt: string;
}

interface ProxyOption {
  id: string;
  name: string;
  host: string;
  port: number;
  isActive: boolean;
}

const exchangeNames: Record<number, string> = { 1: 'Bybit', 2: 'Bitget', 3: 'BingX' };

interface ConnectionStatus {
  success: boolean;
  message: string;
}

export default function AccountsPage() {
  const [showModal, setShowModal] = useState(false);
  const [editAccount, setEditAccount] = useState<ExchangeAccount | null>(null);
  const [connectionStatus, setConnectionStatus] = useState<Record<string, ConnectionStatus>>({});
  const [checking, setChecking] = useState<Record<string, boolean>>({});
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  const { data: accounts, isLoading } = useQuery<ExchangeAccount[]>({
    queryKey: ['accounts'],
    queryFn: () => api.get('/accounts').then((r) => r.data),
  });

  // Auto-test active accounts on load
  useEffect(() => {
    if (!accounts) return;
    const activeAccounts = accounts.filter((a) => a.isActive);
    if (activeAccounts.length === 0) return;

    activeAccounts.forEach(async (acc) => {
      if (connectionStatus[acc.id] !== undefined) return;
      setChecking((prev) => ({ ...prev, [acc.id]: true }));
      try {
        const { data } = await api.post(`/accounts/${acc.id}/test`);
        setConnectionStatus((prev) => ({ ...prev, [acc.id]: data }));
      } catch {
        setConnectionStatus((prev) => ({
          ...prev,
          [acc.id]: { success: false, message: 'Request failed' },
        }));
      } finally {
        setChecking((prev) => ({ ...prev, [acc.id]: false }));
      }
    });
  }, [accounts]);

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/accounts/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['accounts'] }),
  });

  const toggleActiveMutation = useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) =>
      api.put(`/accounts/${id}`, { isActive }),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['accounts'] });
      if (!variables.isActive) {
        setConnectionStatus((prev) => {
          const next = { ...prev };
          delete next[variables.id];
          return next;
        });
      }
    },
  });

  return (
    <div>
      <Header title="Exchange Accounts" subtitle="Manage your exchange API connections">
        <button
          onClick={() => setShowModal(true)}
          className="inline-flex items-center gap-2 bg-accent-blue hover:bg-accent-blue/90 text-white text-sm font-medium px-4 py-2 rounded-lg transition-colors shadow-md shadow-accent-blue/20"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M12 4.5v15m7.5-7.5h-15" />
          </svg>
          Add Account
        </button>
      </Header>

      <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
        <table className="w-full">
          <thead>
            <tr className="text-xs text-text-secondary border-b border-border">
              <th className="text-left px-5 py-2.5 font-medium">Name</th>
              <th className="text-left px-5 py-2.5 font-medium">Exchange</th>
              <th className="text-left px-5 py-2.5 font-medium">Proxy</th>
              <th className="text-left px-5 py-2.5 font-medium">Status</th>
              <th className="text-left px-5 py-2.5 font-medium">Created</th>
              <th className="text-right px-5 py-2.5 font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            {isLoading ? (
              <tr><td colSpan={6} className="px-5 py-8 text-center text-text-secondary text-sm">Loading...</td></tr>
            ) : accounts?.length === 0 ? (
              <tr><td colSpan={6} className="px-5 py-8 text-center text-text-secondary text-sm">No accounts yet. Add one to get started.</td></tr>
            ) : (
              accounts?.map((acc) => {
                const status = connectionStatus[acc.id];
                return (
                  <tr key={acc.id} className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors">
                    <td
                      className="px-5 py-3 text-sm font-medium text-accent-blue cursor-pointer hover:underline"
                      onClick={() => navigate(`/accounts/${acc.id}`)}
                    >
                      {acc.name}
                    </td>
                    <td className="px-5 py-3 text-sm text-text-secondary">{exchangeNames[acc.exchangeType]}</td>
                    <td className="px-5 py-3 text-sm text-text-secondary">
                      {acc.proxyName ? (
                        <span className="inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded-full bg-accent-blue/10 text-accent-blue">
                          <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M12 21a9.004 9.004 0 008.716-6.747M12 21a9.004 9.004 0 01-8.716-6.747M12 21c2.485 0 4.5-4.03 4.5-9S14.485 3 12 3m0 18c-2.485 0-4.5-4.03-4.5-9S9.515 3 12 3m0 0a8.997 8.997 0 017.843 4.582M12 3a8.997 8.997 0 00-7.843 4.582m15.686 0A11.953 11.953 0 0112 10.5c-2.998 0-5.74-1.1-7.843-2.918m15.686 0A8.959 8.959 0 0121 12c0 .778-.099 1.533-.284 2.253m0 0A17.919 17.919 0 0112 16.5c-3.162 0-6.133-.815-8.716-2.247m0 0A9.015 9.015 0 013 12c0-1.605.42-3.113 1.157-4.418" />
                          </svg>
                          {acc.proxyName}
                        </span>
                      ) : (
                        <span className="text-text-secondary/50">—</span>
                      )}
                    </td>
                    <td className="px-5 py-3">
                      {checking[acc.id] ? (
                        <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-accent-blue/10 text-accent-blue">
                          <svg className="w-3 h-3 animate-spin" fill="none" viewBox="0 0 24 24">
                            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                          </svg>
                          Checking...
                        </span>
                      ) : status?.success ? (
                        <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-accent-green/10 text-accent-green">
                          <span className="w-1.5 h-1.5 rounded-full bg-accent-green animate-pulse" />
                          Connected
                        </span>
                      ) : status && !status.success ? (
                        <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-accent-red/10 text-accent-red">
                          <span className="w-1.5 h-1.5 rounded-full bg-accent-red" />
                          Disconnected
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-bg-tertiary text-text-secondary">
                          <span className="w-1.5 h-1.5 rounded-full bg-text-secondary" />
                          Disconnected
                        </span>
                      )}
                    </td>
                    <td className="px-5 py-3 text-sm text-text-secondary">
                      {new Date(acc.createdAt).toLocaleDateString()}
                    </td>
                    <td className="px-5 py-3 text-right">
                      <div className="inline-flex items-center gap-2">
                        {status?.success ? (
                          <button
                            onClick={() => toggleActiveMutation.mutate({ id: acc.id, isActive: false })}
                            disabled={toggleActiveMutation.isPending}
                            className="px-3 py-1.5 text-xs font-medium bg-accent-yellow/10 text-accent-yellow rounded-lg hover:bg-accent-yellow/20 transition-colors"
                          >
                            {toggleActiveMutation.isPending ? 'Disconnecting...' : 'Disconnect'}
                          </button>
                        ) : (
                          <button
                            onClick={async () => {
                              if (!acc.isActive) {
                                toggleActiveMutation.mutate({ id: acc.id, isActive: true });
                              }
                              setChecking((prev) => ({ ...prev, [acc.id]: true }));
                              try {
                                const { data } = await api.post(`/accounts/${acc.id}/test`);
                                setConnectionStatus((prev) => ({ ...prev, [acc.id]: data }));
                              } catch {
                                setConnectionStatus((prev) => ({
                                  ...prev,
                                  [acc.id]: { success: false, message: 'Request failed' },
                                }));
                              } finally {
                                setChecking((prev) => ({ ...prev, [acc.id]: false }));
                              }
                            }}
                            disabled={checking[acc.id] || toggleActiveMutation.isPending}
                            className="px-3 py-1.5 text-xs font-medium bg-accent-green/10 text-accent-green rounded-lg hover:bg-accent-green/20 transition-colors"
                          >
                            {checking[acc.id] ? 'Connecting...' : 'Connect'}
                          </button>
                        )}
                        <button
                          onClick={() => setEditAccount(acc)}
                          className="px-3 py-1.5 text-xs font-medium bg-bg-tertiary text-text-secondary rounded-lg hover:bg-border transition-colors"
                        >
                          Edit
                        </button>
                        <button
                          onClick={() => {
                            if (confirm('Delete this account?')) deleteMutation.mutate(acc.id);
                          }}
                          className="px-3 py-1.5 text-xs font-medium bg-accent-red/10 text-accent-red rounded-lg hover:bg-accent-red/20 transition-colors"
                        >
                          Delete
                        </button>
                      </div>
                      {status && !status.success && (
                        <p className="text-xs text-accent-red mt-1.5 text-right">{status.message}</p>
                      )}
                    </td>
                  </tr>
                );
              }))
            }
          </tbody>
        </table>
      </div>

      {showModal && <AddAccountModal onClose={() => setShowModal(false)} />}
      {editAccount && <EditAccountModal account={editAccount} onClose={() => setEditAccount(null)} />}
    </div>
  );
}

function AddAccountModal({ onClose }: { onClose: () => void }) {
  const [form, setForm] = useState({
    name: '',
    exchangeType: 1,
    apiKey: '',
    apiSecret: '',
    passphrase: '',
    proxyId: '',
  });
  const [error, setError] = useState('');
  const queryClient = useQueryClient();
  const isAdmin = useAuthStore((s) => s.role === 'Admin');

  const { data: proxies } = useQuery<ProxyOption[]>({
    queryKey: ['proxies'],
    queryFn: () => api.get('/proxies').then((r) => r.data),
  });

  const mutation = useMutation({
    mutationFn: () =>
      api.post('/accounts', {
        ...form,
        passphrase: form.exchangeType === 2 ? form.passphrase : undefined,
        proxyId: form.proxyId || undefined,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['accounts'] });
      onClose();
    },
    onError: (err: any) => {
      const msg = err?.response?.data?.message;
      setError(msg || 'Failed to create account');
    },
  });

  const activeProxies = proxies?.filter((p) => p.isActive) ?? [];
  const hasProxies = activeProxies.length > 0;
  const proxyRequired = !isAdmin && !form.proxyId;

  const inputClass = 'w-full bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all';

  return (
    <div className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50" onClick={onClose}>
      <div className="bg-bg-secondary rounded-xl border border-border p-6 w-full max-w-md shadow-2xl" onClick={(e) => e.stopPropagation()}>
        <h3 className="text-lg font-semibold mb-1">Add Exchange Account</h3>
        <p className="text-sm text-text-secondary mb-5">Connect your exchange API keys</p>

        {error && (
          <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg mb-4">
            {error}
          </div>
        )}

        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Proxy</label>
            {!hasProxies && !isAdmin ? (
              <div className="bg-accent-yellow/10 border border-accent-yellow/20 text-accent-yellow text-sm px-4 py-2.5 rounded-lg">
                No proxies available. <Link to="/proxies" className="underline font-medium" onClick={onClose}>Add a proxy first</Link>
              </div>
            ) : (
              <select
                value={form.proxyId}
                onChange={(e) => setForm({ ...form, proxyId: e.target.value })}
                className={inputClass}
              >
                {isAdmin && <option value="">No proxy (admin)</option>}
                {!isAdmin && <option value="">Select proxy...</option>}
                {activeProxies.map((p) => (
                  <option key={p.id} value={p.id}>{p.name} ({p.host}:{p.port})</option>
                ))}
              </select>
            )}
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Display Name</label>
            <input type="text" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} className={inputClass} placeholder="My Bybit Account" />
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Exchange</label>
            <select value={form.exchangeType} onChange={(e) => setForm({ ...form, exchangeType: Number(e.target.value) })} className={inputClass}>
              <option value={1}>Bybit</option>
              <option value={2}>Bitget</option>
              <option value={3}>BingX</option>
            </select>
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">API Key</label>
            <input type="text" value={form.apiKey} onChange={(e) => setForm({ ...form, apiKey: e.target.value })} className={`${inputClass} font-mono`} placeholder="Enter API key" />
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">API Secret</label>
            <input type="password" value={form.apiSecret} onChange={(e) => setForm({ ...form, apiSecret: e.target.value })} className={`${inputClass} font-mono`} placeholder="Enter API secret" />
          </div>

          {form.exchangeType === 2 && (
            <div>
              <label className="block text-sm font-medium text-text-primary mb-1.5">Passphrase (Bitget)</label>
              <input type="password" value={form.passphrase} onChange={(e) => setForm({ ...form, passphrase: e.target.value })} className={`${inputClass} font-mono`} placeholder="Enter passphrase" />
            </div>
          )}
        </div>

        <div className="flex justify-end gap-3 mt-6">
          <button onClick={onClose} className="px-4 py-2 text-sm font-medium text-text-secondary bg-bg-tertiary rounded-lg hover:bg-border transition-colors">
            Cancel
          </button>
          <button
            onClick={() => mutation.mutate()}
            disabled={mutation.isPending || !form.name || !form.apiKey || !form.apiSecret || proxyRequired}
            className="px-4 py-2 text-sm font-medium bg-accent-blue hover:bg-accent-blue/90 text-white rounded-lg transition-colors shadow-md shadow-accent-blue/20 disabled:opacity-50 disabled:shadow-none"
          >
            {mutation.isPending ? 'Adding...' : 'Add Account'}
          </button>
        </div>
      </div>
    </div>
  );
}

function EditAccountModal({ account, onClose }: { account: ExchangeAccount; onClose: () => void }) {
  const [form, setForm] = useState({
    name: account.name,
    apiKey: '',
    apiSecret: '',
    passphrase: '',
    proxyId: account.proxyId || '',
  });
  const [error, setError] = useState('');
  const queryClient = useQueryClient();
  const isAdmin = useAuthStore((s) => s.role === 'Admin');

  const { data: proxies } = useQuery<ProxyOption[]>({
    queryKey: ['proxies'],
    queryFn: () => api.get('/proxies').then((r) => r.data),
  });

  const mutation = useMutation({
    mutationFn: () => {
      const payload: Record<string, string | null | undefined> = { name: form.name };
      if (form.apiKey) payload.apiKey = form.apiKey;
      if (form.apiSecret) payload.apiSecret = form.apiSecret;
      if (account.exchangeType === 2 && form.passphrase) payload.passphrase = form.passphrase;
      if (form.proxyId) {
        payload.proxyId = form.proxyId;
      } else if (isAdmin) {
        payload.proxyId = '00000000-0000-0000-0000-000000000000';
      }
      return api.put(`/accounts/${account.id}`, payload);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['accounts'] });
      onClose();
    },
    onError: () => setError('Failed to update account'),
  });

  const activeProxies = proxies?.filter((p) => p.isActive) ?? [];

  const inputClass = 'w-full bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all';

  return (
    <div className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50" onClick={onClose}>
      <div className="bg-bg-secondary rounded-xl border border-border p-6 w-full max-w-md shadow-2xl" onClick={(e) => e.stopPropagation()}>
        <h3 className="text-lg font-semibold mb-1">Edit Account</h3>
        <p className="text-sm text-text-secondary mb-5">{exchangeNames[account.exchangeType]} — update name, keys, or proxy</p>

        {error && (
          <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg mb-4">
            {error}
          </div>
        )}

        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Proxy</label>
            <select
              value={form.proxyId}
              onChange={(e) => setForm({ ...form, proxyId: e.target.value })}
              className={inputClass}
            >
              {isAdmin && <option value="">No proxy (admin)</option>}
              {!isAdmin && !form.proxyId && <option value="">Select proxy...</option>}
              {activeProxies.map((p) => (
                <option key={p.id} value={p.id}>{p.name} ({p.host}:{p.port})</option>
              ))}
            </select>
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Display Name</label>
            <input type="text" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} className={inputClass} />
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">API Key</label>
            <input type="text" value={form.apiKey} onChange={(e) => setForm({ ...form, apiKey: e.target.value })} className={`${inputClass} font-mono`} placeholder="Leave empty to keep current" />
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">API Secret</label>
            <input type="password" value={form.apiSecret} onChange={(e) => setForm({ ...form, apiSecret: e.target.value })} className={`${inputClass} font-mono`} placeholder="Leave empty to keep current" />
          </div>

          {account.exchangeType === 2 && (
            <div>
              <label className="block text-sm font-medium text-text-primary mb-1.5">Passphrase (Bitget)</label>
              <input type="password" value={form.passphrase} onChange={(e) => setForm({ ...form, passphrase: e.target.value })} className={`${inputClass} font-mono`} placeholder="Leave empty to keep current" />
            </div>
          )}
        </div>

        <div className="flex justify-end gap-3 mt-6">
          <button onClick={onClose} className="px-4 py-2 text-sm font-medium text-text-secondary bg-bg-tertiary rounded-lg hover:bg-border transition-colors">
            Cancel
          </button>
          <button
            onClick={() => mutation.mutate()}
            disabled={mutation.isPending || !form.name}
            className="px-4 py-2 text-sm font-medium bg-accent-blue hover:bg-accent-blue/90 text-white rounded-lg transition-colors shadow-md shadow-accent-blue/20 disabled:opacity-50 disabled:shadow-none"
          >
            {mutation.isPending ? 'Saving...' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}
