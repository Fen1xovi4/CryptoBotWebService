import { create } from 'zustand';
import api from '../api/client';

export type UserRole = 'Admin' | 'Manager' | 'User';

function parseJwtPayload(token: string): Record<string, unknown> | null {
  try {
    const base64 = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    return JSON.parse(atob(base64));
  } catch {
    return null;
  }
}

function getRoleFromToken(): UserRole | null {
  const token = localStorage.getItem('accessToken');
  if (!token) return null;
  const payload = parseJwtPayload(token);
  if (!payload) return null;
  const role = payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'];
  if (role === 'Admin' || role === 'Manager' || role === 'User') return role;
  return null;
}

function getUsernameFromToken(): string | null {
  const token = localStorage.getItem('accessToken');
  if (!token) return null;
  const payload = parseJwtPayload(token);
  if (!payload) return null;
  const name = payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name'];
  return typeof name === 'string' ? name : null;
}

interface AuthState {
  isAuthenticated: boolean;
  role: UserRole | null;
  username: string | null;
  login: (username: string, password: string) => Promise<void>;
  register: (inviteCode: string, username: string, password: string) => Promise<void>;
  logout: () => void;
  checkAuth: () => void;
}

export const useAuthStore = create<AuthState>((set) => ({
  isAuthenticated: !!localStorage.getItem('accessToken'),
  role: getRoleFromToken(),
  username: getUsernameFromToken(),

  login: async (username: string, password: string) => {
    const { data } = await api.post('/auth/login', { username, password });
    localStorage.setItem('accessToken', data.accessToken);
    localStorage.setItem('refreshToken', data.refreshToken);
    set({ isAuthenticated: true, role: getRoleFromToken(), username });
  },

  register: async (inviteCode: string, username: string, password: string) => {
    const { data } = await api.post('/auth/register', { inviteCode, username, password });
    localStorage.setItem('accessToken', data.accessToken);
    localStorage.setItem('refreshToken', data.refreshToken);
    set({ isAuthenticated: true, role: getRoleFromToken(), username });
  },

  logout: () => {
    localStorage.removeItem('accessToken');
    localStorage.removeItem('refreshToken');
    set({ isAuthenticated: false, role: null, username: null });
  },

  checkAuth: () => {
    const token = localStorage.getItem('accessToken');
    set({ isAuthenticated: !!token, role: getRoleFromToken(), username: getUsernameFromToken() });
  },
}));

// Selectors
export const selectIsAdmin = (s: AuthState) => s.role === 'Admin';
export const selectIsManager = (s: AuthState) => s.role === 'Manager';
export const selectIsAdminOrManager = (s: AuthState) => s.role === 'Admin' || s.role === 'Manager';
