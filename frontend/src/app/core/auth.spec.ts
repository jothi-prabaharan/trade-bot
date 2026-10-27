import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from './auth';

const KEY = 'tradebot.session';
const login = (hours = 8) => ({ token: `token-${hours}h`, expiresAtUtc: new Date(Date.now() + hours * 3_600_000).toISOString(), user: 'admin' });
const session = (hours: number, remember: boolean) => ({ ...login(hours), remember });

describe('AuthService', () => {
  // Stands in for another browser tab on the same origin
  let otherTab: BroadcastChannel;
  let heard: unknown[];

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    TestBed.configureTestingModule({ providers: [provideRouter([{ path: 'login', children: [] }, { path: '', children: [] }])] });
    heard = [];
    otherTab = new BroadcastChannel('tradebot.auth');
    otherTab.onmessage = (e: MessageEvent) => heard.push(e.data);
  });

  afterEach(() => {
    otherTab.close();
    TestBed.resetTestingModule();
  });

  it('keeps a remembered session in localStorage and a tab session in sessionStorage, never both', () => {
    const auth = TestBed.inject(AuthService);
    auth.store(login(720), true);
    expect(localStorage.getItem(KEY)).toContain('token-720h');
    expect(sessionStorage.getItem(KEY)).toBeNull();

    auth.store(login(8), false);
    expect(sessionStorage.getItem(KEY)).toContain('token-8h');
    expect(localStorage.getItem(KEY)).toBeNull();
    expect(auth.token()).toBe('token-8h');
  });

  it('restores a remembered session at once, without asking other tabs', async () => {
    localStorage.setItem(KEY, JSON.stringify(session(720, true)));
    const auth = TestBed.inject(AuthService);
    await auth.whenReady();
    expect(auth.token()).toBe('token-720h');
    expect(heard).toEqual([]);
  });

  it('drops an expired session', () => {
    localStorage.setItem(KEY, JSON.stringify(session(-1, true)));
    expect(TestBed.inject(AuthService).isLoggedIn()).toBe(false);
    expect(localStorage.getItem(KEY)).toBeNull();
  });

  it('a new tab takes the session from an open tab', async () => {
    otherTab.onmessage = (e: MessageEvent) => {
      if (e.data.type === 'ask') otherTab.postMessage({ type: 'session', session: session(8, false) });
    };
    const auth = TestBed.inject(AuthService);
    await auth.whenReady();
    expect(auth.token()).toBe('token-8h');
    expect(sessionStorage.getItem(KEY)).toContain('token-8h');
  });

  it('a new tab with no open tabs gives up quickly and stays signed out', async () => {
    const auth = TestBed.inject(AuthService);
    const started = Date.now();
    await auth.whenReady();
    expect(Date.now() - started).toBeLessThan(1000);
    expect(heard).toEqual([{ type: 'ask' }]);
    expect(auth.isLoggedIn()).toBe(false);
  });

  it('answers new tabs, announces its sign-in and follows a sign-out elsewhere', async () => {
    const auth = TestBed.inject(AuthService);
    await auth.whenReady();
    heard = [];
    auth.store(login(8), false);
    await vi.waitFor(() => expect(heard).toContainEqual({ type: 'session', session: expect.objectContaining({ token: 'token-8h', remember: false }) }));

    heard = [];
    otherTab.postMessage({ type: 'ask' });
    await vi.waitFor(() => expect(heard).toEqual([{ type: 'session', session: expect.objectContaining({ token: 'token-8h' }) }]));

    otherTab.postMessage({ type: 'logout' });
    await vi.waitFor(() => expect(auth.isLoggedIn()).toBe(false));
    expect(sessionStorage.getItem(KEY)).toBeNull();
  });

  it('a sign-out here reaches the other tabs', async () => {
    const auth = TestBed.inject(AuthService);
    auth.store(login(720), true);
    auth.logout(false);
    await vi.waitFor(() => expect(heard).toContainEqual({ type: 'logout' }));
    expect(localStorage.getItem(KEY)).toBeNull();
  });
});
