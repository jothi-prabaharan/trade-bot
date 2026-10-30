# Phase 5: Ubuntu Deployment Runbook

One Ubuntu 24.04 LTS server (64 GB / 16 cores, shared) runs the whole stack inside a **16 GB / 4-core budget**: PostgreSQL 16, the API + trading engine, the News and Auth workers, and nginx. Code is built on the server from git; every deploy is a new release directory behind a `current` symlink.

```
Internet ──443──> nginx (TLS, IP allowlist, CSP, rate limits)
                    ├── /            Angular build (current/web)
                    ├── /api, /health ─┐
                    └── /ws (wss)  ────┴─> tradebot-api  127.0.0.1:5080 ──┐
                                           tradebot-news ─────────────────┼─> PostgreSQL 16 (localhost)
                                           tradebot-auth (Chromium) ──────┘
                   └──────────── tradebot.slice: CPUQuota 400%, MemoryMax 16G ────────────┘
```

## 1. Files
| Path in repo | Installed as |
|---|---|
| `deploy/install.sh` | one-time provisioning (idempotent) |
| `deploy/deploy.sh` | build + release + switch + health check + rollback |
| `deploy/bin/tradebotctl`, `tradebot-backup`, `tradebot-restore`, `tradebot-healthcheck` | `/usr/local/sbin/` |
| `deploy/lib/common.sh` | `/usr/local/lib/tradebot/common.sh` |
| `deploy/systemd/*` | `/etc/systemd/system/` (`tradebot.slice` rendered from `install.conf`) |
| `deploy/nginx/*` | `/etc/nginx/sites-available/tradebot.conf`, `tradebot-proxy.conf`, `tradebot-allow.conf` |
| `deploy/postgresql/90-tradebot.conf.in` | `/etc/postgresql/16/main/conf.d/90-tradebot.conf` |
| `deploy/os/*` | chrony, unattended-upgrades, needrestart, fail2ban, journald, sysctl drop-ins |
| `deploy/seed/fno_universe.csv`, `nse_holidays.csv` | imported by `TradeBot.Api seed` on every deploy |
| `deploy/install.conf.example` | `/etc/tradebot/install.conf` (root, 0600) |

| Server path | Content |
|---|---|
| `/etc/tradebot/tradebot.env` | service secrets (root:tradebot 0640): connection string, **master key**, JWT key, admin hash |
| `/etc/tradebot/rclone.conf` | offsite remote (optional) |
| `/opt/tradebot/src` | git checkout |
| `/opt/tradebot/releases/<utc>-<sha>/{api,news,auth,web,seed,deploy,docs}` | last 5 releases |
| `/opt/tradebot/current` | symlink to the live release |
| `/opt/tradebot/ms-playwright` | Chromium for the Auth worker |
| `/var/lib/tradebot` | service state, failed-login screenshots, health-check state |
| `/var/backups/tradebot` | dumps (root 0700) |

## 2. First install
1. **DNS:** an A (and AAAA, if the host has IPv6) record for the dashboard name pointing at the server.
2. **Clone and configure:**
   ```bash
   sudo git clone https://github.com/jothi-prabaharan/trade-bot.git /tmp/trade-bot
   sudo /tmp/trade-bot/deploy/install.sh        # creates /etc/tradebot/install.conf and stops
   sudo nano /etc/tradebot/install.conf          # domain, e-mail, allowed IPs, Telegram ops chat, rclone remote
   sudo /tmp/trade-bot/deploy/install.sh        # provisions + first deploy
   ```
   With the SSH repo URL, the first run prints a deploy key. Add it to GitHub as a **read-only deploy key**, then run the script again.
3. **What the script does:**
   - Sets the time zone to Asia/Kolkata and configures chrony (NPL India + Indian pool).
   - Installs packages: .NET 10 SDK and PostgreSQL 16 from Ubuntu, Node 24 from NodeSource, nginx, certbot, ufw, fail2ban, unattended-upgrades and rclone.
   - Creates the `tradebot` user and directories, writes the PostgreSQL tuning, and creates the role and database.
   - Generates the secrets, firewall rules (22/80/443) and update policy.
   - Clones the repo, obtains a Let's Encrypt certificate (HTTP-01 on port 80), then runs `deploy.sh`.
4. **Set the dashboard password** (prompted if the script runs in a terminal): `sudo tradebotctl set-admin-password`.
5. **Back up the master key.** Copy `Security__MasterKey` from `/etc/tradebot/tradebot.env` into your password manager. Without it, restored backups cannot decrypt API keys, tokens or TOTP secrets.
6. **Offsite backups** (optional but recommended):
   - Run `sudo rclone config --config /etc/tradebot/rclone.conf` and create, for example, a B2/S3/Drive remote wrapped in a **crypt** remote named in `TRADEBOT_RCLONE_REMOTE`.
   - Test it with `sudo tradebotctl backup`.
