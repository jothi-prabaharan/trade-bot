import { DOCUMENT, DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiService } from './api';
import { AuthService } from './auth';
import { AccountDto, DashboardOverview, PnlCurve, Scope, SocketMessage, SystemEvent } from './models';
import { ToastService } from './ui-state';

export type LinkState = 'connecting' | 'live' | 'offline';

const SCOPE_KEY = 'tradebot.scope';

/**
 * Single source of live dashboard state. The /ws/dashboard socket pushes the overview (≤ 1/s) and new
 * system events; while it is down the overview is polled over REST every 5 s and the socket retries
 * with back-off.
 */
@Injectable({ providedIn: 'root' })
export class DashboardStore {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly toasts = inject(ToastService);
  private readonly location = inject(DOCUMENT).location;

  readonly scope = signal<Scope>(DashboardStore.loadScope());
  readonly overview = signal<DashboardOverview | null>(null);
  readonly link = signal<LinkState>('offline');
  readonly events = signal<SystemEvent[]>([]);
  readonly accounts = signal<AccountDto[]>([]);
  readonly pnlCurve = signal<PnlCurve>({ realized: [], net: [] });
  /** Ticks every second; drives countdowns between pushes. */
  readonly now = signal(Date.now());

  readonly openTrades = computed(() => this.overview()?.trades.filter((t) => t.status === 'OPEN') ?? []);
  readonly profiles = computed(() => new Map(this.accounts().map((a) => [a.accountId, a])));

  private socket: WebSocket | null = null;
  private retryMs = 1000;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private clockTimer: ReturnType<typeof setInterval> | null = null;
  private lastTradeSignature = '';
  private started = false;

  constructor() {
    effect(() => {
      const scope = this.scope();
      try { localStorage.setItem(SCOPE_KEY, scope); } catch { /* ignore */ }
      untracked(() => {
        if (!this.started) return;
        // A live socket pushes the new scope within a second; otherwise fetch it now rather than at the next poll
        if (this.socket?.readyState === WebSocket.OPEN) this.socket.send(JSON.stringify({ scope }));
        else void this.refreshOverview();
        void this.refreshCurve();
      });
    });
    effect(() => {
      if (!this.auth.isLoggedIn()) untracked(() => this.stop());
    });
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  start(): void {
    if (this.started) return;
    this.started = true;
    this.clockTimer = setInterval(() => this.now.set(Date.now()), 1000);
    this.pollTimer = setInterval(() => {
      if (this.link() !== 'live') void this.refreshOverview();
      void this.refreshCurve();
    }, 5000);
    void this.refreshOverview();
    void this.refreshAccounts();
    void this.refreshCurve();
    void this.loadEvents();
    this.connect();
  }

  stop(): void {
    this.started = false;
    for (const t of [this.pollTimer, this.clockTimer]) if (t) clearInterval(t);
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.pollTimer = this.clockTimer = this.retryTimer = null;
    const s = this.socket;
    this.socket = null;
    s?.close(1000, 'logout');
    this.link.set('offline');
  }

  async refreshOverview(): Promise<void> {
    const scope = this.scope();
    try {
      const o = await firstValueFrom(this.api.overview(scope));
      // The selection changed while this was loading: its own request (or push) brings the right data
      if (scope === this.scope()) this.applyOverview(o);
    } catch { /* socket or next poll will retry */ }
  }

  async refreshAccounts(): Promise<void> {
    try {
      const accounts = await firstValueFrom(this.api.accounts());
      this.accounts.set(accounts);
      // A remembered single-account selection whose account is gone would show an empty dashboard under "All accounts"
      const scope = this.scope();
      if (/^\d+$/.test(scope) && !accounts.some((a) => String(a.accountId) === scope)) this.scope.set('all');
    } catch { /* keep last */ }
  }

  async refreshCurve(): Promise<void> {
    try { this.pnlCurve.set(await firstValueFrom(this.api.pnlCurve(this.scope()))); } catch { /* keep last */ }
  }

  private async loadEvents(): Promise<void> {
    try { this.events.set(await firstValueFrom(this.api.events(100))); } catch { /* keep last */ }
  }

  private connect(): void {
    const token = this.auth.token();
    if (!this.started || !token) return;
    const proto = this.location.protocol === 'https:' ? 'wss' : 'ws';
    const url = `${proto}://${this.location.host}/ws/dashboard?access_token=${encodeURIComponent(token)}&scope=${encodeURIComponent(this.scope())}`;
    this.link.set('connecting');
    const ws = new WebSocket(url);
    this.socket = ws;

    ws.onopen = () => {
      this.link.set('live');
      this.retryMs = 1000;
      // The URL carried the scope at connect time; it may have changed while connecting
      ws.send(JSON.stringify({ scope: this.scope() }));
    };
    ws.onmessage = (m: MessageEvent<string>) => {
      const msg = JSON.parse(m.data) as SocketMessage;
      if (msg.type === 'events') this.applyEvents(msg.data);
      // Pushes built for the previous selection can still be in flight
      else if (msg.scope === undefined || msg.scope === this.scope()) this.applyOverview(msg.data);
    };
    ws.onclose = () => {
      if (this.socket !== ws) return;
      this.socket = null;
      this.link.set('offline');
      if (!this.started) return;
      this.retryTimer = setTimeout(() => this.connect(), this.retryMs);
      this.retryMs = Math.min(this.retryMs * 2, 30000);
    };
  }

  private applyOverview(o: DashboardOverview): void {
    this.overview.set(o);
    // A trade opened or closed: the PnL curve and the account list (funds) change too
    const signature = o.trades.map((t) => `${t.tradeId}:${t.status}`).join(',');
    if (signature !== this.lastTradeSignature) {
      const first = this.lastTradeSignature === '';
      this.lastTradeSignature = signature;
      if (!first) {
        void this.refreshCurve();
        void this.refreshAccounts();
      }
    }
  }

  private applyEvents(batch: SystemEvent[]): void {
    this.events.update((list) => [...[...batch].reverse(), ...list].slice(0, 300));
    this.toasts.fromEvents(batch);
    if (batch.some((e) => e.eventType === 'MODE_CHANGE' || e.eventType.startsWith('LOGIN_'))) void this.refreshAccounts();
  }

  private static loadScope(): Scope {
    let saved: string | null = null;
    try { saved = localStorage.getItem(SCOPE_KEY); } catch { /* storage blocked */ }
    return saved !== null && isScope(saved) ? saved : 'all';
  }
}

export function isScope(v: string): v is Scope {
  return v === 'all' || v === 'real' || v === 'paper' || /^\d+$/.test(v);
}
