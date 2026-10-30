import { Component, effect, inject, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from '../../core/api';
import { PanelService } from '../../core/panels';
import { ToastService } from '../../core/ui-state';
import { FormSession, RulesService, buildRuleForm } from '../../core/validation/forms';
import { FormErrors, RuleField, SecretField } from '../../shared/rule-field';
import { Drawer } from '../../shared/ui';

/** Global signal-generation and execution timing (mst.strategy_config). */
@Component({
  selector: 'tb-strategy-drawer',
  imports: [Drawer, RuleField, FormErrors],
  template: `
    <tb-drawer [open]="isOpen()" (closed)="panels.close()" title="Strategy settings" width="min(640px, 100vw)">
      @if (session.loadError()) { <div class="alert alert-danger">{{ session.loadError() }}</div> }
      @if (session.form(); as f) {
        <form (submit)="$event.preventDefault(); save()" id="tb-strategy-form" novalidate>
          <h3 class="tb-label mb-2">Contract selection</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="strikeSelection" label="Strike" />
            <tb-rule-field [form]="f" name="expiryPolicy" label="Expiry policy" />
            <tb-rule-field [form]="f" name="product" label="Product" hint="I = intraday" />
            <tb-rule-field [form]="f" name="requireOptionConfirmation" label="Require option premium confirmation" />
          </div>
          <h3 class="tb-label mb-2">Signals</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="trendTimeframe" label="Trend timeframe" />
            <tb-rule-field [form]="f" name="triggerTimeframe" label="Trigger timeframe" />
            <tb-rule-field [form]="f" name="minCombinedConfidence" label="Min combined confidence" />
            <tb-rule-field [form]="f" name="stockWatchlistSize" label="Daily stock watchlist size" />
            <tb-rule-field [form]="f" name="historyBackfillDays" label="History backfill" suffix="days" />
          </div>
          <h3 class="tb-label mb-2">Session (IST)</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="authLoginTime" label="Auto-login" />
            <tb-rule-field [form]="f" name="authMaxRetries" label="Login retries" />
            <tb-rule-field [form]="f" name="entryStartTime" label="Entries from" />
            <tb-rule-field [form]="f" name="entryCutoffTime" label="Entries until" />
            <tb-rule-field [form]="f" name="squareOffTime" label="Time square-off" />
          </div>
          <h3 class="tb-label mb-2">Order execution</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="slLimitBufferPct" label="SL-L limit below trigger" suffix="%" />
            <tb-rule-field [form]="f" name="slGapFallbackSeconds" label="Gap fallback after" suffix="s" />
            <tb-rule-field [form]="f" name="entryFillTimeoutSeconds" label="Entry fill timeout" suffix="s" />
          </div>
        </form>
      }
      <ng-container footer>
        @if (session.form(); as f) { <tb-form-errors [form]="f" [server]="session.serverErrors()" /> }
        <button type="button" class="btn btn-outline-secondary" (click)="panels.close()">Cancel</button>
        <button type="submit" form="tb-strategy-form" class="btn btn-success" [disabled]="session.saving() || !session.form()">{{ session.saving() ? 'Saving…' : 'Save' }}</button>
      </ng-container>
    </tb-drawer>
  `,
})
export class StrategyDrawer {
  protected readonly panels = inject(PanelService);
  private readonly api = inject(ApiService);
  private readonly rules = inject(RulesService);
  private readonly toasts = inject(ToastService);
  protected readonly session = new FormSession();
  protected readonly isOpen = this.panels.isOpen('strategy');

  constructor() {
    effect(() => {
      const open = this.isOpen();
      untracked(() => (open ? void this.load() : this.session.set(null)));
    });
  }

  private async load(): Promise<void> {
    try {
      const [doc, cfg] = await Promise.all([this.rules.load(), firstValueFrom(this.api.strategy())]);
      this.session.set(buildRuleForm(doc, 'strategyConfig', cfg as unknown as Record<string, unknown>));
    } catch (e) {
      this.session.loadError.set(apiErrorText(e));
    }
  }

  protected async save(): Promise<void> {
    if (!(await this.session.submit((v) => this.api.updateStrategy(v)))) return;
    this.toasts.show('success', 'Strategy saved', 'The engine reloads it immediately.');
    this.panels.close();
  }
}

/** Gemini news intelligence and confidence weights (mst.gemini_news_config). */
@Component({
  selector: 'tb-gemini-drawer',
  imports: [Drawer, RuleField, SecretField, FormErrors],
  template: `
    <tb-drawer [open]="isOpen()" (closed)="panels.close()" title="News intelligence (Gemini)" width="min(640px, 100vw)">
      @if (session.loadError()) { <div class="alert alert-danger">{{ session.loadError() }}</div> }
      @if (session.form(); as f) {
        <form (submit)="$event.preventDefault(); save()" id="tb-gemini-form" novalidate>
          <div class="row g-2 mb-3">
            <tb-secret-field [control]="f.group.controls['geminiApiKey']" label="Gemini API key" [stored]="hasApiKey()" />
            <tb-rule-field [form]="f" name="modelName" label="Model" hint="e.g. gemini-flash-latest" />
            <tb-rule-field [form]="f" name="useSearchGrounding" label="Google Search grounding" />
            <tb-rule-field [form]="f" name="maxCallsPerMinute" label="Max Gemini calls" suffix="/ min" />
          </div>
          <h3 class="tb-label mb-2">Scans</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="premarketScanTime" label="Pre-market scan (IST)" />
            <tb-rule-field [form]="f" name="intradayPollSeconds" label="Intraday poll" suffix="s" />
            <tb-rule-field [form]="f" name="volumeSpikeTriggerMultiplier" label="Volume-spike trigger" suffix="× avg" />
          </div>
          <h3 class="tb-label mb-2">Combined confidence weights <span class="num ms-2" [class]="weightsOk(f.tick()) ? 'text-success' : 'text-danger'">Σ {{ weightSum(f.tick()) }}</span></h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="patternWeightPct" label="Pattern" suffix="%" col="col-4" />
            <tb-rule-field [form]="f" name="newsWeightPct" label="News" suffix="%" col="col-4" />
            <tb-rule-field [form]="f" name="oiGreeksWeightPct" label="OI / Greeks" suffix="%" col="col-4" />
          </div>
          <h3 class="tb-label mb-2">Veto &amp; halts</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="vetoMinImpactLevel" label="Veto opposing news from" />
            <tb-rule-field [form]="f" name="autoPauseOnExtremeMacro" label="Pause all on EXTREME macro news" />
          </div>
        </form>
      }
      <ng-container footer>
        @if (session.form(); as f) { <tb-form-errors [form]="f" [server]="session.serverErrors()" /> }
        <button type="button" class="btn btn-outline-secondary" (click)="panels.close()">Cancel</button>
        <button type="submit" form="tb-gemini-form" class="btn btn-success" [disabled]="session.saving() || !session.form()">{{ session.saving() ? 'Saving…' : 'Save' }}</button>
      </ng-container>
    </tb-drawer>
  `,
})
export class GeminiDrawer {
  protected readonly panels = inject(PanelService);
  private readonly api = inject(ApiService);
  private readonly rules = inject(RulesService);
  private readonly toasts = inject(ToastService);
  protected readonly session = new FormSession();
  protected readonly isOpen = this.panels.isOpen('gemini');
  protected readonly hasApiKey = signal(false);

  constructor() {
    effect(() => {
      const open = this.isOpen();
      untracked(() => (open ? void this.load() : this.session.set(null)));
    });
  }

  protected weightSum(_tick: number): number {
    const v = this.session.form()?.group.getRawValue() ?? {};
    return Math.round((Number(v['patternWeightPct'] ?? 0) + Number(v['newsWeightPct'] ?? 0) + Number(v['oiGreeksWeightPct'] ?? 0)) * 100) / 100;
  }
  protected weightsOk(tick: number): boolean { return this.weightSum(tick) === 100; }

  private async load(): Promise<void> {
    try {
      const doc = await this.rules.load();
      let initial: Record<string, unknown> = { modelName: 'gemini-flash-latest' };
      try {
        const r = await firstValueFrom(this.api.gemini());
        initial = r.config as unknown as Record<string, unknown>;
        this.hasApiKey.set(r.hasApiKey);
      } catch { /* 404 until first save: start from defaults */ }
      this.session.set(buildRuleForm(doc, 'geminiConfig', initial, ['geminiApiKey']));
    } catch (e) {
      this.session.loadError.set(apiErrorText(e));
    }
  }

  protected async save(): Promise<void> {
    const saved = await this.session.submit((v) => {
      const body = { ...v };
      if (!body['geminiApiKey']) delete body['geminiApiKey'];
      return this.api.updateGemini(body);
    });
    if (!saved) return;
    this.hasApiKey.set(saved.hasApiKey);
    this.toasts.show('success', 'News settings saved', saved.hasApiKey ? 'Gemini key is set.' : 'No Gemini key yet: scans are skipped.');
    this.panels.close();
  }
}
