import { create } from 'zustand';
import api from '../api/client';

function parseJwtPayload(token: string): Record<string, unknown> | null {
  try {
    const base64 = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    return JSON.parse(atob(base64));
  } catch {
    return null;
  }
}

function getIsAdminFromToken(): boolean {
  const token = localStorage.getItem('accessToken');
  if (!token) return false;
  const payload = parseJwtPayload(token);
  if (!payload) return false;
  const role = payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'];
  return role === 'Admin';
}

interface AuthState {
  isAuthenticated: boolean;
  isAdmin: boolean;
  username: string | null;
  login: (username: string, password: string) => Promise<void>;
  logout: () => void;
  checkAuth: () => void;
}

export const useAuthStore = create<AuthState>((set) => ({
  isAuthenticated: !!localStorage.getItem('accessToken'),
  isAdmin: getIsAdminFromToken(),
  username: null,

  login: async (username: string, password: string) => {
    const { data } = await api.post('/auth/login', { username, password });
    localStorage.setItem('accessToken', data.accessToken);
    localStorage.setItem('refreshToken', data.refreshToken);
    set({ isAuthenticated: true, isAdmin: getIsAdminFromToken(), username });
  },

  logout: () => {
    localStorage.removeItem('accessToken');
    localStorage.removeItem('refreshToken');
    set({ isAuthenticated: false, isAdmin: false, username: null });
  },

  checkAuth: () => {
    const token = localStorage.getItem('accessToken');
    set({ isAuthenticated: !!token, isAdmin: getIsAdminFromToken() });
  },
}));
