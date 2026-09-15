import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { map, Observable } from 'rxjs';

import { LeaderboardSort } from '../seven-tv/leaderboard.model';
import { EmoteListItem } from './emote-list-item.model';
import { EmoteSetStatus } from './emote-set-status.model';

export interface SyncDeletedResult {
  archivedCount: number;
  notFoundIds: string[];
}

export interface SyncRestoredResult {
  restoredCount: number;
  notFoundIds: string[];
}

export interface EmoteSetWarning {
  available: boolean;
  isOwnSet: boolean;
  otherTrackedChannelsSharingSet: string[];
  otherModeratedChannelsSharingSet: string[];
}

/** Request body of syncImported — same shape the server's `SyncImportedRequest` binds.
 *
 *  `sourceKind` is spelled out here rather than derived from `ImportOrigin['kind']`: this is the
 *  wire contract, and it has to be readable next to the endpoint that validates it
 *  (`EmoteEndpoints.cs`, closed vocabulary, ordinal and lower-case). The union stays in step with
 *  `ImportOrigin` because `importOriginSourceChannelName`/`importOriginLeaderboardSort` are
 *  exhaustive over that union — a fifth origin breaks the build there and lands here on the way
 *  past. `sourceChannelName` must be set for `channel`/`seventv-channel`, `leaderboardSort` for
 *  `seventv-leaderboard`; the server answers 400 for either gap, *after* the 7TV writes have
 *  happened (spec F1). */
export interface SyncImportedBody {
  sevenTvEmoteIds: string[];
  sourceChannelName: string | null;
  sourceKind: 'channel' | 'file' | 'seventv-channel' | 'seventv-leaderboard';
  leaderboardSort: LeaderboardSort | null;
}

/** Wire shape of GET .../emotes — wrapped in an object like the admin channel list, not a bare
 *  array, so the endpoint stays extensible without a contract break. Unwrapped by listEmotes(). */
interface EmoteListResponse {
  emotes: EmoteListItem[];
}

@Injectable({ providedIn: 'root' })
export class EmoteAdminService {
  private readonly http = inject(HttpClient);

  /** Reports already-deleted (7TV-side) internal emote ids so Postgres reflects it immediately —
   *  the 1-minute SevenTvPeriodicResyncWorker is the actual safety net regardless. */
  syncDeleted(channelName: string, emoteIds: string[]): Observable<SyncDeletedResult> {
    return this.http.post<SyncDeletedResult>(`/api/channels/${channelName}/emotes/sync-deleted`, {
      emoteIds,
    });
  }

  /** The restore counterpart of syncDeleted: un-archives the re-added emotes server-side and —
   *  its actual purpose — writes the emotes.syncRestored audit entry. Without it a restore only
   *  ever appeared in the log as an anonymous channel.resync. */
  syncRestored(channelName: string, emoteIds: string[]): Observable<SyncRestoredResult> {
    return this.http.post<SyncRestoredResult>(`/api/channels/${channelName}/emotes/sync-restored`, {
      emoteIds,
    });
  }

  /** Best-effort check whether this channel's active 7TV set is shared with/owned by someone else —
   *  see EmoteSetOwnershipService, can never be fully complete (7TV has no reverse "who else has this
   *  set active" lookup). */
  getSetWarning(channelName: string): Observable<EmoteSetWarning> {
    return this.http.get<EmoteSetWarning>(`/api/channels/${channelName}/emotes/set-warning`);
  }

  /** Deliberately separate from ChannelService.getStatus (management-only): a 7TV editor without
   *  Twitch-mod status must still see this to render the mass-delete panel's "Löschen" button.
   *  Carries the slot budget and the tracking start too — same audience, same page, one request. */
  getSetStatus(channelName: string): Observable<EmoteSetStatus> {
    return this.http.get<EmoteSetStatus>(`/api/channels/${channelName}/emotes/active-set`);
  }

  /** The import dialog's own picture of the target set — no time range, no usage numbers — so it
   *  can answer "already there?" and "name collision?" without pulling in the full usage grid.
   *  Fetched once per dialog open, not cached: the server has already reduced it to ~50 KB. */
  listEmotes(channelName: string): Observable<EmoteListItem[]> {
    return this.http
      .get<EmoteListResponse>(`/api/channels/${channelName}/emotes`)
      .pipe(map((response) => response.emotes));
  }

  /** The import's only server-side effect: one audit entry at the target channel. It never touches
   *  Emote rows itself — the resync the import triggers afterwards is what actually adds them. */
  syncImported(channelName: string, body: SyncImportedBody): Observable<void> {
    return this.http.post<void>(`/api/channels/${channelName}/emotes/sync-imported`, body);
  }
}
