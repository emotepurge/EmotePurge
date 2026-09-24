import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { map, Observable, of, tap } from 'rxjs';

import { normalizeChannelName } from '../channels/channel-name';
import { ForeignEmoteSetResponse } from './foreign-emote-set.model';
import { LeaderboardSort } from './leaderboard.model';
import {
  EditableSetResolution,
  EditableSetTarget,
  EmoteSetListResponse,
  EmoteSetTargetAccount,
  EmoteSetTargetSummary,
  EmoteSetTargetsResponse,
  SyncDeletedInSetResponse,
  SyncInSetBody,
  SyncRestoredInSetResponse,
} from './seven-tv-emote-set.model';

/**
 * Mirrors the backend's own cache TTL for the set-ID preview route (6.4's Redis entry, keyed per
 * set id) — chosen so a client-side hit never claims freshness the server would not also have
 * granted a same-moment second reader. The reason this exists at all (operator decision
 * 2026-09-22): that backend cache sits *behind* the shared `ForeignEmoteLookup` limiter (10
 * permits/60 s), so a cache hit there still spends a permit — quickly switching between sets on the
 * usage page hit 429 on the limiter alone, cache or not. See {@link
 * SevenTvEmoteSetService.loadCachedEmoteSetPreview} for what this actually guards and why it does
 * not cover every caller of {@link SevenTvEmoteSetService.loadEmoteSetPreview}.
 */
const EMOTE_SET_PREVIEW_CACHE_TTL_MS = 60_000;

interface EmoteSetPreviewCacheEntry {
  readonly response: ForeignEmoteSetResponse;
  readonly expiresAtMs: number;
}

/**
 * 60 s, same TTL and same reasoning as {@link EMOTE_SET_PREVIEW_CACHE_TTL_MS} (spec F3, E19): the
 * target list sits behind the same `ForeignEmoteLookup` bucket, and since E19 up to three
 * pre-checks (restore, delete, replace) can ask for it inside one user action — without this, each
 * would spend its own permit for what is, in the common case, the exact same answer.
 */
const EMOTE_SET_TARGETS_CACHE_TTL_MS = 60_000;

interface EmoteSetTargetsCacheEntry {
  readonly response: EmoteSetTargetsResponse;
  readonly expiresAtMs: number;
}

/** Every set across every account of a target list, paired with the account it belongs to — the
 *  shape {@link toEditableSetTarget} needs to build an {@link EditableSetTarget} without querying
 *  the response twice for the same account. */
function findTargetSet(
  response: EmoteSetTargetsResponse,
  emoteSetId: string,
): { readonly account: EmoteSetTargetAccount; readonly set: EmoteSetTargetSummary } | null {
  for (const account of response.accounts) {
    const set = account.sets.find((candidate) => candidate.id === emoteSetId);
    if (set !== undefined) {
      return { account, set };
    }
  }
  return null;
}

/** A blank or whitespace-only display value counts as absent, same rule as
 *  `import-target-choices.ts`'s `resolveOwnerLabel` — the one other place this codebase already
 *  falls a 7TV display name back to a Twitch login. Not imported from there: `shared/` may depend
 *  on `core/`, never the other way round (Schichtentreue). */
function nonBlank(value: string): string | null {
  return value.trim().length > 0 ? value : null;
}

function toEditableSetTarget(
  account: EmoteSetTargetAccount,
  set: EmoteSetTargetSummary,
): EditableSetTarget {
  const ownerDisplayName = set.ownerDisplayName !== null ? nonBlank(set.ownerDisplayName) : null;
  return {
    emoteSetId: set.id,
    setName: nonBlank(set.name) ?? set.id,
    ownerDisplayName: ownerDisplayName ?? account.twitchLogin,
    twitchLogin: account.twitchLogin,
    trackedChannelName: account.trackedChannelName,
    isActiveSet: account.activeEmoteSetId === set.id,
  };
}

/**
 * Pure classification behind {@link SevenTvEmoteSetService.resolveEditableSet} (spec 4.2, E19) —
 * kept as a free function so it reads as one decision table instead of being buried in the
 * Observable pipeline. Order matters (spec 4.2's Grenzfälle):
 *
 * 1. A found set with `kind !== 'NORMAL'` is `notSelectable` *regardless* of `editable` — a
 *    personal/global/special set is never offered as a target in the first place (E11), so a
 *    stray one reaching here can only be a rückweg-Datei from before that rule, never a case where
 *    "but it's editable" should win.
 * 2. A found, `NORMAL`, `editable` set is `editable` outright — even when the response also
 *    reports `sevenTvUnavailable` for some *other*, unrelated account (spec 4.2 Grenzfall,
 *    Abschnitt 6 Nr. 2): a confirmed positive is never downgraded by a degradation elsewhere.
 * 3. Anything else (not found at all, or found but `editable === false`) is `notEditable` unless
 *    the list itself was incomplete (`sevenTvUnavailable`, or some account's own list unreadable) —
 *    then it is `unavailable`, because the true answer might be `editable` and the list simply
 *    never got to say so (F5).
 */
