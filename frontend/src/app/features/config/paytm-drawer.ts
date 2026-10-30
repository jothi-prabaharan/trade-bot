import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import { faCircleCheck, faCircleExclamation } from '@fortawesome/free-solid-svg-icons';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from '../../core/api';
import { DashboardStore } from '../../core/dashboard-store';
import { BrokerConnectionCheck, BrokerSetupReport, TotpComparison } from '../../core/models';
import { PanelService } from '../../core/panels';
import { ToastService } from '../../core/ui-state';
import { IstPipe } from '../../shared/format';
import { Drawer } from '../../shared/ui';

/**
 * Paytm connection for one account: the user-details check of today's token, the headless-login setup (credentials as
 * they will be sent, TOTP secret, server clock), a TOTP comparison with the authenticator app, and the manual login.
 */
@Component({
  selector: 'tb-paytm-drawer',
  imports: [Drawer, FaIconComponent, IstPipe],
  template: `
    <tb-drawer [open]="isOpen()" (closed)="panels.close()" [title]="'Paytm connection · ' + name()" width="min(560px, 100vw)">
      <section class="mb-4" aria-label="Connection">
        <div class="d-flex align-items-center gap-2 mb-2">
          <h3 class="tb-label mb-0 flex-grow-1">Connection</h3>
          <button type="button" class="btn btn-sm btn-outline-info" (click)="checkConnection()" [disabled]="busy()">Check connection</button>
        </div>
        @if (connection(); as c) {
          <div class="alert py-2 small mb-0" [class]="connectionCls(c)" data-testid="paytm-connection">
            @if (c.ok) {
              <div><b>Connected</b> as {{ c.name ?? 'unknown' }} · client {{ c.clientId ?? 'unknown' }}</div>
              @if (c.clientIdMatches === false) { <div>This account is set up as client {{ profile()?.clientId }}: check the login ID and API key before trading.</div> }
              @if (c.email || c.phone) { <div class="text-body-secondary">{{ c.email }} {{ c.phone }}</div> }
              @if (c.tokenExpiryUtc) { <div class="text-body-secondary">Token valid until {{ c.tokenExpiryUtc | ist: true }} IST</div> }
            } @else {
              <div><b>{{ c.errorCode }}</b> {{ c.error }}</div>
            }
            <div class="text-body-secondary">Checked {{ c.checkedAtUtc | ist }} with GET /accounts/v1/user/details</div>
          </div>
        } @else {
          <p class="small text-body-secondary mb-0">Asks Paytm for the account details with today's access token: proves the token works and shows whose it is.</p>
        }
      </section>

      <section class="mb-4" aria-label="Headless login setup">
        <h3 class="tb-label mb-2">Headless login setup</h3>
        @if (setup(); as s) {
          <ul class="list-unstyled small mb-2" data-testid="paytm-setup">
            @for (item of s.items; track item.key) {
              <li class="d-flex align-items-start gap-2 py-1 border-bottom">
                <fa-icon [icon]="item.ok ? i.ok : i.bad" [class]="item.ok ? 'text-success' : 'text-danger'" />
                <span class="fw-semibold text-nowrap">{{ item.label }}</span>
                <span class="ms-auto text-end text-body-secondary text-break">{{ item.detail }}</span>
              </li>
            }
            <li class="d-flex align-items-start gap-2 py-1">
              <fa-icon [icon]="s.clockWarning ? i.bad : i.ok" [class]="s.clockWarning ? 'text-danger' : 'text-success'" />
              <span class="fw-semibold text-nowrap">Server clock</span>
              <span class="ms-auto text-end text-body-secondary text-break">{{ s.serverTimeIst }} IST · {{ skewText(s) }}</span>
            </li>
          </ul>
          @if (s.clockWarning) { <div class="alert alert-warning py-2 small">{{ s.clockWarning }}</div> }
        } @else if (setupError()) {
          <div class="alert alert-danger py-2 small">{{ setupError() }}</div>
        } @else {
          <div class="small text-body-secondary">Loading…</div>
        }
        <form class="mt-2" (submit)="$event.preventDefault(); compareTotp(totpInput.value)">
          <label class="form-label small mb-1" for="tb-totp-compare">Code your authenticator app shows for Paytm Money</label>
          <div class="input-group">
            <input #totpInput id="tb-totp-compare" class="form-control num" inputmode="numeric" autocomplete="off" maxlength="7" placeholder="6-digit code" />
            <button type="submit" class="btn btn-outline-info" [disabled]="busy()">Compare</button>
          </div>
          <div class="form-text">Checks the saved TOTP secret and the server clock; the bot's own code is never shown.</div>
          @if (totp(); as t) {
            <div class="alert py-2 small mt-2 mb-0" [class]="t.result === 'MATCH' ? 'alert-success' : 'alert-warning'" data-testid="totp-result">{{ t.message }}</div>
          }
        </form>
      </section>

      <section aria-label="Manual login">
        <h3 class="tb-label mb-2">Manual login</h3>
        <ol class="small ps-3">
          <li>Open the Paytm Money login page and sign in (password, PIN and authenticator code).</li>
          <li>After the redirect, copy the <code>requestToken</code> value from the address bar.</li>
          <li>Paste it below; the API exchanges it for today's tokens and checks them with Paytm.</li>
        </ol>
        <div class="mb-3"><button type="button" class="btn btn-outline-info btn-sm" (click)="openLogin()" [disabled]="busy()">Open Paytm login</button></div>
        <label class="form-label d-block" for="tb-request-token">requestToken</label>
        <input id="tb-request-token" class="form-control mb-2 font-monospace" autocomplete="off" [value]="token()" (input)="token.set($any($event.target).value)" />
        @if (error()) { <div class="alert alert-danger py-2 small">{{ error() }}</div> }
        <p class="small text-body-secondary mb-2">Or ask the auth worker to retry the headless TOTP login now.</p>
        <button type="button" class="btn btn-outline-warning btn-sm" (click)="retryHeadless()" [disabled]="busy()">Retry headless login</button>
      </section>
      <ng-container footer>
        <button type="button" class="btn btn-outline-secondary" (click)="panels.close()">Close</button>
        <button type="button" class="btn btn-success" [disabled]="busy() || !token().trim()" (click)="exchange()">Use token</button>
      </ng-container>
    </tb-drawer>
  `,
})
export class PaytmDrawer {
  protected readonly panels = inject(PanelService);
  private readonly api = inject(ApiService);
  private readonly store = inject(DashboardStore);
  private readonly toasts = inject(ToastService);
  protected readonly i = { ok: faCircleCheck, bad: faCircleExclamation };

