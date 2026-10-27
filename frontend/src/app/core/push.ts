import { DOCUMENT, Injectable, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { SwPush } from '@angular/service-worker';
import { firstValueFrom } from 'rxjs';
import { ApiService } from './api';
import { PushAlertKinds, PushDevice } from './models';

export type PushAvailability = 'ready' | 'no-service-worker' | 'unsupported' | 'ios-needs-install';

/** PushManager.subscribe() never settles when the browser can't reach its push service (FCM, Mozilla, Apple). */
const SUBSCRIBE_TIMEOUT_MS = 30_000;
export class PushServiceUnreachable extends Error {
  constructor() {
    super("The browser could not reach its push service (Google for Chrome/Edge/Android, Mozilla for Firefox, Apple for Safari). Check this device's internet connection or firewall, then try again.");
  }
}

/** This browser's Web Push subscription; the service worker shows the notifications and opens the dashboard on click. */
@Injectable({ providedIn: 'root' })
export class PushService {
  private readonly sw = inject(SwPush);
  private readonly api = inject(ApiService);
  private readonly window = inject(DOCUMENT).defaultView!;

  readonly availability: PushAvailability = this.detect();
  readonly permission = signal<NotificationPermission>(this.window.Notification?.permission ?? 'default');
  /** Endpoint of this browser's current subscription, if any. */
  private readonly subscription = toSignal(this.sw.subscription, { initialValue: null });

  endpoint(): string | null {
    return this.subscription()?.endpoint ?? null;
  }

  /** Asks for permission if needed, subscribes and registers this device with the API. */
  async enable(kinds: PushAlertKinds): Promise<PushDevice> {
    const { publicKey } = await firstValueFrom(this.api.pushConfig());
    try {
      let timer: ReturnType<typeof setTimeout> | undefined;
      const timeout = new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new PushServiceUnreachable()), SUBSCRIBE_TIMEOUT_MS); });
      const sub = await Promise.race([this.sw.requestSubscription({ serverPublicKey: publicKey }), timeout]).finally(() => clearTimeout(timer));
      const keys = sub.toJSON().keys ?? {};
      return await firstValueFrom(this.api.subscribePush({ endpoint: sub.endpoint, p256dh: keys['p256dh'] ?? '', auth: keys['auth'] ?? '', deviceLabel: this.deviceLabel(), kinds }));
    } finally {
      this.permission.set(this.window.Notification?.permission ?? 'default');
    }
  }

  /** Stops notifications here: forgets the device on the server and unsubscribes the browser. */
  async disable(device: PushDevice | undefined): Promise<void> {
    if (device) await firstValueFrom(this.api.removePushDevice(device.id));
    try { await this.sw.unsubscribe(); } catch { /* not subscribed */ }
  }

  deviceLabel(): string {
    const ua = this.window.navigator.userAgent;
    const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
    const os = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Android/.test(ua) ? 'Android'
      : /Windows/.test(ua) ? 'Windows' : /Mac OS X/.test(ua) ? 'Mac' : /Linux/.test(ua) ? 'Linux' : 'device';
    const app = this.window.matchMedia?.('(display-mode: standalone)').matches ? ' (app)' : '';
    return `${browser} on ${os}${app}`;
  }

  private detect(): PushAvailability {
    const w = this.window;
    const ios = /iPhone|iPad/.test(w.navigator.userAgent);
    // iOS only allows Web Push from a home-screen app (16.4+)
    if (ios && !w.matchMedia?.('(display-mode: standalone)').matches) return 'ios-needs-install';
    if (!('PushManager' in w) || !('Notification' in w)) return 'unsupported';
    return this.sw.isEnabled ? 'ready' : 'no-service-worker';
  }
}
