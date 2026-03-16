import { create } from 'zustand';
import api from '../api/client';

interface SubscriptionState {
  plan: string | null;
  status: string | null;
  maxAccounts: number;
  maxActiveBots: number;
  currentAccounts: number;
  currentActiveBots: number;
  expiresAt: string | null;
  isAdmin: boolean;
  loaded: boolean;
  fetchSubscription: () => Promise<void>;
  reset: () => void;
}

export const useSubscriptionStore = create<SubscriptionState>((set) => ({
  plan: null,
  status: null,
  maxAccounts: 0,
  maxActiveBots: 0,
  currentAccounts: 0,
  currentActiveBots: 0,
  expiresAt: null,
  isAdmin: false,
  loaded: false,

  fetchSubscription: async () => {
    try {
      const { data } = await api.get('/subscriptions/my');
      set({
        plan: data.plan,
        status: data.status,
        maxAccounts: data.maxAccounts,
        maxActiveBots: data.maxActiveBots,
        currentAccounts: data.currentAccounts,
        currentActiveBots: data.currentActiveBots,
        expiresAt: data.expiresAt,
        isAdmin: data.isAdmin ?? false,
        loaded: true,
      });
    } catch {
      set({ loaded: true });
    }
  },

  reset: () => set({
    plan: null,
    status: null,
    maxAccounts: 0,
    maxActiveBots: 0,
    currentAccounts: 0,
    currentActiveBots: 0,
    expiresAt: null,
    isAdmin: false,
    loaded: false,
  }),
}));
