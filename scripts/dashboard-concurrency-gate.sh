#!/usr/bin/env bash
# Concurrency gate of the admin load dashboard under production limits (#381 cut B, spec section 11
# "B-Parallelitaets-Gate"). Measurement only: it writes one CSV, and
# scripts/dashboard-concurrency-gate-eval.mjs decides pass/fail from it. Keeping the two apart lets
# the evaluation be proven against prepared violations (scripts/fixtures/dashboard-gate/).
# scripts/dashboard-concurrency-gate-env.sh sets up a throwaway environment, runs this script in it
# and tears everything down again; use this script directly only against an environment you manage.
#
# What it does, on a fixed 2.5-second tick (24 health probes per minute stay under the 30/min
# PublicHealth fixed window):
#   * at tick + 0 ms, CLIENTS parallel GET /api/admin/dashboard/load?range=<R>, the range alternating
#     per tick (24h, 13mo, 24h, ...). A key is cold again once its TTL (30 s for 24h, 60 s for 13mo)
#     has run out, so every ~12th/24th request of a key starts a real rebuild with three clients on it;
#   * at tick + PROBE_OFFSET_MS, one GET /api/health. The offset lets the probe land inside the
#     rebuild the load requests just started (the rebuild begins only after request parsing and the
#     cookie session check, ~12 ms after sending), so the probe measures health latency during a
#     running rebuild. Use 50 ms for the 1-s seed and 8 ms for the 30-s seed, whose warm 24h rebuild
#     lasts only 10-55 ms (20 ms caught only 27 of the 40 required probes in 384 ticks);
#   * at tick + SNAPSHOT_OFFSET_MS, one docker exec into the Api: read VmHWM/VmRSS of PID 1, reset
#     the high-water mark (echo 5 > /proc/1/clear_refs) and read the cgroup's memory.current; plus
#     one pg_stat_statements read of the two load queries (raw and hourly path).
# Then COOLDOWN ticks with probes only (both keys go cold), one tick with CLIENTS clients on 24h and
# CLIENTS on 13mo at the same instant (criterion d), repeated DUAL_CYCLES times. Finally the
# facade's Debug log lines ("Load dashboard cold rebuild started/finished") are read back from the
# container log and written as rebuild intervals. The first CSV row ("meta") records the tick plan,
# so the evaluation can check that every tick produced all of its rows.
#
# Memory: the increment of a tick is VmHWM (peak since the previous reset) minus VmRSS at that reset.
# If clear_refs cannot be written, the script falls back to sampling VmRSS every 50 ms inside the
# container and marks those rows "fallback". cgroup memory.current (what docker stats shows, page
# cache included) is written as an extra column only. A snapshot that returns no numbers aborts the
# run; it is never written as a zero.
#
# Prerequisites:
#   * the Api container running with --cpus=1 --memory=512m, Debug enabled for the category
#     EmotePurge.Infrastructure.Services.AdminDashboardReader, and the simple console formatter with
#     single-line UTC timestamps "yyyy-MM-ddTHH:mm:ss.fffZ " (the log parser below relies on it);
#   * Postgres with pg_stat_statements loaded and the extension created in the app database;
#   * a global-admin auth cookie value in a file (GATE_ADMIN_COOKIE_FILE; never commit it);
#   * a fresh, "connected" worker-health snapshot in Redis - either a running Worker, or set
#     REDIS_CONTAINER and REDIS_PASSWORD and the script keeps a synthetic one fresh.
# Secrets never appear on a command line: curl reads the cookie header from a mode-600 file, and
# redis-cli gets its password through REDISCLI_AUTH passed by name into docker exec.
#
# Environment:
#   API_CONTAINER          (required) name of the Api container
#   GATE_ADMIN_COOKIE_FILE (required) file holding the value of the .AspNetCore.Cookies cookie of a
#                          global admin
#   PG_CONTAINER           (required) Postgres container for pg_stat_statements; PG_USER/PG_DB default emotepurge
#   BASE_URL               default http://127.0.0.1:18080
#   REDIS_CONTAINER, REDIS_PASSWORD  optional: keep worker:health:twitch fresh every 10 s
#   MAIN_TICKS             default 384 (16 minutes)
#   COOLDOWN_TICKS         default 26 (65 s, longer than the 60-s TTL)
#   DUAL_CYCLES            default 3
#   CLIENTS                default 3
#   PROBE_OFFSET_MS        default 50 (1-s seed); 8 for the 30-s seed (with MAIN_TICKS=480)
#   SNAPSHOT_OFFSET_MS     default 2000
#   OUT                    default ${TMPDIR:-/tmp}/dashboard-gate-<UTC timestamp>.csv
#
# Exit codes: 0 measurement complete (judge it with the evaluation), 3 preflight failed, 4 a
# measurement returned no usable value, 5 no rebuild log lines found. An abort during the ticks keeps
# the rows measured so far in <OUT without .csv>.partial.csv; exit 5 keeps them in OUT itself
# (without rebuild rows, which the evaluation then reports as invalid).
#
# Usage: API_CONTAINER=... PG_CONTAINER=... GATE_ADMIN_COOKIE_FILE=... scripts/dashboard-concurrency-gate.sh
#        GATE_CSV_DIR=$(dirname "$OUT") node scripts/dashboard-concurrency-gate-eval.mjs <csv>

