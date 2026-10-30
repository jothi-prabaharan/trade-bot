import { BrowserContext, Locator, Page, expect, test } from '@playwright/test';
import { createECDH, randomBytes } from 'node:crypto';
import pg from 'pg';
import { ADMIN_PASSWORD, dbConfig } from './env.mjs';

// One signed-in page for the whole run: the login endpoint allows 5 attempts a minute (this file makes 4).
test.describe.configure({ mode: 'serial' });

let context: BrowserContext;
let page: Page;
const card = (name: string): Locator => page.locator(`section[aria-label="${name}"]`);
const field = (drawer: Locator, label: string): Locator => drawer.locator('tb-rule-field', { hasText: label }).locator('input, select, textarea').first();
const drawer = (title: string): Locator => page.locator('.offcanvas', { has: page.locator('.offcanvas-title', { hasText: title }) });
const toast = (title: string): Locator => page.locator('.toast', { hasText: title });

// A real context, so later tests can open a second tab next to this one
test.beforeAll(async ({ browser }) => {
  context = await browser.newContext();
  page = await context.newPage();
});
test.afterAll(async () => {
  await context.close();
});

test('rejects a wrong password, then signs in', async () => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Password').fill('wrong-password');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('alert')).toContainText('Invalid username or password');

  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByText('Live', { exact: true })).toBeVisible();
});

test('shows account profiles, funds, consolidated PnL and the trade book', async () => {
  await expect(card('Real Primary')).toContainText('PM12345');
  await expect(card('Real Primary')).toContainText('No token');
  await expect(card('Real Primary').getByRole('button', { name: 'Login now' })).toBeVisible();
  await expect(card('Paper Beta')).toContainText('₹86,955');

  // Realized 3,952.00 − charges 76.84 on Paper Alpha + open MTM (112.40 − 104.25) × 130 − 23.92 on Paper Beta
  await expect(page.getByTestId('net-pnl')).toHaveText('+₹4,910.74');

  // Balance = opening + today's net, per account and in total
  await expect(card('Paper Alpha').getByTestId('account-balance')).toContainText('₹1,03,875.16');
  await expect(card('Paper Alpha').getByTestId('account-balance')).toContainText('+₹3,875.16 (+3.88%)');
  await expect(card('Paper Beta').getByTestId('account-balance')).toContainText('₹1,01,035.58');
  await expect(card('Real Primary').getByTestId('account-balance')).toContainText('₹1,00,000.00');
  await expect(page.getByTestId('total-balance')).toHaveText('₹3,04,910.74');

  const rows = page.locator('tb-trade-grid tbody tr[data-trade-id]');
  await expect(rows).toHaveCount(2);
  const open = rows.filter({ hasText: 'Paper Beta' });
  await expect(open).toContainText('Open');
  await expect(open).toContainText('Bullish Engulfing');
  await expect(open).toContainText('+₹1,035.58');
  await expect(open.getByRole('button', { name: 'Exit' })).toBeVisible();
  await expect(rows.filter({ hasText: 'Paper Alpha' })).toContainText('Target hit');

  await page.getByRole('tab', { name: /Signals/ }).click();
  await expect(page.locator('tb-info-tabs tbody tr')).toHaveCount(3);
  await expect(page.locator('tb-info-tabs')).toContainText('MS_MAX_OPEN_TRADES');
});

test('trade drawer shows option and underlying charts with the order legs', async () => {
  await page.locator('tb-trade-grid tbody tr', { hasText: 'Paper Beta' }).click();
  const d = drawer('NIFTY 25000 CE');
  await expect(d).toBeVisible();
  await expect(d.locator('tb-candle-chart')).toHaveCount(2);
  await expect(d.locator('tb-candle-chart canvas').first()).toBeVisible();
  const legs = d.locator('table tbody tr');
  await expect(legs).toHaveCount(3);
  await expect(legs.filter({ hasText: 'Stop-loss' })).toContainText('TRIGGER_PENDING');
  await expect(legs.filter({ hasText: 'Target' })).toContainText('PENDING');
  await page.keyboard.press('Escape');
  await expect(d).toBeHidden();
});

test('manual exit asks for confirmation and reports the server answer', async () => {
  await page.locator('tb-trade-grid tbody tr', { hasText: 'Paper Beta' }).getByRole('button', { name: 'Exit' }).click();
  const dialog = page.getByRole('dialog', { name: 'Exit NIFTY 25000 CE' });
  await expect(dialog).toContainText('Cancels the stop-loss and target');
  await dialog.getByRole('button', { name: 'Exit now' }).click();
  // The engine is off in e2e, so no live trade owns this row
  await expect(toast('Exit failed')).toBeVisible();
});