function classifyEditableSet(
  response: EmoteSetTargetsResponse,
  emoteSetId: string,
): EditableSetResolution {
  const found = findTargetSet(response, emoteSetId);
  if (found !== null) {
    if (found.set.kind !== 'NORMAL') {
      return { status: 'notSelectable' };
    }
    if (found.set.editable) {
      return { status: 'editable', target: toEditableSetTarget(found.account, found.set) };
    }
  }
  const listIncomplete =
    response.sevenTvUnavailable || response.accounts.some((account) => account.setsUnavailable);
  return { status: listIncomplete ? 'unavailable' : 'notEditable' };
}

/** Request body of the set-centric report (spec 6.7) — the same shape as
 *  `EmoteAdminService`'s `SyncImportedBody` minus `targetEmoteSetId`: the route already names the
 *  set (`POST /api/seventv/emote-sets/{emoteSetId}/sync-imported`), so repeating it in the body
 *  would be a second, redundant source of truth for the very thing the URL already pins down. */
export interface SyncImportedToSetBody {
  sevenTvEmoteIds: string[];
  sourceChannelName: string | null;
  sourceKind: 'channel' | 'file' | 'seventv-channel' | 'seventv-leaderboard';
  leaderboardSort: LeaderboardSort | null;
}

/**
 * The frontend counterpart of `ISevenTvEmoteSetListService.ListByTwitchIdAsync` (spec 2026-09-20,
 * E6) — one service behind the three "list emote sets" routes, plus the set-ID preview (6.4), which
 * shares the same underlying endpoint `loadEmoteSetPreview` below reads but adds the query parameter
 * the plain login-mode preview never needed.
 *
 * Not scoped to the target picker (K2) alone: `ForeignChannelStep` (K3's source-set picker) reads
 * `listForeignChannelEmoteSets` and `loadEmoteSetPreview` together to resolve a foreign channel's
 * sets and preview the chosen one — always by set id (spec 8.7), never through the login-only mode
 * `ForeignEmoteSetService` used to serve before it was retired in favour of this always-by-set-id
 * flow. K4's usage-stats dropdown (`listChannelEmoteSets`) reads the third route the same way.
 *
 * Thin by design: the backend owns caching, coalescing and the breaker for every route here (spec
 * 6.1's guard chain); this only shapes requests. Error mapping is left to callers via
 * `apiErrorTranslationKey`, same as the rest of `core/`.
 */
@Injectable({ providedIn: 'root' })
export class SevenTvEmoteSetService {
  private readonly http = inject(HttpClient);

  /** Backing store for {@link loadCachedEmoteSetPreview} only — keyed `${channelName}:${emoteSetId}`
   *  on the already-normalized channel name. Never read or written by {@link loadEmoteSetPreview}
   *  itself, see that cached method's doc for why. */
  private readonly cachedPreviews = new Map<string, EmoteSetPreviewCacheEntry>();

  /** Backing store for {@link loadCachedEmoteSetTargets} only — a single entry, unlike
   *  {@link cachedPreviews}: there is exactly one target list per caller (`GET
   *  /api/seventv/me/emote-set-targets` takes no parameters), so a `Map` would only add a key
   *  nothing ever varies. */
  private cachedTargets: EmoteSetTargetsCacheEntry | null = null;

  /** 6.1 — the set list of a *tracked* channel (K4's usage-stats dropdown). `isActive` in the
   *  response is `Channel.ActiveEmoteSetId` (E21), our own observed state. */
  listChannelEmoteSets(channelName: string): Observable<EmoteSetListResponse> {
    const normalized = normalizeChannelName(channelName);
    return this.http.get<EmoteSetListResponse>(`/api/channels/${normalized}/emote-sets`);
  }

  /** 6.2 — the target picker's offer list (K2): the caller's own 7TV account, plus every account
   *  they hold a 7TV editor grant for, own account first. Uncached — {@link loadCachedEmoteSetTargets}
   *  is the 60 s-deduped wrapper every caller since E19 should reach for instead; this stays the
   *  one place that actually issues the request. */
  listEmoteSetTargets(): Observable<EmoteSetTargetsResponse> {
    return this.http.get<EmoteSetTargetsResponse>('/api/seventv/me/emote-set-targets');
  }

