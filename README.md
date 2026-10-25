# trade-bot

Multi-account AI options trading bot for Paytm Money (PAPER + REAL), powered by a chart-pattern engine and Gemini news intelligence. Stack: .NET 10 + EF Core / PostgreSQL (`mst` + `trn` schemas), Angular dashboard, Ubuntu + systemd.

| Doc | |
|---|---|
| [docs/DECISIONS.md](docs/DECISIONS.md) | Phase 0 configuration decisions |
| [docs/PHASE1_SCHEMA.md](docs/PHASE1_SCHEMA.md) | Phase 1 database schema and constraints |
| [docs/PHASE2_VALIDATION.md](docs/PHASE2_VALIDATION.md) | Phase 2 validation rules matrix |

```
backend/   .NET solution (Domain, Application validation, Infrastructure + EF Core migrations, tests)
db/        Generated SQL migration scripts
shared/    Cross-stack artifacts (validation test vectors used by backend and Angular)
docs/      Design documents
```
