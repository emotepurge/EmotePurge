import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { PagedResult } from '../models/paged-result.model';
import {
  AdoptWishBody,
  ApprovedWish,
  ApproveWishBody,
  CompleteWishRemovalBody,
  ConfirmWishRewardBody,
  CreatedWish,
  CreateWishBody,
  ReAddWishBody,
  UpdateWishBody,
  UpdateWishRewardBody,
  Wish,
  WishAdoptableSlot,
  WishConflict,
  WishDetail,
  WishListView,
  WishOverviewItem,
  WishReward,
  WishRewardLearning,
  WishRewardsRead,
  WishSettings,
  WishSettingsRead,
  WishSettingsUpdate,
  WishSummary,
} from './wish.model';

/**
 * The typed seam to the wish API (#481, K3): one method per route, the answer handed back unchanged.
 * No state, no cache, no error interpretation — what an error code means in a wish context is the job of
 * the callers (`apiErrorCode`, plan 2.9). `channelName` goes into the path as given; callers normalise it.
 */
@Injectable({ providedIn: 'root' })
export class WishService {
  private readonly http = inject(HttpClient);

  list(
    channelName: string,
    view: WishListView,
    page = 1,
    pageSize = 20,
  ): Observable<PagedResult<Wish>> {
    const params = new HttpParams().set('view', view).set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResult<Wish>>(this.base(channelName), { params });
  }

  get(channelName: string, wishId: number): Observable<WishDetail> {
    return this.http.get<WishDetail>(`${this.base(channelName)}/${wishId}`);
  }

  /** The entries "take over from the set" may pick; optionally narrowed to one 7TV emote. */
  listSlots(channelName: string, sevenTvEmoteId?: string): Observable<WishAdoptableSlot[]> {
    let params = new HttpParams();
    if (sevenTvEmoteId) {
      params = params.set('sevenTvEmoteId', sevenTvEmoteId);
    }
    return this.http.get<WishAdoptableSlot[]>(`${this.base(channelName)}/slots`, { params });
  }

  create(channelName: string, body: CreateWishBody): Observable<CreatedWish> {
    return this.http.post<CreatedWish>(this.base(channelName), body);
  }

  adopt(channelName: string, body: AdoptWishBody): Observable<Wish> {
    return this.http.post<Wish>(`${this.base(channelName)}/adopt`, body);
  }

  update(channelName: string, wishId: number, body: UpdateWishBody): Observable<Wish> {
    return this.http.patch<Wish>(`${this.base(channelName)}/${wishId}`, body);
  }

  extend(channelName: string, wishId: number): Observable<Wish> {
    return this.transition(channelName, wishId, 'extend');
  }

  keep(channelName: string, wishId: number): Observable<Wish> {
    return this.transition(channelName, wishId, 'keep');
  }

  close(channelName: string, wishId: number): Observable<Wish> {
    return this.transition(channelName, wishId, 'close');
  }

  retry(channelName: string, wishId: number): Observable<Wish> {
    return this.transition(channelName, wishId, 'retry');
  }

  /** Until K4 the server refuses every state (409). */
  recheck(channelName: string, wishId: number): Observable<Wish> {
    return this.transition(channelName, wishId, 'recheck');
  }

  /** Books the removal the client's run did; slot, generation and set are required by the contract. */
  completeRemoval(
    channelName: string,
    wishId: number,
    body: CompleteWishRemovalBody,
  ): Observable<Wish> {
    return this.http.post<Wish>(`${this.base(channelName)}/${wishId}/complete-removal`, body);
  }

  reAdd(channelName: string, wishId: number, body: ReAddWishBody): Observable<Wish> {
    return this.http.post<Wish>(`${this.base(channelName)}/${wishId}/re-add`, body);
  }

