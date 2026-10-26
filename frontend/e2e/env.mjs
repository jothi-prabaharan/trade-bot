// Shared e2e settings (stack.mjs, playwright.config.ts, specs). Override with environment variables.
export const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? 'e2e-Secret#1';
export const API_PORT = Number(process.env.E2E_API_PORT ?? 5080);
export const READY_PORT = Number(process.env.E2E_READY_PORT ?? 5099);
export const UI_PORT = Number(process.env.E2E_UI_PORT ?? 4300);

/** TRADEBOT_E2E_DB: Npgsql-style "Host=..;Port=..;Database=..;Username=..;Password=..". The database is dropped and recreated. */
export function dbConfig() {
  const cs = process.env.TRADEBOT_E2E_DB ?? 'Host=localhost;Port=5432;Database=tradebot_e2e;Username=tradebot;Password=tradebot';
  const kv = Object.fromEntries(cs.split(';').filter(Boolean).map((p) => {
    const i = p.indexOf('=');
    return [p.slice(0, i).trim().toLowerCase(), p.slice(i + 1).trim()];
  }));
  return { host: kv.host ?? 'localhost', port: Number(kv.port ?? 5432), database: kv.database, user: kv.username, password: kv.password };
}
