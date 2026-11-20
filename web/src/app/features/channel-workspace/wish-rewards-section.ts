import { Dialog } from '@angular/cdk/dialog';
import { NgTemplateOutlet } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Subscription } from 'rxjs';

import { apiErrorCode, GENERIC_ERROR_TRANSLATION_KEY } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import { channelLiveUrl, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveReload, liveReopened } from '../../core/live/live-reload';
import { pageVisible } from '../../core/page-visibility/page-visibility';
import { wishErrorKey } from '../../core/wishes/wish-error';
import type { WishCaptureGap, WishReward, WishRewardsRead } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { Button } from '../../shared/ui/button';
import { openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StatusBadge } from '../../shared/ui/status-badge';
import { openWishRewardDialog, type WishRewardDialogResult } from './wish-reward-dialog';
import {
  captureGapReasonKey,
  captureLooksStopped,
  captureStampRefreshDue,
  formatRemaining,
  learningPhase,
  learningStartBlock,
  type LearningPhase,
} from './wish-reward-learning';

/** UI-Designsprache §4.5: an acknowledgement fades after this long. */
const FEEDBACK_MS = 4000;
/** Same burst window the settings block above uses. */
const RELOAD_DEBOUNCE_MS = 500;
/** The countdown of a running learning session moves per second. */
const CLOCK_TICK_MS = 1000;

/** What the section shows besides the data: loading, a failed first read, or a read it may not do. */
type LoadState = 'loading' | 'ready' | 'failed' | 'forbidden' | 'unavailable';

/** A finished write the section acknowledges (§4.5), with what the text names. */
interface Feedback {
  key: string;
  params?: Record<string, string>;
}

/**
 * "Belohnungen aus dem Chat" inside the wishes block of the settings tab (K6, plan 2.10, T15): the
 * learned rewards (edit, pause, forget), learning a new one, and the capture gaps of the last 14 days.
 * Mounted only while the capture is on for this channel (`wishCaptureEnabled`); without it the block
 * is not there and `…/rewards` is never read.
 *
 * Learning is a small state machine (`learningPhase`): start → waiting with the remaining time →
 * the first redemption of an unknown reward shows up live (`wish.changed`) as the candidate with its
 * text, login and time → "Bestätigen" opens a dialog that saves label and duration with exactly the
 * ids it showed. Nothing is saved before that. A dialog the server calls stale (someone else started
 * over) says so and reloads.
 */
