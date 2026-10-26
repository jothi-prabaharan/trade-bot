# TradeBot dashboard (Angular 22)

Single-screen dashboard for the TradeBot API: accounts and funds, live PnL with the daily loss-limit bar, the trade book
with its mandatory SL / target bracket, charts, signals, news, events and every config drawer.

| Command | What it does |
|---|---|
| `npm start` | Dev server on :4200; `/api`, `/ws` and `/health` proxy to the API on :5080 (`proxy.conf.json`) |
| `npm run build` | Production build in `dist/tradebot-dashboard/browser` (served by nginx in Phase 5) |
| `npm test` | Vitest: validation twin against `shared/validation` vectors and the rules snapshot |
| `npm run e2e` | Playwright against the real API and a throwaway PostgreSQL database (`e2e/stack.mjs`) |

Requires Node ≥ 22.22.3 (Angular CLI 22). E2E settings: `TRADEBOT_E2E_DB`, `E2E_API_PORT`, `E2E_UI_PORT`, `E2E_READY_PORT`
(see `e2e/env.mjs`); the database named in `TRADEBOT_E2E_DB` is dropped and recreated on every run.

Design notes are in [docs/PHASE4_DASHBOARD.md](../docs/PHASE4_DASHBOARD.md).
