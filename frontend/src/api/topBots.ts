import api from './client';

export interface TopBot {
  id: string;
  strategyType: string;
  exchange: string;
  symbol: string;
  timeframe: string | null;
  direction: string | null;
  isRunning: boolean;
  returnPercent: number;
  pnlUsd: number;
  peakCapitalUsd: number;
  periodClosedTrades: number;
  periodWinRate: number;
  totalClosedTrades: number;
  totalPnlUsd: number;
  activeDays: number;
  config: Record<string, unknown>;
}

export interface TopBotsResponse {
  periodDays: number;
  generatedAtUtc: string;
  bots: TopBot[];
}

export interface CopyTopBotResult {
  workspaceId: string;
  strategyId: string;
}

export type TopBotsPeriod = 7 | 30 | 90;

export async function fetchTopBots(days: number, limit: number): Promise<TopBotsResponse> {
  const res = await api.get<TopBotsResponse>('/top-bots', { params: { days, limit } });
  return res.data;
}

export async function copyTopBot(id: string, accountId: string): Promise<CopyTopBotResult> {
  const res = await api.post<CopyTopBotResult>(`/top-bots/${id}/copy`, { accountId });
  return res.data;
}
