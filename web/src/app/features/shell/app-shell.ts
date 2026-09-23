import { NgOptimizedImage } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { WorkerHealthService } from '../../core/health/worker-health.service';
import { LegalService } from '../../core/legal/legal.service';
import { LiveQuotaService } from '../../core/live/live-quota.service';
import { LOGO_SRC } from '../../shared/branding/logo';
import { AccountMenu } from '../../shared/ui/account-menu';
import { Button } from '../../shared/ui/button';
import { HealthMarker } from '../../shared/ui/health-marker';
import { LegalFooterLinks } from '../../shared/ui/legal-footer-links';
import { Popover } from '../../shared/ui/popover';

@Component({
  selector: 'app-shell',
  imports: [
    AccountMenu,
    Button,
    HealthMarker,
    LegalFooterLinks,
    NgOptimizedImage,
    Popover,
    RouterLink,
    RouterOutlet,
    TranslocoPipe,
  ],
  template: `
    <div class="isolate min-h-screen bg-page text-fg">
      <!-- h-14 is a contract, not styling: the sticky tab bars pin at top-14 and the sticky
           filter toolbars at top-24, both assuming exactly this header height (design doc §8.5).
           z-30 keeps the header (and its mobile disclosure) above the z-20 sticky bars — it is a
           utility here so it beats .app-sticky-bar's own z-20 (utilities layer after components).
           Reusing that class rather than repeating the blur: the translucency has to be denser in
           light than in dark, and --ep-sticky-alpha is where that lives. -->
      <header class="app-sticky-bar top-0 z-30 h-14 border-b border-border px-4">
        <div class="mx-auto flex h-full max-w-7xl items-center justify-between gap-3">
          <!-- max-w-7xl here and on <main> is ONE width for the whole app, deliberately. Widened
               from 64rem to 80rem on 2026-09-07 (issue #93) — still exactly one width; a
               route-dependent variant stays excluded. The same class sits on the dock's inner
               container and on the six landing-page sections — nine places that move together or
               not at all; §8.4a lists them, because the dock is the one that gets forgotten and a
               narrower action bar under a wider sheet is what that costs. A per-route second width
               for the two sprite
               sheets was built and taken out again the same day: the extra pixels really are emote
               columns there, but they cost a frame that jumps on every navigation between a sheet
               page and a list page, and that is the worse deal. If the sheets get their width back,
               it has to be without moving the frame.

               Logo and the worker warning form one anchored group — a lone justify-between middle
               child would float detached between logo and menu button on narrow viewports. -->
          <!-- relative + the popover anchor sit on the whole left group, not on the quota badge
               inside it. Anchoring to the badge made the panel's position follow the badge's, which
               moves with the locale: "Votes" is shorter than "Abstimmungen", so in en the wordmark
               keeps more width, the badge starts further right and the panel ran 27px past the
               right edge at 360px (measured). Anchored here, left-0 is the header's own left
               padding in every locale, and the panel's max-w keeps it inside the viewport from
               there. -->
          <div class="relative flex min-w-0 items-center gap-3" data-popover-anchor>
            <!-- min-w-0 + truncate on the wordmark rather than nowrap: this group is the shrinking
                 side of the row, and a child that refuses to shrink runs out of its own box and
                 lands on top of whatever sits on the right. The logo keeps its size; only the
                 words give way, and only when there is genuinely no room. -->
            <a routerLink="/" class="flex min-w-0 items-center gap-2 text-lg font-semibold">
              <img
                [ngSrc]="logoSrc"
                width="24"
                height="24"
                disableOptimizedSrcset
                alt=""
                class="h-6 w-6 shrink-0"
              />
              <span class="truncate">Emote Purge</span>
            </a>
            <!-- Nothing at all while the pipeline is healthy. This spot carried a dot plus "Worker
                 verbunden" on every page of the app until 2026-08-06, which is the one thing the
                 design language forbids twice over (both in §4.3): a marker present on every screen
                 in every session marks nothing, and a healthy subsystem stays quiet so the first
                 unhealthy one is the loudest thing on screen without shouting. The header holds the
                 stricter version of that rule — HealthMarker's own quiet state is a dot plus a
                 word, and even that is more than an app frame should spend on "as expected".
                 'unknown' stays silent too: it is the state before the first poll answers, so
                 speaking there would flash a warning on every cold load. -->
            @if (workerStale()) {
              <app-health-marker tone="warning" [label]="'shell.workerStatus.stale' | transloco" />
            }
            <!-- Same rule as the marker above, for the other thing that can quietly stop working:
                 until now a page whose live stream was refused simply went still, and still is
                 indistinguishable from "nothing is happening" — which is the whole complaint in
                 issue #42. It belongs in the frame and not on a page (§4.4): every page updates
                 itself from that stream, so the fact is app-wide, and app-wide facts are the
                 header's alone.
                 Only ever shown for a full *per-login* budget, never for a transient close: a
                 restarted Api or a dropped proxy connection puts every stream through 'closed' on
                 the way back up, and a warning that flashes on every deploy is one people learn to
                 ignore (§4.3). LiveQuotaService confirms the cause with the server before this can
                 appear at all.

                 The label is short for a reason the header enforces rather than prefers, and the
                 audit harness measured it: at 360px one badge already truncates the wordmark to
                 3px (shell-both-warnings). The cause ("zu viele offene Tabs") therefore lives one
                 click down, in the popover.

                 The announcement is deliberately NOT inside the @if below: this state appears
                 *after* load, and a live region that only comes into existence with its content
                 announces nothing. Permanent and sr-only, so it can exist without adding an empty
                 flex child (and its gap) to the row. It also keeps speaking when the badge yields
                 its space below — a screen reader has no 360px to run out of, so there is no reason
                 to make it share the header's scarcity. -->
            <span class="sr-only" aria-live="polite">
              @if (liveQuotaExhausted()) {
                {{ 'shell.liveStatus.paused' | transloco }}
              }
            </span>
            <!-- One badge at a time, and this is arithmetic rather than taste: the measurement above
                 shows a single badge already consuming the whole wordmark, so a second one pushes
                 "Abstimmungen" out of the row and gets clipped by the account trigger. The worker
                 warning wins because it outranks this one — "Chat wird nicht gezählt" is data being
                 lost for good, while paused live updates are a stale view that a reload fixes. -->
            @if (liveQuota(); as quota) {
              @if (quota.perSubscriberLimitReached && !workerStale()) {
                <div>
                  <!-- The badge is the trigger, so it also supplies the button's accessible name —
                       hence no aria-label here and no aria-hidden on the marker. -->
                  <!-- min-h-6 is the WCAG 2.5.8 floor (§10): the badge itself is 20px, so the
                       button has to add the rest — without it the only way to open this panel is a
                       target smaller than the standard allows. -->
                  <button
                    type="button"
                    class="inline-flex min-h-6 items-center rounded-md px-0.5 transition hover:bg-surface-inset"
                    aria-haspopup="dialog"
                    [attr.aria-expanded]="liveHintOpen()"
                    (click)="liveHintOpen.set(!liveHintOpen())"
                  >
                    <app-health-marker
                      tone="warning"
                      [label]="'shell.liveStatus.paused' | transloco"
                    />
                  </button>
                  @if (liveHintOpen()) {
                    <!-- align="start", against the instinct that copies the account menu: that
                         panel is right-aligned because its trigger sits at the right edge. This
                         trigger sits in the *left* group, so right-aligning put the panel's left
                         edge at roughly -116px and clipped the first third of every line. Neither
                         horizontalOverflowPx nor beyondRightEdge sees that — overflow to the left
                         does not lengthen scrollWidth — which is why this was caught in the audit
                         screenshot and not in its metrics. Left-aligned, the panel runs from the
                         trigger rightwards and fits inside 360px. -->
                    <app-popover
                      align="start"
                      width="w-72"
                      [ariaLabel]="'shell.liveStatus.paused' | transloco"
                      (closed)="liveHintOpen.set(false)"
                    >
                      <p class="px-4 py-3 text-sm text-fg-muted">
                        {{
                          'shell.liveStatus.explanation'
                            | transloco
                              : {
                                  open: quota.openConnections,
                                  max: quota.maxPerSubscriber,
                                }
                        }}
                      </p>
                    </app-popover>
                  }
                </div>
              }
            }
          </div>

          <div class="flex items-center gap-3">
            <!-- Gated on authResolved so the button does not flash and get replaced: the header
                 must not visibly change its mind about who you are. -->
            @if (authResolved() && currentUser()) {
              <!-- Deliberately the short label: at 360px the wordmark, this link and the 44px
                   trigger share one row, and "Meine Abstimmungen" does not fit next to them. The
                   page it leads to keeps the long form as its heading. -->
              <a
                routerLink="/my-votings"
                class="px-1 py-2 text-sm whitespace-nowrap text-fg-muted transition hover:text-fg"
                >{{ 'shell.votings' | transloco }}</a
              >
            } @else if (authResolved()) {
              <a routerLink="/login" appButton="primary">
                {{ 'shell.login' | transloco }}
              </a>
            }
            <app-account-menu />
          </div>
        </div>
      </header>

      <main class="mx-auto max-w-7xl px-4 py-8">
        <router-outlet />
      </main>

      <!-- Present on every route this shell serves, never per-route (§8.4a's "the frame must not
           jump per route" applies here too) — hidden outright rather than shown empty when the
           operator has configured neither document (issue #247, requirement 4). -->
      @if (hasLegalLinks()) {
        <footer class="border-t border-border px-4 py-4">
          <div class="mx-auto flex max-w-7xl gap-5 text-sm text-fg-muted">
            <app-legal-footer-links />
          </div>
        </footer>
      }
    </div>
  `,
})
export class AppShell {
  private readonly authService = inject(AuthService);
  private readonly healthService = inject(WorkerHealthService);
  private readonly legalService = inject(LegalService);
  private readonly liveQuotaService = inject(LiveQuotaService);
  private readonly router = inject(Router);

