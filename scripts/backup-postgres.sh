#!/usr/bin/env bash
#
# Nightly Postgres backup for EmotePurge (S1-2 fix).
#
# Runs on the HOST, not inside a container: shells out to `docker exec`
# against the already-running Postgres container. This deliberately avoids
# the classic `pg_dump | gzip > file.gz` trap, where gzip still exits 0 even
# if pg_dump failed midway -- leaving a valid-looking but truncated/garbage
# archive behind with no indication anything went wrong. Instead: dump into
# a `.tmp` file first, verify pg_dump's own exit code and that the result is
# non-empty, and only then atomically `mv` it into its final name.
#
# See docs/Operations.md for the environment variables, the restore drill and
# the "volume is gone" disaster-recovery case.

set -euo pipefail

# --- Configuration (override via environment) -------------------------------
#
# Defaults below match docker-compose.prod.yml (container_name:
# emotepurge-postgres; since S3-38 the dev stack uses emotepurge-dev-postgres,
# this script targets prod) and .env.example (POSTGRES_USER=emotepurge,
# POSTGRES_DB is hardcoded to "emotepurge" in both compose files). BACKUP_DIR
# and RETENTION_DAYS are not defined anywhere in the repo -- they are
# operational choices, override as needed.

POSTGRES_CONTAINER="${POSTGRES_CONTAINER:-emotepurge-postgres}"
POSTGRES_USER="${POSTGRES_USER:-emotepurge}"
POSTGRES_DB="${POSTGRES_DB:-emotepurge}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/emotepurge}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"
BACKUP_FILE_PREFIX="${BACKUP_FILE_PREFIX:-emotepurge}"

# Optional off-site copy step, OFF by default. Set OFFSITE_ENABLED=1 and
# OFFSITE_RCLONE_REMOTE (e.g. "b2:my-bucket/emotepurge") to enable. Missing
# `rclone` or an unset remote only produces a warning, never a hard failure
# -- a broken off-site leg must never mask an otherwise-successful local
# backup, and must never be silently "required" for cron to be green.
OFFSITE_ENABLED="${OFFSITE_ENABLED:-0}"
OFFSITE_RCLONE_REMOTE="${OFFSITE_RCLONE_REMOTE:-}"

# --- Helpers ------------------------------------------------------------

log() {
  printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$1"
}

fail() {
  printf '[%s] ERROR: %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$1" >&2
  exit 1
}

# --- Preconditions --------------------------------------------------------

if ! command -v docker >/dev/null 2>&1; then
  fail "'docker' was not found. This script must run on the VPS host, not inside a container."
fi

# Explicit running-check with a clear message instead of letting `docker exec`
# fail cryptically further down.
container_state="$(docker inspect -f '{{.State.Running}}' "$POSTGRES_CONTAINER" 2>/dev/null || true)"
if [ "$container_state" != "true" ]; then
  fail "Postgres container '$POSTGRES_CONTAINER' is not running (or does not exist). No backup created."
fi

mkdir -p "$BACKUP_DIR"

timestamp="$(date '+%Y-%m-%d_%H%M%S')"
final_file="${BACKUP_DIR}/${BACKUP_FILE_PREFIX}-${timestamp}.sql.gz"
tmp_file="${final_file}.tmp"

# Clean up stale .tmp leftovers from a previous run that crashed mid-dump
# (e.g. host reboot). Only touches our own naming pattern.
find "$BACKUP_DIR" -maxdepth 1 -type f -name "${BACKUP_FILE_PREFIX}-*.sql.gz.tmp" -mtime +1 -delete 2>/dev/null || true

# --- Dump -----------------------------------------------------------------
#
# pg_dump runs inside the container; gzip + the tmp-file dance run on the
# host. `set -o pipefail` (above) makes the pipeline fail if either side
# fails, and the explicit exit-code check + non-empty check below are the
# real safety net regardless of shell pipefail semantics. Nothing under
# $final_file's name exists until both checks have passed.

log "Starting backup of database '$POSTGRES_DB' from container '$POSTGRES_CONTAINER' to '$tmp_file'..."

# The captured application logs are diagnostics with a seven-day retention and
# must not outlive it in the backup chain: the tables' schema stays in the dump,
# their data does not. Any change to these exclusions needs the same host
# install-and-verify step as the first one (see docs/Operations.md).
if ! docker exec "$POSTGRES_CONTAINER" pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=plain \
    --exclude-table-data='public."LogEntries"' \
    --exclude-table-data='public."LogCaptureStatus"' \
    | gzip -c > "$tmp_file"; then
  rm -f "$tmp_file"
  fail "pg_dump or gzip failed. No backup archive was left behind."
fi

if [ ! -s "$tmp_file" ]; then
  rm -f "$tmp_file"
  fail "Dump file is 0 bytes. No backup archive was left behind."
fi

mv "$tmp_file" "$final_file"

file_size="$(du -h "$final_file" | cut -f1)"
log "Backup created successfully: $final_file ($file_size)"

# --- Rotation ---------------------------------------------------------------
#
# Only ever touches files matching our own naming pattern -- never a blind
# `find "$BACKUP_DIR" -mtime +N -delete`, which would also remove unrelated
# files someone might have dropped into the same directory.

log "Removing backups older than $RETENTION_DAYS days (pattern: ${BACKUP_FILE_PREFIX}-*.sql.gz)..."
deleted_count=0
while IFS= read -r -d '' old_file; do
  rm -f "$old_file"
  deleted_count=$((deleted_count + 1))
  log "  deleted: $old_file"
done < <(find "$BACKUP_DIR" -maxdepth 1 -type f -name "${BACKUP_FILE_PREFIX}-*.sql.gz" -mtime "+${RETENTION_DAYS}" -print0)
log "Rotation complete: $deleted_count old backup(s) removed."

# --- Optional off-site copy -------------------------------------------------

if [ "$OFFSITE_ENABLED" = "1" ]; then
  if ! command -v rclone >/dev/null 2>&1; then
    log "WARNING: OFFSITE_ENABLED=1, but 'rclone' is not installed. Off-site copy skipped."
  elif [ -z "$OFFSITE_RCLONE_REMOTE" ]; then
    log "WARNING: OFFSITE_ENABLED=1, but OFFSITE_RCLONE_REMOTE is not set. Off-site copy skipped."
  else
    log "Copying backup off-site to '$OFFSITE_RCLONE_REMOTE'..."
    if rclone copy "$final_file" "$OFFSITE_RCLONE_REMOTE"; then
      log "Off-site copy succeeded."
    else
      # Deliberately non-fatal: the local backup above already succeeded and
      # must not be reported as failed just because the off-site leg had a
      # transient problem (network, quota, credentials).
      log "WARNING: Off-site copy failed. The local backup still exists."
    fi
  fi
else
  log "Off-site copy disabled (OFFSITE_ENABLED != 1)."
fi

log "Done."
