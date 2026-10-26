# trade-bot

Multi-account AI options trading bot for Paytm Money (PAPER + REAL), powered by a chart-pattern engine and Gemini news intelligence. Stack: .NET 10 + EF Core / PostgreSQL (`mst` + `trn` schemas), Angular dashboard, Ubuntu + systemd.

| Doc | |
|---|---|
| [docs/DECISIONS.md](docs/DECISIONS.md) | Phase 0 configuration decisions |
| [docs/PHASE1_SCHEMA.md](docs/PHASE1_SCHEMA.md) | Phase 1 database schema and constraints |
| [docs/PHASE2_VALIDATION.md](docs/PHASE2_VALIDATION.md) | Phase 2 validation rules matrix |
| [docs/PHASE3_BACKEND.md](docs/PHASE3_BACKEND.md) | Phase 3 backend, engines, workers and API |
| [docs/PHASE4_DASHBOARD.md](docs/PHASE4_DASHBOARD.md) | Phase 4 unified Angular dashboard |
| [docs/PHASE5_DEPLOYMENT.md](docs/PHASE5_DEPLOYMENT.md) | Phase 5 Ubuntu deployment runbook |

```
backend/   .NET solution: Domain, Application (validation, patterns, OCO engine, news), Infrastructure (EF Core, Paytm, Gemini),
           Api (REST + engine host), Worker.News, Worker.Auth, tests
frontend/  Angular 22 dashboard (Vitest unit tests, Playwright e2e against the real API)
db/        Generated SQL migration scripts
deploy/    Ubuntu 24.04 provisioning, deploy/rollback, systemd, nginx, PostgreSQL tuning, backups, health checks, seed data
shared/    Cross-stack artifacts (validation test vectors used by backend and Angular)
docs/      Design documents
```

Install on a server: see [docs/PHASE5_DEPLOYMENT.md](docs/PHASE5_DEPLOYMENT.md) (`deploy/install.sh`, then `tradebotctl`).
