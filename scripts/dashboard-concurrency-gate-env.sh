#!/usr/bin/env bash
# Throwaway environment for the load-dashboard concurrency gate (#381 cut B): sets up its own Docker
# network, Postgres (pg_stat_statements loaded) and Redis, migrates and seeds, builds and starts the
# Api under production limits (--cpus=1 --memory=512m), runs scripts/dashboard-concurrency-gate.sh
# and scripts/dashboard-concurrency-gate-eval.mjs, and removes everything again on any exit (trap).
# It never touches the compose dev stack and never runs docker compose.
#
# Admin auth: the dashboard route needs a global-admin cookie. Mint one beforehand with throwaway
# tooling outside the repo: a cookie-auth ticket (scheme "Cookies") protected with an ASP.NET Core
# Data Protection key ring under application name "EmotePurge" and purpose
# "Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware"/"Cookies"/"v2",
# carrying the claims NameIdentifier = ADMIN_TWITCH_USER_ID, twitch:login and
# twitch:session_issued_at (round-trip UTC). The key ring directory is mounted into the Api as
# /keys and must be readable by the container user. This script adds the matching Users row and
# the admin allowlist entry. A browser login is no alternative: the registered Twitch OAuth redirect
# points at localhost:5151, not at this container.
#
# Environment:
#   CADENCE                (required) sampling cadence of the seed and the Api sampler in seconds (1 or 30)
#   GATE_ADMIN_COOKIE_FILE (required) file holding the minted .AspNetCore.Cookies value
#   DP_KEYRING_DIR         (required) Data Protection key ring the cookie was minted with
#   ADMIN_TWITCH_USER_ID   (required) the NameIdentifier claim of the cookie
#   ADMIN_TWITCH_LOGIN     default gate-admin
#   PROBE_OFFSET_MS        default 50 for CADENCE <= 5, else 8 (a warm 30-s 24h rebuild lasts 10-55 ms
#                          and starts as the requests arrive)
#   MAIN_TICKS             default from the gate script (384), but 480 for CADENCE > 5
#   PREFIX                 default dashboard-gate (container, network and image names)
#   API_HOST_PORT          default 18080; PG_HOST_PORT default 15499 (both bound to 127.0.0.1)
#   KEEP_IMAGE=1           keep the built Api image
#   OUT                    default ${TMPDIR:-/tmp}/dashboard-gate-<cadence>s-<UTC timestamp>.csv
#   MAIN_TICKS, COOLDOWN_TICKS, DUAL_CYCLES, CLIENTS, SNAPSHOT_OFFSET_MS  passed through to the gate
#
# Exit code: the evaluation's (0 passed, 1 failed, 2 invalid), or the gate script's if it aborted.
# Usage: CADENCE=1 GATE_ADMIN_COOKIE_FILE=... DP_KEYRING_DIR=... ADMIN_TWITCH_USER_ID=... \
#          scripts/dashboard-concurrency-gate-env.sh

set -euo pipefail

: "${CADENCE:?set CADENCE (1 or 30)}"
: "${GATE_ADMIN_COOKIE_FILE:?set GATE_ADMIN_COOKIE_FILE}"
: "${DP_KEYRING_DIR:?set DP_KEYRING_DIR}"
: "${ADMIN_TWITCH_USER_ID:?set ADMIN_TWITCH_USER_ID}"
ADMIN_TWITCH_LOGIN="${ADMIN_TWITCH_LOGIN:-gate-admin}"
PREFIX="${PREFIX:-dashboard-gate}"
API_HOST_PORT="${API_HOST_PORT:-18080}"
PG_HOST_PORT="${PG_HOST_PORT:-15499}"
# A coarse cadence makes the 24h rebuild short (10-55 ms, starting right as the requests arrive), so
# the probe has to follow the requests closely, and more main ticks are needed to collect 40 probes
# inside rebuilds (measured 2026-10-12: offset 20 ms and 384 ticks gave only 27).
if [[ -z "${PROBE_OFFSET_MS:-}" ]]; then
  if ((CADENCE <= 5)); then PROBE_OFFSET_MS=50; else PROBE_OFFSET_MS=8; fi
fi
if [[ -z "${MAIN_TICKS:-}" ]] && ((CADENCE > 5)); then
  MAIN_TICKS=480
