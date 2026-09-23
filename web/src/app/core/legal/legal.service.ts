import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { filter, Observable } from 'rxjs';

import {
  LegalAvailabilityResponse,
  LegalDocumentKind,
  LegalDocumentResponse,
  LegalLanguage,
} from './legal.model';

/**
 * Backs the footer links (issue #247, requirement 3/4) and the `/imprint`/`/privacy` pages.
 * Availability is fetched on first injection — it is app-wide, operator-set configuration, not
 * per-page state, and the footer sits on every page (landing, login, the app shell), so a
 * `WorkerHealthService`-style single fetch is enough; no *polling*, since the configuration only
 * ever changes on a restart the operator caused.
 *
 * A failed fetch is retried once per completed navigation (see the constructor) rather than left
 * to stand for the rest of the session — Codex Sol review of #247 (P2): the original one-shot
 * fetch folded a transient failure (a dropped connection, a rate-limit rejection before the
 * endpoints got their own `PublicLegal` budget) into the same "nothing configured" state as a
 * genuinely empty deployment, with no way back short of a full page reload. Retrying on navigation
 * rather than on a timer paces itself by the visitor's own activity — no retry ever fires without
 * the visitor already having done something, so this adds no load of its own kind, only recovers
 * the same one-shot fetch a little later.
 */
@Injectable({ providedIn: 'root' })
export class LegalService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  /** Both start `false` and stay that way until the first response lands (or fails) — the footer
   *  must show no link before it knows one exists, rather than flashing a link that a failed
   *  request then takes away. */
  readonly imprintAvailable = signal(false);
  readonly privacyAvailable = signal(false);

  /** Whether the footer has anything to show at all — the app shell and the login page use this to
   *  decide whether to render their footer element in the first place, so an operator with neither
   *  document configured gets no empty bordered strip. */
  readonly hasAnyDocument = computed(() => this.imprintAvailable() || this.privacyAvailable());

  /** Set on a failed fetch, cleared on a successful one — distinct from `imprintAvailable`/
   *  `privacyAvailable`, which stay `false` in both the "not configured" and the "request failed"
   *  case (the footer cannot tell those apart and should not try to). This flag is what the retry
   *  below actually checks: once a fetch has genuinely succeeded, further navigations must not
   *  keep re-requesting configuration that will not change until a restart. */
  private fetchFailed = false;

  constructor() {
    this.fetchAvailability();

    this.router.events
      .pipe(filter((event): event is NavigationEnd => event instanceof NavigationEnd))
      .subscribe(() => {
        if (this.fetchFailed) {
          this.fetchAvailability();
        }
      });
  }

  getDocument(kind: LegalDocumentKind, language: LegalLanguage): Observable<LegalDocumentResponse> {
    return this.http.get<LegalDocumentResponse>(`/api/legal/${kind}/${language}`);
  }

  private fetchAvailability(): void {
    this.http.get<LegalAvailabilityResponse>('/api/legal/availability').subscribe({
      next: (response) => {
        this.fetchFailed = false;
        this.imprintAvailable.set(response.imprintAvailable);
        this.privacyAvailable.set(response.privacyAvailable);
      },
      error: () => {
        this.fetchFailed = true;
        this.imprintAvailable.set(false);
        this.privacyAvailable.set(false);
      },
    });
  }
}
