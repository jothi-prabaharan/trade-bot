import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { ApiService } from './api';
import { afterSignIn } from './auth';
import { DashboardStore } from './dashboard-store';
import { PanelService } from './panels';
import { PaytmReturn, parsePaytmRedirect } from './paytm-return';
import { ToastService } from './ui-state';

const query = (s: string) => new URLSearchParams(s);

describe('Paytm redirect', () => {
  it('reads the account and this browser\'s nonce from state, and nothing else', () => {
    expect(parsePaytmRedirect(query('success=true&requestToken=fc11&state=tb2'))).toEqual({ accountId: 2, requestToken: 'fc11', nonce: null, success: true });
    expect(parsePaytmRedirect(query('success=true&requestToken=fc11&state=tb12z0123456789abcdef'))?.nonce).toBe('0123456789abcdef');
    expect(parsePaytmRedirect(query('success=false&state=tb2'))?.success).toBe(false);
    for (const other of ['requestToken=fc11', 'requestToken=fc11&state=x2', 'requestToken=fc11&state=tb2zXYZ', 'state=tb2', 'scope=all']) {
      expect(parsePaytmRedirect(query(other))).toBeNull();
    }
  });
});

describe('PaytmReturn', () => {
  let exchanged: [number, string][];
  let opened: [string, number | null][];
  let refusal: HttpErrorResponse | null;

  beforeEach(() => {
    localStorage.clear();
    exchanged = [];
    opened = [];
    refusal = null;
    TestBed.configureTestingModule({
      providers: [
        {
          provide: ApiService,
          useValue: {
            exchangeRequestToken: (id: number, token: string) => {
              exchanged.push([id, token]);
              return refusal ? throwError(() => refusal) : of({ tokenExpiry: '2026-11-02T18:29:00Z', connection: { ok: true, name: 'Asha Trader' } });
            },
          },
        },
        { provide: DashboardStore, useValue: { refreshOverview: () => Promise.resolve() } },
        { provide: PanelService, useValue: { open: (kind: string, id: number | null) => opened.push([kind, id]) } },
        { provide: ToastService, useValue: { show: () => undefined } },
      ],
    });
  });

  afterEach(() => TestBed.resetTestingModule());

  it('uses the token from a login this browser started, and only once', async () => {
    const paytm = TestBed.inject(PaytmReturn);
    const nonce = paytm.begin(2);
    expect(nonce).toMatch(/^[0-9a-f]{32}$/);

    await paytm.handle({ accountId: 2, requestToken: 'req-1', nonce, success: true });
    expect(exchanged).toEqual([[2, 'req-1']]);
    expect(opened).toEqual([]);

    // The same address again (a reload) is not trusted a second time: it waits for Use token
    await paytm.handle({ accountId: 2, requestToken: 'req-1', nonce, success: true });
    expect(exchanged.length).toBe(1);
    expect(opened).toEqual([['token', 2]]);
  });

  it('leaves a token it did not ask for in the Paytm drawer', async () => {
    const paytm = TestBed.inject(PaytmReturn);
    const nonce = paytm.begin(2);
    // A link without this browser's nonce, and this browser's nonce on another account
    await paytm.handle({ accountId: 2, requestToken: 'req-x', nonce: null, success: true });
    expect(paytm.take(2)).toEqual({ requestToken: 'req-x', note: expect.stringContaining('not started from this browser') });
    expect(paytm.take(2)).toBeNull();
    await paytm.handle({ accountId: 3, requestToken: 'req-y', nonce, success: true });
    expect(paytm.take(3)?.requestToken).toBe('req-y');
    expect(exchanged).toEqual([]);
  });

  it('shows Paytm\'s refusal next to the token instead of losing it', async () => {
    refusal = new HttpErrorResponse({
      status: 400, error: { errors: [{ code: 'PM_MERCHANT_AUTH_400607', message: 'Paytm: PM_MERCHANT_AUTH_400607 Invalid request token.' }] },
    });
    const paytm = TestBed.inject(PaytmReturn);
    await paytm.handle({ accountId: 2, requestToken: 'req-1', nonce: paytm.begin(2), success: true });
    expect(paytm.take(2)).toEqual({ requestToken: 'req-1', note: 'Paytm: PM_MERCHANT_AUTH_400607 Invalid request token.' });
  });
});

describe('sign-in return', () => {
  it('goes back to the page the sign-in interrupted, never to another site', () => {
    const paytm = '/?success=true&requestToken=fc11&state=tb2';
    expect(afterSignIn(paytm)).toBe(paytm);
    for (const other of [null, '', 'https://example.com/', '//example.com', '/\\example.com', '/login?returnUrl=%2F']) expect(afterSignIn(other)).toBe('/');
  });
});
