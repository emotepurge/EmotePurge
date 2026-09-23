import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { catchError, Observable, of } from 'rxjs';

import {
  LegalAvailabilityResponse,
  LegalDocumentKind,
  LegalDocumentResponse,
  LegalLanguage,
} from './legal.model';

/**
 * Backs the footer links (issue #247, requirement 3/4) and the `/imprint`/`/privacy` pages.
 * Availability is fetched exactly once, on first injection — it is app-wide, operator-set
 * configuration, not per-page state, and the footer sits on every page (landing, login, the app
 * shell), so a `WorkerHealthService`-style single fetch is enough; no polling, since it only ever
 * changes on a restart the operator caused.
 */
@Injectable({ providedIn: 'root' })
export class LegalService {
  private readonly http = inject(HttpClient);

  /** Both start `false` and stay that way until the first response lands (or fails) — the footer
   *  must show no link before it knows one exists, rather than flashing a link that a failed
   *  request then takes away. */
  readonly imprintAvailable = signal(false);
  readonly privacyAvailable = signal(false);

  /** Whether the footer has anything to show at all — the app shell and the login page use this to
   *  decide whether to render their footer element in the first place, so an operator with neither
   *  document configured gets no empty bordered strip. */
  readonly hasAnyDocument = computed(() => this.imprintAvailable() || this.privacyAvailable());

  constructor() {
    this.http
      .get<LegalAvailabilityResponse>('/api/legal/availability')
      .pipe(
        catchError(() =>
          of<LegalAvailabilityResponse>({ imprintAvailable: false, privacyAvailable: false }),
        ),
      )
      .subscribe((response) => {
        this.imprintAvailable.set(response.imprintAvailable);
        this.privacyAvailable.set(response.privacyAvailable);
      });
  }

  getDocument(kind: LegalDocumentKind, language: LegalLanguage): Observable<LegalDocumentResponse> {
    return this.http.get<LegalDocumentResponse>(`/api/legal/${kind}/${language}`);
  }
}
