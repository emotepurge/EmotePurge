import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { normalizeChannelName } from '../channels/channel-name';
import { ImportCoverage, ImportCoverageScope } from './import-coverage.model';

/** The disclosure read behind the usage page's caption (spec 2026-10-09, §5.6). */
@Injectable({ providedIn: 'root' })
export class ImportCoverageService {
  private readonly http = inject(HttpClient);

  getCoverage(channelName: string, scope: ImportCoverageScope): Observable<ImportCoverage> {
    return this.http.get<ImportCoverage>(
      `/api/channels/${normalizeChannelName(channelName)}/usage-stats/import-coverage`,
      { params: scopeParams(scope) },
    );
  }
}

/**
 * This route spells "every set" as `emoteSetId=all`, while `/daily` spells it `setScope=all`
 * (`UsageStatService`) — two contracts for one idea, kept as they are (operator decision
 * 2026-10-09). The mapping lives here so no caller writes either string by hand.
 * "Active" omits the parameter and lets the server resolve the channel's active set.
 */
function scopeParams(scope: ImportCoverageScope): Record<string, string> {
  switch (scope.kind) {
    case 'active':
      return {};
    case 'all':
      return { emoteSetId: 'all' };
    case 'set':
      return { emoteSetId: scope.emoteSetId };
  }
}
