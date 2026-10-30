import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { LoginResponse } from './models';

const STORAGE_KEY = 'tradebot.session';
const CHANNEL_NAME = 'tradebot.auth';
/** How long a new tab waits for an open tab to hand over its session. */
const HANDSHAKE_MS = 300;

interface Session { token: string; expiresAtUtc: string; user: string; remember: boolean }
type AuthMessage = { type: 'ask' } | { type: 'session'; session: Session } | { type: 'logout' };

/**
 * Admin JWT. "Remember me" keeps it in localStorage, so it survives browser restarts until it expires.
 * Otherwise each tab holds it in sessionStorage and open tabs hand it to new ones over a BroadcastChannel,
 * so it lasts until the last tab closes. Signing in or out in one tab applies to every tab.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly router = inject(Router);
  private readonly session = signal<Session | null>(restore());
  private readonly channel = openChannel();
  private answered: (() => void) | null = null;
  private readonly ready: Promise<void>;

  readonly user = computed(() => this.session()?.user ?? null);
  readonly token = computed(() => {
    const s = this.session();
    return s && !expired(s) ? s.token : null;
  });
  readonly isLoggedIn = computed(() => this.token() !== null);

  constructor() {
    this.channel?.addEventListener('message', (e: MessageEvent<AuthMessage>) => this.onMessage(e.data));
    inject(DestroyRef).onDestroy(() => this.channel?.close());
    this.ready = this.token() || !this.channel ? Promise.resolve() : this.askOpenTabs();
  }

  /** Resolves once a new tab has heard back from any open tab (or waited long enough). */
  whenReady(): Promise<void> {
    return this.ready;
  }

  store(r: LoginResponse, remember: boolean): void {
    const s: Session = { token: r.token, expiresAtUtc: r.expiresAtUtc, user: r.user, remember };
    persist(s);
    this.session.set(s);
    this.post({ type: 'session', session: s });
  }

  logout(redirect = true): void {
    persist(null);
    this.session.set(null);
    this.post({ type: 'logout' });
    if (redirect) void this.router.navigate(['/login']);
  }

  private askOpenTabs(): Promise<void> {
    return new Promise((resolve) => {
      const timer = setTimeout(() => this.answered?.(), HANDSHAKE_MS);
      this.answered = () => {
        clearTimeout(timer);
        this.answered = null;
        resolve();
      };
      this.post({ type: 'ask' });
    });
  }

  private onMessage(m: AuthMessage): void {
    switch (m.type) {
      case 'ask': {
        const s = this.session();
        if (s && !expired(s)) this.post({ type: 'session', session: s });
        break;
      }
      case 'session':
        if (expired(m.session)) return;
        persist(m.session);
        this.session.set(m.session);
        this.answered?.();
        if (this.router.url.startsWith('/login')) void this.router.navigateByUrl(afterSignIn(this.router.parseUrl(this.router.url).queryParamMap.get('returnUrl')));
        break;
      case 'logout':
        persist(null);
        this.session.set(null);
        void this.router.navigate(['/login']);
        break;
    }
  }

  private post(m: AuthMessage): void {
    try { this.channel?.postMessage(m); } catch { /* channel closed */ }
  }
}

function expired(s: Session): boolean {
  return !(Date.parse(s.expiresAtUtc) > Date.now());
}

function openChannel(): BroadcastChannel | null {
  try { return typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel(CHANNEL_NAME); } catch { return null; }
}

/** A remembered session lives in localStorage, a tab session in sessionStorage; never both. */
function persist(s: Session | null): void {
  const json = s ? JSON.stringify(s) : '';
  try {
    if (s?.remember) localStorage.setItem(STORAGE_KEY, json);
    else localStorage.removeItem(STORAGE_KEY);
  } catch { /* storage blocked: memory only */ }
  try {
    if (s && !s.remember) sessionStorage.setItem(STORAGE_KEY, json);
    else sessionStorage.removeItem(STORAGE_KEY);
  } catch { /* storage blocked: memory only */ }
}

function restore(): Session | null {
  for (const read of [() => localStorage.getItem(STORAGE_KEY), () => sessionStorage.getItem(STORAGE_KEY)]) {
    try {
      const raw = read();
      if (!raw) continue;
      const s = JSON.parse(raw) as Session;
      if (!expired(s)) return s;
    } catch { /* blocked or corrupt: try the next store */ }
  }
  persist(null);
  return null;
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const token = auth.token();
  const authed = token && req.url.startsWith('/api/') ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
  return next(authed).pipe(
    catchError((e: unknown) => {
      if (e instanceof HttpErrorResponse && e.status === 401 && !req.url.endsWith('/api/auth/login')) auth.logout();
      return throwError(() => e);
    }),
  );
};

/** Where to go after signing in: the page the sign-in interrupted (a path on this site), else the dashboard. */
export function afterSignIn(returnUrl: string | null | undefined): string {
  return returnUrl && /^\/(?![/\\])/.test(returnUrl) && !returnUrl.startsWith('/login') ? returnUrl : '/';
}

/** Signed-out visits go to the sign-in page, which brings them back (Paytm's redirect keeps its requestToken). */
export const authGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  await auth.whenReady();
  if (auth.isLoggedIn()) return true;
  return router.createUrlTree(['/login'], state.url === '/' ? undefined : { queryParams: { returnUrl: state.url } });
};

/** The login page sends a tab that is already signed in (here or via another tab) on to where it was going. */
export const guestGuard: CanActivateFn = async (route) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  await auth.whenReady();
  return auth.isLoggedIn() ? router.parseUrl(afterSignIn(route.queryParamMap.get('returnUrl'))) : true;
};