fi
export PROBE_OFFSET_MS ${MAIN_TICKS:+MAIN_TICKS}
export OUT="${OUT:-${TMPDIR:-/tmp}/dashboard-gate-${CADENCE}s-$(date -u +%Y%m%dT%H%M%SZ).csv}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
NETWORK="$PREFIX-net"
PG="$PREFIX-pg"
REDIS="$PREFIX-redis"
API="$PREFIX-api"
IMAGE="$PREFIX-api:local"
[[ -d "$DP_KEYRING_DIR" ]] || { echo "DP_KEYRING_DIR $DP_KEYRING_DIR is not a directory" >&2; exit 64; }
[[ -s "$GATE_ADMIN_COOKIE_FILE" ]] || { echo "GATE_ADMIN_COOKIE_FILE is missing or empty" >&2; exit 64; }
DP_KEYRING_DIR="$(cd "$DP_KEYRING_DIR" && pwd)"

teardown() {
  local status=$?
  # The Api log helps only when setup or measurement broke, not when the evaluation said no.
  if ((status != 0 && EVALUATED == 0)) && docker container inspect "$API" > /dev/null 2>&1; then
    echo "--- last Api log lines ---" >&2
    docker logs --tail 40 "$API" >&2 2>&1 || true
  fi
  # -v removes the anonymous data volumes of the postgres and redis images as well.
  docker rm -f -v "$API" "$PG" "$REDIS" > /dev/null 2>&1 || true
  docker network rm "$NETWORK" > /dev/null 2>&1 || true
  if [[ "${KEEP_IMAGE:-}" != 1 ]]; then
    docker image rm "$IMAGE" > /dev/null 2>&1 || true
  fi
  echo "Throwaway gate environment removed." >&2
  exit "$status"
}
EVALUATED=0

wait_for() {
  local what="$1" seconds="$2"
  shift 2
  for ((i = 0; i < seconds; i++)); do
    if "$@" > /dev/null 2>&1; then return 0; fi
    sleep 1
  done
  echo "timed out after ${seconds}s waiting for $what" >&2
  return 1
}

# Secrets travel by name (-e VAR without a value), never on a command line.
export PG_PASSWORD REDIS_PASSWORD
PG_PASSWORD="$(openssl rand -hex 16)"
REDIS_PASSWORD="$(openssl rand -hex 16)"
export POSTGRES_DB=emotepurge POSTGRES_USER=emotepurge POSTGRES_PASSWORD="$PG_PASSWORD"

# The network is the mutex between runs under one PREFIX: refuse before the trap exists, so a second
# run can never tear down the first one's containers. Stale containers left without a network
# (a crashed earlier run) are removed here, so a new run heals them.
if docker network inspect "$NETWORK" > /dev/null 2>&1; then
  echo "network $NETWORK exists: PREFIX '$PREFIX' in use by another run? (remove it only if that run is gone)" >&2
  exit 75
fi
docker rm -f -v "$API" "$PG" "$REDIS" > /dev/null 2>&1 || true
docker network create "$NETWORK" > /dev/null
trap teardown EXIT
docker run -d --rm --name "$PG" --network "$NETWORK" -p "127.0.0.1:$PG_HOST_PORT:5432" \
  -e POSTGRES_DB -e POSTGRES_USER -e POSTGRES_PASSWORD \
  postgres:16-alpine -c shared_preload_libraries=pg_stat_statements > /dev/null
docker run -d --rm --name "$REDIS" --network "$NETWORK" -e REDIS_PASSWORD redis:7.2-alpine \
  sh -c 'printf "requirepass %s\n" "$REDIS_PASSWORD" > /tmp/redis.conf && exec redis-server /tmp/redis.conf --maxmemory 256mb --maxmemory-policy allkeys-lru' > /dev/null
wait_for "Postgres" 60 docker exec "$PG" pg_isready -U emotepurge -d emotepurge
# pg_isready and a socket query both succeed against the entrypoint's bootstrap server (socket only,
# listen_addresses=''); a query over TCP only succeeds once the final server is up.
wait_for "Postgres queries" 60 docker exec "$PG" sh -c 'PGPASSWORD=$POSTGRES_PASSWORD psql -h 127.0.0.1 -U emotepurge -d emotepurge -Atc "SELECT 1"'
docker exec "$PG" psql -U emotepurge -d emotepurge -qc 'CREATE EXTENSION IF NOT EXISTS pg_stat_statements'

