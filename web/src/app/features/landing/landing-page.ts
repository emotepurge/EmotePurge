import { NgOptimizedImage } from '@angular/common';
import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { SOURCE_URL } from '../../shared/branding/links';
import { LOGO_SRC } from '../../shared/branding/logo';
import { AccountMenu } from '../../shared/ui/account-menu';
import { Button } from '../../shared/ui/button';
import { LegalFooterLinks } from '../../shared/ui/legal-footer-links';
import { SetShape } from './set-shape';

/**
 * The three things the tool does, and they are not three equal things — which is exactly what the
 * four identical feature cards used to claim. Measuring runs on its own for weeks and is the
 * foundation; voting is optional and says so in the product's own copy, twice; deleting happens in
 * someone else's system and cannot be undone (PRODUCT.md, principle 4). The `span` and the rule's
 * colour carry that ranking, so the layout says what the words say.
 */
interface Stage {
  key: string;
  /** Column span at lg — measuring takes half the row. */
  span: string;
  /** The 1px rule above the stage. Neutral except for the irreversible one. */
  rule: string;
  /** Colour of the one-word label under the rule. */
  label: string;
}

const STAGES: Stage[] = [
  { key: 'measure', span: 'sm:col-span-2', rule: 'bg-accent-fg', label: 'text-accent-fg' },
  { key: 'vote', span: 'sm:col-span-1', rule: 'bg-border-strong', label: 'text-fg-muted' },
  { key: 'purge', span: 'sm:col-span-1', rule: 'bg-danger-border', label: 'text-danger-fg' },
];

/**
 * The genuine sequence: nothing can be measured before the channel is joined, and nothing can be
 * purged before it was measured. Numbering earns its place here because the order carries
 * information — unlike the decorative 01/02/03 that the same layout usually gets.
 *
 * Two of the four carry a note. Both are frictions the product actually has (PRODUCT.md, "Operating
 * Context"): the tool is legitimately empty for the first days, and the 7TV write token has to be
 * copied out of the browser's devtools because 7TV offers no login redirect. Naming them on the
 * public page is the trust the page owes; hiding them would only move the surprise later.
 */
const STEPS = ['login', 'join', 'measure', 'purge'] as const;
const STEPS_WITH_NOTE = new Set<string>(['join', 'purge']);

/** The three verifiable facts a visitor needs before handing a tool access to anything. */
const TRUST = ['twitch', 'token', 'source'] as const;

@Component({
  selector: 'app-landing-page',
  imports: [
    AccountMenu,
    Button,
    LegalFooterLinks,
    NgOptimizedImage,
    RouterLink,
    TranslocoPipe,
    SetShape,
  ],
  templateUrl: './landing-page.html',
})
export class LandingPage {
  private readonly authService = inject(AuthService);

  protected readonly stages = STAGES;
  protected readonly steps = STEPS;
  protected readonly trust = TRUST;
  protected readonly logoSrc = LOGO_SRC;
  protected readonly sourceUrl = SOURCE_URL;

  protected hasNote(step: string): boolean {
    return STEPS_WITH_NOTE.has(step);
  }

  protected login(): void {
    this.authService.login();
  }
}
