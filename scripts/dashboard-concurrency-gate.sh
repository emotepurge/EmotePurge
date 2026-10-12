#!/usr/bin/env bash
# Concurrency gate of the admin load dashboard under production limits (#381 cut B, spec section 11
# "B-Parallelitaets-Gate"). Measurement only: it writes one CSV, and
# scripts/dashboard-concurrency-gate-eval.mjs decides pass/fail from it. Keeping the two apart lets
# the evaluation be proven against prepared violations (scripts/fixtures/dashboard-gate/).
#
# What it does, on a fixed 2.5-second tick (24 health probes per minute stay under the 30/min
# PublicHealth fixed window):
#   * at tick + 0 ms, CLIENTS parallel GET /api/admin/dashboard/load?range=<R>, the range alternating
#     per tick (24h, 13mo, 24h, ...). A key is cold again once its TTL (30 s for 24h, 60 s for 13mo)
#     has run out, so every ~12th/24th request of a key starts a real rebuild with three clients on it;
#   * at tick + PROBE_OFFSET_MS, one GET /api/health. The offset lets the probe land inside the
#     rebuild the load requests just started (the rebuild begins only after request parsing and the
#     cookie session check), so the probe measures health latency during a running rebuild;
#   * at tick + SNAPSHOT_OFFSET_MS, one docker exec into the Api: read VmHWM/VmRSS of PID 1, reset
#     the high-water mark (echo 5 > /proc/1/clear_refs) and read the cgroup's memory.current; plus
#     one pg_stat_statements read of the two load queries (raw and hourly path).
# Then COOLDOWN ticks with probes only (both keys go cold), one tick with CLIENTS clients on 24h and
# CLIENTS on 13mo at the same instant (criterion d), repeated DUAL_CYCLES times. Finally the
# facade's Debug log lines ("Load dashboard cold rebuild started/finished") are read back from the
# container log and written as rebuild intervals.
#
# Memory: the increment of a tick is VmHWM (peak since the previous reset) minus VmRSS at that reset.
# If clear_refs cannot be written, the script falls back to sampling VmRSS every 50 ms inside the
# container and marks those rows "fallback". cgroup memory.current (what docker stats shows, page
# cache included) is written as an extra column only.
#
# Prerequisites (see the B6 protocol in docs/superpowers/plans/2026-10-11-admin-dashboard-381-plan.md):
#   * the Api container running with --cpus=1 --memory=512m, Debug enabled for the category
#     EmotePurge.Infrastructure.Services.AdminDashboardReader, and the simple console formatter with
#     single-line UTC timestamps "yyyy-MM-ddTHH:mm:ss.fffZ " (the log parser below relies on it);
#   * Postgres with pg_stat_statements loaded and the extension created in the app database;
#   * a global-admin auth cookie value in GATE_ADMIN_COOKIE (never commit it);
#   * a fresh, "connected" worker-health snapshot in Redis - either a running Worker, or set
#     REDIS_CONTAINER/REDIS_PASSWORD and the script keeps a synthetic one fresh.
#
# Environment:
#   API_CONTAINER      (required) name of the Api container
#   GATE_ADMIN_COOKIE  (required) value of the .AspNetCore.Cookies cookie of a global admin
#   PG_CONTAINER       (required) Postgres container for pg_stat_statements; PG_USER/PG_DB default emotepurge
#   BASE_URL           default http://127.0.0.1:18080
#   REDIS_CONTAINER, REDIS_PASSWORD  optional: keep worker:health:twitch fresh every 10 s
#   MAIN_TICKS         default 384 (16 minutes)
#   COOLDOWN_TICKS     default 26 (65 s, longer than the 60-s TTL)
#   DUAL_CYCLES        default 3
#   CLIENTS            default 3
#   PROBE_OFFSET_MS    default 50
#   SNAPSHOT_OFFSET_MS default 2000
#   OUT                default ./dashboard-gate-<UTC timestamp>.csv
#
# Usage: API_CONTAINER=... PG_CONTAINER=... GATE_ADMIN_COOKIE=... scripts/dashboard-concurrency-gate.sh
#        node scripts/dashboard-concurrency-gate-eval.mjs <csv>

