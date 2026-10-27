import { DOCUMENT, DestroyRef, Injectable, inject, signal } from '@angular/core';
import { SwUpdate } from '@angular/service-worker';

/** Chromium's install prompt event (not in the DOM typings). */
interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

/** How often an open dashboard checks for a newer deploy. */
const UPDATE_CHECK_MS = 15 * 60_000;

/**
 * Installable app and service-worker updates. The worker caches only the app shell; data always comes live from the API.
 * A new deploy is announced instead of applied silently, so an open dashboard never swaps code mid-session.
 */
@Injectable({ providedIn: 'root' })
export class PwaService {
  private readonly updates = inject(SwUpdate);
  private readonly window = inject(DOCUMENT).defaultView!;
  private installEvent: BeforeInstallPromptEvent | null = null;

  /** The browser offers installation (Chromium desktop/Android; iOS uses Share → Add to Home Screen). */
  readonly canInstall = signal(false);
  /** iPhone/iPad Safari: no install prompt exists, the user adds it from the Share menu. */
  readonly manualInstall = this.ios() && !(this.window.matchMedia?.('(display-mode: standalone)').matches ?? false);
  /** Running as the installed app rather than in a browser tab. */
  readonly installed = signal(this.window.matchMedia?.('(display-mode: standalone)').matches ?? false);
  /** 'ready': a new version is downloaded; 'broken': the cached version can't be served, reload needed. */
  readonly update = signal<'none' | 'ready' | 'broken'>('none');

  constructor() {
    const destroy = inject(DestroyRef);
    const onPrompt = (e: Event) => {
      e.preventDefault();
      this.installEvent = e as BeforeInstallPromptEvent;
      this.canInstall.set(true);
    };
    const onInstalled = () => {
      this.installEvent = null;
      this.canInstall.set(false);
      this.installed.set(true);
    };
    this.window.addEventListener('beforeinstallprompt', onPrompt);
    this.window.addEventListener('appinstalled', onInstalled);
    destroy.onDestroy(() => {
      this.window.removeEventListener('beforeinstallprompt', onPrompt);
      this.window.removeEventListener('appinstalled', onInstalled);
    });

    if (!this.updates.isEnabled) return;
    const sub = this.updates.versionUpdates.subscribe((e) => {
      if (e.type === 'VERSION_READY') this.update.set('ready');
    });
    const broken = this.updates.unrecoverable.subscribe(() => this.update.set('broken'));
    const timer = setInterval(() => void this.check(), UPDATE_CHECK_MS);
    const onVisible = () => { if (this.window.document.visibilityState === 'visible') void this.check(); };
    this.window.document.addEventListener('visibilitychange', onVisible);
    destroy.onDestroy(() => {
      sub.unsubscribe();
      broken.unsubscribe();
      clearInterval(timer);
      this.window.document.removeEventListener('visibilitychange', onVisible);
    });
  }

  async install(): Promise<void> {
    const e = this.installEvent;
    if (!e) return;
    await e.prompt();
    const { outcome } = await e.userChoice;
    // The event is single-use either way
    this.installEvent = null;
    this.canInstall.set(false);
    if (outcome === 'accepted') this.installed.set(true);
  }

  async reload(): Promise<void> {
    if (this.update() === 'ready') {
      try { await this.updates.activateUpdate(); } catch { /* reload fetches the new version anyway */ }
    }
    this.window.location.reload();
  }

  private ios(): boolean {
    const n = this.window.navigator;
    // iPadOS reports itself as a Mac with touch
    return /iPhone|iPad|iPod/.test(n.userAgent) || (/Macintosh/.test(n.userAgent) && n.maxTouchPoints > 1);
  }

  private async check(): Promise<void> {
    try { await this.updates.checkForUpdate(); } catch { /* offline or server down: next check */ }
  }
}
