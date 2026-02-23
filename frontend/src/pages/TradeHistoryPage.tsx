import Header from '../components/Layout/Header';

export default function TradeHistoryPage() {
  return (
    <div>
      <Header title="Trade History" subtitle="All executed trades from your strategies" />

      <div className="bg-bg-secondary rounded-xl border border-border overflow-hidden">
        <table className="w-full">
          <thead>
            <tr className="text-xs text-text-secondary border-b border-border">
              <th className="text-left px-5 py-2.5 font-medium">Date</th>
              <th className="text-left px-5 py-2.5 font-medium">Symbol</th>
              <th className="text-left px-5 py-2.5 font-medium">Side</th>
              <th className="text-right px-5 py-2.5 font-medium">Price</th>
              <th className="text-right px-5 py-2.5 font-medium">Quantity</th>
              <th className="text-left px-5 py-2.5 font-medium">Status</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td colSpan={6} className="px-5 py-8 text-center text-text-secondary text-sm">
                No trades recorded yet. Trades will appear here once strategies start executing.
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  );
}
