import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from '../../core/api';
import { DashboardStore } from '../../core/dashboard-store';
import { Candle, TradeDetail } from '../../core/models';
import { PanelService } from '../../core/panels';
import { CandleChart, ChartLine, ChartMarker, ChartOverlay } from '../../shared/charts';
import { InrPipe, IstPipe, PricePipe, pnlClass } from '../../shared/format';
import { Drawer } from '../../shared/ui';
import { STATUS_BADGE } from './trade-grid';

const TF_SECONDS: Record<string, number> = { '1m': 60, '3m': 180, '5m': 300, '15m': 900, '1h': 3600 };
const LEG_LABEL: Record<string, string> = { ENTRY: 'Entry', STOP_LOSS: 'Stop-loss', TARGET_SELL: 'Target', EMERGENCY_EXIT: 'Exit' };

interface Pivot { t: string; price: number; high: boolean }

@Component({
  selector: 'tb-trade-detail',
  imports: [Drawer, CandleChart, FormsModule, InrPipe, IstPipe, PricePipe],
  template: `
    <tb-drawer [open]="isOpen()" (closed)="panels.close()" [title]="title()" width="min(980px, 100vw)">
      @if (error()) { <div class="alert alert-danger">{{ error() }}</div> }
      @if (detail(); as d) {
        @if (row(); as r) {
          <div class="d-flex flex-wrap gap-4 mb-3">
            <div><div class="tb-label">Status</div><span class="badge" [class]="badge(r.status).cls">{{ badge(r.status).text }}</span></div>
            <div><div class="tb-label">Account</div>{{ r.accountName }} · {{ r.mode }}</div>
            <div><div class="tb-label">Qty</div><span class="num">{{ r.quantity }} ({{ r.lots }} lots)</span></div>
            <div><div class="tb-label">Entry</div><span class="num">{{ r.entryPrice | price }}</span><span class="small text-body-secondary ms-2">at {{ r.entryTimeUtc | ist }}</span></div>
            <div><div class="tb-label">{{ r.status === 'OPEN' ? 'LTP' : 'Exit' }}</div><span class="num">{{ (r.status === 'OPEN' ? r.ltp : r.exitPrice) | price }}</span></div>
            <div><div class="tb-label">Net PnL</div><span class="num fw-semibold" [class]="pnlClass(r.netPnl)">{{ r.netPnl | inr: 'signed' }}</span></div>
            <div><div class="tb-label">Charges</div><span class="num">{{ r.charges | inr }}</span></div>
          </div>
        }

        <div class="d-flex align-items-center gap-2 mb-1">
          <h3 class="h6 mb-0">Option premium</h3>
          <select class="form-select form-select-sm w-auto ms-auto" aria-label="Option chart timeframe" [ngModel]="optionTf()" (ngModelChange)="optionTf.set($event)">
            @for (tf of timeframes; track tf) { <option [value]="tf">{{ tf }}</option> }
          </select>
        </div>
        @if (optionBars().length) {
          <tb-candle-chart [candles]="optionBars()" [lines]="lines()" [markers]="markers()" [timeframeSeconds]="tfSeconds(optionTf())" [height]="300" />
        } @else { <div class="text-body-secondary small py-4 text-center">No option candles stored for this trade yet.</div> }

        <h3 class="h6 mt-3 mb-1">Underlying {{ d.trade.detection?.timeframe ?? '' }} · {{ d.trade.detection?.pattern?.patternName ?? 'pattern' }}</h3>
        @if (underlyingBars().length) {
          <tb-candle-chart [candles]="underlyingBars()" [overlays]="pivots()" [lines]="breakoutLine()" [timeframeSeconds]="tfSeconds(underlyingTf())" [height]="240" />
        } @else { <div class="text-body-secondary small py-4 text-center">No underlying candles stored for this day.</div> }

        <h3 class="h6 mt-3">Order legs</h3>
        <div class="table-responsive">
          <table class="table table-sm tb-grid">
            <thead><tr><th>Leg</th><th class="d-none d-sm-table-cell">Side</th><th class="text-end">Qty</th><th class="d-none d-sm-table-cell">Type</th><th class="text-end">Price</th><th class="text-end d-none d-sm-table-cell">Trigger</th><th class="text-end d-none d-sm-table-cell">SL / Target</th><th>Status</th><th class="d-none d-sm-table-cell">Placed</th><th class="d-none d-sm-table-cell">Broker #</th></tr></thead>
            <tbody>
              @for (o of d.orders; track o.orderId) {
                <tr>
                  <td>{{ leg(o.orderLegType) }}</td><td class="d-none d-sm-table-cell">{{ o.txnType === 'B' ? 'BUY' : 'SELL' }}</td>
                  <td class="num text-end">{{ o.filledQuantity }}/{{ o.quantity }}</td><td class="d-none d-sm-table-cell">{{ o.orderType }}</td>
                  <td class="num text-end">{{ (o.avgFillPrice ?? o.price) | price }}
                    @if (o.triggerPrice) { <div class="small text-body-secondary d-sm-none">trigger {{ o.triggerPrice | price }}</div> }
                  </td><td class="num text-end d-none d-sm-table-cell">{{ o.triggerPrice | price }}</td>
                  <td class="num text-end d-none d-sm-table-cell">{{ o.stopLossPrice | price }} / {{ o.targetSellPrice | price }}</td>
                  <td class="tb-wrap">{{ o.status }} @if (o.rejectionReason) { <span class="small text-danger d-block">{{ o.rejectionReason }}</span> }</td>
                  <td class="num d-none d-sm-table-cell">{{ o.placedAt | ist }}</td><td class="small d-none d-sm-table-cell">{{ o.brokerOrderNo ?? '—' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else if (!error()) {
        <div class="placeholder-glow"><span class="placeholder col-12" style="height: 300px"></span></div>
      }
    </tb-drawer>
  `,
})
export class TradeDetailDrawer {
  protected readonly panels = inject(PanelService);
  private readonly api = inject(ApiService);
  private readonly store = inject(DashboardStore);
  protected readonly pnlClass = pnlClass;
  protected readonly timeframes = ['1m', '3m', '5m', '15m'];

