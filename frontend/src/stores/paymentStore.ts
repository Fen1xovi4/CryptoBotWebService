import { create } from 'zustand';
import api from '../api/client';

export interface PaymentSession {
  id: string;
  plan: string;
  network: string;
  token: string;
  expectedAmount: number;
  walletAddress: string;
  status: string;
  txHash: string | null;
  receivedAmount: number | null;
  createdAt: string;
  expiresAt: string;
  confirmedAt: string | null;
  remainingSeconds: number;
}

interface PaymentState {
  activeSession: PaymentSession | null;
  history: PaymentSession[];
  loading: boolean;
  error: string | null;
  createSession: (plan: string, network: string, token: string) => Promise<void>;
  checkSession: (id: string) => Promise<void>;
  cancelSession: (id: string) => Promise<void>;
  fetchHistory: () => Promise<void>;
  reset: () => void;
}

export const usePaymentStore = create<PaymentState>((set) => ({
  activeSession: null,
  history: [],
  loading: false,
  error: null,

  createSession: async (plan, network, token) => {
    set({ loading: true, error: null });
    try {
      const { data } = await api.post('/paymentsessions', { plan, network, token });
      set({ activeSession: data, loading: false });
    } catch (err: any) {
      set({
        error: err.response?.data?.message || 'Failed to create payment session',
        loading: false,
      });
    }
  },

  checkSession: async (id) => {
    try {
      const { data } = await api.get(`/paymentsessions/${id}`);
      set({ activeSession: data });
    } catch {
      // silent
    }
  },

  cancelSession: async (id) => {
    try {
      await api.post(`/paymentsessions/${id}/cancel`);
      set({ activeSession: null });
    } catch (err: any) {
      set({ error: err.response?.data?.message || 'Failed to cancel' });
    }
  },

  fetchHistory: async () => {
    try {
      const { data } = await api.get('/paymentsessions/my');
      set({ history: data });
    } catch {
      // silent
    }
  },

  reset: () => set({ activeSession: null, history: [], loading: false, error: null }),
}));