@Component({
  selector: 'app-wish-rewards-section',
  imports: [
    Button,
    EmptyState,
    NgTemplateOutlet,
    NoticeBanner,
    SkeletonRows,
    StatusBadge,
    TranslocoPipe,
  ],
  host: { class: 'flex flex-col gap-6' },
  template: `
    <!-- Permanently mounted (§4.5): acknowledgements and the learning steps arrive while the
         section stays; the visible twins are aria-hidden. The countdown is never in here. -->
    <p class="sr-only" role="status">
      @if (announcement(); as said) {
        {{ said.key | transloco: said.params }}
      }
    </p>

    @switch (loadState()) {
      @case ('unavailable') {
        <!-- The capture was switched off under the page: there is nothing to show. -->
      }
      @case ('forbidden') {
        <section class="flex flex-col gap-2" aria-labelledby="wish-rewards-heading">
          <h4 id="wish-rewards-heading" class="text-sm font-semibold">
            {{ 'wishes.settings.rewards.title' | transloco }}
          </h4>
          <p class="text-sm text-fg-muted">{{ 'wishes.settings.rewards.forbidden' | transloco }}</p>
        </section>
      }
      @case ('failed') {
        <section class="flex flex-col gap-2" aria-labelledby="wish-rewards-heading">
          <h4 id="wish-rewards-heading" class="text-sm font-semibold">
            {{ 'wishes.settings.rewards.title' | transloco }}
          </h4>
          <app-notice-banner variant="error">
            {{ 'wishes.settings.rewards.loadFailed' | transloco }}
            <button notice-action type="button" appButton="outline" (click)="retryLoad()">
              {{ 'wishes.settings.retry' | transloco }}
            </button>
          </app-notice-banner>
        </section>
      }
      @case ('loading') {
        <section class="flex flex-col gap-2" aria-labelledby="wish-rewards-heading">
          <h4 id="wish-rewards-heading" class="text-sm font-semibold">
            {{ 'wishes.settings.rewards.title' | transloco }}
          </h4>
          <app-skeleton-rows [count]="2" />
        </section>
      }
      @case ('ready') {
        @if (read(); as current) {
          <section class="flex flex-col gap-3" aria-labelledby="wish-rewards-heading">
            <h4 id="wish-rewards-heading" class="text-sm font-semibold">
              {{ 'wishes.settings.rewards.title' | transloco }}
              @if (current.channelBound) {
                <span class="font-normal text-fg-muted">
                  {{
                    'wishes.settings.rewards.count'
                      | transloco: { count: current.rewards.length, limit: current.limit }
                  }}
                </span>
              }
            </h4>
            <p class="text-sm text-fg-secondary">
              {{ 'wishes.settings.rewards.intro' | transloco }}
            </p>

            @if (!current.channelBound) {
              <p class="text-sm text-fg-body">
                {{ 'wishes.settings.rewards.unbound' | transloco }}
              </p>
            } @else {
              @if (captureStopped()) {
                <app-notice-banner variant="warning">
                  {{ 'wishes.settings.rewards.captureStopped' | transloco }}
                </app-notice-banner>
              }
              @if (noticeKey(); as key) {
                <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
              }

              @if (current.rewards.length === 0) {
                <app-empty-state
                  [title]="'wishes.settings.rewards.empty' | transloco"
                  [description]="'wishes.settings.rewards.emptyHint' | transloco"
                />
              } @else {
                <ul class="-mx-3 divide-y divide-border border-y border-border">
                  @for (reward of current.rewards; track reward.id) {
                    <li class="flex flex-col gap-2 px-3 py-3 sm:flex-row sm:items-start">
                      <div class="flex min-w-0 flex-1 flex-col gap-1">
                        <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
                          <span
                            [id]="'wish-reward-' + reward.id"
                            class="max-w-full font-medium break-words"
                            >{{ reward.label }}</span
                          >
                          @if (!reward.isActive) {
                            <app-status-badge tone="neutral">{{
                              'wishes.settings.rewards.paused' | transloco
                            }}</app-status-badge>
                          }
                        </div>
                        <p class="flex flex-wrap gap-x-3 text-xs text-fg-muted">
                          <span>{{
                            durationKey(reward.defaultDurationDays)
                              | transloco: { count: reward.defaultDurationDays }
                          }}</span>
                          @if (reward.lastRedemptionSeenAtUtc; as seen) {
                            <span>{{
                              'wishes.settings.rewards.lastSeen'
                                | transloco: { date: formatDateTime(seen) }
                            }}</span>
                          } @else {
                            <span>{{ 'wishes.settings.rewards.neverSeen' | transloco }}</span>
                          }
                        </p>
                      </div>
                      @if (editable()) {
                        <div
                          role="group"
                          [attr.aria-labelledby]="'wish-reward-' + reward.id"
                          class="flex flex-wrap items-center gap-2 sm:ml-auto sm:justify-end"
                        >
                          <button
                            type="button"
                            appButton="outline"
                            [disabled]="busy() !== null"
                            (click)="edit(reward)"
                          >
                            {{ 'wishes.settings.rewards.edit' | transloco }}
                          </button>
                          <button
                            type="button"
                            appButton="outline"
                            [disabled]="busy() !== null"
                            (click)="togglePaused(reward)"
                          >
                            {{
                              (reward.isActive
                                ? 'wishes.settings.rewards.pause'
                                : 'wishes.settings.rewards.resume'
                              ) | transloco
                            }}
                          </button>
                          <!-- The gap before the destructive action is the §8.7 order rule. -->
                          <button
                            type="button"
                            class="ml-3"
                            appButton="danger-quiet"
                            [disabled]="busy() !== null"
                            (click)="remove(reward)"
                          >
                            {{ 'wishes.settings.rewards.remove' | transloco }}
                          </button>
                        </div>
                      }
                    </li>
                  }
                </ul>
              }

              <div class="flex flex-col gap-2">
                @switch (phase().kind) {
                  @case ('waiting') {
                    <div class="flex flex-col gap-2 rounded-md bg-surface-inset px-3 py-3">
                      <p class="text-sm font-medium" aria-hidden="true">
                        {{ 'wishes.settings.rewards.learning.waiting' | transloco }}
                      </p>
                      <p class="text-sm text-fg-body">
                        {{ 'wishes.settings.rewards.learning.waitingHint' | transloco }}
                      </p>
                      <ng-container [ngTemplateOutlet]="sessionFooter" />
                    </div>
                  }
                  @case ('candidate') {
                    @if (candidateView(); as seen) {
                      <div class="flex flex-col gap-2 rounded-md bg-surface-inset px-3 py-3">
                        <p class="text-sm font-medium" aria-hidden="true">
                          {{ 'wishes.settings.rewards.learning.candidate' | transloco }}
                        </p>
                        <p class="text-sm break-words text-fg-body">
                          @if (seen.text !== '') {
                            <span class="sr-only">{{
                              'wishes.settings.rewards.learning.textLabel' | transloco
                            }}</span>
                            <q>{{ seen.text }}</q>
                          } @else {
                            {{ 'wishes.settings.rewards.learning.noText' | transloco }}
                          }
                        </p>
                        <p class="text-xs text-fg-muted">
                          {{
                            'wishes.settings.rewards.learning.seenBy'
                              | transloco: { login: seen.login, time: formatTime(seen.seenAtUtc) }
                          }}
                        </p>
                        <p class="text-sm text-fg-secondary">
                          {{ 'wishes.settings.rewards.learning.candidateHint' | transloco }}
                        </p>
                        <ng-container [ngTemplateOutlet]="sessionFooter" />
                      </div>
                    }
                  }
                  @default {
                    @if (phase().kind === 'expired') {
                      <p class="text-sm text-fg-body" aria-hidden="true">
                        {{ 'wishes.settings.rewards.learning.expired' | transloco }}
                      </p>
                    }
                    @switch (startBlock()) {
                      @case ('readOnly') {
                        <p class="text-sm text-fg-muted">
                          {{ 'wishes.settings.rewards.readOnly' | transloco }}
                        </p>
                      }
                      @default {
                        <p class="text-sm text-fg-secondary">
                          {{ 'wishes.settings.rewards.learning.startHint' | transloco }}
                        </p>
                        <div class="flex flex-wrap items-center gap-3">
                          <button
                            type="button"
                            appButton="neutral"
                            [disabled]="busy() !== null || startBlock() !== null"
                            [attr.aria-describedby]="
                              startBlock() === 'limit' ? 'wish-rewards-start-blocked' : null
                            "
                            (click)="start()"
                          >
                            {{ 'wishes.settings.rewards.learning.start' | transloco }}
                          </button>
                          @if (startBlock() === 'limit') {
                            <p id="wish-rewards-start-blocked" class="text-xs text-fg-muted">
                              {{
                                'wishes.settings.rewards.learning.limit'
                                  | transloco: { limit: current.limit }
                              }}
                            </p>
                          }
                        </div>
                      }
                    }
                  }
                }
              </div>
            }

            @if (feedback(); as shown) {
              <span class="text-sm text-fg-secondary" aria-hidden="true">{{
                shown.key | transloco: shown.params
              }}</span>
            }
          </section>

          @if (current.channelBound) {
            <section class="flex flex-col gap-2" aria-labelledby="wish-gaps-heading">
              @if (current.gaps.length === 0) {
                <h4 id="wish-gaps-heading" class="text-sm font-semibold">
                  {{ 'wishes.settings.rewards.gaps.title' | transloco }}
                </h4>
                <p class="text-sm text-fg-muted">
                  {{ 'wishes.settings.rewards.gaps.none' | transloco }}
                </p>
              } @else {
                <!-- Details on demand: the gaps matter when looking for a lost redemption. -->
                <h4 id="wish-gaps-heading" class="text-sm font-semibold">
                  <button
                    type="button"
                    class="inline-flex min-h-6 items-center gap-2"
                    [attr.aria-expanded]="gapsOpen()"
                    aria-controls="wish-gaps-list"
                    (click)="gapsOpen.set(!gapsOpen())"
                  >
                    <svg
                      aria-hidden="true"
                      viewBox="0 0 20 20"
                      class="size-4 shrink-0 text-fg-muted transition-transform motion-reduce:transition-none"
                      [class.rotate-90]="gapsOpen()"
                    >
                      <path d="M7 4l6 6-6 6" fill="none" stroke="currentColor" stroke-width="2" />
                    </svg>
                    {{ 'wishes.settings.rewards.gaps.title' | transloco }}
                    <span class="font-normal text-fg-muted">{{ current.gaps.length }}</span>
                  </button>
                </h4>
                <div id="wish-gaps-list" class="flex flex-col gap-2" [hidden]="!gapsOpen()">
                  <p class="text-sm text-fg-secondary">
                    {{ 'wishes.settings.rewards.gaps.hint' | transloco }}
                  </p>
                  <ul class="-mx-3 divide-y divide-border border-y border-border">
                    @for (gap of current.gaps; track gap.fromUtc + gap.reason) {
                      <li class="flex flex-wrap gap-x-3 px-3 py-2 text-sm">
                        <span class="text-fg-body">{{ formatGap(gap) }}</span>
                        <span class="text-fg-muted">{{
                          gapReasonKey(gap.reason) | transloco
                        }}</span>
                      </li>
                    }
                  </ul>
                </div>
              }
            </section>
          }
        }
      }
    }

    <ng-template #sessionFooter>
      <div class="flex flex-wrap items-center gap-3">
        @if (remaining(); as left) {
          <span class="text-sm text-fg-secondary tabular-nums">{{
            'wishes.settings.rewards.learning.remaining' | transloco: { time: left }
          }}</span>
        }
        @if (editable()) {
          <span class="flex flex-wrap gap-2 sm:ml-auto">
            <button
              type="button"
              appButton="outline"
              [disabled]="busy() !== null"
              (click)="cancel()"
            >
              {{ 'wishes.settings.rewards.learning.cancel' | transloco }}
            </button>
            @if (phase().kind === 'candidate') {
              <button
                type="button"
                appButton="primary"
                [disabled]="busy() !== null"
                (click)="confirm()"
              >
                {{ 'wishes.settings.rewards.learning.confirm' | transloco }}
              </button>
            }
          </span>
        }
      </div>
    </ng-template>
  `,
})
export class WishRewardsSection {
  readonly channelName = input.required<string>();
  /** The channel's default wish duration; the confirm dialog starts with it. */
  readonly defaultDurationDays = input.required<number>();

