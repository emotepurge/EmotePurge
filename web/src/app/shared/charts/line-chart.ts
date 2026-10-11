import { Component, computed, inject, input, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslocoService } from '@jsverse/transloco';

import {
  ChartUnit,
  formatAxisTime,
  formatBucketTime,
  formatUnit,
  niceTicks,
  summarizeSeries,
  toLinePath,
  xAt,
  yAt,
} from './chart-series';

export type SeriesTone = 'neutral' | 'accent' | 'danger';

export interface LineChartSeries {
  id: string;
  /** Transloco key of the series name; the chart translates it. */
  labelKey: string;
  labelParams?: Record<string, unknown>;
  /** One value per bucket; `null` = unknown, drawn as a break in the line. */
  values: readonly (number | null)[];
  /** `line` (default) connects known neighbours; `marks` draws separate points only. */
  emphasis?: 'line' | 'marks';
  /** Defaults to `neutral`. `danger` is opt-in for real rejections or data errors only. */
  tone?: SeriesTone;
}

interface RenderedSeries {
  id: string;
  label: string;
  colorClass: string;
  tone: SeriesTone;
  dash: string | null;
  marks: boolean;
  path: string;
  partialPath: string;
  points: { x: number; y: number }[];
  lastText: string;
  alternative: string;
}

/** Fixed viewBox: the SVG scales uniformly with its container, so text never distorts. */
const VIEW_WIDTH = 640;
const VIEW_HEIGHT = 210;
const PLOT = { x: 56, y: 8, width: 576, height: 176 };

/**
 * Colour carries meaning, not identity (spec section 10): capacity curves are neutral, colour marks
 * only real errors or real limit rejections. A series is therefore `neutral` unless the caller opts
 * it into `accent` or `danger`, and series are told apart by a dash pattern that follows their
 * position, so up to eight series stay distinguishable without any hue and no meaning rides on
 * colour alone (WCAG 1.4.1). Only semantic tokens, never the raw palette.
 */
const TONE_CLASS: Record<SeriesTone, string> = {
  neutral: 'text-fg',
  accent: 'text-accent-fg',
  danger: 'text-danger-fg',
};

const DASH_PATTERNS: readonly (string | null)[] = [
  null,
  '6 3',
  '2 3',
  '8 3 2 3',
  '12 4',
  '1 4',
  '12 3 2 3 2 3',
  '4 2',
];

function styleOf(
  series: LineChartSeries,
  position: number,
): { colorClass: string; dash: string | null } {
  return {
    colorClass: TONE_CLASS[series.tone ?? 'neutral'],
    dash: DASH_PATTERNS[position % DASH_PATTERNS.length],
  };
}

/** From this many series on, a text legend (name and last value) is rendered under the chart. */
const LEGEND_FROM_SERIES = 3;

let nextChartId = 0;

/**
 * An inline-SVG line chart for any number of series sharing one unit. No chart library: the app
 * has none and one plot does not justify one.
 *
 * Reading, not hovering: there is no tooltip. The chart is a focusable group; the arrow keys (and
 * the pointer) move a reading cursor, and an `aria-live="polite"` line below the plot states the
 * bucket time and every series' value there — the same text for keyboard, pointer and screen
 * reader. The `role="img"` SVG carries the title as its name and a generated text alternative
 * (min, max, last, known buckets per series) as its description.
 *
 * Unknown values (`null`) break the line; nothing bridges or interpolates a gap. With
 * `partialLastBucket` the last bucket is still filling: it is drawn dashed with a hollow marker and
 * the text alternative says so.
 */
