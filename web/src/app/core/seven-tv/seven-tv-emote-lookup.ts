import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Service, inject, signal } from '@angular/core';
import { Observable, catchError, map, of, timeout } from 'rxjs';

import { SevenTvGqlError, isRateLimitError } from './seven-tv-rate-limit';

/** Same public endpoint as the set reads (`seven-tv-set-entries.ts`); no token, no `Authorization`. */
const SEVEN_TV_GQL_ENDPOINT = 'https://7tv.io/v4/gql';

/**
 * The one emote by id, measured live on 2026-11-18 (#485, T6): `emotes { emote(id) }` answers the
 * emote, or `emote: null` for an id 7TV does not know, both with HTTP 200 and no `errors`. A
 * malformed id is an `errors` entry ("Failed to parse \"Id\""); the client never sends one, since
 * `parseEmoteInput` lets only the two id forms through. `deleted` is read so a deleted emote counts
 * as unknown rather than as a name to prefill.
 */
const GQL_EMOTE_QUERY =
  'query($id: Id!) { emotes { emote(id: $id) { id defaultName deleted flags { animated } } } }';

/** A single small read; past this the dialog falls back to a hand-typed alias. */
export const SEVEN_TV_EMOTE_LOOKUP_TIMEOUT_MS = 8000;

/** Crockford base32, the ULID alphabet. */
const ULID_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
const OBJECT_ID = /^[0-9a-f]{24}$/i;
const ULID = /^[0-9A-HJKMNP-TV-Z]{26}$/i;

/** What one read found, always for the id it was asked for (`id`, as passed in). */
export type SevenTvEmoteLookupResult =
  | {
      id: string;
      kind: 'found';
      /** 7TV's default name; `''` when 7TV sent none. */
      name: string;
      /** `null` when the answer carried no flag. */
      animated: boolean | null;
    }
  | { id: string; kind: 'unknown' }
  | { id: string; kind: 'unreachable' }
  | { id: string; kind: 'throttled' };

interface SevenTvGqlEmoteResponse {
  data?: {
    emotes?: {
      emote?: {
        id?: string;
        defaultName?: string | null;
        deleted?: boolean | null;
        flags?: { animated?: boolean | null } | null;
      } | null;
    } | null;
  } | null;
  errors?: unknown[];
}

/**
 * The id under which the rest of EmotePurge knows an emote (#485, T6). 7TV answers both id forms,
 * but always with the 26-character ULID, upper case; sets, slots and wishes carry that form. An older
 * 24-character hex ObjectID maps onto it without a request: the ULID's 48-bit time is the ObjectID's
 * 4-byte seconds in milliseconds, then 16 zero bits, then the ObjectID's other 8 bytes. Measured live
 * on 2026-11-18 for three emotes (`60ae958e229664e8667aea38` → `01F6MZGCNG000255K4X1K7NTHR`, two more
 * the other way round). Anything that is neither form comes back unchanged.
 */
export function canonicalSevenTvEmoteId(id: string): string {
  if (ULID.test(id)) {
    return id.toUpperCase();
  }
  if (!OBJECT_ID.test(id)) {
    return id;
  }
  const seconds = BigInt(`0x${id.slice(0, 8)}`);
  const rest = BigInt(`0x${id.slice(8)}`);
  let value = ((seconds * 1000n) << 80n) | rest;
  let ulid = '';
  for (let index = 0; index < 26; index++) {
    ulid = ULID_ALPHABET[Number(value & 31n)] + ulid;
    value >>= 5n;
  }
  return ulid;
}

/**
 * The 7TV single read of the wish forms (#485, plan T6): one emote by id, public, without a token,
 * with a deadline. The answer never errors; it is one of four results, each tagged with the id that
 * was asked for, so a caller can tell a late answer for an earlier input from the current one.
 *
 * 7TV disguises its rate limit as HTTP 200 with `extensions.status: 429`; that and a real 429 are
 * `throttled`. Any other failure, the deadline included, is `unreachable`: a GraphQL error is not a
 * statement about the emote.
 *
 * "Found, not animated" is remembered for the session per emote (under its canonical id): the wish
 * picture rule (plan 2.10) may then show the plain rendition of an emote the channel never had in its
 * set. Nothing else is remembered; "animated" and "unknown" unlock nothing.
 */
@Service()
export class SevenTvEmoteLookup {
  private readonly http = inject(HttpClient);

  /** The canonical ids this session's reads found not animated. A signal, so a reader reacts. */
  private readonly stillIds = signal<ReadonlySet<string>>(new Set());

  lookup(id: string): Observable<SevenTvEmoteLookupResult> {
    return this.http
      .post<SevenTvGqlEmoteResponse>(SEVEN_TV_GQL_ENDPOINT, {
        query: GQL_EMOTE_QUERY,
        variables: { id },
      })
      .pipe(
        timeout(SEVEN_TV_EMOTE_LOOKUP_TIMEOUT_MS),
        map((response) => this.read(id, response)),
        catchError((error: unknown) =>
          of<SevenTvEmoteLookupResult>(
            error instanceof HttpErrorResponse && error.status === 429
              ? { id, kind: 'throttled' }
              : { id, kind: 'unreachable' },
          ),
        ),
      );
  }

  /** Whether a read of this session found the emote not animated (any id form). */
  isKnownStill(sevenTvEmoteId: string): boolean {
    return this.stillIds().has(canonicalSevenTvEmoteId(sevenTvEmoteId));
  }

  private read(id: string, response: SevenTvGqlEmoteResponse): SevenTvEmoteLookupResult {
    if (response.errors !== undefined && response.errors.length > 0) {
      const throttled = response.errors.some(
        (error) =>
          typeof error === 'object' && error !== null && isRateLimitError(error as SevenTvGqlError),
      );
      return { id, kind: throttled ? 'throttled' : 'unreachable' };
    }
    const emotes = response.data?.emotes;
    if (emotes === undefined || emotes === null) {
      // No data at all is not an answer about the emote.
      return { id, kind: 'unreachable' };
    }
    const emote = emotes.emote;
    if (emote === undefined || emote === null || emote.deleted === true) {
      return { id, kind: 'unknown' };
    }
    const animated = emote.flags?.animated ?? null;
    if (animated === false) {
      this.rememberStill(canonicalSevenTvEmoteId(emote.id ?? id));
    }
    return { id, kind: 'found', name: emote.defaultName ?? '', animated };
  }

  private rememberStill(canonicalId: string): void {
    if (this.stillIds().has(canonicalId)) {
      return;
    }
    this.stillIds.update((ids) => new Set(ids).add(canonicalId));
  }
}
