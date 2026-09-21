import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { normalizeChannelName } from '../channels/channel-name';
import { ForeignEmoteSetResponse } from './foreign-emote-set.model';
import { LeaderboardSort } from './leaderboard.model';
import { EmoteSetListResponse, EmoteSetTargetsResponse } from './seven-tv-emote-set.model';

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