  /**
   * A cached wrapper around {@link listEmoteSetTargets} (spec F3, 6.2, E19) — same three rules as
   * {@link loadCachedEmoteSetPreview}, just for the single target list instead of a per-set preview:
   * a call within {@link EMOTE_SET_TARGETS_CACHE_TTL_MS} of the last *successful* answer is served
   * from here without a request; `options.refresh` always bypasses the cache and replaces whatever
   * was there; only a successful response is ever cached, so a retry after an error always asks
   * again. This is what lets up to three pre-checks in one user action (restore's file step, a
   * delete confirmation, a replace start) and the target picker itself cost at most one permit
   * together (AK 6) — {@link resolveEditableSet} reads through this, and so does
   * `ImportTargetDialog`, whose own retry action is the one caller that passes `refresh: true`.
   */
  loadCachedEmoteSetTargets(
    options: { refresh?: boolean } = {},
  ): Observable<EmoteSetTargetsResponse> {
    if (
      !options.refresh &&
      this.cachedTargets !== null &&
      this.cachedTargets.expiresAtMs > Date.now()
    ) {
      return of(this.cachedTargets.response);
    }
    return this.listEmoteSetTargets().pipe(
      tap((response) => {
        this.cachedTargets = {
          response,
          expiresAtMs: Date.now() + EMOTE_SET_TARGETS_CACHE_TTL_MS,
        };
      }),
    );
  }

  /**
   * The one shared pre-check every first mutation into a 7TV set runs before touching it (spec 4.2,
   * 6.2, E19: restore's file step, a delete confirmation, a replace start with a replace row) — it
   * reads {@link loadCachedEmoteSetTargets} (so it is free whenever the picker or another pre-check
   * already warmed the cache this minute) and classifies the one set the caller cares about into
   * the four outcomes {@link EditableSetResolution} distinguishes. It trusts the backend's own
   * `editable` verdict rather than recomputing it (spec 5.8: "das Frontend liest `editable`, es
   * berechnet es nicht") — see {@link classifyEditableSet} for the exact decision table.
   */
  resolveEditableSet(emoteSetId: string): Observable<EditableSetResolution> {
    return this.loadCachedEmoteSetTargets().pipe(
      map((response) => classifyEditableSet(response, emoteSetId)),
    );
  }

  /** `POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted` (spec 5.1, 6.4/6.5) — the set-centric
   *  closing report for a delete or a replace's confirmed removals. Replaces the channel-bound
   *  `EmoteAdminService.syncDeleted` for every caller (spec E3); `expectedChannelName` in the body
   *  is what lets the backend tell "nothing to report" apart from "reported the wrong channel"
   *  (E18). */
  reportDeletedInSet(
    emoteSetId: string,
    body: SyncInSetBody,
  ): Observable<SyncDeletedInSetResponse> {
    return this.http.post<SyncDeletedInSetResponse>(
      `/api/seventv/emote-sets/${emoteSetId}/sync-deleted`,
      body,
    );
  }

  /** `POST /api/seventv/emote-sets/{emoteSetId}/sync-restored` (spec 5.1, 6.4) — spiegelbildlich zu
   *  {@link reportDeletedInSet}; replaces the channel-bound `EmoteAdminService.syncRestored`. */
  reportRestoredInSet(
    emoteSetId: string,
    body: SyncInSetBody,
  ): Observable<SyncRestoredInSetResponse> {
    return this.http.post<SyncRestoredInSetResponse>(
      `/api/seventv/emote-sets/${emoteSetId}/sync-restored`,
      body,
    );
  }

  /** 6.3 — the set list of a *foreign* channel (K3's source-set picker). `isActive` in the response
   *  is 7TV's own `style.activeEmoteSetId` (E21) — this path never resolves a `Channel` row. */
  listForeignChannelEmoteSets(channelName: string): Observable<EmoteSetListResponse> {
    const normalized = normalizeChannelName(channelName);
    return this.http.get<EmoteSetListResponse>(`/api/seventv/channels/${normalized}/emote-sets`);
  }

