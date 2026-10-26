import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from '../../core/api';
import { ChartPattern, MarketHoliday, PatternCategory, PatternRow } from '../../core/models';
import { PanelService } from '../../core/panels';
import { ToastService } from '../../core/ui-state';
import { FormSession, RulesService, buildRuleForm } from '../../core/validation/forms';
import { FormErrors, RuleField } from '../../shared/rule-field';
import { ConfirmService, Drawer } from '../../shared/ui';

const CATEGORY_LABEL: Record<PatternCategory, string> = {
  CANDLESTICK: 'Candlestick', CLASSICAL: 'Classical', HARMONIC: 'Harmonic', SMC_STRUCTURE: 'SMC / structure',
};

/** mst.chart_patterns: family switches, per-pattern toggles and detector parameters. */
@Component({
  selector: 'tb-patterns-drawer',
  imports: [Drawer, FormsModule, RuleField, FormErrors],
  template: `
    <tb-drawer [open]="isOpen()" (closed)="panels.close()" title="Chart patterns" width="min(860px, 100vw)">
      @if (error()) { <div class="alert alert-danger">{{ error() }}</div> }

      @if (session.form(); as f) {
        <form class="tb-panel p-3 mb-3" (submit)="$event.preventDefault(); saveEdit()" novalidate>
          <div class="d-flex mb-2"><h3 class="h6 mb-0">{{ editing()?.patternCode }}</h3>
            <button type="button" class="btn-close ms-auto" aria-label="Close editor" (click)="cancelEdit()"></button></div>
          <div class="row g-2 mb-2">
            <tb-rule-field [form]="f" name="patternName" label="Name" />
            <tb-rule-field [form]="f" name="defaultWeight" label="Base confidence weight" col="col-6 col-sm-3" />
            <tb-rule-field [form]="f" name="minCandlesRequired" label="Min candles" col="col-6 col-sm-3" />
            <tb-rule-field [form]="f" name="requireVolumeConfirm" label="Require volume confirmation" />
            <tb-rule-field [form]="f" name="requireBreakoutClose" label="Require breakout close" />
            <tb-rule-field [form]="f" name="isActive" label="Active" />
            <tb-rule-field [form]="f" name="patternParameters" label="Detector parameters (JSON)" col="col-12" />
          </div>
          <tb-form-errors [form]="f" [server]="session.serverErrors()" />
          <div class="d-flex justify-content-end gap-2 mt-2">
            <button type="button" class="btn btn-sm btn-outline-secondary" (click)="cancelEdit()">Cancel</button>
            <button type="submit" class="btn btn-sm btn-success" [disabled]="session.saving()">Save pattern</button>
          </div>
        </form>
      }

      @for (g of groups(); track g.category) {
        <div class="d-flex align-items-center mt-3 mb-1 gap-2">
          <h3 class="h6 mb-0">{{ label(g.category) }}</h3>
          <span class="small text-body-secondary">{{ g.active }}/{{ g.rows.length }} active</span>
          <div class="btn-group btn-group-sm ms-auto">
            <button type="button" class="btn btn-outline-success" (click)="toggleCategory(g.category, true)">All on</button>
            <button type="button" class="btn btn-outline-secondary" (click)="toggleCategory(g.category, false)">All off</button>
          </div>
        </div>
        <div class="table-responsive">
          <table class="table table-sm tb-grid mb-0">
            <thead><tr><th>On</th><th>Pattern</th><th>Code</th><th>Direction</th><th class="text-end">Weight</th><th>Vol.</th><th>Close</th><th></th></tr></thead>
            <tbody>
              @for (r of g.rows; track r.pattern.patternId) {
                <tr [class.text-body-secondary]="!r.pattern.isActive">
                  <td><div class="form-check form-switch mb-0">
                    <input class="form-check-input" type="checkbox" [attr.aria-label]="'Toggle ' + r.pattern.patternName" [checked]="r.pattern.isActive" (change)="toggle(r.pattern)" />
                  </div></td>
                  <td>{{ r.pattern.patternName }} @if (!r.hasDetector) { <span class="badge text-bg-warning ms-1" title="No detector in this build: never fires">no detector</span> }</td>
                  <td class="small font-monospace">{{ r.pattern.patternCode }}</td>
                  <td class="small">{{ r.pattern.direction }}</td>
                  <td class="num text-end">{{ r.pattern.defaultWeight }}</td>
                  <td>{{ r.pattern.requireVolumeConfirm ? '✓' : '' }}</td>
                  <td>{{ r.pattern.requireBreakoutClose ? '✓' : '' }}</td>
                  <td><button type="button" class="btn btn-sm btn-link py-0" (click)="edit(r.pattern)">Edit</button></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @empty { <p class="text-body-secondary">No patterns yet: the engine seeds them at pre-open.</p> }
      <ng-container footer><button type="button" class="btn btn-outline-secondary" (click)="panels.close()">Close</button></ng-container>
    </tb-drawer>
  `,
})
export class PatternsDrawer {
  protected readonly panels = inject(PanelService);
  private readonly api = inject(ApiService);
  private readonly rules = inject(RulesService);
  private readonly toasts = inject(ToastService);
  protected readonly isOpen = this.panels.isOpen('patterns');
  protected readonly rows = signal<PatternRow[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly editing = signal<ChartPattern | null>(null);
  protected readonly session = new FormSession();

  protected readonly groups = computed(() => {
    const order: PatternCategory[] = ['CANDLESTICK', 'CLASSICAL', 'HARMONIC', 'SMC_STRUCTURE'];
    return order.map((category) => {
      const rows = this.rows().filter((r) => r.pattern.category === category).sort((a, b) => a.pattern.patternName.localeCompare(b.pattern.patternName));
      return { category, rows, active: rows.filter((r) => r.pattern.isActive).length };
    }).filter((g) => g.rows.length);
  });

  constructor() {
    effect(() => {
      const open = this.isOpen();
      untracked(() => {
        this.cancelEdit();
        if (open) void this.reload();
      });
    });
  }

  protected label(c: PatternCategory): string { return CATEGORY_LABEL[c]; }

  private async reload(): Promise<void> {
    try {
      this.rows.set(await firstValueFrom(this.api.patterns()));
      this.error.set(null);
    } catch (e) {
      this.error.set(apiErrorText(e));
    }
  }

  protected async toggle(p: ChartPattern): Promise<void> {
    try {
      const saved = await firstValueFrom(this.api.updatePattern(p.patternId, { isActive: !p.isActive }));
      this.rows.update((rows) => rows.map((r) => (r.pattern.patternId === p.patternId ? { ...r, pattern: saved } : r)));
    } catch (e) {
      this.toasts.show('danger', 'Pattern not updated', apiErrorText(e));
      await this.reload();
    }
  }

  protected async toggleCategory(c: PatternCategory, active: boolean): Promise<void> {
    try {
      const r = await firstValueFrom(this.api.togglePatternCategory(c, active));
      this.toasts.show('success', `${this.label(c)} ${active ? 'enabled' : 'disabled'}`, `${r.updated} pattern(s) changed.`);
      await this.reload();
    } catch (e) {
      this.toasts.show('danger', 'Category not updated', apiErrorText(e));
    }
  }

  protected async edit(p: ChartPattern): Promise<void> {
    const doc = await this.rules.load();
    this.editing.set(p);
    this.session.set(buildRuleForm(doc, 'chartPattern', p as unknown as Record<string, unknown>));
  }

  protected cancelEdit(): void {
    this.editing.set(null);
    this.session.set(null);
  }

  protected async saveEdit(): Promise<void> {
    const p = this.editing();
    if (!p) return;
    const saved = await this.session.submit((v) => this.api.updatePattern(p.patternId, v));
    if (!saved) return;
    this.rows.update((rows) => rows.map((r) => (r.pattern.patternId === saved.patternId ? { ...r, pattern: saved } : r)));
    this.toasts.show('success', 'Pattern saved', saved.patternName);
    this.cancelEdit();
  }
}

/** mst.market_holidays: closures and special sessions (e.g. Muhurat). */
@Component({
  selector: 'tb-holidays-drawer',
  imports: [Drawer, RuleField, FormErrors],
  template: `
    <tb-drawer [open]="isOpen()" (closed)="panels.close()" title="Market holidays" width="min(640px, 100vw)">
      @if (error()) { <div class="alert alert-danger">{{ error() }}</div> }
      @if (session.form(); as f) {
        <form class="tb-panel p-3 mb-3" (submit)="$event.preventDefault(); save()" novalidate>
          <h3 class="h6">{{ editingId() ? 'Edit holiday' : 'Add holiday' }}</h3>
          <div class="row g-2 mb-2">
            <tb-rule-field [form]="f" name="holidayDate" label="Date" />
            <tb-rule-field [form]="f" name="exchange" label="Exchange" />
            <tb-rule-field [form]="f" name="description" label="Description" col="col-12" />
            <tb-rule-field [form]="f" name="isSpecialSession" label="Special session (market opens briefly)" col="col-12" />
            <tb-rule-field [form]="f" name="sessionOpenTime" label="Session opens" />
            <tb-rule-field [form]="f" name="sessionCloseTime" label="Session closes" />
          </div>
          <tb-form-errors [form]="f" [server]="session.serverErrors()" />
          <div class="d-flex justify-content-end gap-2 mt-2">
            <button type="button" class="btn btn-sm btn-outline-secondary" (click)="startNew()">Clear</button>
            <button type="submit" class="btn btn-sm btn-success" [disabled]="session.saving()">{{ editingId() ? 'Save' : 'Add' }}</button>
          </div>
        </form>
      }
      <table class="table table-sm tb-grid">
        <thead><tr><th>Date</th><th>Exch.</th><th>Description</th><th>Session</th><th></th></tr></thead>
        <tbody>
          @for (h of holidays(); track h.holidayId) {
            <tr>
              <td class="num">{{ h.holidayDate }}</td><td>{{ h.exchange }}</td><td>{{ h.description }}</td>
              <td class="small">{{ h.isSpecialSession ? h.sessionOpenTime + '–' + h.sessionCloseTime : 'Closed' }}</td>
              <td class="text-end text-nowrap">
                <button type="button" class="btn btn-sm btn-link py-0" (click)="edit(h)">Edit</button>
                <button type="button" class="btn btn-sm btn-link text-danger py-0" (click)="remove(h)">Delete</button>
              </td>
            </tr>
          } @empty { <tr><td colspan="5" class="text-center text-body-secondary py-3">No holidays configured: only weekends are closed.</td></tr> }
        </tbody>
      </table>
      <ng-container footer><button type="button" class="btn btn-outline-secondary" (click)="panels.close()">Close</button></ng-container>
    </tb-drawer>
  `,
})
export class HolidaysDrawer {
  protected readonly panels = inject(PanelService);
  private readonly api = inject(ApiService);
  private readonly rules = inject(RulesService);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  protected readonly isOpen = this.panels.isOpen('holidays');
  protected readonly holidays = signal<MarketHoliday[]>([]);
  protected readonly editingId = signal<number | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly session = new FormSession();