@Component({
  selector: 'app-line-chart',
  template: `
    <div class="space-y-2">
      <p class="text-xs text-fg-muted">{{ unitCaption() }}</p>
      <div
        class="rounded-md"
        role="group"
        tabindex="0"
        [attr.aria-label]="groupLabel()"
        (keydown)="onKeydown($event)"
        (pointermove)="onPointerMove($event)"
        (pointerleave)="onPointerLeave($event)"
        (blur)="clearCursor()"
      >
        <svg
          class="block h-auto w-full"
          [attr.viewBox]="'0 0 ' + viewWidth + ' ' + viewHeight"
          role="img"
          [attr.aria-label]="title()"
          [attr.aria-describedby]="descriptionId"
        >
          @for (tick of yTicks(); track tick.value) {
            <line
              class="text-border"
              [attr.x1]="plot.x"
              [attr.x2]="plot.x + plot.width"
              [attr.y1]="plot.y + tick.y"
              [attr.y2]="plot.y + tick.y"
              stroke="currentColor"
              stroke-width="1"
              vector-effect="non-scaling-stroke"
            />
            <text
              class="fill-fg-muted"
              font-size="11"
              text-anchor="end"
              [attr.x]="plot.x - 6"
              [attr.y]="plot.y + tick.y + 4"
            >
              {{ tick.label }}
            </text>
          }
          @for (tick of xTicks(); track tick.index) {
            <text
              class="fill-fg-muted"
              font-size="11"
              [attr.text-anchor]="tick.anchor"
              [attr.x]="plot.x + tick.x"
              [attr.y]="plot.y + plot.height + 18"
            >
              {{ tick.label }}
            </text>
          }
          <g [attr.transform]="'translate(' + plot.x + ' ' + plot.y + ')'">
            @for (item of rendered(); track item.id) {
              <g [class]="item.colorClass" [attr.data-tone]="item.tone">
                @if (!item.marks) {
                  <path
                    [attr.d]="item.path"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="1.5"
                    stroke-linejoin="round"
                    stroke-linecap="round"
                    [attr.stroke-dasharray]="item.dash"
                    vector-effect="non-scaling-stroke"
                  />
                  @if (item.partialPath) {
                    <path
                      [attr.d]="item.partialPath"
                      fill="none"
                      stroke="currentColor"
                      stroke-width="1.5"
                      stroke-linecap="round"
                      stroke-dasharray="3 3"
                      vector-effect="non-scaling-stroke"
                    />
                  }
                } @else {
                  @for (point of item.points; track point.x) {
                    <circle [attr.cx]="point.x" [attr.cy]="point.y" r="3" fill="currentColor" />
                  }
                }
              </g>
            }
            @if (partialPoints().length > 0) {
              @for (point of partialPoints(); track point.id) {
                <circle
                  [class]="point.colorClass"
                  [attr.cx]="point.x"
                  [attr.cy]="point.y"
                  r="3.5"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="1.5"
                />
              }
            }
            @if (cursorGeometry(); as cursor) {
              <line
                class="text-fg-muted"
                [attr.x1]="cursor.x"
                [attr.x2]="cursor.x"
                y1="0"
                [attr.y2]="plot.height"
                stroke="currentColor"
                stroke-width="1"
                vector-effect="non-scaling-stroke"
              />
              @for (dot of cursor.dots; track dot.id) {
                <circle
                  [class]="dot.colorClass"
                  [attr.cx]="cursor.x"
                  [attr.cy]="dot.y"
                  r="4"
                  fill="currentColor"
                />
              }
            }
          </g>
        </svg>
      </div>
      <p class="min-h-4 text-xs text-fg" aria-live="polite" data-testid="chart-cursor-line">
        {{ cursorLine() }}
      </p>
      @if (showLegend()) {
        <ul class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-fg-muted" data-testid="chart-legend">
          @for (item of rendered(); track item.id) {
            <li class="flex items-center gap-1.5">
              <svg
                [class]="item.colorClass"
                width="16"
                height="8"
                viewBox="0 0 16 8"
                aria-hidden="true"
              >
                <line
                  x1="0"
                  y1="4"
                  x2="16"
                  y2="4"
                  stroke="currentColor"
                  stroke-width="2"
                  [attr.stroke-dasharray]="item.dash"
                />
              </svg>
              <span>{{ item.label }}: {{ item.lastText }}</span>
            </li>
          }
        </ul>
      }
      <p class="sr-only" [id]="descriptionId" data-testid="chart-alternative">
        {{ textAlternative() }}
      </p>
    </div>
  `,
})
export class LineChart {
  readonly series = input.required<readonly LineChartSeries[]>();
  /** UTC start of each bucket, one per value. */
  readonly xStartsUtc = input.required<readonly string[]>();
  readonly bucketSeconds = input.required<number>();
  readonly unit = input.required<ChartUnit>();
  /** The chart's accessible name. */
  readonly title = input.required<string>();
  /** The last bucket is still filling and may be revised. */
  readonly partialLastBucket = input(false);

  private readonly transloco = inject(TranslocoService);

  protected readonly viewWidth = VIEW_WIDTH;
  protected readonly viewHeight = VIEW_HEIGHT;
  protected readonly plot = PLOT;
  protected readonly descriptionId = `app-line-chart-description-${nextChartId++}`;

  private readonly rawCursor = signal<number | null>(null);
  private readonly activeLang = toSignal(this.transloco.langChanges$, {
    initialValue: this.transloco.getActiveLang(),
  });

