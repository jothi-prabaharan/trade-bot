#!/usr/bin/env bash
# One-time (and idempotent) provisioning of an Ubuntu 24.04 server for TradeBot. Run as root from a checkout:
#   sudo ./deploy/install.sh            first run copies install.conf.example to /etc/tradebot/install.conf and stops
#   sudo ./deploy/install.sh            second run (after editing it) provisions everything and deploys main
# Re-running is safe: existing secrets, database, certificates and settings are kept.
set -euo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"
require_root

. /etc/os-release
[ "${ID:-}" = ubuntu ] && [ "${VERSION_ID:-}" = "24.04" ] || [ "${TRADEBOT_ANY_OS:-no}" = yes ] ||
  die "Ubuntu 24.04 LTS expected (found ${PRETTY_NAME:-unknown}). Set TRADEBOT_ANY_OS=yes to override."

install -d -m 0750 "$TRADEBOT_ETC"
if [ ! -f "$TRADEBOT_CONF_FILE" ]; then
  install -m 0600 "$HERE/install.conf.example" "$TRADEBOT_CONF_FILE"
  die "Created $TRADEBOT_CONF_FILE: set the domain, e-mail, allowed IPs and Telegram ops chat, then run this again."
fi
load_conf
[ "$TRADEBOT_DOMAIN" != bot.example.com ] || die "Set TRADEBOT_DOMAIN in $TRADEBOT_CONF_FILE first."

log "Time zone and clock (Asia/Kolkata, chrony)"
timedatectl set-timezone Asia/Kolkata

log "Packages"
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -yq ca-certificates curl gnupg git jq unzip rsync openssl chrony ufw fail2ban unattended-upgrades needrestart \
  nginx certbot python3-certbot-nginx "postgresql-$TRADEBOT_PG_VERSION" postgresql-client-common rclone dotnet-sdk-10.0 aspnetcore-runtime-10.0
if ! command -v node > /dev/null || ! node -e 'const [a,b,c]=process.versions.node.split(".").map(Number);process.exit(a>22||(a===22&&(b>22||(b===22&&c>=3)))?0:1)'; then
  install -d -m 0755 /etc/apt/keyrings
  curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor --yes -o /etc/apt/keyrings/nodesource.gpg
  echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_24.x nodistro main" > /etc/apt/sources.list.d/nodesource.list
  apt-get update -q && apt-get install -yq nodejs
fi

install -m 0644 "$HERE/os/chrony-tradebot.conf" /etc/chrony/conf.d/tradebot.conf
systemctl restart chrony

log "Service user and directories"
id "$TRADEBOT_USER" > /dev/null 2>&1 || useradd --system --home-dir "$TRADEBOT_STATE" --shell /usr/sbin/nologin "$TRADEBOT_USER"
install -d -m 0751 "$TRADEBOT_HOME" "$TRADEBOT_RELEASES"
install -d -m 0750 -o "$TRADEBOT_USER" -g "$TRADEBOT_USER" "$TRADEBOT_STATE" "$TRADEBOT_STATE/auth-failures"
install -d -m 0700 "$TRADEBOT_BACKUPS"
chgrp "$TRADEBOT_USER" "$TRADEBOT_ETC"

log "PostgreSQL $TRADEBOT_PG_VERSION tuning (${TRADEBOT_MEMORY_GB} GB / ${TRADEBOT_CPU_CORES} cores budget)"
m=$TRADEBOT_MEMORY_GB c=$TRADEBOT_CPU_CORES
PG_SHARED_BUFFERS="$(( m / 4 ))GB"
PG_EFFECTIVE_CACHE="$(( m * 5 / 8 ))GB"
PG_MAINTENANCE_MEM="$(( m >= 32 ? 2048 : m * 64 ))MB"
PG_WORK_MEM="$(( m * 2 ))MB"
PG_MAX_WORKERS=$(( c * 2 > 8 ? c * 2 : 8 ))
PG_PARALLEL_PER_GATHER=$(( c / 2 > 1 ? c / 2 : 1 ))
export PG_SHARED_BUFFERS PG_EFFECTIVE_CACHE PG_MAINTENANCE_MEM PG_WORK_MEM PG_MAX_WORKERS PG_PARALLEL_PER_GATHER
render "$HERE/postgresql/90-tradebot.conf.in" "/etc/postgresql/$TRADEBOT_PG_VERSION/main/conf.d/90-tradebot.conf" \
  TRADEBOT_MEMORY_GB TRADEBOT_CPU_CORES PG_SHARED_BUFFERS PG_EFFECTIVE_CACHE PG_MAINTENANCE_MEM PG_WORK_MEM PG_MAX_WORKERS PG_PARALLEL_PER_GATHER