  private readonly wishService = inject(WishService);
  private readonly dialog = inject(Dialog);
  private readonly transloco = inject(TranslocoService);
  private readonly languageService = inject(LanguageService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly visible = pageVisible();

  /** The last rewards answer; `null` until the first one. */
  protected readonly read = signal<WishRewardsRead | null>(null);
  protected readonly loadState = signal<LoadState>('loading');
  /** The write under way (`start`, `cancel`, `reward:<id>`); every button waits meanwhile (§6.1). */
  protected readonly busy = signal<string | null>(null);
  /** A failed write, as the translation key the section names until the next write. */
  protected readonly noticeKey = signal<string | null>(null);
  protected readonly feedback = signal<Feedback | null>(null);
  /** A write answered 403 `wish_settings_restricted`: read-only from then on, whatever `canEdit` says. */
  protected readonly restricted = signal(false);
  protected readonly gapsOpen = signal(false);
  private readonly now = signal(new Date());
  /** The session the section showed ran out since the last start (`learningPhase`). */
  private readonly expiredSeen = signal(false);

  protected readonly editable = computed(() => {
    const current = this.read();
    return current !== null && current.canEdit && !this.restricted();
  });

  protected readonly phase = computed<LearningPhase>(() =>
    learningPhase(this.read()?.learning ?? null, this.now(), this.expiredSeen()),
  );

  protected readonly candidateView = computed(() => {
    const phase = this.phase();
    return phase.kind === 'candidate' ? phase.learning.candidate : null;
  });

  protected readonly remaining = computed(() => {
    const phase = this.phase();
    return (phase.kind === 'waiting' || phase.kind === 'candidate') && phase.remainingMs !== null
      ? formatRemaining(phase.remainingMs)
      : null;
  });

  protected readonly startBlock = computed(() => {
    const current = this.read();
    return current === null ? null : learningStartBlock(current, this.restricted());
  });

  protected readonly captureStopped = computed(() => {
    const current = this.read();
    return current !== null && captureLooksStopped(current.captureAliveAtUtc, this.now());
  });

  /** What the status region says: the latest acknowledgement, else the learning step. */
  protected readonly announcement = computed<Feedback | null>(() => {
    const shown = this.feedback();
    if (shown !== null) {
      return shown;
    }
    switch (this.phase().kind) {
      case 'waiting':
        return { key: 'wishes.settings.rewards.learning.waiting' };
      case 'candidate':
        return { key: 'wishes.settings.rewards.learning.candidate' };
      case 'expired':
        return { key: 'wishes.settings.rewards.learning.expired' };
      default:
        return null;
    }
  });

  private loadSubscription: Subscription | null = null;
  /** When the latest read went out; the life-stamp refresh waits a minute after any read. */
  private lastReadStartedAt = new Date(0);
  /** Number of the latest read; a successful write bumps it too, so no older answer undoes it. */
  private loadSeq = 0;
  private feedbackTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    effect((onCleanup) => {
      const channel = this.channelName();
      untracked(() => {
        this.reset();
        this.load(channel);
      });
      onCleanup(() => this.loadSubscription?.unsubscribe());
    });

    // A session the section showed that runs out stays named as "abgelaufen" until the next start,
    // even after the server has dropped the row; a running one clears the mark.
    effect(() => {
      const raw = learningPhase(this.read()?.learning ?? null, this.now(), false);
      if (raw.kind === 'expired') {
        this.expiredSeen.set(true);
      } else if (raw.kind !== 'idle') {
        this.expiredSeen.set(false);
      }
    });

    const url = computed(() => channelLiveUrl(this.channelName()));
    // A redemption seen while learning, another manager's change, a new gap: all are `wish.changed`.
    liveReload(url, {
      accept: [LIVE_EVENT_TYPES.wishChanged],
      debounceMs: RELOAD_DEBOUNCE_MS,
    }).subscribe(() => this.reloadQuietly());
    // A reopened stream may have swallowed one (the K3 catch-up).
    liveReopened(url).subscribe(() => this.reloadQuietly());

    const timer = setInterval(() => {
      this.now.set(new Date());
      this.refreshStampIfDue();
    }, CLOCK_TICK_MS);
    this.destroyRef.onDestroy(() => {
      clearInterval(timer);
      this.clearFeedbackTimer();
    });
  }