  constructor() {
    effect(() => {
      const open = this.isOpen();
      untracked(() => {
        if (!open) return this.session.set(null);
        void this.reload();
        void this.startNew();
      });
    });
  }

  private async reload(): Promise<void> {
    try {
      this.holidays.set((await firstValueFrom(this.api.holidays())).sort((a, b) => a.holidayDate.localeCompare(b.holidayDate)));
    } catch (e) {
      this.error.set(apiErrorText(e));
    }
  }

  protected async startNew(): Promise<void> {
    const doc = await this.rules.load();
    this.editingId.set(null);
    this.session.set(buildRuleForm(doc, 'marketHoliday', { exchange: 'NSE', isSpecialSession: false }));
  }

  protected async edit(h: MarketHoliday): Promise<void> {
    const doc = await this.rules.load();
    this.editingId.set(h.holidayId);
    this.session.set(buildRuleForm(doc, 'marketHoliday', h as unknown as Record<string, unknown>));
  }

  protected async save(): Promise<void> {
    const id = this.editingId();
    const saved = await this.session.submit((v) => {
      const body = { ...v };
      for (const k of ['sessionOpenTime', 'sessionCloseTime']) if (body[k] === '') body[k] = null;
      return id === null ? this.api.createHoliday(body) : this.api.updateHoliday(id, body);
    });
    if (!saved) return;
    this.toasts.show('success', 'Holiday saved', `${saved.holidayDate} · ${saved.description}`);
    await this.reload();
    await this.startNew();
  }

  protected async remove(h: MarketHoliday): Promise<void> {
    const ok = await this.confirm.ask({ title: 'Delete holiday', message: `${h.holidayDate} · ${h.description}`, confirmLabel: 'Delete', danger: true });
    if (!ok) return;
    try {
      await firstValueFrom(this.api.deleteHoliday(h.holidayId));
      await this.reload();
    } catch (e) {
      this.toasts.show('danger', 'Delete failed', apiErrorText(e));
    }
  }
}
