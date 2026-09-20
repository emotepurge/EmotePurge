import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { normalizeChannelName } from '../channels/channel-name';
import { ForeignEmoteSetResponse } from './foreign-emote-set.model';
import { EmoteSetListResponse, EmoteSetTargetsResponse } from './seven-tv-emote-set.model';

/**
 * The frontend counterpart of `ISevenTvEmoteSetListService.ListByTwitchIdAsync` (spec 2026-09-20,
 * E6) — one service behind the three "list emote sets" routes, plus the set-ID preview (6.4), which
 * shares the same underlying endpoint as `ForeignEmoteSetService.load()` but adds the query
 * parameter that method never needed.
 *
 * Deliberately not scoped to the target picker (K2) alone: K3's source-set picker
 * (`listForeignChannelEmoteSets`) and K4's usage-stats dropdown (`listChannelEmoteSets`) read the
 * same three routes this service exposes, and land in later kind-issues against this same file
 * rather than a second one.
 *
 * Thin like its `ForeignEmoteSetService` sibling: the backend owns caching, coalescing and the
 * breaker for every route here (spec 6.1's guard chain); this only shapes requests. Error mapping is
 * left to callers via `apiErrorTranslationKey`, same as the rest of `core/`.
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
   * the upstream query. `refresh: true` bypasses the backend's 60 s cache, same escape hatch as
   * `ForeignEmoteSetService.load()`.
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
}
