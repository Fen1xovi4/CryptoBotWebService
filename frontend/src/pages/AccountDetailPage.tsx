import { useParams, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import api from '../api/client';
import Header from '../components/Layout/Header';

interface Balance {
  asset: string;
  free: number;
  locked: number;
  total: number;
}

interface AccountBalanceResponse {
  accountId: string;
  accountName: string;
  exchange: string;
  balances: Balance[];
}

export default function AccountDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();

  const { data, isLoading, error } = useQuery<AccountBalanceResponse>({
    queryKey: ['balances', id],
    queryFn: () => api.get(`/exchange/${id}/balances`).then((r) => r.data),
    refetchInterval: 15000,
  });

  return (
    <div>
      <Header title={data?.accountName ?? 'Account Details'} subtitle={data ? `${data.exchange} account balances` : undefined}>
        <button
          onClick={() => navigate('/accounts')}
          className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-text-secondary bg-bg-tertiary rounded-lg hover:bg-border transition-colors"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M10.5 19.5L3 12m0 0l7.5-7.5M3 12h18" />
          </svg>
          Back
        </button>
      </Header>

      <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
        <div className="px-5 py-3.5 border-b border-border">
          <h3 className="text-sm font-semibold text-text-primary">Balances</h3>
        </div>

        {isLoading ? (
          <div className="px-5 py-8 text-center text-text-secondary text-sm">Loading balances...</div>
        ) : error ? (
          <div className="px-5 py-8 text-center text-accent-red text-sm">Failed to load balances</div>
        ) : (
          <table className="w-full">
            <thead>
              <tr className="text-xs text-text-secondary border-b border-border">
                <th className="text-left px-5 py-2.5 font-medium">Asset</th>
                <th className="text-right px-5 py-2.5 font-medium">Free</th>
                <th className="text-right px-5 py-2.5 font-medium">Locked</th>
                <th className="text-right px-5 py-2.5 font-medium">Total</th>
              </tr>
            </thead>
            <tbody>
              {data?.balances?.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-5 py-8 text-center text-text-secondary text-sm">
                    No balances found
                  </td>
                </tr>
              ) : (
                data?.balances?.map((b) => (
                  <tr key={b.asset} className="border-b border-border/50 hover:bg-bg-tertiary/30 transition-colors">
                    <td className="px-5 py-3 text-sm font-medium">{b.asset}</td>
                    <td className="px-5 py-3 text-sm text-right text-accent-green">{formatNum(b.free)}</td>
                    <td className="px-5 py-3 text-sm text-right text-text-secondary">{formatNum(b.locked)}</td>
                    <td className="px-5 py-3 text-sm text-right font-semibold">{formatNum(b.total)}</td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

function formatNum(n: number): string {
  if (n === 0) return '0';
  if (n < 0.0001) return n.toExponential(2);
  return n.toLocaleString('en-US', { maximumFractionDigits: 8 });
}
