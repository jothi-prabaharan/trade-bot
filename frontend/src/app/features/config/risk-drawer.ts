import { Component, computed, effect, inject, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from '../../core/api';
import { DashboardStore } from '../../core/dashboard-store';
import { PanelService } from '../../core/panels';
import { ToastService } from '../../core/ui-state';
import { FormSession, RulesService, buildRuleForm } from '../../core/validation/forms';
import { FormErrors, RuleField } from '../../shared/rule-field';
import { Drawer } from '../../shared/ui';

@Component({
  selector: 'tb-risk-drawer',
  imports: [Drawer, RuleField, FormErrors],
  template: `
    <tb-drawer [open]="isOpen()" (closed)="panels.close()" [title]="'Risk config · ' + (account()?.accountName ?? '')" width="min(680px, 100vw)">
      @if (session.loadError()) { <div class="alert alert-danger">{{ session.loadError() }}</div> }
      @if (session.form(); as f) {
        <form (submit)="$event.preventDefault(); save()" id="tb-risk-form" novalidate>
          <h3 class="tb-label mb-2">Capital {{ isReal() ? '(funds synced from Paytm)' : '(virtual PAPER funds)' }}</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="allocatedCapital" label="Allocated capital" suffix="₹" />
            <tb-rule-field [form]="f" name="riskPerTradePct" label="Risk per trade" suffix="%" hint="Lots = capital × risk% ÷ (SL points × lot size)" />
            <tb-rule-field [form]="f" name="openingBalance" label="Opening balance" suffix="₹" [readonly]="isReal()" />
            <tb-rule-field [form]="f" name="availableMargin" label="Available margin" suffix="₹" [readonly]="isReal()" />
          </div>
          <h3 class="tb-label mb-2">Premium band &amp; mandatory bracket</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="minSingleTradePrice" label="Min premium" suffix="₹" />
            <tb-rule-field [form]="f" name="maxSingleTradePrice" label="Max premium" suffix="₹" />
            <tb-rule-field [form]="f" name="defaultSlPct" label="Fallback SL" suffix="%" />
            <tb-rule-field [form]="f" name="defaultTargetPct" label="Fallback target" suffix="%" />
            <tb-rule-field [form]="f" name="maxSlPct" label="Max structural SL" suffix="%" />
            <tb-rule-field [form]="f" name="minRiskRewardRatio" label="Min risk : reward" suffix="× risk" />
          </div>
          <h3 class="tb-label mb-2">Trailing stop-loss</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="breakevenTriggerPct" label="Breakeven at" suffix="% to target" />
            <tb-rule-field [form]="f" name="trailingSlStepPct" label="Trail step" suffix="% of move" />
          </div>
          <h3 class="tb-label mb-2">Daily limits &amp; pacing</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="maxDailyLoss" label="Max daily loss" suffix="₹" hint="Hit → square off + kill switch" />
            <tb-rule-field [form]="f" name="maxDailyProfit" label="Daily profit lock" suffix="₹" hint="Hit → no new entries" />
            <tb-rule-field [form]="f" name="maxOpenTrades" label="Max open trades" />
            <tb-rule-field [form]="f" name="maxTradesPerDay" label="Max trades per day" />
            <tb-rule-field [form]="f" name="reentryCooldownMinutes" label="Re-entry cooldown after SL" suffix="min" />
            <tb-rule-field [form]="f" name="maxLotsPerTrade" label="Max lots per trade" />
            <tb-rule-field [form]="f" name="lotMultiplier" label="Lot multiplier" suffix="×" />
          </div>
          <h3 class="tb-label mb-2">Signal filters</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="minPatternConfidence" label="Min pattern confidence" />
            <tb-rule-field [form]="f" name="blockTradeOnOpposingNews" label="Block on opposing HIGH/EXTREME news" />
          </div>
          <h3 class="tb-label mb-2">Execution &amp; simulation</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="slippageBufferPct" label="Entry limit buffer" suffix="%" />
            <tb-rule-field [form]="f" name="simulatedSlippagePct" label="PAPER slippage" suffix="%" />
            <tb-rule-field [form]="f" name="simulatedBrokeragePerOrder" label="PAPER brokerage / order" suffix="₹" />
          </div>
        </form>
      }
      <ng-container footer>
        @if (session.form(); as f) { <tb-form-errors [form]="f" [server]="session.serverErrors()" /> }
        <button type="button" class="btn btn-outline-secondary" (click)="panels.close()">Cancel</button>
        <button type="submit" form="tb-risk-form" class="btn btn-success" [disabled]="session.saving() || !session.form()">{{ session.saving() ? 'Saving…' : 'Save' }}</button>
      </ng-container>
    </tb-drawer>
  `,
})
export class RiskDrawer {
  protected readonly panels = inject(PanelService);
  private readonly api = inject(ApiService);
  private readonly rules = inject(RulesService);
  private readonly store = inject(DashboardStore);
  private readonly toasts = inject(ToastService);
  protected readonly session = new FormSession();

  protected readonly isOpen = this.panels.isOpen('risk');
  private readonly accountId = this.panels.idFor('risk');
  protected readonly account = computed(() => {
    const id = this.accountId();
    return id === null ? null : (this.store.profiles().get(id) ?? null);
  });
  protected readonly isReal = computed(() => this.account()?.tradeMode === 'REAL');

  constructor() {
    effect(() => {
      const id = this.accountId();
      untracked(() => (id !== null ? void this.load(id) : this.session.set(null)));
    });
  }

  private async load(id: number): Promise<void> {
    try {
      const [doc, risk] = await Promise.all([this.rules.load(), firstValueFrom(this.api.riskConfig(id))]);
      if (this.accountId() !== id) return;
      this.session.set(buildRuleForm(doc, 'riskConfig', risk as unknown as Record<string, unknown>, ['version']));
    } catch (e) {
      this.session.loadError.set(apiErrorText(e));
    }
  }

  protected async save(): Promise<void> {
    const id = this.accountId();
    if (id === null) return;
    const saved = await this.session.submit((v) => this.api.updateRiskConfig(id, v));
    if (!saved) return;
    this.toasts.show('success', 'Risk config saved', this.account()?.accountName ?? '');
    void this.store.refreshOverview();
    this.panels.close();
  }
}