test('risk drawer applies the backend rules before saving', async () => {
  await card('Paper Alpha').getByRole('button', { name: 'Risk' }).click();
  const d = drawer('Risk config · Paper Alpha');
  await expect(d.locator('form')).toBeVisible();

  await field(d, 'Fallback SL').fill('20');
  await field(d, 'Max daily loss').fill('200000');
  await expect(d).toContainText('CX_FALLBACK_RR');
  await expect(d).toContainText('CX_DAILY_LOSS_LTE_CAPITAL');
  await field(d, 'Risk per trade').fill('12');
  await expect(d.locator('tb-rule-field', { hasText: 'Risk per trade' })).toContainText('Must be at most 10');
  await d.getByRole('button', { name: 'Save' }).click();
  await expect(d).toBeVisible();

  await field(d, 'Fallback SL').fill('15');
  await field(d, 'Max daily loss').fill('3000');
  await field(d, 'Risk per trade').fill('2');
  await field(d, 'Daily profit lock').fill('7000');
  await expect(d).not.toContainText('CX_');
  await d.getByRole('button', { name: 'Save' }).click();
  await expect(toast('Risk config saved')).toBeVisible();
  await expect(d).toBeHidden();

  await card('Paper Alpha').getByRole('button', { name: 'Risk' }).click();
  await expect(field(drawer('Risk config · Paper Alpha'), 'Daily profit lock')).toHaveValue('7000');
  await page.keyboard.press('Escape');
});

test('new account: field errors from the served rules, then a PAPER card', async () => {
  await page.getByRole('button', { name: 'Add account' }).click();
  const d = drawer('Add PAPER account');
  await field(d, 'Account name').fill('Paper Gamma');
  await field(d, 'Paytm client ID').fill('PAPER C');
  await field(d, 'Account holder').fill('Asha Trader');
  await field(d, 'Account holder').blur();
  await expect(d.locator('tb-rule-field', { hasText: 'Paytm client ID' })).toContainText('Invalid format.');
  await d.getByRole('button', { name: 'Save' }).click();
  await expect(d).toBeVisible();

  await field(d, 'Paytm client ID').fill('PAPER-C');
  await d.getByRole('button', { name: 'Save' }).click();
  await expect(toast('Account created')).toBeVisible();
  await expect(card('Paper Gamma')).toContainText('PAPER');
});

test('switching to REAL needs the typed client ID and shows the server rule', async () => {
  await card('Paper Alpha').getByRole('button', { name: 'To REAL' }).click();
  const dialog = page.getByRole('dialog', { name: 'Switch Paper Alpha to REAL' });
  const go = dialog.getByRole('button', { name: 'Go live' });
  await expect(go).toBeDisabled();
  await dialog.getByLabel(/Type .* to confirm/).fill('PAPER-X');
  await expect(go).toBeDisabled();
  await dialog.getByLabel(/Type .* to confirm/).fill('PAPER-A');
  await go.click();
  // Paper Alpha has no API key/secret, so the backend refuses REAL
  await expect(toast('Mode change blocked')).toBeVisible();
  await expect(card('Paper Alpha').locator('.badge', { hasText: 'PAPER' })).toBeVisible();
});

test('engine events arrive over the socket as toasts', async () => {
  const client = new pg.Client(dbConfig());
  await client.connect();
  await client.query(`INSERT INTO trn.system_events (event_type, severity, message, payload) VALUES ('E2E_ALERT', 'CRITICAL', 'Feed lag above threshold', '{}')`);
  await client.end();
  await expect(toast('E2E ALERT')).toContainText('Feed lag above threshold');
  await page.getByRole('tab', { name: /Events/ }).click();
  await expect(page.locator('tb-info-tabs tbody tr').first()).toContainText('E2E_ALERT');
});

