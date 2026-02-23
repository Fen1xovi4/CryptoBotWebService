import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import api from '../api/client';
import Header from '../components/Layout/Header';
import StatusBadge from '../components/ui/StatusBadge';

interface ExchangeAccount {
  id: string;
  name: string;
  exchangeType: number;
  isActive: boolean;
  createdAt: string;
}

const exchangeNames: Record<number, string> = { 1: 'Bybit', 2: 'Bitget', 3: 'BingX' };

interface ConnectionStatus {
  success: boolean;
  message: string;
}

export default function AccountsPage() {
  const [showModal, setShowModal] = useState(false);
  const [connectionStatus, setConnectionStatus] = useState<Record<string, ConnectionStatus>>({});
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  const { data: accounts, isLoading } = useQuery<ExchangeAccount[]>({
    queryKey: ['accounts'],
    queryFn: () => api.get('/accounts').then((r) => r.data),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/accounts/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['accounts'] }),
  });

  const testMutation = useMutation({
    mutationFn: (id: string) => api.post(`/accounts/${id}/test`).then((r) => r.data),
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
              <th className="text-left px-5 py-2.5 font-medium">Status</th>
              <th className="text-left px-5 py-2.5 font-medium">Created</th>
              <th className="text-right px-5 py-2.5 font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            {isLoading ? (
              <tr><td colSpan={5} className="px-5 py-8 text-center text-text-secondary text-sm">Loading...</td></tr>
            ) : accounts?.length === 0 ? (
              <tr><td colSpan={5} className="px-5 py-8 text-center text-text-secondary text-sm">No accounts yet. Add one to get started.</td></tr>
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
                    <td className="px-5 py-3">
                      {status?.success ? (
                        <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-accent-green/10 text-accent-green">
                          <span className="w-1.5 h-1.5 rounded-full bg-accent-green animate-pulse" />
                          Connected
                        </span>
                      ) : status && !status.success ? (
                        <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-accent-red/10 text-accent-red">
                          <span className="w-1.5 h-1.5 rounded-full bg-accent-red" />
                          Error
                        </span>
                      ) : !acc.isActive ? (
                        <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-bg-tertiary text-text-secondary">
                          <span className="w-1.5 h-1.5 rounded-full bg-text-secondary" />
                          Disconnected
                        </span>
                      ) : (
                        <StatusBadge status="Active" />
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
                        ) : !acc.isActive ? (
                          <button
                            onClick={async () => {
                              toggleActiveMutation.mutate({ id: acc.id, isActive: true });
                              try {
                                const result = await testMutation.mutateAsync(acc.id);
                                setConnectionStatus((prev) => ({ ...prev, [acc.id]: result }));
                              } catch {
                                setConnectionStatus((prev) => ({
                                  ...prev,
                                  [acc.id]: { success: false, message: 'Request failed' },
                                }));
                              }
                            }}
                            disabled={testMutation.isPending || toggleActiveMutation.isPending}
                            className="px-3 py-1.5 text-xs font-medium bg-accent-green/10 text-accent-green rounded-lg hover:bg-accent-green/20 transition-colors"
                          >
                            {testMutation.isPending ? 'Connecting...' : 'Connect'}
                          </button>
                        ) : (
                          <button
                            onClick={async () => {
                              try {
                                const result = await testMutation.mutateAsync(acc.id);
                                setConnectionStatus((prev) => ({ ...prev, [acc.id]: result }));
                              } catch {
                                setConnectionStatus((prev) => ({
                                  ...prev,
                                  [acc.id]: { success: false, message: 'Request failed' },
                                }));
                              }
                            }}
                            disabled={testMutation.isPending}
                            className="px-3 py-1.5 text-xs font-medium bg-accent-blue/10 text-accent-blue rounded-lg hover:bg-accent-blue/20 transition-colors"
                          >
                            {testMutation.isPending ? 'Testing...' : 'Test'}
                          </button>
                        )}
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
  });
  const [error, setError] = useState('');
  const queryClient = useQueryClient();

  const mutation = useMutation({
    mutationFn: () =>
      api.post('/accounts', {
        ...form,
        passphrase: form.exchangeType === 2 ? form.passphrase : undefined,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['accounts'] });
      onClose();
    },
    onError: () => setError('Failed to create account'),
  });

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
            disabled={mutation.isPending || !form.name || !form.apiKey || !form.apiSecret}
            className="px-4 py-2 text-sm font-medium bg-accent-blue hover:bg-accent-blue/90 text-white rounded-lg transition-colors shadow-md shadow-accent-blue/20 disabled:opacity-50 disabled:shadow-none"
          >
            {mutation.isPending ? 'Adding...' : 'Add Account'}
          </button>
        </div>
      </div>
    </div>
  );
}
