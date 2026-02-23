import { useQuery } from '@tanstack/react-query';
import api from '../api/client';
import Header from '../components/Layout/Header';
import StatusBadge from '../components/ui/StatusBadge';

interface DashboardSummary {
  totalAccounts: number;
  activeAccounts: number;
  runningStrategies: number;
  totalTrades: number;
  accounts: { accountId: string; name: string; exchange: string; isActive: boolean }[];
}

export default function DashboardPage() {
  const { data, isLoading } = useQuery<DashboardSummary>({
    queryKey: ['dashboard'],
    queryFn: () => api.get('/dashboard/summary').then((r) => r.data),
    refetchInterval: 30000,
  });

  return (
    <div>
      <Header title="Dashboard" subtitle="Overview of your trading platform" />

      {isLoading ? (
        <div className="text-text-secondary text-sm">Loading...</div>
      ) : (
        <>
          <div className="grid grid-cols-2 xl:grid-cols-4 gap-4 mb-6">
            <StatCard label="Total Accounts" value={data?.totalAccounts ?? 0} icon={<WalletIcon />} iconBg="bg-accent-blue/15" iconColor="text-accent-blue" />
            <StatCard label="Active Accounts" value={data?.activeAccounts ?? 0} icon={<CheckIcon />} iconBg="bg-accent-green/15" iconColor="text-accent-green" />
            <StatCard label="Running Strategies" value={data?.runningStrategies ?? 0} icon={<ChartIcon />} iconBg="bg-accent-yellow/15" iconColor="text-accent-yellow" />
            <StatCard label="Total Trades" value={data?.totalTrades ?? 0} icon={<ClockIcon />} iconBg="bg-accent-red/15" iconColor="text-accent-red" />
          </div>

          <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
            <div className="px-5 py-3.5 border-b border-border">
              <h3 className="text-sm font-semibold text-text-primary">Exchange Accounts</h3>
            </div>
            <table className="w-full">
              <thead>
                <tr className="text-xs text-text-secondary border-b border-border">
                  <th className="text-left px-5 py-2.5 font-medium">Name</th>
                  <th className="text-left px-5 py-2.5 font-medium">Exchange</th>
                  <th className="text-left px-5 py-2.5 font-medium">Status</th>
                </tr>
              </thead>
              <tbody>
                {data?.accounts?.map((acc) => (
                  <tr key={acc.accountId} className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors">
                    <td className="px-5 py-3 text-sm font-medium">{acc.name}</td>
                    <td className="px-5 py-3 text-sm text-text-secondary">{acc.exchange}</td>
                    <td className="px-5 py-3">
                      <StatusBadge status={acc.isActive ? 'Active' : 'Inactive'} />
                    </td>
                  </tr>
                ))}
                {(!data?.accounts || data.accounts.length === 0) && (
                  <tr>
                    <td colSpan={3} className="px-5 py-8 text-center text-text-secondary text-sm">
                      No accounts added yet
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}

function StatCard({ label, value, icon, iconBg, iconColor }: {
  label: string; value: number; icon: React.ReactNode; iconBg: string; iconColor: string;
}) {
  return (
    <div className="bg-bg-secondary rounded-xl border border-border p-4">
      <div className="flex items-center justify-between mb-3">
        <div className={`w-9 h-9 rounded-lg ${iconBg} flex items-center justify-center ${iconColor}`}>
          {icon}
        </div>
      </div>
      <p className="text-2xl font-bold text-text-primary leading-none">{value}</p>
      <p className="text-xs text-text-secondary mt-1.5">{label}</p>
    </div>
  );
}

function WalletIcon() {
  return (
    <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.8}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M2.25 8.25h19.5M2.25 9h19.5m-16.5 5.25h6m-6 2.25h3m-3.75 3h15a2.25 2.25 0 002.25-2.25V6.75A2.25 2.25 0 0019.5 4.5h-15a2.25 2.25 0 00-2.25 2.25v10.5A2.25 2.25 0 004.5 19.5z" />
    </svg>
  );
}

function CheckIcon() {
  return (
    <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.8}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M9 12.75L11.25 15 15 9.75M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
    </svg>
  );
}

function ChartIcon() {
  return (
    <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.8}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M10.5 6a7.5 7.5 0 107.5 7.5h-7.5V6z" />
      <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 10.5H21A7.5 7.5 0 0013.5 3v7.5z" />
    </svg>
  );
}

function ClockIcon() {
  return (
    <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.8}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M12 6v6h4.5m4.5 0a9 9 0 11-18 0 9 9 0 0118 0z" />
    </svg>
  );
}
