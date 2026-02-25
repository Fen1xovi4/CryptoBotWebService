import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import api from '../api/client';
import Header from '../components/Layout/Header';

interface ProxyServer {
  id: string;
  name: string;
  host: string;
  port: number;
  hasAuth: boolean;
  isActive: boolean;
  createdAt: string;
  usedByAccounts: number;
}

export default function ProxiesPage() {
  const [showModal, setShowModal] = useState(false);
  const [editProxy, setEditProxy] = useState<ProxyServer | null>(null);
  const queryClient = useQueryClient();

  const { data: proxies, isLoading } = useQuery<ProxyServer[]>({
    queryKey: ['proxies'],
    queryFn: () => api.get('/proxies').then((r) => r.data),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/proxies/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['proxies'] }),
  });

  const toggleActiveMutation = useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) =>
      api.put(`/proxies/${id}`, { isActive }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['proxies'] }),
  });

  return (
    <div>
      <Header title="Proxies" subtitle="Manage SOCKS5 proxy servers for exchange connections">
        <button
          onClick={() => setShowModal(true)}
          className="inline-flex items-center gap-2 bg-accent-blue hover:bg-accent-blue/90 text-white text-sm font-medium px-4 py-2 rounded-lg transition-colors shadow-md shadow-accent-blue/20"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M12 4.5v15m7.5-7.5h-15" />
          </svg>
          Add Proxy
        </button>
      </Header>

      <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
        <table className="w-full">
          <thead>
            <tr className="text-xs text-text-secondary border-b border-border">
              <th className="text-left px-5 py-2.5 font-medium">Name</th>
              <th className="text-left px-5 py-2.5 font-medium">Host:Port</th>
              <th className="text-left px-5 py-2.5 font-medium">Auth</th>
              <th className="text-left px-5 py-2.5 font-medium">Status</th>
              <th className="text-left px-5 py-2.5 font-medium">Used By</th>
              <th className="text-left px-5 py-2.5 font-medium">Created</th>
              <th className="text-right px-5 py-2.5 font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            {isLoading ? (
              <tr><td colSpan={7} className="px-5 py-8 text-center text-text-secondary text-sm">Loading...</td></tr>
            ) : proxies?.length === 0 ? (
              <tr><td colSpan={7} className="px-5 py-8 text-center text-text-secondary text-sm">No proxies yet. Add a SOCKS5 proxy before connecting exchange accounts.</td></tr>
            ) : (
              proxies?.map((proxy) => (
                <tr key={proxy.id} className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors">
                  <td className="px-5 py-3 text-sm font-medium text-text-primary">{proxy.name}</td>
                  <td className="px-5 py-3 text-sm text-text-secondary font-mono">{proxy.host}:{proxy.port}</td>
                  <td className="px-5 py-3">
                    {proxy.hasAuth ? (
                      <span className="inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded-full bg-accent-blue/10 text-accent-blue">
                        <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M16.5 10.5V6.75a4.5 4.5 0 10-9 0v3.75m-.75 11.25h10.5a2.25 2.25 0 002.25-2.25v-6.75a2.25 2.25 0 00-2.25-2.25H6.75a2.25 2.25 0 00-2.25 2.25v6.75a2.25 2.25 0 002.25 2.25z" />
                        </svg>
                        Yes
                      </span>
                    ) : (
                      <span className="text-xs text-text-secondary">No</span>
                    )}
                  </td>
                  <td className="px-5 py-3">
                    {proxy.isActive ? (
                      <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-accent-green/10 text-accent-green">
                        <span className="w-1.5 h-1.5 rounded-full bg-accent-green" />
                        Active
                      </span>
                    ) : (
                      <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-bg-tertiary text-text-secondary">
                        <span className="w-1.5 h-1.5 rounded-full bg-text-secondary" />
                        Disabled
                      </span>
                    )}
                  </td>
                  <td className="px-5 py-3 text-sm text-text-secondary">
                    {proxy.usedByAccounts > 0 ? (
                      <span className="text-accent-blue">{proxy.usedByAccounts} account{proxy.usedByAccounts > 1 ? 's' : ''}</span>
                    ) : (
                      <span>—</span>
                    )}
                  </td>
                  <td className="px-5 py-3 text-sm text-text-secondary">
                    {new Date(proxy.createdAt).toLocaleDateString()}
                  </td>
                  <td className="px-5 py-3 text-right">
                    <div className="inline-flex items-center gap-2">
                      <button
                        onClick={() => toggleActiveMutation.mutate({ id: proxy.id, isActive: !proxy.isActive })}
                        disabled={toggleActiveMutation.isPending}
                        className={`px-3 py-1.5 text-xs font-medium rounded-lg transition-colors ${
                          proxy.isActive
                            ? 'bg-accent-yellow/10 text-accent-yellow hover:bg-accent-yellow/20'
                            : 'bg-accent-green/10 text-accent-green hover:bg-accent-green/20'
                        }`}
                      >
                        {proxy.isActive ? 'Disable' : 'Enable'}
                      </button>
                      <button
                        onClick={() => setEditProxy(proxy)}
                        className="px-3 py-1.5 text-xs font-medium bg-bg-tertiary text-text-secondary rounded-lg hover:bg-border transition-colors"
                      >
                        Edit
                      </button>
                      <button
                        onClick={() => {
                          if (proxy.usedByAccounts > 0) {
                            if (!confirm(`This proxy is used by ${proxy.usedByAccounts} account(s). Delete anyway?`)) return;
                          } else {
                            if (!confirm('Delete this proxy?')) return;
                          }
                          deleteMutation.mutate(proxy.id);
                        }}
                        className="px-3 py-1.5 text-xs font-medium bg-accent-red/10 text-accent-red rounded-lg hover:bg-accent-red/20 transition-colors"
                      >
                        Delete
                      </button>
                    </div>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {showModal && <AddProxyModal onClose={() => setShowModal(false)} />}
      {editProxy && <EditProxyModal proxy={editProxy} onClose={() => setEditProxy(null)} />}
    </div>
  );
}

function AddProxyModal({ onClose }: { onClose: () => void }) {
  const [form, setForm] = useState({
    name: '',
    host: '',
    port: '',
    username: '',
    password: '',
  });
  const [error, setError] = useState('');
  const queryClient = useQueryClient();

  const mutation = useMutation({
    mutationFn: () =>
      api.post('/proxies', {
        name: form.name,
        host: form.host,
        port: Number(form.port),
        username: form.username || undefined,
        password: form.password || undefined,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['proxies'] });
      onClose();
    },
    onError: () => setError('Failed to create proxy'),
  });

  const inputClass = 'w-full bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all';

  return (
    <div className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50" onClick={onClose}>
      <div className="bg-bg-secondary rounded-xl border border-border p-6 w-full max-w-md shadow-2xl" onClick={(e) => e.stopPropagation()}>
        <h3 className="text-lg font-semibold mb-1">Add Proxy</h3>
        <p className="text-sm text-text-secondary mb-5">Add a SOCKS5 proxy server for exchange connections</p>

        {error && (
          <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg mb-4">
            {error}
          </div>
        )}

        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Display Name</label>
            <input type="text" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} className={inputClass} placeholder="My Proxy #1" />
          </div>

          <div className="grid grid-cols-3 gap-3">
            <div className="col-span-2">
              <label className="block text-sm font-medium text-text-primary mb-1.5">Host</label>
              <input type="text" value={form.host} onChange={(e) => setForm({ ...form, host: e.target.value })} className={`${inputClass} font-mono`} placeholder="192.168.1.1" />
            </div>
            <div>
              <label className="block text-sm font-medium text-text-primary mb-1.5">Port</label>
              <input type="number" value={form.port} onChange={(e) => setForm({ ...form, port: e.target.value })} className={`${inputClass} font-mono`} placeholder="8080" />
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Username <span className="text-text-secondary font-normal">(optional)</span></label>
            <input type="text" value={form.username} onChange={(e) => setForm({ ...form, username: e.target.value })} className={inputClass} placeholder="proxy_user" />
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Password <span className="text-text-secondary font-normal">(optional)</span></label>
            <input type="password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} className={inputClass} placeholder="proxy_password" />
          </div>
        </div>

        <div className="flex justify-end gap-3 mt-6">
          <button onClick={onClose} className="px-4 py-2 text-sm font-medium text-text-secondary bg-bg-tertiary rounded-lg hover:bg-border transition-colors">
            Cancel
          </button>
          <button
            onClick={() => mutation.mutate()}
            disabled={mutation.isPending || !form.name || !form.host || !form.port}
            className="px-4 py-2 text-sm font-medium bg-accent-blue hover:bg-accent-blue/90 text-white rounded-lg transition-colors shadow-md shadow-accent-blue/20 disabled:opacity-50 disabled:shadow-none"
          >
            {mutation.isPending ? 'Adding...' : 'Add Proxy'}
          </button>
        </div>
      </div>
    </div>
  );
}

