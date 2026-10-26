import { Component, computed, effect, inject, untracked } from '@angular/core';
import { FormControl } from '@angular/forms';
import { ApiService } from '../../core/api';
import { DashboardStore } from '../../core/dashboard-store';
import { PanelService } from '../../core/panels';
import { ToastService } from '../../core/ui-state';
import { FormSession, RulesService, buildRuleForm } from '../../core/validation/forms';
import { FormErrors, RuleField, SecretField } from '../../shared/rule-field';
import { Drawer } from '../../shared/ui';

const SECRETS = ['apiKey', 'apiSecret', 'loginPassword', 'loginPin'] as const;

@Component({
  selector: 'tb-account-drawer',
  imports: [Drawer, RuleField, SecretField, FormErrors],
  template: `
    <tb-drawer [open]="isOpen()" (closed)="panels.close()" [title]="accountId() ? 'Edit account' : 'Add PAPER account'">
      @if (session.loadError()) { <div class="alert alert-danger">{{ session.loadError() }}</div> }
      @if (session.form(); as f) {
        <form (submit)="$event.preventDefault(); save()" id="tb-account-form" novalidate>
          @if (!accountId()) { <p class="small text-body-secondary">New accounts always start in PAPER mode. Switch to REAL from the account card once credentials are set.</p> }
          <h3 class="tb-label mb-2">Profile</h3>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="accountName" label="Account name" />
            <tb-rule-field [form]="f" name="clientId" label="Paytm client ID" />
            <tb-rule-field [form]="f" name="userName" label="Account holder" />
            <tb-rule-field [form]="f" name="email" label="Email" />
            <tb-rule-field [form]="f" name="phone" label="Mobile" />
            <tb-rule-field [form]="f" name="brokerBranch" label="Broker branch" />
          </div>
          <h3 class="tb-label mb-2">Broker API &amp; headless login</h3>
          <div class="row g-2 mb-3">
            <tb-secret-field [control]="secret('apiKey')" label="API key" [stored]="!!profile()?.hasApiKey" />
            <tb-secret-field [control]="secret('apiSecret')" label="API secret" [stored]="!!profile()?.hasApiSecret" />
            <tb-rule-field [form]="f" name="loginId" label="Login ID (mobile / email)" />
            <tb-secret-field [control]="secret('loginPassword')" label="Login password" [stored]="!!profile()?.hasLoginPassword" />
            <tb-secret-field [control]="secret('loginPin')" label="Login PIN" [stored]="!!profile()?.hasLoginPin" />
            <tb-rule-field [form]="f" name="totpSecret" label="TOTP secret (base32)" [secret]="true"
              [placeholder]="profile()?.hasTotpSecret ? 'Stored · leave blank to keep' : 'e.g. JBSWY3DPEHPK3PXP'" />
          </div>
          <div class="row g-2 mb-3">
            <tb-rule-field [form]="f" name="isDataProvider" label="Market-data provider (WebSocket feed)" />
            <tb-rule-field [form]="f" name="isActive" label="Active" />
          </div>
        </form>
      }
      <ng-container footer>
        @if (session.form(); as f) { <tb-form-errors [form]="f" [server]="session.serverErrors()" /> }
        <button type="button" class="btn btn-outline-secondary" (click)="panels.close()">Cancel</button>
        <button type="submit" form="tb-account-form" class="btn btn-success" [disabled]="session.saving() || !session.form()">{{ session.saving() ? 'Saving…' : 'Save' }}</button>
      </ng-container>
    </tb-drawer>
  `,
})
export class AccountDrawer {
  protected readonly panels = inject(PanelService);
  private readonly api = inject(ApiService);
  private readonly rules = inject(RulesService);
  private readonly store = inject(DashboardStore);
  private readonly toasts = inject(ToastService);
  protected readonly session = new FormSession();

  protected readonly isOpen = this.panels.isOpen('account');
  protected readonly accountId = this.panels.idFor('account');
  protected readonly profile = computed(() => {
    const id = this.accountId();
    return id === null ? null : (this.store.profiles().get(id) ?? null);
  });

  constructor() {
    effect(() => {
      const open = this.isOpen();
      const id = this.accountId();
      untracked(() => (open ? void this.load(id) : this.session.set(null)));
    });
  }

  protected secret(key: string): FormControl<unknown> {
    return this.session.form()!.group.controls[key];
  }

  private async load(id: number | null): Promise<void> {
    try {
      const doc = await this.rules.load();
      const p = id === null ? null : this.store.profiles().get(id);
      const initial: Record<string, unknown> = p
        ? { ...p, totpSecret: null, hasApiKey: p.hasApiKey, hasApiSecret: p.hasApiSecret }
        : { isActive: true, isDataProvider: false };
      this.session.set(buildRuleForm(doc, 'tradingAccount', initial, [...SECRETS, 'hasApiKey', 'hasApiSecret', 'version']));
    } catch {
      this.session.loadError.set('Could not load validation rules.');
    }
  }

  protected async save(): Promise<void> {
    const id = this.accountId();
    const saved = await this.session.submit((v) => {
      const body: Record<string, unknown> = {};
      for (const [k, value] of Object.entries(v)) {
        if (k === 'hasApiKey' || k === 'hasApiSecret') continue;
        // Secrets are write-only: send them only when typed
        if (((SECRETS as readonly string[]).includes(k) || k === 'totpSecret') && (value === null || value === '')) continue;
        body[k] = value === '' ? null : value;
      }
      return id === null ? this.api.createAccount(body) : this.api.updateAccount(id, body);
    });
    if (!saved) return;
    this.toasts.show('success', id === null ? 'Account created' : 'Account saved', saved.accountName);
    void this.store.refreshAccounts();
    void this.store.refreshOverview();
    this.panels.close();
  }
}