  /**
   * 6.4's set-ID read mode: a preview of one specific (possibly non-active) set of `channelName`.
   * No identity resolution runs on this path (E8) — `emoteSetId` alone selects the cache entry and
   * the upstream query. `refresh: true` bypasses the backend's 60 s cache. `ForeignChannelStep`
   * (K3, spec 8.7) calls this for *every* preview it loads, active set included — never the plain
   * login-mode `GET …/emotes` (no `emoteSetId`) this same route also answers — so a source channel's
   * radiogroup selection and its preview request always name the same set explicitly.
   */
  loadEmoteSetPreview(
    channelName: string,
    emoteSetId: string,
    options: { refresh?: boolean } = {},
  ): Observable<ForeignEmoteSetResponse> {
    const normalized = normalizeChannelName(channelName);
    let params = new HttpParams().set('emoteSetId', emoteSetId);
    if (options.refresh) {
      params = params.set('refresh', 'true');
    }
    return this.http.get<ForeignEmoteSetResponse>(`/api/seventv/channels/${normalized}/emotes`, {
      params,
    });
  }

  /**
   * A cached wrapper around {@link loadEmoteSetPreview} for K4's usage-stats page, and since #227
   * also the vote-session detail page's own live-membership check (K6 follow-up: which ballot rows
   * are no longer members of the session's set) — **not** used by K3's `ForeignChannelStep` or the
   * K2/T4.5 import-target loader, which keep calling the plain method directly. Deliberately scoped
   * this narrowly (operator decision 2026-09-22):
   *
   * - A params-driven load (a set switch) within {@link EMOTE_SET_PREVIEW_CACHE_TTL_MS} of the last
   *   *successful* answer for the same `(channelName, emoteSetId)` is served from here without a
   *   request at all — the client-side half of closing the 429s a fast A→B→A switch produced.
   * - `options.refresh` always bypasses the cache, fetches, and replaces whatever entry was there —
   *   the loud reload (`channel.synced`, the refresh button) must never show data the caller just
   *   said not to trust.
   * - Only a successful response is ever cached; a 429/503 is never stored, so a retry after an
   *   error always asks again.
   * - Keyed on the normalized channel name, so a cached set id can never answer for another
   *   channel; there is no cross-channel bound beyond that (a handful of sets per channel in
   *   practice), matching {@link ChannelService.getPermissions}'s own per-key TTL cache rather than
   *   introducing a second eviction scheme for the same shape of problem.
   *
   * K3 already solves the same rate-limit symptom for its own radiogroup switches with a
   * component-scoped, indefinitely-lived preview cache (`ForeignChannelStep.previewCache`) that
   * additionally guards a same-set-id request race (P3-5: an older answer must never overwrite a
   * newer one) — a guarantee this TTL cache does not make, because it caches whatever response
   * arrives, in arrival order, with no notion of which request was issued first. Layering this cache
   * underneath K3's would risk exactly that: an older, later-arriving response landing here after a
   * newer one, then being served back on some later switch past the point where K3's own guard would
   * have discarded it — a correctness regression for a symptom K3 has already fixed on its own, for
   * no benefit it does not already have (its cache never expires within one dialog session and is
   * cleared exactly when the data it holds stops being trustworthy). Sharing the same backend TTL
   * and the same permit bucket between the tracked-channel preview (K4) and the foreign-channel one
   * (K2/K3) is itself a separate, existing question — splitting `ForeignEmoteLookup` into its own
   * bucket per use is a follow-up issue.
   */
  loadCachedEmoteSetPreview(
    channelName: string,
    emoteSetId: string,
    options: { refresh?: boolean } = {},
  ): Observable<ForeignEmoteSetResponse> {
    const key = `${normalizeChannelName(channelName)}:${emoteSetId}`;
    if (!options.refresh) {
      const cached = this.cachedPreviews.get(key);
      if (cached && cached.expiresAtMs > Date.now()) {
        return of(cached.response);
      }
    }
    return this.loadEmoteSetPreview(channelName, emoteSetId, options).pipe(
      tap((response) => {
        this.cachedPreviews.set(key, {
          response,
          expiresAtMs: Date.now() + EMOTE_SET_PREVIEW_CACHE_TTL_MS,
        });
      }),
    );
  }

  /**
   * 6.7's set-centric report: the closing bookkeeping call for a copy run into an *untracked*
   * account's set (T2.6). Writes an `AuditLogDetail` with `ChannelName = null` (admin-only log,
   * spec 6.7) — there is no `Channel` of ours to own the entry, which is also why
   * `SevenTvImportService` never follows this call with a resync (T2.6/8.6): a resync pulls emote
   * rows into *our* database for a tracked channel, and an untracked target has none to pull into.
   */
  reportImportedToSet(emoteSetId: string, body: SyncImportedToSetBody): Observable<void> {
    return this.http.post<void>(`/api/seventv/emote-sets/${emoteSetId}/sync-imported`, body);
  }
}
