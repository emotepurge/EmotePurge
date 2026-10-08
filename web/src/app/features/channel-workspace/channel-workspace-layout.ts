import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterOutlet } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { ChannelService } from '../../core/channels/channel.service';
import { isUnknownOutcome } from '../../core/http/unknown-outcome';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvUndoService } from '../../core/seven-tv/seven-tv-undo.service';
import { joinWithBroadcasterLockPrompt } from '../../shared/channels/join-with-lock-prompt';
import { BackLink } from '../../shared/ui/back-link';
import { Button } from '../../shared/ui/button';
import { ConfirmDialogData, openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { TabLink } from '../../shared/ui/tab-link';
import { openTypedConfirmDialog } from '../../shared/ui/typed-confirm-dialog';

const LOCKED_BY_BROADCASTER_KEY = 'errors.api.channel_locked_by_broadcaster';

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
          <!-- Its own condition, not nested in canManage: it follows the server's
               canPurgeAsBroadcaster (the channel's own broadcaster only) and, like the reactivate
               button, stays available while the bot is inactive. -->
          @if (canPurgeAsBroadcaster()) {
            <button
              type="button"
              appButton="danger"
              [disabled]="purgeInProgress()"
              (click)="purgeOwnData()"
            >
              {{ 'channelWorkspace.purgeOwnData' | transloco }}
            </button>
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
          <!-- Same visibility as the usage tab: the tags page has the same route guard (spec 9.3). -->
          <app-tab-link link="tags" [label]="'channelWorkspace.tabs.tags' | transloco" />
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
  private readonly undoService = inject(SevenTvUndoService);
  private readonly router = inject(Router);
  private readonly translocoService = inject(TranslocoService);
  private readonly dialog = inject(Dialog);
  private readonly authService = inject(AuthService);
  private readonly languageService = inject(LanguageService);

  // Was two probes: GET /api/channels/{c} for "may manage" and a throwaway one-day
  // GetUsageTotalsAsync call for "may see the usage tab" (weaker — it also admits the channel's 7TV
  // editors, who may not manage the channel at all, so it cannot just reuse canManage). Both are now
  // fields of one /permissions response. This is the UI-visibility half only: every action still goes
  // through the server-side filter, and the usage route has its own guard.
  protected readonly canManage = signal(false);
  protected readonly canViewUsageStats = signal(false);
  protected readonly canPurgeAsBroadcaster = signal(false);
  protected readonly purgeInProgress = signal(false);

  // Without this a deactivated channel offered no way back in: leaving keeps the row (see
  // ChannelService.LeaveAsync), so the overview lists it as tracked and never shows the "Hinzufügen"
  // button again — a non-admin was stuck with a permanently silent bot and no control anywhere in the
  // UI. Starts true so the leave button does not flicker into a reactivate button on load.
  protected readonly isBotActive = signal(true);
  protected readonly rejoinInProgress = signal(false);

  protected readonly errorMessage = signal<string | null>(null);

  constructor() {
    effect(() => {
      const channelName = this.channelName();
      // #256 P1 (Plan-256 review): everything below must run only when `channelName` itself
      // changes, never when a run's own signal does. `resetIfChannelChanged` (every service) has
      // read `run()`/`phase` since #256 T2 — without `untracked`, that read makes this effect a
      // dependent of the very record it inspects, so the moment a carried-over run's report reaches
      // `closed` (Plan-256 Festlegung 13 lets it follow the user here while still `reporting`), the
      // effect reruns with the *same* `channelName` and immediately resets it — the dock and its
      // retry vanish the instant the run finishes, with no `console.warn` and no chance for the user
      // to ever see the outcome. `untracked` is what keeps a channel *switch* as the only trigger;
      // a run reaching `closed` on the page it now sits on then waits for the next switch (or an
      // explicit close) exactly as Festlegung 13 intends.
      untracked(() => {
        // A finished mass-delete, restore or undo run from another channel must not follow the user
        // in here.
        this.deleteService.resetIfChannelChanged(channelName);
        this.restoreService.resetIfChannelChanged(channelName);
        this.undoService.resetIfChannelChanged(channelName);
        // Its own call, not part of the reset above: `resetIfChannelChanged` deliberately returns
        // early when there is no run record at all, which is exactly the state a
        // confirmed-but-not-yet started delete is in. Its dock claim would otherwise survive the
        // channel change and hold this channel's dock open — empty — for the length of its notice
        // window.
        this.deleteService.clearConfirmedRun();
        this.loadPermissions(channelName);
      });
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

    const deps = {
      channelService: this.channelService,
      dialog: this.dialog,
      transloco: this.translocoService,
      lang: this.languageService.lang(),
    };
    joinWithBroadcasterLockPrompt(deps, this.channelName()).subscribe({
      next: (status) => {
        // null = an admin declined to lift the broadcaster's lock: nothing changed.
        if (status) {
          this.isBotActive.set(true);
        }
        this.rejoinInProgress.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.rejoinInProgress.set(false);
        // 403 keeps its own copy (same wording as leave(), unusual for a rejoin but pre-existing);
        // everything else — including a 409 channel_capacity_reached — goes through the generic
        // mapping instead of the single hardcoded "could not reactivate" this used to fall back to,
        // the same generic mapping the other channel actions use.
        // A locked channel's 403 is the exception: it has its own text (the streamer removed the
        // channel), which is also how the mod team learns about the purge.
        const key = apiErrorTranslationKey(error);
        this.errorMessage.set(
          error.status === 403 && key !== LOCKED_BY_BROADCASTER_KEY
            ? 'channelWorkspace.errors.leaveForbidden'
            : key,
        );
      },
    });
  }

  /**
   * The broadcaster's own wipe of this channel's data. The numbers in the dialog are the server's
   * (`data-summary`), the confirmation is the typed channel name, and the call is bound to the
   * account the user confirmed as (`expectedTwitchUserId`, like the account deletion): a session
   * that changed in another tab by then is refused instead of acting for the wrong person.
   */
  protected purgeOwnData(): void {
    const user = this.authService.currentUser();
    if (!user || this.purgeInProgress()) {
      return;
    }
    const channelName = this.channelName();
    const expectedTwitchUserId = user.twitchUserId;
    this.purgeInProgress.set(true);
    this.errorMessage.set(null);

    this.channelService.getDataSummary(channelName).subscribe({
      next: (summary) => {
        openTypedConfirmDialog(this.dialog, {
          title: this.translocoService.translate('channelWorkspace.purgeOwnDataDialog.title'),
          message: this.translocoService.translate('channelWorkspace.purgeOwnDataDialog.message', {
            channelName,
            emotes: summary.emoteCount,
            voteSessions: summary.voteSessionCount,
            liveDays: summary.liveDayCount,
            tags: summary.tagCount,
          }),
          requiredText: channelName,
          inputLabel: this.translocoService.translate(
            'channelWorkspace.purgeOwnDataDialog.inputLabel',
          ),
          confirmLabel: this.translocoService.translate(
            'channelWorkspace.purgeOwnDataDialog.confirm',
          ),
        }).closed.subscribe((confirmed) => {
          if (!confirmed) {
            this.purgeInProgress.set(false);
            return;
          }
          this.channelService.purgeOwnData(channelName, expectedTwitchUserId).subscribe({
            next: () => {
              this.purgeInProgress.set(false);
              this.router.navigateByUrl('/');
            },
            error: (error: HttpErrorResponse) => {
              // Only the deletion can have committed behind a lost answer; the summary is a read.
              if (isUnknownOutcome(error.status)) {
                this.purgeInProgress.set(false);
                this.errorMessage.set('channelWorkspace.errors.purgeOwnDataUnconfirmed');
                return;
              }
              this.failPurge(error);
            },
          });
        });
      },
      error: (error: HttpErrorResponse) => this.failPurge(error),
    });
  }

  private failPurge(error: HttpErrorResponse): void {
    this.purgeInProgress.set(false);
    // A confirmed refusal, or a failed summary read (nothing was deleted then). A coded answer
    // (account_mismatch, channel_identity_unresolved, ...) says exactly what went wrong; anything else
    // gets the action's own "nothing changed" sentence.
    const key = apiErrorTranslationKey(error);
    this.errorMessage.set(
      key.startsWith('errors.api.') ? key : 'channelWorkspace.errors.purgeOwnDataFailed',
    );
  }

  private loadPermissions(channelName: string): void {
    this.channelService.getPermissions(channelName).subscribe({
      next: (permissions) => {
        this.canManage.set(permissions.canManage);
        this.canViewUsageStats.set(permissions.canViewUsageStats);
        this.isBotActive.set(permissions.isBotActive);
        this.canPurgeAsBroadcaster.set(permissions.canPurgeAsBroadcaster);
      },
      // Only reachable for a logged-out user (the interceptor already redirects) or a server error —
      // hide everything privileged rather than guess.
      error: () => {
        this.canManage.set(false);
        this.canViewUsageStats.set(false);
        this.canPurgeAsBroadcaster.set(false);
      },
    });
  }
}
