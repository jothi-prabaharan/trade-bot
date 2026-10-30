import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import { faCircleCheck, faCircleExclamation } from '@fortawesome/free-solid-svg-icons';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from '../../core/api';
import { DashboardStore } from '../../core/dashboard-store';
import { BrokerConnectionCheck, BrokerSetupReport, TotpComparison } from '../../core/models';
import { PanelService } from '../../core/panels';
import { PaytmReturn } from '../../core/paytm-return';
import { ToastService } from '../../core/ui-state';
import { IstPipe } from '../../shared/format';
import { Drawer } from '../../shared/ui';

/**
 * Paytm connection for one account: the user-details check of today's token, the automatic TOTP login (when next, how
 * the last one went, Log in now), the headless-login setup, the bot as the account's authenticator app (Show code), and
 * the manual login as a fallback.
 */
@Component({
  selector: 'tb-paytm-drawer',
  imports: [Drawer, FaIconComponent, IstPipe],
  template: `
    <tb-drawer [open]="isOpen()" (closed)="panels.close()" [title]="'Paytm connection · ' + name()" width="min(560px, 100vw)">
      @if (returnNote(); as note) { <div class="alert alert-info py-2 small" data-testid="paytm-return-note">{{ note }}</div> }

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
            <div class="text-body-secondary">Checked {{ c.checkedAtUtc | ist }}{{ c.errorCode === 'NO_TOKEN' ? '' : ' with GET /accounts/v1/user/details' }}</div>
          </div>
        } @else {
          <p class="small text-body-secondary mb-0">Asks Paytm for the account details with today's access token: proves the token works and shows whose it is.</p>
        }
      </section>

      <section class="mb-4" aria-label="Automatic login">
        <div class="d-flex align-items-center gap-2 mb-2">
          <h3 class="tb-label mb-0 flex-grow-1">Automatic login</h3>
          <button type="button" class="btn btn-sm btn-outline-warning" (click)="loginNow()" [disabled]="busy()">Log in now</button>
        </div>
        @if (setup()?.autoLogin; as auto) {
          <div class="d-flex align-items-start gap-2 small" data-testid="paytm-auto-login">
            <fa-icon [icon]="auto.enabled ? i.ok : i.bad" [class]="auto.enabled ? 'text-success' : 'text-warning'" />
            <div class="text-break">
              <div>{{ auto.note }}</div>
              @if (auto.nextIst) { <div class="text-body-secondary">Next: {{ auto.nextIst }} IST</div> }
              @if (auto.last; as l) {
                <div [class]="l.ok ? 'text-body-secondary' : 'text-danger'" data-testid="paytm-last-login">
                  Last login: {{ l.atIst }} IST · {{ l.method === 'MANUAL' ? 'manual' : 'automatic' }} · {{ l.ok ? 'succeeded' : 'failed' }}{{ !l.ok && l.error ? ': ' + l.error : '' }}
                </div>
              }
            </div>
          </div>
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
      </section>

      <section class="mb-4" aria-label="Authenticator">
        <h3 class="tb-label mb-2">Authenticator</h3>
        <p class="small text-body-secondary mb-2">
          The bot holds this account's TOTP secret and works as its authenticator app: it enters the code itself when it logs in.
          Show the code to finish Paytm's TOTP setup or to log in to Paytm Money yourself.
        </p>
        @if (code(); as c) {
          <div class="d-flex align-items-center gap-3" data-testid="totp-code">
            <span class="font-monospace fs-3 fw-semibold" aria-label="Current TOTP code">{{ c.code.slice(0, 3) }} {{ c.code.slice(3) }}</span>
            <span class="small text-body-secondary text-nowrap">{{ codeLeft() }} s left</span>
            <button type="button" class="btn btn-sm btn-outline-secondary ms-auto" (click)="copyCode(c.code)">Copy</button>
            <button type="button" class="btn btn-sm btn-outline-secondary" (click)="hideCode()">Hide</button>
          </div>
          <div class="progress mt-1" style="height: 4px" role="progressbar" aria-label="Time left for this code" [attr.aria-valuenow]="codeLeft()" aria-valuemin="0" aria-valuemax="30">
            <div class="progress-bar" [style.width.%]="(codeLeft() / 30) * 100"></div>
          </div>
        } @else {
          <button type="button" class="btn btn-sm btn-outline-info" (click)="showCode()" [disabled]="busy() || !hasTotp()">Show code</button>
          @if (setup() && !hasTotp()) { <span class="small text-body-secondary ms-2">Save a valid TOTP secret in the profile first.</span> }
        }
        <form class="mt-3" (submit)="$event.preventDefault(); compareTotp(totpInput.value)">
          <label class="form-label small mb-1" for="tb-totp-compare">Also have this secret in a phone app? Type its code to check both agree</label>
          <div class="input-group">
            <input #totpInput id="tb-totp-compare" class="form-control num" inputmode="numeric" autocomplete="off" maxlength="7" placeholder="6-digit code" />
            <button type="submit" class="btn btn-outline-info" [disabled]="busy()">Compare</button>
          </div>
          <div class="form-text">Checks the saved TOTP secret and the server clock.</div>
          @if (totp(); as t) {
            <div class="alert py-2 small mt-2 mb-0" [class]="t.result === 'MATCH' ? 'alert-success' : 'alert-warning'" data-testid="totp-result">{{ t.message }}</div>
          }
        </form>
      </section>

      <section aria-label="Manual login">
        <h3 class="tb-label mb-2">Manual login</h3>
        <p class="small text-body-secondary mb-2">Only needed when the automatic login cannot run.</p>
        <ol class="small ps-3">
          <li>Open the Paytm Money login page and sign in (password, PIN and the code from Show code above).</li>
          <li>Paytm sends you back to this dashboard, which saves the login by itself.</li>
          <li>If Paytm lands anywhere else, copy the <code>requestToken</code> value from the address bar and paste it below.</li>
        </ol>
        <div class="mb-3"><button type="button" class="btn btn-outline-info btn-sm" (click)="openLogin()" [disabled]="busy()">Open Paytm login</button></div>
        <label class="form-label d-block" for="tb-request-token">requestToken</label>
        <input id="tb-request-token" class="form-control mb-2 font-monospace" autocomplete="off" [value]="token()" (input)="token.set($any($event.target).value)" />
        @if (error()) { <div class="alert alert-danger py-2 small">{{ error() }}</div> }
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
  private readonly paytmReturn = inject(PaytmReturn);
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
  /** Why a requestToken Paytm sent back waits here instead of having been used already. */
  protected readonly returnNote = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly setup = signal<BrokerSetupReport | null>(null);
  protected readonly setupError = signal<string | null>(null);
  protected readonly totp = signal<TotpComparison | null>(null);
  private readonly checked = signal<BrokerConnectionCheck | null>(null);
  /** This drawer's own check, else the latest one the server made with today's token. */
  protected readonly connection = computed(() => this.checked() ?? this.card()?.connection ?? null);
  protected readonly hasTotp = computed(() => this.setup()?.items.find((x) => x.key === 'totpSecret')?.ok === true);

  /** The bot's code on screen (then the next one, once), until `until` (ms since epoch). */
  protected readonly code = signal<{ code: string; next: string | null; until: number } | null>(null);
  private readonly now = signal(Date.now());
  protected readonly codeLeft = computed(() => {
    const c = this.code();
    return c ? Math.max(0, Math.ceil((c.until - this.now()) / 1000)) : 0;
  });
  private ticker: ReturnType<typeof setInterval> | null = null;
  private seen: { id: number | null; status: string | undefined } = { id: null, status: undefined };

  constructor() {
    effect(() => {
      const open = this.isOpen();
      const id = this.accountId();
      untracked(() => {
        const handed = open && id !== null ? this.paytmReturn.take(id) : null;
        this.token.set(handed?.requestToken ?? '');
        this.returnNote.set(handed?.note ?? null);
        this.error.set(null);
        this.totp.set(null);
        this.checked.set(null);
        this.setup.set(null);
        this.setupError.set(null);
        this.hideCode();
        if (open && id !== null) void this.loadSetup(id);
      });
    });
    // A login finishing (the card's token status changes) refreshes the automatic-login status and the connection
    effect(() => {
      const id = this.accountId();
      const status = this.card()?.tokenStatus;
      untracked(() => {
        const prev = this.seen;
        this.seen = { id, status };
        if (id === null || prev.id !== id || prev.status === undefined || prev.status === status || !this.isOpen()) return;
        this.checked.set(null);
        void this.loadSetup(id);
      });
    });
    inject(DestroyRef).onDestroy(() => this.hideCode());
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
      const s = await firstValueFrom(this.api.brokerSetup(id));
      if (this.accountId() === id) this.setup.set(s);
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

  protected async loginNow(): Promise<void> {
    const id = this.accountId();
    if (id === null) return;
    try {
      await firstValueFrom(this.api.triggerLogin(id));
      this.toasts.show('info', 'Login requested', `The bot is logging in ${this.name()} with the TOTP; the result shows here and on the account card.`);
    } catch (e) {
      this.toasts.show('danger', 'Login request failed', apiErrorText(e), true);
    }
  }

  protected async showCode(): Promise<void> {
    const id = this.accountId();
    if (id === null) return;
    this.busy.set(true);
    try {
      const r = await firstValueFrom(this.api.totpCode(id));
      this.now.set(Date.now());
      this.code.set({ code: r.code, next: r.nextCode, until: Date.now() + r.secondsRemaining * 1000 });
      this.ticker ??= setInterval(() => this.tick(), 1000);
    } catch (e) {
      this.toasts.show('danger', 'Could not show the code', apiErrorText(e), true);
    } finally {
      this.busy.set(false);
    }
  }

  /** When a code runs out the next one takes its place for its 30 s, then the code is hidden again. */
  private tick(): void {
    this.now.set(Date.now());
    const c = this.code();
    if (c && this.now() >= c.until) this.code.set(c.next ? { code: c.next, next: null, until: c.until + 30_000 } : null);
    if (!this.code()) this.hideCode();
  }

  protected hideCode(): void {
    if (this.ticker !== null) clearInterval(this.ticker);
    this.ticker = null;
    this.code.set(null);
  }

  protected async copyCode(code: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(code);
      this.toasts.show('success', 'Code copied', 'Paste it into Paytm before the countdown ends.');
    } catch {
      this.toasts.show('warning', 'Copy not available', 'Type the code instead.');
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
      // The nonce comes back in Paytm's state: the dashboard then uses the returning token by itself
      const { url } = await firstValueFrom(this.api.loginUrl(id, this.paytmReturn.begin(id)));
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
      this.returnNote.set(null);
      void this.store.refreshOverview();
      void this.loadSetup(id);
    } catch (e) {
      this.error.set(apiErrorText(e));
    } finally {
      this.busy.set(false);
    }
  }
}
