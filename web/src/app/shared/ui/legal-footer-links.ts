import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { LegalService } from '../../core/legal/legal.service';

/**
 * The two footer links onto `/imprint`/`/privacy` (issue #247, requirement 3/4) — each renders only
 * once its document is actually configured, so an operator who has filled in neither shows no legal
 * footer at all rather than dead links. A bare `<a>` pair rather than a `<footer>` of its own: the
 * three call sites (landing, login, app shell) each own a differently-shaped footer already or need
 * one added at the shell level, and this component only ever contributes the two links into
 * whichever row the caller provides.
 */
@Component({
  selector: 'app-legal-footer-links',
  imports: [RouterLink, TranslocoPipe],
  template: `
    @if (imprintAvailable()) {
      <a routerLink="/imprint" class="inline-block px-1 py-2 transition hover:text-fg-secondary">{{
        'legal.footer.imprint' | transloco
      }}</a>
    }
    @if (privacyAvailable()) {
      <a routerLink="/privacy" class="inline-block px-1 py-2 transition hover:text-fg-secondary">{{
        'legal.footer.privacy' | transloco
      }}</a>
    }
  `,
})
export class LegalFooterLinks {
  private readonly legalService = inject(LegalService);

  protected readonly imprintAvailable = this.legalService.imprintAvailable;
  protected readonly privacyAvailable = this.legalService.privacyAvailable;
}
