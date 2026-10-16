import { Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';

import { AdminLogsService } from '../../core/admin/admin-logs.service';
import {
  LogEntry,
  LogEntryDetail,
  LogLevelKey,
  LogProcessStart,
  LogPropertyValue,
} from '../../core/admin/admin-logs.model';
import { filterTerm } from '../../core/admin/log-filter-text';
import { LOG_LEVEL_NAMES } from './admin-log-facets';

/** Keys the filter language reads itself — a property of the same name cannot be addressed. */
const RESERVED_KEYS = new Set(['level', 'source', 'cat', 'event', 'instance', 'uptime', 'has']);
const PROPERTY_KEY = /^[A-Za-z][A-Za-z0-9_.]*$/;

const LEVEL_TONES: Record<LogLevelKey, string> = {
  info: 'text-fg-muted',
  warn: 'text-warning-fg',
  error: 'font-medium text-danger-fg',
  crit: 'font-medium text-danger-fg',
};

/** `HH:mm:ss.fff` of an ISO instant, cut from the string — the wire form is already UTC. */
export function formatLogTime(timestampUtc: string): string {
  const match = /T(\d{2}:\d{2}:\d{2})(?:\.(\d+))?/.exec(timestampUtc);
  if (!match) {
    return timestampUtc;
  }
  return `${match[1]}.${(match[2] ?? '').padEnd(3, '0').slice(0, 3)}`;
}

/** The UTC calendar day (`yyyy-MM-dd`) of an ISO instant. */
export function logDay(timestampUtc: string): string {
  return timestampUtc.slice(0, 10);
}

/** The last segment of a category name. */
export function shortCategory(category: string): string {
  return category.slice(category.lastIndexOf('.') + 1);
}

/**
 * One `docker logs`-style line plus the exception, which is what an admin pastes into a chat:
 * `2026-10-14T12:00:01.123Z warn worker EmotePurge.Worker.X: message`.
 */
export function formatLogLine(entry: LogEntry, exception?: string | null): string {
  const head = `${entry.timestampUtc} ${entry.level} ${entry.source} ${entry.category}: ${entry.message}`;
  return exception ? `${head}\n${exception}` : head;
}

/** A short uptime such as `45 s`, `12 min`, `3 h 5 min` or `2 d 4 h` — units are language-neutral. */
export function formatUptime(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  if (s < 60) {
    return `${s} s`;
  }
  const minutes = Math.floor(s / 60);
  if (minutes < 60) {
    return `${minutes} min`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours} h ${minutes % 60} min`;
  }
  return `${Math.floor(hours / 24)} d ${hours % 24} h`;
}

function propertyText(value: LogPropertyValue): string {
  return typeof value === 'string' ? value : JSON.stringify(value);
}

interface PropertyRow {
  key: string;
  text: string;
  /** Terms to append, or null when the property cannot be addressed by the filter language. */
  include: string | null;
  exclude: string | null;
}

interface ListRow {
  entry: LogEntry;
  /** The UTC day when it differs from the previous row's — a separator goes above. */
  dayBreak: string | null;
}

type DetailState =
  { status: 'loading' } | { status: 'loaded'; detail: LogEntryDetail } | { status: 'failed' };

/**
 * The log list: dense one-line rows, a day separator when the UTC date changes, and a disclosure per
 * row with the details. Properties offer "filter"/"exclude"; the exception is fetched lazily when a
 * row with one is opened. Only presentation and the row-local state live here — loading and the
 * filter belong to the page, which receives terms through {@link termAdded}.
 */
@Component({
  selector: 'app-admin-log-list',
  imports: [TranslocoPipe],
  template: `
    <ul class="-mx-3 divide-y divide-border border-y border-border" data-testid="log-list">
      @for (row of rows(); track row.entry.id) {
        @if (row.dayBreak; as day) {
          <li
            class="bg-surface-inset px-3 py-1 text-xs font-medium text-fg-muted tabular-nums"
            data-testid="log-day"
          >
            {{ day }}
          </li>
        }
        <li
          class="text-sm"
          [class.log-row-new]="highlighted().has(row.entry.id)"
          [attr.data-log-id]="row.entry.id"
          [attr.data-log-new]="highlighted().has(row.entry.id) ? '' : null"
        >
          <button
            type="button"
            class="flex min-h-11 w-full items-baseline gap-2 px-3 py-1.5 text-left hover:bg-surface-inset sm:min-h-8"
            [attr.aria-expanded]="isOpen(row.entry.id)"
            [attr.aria-controls]="detailsId(row.entry.id)"
            (click)="toggle(row.entry)"
          >
            <span class="shrink-0 font-mono text-xs text-fg-muted tabular-nums">{{
              time(row.entry)
            }}</span>
            <span class="w-8 shrink-0 font-mono text-xs" [class]="tone(row.entry)">
              <span aria-hidden="true">{{
                'admin.logs.list.levelShort.' + row.entry.level | transloco
              }}</span>
              <span class="sr-only">{{ levelName(row.entry.level) }}</span>
            </span>
            <span class="hidden w-14 shrink-0 text-xs text-fg-muted sm:inline">{{
              row.entry.source
            }}</span>
            <span class="hidden w-44 shrink-0 truncate text-xs text-fg-secondary sm:inline">{{
              short(row.entry.category)
            }}</span>
            <span class="min-w-0 flex-1 truncate text-fg-body">{{ row.entry.message }}</span>
          </button>
          @if (isOpen(row.entry.id)) {
            <div
              [id]="detailsId(row.entry.id)"
              class="flex flex-col gap-3 border-t border-border bg-surface-inset px-3 py-3"
            >
              <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
                <dt class="text-fg-muted">{{ 'admin.logs.row.timestamp' | transloco }}</dt>
                <dd class="font-mono break-words text-fg-body">{{ row.entry.timestampUtc }}</dd>
                <dt class="text-fg-muted">{{ 'admin.logs.row.source' | transloco }}</dt>
                <dd class="text-fg-body">{{ row.entry.source }}</dd>
                <dt class="text-fg-muted">{{ 'admin.logs.row.category' | transloco }}</dt>
                <dd class="break-words text-fg-body">{{ row.entry.category }}</dd>
                @if (row.entry.eventName || row.entry.eventId !== null) {
                  <dt class="text-fg-muted">{{ 'admin.logs.row.event' | transloco }}</dt>
                  <dd class="break-words text-fg-body">
                    {{ row.entry.eventName ?? ''
                    }}{{ row.entry.eventId !== null ? ' (' + row.entry.eventId + ')' : '' }}
                  </dd>
                }
                <dt class="text-fg-muted">{{ 'admin.logs.row.instance' | transloco }}</dt>
                <dd class="text-fg-body">
                  <span class="font-mono">{{ row.entry.instanceId }}</span>
                  @if (uptimeOf(row.entry); as uptime) {
                    ·
                    {{ 'admin.logs.row.uptime' | transloco: { value: uptime } }}
                  }
                </dd>
              </dl>

              <p class="text-sm break-words whitespace-pre-wrap text-fg-body">
                {{ row.entry.message }}
              </p>
              @if (row.entry.truncated) {
                <p class="text-xs text-fg-muted">{{ 'admin.logs.row.truncated' | transloco }}</p>
              }

              @if (propertyRows(row.entry); as props) {
                @if (props.length > 0) {
                  <div class="flex flex-col gap-1">
                    <h4 class="text-xs font-medium text-fg-muted">
                      {{ 'admin.logs.row.properties' | transloco }}
                    </h4>
                    <dl class="flex flex-col gap-1 text-xs">
                      @for (prop of props; track prop.key) {
                        <div class="flex flex-wrap items-baseline gap-x-2">
                          <dt class="font-mono text-fg-muted">{{ prop.key }}</dt>
                          <dd class="min-w-0 font-mono break-words text-fg-body">
                            {{ prop.text }}
                          </dd>
                          @if (prop.include && prop.exclude) {
                            <dd class="flex gap-2">
                              <button
                                type="button"
                                class="text-accent-fg underline-offset-2 hover:underline"
                                [attr.aria-label]="
                                  'admin.logs.row.filterProperty'
                                    | transloco: { key: prop.key, value: prop.text }
                                "
                                (click)="termAdded.emit(prop.include)"
                              >
                                {{ 'admin.logs.row.filter' | transloco }}
                              </button>
                              <button
                                type="button"
                                class="text-accent-fg underline-offset-2 hover:underline"
                                [attr.aria-label]="
                                  'admin.logs.row.excludeProperty'
                                    | transloco: { key: prop.key, value: prop.text }
                                "
                                (click)="termAdded.emit(prop.exclude)"
                              >
                                {{ 'admin.logs.row.exclude' | transloco }}
                              </button>
                            </dd>
                          }
                        </div>
                      }
                    </dl>
                  </div>
                }
              }

              @if (row.entry.hasException) {
                <div class="flex flex-col gap-1">
                  <h4 class="text-xs font-medium text-fg-muted">
                    {{ 'admin.logs.row.exception' | transloco }}
                  </h4>
                  @switch (detailOf(row.entry.id)?.status) {
                    @case ('loaded') {
                      <pre
                        class="max-h-96 overflow-auto rounded bg-surface p-2 font-mono text-xs break-words whitespace-pre-wrap text-fg-body"
                        >{{ exceptionText(row.entry.id) }}</pre>
                    }
                    @case ('failed') {
                      <p class="text-xs text-danger-fg">
                        {{ 'admin.logs.row.exceptionFailed' | transloco }}
                      </p>
                    }
                    @default {
                      <div
                        role="status"
                        [attr.aria-label]="'admin.logs.row.exceptionLoading' | transloco"
                      >
                        <div class="app-skeleton h-16 w-full" aria-hidden="true"></div>
                      </div>
                    }
                  }
                </div>
              }

              <div class="flex items-center gap-3">
                <button
                  type="button"
                  class="min-h-9 rounded border border-border-strong px-3 text-xs text-fg-body hover:bg-surface"
                  (click)="copy(row.entry)"
                >
                  {{ 'admin.logs.row.copy' | transloco }}
                </button>
                @if (copiedId() === row.entry.id) {
                  <span class="text-xs text-fg-muted" role="status">{{
                    'admin.logs.row.copied' | transloco
                  }}</span>
                }
              </div>
            </div>
          }
        </li>
      }
    </ul>
  `,
  // The live insert's accent wash fades over ~2 s; under reduced motion the row is simply marked for
  // those 2 s (spec §9.4). The page removes the class afterwards.
  styles: `
    .log-row-new {
      animation: log-row-wash 2s ease-out forwards;
    }
    @keyframes log-row-wash {
      from {
        background-color: var(--color-accent-wash);
      }
      to {
        background-color: transparent;
      }
    }
    @media (prefers-reduced-motion: reduce) {
      .log-row-new {
        animation: none;
        background-color: var(--color-accent-wash);
      }
    }
  `,
})
export class AdminLogList {
  private readonly service = inject(AdminLogsService);
  private readonly destroyRef = inject(DestroyRef);

  readonly entries = input.required<readonly LogEntry[]>();
  /** Rows the live mode just inserted; they carry the fading accent wash. */
  readonly highlighted = input<ReadonlySet<number>>(new Set());
  /** The summary's process starts, to word "n min nach Start" without asking for the detail. */
  readonly processStarts = input<readonly LogProcessStart[]>([]);

  /** A complete filter term (`key:value` or `-key:value`) the reader asked to append. */
  readonly termAdded = output<string>();

  protected readonly details = signal<ReadonlyMap<number, DetailState>>(new Map());
  protected readonly copiedId = signal<number | null>(null);

  private readonly open = signal<ReadonlySet<number>>(new Set());
  private copiedTimer?: ReturnType<typeof setTimeout>;

  /** True while any row is unfolded — the live mode must not insert rows then (spec §9.4). */
  readonly hasExpanded = computed(() => this.open().size > 0);

  protected readonly rows = computed<ListRow[]>(() => {
    let previousDay: string | null = null;
    return this.entries().map((entry) => {
      const day = logDay(entry.timestampUtc);
      const dayBreak = previousDay !== null && day !== previousDay ? day : null;
      previousDay = day;
      return { entry, dayBreak };
    });
  });

  protected time(entry: LogEntry): string {
    return formatLogTime(entry.timestampUtc);
  }

  protected short(category: string): string {
    return shortCategory(category);
  }

  protected tone(entry: LogEntry): string {
    return LEVEL_TONES[entry.level];
  }

  protected levelName(level: LogLevelKey): string {
    return LOG_LEVEL_NAMES[level];
  }

  protected isOpen(id: number): boolean {
    return this.open().has(id);
  }

  protected detailsId(id: number): string {
    return `log-details-${id}`;
  }

  protected detailOf(id: number): DetailState | undefined {
    return this.details().get(id);
  }

  protected exceptionText(id: number): string {
    const state = this.details().get(id);
    return state?.status === 'loaded' ? (state.detail.exception ?? '') : '';
  }

  protected uptimeOf(entry: LogEntry): string | null {
    const detail = this.details().get(entry.id);
    if (detail?.status === 'loaded') {
      return formatUptime(detail.detail.uptimeSeconds);
    }
    const start = this.processStarts().find((p) => p.instanceId === entry.instanceId);
    if (!start) {
      return null;
    }
    const seconds = (Date.parse(entry.timestampUtc) - Date.parse(start.startedUtc)) / 1000;
    return Number.isFinite(seconds) ? formatUptime(seconds) : null;
  }

  protected propertyRows(entry: LogEntry): PropertyRow[] {
    return Object.entries(entry.properties).map(([key, value]) => {
      const text = propertyText(value);
      const addressable =
        PROPERTY_KEY.test(key) &&
        !RESERVED_KEYS.has(key.toLowerCase()) &&
        value !== null &&
        typeof value !== 'object';
      return {
        key,
        text,
        include: addressable ? filterTerm(key.toLowerCase(), text, false) : null,
        exclude: addressable ? filterTerm(key.toLowerCase(), text, true) : null,
      };
    });
  }

  protected toggle(entry: LogEntry): void {
    const next = new Set(this.open());
    if (next.has(entry.id)) {
      next.delete(entry.id);
    } else {
      next.add(entry.id);
      this.loadDetail(entry);
    }
    this.open.set(next);
  }

  protected copy(entry: LogEntry): void {
    const state = this.details().get(entry.id);
    const exception = state?.status === 'loaded' ? state.detail.exception : null;
    void navigator.clipboard?.writeText(formatLogLine(entry, exception)).then(() => {
      this.copiedId.set(entry.id);
      clearTimeout(this.copiedTimer);
      this.copiedTimer = setTimeout(() => this.copiedId.set(null), 2000);
    });
  }

  /** Only a row with an exception needs the single-entry call; the rest of the details are in the list. */
  private loadDetail(entry: LogEntry): void {
    if (!entry.hasException || this.details().has(entry.id)) {
      return;
    }
    this.setDetail(entry.id, { status: 'loading' });
    this.service
      .getEntry(entry.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => this.setDetail(entry.id, { status: 'loaded', detail }),
        error: () => this.setDetail(entry.id, { status: 'failed' }),
      });
  }

  private setDetail(id: number, state: DetailState): void {
    this.details.update((current) => new Map(current).set(id, state));
  }
}
