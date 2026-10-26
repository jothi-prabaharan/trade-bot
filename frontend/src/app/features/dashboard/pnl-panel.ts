import { Component, computed, inject } from '@angular/core';
import { DashboardStore } from '../../core/dashboard-store';
import { PnlChart } from '../../shared/charts';
import { InrPipe, pnlClass } from '../../shared/format';

@Component({
  selector: 'tb-pnl-panel',
  imports: [InrPipe, PnlChart],
  template: `
    <section class="tb-panel p-3 h-100" aria-label="Profit and loss">
      @if (pnl(); as p) {
        <div class="d-flex flex-wrap align-items-end gap-4 mb-2 pb-2 border-bottom">
          <div>
            <div class="tb-label">Total balance ({{ accountCount() }} {{ accountCount() === 1 ? 'account' : 'accounts' }})</div>
            <div class="tb-kpi num" data-testid="total-balance">{{ totals().balance | inr }}</div>
          </div>
          <div><div class="tb-label">Opening</div><div class="num">{{ totals().opening | inr }}</div></div>
          <div><div class="tb-label">Available margin</div><div class="num">{{ totals().available | inr }}</div></div>
          <div><div class="tb-label">Utilised</div><div class="num">{{ totals().utilised | inr }}</div></div>
        </div>
        <div class="d-flex flex-wrap align-items-end gap-4 mb-3">
          <div>
            <div class="tb-label">Net PnL today</div>
            <div class="tb-kpi num" [class]="pnlClass(p.net)" data-testid="net-pnl">{{ p.net | inr: 'signed' }}</div>
          </div>
          <div><div class="tb-label">Realized</div><div class="num" [class]="pnlClass(p.realizedGross)">{{ p.realizedGross | inr: 'signed' }}</div></div>
          <div><div class="tb-label">Unrealized MTM</div><div class="num" [class]="pnlClass(p.unrealizedMtm)">{{ p.unrealizedMtm | inr: 'signed' }}</div></div>
          <div><div class="tb-label">Charges</div><div class="num">{{ p.charges | inr }}</div></div>
          <div><div class="tb-label">Trades</div><div class="num">{{ p.totalTrades }} <span class="text-body-secondary">({{ p.wins }}W / {{ p.losses }}L)</span></div></div>
          <div><div class="tb-label">Win rate</div><div class="num">{{ p.winRatePct }}%</div></div>
        </div>

        <div class="d-flex justify-content-between small mb-1">
          <span class="text-body-secondary">Daily loss limit used (max {{ p.maxDailyLoss | inr: 'whole' }})</span>
          <span class="num">{{ p.lossLimitUsedPct }}%</span>
        </div>
        <div class="progress mb-3" role="progressbar" aria-label="Daily loss limit used" [attr.aria-valuenow]="p.lossLimitUsedPct" aria-valuemin="0" aria-valuemax="100" style="height: 0.6rem">
          <div class="progress-bar" [class]="p.lossLimitUsedPct >= 80 ? 'bg-danger' : p.lossLimitUsedPct >= 50 ? 'bg-warning' : 'bg-success'" [style.width.%]="p.lossLimitUsedPct"></div>
        </div>
        @if (p.killSwitch) { <div class="alert alert-danger py-1 px-2 small">All accounts in view are halted by their kill switch.</div> }
        @if (p.profitLock) { <div class="alert alert-success py-1 px-2 small">Profit lock reached on at least one account: no new entries there.</div> }
      } @else {
        <div class="placeholder-glow"><span class="placeholder col-6"></span><span class="placeholder col-8"></span></div>
      }

      <div class="tb-label mt-2 mb-1">Intraday PnL</div>
      @if (hasCurve()) {
        <tb-pnl-chart [net]="store.pnlCurve().net" [realized]="store.pnlCurve().realized" [height]="190" />
      } @else {
        <div class="text-body-secondary small py-4 text-center">No PnL movement yet today.</div>
      }
    </section>
  `,
})
export class PnlPanel {
  protected readonly store = inject(DashboardStore);
  protected readonly pnlClass = pnlClass;
  protected readonly pnl = computed(() => this.store.overview()?.consolidated ?? null);
  protected readonly accountCount = computed(() => this.store.overview()?.accounts.length ?? 0);
  protected readonly totals = computed(() => {
    const cards = this.store.overview()?.accounts ?? [];
    const sum = (f: (c: (typeof cards)[number]) => number) => Math.round(cards.reduce((t, c) => t + f(c), 0) * 100) / 100;
    return { balance: sum((c) => c.balance), opening: sum((c) => c.openingBalance), available: sum((c) => c.availableMargin), utilised: sum((c) => c.utilisedMargin) };
  });
  protected readonly hasCurve = computed(() => this.store.pnlCurve().net.length > 1 || this.store.pnlCurve().realized.length > 0);
}
