import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import api from '../api/client';
import Header from '../components/Layout/Header';

interface UserDto {
  id: string;
  username: string;
  role: string;
  isEnabled: boolean;
  invitedBy: string | null;
  createdAt: string;
  accountsCount: number;
  strategiesCount: number;
}

const roleBadge: Record<string, string> = {
  Admin: 'bg-accent-blue/10 text-accent-blue',
  Manager: 'bg-purple-500/10 text-purple-400',
  User: 'bg-bg-tertiary text-text-secondary',
};

const roles = ['Admin', 'Manager', 'User'] as const;

export default function UsersPage() {
  const queryClient = useQueryClient();
  const [roleChanging, setRoleChanging] = useState<string | null>(null);

  const { data: users, isLoading } = useQuery<UserDto[]>({
    queryKey: ['admin-users'],
    queryFn: () => api.get('/users').then((r) => r.data),
  });

  const roleMutation = useMutation({
    mutationFn: ({ id, role }: { id: string; role: string }) =>
      api.put(`/users/${id}/role`, { role }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin-users'] });
      setRoleChanging(null);
    },
  });

  const enabledMutation = useMutation({
    mutationFn: ({ id, isEnabled }: { id: string; isEnabled: boolean }) =>
      api.put(`/users/${id}/enabled`, { isEnabled }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin-users'] }),
  });

  return (
    <div>
      <Header title="Users" subtitle="Manage platform users and roles" />

      <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
        <table className="w-full">
          <thead>
            <tr className="text-xs text-text-secondary border-b border-border">
              <th className="text-left px-5 py-2.5 font-medium">Username</th>
              <th className="text-left px-5 py-2.5 font-medium">Role</th>
              <th className="text-left px-5 py-2.5 font-medium">Status</th>
              <th className="text-left px-5 py-2.5 font-medium">Invited by</th>
              <th className="text-left px-5 py-2.5 font-medium">Accounts</th>
              <th className="text-left px-5 py-2.5 font-medium">Strategies</th>
              <th className="text-left px-5 py-2.5 font-medium">Created</th>
              <th className="text-right px-5 py-2.5 font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            {isLoading ? (
              <tr><td colSpan={8} className="px-5 py-8 text-center text-text-secondary text-sm">Loading...</td></tr>
            ) : users?.length === 0 ? (
              <tr><td colSpan={8} className="px-5 py-8 text-center text-text-secondary text-sm">No users found.</td></tr>
            ) : (
              users?.map((user) => (
                <tr key={user.id} className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors">
                  <td className="px-5 py-3">
                    <div className="flex items-center gap-2">
                      <div className="w-7 h-7 rounded-full bg-bg-tertiary flex items-center justify-center text-[10px] font-bold text-text-primary uppercase">
                        {user.username[0]}
                      </div>
                      <span className="text-sm font-medium text-text-primary">{user.username}</span>
                    </div>
                  </td>
                  <td className="px-5 py-3">
                    {roleChanging === user.id ? (
                      <div className="flex items-center gap-1">
                        {roles.map((r) => (
                          <button
                            key={r}
                            onClick={() => {
                              if (r !== user.role) roleMutation.mutate({ id: user.id, role: r });
                              else setRoleChanging(null);
                            }}
                            className={`text-[10px] font-semibold px-2 py-0.5 rounded transition-colors ${
                              r === user.role
                                ? 'ring-1 ring-accent-blue ' + (roleBadge[r] || '')
                                : 'bg-bg-tertiary text-text-secondary hover:bg-border'
                            }`}
                          >
                            {r}
                          </button>
                        ))}
                      </div>
                    ) : (
                      <span
                        onClick={() => setRoleChanging(user.id)}
                        className={`inline-flex items-center text-xs font-semibold px-2.5 py-1 rounded-full cursor-pointer hover:opacity-80 ${roleBadge[user.role] || ''}`}
                      >
                        {user.role}
                      </span>
                    )}
                  </td>
                  <td className="px-5 py-3">
                    {user.isEnabled ? (
                      <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-accent-green/10 text-accent-green">
                        <span className="w-1.5 h-1.5 rounded-full bg-accent-green" />
                        Active
                      </span>
                    ) : (
                      <span className="inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full bg-accent-red/10 text-accent-red">
                        <span className="w-1.5 h-1.5 rounded-full bg-accent-red" />
                        Disabled
                      </span>
                    )}
                  </td>
                  <td className="px-5 py-3 text-sm text-text-secondary">{user.invitedBy || '—'}</td>
                  <td className="px-5 py-3 text-sm text-text-secondary">{user.accountsCount}</td>
                  <td className="px-5 py-3 text-sm text-text-secondary">{user.strategiesCount}</td>
                  <td className="px-5 py-3 text-sm text-text-secondary">
                    {new Date(user.createdAt).toLocaleDateString()}
                  </td>
                  <td className="px-5 py-3 text-right">
                    <button
                      onClick={() => {
                        if (!confirm(`${user.isEnabled ? 'Disable' : 'Enable'} user "${user.username}"?`)) return;
                        enabledMutation.mutate({ id: user.id, isEnabled: !user.isEnabled });
                      }}
                      disabled={enabledMutation.isPending}
                      className={`px-3 py-1.5 text-xs font-medium rounded-lg transition-colors ${
                        user.isEnabled
                          ? 'bg-accent-red/10 text-accent-red hover:bg-accent-red/20'
                          : 'bg-accent-green/10 text-accent-green hover:bg-accent-green/20'
                      }`}
                    >
                      {user.isEnabled ? 'Disable' : 'Enable'}
                    </button>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