set -euo pipefail

: "${API_CONTAINER:?set API_CONTAINER}"
: "${GATE_ADMIN_COOKIE_FILE:?set GATE_ADMIN_COOKIE_FILE}"
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
OUT="${OUT:-${TMPDIR:-/tmp}/dashboard-gate-$(date -u +%Y%m%dT%H%M%SZ).csv}"
CSV_HEADER="kind,phase,tick,range,start_ms,end_ms,status,value,size_bytes,note"
SAMPLER_STOP_FILE=/tmp/dashboard-gate-rss-sampler.stop

WORK="$(mktemp -d)"
RAW="$WORK/rows.csv"
: > "$RAW"
BACKGROUND_PIDS=()
REQUEST_PIDS=()
RUN_STARTED=0
OUT_WRITTEN=0
SAMPLER_STARTED=0

write_csv() {
  {
    echo "$CSV_HEADER"
    sort -t, -k5,5n "$RAW"
  } > "$1"
}

cleanup() {
  for pid in "${BACKGROUND_PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  if ((SAMPLER_STARTED == 1)); then
    # Killing the local docker CLI does not stop the loop inside the container; the stop file does.
    docker exec -u root "$API_CONTAINER" touch "$SAMPLER_STOP_FILE" 2>/dev/null || true
  fi
  if ((RUN_STARTED == 1 && OUT_WRITTEN == 0)) && [[ -s "$RAW" ]]; then
    local partial="${OUT%.csv}.partial.csv"
    write_csv "$partial"
    echo "Run aborted - rows measured so far kept in $partial" >&2
  fi
  rm -rf "$WORK"
}
trap cleanup EXIT

die() {
  echo "$2" >&2
  exit "$1"
}

is_int() {
  [[ "$1" =~ ^-?[0-9]+$ ]]
}

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

emit() {
  printf '%s\n' "$1" >> "$RAW"
}

# Request times: end_ms is taken right after curl returns and start_ms = end_ms - time_total. A clock
# read before starting curl would be early by the process spawn delay, which reached ~160 ms on a
# busy box and made requests look in flight before they were sent.
fire_probe() {
  local phase="$1" tick="$2" out code total ms end
  out="$(curl -s -o /dev/null --max-time 5 -w '%{http_code} %{time_total}' "$BASE_URL/api/health" || true)"
  end="$(now_ms)"
  read -r code total <<< "$out"
  code="${code:-000}"
  total="${total:-0}"
  ms="$(awk -v t="$total" 'BEGIN { printf "%.1f", t * 1000 }')"
  emit "probe,$phase,$tick,,$((end - ${ms%.*})),$end,$code,$ms,,"
}

fire_load() {
  local phase="$1" tick="$2" range="$3" headers out code total size retry ms end
  headers="$WORK/h.$tick.$range.$BASHPID"
  out="$(curl -s -o /dev/null -D "$headers" --max-time 20 \
    -H "@$WORK/cookie-header" \
    -w '%{http_code} %{time_total} %{size_download}' \
    "$BASE_URL/api/admin/dashboard/load?range=$range" || true)"
  end="$(now_ms)"
  read -r code total size <<< "$out"
  code="${code:-000}"
  total="${total:-0}"
  size="${size:-0}"
  retry="$(grep -i '^retry-after:' "$headers" 2>/dev/null | tr -d '\r' | awk '{ print $2 }' || true)"
  rm -f "$headers"
  ms="$(awk -v t="$total" 'BEGIN { printf "%.1f", t * 1000 }')"
  emit "load,$phase,$tick,$range,$((end - ${ms%.*})),$end,$code,$ms,$size,$retry"
}