echo "Migrating ..." >&2
(
  export ConnectionStrings__DefaultConnection="Host=localhost;Port=$PG_HOST_PORT;Database=emotepurge;Username=emotepurge;Password=$PG_PASSWORD"
  cd "$REPO_ROOT"
  dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api > /dev/null
)

echo "Seeding at ${CADENCE}-s cadence ..." >&2
docker exec -i "$PG" psql -U emotepurge -d emotepurge -v ON_ERROR_STOP=1 -v confirm_truncate=yes \
  -v cadence="$CADENCE" -f - < "$SCRIPT_DIR/seed-system-load-samples.sql" >&2
docker exec -i "$PG" psql -U emotepurge -d emotepurge -v ON_ERROR_STOP=1 -q \
  -v id="$ADMIN_TWITCH_USER_ID" -v login="$ADMIN_TWITCH_LOGIN" <<'SQL'
INSERT INTO "Users" ("Id", "TwitchUsername", "DisplayName", "LastLogin", "LastSeenAtUtc")
VALUES (:'id', :'login', :'login', now(), now());
SELECT pg_stat_statements_reset();
SQL

echo "Building the Api image ..." >&2
docker build -q -f "$REPO_ROOT/src/EmotePurge.Api/Dockerfile" -t "$IMAGE" "$REPO_ROOT" > /dev/null

export ConnectionStrings__DefaultConnection="Host=$PG;Database=emotepurge;Username=emotepurge;Password=$PG_PASSWORD"
export Redis__ConnectionString="$REDIS:6379,password=$REDIS_PASSWORD"
export Auth__Twitch__TokenEncryptionKey
Auth__Twitch__TokenEncryptionKey="$(openssl rand -base64 32)"
docker run -d --rm --name "$API" --network "$NETWORK" --cpus=1 --memory=512m \
  -p "127.0.0.1:$API_HOST_PORT:8080" -v "$DP_KEYRING_DIR:/keys" \
  -e ConnectionStrings__DefaultConnection -e Redis__ConnectionString -e Auth__Twitch__TokenEncryptionKey \
  -e SystemLoad__Enabled=true -e "SystemLoad__IntervalSeconds=$CADENCE" -e SystemLoad__RollupGraceMinutes=5 \
  -e Auth__Twitch__ClientId=gate-unused -e Auth__Twitch__ClientSecret=gate-unused \
  -e "Auth__Twitch__RedirectUri=http://localhost:$API_HOST_PORT/api/auth/twitch/callback" \
  -e Auth__Twitch__PostLoginRedirectUrl=/ \
  -e "Auth__AdminTwitchUserIds=$ADMIN_TWITCH_USER_ID" -e DataProtection__KeyPath=/keys \
  -e Logging__LogLevel__EmotePurge.Infrastructure.Services.AdminDashboardReader=Debug \
  -e Logging__Console__FormatterName=simple -e Logging__Console__FormatterOptions__SingleLine=true \
  -e 'Logging__Console__FormatterOptions__TimestampFormat=yyyy-MM-ddTHH:mm:ss.fffZ ' \
  -e Logging__Console__FormatterOptions__UseUtcTimestamp=true \
  "$IMAGE" > /dev/null
# Any HTTP answer means Kestrel is up; /api/health stays 503 until the gate's health keeper runs.
wait_for "the Api" 90 sh -c "curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:$API_HOST_PORT/api/health | grep -qv '^000$'"

export API_CONTAINER="$API" PG_CONTAINER="$PG" REDIS_CONTAINER="$REDIS"
export BASE_URL="http://127.0.0.1:$API_HOST_PORT"
export GATE_ADMIN_COOKIE_FILE
unset PG_PASSWORD POSTGRES_PASSWORD ConnectionStrings__DefaultConnection Redis__ConnectionString Auth__Twitch__TokenEncryptionKey
"$SCRIPT_DIR/dashboard-concurrency-gate.sh"
set +e
GATE_CSV_DIR="$(dirname "$OUT")" node "$SCRIPT_DIR/dashboard-concurrency-gate-eval.mjs" "$OUT"
evaluation=$?
set -e
EVALUATED=1
echo "Measurement kept in $OUT" >&2
exit "$evaluation"
