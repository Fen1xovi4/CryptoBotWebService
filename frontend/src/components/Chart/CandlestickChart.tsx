import { useEffect, useRef } from 'react';
import {
  createChart,
  ColorType,
  CandlestickSeries,
  HistogramSeries,
  LineSeries,
  createSeriesMarkers,
} from 'lightweight-charts';
import type {
  IChartApi,
  ISeriesApi,
  CandlestickData,
  HistogramData,
  LineData,
  Time,
  SeriesMarkerBar,
  ISeriesMarkersPluginApi,
} from 'lightweight-charts';

export interface CandleData {
  openTime: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
}

export interface ChartMarker {
  time: number;
  position: 'aboveBar' | 'belowBar';
  shape: 'arrowUp' | 'arrowDown' | 'circle';
  color: string;
  text?: string;
}

export interface IndicatorDataPoint {
  time: number;
  value: number;
}

interface Props {
  data: CandleData[];
  isLoading: boolean;
  markers?: ChartMarker[];
  indicatorData?: IndicatorDataPoint[];
  indicatorColor?: string;
}

export default function CandlestickChart({
  data,
  isLoading,
  markers,
  indicatorData,
  indicatorColor = '#f59e0b',
}: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const candleRef = useRef<ISeriesApi<'Candlestick'> | null>(null);
  const volumeRef = useRef<ISeriesApi<'Histogram'> | null>(null);
  const lineRef = useRef<ISeriesApi<'Line'> | null>(null);
  const markersRef = useRef<ISeriesMarkersPluginApi<Time> | null>(null);

  // Create chart on mount
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
      timeScale: {
        borderColor: '#333a50',
        timeVisible: true,
        secondsVisible: false,
      },
      width: containerRef.current.clientWidth,
      height: 500,
    });

    const candles = chart.addSeries(CandlestickSeries, {
      upColor: '#22c55e',
      downColor: '#ef4444',
      borderDownColor: '#ef4444',
      borderUpColor: '#22c55e',
      wickDownColor: '#ef4444',
      wickUpColor: '#22c55e',
    });

    const volume = chart.addSeries(HistogramSeries, {
      color: '#3b82f680',
      priceFormat: { type: 'volume' },
      priceScaleId: 'volume',
    });
    volume.priceScale().applyOptions({
      scaleMargins: { top: 0.8, bottom: 0 },
    });

    const line = chart.addSeries(LineSeries, {
      color: indicatorColor,
      lineWidth: 2,
      priceScaleId: 'right',
      lastValueVisible: false,
      crosshairMarkerVisible: false,
    });

    const markersPlugin = createSeriesMarkers(candles);

    chartRef.current = chart;
    candleRef.current = candles;
    volumeRef.current = volume;
    lineRef.current = line;
    markersRef.current = markersPlugin;

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
      candleRef.current = null;
      volumeRef.current = null;
      lineRef.current = null;
      markersRef.current = null;
    };
  }, []);

  // Update candle + volume data
  useEffect(() => {
    if (!candleRef.current || !volumeRef.current || !data?.length) return;

    const mapped: CandlestickData<Time>[] = data.map((c) => ({
      time: (new Date(c.openTime).getTime() / 1000) as Time,
      open: c.open,
      high: c.high,
      low: c.low,
      close: c.close,
    }));

    const vol: HistogramData<Time>[] = data.map((c) => ({
      time: (new Date(c.openTime).getTime() / 1000) as Time,
      value: c.volume,
      color: c.close >= c.open ? '#22c55e40' : '#ef444440',
    }));

    candleRef.current.setData(mapped);
    volumeRef.current.setData(vol);
    chartRef.current?.timeScale().fitContent();
  }, [data]);

  // Update indicator line
  useEffect(() => {
    if (!lineRef.current) return;

    if (indicatorData?.length) {
      const lineData: LineData<Time>[] = indicatorData.map((p) => ({
        time: p.time as Time,
        value: p.value,
      }));
      lineRef.current.setData(lineData);
    } else {
      lineRef.current.setData([]);
    }
  }, [indicatorData]);

  // Update indicator color
  useEffect(() => {
    if (!lineRef.current) return;
    lineRef.current.applyOptions({ color: indicatorColor });
  }, [indicatorColor]);

  // Update markers
  useEffect(() => {
    if (!markersRef.current) return;

    if (markers?.length) {
      const sorted = [...markers].sort((a, b) => a.time - b.time);
      const mapped: SeriesMarkerBar<Time>[] = sorted.map((m) => ({
        time: m.time as Time,
        position: m.position,
        shape: m.shape,
        color: m.color,
        text: m.text ?? '',
        size: 1.5,
      }));
      markersRef.current.setMarkers(mapped);
    } else {
      markersRef.current.setMarkers([]);
    }
  }, [markers]);

  return (
    <div className="relative">
      {isLoading && !data?.length && (
        <div className="absolute inset-0 flex items-center justify-center bg-bg-secondary/80 z-10 rounded-xl">
          <span className="text-sm text-text-secondary">Loading chart...</span>
        </div>
      )}
      <div ref={containerRef} className="w-full rounded-xl overflow-hidden" />
    </div>
  );
}
