import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from '../../core/api';
import { PushAlertKinds, PushDevice } from '../../core/models';
import { PanelService } from '../../core/panels';
import { PushService, PushServiceUnreachable } from '../../core/push';
import { ToastService } from '../../core/ui-state';
import { IstPipe } from '../../shared/format';
import { ConfirmService, Drawer } from '../../shared/ui';

const KINDS: { key: keyof PushAlertKinds; label: string; hint: string }[] = [
  { key: 'critical', label: 'Critical', hint: 'Kill switch, stop-loss problems, broker login or feed failures, EXTREME news' },
  { key: 'entry', label: 'Trade entries', hint: 'Each new position with its SL and target' },
  { key: 'exit', label: 'Trade exits', hint: 'Target, stop-loss, trailing, square-off, with net PnL' },
  { key: 'info', label: 'Daily', hint: 'Bot ready, profit lock, end-of-day summary' },
];

/** Web Push: turn system notifications on for this browser or installed app, and manage every subscribed device. */
@Component({
  selector: 'tb-notifications-drawer',
  imports: [Drawer, FormsModule, IstPipe],
  template: `
    <tb-drawer [open]="isOpen()" (closed)="panels.close()" title="Notifications" width="min(640px, 100vw)">
      @if (error()) { <div class="alert alert-danger">{{ error() }}</div> }

      <section class="tb-panel p-3 mb-3" aria-label="This device">
        <h3 class="h6">This device · <span class="text-body-secondary fw-normal">{{ label }}</span></h3>
        @switch (push.availability) {
          @case ('ios-needs-install') {
            <p class="mb-0">On iPhone and iPad, notifications work in the installed app: tap <b>Share → Add to Home Screen</b>, open TradeBot from the home screen, then turn them on here (iOS 16.4 or later).</p>
          }
          @case ('unsupported') { <p class="mb-0">This browser does not support web notifications. Telegram alerts still arrive.</p> }
          @case ('no-service-worker') { <p class="mb-0">Notifications need the production dashboard over HTTPS (the development server has no service worker).</p> }
          @default {
            @if (push.permission() === 'denied') {
              <div class="alert alert-warning mb-2">Notifications are blocked for this site. Allow them in the browser's site settings, then reload.</div>
            }
            <p class="small text-body-secondary">Alerts arrive as system notifications, even when the dashboard is closed. Tapping one opens TradeBot.</p>
            @for (k of kinds; track k.key) {
              <div class="form-check">
                <input class="form-check-input" type="checkbox" [id]="'push-' + k.key" [ngModel]="choice()[k.key]" (ngModelChange)="setKind(k.key, $event)" [disabled]="busy()" />
                <label class="form-check-label" [for]="'push-' + k.key">{{ k.label }} <span class="small text-body-secondary">· {{ k.hint }}</span></label>
              </div>
            }
            <div class="d-flex flex-wrap gap-2 mt-3">
              @if (thisDevice(); as d) {
                <button type="button" class="btn btn-sm btn-outline-primary" [disabled]="busy()" (click)="test(d)">Send test</button>
                <button type="button" class="btn btn-sm btn-outline-danger" [disabled]="busy()" (click)="turnOff(d)">Turn off on this device</button>
              } @else {
                <button type="button" class="btn btn-sm btn-success" [disabled]="busy() || push.permission() === 'denied' || noKinds()" (click)="turnOn()">Turn on notifications</button>
              }
            </div>
          }
        }
      </section>

      <h3 class="tb-label mb-2">Devices receiving alerts</h3>
      <table class="table table-sm tb-grid">
        <thead><tr><th>Device</th><th>Alerts</th><th>Last delivered</th><th></th></tr></thead>
        <tbody>
          @for (d of devices(); track d.id) {
            <tr [attr.data-device-id]="d.id">
              <td>{{ d.deviceLabel }} @if (d.endpoint === push.endpoint()) { <span class="badge text-bg-secondary">this device</span> }</td>
              <td class="small">{{ kindText(d) }}</td>
              <td class="small">
                {{ d.lastSuccessAt ? (d.lastSuccessAt | ist: true) : '—' }}
                @if (d.failureCount > 0) { <div class="text-danger" [title]="d.lastError ?? ''">{{ d.failureCount }} failed · {{ d.lastError }}</div> }
              </td>
              <td class="text-end text-nowrap">
                <button type="button" class="btn btn-sm btn-link py-0" [disabled]="busy()" (click)="test(d)">Test</button>
                <button type="button" class="btn btn-sm btn-link text-danger py-0" [disabled]="busy()" (click)="remove(d)">Remove</button>
              </td>
            </tr>
          } @empty { <tr><td colspan="4" class="text-center text-body-secondary py-3">No devices yet: alerts go to Telegram only.</td></tr> }
        </tbody>
      </table>
      <ng-container footer><button type="button" class="btn btn-outline-secondary" (click)="panels.close()">Close</button></ng-container>
    </tb-drawer>
  `,
})
export class NotificationsDrawer {
  protected readonly panels = inject(PanelService);
  protected readonly push = inject(PushService);
  private readonly api = inject(ApiService);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  protected readonly kinds = KINDS;
  protected readonly label = this.push.deviceLabel();
  protected readonly isOpen = this.panels.isOpen('notifications');
  protected readonly devices = signal<PushDevice[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly thisDevice = computed(() => this.devices().find((d) => d.endpoint === this.push.endpoint()));
  // Choices before this device is subscribed; afterwards its saved kinds
  private readonly draft = signal<PushAlertKinds>({ critical: true, entry: false, exit: false, info: false });
  protected readonly choice = computed(() => this.thisDevice()?.kinds ?? this.draft());
  protected readonly noKinds = computed(() => !Object.values(this.choice()).some(Boolean));

  constructor() {
    effect(() => {
      if (this.isOpen()) untracked(() => void this.reload());
    });
  }

  protected kindText(d: PushDevice): string {
    return KINDS.filter((k) => d.kinds[k.key]).map((k) => k.label).join(', ') || 'None';
  }

  protected async setKind(key: keyof PushAlertKinds, on: boolean): Promise<void> {
    const kinds = { ...this.choice(), [key]: on };
    const d = this.thisDevice();
    if (!d) return this.draft.set(kinds);
    await this.run(async () => {
      const saved = await firstValueFrom(this.api.updatePushKinds(d.id, kinds));
      this.devices.update((all) => all.map((x) => (x.id === saved.id ? saved : x)));
    });
  }

  protected turnOn(): Promise<void> {
    return this.run(async () => {
      await this.push.enable(this.draft());
      await this.reload();
      this.toasts.show('success', 'Notifications on', `${this.label} will receive alerts.`);
    });
  }

  protected turnOff(d: PushDevice): Promise<void> {
    return this.run(async () => {
      await this.push.disable(d);
      await this.reload();
    });
  }

  protected test(d: PushDevice): Promise<void> {
    return this.run(async () => {
      const r = await firstValueFrom(this.api.testPush(d.id));
      if (r.sent) this.toasts.show('info', 'Test sent', `Check ${d.deviceLabel} for a TradeBot notification.`);
      else this.toasts.show('warning', 'Test not delivered', r.removed ? 'The subscription had expired and was removed.' : 'The push service refused it; see the device row.');
      await this.reload();
    });
  }

  protected async remove(d: PushDevice): Promise<void> {
    const ok = await this.confirm.ask({ title: 'Remove device', message: `${d.deviceLabel} will stop receiving notifications.`, confirmLabel: 'Remove', danger: true });
    if (!ok) return;
    await this.run(async () => {
      if (d.endpoint === this.push.endpoint()) await this.push.disable(d);
      else await firstValueFrom(this.api.removePushDevice(d.id));
      await this.reload();
    });
  }

  private async reload(): Promise<void> {
    try {
      this.devices.set(await firstValueFrom(this.api.pushDevices()));
      this.error.set(null);
    } catch (e) {
      this.error.set(apiErrorText(e));
    }
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (e) {
      this.error.set(e instanceof PushServiceUnreachable ? e.message : e instanceof DOMException ? `The browser refused: ${e.message}` : apiErrorText(e));
    } finally {
      this.busy.set(false);
    }
  }
}
