/**
 * Client-side mirror of the chat-log backfill payloads (spec 2026-10-09, §5.1/§5.2). Dates are the
 * server's ISO calendar days (`YYYY-MM-DD`, UTC), timestamps ISO instants.
 */

/** The window lengths the server accepts (`backfill_months_invalid` otherwise). */
export type BackfillMonths = 1 | 3 | 6;

export type BackfillRunStatus =
  'queued' | 'running' | 'paused' | 'completed' | 'failed' | 'cancelled';

export interface BackfillArchive {
  name: string;
  url: string;
}

export interface BackfillOption {
  months: number;
  /** First imported day, inclusive. */
  windowFrom: string;
  /** Exclusive upper bound — the day counting started, so the last imported day is the one before. */
  windowTo: string;
  days: number;
  weeks: number;
  available: boolean;
  /** `no_days_before_counting` today; the UI translates, unknown values get a generic sentence. */
  reason: string | null;
}

export interface BackfillCoverageInterval {
  from: string;
  /** Exclusive. */
  to: string;
  emoteSetId: string;
  emoteSetName: string | null;
}

export interface BackfillRun {
  id: number;
  status: BackfillRunStatus;
  requestedMonths: number;
  windowFrom: string;
  windowTo: string;
  weeksDone: number;
  weeksTotal: number;
  queuePosition: number | null;
  pausedUntilUtc: string | null;
  requestedAtUtc: string;
  startedAtUtc: string | null;
  finishedAtUtc: string | null;
  requestedByLogin: string | null;
  emoteSetId: string;
  emoteSetName: string | null;
  emoteCount: number;
  errorCode: string | null;
  errorHttpStatus: number | null;
  bytesReceived: number;
  messagesRead: number;
}

/** GET /api/channels/{c}/backfill. */
export interface BackfillStatus {
  countingSince: string;
  archive: BackfillArchive;
  requestDelaySeconds: number;
  options: BackfillOption[];
  /** `Channel.ActiveEmoteSetId`; `""` while no sync has completed. */
  activeEmoteSetId: string;
  coverage: BackfillCoverageInterval[];
  activeRun: BackfillRun | null;
  lastRun: BackfillRun | null;
  importedFrom: string | null;
  importedTo: string | null;
  importedContiguous: boolean;
  cooldownUntilUtc: string | null;
}

/** POST /api/channels/{c}/backfill body. */
export interface StartBackfillRequest {
  emoteSetId: string;
  months: number;
}