# Prints "VmHWM_kib VmRSS_kib cgroup_bytes reset_ok VmRSS_after_reset_kib" and resets the high-water mark.
memory_snapshot() {
  docker exec -u root "$API_CONTAINER" sh -c '
    hwm=$(awk "/^VmHWM:/ { print \$2 }" /proc/1/status)
    rss=$(awk "/^VmRSS:/ { print \$2 }" /proc/1/status)
    cg=$(cat /sys/fs/cgroup/memory.current 2>/dev/null || echo "-")
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
  export REDISCLI_AUTH="$REDIS_PASSWORD"
  while true; do
    local now
    now="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
    docker exec -e REDISCLI_AUTH "$REDIS_CONTAINER" redis-cli SET worker:health:twitch \
      "{\"isConnected\":true,\"lastMessageReceivedUtc\":\"$now\",\"connectAttemptedUtc\":\"$now\",\"twitchLastFrameUtc\":\"$now\"}" \
      EX 60 > /dev/null || echo "health keeper: redis SET failed" >&2
    sleep 10
  done
}

# Fallback memory sampler: VmRSS every 50 ms inside the container, "epoch_ms kib" per line, until the
# stop file appears (cleanup creates it).
start_rss_sampler() {
  docker exec -u root "$API_CONTAINER" rm -f "$SAMPLER_STOP_FILE"
  docker exec -u root "$API_CONTAINER" sh -c "
    while [ ! -e $SAMPLER_STOP_FILE ]; do
      echo \"\$(date +%s%3N) \$(awk '/^VmRSS:/ { print \$2 }' /proc/1/status)\"
      sleep 0.05
    done" > "$WORK/rss-samples.txt" &
  BACKGROUND_PIDS+=("$!")
  SAMPLER_STARTED=1
}

fallback_peak_kib() {
  local from="$1" to="$2"
  awk -v f="$from" -v t="$to" '$1 >= f && $1 <= t && $2 > m { m = $2 } END { if (m > 0) print m }' "$WORK/rss-samples.txt"
}

# Reads one memory snapshot into SNAP_HWM, SNAP_RSS, SNAP_CGROUP, SNAP_RESET_OK and SNAP_RSS_AFTER;
# aborts the run if any of the numbers is missing.
take_memory_snapshot() {
  local out
  out="$(memory_snapshot)" || die 4 "memory snapshot failed (docker exec into $API_CONTAINER)"
  read -r SNAP_HWM SNAP_RSS SNAP_CGROUP SNAP_RESET_OK SNAP_RSS_AFTER <<< "$out"
  for value in "$SNAP_HWM" "$SNAP_RSS" "$SNAP_RSS_AFTER"; do
    if ! is_int "$value" || ((value <= 0)); then
      die 4 "memory snapshot returned no usable numbers: '$out'"
    fi
  done
}

take_query_snapshot() {
  local out
  out="$(pgss_snapshot)" || die 4 "pg_stat_statements read failed (docker exec into $PG_CONTAINER)"
  read -r SNAP_RAW SNAP_HOURLY <<< "$out"
  if ! is_int "${SNAP_RAW:-}" || ! is_int "${SNAP_HOURLY:-}"; then
    die 4 "pg_stat_statements returned no usable numbers: '$out'"
  fi
}

umask 077
cookie_value="$(tr -d '\r\n' < "$GATE_ADMIN_COOKIE_FILE")"
[[ -n "$cookie_value" ]] || die 3 "GATE_ADMIN_COOKIE_FILE is empty"
printf 'Cookie: .AspNetCore.Cookies=%s\n' "$cookie_value" > "$WORK/cookie-header"
unset cookie_value

if [[ -n "${REDIS_CONTAINER:-}" ]]; then
  : "${REDIS_PASSWORD:?set REDIS_PASSWORD together with REDIS_CONTAINER}"
  keep_health_fresh &
  BACKGROUND_PIDS+=("$!")
  sleep 1
fi

if [[ "$(curl -s -o /dev/null -w '%{http_code}' "$BASE_URL/api/health")" != 200 ]]; then
  die 3 "GET /api/health is not 200 before the run - no fresh connected worker-health snapshot?"
fi
auth_code="$(curl -s -o /dev/null -w '%{http_code}' -H "@$WORK/cookie-header" \
  "$BASE_URL/api/admin/dashboard/load?range=1h")"
if [[ "$auth_code" != 200 ]]; then
  die 3 "GET /api/admin/dashboard/load answered $auth_code - the cookie is not a valid global-admin session."
fi

RUN_STARTED=1
run_started_iso="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
emit "meta,,,,0,0,,,,clients=$CLIENTS;main_ticks=$MAIN_TICKS;cooldown_ticks=$COOLDOWN_TICKS;dual_cycles=$DUAL_CYCLES;tick_ms=$TICK_MS;probe_offset_ms=$PROBE_OFFSET_MS;snapshot_offset_ms=$SNAPSHOT_OFFSET_MS;timing=curl_end"
take_memory_snapshot
base_rss="$SNAP_RSS"
memory_mode=clear_refs
if [[ "$SNAP_RESET_OK" != 1 ]]; then
  memory_mode=fallback
  echo "clear_refs not writable - falling back to 50-ms VmRSS sampling (rows marked fallback)." >&2
  start_rss_sampler
