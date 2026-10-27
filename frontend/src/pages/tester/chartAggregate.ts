import type { CandleData, ChartMarker, IndicatorDataPoint } from '../../components/Chart/CandlestickChart';

export const CHART_TF_MINUTES: Record<string, number> = {
  '1m': 1, '3m': 3, '5m': 5, '15m': 15, '30m': 30, '1h': 60, '2h': 120, '4h': 240, '6h': 360, '12h': 720, '1d': 1440,
};

/** Timeframes the result chart can be viewed at: the base one plus coarser ones it divides into. */
export function chartTimeframeOptions(baseTf: string): string[] {
  const base = CHART_TF_MINUTES[baseTf];
  if (!base) return [baseTf];
  const coarser = ['5m', '15m', '30m', '1h', '4h', '1d'].filter(
    (tf) => CHART_TF_MINUTES[tf] > base && CHART_TF_MINUTES[tf] % base === 0,
  );
  return [baseTf, ...coarser];
}

/** Bucket start (unix seconds) for an epoch-aligned bucket — same alignment as backend CandleAggregator. */
export function bucketStart(timeSec: number, tf: string): number {
  const span = (CHART_TF_MINUTES[tf] ?? 1) * 60;
  return Math.floor(timeSec / span) * span;
}

export function aggregateCandles(candles: CandleData[], tf: string): CandleData[] {
  const result: CandleData[] = [];
  let current: CandleData | null = null;
  let currentBucket = -1;
  for (const c of candles) {
    const bucket = bucketStart(new Date(c.openTime).getTime() / 1000, tf);
    if (bucket !== currentBucket) {
      if (current) result.push(current);
      currentBucket = bucket;
      current = { ...c, openTime: new Date(bucket * 1000).toISOString() };
    } else {
      current!.high = Math.max(current!.high, c.high);
      current!.low = Math.min(current!.low, c.low);
      current!.close = c.close;
      current!.volume += c.volume;
    }
  }
  if (current) result.push(current);
  return result;
}

/** Snap markers onto bar open times — a marker off the bar grid may not render. */
export function snapMarkers(markers: ChartMarker[], tf: string): ChartMarker[] {
  return markers.map((m) => ({ ...m, time: bucketStart(m.time, tf) }));
}

/**
 * Indicator points on the chart's bar grid: last value per bucket. Points off the grid would add
 * extra time points to the shared time scale and leave gaps between candles.
 */
export function snapIndicator(points: IndicatorDataPoint[], tf: string): IndicatorDataPoint[] {
  const result: IndicatorDataPoint[] = [];
  for (const p of points) {
    const time = bucketStart(p.time, tf);
    const last = result[result.length - 1];
    if (last && last.time === time) last.value = p.value;
    else result.push({ time, value: p.value });
  }
  return result;
}
