# Shared helpers for install.sh, deploy.sh, tradebotctl and the ops jobs. Source, don't execute.
# Every path can be overridden through the environment (used by the sandbox dry-run in the docs).

: "${TRADEBOT_ETC:=/etc/tradebot}"
: "${TRADEBOT_HOME:=/opt/tradebot}"
: "${TRADEBOT_STATE:=/var/lib/tradebot}"
: "${TRADEBOT_BACKUPS:=/var/backups/tradebot}"
: "${TRADEBOT_USER:=tradebot}"
: "${TRADEBOT_DB:=tradebot}"
: "${TRADEBOT_PG_VERSION:=16}"
: "${TRADEBOT_SERVICES:=tradebot-api tradebot-news tradebot-auth}"
: "${TRADEBOT_API_PORT:=5080}"

# shellcheck disable=SC2034  # used by the scripts that source this file
TRADEBOT_SRC="$TRADEBOT_HOME/src"
# shellcheck disable=SC2034
TRADEBOT_RELEASES="$TRADEBOT_HOME/releases"
TRADEBOT_CURRENT="$TRADEBOT_HOME/current"
# shellcheck disable=SC2034
TRADEBOT_BROWSERS="$TRADEBOT_HOME/ms-playwright"
TRADEBOT_ENV_FILE="$TRADEBOT_ETC/tradebot.env"
TRADEBOT_CONF_FILE="$TRADEBOT_ETC/install.conf"

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mWARN:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

require_root() { [ "$(id -u)" -eq 0 ] || die "Run as root (sudo)."; }

load_conf() {
  [ -f "$TRADEBOT_CONF_FILE" ] || die "$TRADEBOT_CONF_FILE not found. Copy deploy/install.conf.example there and edit it."
  # shellcheck disable=SC1090
  . "$TRADEBOT_CONF_FILE"
  : "${TRADEBOT_DOMAIN:?TRADEBOT_DOMAIN is not set in $TRADEBOT_CONF_FILE}"
  : "${TRADEBOT_BRANCH:=main}"
  : "${TRADEBOT_CPU_CORES:=4}"
  : "${TRADEBOT_MEMORY_GB:=16}"
  : "${TRADEBOT_PG_IN_SLICE:=yes}"
  : "${TRADEBOT_BACKUP_KEEP_DAYS:=14}"
  : "${TRADEBOT_OFFSITE_KEEP_DAYS:=30}"
  : "${TRADEBOT_RCLONE_REMOTE:=}"
  : "${TRADEBOT_ALLOW_IPS:=}"
  : "${TRADEBOT_OPS_TELEGRAM_TOKEN:=}"
  : "${TRADEBOT_OPS_TELEGRAM_CHAT:=}"
}

# Telegram ops alert; silently skipped when not configured. Never fails the caller.
notify() {
  [ -n "${TRADEBOT_OPS_TELEGRAM_TOKEN:-}" ] && [ -n "${TRADEBOT_OPS_TELEGRAM_CHAT:-}" ] || return 0
  curl -fsS --max-time 10 -o /dev/null "https://api.telegram.org/bot${TRADEBOT_OPS_TELEGRAM_TOKEN}/sendMessage" \
    --data-urlencode "chat_id=${TRADEBOT_OPS_TELEGRAM_CHAT}" \
    --data-urlencode "text=[$(hostname -s)] $*" || warn "Telegram alert failed"
  return 0
}

# Runs a command as the service user with the service environment (connection string, master key, ...).
run_as_service() {
  ( set -a; # shellcheck disable=SC1090
    . "$TRADEBOT_ENV_FILE"; set +a
    export HOME="$TRADEBOT_STATE" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    cd /
    exec runuser -u "$TRADEBOT_USER" --preserve-environment -- "$@" )
}

psql_db() { runuser -u postgres -- psql -X -v ON_ERROR_STOP=1 -d "$TRADEBOT_DB" "$@"; }

# True on NSE trading days between 09:00 and 15:35 IST (holidays from mst.market_holidays when reachable).
market_hours() {
  local dow now
  dow=$(TZ=Asia/Kolkata date +%u)
  now=$(TZ=Asia/Kolkata date +%H%M)
  [ "$dow" -le 5 ] || return 1
  [ "$now" -ge 0900 ] && [ "$now" -le 1535 ] || return 1
  if psql_db -tAc "SELECT 1 FROM mst.market_holidays WHERE holiday_date = (now() AT TIME ZONE 'Asia/Kolkata')::date AND NOT is_special_session" 2>/dev/null | grep -q 1; then
    return 1
  fi
  return 0
}