  protected retryLoad(): void {
    this.loadState.set('loading');
    this.load(this.channelName());
  }

  protected start(): void {
    if (this.busy() !== null || this.startBlock() !== null) {
      return;
    }
    const channel = this.channelName();
    this.beginWrite('start');
    this.wishService.startLearning(channel).subscribe({
      next: (learning) => {
        if (!this.endWrite(channel)) {
          return;
        }
        this.expiredSeen.set(false);
        // A redemption may have come in while the answer was on its way, and a live read may already
        // show it: the same session with its candidate is newer than the empty one the start returned.
        this.patch((current) =>
          current.learning?.sessionId === learning.sessionId && current.learning.candidate
            ? current
            : { ...current, learning },
        );
      },
      error: (error: unknown) => {
        if (this.endWrite(channel)) {
          this.onWriteError(error);
        }
      },
    });
  }

  protected cancel(): void {
    const learning = this.read()?.learning;
    if (this.busy() !== null || !learning) {
      return;
    }
    const channel = this.channelName();
    this.beginWrite('cancel');
    // The session the section shows, not whatever lies there now (Sol-P2-4).
    this.wishService.cancelLearning(channel, learning.sessionId).subscribe({
      next: () => {
        if (!this.endWrite(channel)) {
          return;
        }
        this.patch((current) =>
          current.learning?.sessionId === learning.sessionId
            ? { ...current, learning: null }
            : current,
        );
        this.showFeedback({ key: 'wishes.settings.rewards.feedback.cancelled' });
      },
      error: (error: unknown) => {
        if (this.endWrite(channel)) {
          this.onWriteError(error);
        }
      },
    });
  }

