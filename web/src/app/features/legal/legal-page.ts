import { Location, NgOptimizedImage } from '@angular/common';
import { Component, computed, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { catchError, of } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { LanguageService } from '../../core/i18n/language.service';
import { LegalDocumentKind } from '../../core/legal/legal.model';
import { LegalService } from '../../core/legal/legal.service';
import { NavigationHistoryService } from '../../core/routing/navigation-history.service';
import { LOGO_SRC } from '../../shared/branding/logo';
import { AccountMenu } from '../../shared/ui/account-menu';
import { BackLink } from '../../shared/ui/back-link';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { resolveLegalBackTarget } from './legal-back-target';

/**
 * Renders `/imprint` and `/privacy` (issue #247) — the same component for both, distinguished by
 * the `kind` route data set in `app.routes.ts` and bound to the `kind` input below via the app's
 * `withComponentInputBinding()` (`app.config.ts`) — the same mechanism `channelName` and friends
 * use for path params, applied here to static `data` instead. Deliberately outside the app shell
 * and every auth guard: requirement 3 is reachability without being logged in, before the Twitch
 * OAuth redirect even happens, so this brings its own minimal frame the way `LoginPage` does.
 *
 * No page-chrome `<h1>` of its own — the rendered Markdown's own top heading (the operator's
 * document title) is that heading; adding a second one above it would print the same title twice.
 * `.app-legal-content` (styles.css) applies the typographic-hierarchy table (design doc §3) onto
 * the plain h1/h2/h3/p/ul/ol/a tags Markdig emits.
 *
 * The one navigation control is state-dependent (`legal-back-target.ts`, `resolveLegalBackTarget`):
 * a visitor who opened this page from elsewhere in the app (the footer on a channel page, say) gets
 * a literal "Back" that returns them there; a visitor for whom this page was the session's own
 * entry point (fresh tab, reload, external link) gets a fixed destination instead — their own
 * overview if logged in, the landing page otherwise. This is the one place in the app that uses
 * `Location.back()` rather than `app-back-link`'s fixed hierarchical target: every other consumer of
 * that primitive is a permanent parent in the route tree (see its own doc comment for why it
 * otherwise never does this), but THIS page has no such parent — it sits outside the whole app-shell
 * route tree and is reachable from everywhere in it, so "back" is the only accurate destination
 * when one exists. `Location.back()` over re-navigating to a recorded URL: it is real browser-back
 * (no new, forward-breaking history entry; scroll position restores through this app's own
 * `withInMemoryScrolling` config), and it is safe from leaving the app entirely — the failure mode
 * a plain "go back" control risks — only because it is gated on `NavigationHistoryService`, which
 * tracks Angular's OWN completed navigations rather than raw `history.length`; the button never
 * renders unless this app itself pushed the entry that `back()` would return to.
 */
@Component({
  selector: 'app-legal-page',
  imports: [
    AccountMenu,
    BackLink,
    EmptyState,
    NgOptimizedImage,
    NoticeBanner,
    RouterLink,
    TranslocoPipe,
  ],
  template: `
    <div class="flex min-h-screen flex-col bg-page text-fg">
      <header class="flex items-center justify-between px-4 py-3">
        <a routerLink="/welcome" class="flex items-center gap-2 text-lg font-semibold">
          <img
            [ngSrc]="logoSrc"
            width="24"
            height="24"
            disableOptimizedSrcset
            alt=""
            class="h-6 w-6"
          />
          Emote Purge
        </a>
        <app-account-menu />
      </header>

      <main class="mx-auto w-full max-w-7xl flex-1 px-4 py-8">
        @if (backTarget(); as target) {
          @if (target.kind === 'back') {
            <button
              type="button"
              (click)="goBack()"
              class="inline-flex items-center gap-1 rounded-md border border-accent-selected px-3 py-1.5 text-sm whitespace-nowrap text-accent-fg transition hover:bg-accent-wash"
            >
              <span aria-hidden="true">←</span>{{ 'legal.backAction' | transloco }}
            </button>
          } @else {
            <app-back-link [link]="target.link" [label]="target.labelKey | transloco" />
          }
        }

        @if (documentResource.isLoading()) {
          <div
            role="status"
            [attr.aria-label]="'common.loading' | transloco"
            class="mt-6 flex max-w-3xl flex-col gap-3"
          >
            <div class="app-skeleton h-8 w-2/3"></div>
            <div class="app-skeleton h-4 w-full"></div>
            <div class="app-skeleton h-4 w-full"></div>
            <div class="app-skeleton h-4 w-5/6"></div>
          </div>
        } @else if (document(); as doc) {
          @if (doc.isGermanFallback) {
            <app-notice-banner variant="info" class="mt-6 block max-w-3xl">
              {{ 'legal.onlyGerman' | transloco }}
            </app-notice-banner>
          }
          <div class="app-legal-content max-w-3xl" [innerHTML]="doc.html"></div>
        } @else {
          <app-empty-state
            class="mt-6 block"
            [title]="notConfiguredTitleKey() | transloco"
            [description]="'legal.notConfigured.description' | transloco"
          />
        }
      </main>
    </div>
  `,
})
export class LegalPage {
  readonly kind = input.required<LegalDocumentKind>();

  private readonly authService = inject(AuthService);
  private readonly languageService = inject(LanguageService);
  private readonly legalService = inject(LegalService);
  private readonly location = inject(Location);
  private readonly navigationHistoryService = inject(NavigationHistoryService);

  protected readonly logoSrc = LOGO_SRC;

  protected readonly notConfiguredTitleKey = computed(() =>
    this.kind() === 'imprint'
      ? 'legal.imprint.notConfiguredTitle'
      : 'legal.privacy.notConfiguredTitle',
  );

  // Not gated on authService.isResolved(): unlike AccountMenu's avatar trigger (which reserves a
  // silent placeholder until resolution, see its own doc comment), a wrong initial guess here is
  // cheap to correct — worst case this label reads "Zur Startseite" for the one microtask before
  // /api/auth/me answers and then relabels itself once AccountMenu's own request (issued
  // unconditionally in the header above) resolves currentUser().
  protected readonly backTarget = computed(() =>
    resolveLegalBackTarget(
      this.navigationHistoryService.hasPreviousPage(),
      this.authService.currentUser() !== null,
    ),
  );

  protected readonly documentResource = rxResource({
    params: () => ({ kind: this.kind(), language: this.languageService.lang() }),
    stream: ({ params }) =>
      this.legalService.getDocument(params.kind, params.language).pipe(catchError(() => of(null))),
  });

  // value() throws once a resource is in its error state (project convention — see
  // admin-monitoring-page.ts); the stream above already converts a failed request to a `null`
  // value instead of letting the resource error, specifically so this stays a plain hasValue()
  // check. A `null` value (not configured, or the request failed) and "still loading" both read as
  // "nothing to show yet" here — the template's isLoading() branch is checked first and wins while
  // a request is in flight.
  protected readonly document = computed(() =>
    this.documentResource.hasValue() ? this.documentResource.value() : null,
  );

  // Only rendered when backTarget() is 'back', i.e. only when NavigationHistoryService has already
  // observed a previous completed in-app navigation — see the class doc comment for why that makes
  // this safe from leaving the app.
  protected goBack(): void {
    this.location.back();
  }
}
