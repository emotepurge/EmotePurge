import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Dialog } from '@angular/cdk/dialog';
import { CdkVirtualScrollViewport, ScrollingModule } from '@angular/cdk/scrolling';
import { NgOptimizedImage } from '@angular/common';
import {
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { Subscription, catchError, first, merge, of, switchMap, timer } from 'rxjs';

import { ChannelService } from '../../core/channels/channel.service';
import { botsExcludedCaptionKey } from '../../core/emotes/bots-excluded-caption';
import { sharedChatSeparatedCaptionKey } from '../../core/emotes/shared-chat-separated-caption';
import { DuplicateEmoteName } from '../../core/emotes/duplicate-emote-name.model';
import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { sevenTvSyncFailureKey } from '../../core/emotes/seven-tv-sync-failure';
import { latestOnly } from '../../core/http/latest-only';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import { PointerModeService } from '../../core/pointer/pointer-mode.service';
import { dedupeImportRows, ImportRow, ImportSource } from '../../core/seven-tv/import-source';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { VoteSessionSummary } from '../../core/voting/vote-session.model';
import {
  CreateVoteSessionDialogData,
  openCreateVoteSessionDialog,
} from './create-vote-session-dialog';
import { SetStatusFlushProbeGate } from './set-status-flush-probe-gate';
import { LIVE_EVENT_TYPES, channelLiveUrl } from '../../core/live/live-event.model';
import { CHANNEL_RELOAD_DEBOUNCE_MS, liveReload } from '../../core/live/live-reload';
import { ChannelUsageSeries, EmoteUsageTotal } from '../../core/usage-stats/usage-stat.model';
import { UsageStatService } from '../../core/usage-stats/usage-stat.service';
import {
  DateRangeMenu,
  DateRangePreset,
  allTimeStart,
  toIsoDate,
} from '../../shared/datetime/date-range-menu';
import {
  EmoteDrilldownData,
  openEmoteDrilldownDialog,
} from '../../shared/emotes/emote-drilldown-dialog';
import {
  UsageTrend,
  daysInSet,
  isUnderObservation,
  usageTrend,
} from '../../shared/emotes/emote-context';
import { EmoteSprite } from '../../shared/emotes/emote-sprite';
import { EmoteSpriteAnimated } from '../../shared/emotes/emote-sprite-animated';
import { EmoteUsageFilter } from '../../shared/emotes/emote-usage-filter';
import { UsageRangeMenu } from '../../shared/emotes/usage-range-menu';
import { UsageSparkline } from '../../shared/emotes/usage-sparkline';
import {
  fillOffsetSeries,
  liveDayCaptionKey,
  liveDayCoverage,
  offsetsToDates,
  seriesPeak,
} from '../../shared/emotes/usage-series';
import {
  USAGE_BAND_FILL,
  UsageBandKey,
  groupIntoUsageBands,
  topFifthShare,
  usageBandBars,
  usageBandOf,
  usageBandThresholds,
  usageDistribution,
  usageFillPercent,
} from '../../shared/emotes/usage-bands';
import { SlotBudgetBar } from '../../shared/emotes/slot-budget-bar';
import { ExportDialogData, ExportScope, openExportDialog } from '../../shared/export/export-dialog';
import { downloadFile } from '../../shared/export/file-download';
import {
  ExportPurposeId,
  buildUsageExportPurposeDownload,
  toImportRow,
  usageExportPurposeOptions,
} from '../../shared/export/usage-export-purposes';
import {
  ATLAS_CELL_PX,
  ATLAS_ROW_PX,
  ATLAS_STICKY_TOP_PX,
  SIDECAR_GAP_PX,
  atlasColumns,
  atlasRowOfIndex,
  moveInAtlas,
  packAtlasRows,
} from '../../shared/grid/atlas-grid';
import { actionDockHasContent } from '../../shared/seven-tv/action-dock';
import { DockOutcomeAnnouncer } from '../../shared/seven-tv/dock-outcome-announcer';
import { ImportFlowDeps, startImportFlow } from '../../shared/seven-tv/import-flow';
import { ImportProgressSection } from '../../shared/seven-tv/import-progress-section';
import { importScopeIsCurrent } from '../../shared/seven-tv/import-scope';
import { importShortcutDisabled } from '../../shared/seven-tv/import-shortcut';
import {
  ImportTargetChoice,
  openImportTargetDialog,
} from '../../shared/seven-tv/import-target-dialog';
import { ImportTrigger } from '../../shared/seven-tv/import-trigger';
import { DeletableEmote, MassDeletePanel } from '../../shared/seven-tv/mass-delete-panel';
import { ListSelection } from '../../shared/selection/list-selection';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';

type SortDirection = 'asc' | 'desc';
type SortKey = 'usage' | 'lastUsed';

/**
 * Everything the target picker's continuation is allowed to know, frozen at the moment the picker
 * opened: both scopes' rows, the set they came from and the channel that owns it. Held together in
 * one object so no later addition can accidentally re-read a live signal for just one of them —
 * a mix of captured and freshly read values is precisely the defect this replaces.
 */
interface CapturedImportScope {
  readonly channelName: string;
  readonly emoteSetId: string;
  readonly selection: readonly ImportRow[];
  readonly visible: readonly ImportRow[];
}

/**
 * `openExport`'s counterpart to `CapturedImportScope` — same reasoning (see `openImportTarget`'s
 * docstring), now applying to the export dialog too since the emote-list purpose put a file path
 * that reads `emoteSetId` behind it. Holds the raw `EmoteUsageTotal` rows rather than `ImportRow`s
 * because the two usage branches (CSV/JSON) need the full totals; only the emote-list purpose
 * narrows them, inside `buildUsageExportPurposeDownload`.
 *
 * `channelName`/`from`/`to` are read from `totalsChannel`/`totalsRange`, not from
 * `channelName()`/`from()`/`to()` — those move the instant a range change or a live reload starts,
 * while `totalsChannel`/`totalsRange` only move once that reload's rows have actually landed (see
 * their declarations). Reading the live signals here would let the capture pair rows from one
 * query with the channel/range label of a newer, still in-flight one.
 */
interface CapturedExportScope {
  readonly channelName: string;
  readonly emoteSetId: string | null;
  readonly from: string;
  readonly to: string;
  readonly filtered: boolean;
  readonly selection: readonly EmoteUsageTotal[];
  readonly visible: readonly EmoteUsageTotal[];
}

// Sorting a never-used emote needs a position, not a crash. It is the deadest thing in the list, so
// it sorts as older than any real date: descending (most recent first) puts them at the very end,
// ascending puts them at the front, and either way they stay together instead of scattering.
const NEVER_USED_SORT_VALUE = Number.NEGATIVE_INFINITY;

// Joining a channel does not fill it with emotes right away: POST /join only writes the channel row
// and publishes JOIN to Redis, and the worker resolves the 7TV set a beat later. Since the overview
// navigates straight into the workspace, the user reliably landed inside that window and saw an
// empty grid that only a manual reload fixed.
//
// What ends that wait is the `channel.synced` event, not a clock: it fires the moment the worker has
// written the set, and the live subscription in the constructor already reloads status and totals
// from it. The offsets below are only the fallback for an event that never arrives — a dropped SSE
// frame, a connection reopened mid-sync. Three probes, where this used to fire fifteen two-second
// ticks: each one is a request against the very rate limit issue #33 is about, and a fourth would
// say nothing a slightly later third does not. The first covers the ordinary one-or-two-second
// sync, the second a slow 7TV, and the last closes the same 30-second window as before — after
// which the ordinary "no active emotes" state is the honest answer.
const SYNC_PROBE_DELAYS_MS = [2000, 8000, 30000];

// A shown sync-failure reason is already a true statement, not a blind wait — this is NOT the
// abolished sync burst coming back (see awaitSync below): the page has something to say the whole
// time, it just has to keep saying the right thing. Nothing tells an open page when that changes:
// SyncChannelAsync returns null both when a sync fails again (case 1: the reason itself can flip,
// e.g. no_seventv_account -> no_active_emote_set once someone registers) and when it succeeds but
// changes nothing (case 2: emoteSetSwitched and inventoryChanged both false), so `channel.synced`
// never fires for either case and the live subscription above cannot help. Polling is the fallback.
// 60 s to match the backend's own resync cadence (SevenTv:ResyncIntervalSeconds,
// SevenTvPeriodicResyncWorker) — asking faster could not possibly see a fresher answer than that
// floor allows, so there is nothing to buy by polling twice as often as the thing being polled for
// can itself change. This used to be 30 s, halving the worst-case staleness against that same
// floor; issue #33 traded that headroom back for halving the request rate a page with a visible
// reason keeps up for as long as it stays open.
const SYNC_FAILURE_RECHECK_INTERVAL_MS = 60000;

// Buckets in the distribution strip. Around a hundred is what fits across the content width at one
// readable bar plus gutter — enough to show where the curve's knee sits, few enough that each bar
// stays a bar rather than a hairline.
const DISTRIBUTION_BUCKETS = 96;

// Long enough to read, short enough that a stale notice never lingers — same value and reasoning as
// channel-workspace-layout's RESYNC_FEEDBACK_MS and admin-channels-page's own feedback timer.
const SELECTION_PRUNED_FEEDBACK_MS = 4000;

function sortableLastUsed(lastUsedDate: string | null): number {
  if (!lastUsedDate) {
    return NEVER_USED_SORT_VALUE;
  }

  const parsed = Date.parse(`${lastUsedDate}T00:00:00Z`);
  return Number.isNaN(parsed) ? NEVER_USED_SORT_VALUE : parsed;
}

@Component({
  selector: 'app-usage-stats-page',
  imports: [
    Button,
    EmptyState,
    NgOptimizedImage,
    NoticeBanner,
    ScrollingModule,
    EmoteSprite,
    EmoteSpriteAnimated,
    DockOutcomeAnnouncer,
    ImportProgressSection,
    MassDeletePanel,
    ImportTrigger,
    SlotBudgetBar,
    DateRangeMenu,
    SegmentedControl,
    UsageRangeMenu,
    UsageSparkline,
    TranslocoPipe,
  ],
  templateUrl: './usage-stats-page.html',
})
export class UsageStatsPage {
  readonly channelName = input.required<string>();

  private readonly usageStatService = inject(UsageStatService);
  private readonly emoteAdminService = inject(EmoteAdminService);
  /** Only for `filterAlreadyPresent`'s direct read against 7TV (#149 P1 fix, via `import-flow.ts`)
   *  — every other read on this page goes through `emoteAdminService`. */
  private readonly httpClient = inject(HttpClient);
  private readonly channelService = inject(ChannelService);
  private readonly languageService = inject(LanguageService);
  private readonly deleteService = inject(SevenTvDeleteService);
  private readonly restoreService = inject(SevenTvRestoreService);
  private readonly importService = inject(SevenTvImportService);
  private readonly tokenService = inject(SevenTvTokenService);
  /** Read here only for the header button's lock (#72, R1) — the template needs it too, hence
   *  `protected` rather than `private`, mirroring the same choice on the mass-delete panel and the
   *  restore panel (#70, Task 4; see docs/DECISIONS.md). */
  protected readonly arbiter = inject(SevenTvRunArbiter);
  private readonly dialog = inject(Dialog);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  /**
   * Capability, not layout: no 7TV write access without a mouse. The write token can only be
   * obtained from DevTools' local-storage view on 7tv.app, which a phone does not have — so
   * selection, mass delete and protocol re-import are desktop work, and the tap on a cell is freed
   * up to mean one thing (see onCellClick). Width stays responsible for what fits; this is
   * responsible for what can be operated.
   */
  protected readonly isCoarse = inject(PointerModeService).isCoarse;

  // The sheet element is what the column count is measured against — see atlasColumns() for why the
  // window's width was the wrong ruler. Unconditionally rendered (it wraps the loading, empty and
  // atlas states alike) so `required` can never miss it.
  private readonly sheetRef = viewChild.required<ElementRef<HTMLElement>>('sheet');
  private readonly stickyBarRef = viewChild.required<ElementRef<HTMLElement>>('stickyBar');
  private readonly viewport = viewChild(CdkVirtualScrollViewport);

  // The route guard admits 7TV editors (canViewUsageStats), but creating a vote session is a
  // management action (ChannelManagementAuthorizationFilter on the endpoint) — the button only
  // shows where the click can succeed. Same pattern as VoteSessionListPage's create form.
  private readonly permissionsResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => this.channelService.getPermissions(params),
  });
  protected readonly canManage = computed(
    () => this.permissionsResource.value()?.canManage ?? false,
  );

  // A computed, not a field read in the constructor: channelName is a required input and reading it
  // during construction throws NG0950. computed() is lazy, so the first read happens inside
  // liveReload's toObservable effect, by which time the input is set.
  private readonly liveUrl = computed(() => channelLiveUrl(this.channelName()));

  protected readonly cellPx = ATLAS_CELL_PX;
  protected readonly rowHeight = ATLAS_ROW_PX;
  protected readonly sheetWidth = signal(0);
  protected readonly columns = computed(() => atlasColumns(this.sheetWidth()));

  /** Where the sidecar pins, measured against the toolbar rather than hard-coded — the filter row
   *  wraps into one, two or three rows depending on the width, and a fixed offset would leave a gap
   *  at one end and clip the panel's first line at the other. */
  protected readonly sidecarTop = signal(ATLAS_STICKY_TOP_PX + SIDECAR_GAP_PX);

  // trackedSince is not known until the set-status response lands, so the first request uses the
  // widest span the endpoint accepts — identical rows for any channel younger than that. Once the
  // response arrives, the constructor effect below pulls from() forward to the tracking start, so
  // "all time" never keeps claiming a year of data on a channel tracked for days.
  protected readonly from = signal(allTimeStart(null));
  protected readonly to = signal(toIsoDate(new Date()));
  protected readonly sortKey = signal<SortKey>('usage');
  protected readonly sortDirection = signal<SortDirection>('desc');

  protected readonly sortKeyOptions: SegmentedControlOption[] = [
    { value: 'usage', labelKey: 'usageStats.sortByUsage' },
    { value: 'lastUsed', labelKey: 'usageStats.sortByLastUsed' },
  ];

  protected readonly sortDirectionOptions: SegmentedControlOption[] = [
    { value: 'desc', labelKey: 'usageStats.sortDescending' },
    { value: 'asc', labelKey: 'usageStats.sortAscending' },
  ];

  // "All time" is the default because this page exists to answer "has this emote ever been used".
  // A rolling 7-day window answered a different question and got it wrong twice over: it made a
  // long-serving emote that had a quiet week look like a deletion candidate, and on a freshly
  // joined channel it reached back before tracking started, so the very first thing a new user saw
  // was the incomplete-data warning banner. Owned here rather than inside the menu so the page can
  // name the range it is showing without reaching into a child; must match from()/to() above.
  protected readonly rangePreset = signal<DateRangePreset>('all');

  protected readonly emotes = signal<EmoteUsageTotal[]>([]);
  protected readonly setStatus = signal<EmoteSetStatus | null>(null);
  protected readonly activeEmoteSetId = computed(() => this.setStatus()?.activeEmoteSetId || null);
  protected readonly trackedSince = computed(() => this.setStatus()?.trackedSince ?? null);

  /** Date-only form, which is what the range menu and the vote-session ballot both speak. */
  protected readonly trackedSinceDate = computed(() => this.trackedSince()?.slice(0, 10) ?? null);

  /** `?? null` guards against an older Api response taken mid-deploy, which simply omits the field —
   *  that must read exactly like "no bot ever seen here", not throw or render `undefined`. */
  protected readonly botsExcludedKey = computed(() =>
    botsExcludedCaptionKey(this.setStatus()?.botsExcludedSince ?? null),
  );

  /** Same `?? null` guard and the same reason as `botsExcludedKey`: an older Api response taken
   *  mid-deploy omits the field, and that has to read exactly like "no shared chat ever seen
   *  here". */
  protected readonly sharedChatSeparatedKey = computed(() =>
    sharedChatSeparatedCaptionKey(this.setStatus()?.sharedChatSeparatedSince ?? null),
  );

  /** Why the last 7TV sync produced nothing, or null when it worked (or was never attempted). */
  protected readonly syncFailureReason = computed(
    () => this.setStatus()?.syncFailureReason ?? null,
  );
  protected readonly syncFailureKey = sevenTvSyncFailureKey;

  /**
   * Exact-name collisions in the channel's active 7TV set (issue #45). Guarded on
   * `setStatusChannel() === channelName()`, the same check `importScopeCurrent` uses for the
   * identical reason: `setStatus()` keeps the previous channel's answer on screen until the new
   * one's request lands (see `setStatusChannel`'s own comment), so reading `setStatus()` alone here
   * would flash the outgoing channel's collisions under the incoming channel's heading for exactly
   * that window.
   */
  protected readonly duplicateNames = computed<DuplicateEmoteName[]>(() =>
    this.setStatusChannel() === this.channelName() ? (this.setStatus()?.duplicateNames ?? []) : [],
  );
  /** Collapsed by default on every mount and every channel switch — see the constructor effect that
   *  resets it, keyed on channelName() alone so a range change or a silent reload never collapses a
   *  list the user just opened. */
  protected readonly duplicatesExpanded = signal(false);
  protected readonly duplicateNoticeKey = computed(() =>
    pluralKey(this.duplicateNames().length, 'usageStats.duplicateNames.notice'),
  );

  // The selected range reaches back further than we have been counting, so its leading part is
  // silently empty. Saying so is the difference between "this emote is dead" and "we weren't here".
  protected readonly rangeStartsBeforeTracking = computed(() => {
    // "All time" means "everything we have", not a range someone chose that happens to start too
    // early — warning about its own definition would fire on every default page load.
    if (this.rangePreset() === 'all') {
      return false;
    }

    const trackedSince = this.trackedSince();
    if (!trackedSince) {
      return false;
    }

    const trackingStart = Date.parse(trackedSince);
    const rangeStart = Date.parse(`${this.from()}T00:00:00Z`);
    return !Number.isNaN(trackingStart) && !Number.isNaN(rangeStart) && rangeStart < trackingStart;
  });
  protected readonly isLoading = signal(false);
  protected readonly skeletonCells = Array.from({ length: 48 }, (_, i) => i);
  protected readonly isAwaitingSync = signal(false);
  protected readonly errorMessage = signal<string | null>(null);

  private syncPoll?: Subscription;

  /**
   * Feedback for a silent reload's selection reconciliation (#94) — held as key+count together
   * rather than two separate signals, because both must clear atomically once the timeout fires;
   * two independent signals could leave a stale count paired with a cleared key (or vice versa) if
   * a second prune landed between their two writes. `key` is already the resolved `.one`/`.other`
   * key from pluralKey(), matching every other transient-feedback signal in the codebase
   * (resyncFeedbackKey et al.) — only the count still needs interpolating in the template.
   */
  protected readonly selectionPrunedFeedback = signal<{ key: string; count: number } | null>(null);
  private selectionPrunedFeedbackTimeout: ReturnType<typeof setTimeout> | null = null;

  // Prune, don't clear (S2-16): narrowing a filter keeps the still-visible part of the selection,
  // while anything filtered out is dropped so the delete path never holds an off-screen emote.
  protected readonly usageFilter = new EmoteUsageFilter<EmoteUsageTotal>(() =>
    this.selection.retainVisible(),
  );

  protected readonly filteredEmotes = computed(() => this.usageFilter.apply(this.emotes()));

  protected readonly sortedEmotes = computed(() => {
    const key = this.sortKey();
    const factor = this.sortDirection() === 'desc' ? -1 : 1;
    const value = (emote: EmoteUsageTotal) =>
      key === 'usage' ? emote.totalUseCount : sortableLastUsed(emote.lastUsedDate);

    const items = [...this.filteredEmotes()];
    // Name as the tiebreaker: "last used" collapses to a handful of distinct days, and without it
    // equal-day emotes would reshuffle on every refetch.
    items.sort(
      (a, b) =>
        factor * (value(a) - value(b)) ||
        a.emoteName.localeCompare(b.emoteName, undefined, { sensitivity: 'base' }),
    );
    return items;
  });

  // Derived from the WHOLE set, not from the filtered view: the weight classes are a property of
  // the channel, and they must not move under the user because they typed three letters into the
  // name filter. Otherwise an emote would change band while nothing about it changed.
  private readonly bandThresholds = computed(() =>
    usageBandThresholds(this.emotes().map((emote) => emote.totalUseCount)),
  );

  /** Usage of the whole set — the denominator every band share is measured against. */
  private readonly totalUsage = computed(() =>
    this.emotes().reduce((sum, emote) => sum + emote.totalUseCount, 0),
  );

  protected readonly bands = computed(() =>
    groupIntoUsageBands(
      this.sortedEmotes(),
      (emote) => emote.totalUseCount,
      this.bandThresholds(),
      this.totalUsage(),
    ),
  );

  /**
   * The bands of the WHOLE set, for the distribution strip's segment bar.
   *
   * A second grouping rather than a reuse of bands(): the strip describes the set ("Das ganze Set,
   * nach Nutzung gereiht"), while a band header describes what sits under it. With a name filter
   * active the two say different things on purpose.
   */
  protected readonly usageSegments = computed(() =>
    groupIntoUsageBands(
      this.emotes(),
      (emote) => emote.totalUseCount,
      this.bandThresholds(),
      this.totalUsage(),
    ).filter((band) => band.usage > 0),
  );

  /**
   * The atlas's true display order: bands first, the chosen sort within each band. Everything
   * position-dependent — shift-click ranges, keyboard navigation, the export — reads this rather
   * than sortedEmotes(), or a shift-click would select a range the user never saw as contiguous.
   */
  protected readonly atlasOrder = computed(() => this.bands().flatMap((band) => band.items));

  protected readonly rows = computed(() => packAtlasRows(this.bands(), this.columns()).rows);

  /** Fill-bar width per emote id — precomputed per band peak so the template stays a lookup. */
  protected readonly fillPercents = computed(() => {
    const map = new Map<string, number>();
    for (const band of this.bands()) {
      for (const emote of band.items) {
        map.set(emote.emoteId, usageFillPercent(emote.totalUseCount, band.peak));
      }
    }
    return map;
  });

  /** Ranked usage curve of the whole set — the orientation device above the sheet. */
  protected readonly distribution = computed(() =>
    usageDistribution(
      this.emotes().map((emote) => emote.totalUseCount),
      DISTRIBUTION_BUCKETS,
    ),
  );

  /**
   * The band each bar of the strip belongs to, so the curve carries the same four colours as the
   * headers below it. Against the strip's actual bar count, not the requested one — a set smaller
   * than the bucket budget gets one bar per emote, and the colouring has to follow that.
   */
  protected readonly bandBars = computed(() =>
    usageBandBars(
      this.emotes().map((emote) => emote.totalUseCount),
      this.bandThresholds(),
      this.distribution().length,
    ),
  );

  protected readonly concentration = computed(() =>
    topFifthShare(this.emotes().map((emote) => emote.totalUseCount)),
  );

  protected readonly deadCount = computed(
    () => this.emotes().filter((emote) => emote.totalUseCount === 0).length,
  );

  private readonly totalUses = computed(() =>
    this.emotes().reduce((sum, emote) => sum + emote.totalUseCount, 0),
  );

  /** Position in the set's usage ranking — the inspector's "#003", stable across sort changes. */
  private readonly usageRank = computed(() => {
    const ranks = new Map<string, number>();
    [...this.emotes()]
      .sort((a, b) => b.totalUseCount - a.totalUseCount)
      .forEach((emote, index) => ranks.set(emote.emoteId, index + 1));
    return ranks;
  });

  // Plural follows the number that is actually spelled first ("1 von 409 Emote", not "Emotes").
  protected readonly emoteCountKey = computed(() =>
    pluralKey(this.atlasOrder().length, 'emoteCount'),
  );

  protected readonly selection = new ListSelection(this.atlasOrder, (emote) => emote.emoteId);

  /**
   * The cell the inspector is describing, held by id rather than by object: a refetch hands out
   * fresh instances for the same rows, and an object reference would leave the inspector pinned to
   * a row that no longer exists. Falls back to the busiest emote so the line is never empty —
   * before the first hover, the top of the set is the honest thing to be looking at.
   */
  private readonly inspectedId = signal<string | null>(null);
  protected readonly inspected = computed(() => {
    const order = this.atlasOrder();
    const id = this.inspectedId();
    return (id ? order.find((emote) => emote.emoteId === id) : undefined) ?? order[0] ?? null;
  });

  protected readonly inspectedRank = computed(() => {
    const emote = this.inspected();
    return emote ? (this.usageRank().get(emote.emoteId) ?? null) : null;
  });

  protected readonly inspectedBand = computed(() => {
    const emote = this.inspected();
    return emote ? usageBandOf(emote.totalUseCount, this.bandThresholds()) : 'dead';
  });

  protected readonly inspectedShare = computed(() => {
    const emote = this.inspected();
    const total = this.totalUses();
    return emote && total > 0 ? emote.totalUseCount / total : null;
  });

  /**
   * Every emote's daily curve for the current range, fetched once (see UsageStatService.
   * getChannelSeries for why it is not one request per emote). `null` while in flight; a failure
   * leaves it null and sets seriesFailed, because a missing curve must not take the sidecar's
   * numbers with it — they come from the totals and are still true.
   *
   * Deliberately NOT refetched on live usage events, unlike the totals beside it. A 30-second flush
   * moves the last day of the curve by a few counts, which is invisible at sidecar width, while the
   * response covers the whole set — so the refetch would cost the most and show the least. The
   * curve therefore states the range as of the last range change, exactly like the drilldown
   * dialog's own cached series has always done.
   */
  private readonly channelSeries = signal<ChannelUsageSeries | null>(null);
  protected readonly seriesFailed = signal(false);
  protected readonly seriesPending = computed(
    () => this.channelSeries() === null && !this.seriesFailed(),
  );

  // "Last asked wins" for the two range-dependent readouts. Both are fired from an effect and from
  // event handlers, so two can be in flight at once — a range change on top of an unfinished load,
  // a live-event refetch overtaking the initial one. Without this, the slower answer overwrites the
  // faster one, and the sidecar cannot even notice: it takes its axis from the range echoed by the
  // response (see inspectedPoints), so a superseded answer draws a silently wrong span.
  private readonly latestTotals = latestOnly<EmoteUsageTotal[]>();
  private readonly latestSeries = latestOnly<ChannelUsageSeries>();

  /** The channel whose set status has come back *successfully*. Held as a channel rather than a
   *  flag so that navigating to another one makes the range provisional again. Deliberately not
   *  written on a failed attempt (see load()) — that used to make a transient failure permanent by
   *  making this equal the channel anyway, which silenced every later retry for it, refresh button
   *  included. */
  private readonly setStatusChannel = signal<string | null>(null);

  /** The channel whose set status request has *failed* — narrower than setStatusChannel above: it
   *  only exists to unblock the "all time" placeholder (see rangeResolved) once a failed attempt
   *  makes it clear no tracking start is coming, without also gating load()'s own retry guard. */
  private readonly setStatusFailedChannel = signal<string | null>(null);

  /** The channel the rows in `emotes()` were loaded for — the totals counterpart to
   *  setStatusChannel above, and written in the very same place the rows are (loadTotals' success
   *  branch, so a failed refetch leaves it naming the channel whose rows are still on screen).
   *  Exists because the two requests answer independently: knowing that *something* has finished
   *  loading says nothing about whether the rows and the set id describe the same channel. */
  private readonly totalsChannel = signal<string | null>(null);

  /** The `[from, to]` range the rows in `emotes()` were loaded for — totalsChannel's range
   *  counterpart, written alongside it in the very same place for the very same reason: `from()`/
   *  `to()` move the moment a range-menu change or a live `usageFlushed`/`channel.synced` event
   *  starts a reload, while `emotes()` still holds the previous response until that reload's own
   *  success branch replaces it. `openExport` reads this instead of `from()`/`to()` so the exported
   *  file cannot label one moment's rows with another moment's range. */
  private readonly totalsRange = signal<{ readonly from: string; readonly to: string } | null>(
    null,
  );

  /** The channel for which an active-set request is in flight or has already answered, success or
   *  failure. A plain field, not a signal: writing it must never itself retrigger load()'s effect —
   *  only reading channelName()/from()/to()/rangeResolved() should. A signal here would double-fire
   *  the request when a failure flips rangeResolved from false to true (see setStatusFailedChannel):
   *  that recompute reruns the effect once to release the placeholder, and if the guard reacted to
   *  the same write it would ask a second time on that very rerun. refresh() resets this explicitly,
   *  which is what lets the refresh button, and only the refresh button, ask again on an unchanged
   *  channel. */
  private requestedSetStatusFor: string | null = null;

  /** Bounds how many `usageFlushed` bursts may still trigger an active-set refetch, counted from
   *  each base status request — see the class doc for why an OR-linked staleness check on the two
   *  completion fields was asking again after nearly every flush. Reset alongside
   *  `requestedSetStatusFor` in load(), the same bookkeeping and therefore the same trigger set:
   *  mount, channel switch and explicit reload, but not a range change. */
  private readonly setStatusFlushProbeGate = new SetStatusFlushProbeGate();

  /**
   * False while "all time" still means the placeholder span rather than this channel's tracking
   * start — see from()'s declaration. The set-status request is what resolves it.
   *
   * Compares the range itself rather than just asking whether the status has landed, because the
   * correction runs in a second effect: on the flush after the status arrives, this one is already
   * up to date while from() is still the placeholder, and a "has it landed" test would wave that
   * intermediate state through. Comparing values does not care which effect runs first.
   */
  private readonly rangeResolved = computed(() => {
    if (this.rangePreset() !== 'all') {
      return true;
    }
    if (this.setStatusChannel() === this.channelName()) {
      // A channel with no tracking start keeps the placeholder, and this holds for it too.
      return this.from() === allTimeStart(this.trackedSinceDate());
    }
    // A failed attempt never learns a tracking start, so nothing will ever correct the range for
    // this channel — treating it as resolved is what stops "all time" from waiting forever.
    return this.setStatusFailedChannel() === this.channelName();
  });

  private readonly seriesByEmote = computed(
    () => new Map(this.channelSeries()?.emotes.map((entry) => [entry.emoteId, entry.days]) ?? []),
  );

  /** Channel-level, so converted once per response rather than per emote inspected. */
  protected readonly liveDayDates = computed(() => {
    const series = this.channelSeries();
    return series ? offsetsToDates(series.liveDays, series.from) : [];
  });

  protected readonly inspectedPoints = computed(() => {
    const series = this.channelSeries();
    const emote = this.inspected();
    if (!series || !emote) {
      return [];
    }
    // An emote absent from the response had no usage in the range — the same statement its absent
    // days would make, one level up. Zero-filling here is what turns that into a flat baseline.
    return fillOffsetSeries(this.seriesByEmote().get(emote.emoteId) ?? [], series.from, series.to);
  });

  /**
   * The day the inspected emote entered the set — the first day its curve may speak for. `null` when
   * 7TV reported no date: then nothing is trimmed and nothing is claimed.
   */
  protected readonly inspectedDrawFrom = computed(
    () => this.inspected()?.firstSeenAt?.slice(0, 10) ?? null,
  );

  protected readonly inspectedPeak = computed(() =>
    seriesPeak(this.inspectedPoints(), this.inspectedDrawFrom() ?? undefined),
  );

  /**
   * How many live days this emote could have been used on, and how many of those it went unused —
   * the emote-specific counterpart of the channel-wide count that used to stand under the curve and
   * read identically for every row.
   */
  protected readonly inspectedCoverage = computed(() =>
    liveDayCoverage(
      this.inspectedPoints(),
      this.liveDayDates(),
      this.inspectedDrawFrom() ?? undefined,
    ),
  );

  protected readonly inspectedLiveKey = computed(() =>
    liveDayCaptionKey(this.inspectedCoverage(), this.liveDayDates().length > 0),
  );

  /** Channel-wide and range-dependent, so it is stated once at the top rather than on every emote. */
  protected readonly liveDaysInRangeKey = computed(() => {
    const count = this.liveDayDates().length;
    return count > 0 ? pluralKey(count, 'usageStats.liveDaysInRange') : null;
  });

  /** The single tab stop in the grid (WAI-ARIA grid pattern) — arrow keys move it. */
  protected readonly activeIndex = signal(0);

  // Resolved items, not selection.selectedKeys(): the delete engine needs sevenTvEmoteId and the
  // display name, which only the loaded row carries. Safe because every path that removes a row
  // from the atlas while keeping the page open (filter change, reload, finished delete) clears the
  // selection — so nothing selected can be missing here.
  protected readonly selectedForDelete = computed<DeletableEmote[]>(() =>
    this.selection.selectedItems().map((emote) => ({
      emoteId: emote.emoteId,
      sevenTvEmoteId: emote.sevenTvEmoteId,
      name: emote.emoteName,
    })),
  );

  /**
   * The action bar is bound to the selection, but it must not disappear the moment a finished
   * delete clears it: the run summary carries the protocol download, and that file is the only
   * durable record of what was removed. So a live or settled run keeps the dock mounted.
   *
   * Mirrors each half's own template gate rather than just asking whether something is selected —
   * see actionDockHasContent for why the bar would otherwise render empty. The import clause repeats
   * `app-import-progress-section`'s gate exactly, `run()` included, since that component draws
   * nothing without a run to name.
   *
   * `importNoticePending`/`restoreNoticePending` (#149 P2, independent review) cover the one case
   * neither `deleteShown`/`restoreShown`/`importShown` can: a fresh pre-run duplicate check
   * (`already-present-filter.ts`) that filtered away *every* row. The engine then refuses to start
   * — no run, no queue — so without these two flags the dock (and the notice-carrying section/panel
   * inside it) would never mount for exactly the outcome the notice exists to report, and a user who
   * confirmed a restore of duplicates-only rows would see nothing happen at all.
   */
  protected readonly dockVisible = computed(() =>
    actionDockHasContent({
      hasActiveSet: this.activeEmoteSetId() !== null,
      markedCount: this.selection.selectedKeys().length,
      deleteShown: this.deleteService.isRunning() || this.deleteService.queue().length > 0,
      restoreShown: this.restoreService.isRunning() || this.restoreService.queue().length > 0,
      // An import copies INTO this channel's set only when this channel is the chosen target — but
      // the run stays visible on every usage-stats page it is opened from (R9), source included, so
      // its start does not silently disappear the moment the picker closes. Deliberately NOT gated
      // on the set status, which is the whole point of the section sitting outside that gate.
      importShown:
        this.importService.run() !== null &&
        (this.importService.isRunning() || this.importService.queue().length > 0),
      importNoticePending: this.importService.duplicateNoticePending(),
      restoreNoticePending: this.restoreService.duplicateNoticePending(),
    }),
  );

  /**
   * Whether the push scope this page would capture still describes the channel in the URL — see
   * importScopeIsCurrent for the window this closes and for why neither isLoading() nor the set
   * status alone would do it. Gates the copy button visibly (a silently inert button reads as a
   * broken one) and openImportTarget itself.
   */
  protected readonly importScopeCurrent = computed(() =>
    importScopeIsCurrent(this.channelName(), this.setStatusChannel(), this.totalsChannel()),
  );

  /**
   * Count of the grid selection that would actually be captured by the dock's copy shortcut —
   * built on `selection.selectedItems()`, NOT the raw `selection.selectedKeys()`. A silent totals
   * reload (the `usageFlushed`/`channel.synced` live-event path or the sync-recheck poll, both via
   * `loadTotals(..., { preserveSelection: true })`) can drop rows out of `atlasOrder()` — e.g. an
   * emote deleted externally, which the totals query filters via `!e.IsArchived` — without ever
   * touching the raw key set, since `preserveSelection` only skips `selection.clear()`; it does not
   * reconcile the keys against the new rows. `selectedKeys()` stays at the old size regardless.
   * Gating the shortcut and its label on the raw key count would let `importShortcutLocked` report
   * "unlocked" with zero resolvable rows, so a click runs into `openImportTarget`'s
   * `captured.selection.length === 0` guard and silently does nothing — no dialog, no feedback. If
   * only some rows survive, the label would announce more emotes than the run actually copies.
   */
  protected readonly importShortcutSelectionCount = computed(
    () => this.selection.selectedItems().length,
  );

  /**
   * Whether the dock's copy shortcut (Designsprache §8.7, an allowance on revocation rather than a
   * requirement) is disabled.
   * `!isCoarse()` and an active 7TV set are deliberately not part of this — the shortcut only ever
   * renders inside the dock's own `!isCoarse()` gate and the marking half's `activeEmoteSetId()`
   * gate, so re-checking either here would test a condition it can never actually violate. See
   * importShortcutDisabled for why the remaining three locks are exactly the header button's, and
   * importShortcutSelectionCount for why the count feeding it is not the raw selection size.
   */
  protected readonly importShortcutLocked = computed(() =>
    importShortcutDisabled({
      selectionCount: this.importShortcutSelectionCount(),
      importScopeCurrent: this.importScopeCurrent(),
      hasActiveRun: this.arbiter.activeRun() !== null,
    }),
  );

  /** Occupied slots after the pending selection would be deleted — the dock's one number. */
  protected readonly projectedSlots = computed(() => {
    const status = this.setStatus();
    if (!status || status.capacity === null) {
      return null;
    }
    return {
      projected: Math.max(status.occupiedSlots - this.selection.selectedKeys().length, 0),
      capacity: status.capacity,
    };
  });

  constructor() {
    effect(() => {
      this.load(this.channelName(), this.from(), this.to(), this.rangeResolved());
    });

    // Keyed on channelName() alone, deliberately separate from load()'s effect above: that one also
    // reruns on a bare range correction, and an expanded collision list the user just opened must
    // not collapse under them just because "all time" resolved against the tracking start a moment
    // later. A genuine channel switch is the only thing that should re-collapse it.
    effect(() => {
      this.channelName();
      this.duplicatesExpanded.set(false);
    });

    // A selection made in a desktop window would otherwise survive invisibly into the touch mode and
    // reappear on the way back.
    effect(() => {
      if (this.isCoarse()) {
        this.selection.clear();
      }
    });

    // Resolves "all time" against the tracking start as soon as it is known — the initial from()
    // is only a placeholder (see its declaration). Every consumer of from() (grid request,
    // drilldown, export, vote-session prefill) inherits the corrected value. Re-selecting "all" in
    // the menu writes the same date again, which a signal treats as no change — so this cannot loop.
    effect(() => {
      const trackedSinceDate = this.trackedSinceDate();
      if (this.rangePreset() === 'all' && trackedSinceDate) {
        this.from.set(allTimeStart(trackedSinceDate));
      }
    });

    // The column count follows the element that actually holds the cells. The incumbent grid read
    // window.innerWidth while the shell capped content at 1024 px, which is how a 2560 px monitor
    // ended up with eight 113 px cards in a 992 px container. The cap is 1280 px since #93; the
    // numbers here are the 2026-08-06 measurement and stay as measured.
    effect((onCleanup) => {
      const element = this.sheetRef().nativeElement;
      this.sheetWidth.set(element.clientWidth);
      const observer = new ResizeObserver((entries) => {
        this.sheetWidth.set(entries[0].contentRect.width);
      });
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });

    effect((onCleanup) => {
      const element = this.stickyBarRef().nativeElement;
      const measure = () =>
        this.sidecarTop.set(ATLAS_STICKY_TOP_PX + element.offsetHeight + SIDECAR_GAP_PX);
      measure();
      const observer = new ResizeObserver(measure);
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });

    // A shorter list must not leave the tab stop pointing past its end — the next arrow key would
    // then find no row to move from and the grid would be unreachable by keyboard.
    effect(() => {
      if (this.activeIndex() >= this.atlasOrder().length) {
        this.activeIndex.set(0);
      }
    });

    this.destroyRef.onDestroy(() => {
      this.syncPoll?.unsubscribe();
      this.resetSelectionPrunedFeedback();
    });

    // Live refresh after the worker's usage flush and after real emote-inventory changes
    // (`channel.synced` only fires when a sync actually changed something — add/remove on 7TV,
    // set swap, mass delete).
    // The reload is deliberately quiet: neither the selection nor the skeleton may move under a
    // user who did not ask for anything — this update arrives unrequested.
    liveReload(this.liveUrl, {
      accept: [LIVE_EVENT_TYPES.usageFlushed, LIVE_EVENT_TYPES.channelSynced],
      debounceMs: CHANNEL_RELOAD_DEBOUNCE_MS,
    }).subscribe((seen) => {
      this.loadTotals(this.channelName(), this.from(), this.to(), {
        preserveSelection: true,
        silent: true,
      });
      // A sync can move the active set id, its capacity or the occupied-slot count — nothing else in
      // EmoteSetStatus moves on a sync, so it always earns a refetch.
      if (seen.has(LIVE_EVENT_TYPES.channelSynced)) {
        // This event is what awaitSync is really waiting for — the probes are only there for the
        // case where it never shows up. The totals have just been refetched above, so letting the
        // remaining probes run would only ask the same question again. This path is unconditional
        // and does not touch setStatusFlushProbeGate — a sync is a real inventory change, not the
        // bounded "did the flush catch up yet" question the gate answers.
        this.stopAwaitingSync();
        this.refreshSetStatus();
      } else if (
        seen.has(LIVE_EVENT_TYPES.usageFlushed) &&
        this.setStatusFlushProbeGate.shouldRefreshOn(this.setStatus())
      ) {
        // A flush can move the same DTO's botsExcludedSince and sharedChatSeparatedSince: each is
        // set the moment a flush first counts that kind of usage for this channel, not by a sync.
        // Both are MINs over growing dates and therefore provably done changing once they hold a
        // date, but most channels never take part in a shared-chat session at all, so
        // sharedChatSeparatedSince alone staying null must not license asking forever — the gate
        // caps this at the first few bursts after a mount/channel switch (see its own doc) instead
        // of deriving "still worth asking" from the data.
        this.refreshSetStatus();
      }
    });

    // Keeps a shown sync-failure reason current — see the constant's comment for why polling is the
    // only option here. A signal effect already gives "start when true, stop when false, restart on
    // change" for free: syncFailureReason() and channelName() are the only two reads before the
    // timer is built, so a resolved reason, a cleared reason or a channel switch all clean up the
    // same way, through onCleanup — which also cancels an in-flight request, not just future ticks,
    // so a channel switch mid-poll cannot land a foreign channel's status on this page (getSetStatus
    // is a plain HttpClient call with no shareReplay, so unsubscribing does cancel it).
    effect((onCleanup) => {
      const reason = this.syncFailureReason();
      if (!reason) {
        return;
      }
      const channelName = this.channelName();

      const recheck = timer(SYNC_FAILURE_RECHECK_INTERVAL_MS, SYNC_FAILURE_RECHECK_INTERVAL_MS)
        .pipe(
          switchMap(() =>
            this.emoteAdminService.getSetStatus(channelName).pipe(catchError(() => of(null))),
          ),
        )
        .subscribe((status) => {
          if (!status) {
            return;
          }
          this.setStatus.set(status);
          // Mirrors awaitSync's own resolution: a resolved set id needs the grid filled in, which a
          // status refresh alone does not do. Read now rather than closed over at effect start,
          // because the range can change while this keeps polling (see the comment above — range
          // changes do not re-run this effect) and a stale from/to would fetch the wrong window.
          if (status.activeEmoteSetId) {
            this.loadTotals(channelName, this.from(), this.to(), {
              preserveSelection: true,
              silent: true,
            });
          }
        });
      onCleanup(() => recheck.unsubscribe());
    });
  }

  // See VoteSessionDetailPage.trackRow for the full reasoning: rows() rebuilds fresh row arrays on
  // every recompute, so tracking by index (not identity) keeps the row views stable and avoids
  // rebuilding every cell — the inner @for (… track emote.emoteId) still reconciles row content
  // correctly on a resize.
  protected trackRow(index: number): number {
    return index;
  }

  protected refresh(): void {
    // Forces a fresh active-set request even though the channel has not changed: load()'s guard
    // above only exists to stop a bare range correction from asking twice, and must not also
    // swallow the one deliberate retry a previously failed request needs.
    this.requestedSetStatusFor = null;
    this.load(this.channelName(), this.from(), this.to(), this.rangeResolved());
  }

  /** Signature takes `string` because SegmentedControl is untyped by design — one control for every
   *  small mutually-exclusive set, and a generic there would have to be threaded through every use. */
  protected setSortKey(key: string): void {
    if (this.sortKey() === key) {
      return;
    }

    this.sortKey.set(key as SortKey);
    // Every key change starts at "most first" again. Carrying an ascending order over to a
    // different column silently answers a question nobody asked twice.
    this.sortDirection.set('desc');
    // A different sort key reorders the whole grid, and the selection carries shift-range anchors
    // into that new order — keeping it would let the next shift-click sweep up rows the user never
    // saw next to each other. Flipping the direction alone reverses a list they are still looking
    // at, so that keeps the selection (see setSortDirection, which does not clear).
    this.selection.clear();
  }

  /** Keeps the selection on purpose: reversing a list the user is still looking at does not move
   *  anything out from under a shift-range anchor the way a different column does. */
  protected setSortDirection(direction: string): void {
    this.sortDirection.set(direction as SortDirection);
  }

  protected inspect(emote: EmoteUsageTotal): void {
    this.inspectedId.set(emote.emoteId);
  }

  /** Pointer or focus landing on a cell makes it both the inspected row and the grid's tab stop. */
  protected onCellFocus(emote: EmoteUsageTotal, index: number): void {
    this.inspectedId.set(emote.emoteId);
    this.activeIndex.set(index);
  }

  /**
   * On a fine pointer a click selects. On a coarse one it opens the drilldown instead — there is no
   * write path left to select for once the 7TV token can no longer be copied off a phone, so the
   * whole cell means exactly one thing there. Either way it pins the inspector to the clicked cell.
   *
   * The pinning is what makes the inspector usable without a pointer at all: on a touch screen
   * there is no hover to drive it, so without this the line would keep describing the busiest emote
   * no matter what the user tapped. Safari also does not focus a button on click, so relying on the
   * focus handler alone would leave the same gap on a desktop browser.
   */
  protected onCellClick(emote: EmoteUsageTotal, index: number, event: MouseEvent): void {
    this.inspectedId.set(emote.emoteId);
    this.activeIndex.set(index);

    // On a coarse pointer the cell has only one meaning left. Returning before the selection call
    // rather than gating the whole method keeps inspectedId/activeIndex in sync, which is what the
    // sidecar and the roving tab stop read.
    if (this.isCoarse()) {
      this.openDrilldown(emote);
      return;
    }

    this.selection.onRowClick(emote, event);
  }

  protected onAtlasKeydown(event: KeyboardEvent): void {
    // Space marks, Enter opens the history. They used to do the same thing, because both fire a
    // native click on a <button> — which left the keyboard with no way at all to reach the per-cell
    // trigger without giving it a second tab stop in a grid that deliberately has one.
    if (event.key === 'Enter') {
      const emote = this.atlasOrder()[this.activeIndex()];
      if (emote) {
        // Before the browser turns this keydown into a click on the cell, which would select it.
        event.preventDefault();
        this.openDrilldown(emote);
      }
      return;
    }

    const next = moveInAtlas(this.rows(), this.activeIndex(), event.key);
    if (next === null) {
      return;
    }

    // Only now: an unhandled key (typing into nothing, Tab out of the grid) must keep its default.
    event.preventDefault();
    this.activeIndex.set(next);
    this.focusCell(next);
  }

  protected selectBand(key: UsageBandKey): void {
    const band = this.bands().find((candidate) => candidate.key === key);
    if (band) {
      this.selection.selectMany(band.items);
    }
  }

  protected fillPercent(emote: EmoteUsageTotal): number {
    return this.fillPercents().get(emote.emoteId) ?? 0;
  }

  /** Compact figure printed onto the sprite — "9,4k" fits a 64 px cell, "9412" does not. */
  protected shortCount(count: number): string {
    const locale = toLocale(this.languageService.lang());
    return count >= 1000
      ? `${(count / 1000).toLocaleString(locale, { maximumFractionDigits: 1 })}k`
      : count.toLocaleString(locale);
  }

  protected formatCount(count: number): string {
    return count.toLocaleString(toLocale(this.languageService.lang()));
  }

  protected formatPercent(share: number): string {
    return share.toLocaleString(toLocale(this.languageService.lang()), {
      style: 'percent',
      maximumFractionDigits: share < 0.1 ? 1 : 0,
    });
  }

  /**
   * Whole percent for a band's share, its own function rather than a mode of formatPercent(): that
   * one also renders the concentration sentence, which is deliberately allowed a decimal.
   *
   * A live band that rounds to nothing reads as "<1 %" instead of "0 %" — with a name filter active
   * a band can legitimately be down to a fraction of a percent, and zero is what the dead band
   * means.
   */
  protected formatBandShare(share: number): string {
    const locale = toLocale(this.languageService.lang());
    if (share > 0 && share < 0.005) {
      return `<${(0.01).toLocaleString(locale, { style: 'percent' })}`;
    }
    return share.toLocaleString(locale, { style: 'percent', maximumFractionDigits: 0 });
  }

  protected bandFill(band: UsageBandKey): string {
    return USAGE_BAND_FILL[band];
  }

  protected trendFor(emote: EmoteUsageTotal): UsageTrend {
    const trackedSince = this.trackedSince();
    if (!trackedSince) {
      return 'unknown';
    }

    return usageTrend({
      totalUseCount: emote.totalUseCount,
      previousWindowUseCount: emote.previousWindowUseCount,
      firstSeenAt: emote.firstSeenAt,
      windowStart: this.from(),
      windowEnd: this.to(),
      trackedSince,
    });
  }

  protected isUnderObservation(emote: EmoteUsageTotal): boolean {
    return isUnderObservation(emote.firstSeenAt, new Date());
  }

  /** Whole days the emote has been in the set; floored at 0 so a clock skew cannot read as "-1". */
  protected observationDays(emote: EmoteUsageTotal): number {
    return Math.max(daysInSet(emote.firstSeenAt, new Date()) ?? 0, 0);
  }

  // Day zero gets its own wording: "seit 0 Tagen" is not a sentence anyone writes.
  protected observationLabelKey(emote: EmoteUsageTotal): string {
    const days = this.observationDays(emote);
    return days === 0
      ? 'usageStats.observationToday'
      : pluralKey(days, 'usageStats.observationAge');
  }

  protected formatDate(iso: string | null): string {
    if (!iso) {
      return '—';
    }

    // A bare yyyy-MM-dd value (lastUsedDate, peak.date, botsExcludedSince, sharedChatSeparatedSince
    // — all date-only, no time
    // of their own) must read as local midnight, not UTC midnight: `new Date('yyyy-MM-dd')` parses
    // as the latter, and toLocaleDateString then renders it in the viewer's zone, which reads as the
    // previous day for anyone west of UTC. A value that already carries a time part (e.g.
    // trackedSince, a full UTC timestamp) has its own offset and must parse unshifted, so only the
    // 10-character date-only form gets the local-midnight treatment.
    const date =
      iso.length === 10
        ? new Date(Number(iso.slice(0, 4)), Number(iso.slice(5, 7)) - 1, Number(iso.slice(8, 10)))
        : new Date(iso);

    // LOCALE_ID is bootstrap-time static and cannot follow a runtime language switch, so dates go
    // through toLocale() — same as the admin pages.
    return date.toLocaleDateString(toLocale(this.languageService.lang()), {
      dateStyle: 'medium',
    });
  }

  protected onDeleted(deletedIds: string[]): void {
    this.emotes.update((items) => items.filter((item) => !deletedIds.includes(item.emoteId)));
    // Freed slots are shown right away rather than waiting for the channel.synced round trip the
    // bookkeeping call triggers — the emptied bar is the feedback the delete was run for. The
    // refetch that follows a moment later confirms or corrects it.
    this.setStatus.update((status) =>
      status
        ? { ...status, occupiedSlots: Math.max(status.occupiedSlots - deletedIds.length, 0) }
        : status,
    );
    this.selection.clear();
  }

  // Hands the dialog a LIVE view of the selection (#132), not a snapshot: this page keeps reloading
  // while the dialog is open (usageFlushed/channel.synced can prune a marked emote that was archived
  // from outside the tab), and a frozen array would keep offering ids the backend's all-or-nothing
  // check would reject on every retry. loadTotals still clears the whole selection outright on a
  // channel/date-range change — that path is unreachable while a modal dialog has focus, so it is
  // not a case the dialog itself needs to guard against.
  protected openCreateVoteSession(): void {
    if (this.selection.selectedKeys().length === 0) {
      return;
    }

    const data: CreateVoteSessionDialogData = {
      channelName: this.channelName(),
      emoteIds: this.selection.selectedKeys,
      // The dialog turns this into the session's "count usage from" prefill. On the "all time"
      // preset from() already equals the tracking start (the constructor effect keeps it there),
      // so it is a date a human would recognise on every path.
      usageFromDate: this.from(),
    };
    openCreateVoteSessionDialog(this.dialog, data).closed.subscribe((created) => {
      if (created) {
        this.selection.clear();
        this.router.navigate(['/channels', this.channelName(), 'vote-sessions', created.id]);
      }
    });
  }

  // Opened from the inspector, not from the cell: the cell click belongs to the selection, and a
  // 64 px sprite has no room for a second control that would not be a mis-click waiting to happen.
  // The dialog loads the series on its own through the service cache, so nothing is fetched until
  // the user explicitly asks for the history.
  protected openDrilldown(emote: EmoteUsageTotal): void {
    const data: EmoteDrilldownData = {
      channelName: this.channelName(),
      from: this.from(),
      to: this.to(),
      emoteId: emote.emoteId,
      emoteName: emote.emoteName,
      imageUrl: emote.imageUrl,
      firstSeenAt: emote.firstSeenAt,
      previousWindowUseCount: emote.previousWindowUseCount,
      trackedSince: this.trackedSince(),
    };
    openEmoteDrilldownDialog(this.dialog, data);
  }

  /**
   * Exports the *visible* list (filtered + sorted, in atlas order) by default, or — chosen in the
   * dialog — the current selection: the same rows that drive mass-delete and vote-session
   * creation. Client-side serialization on purpose — the read model is already loaded, and a
   * download must never see more than the page does (A12).
   *
   * Sorted by purpose, not by format (#141): usage figures as CSV or JSON, or — when an active
   * 7TV set makes it fulfillable (E3) — the emote list to re-import elsewhere. An option that
   * fails on submit is exactly the trap E3 rules out, which is why the third row is a visibility
   * check rather than an always-shown option.
   *
   * Everything is captured *before* the dialog opens, for the same reason `openImportTarget`
   * captures early (see its docstring): this page keeps reloading while the dialog is open
   * (`usageFlushed`, `channel.synced`), and the keyed selection deliberately survives that reload.
   * Reading `rows`/`filtered`/the scope only after close would let a later reload swap in
   * different rows under an unchanged-looking selection, silently pairing them with the
   * already-captured `emoteSetId` — invisible, because a surviving selection looks exactly like an
   * unchanged one. Capturing up front also means the dialog's own `rowCount`/`selectionCount`
   * (read from this same capture) can no longer promise a count the download later disagrees with.
   *
   * `channelName`/`from`/`to` are captured from `totalsChannel`/`totalsRange`, not from
   * `channelName()`/`from()`/`to()`: `load()` sets `isLoading` without clearing `emotes()`, so an
   * in-flight reload (a range change, a channel switch, a live event) leaves `atlasOrder()` still
   * showing the previous query's rows while the route/range signals already report the next one.
   * Reading those live would label one moment's rows with another moment's query — see
   * `totalsChannel`'s and `totalsRange`'s declarations, which solve the same problem for the import
   * push already.
   *
   * `trendFor` stays a live callback, deliberately not captured — the trend column is derived from
   * live state at serialization time, same as before (E4).
   *
   * The two decisions this method used to make itself — which purposes are on offer, and what each
   * one serializes — now live in `shared/export/usage-export-purposes.ts` as pure functions with
   * their own spec (Regel 12). What is left here is capture, opening the dialog, and handing the
   * choice to that module.
   */
  protected openExport(): void {
    // Falls back to the live signals only in the state atlasOrder().length === 0 already rules out
    // for the button that calls this (see the template): before the very first totals response,
    // totalsChannel()/totalsRange() are still null and there are no rows to mislabel anyway.
    const range = this.totalsRange();
    const captured: CapturedExportScope = {
      channelName: this.totalsChannel() ?? this.channelName(),
      emoteSetId: this.activeEmoteSetId(),
      from: range?.from ?? this.from(),
      to: range?.to ?? this.to(),
      filtered: this.usageFilter.isAnyActive(),
      selection: this.selection.selectedItems(),
      visible: this.atlasOrder(),
    };

    // E3: the emote-list purpose is only offered when there is a set to source it from and the
    // capture still matches the channel that set belongs to — see `usageExportPurposeOptions`.
    const emoteListOfferable = captured.emoteSetId !== null && this.importScopeCurrent();
    const options = usageExportPurposeOptions(emoteListOfferable);

    const data: ExportDialogData<ExportPurposeId> = {
      rowCount: captured.visible.length,
      filtered: captured.filtered,
      selectionCount: captured.selection.length,
      // Whoever can open this page sees every usage figure — nothing to explain away here.
      noticeKeys: [],
      optionsLegendKey: 'export.purposeLabel',
      options,
    };
    openExportDialog(this.dialog, data).closed.subscribe((choice) => {
      if (!choice) {
        return;
      }
      const rows = choice.scope === 'selection' ? captured.selection : captured.visible;
      const download = buildUsageExportPurposeDownload(choice.optionId, {
        channelName: captured.channelName,
        emoteSetId: captured.emoteSetId,
        from: captured.from,
        to: captured.to,
        filtered: captured.filtered,
        rows,
        scope: choice.scope,
        trendFor: (row) => this.trendFor(row),
      });
      if (download === null) {
        // Unreachable: the emote-list option is only offered when the capture carried a set id
        // (E3) — see `buildUsageExportPurposeDownload`'s own docstring for why this stays a
        // narrowed no-op rather than an assertion.
        return;
      }
      downloadFile(download.filename, download.content, download.mimeType);
    });
  }

  /**
   * The push entry point (#72, K3): pick a scope and a destination, then either save the rows as a
   * file or hand them to the confirm-and-run flow. `selectionCount` gates the scope radiogroup the
   * same way `openExport` does — unless `forcedScope` is given, in which case the dialog skips the
   * radiogroup entirely and closes with exactly that scope (design doc §8.7). The dock's copy
   * shortcut is the one caller that passes `'selection'`; the header button passes nothing, keeping
   * today's behaviour. Both calls go through this one method rather than each freezing their own
   * scope, so the capture discipline below cannot drift between the two entry points.
   *
   * Everything the continuation needs is read *here*, before the dialog opens, and never again
   * afterwards. Unlike the export dialog, this page keeps updating while the picker is open: a
   * `usageFlushed` or `channel.synced` event reloads the grid, and the keyed selection deliberately
   * survives that reload. Reading the rows after the close would therefore let the dialog count one
   * set of emotes while the run copies another — silently, because a surviving selection looks
   * exactly like an unchanged one. The same read pairs the already-captured `emoteSetId` (and
   * channel name) with rows that may no longer belong to it.
   */
  protected openImportTarget(forcedScope?: ExportScope): void {
    const emoteSetId = this.activeEmoteSetId();
    if (emoteSetId === null || !this.importScopeCurrent()) {
      // The header button is gated on both of these, so this only guards against a click that
      // outraces a channel switch. The scope check is what keeps a mid-switch capture from pairing
      // the new channel's name with the previous one's set id and rows — see importScopeIsCurrent.
      return;
    }

    const captured: CapturedImportScope = {
      channelName: this.channelName(),
      emoteSetId,
      selection: this.selection.selectedItems().map(toImportRow),
      visible: this.atlasOrder().map(toImportRow),
    };
    if (forcedScope === 'selection' && captured.selection.length === 0) {
      // The dock shortcut's own template disables the button on an empty selection
      // (importShortcutLocked/importShortcutDisabled) — this only guards a click that outraces
      // that, the same role the emoteSetId/importScopeCurrent check above plays for the header.
      return;
    }

    const data = {
      currentChannelName: captured.channelName,
      visibleCount: captured.visible.length,
      selectionCount: captured.selection.length,
      forcedScope,
    };
    openImportTargetDialog(this.dialog, data).closed.subscribe((choice) => {
      if (!choice) {
        return;
      }
      this.startImportFromChoice(captured, choice);
    });
  }

  // The delete run finished on 7TV, but the backend could not confirm it — refetch instead of
  // filtering locally, so the list never claims a state the server does not share.
  protected onReloadRequested(): void {
    this.selection.clear();
    this.refresh();
  }

  /**
   * Moves DOM focus onto a cell that the virtual scroller may not have rendered yet.
   *
   * The straightforward `querySelector().focus()` covers every move inside the mounted range, which
   * is the common case; only a jump past the buffer (Home, End, an arrow at the edge) needs the
   * viewport scrolled first and the focus deferred until CDK has mounted the new range.
   */
  private focusCell(index: number): void {
    const find = () =>
      this.sheetRef().nativeElement.querySelector<HTMLElement>(`[data-atlas-index="${index}"]`);

    const rendered = find();
    if (rendered) {
      rendered.focus();
      return;
    }

    const rowIndex = atlasRowOfIndex(this.rows(), index);
    if (rowIndex < 0) {
      return;
    }
    this.viewport()?.scrollToIndex(rowIndex);
    requestAnimationFrame(() => find()?.focus());
  }

  /**
   * `openImportTarget`'s continuation once a target has been chosen. Works exclusively off the
   * scope captured before the dialog opened (see there) — the run started here reads the very
   * same rows the dialog counted, not whatever the grid holds by the time this fires.
   */
  private startImportFromChoice(captured: CapturedImportScope, choice: ImportTargetChoice): void {
    const rows = choice.scope === 'selection' ? captured.selection : captured.visible;
    const deduped = dedupeImportRows(rows);
    const source: ImportSource = {
      origin: { kind: 'channel', channelName: captured.channelName },
      rows: deduped.rows,
      duplicatesCollapsed: deduped.duplicatesCollapsed,
      // The channel grid only ever supplies rows that already passed server-side EmoteListItem
      // validation — nothing here can be invalid the way a parsed file's rows can be (R6).
      discardedRows: 0,
    };

    const deps: ImportFlowDeps = {
      dialog: this.dialog,
      emoteAdminService: this.emoteAdminService,
      httpClient: this.httpClient,
      tokenService: this.tokenService,
      importService: this.importService,
      arbiter: this.arbiter,
    };
    startImportFlow(deps, source, choice.channelName);
  }

  // Quiet counterpart to the set-status fetch in load(): no sync-poll, and a failed refetch keeps
  // the current value — this runs unrequested, so it must never take the mass-delete panel away
  // over a transient error.
  //
  // The channel is frozen at request time because a live event can fire this while the user has
  // already navigated to another channel within the same route (the import summary's "open target
  // channel" link, see importScopeIsCurrent) — a late answer for the old channel must not land
  // under the new one's name. setStatusChannel is written alongside setStatus on success, mirroring
  // load()'s own success branch: a channel that only just recovered from a failed status fetch
  // needs exactly this write to finally be allowed to claim it.
  private refreshSetStatus(): void {
    const channelName = this.channelName();
    this.emoteAdminService.getSetStatus(channelName).subscribe({
      next: (status) => {
        if (this.channelName() !== channelName) {
          return;
        }
        this.setStatus.set(status);
        this.setStatusChannel.set(channelName);
      },
      error: () => undefined,
    });
  }

  private load(channelName: string, from: string, to: string, rangeResolved: boolean): void {
    // A drilldown series cached against the previous channel or range must not survive into this one.
    this.usageStatService.clearSeriesCache();
    this.isLoading.set(true);
    this.errorMessage.set(null);
    // A selection-pruned notice (#94) names emotes from the *previous* channel/range's selection —
    // this method is the constructor effect's only entry point, so it runs on every channel switch
    // and every date-range change, and `loadTotals` below already clears the selection outright on
    // both (see its non-`preserveSelection` branch). A standing notice would otherwise misattribute
    // itself to whatever channel happens to be on screen when its timeout fires (#94 follow-up P3).
    // The two callers that must NOT lose a just-set notice — the live-reload subscription and the
    // sync-failure recheck poll — both call `loadTotals(..., { preserveSelection: true })` directly
    // and never go through this method, so they are unaffected.
    this.resetSelectionPrunedFeedback();

    // The set status is bound to the channel, not to the range: requestedSetStatusFor already names
    // the channel of the last status request (in flight, successful or failed), so a range-only
    // rerun of this effect (the "all time" correction below, or a manual from/to change) finds it
    // already equal to channelName and skips this whole block — only a channel switch, or an
    // explicit refresh() resetting the field, clears that equality. This is what turns the second
    // "all time" effect run from a duplicate active-set request into totals/series only, and it is
    // why the wait for a first sync is ended here and nowhere else: cancelling it on a range change
    // too would kill it one frame after it started, with no status request left on that second run
    // to start it again.
    if (this.requestedSetStatusFor !== channelName) {
      this.requestedSetStatusFor = channelName;
      this.setStatusFlushProbeGate.reset();
      this.stopAwaitingSync();
      this.emoteAdminService.getSetStatus(channelName).subscribe({
        next: (status) => {
          this.setStatus.set(status);
          this.setStatusChannel.set(channelName);
          // An empty id means SevenTvSyncService has not written a set for this channel. Only worth
          // waiting on while there is no reason: with one, the answer is already final — no sync will
          // ever produce an id until the cause is fixed on 7TV, and channel.synced brings us back
          // (see the live subscription in the constructor) the moment it is.
          if (!status.activeEmoteSetId && !status.syncFailureReason) {
            this.awaitSync(channelName);
          }
        },
        // setStatusChannel is deliberately left as-is here — see its own comment. Marking only
        // setStatusFailedChannel keeps "all time" from waiting forever (the honest placeholder
        // fallback, unchanged) without also telling requestedSetStatusFor's guard above that this
        // channel is done asking: a later load() call — most importantly the refresh button, which
        // resets requestedSetStatusFor itself — must still be free to try again.
        error: () => {
          this.setStatus.set(null);
          this.setStatusFailedChannel.set(channelName);
        },
      });
    }

    // Under "all time" the range is still the placeholder span until the status above names the
    // tracking start. Asking now would aggregate a year of rows for a channel counted for days and
    // then discard the answer the moment the corrected range re-runs this effect. The subscription
    // above is what flips rangeResolved, so this always resumes.
    if (!rangeResolved) {
      return;
    }

    this.loadTotals(channelName, from, to);
    this.loadChannelSeries(channelName, from, to);
  }

  // No error surface of its own: the sidecar simply carries no curve, and the page's numbers are
  // unaffected. Raising a banner here would report a failure of the secondary readout as a failure
  // of the page.
  private loadChannelSeries(channelName: string, from: string, to: string): void {
    this.channelSeries.set(null);
    this.seriesFailed.set(false);
    this.usageStatService
      .getChannelSeries(channelName, from, to)
      .pipe(this.latestSeries)
      .subscribe({
        next: (series) => this.channelSeries.set(series),
        error: () => this.seriesFailed.set(true),
      });
  }

  // A transient inline status rather than a toast — there is no toast service (see
  // channel-workspace-layout's showResyncFeedback and admin-channels-page's counterpart, the two
  // existing instances of this exact pattern). Placed at the emote-count line rather than the dock,
  // because the dock unmounts the moment the selection it is bound to reaches zero — precisely the
  // case where every selected emote turned out to be gone (#94).
  private showSelectionPrunedFeedback(count: number): void {
    this.resetSelectionPrunedFeedback();
    this.selectionPrunedFeedback.set({
      key: pluralKey(count, 'usageStats.selectionPruned'),
      count,
    });
    this.selectionPrunedFeedbackTimeout = setTimeout(
      () => this.selectionPrunedFeedback.set(null),
      SELECTION_PRUNED_FEEDBACK_MS,
    );
  }

  // Shared by load() (a channel/range switch invalidates a standing notice, #94 follow-up P3),
  // showSelectionPrunedFeedback() itself (a second prune must not leave the first one's timer
  // racing the new one to clear the signal) and the constructor's destroyRef.onDestroy hook.
  private resetSelectionPrunedFeedback(): void {
    this.selectionPrunedFeedback.set(null);
    if (this.selectionPrunedFeedbackTimeout !== null) {
      clearTimeout(this.selectionPrunedFeedbackTimeout);
      this.selectionPrunedFeedbackTimeout = null;
    }
  }

  // Ends the wait for the first sync, the in-flight probe included, and takes the banner down with
  // it. Called both when something else has answered the question (a `channel.synced` event) and
  // when the question no longer applies (a channel switch).
  private stopAwaitingSync(): void {
    this.syncPoll?.unsubscribe();
    this.syncPoll = undefined;
    this.isAwaitingSync.set(false);
  }

  // Waits for the worker's 7TV sync to fill in the set id, then loads the totals once more.
  // Deliberately bounded: a channel with no 7TV emote set at all never gets an id, so this has to
  // give up eventually — at which point the ordinary "no active emotes" state is the honest answer.
  private awaitSync(channelName: string): void {
    this.isAwaitingSync.set(true);
    // Separate timers at fixed offsets from now rather than one interval: the gaps are deliberately
    // uneven (see SYNC_PROBE_DELAYS_MS), and merge keeps each offset measured from the same start.
    this.syncPoll = merge(...SYNC_PROBE_DELAYS_MS.map((delay) => timer(delay)))
      .pipe(
        // catchError sits on the inner request, not on the outer pipe: out here it would replace the
        // whole probe stream on the first hiccup and end the wait. Inside, one failed probe just
        // counts as "still empty" and the next probe tries again.
        switchMap(() =>
          this.emoteAdminService.getSetStatus(channelName).pipe(catchError(() => of(null))),
        ),
        // Completes on the first status that settles the question — a set id (the sync landed) or a
        // reason (it cannot land). Probing on against a known reason would spend the rest of the
        // window arriving at an answer the first probe already had.
        first((status) => !!status?.activeEmoteSetId || !!status?.syncFailureReason, null),
      )
      .subscribe((status) => {
        this.isAwaitingSync.set(false);
        if (!status) {
          return;
        }

        // Adopted even without a set id: the reason is the whole payload in that case, and the
        // empty state below renders from it.
        this.setStatus.set(status);
        // Range read now rather than closed over when the wait started: a range change keeps the
        // wait alive (see load()), and a stale from/to would fetch the wrong window — the same
        // reasoning as in the failure-reason recheck.
        if (status.activeEmoteSetId) {
          this.loadTotals(channelName, this.from(), this.to());
        }
      });
  }

  /**
   * `preserveSelection` and `silent` are what separates a user-triggered load from a pushed one:
   * a live update must not throw away a half-built delete selection, and must not flash the
   * skeleton over numbers the user is currently reading. Both default to the loud behaviour, so
   * every existing caller (initial load, refresh button, sync poll) is unchanged.
   */
  private loadTotals(
    channelName: string,
    from: string,
    to: string,
    options: { preserveSelection?: boolean; silent?: boolean } = {},
  ): void {
    this.usageStatService
      .getTotals(channelName, from, to)
      .pipe(this.latestTotals)
      .subscribe({
        next: (emotes) => {
          this.emotes.set(emotes);
          // Written next to the rows themselves, never before: until this line runs, the grid still
          // shows the previous channel's emotes (see totalsChannel's declaration).
          this.totalsChannel.set(channelName);
          this.totalsRange.set({ from, to });
          if (options.preserveSelection) {
            // Reconciles against the freshly loaded, UNFILTERED `emotes` — not atlasOrder()/
            // retainVisible(), which read the filtered view and would wrongly drop a row that
            // merely fell outside the current min/max-usage or name filter this reload changed the
            // numbers under (#94). `emotes` is the response payload itself, not the signal, so the
            // reconciliation cannot read a half-updated view no matter where the set() calls land.
            const removedCount = this.selection.retainAmong(emotes);
            if (removedCount > 0) {
              this.showSelectionPrunedFeedback(removedCount);
            }
          } else {
            // Kept even though a keyed selection survives a plain refetch: load() also runs on a
            // channel or date-range change, where the existing selection was made against different
            // numbers (an emote with "0x in 7 days" may be heavily used over 30 days). Carrying it
            // over would be its own deliberate feature, not a by-product of the keying.
            this.selection.clear();
          }
          if (!options.silent) {
            this.isLoading.set(false);
          }
        },
        // 401 is not handled here — apiAuthInterceptor resets the session and redirects for every
        // /api/ call in the app.
        //
        // The endpoint's own error codes beat the blanket "could not load" this used to show: a
        // hand-typed custom range wider than the API's 366-day guard came back as range_too_large and
        // was rendered as a generic failure, which reads as "the server is broken" rather than "your
        // range is too wide". The generic key stays as the fallback for a body-less failure.
        error: (error: unknown) => {
          this.errorMessage.set(
            error instanceof HttpErrorResponse
              ? apiErrorTranslationKey(error)
              : 'usageStats.errors.loadFailed',
          );
          if (!options.silent) {
            this.isLoading.set(false);
          }
        },
      });
  }
}
