import { DOCUMENT, Injectable, effect, inject, signal } from '@angular/core';
import { Severity, SystemEvent } from './models';

function load(key: string, fallback: string): string {
  try { return localStorage.getItem(key) ?? fallback; } catch { return fallback; }
}
function save(key: string, value: string): void {
  try { localStorage.setItem(key, value); } catch { /* storage blocked: keep in memory */ }
}

/** Bootstrap 5.3 colour mode on <html>, dark by default, remembered per browser. */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly html = inject(DOCUMENT).documentElement;
  readonly theme = signal<'dark' | 'light'>(load('tradebot.theme', 'dark') === 'light' ? 'light' : 'dark');

  constructor() {
    effect(() => {
      this.html.setAttribute('data-bs-theme', this.theme());
      // Browser and installed-app title bar follow the colour mode
      this.html.ownerDocument.querySelector('meta[name="theme-color"]')?.setAttribute('content', this.theme() === 'dark' ? '#0d1117' : '#ffffff');
      save('tradebot.theme', this.theme());
    });
  }

  toggle(): void { this.theme.update((t) => (t === 'dark' ? 'light' : 'dark')); }
}

export type ToastTone = 'success' | 'info' | 'warning' | 'danger';
export interface Toast { id: number; tone: ToastTone; title: string; body: string; critical: boolean }

const TOAST_EVENTS: Record<string, { title: string; tone: ToastTone }> = {
  TRADE_OPENED: { title: 'Trade opened', tone: 'success' },
  TRADE_CLOSED: { title: 'Trade closed', tone: 'info' },
  ENTRY_REJECTED: { title: 'Entry rejected', tone: 'warning' },
  ENTRY_NOT_FILLED: { title: 'Entry not filled', tone: 'warning' },
  KILL_SWITCH: { title: 'Kill switch', tone: 'danger' },
  GLOBAL_KILL_SWITCH: { title: 'Global kill switch', tone: 'danger' },
  PROFIT_LOCK: { title: 'Profit locked', tone: 'success' },
  LOGIN_SUCCESS: { title: 'Broker login', tone: 'success' },
  LOGIN_FAILED: { title: 'Broker login failed', tone: 'danger' },
  MODE_CHANGE: { title: 'Mode changed', tone: 'warning' },
  NEWS_EXTREME: { title: 'EXTREME news', tone: 'danger' },
};

/** Toasts for engine events (via the dashboard socket) and UI actions; a short beep for critical ones. */
@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly toasts = signal<Toast[]>([]);
  readonly muted = signal(load('tradebot.muted', 'false') === 'true');
  private nextId = 1;
  private audio: AudioContext | null = null;

  constructor() {
    effect(() => save('tradebot.muted', String(this.muted())));
  }

  show(tone: ToastTone, title: string, body: string, critical = false): void {
    const toast: Toast = { id: this.nextId++, tone, title, body, critical };
    this.toasts.update((list) => [...list.slice(-5), toast]);
    if (critical) this.beep();
    setTimeout(() => this.dismiss(toast.id), critical ? 15000 : 6000);
  }

  dismiss(id: number): void { this.toasts.update((list) => list.filter((t) => t.id !== id)); }

  /** Signal rejections and other routine events stay in the events log only. */
  fromEvents(events: SystemEvent[]): void {
    for (const e of events) {
      const known = TOAST_EVENTS[e.eventType];
      const critical = e.severity === 'CRITICAL';
      if (!known && !critical) continue;
      this.show(known?.tone ?? severityTone(e.severity), known?.title ?? e.eventType.replaceAll('_', ' '), e.message, critical);
    }
  }

  toggleMute(): void { this.muted.update((m) => !m); }

  private beep(): void {
    if (this.muted()) return;
    try {
      this.audio ??= new AudioContext();
      const ctx = this.audio;
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = 'square';
      osc.frequency.setValueAtTime(880, ctx.currentTime);
      osc.frequency.setValueAtTime(660, ctx.currentTime + 0.18);
      gain.gain.setValueAtTime(0.08, ctx.currentTime);
      gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.4);
      osc.connect(gain).connect(ctx.destination);
      osc.start();
      osc.stop(ctx.currentTime + 0.4);
    } catch { /* audio unavailable (autoplay policy, tests) */ }
  }
}

export function severityTone(s: Severity): ToastTone {
  return s === 'CRITICAL' ? 'danger' : s === 'WARNING' ? 'warning' : 'info';
}