set -euo pipefail

: "${API_CONTAINER:?set API_CONTAINER}"
: "${GATE_ADMIN_COOKIE:?set GATE_ADMIN_COOKIE}"
: "${PG_CONTAINER:?set PG_CONTAINER}"
PG_USER="${PG_USER:-emotepurge}"
PG_DB="${PG_DB:-emotepurge}"
BASE_URL="${BASE_URL:-http://127.0.0.1:18080}"
MAIN_TICKS="${MAIN_TICKS:-384}"
COOLDOWN_TICKS="${COOLDOWN_TICKS:-26}"
DUAL_CYCLES="${DUAL_CYCLES:-3}"
CLIENTS="${CLIENTS:-3}"
PROBE_OFFSET_MS="${PROBE_OFFSET_MS:-50}"
SNAPSHOT_OFFSET_MS="${SNAPSHOT_OFFSET_MS:-2000}"
TICK_MS=2500
OUT="${OUT:-./dashboard-gate-$(date -u +%Y%m%dT%H%M%SZ).csv}"

WORK="$(mktemp -d)"
RAW="$WORK/rows.csv"
: > "$RAW"
BACKGROUND_PIDS=()

cleanup() {
  for pid in "${BACKGROUND_PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  rm -rf "$WORK"
}
trap cleanup EXIT

now_ms() {
  local t="${EPOCHREALTIME/./}"
  echo $((t / 1000))
}

sleep_until_ms() {
  local target="$1" now
  now="$(now_ms)"
  if ((target > now)); then
    sleep "$(printf '%d.%03d' $(((target - now) / 1000)) $(((target - now) % 1000)))"
  fi
}

# kind,phase,tick,range,start_ms,end_ms,status,value,size_bytes,note
emit() {
  printf '%s\n' "$1" >> "$RAW"
}

fire_probe() {
  local phase="$1" tick="$2" start out code total
  start="$(now_ms)"
  out="$(curl -s -o /dev/null --max-time 5 -w '%{http_code} %{time_total}' "$BASE_URL/api/health" || true)"
  code="${out%% *}"
  total="${out##* }"
  [[ -z "$code" ]] && code=000
  [[ "$total" == "$out" || -z "$total" ]] && total=0
  local ms
  ms="$(awk -v t="$total" 'BEGIN { printf "%.1f", t * 1000 }')"
  emit "probe,$phase,$tick,,$start,$((start + ${ms%.*})),$code,$ms,,"
}

fire_load() {
  local phase="$1" tick="$2" range="$3" start headers out code total size retry ms
  headers="$WORK/h.$tick.$range.$RANDOM$RANDOM"
  start="$(now_ms)"
  out="$(curl -s -o /dev/null -D "$headers" --max-time 20 \
    -H "Cookie: .AspNetCore.Cookies=$GATE_ADMIN_COOKIE" \
    -w '%{http_code} %{time_total} %{size_download}' \
    "$BASE_URL/api/admin/dashboard/load?range=$range" || true)"
  read -r code total size <<< "$out"
  code="${code:-000}"
  total="${total:-0}"
  size="${size:-0}"
  retry="$(grep -i '^retry-after:' "$headers" 2>/dev/null | tr -d '\r' | awk '{ print $2 }' || true)"
  rm -f "$headers"
  ms="$(awk -v t="$total" 'BEGIN { printf "%.1f", t * 1000 }')"
  emit "load,$phase,$tick,$range,$start,$((start + ${ms%.*})),$code,$ms,$size,$retry"
}

