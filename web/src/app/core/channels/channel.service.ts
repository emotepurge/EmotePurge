import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, Observable, shareReplay, throwError } from 'rxjs';

import { normalizeChannelName } from './channel-name';
import {
  ChannelDataSummary,
  ChannelPermissions,
  ChannelStatus,
  MyChannelsResult,
} from './channel.model';

/** Long enough to collapse one navigation's worth of readers, short enough that a role change
 *  (a new mod, a revoked 7TV editor) shows up on the next page the user opens rather than after
 *  a reload. The server's own role cache is the authority on freshness; this only stops the same
 *  answer being asked for three times in one breath. */
const PERMISSIONS_TTL_MS = 30_000;

interface PermissionsCacheEntry {
  readonly permissions$: Observable<ChannelPermissions>;
  readonly expiresAtMs: number;
}

@Injectable({ providedIn: 'root' })
export class ChannelService {
  private readonly http = inject(HttpClient);
  private readonly permissionsCache = new Map<string, PermissionsCacheEntry>();

  getStatus(channelName: string): Observable<ChannelStatus> {
    return this.http.get<ChannelStatus>(`/api/channels/${channelName}`);
  }

  /**
   * The one permission read for a channel. Succeeds for every logged-in user (it reports what they
   * may do, it does not gate on it), so callers read the flags — they must not treat a 403 as
   * "not allowed", the way the probe calls this replaced had to.
   *
   * Cached per channel for {@link PERMISSIONS_TTL_MS}, because opening a channel asks three
   * independent times: the route guard, the workspace layout, and the page's own resource. All
   * three are legitimate — each needs the answer for its own decision — but on the wire they were
   * three identical requests, and each one costs a live Twitch/7TV role check server-side plus a
   * rate-limit permit. That is what made ordinary clicking-around hit 429 back when those permits
   * came from a 40/min fixed window; the limit had already been doubled once for the same symptom,
   * which is the sign that the requests, not the ceiling, were the problem. The window is gone
   * since #33 (InteractiveRead, a token bucket), but the caching reason stands on its own. `shareReplay` also covers the in-flight case, so
   * the three readers of a single navigation share one response even before the TTL matters.
   *
   * Deliberately NOT extended to `listMine()`: the overview refetches that on live events, and a
   * cache would answer those refetches with the very state they were sent to replace.
   *
   * One behavioural consequence for callers: a cache hit emits **synchronously** on subscribe,
   * where the bare HTTP call always emitted later. `ChannelWorkspaceLayout` subscribes from inside
   * an `effect()`, so its signal writes now happen during that effect rather than after it — which
   * is allowed, and holds because the signals it writes are not the ones the effect reads. A future
   * caller that subscribes from a `computed()` would not be so lucky.
   */
  getPermissions(channelName: string): Observable<ChannelPermissions> {
    const key = normalizeChannelName(channelName);
    const cached = this.permissionsCache.get(key);
    if (cached && cached.expiresAtMs > Date.now()) {
      return cached.permissions$;
    }

    const permissions$ = this.http
      .get<ChannelPermissions>(`/api/channels/${channelName}/permissions`)
      .pipe(
        // A failure must not be what the next caller gets served for 30 seconds: `shareReplay`
        // replays a terminal error to every later subscriber and never re-subscribes the source,
        // so the entry has to go before the error is allowed through.
        catchError((error: unknown) => {
          this.permissionsCache.delete(key);
          return throwError(() => error);
        }),
        shareReplay({ bufferSize: 1, refCount: false }),
      );

    this.permissionsCache.set(key, {
      permissions$,
      expiresAtMs: Date.now() + PERMISSIONS_TTL_MS,
    });
    return permissions$;
  }

  /**
   * Drops cached permissions — for one channel, or all of them when called without a name.
   * Called on session reset (`AuthService`) and after anything that can change what a user may do.
   */
  invalidatePermissions(channelName?: string): void {
    if (channelName === undefined) {
      this.permissionsCache.clear();
      return;
    }
    this.permissionsCache.delete(normalizeChannelName(channelName));
  }

  /**
   * `liftBroadcasterLock` is only sent after an admin has confirmed the prompt for a channel its
   * broadcaster deleted and locked, together with the lock date that prompt showed — exactly the
   * `lockedAtUtc` string of the 409, never re-formatted, because the server lifts only while the lock
   * still carries that date, compared at full precision. Without the option the request is
   * byte-identical to a plain join.
   */
  join(
    channelName: string,
    options?: { liftBroadcasterLock?: { confirmedLockedAtUtc: string } },
  ): Observable<ChannelStatus> {
    this.invalidatePermissions(channelName);
    const lift = options?.liftBroadcasterLock;
    const params = lift
      ? { liftBroadcasterLock: 'true', confirmedLockedAtUtc: lift.confirmedLockedAtUtc }
      : undefined;
    return this.http.post<ChannelStatus>(`/api/channels/${channelName}/join`, {}, { params });
  }

  getDataSummary(channelName: string): Observable<ChannelDataSummary> {
    return this.http.get<ChannelDataSummary>(`/api/channels/${channelName}/data-summary`);
  }

  /**
   * Irreversible, broadcaster-only: deletes this channel's data and locks the channel against
   * re-adding by anyone but its broadcaster. `expectedTwitchUserId` binds the call to the session
   * the user confirmed in (409 `account_mismatch` otherwise).
   */
  purgeOwnData(channelName: string, expectedTwitchUserId: string): Observable<void> {
    this.invalidatePermissions(channelName);
    return this.http.delete<void>(`/api/channels/${channelName}/data`, {
      params: { expectedTwitchUserId },
    });
  }

  leave(channelName: string): Observable<void> {
    this.invalidatePermissions(channelName);
    return this.http.delete<void>(`/api/channels/${channelName}`);
  }

  /**
   * Asks the worker for a full 7TV resync. Answers 202 — the command protocol is one-way, so this
   * only means "the worker was told". Completion arrives as a `channel.synced` live event.
   *
   * Called by the import and restore flows (the latter also for the undo after a mass delete), not
   * by a button of its own any more. Available to everyone who may see the usage stats, including
   * the channel's 7TV editors. Guarded server-side by a per-channel cooldown, which answers 429
   * with `resync_cooldown_active`.
   */
  resync(channelName: string): Observable<void> {
    return this.http.post<void>(`/api/channels/${channelName}/resync`, {});
  }

  /**
   * Irreversible: removes the channel with its emotes, usage history, vote sessions and votes
   * (server-side cascades). Global-admin-only — deliberately not reachable from any channel-scoped
   * screen, only from the admin channel page, and only behind a typed name confirmation.
   */
  purge(channelName: string): Observable<void> {
    this.invalidatePermissions(channelName);
    return this.http.delete<void>(`/api/channels/${channelName}/purge`);
  }

  listMine(): Observable<MyChannelsResult> {
    return this.http.get<MyChannelsResult>('/api/channels/mine');
  }
}
