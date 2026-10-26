#!/usr/bin/env bash
# Builds a release from git on this server and switches to it.
#   deploy.sh [--ref <git-ref>] [--force] [--no-fetch]
#     --ref       branch, tag or commit (default: origin/$TRADEBOT_BRANCH)
#     --force     allow restarting services during market hours (09:00–15:35 IST)
#     --no-fetch  build the checked-out working tree as is
# Steps: fetch → publish .NET + build Angular → Chromium for the auth worker → ops files → pre-deploy backup →
# migrate + seed with the new binaries → switch current → restart → health check (automatic rollback on failure).
set -euo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

ref="" force=no fetch=yes
while [ $# -gt 0 ]; do
  case $1 in
    --ref) ref=${2:?--ref needs a value}; shift 2 ;;
    --force) force=yes; shift ;;
    --no-fetch) fetch=no; shift ;;
    -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
    *) die "Unknown option $1" ;;
  esac
done

require_root
load_conf
exec 9> /run/lock/tradebot-deploy.lock
flock -n 9 || die "Another deploy is running."
if [ "$force" = no ] && market_hours; then
  die "Market hours (09:00–15:35 IST): restarting now would interrupt live brackets. Deploy after the close or pass --force."
fi

log "Source"
if [ "$fetch" = yes ]; then
  git -C "$TRADEBOT_SRC" fetch --prune --tags origin
  git -C "$TRADEBOT_SRC" checkout --force --detach "${ref:-origin/$TRADEBOT_BRANCH}"
fi
sha=$(git -C "$TRADEBOT_SRC" rev-parse --short=10 HEAD)
release="$TRADEBOT_RELEASES/$(date -u +%Y%m%d%H%M%S)-$sha"
install -d -m 0751 "$TRADEBOT_HOME" "$TRADEBOT_RELEASES"
install -d -m 0751 "$release"
cleanup_failed_build() {
  if [ "$(readlink -f "$TRADEBOT_CURRENT" 2>/dev/null)" != "$release" ]; then rm -rf "$release"; fi
}
trap cleanup_failed_build ERR

log "Build .NET ($sha)"
# shellcheck disable=SC2031  # run_as_service sets these in its own subshell only
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Only the auth worker launches Chromium; the others skip Playwright's ~130 MB driver
for p in Api:api:none Worker.News:news:none Worker.Auth:auth:linux-x64; do
  IFS=: read -r project dir platform <<< "$p"
  dotnet publish "$TRADEBOT_SRC/backend/src/TradeBot.$project" -c Release -o "$release/$dir" --nologo -v q \
    -p:ContinuousIntegrationBuild=true -p:PlaywrightPlatform="$platform"
done

log "Build dashboard"
( cd "$TRADEBOT_SRC/frontend" && npm ci --no-audit --no-fund --loglevel=error && npm run build -- --configuration production )
cp -a "$TRADEBOT_SRC/frontend/dist/tradebot-dashboard/browser" "$release/web"
cp -a "$TRADEBOT_SRC/deploy" "$release/deploy"
cp -a "$TRADEBOT_SRC/deploy/seed" "$release/seed"
cp -a "$TRADEBOT_SRC/docs" "$release/docs"
echo "$sha" > "$release/REVISION"
chown -R root:"$TRADEBOT_USER" "$release"
chmod -R u=rwX,g=rX,o= "$release"
chmod 0751 "$release"
chmod -R o+rX "$release/web"   # nginx (www-data) serves the dashboard

log "Chromium for the auth worker"
install -d -m 0750 -o root -g "$TRADEBOT_USER" "$TRADEBOT_BROWSERS"
PLAYWRIGHT_BROWSERS_PATH="$TRADEBOT_BROWSERS" "$release/auth/.playwright/node/linux-x64/node" \
  "$release/auth/.playwright/package/cli.js" install --with-deps chromium > /dev/null
chmod -R g+rX "$TRADEBOT_BROWSERS"

log "Ops files (systemd, nginx, scripts)"
install_ops_files "$release/deploy"
install_nginx_site "$release/deploy"

log "Database: backup, migrate, seed"
if psql_db -tAc "SELECT 1 FROM information_schema.schemata WHERE schema_name = 'trn'" | grep -q 1; then
  tradebot-backup pre-deploy
fi
run_as_service /usr/bin/dotnet "$release/api/TradeBot.Api.dll" migrate
run_as_service /usr/bin/dotnet "$release/api/TradeBot.Api.dll" seed --stocks "$release/seed/fno_universe.csv" --holidays "$release/seed/nse_holidays.csv"

log "Switch and restart"
previous=$(readlink -f "$TRADEBOT_CURRENT" 2>/dev/null || true)
switch_release "$release"
trap - ERR
systemctl enable --quiet tradebot-api tradebot-news tradebot-auth tradebot-backup.timer tradebot-healthcheck.timer
systemctl start --quiet tradebot-backup.timer tradebot-healthcheck.timer
# shellcheck disable=SC2086
systemctl restart $TRADEBOT_SERVICES

if ! wait_healthy 120; then
  warn "New release is not healthy."
  if [ -n "$previous" ] && [ -d "$previous" ] && [ "$previous" != "$release" ]; then
    switch_release "$previous"
    # shellcheck disable=SC2086
    systemctl restart $TRADEBOT_SERVICES
    notify "Deploy of $sha FAILED health check; rolled back to $(cat "$previous/REVISION" 2>/dev/null)"
    die "Rolled back to $previous. Logs: journalctl -u tradebot-api -n 200"
  fi
  notify "Deploy of $sha FAILED health check (no previous release to roll back to)"
  die "Logs: journalctl -u tradebot-api -n 200"
fi

log "Prune old releases (keep 5)"
find "$TRADEBOT_RELEASES" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\n' | sort -rn | tail -n +6 | cut -d' ' -f2- |
  while read -r old; do [ "$old" = "$release" ] || [ "$old" = "$previous" ] || rm -rf "$old"; done

notify "Deployed $sha"
log "Deployed $sha → $release"