# Prints "VmHWM_kib VmRSS_kib cgroup_bytes reset_ok" and resets the high-water mark.
memory_snapshot() {
  docker exec -u root "$API_CONTAINER" sh -c '
    hwm=$(awk "/^VmHWM:/ { print \$2 }" /proc/1/status)
    rss=$(awk "/^VmRSS:/ { print \$2 }" /proc/1/status)
    cg=$(cat /sys/fs/cgroup/memory.current 2>/dev/null || echo "")
    if echo 5 > /proc/1/clear_refs 2>/dev/null; then ok=1; else ok=0; fi
    rss_after=$(awk "/^VmRSS:/ { print \$2 }" /proc/1/status)
    echo "$hwm $rss $cg $ok $rss_after"'
}

pgss_snapshot() {
  docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -At -F ' ' -c "
    SELECT
      coalesce(sum(calls) FILTER (WHERE query LIKE '%FROM \"SystemLoadSamples\" AS sample%'), 0),
      coalesce(sum(calls) FILTER (WHERE query LIKE '%FROM \"SystemLoadHourly\" AS%'), 0)
    FROM pg_stat_statements
    WHERE query NOT LIKE '%pg_stat_statements%'"
}

keep_health_fresh() {
  while true; do
    local now
    now="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
    docker exec "$REDIS_CONTAINER" redis-cli -a "$REDIS_PASSWORD" --no-auth-warning SET worker:health:twitch \
      "{\"isConnected\":true,\"lastMessageReceivedUtc\":\"$now\",\"connectAttemptedUtc\":\"$now\",\"twitchLastFrameUtc\":\"$now\"}" \
      EX 60 > /dev/null || echo "health keeper: redis SET failed" >&2
    sleep 10
  done
}

# Fallback memory sampler: VmRSS every 50 ms inside the container, "epoch_ms kib" per line.
start_rss_sampler() {
  docker exec "$API_CONTAINER" sh -c '
    while true; do
      echo "$(date +%s%3N) $(awk "/^VmRSS:/ { print \$2 }" /proc/1/status)"
      sleep 0.05
    done' > "$WORK/rss-samples.txt" &
  BACKGROUND_PIDS+=("$!")
}

fallback_peak_kib() {
  local from="$1" to="$2"
  awk -v f="$from" -v t="$to" '$1 >= f && $1 <= t && $2 > m { m = $2 } END { print m + 0 }' "$WORK/rss-samples.txt"
}

if [[ -n "${REDIS_CONTAINER:-}" ]]; then
  : "${REDIS_PASSWORD:?set REDIS_PASSWORD together with REDIS_CONTAINER}"
  keep_health_fresh &
  BACKGROUND_PIDS+=("$!")
  sleep 1
fi

if [[ "$(curl -s -o /dev/null -w '%{http_code}' "$BASE_URL/api/health")" != 200 ]]; then
  echo "GET /api/health is not 200 before the run - no fresh connected worker-health snapshot?" >&2
  exit 3
fi
auth_code="$(curl -s -o /dev/null -w '%{http_code}' -H "Cookie: .AspNetCore.Cookies=$GATE_ADMIN_COOKIE" \
  "$BASE_URL/api/admin/dashboard/load?range=1h")"
if [[ "$auth_code" != 200 ]]; then
  echo "GET /api/admin/dashboard/load answered $auth_code - GATE_ADMIN_COOKIE is not a valid global-admin session." >&2
  exit 3
fi

run_started_iso="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
read -r _ base_rss _ reset_ok _ <<< "$(memory_snapshot)"
memory_mode=clear_refs
if [[ "$reset_ok" != 1 ]]; then
  memory_mode=fallback
  echo "clear_refs not writable - falling back to 50-ms VmRSS sampling (rows marked fallback)." >&2
  start_rss_sampler
fi
read -r pg_raw pg_hourly <<< "$(pgss_snapshot)"
prev_snapshot_ms="$(now_ms)"

# Tick plan: main ticks alternate 24h/13mo, then per dual cycle COOLDOWN probe-only ticks and one
# dual tick.
plan=()
for ((i = 0; i < MAIN_TICKS; i++)); do
  if ((i % 2 == 0)); then plan+=("main:24h"); else plan+=("main:13mo"); fi
