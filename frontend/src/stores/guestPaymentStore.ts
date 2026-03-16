import { create } from 'zustand';
import api from '../api/client';

export interface GuestPaymentSession {
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
  inviteCode: string | null;
}

interface GuestPaymentState {
  session: GuestPaymentSession | null;
  guestToken: string | null;
  loading: boolean;
  error: string | null;
  createSession: (plan: string, network: string, token: string) => Promise<void>;
  checkSession: () => Promise<void>;
  cancelSession: () => Promise<void>;
  reset: () => void;
}

export const useGuestPaymentStore = create<GuestPaymentState>((set, get) => ({
  session: null,
  guestToken: null,
  loading: false,
  error: null,

  createSession: async (plan, network, token) => {
    set({ loading: true, error: null });
    try {
      const { data } = await api.post('/paymentsessions/guest', { plan, network, token });
      set({ session: data, guestToken: data.guestToken, loading: false });
    } catch (err: unknown) {
      const apiError = err as { response?: { data?: { message?: string } } };
      set({
        error: apiError.response?.data?.message || 'Failed to create payment session',
        loading: false,
      });
    }
  },

  checkSession: async () => {
    const { session, guestToken } = get();
    if (!session || !guestToken) return;
    try {
      const { data } = await api.get(`/paymentsessions/guest/${session.id}?token=${guestToken}`);
      set({ session: data });
    } catch {
      // silent
    }
  },

  cancelSession: async () => {
    // Guest sessions can't be cancelled via API (no auth), just reset locally
    set({ session: null, guestToken: null });
  },

  reset: () => set({ session: null, guestToken: null, loading: false, error: null }),
}));