  protected confirm(): void {
    const phase = this.phase();
    if (this.busy() !== null || phase.kind !== 'candidate') {
      return;
    }
    const channel = this.channelName();
    this.noticeKey.set(null);
    // The dialog holds the candidate as shown now; a live reload behind it changes nothing in it.
    openWishRewardDialog(this.dialog, {
      mode: 'confirm',
      channelName: channel,
      learning: phase.learning,
      defaultDurationDays: this.defaultDurationDays(),
    }).closed.subscribe((result) => {
      if (channel !== this.channelName()) {
        return;
      }
      if (result?.kind === 'saved') {
        const reward = result.reward;
        this.loadSeq++;
        this.patch((current) => ({
          ...current,
          learning: null,
          rewards: [...current.rewards.filter((r) => r.id !== reward.id), reward],
        }));
        this.showFeedback({
          key: 'wishes.settings.rewards.feedback.learned',
          params: { label: reward.label },
        });
        return;
      }
      this.onDialogClosed(result);
    });
  }

  protected edit(reward: WishReward): void {
    if (this.busy() !== null) {
      return;
    }
    const channel = this.channelName();
    this.noticeKey.set(null);
    openWishRewardDialog(this.dialog, {
      mode: 'edit',
      channelName: channel,
      reward,
    }).closed.subscribe((result) => {
      if (channel !== this.channelName()) {
        return;
      }
      if (result?.kind === 'saved') {
        this.replaceReward(result.reward);
        this.showFeedback({ key: 'wishes.settings.rewards.feedback.saved' });
        return;
      }
      this.onDialogClosed(result);
    });
  }

