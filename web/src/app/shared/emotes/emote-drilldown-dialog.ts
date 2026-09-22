import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import { EmoteUsageSeries } from '../../core/usage-stats/usage-stat.model';
import { UsageStatService } from '../../core/usage-stats/usage-stat.service';
import { VoteType } from '../../core/voting/vote-session.model';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NoticeBanner } from '../ui/notice-banner';
import { UsageTrend, daysInSet, usageTrend } from './emote-context';
import { EmoteSpriteAnimated } from './emote-sprite-animated';
import { UsageSparkline } from './usage-sparkline';
import {
  SparklinePoint,
  fillDailySeries,
  liveDayCaptionKey,
  liveDayCoverage,
  seriesPeak,
} from './usage-series';

/**
 * What the host page hands over. The optional blocks follow what the page actually knows: only the
 * usage page carries trend inputs, only the vote page carries a vote block — the dialog renders
 * the blocks it has inputs for and stays silent about the rest.
 */
export interface EmoteDrilldownData {
  channelName: string;
  /** ISO dates of the page's selected range, both inclusive. */
  from: string;
  to: string;
  emoteId: string;
  emoteName: string;
  imageUrl: string;
  /**
   * The set the host page's numbers are counted under, frozen when the dialog opens (spec #200,
   * 7.2/F4, AK 64): a dropdown switch behind an open dialog must not re-point its request. `null` or
   * absent means "the channel's active set" — a null-session's case on the vote page (T6.3): a
   * set-session passes its own `emoteSetId` instead, so its drilldown charts the ballot's set, not
   * whatever the channel happens to be showing as active right now.
   */
  emoteSetId?: string | null;
  /** Usage page only — trend and time-in-set need these. */
  firstSeenAt?: string | null;
  previousWindowUseCount?: number;
  trackedSince?: string | null;
  /** Vote page only. `null` inside = withheld (secret ballot) and simply not rendered. */
  vote?: {
    keepVotes: number | null;
    deleteVotes: number | null;
    score: number | null;
    myVote: VoteType | null;
  };
}

/**
 * The A5 drilldown: daily sparkline plus first/last use for the ~200 borderline emotes of a big
 * set that the grid's single number cannot answer. A CDK dialog rather than a popover — the
 * trigger sits inside a virtualized card row, which recycles under an anchored panel (§7.1 keeps
 * the popover primitive for sticky-bar dropdowns). Loads its own series through the service's
 * per-(channel, emote, range) cache, so reopening the same emote costs no request.
 */
