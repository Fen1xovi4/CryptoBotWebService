import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
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

export default function SupportPage() {
  const [showModal, setShowModal] = useState(false);
  const navigate = useNavigate();

  const { data: tickets, isLoading } = useQuery<SupportTicketDto[]>({
    queryKey: ['support-tickets'],
    queryFn: supportApi.getMyTickets,
    staleTime: 30000,
  });

  return (
    <div>
      <Header title="Support" subtitle="Your support tickets">
        <button
          onClick={() => setShowModal(true)}
          className="inline-flex items-center gap-2 bg-accent-blue hover:bg-accent-blue/90 text-white text-sm font-medium px-4 py-2 rounded-lg transition-colors shadow-md shadow-accent-blue/20"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M12 4.5v15m7.5-7.5h-15" />
          </svg>
          New Ticket
        </button>
      </Header>

      {isLoading ? (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="bg-bg-secondary rounded-xl border border-border p-5 animate-pulse">
              <div className="h-4 bg-bg-tertiary rounded w-1/3 mb-2" />
              <div className="h-3 bg-bg-tertiary rounded w-2/3" />
            </div>
          ))}
        </div>
      ) : tickets?.length === 0 ? (
        <div className="bg-bg-secondary rounded-xl border border-border p-12 text-center">
          <svg className="w-12 h-12 text-text-secondary mx-auto mb-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M8.625 12a.375.375 0 11-.75 0 .375.375 0 01.75 0zm0 0H8.25m4.125 0a.375.375 0 11-.75 0 .375.375 0 01.75 0zm0 0H12m4.125 0a.375.375 0 11-.75 0 .375.375 0 01.75 0zm0 0h-.375M21 12c0 4.556-4.03 8.25-9 8.25a9.764 9.764 0 01-2.555-.337A5.972 5.972 0 015.41 20.97a5.969 5.969 0 01-.474-.065 4.48 4.48 0 00.978-2.025c.09-.457-.133-.901-.467-1.226C3.93 16.178 3 14.189 3 12c0-4.556 4.03-8.25 9-8.25s9 3.694 9 8.25z" />
          </svg>
          <p className="text-text-secondary text-sm">No tickets yet</p>
          <p className="text-text-secondary/60 text-xs mt-1">Create a new ticket if you have any questions</p>
        </div>
      ) : (
        <div className="space-y-3">
          {tickets?.map((ticket) => (
            <TicketCard key={ticket.id} ticket={ticket} onClick={() => navigate(`/support/${ticket.id}`)} />
          ))}
        </div>
      )}

      {showModal && <CreateTicketModal onClose={() => setShowModal(false)} />}
    </div>
  );
}

function TicketCard({ ticket, onClick }: { ticket: SupportTicketDto; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      className="w-full text-left bg-bg-secondary rounded-xl border border-border p-5 hover:bg-bg-tertiary/30 transition-colors"
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1">
            <span className="text-sm font-medium text-text-primary truncate">{ticket.subject}</span>
            {ticket.unreadCount > 0 && (
              <span className="shrink-0 inline-flex items-center justify-center min-w-[18px] h-[18px] px-1 rounded-full bg-accent-blue text-white text-[10px] font-bold">
                {ticket.unreadCount}
              </span>
            )}
          </div>
          {ticket.lastMessage && (
            <p className="text-xs text-text-secondary truncate max-w-lg">{ticket.lastMessage}</p>
          )}
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

function CreateTicketModal({ onClose }: { onClose: () => void }) {
  const [subject, setSubject] = useState('');
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  const mutation = useMutation({
    mutationFn: () => supportApi.createTicket({ subject: subject.trim(), message: message.trim() }),
    onSuccess: (ticket) => {
      queryClient.invalidateQueries({ queryKey: ['support-tickets'] });
      queryClient.invalidateQueries({ queryKey: ['support-unread-count'] });
      onClose();
      navigate(`/support/${ticket.id}`);
    },
    onError: (err: unknown) => {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg || 'Failed to create ticket');
    },
  });

  const inputClass =
    'w-full bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all';

  const canSubmit = subject.trim().length > 0 && message.trim().length > 0;

  return (
    <div
      className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50"
      onClick={onClose}
    >
      <div
        className="bg-bg-secondary rounded-xl border border-border p-6 w-full max-w-lg shadow-2xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between mb-5">
          <div>
            <h3 className="text-lg font-semibold text-text-primary">New Ticket</h3>
            <p className="text-sm text-text-secondary mt-0.5">Describe your issue or question</p>
          </div>
          <button
            onClick={onClose}
            className="p-1.5 rounded-lg text-text-secondary hover:text-text-primary hover:bg-bg-tertiary transition-colors"
            aria-label="Close"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        {error && (
          <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg mb-4">
            {error}
          </div>
        )}

        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Subject</label>
            <input
              type="text"
              value={subject}
              onChange={(e) => setSubject(e.target.value)}
              placeholder="Briefly describe the topic"
              className={inputClass}
              maxLength={200}
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-text-primary mb-1.5">Message</label>
            <textarea
              value={message}
              onChange={(e) => setMessage(e.target.value)}
              placeholder="Describe your issue or question in detail..."
              rows={5}
              className={`${inputClass} resize-none`}
            />
          </div>
        </div>

        <div className="flex justify-end gap-3 mt-6">
          <button
            onClick={onClose}
            className="px-4 py-2 text-sm font-medium text-text-secondary bg-bg-tertiary rounded-lg hover:bg-border transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={() => mutation.mutate()}
            disabled={mutation.isPending || !canSubmit}
            className="px-4 py-2 text-sm font-medium bg-accent-blue hover:bg-accent-blue/90 text-white rounded-lg transition-colors shadow-md shadow-accent-blue/20 disabled:opacity-50 disabled:shadow-none"
          >
            {mutation.isPending ? 'Sending...' : 'Submit'}
          </button>
        </div>
      </div>
    </div>
  );
}
