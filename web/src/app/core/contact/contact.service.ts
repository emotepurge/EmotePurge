import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { ContactConfigResponse, ContactSubmitRequest } from './contact.model';

/**
 * Backs `ContactPage` (docs/DECISIONS.md 2026-09-24, "contact form"). Unlike `LegalService`, this
 * holds no state of its own — `/contact` is the one and only consumer of both endpoints, so there is
 * nothing to share across call sites the way the footer and the legal pages share
 * `LegalService.imprintAvailable`/`privacyAvailable`.
 */
@Injectable({ providedIn: 'root' })
export class ContactService {
  private readonly http = inject(HttpClient);

  getConfig(): Observable<ContactConfigResponse> {
    return this.http.get<ContactConfigResponse>('/api/contact/config');
  }

  /** Resolves once the API answers 204; an error propagates for the caller to map via `apiErrorTranslationKey`. */
  submit(request: ContactSubmitRequest): Observable<void> {
    return this.http.post<void>('/api/contact', request);
  }
}
