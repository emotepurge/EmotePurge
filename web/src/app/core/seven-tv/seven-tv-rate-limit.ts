/*
 * 7TV's rate-limit answer, read the same way wherever it arrives — a rejected mutation in the run
 * engine, or a set read (#454). Moved out of `seven-tv-run-engine.ts` unchanged.
 */

/** 7TV's rate-limit bucket guarding every emote-set mutation, one ticket per call — REMOVE and ADD
 *  alike, which is why one engine paces both the delete and the restore run.
 *  Source: SevenTV/SevenTV, apps/api/src/http/v3/gql/mutations/emote_sets/mod.rs. */
export const RATE_LIMIT_RESOURCE = 'emote_set_change';
const RATE_LIMIT_ERROR_CODE = 'RATE_LIMIT_EXCEEDED';

/** The rate-limit numbers 7TV mirrors into a rejected mutation's `extensions.headers`. All values
 *  arrive as strings; `reset` is in seconds. Any of them can be missing. */
export interface RateLimitInfo {
  limit: number | null;
  remaining: number | null;
  reset: number | null;
  used: number | null;
}

/** Thrown internally so RxJS `retry` can drive the wait. Not an `Error` subclass on purpose — it is
 *  a control-flow signal, never surfaced to the user. `repace` is `false` for a hit a `beforeStep`
 *  hook's read ran into (#454): its numbers belong to another bucket than the mutations' and must
 *  not re-pace them. */
export class RateLimitHit {
  constructor(
    readonly info: RateLimitInfo,
    readonly repace = true,
  ) {}
}

export interface SevenTvGqlError {
  message?: string;
  extensions?: {
    code?: string;
    status?: number;
    headers?: Record<string, string>;
  };
}

function parseHeaderNumber(headers: Record<string, string> | undefined, suffix: string) {
  const raw = headers?.[`x-ratelimit-${RATE_LIMIT_RESOURCE}-${suffix}`];
  if (raw === undefined) {
    return null;
  }
  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) ? parsed : null;
}

export function isRateLimitError(error: SevenTvGqlError): boolean {
  return error.extensions?.code === RATE_LIMIT_ERROR_CODE || error.extensions?.status === 429;
}

export function readRateLimitInfo(error: SevenTvGqlError): RateLimitInfo {
  const headers = error.extensions?.headers;
  return {
    limit: parseHeaderNumber(headers, 'limit'),
    remaining: parseHeaderNumber(headers, 'remaining'),
    reset: parseHeaderNumber(headers, 'reset'),
    used: parseHeaderNumber(headers, 'used'),
  };
}
