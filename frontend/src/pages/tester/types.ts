import type { CandleData } from '../../components/Chart/CandlestickChart';

export const STRATEGY_TYPES = [
  'MaratG',
  'MaratGGrid',
  'SmaDca',
  'GridFloat',
  'GridHedge',
  'SmartGridHedge',
  'HuntingFunding',
  'SpotPingPong',
] as const;

export type StrategyType = (typeof STRATEGY_TYPES)[number];

export const STRATEGY_LABELS: Record<StrategyType, string> = {
  MaratG: 'MaratG (EMA Bounce)',
  MaratGGrid: 'MaratG+Grid (EMA Bounce + сетка)',
  SmaDca: 'SmaDca (SMA DCA-сетка)',
  GridFloat: 'GridFloat (плавающая сетка)',
  GridHedge: 'GridHedge (сетка + хедж)',
  SmartGridHedge: 'SmartGridHedge (геометрическая сетка + хедж)',
  HuntingFunding: 'HuntingFunding (охота за фандингом)',
  SpotPingPong: 'Spot Ping-Pong (спот, пинг-понг по уровням)',
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

export interface SppLevel {
  buyPrice: string;
  sellPrice: string;
  amountUsdt: string;
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

/* ── Simulate request/response (matches backend /tester/simulate contract) ── */

export interface SimulateRequest {
  accountId: string;
  strategyType: StrategyType;
  symbol: string;
  secondSymbol?: string | null;
  fromUtc?: string | null;
  toUtc?: string | null;
  days: number;
  /** Timeframe of the downloaded price path ("1m".."1d"); coarser = fewer bars, lower intrabar fidelity. */
  pathTimeframe: string;
  configJson: string;
  makerFeeRate?: number | null;
  takerFeeRate?: number | null;
  /** Re-download the whole window from the exchange instead of serving cached history. */
  bypassCache?: boolean;
}

/** Where the price path came from (backend kline cache vs live download). */
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
  /** Strategy-specific figures (e.g. GridFloat stake-ladder stats). */
  extras?: { label: string; value: string }[];
}

/* ── Parameter optimization (POST /tester/optimize + polling) ── */

export interface OptimizationParameterSpec {
  /** Dot/index path into configJson, e.g. "takeProfitPercent" or "levels[0].entrySpreadPercent". */
  path: string;
  from?: number | null;
  to?: number | null;
  step?: number | null;
  values?: number[] | null;
}

export interface OptimizeRequest extends SimulateRequest {
  parameters: OptimizationParameterSpec[];
  /** Share of the window (from the end) held out as the out-of-sample check; 0 = off. */
  outOfSamplePercent: number;
}

export interface OptimizationComboResult {
  parameters: Record<string, number>;
  /** In-sample result (whole window when no split). */
  summary?: SimulationSummary | null;
  /** Out-of-sample result; null when the split is off. */
  oosSummary?: SimulationSummary | null;
  error?: string | null;
}

export interface OptimizationStatus {
  jobId: string;
  status: 'Queued' | 'Downloading' | 'Running' | 'Done' | 'Failed' | 'Cancelled';
  completed: number;
  total: number;
  elapsedSeconds: number;
  error?: string | null;
  warnings: string[];
  history?: SimulationHistoryStats | null;
  outOfSamplePercent: number;
  inSampleFromUtc?: string | null;
  inSampleToUtc?: string | null;
  outOfSampleFromUtc?: string | null;
  outOfSampleToUtc?: string | null;
  /** Successful / failed combination counts (Done only). */
  okCount: number;
  failedCount: number;
  /** Rows hidden by the minTrades filter (Done only). */
  filteredOutCount: number;
  firstError?: string | null;
  /** Present only when status === 'Done': one page, already sorted server-side by the requested key. */
  results?: OptimizationComboResult[] | null;
}

/* ── Saved parameter sets (GET/POST/PATCH/DELETE /tester/param-sets) ── */

export interface TesterParamSet {
  id: string;
  exchangeType: number;
  symbol: string;
  strategyType: string;
  name: string;
  note?: string | null;
  configJson: string;
  sweptParams: Record<string, number>;
  periodFromUtc?: string | null;
  periodToUtc?: string | null;
  pathTimeframe: string;
  netPnlUsd: number;
  maxDrawdownUsd: number;
  maxDrawdownPercent: number;
  winRate: number;
  totalTrades: number;
  createdAt: string;
}

export interface SaveTesterParamSetRequest {
  accountId: string;
  symbol: string;
  strategyType: StrategyType;
  name: string;
  note?: string | null;
  configJson: string;
  sweptParams?: Record<string, number> | null;
  pathTimeframe: string;
  summary: SimulationSummary;
}

export interface SimulationResult {
  trades: SimulatedTrade[];
  indicatorValues: { time: string; value: number }[];
  equityCurve: EquityPoint[];
  chartCandles: CandleData[];
  /** Timeframe of chartCandles — the modeled one unless the window was too long. */
  chartTimeframe: string;
  summary: SimulationSummary;
  warnings: string[];
  history?: SimulationHistoryStats;
}
