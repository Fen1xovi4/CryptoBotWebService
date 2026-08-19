import type { CandleData } from '../../components/Chart/CandlestickChart';

export const STRATEGY_TYPES = [
  'MaratG',
  'SmaDca',
  'GridFloat',
  'GridHedge',
  'SmartGridHedge',
  'HuntingFunding',
  'FundingClaim',
  'FuturesArbitrage',
] as const;

export type StrategyType = (typeof STRATEGY_TYPES)[number];

export const STRATEGY_LABELS: Record<StrategyType, string> = {
  MaratG: 'MaratG (EMA Bounce)',
  SmaDca: 'SmaDca (SMA DCA-сетка)',
  GridFloat: 'GridFloat (плавающая сетка)',
  GridHedge: 'GridHedge (сетка + хедж)',
  SmartGridHedge: 'SmartGridHedge (геометрическая сетка + хедж)',
  HuntingFunding: 'HuntingFunding (охота за фандингом)',
  FundingClaim: 'FundingClaim (сбор фандинга)',
  FuturesArbitrage: 'Арбитраж (фьюч/фьюч)',
};

export interface Account {
  id: string;
  name: string;
  exchangeType: number;
  isActive: boolean;
}

export interface HFLevel {
  offsetPercent: number;
  sizeUsdt: number;
}

export interface SdLevel {
  stepPercent: string;
  multiplier: string;
  count: string;
}

export interface SdTpShift {
  fromTier: string;
  takeProfitPercent: string;
}

export interface GfTier {
  upTo: string;
  size: string;
  dca: string;
  tp: string;
}

export interface ArbLevel {
  entrySpreadPercent: string;
  exitSpreadPercent: string;
  notionalUsdt: string;
}

/* ── Simulate request/response (matches backend /tester/simulate contract) ── */

export interface SimulateRequest {
  accountId: string;
  strategyType: StrategyType;
  symbol: string;
  secondSymbol?: string | null;
  /** Required for FuturesArbitrage — the account on the second exchange. */
  secondAccountId?: string | null;
  fromUtc?: string | null;
  toUtc?: string | null;
  days: number;
  configJson: string;
  makerFeeRate?: number | null;
  takerFeeRate?: number | null;
  /** Re-download the whole window from the exchange instead of serving cached 1m history. */
  bypassCache?: boolean;
}

/** Where the 1m price path came from (backend kline cache vs live download). */
export interface SimulationHistoryStats {
  cacheUsed: boolean;
  candlesFromCache: number;
  candlesDownloaded: number;
  gapsFilled: number;
  downloadSeconds: number;
  cacheReadSeconds: number;
}

/** One covered window of the backend kline cache (GET /tester/cache). */
export interface KlineCacheEntry {
  exchangeType: number;
  symbol: string;
  timeframe: string;
  fromUtc: string;
  toUtc: string;
  candles: number;
  downloadedAt: string;
}

export interface SimulatedTrade {
  time: string;
  side: string;
  action: string;
  price: number;
  quantity: number;
  notionalUsd: number;
  feeUsd: number;
  pnlUsd?: number | null;
  pnlPercent?: number | null;
  reason: string;
}

export interface EquityPoint {
  time: string;
  realizedPnlUsd: number;
  unrealizedPnlUsd: number;
  equityUsd: number;
}

export interface SimulationSummary {
  totalTrades: number;
  winningTrades: number;
  losingTrades: number;
  winRate: number;
  grossPnlUsd: number;
  feesUsd: number;
  fundingPnlUsd: number;
  netPnlUsd: number;
  maxDrawdownUsd: number;
  maxDrawdownPercent: number;
  maxNotionalUsd: number;
  openPositionsAtEnd: number;
  unrealizedPnlAtEndUsd: number;
  completedCycles: number;
  startTime: string;
  endTime: string;
  pathCandlesProcessed: number;
}

export interface SimulationResult {
  trades: SimulatedTrade[];
  indicatorValues: { time: string; value: number }[];
  equityCurve: EquityPoint[];
  chartCandles: CandleData[];
  summary: SimulationSummary;
  warnings: string[];
  history?: SimulationHistoryStats;
}
