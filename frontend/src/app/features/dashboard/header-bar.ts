import { Component, computed, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import {
  faBell, faBellSlash, faCalendarDays, faCircle, faDownload, faMobileScreenButton, faGear, faMoon, faNewspaper, faPowerOff, faPuzzlePiece, faRightFromBracket, faSun, faUserPlus,
} from '@fortawesome/free-solid-svg-icons';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from '../../core/api';
import { AuthService } from '../../core/auth';
import { DashboardStore } from '../../core/dashboard-store';
import { Scope } from '../../core/models';
import { PanelKind, PanelService } from '../../core/panels';
import { PwaService } from '../../core/pwa';
import { ThemeService, ToastService } from '../../core/ui-state';
import { IstPipe } from '../../shared/format';
import { ConfirmService } from '../../shared/ui';

const SESSION_LABEL: Record<string, { text: string; cls: string }> = {
  OPEN: { text: 'Market open', cls: 'text-bg-success' },
  ENTRY_CLOSED: { text: 'Entries closed', cls: 'text-bg-warning' },
  PRE_OPEN: { text: 'Pre-open', cls: 'text-bg-info' },
  CLOSED: { text: 'Closed', cls: 'text-bg-secondary' },
  HOLIDAY: { text: 'Holiday', cls: 'text-bg-secondary' },
};

@Component({
  selector: 'tb-header-bar',
  imports: [FormsModule, FaIconComponent, IstPipe],
  template: `
    <header class="navbar navbar-expand border-bottom px-3 py-2 gap-2 flex-wrap sticky-top bg-body">
      <span class="navbar-brand fw-semibold me-2 d-flex align-items-center"><img src="favicon.svg" alt="" width="28" height="28" class="me-2" />TradeBot</span>

      @if (bot(); as b) {
        <span class="badge" [class]="session().cls">{{ session().text }}</span>
        <span class="small d-none d-md-inline" [title]="'Last tick ' + (b.lastTickUtc | ist)">
          <fa-icon [icon]="i.dot" [class]="b.feedConnected ? 'text-success' : 'text-danger'" size="xs" /> Feed {{ b.feedConnected ? 'live' : 'down' }}
        </span>
        @if (!b.prepared && b.marketSession !== 'HOLIDAY') { <span class="badge text-bg-warning d-none d-md-inline">Engine preparing</span> }
        <span class="small num text-body-secondary d-none d-lg-inline">{{ b.serverTimeUtc | ist }} IST</span>
      }
      <span class="small d-none d-sm-inline" [title]="'Dashboard link: ' + store.link()">
        <fa-icon [icon]="i.dot" size="xs" [class]="store.link() === 'live' ? 'text-success' : store.link() === 'connecting' ? 'text-warning' : 'text-danger'" />
        {{ store.link() === 'live' ? 'Live' : store.link() === 'connecting' ? 'Connecting' : 'Polling' }}
      </span>

      <div class="ms-auto d-flex align-items-center gap-2 flex-wrap">
        <label class="visually-hidden" for="tb-scope">Accounts shown</label>
        <select id="tb-scope" class="form-select form-select-sm w-auto" [ngModel]="store.scope()" (ngModelChange)="setScope($event)">
          <option value="all">All accounts</option>
          <option value="real">REAL only</option>
          <option value="paper">PAPER only</option>
          @for (a of store.accounts(); track a.accountId) { <option [value]="'' + a.accountId">{{ a.accountName }}</option> }
        </select>

        <div class="btn-group btn-group-sm" role="group" aria-label="Configuration">
          @for (b of configButtons; track b.kind) {
            <button type="button" class="btn btn-outline-secondary" [title]="b.label" [attr.aria-label]="b.label" (click)="panels.open(b.kind)"><fa-icon [icon]="b.icon" /></button>
          }
        </div>

        <button type="button" class="btn btn-sm btn-outline-secondary" [title]="toasts.muted() ? 'Unmute alerts' : 'Mute alerts'" (click)="toasts.toggleMute()">
          <fa-icon [icon]="toasts.muted() ? i.muted : i.bell" />
        </button>
        @if (pwa.canInstall()) {
          <button type="button" class="btn btn-sm btn-outline-success" title="Install TradeBot as an app" (click)="pwa.install()">
            <fa-icon [icon]="i.install" /><span class="d-none d-md-inline ms-1">Install app</span>
          </button>
        }
        <button type="button" class="btn btn-sm btn-outline-secondary" [title]="theme.theme() === 'dark' ? 'Light theme' : 'Dark theme'" (click)="theme.toggle()">
          <fa-icon [icon]="theme.theme() === 'dark' ? i.sun : i.moon" />
        </button>
        <button type="button" class="btn btn-sm btn-danger" [disabled]="bot()?.globalKillSwitch" (click)="killAll()">
          <fa-icon [icon]="i.kill" class="me-1" />{{ bot()?.globalKillSwitch ? 'Halted' : 'Kill switch' }}
        </button>
        <button type="button" class="btn btn-sm btn-outline-secondary" title="Sign out" aria-label="Sign out" (click)="auth.logout()"><fa-icon [icon]="i.logout" /></button>
      </div>
    </header>
  `,
})
export class HeaderBar {
  protected readonly store = inject(DashboardStore);
  protected readonly panels = inject(PanelService);
  protected readonly theme = inject(ThemeService);
  protected readonly toasts = inject(ToastService);
  protected readonly auth = inject(AuthService);
  protected readonly pwa = inject(PwaService);
  private readonly api = inject(ApiService);
  private readonly confirm = inject(ConfirmService);

  protected readonly i = {
    dot: faCircle, install: faDownload, bell: faBell, muted: faBellSlash, sun: faSun, moon: faMoon, kill: faPowerOff, logout: faRightFromBracket,
  };
  protected readonly configButtons: { kind: PanelKind; label: string; icon: typeof faGear }[] = [
    { kind: 'account', label: 'Add account', icon: faUserPlus },
    { kind: 'strategy', label: 'Strategy settings', icon: faGear },
    { kind: 'gemini', label: 'News intelligence settings', icon: faNewspaper },
    { kind: 'patterns', label: 'Chart patterns', icon: faPuzzlePiece },
    { kind: 'holidays', label: 'Market holidays', icon: faCalendarDays },
    { kind: 'notifications', label: 'Notifications', icon: faMobileScreenButton },
  ];

  protected readonly bot = computed(() => this.store.overview()?.bot ?? null);
  protected readonly session = computed(() => SESSION_LABEL[this.bot()?.marketSession ?? 'CLOSED']);

  protected setScope(scope: string): void { this.store.scope.set(scope as Scope); }

  protected async killAll(): Promise<void> {
    const ok = await this.confirm.ask({
      title: 'Global kill switch',
      message: 'Squares off every open position on every account and halts all new entries for the rest of the day.',
      confirmLabel: 'Kill all trading', danger: true, typeToConfirm: 'KILL', askReason: true,
    });
    if (!ok) return;
    try {
      await firstValueFrom(this.api.globalKill(ok.reason));
      this.toasts.show('danger', 'Kill switch sent', 'All accounts are being squared off.');
      void this.store.refreshOverview();
    } catch (e) {
      this.toasts.show('danger', 'Kill switch failed', apiErrorText(e), true);
    }
  }
}
