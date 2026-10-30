import { normalizeTotpSecret } from '../features/config/account-drawer';
import { isScope } from './dashboard-store';

describe('account selection', () => {
  it('accepts the fixed views and account ids, nothing else', () => {
    for (const ok of ['all', 'real', 'paper', '7', '1024']) expect(isScope(ok)).toBe(true);
    for (const bad of ['', 'ALL', '-1', '7a', 'null', ' 7']) expect(isScope(bad)).toBe(false);
  });
});

describe('TOTP secret field', () => {
  it('turns the secret as authenticator apps show it into plain Base32', () => {
    expect(normalizeTotpSecret(' jbsw y3dp\tehpk​ 3pxp ')).toBe('JBSWY3DPEHPK3PXP');
    expect(normalizeTotpSecret(null)).toBeNull();
  });
});
