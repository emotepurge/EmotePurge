import {
  afterNextRender,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslocoService } from '@jsverse/transloco';

import { formatAxisTime, formatBucketTime, niceTicks, yAt } from './chart-series';

export interface HistogramProcessStart {
  source: string;
  startedUtc: string;
}

export interface HistogramSelection {
  fromUtc: string;
  toUtc: string;
}

/** See `line-chart.ts`: real pixel coordinates, a measured width and a fixed height. */
const FALLBACK_WIDTH = 640;
const PLOT_LEFT = 40;
const PLOT_RIGHT = 8;
const PLOT_TOP = 8;
const PLOT_HEIGHT = 96;
const AXIS_HEIGHT = 24;
const COMPACT_HEIGHT = 32;
const TICK_FONT_SIZE = 12;
const Y_LABEL_GAP = 6;
/** Share of a bucket's band the bar fills; the rest is the gap between bars. */
const BAR_FILL = 0.8;
const MIN_BAR_WIDTH = 1;

let nextHistogramId = 0;

/**
 * A stacked time histogram: one bar per bucket, three segments from the bottom (information,
 * warning, error and critical). Inline SVG without a library, sharing helpers with `app-line-chart`.
 *
 * Meaning never rides on colour alone: information is neutral, warning is warm and hatched, error
 * is `danger`. Like the line chart there is no tooltip: the chart is one focusable group, the
 * arrow keys (and the pointer) move a reading cursor, and an `aria-live` line states the bucket
 * time and the three counts. Enter, like a click, selects the bucket as a time range.
 */