test('Paytm SMS OTP: the card asks for it while the auth worker waits, and relays the code to it', async () => {
  const client = new pg.Client(dbConfig());
  await client.connect();
  const id = (await client.query(`SELECT account_id FROM mst.trading_accounts WHERE account_name = 'Real Primary'`)).rows[0].account_id as string;
  const relayed = new Promise<string>((resolve) => client.on('notification', (n) => resolve(n.payload ?? '')));
  await client.query('LISTEN tradebot_otp_submitted');
  // What the Auth worker announces when Paytm shows its SMS-OTP screen
  const until = new Date(Date.now() + 180_000).toISOString();
  await client.query(`SELECT pg_notify('tradebot_otp_requested', $1)`,
    [JSON.stringify({ AccountId: Number(id), UntilUtc: until, SentTo: '******3210', Note: null })]);

  const box = card('Real Primary').getByTestId('otp-prompt');
  await expect(box).toContainText('Paytm sent an OTP to ******3210');
  await expect(box).toContainText(/0:0[23]:\d\d left/);
  await box.getByPlaceholder('SMS OTP').fill('482913');
  await box.getByRole('button', { name: 'Submit OTP' }).click();
  expect(await relayed).toBe(`${id}:482913`);
  await expect(toast('OTP sent')).toBeVisible();
  await expect(box).toHaveCount(0);
  await client.end();
});

test('strategy drawer blocks inconsistent session times', async () => {
  await page.getByRole('button', { name: 'Strategy settings' }).click();
  const d = drawer('Strategy settings');
  await field(d, 'Entries until').fill('15:20:00');
  await expect(d).toContainText('CX_SESSION_TIMES');
  await field(d, 'Trigger timeframe').selectOption('15m');
  await expect(d).toContainText('CX_TREND_TF_ABOVE_TRIGGER');
  await d.getByRole('button', { name: 'Cancel' }).click();
  await expect(d).toBeHidden();
});

test('holidays: special sessions need times; a valid holiday is listed', async () => {
  await page.getByRole('button', { name: 'Market holidays' }).click();
  const d = drawer('Market holidays');
  await field(d, 'Date').fill('2026-12-25');
  await field(d, 'Description').fill('Christmas');
  await d.getByLabel('Special session (market opens briefly)').check();
  await expect(d).toContainText('CX_SPECIAL_SESSION_TIMES');
  await d.getByLabel('Special session (market opens briefly)').uncheck();
  await d.getByRole('button', { name: 'Add' }).click();
  await expect(d.locator('table tbody tr', { hasText: 'Christmas' })).toContainText('2026-12-25');
  await page.keyboard.press('Escape');
});

test('notifications: devices from other browsers are listed; removing one asks first', async () => {
  // A phone registered earlier; its keys only need the browser format, nothing is pushed
  const ecdh = createECDH('prime256v1');
  ecdh.generateKeys();
  const token = await page.evaluate(() => JSON.parse(sessionStorage.getItem('tradebot.session') ?? '{}').token as string);
  const created = await page.request.post('/api/push/devices', {
    headers: { Authorization: `Bearer ${token}` },
    data: {
      endpoint: 'https://push.example.test/send/e2e-phone', p256dh: ecdh.getPublicKey('base64url'), auth: randomBytes(16).toString('base64url'),
      deviceLabel: 'Chrome on Android (app)', kinds: { critical: true, entry: false, exit: true, info: false },
    },
  });
  expect(created.ok()).toBeTruthy();

  await page.getByRole('button', { name: 'Notifications' }).click();
  const d = drawer('Notifications');
  // ng serve runs without a service worker; the production build has one
  await expect(d).toContainText('need the production dashboard over HTTPS');
  const row = d.locator('tbody tr', { hasText: 'Chrome on Android (app)' });
  await expect(row).toContainText('Critical, Trade exits');
  await row.getByRole('button', { name: 'Remove' }).click();
  await page.getByRole('dialog', { name: 'Remove device' }).getByRole('button', { name: 'Remove' }).click();
  await expect(row).toHaveCount(0);
  await expect(d).toContainText('No devices yet');
  await page.keyboard.press('Escape');
});

