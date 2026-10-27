import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import {
  faBars, faBell, faBellSlash, faCalendarDays, faCircle, faDownload, faMobileScreenButton, faGear, faMoon, faNewspaper, faPowerOff, faPuzzlePiece, faRightFromBracket, faSun,
  faUserPlus,
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
import { ConfirmService, Drawer } from '../../shared/ui';

const SESSION_LABEL: Record<string, { text: string; cls: string }> = {
  OPEN: { text: 'Market open', cls: 'text-bg-success' },
  ENTRY_CLOSED: { text: 'Entries closed', cls: 'text-bg-warning' },
  PRE_OPEN: { text: 'Pre-open', cls: 'text-bg-info' },
  CLOSED: { text: 'Closed', cls: 'text-bg-secondary' },
  HOLIDAY: { text: 'Holiday', cls: 'text-bg-secondary' },
};

@Component({
  selector: 'tb-header-bar',
  imports: [FormsModule, FaIconComponent, IstPipe, Drawer],
  template: `
    <header class="navbar navbar-expand border-bottom px-3 py-2 gap-2 flex-wrap sticky-top bg-body tb-header">
      <span class="navbar-brand fw-semibold me-2 d-flex align-items-center"><img src="favicon.svg" alt="" width="28" height="28" class="me-2" />TradeBot</span>

      @if (bot(); as b) {
        <span class="badge" [class]="session().cls">{{ session().text }}</span>
        <span class="small d-none d-md-inline" [title]="'Last tick ' + (b.lastTickUtc | ist)">
          <fa-icon [icon]="i.dot" [class]="b.feedConnected ? 'text-success' : 'text-danger'" size="xs" /> Feed {{ b.feedConnected ? 'live' : 'down' }}
        </span>
        @if (!b.prepared && b.marketSession !== 'HOLIDAY') { <span class="badge text-bg-warning d-none d-md-inline">Engine preparing</span> }
        <span class="small num text-body-secondary d-none d-lg-inline">{{ b.serverTimeUtc | ist }} IST</span>
      }
      <span class="small d-none d-md-inline" [title]="'Dashboard link: ' + store.link()">
        <fa-icon [icon]="i.dot" size="xs" [class]="linkCls()" /> {{ linkText() }}
      </span>
      <!-- Phones: everything except the scope and the kill switch lives in the menu -->
      <button type="button" class="btn btn-outline-secondary ms-auto d-md-none" aria-label="Menu" (click)="menuOpen.set(true)"><fa-icon [icon]="i.menu" /></button>

      <div class="d-flex align-items-center gap-2 tb-header-actions">
        <label class="visually-hidden" for="tb-scope">Accounts shown</label>
        <select id="tb-scope" class="form-select form-select-sm w-auto" [ngModel]="store.scope()" (ngModelChange)="setScope($event)">
          <option value="all">All accounts</option>
          <option value="real">REAL only</option>
          <option value="paper">PAPER only</option>
          @for (a of store.accounts(); track a.accountId) { <option [value]="'' + a.accountId">{{ a.accountName }}</option> }
        </select>

        <div class="d-none d-md-flex align-items-center gap-2 flex-wrap">
          <div class="btn-group btn-group-sm" role="group" aria-label="Configuration">
            @for (b of configButtons; track b.kind) {
              <button type="button" class="btn btn-outline-secondary" [title]="b.label" [attr.aria-label]="b.label" (click)="panels.open(b.kind)"><fa-icon [icon]="b.icon" /></button>
            }
          </div>
          <button type="button" class="btn btn-sm btn-outline-secondary" [title]="muteLabel()" (click)="toasts.toggleMute()">
            <fa-icon [icon]="toasts.muted() ? i.muted : i.bell" />
          </button>
          @if (pwa.canInstall()) {
            <button type="button" class="btn btn-sm btn-outline-success" title="Install TradeBot as an app" (click)="pwa.install()">
              <fa-icon [icon]="i.install" class="me-1" />Install app
            </button>
          }
          <button type="button" class="btn btn-sm btn-outline-secondary" [title]="themeLabel()" (click)="theme.toggle()">
            <fa-icon [icon]="theme.theme() === 'dark' ? i.sun : i.moon" />
          </button>
        </div>
        <button type="button" class="btn btn-sm btn-danger text-nowrap" [disabled]="bot()?.globalKillSwitch" (click)="killAll()">
          <fa-icon [icon]="i.kill" class="me-1" />{{ bot()?.globalKillSwitch ? 'Halted' : 'Kill switch' }}
        </button>
        <button type="button" class="btn btn-sm btn-outline-secondary d-none d-md-inline-block" title="Sign out" aria-label="Sign out" (click)="auth.logout()"><fa-icon [icon]="i.logout" /></button>
      </div>
    </header>

    <tb-drawer [(open)]="menuOpen" title="Menu" width="min(340px, 90vw)">
      @if (bot(); as b) {
        <div class="d-flex flex-wrap align-items-center gap-2 small mb-3" aria-label="Status">
          <span class="badge" [class]="session().cls">{{ session().text }}</span>
          <span><fa-icon [icon]="i.dot" [class]="b.feedConnected ? 'text-success' : 'text-danger'" size="xs" /> Feed {{ b.feedConnected ? 'live' : 'down' }}</span>
          @if (!b.prepared && b.marketSession !== 'HOLIDAY') { <span class="badge text-bg-warning">Engine preparing</span> }
          <span><fa-icon [icon]="i.dot" size="xs" [class]="linkCls()" /> {{ linkText() }}</span>
          <span class="num text-body-secondary">{{ b.serverTimeUtc | ist }} IST</span>
        </div>
      }
      <div class="tb-label mb-1">Settings</div>
      <div class="list-group mb-3">
        @for (b of configButtons; track b.kind) {
          <button type="button" class="list-group-item list-group-item-action tb-menu-item" (click)="openPanel(b.kind)">
            <fa-icon [icon]="b.icon" class="tb-menu-icon" />{{ b.label }}
          </button>
        }
      </div>
      <div class="tb-label mb-1">App</div>
      <div class="list-group">
        @if (pwa.canInstall()) {
          <button type="button" class="list-group-item list-group-item-action tb-menu-item text-success" (click)="install()">
            <fa-icon [icon]="i.install" class="tb-menu-icon" />Install app
          </button>
        } @else if (pwa.manualInstall) {
          <div class="list-group-item tb-menu-item small">
            <fa-icon [icon]="i.install" class="tb-menu-icon" /><span>Install: tap <b>Share</b>, then <b>Add to Home Screen</b></span>
          </div>
        }
        <button type="button" class="list-group-item list-group-item-action tb-menu-item" (click)="toasts.toggleMute()">
          <fa-icon [icon]="toasts.muted() ? i.muted : i.bell" class="tb-menu-icon" />{{ muteLabel() }}
        </button>
        <button type="button" class="list-group-item list-group-item-action tb-menu-item" (click)="theme.toggle()">
          <fa-icon [icon]="theme.theme() === 'dark' ? i.sun : i.moon" class="tb-menu-icon" />{{ themeLabel() }}
        </button>
      </div>
      <ng-container footer>
        <button type="button" class="btn btn-outline-secondary w-100" (click)="signOut()"><fa-icon [icon]="i.logout" class="me-2" />Sign out</button>
      </ng-container>
    </tb-drawer>
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
    menu: faBars, dot: faCircle, install: faDownload, bell: faBell, muted: faBellSlash, sun: faSun, moon: faMoon, kill: faPowerOff, logout: faRightFromBracket,
  };
  protected readonly configButtons: { kind: PanelKind; label: string; icon: typeof faGear }[] = [
    { kind: 'account', label: 'Add account', icon: faUserPlus },
    { kind: 'strategy', label: 'Strategy settings', icon: faGear },
    { kind: 'gemini', label: 'News intelligence settings', icon: faNewspaper },
    { kind: 'patterns', label: 'Chart patterns', icon: faPuzzlePiece },
    { kind: 'holidays', label: 'Market holidays', icon: faCalendarDays },
    { kind: 'notifications', label: 'Notifications', icon: faMobileScreenButton },
  ];

  protected readonly menuOpen = signal(false);
  protected readonly bot = computed(() => this.store.overview()?.bot ?? null);
  protected readonly linkText = computed(() => ({ live: 'Live', connecting: 'Connecting' })[this.store.link() as string] ?? 'Polling');
  protected readonly linkCls = computed(() => ({ live: 'text-success', connecting: 'text-warning' })[this.store.link() as string] ?? 'text-danger');
  protected readonly muteLabel = computed(() => (this.toasts.muted() ? 'Unmute alerts' : 'Mute alerts'));
  protected readonly themeLabel = computed(() => (this.theme.theme() === 'dark' ? 'Light theme' : 'Dark theme'));
  protected readonly session = computed(() => SESSION_LABEL[this.bot()?.marketSession ?? 'CLOSED']);

  protected setScope(scope: string): void { this.store.scope.set(scope as Scope); }

  protected openPanel(kind: PanelKind): void {
    this.menuOpen.set(false);
    this.panels.open(kind);
  }

  protected install(): void {
    this.menuOpen.set(false);
    void this.pwa.install();
  }

  protected signOut(): void {
    this.menuOpen.set(false);
    this.auth.logout();
  }

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
