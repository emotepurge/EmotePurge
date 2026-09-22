import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { Router, RouterOutlet } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { ChannelService } from '../../core/channels/channel.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { channelLiveUrl, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveEvents } from '../../core/live/live-reload';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { BackLink } from '../../shared/ui/back-link';
import { Button } from '../../shared/ui/button';
import { ConfirmDialogData, openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { TabLink } from '../../shared/ui/tab-link';

/** Long enough to read, short enough that a stale "queued" never lingers on screen. */
const RESYNC_FEEDBACK_MS = 4000;

@Component({
  selector: 'app-channel-workspace-layout',
  imports: [BackLink, Button, NoticeBanner, RouterOutlet, TabLink, TranslocoPipe],
  template: `
    <div>
      <div class="mb-4 flex flex-wrap items-center gap-x-4 gap-y-2">
        <app-back-link link="/" [label]="'nav.overview' | transloco" />
        <!-- On narrow viewports the title takes its own full-width line below the buttons instead
             of being squeezed to an ellipsis between them; from md: it sits inline as before. -->
        <h1
          class="order-last w-full truncate text-2xl font-bold tracking-tight md:order-0 md:w-auto md:min-w-0 md:flex-1"
        >
          #{{ channelName() }}
        </h1>
        <!-- One wrapper carries the ml-auto, not each button: with it on two siblings they would be
             pushed to opposite ends and collide with the title's order-last/md:flex-1 contract. -->
        <div class="ml-auto flex flex-wrap items-center gap-2">
          <!-- A role="status" region that enters the DOM together with its content announces
               nothing to most screen reader/browser pairings — only a mutation *inside* an
               already-mounted region is announced. So the sr-only region below is permanent and
               only its text comes and goes; the visible twin stays an @if (it must not occupy
               layout space when there is nothing to say) and is aria-hidden so the message is not
               spoken twice — once from the live region, once from the visible text a screen
               reader would otherwise also read. Same split as usage-stats-page.html's
               selection-pruned notice and app-shell.ts's live-quota announcement
               (docs/UI-Designsprache.md §4.5). -->
          <span role="status" class="sr-only">
            @if (resyncFeedbackKey(); as key) {
              {{ key | transloco }}
            }
          </span>
          @if (resyncFeedbackKey(); as key) {
            <span aria-hidden="true" class="text-sm text-fg-muted">{{ key | transloco }}</span>
          }
          @if (canViewUsageStats() && isBotActive()) {
            <button
              type="button"
              appButton="outline"
              [disabled]="resyncInProgress()"
              (click)="resync()"
              [title]="'channelWorkspace.resync.title' | transloco"
            >
              {{ 'channelWorkspace.resync.label' | transloco }}
            </button>
          }
          @if (canManage()) {
            @if (isBotActive()) {
              <button type="button" appButton="danger" (click)="leave()">
                {{ 'channelWorkspace.leaveChannel' | transloco }}
              </button>
            } @else {
              <button
                type="button"
                appButton="primary"
                [disabled]="rejoinInProgress()"
                (click)="rejoin()"
              >
                {{ 'channelWorkspace.rejoinChannel' | transloco }}
              </button>
            }
          }
        </div>
      </div>

      <!-- An inactive bot collects nothing, but every page below still renders its historical data
           as usual — without this the channel looks healthy while silently recording nothing. -->
      @if (canManage() && !isBotActive()) {
        <app-notice-banner variant="warning" class="mb-4 block">
          {{ 'channelWorkspace.botInactiveNotice' | transloco }}
        </app-notice-banner>
      }

      @if (errorMessage(); as message) {
        <app-notice-banner variant="error" class="mb-4 block">{{
          message | transloco
        }}</app-notice-banner>
      }

      <!-- Sticky under the h-14 shell header; h-10 is a contract — filter toolbars pin at
           top-24 (= 14 + 10). Links are flex/items-center so the fixed height carries exactly. -->
      <nav class="app-sticky-bar top-14 mb-6 flex h-10 gap-2 border-b border-border">
        @if (canViewUsageStats()) {
          <app-tab-link link="usage-stats" [label]="'channelWorkspace.tabs.usage' | transloco" />
        }
        <app-tab-link link="vote-sessions" [label]="'channelWorkspace.tabs.voting' | transloco" />
        <!-- canManage, not canViewUsageStats: the rows name which moderator did what, and the
             channel's 7TV editors are frequently outside the mod team. The route carries the same
             check as its own guard, so hiding the tab is visibility only. -->
        @if (canManage()) {
          <app-tab-link link="activity" [label]="'channelWorkspace.tabs.activity' | transloco" />
        }
      </nav>

      <router-outlet />
    </div>
  `,
})
export class ChannelWorkspaceLayout {
  readonly channelName = input.required<string>();

  private readonly channelService = inject(ChannelService);
  private readonly deleteService = inject(SevenTvDeleteService);
  private readonly restoreService = inject(SevenTvRestoreService);
  private readonly router = inject(Router);
  private readonly translocoService = inject(TranslocoService);
  private readonly dialog = inject(Dialog);

  // Was two probes: GET /api/channels/{c} for "may manage" and a throwaway one-day
  // GetUsageTotalsAsync call for "may see the usage tab" (weaker — it also admits the channel's 7TV
  // editors, who may not manage the channel at all, so it cannot just reuse canManage). Both are now
  // fields of one /permissions response. This is the UI-visibility half only: every action still goes
  // through the server-side filter, and the usage route has its own guard.
  protected readonly canManage = signal(false);
  protected readonly canViewUsageStats = signal(false);

  // Without this a deactivated channel offered no way back in: leaving keeps the row (see
  // ChannelService.LeaveAsync), so the overview lists it as tracked and never shows the "Hinzufügen"
  // button again — a non-admin was stuck with a permanently silent bot and no control anywhere in the
  // UI. Starts true so the leave button does not flicker into a reactivate button on load.
  protected readonly isBotActive = signal(true);
  protected readonly rejoinInProgress = signal(false);

  protected readonly resyncInProgress = signal(false);
  protected readonly resyncFeedbackKey = signal<string | null>(null);

  protected readonly errorMessage = signal<string | null>(null);

  private feedbackTimeout: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    effect(() => {
      const channelName = this.channelName();
      // A finished mass-delete or restore run from another channel must not follow the user in here.
      this.deleteService.resetIfChannelChanged(channelName);
      this.restoreService.resetIfChannelChanged(channelName);
      // Its own call, not part of the reset above: `resetIfChannelChanged` deliberately returns
      // early when there is no run record at all, which is exactly the state a confirmed-but-not-yet
      // started delete is in. Its dock claim would otherwise survive the channel change and hold
      // this channel's dock open — empty — for the length of its notice window.
      this.deleteService.clearConfirmedRun();
      this.loadPermissions(channelName);
    });

    // The 202 only means "the worker was told". This is what turns "angestoßen" into
    // "abgeschlossen": the RESYNC path publishes channel.synced unconditionally, unlike the
    // periodic one, precisely so this confirmation can exist. The stream is already scoped to this
    // channel, so no event needs inspecting beyond its type — but the upgrade only fires while a
    // resync of ours is still on screen, otherwise the periodic sync of any channel would announce
    // itself.
    //
    // liveEvents, undebounced, on purpose (see the two regression tests in
    // channel-workspace.e2e.spec.ts): a channel.synced that arrived before the click (the periodic
    // resync, say) must not sit in a debounce window and fire after resyncFeedbackKey was set by the
    // click, reporting "abgeschlossen" for a resync that had barely started. And during a dense burst
    // (7TV mass delete, ~275 ms apart) a debounce window never elapses at all, so a resync started
    // mid-burst would show "angestoßen" and then lose the confirmation entirely once
    // RESYNC_FEEDBACK_MS cleared it. This handler only sets a signal — it makes no HTTP request — so
    // none of that debouncing applies to it, unlike usage-stats-page.ts's own reload of this same
    // channel's live stream for its totals/set-status refetch.
    //
    // This costs nothing extra: since 5f4cd14 ("share one live sse connection per url")
    // LiveUpdateService.stream() is shared and ref-counted per URL, so this subscription and
    // usage-stats-page.ts's separate one against the same channelLiveUrl share one EventSource
    // rather than opening two.
    liveEvents(
      computed(() => channelLiveUrl(this.channelName())),
      [LIVE_EVENT_TYPES.channelSynced],
    ).subscribe(() => {
      if (this.resyncFeedbackKey() !== null) {
        this.showResyncFeedback('channelWorkspace.resync.completed');
      }
    });
  }

  protected leave(): void {
    // A leave now only deactivates the bot and keeps all history (see ChannelService.LeaveAsync) —
    // reversible by rejoining. Still confirmed, because it stops data collection for the channel.
    const data: ConfirmDialogData = {
      message: this.translocoService.translate('channelWorkspace.leaveConfirm', {
        channelName: this.channelName(),
      }),
      confirmLabel: this.translocoService.translate('channelWorkspace.leaveChannel'),
    };
    openConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }
      this.channelService.leave(this.channelName()).subscribe({
        next: () => this.router.navigateByUrl('/'),
        error: (error: HttpErrorResponse) => {
          this.errorMessage.set(
            error.status === 403
              ? 'channelWorkspace.errors.leaveForbidden'
              : 'channelWorkspace.errors.leaveFailed',
          );
        },
      });
    });
  }

  // Deliberately no confirmation and no navigation: reactivating is non-destructive and the admin is
  // already on the page they want to keep working on.
  protected rejoin(): void {
    this.rejoinInProgress.set(true);
    this.errorMessage.set(null);

    this.channelService.join(this.channelName()).subscribe({
      next: () => {
        this.isBotActive.set(true);
        this.rejoinInProgress.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.rejoinInProgress.set(false);
        this.errorMessage.set(
          error.status === 403
            ? 'channelWorkspace.errors.leaveForbidden'
            : 'channelWorkspace.errors.rejoinFailed',
        );
      },
    });
  }

  /**
   * The answer to "I added an emote and it is not showing up". No confirmation: it is
   * non-destructive and only asks the worker to re-read from 7TV.
   *
   * The server keeps a per-channel cooldown, so a second click within the window answers 429 with
   * `resync_cooldown_active` — rendered like any other API error rather than hidden, because "wait
   * a moment" is the useful answer there.
   */
  protected resync(): void {
    this.resyncInProgress.set(true);
    this.errorMessage.set(null);

    this.channelService.resync(this.channelName()).subscribe({
      next: () => {
        this.resyncInProgress.set(false);
        this.showResyncFeedback('channelWorkspace.resync.queued');
      },
      error: (error: HttpErrorResponse) => {
        this.resyncInProgress.set(false);
        this.errorMessage.set(apiErrorTranslationKey(error));
      },
    });
  }

  // A transient inline status rather than a toast — there is no toast service, and the admin
  // channel page solves the same problem the same way.
  private showResyncFeedback(key: string): void {
    this.resyncFeedbackKey.set(key);
    if (this.feedbackTimeout !== null) {
      clearTimeout(this.feedbackTimeout);
    }
    this.feedbackTimeout = setTimeout(() => this.resyncFeedbackKey.set(null), RESYNC_FEEDBACK_MS);
  }

  private loadPermissions(channelName: string): void {
    this.channelService.getPermissions(channelName).subscribe({
      next: (permissions) => {
        this.canManage.set(permissions.canManage);
        this.canViewUsageStats.set(permissions.canViewUsageStats);
        this.isBotActive.set(permissions.isBotActive);
      },
      // Only reachable for a logged-out user (the interceptor already redirects) or a server error —
      // hide everything privileged rather than guess.
      error: () => {
        this.canManage.set(false);
        this.canViewUsageStats.set(false);
      },
    });
  }
}
