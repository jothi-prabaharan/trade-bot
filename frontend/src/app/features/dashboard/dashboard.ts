import { Component, computed, inject } from '@angular/core';
import { DashboardStore } from '../../core/dashboard-store';
import { RulesService } from '../../core/validation/forms';
import { AccountDrawer } from '../config/account-drawer';
import { HolidaysDrawer, PatternsDrawer } from '../config/catalog-drawers';
import { NotificationsDrawer } from '../config/notifications-drawer';
import { RiskDrawer } from '../config/risk-drawer';
import { PaytmDrawer } from '../config/paytm-drawer';
import { GeminiDrawer, StrategyDrawer } from '../config/settings-drawers';
import { AccountCards } from './account-cards';
import { HeaderBar } from './header-bar';
import { InfoTabs } from './info-tabs';
import { PnlPanel } from './pnl-panel';
import { TradeDetailDrawer } from './trade-detail';
import { TradeGrid } from './trade-grid';

/** The single unified screen; every editor opens as a drawer over it. */
@Component({
  selector: 'tb-dashboard',
  imports: [
    HeaderBar, AccountCards, PnlPanel, TradeGrid, InfoTabs, TradeDetailDrawer,
    AccountDrawer, RiskDrawer, StrategyDrawer, GeminiDrawer, PatternsDrawer, HolidaysDrawer, PaytmDrawer, NotificationsDrawer,
  ],
  template: `
    <tb-header-bar />
    <main class="container-fluid py-3">
      @if (halt(); as reason) {
        <div class="alert alert-danger d-flex align-items-center gap-2" role="alert"><strong>Trading halted.</strong> {{ reason }}</div>
      }
      <div class="row g-3 mb-3">
        <div class="col-12 col-xl-8"><tb-account-cards /></div>
        <div class="col-12 col-xl-4"><tb-pnl-panel /></div>
      </div>
      <div class="mb-3"><tb-trade-grid /></div>
      <tb-info-tabs />
    </main>
    <tb-trade-detail />
    <tb-account-drawer />
    <tb-risk-drawer />
    <tb-strategy-drawer />
    <tb-gemini-drawer />
    <tb-patterns-drawer />
    <tb-holidays-drawer />
    <tb-paytm-drawer />
    <tb-notifications-drawer />
  `,
})
export class Dashboard {
  private readonly store = inject(DashboardStore);
  protected readonly halt = computed(() => {
    const b = this.store.overview()?.bot;
    return b?.globalKillSwitch ? (b.globalReason ?? 'Global kill switch is on for today.') : null;
  });

  constructor() {
    this.store.start();
    void inject(RulesService).load().catch(() => undefined);
  }
}