log "Secrets ($TRADEBOT_ENV_FILE)"
if [ ! -f "$TRADEBOT_ENV_FILE" ]; then
  db_password=$(openssl rand -hex 24)
  umask 027
  cat > "$TRADEBOT_ENV_FILE" <<ENV
# TradeBot service environment (systemd EnvironmentFile). Values are single-quoted; keep it that way.
# Security__MasterKey encrypts API keys, tokens and TOTP secrets in the database: back it up OFF the server.
# Losing it makes those columns unreadable (re-enter credentials); leaking it exposes them together with a dump.
ConnectionStrings__TradeBot='Host=localhost;Port=5432;Database=$TRADEBOT_DB;Username=$TRADEBOT_USER;Password=$db_password;Maximum Pool Size=25;Timeout=15;Command Timeout=60;Keepalive=30'
Security__MasterKey='$(openssl rand -base64 32)'
Auth__AdminUser='admin'
Auth__AdminPasswordHash=''
Auth__JwtKey='$(openssl rand -base64 48)'
Auth__TokenHours='8'
Cors__Origins__0='https://$TRADEBOT_DOMAIN'
Database__MigrateOnStartup='false'
Engine__Enabled='true'
BrokerLogin__ScreenshotDirectory='$TRADEBOT_STATE/auth-failures'
ENV
  chown root:"$TRADEBOT_USER" "$TRADEBOT_ENV_FILE"
  chmod 0640 "$TRADEBOT_ENV_FILE"
  new_secrets=yes
else
  db_password=$(sed -n "s/^ConnectionStrings__TradeBot=.*Password=\([^;']*\).*/\1/p" "$TRADEBOT_ENV_FILE")
  new_secrets=no
fi

log "Database role and database"
systemctl enable --now "postgresql@${TRADEBOT_PG_VERSION}-main"
if runuser -u postgres -- psql -X -tAc "SELECT 1 FROM pg_roles WHERE rolname = '$TRADEBOT_USER'" | grep -q 1; then
  runuser -u postgres -- psql -X -q -c "ALTER ROLE \"$TRADEBOT_USER\" WITH LOGIN PASSWORD '$db_password'"
else
  runuser -u postgres -- psql -X -q -c "CREATE ROLE \"$TRADEBOT_USER\" WITH LOGIN PASSWORD '$db_password'"
fi
runuser -u postgres -- psql -X -tAc "SELECT 1 FROM pg_database WHERE datname = '$TRADEBOT_DB'" | grep -q 1 ||
  runuser -u postgres -- createdb -O "$TRADEBOT_USER" -E UTF8 "$TRADEBOT_DB"

log "Systemd units, slice and ops scripts"
install_ops_files "$HERE"
systemctl restart "postgresql@${TRADEBOT_PG_VERSION}-main"   # applies tuning and the slice placement
runuser -u postgres -- pg_isready -q -t 30 || die "PostgreSQL did not come back after tuning; check journalctl -u postgresql@${TRADEBOT_PG_VERSION}-main"