  protected togglePaused(reward: WishReward): void {
    if (this.busy() !== null) {
      return;
    }
    const channel = this.channelName();
    this.beginWrite(`reward:${reward.id}`);
    this.wishService.updateReward(channel, reward.id, { isActive: !reward.isActive }).subscribe({
      next: (saved) => {
        if (!this.endWrite(channel)) {
          return;
        }
        this.replaceReward(saved);
        this.showFeedback({
          key: saved.isActive
            ? 'wishes.settings.rewards.feedback.resumed'
            : 'wishes.settings.rewards.feedback.paused',
          params: { label: saved.label },
        });
      },
      error: (error: unknown) => {
        if (this.endWrite(channel)) {
          this.onWriteError(error);
        }
      },
    });
  }

  protected remove(reward: WishReward): void {
    if (this.busy() !== null) {
      return;
    }
    const channel = this.channelName();
    openConfirmDialog(this.dialog, {
      message: this.transloco.translate('wishes.settings.rewards.removeConfirm', {
        label: reward.label,
      }),
      confirmLabel: this.transloco.translate('wishes.settings.rewards.remove'),
    }).closed.subscribe((confirmed) => {
      if (confirmed !== true || channel !== this.channelName() || this.busy() !== null) {
        return;
      }
      this.beginWrite(`reward:${reward.id}`);
      this.wishService.deleteReward(channel, reward.id).subscribe({
        next: () => {
          if (!this.endWrite(channel)) {
            return;
          }
          this.patch((current) => ({
            ...current,
            rewards: current.rewards.filter((r) => r.id !== reward.id),
          }));
          this.showFeedback({
            key: 'wishes.settings.rewards.feedback.removed',
            params: { label: reward.label },
          });
        },
        error: (error: unknown) => {
          if (this.endWrite(channel)) {
            this.onWriteError(error);
          }
        },
      });
    });
  }

