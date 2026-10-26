import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import { faArrowRightFromBracket, faNewspaper, faShapes } from '@fortawesome/free-solid-svg-icons';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from '../../core/api';
import { DashboardStore } from '../../core/dashboard-store';
import { MarketBias, TradeRow, TradeStatus } from '../../core/models';
import { PanelService } from '../../core/panels';
import { ToastService } from '../../core/ui-state';
import { InrPipe, IstPipe, PricePipe, pnlClass } from '../../shared/format';
import { ConfirmService } from '../../shared/ui';

export const STATUS_BADGE: Record<TradeStatus, { text: string; cls: string }> = {
  OPEN: { text: 'Open', cls: 'text-bg-primary' },
  TARGET_HIT: { text: 'Target hit', cls: 'text-bg-success' },
  SL_HIT: { text: 'SL hit', cls: 'text-bg-danger' },
  TRAILING_SL_HIT: { text: 'Trailing SL', cls: 'text-bg-warning' },
  TIME_SQUARE_OFF: { text: 'Square-off', cls: 'text-bg-secondary' },
  KILL_SWITCH_EXIT: { text: 'Kill switch', cls: 'text-bg-danger' },
  MANUAL_EXIT: { text: 'Manual exit', cls: 'text-bg-info' },
};

export const BIAS_BADGE: Record<MarketBias, string> = { BULLISH: 'text-bg-success', BEARISH: 'text-bg-danger', NEUTRAL: 'text-bg-secondary' };

@Component({
  selector: 'tb-trade-grid',
  imports: [FormsModule, FaIconComponent, InrPipe, IstPipe, PricePipe],
  template: `
    <section class="tb-panel" aria-label="Today's trades">
      <div class="d-flex align-items-center gap-3 p-3 pb-2 flex-wrap">
        <h2 class="h6 mb-0">Today's trades</h2>
        <span class="badge text-bg-primary">{{ openCount() }} open</span>
        <span class="small text-body-secondary">{{ rows().length }} shown</span>
        <div class="form-check form-switch ms-auto mb-0">
          <input class="form-check-input" type="checkbox" id="tb-open-only" [ngModel]="openOnly()" (ngModelChange)="openOnly.set($event)" />
          <label class="form-check-label small" for="tb-open-only">Open only</label>
        </div>
      </div>
      <div class="table-responsive" style="max-height: 460px">
        <table class="table table-sm table-hover tb-grid mb-0">
          <thead>
            <tr>
              <th>Entry</th><th>Account</th><th>Contract</th><th class="text-end">Qty</th>
              <th class="text-end">Entry ₹</th><th class="text-end">SL</th><th class="text-end">Trail SL</th><th class="text-end">Target</th>
              <th class="text-end">LTP / Exit</th><th style="min-width: 120px">SL → Target</th><th>Signal</th><th>Status</th>
              <th class="text-end">Net PnL</th><th></th>
            </tr>
          </thead>
          <tbody>
            @for (t of rows(); track t.tradeId) {
              <tr class="tb-clickable" (click)="panels.open('trade', t.tradeId)" [attr.data-trade-id]="t.tradeId">
                <td class="num">{{ t.entryTimeUtc | ist }}</td>
                <td class="text-nowrap">{{ t.accountName }} <span class="badge" [class]="t.mode === 'REAL' ? 'text-bg-danger' : 'text-bg-primary'">{{ t.mode }}</span></td>
                <td class="text-nowrap">{{ t.contract }}</td>
                <td class="num text-end">{{ t.quantity }} <span class="text-body-secondary">({{ t.lots }}L)</span></td>
                <td class="num text-end">{{ t.entryPrice | price }}</td>
                <td class="num text-end text-danger">{{ t.stopLoss | price }}</td>
                <td class="num text-end" [class.text-warning]="t.trailingSl > t.stopLoss">{{ t.trailingSl | price }}</td>
                <td class="num text-end text-success">{{ t.target | price }}</td>
                <td class="num text-end fw-semibold">{{ (t.status === 'OPEN' ? t.ltp : t.exitPrice) | price }}</td>
                <td>
                  <div class="tb-bracket" role="progressbar" [attr.aria-valuenow]="t.progressPct" aria-valuemin="0" aria-valuemax="100" [title]="t.progressPct + '% from SL to target'">
                    <div class="tb-fill" [style.width.%]="t.progressPct"></div>
                  </div>
                  <div class="small num text-body-secondary">{{ t.progressPct }}%</div>
                </td>
                <td class="text-nowrap">
                  @if (t.patternName) { <span class="badge text-bg-dark border me-1" [title]="t.patternCode + (t.patternTimeframe ? ' · ' + t.patternTimeframe : '')"><fa-icon [icon]="i.pattern" class="me-1" />{{ t.patternName }}</span> }
                  @if (t.newsSentiment) { <span class="badge" [class]="bias(t.newsSentiment)" [title]="t.newsHeadline ?? ''"><fa-icon [icon]="i.news" class="me-1" />{{ t.newsSentiment }}</span> }
                </td>
                <td><span class="badge" [class]="status(t.status).cls">{{ status(t.status).text }}</span></td>
                <td class="num text-end fw-semibold" [class]="pnlClass(t.netPnl)">{{ t.netPnl | inr: 'signed' }}<div class="small fw-normal">{{ t.netPnlPct }}%</div></td>
                <td>
                  @if (t.status === 'OPEN') {
                    <button type="button" class="btn btn-sm btn-outline-danger py-0" (click)="$event.stopPropagation(); exit(t)" [disabled]="exiting().has(t.tradeId)">
                      <fa-icon [icon]="i.exit" class="me-1" />Exit
                    </button>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="14" class="text-center text-body-secondary py-4">No trades yet today.</td></tr>
            }
          </tbody>
        </table>
      </div>
    </section>
  `,
})
export class TradeGrid {
  protected readonly store = inject(DashboardStore);
  protected readonly panels = inject(PanelService);
  private readonly api = inject(ApiService);
  private readonly confirm = inject(ConfirmService);
  private readonly toasts = inject(ToastService);
  protected readonly i = { exit: faArrowRightFromBracket, news: faNewspaper, pattern: faShapes };
  protected readonly pnlClass = pnlClass;
  protected readonly openOnly = signal(false);
  protected readonly exiting = signal(new Set<number>());

  protected readonly rows = computed(() => {
    const all = this.store.overview()?.trades ?? [];
    return this.openOnly() ? all.filter((t) => t.status === 'OPEN') : all;
  });
  protected readonly openCount = computed(() => this.store.openTrades().length);

  protected status(s: TradeStatus) { return STATUS_BADGE[s]; }
  protected bias(b: MarketBias) { return BIAS_BADGE[b]; }

  protected async exit(t: TradeRow): Promise<void> {
    const ok = await this.confirm.ask({
      title: `Exit ${t.contract}`,
      message: `Cancels the stop-loss and target, then sells ${t.quantity} at a marketable limit on ${t.accountName} (${t.mode}).`,
      confirmLabel: 'Exit now', danger: t.mode === 'REAL',
    });
    if (!ok) return;
    this.exiting.update((s) => new Set(s).add(t.tradeId));
    try {
      await firstValueFrom(this.api.exitTrade(t.tradeId));
      this.toasts.show('info', 'Exit sent', `${t.contract} on ${t.accountName}`);
    } catch (e) {
      this.toasts.show('danger', 'Exit failed', apiErrorText(e), true);
    } finally {
      this.exiting.update((s) => { const n = new Set(s); n.delete(t.tradeId); return n; });
    }
  }
}