@Component({
  selector: 'app-time-histogram',
  host: { class: 'block' },
  template: `
    <div class="min-w-0 space-y-2">
      @if (!compact()) {
        <p class="text-xs text-fg-muted">{{ caption() }}</p>
      }
      <div
        class="min-w-0 rounded-md"
        role="group"
        tabindex="0"
        [attr.aria-label]="groupLabel()"
        (keydown)="onKeydown($event)"
        (pointermove)="onPointerMove($event)"
        (pointerleave)="onPointerLeave($event)"
        (blur)="clearCursor()"
      >
        <svg
          class="block w-full"
          [attr.height]="viewHeight()"
          [attr.viewBox]="'0 0 ' + width() + ' ' + viewHeight()"
          role="img"
          [attr.aria-label]="label()"
          [attr.aria-describedby]="descriptionId"
        >
          <defs>
            <pattern
              [id]="hatchId"
              class="text-warning-fg"
              width="4"
              height="4"
              patternUnits="userSpaceOnUse"
              patternTransform="rotate(45)"
            >
              <rect width="4" height="4" fill="currentColor" fill-opacity="0.35" />
              <line x1="0" y1="0" x2="0" y2="4" stroke="currentColor" stroke-width="1.5" />
            </pattern>
          </defs>
          @if (!compact()) {
            @for (tick of yTicks(); track tick.value) {
              <line
                class="text-border"
                [attr.x1]="plot().x"
                [attr.x2]="plot().x + plot().width"
                [attr.y1]="plot().y + tick.y"
                [attr.y2]="plot().y + tick.y"
                stroke="currentColor"
                stroke-width="1"
                vector-effect="non-scaling-stroke"
              />
              <text
                class="fill-fg-muted"
                [attr.font-size]="fontSize"
                text-anchor="end"
                [attr.x]="plot().x - Y_GAP"
                [attr.y]="plot().y + tick.y + 4"
              >
                {{ tick.value }}
              </text>
            }
            @for (tick of xTicks(); track tick.key) {
              <text
                class="fill-fg-muted"
                [attr.font-size]="fontSize"
                [attr.text-anchor]="tick.anchor"
                [attr.x]="plot().x + tick.x"
                [attr.y]="plot().y + plot().height + 18"
              >
                {{ tick.label }}
              </text>
            }
          }
          @if (empty()) {
            <text
              class="fill-fg-muted"
              [attr.font-size]="fontSize"
              text-anchor="middle"
              data-testid="histogram-empty"
              [attr.x]="plot().x + plot().width / 2"
              [attr.y]="plot().y + plot().height / 2 + 4"
            >
              {{ emptyText() }}
            </text>
          }
          <g [attr.transform]="'translate(' + plot().x + ' ' + plot().y + ')'">
            @for (bar of bars(); track bar.index) {
              <g [class.opacity-60]="bar.partial" data-testid="histogram-bar">
                <rect
                  [attr.x]="bar.bandX"
                  y="0"
                  [attr.width]="bar.bandWidth"
                  [attr.height]="plot().height"
                  fill="transparent"
                  class="cursor-pointer"
                  data-testid="histogram-hit"
                  (click)="onBarClick(bar.index)"
                />
                @if (bar.info.height > 0) {
                  <rect
                    class="text-fg-muted"
                    [attr.x]="bar.x"
                    [attr.y]="bar.info.y"
                    [attr.width]="bar.width"
                    [attr.height]="bar.info.height"
                    fill="currentColor"
                    fill-opacity="0.55"
                  />
                }
                @if (bar.warn.height > 0) {
                  <rect
                    [attr.x]="bar.x"
                    [attr.y]="bar.warn.y"
                    [attr.width]="bar.width"
                    [attr.height]="bar.warn.height"
                    [attr.fill]="hatchFill"
                  />
                }
                @if (bar.error.height > 0) {
                  <rect
                    class="text-danger-fg"
                    [attr.x]="bar.x"
                    [attr.y]="bar.error.y"
                    [attr.width]="bar.width"
                    [attr.height]="bar.error.height"
                    fill="currentColor"
                  />
                }
              </g>
            }
            @for (mark of marks(); track mark.key) {
              <line
                class="text-fg"
                [attr.x1]="mark.x"
                [attr.x2]="mark.x"
                y1="0"
                [attr.y2]="plot().height"
                stroke="currentColor"
                stroke-width="1"
                stroke-dasharray="2 2"
                vector-effect="non-scaling-stroke"
                data-testid="histogram-process-start"
              />
            }
            @if (cursorGeometry(); as cursor) {
              <rect
                class="text-fg"
                [attr.x]="cursor.x"
                y="0"
                [attr.width]="cursor.width"
                [attr.height]="plot().height"
                fill="none"
                stroke="currentColor"
                stroke-width="1.5"
                vector-effect="non-scaling-stroke"
              />
            }
          </g>
        </svg>
      </div>
      <p class="min-h-4 text-xs text-fg" aria-live="polite" data-testid="histogram-cursor-line">
        {{ cursorLine() }}
      </p>
      @if (!compact()) {
        <ul
          class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-fg-muted"
          data-testid="histogram-legend"
        >
          <li class="flex items-center gap-1.5">
            <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true">
              <rect
                class="text-fg-muted"
                width="12"
                height="12"
                fill="currentColor"
                fill-opacity="0.55"
              />
            </svg>
            <span>{{ levelName('info') }}</span>
          </li>
          <li class="flex items-center gap-1.5">
            <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true">
              <rect width="12" height="12" [attr.fill]="hatchFill" />
            </svg>
            <span>{{ levelName('warn') }}</span>
          </li>
          <li class="flex items-center gap-1.5">
            <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true">
              <rect class="text-danger-fg" width="12" height="12" fill="currentColor" />
            </svg>
            <span>{{ levelName('error') }}</span>
          </li>
        </ul>
      }
      <p class="sr-only" [id]="descriptionId" data-testid="histogram-alternative">
        {{ textAlternative() }}
      </p>
    </div>
  `,
})
export class TimeHistogram {
  /** UTC start of each bucket, one per count. */
  readonly bucketStartsUtc = input.required<readonly string[]>();
  readonly info = input.required<readonly number[]>();
  readonly warn = input.required<readonly number[]>();
  /** Error and critical together. */
  readonly error = input.required<readonly number[]>();
  readonly bucketSeconds = input.required<number>();
  /** The chart's accessible name. */
  readonly label = input.required<string>();
  readonly processStarts = input<readonly HistogramProcessStart[]>([]);
  /** The last bucket is still filling; it is drawn lighter and named as incomplete. */
  readonly lastBucketPartial = input(false);
  /** A 32 px strip without axes and legend instead of the 96 px plot. */
  readonly compact = input(false);