  protected durationKey(days: number): string {
    return pluralKey(days, 'wishes.settings.rewards.duration');
  }

  protected gapReasonKey(reason: string): string {
    return captureGapReasonKey(reason);
  }

  protected formatDateTime(iso: string): string {
    return new Date(iso).toLocaleString(this.locale(), { dateStyle: 'medium', timeStyle: 'short' });
  }

  protected formatTime(iso: string): string {
    return new Date(iso).toLocaleTimeString(this.locale(), { timeStyle: 'short' });
  }

  /** "19.11.2026, 14:02 – 14:09"; the end carries its date only when it is another day. */
  protected formatGap(gap: WishCaptureGap): string {
    const from = new Date(gap.fromUtc);
    const to = new Date(gap.toUtc);
    const sameDay = from.toDateString() === to.toDateString();
    const end = sameDay ? this.formatTime(gap.toUtc) : this.formatDateTime(gap.toUtc);
    return `${this.formatDateTime(gap.fromUtc)} – ${end}`;
  }

  /** What a dialog result other than `saved` asks of the section. */
  private onDialogClosed(result: WishRewardDialogResult | undefined): void {
    switch (result?.kind) {
      case 'stale':
        this.reloadQuietly();
        return;
      case 'gone':
        this.noticeKey.set(result.errorKey);
        this.reloadQuietly();
        return;
      case 'restricted':
        this.restricted.set(true);
        return;
      default:
        return;
    }
  }

