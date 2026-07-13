import { useEffect, useRef } from 'react';
import { createChart, ColorType, LineSeries } from 'lightweight-charts';
import type { IChartApi, ISeriesApi, LineData, Time } from 'lightweight-charts';
import type { EquityPoint } from './types';

interface Props {
  data: EquityPoint[];
}

/** Simple equity-over-time line chart. Uses lightweight-charts (already the
 * project's charting library — recharts is in package.json but unused). */
export default function EquityChart({ data }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const lineRef = useRef<ISeriesApi<'Line'> | null>(null);

  useEffect(() => {
    if (!containerRef.current) return;

    const chart = createChart(containerRef.current, {
      layout: {
        background: { type: ColorType.Solid, color: '#242838' },
        textColor: '#7a8299',
      },
      grid: {
        vertLines: { color: '#2d3148' },
        horzLines: { color: '#2d3148' },
      },
      crosshair: { mode: 0 },
      rightPriceScale: { borderColor: '#333a50' },
      timeScale: { borderColor: '#333a50', timeVisible: true, secondsVisible: false },
      width: containerRef.current.clientWidth,
      height: 260,
    });

    const line = chart.addSeries(LineSeries, {
      color: '#3b82f6',
      lineWidth: 2,
      priceFormat: { type: 'price', precision: 2, minMove: 0.01 },
    });

    chartRef.current = chart;
    lineRef.current = line;

    const ro = new ResizeObserver((entries) => {
      for (const entry of entries) {
        chart.applyOptions({ width: entry.contentRect.width });
      }
    });
    ro.observe(containerRef.current);

    return () => {
      ro.disconnect();
      chart.remove();
      chartRef.current = null;
      lineRef.current = null;
    };
  }, []);

  useEffect(() => {
    if (!lineRef.current || !data?.length) return;
    const mapped: LineData<Time>[] = data.map((p) => ({
      time: (new Date(p.time).getTime() / 1000) as Time,
      value: p.equityUsd,
    }));
    lineRef.current.setData(mapped);
    const last = data[data.length - 1];
    lineRef.current.applyOptions({ color: last.equityUsd >= 0 ? '#22c55e' : '#ef4444' });
    chartRef.current?.timeScale().fitContent();
  }, [data]);

  return <div ref={containerRef} className="w-full rounded-xl overflow-hidden" />;
}
