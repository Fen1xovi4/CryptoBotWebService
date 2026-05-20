import axios from 'axios';
import { useAuthStore } from '../stores/authStore';

const api = axios.create({
  baseURL: '/api',
  headers: { 'Content-Type': 'application/json' },
});

api.interceptors.request.use((config) => {
  const token = localStorage.getItem('accessToken');
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

api.interceptors.response.use(
  (response) => response,
  async (error) => {
    const originalRequest = error.config;
    if (error.response?.status === 401 && !originalRequest._retry) {
      originalRequest._retry = true;
      const refreshToken = localStorage.getItem('refreshToken');
      if (refreshToken) {
        try {
          const { data } = await axios.post('/api/auth/refresh', { refreshToken });
          localStorage.setItem('accessToken', data.accessToken);
          localStorage.setItem('refreshToken', data.refreshToken);
          useAuthStore.getState().checkAuth();
          originalRequest.headers.Authorization = `Bearer ${data.accessToken}`;
          return api(originalRequest);
        } catch {
          localStorage.removeItem('accessToken');
          localStorage.removeItem('refreshToken');
          window.location.href = '/login';
        }
      } else {
        window.location.href = '/login';
      }
    }
    if (error.response?.status === 403) {
      const msg = error.response?.data?.message;
      if (msg === 'Account is disabled') {
        localStorage.removeItem('accessToken');
        localStorage.removeItem('refreshToken');
        window.location.href = '/login';
      }
    }
    return Promise.reject(error);
  }
);

export interface OptimizeSmartGridHedgeRequest {
  p0: number;
  step: number;
  nUp: number;
  nDown: number;
  lotUsd: number;
  skimMode: number; // 0=OneShot, 1=ExcessRecycle, 2=FullRecycle
  makerFeeBps: number;
  takerFeeBps: number;
}

export interface OptimizeSmartGridHedgeResponse {
  qHedgeCoins: number;
  worstCaseLoss: number;
}

export function optimizeSmartGridHedge(
  body: OptimizeSmartGridHedgeRequest,
): Promise<OptimizeSmartGridHedgeResponse> {
  return api.post('/strategies/smart-grid-hedge/optimize-hedge', body).then((r) => r.data);
}

export default api;
