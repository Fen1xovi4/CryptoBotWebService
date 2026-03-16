import api from './client';

export interface SupportTicketDto {
  id: string;
  userId: string;
  username: string;
  subject: string;
  status: string; // "Open" | "Answered" | "Closed"
  lastMessage: string | null;
  unreadCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface SupportMessageDto {
  id: string;
  senderId: string;
  senderName: string;
  text: string;
  isFromAdmin: boolean;
  isRead: boolean;
  createdAt: string;
}

export const supportApi = {
  // User
  getMyTickets: () => api.get<SupportTicketDto[]>('/support').then((r) => r.data),
  getTicketMessages: (id: string) => api.get<SupportMessageDto[]>(`/support/${id}`).then((r) => r.data),
  createTicket: (data: { subject: string; message: string }) =>
    api.post<SupportTicketDto>('/support', data).then((r) => r.data),
  sendMessage: (ticketId: string, text: string) =>
    api.post<SupportMessageDto>(`/support/${ticketId}/messages`, { text }).then((r) => r.data),
  getUnreadCount: () => api.get<{ count: number }>('/support/unread-count').then((r) => r.data),

  // Admin
  getAdminTickets: (params?: { status?: string; search?: string }) =>
    api.get<SupportTicketDto[]>('/support/admin', { params }).then((r) => r.data),
  getAdminTicketMessages: (id: string) =>
    api.get<SupportMessageDto[]>(`/support/admin/${id}`).then((r) => r.data),
  adminSendMessage: (ticketId: string, text: string) =>
    api.post<SupportMessageDto>(`/support/admin/${ticketId}/messages`, { text }).then((r) => r.data),
  adminCloseTicket: (id: string) => api.put(`/support/admin/${id}/close`),
  getAdminUnreadCount: () =>
    api.get<{ count: number }>('/support/admin/unread-count').then((r) => r.data),
};