else
  base_rss="$SNAP_RSS_AFTER"
fi
take_query_snapshot
pg_raw="$SNAP_RAW"
pg_hourly="$SNAP_HOURLY"
prev_snapshot_ms="$(now_ms)"

# Tick plan: main ticks alternate 24h/13mo, then per dual cycle COOLDOWN probe-only ticks and one
# dual tick. The evaluation rebuilds the same plan from the meta row.
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
        REQUEST_PIDS+=("$!")
      done
    done
  fi
  sleep_until_ms $((grid + PROBE_OFFSET_MS))
  fire_probe "$phase" "$tick" &
  REQUEST_PIDS+=("$!")

  sleep_until_ms $((grid + SNAPSHOT_OFFSET_MS))
  snapshot_ms="$(now_ms)"
  take_memory_snapshot
  hwm="$SNAP_HWM"
  if [[ "$memory_mode" == fallback ]]; then
    hwm="$(fallback_peak_kib "$prev_snapshot_ms" "$snapshot_ms")"
    is_int "${hwm:-}" || die 4 "fallback sampler delivered no VmRSS samples between $prev_snapshot_ms and $snapshot_ms"
  fi
  emit "mem,$phase,$tick,,$prev_snapshot_ms,$snapshot_ms,$memory_mode,$((hwm - base_rss)),,hwm_kib=$hwm;rss_base_kib=$base_rss;rss_now_kib=$SNAP_RSS;cgroup_memory_current_bytes=$SNAP_CGROUP"
  base_rss="$SNAP_RSS_AFTER"
  take_query_snapshot
  emit "query,$phase,$tick,,$prev_snapshot_ms,$snapshot_ms,,$(((SNAP_RAW - pg_raw) + (SNAP_HOURLY - pg_hourly))),,raw=$((SNAP_RAW - pg_raw));hourly=$((SNAP_HOURLY - pg_hourly))"
  pg_raw="$SNAP_RAW"
  pg_hourly="$SNAP_HOURLY"
  prev_snapshot_ms="$snapshot_ms"
done

# Every request writes its row when it ends; the slowest load may run up to its 20-s --max-time.
for pid in "${REQUEST_PIDS[@]}"; do
  wait "$pid" 2>/dev/null || true
done

# Keep the measured rows before parsing the log, so a parser failure cannot lose them.
write_csv "$OUT"
OUT_WRITTEN=1

# Rebuild intervals from the facade's Debug lines. Only one rebuild runs at a time (the facade's
# gate), so every "finished" closes the "started" before it.
docker logs --since "$run_started_iso" "$API_CONTAINER" > "$WORK/api.log" 2>&1 \
  || die 5 "docker logs $API_CONTAINER failed"
grep -E 'Load dashboard cold rebuild (started|finished)' "$WORK/api.log" > "$WORK/rebuild-lines.txt" || true
if [[ ! -s "$WORK/rebuild-lines.txt" ]]; then
  die 5 "no 'Load dashboard cold rebuild' log lines found - is Debug enabled for EmotePurge.Infrastructure.Services.AdminDashboardReader and the single-line UTC console format set? Measured rows are in $OUT."
fi
while read -r ts _ _ _ _ _ _ what rest; do
  ms_epoch="$(date -u -d "$ts" +%s%3N)" || die 5 "unparseable log timestamp '$ts' - is the console TimestampFormat yyyy-MM-ddTHH:mm:ss.fffZ?"
  range="$(sed -E 's/.*range=([^ ]+).*/\1/' <<< "$rest")"
  if [[ "$what" == started ]]; then
    echo "S $range $ms_epoch"
  else
    logged_ms="$(sed -E 's/.*ms=([0-9]+).*/\1/' <<< "$rest")"
    outcome="$(sed -E 's/.*outcome=([a-z]+).*/\1/' <<< "$rest")"
    echo "F $range $ms_epoch $logged_ms $outcome"
  fi
done < "$WORK/rebuild-lines.txt" \
  | awk '$1 == "S" { start[$2] = $3; next }
         $1 == "F" { s = ($2 in start) ? start[$2] : $3 - $4; printf "rebuild,,,%s,%s,%s,%s,%s,,\n", $2, s, $3, $5, $4; delete start[$2] }' \
  >> "$RAW"

write_csv "$OUT"
echo "Wrote $OUT ($(($(wc -l < "$OUT") - 1)) rows). Evaluate with: node scripts/dashboard-concurrency-gate-eval.mjs $OUT" >&2
