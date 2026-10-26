import { Component, DestroyRef, ElementRef, afterNextRender, effect, inject, input, viewChild } from '@angular/core';
import {
  AutoscaleInfo, BaselineSeries, CandlestickSeries, ColorType, IChartApi, IPriceLine, ISeriesApi, ISeriesMarkersPluginApi, LineSeries, LineStyle, LineType,
  SeriesMarker, Time, UTCTimestamp, createChart, createSeriesMarkers,
} from 'lightweight-charts';
import { Candle, CurvePoint } from '../core/models';
import { ThemeService } from '../core/ui-state';
import { IST_OFFSET_SECONDS } from './format';

export interface ChartLine { price: number; color: string; title: string; dashed?: boolean }
export interface ChartMarker { timeUtc: string; text: string; color: string; position: 'aboveBar' | 'belowBar' | 'inBar'; shape: 'arrowUp' | 'arrowDown' | 'circle' | 'square' }
export interface ChartOverlay { points: { timeUtc: string; value: number }[]; color: string; title: string }

/** Chart time axis in IST: the library renders UTC, so timestamps are shifted by +05:30. */
export function istTime(utc: string): UTCTimestamp {
  return (Math.floor(Date.parse(utc) / 1000) + IST_OFFSET_SECONDS) as UTCTimestamp;
}

function palette(dark: boolean) {
  return {
    text: dark ? '#c9d1d9' : '#24292f',
    grid: dark ? 'rgba(240,246,252,0.06)' : 'rgba(31,35,40,0.08)',
    border: dark ? '#30363d' : '#d0d7de',
    up: dark ? '#3fb950' : '#1a7f37',
    down: dark ? '#f85149' : '#cf222e',
  };
}

function baseOptions(dark: boolean) {
  const p = palette(dark);
  return {
    autoSize: true,
    layout: { background: { type: ColorType.Solid, color: 'transparent' }, textColor: p.text, attributionLogo: false },
    grid: { vertLines: { color: p.grid }, horzLines: { color: p.grid } },
    rightPriceScale: { borderColor: p.border },
    timeScale: { borderColor: p.border, timeVisible: true, secondsVisible: false },
    crosshair: { mode: 0 },
  };
}

/** Candles with bracket price lines, entry/exit markers and optional overlays (e.g. pattern pivots). */
@Component({
  selector: 'tb-candle-chart',
  template: `<div #host [style.height.px]="height()" class="w-100"></div>`,
})
export class CandleChart {
  readonly candles = input<Candle[]>([]);
  readonly lines = input<ChartLine[]>([]);
  readonly markers = input<ChartMarker[]>([]);
  readonly overlays = input<ChartOverlay[]>([]);
  readonly timeframeSeconds = input(60);
  readonly height = input(280);

  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private readonly theme = inject(ThemeService);
  private chart: IChartApi | null = null;
  private series: ISeriesApi<'Candlestick'> | null = null;
  private markerApi: ISeriesMarkersPluginApi<Time> | null = null;
  private priceLines: IPriceLine[] = [];
  private overlaySeries: ISeriesApi<'Line'>[] = [];

  constructor() {
    afterNextRender(() => {
      const dark = this.theme.theme() === 'dark';
      const p = palette(dark);
      this.chart = createChart(this.host().nativeElement, baseOptions(dark));
      this.series = this.chart.addSeries(CandlestickSeries, {
        upColor: p.up, downColor: p.down, wickUpColor: p.up, wickDownColor: p.down, borderVisible: false,
        // Keep entry / SL / target lines on screen even when price has not reached them
        autoscaleInfoProvider: (original: () => AutoscaleInfo | null) => {
          const info = original();
          const prices = this.lines().map((l) => l.price);
          if (!info?.priceRange || !prices.length) return info;
          return {
            ...info,
            priceRange: {
              minValue: Math.min(info.priceRange.minValue, ...prices),
              maxValue: Math.max(info.priceRange.maxValue, ...prices),
            },
          };
        },
      });
      this.markerApi = createSeriesMarkers(this.series, []);
      this.render();
    });
    effect(() => {
      this.candles(); this.lines(); this.markers(); this.overlays();
      this.render();
    });
    effect(() => {
      const dark = this.theme.theme() === 'dark';
      const p = palette(dark);
      this.chart?.applyOptions(baseOptions(dark));
      this.series?.applyOptions({ upColor: p.up, downColor: p.down, wickUpColor: p.up, wickDownColor: p.down });
    });
    inject(DestroyRef).onDestroy(() => this.chart?.remove());
  }