  protected readonly isOpen = this.panels.isOpen('token');
  private readonly accountId = this.panels.idFor('token');
  private readonly card = computed(() => {
    const id = this.accountId();
    return this.store.overview()?.accounts.find((a) => a.accountId === id) ?? null;
  });
  protected readonly profile = computed(() => {
    const id = this.accountId();
    return id === null ? null : (this.store.profiles().get(id) ?? null);
  });
  protected readonly name = computed(() => this.profile()?.accountName ?? this.card()?.accountName ?? '');

  protected readonly token = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly setup = signal<BrokerSetupReport | null>(null);
  protected readonly setupError = signal<string | null>(null);
  protected readonly totp = signal<TotpComparison | null>(null);
  private readonly checked = signal<BrokerConnectionCheck | null>(null);
  /** This drawer's own check, else the latest one the server made with today's token. */
  protected readonly connection = computed(() => this.checked() ?? this.card()?.connection ?? null);

  constructor() {
    effect(() => {
      const open = this.isOpen();
      const id = this.accountId();
      untracked(() => {
        this.token.set('');
        this.error.set(null);
        this.totp.set(null);
        this.checked.set(null);
        this.setup.set(null);
        this.setupError.set(null);
        if (open && id !== null) void this.loadSetup(id);
      });
    });
  }

  protected connectionCls(c: BrokerConnectionCheck): string {
    return !c.ok ? 'alert-danger' : c.clientIdMatches === false ? 'alert-warning' : 'alert-success';
  }

  protected skewText(s: BrokerSetupReport): string {
    if (s.clockSkewSeconds === null) return 'Paytm time not available';
    const v = Math.abs(s.clockSkewSeconds);
    return v < 1 ? 'in sync with Paytm' : `${v.toFixed(1)} s ${s.clockSkewSeconds > 0 ? 'behind' : 'ahead of'} Paytm`;
  }

  private async loadSetup(id: number): Promise<void> {
    try {
      this.setup.set(await firstValueFrom(this.api.brokerSetup(id)));
    } catch (e) {
      this.setupError.set(apiErrorText(e));
    }
  }

  protected async checkConnection(): Promise<void> {
    const id = this.accountId();
    if (id === null) return;
    this.busy.set(true);
    try {
      this.checked.set(await firstValueFrom(this.api.checkConnection(id)));
      void this.store.refreshOverview();
    } catch (e) {
      this.toasts.show('danger', 'Connection check failed', apiErrorText(e), true);
    } finally {
      this.busy.set(false);
    }
  }

  protected async compareTotp(code: string): Promise<void> {
    const id = this.accountId();
    if (id === null) return;
    this.busy.set(true);
    try {
      this.totp.set(await firstValueFrom(this.api.totpCheck(id, code)));
    } catch (e) {
      this.toasts.show('danger', 'TOTP check failed', apiErrorText(e), true);
    } finally {
      this.busy.set(false);
    }
  }

  protected async openLogin(): Promise<void> {
    const id = this.accountId();
    if (id === null) return;
    try {
      const { url } = await firstValueFrom(this.api.loginUrl(id));
      window.open(url, '_blank', 'noopener');
    } catch (e) {
      this.error.set(apiErrorText(e));
    }
  }

  protected async exchange(): Promise<void> {
    const id = this.accountId();
    if (id === null) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const r = await firstValueFrom(this.api.exchangeRequestToken(id, this.token().trim()));
      if (r.connection) this.checked.set(r.connection);
      this.toasts.show('success', 'Broker token saved', r.connection?.ok ? `Verified with Paytm as ${r.connection.name ?? 'the account holder'}.` : 'The engine reconnects with the new token.');
      this.token.set('');
      void this.store.refreshOverview();
    } catch (e) {
      this.error.set(apiErrorText(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected async retryHeadless(): Promise<void> {
    const id = this.accountId();
    if (id === null) return;
    try {
      await firstValueFrom(this.api.triggerLogin(id));
      this.toasts.show('info', 'Login requested', 'Watch the account card for the token status.');
    } catch (e) {
      this.error.set(apiErrorText(e));
    }
  }
}