function EditProxyModal({ proxy, onClose }: { proxy: ProxyServer; onClose: () => void }) {
  const [form, setForm] = useState({
    name: proxy.name,
    host: proxy.host,
    port: String(proxy.port),
    username: '',
    password: '',
  });
  const [error, setError] = useState('');
  const queryClient = useQueryClient();

  const mutation = useMutation({
    mutationFn: () => {
      const payload: Record<string, string | number> = {
        name: form.name,
        host: form.host,
        port: Number(form.port),
      };
      if (form.username) payload.username = form.username;
      if (form.password) payload.password = form.password;
      return api.put(`/proxies/${proxy.id}`, payload);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['proxies'] });
      onClose();
    },
    onError: () => setError('Failed to update proxy'),
  });

  const inputClass = 'w-full bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all';

  return (
    <div className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50" onClick={onClose}>
      <div className="bg-bg-secondary rounded-xl border border-border p-6 w-full max-w-md shadow-2xl" onClick={(e) => e.stopPropagation()}>
        <h3 className="text-lg font-semibold mb-1">Edit Proxy</h3>
        <p className="text-sm text-text-secondary mb-5">Update proxy settings</p>

        {error && (
          <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg mb-4">
            {error}
          </div>
        )}

        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Display Name</label>
            <input type="text" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} className={inputClass} />
          </div>

          <div className="grid grid-cols-3 gap-3">
            <div className="col-span-2">
              <label className="block text-sm font-medium text-text-primary mb-1.5">Host</label>
              <input type="text" value={form.host} onChange={(e) => setForm({ ...form, host: e.target.value })} className={`${inputClass} font-mono`} />
            </div>
            <div>
              <label className="block text-sm font-medium text-text-primary mb-1.5">Port</label>
              <input type="number" value={form.port} onChange={(e) => setForm({ ...form, port: e.target.value })} className={`${inputClass} font-mono`} />
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Username <span className="text-text-secondary font-normal">(leave empty to keep current)</span></label>
            <input type="text" value={form.username} onChange={(e) => setForm({ ...form, username: e.target.value })} className={inputClass} placeholder="Leave empty to keep current" />
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Password <span className="text-text-secondary font-normal">(leave empty to keep current)</span></label>
            <input type="password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} className={inputClass} placeholder="Leave empty to keep current" />
          </div>
        </div>

        <div className="flex justify-end gap-3 mt-6">
          <button onClick={onClose} className="px-4 py-2 text-sm font-medium text-text-secondary bg-bg-tertiary rounded-lg hover:bg-border transition-colors">
            Cancel
          </button>
          <button
            onClick={() => mutation.mutate()}
            disabled={mutation.isPending || !form.name || !form.host || !form.port}
            className="px-4 py-2 text-sm font-medium bg-accent-blue hover:bg-accent-blue/90 text-white rounded-lg transition-colors shadow-md shadow-accent-blue/20 disabled:opacity-50 disabled:shadow-none"
          >
            {mutation.isPending ? 'Saving...' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}