# Renders a template replacing @@KEY@@ with the value of $KEY for the listed keys.
render() {
  local template=$1 out=$2; shift 2
  local content key
  content=$(cat "$template")
  for key in "$@"; do content=${content//@@${key}@@/${!key}}; done
  printf '%s\n' "$content" > "$out"
}

# nginx allowlist from TRADEBOT_ALLOW_IPS (validated IPv4/IPv6 addresses or CIDRs).
write_allowlist() {
  local out=${1:-/etc/nginx/tradebot-allow.conf} ip
  {
    echo "# Generated from TRADEBOT_ALLOW_IPS in $TRADEBOT_CONF_FILE (tradebotctl allow-ip). Do not edit."
    for ip in $TRADEBOT_ALLOW_IPS; do
      valid_cidr "$ip" || die "Invalid IP/CIDR in TRADEBOT_ALLOW_IPS: $ip"
      echo "allow $ip;"
    done
    # No implicit localhost: a local tunnel/proxy in front of nginx would otherwise bypass the list.
    # Local checks talk to the API on 127.0.0.1:$TRADEBOT_API_PORT directly.
    echo "deny all;"
  } > "$out"
}

valid_cidr() {
  [[ $1 =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}(/([0-9]|[12][0-9]|3[0-2]))?$ ]] || [[ $1 =~ ^[0-9a-fA-F:]+:[0-9a-fA-F:]*(/[0-9]{1,3})?$ ]]
}

# Renders the full TLS site when a certificate exists, else the ACME-only bootstrap site; validates and reloads nginx.
install_nginx_site() {
  local deploy_dir=$1 site=/etc/nginx/sites-available/tradebot.conf
  install -m 0644 "$deploy_dir/nginx/tradebot-proxy.conf" /etc/nginx/tradebot-proxy.conf
  install -d -m 0755 /var/www/letsencrypt
  write_allowlist /etc/nginx/tradebot-allow.conf
  # IPv6 listeners only where the kernel has IPv6 (some VPS images disable it and nginx would not start)
  if [ -f /proc/net/if_inet6 ]; then LISTEN6_80="listen [::]:80;" LISTEN6_443="listen [::]:443 ssl http2;"; else LISTEN6_80="" LISTEN6_443=""; fi
  export LISTEN6_80 LISTEN6_443
  if [ -f "/etc/letsencrypt/live/$TRADEBOT_DOMAIN/fullchain.pem" ]; then
    render "$deploy_dir/nginx/tradebot.conf.in" "$site" TRADEBOT_DOMAIN TRADEBOT_API_PORT LISTEN6_80 LISTEN6_443
  else
    render "$deploy_dir/nginx/tradebot-bootstrap.conf.in" "$site" TRADEBOT_DOMAIN LISTEN6_80
  fi
  ln -sfn "$site" /etc/nginx/sites-enabled/tradebot.conf
  rm -f /etc/nginx/sites-enabled/default
  nginx -t -q || die "nginx configuration test failed"
  systemctl reload nginx
}

# Units, slice, drop-ins, ops scripts and the shared library from a deploy/ directory.
install_ops_files() {
  local deploy_dir=$1
  install -d -m 0755 /usr/local/lib/tradebot
  install -m 0644 "$deploy_dir/lib/common.sh" /usr/local/lib/tradebot/common.sh
  install -m 0755 "$deploy_dir"/bin/* /usr/local/sbin/

  CPU_QUOTA="$(( TRADEBOT_CPU_CORES * 100 ))%"
  MEMORY_MAX="${TRADEBOT_MEMORY_GB}G"
  MEMORY_HIGH="$(( TRADEBOT_MEMORY_GB * 1024 * 15 / 16 ))M"
  export CPU_QUOTA MEMORY_MAX MEMORY_HIGH
  render "$deploy_dir/systemd/tradebot.slice.in" /etc/systemd/system/tradebot.slice TRADEBOT_CPU_CORES TRADEBOT_MEMORY_GB CPU_QUOTA MEMORY_MAX MEMORY_HIGH
  local unit
  for unit in tradebot-api.service tradebot-news.service tradebot-auth.service tradebot-backup.service tradebot-backup.timer \
              tradebot-healthcheck.service tradebot-healthcheck.timer; do
    install -m 0644 "$deploy_dir/systemd/$unit" "/etc/systemd/system/$unit"
  done
  local pg_dropin="/etc/systemd/system/postgresql@${TRADEBOT_PG_VERSION}-main.service.d"
  if [ "$TRADEBOT_PG_IN_SLICE" = yes ]; then
    install -d "$pg_dropin" && install -m 0644 "$deploy_dir/systemd/postgresql-slice.conf" "$pg_dropin/tradebot-slice.conf"
  else
    rm -f "$pg_dropin/tradebot-slice.conf"
  fi
  install -d /etc/systemd/system/apt-daily-upgrade.timer.d
  install -m 0644 "$deploy_dir/systemd/apt-after-hours.conf" /etc/systemd/system/apt-daily-upgrade.timer.d/tradebot.conf
  systemctl daemon-reload
}

# Polls the API health endpoint until it answers or the timeout (seconds) passes.
wait_healthy() {
  local timeout=${1:-90} waited=0
  until curl -fsS --max-time 3 "http://127.0.0.1:${TRADEBOT_API_PORT}/health" > /dev/null 2>&1; do
    sleep 2; waited=$((waited + 2))
    [ "$waited" -lt "$timeout" ] || return 1
  done
}

# Atomically points current at a release directory.
switch_release() {
  ln -sfn "$1" "$TRADEBOT_HOME/current.next"
  mv -Tf "$TRADEBOT_HOME/current.next" "$TRADEBOT_CURRENT"
}