  /** A click or Enter on a bucket: the half-open range `[fromUtc, toUtc)` of that bucket. */
  readonly bucketSelected = output<HistogramSelection>();

  private readonly transloco = inject(TranslocoService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly fontSize = TICK_FONT_SIZE;
  protected readonly Y_GAP = Y_LABEL_GAP;
  protected readonly descriptionId = `app-time-histogram-description-${nextHistogramId}`;
  protected readonly hatchId = `app-time-histogram-hatch-${nextHistogramId++}`;
  protected readonly hatchFill = `url(#${this.hatchId})`;

  protected readonly width = signal(FALLBACK_WIDTH);

  private readonly rawCursor = signal<number | null>(null);

  private readonly activeLang = toSignal(this.transloco.langChanges$, {
    initialValue: this.transloco.getActiveLang(),
  });

  private readonly locale = computed(() => this.activeLang() || 'en');

  private readonly bucketCount = computed(() =>
    Math.max(
      this.bucketStartsUtc().length,
      this.info().length,
      this.warn().length,
      this.error().length,
    ),
  );

  private readonly counts = computed(() => {
    const info = this.info();
    const warn = this.warn();
    const error = this.error();
    return Array.from({ length: this.bucketCount() }, (_, i) => ({
      info: info[i] ?? 0,
      warn: warn[i] ?? 0,
      error: error[i] ?? 0,
    }));
  });

  private readonly cursorIndex = computed(() => {
    const raw = this.rawCursor();
    const count = this.bucketCount();
    return raw === null || count === 0 ? null : Math.min(raw, count - 1);
  });

  protected readonly empty = computed(() =>
    this.counts().every((c) => c.info + c.warn + c.error === 0),
  );

  protected readonly viewHeight = computed(() =>
    this.compact() ? COMPACT_HEIGHT : PLOT_TOP + PLOT_HEIGHT + AXIS_HEIGHT,
  );

  protected readonly plot = computed(() => {
    const left = this.compact() ? PLOT_RIGHT : PLOT_LEFT;
    return {
      x: left,
      y: this.compact() ? 2 : PLOT_TOP,
      width: Math.max(40, this.width() - left - PLOT_RIGHT),
      height: this.compact() ? COMPACT_HEIGHT - 4 : PLOT_HEIGHT,
    };
  });

  /** Whole-number ticks only; a stack of 0-3 entries never gets a 1.5. */
  private readonly yDomain = computed(() => {
    const max = Math.max(0, ...this.counts().map((c) => c.info + c.warn + c.error));
    const ticks = max === 0 ? [0, 1] : niceTicks(0, max, 4, 1);
    return { ticks, max: ticks[ticks.length - 1] };
  });

  protected readonly yTicks = computed(() => {
    const { ticks, max } = this.yDomain();
    return ticks.map((value) => ({ value, y: yAt(value, 0, max, this.plot().height) }));
  });

  protected readonly xTicks = computed(() => {
    const starts = this.bucketStartsUtc();
    const count = starts.length;
    if (count === 0) {
      return [];
    }
    const range = this.rangeMs();
    if (range === null) {
      return [];
    }
    const bucketSeconds = this.bucketSeconds();
    const locale = this.locale();
    const at = (ms: number) => new Date(ms).toISOString();
    const width = this.plot().width;
    return [
      { key: 'start', anchor: 'start', ms: range.start },
      { key: 'middle', anchor: 'middle', ms: range.start + (range.end - range.start) / 2 },
      { key: 'end', anchor: 'end', ms: range.end },
    ].map((tick) => ({
      key: tick.key,
      anchor: tick.anchor,
      x: ((tick.ms - range.start) / (range.end - range.start)) * width,
      label: formatAxisTime(at(tick.ms), bucketSeconds, locale),
    }));
  });

  protected readonly bars = computed(() => {
    const plot = this.plot();
    const count = this.bucketCount();
    const { max } = this.yDomain();
    const band = plot.width / Math.max(count, 1);
    const barWidth = Math.max(MIN_BAR_WIDTH, band * BAR_FILL);
    const lastIndex = count - 1;
    const segment = (from: number, value: number) => {
      const top = yAt(from + value, 0, max, plot.height);
      const bottom = yAt(from, 0, max, plot.height);
      return { y: top, height: value > 0 ? Math.max(1, bottom - top) : 0 };
    };
    return this.counts().map((c, index) => ({
      index,
      bandX: index * band,
      bandWidth: band,
      x: index * band + (band - barWidth) / 2,
      width: barWidth,
      partial: this.lastBucketPartial() && index === lastIndex,
      info: segment(0, c.info),
      warn: segment(c.info, c.warn),
      error: segment(c.info + c.warn, c.error),
    }));
  });

  /** The covered time range: first bucket start to the end of the last bucket. */
  private readonly rangeMs = computed(() => {
    const starts = this.bucketStartsUtc();
    if (starts.length === 0) {
      return null;
    }
    const start = Date.parse(starts[0]);
    const lastStart = Date.parse(starts[starts.length - 1]);
    if (!Number.isFinite(start) || !Number.isFinite(lastStart)) {
      return null;
    }
    return { start, end: lastStart + this.bucketSeconds() * 1000 };
  });

  protected readonly marks = computed(() => {
    const range = this.rangeMs();
    if (range === null) {
      return [];
    }
    const width = this.plot().width;
    return this.processStarts().flatMap((start, index) => {
      const ms = Date.parse(start.startedUtc);
      if (this.bucketOf(start.startedUtc) < 0) {
        return [];
      }
      return [
        {
          key: `${start.source}-${start.startedUtc}-${index}`,
          x: ((ms - range.start) / (range.end - range.start)) * width,
        },
      ];
    });
  });

  protected readonly caption = computed(() => {
    this.activeLang();
    return `${this.transloco.translate('admin.logs.histogram.unit')} · ${this.transloco.translate('admin.logs.histogram.utc')}`;
  });

  protected readonly groupLabel = computed(() => {
    this.activeLang();
    return this.transloco.translate('admin.logs.histogram.cursorHint', { title: this.label() });
  });

  protected readonly emptyText = computed(() => {
    this.activeLang();
    return this.transloco.translate('admin.logs.histogram.empty');
  });

  protected readonly cursorGeometry = computed(() => {
    const index = this.cursorIndex();
    if (index === null) {
      return null;
    }
    const bar = this.bars()[index];
    return bar ? { x: bar.x, width: bar.width } : null;
  });

  protected readonly textAlternative = computed(() => {
    this.activeLang();
    const counts = this.counts();
    const totals = counts.map((c) => c.info + c.warn + c.error);
    const text = this.transloco.translate('admin.logs.histogram.summary', {
      total: totals.reduce((a, b) => a + b, 0),
      max: Math.max(0, ...totals),
      buckets: counts.length,
      warnBuckets: counts.filter((c) => c.warn > 0).length,
      errorBuckets: counts.filter((c) => c.error > 0).length,
    });
    return this.lastBucketPartial()
      ? `${text} ${this.transloco.translate('admin.logs.histogram.partial')}`
      : text;
  });

  /** The live-region text: bucket time, the three counts, process starts inside the bucket. */
  protected readonly cursorLine = computed(() => {
    this.activeLang();
    const index = this.cursorIndex();
    if (index === null) {
      return '';
    }
    const c = this.counts()[index];
    const start = this.bucketStartsUtc()[index];
    const time =
      start === undefined ? '' : formatBucketTime(start, this.bucketSeconds(), this.locale());
    const incomplete =
      this.lastBucketPartial() && index === this.bucketCount() - 1
        ? ` (${this.transloco.translate('admin.logs.histogram.partialShort')})`
        : '';
    let text = `${time}${incomplete} — ${this.transloco.translate('admin.logs.histogram.reading', c)}`;
    const starts = this.processStarts()
      .filter((s) => this.bucketOf(s.startedUtc) === index)
      .map((s) =>
        this.transloco.translate('admin.logs.histogram.processStart', {
          source: s.source,
          time: formatBucketTime(s.startedUtc, 1, this.locale()),
        }),
      );
    if (starts.length > 0) {
      text += ` · ${starts.join('; ')}`;
    }
    return text;
  });

  constructor() {
    afterNextRender(() => {
      const element = this.host.nativeElement;
      const measure = () => {
        const measured = Math.floor(element.getBoundingClientRect().width);
        if (measured > 0) {
          this.width.set(measured);
        }
      };
      measure();
      if (typeof ResizeObserver === 'undefined') {
        return;
      }
      const observer = new ResizeObserver(measure);
      observer.observe(element);
      this.destroyRef.onDestroy(() => observer.disconnect());
    });
  }

  protected levelName(level: 'info' | 'warn' | 'error'): string {
    this.activeLang();
    return this.transloco.translate(`admin.logs.histogram.level.${level}`);
  }

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
      case 'Enter':
        if (current !== null) {
          event.preventDefault();
          this.select(current);
        }
        return;
      default:
        return;
    }