  /**
   * Releases an open wish. The answer carries `extendedExpiresAtUtc`: set when the wish was merged into the
   * running one as an extension, `null` when it was approved on its own. Reachable with capture switched off.
   */
  approve(
    channelName: string,
    wishId: number,
    body: ApproveWishBody = {},
  ): Observable<ApprovedWish> {
    return this.http.post<ApprovedWish>(`${this.base(channelName)}/${wishId}/approve`, body);
  }

  reject(channelName: string, wishId: number): Observable<Wish> {
    return this.transition(channelName, wishId, 'reject');
  }

  /** Takes an automatic link back; only an `active` wish with `source` `irc` and `linkOrigin` `auto` allows it. */
  unlink(channelName: string, wishId: number): Observable<Wish> {
    return this.transition(channelName, wishId, 'unlink');
  }

  getRewards(channelName: string): Observable<WishRewardsRead> {
    return this.http.get<WishRewardsRead>(this.rewardsBase(channelName));
  }

  startLearning(channelName: string): Observable<WishRewardLearning> {
    return this.http.post<WishRewardLearning>(`${this.rewardsBase(channelName)}/learning`, null);
  }

  confirmLearning(channelName: string, body: ConfirmWishRewardBody): Observable<WishReward> {
    return this.http.post<WishReward>(`${this.rewardsBase(channelName)}/learning/confirm`, body);
  }

  /** Ends the session the dialog holds; a session that was replaced meanwhile is not the caller's to end. */
  cancelLearning(channelName: string, sessionId: string): Observable<void> {
    const params = new HttpParams().set('sessionId', sessionId);
    return this.http.delete<void>(`${this.rewardsBase(channelName)}/learning`, { params });
  }

  updateReward(
    channelName: string,
    rewardId: number,
    body: UpdateWishRewardBody,
  ): Observable<WishReward> {
    return this.http.patch<WishReward>(`${this.rewardsBase(channelName)}/${rewardId}`, body);
  }

  deleteReward(channelName: string, rewardId: number): Observable<void> {
    return this.http.delete<void>(`${this.rewardsBase(channelName)}/${rewardId}`);
  }

  getSettings(channelName: string): Observable<WishSettingsRead> {
    return this.http.get<WishSettingsRead>(`${this.base(channelName)}/settings`);
  }

  updateSettings(channelName: string, body: WishSettingsUpdate): Observable<WishSettings> {
    return this.http.put<WishSettings>(`${this.base(channelName)}/settings`, body);
  }

  /** Cross-channel list for `my-wishes`, already sorted by the server. */
  listMine(): Observable<WishOverviewItem[]> {
    return this.http.get<WishOverviewItem[]>('/api/wishes/mine');
  }

  summary(): Observable<WishSummary> {
    return this.http.get<WishSummary>('/api/wishes/summary');
  }

  private transition(
    channelName: string,
    wishId: number,
    action: 'extend' | 'keep' | 'close' | 'retry' | 'recheck' | 'reject' | 'unlink',
  ): Observable<Wish> {
    return this.http.post<Wish>(`${this.base(channelName)}/${wishId}/${action}`, null);
  }

  private base(channelName: string): string {
    return `/api/channels/${channelName}/wishes`;
  }

  private rewardsBase(channelName: string): string {
    return `${this.base(channelName)}/rewards`;
  }
}

/**
 * The ids a 409 body names, read defensively: only a string array for `slotIds` and a number or `null`
 * for `wishId` are taken, anything else (no body, a proxy page, a foreign type) leaves the field out.
 */
export function wishConflict(error: HttpErrorResponse): WishConflict {
  const body: unknown = error.error;
  if (typeof body !== 'object' || body === null) {
    return {};
  }
  const conflict: WishConflict = {};
  if ('slotIds' in body && Array.isArray(body.slotIds) && body.slotIds.every(isString)) {
    conflict.slotIds = body.slotIds;
  }
  if ('wishId' in body && (body.wishId === null || typeof body.wishId === 'number')) {
    conflict.wishId = body.wishId;
  }
  return conflict;
}

function isString(value: unknown): value is string {
  return typeof value === 'string';
}
