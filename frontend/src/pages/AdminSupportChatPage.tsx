import { useState, useEffect, useRef } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { supportApi } from '../api/support';
import type { SupportMessageDto, SupportTicketDto } from '../api/support';

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

function formatTime(dateStr: string): string {
  return new Date(dateStr).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
}

function formatDate(dateStr: string): string {
  return new Date(dateStr).toLocaleDateString('en-US', { day: 'numeric', month: 'long' });
}

function isSameDay(a: string, b: string): boolean {
  const da = new Date(a);
  const db = new Date(b);
  return da.getFullYear() === db.getFullYear() && da.getMonth() === db.getMonth() && da.getDate() === db.getDate();
}

export default function AdminSupportChatPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [text, setText] = useState('');
  const [showCloseConfirm, setShowCloseConfirm] = useState(false);
  const messagesEndRef = useRef<HTMLDivElement>(null);

  // Try to get ticket info from cached admin list
  const { data: tickets } = useQuery<SupportTicketDto[]>({
    queryKey: ['admin-support-tickets', 'All', ''],
    queryFn: () => supportApi.getAdminTickets(),
    staleTime: 30000,
  });
  const ticket = tickets?.find((t) => t.id === id);

  const { data: messages, isLoading } = useQuery<SupportMessageDto[]>({
    queryKey: ['admin-support-messages', id],
    queryFn: () => supportApi.getAdminTicketMessages(id!),
    enabled: !!id,
    refetchInterval: 10000,
    staleTime: 5000,
  });

  const sendMutation = useMutation({
    mutationFn: (t: string) => supportApi.adminSendMessage(id!, t),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin-support-messages', id] });
      queryClient.invalidateQueries({ queryKey: ['admin-support-tickets'] });
      queryClient.invalidateQueries({ queryKey: ['admin-support-unread-count'] });
      setText('');
    },
  });

  const closeMutation = useMutation({
    mutationFn: () => supportApi.adminCloseTicket(id!),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin-support-tickets'] });
      queryClient.invalidateQueries({ queryKey: ['admin-support-messages', id] });
      setShowCloseConfirm(false);
    },
  });

  // Scroll to bottom when messages change
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  const handleSend = () => {
    const trimmed = text.trim();
    if (!trimmed || sendMutation.isPending) return;
    sendMutation.mutate(trimmed);
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  const isClosed = ticket?.status === 'Closed';

  return (
    <div className="flex flex-col h-full -m-6">
      {/* Header */}
      <div className="flex items-center gap-3 px-6 py-4 border-b border-border bg-bg-secondary shrink-0">
        <button
          onClick={() => navigate('/admin/support')}
          className="p-1.5 rounded-lg text-text-secondary hover:text-text-primary hover:bg-bg-tertiary transition-colors"
          aria-label="Back"
        >
          <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M15.75 19.5L8.25 12l7.5-7.5" />
          </svg>
        </button>

        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            {ticket?.username && (
              <>
                <div className="w-6 h-6 rounded-full bg-bg-tertiary flex items-center justify-center text-[10px] font-bold text-text-primary uppercase shrink-0">
                  {ticket.username[0]}
                </div>
                <span className="text-sm font-semibold text-text-primary">{ticket.username}</span>
                <span className="text-text-secondary">—</span>
              </>
            )}
            <span className="text-sm font-medium text-text-primary truncate">
              {ticket?.subject ?? 'Ticket'}
            </span>
          </div>
          {ticket && (
            <span className={`inline-flex items-center text-xs font-medium px-2 py-0.5 rounded-full mt-0.5 ${statusBadge[ticket.status] ?? 'bg-bg-tertiary text-text-secondary'}`}>
              {statusLabel[ticket.status] ?? ticket.status}
            </span>
          )}
        </div>

        {/* Close ticket button */}
        {!isClosed && (
          <button
            onClick={() => setShowCloseConfirm(true)}
            className="shrink-0 px-3 py-1.5 text-xs font-medium rounded-lg bg-bg-tertiary text-text-secondary hover:bg-accent-red/10 hover:text-accent-red border border-border hover:border-accent-red/30 transition-colors"
          >
            Close Ticket
          </button>
        )}
      </div>

      {/* Messages */}
      <div className="flex-1 overflow-y-auto px-6 py-4 space-y-1">
        {isLoading ? (
          <div className="flex items-center justify-center h-32">
            <span className="text-text-secondary text-sm">Loading...</span>
          </div>
        ) : messages?.length === 0 ? (
          <div className="flex items-center justify-center h-32">
            <span className="text-text-secondary text-sm">No messages</span>
          </div>
        ) : (
          messages?.map((msg, idx) => {
            const prevMsg = idx > 0 ? messages[idx - 1] : null;
            const showDateSep = !prevMsg || !isSameDay(prevMsg.createdAt, msg.createdAt);
            // Admin view: admin messages on right, user messages on left
            const isRight = msg.isFromAdmin;

            return (
              <div key={msg.id}>
                {showDateSep && (
                  <div className="flex items-center justify-center my-4">
                    <span className="text-xs text-text-secondary bg-bg-tertiary px-3 py-1 rounded-full">
                      {formatDate(msg.createdAt)}
                    </span>
                  </div>
                )}
                <div className={`flex ${isRight ? 'justify-end' : 'justify-start'} mb-2`}>
                  <div className={`max-w-[70%] ${isRight ? 'items-end' : 'items-start'} flex flex-col gap-1`}>
                    <span className="text-[11px] text-text-secondary px-1">
                      {msg.senderName}
                    </span>
                    <div
                      className={`px-4 py-2.5 rounded-2xl text-sm leading-relaxed ${
                        isRight
                          ? 'bg-accent-blue text-white rounded-br-sm'
                          : 'bg-bg-secondary border border-border text-text-primary rounded-bl-sm'
                      }`}
                    >
                      {msg.text}
                    </div>
                    <span className="text-[11px] text-text-secondary px-1">{formatTime(msg.createdAt)}</span>
                  </div>
                </div>
              </div>
            );
          })
        )}
        <div ref={messagesEndRef} />
      </div>

      {/* Input */}
      <div className="shrink-0 px-6 py-4 border-t border-border bg-bg-secondary">
        {isClosed ? (
          <div className="flex items-center justify-center py-2">
            <span className="text-sm text-text-secondary">Ticket closed</span>
          </div>
        ) : (
          <div className="flex items-end gap-3">
            <textarea
              value={text}
              onChange={(e) => setText(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder="Type a message... (Enter to send, Shift+Enter for new line)"
              rows={1}
              className="flex-1 bg-bg-primary border border-border rounded-xl px-4 py-2.5 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all resize-none min-h-[42px] max-h-32"
              style={{ height: 'auto' }}
              onInput={(e) => {
                const el = e.currentTarget;
                el.style.height = 'auto';
                el.style.height = `${Math.min(el.scrollHeight, 128)}px`;
              }}
            />
            <button
              onClick={handleSend}
              disabled={!text.trim() || sendMutation.isPending}
              className="shrink-0 w-10 h-10 rounded-xl bg-accent-blue hover:bg-accent-blue/90 text-white flex items-center justify-center transition-colors shadow-md shadow-accent-blue/20 disabled:opacity-50 disabled:shadow-none"
              aria-label="Send"
            >
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M6 12L3.269 3.126A59.768 59.768 0 0121.485 12 59.77 59.77 0 013.27 20.876L5.999 12zm0 0h7.5" />
              </svg>
            </button>
          </div>
        )}
      </div>

      {/* Close confirm modal */}
      {showCloseConfirm && (
        <div
          className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50"
          onClick={() => setShowCloseConfirm(false)}
        >
          <div
            className="bg-bg-secondary rounded-xl border border-border p-6 w-full max-w-sm shadow-2xl"
            onClick={(e) => e.stopPropagation()}
          >
            <h3 className="text-base font-semibold text-text-primary mb-2">Close ticket?</h3>
            <p className="text-sm text-text-secondary mb-5">
              Once closed, the user will not be able to send new messages in this ticket.
            </p>
            <div className="flex justify-end gap-3">
              <button
                onClick={() => setShowCloseConfirm(false)}
                className="px-4 py-2 text-sm font-medium text-text-secondary bg-bg-tertiary rounded-lg hover:bg-border transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={() => closeMutation.mutate()}
                disabled={closeMutation.isPending}
                className="px-4 py-2 text-sm font-medium bg-accent-red hover:bg-accent-red/90 text-white rounded-lg transition-colors disabled:opacity-50"
              >
                {closeMutation.isPending ? 'Closing...' : 'Close'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
