import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AddTagEntriesResult,
  EmoteTag,
  EmoteTagEntries,
  EmoteTagList,
  RemoveTagEntriesResult,
} from './emote-tag.model';

@Injectable({ providedIn: 'root' })
export class EmoteTagService {
  private readonly http = inject(HttpClient);

  /** Without `emoteSetId` the server resolves the channel's active set. */
  list(channelName: string, emoteSetId?: string): Observable<EmoteTagList> {
    return this.http.get<EmoteTagList>(this.base(channelName), {
      params: this.setParams(emoteSetId),
    });
  }

  listEntries(
    channelName: string,
    tagId: number,
    emoteSetId?: string,
  ): Observable<EmoteTagEntries> {
    return this.http.get<EmoteTagEntries>(`${this.base(channelName)}/${tagId}/entries`, {
      params: this.setParams(emoteSetId),
    });
  }

  create(channelName: string, name: string): Observable<EmoteTag> {
    return this.http.post<EmoteTag>(this.base(channelName), { name });
  }

  rename(channelName: string, tagId: number, name: string): Observable<EmoteTag> {
    return this.http.patch<EmoteTag>(`${this.base(channelName)}/${tagId}`, { name });
  }

  delete(channelName: string, tagId: number): Observable<void> {
    return this.http.delete<void>(`${this.base(channelName)}/${tagId}`);
  }

  addEntries(
    channelName: string,
    tagId: number,
    sevenTvEmoteIds: readonly string[],
  ): Observable<AddTagEntriesResult> {
    return this.http.post<AddTagEntriesResult>(`${this.base(channelName)}/${tagId}/entries`, {
      sevenTvEmoteIds,
    });
  }

  /** A POST with a body, not a DELETE: the id list can be long (server cap 2000). */
  removeEntries(
    channelName: string,
    tagId: number,
    sevenTvEmoteIds: readonly string[],
  ): Observable<RemoveTagEntriesResult> {
    return this.http.post<RemoveTagEntriesResult>(
      `${this.base(channelName)}/${tagId}/entries/remove`,
      { sevenTvEmoteIds },
    );
  }

  private base(channelName: string): string {
    return `/api/channels/${encodeURIComponent(channelName)}/tags`;
  }

  private setParams(emoteSetId: string | undefined): HttpParams {
    return emoteSetId === undefined
      ? new HttpParams()
      : new HttpParams().set('emoteSetId', emoteSetId);
  }
}
