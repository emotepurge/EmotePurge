import {
  afterRenderEffect,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import type { Wish } from '../../core/wishes/wish.model';
import {
  endedDate,
  isDue,
  isProbation,
  isSevenTvAction,
  remainingTime,
  timeInSet,
  WishAction,
  wishActions,
} from '../../core/wishes/wish-view';
import { emotePageUrl } from '../../shared/emotes/emote-url';
import { UsageSparkline } from '../../shared/emotes/usage-sparkline';
import { Button } from '../../shared/ui/button';
import { StatusBadge, StatusBadgeTone } from '../../shared/ui/status-badge';
import { WishImage } from './wish-image';
import { usageSeries, usageWindowLabel, WishUsageBlock } from './wish-usage';

/** The thirteen statuses of spec 5 that have a name; anything else reads as "unknown". */
const KNOWN_STATUSES: ReadonlySet<string> = new Set([
  'pending',
  'approved',
  'unconfirmed',
  'failed',
  'active',
  'presented',
  'rejected',
  'expired',
  'removed',
  'removed_externally',
  'kept',
  'closed',
  'merged',
]);

/** The reasons K3 explains in words; every other one renders the neutral line (plan 2.3, 2.10). */
const EXPLAINED_REASONS: ReadonlySet<string> = new Set(['expired', 'unconfirmed_start']);

const KNOWN_SOURCES: ReadonlySet<string> = new Set(['irc', 'manual', 'adopted']);

/** Actions that destroy or end without keeping; they stand last, after a gap (§8.7). */
const DESTRUCTIVE_ACTIONS: ReadonlySet<WishAction> = new Set<WishAction>(['remove', 'close']);

interface TextPart {
  key: string;
  params?: Record<string, string | number>;
}

/**
 * One wish as a ruled row (#485, plan T4): picture, alias, status, deadlines and the actions of the
 * action table. The row decides nothing beyond the rules of `wish-view.ts` and calls no service: a
 * chosen action goes out as `action`, the page carries it out.
 *
 * Which actions it renders is the table's list cut down to what the page `offers`, so a task that
 * has not built an action yet renders no button that would pretend to do it. The 7TV actions vanish
 * entirely on a coarse pointer (plan 2.7, no handler, no ARIA); while a 7TV run holds the start
 * (`sevenTvLocked`) they stay focusable but `aria-disabled` and describe themselves through the
 * page's one status line (`lockDescriptionId`).
 */
@Component({
  selector: 'app-wish-row',
  imports: [Button, StatusBadge, TranslocoPipe, UsageSparkline, WishImage],
  template: `
    <div #root class="flex flex-col gap-3 sm:flex-row sm:items-start">
      <div class="flex min-w-0 flex-1 items-start gap-3">
        <app-wish-image
          [imageUrl]="wish().imageUrl"
          [sevenTvEmoteId]="wish().sevenTvEmoteId"
          [knownStill]="knownStill()"
        />
        <div class="flex min-w-0 flex-1 flex-col gap-1">
          <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
            <span [id]="titleId()" class="max-w-full truncate font-medium">{{ name() }}</span>
            <app-status-badge [tone]="statusTone()">{{ statusKey() | transloco }}</app-status-badge>
            @if (probation()) {
              <app-status-badge tone="accent">{{
                'wishes.row.probation' | transloco
              }}</app-status-badge>
            }
            @if (remaining(); as part) {
              <span class="text-sm text-fg-secondary">{{ part.key | transloco: part.params }}</span>
            }
          </div>
          @if (reasonKey(); as key) {
            <p class="text-sm text-fg-body">{{ key | transloco }}</p>
          }
          <p class="flex flex-wrap gap-x-3 text-xs text-fg-muted">
            @for (part of details(); track part.key) {
              <span>{{ part.key | transloco: part.params }}</span>
            }
          </p>
          @if (usageView(); as view) {
            <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
              @if (view.series; as points) {
                <div class="h-8 w-28 shrink-0">
                  <app-usage-sparkline
                    [points]="points"
                    [ariaLabel]="
                      ('wishes.usage.sparkline' | transloco) +
                      ' ' +
                      (view.window.key | transloco: view.window.params)
                    "
                  />
                </div>
              }
              <p class="flex flex-wrap gap-x-3 text-xs text-fg-secondary">
                @for (part of view.parts; track part.key) {
                  <span>{{ part.key | transloco: part.params }}</span>
                }
              </p>
            </div>
            @if (view.gap) {
              <p class="text-xs text-fg-muted">{{ 'wishes.usage.gap' | transloco }}</p>
            }
          } @else if (usage()?.state === 'limited') {
            <p class="text-xs text-fg-muted">{{ 'wishes.usage.limited' | transloco }}</p>
          }
          @if (sevenTvHint()) {
            <p class="text-xs text-fg-secondary">{{ 'wishes.row.sevenTvHint' | transloco }}</p>
          }
          <a
            [href]="pageUrl()"
            target="_blank"
            rel="noopener"
            class="self-start text-sm text-fg-secondary underline decoration-border-strong underline-offset-2 transition hover:text-fg hover:decoration-accent"
            >{{ 'wishes.row.openOnSevenTv' | transloco
            }}<span class="sr-only"> {{ 'common.opensInNewTab' | transloco }}</span></a
          >
        </div>
      </div>

      @if (constructive().length > 0 || destructive().length > 0) {
        <div
          role="group"
          [attr.aria-labelledby]="titleId()"
          class="flex flex-wrap items-center gap-2 sm:ml-auto sm:justify-end"
        >
          @for (action of constructive(); track action) {
            <button
              type="button"
              appButton="outline"
              [disabled]="busy()"
              [attr.aria-disabled]="locked(action) ? 'true' : null"
              [attr.aria-describedby]="locked(action) ? lockDescriptionId() : null"
              (click)="choose(action)"
            >
              {{ actionKey(action) | transloco }}
            </button>
          }
          @if (destructive().length > 0) {
            <!-- The gap before the destructive group is the §8.7 order rule, not decoration. -->
            <span class="flex flex-wrap gap-2" [class.ml-3]="constructive().length > 0">
              @for (action of destructive(); track action) {
                <button
                  type="button"
                  appButton="danger-quiet"
                  [disabled]="busy()"
                  [attr.aria-disabled]="locked(action) ? 'true' : null"
                  [attr.aria-describedby]="locked(action) ? lockDescriptionId() : null"
                  (click)="choose(action)"
                >
                  {{ actionKey(action) | transloco }}
                </button>
              }
            </span>
          }
        </div>
      }
    </div>
  `,
})
export class WishRow {
  readonly wish = input.required<Wish>();
  /** The page's minute clock; the row never reads the time itself. */
  readonly now = input.required<Date>();
  /** The actions the page can carry out today; the table's others are not rendered. */
  readonly offered = input.required<ReadonlySet<WishAction>>();
  /** Coarse pointer: no 7TV action at all, and the 7TV hint also on due rows (plan 2.7). */
  readonly coarse = input(false);
  /** A 7TV run holds the start: the 7TV actions are `aria-disabled` and click to nothing. */
  readonly sevenTvLocked = input(false);
  /** Id of the page's status line that names the blocking run. */
  readonly lockDescriptionId = input<string | null>(null);
  /** The page is carrying out an action of this row: every button waits (§6.1). */
  readonly busy = input(false);
  /** Passed on to the picture rule (plan 2.10); set from the 7TV read of this session (T6). */
  readonly knownStill = input(false);

  /**
   * The usage of this wish's window, handed in by the page (T5); `null` shows nothing. The row
   * decides nothing about it: numbers and line come from the one `usage` object (Sol-P2-11).
   */
  readonly usage = input<WishUsageBlock | null>(null);
  /** The page wants to know when this row is in view (usage loads only then). */
  readonly trackUsage = input(false);

  readonly action = output<WishAction>();
  /** Whether the row is in the viewport; only while `trackUsage` is set, and only on change. */
  readonly seen = output<boolean>();

  private readonly languageService = inject(LanguageService);

  private readonly root = viewChild.required<ElementRef<HTMLElement>>('root');

  protected readonly titleId = computed(() => `wish-title-${this.wish().id}`);

  /** Only the alias names a wish (plan 2.8 c); the emote name is a fallback for an empty alias. */
  protected readonly name = computed(() => this.wish().alias || this.wish().emoteName);

  protected readonly pageUrl = computed(() => emotePageUrl(this.wish().sevenTvEmoteId));

  protected readonly statusKey = computed(() =>
    KNOWN_STATUSES.has(this.wish().status)
      ? `wishes.status.${this.wish().status}`
      : 'wishes.status.unknown',
  );

  /** "Notable how often?" (§4.3): only the two states that ask for a decision get a colour. */
  protected readonly statusTone = computed<StatusBadgeTone>(() => {
    switch (this.wish().status) {
      case 'presented':
        return 'warning';
      case 'failed':
        return 'danger';
      default:
        return 'neutral';
    }
  });

  protected readonly probation = computed(() => isProbation(this.wish(), this.now()));

  protected readonly reasonKey = computed(() => {
    const { status, statusReason } = this.wish();
    if ((status !== 'presented' && status !== 'failed') || statusReason === null) {
      return null;
    }
    return EXPLAINED_REASONS.has(statusReason)
      ? `wishes.reason.${statusReason}`
      : 'wishes.reason.other';
  });

  protected readonly remaining = computed<TextPart | null>(() => {
    const wish = this.wish();
    if ((wish.status !== 'active' && wish.status !== 'presented') || wish.permanent) {
      return null;
    }
    const left = remainingTime(wish.expiresAtUtc, this.now());
    switch (left?.kind) {
      case 'days':
        return { key: pluralKey(left.count, 'wishes.row.remaining.days'), params: left };
      case 'hours':
        return { key: pluralKey(left.count, 'wishes.row.remaining.hours'), params: left };
      case 'expired_days':
        return { key: pluralKey(left.count, 'wishes.row.remaining.expiredDays'), params: left };
      case 'expired_today':
        return { key: 'wishes.row.remaining.expiredToday' };
      default:
        return null;
    }
  });

  protected readonly usageView = computed(() => {
    const block = this.usage();
    const usage = block?.state === 'ready' ? block.usage : null;
    if (!usage) {
      return null;
    }
    const locale = toLocale(this.languageService.lang());
    const window = usageWindowLabel(usage, this.wish().startsAtUtc);
    const windowPart: TextPart =
      window.kind === 'sinceStart'
        ? { key: 'wishes.usage.window.sinceStart' }
        : {
            key: pluralKey(window.days, 'wishes.usage.window.lastDays'),
            params: { count: window.days },
          };
    const parts: TextPart[] = [
      {
        key: pluralKey(usage.total, 'wishes.usage.total'),
        params: { count: usage.total.toLocaleString(locale) },
      },
      {
        key: 'wishes.usage.mean',
        params: { mean: usage.perDayMean.toLocaleString(locale, { maximumFractionDigits: 1 }) },
      },
    ];
    if (usage.rank !== null && usage.rankOf !== null) {
      parts.push({ key: 'wishes.usage.rank', params: { rank: usage.rank, of: usage.rankOf } });
    }
    parts.push(windowPart);
    return {
      parts,
      series: usageSeries(usage),
      gap: usage.coverageGap,
      window: windowPart,
    };
  });

  protected readonly details = computed<TextPart[]>(() => {
    const wish = this.wish();
    const locale = toLocale(this.languageService.lang());
    const parts: TextPart[] = [];
    if (wish.requesterLogin) {
      parts.push({ key: 'wishes.row.requester', params: { login: wish.requesterLogin } });
    }
    if (KNOWN_SOURCES.has(wish.source)) {
      parts.push({ key: `wishes.source.${wish.source}` });
    }
    parts.push(
      wish.permanent
        ? { key: 'wishes.row.permanent' }
        : {
            key: pluralKey(wish.durationDays, 'wishes.row.duration'),
            params: { count: wish.durationDays },
          },
    );
    const inSet = timeInSet(wish);
    if (inSet) {
      parts.push({
        key: inSet.provisional ? 'wishes.row.inSetSinceProvisional' : 'wishes.row.inSetSince',
        params: { date: formatDate(inSet.sinceUtc, locale, undefined) },
      });
    }
    if (wish.extensionCount > 0) {
      parts.push({
        key: pluralKey(wish.extensionCount, 'wishes.row.extensions'),
        params: { count: wish.extensionCount },
      });
    }
    const ended = endedDate(wish);
    if (ended) {
      // The server's UTC day, so the date is read in UTC as well.
      parts.push({
        key: 'wishes.row.ended',
        params: { date: formatDate(`${ended}T00:00:00Z`, locale, 'UTC') },
      });
    }
    return parts;
  });

  /** The way through 7TV itself: always beside an approved wish, on a phone also beside a due one. */
  protected readonly sevenTvHint = computed(
    () => this.wish().status === 'approved' || (this.coarse() && isDue(this.wish(), this.now())),
  );

  private readonly visibleActions = computed(() =>
    wishActions(this.wish()).filter(
      (action) => this.offered().has(action) && !(this.coarse() && isSevenTvAction(action)),
    ),
  );

  protected readonly constructive = computed(() =>
    this.visibleActions().filter((action) => !DESTRUCTIVE_ACTIONS.has(action)),
  );

  protected readonly destructive = computed(() =>
    this.visibleActions().filter((action) => DESTRUCTIVE_ACTIONS.has(action)),
  );

  constructor() {
    // Without IntersectionObserver the row never reports; the usage block then stays out.
    afterRenderEffect((onCleanup) => {
      if (!this.trackUsage() || typeof IntersectionObserver === 'undefined') {
        return;
      }
      let last: boolean | null = null;
      const observer = new IntersectionObserver((hits) => {
        const now = hits[hits.length - 1].isIntersecting;
        if (now !== last) {
          last = now;
          this.seen.emit(now);
        }
      });
      observer.observe(this.root().nativeElement);
      onCleanup(() => observer.disconnect());
    });
  }

  protected actionKey(action: WishAction): string {
    return `wishes.actions.${action}`;
  }

  protected locked(action: WishAction): boolean {
    return this.sevenTvLocked() && isSevenTvAction(action);
  }

  protected choose(action: WishAction): void {
    if (this.busy() || this.locked(action)) {
      return;
    }
    this.action.emit(action);
  }
}

function formatDate(iso: string, locale: string, timeZone: string | undefined): string {
  return new Date(iso).toLocaleDateString(locale, { dateStyle: 'medium', timeZone });
}
