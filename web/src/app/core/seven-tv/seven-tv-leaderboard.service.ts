import { HttpClient, HttpParams } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { LeaderboardSort, SevenTvLeaderboardResponse } from './leaderboard.model';

/**
 * The one client for `GET /api/seventv/leaderboard` (spec §4) — 7TV's network-wide ranking as the
 * third import source.
 *
 * Thinner than `SevenTvEmoteSetService.loadEmoteSetPreview` on purpose, and the difference is the
 * point: that method takes a `refresh=true` that bypasses the server's 60 s cache, and this one has
 * **no** such escape hatch. The leaderboard's whole safety argument rests on the server calling 7TV at
 * most ten times an hour no matter how often anyone clicks (spec §1/§6); a client-triggerable cache
 * bypass would hand every logged-in browser one upstream request per click and take the ceiling
 * with it. So `sortBy` is the only thing that goes on the wire — no `page`, no `perPage`, no
 * `refresh` (spec §4, "Bypass-Freiheit"). A "load again" in the UI re-reads the server's stock.
 *
 * Error mapping is left to the caller through `apiErrorTranslationKey`, as with every other feature
 * service in `core/`: the 400 (`invalid_leaderboard_sort`) and the 503
 * (`foreign_channel_seventv_unavailable`, reused for every failure state — E13) are registered in
 * `KNOWN_API_ERROR_CODES` already.
 */
@Service()
export class SevenTvLeaderboardService {
  private readonly http = inject(HttpClient);

  load(sortBy: LeaderboardSort): Observable<SevenTvLeaderboardResponse> {
    return this.http.get<SevenTvLeaderboardResponse>('/api/seventv/leaderboard', {
      params: new HttpParams().set('sortBy', sortBy),
    });
  }
}