@Component({
  selector: 'app-emote-drilldown-dialog',
  imports: [Button, DialogShell, EmoteSpriteAnimated, NoticeBanner, TranslocoPipe, UsageSparkline],
  template: `
    <app-dialog-shell>
      <!-- Heading with a thumbnail, hence the projection slot rather than [dialogTitle]. The id is
           the shell's DIALOG_TITLE_ID — that is what names the dialog for assistive tech. -->
      <div dialog-header class="flex items-center gap-3">
        <!-- On touch this dialog is the only place the animation is ever seen: the sidecar is
             'hidden lg:block' and the readout line is 'pointer-coarse:hidden'.
             spriteClass is left at its default rather than the fixed-size 'max-h-10 max-w-10'
             the plain sprite used: EmoteSpriteAnimated wraps its two layers in a
             'relative block h-full w-full' span (the overlay needs that for its absolute
             positioning), so the image is no longer a flex child this box's 'items-center
             justify-center' can center — a fixed max-size class would sit top-left instead. The
             default 'object-contain' fills and centers within the full 48px box on its own, and
             its 'p-1' (4px per side) reproduces the same 40px picture that was rendered before.
             Same construction as the usage page's sidecar. -->
        <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded bg-emote-canvas">
          <app-emote-sprite-animated [url]="data.imageUrl" [size]="40" />
        </div>
        <div class="min-w-0">
          <h2 id="app-dialog-title" class="truncate text-lg font-semibold text-fg">
            {{ data.emoteName }}
          </h2>
          <p class="text-xs text-fg-muted">
            {{
              'usageStats.drilldown.range'
                | transloco: { from: formatDate(data.from), to: formatDate(data.to) }
            }}
          </p>
        </div>
      </div>

      @if (errorKey(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      } @else if (!series()) {
        <div
          class="app-skeleton h-20"
          role="status"
          [attr.aria-label]="'usageStats.drilldown.loading' | transloco"
        ></div>
      } @else {
        <div class="flex flex-col gap-3">
          <!-- The y-axis lives in HTML next to the SVG: the sparkline's viewBox is stretched
               non-uniformly (preserveAspectRatio="none"), so text inside it would distort. Hidden
               from AT — the peak line below already states the maximum in words. -->
          <div class="flex h-20 gap-2 rounded-md bg-surface-inset p-2">
            @if (yMax() > 0) {
              <div
                class="flex shrink-0 flex-col justify-between text-right text-xs leading-none text-fg-muted"
                aria-hidden="true"
              >
                <span>{{ formatCount(yMax()) }}</span>
                <span>0</span>
              </div>
            }
            <app-usage-sparkline
              class="block h-full min-w-0 flex-1"
              [points]="points()"
              [liveDays]="series()!.liveDays"
              [drawFrom]="drawFrom()"
              [ariaLabel]="'usageStats.chart.label' | transloco"
            />
          </div>

          <div class="flex flex-col gap-1">
            <!-- The text line under the graphic, so the curve never carries the numbers alone. -->
            @if (peak(); as peakDay) {
              <p class="text-sm text-fg-secondary">
                {{
                  'usageStats.drilldown.peak'
                    | transloco: { count: peakDay.useCount, date: formatDate(peakDay.date) }
                }}
              </p>
            } @else {
              <p class="text-sm text-fg-muted">{{ 'usageStats.drilldown.noUsage' | transloco }}</p>
            }
            <!-- What this emote did on the days we know the stream was live. Silent without any
                 coverage — an older range predates the poll's data, and a count over days we never
                 measured would be a false statement. -->
            @if (liveKey(); as key) {
              <p class="flex items-center gap-1.5 text-xs text-fg-muted">
                <span
                  class="inline-block h-2 w-2 rounded-sm bg-success-dot"
                  aria-hidden="true"
                ></span>
                {{ key | transloco: coverage() }}
              </p>
            }
          </div>

          <dl class="grid grid-cols-2 gap-x-6 gap-y-1 text-sm">
            <div class="flex justify-between gap-2 sm:block">
              <dt class="text-fg-muted">{{ 'usageStats.drilldown.totalInRange' | transloco }}</dt>
              <dd class="text-fg-body">
                {{ series()!.totalUseCount }}x
                @if (trend(); as trendValue) {
                  @if (trendValue !== 'unknown') {
                    <span
                      [class]="
                        'text-xs ' +
                        (trendValue === 'rising'
                          ? 'text-success-fg'
                          : trendValue === 'falling'
                            ? 'text-warning-fg'
                            : 'text-fg-muted')
                      "
                      [title]="'usageStats.trend.' + trendValue | transloco"
                      >{{
                        trendValue === 'rising' ? '↗' : trendValue === 'falling' ? '↘' : '→'
                      }}</span
                    >
                  }
                }
              </dd>
            </div>
            @if (inSetDays(); as days) {
              <div class="flex justify-between gap-2 sm:block">
                <dt class="text-fg-muted">{{ 'usageStats.drilldown.inSet' | transloco }}</dt>
                <dd class="text-fg-body">{{ inSetKey()! | transloco: { days } }}</dd>
              </div>
            }
            <div class="flex justify-between gap-2 sm:block">
              <dt class="text-fg-muted">{{ 'usageStats.drilldown.firstUsed' | transloco }}</dt>
              <dd class="text-fg-body">
                {{
                  series()!.firstUsedDate
                    ? formatDate(series()!.firstUsedDate!)
                    : ('usageStats.neverUsed' | transloco)
                }}
              </dd>
            </div>
            <div class="flex justify-between gap-2 sm:block">
              <dt class="text-fg-muted">{{ 'usageStats.drilldown.lastUsed' | transloco }}</dt>
              <dd class="text-fg-body">
                {{
                  series()!.lastUsedDate
                    ? formatDate(series()!.lastUsedDate!)
                    : ('usageStats.neverUsed' | transloco)
                }}
              </dd>
            </div>
          </dl>
        </div>
      }

      <!-- Only the vote page passes this block; null values inside are the server's secret-ballot
           verdict and render nothing rather than a placeholder number. -->
      @if (data.vote; as vote) {
        <div class="border-t border-border pt-3 text-sm">
          <h3 class="mb-1 text-base font-semibold text-fg-body">
            {{ 'usageStats.drilldown.votes' | transloco }}
          </h3>
          <div class="flex flex-wrap gap-x-4 gap-y-1">
            @if (vote.keepVotes !== null) {
              <span class="text-fg-secondary">
                {{ 'usageStats.drilldown.keepVotes' | transloco: { count: vote.keepVotes } }}
              </span>
            }
            @if (vote.deleteVotes !== null) {
              <span class="text-fg-secondary">
                {{ 'usageStats.drilldown.deleteVotes' | transloco: { count: vote.deleteVotes } }}
              </span>
            }
            @if (vote.score !== null) {
              <span class="text-fg-secondary">
                {{ 'voting.detail.scoreLabel' | transloco }} {{ formatScore(vote.score) }}
              </span>
            }
            @if (vote.keepVotes === null && vote.score === null) {
              <span class="text-fg-muted italic">
                {{ 'voting.detail.resultsHiddenShort' | transloco }}
              </span>
            }
          </div>
          <p class="mt-1 text-xs text-fg-muted">{{ myVoteKey() | transloco }}</p>
        </div>
      }

      <button
        dialog-actions
        type="button"
        appButton="neutral"
        buttonSize="lg"
        (click)="dialogRef.close()"
      >
        {{ 'common.close' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class EmoteDrilldownDialog {
  protected readonly data = inject<EmoteDrilldownData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<void>>(DialogRef);
  private readonly usageStatService = inject(UsageStatService);
  private readonly languageService = inject(LanguageService);

  protected readonly series = signal<EmoteUsageSeries | null>(null);
  protected readonly errorKey = signal<string | null>(null);

  protected readonly points = computed<SparklinePoint[]>(() => {
    const series = this.series();
    return series ? fillDailySeries(series.days, series.from, series.to) : [];
  });

  /** The day this emote entered the set; `null` when 7TV reported none, and then nothing is trimmed. */
  protected readonly drawFrom = computed(() => this.data.firstSeenAt?.slice(0, 10) ?? null);

  protected readonly peak = computed(() => seriesPeak(this.points(), this.drawFrom() ?? undefined));
  protected readonly yMax = computed(() => this.peak()?.useCount ?? 0);

  protected readonly coverage = computed(() =>
    liveDayCoverage(this.points(), this.series()?.liveDays ?? [], this.drawFrom() ?? undefined),
  );

  protected readonly liveKey = computed(() =>
    liveDayCaptionKey(this.coverage(), (this.series()?.liveDays.length ?? 0) > 0),
  );

  /** Suppressed ('unknown') without the usage page's inputs — never guessed. */
  protected readonly trend = computed<UsageTrend>(() => {
    const series = this.series();
    const trackedSince = this.data.trackedSince;
    const previousWindowUseCount = this.data.previousWindowUseCount;
    if (!series || !trackedSince || previousWindowUseCount === undefined) {
      return 'unknown';
    }
    return usageTrend({
      totalUseCount: series.totalUseCount,
      previousWindowUseCount,
      firstSeenAt: this.data.firstSeenAt ?? null,
      windowStart: series.from,
      windowEnd: series.to,
      trackedSince,
    });
  });

  protected readonly inSetDays = computed(() =>
    this.data.firstSeenAt !== undefined ? daysInSet(this.data.firstSeenAt, new Date()) : null,
  );

  protected readonly inSetKey = computed(() => {
    const days = this.inSetDays();
    return days === null ? null : pluralKey(days, 'usageStats.drilldown.inSetDays');
  });

  protected readonly myVoteKey = computed(() => {
    switch (this.data.vote?.myVote) {
      case VoteType.Keep:
        return 'usageStats.drilldown.myVoteKeep';
      case VoteType.Delete:
        return 'usageStats.drilldown.myVoteDelete';
      default:
        return 'usageStats.drilldown.myVoteNone';
    }
  });

  constructor() {
    this.usageStatService
      .getDailySeries(
        this.data.channelName,
        this.data.emoteId,
        this.data.from,
        this.data.to,
        this.data.emoteSetId ?? null,
      )
      .subscribe({
        next: (series) => this.series.set(series),
        error: (error: unknown) =>
          this.errorKey.set(
            error instanceof HttpErrorResponse
              ? apiErrorTranslationKey(error)
              : 'usageStats.errors.loadFailed',
          ),
      });
  }

  protected formatDate(iso: string): string {
    return new Date(iso.length === 10 ? `${iso}T00:00:00Z` : iso).toLocaleDateString(
      toLocale(this.languageService.lang()),
      { dateStyle: 'medium', timeZone: iso.length === 10 ? 'UTC' : undefined },
    );
  }

  protected formatCount(value: number): string {
    return value.toLocaleString(toLocale(this.languageService.lang()));
  }

  protected formatScore(value: number): string {
    return new Intl.NumberFormat(toLocale(this.languageService.lang()), {
      maximumFractionDigits: 0,
      signDisplay: 'exceptZero',
    }).format(value);
  }
}

export function openEmoteDrilldownDialog(dialog: Dialog, data: EmoteDrilldownData): void {
  openAppDialog<void, EmoteDrilldownData>(dialog, EmoteDrilldownDialog, { data });
}