    event.preventDefault();
    this.rawCursor.set(next);
  }

  protected onPointerMove(event: PointerEvent): void {
    const index = this.indexAt(event);
    if (index !== null) {
      this.rawCursor.set(index);
    }
  }

  protected onPointerLeave(event: PointerEvent): void {
    // Keyboard focus keeps its cursor; only a pointer-set cursor goes with the pointer.
    if (!(event.currentTarget as HTMLElement).matches(':focus')) {
      this.rawCursor.set(null);
    }
  }

  protected onBarClick(index: number): void {
    this.rawCursor.set(index);
    this.select(index);
  }

  protected clearCursor(): void {
    this.rawCursor.set(null);
  }

  private select(index: number): void {
    const start = this.bucketStartsUtc()[index];
    const startMs = Date.parse(start ?? '');
    if (start === undefined || !Number.isFinite(startMs)) {
      return;
    }
    const end = new Date(startMs + this.bucketSeconds() * 1000).toISOString();
    this.bucketSelected.emit({ fromUtc: start, toUtc: end.replace('.000Z', 'Z') });
  }

  /** Bucket index a process start falls into, or -1 outside the range. */
  private bucketOf(startedUtc: string): number {
    const range = this.rangeMs();
    const ms = Date.parse(startedUtc);
    if (range === null || !Number.isFinite(ms) || ms < range.start || ms >= range.end) {
      return -1;
    }
    return Math.min(
      this.bucketStartsUtc().length - 1,
      Math.floor((ms - range.start) / (this.bucketSeconds() * 1000)),
    );
  }

  /** The bucket under the pointer, or null without a measurable plot; outside it clamps to the ends. */
  private indexAt(event: PointerEvent): number | null {
    const count = this.bucketCount();
    const svg = (event.currentTarget as HTMLElement).querySelector('svg');
    const rect = svg?.getBoundingClientRect();
    if (count === 0 || !rect || rect.width === 0) {
      return null;
    }
    const plot = this.plot();
    const viewX = ((event.clientX - rect.left) / rect.width) * this.width();
    const fraction = Math.min(0.999999, Math.max(0, (viewX - plot.x) / plot.width));
    return Math.floor(fraction * count);
  }
}
