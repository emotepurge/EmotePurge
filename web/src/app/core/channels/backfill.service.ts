import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { BackfillRun, BackfillStatus, StartBackfillRequest } from './backfill.model';
import { normalizeChannelName } from './channel-name';

/**
 * Chat-log backfill routes (spec 2026-10-09, §5). Thin on purpose: the server owns every rule, and
 * errors are mapped by callers through `apiErrorTranslationKey`.
 */
@Injectable({ providedIn: 'root' })
export class BackfillService {
  private readonly http = inject(HttpClient);

  getStatus(channelName: string): Observable<BackfillStatus> {
    return this.http.get<BackfillStatus>(this.url(channelName));
  }

  /** `months` goes out as a JSON number — the server rejects anything but 1, 3 and 6. */
  start(channelName: string, emoteSetId: string, months: number): Observable<BackfillRun> {
    const body: StartBackfillRequest = { emoteSetId, months };
    return this.http.post<BackfillRun>(this.url(channelName), body);
  }

  cancel(channelName: string): Observable<void> {
    return this.http.delete<void>(this.url(channelName));
  }

  private url(channelName: string): string {
    return `/api/channels/${normalizeChannelName(channelName)}/backfill`;
  }
}
