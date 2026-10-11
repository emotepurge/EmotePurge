import { Component, computed, inject, input, model } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { AdminDashboardActivity, AdminDashboardActivityRange } from '../../core/admin/admin.model';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { LineChart, LineChartSeries } from '../../shared/charts/line-chart';
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';
import { SkeletonSections } from '../../shared/ui/skeleton-sections';

const RANGE_OPTIONS: SegmentedControlOption[] = [
  { value: '30d', labelKey: 'admin.dashboard.activity.range.30d' },
  { value: '90d', labelKey: 'admin.dashboard.activity.range.90d' },
  { value: '13mo', labelKey: 'admin.dashboard.activity.range.13mo' },
];

const DAY_SECONDS = 86_400;

/**
 * Observed live channels per UTC day, with the current channel count as a small context value in
 * the heading. Presentational: the page owns the resource, the polling and the roster.
 */
@Component({
  selector: 'app-admin-activity-section',
  imports: [LineChart, SegmentedControl, SkeletonSections, TranslocoPipe],
  template: `
    <section class="flex flex-col gap-3 border-t border-border pt-4">
      <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-2">
        <h3 class="text-base font-semibold">
          {{ 'admin.dashboard.activity.title' | transloco }}
        </h3>
        @if (contextParams(); as params) {
          <p class="text-sm text-fg-body">
            {{ 'admin.dashboard.activity.context' | transloco: params }}
          </p>
        }
      </div>

      <app-segmented-control
        [options]="rangeOptions"
        [value]="range()"
        (valueChange)="onRangeChange($event)"
        [ariaLabel]="'admin.dashboard.activity.range.label' | transloco"
      />

      @if (activity(); as data) {
        <app-line-chart
          [series]="series()"
          [xStartsUtc]="xStartsUtc()"
          [bucketSeconds]="daySeconds"
          unit="count"
          [title]="'admin.dashboard.activity.chartTitle' | transloco"
          [partialLastBucket]="data.todayIsPartial"
        />
      } @else {
        <app-skeleton-sections [count]="1" />
      }

      <div class="flex flex-col gap-1 text-xs text-fg-muted">
        <p>{{ 'admin.dashboard.activity.help.poller' | transloco }}</p>
        <p>{{ 'admin.dashboard.activity.help.purge' | transloco }}</p>
        <p>{{ 'admin.dashboard.activity.help.today' | transloco }}</p>
        @if (contextParams()) {
          <p>{{ 'admin.dashboard.activity.help.cap' | transloco }}</p>
        }
      </div>
    </section>
  `,
})
export class AdminActivitySection {
  /** The last successfully loaded series; `undefined` only before the very first load. */
  readonly activity = input<AdminDashboardActivity | undefined>();
  readonly trackedChannelCount = input<number | null>(null);
  readonly maxActiveChannels = input<number | null>(null);
  readonly range = model.required<AdminDashboardActivityRange>();

  private readonly languageService = inject(LanguageService);

  protected readonly rangeOptions = RANGE_OPTIONS;
  protected readonly daySeconds = DAY_SECONDS;

  protected readonly xStartsUtc = computed(() =>
    (this.activity()?.days ?? []).map((day) => `${day.date}T00:00:00Z`),
  );

  protected readonly series = computed<LineChartSeries[]>(() => [
    {
      id: 'live-channels',
      labelKey: 'admin.dashboard.activity.series.liveChannels',
      values: (this.activity()?.days ?? []).map((day) => day.liveChannels),
    },
  ]);

  /** "tracked / admission limit" — absent until the roster card has loaded; no placeholder digits. */
  protected readonly contextParams = computed(() => {
    const tracked = this.trackedChannelCount();
    const max = this.maxActiveChannels();
    if (tracked === null || max === null) {
      return null;
    }
    const format = new Intl.NumberFormat(toLocale(this.languageService.lang()));
    return { tracked: format.format(tracked), max: format.format(max) };
  });

  protected onRangeChange(value: string): void {
    const option = RANGE_OPTIONS.find((o) => o.value === value);
    if (option) {
      this.range.set(option.value as AdminDashboardActivityRange);
    }
  }
}