  private render(): void {
    const chart = this.chart, series = this.series;
    if (!chart || !series) return;
    const bars = this.candles().map((c) => ({ time: istTime(c.time), open: c.open, high: c.high, low: c.low, close: c.close }));
    series.setData(bars);

    for (const l of this.priceLines) series.removePriceLine(l);
    this.priceLines = this.lines().map((l) => series.createPriceLine({
      price: l.price, color: l.color, lineWidth: 1, lineStyle: l.dashed ? LineStyle.Dashed : LineStyle.Solid, axisLabelVisible: true, title: l.title,
    }));

    // Markers must sit on a bar: snap each to the bar that contains it
    const tf = this.timeframeSeconds();
    const times = new Set(bars.map((b) => b.time as number));
    const markers: SeriesMarker<Time>[] = this.markers()
      .map((m) => ({ ...m, time: (Math.floor(istTime(m.timeUtc) / tf) * tf) as UTCTimestamp }))
      .filter((m) => times.has(m.time))
      .sort((a, b) => a.time - b.time)
      .map((m) => ({ time: m.time, position: m.position, color: m.color, shape: m.shape, text: m.text }));
    this.markerApi?.setMarkers(markers);

    for (const s of this.overlaySeries) chart.removeSeries(s);
    this.overlaySeries = this.overlays().filter((o) => o.points.length > 1).map((o) => {
      const s = chart.addSeries(LineSeries, { color: o.color, lineWidth: 2, lineStyle: LineStyle.Dotted, title: o.title, priceLineVisible: false, lastValueVisible: false });
      const seen = new Set<number>();
      s.setData(o.points.map((p) => ({ time: istTime(p.timeUtc), value: p.value }))
        .sort((a, b) => a.time - b.time).filter((p) => !seen.has(p.time) && (seen.add(p.time), true)));
      return s;
    });
    chart.timeScale().fitContent();
  }
}

/** Intraday PnL: net incl. MTM as a baseline (green above zero, red below) and realized as steps. */
@Component({
  selector: 'tb-pnl-chart',
  template: `<div #host [style.height.px]="height()" class="w-100"></div>`,
})
export class PnlChart {
  readonly net = input<CurvePoint[]>([]);
  readonly realized = input<CurvePoint[]>([]);
  readonly height = input(200);

  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private readonly theme = inject(ThemeService);
  private chart: IChartApi | null = null;
  private netSeries: ISeriesApi<'Baseline'> | null = null;
  private realizedSeries: ISeriesApi<'Line'> | null = null;

  constructor() {
    afterNextRender(() => {
      const dark = this.theme.theme() === 'dark';
      this.chart = createChart(this.host().nativeElement, baseOptions(dark));
      this.netSeries = this.chart.addSeries(BaselineSeries, { baseValue: { type: 'price', price: 0 }, lineWidth: 2, title: 'Net', priceLineVisible: false });
      this.realizedSeries = this.chart.addSeries(LineSeries, { lineType: LineType.WithSteps, lineWidth: 2, lineStyle: LineStyle.Dashed, title: 'Realized', priceLineVisible: false });
      this.applyColours(dark);
      this.render();
    });
    effect(() => {
      this.net(); this.realized();
      this.render();
    });
    effect(() => {
      const dark = this.theme.theme() === 'dark';
      this.chart?.applyOptions(baseOptions(dark));
      this.applyColours(dark);
    });
    inject(DestroyRef).onDestroy(() => this.chart?.remove());
  }

  private applyColours(dark: boolean): void {
    const p = palette(dark);
    this.netSeries?.applyOptions({
      topLineColor: p.up, topFillColor1: `${p.up}55`, topFillColor2: `${p.up}05`,
      bottomLineColor: p.down, bottomFillColor1: `${p.down}05`, bottomFillColor2: `${p.down}55`,
    });
    this.realizedSeries?.applyOptions({ color: dark ? '#8b949e' : '#57606a' });
  }

  private render(): void {
    if (!this.chart) return;
    const toData = (pts: CurvePoint[]) => {
      const seen = new Set<number>();
      return pts.map((p) => ({ time: istTime(p.timeUtc), value: p.value }))
        .sort((a, b) => a.time - b.time).filter((p) => !seen.has(p.time) && (seen.add(p.time), true));
    };
    this.netSeries?.setData(toData(this.net()));
    this.realizedSeries?.setData(toData(this.realized()));
    this.chart.timeScale().fitContent();
  }
}
