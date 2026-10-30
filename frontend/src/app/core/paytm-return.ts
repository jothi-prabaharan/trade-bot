import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from './api';
import { DashboardStore } from './dashboard-store';
import { PanelService } from './panels';
import { ToastService } from './ui-state';

const STORAGE_KEY = 'tradebot.paytmLogins';
/** A login started longer ago than this is not trusted when Paytm comes back (its request token is short-lived anyway). */
const PENDING_MS = 30 * 60_000;

interface StartedLogin { accountId: number; nonce: string; startedAt: number }

/** What Paytm appends to the redirect URL after its login: `?success=true&requestToken=…&state=tb{id}[z{nonce}]`. */
export interface PaytmRedirect { accountId: number; requestToken: string | null; nonce: string | null; success: boolean }

/** Paytm's redirect, or null when the query string is not one (`state` is how this bot names the account). */
export function parsePaytmRedirect(params: { get(name: string): string | null; has(name: string): boolean }): PaytmRedirect | null {
  const m = /^tb(\d+)(?:z([0-9a-f]{16,32}))?$/.exec(params.get('state') ?? '');
  const token = (params.get('requestToken') ?? params.get('request_token'))?.trim() || null;
  if (!m || (token === null && !params.has('success'))) return null;
  return { accountId: Number(m[1]), requestToken: token, nonce: m[2] ?? null, success: params.get('success') !== 'false' && token !== null };
}

/**
 * Paytm's redirect URL is this dashboard. The requestToken it brings back is exchanged straight away when the login was
 * started from this browser (the nonce it put into Paytm's state matches); any other token waits in the Paytm drawer for
 * Use token, so a link made by someone else cannot attach their Paytm session to an account here.
 */
@Injectable({ providedIn: 'root' })
export class PaytmReturn {
  private readonly api = inject(ApiService);
  private readonly store = inject(DashboardStore);
  private readonly panels = inject(PanelService);
  private readonly toasts = inject(ToastService);
  private readonly waiting = signal<{ accountId: number; requestToken: string; note: string } | null>(null);

  /** Remembers a login this browser is starting; the returned nonce goes into Paytm's state. */
  begin(accountId: number): string {
    const nonce = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
    write([...read().filter((l) => Date.now() - l.startedAt < PENDING_MS), { accountId, nonce, startedAt: Date.now() }].slice(-5));
    return nonce;
  }

  /** The token waiting for this account and why it was not used by itself; handed over once. */
  take(accountId: number): { requestToken: string; note: string } | null {
    const w = this.waiting();
    if (w?.accountId !== accountId) return null;
    this.waiting.set(null);
    return { requestToken: w.requestToken, note: w.note };
  }

  async handle(r: PaytmRedirect): Promise<void> {
    if (!r.success || r.requestToken === null) {
      this.toasts.show('warning', 'Paytm login not completed', 'Paytm came back without a requestToken. Start the login again from the Paytm drawer.');
      return;
    }
    const started = read();
    const mine = started.some((l) => l.nonce === r.nonce && l.accountId === r.accountId && Date.now() - l.startedAt < PENDING_MS);
    write(started.filter((l) => l.nonce !== r.nonce));
    if (!mine) {
      this.offer(r.accountId, r.requestToken, 'Paytm sent this requestToken back, but the login was not started from this browser. If it was your own login, press Use token.');
      return;
    }
    try {
      const res = await firstValueFrom(this.api.exchangeRequestToken(r.accountId, r.requestToken));
      this.toasts.show('success', 'Paytm login saved', res.connection?.ok ? `Verified with Paytm as ${res.connection.name ?? 'the account holder'}.` : "Today's token is stored.");
      void this.store.refreshOverview();
    } catch (e) {
      this.offer(r.accountId, r.requestToken, apiErrorText(e));
    }
  }

  private offer(accountId: number, requestToken: string, note: string): void {
    this.waiting.set({ accountId, requestToken, note });
    this.panels.open('token', accountId);
  }
}

function read(): StartedLogin[] {
  try {
    const v: unknown = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '[]');
    return Array.isArray(v) ? v.filter((l): l is StartedLogin => typeof l?.nonce === 'string' && typeof l?.accountId === 'number') : [];
  } catch {
    return [];
  }
}

function write(logins: StartedLogin[]): void {
  try {
    if (logins.length) localStorage.setItem(STORAGE_KEY, JSON.stringify(logins));
    else localStorage.removeItem(STORAGE_KEY);
  } catch {
    /* storage blocked: the token then waits for Use token */
  }
}
