import { NgOptimizedImage } from '@angular/common';
import { Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { catchError, of } from 'rxjs';

import { LanguageService } from '../../core/i18n/language.service';
import { LegalDocumentKind } from '../../core/legal/legal.model';
import { LegalService } from '../../core/legal/legal.service';
import { LOGO_SRC } from '../../shared/branding/logo';
import { AccountMenu } from '../../shared/ui/account-menu';
import { BackLink } from '../../shared/ui/back-link';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';

/**
 * Renders `/imprint` and `/privacy` (issue #247) — the same component for both, distinguished by
 * the `kind` route data set in `app.routes.ts`. Deliberately outside the app shell and every auth
 * guard: requirement 3 is reachability without being logged in, before the Twitch OAuth redirect
 * even happens, so this brings its own minimal frame the way `LoginPage` does.
 *
 * No page-chrome `<h1>` of its own — the rendered Markdown's own top heading (the operator's
 * document title) is that heading; adding a second one above it would print the same title twice.
 * `.app-legal-content` (styles.css) applies the typographic-hierarchy table (design doc §3) onto
 * the plain h1/h2/h3/p/ul/ol/a tags Markdig emits.
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
        <app-back-link link="/welcome" [label]="'legal.back' | transloco" />

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
            [title]="notConfiguredTitleKey | transloco"
            [description]="'legal.notConfigured.description' | transloco"
          />
        }
      </main>
    </div>
  `,
})
export class LegalPage {
  private readonly route = inject(ActivatedRoute);
  private readonly languageService = inject(LanguageService);
  private readonly legalService = inject(LegalService);

  protected readonly logoSrc = LOGO_SRC;

  // Route data, not a component input: the two routes ('/imprint', '/privacy') are distinct path
  // segments, so a fresh instance of this component is created per navigation between them and the
  // snapshot value never needs to change under an existing instance.
  private readonly kind: LegalDocumentKind =
    (this.route.snapshot.data['kind'] as LegalDocumentKind | undefined) ?? 'imprint';

  protected readonly notConfiguredTitleKey =
    this.kind === 'imprint'
      ? 'legal.imprint.notConfiguredTitle'
      : 'legal.privacy.notConfiguredTitle';

  protected readonly documentResource = rxResource({
    params: () => this.languageService.lang(),
    stream: ({ params }) =>
      this.legalService.getDocument(this.kind, params).pipe(catchError(() => of(null))),
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
}
