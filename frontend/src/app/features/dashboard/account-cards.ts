import { Component, computed, inject, signal } from '@angular/core';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import {
  faArrowRightArrowLeft, faCircleCheck, faCircleExclamation, faKey, faMobileScreen, faPenToSquare, faPlug, faPowerOff, faRotate, faServer, faSliders,
} from '@fortawesome/free-solid-svg-icons';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from '../../core/api';
import { DashboardStore } from '../../core/dashboard-store';
import { AccountCard, AccountDto, AccountPnl, TokenStatus } from '../../core/models';
import { PanelService } from '../../core/panels';
import { ToastService } from '../../core/ui-state';
import { InrPipe, IstPipe, formatDuration, pnlClass } from '../../shared/format';
import { ConfirmService } from '../../shared/ui';

const TOKEN_BADGE: Record<TokenStatus, { text: string; cls: string }> = {
  VALID: { text: 'Token valid', cls: 'text-bg-success' },
  EXPIRING: { text: 'Token expiring', cls: 'text-bg-warning' },
  EXPIRED: { text: 'Token expired', cls: 'text-bg-danger' },
  FAILED: { text: 'Login failed', cls: 'text-bg-danger' },
  MISSING: { text: 'No token', cls: 'text-bg-danger' },
  PENDING: { text: 'Login pending', cls: 'text-bg-secondary' },
  REJECTED: { text: 'Token rejected', cls: 'text-bg-danger' },
  NOT_REQUIRED: { text: 'No broker login', cls: 'text-bg-secondary' },
};

interface CardView { card: AccountCard; profile: AccountDto | undefined; pnl: AccountPnl | undefined; secondsLeft: number | null; otpSecondsLeft: number | null }