  protected readonly isOpen = this.panels.isOpen('trade');
  private readonly tradeId = this.panels.idFor('trade');
  protected readonly detail = signal<TradeDetail | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly optionBars = signal<Candle[]>([]);
  protected readonly underlyingBars = signal<Candle[]>([]);
  protected readonly optionTf = signal('1m');

  protected readonly row = computed(() => this.store.overview()?.trades.find((t) => t.tradeId === this.tradeId()) ?? null);
  protected readonly title = computed(() => this.row()?.contract ?? this.detail()?.trade.security?.pmlSymbol ?? 'Trade');
  protected readonly underlyingTf = computed(() => this.detail()?.trade.detection?.timeframe ?? '5m');

  protected readonly lines = computed<ChartLine[]>(() => {
    const t = this.detail()?.trade;
    const r = this.row();
    if (!t) return [];
    const trailing = r?.trailingSl ?? t.trailingSlPrice;
    const out: ChartLine[] = [
      { price: t.entryBuyPrice, color: '#58a6ff', title: 'Entry' },
      { price: t.stopLossPrice, color: '#f85149', title: 'SL' },
      { price: t.targetSellPrice, color: '#3fb950', title: 'Target' },
    ];
    if (trailing > t.stopLossPrice) out.push({ price: trailing, color: '#d29922', title: 'Trail SL', dashed: true });
    if (t.exitSellPrice) out.push({ price: t.exitSellPrice, color: '#8b949e', title: 'Exit', dashed: true });
    return out;
  });

  protected readonly markers = computed<ChartMarker[]>(() => {
    const t = this.detail()?.trade;
    if (!t) return [];
    const m: ChartMarker[] = [{ timeUtc: t.entryTime, text: 'Buy', color: '#58a6ff', position: 'belowBar', shape: 'arrowUp' }];
    if (t.exitTime) m.push({ timeUtc: t.exitTime, text: 'Sell', color: '#d29922', position: 'aboveBar', shape: 'arrowDown' });
    return m;
  });

  protected readonly pivots = computed<ChartOverlay[]>(() => {
    const raw = this.detail()?.trade.detection?.pivotCoordinates;
    if (!raw) return [];
    try {
      const pts = (JSON.parse(raw) as Pivot[]).map((p) => ({ timeUtc: p.t, value: p.price }));
      return [{ points: pts, color: '#a371f7', title: 'Pivots' }];
    } catch {
      return [];
    }
  });

  protected readonly breakoutLine = computed<ChartLine[]>(() => {
    const d = this.detail()?.trade.detection;
    return d ? [{ price: d.neckOrBreakoutPrice, color: '#a371f7', title: 'Breakout', dashed: true }] : [];
  });

  private refreshTimer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    effect(() => {
      const id = this.tradeId();
      untracked(() => {
        this.reset();
        if (id !== null) void this.load(id);
      });
    });
    effect(() => {
      this.optionTf();
      untracked(() => void this.loadOptionBars());
    });
    inject(DestroyRef).onDestroy(() => this.reset());
  }

  protected badge(s: keyof typeof STATUS_BADGE) { return STATUS_BADGE[s]; }
  protected leg(l: string): string { return LEG_LABEL[l] ?? l; }
  protected tfSeconds(tf: string): number { return TF_SECONDS[tf] ?? 60; }

  private reset(): void {
    if (this.refreshTimer) clearInterval(this.refreshTimer);
    this.refreshTimer = null;
    this.detail.set(null);
    this.error.set(null);
    this.optionBars.set([]);
    this.underlyingBars.set([]);
  }

  private async load(id: number): Promise<void> {
    try {
      const d = await firstValueFrom(this.api.trade(id));
      if (this.tradeId() !== id) return;
      this.detail.set(d);
      await Promise.all([this.loadOptionBars(), this.loadUnderlyingBars()]);
      if (d.trade.tradeStatus === 'OPEN') {
        this.refreshTimer = setInterval(() => {
          void this.loadOptionBars();
          void this.reloadDetail(id);
        }, 15000);
      }
    } catch (e) {
      this.error.set(apiErrorText(e));
    }
  }

  private async reloadDetail(id: number): Promise<void> {
    try { if (this.tradeId() === id) this.detail.set(await firstValueFrom(this.api.trade(id))); } catch { /* keep last */ }
  }

  private async loadOptionBars(): Promise<void> {
    const t = this.detail()?.trade;
    if (!t?.security) return;
    try {
      this.optionBars.set(await firstValueFrom(this.api.candles(t.security.instrumentId, t.securityId, this.optionTf(), t.entryTime.slice(0, 10))));
    } catch { /* chart stays empty */ }
  }

  private async loadUnderlyingBars(): Promise<void> {
    const t = this.detail()?.trade;
    if (!t?.security) return;
    try {
      this.underlyingBars.set(await firstValueFrom(this.api.candles(t.security.instrumentId, null, this.underlyingTf(), t.entryTime.slice(0, 10))));
    } catch { /* chart stays empty */ }
  }
}
