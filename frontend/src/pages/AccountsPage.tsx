import { useState, useEffect, Fragment } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, Link } from 'react-router-dom';
import api from '../api/client';
import Header from '../components/Layout/Header';
import { useAuthStore } from '../stores/authStore';
import { useSubscriptionStore } from '../stores/subscriptionStore';

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

const exchangeNames: Record<number, string> = { 1: 'Bybit', 2: 'Bitget', 3: 'BingX', 4: 'Dzengi' };

interface ConnectionStatus {
  success: boolean;
  message: string;
}

interface Balance {
  asset: string;
  free: number;
  locked: number;
  total: number;
}

interface BalanceResponse {
  accountId: string;
  accountName: string;
  exchange: string;
  balances: Balance[];
}

interface Position {
  symbol: string;
  side: string;
  quantity: number;
  entryPrice: number;
  unrealizedPnl: number;
}

function formatAmount(n: number): string {
  if (n === 0) return '0';
  if (n >= 1) return n.toLocaleString(undefined, { maximumFractionDigits: 2 });
  return n.toLocaleString(undefined, { maximumFractionDigits: 8 });
}

export default function AccountsPage() {
  const [showModal, setShowModal] = useState(false);
  const [editAccount, setEditAccount] = useState<ExchangeAccount | null>(null);
  const [connectionStatus, setConnectionStatus] = useState<Record<string, ConnectionStatus>>({});
  const [checking, setChecking] = useState<Record<string, boolean>>({});
  const [balances, setBalances] = useState<Record<string, Balance[] | null>>({});
  const [loadingBalance, setLoadingBalance] = useState<Record<string, boolean>>({});
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [positions, setPositions] = useState<Record<string, Position[] | null>>({});
  const [loadingPositions, setLoadingPositions] = useState<Record<string, boolean>>({});
  const [closingPosition, setClosingPosition] = useState<Record<string, boolean>>({});
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { maxAccounts, currentAccounts } = useSubscriptionStore();
  const role = useAuthStore((s) => s.role);
  const isAdmin = role === 'Admin';
  const atLimit = !isAdmin && currentAccounts >= maxAccounts && maxAccounts > 0;

  const { data: accounts, isLoading } = useQuery<ExchangeAccount[]>({
    queryKey: ['accounts'],
    queryFn: () => api.get('/accounts').then((r) => r.data),
  });

  const fetchPositions = async (accId: string) => {
    setLoadingPositions((prev) => ({ ...prev, [accId]: true }));
    try {
      const { data } = await api.get<Position[]>(`/accounts/${accId}/positions`);
      setPositions((prev) => ({ ...prev, [accId]: data ?? [] }));
    } catch {
      setPositions((prev) => ({ ...prev, [accId]: null }));
    } finally {
      setLoadingPositions((prev) => ({ ...prev, [accId]: false }));
    }
  };

  const closePosition = async (accId: string, symbol: string, side: string) => {
    const key = `${accId}|${symbol}|${side}`;
    setClosingPosition((prev) => ({ ...prev, [key]: true }));
    try {
      await api.post(`/accounts/${accId}/positions/close`, { symbol, side });
      await fetchPositions(accId);
      fetchBalance(accId);
    } catch (err: any) {
      const msg = err?.response?.data?.message || 'Failed to close position';
      alert(msg);
    } finally {
      setClosingPosition((prev) => ({ ...prev, [key]: false }));
    }
  };

  const toggleExpanded = (accId: string) => {
    setExpanded((prev) => {
      const next = { ...prev, [accId]: !prev[accId] };
      if (next[accId] && positions[accId] === undefined) fetchPositions(accId);
      return next;
    });
  };

  const fetchBalance = async (accId: string) => {
    setLoadingBalance((prev) => ({ ...prev, [accId]: true }));
    try {
      const { data } = await api.get<BalanceResponse>(`/accounts/${accId}/balance`);
      setBalances((prev) => ({ ...prev, [accId]: data.balances ?? [] }));
    } catch {
      setBalances((prev) => ({ ...prev, [accId]: null }));
    } finally {
      setLoadingBalance((prev) => ({ ...prev, [accId]: false }));
    }
  };

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
        if (data.success) fetchBalance(acc.id);
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
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['accounts'] });
      useSubscriptionStore.getState().fetchSubscription();
    },
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
        setBalances((prev) => {
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
        <div className="flex items-center gap-3">
          {!isAdmin && maxAccounts > 0 && (
            <span className={`text-xs font-medium px-2.5 py-1 rounded-full ${
              atLimit
                ? 'bg-accent-red/10 text-accent-red'
                : 'bg-bg-tertiary text-text-secondary'
            }`}>
              Accounts: {currentAccounts}/{maxAccounts}
            </span>
          )}
          <div className="relative group">
            <button
              onClick={() => !atLimit && setShowModal(true)}
              disabled={atLimit}
              className="inline-flex items-center gap-2 bg-accent-blue hover:bg-accent-blue/90 text-white text-sm font-medium px-4 py-2 rounded-lg transition-colors shadow-md shadow-accent-blue/20 disabled:opacity-50 disabled:cursor-not-allowed disabled:shadow-none"
            >
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 4.5v15m7.5-7.5h-15" />
              </svg>
              Add Account
            </button>
            {atLimit && (
              <div className="absolute right-0 top-full mt-2 w-56 bg-bg-secondary border border-border rounded-lg px-3 py-2 text-xs text-text-secondary shadow-lg z-10 hidden group-hover:block">
                Upgrade your plan to add more accounts
              </div>
            )}
          </div>
        </div>
      </Header>

      <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
        <div className="overflow-x-auto">
        <table className="w-full">
          <thead>
            <tr className="text-xs text-text-secondary border-b border-border">
              <th className="w-8 px-2 py-2.5"></th>
              <th className="text-left px-5 py-2.5 font-medium">Name</th>
              <th className="text-left px-5 py-2.5 font-medium">Exchange</th>
              <th className="text-left px-5 py-2.5 font-medium">Proxy</th>
              <th className="text-left px-5 py-2.5 font-medium">Status</th>
              <th className="text-left px-5 py-2.5 font-medium">Balance</th>
              <th className="text-left px-5 py-2.5 font-medium">Created</th>
              <th className="text-right px-5 py-2.5 font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            {isLoading ? (
              <tr><td colSpan={8} className="px-5 py-8 text-center text-text-secondary text-sm">Loading...</td></tr>
            ) : accounts?.length === 0 ? (
              <tr><td colSpan={8} className="px-5 py-8 text-center text-text-secondary text-sm">No accounts yet. Add one to get started.</td></tr>
            ) : (
              accounts?.map((acc) => {
                const status = connectionStatus[acc.id];
                const isOpen = !!expanded[acc.id];
                return (
                <Fragment key={acc.id}>
                  <tr className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors">
                    <td className="px-2 py-3 align-middle">
                      <button
                        onClick={() => toggleExpanded(acc.id)}
                        className="w-6 h-6 flex items-center justify-center rounded hover:bg-bg-tertiary text-text-secondary"
                        aria-label={isOpen ? 'Collapse' : 'Expand'}
                      >
                        <svg className={`w-3.5 h-3.5 transition-transform ${isOpen ? 'rotate-90' : ''}`} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                        </svg>
                      </button>
                    </td>
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
                    <td className="px-5 py-3 text-sm">
                      {loadingBalance[acc.id] ? (
                        <span className="text-text-secondary/60 text-xs">Loading...</span>
                      ) : balances[acc.id] === undefined ? (
                        <span className="text-text-secondary/40 text-xs">—</span>
                      ) : balances[acc.id] === null ? (
                        <span className="text-accent-red/70 text-xs">Failed</span>
                      ) : balances[acc.id]!.length === 0 ? (
                        <span className="text-text-secondary/60 text-xs">Empty</span>
                      ) : (() => {
                        const list = balances[acc.id]!;
                        const usdt = list.find((b) => b.asset === 'USDT');
                        const primary = usdt ?? [...list].sort((a, b) => b.total - a.total)[0];
                        const others = list.filter((b) => b.asset !== primary.asset);
                        return (
                          <div className="group relative inline-block">
                            <span className="font-mono text-text-primary">
                              {formatAmount(primary.total)}{' '}
                              <span className="text-text-secondary text-xs">{primary.asset}</span>
                            </span>
                            {others.length > 0 && (
                              <span className="ml-1.5 text-xs text-text-secondary/70">+{others.length}</span>
                            )}
                            {list.length > 1 && (
                              <div className="absolute left-0 top-full mt-1 w-56 bg-bg-primary border border-border rounded-lg p-2.5 shadow-lg z-20 hidden group-hover:block">
                                <div className="space-y-1">
                                  {list.slice(0, 12).map((b) => (
                                    <div key={b.asset} className="flex justify-between text-xs">
                                      <span className="text-text-secondary">{b.asset}</span>
                                      <span className="font-mono text-text-primary">{formatAmount(b.total)}</span>
                                    </div>
                                  ))}
                                  {list.length > 12 && (
                                    <div className="text-xs text-text-secondary/60 pt-1">+ {list.length - 12} more</div>
                                  )}
                                </div>
                              </div>
                            )}
                          </div>
                        );
                      })()}
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
                                if (data.success) fetchBalance(acc.id);
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
                  {isOpen && (
                    <tr className="bg-bg-primary/60 border-b border-border/50">
                      <td colSpan={8} className="px-5 py-3">
                        <PositionsPanel
                          positions={positions[acc.id]}
                          loading={loadingPositions[acc.id]}
                          onRefresh={() => fetchPositions(acc.id)}
                          onClose={(symbol, side) => closePosition(acc.id, symbol, side)}
                          isClosing={(symbol, side) => !!closingPosition[`${acc.id}|${symbol}|${side}`]}
                        />
                      </td>
                    </tr>
                  )}
                </Fragment>
                );
              }))
            }
          </tbody>
        </table>
        </div>
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
      useSubscriptionStore.getState().fetchSubscription();
      onClose();
    },
    onError: (err: any) => {
      const status = err?.response?.status;
      const msg = err?.response?.data?.message;
      if (status === 403) {
        setError('Account limit reached. Upgrade your plan to add more accounts.');
      } else {
        setError(msg || 'Failed to create account');
      }
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
              <option value={4}>Dzengi</option>
            </select>
            {form.exchangeType === 1 && (
              <div className="flex items-start gap-2 mt-2 bg-accent-yellow/10 border border-accent-yellow/20 rounded-lg px-3 py-2">
                <svg className="w-4 h-4 text-accent-yellow mt-0.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z" />
                </svg>
                <p className="text-xs text-accent-yellow">Set <span className="font-semibold">One-Way Mode</span> in your Bybit account futures settings before connecting.</p>
              </div>
            )}
            {(form.exchangeType === 2 || form.exchangeType === 3) && (
              <div className="flex items-start gap-2 mt-2 bg-accent-yellow/10 border border-accent-yellow/20 rounded-lg px-3 py-2">
                <svg className="w-4 h-4 text-accent-yellow mt-0.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z" />
                </svg>
                <p className="text-xs text-accent-yellow">Set <span className="font-semibold">One-Way Mode</span> in your {form.exchangeType === 2 ? 'Bitget' : 'BingX'} account futures settings before connecting.</p>
              </div>
            )}
            {form.exchangeType === 4 && (
              <div className="flex items-start gap-2 mt-2 bg-accent-yellow/10 border border-accent-yellow/20 rounded-lg px-3 py-2">
                <svg className="w-4 h-4 text-accent-yellow mt-0.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z" />
                </svg>
                <p className="text-xs text-accent-yellow">Dzengi uses <span className="font-semibold">One-Way Mode</span>; the leverage account ID will be auto-detected on save. Funding-rate strategies (HuntingFunding, FundingClaim) are not supported.</p>
              </div>
            )}
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

function PositionsPanel({
  positions,
  loading,
  onRefresh,
  onClose,
  isClosing,
}: {
  positions: Position[] | null | undefined;
  loading: boolean | undefined;
  onRefresh: () => void;
  onClose: (symbol: string, side: string) => void;
  isClosing: (symbol: string, side: string) => boolean;
}) {
  if (loading && positions === undefined) {
    return <div className="text-sm text-text-secondary py-3">Loading positions...</div>;
  }
  if (positions === null) {
    return (
      <div className="flex items-center justify-between py-2">
        <span className="text-sm text-accent-red">Failed to fetch positions</span>
        <button
          onClick={onRefresh}
          className="text-xs px-2.5 py-1 rounded bg-bg-tertiary text-text-secondary hover:bg-border"
        >
          Retry
        </button>
      </div>
    );
  }
  if (!positions || positions.length === 0) {
    return (
      <div className="flex items-center justify-between py-2">
        <span className="text-sm text-text-secondary">No open futures positions</span>
        <button
          onClick={onRefresh}
          disabled={loading}
          className="text-xs px-2.5 py-1 rounded bg-bg-tertiary text-text-secondary hover:bg-border disabled:opacity-50"
        >
          {loading ? "Refreshing..." : "Refresh"}
        </button>
      </div>
    );
  }

  const totalPnl = positions.reduce((s, p) => s + p.unrealizedPnl, 0);
  const fmt = (n: number, frac = 2) =>
    n.toLocaleString(undefined, { minimumFractionDigits: frac, maximumFractionDigits: frac });

  return (
    <div>
      <div className="flex items-center justify-between mb-2">
        <div className="flex items-center gap-3 text-xs text-text-secondary">
          <span>{positions.length} open position{positions.length === 1 ? "" : "s"}</span>
          <span>
            Total uPnL:{" "}
            <span className={totalPnl >= 0 ? "text-accent-green font-medium" : "text-accent-red font-medium"}>
              {totalPnl >= 0 ? "+" : ""}{fmt(totalPnl)} USDT
            </span>
          </span>
        </div>
        <button
          onClick={onRefresh}
          disabled={loading}
          className="text-xs px-2.5 py-1 rounded bg-bg-tertiary text-text-secondary hover:bg-border disabled:opacity-50"
        >
          {loading ? "Refreshing..." : "Refresh"}
        </button>
      </div>
      <div className="overflow-x-auto rounded-lg border border-border">
        <table className="w-full">
          <thead>
            <tr className="text-xs text-text-secondary bg-bg-secondary/40">
              <th className="text-left px-3 py-2 font-medium">Symbol</th>
              <th className="text-left px-3 py-2 font-medium">Side</th>
              <th className="text-right px-3 py-2 font-medium">Qty</th>
              <th className="text-right px-3 py-2 font-medium">Entry</th>
              <th className="text-right px-3 py-2 font-medium">uPnL (USDT)</th>
              <th className="text-right px-3 py-2 font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            {positions.map((p, i) => {
              const pnlPositive = p.unrealizedPnl >= 0;
              const sideLong = p.side.toLowerCase() === "long";
              const closing = isClosing(p.symbol, p.side);
              return (
                <tr key={`${p.symbol}-${p.side}-${i}`} className="border-t border-border/40">
                  <td className="px-3 py-2 text-sm font-mono">{p.symbol}</td>
                  <td className="px-3 py-2 text-sm">
                    <span
                      className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                        sideLong
                          ? "bg-accent-green/10 text-accent-green"
                          : "bg-accent-red/10 text-accent-red"
                      }`}
                    >
                      {p.side}
                    </span>
                  </td>
                  <td className="px-3 py-2 text-right text-sm font-mono">{fmt(p.quantity, 4)}</td>
                  <td className="px-3 py-2 text-right text-sm font-mono">{fmt(p.entryPrice, 4)}</td>
                  <td className={`px-3 py-2 text-right text-sm font-mono ${pnlPositive ? "text-accent-green" : "text-accent-red"}`}>
                    {pnlPositive ? "+" : ""}{fmt(p.unrealizedPnl)}
                  </td>
                  <td className="px-3 py-2 text-right">
                    <button
                      onClick={() => {
                        if (confirm(`Close ${p.side} ${p.symbol} (${fmt(p.quantity, 4)})?\n\nIf a strategy is currently managing this position it may try to reopen it.`)) {
                          onClose(p.symbol, p.side);
                        }
                      }}
                      disabled={closing}
                      className="px-2.5 py-1 text-xs font-medium bg-accent-red/10 text-accent-red rounded hover:bg-accent-red/20 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                    >
                      {closing ? "Closing..." : "Close"}
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