  private onWriteError(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.noticeKey.set(GENERIC_ERROR_TRANSLATION_KEY);
      return;
    }
    switch (apiErrorCode(error)) {
      case 'wish_settings_restricted':
        // Same state as `canEdit = false`: the explanation takes the place of the error.
        this.restricted.set(true);
        return;
      case 'wish_capture_disabled':
      case 'wishes_disabled':
        this.loadState.set('unavailable');
        return;
      case undefined:
        if (error.status === 403) {
          // No body: the Twitch-ID check refused (T7). The generic 403 text talks about missing
          // rights in general; here the manager can act on the actual reason.
          this.noticeKey.set('wishes.settings.rewards.forbiddenWrite');
          return;
        }
        this.noticeKey.set(wishErrorKey(error, 'action'));
        this.reloadQuietly();
        return;
      case 'wish_channel_unbound':
        // The section explains the unbound state itself once the read says so.
        this.reloadQuietly();
        return;
      default:
        // `wish_learning_active`, `wish_learning_stale`, `wish_reward_limit`, `wish_reward_not_found`:
        // the state moved on under the section. Name it and show the current one.
        this.noticeKey.set(wishErrorKey(error, 'action'));
        this.reloadQuietly();
    }
  }

  private beginWrite(what: string): void {
    this.busy.set(what);
    this.noticeKey.set(null);
  }

  /**
   * Ends the write; `false` when the channel changed meanwhile and the answer must be ignored. A read
   * still on its way is dropped (it may predate the write), and read again once the caller has applied
   * the answer: it was most likely a `wish.changed` reload (a candidate seen while starting, another
   * manager's change), and no later event may come for it.
   */
  private endWrite(channel: string): boolean {
    if (channel !== this.channelName()) {
      return false;
    }
    this.busy.set(null);
    const dropped = this.readInFlight();
    // An older read must not undo what this write's answer says.
    const seq = ++this.loadSeq;
    if (dropped) {
      // After the caller's synchronous patch, and only if nothing has read since (an error path reloads).
      queueMicrotask(() => {
        if (seq === this.loadSeq && channel === this.channelName()) {
          this.reloadQuietly();
        }
      });
    }
    return true;
  }

  private patch(change: (current: WishRewardsRead) => WishRewardsRead): void {
    const current = this.read();
    if (current !== null) {
      this.read.set(change(current));
    }
  }

  private replaceReward(saved: WishReward): void {
    this.patch((current) => ({
      ...current,
      rewards: current.rewards.map((r) => (r.id === saved.id ? saved : r)),
    }));
  }

  /**
   * The worker's stamp writes send no `wish.changed`: while the section is shown, a stamp nearing the
   * three-minute warning (or past it, or missing) is read again, at most once a minute and never while
   * the window is hidden or a read or write is under way (plan 2.7, Sol review). No warning without a
   * bound channel, so no read for one either.
   */
  private refreshStampIfDue(): void {
    const current = this.read();
    if (
      current === null ||
      !current.channelBound ||
      !this.visible() ||
      this.loadState() !== 'ready' ||
      this.busy() !== null ||
      this.readInFlight()
    ) {
      return;
    }
    if (captureStampRefreshDue(current.captureAliveAtUtc, this.now(), this.lastReadStartedAt)) {
      this.reloadQuietly();
    }
  }

  private readInFlight(): boolean {
    return this.loadSubscription !== null && !this.loadSubscription.closed;
  }

  private reloadQuietly(): void {
    if (this.loadState() === 'unavailable') {
      return;
    }
    this.load(this.channelName());
  }

  private load(channel: string): void {
    this.loadSubscription?.unsubscribe();
    const seq = ++this.loadSeq;
    this.lastReadStartedAt = new Date();
    this.loadSubscription = this.wishService.getRewards(channel).subscribe({
      next: (read) => {
        if (seq !== this.loadSeq) {
          return;
        }
        this.read.set(read);
        this.loadState.set('ready');
      },
      error: (error: unknown) => {
        if (seq !== this.loadSeq) {
          return;
        }
        const code = error instanceof HttpErrorResponse ? apiErrorCode(error) : null;
        if (code === 'wish_capture_disabled' || code === 'wishes_disabled') {
          this.loadState.set('unavailable');
          return;
        }
        // A failed background read keeps what is on screen; only a section without data says so.
        if (this.read() === null) {
          const forbidden = error instanceof HttpErrorResponse && error.status === 403;
          this.loadState.set(forbidden ? 'forbidden' : 'failed');
        }
      },
    });
  }

  private showFeedback(feedback: Feedback): void {
    this.clearFeedbackTimer();
    this.feedback.set(feedback);
    this.feedbackTimer = setTimeout(() => {
      this.feedbackTimer = null;
      this.feedback.set(null);
    }, FEEDBACK_MS);
  }

  private clearFeedbackTimer(): void {
    if (this.feedbackTimer !== null) {
      clearTimeout(this.feedbackTimer);
      this.feedbackTimer = null;
    }
  }

  private locale(): string {
    return toLocale(this.languageService.lang());
  }

  /** A channel switch starts over: the router reuses this component between channels. */
  private reset(): void {
    this.read.set(null);
    this.loadState.set('loading');
    this.busy.set(null);
    this.noticeKey.set(null);
    this.restricted.set(false);
    this.expiredSeen.set(false);
    this.gapsOpen.set(false);
    this.clearFeedbackTimer();
    this.feedback.set(null);
  }
}