  protected readonly currentUser = this.authService.currentUser;
  protected readonly authResolved = this.authService.isResolved;
  protected readonly workerStale = computed(() => this.healthService.status() === 'stale');
  protected readonly hasLegalLinks = this.legalService.hasAnyDocument;
  protected readonly liveQuotaExhausted = this.liveQuotaService.perSubscriberLimitReached;
  protected readonly liveQuota = this.liveQuotaService.quota;
  protected readonly liveHintOpen = signal(false);
  protected readonly logoSrc = LOGO_SRC;

  constructor() {
    // AppShell is mounted for every route (overview, usage-stats, vote-sessions), unlike
    // ensureLoaded()'s other callers (authGuard only guards the overview route). Without this,
    // a logged-in user landing directly on a usage-stats or vote-session deep link never gets
    // currentUser populated, and the header wrongly shows "Login" despite a valid session cookie.
    // ensureLoaded() is idempotent (cached via an internal isLoaded flag), so this never causes a
    // duplicate /api/auth/me call when authGuard also runs.
    this.authService.ensureLoaded().subscribe();

    // A panel left open when the stream recovers would keep liveHintOpen true, and the next
    // exhaustion in the same session would remount the block already expanded — a dialog opening
    // with no user action behind it.
    effect(() => {
      if (!this.liveQuotaExhausted()) {
        this.liveHintOpen.set(false);
      }
    });

    // Consumes a return URL stashed by AuthService.login()/stashReturnUrl() (e.g. a route guard
    // redirecting a logged-out visitor) once the session is confirmed, sending them back to the
    // page they originally tried to reach instead of the fixed post-login redirect the backend
    // always uses.
    effect(() => {
      if (this.currentUser()) {
        const returnUrl = this.authService.consumeReturnUrl();
        if (returnUrl) {
          this.router.navigateByUrl(returnUrl);
        }
      }
    });
  }
}