done
for ((c = 0; c < DUAL_CYCLES; c++)); do
  for ((i = 0; i < COOLDOWN_TICKS; i++)); do plan+=("cooldown:"); done
  plan+=("dual:24h+13mo")
done

echo "Gate run: ${#plan[@]} ticks (~$((${#plan[@]} * TICK_MS / 60000)) min), memory mode $memory_mode, output $OUT" >&2
t0=$(($(now_ms) + 1000))
for ((tick = 0; tick < ${#plan[@]}; tick++)); do
  phase="${plan[$tick]%%:*}"
  ranges="${plan[$tick]#*:}"
  grid=$((t0 + tick * TICK_MS))
  sleep_until_ms "$grid"
  if [[ -n "$ranges" ]]; then
    for range in ${ranges//+/ }; do
      for ((client = 0; client < CLIENTS; client++)); do
        fire_load "$phase" "$tick" "$range" &
      done
    done
  fi
  sleep_until_ms $((grid + PROBE_OFFSET_MS))
  fire_probe "$phase" "$tick" &

  sleep_until_ms $((grid + SNAPSHOT_OFFSET_MS))
  snapshot_ms="$(now_ms)"
  read -r hwm rss cgroup _ rss_after <<< "$(memory_snapshot)"
  if [[ "$memory_mode" == fallback ]]; then
    hwm="$(fallback_peak_kib "$prev_snapshot_ms" "$snapshot_ms")"
  fi
  emit "mem,$phase,$tick,,$prev_snapshot_ms,$snapshot_ms,$memory_mode,$((hwm - base_rss)),,hwm_kib=$hwm;rss_base_kib=$base_rss;rss_now_kib=$rss;cgroup_memory_current_bytes=$cgroup"
  base_rss="$rss_after"
  read -r raw hourly <<< "$(pgss_snapshot)"
  emit "query,$phase,$tick,,$prev_snapshot_ms,$snapshot_ms,,$(((raw - pg_raw) + (hourly - pg_hourly))),,raw=$((raw - pg_raw));hourly=$((hourly - pg_hourly))"
  pg_raw="$raw"
  pg_hourly="$hourly"
  prev_snapshot_ms="$snapshot_ms"
done
wait_for=$(($(now_ms) + 6000))
sleep_until_ms "$wait_for"

# Rebuild intervals from the facade's Debug lines. Only one rebuild runs at a time (the facade's
# gate), so every "finished" closes the "started" before it.
docker logs --since "$run_started_iso" "$API_CONTAINER" 2>&1 \
  | grep -E 'Load dashboard cold rebuild (started|finished)' \
  | while read -r ts _ _ _ _ _ _ what rest; do
      ms_epoch="$(date -u -d "$ts" +%s%3N)"
      range="$(sed -E 's/.*range=([^ ]+).*/\1/' <<< "$rest")"
      if [[ "$what" == started ]]; then
        echo "S $range $ms_epoch"
      else
        logged_ms="$(sed -E 's/.*ms=([0-9]+).*/\1/' <<< "$rest")"
        outcome="$(sed -E 's/.*outcome=([a-z]+).*/\1/' <<< "$rest")"
        echo "F $range $ms_epoch $logged_ms $outcome"
      fi
    done \
  | awk '$1 == "S" { start[$2] = $3; next }
         $1 == "F" { s = ($2 in start) ? start[$2] : $3 - $4; printf "rebuild,,,%s,%s,%s,%s,%s,,\n", $2, s, $3, $5, $4; delete start[$2] }' \
  >> "$RAW"

{
  echo "kind,phase,tick,range,start_ms,end_ms,status,value,size_bytes,note"
  sort -t, -k5,5n "$RAW"
} > "$OUT"
echo "Wrote $OUT ($(($(wc -l < "$OUT") - 1)) rows). Evaluate with: node scripts/dashboard-concurrency-gate-eval.mjs $OUT" >&2