@Component({
  selector: 'tb-account-cards',
  imports: [FaIconComponent, InrPipe, IstPipe],
  template: `
    <div class="row g-3">
      @for (v of views(); track v.card.accountId) {
        <div class="col-12 col-md-6 col-xxl-4">
          <section class="tb-panel p-3 h-100" [attr.aria-label]="v.card.accountName">
            <div class="d-flex align-items-start gap-2 mb-2">
              <div class="flex-grow-1 min-w-0">
                <h2 class="h6 mb-0 text-truncate">{{ v.card.accountName }}</h2>
                <div class="small text-body-secondary text-truncate">{{ v.card.clientId }} · {{ v.card.userName }}
                  @if (v.profile?.email) { · {{ v.profile?.email }} }
                </div>
              </div>
              <span class="badge" [class]="v.card.mode === 'REAL' ? 'text-bg-danger' : 'text-bg-primary'">{{ v.card.mode }}</span>
              @if (v.card.isDataProvider) { <span class="badge text-bg-dark border" title="Market-data provider"><fa-icon [icon]="i.feed" /></span> }
              @if (!v.card.isActive) { <span class="badge text-bg-secondary">Inactive</span> }
            </div>

            @if (v.otpSecondsLeft !== null && v.card.otpPrompt; as p) {
              <form class="alert alert-warning p-2 mb-3" data-testid="otp-prompt" (submit)="$event.preventDefault(); submitOtp(v.card, otpInput.value)">
                <label class="form-label small fw-semibold mb-1" [for]="'tb-otp-' + v.card.accountId">
                  <fa-icon [icon]="i.sms" class="me-1" />Paytm sent an OTP{{ p.sentTo ? ' to ' + p.sentTo : '' }}
                  <span class="fw-normal">· {{ duration(v.otpSecondsLeft) }} left</span>
                </label>
                @if (p.note) { <div class="small mb-1">{{ p.note }}</div> }
                <div class="input-group">
                  <input #otpInput [id]="'tb-otp-' + v.card.accountId" class="form-control num" inputmode="numeric" autocomplete="one-time-code"
                    pattern="[0-9]{4,8}" maxlength="8" placeholder="SMS OTP" required [disabled]="sending() === v.card.accountId" />
                  <button type="submit" class="btn btn-warning" [disabled]="sending() === v.card.accountId">Submit OTP</button>
                </div>
                <div class="small text-body-secondary mt-1">One time only: the bot ticks "trust this device", so later logins use TOTP.</div>
              </form>
            }

            <div class="d-flex align-items-center flex-wrap gap-2 mb-3 small">
              <span class="badge" [class]="token(v.card.tokenStatus).cls">{{ token(v.card.tokenStatus).text }}</span>
              @if (v.secondsLeft !== null && v.card.tokenStatus !== 'NOT_REQUIRED') {
                <span class="num" [title]="'Expires ' + (v.card.tokenExpiryUtc | ist: true)">{{ duration(v.secondsLeft) }} left</span>
              }
              @if (needsLogin(v.card)) {
                <button type="button" class="btn btn-sm btn-outline-warning ms-auto py-0 text-nowrap" (click)="login(v.card)"><fa-icon [icon]="i.retry" class="me-1" />Login now</button>
                <button type="button" class="btn btn-sm btn-outline-secondary py-0 text-nowrap" (click)="panels.open('token', v.card.accountId)"><fa-icon [icon]="i.key" class="me-1" />Paste token</button>
              }
            </div>
            @if (v.card.connection; as c) {
              <div class="small mb-3 text-truncate" data-testid="paytm-connection-line" [class]="c.ok ? (c.clientIdMatches === false ? 'text-warning' : 'text-body-secondary') : 'text-danger'"
                [title]="c.ok ? 'Checked ' + (c.checkedAtUtc | ist) + ' with Paytm user details' : (c.error ?? '')">
                <fa-icon [icon]="c.ok && c.clientIdMatches !== false ? i.ok : i.bad" class="me-1" />
                @if (c.ok) { Paytm: {{ c.name ?? 'connected' }} · {{ c.clientId ?? '—' }}{{ c.clientIdMatches === false ? ' (not this account)' : '' }} }
                @else { Paytm check: {{ c.errorCode }} }
              </div>
            }

            <div class="d-flex align-items-baseline flex-wrap gap-2 mb-2" data-testid="account-balance">
              <div>
                <div class="tb-label">Account balance</div>
                <div class="tb-kpi num">{{ v.card.balance | inr }}</div>
              </div>
              <span class="num small fw-semibold ms-auto" [class]="pnlClass(v.card.balanceChange)">
                {{ v.card.balanceChange | inr: 'signed' }} ({{ v.card.balanceChangePct > 0 ? '+' : '' }}{{ v.card.balanceChangePct }}%)
              </span>
            </div>
            <div class="row g-2 mb-3">
              <div class="col-6"><div class="tb-label">Opening</div><div class="num">{{ v.card.openingBalance | inr: 'whole' }}</div></div>
              <div class="col-6"><div class="tb-label">Available</div><div class="num">{{ v.card.availableMargin | inr: 'whole' }}</div></div>
              <div class="col-6"><div class="tb-label">Utilised</div><div class="num">{{ v.card.utilisedMargin | inr: 'whole' }}</div></div>
              <div class="col-6"><div class="tb-label">Net today</div><div class="num fw-semibold" [class]="pnlClass(v.pnl?.pnl?.net)">{{ v.pnl?.pnl?.net | inr: 'signed' }}</div></div>
              @if (v.card.fundsSyncedAtUtc && v.card.mode === 'REAL') {
                <div class="col-12 small text-body-secondary">Funds from Paytm, synced {{ v.card.fundsSyncedAtUtc | ist }}</div>
              }
            </div>

            @if (v.pnl?.pnl; as p) {
              <div class="d-flex justify-content-between small mb-1">
                <span class="text-body-secondary">Daily loss limit {{ p.maxDailyLoss | inr: 'whole' }}</span>
                <span class="num">{{ p.lossLimitUsedPct }}% used</span>
              </div>
              <div class="progress mb-2" role="progressbar" [attr.aria-valuenow]="p.lossLimitUsedPct" aria-valuemin="0" aria-valuemax="100" style="height: 0.5rem">
                <div class="progress-bar" [class]="lossBar(p.lossLimitUsedPct)" [style.width.%]="p.lossLimitUsedPct"></div>
              </div>
              @if (p.killSwitch) { <div class="alert alert-danger py-1 px-2 small mb-2">Kill switch: {{ p.haltReason }}</div> }
              @else if (p.profitLock) { <div class="alert alert-success py-1 px-2 small mb-2">Profit locked: {{ p.haltReason }}</div> }
            }

            <div class="d-flex flex-wrap gap-1 tb-card-actions">
              <button type="button" class="btn btn-sm btn-outline-secondary" (click)="panels.open('account', v.card.accountId)"><fa-icon [icon]="i.edit" class="me-1" />Profile</button>
              <button type="button" class="btn btn-sm btn-outline-secondary" (click)="panels.open('risk', v.card.accountId)"><fa-icon [icon]="i.risk" class="me-1" />Risk</button>
              <button type="button" class="btn btn-sm btn-outline-secondary" (click)="toggleMode(v.card)"><fa-icon [icon]="i.mode" class="me-1" />{{ v.card.mode === 'REAL' ? 'To PAPER' : 'To REAL' }}</button>
              @if (usesBroker(v)) {
                <button type="button" class="btn btn-sm btn-outline-secondary" (click)="panels.open('token', v.card.accountId)"><fa-icon [icon]="i.paytm" class="me-1" />Paytm</button>
              }
              <button type="button" class="btn btn-sm btn-outline-danger ms-auto" [disabled]="v.pnl?.pnl?.killSwitch" (click)="kill(v.card)"><fa-icon [icon]="i.kill" class="me-1" />Kill</button>
            </div>
          </section>
        </div>
      } @empty {
        <div class="col-12">
          <div class="tb-panel p-4 text-center text-body-secondary">
            No accounts in this view. <button type="button" class="btn btn-link p-0 align-baseline" (click)="panels.open('account')">Add an account</button>
          </div>
        </div>
      }
    </div>
  `,
})
export class AccountCards {
  protected readonly store = inject(DashboardStore);
  protected readonly panels = inject(PanelService);
  private readonly api = inject(ApiService);
  private readonly confirm = inject(ConfirmService);
  private readonly toasts = inject(ToastService);
  protected readonly i = {
    feed: faServer, retry: faRotate, key: faKey, edit: faPenToSquare, risk: faSliders, mode: faArrowRightArrowLeft, kill: faPowerOff, sms: faMobileScreen,
    paytm: faPlug, ok: faCircleCheck, bad: faCircleExclamation,
  };
  protected readonly sending = signal<number | null>(null);
  protected readonly pnlClass = pnlClass;
  protected readonly duration = formatDuration;