7. **Configure the bot from the dashboard** (`https://<domain>`):
   - Add accounts (all start in PAPER), with API key/secret, login ID, password, PIN and TOTP secret for the REAL/data-provider account.
   - **Enable TOTP on the Paytm Money account first** (Paytm Money app → security settings → authenticator/TOTP) and save that same secret in the account profile.
   - The first headless login from the server is a new device to Paytm, so it may still send one SMS OTP. You get an alert, and the account card shows "Paytm sent an OTP" with a box: type the code within 3 minutes. The bot ticks "trust this device" and keeps a browser profile per account in `/var/lib/tradebot/browser-profiles`, so later logins use the TOTP only. To make Paytm forget the server, stop `tradebot-auth` and delete that account's `account-<id>` folder.
   - Set the Gemini key and the Telegram channel.
8. **Muhurat session:** add it under *Market holidays* once NSE notifies its timings. Without it the bot treats that Sunday as closed.

## 3. Everyday operations
| Task | Command |
|---|---|
| Overview (services, health, release, newest backup, allowlist, slice memory) | `sudo tradebotctl status` |
| Logs | `sudo tradebotctl logs api -f` (`news`, `auth`, `nginx`, `postgres`, `backup`, `health`) |
| Deploy latest `main` | `sudo tradebotctl deploy` |
| Which build is live | `sudo tradebotctl version` (also `build` in `tradebotctl status`'s health line; login-failure alerts say `(build abc1234)`) |
| Deploy a tag/commit | `sudo tradebotctl deploy --ref v1.2.0` |
| Roll back to the previous release | `sudo tradebotctl rollback` |
| Restart services | `sudo tradebotctl restart` |
| Allow / remove an IP | `sudo tradebotctl allow-ip add 198.51.100.7` · `remove …` · `list` |
| Sign out every browser ("Remember me" included), e.g. after losing a device | `sudo tradebotctl sign-out-all` (also happens on `set-admin-password`) |
| Backup now | `sudo tradebotctl backup` |
| Re-import F&O universe / weights / holidays | `sudo tradebotctl seed` (bundled files) or `--stocks my.csv --holidays my.csv` |
| Renew/enable TLS manually | `sudo tradebotctl tls` (certbot.timer renews automatically) |

**Market-hours guard.** `deploy`, `rollback`, `restart`, `sign-out-all` and a production restore refuse to run on trading days between 09:00 and 15:35 IST (holidays from `mst.market_holidays`), unless you pass `--force`. The engine recovers open brackets from `trn.*` after a restart, but a mid-session restart still drops ticks and pauses the stop-loss/target engine for a few seconds.

**How `deploy.sh` runs:**
1. Fetches `main` and publishes the API and both workers (`-c Release`; only the auth worker carries Playwright's driver).
2. Builds the dashboard and installs/updates Chromium.
3. Installs the units, nginx config and ops scripts (`nginx -t` must pass).
4. Takes a **pre-deploy backup**, then runs `migrate` and `seed` using the *new* binaries.
5. Switches `current` atomically and restarts the services.
6. Waits up to 120 s for `/health`. If the check fails it **rolls back automatically** and alerts on Telegram.
7. Prunes to 5 releases.

Rollback restores code, not schema. Migrations are forward-only and additive, so previous releases keep working against the newer schema.

## 4. Resource budget (16 GB / 4 cores on a 64 GB / 16-core host)
| Unit | Limit | Notes |
|---|---|---|
| `tradebot.slice` | CPUQuota 400 %, MemoryHigh 15 GB, MemoryMax 16 GB | the whole stack; values from `TRADEBOT_CPU_CORES` / `TRADEBOT_MEMORY_GB` |
| PostgreSQL 16/main | inside the slice (`TRADEBOT_PG_IN_SLICE=yes`) | set `no` if the cluster serves other apps |
| tradebot-api | MemoryMax 4 GB | `DOTNET_PROCESSOR_COUNT=4`, server GC with 2 heaps |
| tradebot-news | MemoryMax 1 GB | workstation GC |
| tradebot-auth | MemoryMax 2 GB | headless Chromium at 08:45 |

The host-wide changes are small: `vm.swappiness=10` and `somaxconn`. No overcommit settings are changed, because the host is shared.

**PostgreSQL** (`90-tradebot.conf`, rendered for 16 GB / 4 cores):

| Setting | Value |
|---|---|
| `shared_buffers` | 4GB |
| `effective_cache_size` | 10GB |
| `work_mem` | 32MB |
| `maintenance_work_mem` | 1GB |
| `max_connections` | 100 (3 services × pool 25) |
| Parallel workers | 4 total, 2 per gather |
| `random_page_cost` / `effective_io_concurrency` | 1.1 / 200 (SSD) |
| WAL | `wal_compression=lz4`, `max_wal_size` 8GB, 15-min checkpoints |
| Other | `jit=off`, eager autovacuum for candles/orders, UTC storage with IST log timestamps, `listen_addresses=localhost` |

## 5. Security
- **Network:**
  - ufw allows only 22, 80 and 443.
  - nginx serves the app only to `TRADEBOT_ALLOW_IPS`. Only the ACME challenge path is public, and there is no implicit localhost allowance.
  - The API listens on 127.0.0.1 only.
- **TLS:** Let's Encrypt (TLS 1.2/1.3) with HSTS.
- **Content Security Policy:** `script-src 'self'` with no inline scripts (the Angular build disables critical-CSS inlining for this), `connect-src 'self' wss://<domain>`, `frame-ancestors 'none'`.
- **Rate limits:** nginx allows 10 login attempts a minute (burst 5) and 20 API requests a second (burst 40), both returning 429. These sit on top of the API's own limits.
- **Logs:** the WebSocket location is not access-logged, because its JWT travels in the query string.
- **Caching:** only content-hashed bundles (`main-XXXXXXXX.js`, `chunk-…`, `styles-…`) are `immutable` for a year. Icons, the manifest (`application/manifest+json`), `ngsw-worker.js` and `ngsw.json` revalidate on every request, so a deploy reaches installed apps; `/api/` and `/ws/` are `^~` prefixes so the static rules never capture them.
- **Web Push:** the API, News and Auth services send directly to the browsers' push services (Google FCM, Mozilla, Apple) over outbound HTTPS; nothing inbound is needed. Optionally set `WebPush__Subject='mailto:you@example.com'` in `tradebot.env` (default: the dashboard URL).
- **Secrets:**
  - `tradebot.env` is root:tradebot 0640. The master key encrypts credentials in the database.
  - Passwords are bcrypt-hashed via stdin, so they never appear in the process list.
  - Dumps are root 0700 and contain only ciphertext for credentials.
- **Services:** run as `tradebot` with `ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`, no capabilities, `NoNewPrivileges` and restricted address families. Write access is limited to `/var/lib/tradebot`.
- **fail2ban:** jails for sshd and nginx rate-limit abuse.
- **Updates:** security-only unattended upgrades, installed at 22:30 IST, with any needed reboot at **23:30 IST**. needrestart never restarts services by itself.

## 6. Backups and restore
- **Schedule:** `tradebot-backup.timer` runs at **15:50** (after the close) and **23:45 IST**, and deploys take a `pre-deploy` dump.
- **Format:** `pg_dump --format=custom --schema=mst --schema=trn --no-owner`. Each dump is verified with `pg_restore --list` and gets a `.sha256`.
- **Retention:** kept locally for `TRADEBOT_BACKUP_KEEP_DAYS` (14). Copied to `TRADEBOT_RCLONE_REMOTE` and pruned there after `TRADEBOT_OFFSITE_KEEP_DAYS` (30).
- **Restore drill:** `sudo tradebot-restore /var/backups/tradebot/<file>.dump` restores into a new scratch database and prints row counts. Do this monthly.
- **Production restore:** `sudo tradebot-restore <file> --into-production`. It asks you to type the database name, takes a pre-restore dump, stops the services, replaces `mst` and `trn`, then starts the services.
- **New server:** install with the same `Security__MasterKey` in `tradebot.env` (edit it before the first deploy), then restore.

## 7. Monitoring
`tradebot-healthcheck.timer` runs every minute and sends a Telegram alert when the set of problems changes (failure or recovery). It repeats every 30 minutes while anything is failing. It checks:
- The api/news/auth services, PostgreSQL and nginx are active.
- `/health` answers. In session, the engine is prepared by 09:10, the feed is connected and ticks are under 2 minutes old.
- `pg_isready`.
- Disk usage is under 85 % for `/`, PostgreSQL and the backups.
- chrony is synchronised and the clock offset is under 0.5 s.
- The newest backup is under 26 h old.
- The TLS certificate has more than 14 days left.

Trading alerts (entries, exits, kill switch, login failures) come from the application itself via the Telegram channel set in the dashboard.

## 8. Seed data
- **Indices:** NIFTY (lot 65, weekly), BANKNIFTY (lot 30) and FINNIFTY (lot 60, sentiment only), using the January 2026 lot sizes. The scrip-master sync refreshes lot and freeze sizes daily.
- **Universe:** `fno_universe.csv` lists 220 F&O stocks with NIFTY 50 / BANK NIFTY / FIN NIFTY weights, as of 26 Oct 2026.
  - The weights are **approximate**: each index sums to 100, and Bank/Fin Nifty respect SEBI's 20 % single / 45 % top-3 caps.
  - Verify them against the NSE Indices monthly factsheets and re-import with `tradebotctl seed`.
  - The file is the source of truth for weights: companies it omits get their weights cleared.
- **Automatic universe:** the daily scrip-master sync adds new F&O stocks and marks stocks without live futures/options as not-F&O and untradable. It re-enables them if they return. A file listing fewer than 50 stock underlyings is ignored, so a broken download cannot empty the universe.
- **Patterns:** all 130 detectors. Candlestick and classical are active; harmonic and SMC are inactive. Toggles made in the dashboard are kept.
- **Holidays:** the 15 NSE trading holidays of 2026.
- **Charges:** option charge rates (STT 0.1 % sell, exchange 0.03503 %, SEBI 0.0001 %, stamp 0.003 % buy, GST 18 %, ₹20 brokerage).
- **Defaults:** default strategy and Gemini rows.

## 9. Go-live checklist
1. Run at least a week in PAPER on the server. Watch the 08:45 login, 08:50 preparation, feed, signals and 15:15 square-off, and get Telegram alerts working.
2. Confirm the scrip-master sync worked: lot sizes are filled in and `tradebotctl status` is healthy before 09:15.
3. On each REAL/data-provider account card, open **Paytm**:
   - Every setup line should be green.
   - **Compare** the code from your authenticator app: it should say *Matches*.
   - After the 08:45 login, **Check connection** should show your name and Paytm client ID.
4. On the first REAL account, set `max_lots_per_trade = 1` and keep the daily loss limit small.
5. Switch the account to REAL from its card (typed client ID) **after** the close; the first REAL session runs the next morning.
6. Run a restore drill and check that offsite copies appear.

## 10. Validation done before release (in a container similar to the target)
| Check | Result |
|---|---|
| ShellCheck on all scripts | clean |
| `systemd-analyze verify` on all units and the rendered slice | clean (ops scripts only exist after install) |
| PostgreSQL 16 restarted with the rendered 16 GB / 4-core tuning | `shared_buffers` 4 GB, `effective_cache_size` 10 GB, `work_mem` 32 MB, `jit` off |
| nginx 1.24 with the rendered site (self-signed stand-in certificate) | `nginx -t` passes. Verified: HTTP→HTTPS redirect, HTTP/2, headers, immutable assets, SPA fallback, 401 without token, 403 for non-allowlisted IPs while ACME stays reachable, 429 rate limiting, invalid allowlist entries rejected |
| Production Angular build through nginx in Chromium | **0 CSP violations, 0 console errors**: login, live `wss://` socket, charts and drawers working |
| PWA through nginx in Chromium (persistent profile) | Manifest and installability: no errors, *Install app* offered; service worker controls the page; API responses live (not cached); offline reload opens the shell; a release swapped under an open dashboard shows *new version · Reload* and loads it only on Reload; the server's push payload shows as a notification with icon, badge and require-interaction. A real browser subscription needs Google's push service, which the validation sandbox blocks; the drawer then reports it after 30 s. |
| Backup → scratch restore → `migrate` | identical row counts; migration history intact; tampered dump rejected by checksum |
| `migrate` / `seed` CLI on an empty database, twice | 4 migrations, 3 indices + 220 stocks + 130 patterns + 15 holidays + 2 charge rates; second run changes nothing |
| Release publish | API and news 11 MB each (no Playwright driver); auth carries the Linux driver |
| Test suites | backend 293 + 108 + 17, Vitest 116, Playwright 16, all passing |

Bugs found and fixed during validation:
- `http2 on;` needs nginx 1.25.1+, so the 1.24 `listen … http2` form is used.
- A duplicate `proxy_read_timeout` made nginx reject the `/ws` location.
- IPv6 listeners broke on hosts without IPv6; they are now rendered only when the host supports it.
- Angular's critical-CSS inline script would have been blocked by the CSP.
- A strict-overcommit sysctl was wrong for a shared host and was removed.
- The allowlist no longer includes an implicit localhost entry.
- The old static rule cached every `.js`/`.png` for a year, which would have pinned an unhashed service worker; only hashed files are immutable now. nginx also needs the `{8}` regex quoted.
