import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AddTagEntriesResult,
  EmoteTag,
  EmoteTagEntries,
  EmoteTagList,
  RegisterTagOperationBody,
  RemoveTagEntriesResult,
  TagOperationRegistration,
  TagPlacementsBody,
  TagPlacementsResult,
  TagRemovalBody,
  TagRemovalResult,
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
    emoteSetId?: string,
  ): Observable<AddTagEntriesResult> {
    return this.http.post<AddTagEntriesResult>(`${this.base(channelName)}/${tagId}/entries`, {
      sevenTvEmoteIds,
      ...(emoteSetId ? { emoteSetId } : {}),
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

  /** Registers a play-in or removal operation before the run starts; its server time anchors the report. */
  registerOperation(
    channelName: string,
    tagId: number,
    body: RegisterTagOperationBody,
  ): Observable<TagOperationRegistration> {
    return this.http.post<TagOperationRegistration>(
      `${this.base(channelName)}/${tagId}/operations`,
      body,
    );
  }

  /** Reports a finished (or empty) play-in. The set travels in the body, never as a query. */
  reportPlacements(
    channelName: string,
    tagId: number,
    body: TagPlacementsBody,
  ): Observable<TagPlacementsResult> {
    return this.http.post<TagPlacementsResult>(
      `${this.base(channelName)}/${tagId}/placements`,
      body,
    );
  }

  /** Reports a finished (or empty) removal. */
  reportRemoval(
    channelName: string,
    tagId: number,
    body: TagRemovalBody,
  ): Observable<TagRemovalResult> {
    return this.http.post<TagRemovalResult>(
      `${this.base(channelName)}/${tagId}/placements/removed`,
      body,
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
