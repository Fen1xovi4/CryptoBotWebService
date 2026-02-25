import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import api from '../api/client';
import Header from '../components/Layout/Header';
import { useAuthStore } from '../stores/authStore';

interface InviteCodeUsageDto {
  username: string;
  usedAt: string;
}

interface InviteCodeDto {
  id: string;
  code: string;
  assignedRole: string;
  maxUses: number;
  usedCount: number;
  isActive: boolean;
  createdBy: string;
  createdAt: string;
  expiresAt: string | null;
  usages: InviteCodeUsageDto[];
}

const roleBadge: Record<string, string> = {
  Admin: 'bg-accent-blue/10 text-accent-blue',
  Manager: 'bg-purple-500/10 text-purple-400',
  User: 'bg-bg-tertiary text-text-secondary',
};

export default function InviteCodesPage() {
  const [showModal, setShowModal] = useState(false);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const queryClient = useQueryClient();

  const { data: codes, isLoading } = useQuery<InviteCodeDto[]>({
    queryKey: ['invite-codes'],
    queryFn: () => api.get('/invite-codes').then((r) => r.data),
  });

  const deactivateMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/invite-codes/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['invite-codes'] }),
  });

  const copyToClipboard = (text: string) => {
    navigator.clipboard.writeText(text);
  };

  return (
    <div>
      <Header title="Invite Codes" subtitle="Create and manage invitation codes for new users">
        <button
          onClick={() => setShowModal(true)}
          className="inline-flex items-center gap-2 bg-accent-blue hover:bg-accent-blue/90 text-white text-sm font-medium px-4 py-2 rounded-lg transition-colors shadow-md shadow-accent-blue/20"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M12 4.5v15m7.5-7.5h-15" />
          </svg>
          Create Code
        </button>
      </Header>

      <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
        <table className="w-full">
          <thead>
            <tr className="text-xs text-text-secondary border-b border-border">
              <th className="text-left px-5 py-2.5 font-medium">Code</th>
              <th className="text-left px-5 py-2.5 font-medium">Role</th>
              <th className="text-left px-5 py-2.5 font-medium">Uses</th>
              <th className="text-left px-5 py-2.5 font-medium">Status</th>
              <th className="text-left px-5 py-2.5 font-medium">Created by</th>
              <th className="text-left px-5 py-2.5 font-medium">Created</th>
              <th className="text-left px-5 py-2.5 font-medium">Expires</th>
              <th className="text-right px-5 py-2.5 font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            {isLoading ? (
              <tr><td colSpan={8} className="px-5 py-8 text-center text-text-secondary text-sm">Loading...</td></tr>
            ) : codes?.length === 0 ? (
              <tr><td colSpan={8} className="px-5 py-8 text-center text-text-secondary text-sm">No invite codes yet.</td></tr>
            ) : (
              codes?.map((code) => (
                <>
                  <tr key={code.id} className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors">
                    <td className="px-5 py-3">
                      <div className="flex items-center gap-2">
                        <span className="font-mono text-sm font-medium text-text-primary tracking-wider">{code.code}</span>
                        <button
                          onClick={() => copyToClipboard(code.code)}
                          className="text-text-secondary hover:text-accent-blue transition-colors"
                          title="Copy code"
                        >
                          <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M15.666 3.888A2.25 2.25 0 0013.5 2.25h-3c-1.03 0-1.9.693-2.166 1.638m7.332 0c.055.194.084.4.084.612v0a.75.75 0 01-.75.75H9.75a.75.75 0 01-.75-.75v0c0-.212.03-.418.084-.612m7.332 0c.646.049 1.288.11 1.927.184 1.1.128 1.907 1.077 1.907 2.185V19.5a2.25 2.25 0 01-2.25 2.25H6.75A2.25 2.25 0 014.5 19.5V6.257c0-1.108.806-2.057 1.907-2.185a48.208 48.208 0 011.927-.184" />
                          </svg>
                        </button>
                      </div>
                    </td>
                    <td className="px-5 py-3">
                      <span className={`inline-flex items-center text-xs font-semibold px-2.5 py-1 rounded-full ${roleBadge[code.assignedRole] || ''}`}>
                        {code.assignedRole}
                      </span>
                    </td>
                    <td className="px-5 py-3 text-sm text-text-secondary">
                      <span className={code.usedCount > 0 ? 'text-accent-blue' : ''}>
                        {code.usedCount} / {code.maxUses === 0 ? '∞' : code.maxUses}
                      </span>
                    </td>
                    <td className="px-5 py-3">
                      {code.isActive ? (
                        <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-accent-green/10 text-accent-green">
                          <span className="w-1.5 h-1.5 rounded-full bg-accent-green" />
                          Active
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-bg-tertiary text-text-secondary">
                          <span className="w-1.5 h-1.5 rounded-full bg-text-secondary" />
                          Inactive
                        </span>
                      )}
                    </td>
                    <td className="px-5 py-3 text-sm text-text-secondary">{code.createdBy}</td>
                    <td className="px-5 py-3 text-sm text-text-secondary">
                      {new Date(code.createdAt).toLocaleDateString()}
                    </td>
                    <td className="px-5 py-3 text-sm text-text-secondary">
                      {code.expiresAt ? new Date(code.expiresAt).toLocaleDateString() : '—'}
                    </td>
                    <td className="px-5 py-3 text-right">
                      <div className="inline-flex items-center gap-2">
                        {code.usedCount > 0 && (
                          <button
                            onClick={() => setExpandedId(expandedId === code.id ? null : code.id)}
                            className="px-3 py-1.5 text-xs font-medium bg-bg-tertiary text-text-secondary rounded-lg hover:bg-border transition-colors"
                          >
                            {expandedId === code.id ? 'Hide' : 'Usages'}
                          </button>
                        )}
                        {code.isActive && (
                          <button
                            onClick={() => {
                              if (!confirm('Deactivate this invite code?')) return;
                              deactivateMutation.mutate(code.id);
                            }}
                            className="px-3 py-1.5 text-xs font-medium bg-accent-red/10 text-accent-red rounded-lg hover:bg-accent-red/20 transition-colors"
                          >
                            Deactivate
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                  {expandedId === code.id && code.usages.length > 0 && (
                    <tr key={`${code.id}-usages`}>
                      <td colSpan={8} className="px-5 py-3 bg-bg-tertiary/20">
                        <div className="text-xs text-text-secondary mb-2 font-medium">Registered users:</div>
                        <div className="flex flex-wrap gap-2">
                          {code.usages.map((u) => (
                            <span key={u.username} className="inline-flex items-center gap-1.5 text-xs px-2.5 py-1 rounded-full bg-bg-tertiary text-text-primary">
                              {u.username}
                              <span className="text-text-secondary">
                                {new Date(u.usedAt).toLocaleDateString()}
                              </span>
                            </span>
                          ))}
                        </div>
                      </td>
                    </tr>
                  )}
                </>
              ))
            )}
          </tbody>
        </table>
      </div>

      {showModal && <CreateInviteCodeModal onClose={() => setShowModal(false)} />}
    </div>
  );
}