test('phone: settings move to a menu, compact lists expand one row at a time, Escape closes only the top dialog', async () => {
  await page.setViewportSize({ width: 390, height: 844 });
  // Earlier tests leave toasts up; on a phone they cover the bottom of the screen until dismissed
  const dismiss = page.getByRole('button', { name: 'Dismiss' });
  while (await dismiss.count()) await dismiss.first().click({ timeout: 1000 }).catch(() => undefined);
  await expect(page.getByRole('button', { name: 'Strategy settings' })).toBeHidden();
  await expect(page.getByRole('button', { name: 'Kill switch' })).toBeVisible();
  expect((await page.locator('tb-header-bar header').boundingBox())!.height).toBeLessThan(130);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);

  // Trades and signals become compact lists; tapping a row expands it, one at a time
  const trades = page.getByRole('list', { name: 'Trades' });
  const beta = trades.locator('li', { hasText: 'Paper Beta' });
  const alpha = trades.locator('li', { hasText: 'Paper Alpha' });
  await expect(beta.getByRole('button', { name: 'Exit' })).toBeVisible();
  await expect(beta).not.toContainText('from SL to target');
  await beta.getByRole('button', { expanded: false }).click();
  await expect(beta).toContainText('% from SL to target');
  await alpha.getByRole('button', { expanded: false }).click();
  await expect(alpha).toContainText('% from SL to target');
  await expect(beta).not.toContainText('from SL to target');
  await alpha.getByRole('button', { name: 'Charts & orders' }).click();
  await expect(drawer('NIFTY 25000 CE')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(drawer('NIFTY 25000 CE')).toBeHidden();

  await page.getByRole('tab', { name: /Signals/ }).click();
  const signals = page.getByRole('list', { name: 'Signals' }).locator('li');
  await expect(signals).toHaveCount(3);
  await signals.first().getByRole('button').click();
  await expect(signals.first()).toContainText('Pattern / News / OI');

  // Menu → Market holidays; a confirmation over the drawer takes the first Escape
  await page.getByRole('button', { name: 'Menu' }).click();
  await page.getByRole('dialog', { name: 'Menu' }).getByRole('button', { name: 'Market holidays' }).click();
  const d = drawer('Market holidays');
  await d.locator('table tbody tr', { hasText: 'Christmas' }).getByRole('button', { name: 'Delete' }).click();
  const ask = page.getByRole('dialog', { name: 'Delete holiday' });
  await expect(ask).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(ask).toBeHidden();
  await expect(d).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(d).toBeHidden();
  await page.setViewportSize({ width: 1440, height: 1000 });
});

test('theme toggle is remembered across reloads', async () => {
  await expect(page.locator('html')).toHaveAttribute('data-bs-theme', 'dark');
  await page.getByRole('button', { name: 'Light theme' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-bs-theme', 'light');
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-bs-theme', 'light');
  await expect(page.getByTestId('net-pnl')).toBeVisible();
  await page.getByRole('button', { name: 'Dark theme' }).click();
});

test('global kill switch needs KILL and a reason, then halts trading', async () => {
  await page.getByRole('button', { name: 'Kill switch' }).click();
  const dialog = page.getByRole('dialog', { name: 'Global kill switch' });
  const kill = dialog.getByRole('button', { name: 'Kill all trading' });
  await dialog.getByLabel(/Type .* to confirm/).fill('KILL');
  await expect(kill).toBeDisabled();
  await dialog.getByLabel('Reason').fill('e2e drill');
  await kill.click();
  await expect(toast('Kill switch sent')).toBeVisible();
  await expect(page.getByRole('alert').filter({ hasText: 'Trading halted.' })).toContainText('e2e drill');
  await expect(page.getByRole('button', { name: 'Halted' })).toBeDisabled();
});

test('a new tab is already signed in; signing out there signs out every tab', async () => {
  const second = await context.newPage();
  await second.goto('/');
  await expect(second.getByTestId('net-pnl')).toBeVisible();
  await expect(second).toHaveURL(/\/$/);
  // The login page sends a signed-in tab to the dashboard
  await second.goto('/login');
  await expect(second).toHaveURL(/\/$/);

  await second.getByRole('button', { name: 'Sign out' }).click();
  await expect(second).toHaveURL(/\/login$/);
  await expect(page).toHaveURL(/\/login$/);
  await second.close();
});

test('"Remember me" survives closing every tab; a plain sign-in does not', async ({ browser }) => {
  const own = await browser.newContext();
  const signIn = async (remember: boolean) => {
    const tab = await own.newPage();
    await tab.goto('/login');
    await tab.getByLabel('Password').fill(ADMIN_PASSWORD);
    if (remember) await tab.getByLabel('Remember me').check();
    await tab.getByRole('button', { name: 'Sign in' }).click();
    await expect(tab.getByTestId('net-pnl')).toBeVisible();
    await tab.close();
  };

  await signIn(false);
  const fresh = await own.newPage();
  await fresh.goto('/');
  await expect(fresh).toHaveURL(/\/login$/);
  await fresh.close();

  await signIn(true);
  const later = await own.newPage();
  await later.goto('/');
  await expect(later.getByTestId('net-pnl')).toBeVisible();
  await later.reload();
  await expect(later).toHaveURL(/\/$/);
  await own.close();
});