  private readonly bucketCount = computed(() => {
    const lengths = [this.xStartsUtc().length, ...this.series().map((s) => s.values.length)];
    return Math.max(0, ...lengths);
  });

  /** The cursor, held inside the current buckets: inputs may shrink while a cursor is set. */
  private readonly cursorIndex = computed(() => {
    const raw = this.rawCursor();
    const count = this.bucketCount();
    return raw === null || count === 0 ? null : Math.min(raw, count - 1);
  });

  private readonly yDomain = computed(() => {
    const known = this.series().flatMap((s) =>
      s.values.filter((v): v is number => v !== null && Number.isFinite(v)),
    );
    if (known.length === 0) {
      return { ticks: [0, 1], min: 0, max: 1 };
    }
    const ticks = niceTicks(
      Math.min(0, ...known),
      Math.max(...known),
      4,
      this.unit() === 'count' ? 1 : 0,
    );
    return { ticks, min: ticks[0], max: ticks[ticks.length - 1] };
  });

  protected readonly yTicks = computed(() => {
    const { ticks, min, max } = this.yDomain();
    const locale = this.locale();
    return ticks.map((value) => ({
      value,
      y: yAt(value, min, max, PLOT.height),
      label: this.format(value, locale),
    }));
  });

  protected readonly xTicks = computed(() => {
    const count = this.bucketCount();
    const starts = this.xStartsUtc();
    if (count === 0 || starts.length === 0) {
      return [];
    }
    const indices = [...new Set([0, Math.floor((count - 1) / 2), count - 1])];
    return indices
      .filter((index) => index < starts.length)
      .map((index) => ({
        index,
        x: xAt(index, count, PLOT.width),
        anchor:
          index === 0 && count > 1 ? 'start' : index === count - 1 && count > 1 ? 'end' : 'middle',
        label: formatAxisTime(starts[index], this.bucketSeconds(), this.locale()),
      }));
  });

  protected readonly rendered = computed<RenderedSeries[]>(() => {
    const { min, max } = this.yDomain();
    const count = this.bucketCount();
    const locale = this.locale();
    const geometry = { width: PLOT.width, height: PLOT.height, yMin: min, yMax: max, count };
    const partial = this.partialLastBucket();

    return this.series().map((series, position) => {
      const style = styleOf(series, position);
      const values = series.values;
      const complete = partial ? [...values.slice(0, -1), null] : values;
      const partialTail = partial
        ? values.map((value, index) => (index >= values.length - 2 ? value : null))
        : [];
      const summary = summarizeSeries(values);
      const label = this.transloco.translate(series.labelKey, series.labelParams ?? {});
      const noData = this.transloco.translate('admin.dashboard.chart.noData');

      return {
        id: series.id,
        label,
        colorClass: style.colorClass,
        tone: series.tone ?? 'neutral',
        dash: style.dash,
        marks: series.emphasis === 'marks',
        path: toLinePath(complete, geometry),
        // Dashed bridge into the still-filling bucket; only when both ends are known.
        partialPath:
          partial &&
          partialTail[partialTail.length - 2] != null &&
          partialTail[partialTail.length - 1] != null
            ? toLinePath(partialTail, geometry)
            : '',
        points: values.flatMap((value, index) =>
          value === null ||
          value === undefined ||
          !Number.isFinite(value) ||
          (partial && index === values.length - 1)
            ? []
            : [
                {
                  x: xAt(index, count, PLOT.width),
                  y: yAt(value, min, max, PLOT.height),
                },
              ],
        ),
        lastText: summary.last === null ? noData : this.format(summary.last, locale),
        alternative:
          summary.known === 0
            ? `${label}: ${noData}`
            : this.transloco.translate('admin.dashboard.chart.summary', {
                label,
                min: this.format(summary.min as number, locale),
                max: this.format(summary.max as number, locale),
                last: this.format(summary.last as number, locale),
                known: summary.known,
                total: values.length,
              }),
      };
    });
  });

  /** Hollow markers for the still-filling last bucket, one per series that has a value there. */
  protected readonly partialPoints = computed(() => {
    if (!this.partialLastBucket()) {
      return [];
    }
    const { min, max } = this.yDomain();
    const count = this.bucketCount();
    return this.series().flatMap((series, position) => {
      const value = series.values[series.values.length - 1];
      if (value === null || value === undefined) {
        return [];
      }
      return [
        {
          id: series.id,
          colorClass: styleOf(series, position).colorClass,
          x: xAt(series.values.length - 1, count, PLOT.width),
          y: yAt(value, min, max, PLOT.height),
        },
      ];
    });
  });

