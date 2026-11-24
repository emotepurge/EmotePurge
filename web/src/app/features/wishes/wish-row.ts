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
  approveExtendsRunning,
  endedDate,
  hasEmote,
  isDue,
  isProbation,
  isSevenTvAction,
  timeInSet,
  WishAction,
  wishActions,
  wishTitle,
} from '../../core/wishes/wish-view';
import { emotePageUrl } from '../../shared/emotes/emote-url';
import { UsageSparkline } from '../../shared/emotes/usage-sparkline';
import { Button } from '../../shared/ui/button';
import { StatusBadge, StatusBadgeTone } from '../../shared/ui/status-badge';
import { WishImage } from './wish-image';
import {
  TextPart,
  wishReasonKey,
  wishDurationPart,
  wishPendingAgePart,
  wishRemainingPart,
  wishStatusKey,
  wishStatusTone,
} from './wish-labels';
import type { WishOperationView } from './wish-operation';
import { usageSeries, usageWindowLabel, WishUsageBlock } from './wish-usage';

const KNOWN_SOURCES: ReadonlySet<string> = new Set(['irc', 'manual', 'adopted']);

/** Actions that destroy or end without keeping; they stand last, after a gap (§8.7). */
const DESTRUCTIVE_ACTIONS: ReadonlySet<WishAction> = new Set<WishAction>([
  'remove',
  'close',
  'reject',
  'delete',
]);

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
        @if (emoteKnown()) {
          <app-wish-image
            [imageUrl]="wish().imageUrl"
            [sevenTvEmoteId]="wish().sevenTvEmoteId"
            [knownStill]="knownStill()"
          />
        } @else {
          <!-- An open wish without an emote has no picture yet; the empty plate keeps the column. -->
          <span class="flex size-10 shrink-0 bg-emote-canvas" aria-hidden="true"></span>
        }
        <div class="flex min-w-0 flex-1 flex-col gap-1">
          <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
            <!-- The redemption text as title wraps (it may be long); a name stays on one line. -->
            @if (titleIsText()) {
              <span
                [id]="titleId()"
                class="line-clamp-2 max-w-full font-medium break-words"
                [attr.title]="name()"
                >{{ name() }}</span
              >
            } @else {
              <span [id]="titleId()" class="max-w-full truncate font-medium">{{
                name() || ('wishes.pending.untitled' | transloco)
              }}</span>
            }
            @if (!statusImplied()) {
              <app-status-badge [tone]="statusTone()">{{
                statusKey() | transloco
              }}</app-status-badge>
            }
            @if (probation()) {
              <app-status-badge tone="accent">{{
                'wishes.row.probation' | transloco
              }}</app-status-badge>
            }
            @if (remaining(); as part) {
              <span class="text-sm text-fg-secondary">{{ part.key | transloco: part.params }}</span>
            }
          </div>
          @if (requestText(); as text) {
            <!-- What the viewer typed, as plain text: never a link, never markup (plan 2.10). -->
            <p class="line-clamp-3 text-sm break-words text-fg-body" [attr.title]="text">
              <span class="sr-only">{{ 'wishes.pending.textLabel' | transloco }}</span>
              <q>{{ text }}</q>
            </p>
          }
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
          <!-- The row's own 7TV operation (plan 2.5a). The sr-only region stays mounted so a change is
               announced (§4.5); the visible twin is aria-hidden so it is not read twice. A line the page
               speaks itself (spokenByPage) stays out of the region and its text stays readable. -->
          <p role="status" class="sr-only">
            @if (operation(); as op) {
              @if (!op.spokenByPage) {
                @if (op.leadKey) {
                  {{ op.leadKey | transloco }}
                }
                {{ op.key | transloco: op.params }}
              }
            }
          </p>
          @if (operation(); as op) {
            <p
              [attr.aria-hidden]="op.spokenByPage ? null : 'true'"
              class="text-sm"
              [class.text-danger-fg]="op.tone === 'problem'"
              [class.text-fg-secondary]="op.tone !== 'problem'"
            >
              @if (op.leadKey) {
                {{ op.leadKey | transloco }}
              }
              {{ op.key | transloco: op.params }}
            </p>
            @if (op.retryKey) {
              <button
                type="button"
                appButton="outline"
                class="self-start"
                (click)="retryOperation.emit()"
              >
                {{ op.retryKey | transloco }}
              </button>
            }
          }
          @if (removeBlockShown(); as key) {
            <!-- Why "Entfernen" is locked (plan 2.6); the button points here. -->
            <p [id]="removeBlockId()" class="text-xs text-fg-muted">{{ key | transloco }}</p>
          }
          @if (pageUrl(); as url) {
            <a
              [href]="url"
              target="_blank"
              rel="noopener"
              class="inline-flex min-h-6 items-center self-start text-sm text-fg-secondary underline decoration-border-strong underline-offset-2 transition hover:text-fg hover:decoration-accent"
              >{{ 'wishes.row.openOnSevenTv' | transloco
              }}<span class="sr-only"> {{ 'common.opensInNewTab' | transloco }}</span></a
            >
          }
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
                  [attr.aria-describedby]="describedBy(action)"
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
   * The emote's 7TV default name from this session's preview read (K6): an open wish captured from a
   * link knows the emote but has no alias or name yet. `null` while unknown.
   */
  readonly previewName = input<string | null>(null);
  /**
   * The surrounding group already names the status (the "Offen" group of the work list): the pill
   * would repeat the heading on every row (UI-Designsprache §4.3, notable how often), so it is left
   * out. Wherever the same wish stands outside that group, the pill stays.
   */
  readonly statusImplied = input(false);

  /**
   * The usage of this wish's window, handed in by the page (T5); `null` shows nothing. The row
   * decides nothing about it: numbers and line come from the one `usage` object (Sol-P2-11).
   */
  readonly usage = input<WishUsageBlock | null>(null);
  /** The page wants to know when this row is in view (usage loads only then). */
  readonly trackUsage = input(false);
  /** Where this wish's own 7TV operation stands (T8 add, T9 removal); `null` shows nothing. */
  readonly operation = input<WishOperationView | null>(null);
  /** Why "Entfernen" is locked for this wish (`wishRemovalBlockKey`, or a removal still under way);
   *  `null` when it may start. The button stays, `aria-disabled`, and points at the reason. */
  readonly removeBlockKey = input<string | null>(null);

  readonly action = output<WishAction>();
  /** The operation line's follow-up was chosen (`WishOperationView.retryKey`). */
  readonly retryOperation = output<void>();
  /** Whether the row is in the viewport; only while `trackUsage` is set, and only on change. */
  readonly seen = output<boolean>();

  private readonly languageService = inject(LanguageService);

  private readonly root = viewChild.required<ElementRef<HTMLElement>>('root');

  protected readonly titleId = computed(() => `wish-title-${this.wish().id}`);

  /** Whether the wish knows its 7TV emote (plan 2.8a): picture and 7TV link only then. */
  protected readonly emoteKnown = computed(() => hasEmote(this.wish()));

  /**
   * The alias names a wish (plan 2.8 c), the emote name and the preview's default name stand in for
   * an empty one; an open wish without an emote is named by what the viewer typed (`wishTitle`). With
   * nothing known at all (redeemer data withheld) a neutral title stands.
   */
  protected readonly name = computed(() => wishTitle(this.wish(), this.previewName()));

  /** The title is the redemption text itself: it then wraps, and the text line is not repeated. */
  protected readonly titleIsText = computed(() => {
    const text = this.wish().requestText ?? '';
    return text !== '' && this.name() === text;
  });

  /** The redemption text as its own line, unless it already is the title. */
  protected readonly requestText = computed(() =>
    this.titleIsText() ? null : this.wish().requestText || null,
  );

  protected readonly pageUrl = computed(() =>
    this.emoteKnown() ? emotePageUrl(this.wish().sevenTvEmoteId) : null,
  );

  protected readonly statusKey = computed(() => wishStatusKey(this.wish()));

  protected readonly statusTone = computed<StatusBadgeTone>(() => wishStatusTone(this.wish()));

  protected readonly probation = computed(() => isProbation(this.wish(), this.now()));

  protected readonly reasonKey = computed(() => wishReasonKey(this.wish()));

  protected readonly remaining = computed<TextPart | null>(() =>
    wishRemainingPart(this.wish(), this.now()),
  );

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
    ];
    // The server flags a window under 24 hours; no per-day mean is shown then (spec E4).
    if (!usage.perDayMeanWithheld) {
      parts.push({
        key: 'wishes.usage.mean',
        params: { mean: usage.perDayMean.toLocaleString(locale, { maximumFractionDigits: 1 }) },
      });
    }
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
    if (wish.rewardLabel) {
      parts.push({ key: 'wishes.pending.reward', params: { label: wish.rewardLabel } });
    }
    const age = wishPendingAgePart(wish, this.now());
    if (age) {
      parts.push(age);
    }
    if (KNOWN_SOURCES.has(wish.source)) {
      parts.push({ key: `wishes.source.${wish.source}` });
    }
    parts.push(wishDurationPart(wish) ?? { key: 'wishes.row.permanent' });
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

  protected readonly removeBlockId = computed(() => `wish-remove-block-${this.wish().id}`);

  /** The lock reason of "Entfernen", only while the button is on screen. */
  protected readonly removeBlockShown = computed(() =>
    this.visibleActions().includes('remove') ? this.removeBlockKey() : null,
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
    // Approving a wish whose emote already runs books an extension of that one (plan 2.10).
    if (action === 'approve' && approveExtendsRunning(this.wish())) {
      return 'wishes.actions.approveExtend';
    }
    return `wishes.actions.${action}`;
  }

  protected locked(action: WishAction): boolean {
    return (
      (this.sevenTvLocked() && isSevenTvAction(action)) ||
      (action === 'remove' && this.removeBlockKey() !== null)
    );
  }

  /** The page's lock line while a 7TV run holds the start, and the removal's own reason. */
  protected describedBy(action: WishAction): string | null {
    const ids = [
      this.sevenTvLocked() && isSevenTvAction(action) ? this.lockDescriptionId() : null,
      action === 'remove' && this.removeBlockKey() !== null ? this.removeBlockId() : null,
    ].filter((id): id is string => !!id);
    return ids.length > 0 ? ids.join(' ') : null;
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