  protected readonly views = computed<CardView[]>(() => {
    const o = this.store.overview();
    const now = this.store.now();
    const profiles = this.store.profiles();
    return (o?.accounts ?? []).map((card) => ({
      card,
      profile: profiles.get(card.accountId),
      pnl: o?.perAccount.find((p) => p.accountId === card.accountId),
      secondsLeft: card.tokenExpiryUtc ? Math.max(0, (Date.parse(card.tokenExpiryUtc) - now) / 1000) : null,
      otpSecondsLeft: card.otpPrompt && Date.parse(card.otpPrompt.untilUtc) > now ? (Date.parse(card.otpPrompt.untilUtc) - now) / 1000 : null,
    }));
  });

  protected token(s: TokenStatus) { return TOKEN_BADGE[s]; }

  protected needsLogin(c: AccountCard): boolean {
    return (c.mode === 'REAL' || c.isDataProvider) && ['MISSING', 'FAILED', 'EXPIRED', 'EXPIRING', 'REJECTED'].includes(c.tokenStatus);
  }

  /** Accounts that log in to Paytm (REAL, the market-data provider, or any with an API key saved). */
  protected usesBroker(v: CardView): boolean {
    return v.card.mode === 'REAL' || v.card.isDataProvider || !!v.profile?.hasApiKey;
  }

  protected lossBar(pct: number): string {
    return pct >= 80 ? 'bg-danger' : pct >= 50 ? 'bg-warning' : 'bg-success';
  }

  protected async login(c: AccountCard): Promise<void> {
    try {
      await firstValueFrom(this.api.triggerLogin(c.accountId));
      this.toasts.show('info', 'Login requested', `The auth worker is logging in ${c.accountName}.`);
    } catch (e) {
      this.toasts.show('danger', 'Login request failed', apiErrorText(e));
    }
  }

  protected async submitOtp(c: AccountCard, value: string): Promise<void> {
    const otp = value.replace(/\s/g, '');
    if (!/^\d{4,8}$/.test(otp)) { this.toasts.show('warning', 'Check the OTP', 'Enter the 4 to 8 digit code from the SMS.'); return; }
    this.sending.set(c.accountId);
    try {
      await firstValueFrom(this.api.submitOtp(c.accountId, otp));
      this.toasts.show('info', 'OTP sent', `The auth worker is finishing the Paytm login for ${c.accountName}.`);
      void this.store.refreshOverview();
    } catch (e) {
      this.toasts.show('danger', 'OTP not accepted', apiErrorText(e), true);
    } finally {
      this.sending.set(null);
    }
  }

  protected async toggleMode(c: AccountCard): Promise<void> {
    const toReal = c.mode === 'PAPER';
    const ok = await this.confirm.ask(toReal
      ? {
          title: `Switch ${c.accountName} to REAL`,
          message: 'New signals will place real orders with real money on Paytm Money. The account must have no open trades.',
          confirmLabel: 'Go live', danger: true, typeToConfirm: c.clientId,
        }
      : { title: `Switch ${c.accountName} to PAPER`, message: 'New signals will be simulated. The account must have no open trades.', confirmLabel: 'Switch to PAPER' });
    if (!ok) return;
    try {
      await firstValueFrom(this.api.setMode(c.accountId, toReal ? 'REAL' : 'PAPER', toReal ? c.clientId : null));
      this.toasts.show('warning', 'Mode changed', `${c.accountName} is now ${toReal ? 'REAL' : 'PAPER'}.`);
      void this.store.refreshAccounts();
      void this.store.refreshOverview();
    } catch (e) {
      this.toasts.show('danger', 'Mode change blocked', apiErrorText(e), true);
    }
  }

  protected async kill(c: AccountCard): Promise<void> {
    const ok = await this.confirm.ask({
      title: `Kill switch: ${c.accountName}`,
      message: 'Squares off this account\'s open positions and blocks new entries for the rest of the day.',
      confirmLabel: 'Kill this account', danger: true, typeToConfirm: 'KILL', askReason: true,
    });
    if (!ok) return;
    try {
      await firstValueFrom(this.api.accountKill(c.accountId, ok.reason));
      this.toasts.show('danger', 'Kill switch sent', `${c.accountName} is being squared off.`);
    } catch (e) {
      this.toasts.show('danger', 'Kill switch failed', apiErrorText(e), true);
    }
  }
}
