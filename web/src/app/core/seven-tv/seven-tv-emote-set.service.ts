import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, of, tap } from 'rxjs';

import { normalizeChannelName } from '../channels/channel-name';
import { ForeignEmoteSetResponse } from './foreign-emote-set.model';
import { LeaderboardSort } from './leaderboard.model';
import { EmoteSetListResponse, EmoteSetTargetsResponse } from './seven-tv-emote-set.model';

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

  /** 6.1 — the set list of a *tracked* channel (K4's usage-stats dropdown). `isActive` in the
   *  response is `Channel.ActiveEmoteSetId` (E21), our own observed state. */
  listChannelEmoteSets(channelName: string): Observable<EmoteSetListResponse> {
    const normalized = normalizeChannelName(channelName);
    return this.http.get<EmoteSetListResponse>(`/api/channels/${normalized}/emote-sets`);
  }

  /** 6.2 — the target picker's offer list (K2): the caller's own 7TV account, plus every account
   *  they hold a 7TV editor grant for, own account first. */
  listEmoteSetTargets(): Observable<EmoteSetTargetsResponse> {
    return this.http.get<EmoteSetTargetsResponse>('/api/seventv/me/emote-set-targets');
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