  protected readonly showLegend = computed(() => this.series().length >= LEGEND_FROM_SERIES);

  protected readonly unitCaption = computed(() => {
    this.activeLang();
    return `${this.transloco.translate(`admin.dashboard.chart.unit.${this.unit()}`)} · ${this.transloco.translate('admin.dashboard.chart.utc')}`;
  });

  protected readonly groupLabel = computed(() => {
    this.activeLang();
    return this.transloco.translate('admin.dashboard.chart.cursorHint', { title: this.title() });
  });

  protected readonly textAlternative = computed(() => {
    const parts = this.rendered().map((item) => item.alternative);
    if (this.partialLastBucket()) {
      parts.push(this.transloco.translate('admin.dashboard.chart.incomplete'));
    }
    return parts.join(' ');
  });

  protected readonly cursorGeometry = computed(() => {
    const index = this.cursorIndex();
    if (index === null) {
      return null;
    }
    const { min, max } = this.yDomain();
    const count = this.bucketCount();
    return {
      x: xAt(index, count, PLOT.width),
      dots: this.series().flatMap((series, position) => {
        const value = series.values[index];
        return value === null || value === undefined
          ? []
          : [
              {
                id: series.id,
                colorClass: styleOf(series, position).colorClass,
                y: yAt(value, min, max, PLOT.height),
              },
            ];
      }),
    };
  });

  /** The live-region text: bucket time, then every series' value there. Empty without a cursor. */
  protected readonly cursorLine = computed(() => {
    const index = this.cursorIndex();
    if (index === null) {
      return '';
    }
    const locale = this.locale();
    const noData = this.transloco.translate('admin.dashboard.chart.noData');
    const start = this.xStartsUtc()[index];
    const time = start === undefined ? '' : formatBucketTime(start, this.bucketSeconds(), locale);
    const values = this.series()
      .map((series) => {
        const value = series.values[index];
        const label = this.transloco.translate(series.labelKey, series.labelParams ?? {});
        return `${label}: ${value === null || value === undefined ? noData : this.format(value, locale)}`;
      })
      .join('; ');
    const incomplete =
      this.partialLastBucket() && index === this.bucketCount() - 1
        ? ` (${this.transloco.translate('admin.dashboard.chart.incompleteShort')})`
        : '';
    return `${time}${incomplete} — ${values}`;
  });

  protected onKeydown(event: KeyboardEvent): void {
    const count = this.bucketCount();
    if (count === 0) {
      return;
    }
    const current = this.cursorIndex();
    let next: number | null;

    switch (event.key) {
      case 'ArrowRight':
        next = current === null ? count - 1 : Math.min(count - 1, current + 1);
        break;
      case 'ArrowLeft':
        next = current === null ? count - 1 : Math.max(0, current - 1);
        break;
      case 'Home':
        next = 0;
        break;
      case 'End':
        next = count - 1;
        break;
      case 'Escape':
        next = null;
        break;
      default:
        return;
    }

    event.preventDefault();
    this.rawCursor.set(next);
  }

  protected onPointerMove(event: PointerEvent): void {
    const count = this.bucketCount();
    const svg = (event.currentTarget as HTMLElement).querySelector('svg');
    const rect = svg?.getBoundingClientRect();
    if (count === 0 || !rect || rect.width === 0) {
      return;
    }
    // Map the pointer into viewBox units, then into the plot area; outside it clamps to the ends.
    const viewX = ((event.clientX - rect.left) / rect.width) * VIEW_WIDTH;
    const fraction = Math.min(1, Math.max(0, (viewX - PLOT.x) / PLOT.width));
    this.rawCursor.set(count === 1 ? 0 : Math.round(fraction * (count - 1)));
  }

  protected onPointerLeave(event: PointerEvent): void {
    // Keyboard focus keeps its cursor; only a pointer-set cursor goes with the pointer.
    if (!(event.currentTarget as HTMLElement).matches(':focus')) {
      this.rawCursor.set(null);
    }
  }

  protected clearCursor(): void {
    this.rawCursor.set(null);
  }

  private format(value: number, locale: string): string {
    const unit = this.unit();
    const word =
      unit === 'cores' || unit === 'tasks'
        ? this.transloco.translate(`admin.dashboard.chart.unitWord.${unit}`)
        : '';
    return formatUnit(value, unit, locale, word);
  }

  private locale(): string {
    return this.activeLang() || 'de';
  }
}
