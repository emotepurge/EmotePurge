import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import {
  afterNextRender,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  Injector,
  input,
  linkedSignal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { catchError, map, of } from 'rxjs';

import {
  BackfillCoverageInterval,
  BackfillOption,
  BackfillRemovalResult,
  BackfillStatus,
} from '../../core/channels/backfill.model';
import { BackfillService } from '../../core/channels/backfill.service';
import {
  formatIsoDay,
  lastDayOf,
  ReplacedInterval,
  replacedIntervals,
  backfillSetLabel,
} from '../../core/channels/backfill-window';
import { isUnknownOutcome } from '../../core/http/unknown-outcome';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { pluralKey } from '../../core/i18n/plural';
import { channelLiveUrl, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveReload } from '../../core/live/live-reload';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { Button } from '../../shared/ui/button';
import { openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { BackfillRunStatusView, backfillRunStatusKey } from './backfill-run-status';

/** Same burst window the other live-reloading pages use. */
const STATUS_RELOAD_DEBOUNCE_MS = 500;

const REASON_KEYS: Record<string, string> = {
  no_days_before_counting: 'backfill.reasons.noDaysBeforeCounting',
};
const UNKNOWN_REASON_KEY = 'backfill.reasons.unknown';
const REMOVE_BLOCKED_BY_RUN_KEY = 'backfill.remove.blockedByRun';
// The client-side mismatch says what the server's 409 says: the shown scope is not the current one.
const REMOVE_SCOPE_CHANGED_KEY = 'errors.api.backfill_remove_scope_changed';
// Same vocabulary as the run errors (`backfill.errors.<snake_case ErrorCode>`, spec §4.6).
const CHANNEL_EXCLUDED_KEY = 'backfill.errors.channel_excluded';

function errorCodeOf(error: HttpErrorResponse): string | undefined {
  return (error.error as { errorCode?: string } | null)?.errorCode;
}

/**
 * The translation key for a failed start. The generic mapping, except that `channel_excluded` gets
 * a sentence about the backfill instead of the join flow's "This channel cannot be added."
 */
export function backfillStartErrorKey(error: HttpErrorResponse): string {
  return errorCodeOf(error) === 'channel_excluded'
    ? CHANNEL_EXCLUDED_KEY
    : apiErrorTranslationKey(error);
}

interface OptionView {
  months: number;
  from: string;
  /** Inclusive last day. */
  to: string;
  days: number;
  available: boolean;
  /** An unavailable option has no meaningful window (the server's `windowFrom` lies at or after
   *  `windowTo`), so it shows its reason only. */
  showRange: boolean;
  reasonKey: string | null;
}

/** The parameters of one `backfill.replaceWarning` sentence, already formatted for the reader. */
interface ReplaceSentence {
  set: string;
  from: string;
  /** Inclusive last day. */
  to: string;
}

/** What the live region says: a translation key plus its parameters. */
export interface BackfillAnnouncement {
  runId: number;
  key: string;
  /** Translation key of the run status, for `backfill.announce.active`. */
  statusKey?: string;
  done?: number;
  total?: number;
}

const OUTCOME_STATUSES: readonly string[] = ['completed', 'failed', 'cancelled'];

/**
 * The live region's content: the running run's progress, or — for the run that has just left
 * `activeRun` and shows up as `lastRun` — its outcome. A `lastRun` the section never saw running is
 * not announced (it is old news on arrival), and an outcome stays until the next run shows up.
 */
export function backfillAnnouncement(
  status: BackfillStatus | null,
  previousStatus: BackfillStatus | null,
  previous: BackfillAnnouncement | null,
): BackfillAnnouncement | null {
  if (status === null) {
    return null;
  }
  const active = status.activeRun;
  if (active) {
    return {
      runId: active.id,
      key: 'backfill.announce.active',
      statusKey: backfillRunStatusKey(active.status),
      done: active.weeksDone,
      total: active.weeksTotal,
    };
  }
  const last = status.lastRun;
  if (last === null || !OUTCOME_STATUSES.includes(last.status)) {
    return null;
  }
  if (previousStatus?.activeRun?.id === last.id) {
    return { runId: last.id, key: `backfill.announce.${last.status}` };
  }
  return previous !== null && previous.runId === last.id && previous.statusKey === undefined
    ? previous
    : null;
}

/** What the live region says after a removal: a translation key plus the day count. */
export interface BackfillRemovalAnnouncement {
  key: string;
  count: number;
}

/**
 * The sentence for a removal's answer. Zeros mean the data was already gone (a second manager, a
 * double submit — the server answers "already done", D9), which "0 days removed" would misreport.
 */
export function backfillRemovalAnnouncement(
  result: BackfillRemovalResult,
): BackfillRemovalAnnouncement {
  const count = result.removedCoverageDays;
  return count === 0 && result.removedUsageRows === 0
    ? { key: 'backfill.remove.announceNothingLeft', count }
    : { key: pluralKey(count, 'backfill.remove.announceDone'), count };
}

/**
 * One settled status read. `generation` numbers the reads in the order they were issued, so the
 * section can tell a read that began after some point in time from one that merely finished after
 * it. A failed read is a settled read too, so the stream never errors: the failure travels as
 * `error`.
 */
interface StatusRead {
  generation: number;
  status: BackfillStatus | undefined;
  error: unknown;
}

/** One line of the imported-data section, already formatted for the reader. */
interface ImportedIntervalView {
  key: string;
  from: string;
  /** Inclusive last day. */
  to: string;
  set: string;
}

/**
 * What a removal confirmation vouches for: the channel and its coverage intervals as the status
 * listed them. `key` is their canonical form; the dialog text and the sent expectation are both
 * built from `coverage`, so the two cannot differ.
 */
interface RemovalScope {
  key: string;
  channel: string;
  coverage: readonly BackfillCoverageInterval[];
  countingSince: string;
}

/** The statuses of a run that can still be cancelled (B11); the other three are final. */
const CANCELLABLE_STATUSES: readonly string[] = ['queued', 'running', 'paused'];

interface SetView {
  id: string;
  label: string;
  capacity: number | null;
  isActive: boolean;
}

/**
 * The chat-log backfill section (spec 2026-10-09, §7): what the numbers are, which of the channel's
 * 7TV sets to match against, how far back to go, the start button — and the running and the last
 * run, with progress and cancel — and below it, while the channel holds imported days, the section
 * that removes them again (spec 2026-10-13, §10). Both share one status read, one post-cancel gate
 * and the request tokens: a second read or a second source of `activeRun` would reopen the gap the
 * removal's confirmation binding closes.
 */
@Component({
  selector: 'app-backfill-section',
  imports: [BackfillRunStatusView, Button, NoticeBanner, SkeletonRows, TranslocoPipe],
  host: { class: 'flex flex-col gap-6' },
  template: `
    <section class="flex flex-col gap-4" aria-labelledby="backfill-heading">
      <h3 id="backfill-heading" class="text-base font-semibold">
        {{ 'backfill.title' | transloco }}
      </h3>

      <!-- Permanently mounted (UI-Designsprache §4.5): a region that appears together with its text
           is not announced, and a run's start and end are exactly what it is for. -->
      <!-- A removal's outcome is not spoken here: focus moves onto its visible confirmation below,
           which reads it out once. Saying it here as well would make it heard twice. -->
      <p class="sr-only" role="status">
        @if (announcement(); as announced) {
          {{
            announced.key
              | transloco
                : {
                    status: announced.statusKey ? (announced.statusKey | transloco) : '',
                    done: announced.done,
                    total: announced.total,
                  }
          }}
        }
      </p>

      @if (status(); as status) {
        @if (statusRefetchFailed()) {
          <app-notice-banner variant="warning">{{
            'backfill.statusRefreshFailed' | transloco
          }}</app-notice-banner>
        }
        <div class="flex flex-col gap-2 text-sm text-fg-secondary">
          <p>
            {{ 'backfill.explain.source' | transloco }}
            <a
              class="text-accent-fg underline underline-offset-4 transition hover:text-fg"
              [href]="status.archive.url"
              target="_blank"
              rel="noopener noreferrer"
              >{{ status.archive.name
              }}<span class="sr-only"> {{ 'common.opensInNewTab' | transloco }}</span></a
            >.
          </p>
          <ul class="list-disc pl-5">
            <li>{{ 'backfill.explain.inaccurate' | transloco }}</li>
            <li>{{ 'backfill.explain.namesOnly' | transloco }}</li>
            <li>{{ 'backfill.explain.renamed' | transloco }}</li>
            <li>{{ 'backfill.explain.gaps' | transloco }}</li>
            <li>{{ 'backfill.explain.oneSetPerDay' | transloco }}</li>
          </ul>
        </div>

        <div class="flex flex-col gap-1">
          <label for="backfill-set" class="text-sm font-medium">
            {{ 'backfill.setLabel' | transloco }}
          </label>
          <select
            id="backfill-set"
            class="app-input max-w-md"
            [disabled]="!setPickerEnabled()"
            [attr.aria-describedby]="'backfill-set-note'"
            (change)="onSetChange($event)"
          >
            @if (selectedSetId() === null) {
              <option value="" selected>{{ 'backfill.setPlaceholder' | transloco }}</option>
            }
            @for (set of offeredSets(); track set.id) {
              <option [value]="set.id" [selected]="set.id === selectedSetId()">
                {{ set.label
                }}{{
                  set.capacity !== null
                    ? ' · ' + ('backfill.setCapacity' | transloco: { count: set.capacity })
                    : ''
                }}{{ set.isActive ? ' · ' + ('backfill.setActiveMarker' | transloco) : '' }}
              </option>
            }
          </select>
          <p id="backfill-set-note" class="text-xs text-fg-muted">
            {{ 'backfill.setNote' | transloco }}
          </p>
          @if (setListErrorKey(); as key) {
            <app-notice-banner variant="warning">{{ key | transloco }}</app-notice-banner>
          }
        </div>

        <fieldset class="flex flex-col gap-1">
          <legend class="mb-1 text-sm font-medium">{{ 'backfill.windowLabel' | transloco }}</legend>
          @for (option of optionViews(); track option.months) {
            <label class="flex flex-wrap items-center gap-x-2 py-1 text-sm text-fg-secondary">
              <input
                type="radio"
                class="h-4 w-4 accent-accent-solid"
                name="backfill-months"
                [checked]="option.months === selectedMonths()"
                [disabled]="!option.available"
                (change)="selectedMonthsChoice.set(option.months)"
              />
              <span>{{
                pluralKey(option.months, 'backfill.option.months')
                  | transloco: { count: option.months }
              }}</span>
              @if (option.showRange) {
                <span class="text-fg-muted">
                  {{
                    'backfill.option.range'
                      | transloco: { from: formatDay(option.from), to: formatDay(option.to) }
                  }}
                  ·
                  {{
                    pluralKey(option.days, 'backfill.option.days')
                      | transloco: { count: option.days }
                  }}
                </span>
              }
              @if (option.reasonKey; as reasonKey) {
                <span class="text-fg-muted">· {{ reasonKey | transloco }}</span>
              }
            </label>
          }
        </fieldset>

        @if (replaceWarning().length > 0) {
          <app-notice-banner variant="warning">
            <ul class="flex flex-col gap-1">
              @for (sentence of replaceWarning(); track sentence.from) {
                <li>{{ 'backfill.replaceWarning' | transloco: sentence }}</li>
              }
            </ul>
          </app-notice-banner>
        }

        @if (status.activeRun; as run) {
          <p class="text-sm">{{ 'backfill.runActive' | transloco }}</p>
          <app-backfill-run-status
            kind="active"
            [run]="run"
            [cooldownUntilUtc]="status.cooldownUntilUtc"
          />
          @if (canCancel()) {
            <div class="flex flex-col items-start gap-2">
              <button type="button" appButton="danger" [disabled]="cancelling()" (click)="cancel()">
                {{ 'backfill.cancel' | transloco }}
              </button>
              @if (cancelErrorKey(); as key) {
                <app-notice-banner variant="error" class="block">{{
                  key | transloco
                }}</app-notice-banner>
              }
            </div>
          }
        }

        <div class="flex flex-col items-start gap-2">
          <button
            type="button"
            appButton="primary"
            [disabled]="!canStart() || starting()"
            (click)="start()"
          >
            {{ 'backfill.start' | transloco }}
          </button>
          @if (startErrorKey(); as key) {
            <app-notice-banner variant="error" class="block">{{
              key | transloco
            }}</app-notice-banner>
          }
        </div>

        @if (status.lastRun; as run) {
          <app-backfill-run-status kind="last" [run]="run" />
        }
      } @else if (statusReadError()) {
        <app-notice-banner variant="error">{{ statusErrorKey() | transloco }}</app-notice-banner>
      } @else {
        <app-skeleton-rows [count]="3" />
      }
    </section>

    @if (removalAnnouncement(); as removal) {
      <!-- Replaces the section it reports on, under the same heading, so the trigger that vanished
           with it hands focus to the outcome rather than to <body>. -->
      <section class="flex flex-col gap-4" aria-labelledby="backfill-imported-heading">
        <h3 id="backfill-imported-heading" class="text-base font-semibold">
          {{ 'backfill.remove.title' | transloco }}
        </h3>
        <p #removalConfirmation tabindex="-1" class="text-sm">
          {{ removal.key | transloco: { count: removal.count } }}
        </p>
      </section>
    } @else if (importedIntervals().length > 0) {
      <section class="flex flex-col gap-4" aria-labelledby="backfill-imported-heading">
        <h3 id="backfill-imported-heading" class="text-base font-semibold">
          {{ 'backfill.remove.title' | transloco }}
        </h3>
        <p class="text-sm text-fg-secondary">{{ 'backfill.remove.intro' | transloco }}</p>
        <ul class="flex flex-col gap-1 text-sm">
          @for (interval of importedIntervals(); track interval.key) {
            <li>{{ 'backfill.remove.interval' | transloco: interval }}</li>
          }
        </ul>
        <div class="flex flex-col items-start gap-2">
          <button
            type="button"
            appButton="danger"
            [disabled]="!canRemove()"
            [attr.aria-describedby]="removeBlockedReasonKey() ? 'backfill-remove-blocked' : null"
            (click)="remove()"
          >
            {{ 'backfill.remove.action' | transloco }}
          </button>
          @if (removeBlockedReasonKey(); as reasonKey) {
            <p id="backfill-remove-blocked" class="text-sm text-fg-muted">
              {{ reasonKey | transloco }}
            </p>
          }
          @if (removeErrorKey(); as key) {
            <app-notice-banner variant="error" class="block">{{
              key | transloco
            }}</app-notice-banner>
          }
        </div>
      </section>
    }
  `,
})
export class BackfillSection {
  readonly channelName = input.required<string>();

  private readonly backfillService = inject(BackfillService);
  private readonly emoteSetService = inject(SevenTvEmoteSetService);
  private readonly languageService = inject(LanguageService);
  private readonly transloco = inject(TranslocoService);
  private readonly dialog = inject(Dialog);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);

  /** How many status reads have been issued so far; the next one gets this number plus one. */
  private readsIssued = 0;

  protected readonly statusResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => {
      // Numbered when the GET is actually sent (the stream is subscribed right after this call), not
      // when a reload is asked for — see `postCancelReadGeneration`.
      const generation = ++this.readsIssued;
      return this.backfillService.getStatus(params).pipe(
        map((status): StatusRead => ({ generation, status, error: undefined })),
        catchError((error: unknown) => of<StatusRead>({ generation, status: undefined, error })),
      );
    },
  });

  /** The last settled read of this channel; kept while a refetch is in flight, gone on a switch. */
  private readonly lastRead = computed(() =>
    this.statusResource.hasValue() ? this.statusResource.value() : undefined,
  );

  /** The failure of the last settled read, if it failed. */
  protected readonly statusReadError = computed(() => this.lastRead()?.error);

  /**
   * The status on screen: the freshest good read of this channel. A failed background refetch must
   * not throw the whole section away (it would drop the user's picks), so the last good answer
   * stays until a newer one replaces it; a channel switch starts over with nothing.
   */
  protected readonly status = linkedSignal<
    { channel: string; value: BackfillStatus | undefined },
    BackfillStatus | null
  >({
    source: () => ({
      channel: this.channelName(),
      value: this.lastRead()?.status,
    }),
    computation: (source, previous) =>
      source.value ??
      (previous && previous.source.channel === source.channel ? previous.value : null),
  });

  /** What the permanent live region says; see {@link backfillAnnouncement}. */
  protected readonly announcement = linkedSignal<
    BackfillStatus | null,
    BackfillAnnouncement | null
  >({
    source: this.status,
    computation: (status, previous) =>
      backfillAnnouncement(status, previous?.source ?? null, previous?.value ?? null),
  });

  /**
   * A start error belongs to one channel and to the "no run" state it was answered in: it goes when
   * the channel changes and when a run shows up (a lost `backfill_already_active` race, a 5xx whose
   * run had committed) — cleared, not masked, so it cannot come back when that run ends. The source
   * is a primitive on purpose: an object would reset on every refetch.
   */
  protected readonly startErrorKey = linkedSignal<string, string | null>({
    source: computed(() => `${this.channelName()}|${this.status()?.activeRun?.id ?? ''}`),
    computation: () => null,
  });

  /**
   * The in-flight cancel, or `null`. A request token rather than a flag: a callback applies only
   * while it still owns this signal, so the answer of a cancel for a run (or channel) the section
   * has since left touches nothing. Keyed like the start error — on the channel and the run it
   * targets — and primitive on purpose: an object would reset on every refetch.
   */
  private readonly cancelRequest = linkedSignal<string, object | null>({
    source: computed(() => `${this.channelName()}|${this.status()?.activeRun?.id ?? ''}`),
    computation: () => null,
  });
  protected readonly cancelling = computed(() => this.cancelRequest() !== null);

  /**
   * The translated error of a failed cancel; goes with the run and the channel, and on the next
   * attempt.
   */
  protected readonly cancelErrorKey = linkedSignal<string, string | null>({
    source: computed(() => `${this.channelName()}|${this.status()?.activeRun?.id ?? ''}`),
    computation: () => null,
  });

  private readonly setListResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => this.emoteSetService.listChannelEmoteSets(params),
  });

  /** The user's own pick; `undefined` = never touched, so the preselection applies. */
  private readonly setChoice = linkedSignal<string, string | undefined>({
    source: this.channelName,
    computation: () => undefined,
  });
  // The three below start over with every channel: the router reuses this component when the
  // user moves from /channels/a/settings to /channels/b/settings.
  protected readonly selectedMonthsChoice = linkedSignal<string, number | null>({
    source: this.channelName,
    computation: () => null,
  });
  /**
   * The in-flight start, or `null` — a request token, not a flag, so a callback applies only while
   * it still owns the signal. Comparing channel names let a stale answer through after A → B → A.
   */
  private readonly startRequest = linkedSignal<string, object | null>({
    source: this.channelName,
    computation: () => null,
  });
  protected readonly starting = computed(() => this.startRequest() !== null);
  protected readonly pluralKey = pluralKey;

  /** The active set, or `null` while no sync has completed (`""` on the wire). */
  readonly preselectedSetId = computed(() => {
    return this.status()?.activeEmoteSetId || null;
  });

  /**
   * What the server accepts (`EmoteSetMembershipRule.BelongsToChannel`): the channel's `NORMAL`
   * sets of its 7TV account plus the active set. Personal sets are not offered unless one is the
   * active set. When the list does not name the active set (7TV's REST cache lags behind a set
   * switch) a synthetic entry keeps the preselection working. Empty until the list is on hand.
   */
  readonly offeredSets = computed<SetView[]>(() => {
    if (!this.setListResource.hasValue()) {
      return [];
    }
    const activeId = this.preselectedSetId();
    const sets: SetView[] = this.setListResource
      .value()
      .sets.filter((set) => set.id === activeId || (set.kind === 'NORMAL' && !set.isPersonal))
      .map((set) => ({
        id: set.id,
        label: set.name || set.id,
        capacity: set.capacity,
        isActive: set.id === activeId,
      }));
    if (activeId !== null && !sets.some((set) => set.id === activeId)) {
      sets.unshift({ id: activeId, label: activeId, capacity: null, isActive: true });
    }
    return sets;
  });

  /** Only an offered set can be selected, so a stale pick yields `null`. */
  readonly selectedSetId = computed(() => {
    const candidate = this.setChoice() ?? this.preselectedSetId();
    return candidate !== null && this.offeredSets().some((set) => set.id === candidate)
      ? candidate
      : null;
  });

  readonly setListLoaded = computed(() => this.setListResource.hasValue());

  protected readonly setPickerEnabled = computed(() => this.setListLoaded());

  /** The list's own error sentence while it is unreadable (503) — picker and start stay locked. */
  readonly setListErrorKey = computed(() => {
    const error = this.setListResource.error();
    if (!error || this.setListResource.hasValue()) {
      return null;
    }
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : 'errors.generic';
  });

  readonly optionViews = computed<OptionView[]>(() => {
    return (this.status()?.options ?? []).map((option) => this.toOptionView(option));
  });

  /** The user's pick if it is still available, otherwise the first available option. */
  readonly selectedMonths = computed(() => {
    const options = this.optionViews();
    const choice = this.selectedMonthsChoice();
    const chosen = options.find((option) => option.months === choice && option.available);
    return (chosen ?? options.find((option) => option.available))?.months ?? null;
  });

  /**
   * The first read generation that counts as a post-cancel read, or `null`. The status patched on
   * the 204 has no run, but its coverage is the pre-cancel snapshot (a block committed just before
   * the DELETE is missing), so Start and Remove wait for a read that was *issued after* the 204 to
   * settle — answer or failure; a failed GET must not lock them for good. A read that was already in
   * flight at the 204 does not count, however late it lands: it may still say `running`
   * (Sol-1, plan #384 §3). Keyed on the channel like the other request state.
   */
  private readonly postCancelReadGeneration = linkedSignal<string, number | null>({
    source: this.channelName,
    computation: () => null,
  });

  /** No read issued after the cancel's 204 has settled yet. */
  private readonly awaitingPostCancelRead = computed(() => {
    const needed = this.postCancelReadGeneration();
    return needed !== null && (this.lastRead()?.generation ?? 0) < needed;
  });

  /**
   * A status read was asked for while one was in flight. Angular's `reload()` refuses (returns
   * `false`) while the resource is loading, so without this the request would be lost — an SSE
   * nudge as much as the read a cancel waits for. Fired once the running load settles.
   */
  private readonly statusReadQueued = linkedSignal<string, boolean>({
    source: this.channelName,
    computation: () => false,
  });

  /** The channel and the run on screen — the key the removal's request state goes with. */
  private readonly channelRunKey = computed(
    () => `${this.channelName()}|${this.status()?.activeRun?.id ?? ''}`,
  );

  /**
   * The in-flight removal, or `null` — a request token like `cancelRequest`, keyed on the channel
   * and the active run, so the answer of a removal for a channel the user has since left touches
   * nothing.
   */
  private readonly removeRequest = linkedSignal<string, object | null>({
    source: this.channelRunKey,
    computation: () => null,
  });
  protected readonly removing = computed(() => this.removeRequest() !== null);

  /**
   * The translated error of a failed removal. Goes with the channel, on the next attempt, and when a
   * run shows up or leaves: while a run exists the section shows the reason instead (a 409
   * `backfill_remove_run_active` followed by its refetch would otherwise say the same thing twice),
   * and it must not come back when that run ends.
   */
  protected readonly removeErrorKey = linkedSignal<string, string | null>({
    source: this.channelRunKey,
    computation: () => null,
  });

  /**
   * The outcome of the last removal, shown in place of the imported-data section and focused once.
   * Outlives the refetch that empties the coverage; goes with the channel and when a new run shows
   * up, like the rest of the removal's state — no control of its own to dismiss it.
   */
  protected readonly removalAnnouncement = linkedSignal<string, BackfillRemovalAnnouncement | null>(
    {
      source: this.channelRunKey,
      computation: () => null,
    },
  );

  /** The rendered confirmation, the focus target after a successful removal. */
  private readonly removalConfirmation = viewChild<ElementRef<HTMLElement>>('removalConfirmation');

  readonly canStart = computed(
    () =>
      !this.awaitingPostCancelRead() &&
      !this.removing() &&
      this.status() !== null &&
      this.status()?.activeRun === null &&
      this.selectedMonths() !== null &&
      this.selectedSetId() !== null &&
      this.setListLoaded(),
  );

  /** The running run may be cancelled while it is queued, running or paused. */
  readonly canCancel = computed(() =>
    CANCELLABLE_STATUSES.includes(this.status()?.activeRun?.status ?? ''),
  );

  /** The selected option's window, `[from, to)`, or `null` while there is no available pick. */
  private readonly selectedWindow = computed(() => {
    const months = this.selectedMonths();
    const option = this.status()?.options.find((candidate) => candidate.months === months);
    return option ? { from: option.windowFrom, to: option.windowTo } : null;
  });

  /**
   * What a run with the current picks would replace, one sentence's parameters per interval — the
   * one source of the inline notice and of the confirm dialog, so the two cannot differ.
   */
  readonly replaceWarning = computed<ReplaceSentence[]>(() => {
    const window = this.selectedWindow();
    const setId = this.selectedSetId();
    const status = this.status();
    // Start is impossible while a run exists, so there is nothing to warn about.
    if (window === null || setId === null || status === null || status.activeRun !== null) {
      return [];
    }
    return replacedIntervals(status.coverage, window.from, window.to, setId).map((interval) =>
      this.toSentence(interval),
    );
  });

  /**
   * The imported intervals in status order, one line each: range with the inclusive last day, set
   * name or id.
   */
  readonly importedIntervals = computed<ImportedIntervalView[]>(() =>
    (this.status()?.coverage ?? []).map((interval) => ({
      key: `${interval.from}|${interval.emoteSetId}|${interval.archiveHost}`,
      ...this.toIntervalLine(interval),
    })),
  );

  /** The confirmation's subject; `null` without a status. See {@link RemovalScope}. */
  private readonly removalScope = computed<RemovalScope | null>(() => {
    const status = this.status();
    if (status === null) {
      return null;
    }
    const channel = this.channelName();
    return {
      key: JSON.stringify([
        channel,
        status.coverage.map((i) => [i.from, i.to, i.emoteSetId, i.archiveHost]),
      ]),
      channel,
      coverage: status.coverage,
      countingSince: status.countingSince,
    };
  });

  readonly canRemove = computed(
    () =>
      this.importedIntervals().length > 0 &&
      this.status()?.activeRun === null &&
      !this.awaitingPostCancelRead() &&
      !this.removing() &&
      !this.starting(),
  );

  /** Why Remove is locked, said under it; the short locks (a request, the post-cancel read) say
   *  nothing. */
  readonly removeBlockedReasonKey = computed(() =>
    this.status()?.activeRun ? REMOVE_BLOCKED_BY_RUN_KEY : null,
  );

  protected readonly statusErrorKey = computed(() => {
    const error = this.statusReadError();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : 'errors.generic';
  });

  /**
   * A failed refetch while an earlier answer is on screen: said next to the section, not instead of
   * it.
   */
  readonly statusRefetchFailed = computed(() => this.status() !== null && !!this.statusReadError());

  constructor() {
    liveReload(
      computed(() => channelLiveUrl(this.channelName())),
      {
        accept: [LIVE_EVENT_TYPES.backfillProgress],
        debounceMs: STATUS_RELOAD_DEBOUNCE_MS,
      },
    ).subscribe(() => this.requestStatusRead());

    // A read refused while another was loading is issued as soon as that one settles.
    effect(() => {
      if (this.statusReadQueued() && !this.statusResource.isLoading()) {
        untracked(() => this.requestStatusRead());
      }
    });
  }

  protected onSetChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.setChoice.set(value === '' ? undefined : value);
  }

  protected formatDay(isoDay: string): string {
    return formatIsoDay(isoDay, this.languageService.lang());
  }

  /**
   * Starts at once, or — when the run would replace imported days — after the user confirmed it.
   * The confirmation holds only for the picks and the warning it showed.
   */
  protected start(): void {
    if (!this.canStart() || this.starting()) {
      return;
    }
    if (this.replaceWarning().length === 0) {
      this.sendStart();
      return;
    }
    const asked = this.startPlanKey();
    openConfirmDialog(this.dialog, {
      message: this.startConfirmMessage(),
      confirmLabel: this.transloco.translate('backfill.start'),
    }).closed.subscribe((confirmed) => {
      // What was confirmed is what the dialog showed: a status refetch behind the modal may have
      // changed the coverage, and the user has not agreed to that.
      if (confirmed && this.startPlanKey() === asked) {
        this.sendStart();
      }
    });
  }

  /**
   * Removes the channel's imported data after a confirmation (spec 2026-10-13, §10.3/§10.4). What
   * goes out is exactly what the dialog showed, and only while it is still the current scope: a
   * status refetch behind the modal (a run finished, another manager removed the data) asks for a
   * new confirmation instead.
   */
  protected remove(): void {
    const scope = this.removalScope();
    if (scope === null || !this.canRemove()) {
      return;
    }
    openConfirmDialog(this.dialog, {
      message: this.removeConfirmMessage(scope),
      confirmLabel: this.transloco.translate('backfill.remove.action'),
    }).closed.subscribe((confirmed) => {
      // Another channel is on screen now: none of its business.
      if (!confirmed || this.channelName() !== scope.channel) {
        return;
      }
      if (this.removalScope()?.key !== scope.key) {
        // A run on screen says why on its own (the reason under the button).
        if (!this.status()?.activeRun) {
          this.removeErrorKey.set(REMOVE_SCOPE_CHANGED_KEY);
        }
        return;
      }
      if (this.canRemove()) {
        this.sendRemove(scope.coverage);
      }
    });
  }

  /** Cancels the running run after a confirmation. Completed weeks stay (B11). */
  protected cancel(): void {
    const run = this.status()?.activeRun;
    if (!run || !this.canCancel() || this.cancelling()) {
      return;
    }
    openConfirmDialog(this.dialog, {
      message: this.transloco.translate('backfill.cancelConfirm'),
      confirmLabel: this.transloco.translate('backfill.cancel'),
    }).closed.subscribe((confirmed) => {
      // The dialog was open for a while: DELETE ends whatever run is active, so it must still be
      // the one the user was asked about.
      if (confirmed && this.status()?.activeRun?.id === run.id) {
        this.sendCancel();
      }
    });
  }

  private sendStart(): void {
    const emoteSetId = this.selectedSetId();
    const months = this.selectedMonths();
    if (!this.canStart() || this.starting() || emoteSetId === null || months === null) {
      return;
    }
    const request = {};
    this.startRequest.set(request);
    this.startErrorKey.set(null);
    this.backfillService.start(this.channelName(), emoteSetId, months).subscribe({
      next: (run) => {
        // Not ours any more (another channel was on screen meanwhile): none of this state's
        // business.
        if (this.startRequest() !== request) {
          return;
        }
        this.startRequest.set(null);
        // Until the refetch lands the status still says "no run": patch the retained status (the
        // one place that does) so the button stays locked and a second click cannot send a second
        // POST.
        this.status.update((current) => current && { ...current, activeRun: run });
        this.requestStatusRead();
      },
      error: (error: HttpErrorResponse) => {
        if (this.startRequest() !== request) {
          return;
        }
        this.startRequest.set(null);
        this.startErrorKey.set(backfillStartErrorKey(error));
        // A race lost to another manager, or an answer that may have been lost after the commit:
        // either way the status says what is really queued.
        if (errorCodeOf(error) === 'backfill_already_active' || isUnknownOutcome(error.status)) {
          this.requestStatusRead();
        }
      },
    });
  }

  /** The dialog text: the action first (the dialog has no heading), then the warning sentences. */
  private startConfirmMessage(): string {
    return [
      this.transloco.translate('backfill.startConfirm'),
      ...this.replaceWarning().map((sentence) =>
        this.transloco.translate('backfill.replaceWarning', sentence),
      ),
    ].join('\n');
  }

  /** Everything a start confirmation vouches for. */
  private startPlanKey(): string {
    return `${this.selectedSetId()}|${this.selectedMonths()}|${this.startConfirmMessage()}`;
  }

  private sendCancel(): void {
    const run = this.status()?.activeRun;
    const request = {};
    this.cancelRequest.set(request);
    this.cancelErrorKey.set(null);
    this.backfillService.cancel(this.channelName()).subscribe({
      next: () => {
        if (this.cancelRequest() !== request) {
          return;
        }
        // The token stays: it goes when the run leaves `activeRun`, which the patch does at once,
        // so the button cannot send a second DELETE in the window before the refetch lands.
        this.status.update((current) =>
          current && run && current.activeRun?.id === run.id
            ? { ...current, activeRun: null, lastRun: { ...run, status: 'cancelled' } }
            : current,
        );
        // Every read issued from here on is a post-cancel read.
        this.postCancelReadGeneration.set(this.readsIssued + 1);
        this.requestStatusRead();
      },
      error: (error: HttpErrorResponse) => {
        if (this.cancelRequest() !== request) {
          return;
        }
        this.cancelRequest.set(null);
        // The run ended on its own while the dialog was open: nothing went wrong, the status says
        // so.
        if (errorCodeOf(error) === 'backfill_no_active_run') {
          this.requestStatusRead();
          return;
        }
        this.cancelErrorKey.set(apiErrorTranslationKey(error));
        // The outcome of a DELETE with no answer is unknown — the run may be gone after all.
        if (isUnknownOutcome(error.status)) {
          this.requestStatusRead();
        }
      },
    });
  }

  private sendRemove(coverage: readonly BackfillCoverageInterval[]): void {
    const request = {};
    this.removeRequest.set(request);
    this.removeErrorKey.set(null);
    this.backfillService.removeImported(this.channelName(), coverage).subscribe({
      next: (result) => {
        if (this.removeRequest() !== request) {
          return;
        }
        this.removeRequest.set(null);
        this.removalAnnouncement.set(backfillRemovalAnnouncement(result));
        // The dialog has handed focus back to the trigger, which goes now: hand it on to the
        // outcome that takes the section's place.
        // A view that is already gone (the user left the tab before the answer) takes no focus, and
        // registering a render hook on it would throw NG0911.
        if (!this.destroyRef.destroyed) {
          afterNextRender(() => this.removalConfirmation()?.nativeElement.focus(), {
            injector: this.injector,
          });
        }
        // After the commit nothing imported is left: take it off screen at once, so a second click
        // in the window before the refetch lands cannot send a second POST.
        this.status.update(
          (current) =>
            current && {
              ...current,
              coverage: [],
              importedFrom: null,
              importedTo: null,
              importedContiguous: false,
            },
        );
        this.requestStatusRead();
      },
      error: (error: HttpErrorResponse) => {
        if (this.removeRequest() !== request) {
          return;
        }
        this.removeRequest.set(null);
        this.removeErrorKey.set(apiErrorTranslationKey(error));
        // A 409 says the status on screen is out of date; a 404 or a lost answer leaves the outcome
        // unknown (the commit may have happened). Either way the status says what is there now.
        this.requestStatusRead();
      },
    });
  }

  /**
   * The one way this section asks for a status read. `reload()` refuses while a load is running;
   * the refusal is remembered and the read issued once that load settles, so neither an SSE nudge
   * nor the read a cancel waits for can be lost.
   */
  private requestStatusRead(): void {
    this.statusReadQueued.set(!this.statusResource.reload());
  }

  /** The dialog text: the action first (the dialog has no heading), then each interval, then what
   *  stays. */
  private removeConfirmMessage(scope: RemovalScope): string {
    return [
      this.transloco.translate('backfill.remove.confirm.title'),
      this.transloco.translate('backfill.remove.confirm.lead'),
      ...scope.coverage.map((interval) =>
        this.transloco.translate('backfill.remove.confirm.interval', this.toIntervalLine(interval)),
      ),
      this.transloco.translate('backfill.remove.confirm.label'),
      this.transloco.translate('backfill.remove.confirm.keeps', {
        countingSince: this.formatDay(scope.countingSince),
      }),
      this.transloco.translate('backfill.remove.confirm.irreversible'),
    ].join('\n');
  }

  private toIntervalLine(interval: BackfillCoverageInterval): Omit<ImportedIntervalView, 'key'> {
    return {
      from: this.formatDay(interval.from),
      to: this.formatDay(lastDayOf(interval.to)),
      set: backfillSetLabel(interval),
    };
  }

  private toSentence(interval: ReplacedInterval): ReplaceSentence {
    return {
      set: backfillSetLabel(interval),
      from: this.formatDay(interval.from),
      to: this.formatDay(lastDayOf(interval.to)),
    };
  }

  private toOptionView(option: BackfillOption): OptionView {
    return {
      months: option.months,
      from: option.windowFrom,
      to: lastDayOf(option.windowTo),
      days: option.days,
      available: option.available,
      showRange: option.available,
      reasonKey: option.available ? null : (REASON_KEYS[option.reason ?? ''] ?? UNKNOWN_REASON_KEY),
    };
  }
}
