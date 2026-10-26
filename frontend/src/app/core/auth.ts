import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { LoginResponse } from './models';

const STORAGE_KEY = 'tradebot.session';

interface Session { token: string; expiresAtUtc: string; user: string }

/** Admin JWT kept in sessionStorage: closing the tab ends the session. */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly router = inject(Router);
  private readonly session = signal<Session | null>(AuthService.restore());

  readonly user = computed(() => this.session()?.user ?? null);
  readonly token = computed(() => {
    const s = this.session();
    return s && Date.parse(s.expiresAtUtc) > Date.now() ? s.token : null;
  });
  readonly isLoggedIn = computed(() => this.token() !== null);

  store(r: LoginResponse): void {
    const s: Session = { token: r.token, expiresAtUtc: r.expiresAtUtc, user: r.user };
    try { sessionStorage.setItem(STORAGE_KEY, JSON.stringify(s)); } catch { /* private mode: memory only */ }
    this.session.set(s);
  }

  logout(redirect = true): void {
    try { sessionStorage.removeItem(STORAGE_KEY); } catch { /* ignore */ }
    this.session.set(null);
    if (redirect) void this.router.navigate(['/login']);
  }

  private static restore(): Session | null {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      return raw ? (JSON.parse(raw) as Session) : null;
    } catch {
      return null;
    }
  }
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

export const authGuard: CanActivateFn = () => (inject(AuthService).isLoggedIn() ? true : inject(Router).createUrlTree(['/login']));
