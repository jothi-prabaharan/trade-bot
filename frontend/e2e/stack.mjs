// Playwright webServer: fresh PostgreSQL database → db/migrations/*.sql → seed.sql → TradeBot.Api (engine off) → "ready" on E2E_READY_PORT.
// Seeding precedes the API so its startup config load sees the seeded accounts.
import { spawn, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { createServer } from 'node:http';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import pg from 'pg';
import { ADMIN_PASSWORD, API_PORT, READY_PORT, dbConfig } from './env.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const apiProject = resolve(here, '../../backend/src/TradeBot.Api');
const apiDll = resolve(apiProject, 'bin/Debug/net10.0/TradeBot.Api.dll');
const db = dbConfig();

function run(cmd, args, opts = {}) {
  const r = spawnSync(cmd, args, { encoding: 'utf8', ...opts });
  if (r.status !== 0) throw new Error(`${cmd} ${args.join(' ')} failed:\n${r.stdout}\n${r.stderr}`);
  return r.stdout;
}

async function recreateDatabase() {
  const admin = new pg.Client({ ...db, database: 'postgres' });
  await admin.connect();
  await admin.query(`DROP DATABASE IF EXISTS "${db.database}" WITH (FORCE)`);
  await admin.query(`CREATE DATABASE "${db.database}"`);
  await admin.end();
}

async function waitFor(url, ms) {
  const until = Date.now() + ms;
  while (Date.now() < until) {
    try { if ((await fetch(url)).ok) return; } catch { /* not up yet */ }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`Timed out waiting for ${url}`);
}

await recreateDatabase();
const client = new pg.Client(db);
await client.connect();
const migrations = resolve(here, '../../db/migrations');
const sql = (path) => readFileSync(path, 'utf8').replace(/^\uFEFF/, ''); // EF scripts start with a BOM
for (const f of readdirSync(migrations).filter((f) => f.endsWith('.sql')).sort()) await client.query(sql(resolve(migrations, f)));
await client.query(sql(resolve(here, 'seed.sql')));
await client.end();

run('dotnet', ['build', apiProject, '-v', 'q', '-nologo']);
const hash = run('dotnet', [apiDll, 'hash-password', ADMIN_PASSWORD]).trim().replace(/^Password:\s*/, '');

const api = spawn('dotnet', [apiDll], {
  cwd: apiProject,
  stdio: ['ignore', 'inherit', 'inherit'],
  env: {
    ...process.env,
    ASPNETCORE_URLS: `http://localhost:${API_PORT}`,
    ASPNETCORE_ENVIRONMENT: 'Production',
    ConnectionStrings__TradeBot: `Host=${db.host};Port=${db.port};Database=${db.database};Username=${db.user};Password=${db.password}`,
    Security__MasterKey: randomBytes(32).toString('base64'),
    Auth__JwtKey: randomBytes(32).toString('base64'),
    Auth__AdminPasswordHash: hash,
    Engine__Enabled: 'false',
    Logging__LogLevel__Default: 'Warning',
  },
});
const stop = () => { api.kill('SIGTERM'); process.exit(0); };
process.on('SIGTERM', stop);
process.on('SIGINT', stop);
api.on('exit', (code) => { console.error(`TradeBot.Api exited (${code})`); process.exit(1); });

await waitFor(`http://localhost:${API_PORT}/health`, 120_000);

createServer((_, res) => res.writeHead(200).end('ready')).listen(READY_PORT, '127.0.0.1');
console.log(`e2e stack ready: API :${API_PORT}, database ${db.database}`);
