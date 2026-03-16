import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import Header from '../components/Layout/Header';
import { supportApi } from '../api/support';
import type { SupportTicketDto } from '../api/support';

const statusBadge: Record<string, string> = {
  Open: 'bg-accent-yellow/10 text-accent-yellow',
  Answered: 'bg-accent-green/10 text-accent-green',
  Closed: 'bg-bg-tertiary text-text-secondary',
};

const statusLabel: Record<string, string> = {
  Open: 'Open',
  Answered: 'Answered',
  Closed: 'Closed',
};

type FilterTab = 'All' | 'Open' | 'Answered' | 'Closed';

const FILTER_TABS: FilterTab[] = ['All', 'Open', 'Answered', 'Closed'];

function formatRelativeTime(dateStr: string): string {
  const diff = Date.now() - new Date(dateStr).getTime();
  const minutes = Math.floor(diff / 60000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days}d ago`;
  return new Date(dateStr).toLocaleDateString('en-US');
}

export default function AdminSupportPage() {
  const [activeTab, setActiveTab] = useState<FilterTab>('All');
  const [search, setSearch] = useState('');
  const [searchInput, setSearchInput] = useState('');
  const navigate = useNavigate();

  const queryParams = {
    status: activeTab !== 'All' ? activeTab : undefined,
    search: search || undefined,
  };

  const { data: tickets, isLoading } = useQuery<SupportTicketDto[]>({
    queryKey: ['admin-support-tickets', activeTab, search],
    queryFn: () => supportApi.getAdminTickets(queryParams),
    staleTime: 15000,
  });

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault();
    setSearch(searchInput.trim());
  };

  const handleClearSearch = () => {
    setSearch('');
    setSearchInput('');
  };

  return (
    <div>
      <Header title="Support Tickets" subtitle="Manage user support requests" />

      {/* Filters */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center gap-3 mb-5">
        {/* Status tabs */}
        <div className="flex items-center gap-1.5 flex-wrap">
          {FILTER_TABS.map((tab) => (
            <button
              key={tab}
              onClick={() => setActiveTab(tab)}
              className={`text-xs font-medium px-3 py-1.5 rounded-lg transition-colors ${
                activeTab === tab
                  ? 'bg-accent-blue text-white shadow-md shadow-accent-blue/25'
                  : 'bg-bg-secondary border border-border text-text-secondary hover:text-text-primary hover:bg-bg-tertiary'
              }`}
            >
              {tab}
            </button>
          ))}
        </div>

        {/* Search */}
        <form onSubmit={handleSearch} className="flex items-center gap-2 ml-auto">
          <div className="relative">
            <svg className="w-4 h-4 text-text-secondary absolute left-3 top-1/2 -translate-y-1/2" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M21 21l-5.197-5.197m0 0A7.5 7.5 0 105.196 5.196a7.5 7.5 0 0010.607 10.607z" />
            </svg>
            <input
              type="text"
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
              placeholder="Search by username"
              className="bg-bg-secondary border border-border rounded-lg pl-9 pr-4 py-2 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all w-52"
            />
          </div>
          <button
            type="submit"
            className="px-3 py-2 text-xs font-medium bg-bg-secondary border border-border rounded-lg text-text-secondary hover:text-text-primary hover:bg-bg-tertiary transition-colors"
          >
            Search
          </button>
          {search && (
            <button
              type="button"
              onClick={handleClearSearch}
              className="px-3 py-2 text-xs font-medium bg-bg-secondary border border-border rounded-lg text-text-secondary hover:text-text-primary hover:bg-bg-tertiary transition-colors"
            >
              Clear
            </button>
          )}
        </form>
      </div>

      {isLoading ? (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="bg-bg-secondary rounded-xl border border-border p-5 animate-pulse">
              <div className="flex items-center gap-3 mb-2">
                <div className="w-8 h-8 rounded-full bg-bg-tertiary" />
                <div className="h-4 bg-bg-tertiary rounded w-24" />
              </div>
              <div className="h-3 bg-bg-tertiary rounded w-1/2" />
            </div>
          ))}
        </div>
      ) : tickets?.length === 0 ? (
        <div className="bg-bg-secondary rounded-xl border border-border p-12 text-center">
          <svg className="w-12 h-12 text-text-secondary mx-auto mb-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M8.625 12a.375.375 0 11-.75 0 .375.375 0 01.75 0zm0 0H8.25m4.125 0a.375.375 0 11-.75 0 .375.375 0 01.75 0zm0 0H12m4.125 0a.375.375 0 11-.75 0 .375.375 0 01.75 0zm0 0h-.375M21 12c0 4.556-4.03 8.25-9 8.25a9.764 9.764 0 01-2.555-.337A5.972 5.972 0 015.41 20.97a5.969 5.969 0 01-.474-.065 4.48 4.48 0 00.978-2.025c.09-.457-.133-.901-.467-1.226C3.93 16.178 3 14.189 3 12c0-4.556 4.03-8.25 9-8.25s9 3.694 9 8.25z" />
          </svg>
          <p className="text-text-secondary text-sm">
            {search ? `No tickets found for "${search}"` : 'No tickets'}
          </p>
        </div>
      ) : (
        <div className="space-y-3">
          {tickets?.map((ticket) => (
            <AdminTicketCard
              key={ticket.id}
              ticket={ticket}
              onClick={() => navigate(`/admin/support/${ticket.id}`)}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function AdminTicketCard({ ticket, onClick }: { ticket: SupportTicketDto; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      className="w-full text-left bg-bg-secondary rounded-xl border border-border p-5 hover:bg-bg-tertiary/30 transition-colors"
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-start gap-3 flex-1 min-w-0">
          {/* Avatar */}
          <div className="w-9 h-9 rounded-full bg-bg-tertiary flex items-center justify-center text-xs font-bold text-text-primary uppercase shrink-0">
            {ticket.username[0]}
          </div>
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2 mb-0.5">
              <span className="text-xs font-semibold text-text-secondary">{ticket.username}</span>
              {ticket.unreadCount > 0 && (
                <span className="inline-flex items-center justify-center min-w-[18px] h-[18px] px-1 rounded-full bg-accent-blue text-white text-[10px] font-bold">
                  {ticket.unreadCount}
                </span>
              )}
            </div>
            <span className="text-sm font-medium text-text-primary block truncate">{ticket.subject}</span>
            {ticket.lastMessage && (
              <p className="text-xs text-text-secondary truncate max-w-lg mt-0.5">{ticket.lastMessage}</p>
            )}
          </div>
        </div>
        <div className="flex items-center gap-3 shrink-0">
          <span className={`inline-flex items-center text-xs font-medium px-2.5 py-1 rounded-full ${statusBadge[ticket.status] ?? 'bg-bg-tertiary text-text-secondary'}`}>
            {statusLabel[ticket.status] ?? ticket.status}
          </span>
          <span className="text-xs text-text-secondary">{formatRelativeTime(ticket.updatedAt)}</span>
          <svg className="w-4 h-4 text-text-secondary" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M8.25 4.5l7.5 7.5-7.5 7.5" />
          </svg>
        </div>
      </div>
    </button>
  );
}