log "Firewall, fail2ban, updates, journald, sysctl"
ufw default deny incoming > /dev/null
ufw default allow outgoing > /dev/null
ufw allow OpenSSH > /dev/null
ufw allow 80/tcp > /dev/null
ufw allow 443/tcp > /dev/null
ufw --force enable > /dev/null
install -m 0644 "$HERE/os/fail2ban-tradebot.local" /etc/fail2ban/jail.d/tradebot.local
systemctl restart fail2ban
install -m 0644 "$HERE/os/52tradebot-unattended-upgrades" /etc/apt/apt.conf.d/52tradebot-unattended-upgrades
install -m 0644 "$HERE/os/20auto-upgrades" /etc/apt/apt.conf.d/20auto-upgrades
install -d /etc/needrestart/conf.d && install -m 0644 "$HERE/os/needrestart-tradebot.conf" /etc/needrestart/conf.d/tradebot.conf
install -d /etc/systemd/journald.conf.d && install -m 0644 "$HERE/os/journald-tradebot.conf" /etc/systemd/journald.conf.d/tradebot.conf
systemctl restart systemd-journald
install -m 0644 "$HERE/os/sysctl-tradebot.conf" /etc/sysctl.d/90-tradebot.conf
sysctl -q --system
systemctl daemon-reload
systemctl restart apt-daily-upgrade.timer

log "Source repository ($TRADEBOT_REPO_URL)"
if [[ $TRADEBOT_REPO_URL == git@* ]]; then
  key=/root/.ssh/tradebot_deploy
  if [ ! -f "$key" ]; then
    install -d -m 0700 /root/.ssh
    ssh-keygen -q -t ed25519 -N "" -C "tradebot-deploy@$(hostname -s)" -f "$key"
    ssh-keyscan -t ed25519 github.com >> /root/.ssh/known_hosts 2> /dev/null
  fi
  export GIT_SSH_COMMAND="ssh -i $key -o IdentitiesOnly=yes"
  git config --system core.sshCommand "ssh -i $key -o IdentitiesOnly=yes"
fi
if [ ! -d "$TRADEBOT_SRC/.git" ]; then
  if ! git clone --quiet --branch "$TRADEBOT_BRANCH" "$TRADEBOT_REPO_URL" "$TRADEBOT_SRC"; then
    [ -f /root/.ssh/tradebot_deploy.pub ] && { echo; echo "Add this read-only deploy key to the GitHub repository, then run install.sh again:"; cat /root/.ssh/tradebot_deploy.pub; }
    die "Could not clone $TRADEBOT_REPO_URL"
  fi
fi

log "TLS for $TRADEBOT_DOMAIN"
install_nginx_site "$HERE"
if [ ! -f "/etc/letsencrypt/live/$TRADEBOT_DOMAIN/fullchain.pem" ]; then
  if certbot certonly --webroot -w /var/www/letsencrypt -d "$TRADEBOT_DOMAIN" -m "$TRADEBOT_ACME_EMAIL" --agree-tos -n \
       --deploy-hook "systemctl reload nginx"; then
    install_nginx_site "$HERE"
  else
    warn "Certificate request failed (is DNS for $TRADEBOT_DOMAIN pointing here?). Fix it, then run: tradebotctl tls"
  fi
fi

log "First deploy"
"$TRADEBOT_SRC/deploy/deploy.sh" --force

if grep -q "^Auth__AdminPasswordHash=''" "$TRADEBOT_ENV_FILE"; then
  if [ -t 0 ]; then tradebotctl set-admin-password
  else warn "No admin password yet: run 'tradebotctl set-admin-password'"; fi
fi

echo
log "TradeBot is installed: https://$TRADEBOT_DOMAIN (allowed: ${TRADEBOT_ALLOW_IPS:-nobody, see tradebotctl allow-ip})"
if [ "$new_secrets" = yes ]; then
  warn "Store Security__MasterKey from $TRADEBOT_ENV_FILE in your password manager now. Backups are useless for credentials without it."
fi
[ -n "$TRADEBOT_RCLONE_REMOTE" ] && [ ! -f "$TRADEBOT_ETC/rclone.conf" ] &&
  warn "Offsite backups: run 'rclone config --config $TRADEBOT_ETC/rclone.conf' to create remote ${TRADEBOT_RCLONE_REMOTE%%:*}"
log "Status: tradebotctl status"