function CreateInviteCodeModal({ onClose }: { onClose: () => void }) {
  const role = useAuthStore((s) => s.role);
  const isAdmin = role === 'Admin';

  const [form, setForm] = useState({
    role: 'User',
    maxUses: '1',
    expiresAt: '',
  });
  const [error, setError] = useState('');
  const queryClient = useQueryClient();

  const mutation = useMutation({
    mutationFn: () =>
      api.post('/invite-codes', {
        role: form.role,
        maxUses: Number(form.maxUses),
        expiresAt: form.expiresAt || undefined,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['invite-codes'] });
      onClose();
    },
    onError: (err: unknown) => {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg || 'Failed to create invite code');
    },
  });

  const inputClass = 'w-full bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all';

  return (
    <div className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50" onClick={onClose}>
      <div className="bg-bg-secondary rounded-xl border border-border p-6 w-full max-w-md shadow-2xl" onClick={(e) => e.stopPropagation()}>
        <h3 className="text-lg font-semibold mb-1">Create Invite Code</h3>
        <p className="text-sm text-text-secondary mb-5">Generate a new invitation code for user registration</p>

        {error && (
          <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg mb-4">
            {error}
          </div>
        )}

        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Assigned Role</label>
            {isAdmin ? (
              <select
                value={form.role}
                onChange={(e) => setForm({ ...form, role: e.target.value })}
                className={inputClass}
              >
                <option value="User">User</option>
                <option value="Manager">Manager</option>
                <option value="Admin">Admin</option>
              </select>
            ) : (
              <input type="text" value="User" disabled className={`${inputClass} opacity-60`} />
            )}
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">
              Max Uses <span className="text-text-secondary font-normal">(0 = unlimited)</span>
            </label>
            <input
              type="number"
              min="0"
              value={form.maxUses}
              onChange={(e) => setForm({ ...form, maxUses: e.target.value })}
              className={inputClass}
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">
              Expires <span className="text-text-secondary font-normal">(optional)</span>
            </label>
            <input
              type="date"
              value={form.expiresAt}
              onChange={(e) => setForm({ ...form, expiresAt: e.target.value })}
              className={inputClass}
            />
          </div>
        </div>

        <div className="flex justify-end gap-3 mt-6">
          <button onClick={onClose} className="px-4 py-2 text-sm font-medium text-text-secondary bg-bg-tertiary rounded-lg hover:bg-border transition-colors">
            Cancel
          </button>
          <button
            onClick={() => mutation.mutate()}
            disabled={mutation.isPending}
            className="px-4 py-2 text-sm font-medium bg-accent-blue hover:bg-accent-blue/90 text-white rounded-lg transition-colors shadow-md shadow-accent-blue/20 disabled:opacity-50 disabled:shadow-none"
          >
            {mutation.isPending ? 'Creating...' : 'Create Code'}
          </button>
        </div>
      </div>
    </div>
  );
}
