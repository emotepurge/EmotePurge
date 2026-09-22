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
  untracked,
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
import { listQueryState } from '../../core/routing/list-query-state';
import { dedupeImportRows, ImportRow, ImportSource } from '../../core/seven-tv/import-source';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { EmoteSetListResponse } from '../../core/seven-tv/seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import {
  CreateVoteSessionDialogData,
  openCreateVoteSessionDialog,
} from './create-vote-session-dialog';
import { SetStatusFlushProbeGate } from './set-status-flush-probe-gate';
import { LIVE_EVENT_TYPES, channelLiveUrl } from '../../core/live/live-event.model';
import { CHANNEL_RELOAD_DEBOUNCE_MS, liveReload } from '../../core/live/live-reload';
import { mergeSetView } from '../../core/usage-stats/merge-set-view';
import {
  ChannelUsageSeries,
  EmoteUsageTotal,
  EmoteUsageTotalDto,
} from '../../core/usage-stats/usage-stat.model';
import { UsageStatService } from '../../core/usage-stats/usage-stat.service';
import {
  DateRangeMenu,
  DateRangePreset,
  allTimeStart,
  setObservedRange,
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
import { EmoteSetMenu } from '../../shared/emotes/emote-set-menu';
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
import {
  DockOutcomeAnnouncer,
  hiddenByFilterNoticeKey,
} from '../../shared/seven-tv/dock-outcome-announcer';
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
 * The atlas's group headings: the four usage bands, plus the set view's own trailing group of rows
 * with no counts under the shown set (spec #200, 8.2, E17 — "keine Zählungen unter diesem Set").
 * That group is deliberately not a band: `null` is not a small number, it has no share of usage and
 * no place in the Pareto cut, so it can never be `usageBandOf`'s answer.
 */
type AtlasGroupKey = UsageBandKey | 'uncounted';

/** A row that has a count under the shown set — the only kind bands, sort, sums and fill bars may
 *  ever see (spec F16: every one of them would turn a `null` into a silent `NaN`). */
type CountedEmote = EmoteUsageTotal & { totalUseCount: number };

/** One sentence of the set view's caption line: a translation key plus its parameters. */
interface CaptionSentence {
  readonly key: string;
  readonly params?: Record<string, string | number>;
}

/** Where the non-active view's live 7TV member list stands (spec #200, 8.3). `'none'` = the rows on
 *  screen belong to the active set, which never fetches one (E16). */
type LiveMembersState = 'none' | 'loading' | 'ready' | 'unavailable';

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
 * that reads `emoteSetId` behind it. Holds the page's `EmoteUsageTotal` rows rather than `ImportRow`s
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
  /** The same set's display name (spec 7.4), read from the dropdown's own list
   *  (`shownSetSummary`) at the same moment as `emoteSetId` — `null` alongside it whenever there
   *  is no set, and also whenever the list simply has not (or no longer) named that id. */
  readonly emoteSetName: string | null;
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

function isCounted(emote: EmoteUsageTotal): emote is CountedEmote {
  return emote.totalUseCount !== null;
}

function byName(a: EmoteUsageTotal, b: EmoteUsageTotal): number {
  return a.emoteName.localeCompare(b.emoteName, undefined, { sensitivity: 'base' });
}

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
    EmoteSetMenu,
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
  /** Threaded through to `ImportFlowDeps` (`loadImportTarget`'s live-list collaborator for a
   *  non-active/untracked target, spec F5) and, since T4.2, used directly here for the set-dropdown's
   *  own list request (`emoteSetListResource` below). */
  private readonly emoteSetService = inject(SevenTvEmoteSetService);
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
  // shows where the click can succeed. Same pattern as VoteSessionListPage's own header link,
  // gated behind the identical canManage computed.
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

  /**
   * The `/totals` rows exactly as the last successful request returned them, and the set they were
   * answered for — fixed when the request went out and never re-derived afterwards: the explicit
   * set, or for a request without one (the endpoint's active-set fallback, spec 6.5) the active id
   * known at that moment, or `null` = unknown when none was known. Written together, in
   * `loadTotals`' success branch only, so the pair always describes one answer — the same
   * discipline as `totalsChannel`/`totalsRange` below.
   */
  private readonly totalsRows = signal<EmoteUsageTotalDto[]>([]);
  private readonly totalsSetId = signal<string | null>(null);
  /** Whether those rows were requested as a non-active set's view — the set against the active id
   *  known at request time. Written with the pair above, in the same place, for the same reason. */
  private readonly totalsNonActive = signal(false);

  /**
   * The grid's rows: the `/totals` rows unioned with the shown set's live 7TV membership
   * (`mergeSetView`, spec #200 7.1, E16). In the active set's view this is a lossless 1:1 mapping —
   * no live list is ever fetched there — so every consumer that predates set views sees exactly the
   * rows it always did. In a non-active view the union only happens once the member list is there
   * (`liveMembers`); until then `viewLoading()` keeps the skeleton up (8.3), and without a readable
   * list the rows stay the DB rows alone.
   *
   * Keyed by `sevenTvEmoteId` everywhere below (7.2): a live member without a counted row has no
   * `Emote.Id` at all, and two of those must still be two distinct cells (NG0955, AK 54/55).
   */
  protected readonly emotes = computed<EmoteUsageTotal[]>(() => {
    const rows = this.totalsRows();
    if (this.liveMembersState() === 'none') {
      return mergeSetView(rows, null, true);
    }
    return mergeSetView(rows, this.liveMembers()?.emotes ?? null, false);
  });
  protected readonly setStatus = signal<EmoteSetStatus | null>(null);
  /**
   * The channel's active set — but only once the status on hand belongs to the channel in the URL.
   * `setStatus()` keeps the previous channel's answer until the new one lands (see
   * `setStatusChannel`), and every consumer of this id (the dropdown's fallback, the `/totals` and
   * `/series` request, the dock's delete target) would otherwise ask the new channel about the old
   * channel's set for exactly that window. `null` = not known for this channel (yet, or at all).
   */
  protected readonly activeEmoteSetId = computed(() =>
    this.setStatusChannel() === this.channelName()
      ? this.setStatus()?.activeEmoteSetId || null
      : null,
  );
  protected readonly trackedSince = computed(() => this.setStatus()?.trackedSince ?? null);

  // --- Set dropdown (spec #200, 8.1) --------------------------------------------------------
  //
  // `emoteSetId` in the URL means "follow the active set" when empty (T4.0 decision (a), operator
  // decision 2026-09-21) — never the active id written out. `listQueryState` is built for paginated
  // lists, but this page has exactly the shape it already solves (a filter value that must survive
  // reload/deep-link/back, replace rather than push, defaults stripped from the URL), so this reuses
  // it as a plain field rather than hand-rolling those same rules a sixth time
  // (core/routing/list-query-state.ts's own doc comment).
  private readonly setQuery = listQueryState({ emoteSetId: '' });
  private readonly emoteSetIdParam = computed(() => this.setQuery.params().emoteSetId);

  /**
   * The channel's set list for the dropdown — loaded once per channel (E19: never re-triggered by a
   * set switch, and the constructor's live-reload subscription never touches it either, only a loud
   * `channel.synced` reload does, explicitly, further down). `rxResource` rather than a hand-rolled
   * subscription because nothing here needs the bespoke channel/range bookkeeping `setStatus`/
   * `emotes` carry — a stale answer surviving one tick into a channel switch is the same acceptable
   * staleness `permissionsResource` above already has.
   */
  private readonly emoteSetListResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => this.emoteSetService.listChannelEmoteSets(params),
  });
  /**
   * `hasValue()` guards `.value()` deliberately: a `resource()`'s `.value()` *re-throws* the load
   * error once `status()` is `'error'` (Angular's own contract for it — a plain `?? null` around it
   * still crashes, because the throw happens before the `??` ever sees a value to fall back on).
   * Found live (2026-09-21): a 503/502 on `/emote-sets` took the whole page down through this
   * computed rather than degrading it — every `data-atlas-index` cell along with it, since the read
   * happens inside a template binding and Angular has nothing to catch it with.
   */
  /**
   * The last set list that was read successfully, and the channel it belongs to (operator decision
   * 2026-09-22). Once a channel's list has been read, a later failed reload — the loud
   * `channel.synced` one, typically a 503/429 from 7TV — must not take it away: the page keeps
   * serving this answer, so neither the pin below nor a silent fallback to the active set can be
   * triggered by a background request nobody asked for. Keyed by channel, so a channel switch starts
   * over with nothing latched.
   */
  private readonly lastReadSetList = signal<{
    readonly channelName: string;
    readonly list: EmoteSetListResponse;
  } | null>(null);

  protected readonly emoteSetList = computed<EmoteSetListResponse | null>(() => {
    if (this.emoteSetListResource.hasValue()) {
      return this.emoteSetListResource.value();
    }
    const latched = this.lastReadSetList();
    return latched?.channelName === this.channelName() ? latched.list : null;
  });
  /** Only the first read of a channel's list shows as busy — a background reload keeps serving the
   *  list on hand (`lastReadSetList`) and must not flip the trigger to "loading" under the user. */
  protected readonly emoteSetListLoading = computed(
    () => this.emoteSetListResource.isLoading() && this.emoteSetList() === null,
  );
  /** Unreadable in the sense that matters: failed, with no successfully read list for this channel
   *  to fall back on. A failed *re*load after a good read is not "unavailable" (decision
   *  2026-09-22) — see `lastReadSetList`. */
  protected readonly emoteSetListUnavailable = computed(
    () => this.emoteSetListResource.error() !== undefined && this.emoteSetList() === null,
  );

  /**
   * Set once the set list has failed to load for the channel now on screen *before it was ever read
   * successfully* (operator decisions 2026-09-21, #3, refined 2026-09-22): an unreadable list can
   * neither confirm nor reject a `emoteSetId` from the URL, so the page shows the active set — and
   * keeps showing it even once a later read (the loud `channel.synced` reload) succeeds, because
   * jumping the view to a set nobody just chose, mid-session, would be worse than staying on the
   * degraded but stable answer. Two things lift it: a channel switch (it is keyed by channel), and an
   * explicit choice in the dropdown (`onEmoteSetSelected`) — the pin never moves the view unrequested,
   * but a deliberate choice is never ignored. A list that *was* read once never pins at all: a later
   * failed reload is absorbed by `lastReadSetList`.
   */
  private readonly pinnedToActiveChannel = signal<string | null>(null);
  protected readonly isPinnedToActiveSet = computed(
    () => this.pinnedToActiveChannel() === this.channelName(),
  );

  /**
   * The set this page actually shows. Resolves the URL's `emoteSetId` against the set list and every
   * fallback rule spec 8.1 and the operator's 2026-09-21 decisions name: `''` (or a pinned channel)
   * follows the active set; while the list is still loading this already answers with the active set
   * as a safe default ({@link awaitingEmoteSetId} is what actually holds the totals/series request
   * back, not this); an id the list does not confirm as `kind === 'NORMAL'` — unknown, or hidden
   * (decision #4, a `PERSONAL`/`GLOBAL`/`SPECIAL` id counts exactly like unknown) — falls back to
   * the active set, silently.
   */
  protected readonly selectedEmoteSetId = computed<string | null>(() => {
    const active = this.activeEmoteSetId();
    const param = this.emoteSetIdParam();
    if (param === '' || this.isPinnedToActiveSet()) {
      return active;
    }
    const list = this.emoteSetList();
    if (list === null) {
      return active;
    }
    const match = list.sets.find((set) => set.id === param && set.kind === 'NORMAL');
    return match ? match.id : active;
  });

  /** The selected set's own name, for `app-import-trigger`'s confirm-dialog title (spec 8.6, T4.5)
   *  — `null` when the list has not (or no longer) named it, which `toImportTarget`
   *  (`import-trigger.ts`) then falls back to the id for, same as every other unnamed set. */
  protected readonly selectedEmoteSetName = computed(
    () =>
      this.emoteSetList()?.sets.find((set) => set.id === this.selectedEmoteSetId())?.name ?? null,
  );

  /**
   * True only while a `emoteSetId` the URL actually carries cannot yet be trusted or rejected,
   * because the set list for this channel has not answered — holds the totals/series load back the
   * same way `rangeResolved` already holds it back for "all time" (T4.2 decision #2), so a fresh
   * mount/deep-link never fires one request for the active set only to immediately refetch for the
   * URL's real target, and never shows a spurious #94 selection-pruned notice for that first, wrong
   * answer. `false` whenever there is nothing in the URL to wait for (decision #2: "ohne id in der
   * URL, don't wait").
   */
  protected readonly awaitingEmoteSetId = computed(() => {
    if (this.emoteSetIdParam() === '' || this.isPinnedToActiveSet()) {
      return false;
    }
    return this.emoteSetList() === null;
  });

  /**
   * True once the list has answered and the URL's id is confirmed neither the active set nor a
   * selectable (`NORMAL`) member of it — the one case that must silently clean the URL rather than
   * just fall back for this render (decision #3: a *readable* list that rejects the id removes it;
   * an *unreadable* one keeps it, because removing it would discard information a later successful
   * read could still have used).
   */
  private readonly shouldClearStaleEmoteSetIdParam = computed(() => {
    const param = this.emoteSetIdParam();
    if (param === '' || this.isPinnedToActiveSet()) {
      return false;
    }
    const list = this.emoteSetList();
    if (list === null) {
      return false;
    }
    return !list.sets.some((set) => set.id === param && set.kind === 'NORMAL');
  });

  // --- Set view (spec #200, 8.2-8.5) -----------------------------------------------------------

  /**
   * The set the rows on screen belong to — their fixed identity (`totalsSetId`), `null` when they
   * were answered while no active set was known. Deliberately **not** `totalsSetId ?? today's active
   * id`: rows fetched through the endpoint's fallback while the status request had failed would
   * otherwise be relabelled retroactively as whatever set the status names once it recovers — after
   * a 7TV set switch in between, one set's rows under another set's name, with every write path
   * open. An unknown identity never equals a known selected set, so `viewSwitching` keeps writes
   * locked until rows answered for an explicit, known set land (spec §36, unknown active set).
   */
  private readonly shownSetId = computed(() => this.totalsSetId());

  /**
   * Whether the rows on screen were loaded as a view of a set other than the channel's active one —
   * decided when they were *requested* (`totalsNonActive`, written together with the rows), not
   * re-derived from today's active id. Between a dropdown switch and the new rows landing the sheet
   * still shows the previous set's rows, and when a sync makes the viewed set the active one, the
   * rows on screen are still the ones merged as a non-active view until the reload
   * (`viewKindStale`) replaces them — every lock and caption below has to describe what is on
   * screen, never what is about to be.
   */
  protected readonly isNonActiveView = computed(() => this.totalsNonActive());

  /**
   * True while the rows on screen were loaded under a different active set than the one known now —
   * the viewed set just became (or stopped being) the channel's active set. The constructor reloads
   * the rows the moment this turns true; until they land, `viewSwitching` locks every write path, so
   * a delete can never be offered on rows merged for the other kind of view.
   */
  private readonly viewKindStale = computed(() => {
    const shown = this.totalsSetId();
    if (shown === null) {
      return false;
    }
    return this.totalsNonActive() !== (shown !== this.activeEmoteSetId());
  });

  /**
   * "Selected ≠ shown": the dropdown (or the URL, or a channel switch) already names a set whose rows
   * are not on screen yet — in flight, or failed — or the rows on screen are stale in kind
   * (`viewKindStale`). The same predicate `importScopeCurrent` guards the push with; here it also
   * locks deleting and voting with a visible reason (`deleteLockReasonKey`), because the dock stays
   * mounted across a set switch and would otherwise offer a 7TV delete in the active set while
   * another set is already chosen.
   */
  protected readonly viewSwitching = computed(
    () => this.shownSetId() !== this.selectedEmoteSetId() || this.viewKindStale(),
  );

  /**
   * The chosen non-active set's live 7TV member list (spec 8.3, E16) — requested beside `/totals`
   * and `/series` when a non-active set is selected, never for the active set. Bound to the selected
   * set, not to any reload: a silent `usage.flushed` reload never touches it; the loud ones
   * (`channel.synced`, the refresh button) reload it explicitly and ask the Api to bypass its cache
   * (`reloadLiveMembers`). Read through `liveMembers`/`liveMembersState` only — `.value()` re-throws
   * in the error state (see `emoteSetList`).
   */
  private readonly liveMembersResource = rxResource({
    params: () => {
      const emoteSetId = this.selectedEmoteSetId();
      return emoteSetId !== null && emoteSetId !== this.activeEmoteSetId()
        ? { channelName: this.channelName(), emoteSetId }
        : undefined;
    },
    stream: ({ params }) => {
      const asked = this.liveMembersRefreshFor;
      this.liveMembersRefreshFor = null;
      const refresh =
        asked?.channelName === params.channelName && asked.emoteSetId === params.emoteSetId;
      // Cached, not the plain method: switching back to a recently-shown set within the cache's
      // TTL must cost no request at all (operator decision 2026-09-22) — see
      // SevenTvEmoteSetService.loadCachedEmoteSetPreview's doc for the TTL and why this is the one
      // caller that gets it. `refresh` (channel.synced, the refresh button) still bypasses it.
      return this.emoteSetService.loadCachedEmoteSetPreview(params.channelName, params.emoteSetId, {
        refresh,
      });
    },
  });

  /**
   * The member list a loud reload was asked for (`reloadLiveMembers`) — read and cleared by the
   * stream above, so only that one request carries `refresh: true` (spec 8.3: the loud reload fetches
   * the list anew; a params-driven load after a set switch may use the Api's cache). A plain field,
   * not a signal: it must never itself retrigger the resource.
   */
  private liveMembersRefreshFor: {
    readonly channelName: string;
    readonly emoteSetId: string;
  } | null = null;

  /**
   * Where the member list of the set on screen stands. `'loading'` only while a request is actually
   * in flight — never derived from "the list does not match the rows", which used to stay true
   * forever once the rows' own request failed after a switch (the member list then either belonged
   * to the new set or was idle) and so kept the skeleton up with the refresh button disabled.
   * Anything settled that is not the shown set's list is `'unavailable'`; whether the view is
   * mid-switch is `viewSwitching`'s business, not this one's.
   */
  protected readonly liveMembersState = computed<LiveMembersState>(() => {
    if (!this.isNonActiveView()) {
      return 'none';
    }
    const resource = this.liveMembersResource;
    // Matched against the set the rows belong to, so a list still held from the previously chosen
    // set can never be unioned with the new set's rows.
    if (resource.hasValue() && resource.value().emoteSetId === this.shownSetId()) {
      return 'ready';
    }
    return resource.isLoading() ? 'loading' : 'unavailable';
  });

  /**
   * Whether the non-active view's member list is still about to change — any request in flight,
   * including a loud reload. Wider than `liveMembersState() === 'loading'` on purpose: a reload
   * keeps the previous list renderable (`'ready'`, no skeleton), but reconciling the selection
   * against it would miss a live member the new list drops — and nothing would reconcile again
   * afterwards. `reconcileSelection` therefore waits for this, not for the render state.
   */
  private readonly liveMembersSettling = computed(
    () => this.isNonActiveView() && this.liveMembersResource.isLoading(),
  );

  private readonly liveMembers = computed(() =>
    this.liveMembersState() === 'ready' ? this.liveMembersResource.value() : null,
  );

  /** The sheet's loading state: the rows' own request, plus — in a non-active view — the member list
   *  they are unioned with (8.3: "die Vereinigung erst, wenn beide da sind"). A loud reload keeps the
   *  previous list on screen while it refetches, so it never flashes the skeleton. */
  protected readonly viewLoading = computed(
    () => this.isLoading() || this.liveMembersState() === 'loading',
  );

  /**
   * A set switch that settled without the chosen set's rows: the request for them failed, so the
   * rows still on screen belong to a set that is no longer the chosen one. The sheet then shows the
   * error with a way to retry instead of those rows (they would read as the chosen set's), and the
   * refresh button is free again — `viewLoading()` is false by definition here.
   */
  protected readonly setSwitchFailed = computed(
    () => !this.viewLoading() && this.viewSwitching() && this.errorMessage() !== null,
  );

  /** The shown set's entry in the dropdown's list — its name and observation intervals. */
  private readonly shownSetSummary = computed(
    () => this.emoteSetList()?.sets.find((set) => set.id === this.shownSetId()) ?? null,
  );

  /**
   * The "objective" reasons a non-active set view blocks a writer, independent of *which* writer:
   * the view is switching (`viewSwitching`: the chosen set's rows are not on screen yet, or their
   * request failed), the member list could not be read (503/429), the member list came back
   * `truncated`, or the member list is still loading for the first time in a settled,
   * non-switching view (same "not loaded yet" reason as switching — a list not yet confirmed
   * readable must not be trusted either way). A *loud* reload (`channel.synced`, the refresh
   * button) does not hit that last case: it keeps serving the previous list as `'ready'` while it
   * refetches (spec's own "second review round" addendum in DECISIONS), so it locks only if that
   * previous list was itself `truncated` — never merely for being mid-reload. `null` for the
   * active view and for a non-active view whose member list loaded clean.
   *
   * Shared by `deleteLockReasonKey` (spec #200, 8.3/8.8) and `voteLockReasonKey` (spec 9, K6) —
   * identically since K6: a plain non-active view with a good member list locks neither. Deleting
   * is set-aware since K5/T5.3 (T5.1/T5.2, the confirmation names the set); voting creates a
   * set-session over the shown set since K6. An unreadable or truncated list locks both: the
   * set-session create validates the ballot against the live member list server-side, so a list
   * the page cannot read in full cannot back a trustworthy ballot either.
   */
  private readonly sharedSetViewLockReasonKey = computed<string | null>(() => {
    if (this.viewSwitching()) {
      return 'usageStats.setView.lock.switching';
    }
    switch (this.liveMembersState()) {
      case 'none':
        return null;
      case 'unavailable':
        return 'usageStats.setView.lock.membersUnavailable';
      case 'ready':
        return this.liveMembers()?.truncated ? 'usageStats.setView.lock.truncated' : null;
      case 'loading':
        return 'usageStats.setView.lock.switching';
      default:
        return null;
    }
  });

  /**
   * Why deleting is locked in the view on screen, as a translation key, or `null` when it is not
   * (spec 8.3, 8.8): the view is switching, or the member list could not be read / came back
   * truncated / is still loading — `sharedSetViewLockReasonKey` above. A plain non-active view with
   * a good member list is no longer locked for deleting since K5/T5.3: the run is set-aware
   * (T5.1/T5.2) and the confirmation names the set (spec 8.8), so there is nothing left this lock
   * protected against.
   */
  protected readonly deleteLockReasonKey = computed<string | null>(() =>
    this.sharedSetViewLockReasonKey(),
  );

  /**
   * Set names by id, for the name-twin marker's tooltip (E24, AK 59) and — since K5/T5.3 — for the
   * mass-delete panel's `[setNames]` input, which resolves a finished delete run's own frozen set
   * (possibly not `selectedEmoteSetId()` any more) for the restore confirmation (spec 8.8). A twin
   * (or a run's set) the list does not (or no longer) name still gets a stable handle: the id's
   * last six characters, the same short form the audit view uses for a set — that fallback lives
   * at each reader, not here, so a reader missing from this map is unambiguous (`undefined`, not a
   * pre-shortened string masquerading as a name).
   */
  protected readonly emoteSetNames = computed(
    () => new Map((this.emoteSetList()?.sets ?? []).map((set) => [set.id, set.name])),
  );

  /**
   * The honesty sentences a non-active view adds to the caption line under the sheet (spec 8.4 and
   * 8.3), in this order: whether the set was observed in the range, whether any counts exist, then
   * the member list's own caveat. Empty in the active view — "für das aktive Set … bleibt alles wie
   * heute" (AK 60) — and while the view is still loading.
   *
   * **Two independent statements, never one derived from the other** (8.4): *observed yes/no* comes
   * from the shown set's `observations` against the loaded range alone, *counts yes/no* from the
   * loaded `/totals` rows alone. A range with counts but without an observation interval is real
   * (an inactive channel's rows backfilled onto a set id by the migration, then rejoined) and gets
   * B− only — the numbers stand, nothing relativises them.
   */
  protected readonly setViewCaptions = computed<CaptionSentence[]>(() => {
    if (!this.isNonActiveView() || this.viewLoading() || this.viewSwitching()) {
      return [];
    }
    const sentences: CaptionSentence[] = [];
    const summary = this.shownSetSummary();
    const range = this.totalsRange();
    if (summary && range) {
      const rangeStart = Date.parse(`${range.from}T00:00:00Z`);
      const rangeEnd = Date.parse(`${range.to}T23:59:59.999Z`);
      const now = Date.now();
      // Ascending by the Api's own contract (6.1); the first intersecting interval is therefore the
      // one that decides whether counting began inside the range.
      const firstIntersecting = summary.observations.find((interval) => {
        const intervalStart = Date.parse(interval.fromUtc);
        const intervalEnd = interval.toUtc === null ? now : Date.parse(interval.toUtc);
        return intervalStart <= rangeEnd && intervalEnd >= rangeStart;
      });
      if (!firstIntersecting) {
        sentences.push({ key: 'usageStats.setView.facts.notObserved' });
      } else if (Date.parse(firstIntersecting.fromUtc) > rangeStart) {
        // The rangeStartsBeforeTracking idiom, with the interval's start in trackedSince's place.
        sentences.push({
          key: 'usageStats.setView.facts.countedSince',
          params: { date: this.formatDate(firstIntersecting.fromUtc) },
        });
      }
    }
    if (!this.totalsRows().some((row) => row.totalUseCount > 0)) {
      sentences.push({ key: 'usageStats.setView.facts.noCounts' });
    }

    const state = this.liveMembersState();
    const members = this.liveMembers();
    if (state === 'unavailable') {
      sentences.push({ key: 'usageStats.setView.membersUnavailable' });
    } else if (members?.truncated) {
      sentences.push({
        key: 'usageStats.setView.truncated',
        params: { total: this.formatCount(members.totalCount) },
      });
    }
    return sentences;
  });

  /** What the date menu's `'set-observed'` preset selects for the chosen set (8.5), or `null` when
   *  the set was never observed — which also keeps the preset out of the menu. */
  protected readonly setObservedPresetRange = computed(() => {
    const selected = this.selectedEmoteSetId();
    const summary = this.emoteSetList()?.sets.find((set) => set.id === selected);
    return summary ? setObservedRange(summary.observations) : null;
  });

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

  // Survives search and filter (supersedes S2-16, 2026-09-18): a filter change narrows what is on
  // screen, never what is selected — the filter lost its onChange hook entirely, there is nothing
  // left for it to call. What used to be pruned here now only shows up as selection.hiddenSelectedCount().
  protected readonly usageFilter = new EmoteUsageFilter<EmoteUsageTotal>();

  protected readonly filteredEmotes = computed(() => this.usageFilter.apply(this.emotes()));

  /**
   * Every row that has a count under the shown set — the only rows bands, sort, sums, the Pareto
   * denominator, the distribution strip and the fill bars may see (spec 8.2, F16). A `null` row is
   * split off *before* any of them, never coerced: `null` means "no counts under this set", which is
   * not the same statement as 0 (E17). `'left'` rows do have counts and stay (E23, AK 57).
   */
  protected readonly countedEmotes = computed(() => this.emotes().filter(isCounted));

  protected readonly sortedEmotes = computed(() => {
    const key = this.sortKey();
    const factor = this.sortDirection() === 'desc' ? -1 : 1;
    const value = (emote: CountedEmote) =>
      key === 'usage' ? emote.totalUseCount : sortableLastUsed(emote.lastUsedDate);

    const items = this.filteredEmotes().filter(isCounted);
    // Name as the tiebreaker: "last used" collapses to a handful of distinct days, and without it
    // equal-day emotes would reshuffle on every refetch.
    items.sort((a, b) => factor * (value(a) - value(b)) || byName(a, b));
    return items;
  });

  /** The rows with no counts under the shown set, as their own trailing group in name order
   *  (AK 56) — independent of the sort toolbar, which orders by numbers these rows do not have. */
  private readonly uncountedEmotes = computed(() =>
    this.filteredEmotes()
      .filter((emote) => emote.totalUseCount === null)
      .sort(byName),
  );

  // Derived from the WHOLE set, not from the filtered view: the weight classes are a property of
  // the channel, and they must not move under the user because they typed three letters into the
  // name filter. Otherwise an emote would change band while nothing about it changed.
  private readonly bandThresholds = computed(() =>
    usageBandThresholds(this.countedEmotes().map((emote) => emote.totalUseCount)),
  );

  /** Usage of the whole set — the denominator every band share is measured against. */
  private readonly totalUsage = computed(() =>
    this.countedEmotes().reduce((sum, emote) => sum + emote.totalUseCount, 0),
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
      this.countedEmotes(),
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
  /** The bands, then — only in a set view that has any — the trailing "no counts under this set"
   *  group (spec 8.2, AK 56). */
  private readonly atlasGroups = computed(() => {
    const groups: { key: AtlasGroupKey; items: readonly EmoteUsageTotal[]; share: number }[] = [
      ...this.bands(),
    ];
    const uncounted = this.uncountedEmotes();
    if (uncounted.length > 0) {
      groups.push({ key: 'uncounted', items: uncounted, share: 0 });
    }
    return groups;
  });

  protected readonly atlasOrder = computed(() =>
    this.atlasGroups().flatMap((group) => group.items),
  );

  protected readonly rows = computed(
    () => packAtlasRows<EmoteUsageTotal, AtlasGroupKey>(this.atlasGroups(), this.columns()).rows,
  );

  /** Fill-bar width per 7TV id — precomputed per band peak so the template stays a lookup. */
  protected readonly fillPercents = computed(() => {
    const map = new Map<string, number>();
    for (const band of this.bands()) {
      for (const emote of band.items) {
        map.set(emote.sevenTvEmoteId, usageFillPercent(emote.totalUseCount, band.peak));
      }
    }
    return map;
  });

  /** Ranked usage curve of the whole set — the orientation device above the sheet. */
  protected readonly distribution = computed(() =>
    usageDistribution(
      this.countedEmotes().map((emote) => emote.totalUseCount),
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
      this.countedEmotes().map((emote) => emote.totalUseCount),
      this.bandThresholds(),
      this.distribution().length,
    ),
  );

  protected readonly concentration = computed(() =>
    topFifthShare(this.countedEmotes().map((emote) => emote.totalUseCount)),
  );

  protected readonly deadCount = computed(
    () => this.countedEmotes().filter((emote) => emote.totalUseCount === 0).length,
  );

  private readonly totalUses = computed(() =>
    this.countedEmotes().reduce((sum, emote) => sum + emote.totalUseCount, 0),
  );

  /** Position in the set's usage ranking — the inspector's "#003", stable across sort changes.
   *  Counted rows only: a row without counts has no place in a ranking by counts. */
  private readonly usageRank = computed(() => {
    const ranks = new Map<string, number>();
    [...this.countedEmotes()]
      .sort((a, b) => b.totalUseCount - a.totalUseCount)
      .forEach((emote, index) => ranks.set(emote.sevenTvEmoteId, index + 1));
    return ranks;
  });

  // Plural follows the number that is actually spelled first ("1 von 409 Emote", not "Emotes").
  protected readonly emoteCountKey = computed(() =>
    pluralKey(this.atlasOrder().length, 'emoteCount'),
  );

  // Display list is atlasOrder() (filtered, sorted, banded — the basis for shift ranges and
  // visibility); universe is the unfiltered emotes() (Konzept "Auswahl überlebt Suche und Filter"
  // 1) — selectedItems() resolves against the latter, so a filter change can no longer make a
  // marked-but-hidden row unresolvable to the delete/export/vote-session run that reads it.
  //
  // Keyed by the 7TV id, not `Emote.Id` (spec #200, 7.2, AK 54): a set view's live member without a
  // counted row has no Guid, and keying on a `null` would fold every such row into one selection
  // entry. The class itself is untouched — it has always been generic over its key.
  protected readonly selection = new ListSelection(
    this.atlasOrder,
    (emote) => emote.sevenTvEmoteId,
    this.emotes,
  );

  /**
   * The cell the inspector is describing, held by id rather than by object: a refetch hands out
   * fresh instances for the same rows, and an object reference would leave the inspector pinned to
   * a row that no longer exists. Falls back to the busiest emote so the line is never empty —
   * before the first hover, the top of the set is the honest thing to be looking at.
   */
  // Held by 7TV id, like the selection (7.2).
  private readonly inspectedId = signal<string | null>(null);
  protected readonly inspected = computed(() => {
    // A failed switch keeps the previous set's rows loaded but not on screen (`setSwitchFailed`) —
    // the sidecar must not go on describing one of them either.
    if (this.setSwitchFailed()) {
      return null;
    }
    const order = this.atlasOrder();
    const id = this.inspectedId();
    return (
      (id ? order.find((emote) => emote.sevenTvEmoteId === id) : undefined) ?? order[0] ?? null
    );
  });

  protected readonly inspectedRank = computed(() => {
    const emote = this.inspected();
    return emote ? (this.usageRank().get(emote.sevenTvEmoteId) ?? null) : null;
  });

  protected readonly inspectedBand = computed<AtlasGroupKey>(() => {
    const emote = this.inspected();
    if (!emote) {
      return 'dead';
    }
    return emote.totalUseCount === null
      ? 'uncounted'
      : usageBandOf(emote.totalUseCount, this.bandThresholds());
  });

  protected readonly inspectedShare = computed(() => {
    const emote = this.inspected();
    const total = this.totalUses();
    return emote && emote.totalUseCount !== null && total > 0 ? emote.totalUseCount / total : null;
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
  private readonly latestTotals = latestOnly<EmoteUsageTotalDto[]>();

  /**
   * Set when a same-channel reload's rows landed while the non-active view's member list was still
   * loading: reconciling the selection (#94's `retainAmong`) against the half-built view would prune
   * a marked live member that merely has no counted row yet. The constructor's effect runs the
   * reconciliation once the list has settled (loaded or failed). See `reconcileSelection`.
   */
  private readonly selectionReconcilePending = signal(false);
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

  // Keyed by 7TV id (spec 6.5 step 1, 7.2): `/series` still carries `emoteId` beside it, but a set
  // view's row need not have one.
  private readonly seriesByEmote = computed(
    () =>
      new Map(
        this.channelSeries()?.emotes.map((entry) => [entry.sevenTvEmoteId, entry.days]) ?? [],
      ),
  );

  /** Channel-level, so converted once per response rather than per emote inspected. */
  protected readonly liveDayDates = computed(() => {
    const series = this.channelSeries();
    return series ? offsetsToDates(series.liveDays, series.from) : [];
  });

  protected readonly inspectedPoints = computed(() => {
    const series = this.channelSeries();
    const emote = this.inspected();
    // A row without counts under the shown set has no curve either: zero-filling it would draw a
    // flat baseline, i.e. claim "counted, never used" — the one statement E17 rules out.
    if (!series || !emote || emote.totalUseCount === null) {
      return [];
    }
    // An emote absent from the response had no usage in the range — the same statement its absent
    // days would make, one level up. Zero-filling here is what turns that into a flat baseline.
    return fillOffsetSeries(
      this.seriesByEmote().get(emote.sevenTvEmoteId) ?? [],
      series.from,
      series.to,
    );
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
  // display name, which only the loaded row carries. selectedItems() resolves against the
  // unfiltered universe (emotes()), not the filtered atlasOrder(), so a row a name/usage filter is
  // currently hiding still resolves here and stays part of the run — a filter change no longer
  // clears or prunes the selection at all (Konzept "Auswahl überlebt Suche und Filter"). What
  // cannot resolve is a key that was actually removed from emotes() (reload, finished delete),
  // which retainAmong()/clear() already keep out of selectedKeys() before this ever reads it.
  //
  // One kind of row never reaches the delete run (spec #200, 8.2): a `'left'` row is no longer in
  // the set, so there is nothing to remove (E23, AK 57) — it only exists in a non-active set's view.
  // A row without `Emote.Id` goes in like any other since K5 (spec 7.2): the run is keyed by the 7TV
  // id and the protocol writes `emoteId: null` for it. A #74 duplicate cell is one row with both
  // aliases — one `REMOVE` takes both entries (Sonde 5, branch A), and the protocol keeps both names
  // so the restore can re-add each.
  protected readonly selectedForDelete = computed<DeletableEmote[]>(() =>
    this.selection.selectedItems().flatMap((emote) =>
      emote.membership === 'live'
        ? [
            {
              emoteId: emote.emoteId ?? undefined,
              sevenTvEmoteId: emote.sevenTvEmoteId,
              name: emote.emoteName,
              aliases: emote.aliases,
              // Feeds the delete-confirm dialog's hidden-by-filter block (Konzept "Auswahl
              // überlebt Suche und Filter" 2.1) — `isVisible` reads the same atlasOrder() the
              // dock's own hiddenSelectedCount is built from, so the two numbers can never disagree.
              hidden: !this.selection.isVisible(emote),
            },
          ]
        : [],
    ),
  );

  /**
   * How many slots of the shown set deleting the marked rows would free (spec 8.2, AK 58): a #74
   * duplicate cell is one row but two set entries, and one `REMOVE` takes both (Sonde 5, branch A).
   * `'left'` rows occupy no slot of the set any more and free none. In the active set's view every
   * `slotCount` is 1, so this is the selection size, exactly as before.
   */
  protected readonly pendingRemovalSlots = computed(() =>
    this.selection
      .selectedItems()
      .reduce((sum, emote) => sum + (emote.membership === 'live' ? emote.slotCount : 0), 0),
  );

  /**
   * Capacity and occupied slots of the shown set, for the slot bar and the dock's projection. The
   * active set's come from its status (`Channel.ActiveEmoteSetCapacity`); a non-active set's only
   * from its own member list (`capacity`/`totalCount`, spec 8.3) — never the active set's numbers
   * under another set's name. `null` while that list is loading or unreadable.
   */
  protected readonly slotBudget = computed<{ capacity: number | null; occupied: number } | null>(
    () => {
      // A failed switch shows no set at all — never the previous set's budget under the new choice.
      if (this.setSwitchFailed()) {
        return null;
      }
      if (this.liveMembersState() === 'none') {
        const status = this.setStatus();
        return status ? { capacity: status.capacity, occupied: status.occupiedSlots } : null;
      }
      const members = this.liveMembers();
      return members ? { capacity: members.capacity, occupied: members.totalCount } : null;
    },
  );

  /**
   * The ballot a NULL-session created from the selection would carry: `Emote.Id` Guids, resolved
   * from the selected rows at the moment it is read — which, handed to the dialog as a signal, is
   * the moment it submits (spec E4, 7.2: the grid's keys are 7TV ids, a null session still speaks
   * Guids). Live, not a snapshot, for the same reason the dialog always took a live signal (#132).
   * Every row of the active set's view has a Guid, so nothing is ever dropped here — this is only
   * ever read for that view (`openCreateVoteSession`), never for a non-active one, which builds a
   * SET-session ballot from `voteBallotSevenTvEmoteIds` instead (spec 6.9, K6).
   */
  private readonly voteBallotEmoteIds = computed(() =>
    this.selection
      .selectedItems()
      .flatMap((emote) => (emote.emoteId !== null ? [emote.emoteId] : [])),
  );

  /**
   * The ballot a SET-session created from the selection would carry: 7TV emote ids, the same live
   * resolution as `voteBallotEmoteIds` above, just against the id every row of *any* view — active
   * or not, Guid or class-2b — always has (spec 6.9, 7.2). Read only for a non-active view
   * (`openCreateVoteSession`); the active view's button still speaks Guids.
   *
   * `membership === 'live'` only (K6 whole-branch review, Fable B — mirrors `selectedForDelete`'s
   * own filter above): a `'left'` row is no longer a member of the set on 7TV at all, and
   * `VoteSessionService.CreateAsync`'s set-session branch validates the whole ballot all-or-nothing
   * against the *live* membership (spec section 9 step 2) — one stale id in the request would fail
   * the entire create with 400 `emote_ids_invalid`, silently dropping every other selected emote
   * along with it.
   */
  private readonly voteBallotSevenTvEmoteIds = computed(() =>
    this.selection
      .selectedItems()
      .flatMap((emote) => (emote.membership === 'live' ? [emote.sevenTvEmoteId] : [])),
  );

  /**
   * The size of the ballot `openCreateVoteSession` would actually send for the view shown right
   * now — `voteBallotSevenTvEmoteIds().length` in a non-active view, `voteBallotEmoteIds().length`
   * in the active one. Not `selection.selectedItems().length`: that counts every selected row
   * regardless of membership, so a non-active view's selection of `'left'`-only rows would read as
   * non-empty here while the ballot it would actually submit is empty (`membership === 'live'`
   * only, see `voteBallotSevenTvEmoteIds`'s own doc comment) — exactly the button-enabled-but-
   * dialog-opens-empty gap this closes.
   */
  protected readonly voteBallotSize = computed(
    () =>
      (this.isNonActiveView() ? this.voteBallotSevenTvEmoteIds() : this.voteBallotEmoteIds())
        .length,
  );

  /** Voting is locked exactly when deleting is (`sharedSetViewLockReasonKey`): mid-switch (spec
   *  §36: the selected set is not yet the one shown), or while the shown non-active set's member
   *  list is loading, unreadable or truncated. A settled non-active view is otherwise a legitimate
   *  place to vote from since K6 (spec 9): it creates a set-session over the *shown* set rather
   *  than the null-session the active view creates. The dialog re-checks this at submit time. */
  protected readonly voteLocked = computed(() => this.voteLockReasonKey() !== null);

  /** The reason behind `voteLocked`, for the vote dialog, which re-checks it at submit time (the
   *  dialog outlives the moment its button was enabled): `sharedSetViewLockReasonKey`, the same
   *  reason — and therefore the same paragraph — the dock shows next to the delete button. */
  protected readonly voteLockReasonKey = computed<string | null>(() =>
    this.sharedSetViewLockReasonKey(),
  );

  /**
   * A delete run of this page is still writing, or its closing bookkeeping call (`sync-deleted`) is
   * still out — `onDeleted` edits the rows on screen once that call answers, so the set on screen
   * must not change until then. Locks the set dropdown with a visible reason.
   */
  protected readonly deleteRunActive = computed(
    () => this.deleteService.isRunning() || this.deleteService.syncReport() === 'pending',
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
      hasActiveSet: this.selectedEmoteSetId() !== null,
      markedCount: this.selection.selectedKeys().length,
      deleteShown: this.deleteService.isRunning() || this.deleteService.queue().length > 0,
      // A confirmed delete whose pre-run live alias read is still out — or whose abort notice is
      // still showing — is invisible in every other clause here, marked count included: the reload
      // that prunes the selection is exactly the case this covers. See that signal's own doc.
      deleteConfirmPending: this.deleteService.confirmedRunPending(),
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
  //
  // Plus one condition of the set view's own (spec #200, 7.3): the loaded rows must belong to the
  // set now selected. Between a dropdown switch and the new rows landing the grid still shows the
  // previous set, and a capture there would pair one set's rows with the other set's id.
  protected readonly importScopeCurrent = computed(
    () =>
      importScopeIsCurrent(this.channelName(), this.setStatusChannel(), this.totalsChannel()) &&
      this.shownSetId() === this.selectedEmoteSetId(),
  );

  /**
   * Whether the header "Exportieren" button is disabled — Konzept "Auswahl überlebt Suche und
   * Filter" nachtrag (2026-09-19). Disabled only when there is nothing at all to act on: the
   * visible list AND the selection both empty. Used to disable on `atlasOrder().length === 0`
   * alone, which meant a filter that hid every row locked this out even while a selection built
   * across several searches survived underneath it — the exact bug this nachtrag fixes, one level
   * above the same mistake in `retainVisible()`. The dialog itself (`ExportDialog`) is what then
   * keeps a genuinely empty scope from being chosen or submitted once it opens.
   */
  protected readonly exportButtonDisabled = computed(
    () => this.atlasOrder().length === 0 && this.selection.selectedItems().length === 0,
  );

  /**
   * Whether the header "Übertragen" button is disabled — same empty-scope reasoning as
   * `exportButtonDisabled` above, plus the two locks the push shares with `app-import-trigger`
   * (see the template comment above both buttons): any of the three 7TV-writing runs active, or
   * the set status/rows still belonging to the previous channel (`importScopeCurrent`).
   */
  protected readonly transferButtonDisabled = computed(
    () =>
      (this.atlasOrder().length === 0 && this.selection.selectedItems().length === 0) ||
      this.arbiter.activeRun() !== null ||
      !this.importScopeCurrent(),
  );

  /** Whether the sheet is actually showing the rows `atlasOrder()` describes — the same condition
   *  that picks the grid `@else` branch in the template (`usage-stats-page.html`'s
   *  `isLoading()`/`isAwaitingSync()`/`atlasOrder().length === 0` chain), pulled out as one named
   *  source rather than re-derived at the one other place that needs it (`showMarkAll` below). While
   *  `isLoading()` is true, `atlasOrder()` can still be non-empty — it still describes the OUTGOING
   *  query until `loadTotals()`'s response lands (see that method's own comment) — which is exactly
   *  the state this excludes. */
  protected readonly sheetShowsRows = computed(
    () => !this.viewLoading() && !this.isAwaitingSync() && this.atlasOrder().length > 0,
  );

  /** Whether the toolbar's mark-all control exists at all — a fine pointer (no write path off a
   *  phone, same reasoning as everywhere else selection appears) and the sheet actually showing a
   *  non-empty current view (`sheetShowsRows`). Scope is `atlasOrder()`, not `emotes()`: the button
   *  marks what the sheet is showing, filtered, sorted and banded, not the channel's whole universe
   *  underneath an active filter.
   *
   *  Codex/Opus review: this used to read `atlasOrder().length > 0` alone, which stayed true while
   *  the sheet itself showed the "sync pending" banner in place of the grid (`emotes()` — and with
   *  it `atlasOrder()` — is filled by the totals endpoint independently of the 7TV set status). The
   *  toolbar button was therefore live over rows nobody could see, and a press marked them into a
   *  dock with zero visible feedback (the dock mounts off `activeEmoteSetId`, which is exactly what
   *  is missing while the banner shows). Requiring `sheetShowsRows()` closes that window; it also
   *  makes the loading case redundant here, see `markAllDisabled` below. */
  protected readonly showMarkAll = computed(() => !this.isCoarse() && this.sheetShowsRows());

  /** Disabled once every row the sheet currently shows is already marked — a press that could not
   *  add anything is not a control worth pressing. Vacuously true on an empty view, which never
   *  matters in practice: `showMarkAll()` already hides the button there.
   *
   *  No loading gate here any more (Codex/Opus review): `showMarkAll()` now requires
   *  `sheetShowsRows()`, which is already false for the whole window `isLoading()` covers, so the
   *  button cannot be on screen while a reload is in flight in the first place — a second gate here
   *  for the same case would just be two locks on one door. The per-band select-all button in the
   *  template loses the same dead binding for the same reason, only more directly: it sits inside
   *  the grid `@else` branch itself, which `isLoading()` never reaches at all. */
  protected readonly markAllDisabled = computed(() =>
    this.atlasOrder().every((emote) => this.selection.isSelected(emote)),
  );

  /**
   * Count of the grid selection that would actually be captured by the dock's copy shortcut —
   * built on `selection.selectedItems()`, the one source every displayed count now reads from
   * (Konzept "Auswahl überlebt Suche und Filter" 1, "eine Zahl je Knopf, aus einer Quelle"), NOT
   * the raw `selection.selectedKeys()`. A key that a reload actually dropped from the unfiltered
   * `emotes()` universe (an emote deleted externally, which the totals query filters via
   * `!e.IsArchived`) does not resolve here even before `retainAmong()`/`clear()` catch up and prune
   * `selectedKeys()` itself — so this count can briefly run ahead of a stale key set, never behind
   * it. A name/usage filter hiding a marked row, by contrast, no longer touches either number at
   * all. Gating the shortcut and its label on the raw key count would let `importShortcutLocked`
   * report "unlocked" with zero resolvable rows, so a click runs into `openImportTarget`'s
   * `captured.selection.length === 0` guard and silently does nothing — no dialog, no feedback. If
   * only some rows survive, the label would announce more emotes than the run actually copies.
   */
  protected readonly importShortcutSelectionCount = computed(
    () => this.selection.selectedItems().length,
  );

  /** Wording for the dock's hidden-by-filter secondary line (Konzept "Auswahl überlebt Suche und
   *  Filter" 2.2) — only ever read from the template behind `selection.hiddenSelectedCount() > 0`,
   *  so the "no permanent control" rule (Frontend-Zurückhaltung) lives in the `@if`, not here.
   *  The key comes from the same helper the announcer uses, so the shown and the spoken sentence
   *  cannot drift apart. */
  protected readonly hiddenSelectedFilterKey = computed(() =>
    hiddenByFilterNoticeKey(this.selection.hiddenSelectedCount()),
  );

  /**
   * The same number again, but 0 whenever the dock's hidden-by-filter line is not on screen —
   * what `DockOutcomeAnnouncer` speaks (docs/UI-Designsprache.md §4.5). The visible line is
   * `aria-hidden`, so this is the only voice it has, and it must say exactly what is shown: the
   * line lives behind the dock's `!isCoarse()` gate and behind the marking half's active-set gate,
   * and a selection is only reachable with a fine pointer in the first place. `dockVisible()` is
   * implied by an active set plus a non-zero marked count and is therefore not repeated here.
   */
  protected readonly dockHiddenSelectedCount = computed(() =>
    !this.isCoarse() && this.selectedEmoteSetId() !== null
      ? this.selection.hiddenSelectedCount()
      : 0,
  );

  /**
   * The dock's own marked-count row (`usageStats.dock.marked`, next to `selection.selectedItems()
   * .length`) is created by the same set gate (`@if (selectedEmoteSetId() && activeEmoteSetId();
   * as setId)`) that fills it — the exact case §4.5 describes for `dockHiddenSelectedCount` above: appearing content does not announce
   * itself. This used to feed the announcer the raw, continuously live `selection.selectedItems()
   * .length`, which meant an individual mark or unmark — already announced by its own cell's
   * `aria-pressed` flip — spoke a *second* time here, one paragraph per click; ten keyboard marks
   * became ten status paragraphs (Opus review). Only a bulk-mark gesture (the toolbar's "mark all",
   * the per-band one) has no cell of its own to announce through, so only those two write
   * `bulkMarkAnnouncement`/`bulkMarkSnapshot` (see `markAll()`/`selectBand()`) — the number is the
   * selection size *after* the gesture, not a running total.
   *
   * 0 whenever the row itself is not on screen, same gates as `dockHiddenSelectedCount` above, plus
   * two more: 0 whenever the selection is empty, and 0 whenever the live selection no longer matches
   * `bulkMarkSnapshot` — the exact key set the last bulk gesture left behind. The second condition is
   * the one a plain "did it reach zero" check misses (Codex P2, follow-up 2026-09-19): unmarking a
   * *single* row after a bulk mark leaves the selection non-empty, so the old code kept showing the
   * stale total, and a second "mark all" that happened to land back on the very same number then
   * wrote that number again — an unchanged `role="status"` paragraph announces nothing for a mutation
   * that did not touch its text, so a real second bulk gesture went unheard. Any non-bulk change
   * (an individual click, `retainAmong()`'s pruning, `clear()`) now retires the row instead — the
   * `@if` in `DockOutcomeAnnouncer` unmounts it — so the *next* bulk gesture always remounts it fresh,
   * a genuine DOM mutation, even when the number it carries repeats. `matchesBulkMarkSnapshot()`
   * holds the one place this comparison happens; nothing else re-derives it.
   */
  private readonly bulkMarkAnnouncement = signal(0);

  /** Selection key snapshot the last bulk-mark gesture (`markAll()`/`selectBand()`) left behind, or
   *  `null` before either has ever fired — see `dockMarkedCount`'s comment for what this guards
   *  against. A signal, not a plain field, because `dockMarkedCount` reads it: a `computed()` that
   *  reaches through the instance for mutable state never reacts to it (rule 14). It would happen to
   *  work today, since every write here is paired with a `bulkMarkAnnouncement` write that does
   *  invalidate the computed — but that is a coincidence of call order, not a property anyone should
   *  have to preserve. */
  private readonly bulkMarkSnapshot = signal<ReadonlySet<string> | null>(null);

  protected readonly dockMarkedCount = computed(() =>
    !this.isCoarse() &&
    this.dockVisible() &&
    this.selectedEmoteSetId() !== null &&
    this.selection.selectedItems().length > 0 &&
    this.matchesBulkMarkSnapshot()
      ? this.bulkMarkAnnouncement()
      : 0,
  );

  /**
   * Whether the dock's copy shortcut (Designsprache §8.7, an allowance on revocation rather than a
   * requirement) is disabled.
   * `!isCoarse()` and an active 7TV set are deliberately not part of this — the shortcut only ever
   * renders inside the dock's own `!isCoarse()` gate and the marking half's set gate, so
   * re-checking either here would test a condition it can never actually violate. See
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

  /** Occupied slots after the pending selection would be deleted — the dock's one number. Counted
   *  in slots, not rows (`pendingRemovalSlots`, AK 58), against the shown set's own budget. */
  protected readonly projectedSlots = computed(() => {
    const budget = this.slotBudget();
    if (budget?.capacity == null) {
      return null;
    }
    return {
      projected: Math.max(budget.occupied - this.pendingRemovalSlots(), 0),
      capacity: budget.capacity,
    };
  });

  constructor() {
    effect(() => {
      this.load(
        this.channelName(),
        this.from(),
        this.to(),
        this.rangeResolved(),
        this.selectedEmoteSetId(),
        this.awaitingEmoteSetId(),
      );
    });

    // Decision #3: a *readable* set list that rejects the URL's id (unknown, or hidden per decision
    // #4) removes it — silently and without a history step (setParams always replaces). A pinned
    // channel (list unreadable) or an empty param never reaches shouldClearStaleEmoteSetIdParam as
    // true in the first place, so this cannot fight the pin logic below.
    effect(() => {
      if (this.shouldClearStaleEmoteSetIdParam()) {
        untracked(() => this.setQuery.setParams({ emoteSetId: '' }));
      }
    });

    // Decision #3, other half (refined 2026-09-22): once the set list has failed to load for the
    // channel now on screen *without ever having been read*, pin the display to the active set, so a
    // later successful read (the loud channel.synced reload below) cannot jump the view out from
    // under someone reading it. `emoteSetListUnavailable()` is already false for a failed reload
    // after a good read — `lastReadSetList` absorbs that one, so it never pins. Reading
    // channelName() here is what makes the pin channel-scoped rather than global.
    effect(() => {
      if (this.emoteSetListUnavailable()) {
        const channelName = this.channelName();
        untracked(() => this.pinnedToActiveChannel.set(channelName));
      }
    });

    // Latches every successful read of the set list for its channel (decision 2026-09-22) — what
    // `emoteSetList()` keeps serving when a later reload fails.
    effect(() => {
      if (this.emoteSetListResource.hasValue()) {
        const list = this.emoteSetListResource.value();
        const channelName = this.channelName();
        untracked(() => this.lastReadSetList.set({ channelName, list }));
      }
    });

    // The viewed set just became (or stopped being) the channel's active set, under rows merged for
    // the other kind of view (`viewKindStale`) — reload them now rather than wait for the next event;
    // `viewSwitching` keeps delete and vote locked until they land. Silent: nobody asked for this,
    // so neither the skeleton nor the selection may move under the user. Runs once per flip — a
    // failed reload leaves the lock (and the error state) standing instead of retrying in a loop.
    // Only while the selected set is still the one on screen: when the flip also moved the
    // selection (the URL follows the active set), the load effect above is already on it.
    effect(() => {
      if (
        this.viewKindStale() &&
        this.shownSetId() === this.selectedEmoteSetId() &&
        this.totalsChannel() === this.channelName() &&
        !this.awaitingEmoteSetId()
      ) {
        untracked(() =>
          this.loadTotals(this.channelName(), this.from(), this.to(), this.selectedEmoteSetId(), {
            preserveSelection: true,
            silent: true,
          }),
        );
      }
    });

    // #94's reconciliation, deferred: a same-channel reload's rows landed while a non-active view's
    // member list was still loading (see selectionReconcilePending). Runs once the list settles.
    effect(() => {
      if (this.selectionReconcilePending() && !this.liveMembersSettling()) {
        untracked(() => this.reconcileSelection());
      }
    });

    // The 'set-observed' preset (spec 8.5) describes the chosen set, so a set switch must move the
    // range with it — otherwise the menu would keep claiming "while this set was observed" over the
    // previous set's interval. A set that was never observed has no such range; the dates stay and
    // the menu names them as a custom range instead of a preset they no longer are.
    effect(() => {
      const range = this.setObservedPresetRange();
      untracked(() => {
        if (this.rangePreset() !== 'set-observed') {
          return;
        }
        if (range) {
          this.from.set(range.from);
          this.to.set(range.to);
        } else {
          this.rangePreset.set('custom');
        }
      });
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
      // Not while a URL-carried set is still unconfirmed: `selectedEmoteSetId()` answers with the
      // active set as a placeholder until the set list is in (see `awaitingEmoteSetId`), and rows
      // requested for that placeholder would briefly claim to be the chosen set's.
      if (!this.awaitingEmoteSetId()) {
        this.loadTotals(this.channelName(), this.from(), this.to(), this.selectedEmoteSetId(), {
          preserveSelection: true,
          silent: true,
        });
      }
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
        // The set LIST changes far less often than status/totals, but it does change (a new set
        // created on 7TV, one renamed) — and this is the one loud signal spec E19 ties a re-fetch
        // to. Never on the silent `usage.flushed` branch below.
        this.emoteSetListResource.reload();
        // Same rule one level down (spec 8.3): a non-active view's member list follows the loud
        // reload, never the silent one. A no-op while no non-active set is selected.
        this.reloadLiveMembers();
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
          if (!status || this.channelName() !== channelName) {
            return;
          }
          this.setStatus.set(status);
          // A successful status for this channel — it may claim it, same as refreshSetStatus().
          this.setStatusChannel.set(channelName);
          // Mirrors awaitSync's own resolution: a resolved set id needs the grid filled in, which a
          // status refresh alone does not do. Read now rather than closed over at effect start,
          // because the range can change while this keeps polling (see the comment above — range
          // changes do not re-run this effect) and a stale from/to would fetch the wrong window.
          // Not while a URL-carried set is unconfirmed (see the live-reload subscription above).
          if (status.activeEmoteSetId && !this.awaitingEmoteSetId()) {
            this.loadTotals(channelName, this.from(), this.to(), this.selectedEmoteSetId(), {
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
  // rebuilding every cell — the inner @for (… track emote.sevenTvEmoteId) still reconciles row
  // content correctly on a resize (DECISIONS 2026-08-30; the inner key is the 7TV id since #200).
  protected trackRow(index: number): number {
    return index;
  }

  protected refresh(): void {
    // Forces a fresh active-set request even though the channel has not changed: load()'s guard
    // above only exists to stop a bare range correction from asking twice, and must not also
    // swallow the one deliberate retry a previously failed request needs.
    this.requestedSetStatusFor = null;
    // A loud reload (spec 8.3): the member list too, not just the numbers. No-op in the active view.
    this.reloadLiveMembers();
    this.load(
      this.channelName(),
      this.from(),
      this.to(),
      this.rangeResolved(),
      this.selectedEmoteSetId(),
      this.awaitingEmoteSetId(),
    );
  }

  /**
   * Handles a choice from `<app-emote-set-menu>`. Writes the URL — plus lifting a pin, see below —
   * and nothing else (T4.0/T4.2 decision #1) — every consequence of a set switch (clearSeriesCache, retainAmong, the totals/series
   * refetch) already follows from `selectedEmoteSetId()` changing under the constructor's load
   * effect. Choosing the active set writes `''`, not its own id (setParams already treats a value
   * equal to the default as "remove the param" — see list-query-state.ts), so the URL never spells
   * out an id it would just have to recognise as "the active one" again on the next read.
   */
  protected onEmoteSetSelected(id: string): void {
    // The trigger is disabled while a delete run is out (`deleteRunActive`); this guards a choice
    // that outraces that lock.
    if (this.deleteRunActive()) {
      return;
    }
    // A deliberate choice lifts the pin for this channel (operator decision 2026-09-22): the pin
    // exists so the view never moves *unrequested*, not to ignore a choice. The dropdown can only be
    // opened on a readable list, so the chosen id goes through the usual validation from here on.
    if (this.isPinnedToActiveSet()) {
      this.pinnedToActiveChannel.set(null);
    }
    this.setQuery.setParams({ emoteSetId: id === this.activeEmoteSetId() ? '' : id });
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
    // A different sort key reorders the whole grid, and a shift-range anchor is a position in the
    // OLD order — keeping it would let the next shift-click sweep up rows the user never saw next
    // to each other. The selection itself survives, though (Konzept 2.3, the operator's decision):
    // the reorder says nothing about which rows are still wanted, only the anchor is stale.
    this.selection.resetAnchor();
  }

  /** Keeps the selection on purpose: reversing a list the user is still looking at does not move
   *  anything out from under a shift-range anchor the way a different column does. */
  protected setSortDirection(direction: string): void {
    this.sortDirection.set(direction as SortDirection);
  }

  protected inspect(emote: EmoteUsageTotal): void {
    this.inspectedId.set(emote.sevenTvEmoteId);
  }

  /** Pointer or focus landing on a cell makes it both the inspected row and the grid's tab stop. */
  protected onCellFocus(emote: EmoteUsageTotal, index: number): void {
    this.inspectedId.set(emote.sevenTvEmoteId);
    this.activeIndex.set(index);
  }

  /**
   * Whether a row has a history to open (spec #200, 7.2, drilldown gate): `/daily` asks by
   * `Emote.Id` and answers in counts, so a row without either — a live member with no counts under
   * the shown set — has nothing the dialog could show. `'left'` rows do have both and keep it.
   */
  protected canDrilldown(emote: EmoteUsageTotal): boolean {
    return emote.emoteId !== null && emote.totalUseCount !== null;
  }

  /** The name-twin marker's set names (E24, AK 59) — see `emoteSetNames`. */
  protected nameTwinSetNames(emote: EmoteUsageTotal): string {
    const names = this.emoteSetNames();
    return emote.nameTwinEmoteSetIds.map((id) => names.get(id) ?? id.slice(-6)).join(', ');
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
    this.inspectedId.set(emote.sevenTvEmoteId);
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
      // A bulk gesture, unlike an individual click, has no cell of its own to announce through —
      // see `dockMarkedCount`'s comment for why only these two writers exist.
      this.recordBulkMarkAnnouncement();
    }
  }

  /** The toolbar's mark-all control (see `showMarkAll`/`markAllDisabled`) — scope is the current
   *  filtered/sorted/banded view, exactly what `atlasOrder()` returns and the sheet is showing. */
  protected markAll(): void {
    this.selection.selectMany(this.atlasOrder());
    // See `selectBand()`'s comment just above for why this writes here and `dockMarkedCount`'s own
    // comment for why the announcer needs it at all.
    this.recordBulkMarkAnnouncement();
  }

  protected fillPercent(emote: EmoteUsageTotal): number {
    return this.fillPercents().get(emote.sevenTvEmoteId) ?? 0;
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

  /** The "no counts" group gets an outline instead of a band fill: it is not a fifth band on the
   *  lightness ramp (§2.5), and a filled chip would claim a place on it. */
  protected bandFill(band: AtlasGroupKey): string {
    return band === 'uncounted' ? 'inset-ring-1 inset-ring-border-strong' : USAGE_BAND_FILL[band];
  }

  protected trendFor(
    emote: Pick<EmoteUsageTotal, 'totalUseCount' | 'previousWindowUseCount' | 'firstSeenAt'>,
  ): UsageTrend {
    const trackedSince = this.trackedSince();
    // No counts under the shown set means no trend either — "unknown" is the one honest answer, and
    // never a "falling" computed from a coerced 0.
    if (!trackedSince || emote.totalUseCount === null || emote.previousWindowUseCount === null) {
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

  // `deletedSevenTvEmoteIds` are the run's keys — 7TV ids (spec #200, E18) — so rows are matched by
  // `sevenTvEmoteId`, never by the Guid a row may not have.
  //
  // Edits the rows on screen only when they are the run's own set of the run's own channel: the set
  // dropdown is locked for the length of the run (`deleteRunActive`), but a sync can still move the
  // active set, and the delete service outlives a channel switch. A run of another channel has
  // nothing to say about this one's rows; a run whose set is no longer the one on screen reloads
  // instead of subtracting another set's emotes and slots (the selection reconciles against it).
  protected onDeleted(deletedSevenTvEmoteIds: string[]): void {
    const run = this.deleteService.lastRun();
    if (!run || run.channelName !== this.totalsChannel()) {
      return;
    }
    if (run.setId !== this.shownSetId() || this.isNonActiveView()) {
      this.refresh();
      return;
    }
    const deleted = new Set(deletedSevenTvEmoteIds);
    // A cell frees one slot per entry, not one (spec 7.2, AK 72) — a #74 duplicate took two with
    // its one `REMOVE`. In the active view a cell's own `slotCount` is always 1 (one name per id,
    // E20), so the run's row is asked first: the panel read the set's live entries before the run
    // (`readLiveAliasesFromActiveSet`) and recorded every alias the `REMOVE` took. Summed over the
    // rows on screen before they are dropped.
    const runAliasCounts = new Map(
      run.result.items.map((item) => [item.sevenTvEmoteId, item.aliases?.length ?? 0]),
    );
    const freedSlots = this.emotes()
      .filter((emote) => emote.membership === 'live' && deleted.has(emote.sevenTvEmoteId))
      .reduce(
        (sum, emote) =>
          sum + Math.max(emote.slotCount, runAliasCounts.get(emote.sevenTvEmoteId) ?? 0),
        0,
      );
    this.totalsRows.update((items) => items.filter((item) => !deleted.has(item.sevenTvEmoteId)));
    // Freed slots are shown right away rather than waiting for the channel.synced round trip the
    // bookkeeping call triggers — the emptied bar is the feedback the delete was run for. The
    // refetch that follows a moment later confirms or corrects it.
    this.setStatus.update((status) =>
      status
        ? { ...status, occupiedSlots: Math.max(status.occupiedSlots - freedSlots, 0) }
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
    // voteLocked: the dock's button is disabled whenever voteLocked() holds (switching, loading,
    // unavailable, truncated), not only mid-switch; this guards a click that outraces that (see
    // voteLocked).
    if (this.voteBallotSize() === 0 || this.voteLocked()) {
      return;
    }

    // A non-active view creates a set-session over the *shown* set (spec 6.9, K6) — the active
    // view still creates today's null-session over the active set (E4). shownSetId() is non-null
    // here: voteLocked() above already rejects a click while the view is mid-switch, which is the
    // only state where it can be null (see its own doc comment).
    const shownSetId = this.isNonActiveView() ? this.shownSetId() : null;

    const data: CreateVoteSessionDialogData = {
      channelName: this.channelName(),
      emoteIds: shownSetId !== null ? this.voteBallotSevenTvEmoteIds : this.voteBallotEmoteIds,
      ...(shownSetId !== null ? { setSession: { emoteSetId: shownSetId } } : {}),
      // Live, like the ballot: the dialog outlives the moment the button was enabled, and a set
      // switch started behind it must block the submit rather than create a session over the
      // active set while another set is chosen.
      lockReasonKey: this.voteLockReasonKey,
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
    // The drilldown gate (spec 7.2): the trigger is not rendered for such a row, and Enter/a coarse
    // tap land here too — both must be a no-op rather than a request for a Guid that does not exist.
    if (emote.emoteId === null || emote.totalUseCount === null) {
      return;
    }
    const data: EmoteDrilldownData = {
      channelName: this.channelName(),
      from: this.from(),
      to: this.to(),
      emoteId: emote.emoteId,
      // The set these numbers were counted under, frozen into the dialog (F4, AK 64) — the set of
      // the rows on screen, not a dropdown value that may already be moving.
      emoteSetId: this.shownSetId(),
      emoteName: emote.emoteName,
      imageUrl: emote.imageUrl,
      firstSeenAt: emote.firstSeenAt,
      previousWindowUseCount: emote.previousWindowUseCount ?? undefined,
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
    // Falls back to the live signals only in the state exportButtonDisabled() already rules out:
    // before the very first totals response both atlasOrder() and the selection are empty, since
    // selectedItems() resolves against emotes(), which starts as []. totalsChannel()/totalsRange()
    // are still null in that state, and there are no rows to mislabel anyway.
    const range = this.totalsRange();
    const captured: CapturedExportScope = {
      channelName: this.totalsChannel() ?? this.channelName(),
      // The set the rows on screen belong to (spec 7.3). `importScopeCurrent` below — which gates
      // the one purpose that reads this — also requires it to be the selected set.
      emoteSetId: this.shownSetId(),
      emoteSetName: this.shownSetSummary()?.name ?? null,
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
        emoteSetName: captured.emoteSetName,
        from: captured.from,
        to: captured.to,
        // The filter describes the VISIBLE list, not the content of a selection (Konzept "Auswahl
        // überlebt Suche und Filter" 2.6): a selection built across several searches can hold rows
        // the current filter would hide, so `filtered` would be actively misleading about what the
        // "selection" scope's rows actually are. Only the "visible" scope inherits the filter state.
        filtered: choice.scope === 'selection' ? false : captured.filtered,
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
    // The selected set — the one the rows below were loaded for (importScopeCurrent checks exactly
    // that), so the picker disables the right set as "the source" (spec 7.3, 8.6).
    const emoteSetId = this.selectedEmoteSetId();
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
      // The picker (spec 8.6) disables only this one set wherever it turns up in the offer list —
      // never the whole current channel. See CapturedImportScope above.
      sourceEmoteSetId: captured.emoteSetId,
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

  /** The single writer of `bulkMarkAnnouncement`/`bulkMarkSnapshot` (see `markAll()`/`selectBand()`,
   *  the only two callers) — keeps the pair in lock-step so `dockMarkedCount` never reads one from
   *  before this gesture and the other from after it. Snapshotted *after* `selectMany()` has run,
   *  same reasoning as the comment at each call site: an additive gesture over an already-partially-
   *  marked band/view leaves a different total than the gesture's own item count. */
  private recordBulkMarkAnnouncement(): void {
    this.bulkMarkAnnouncement.set(this.selection.selectedItems().length);
    this.bulkMarkSnapshot.set(new Set(this.selection.selectedKeys()));
  }

  /** Whether the live selection still is exactly what the last bulk-mark gesture left behind — see
   *  `dockMarkedCount`'s comment for what this guards against. `false` before either `markAll()` or
   *  `selectBand()` has ever fired (`bulkMarkSnapshot` still `null`). Content equality, not identity
   *  or a revision counter: `selection.selectedKeys()` is a fresh array on every read, and a no-op
   *  `retainAmong()` call (a routine reload that prunes nothing) must not falsely count as the kind
   *  of change this exists to detect, or the row would flicker off on every silent refresh. */
  private matchesBulkMarkSnapshot(): boolean {
    const snapshot = this.bulkMarkSnapshot();
    if (snapshot === null) {
      return false;
    }
    const current = this.selection.selectedKeys();
    return current.length === snapshot.size && current.every((key) => snapshot.has(key));
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
   *
   * `choice.channelName === null` (an untracked target, spec 8.6) is no longer special-cased here
   * (T2.6): the picker itself already ran its own confirmation step before closing with such a
   * choice (`import-target-dialog.ts`), so by the time this method runs there is nothing left to
   * ask — the same `'chosen'` target below carries it straight through `startImportFlow` and
   * `toTargetSelection`'s `'untrackedSet'` branch (spec F5), which resolves the live list itself
   * rather than assuming a channel's *active* set the way the pre-K2 loader used to (F5).
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
      emoteSetService: this.emoteSetService,
      httpClient: this.httpClient,
      tokenService: this.tokenService,
      importService: this.importService,
      arbiter: this.arbiter,
    };
    startImportFlow(deps, source, { kind: 'chosen', choice });
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

  private load(
    channelName: string,
    from: string,
    to: string,
    rangeResolved: boolean,
    emoteSetId: string | null,
    awaitingEmoteSetId: boolean,
  ): void {
    // A drilldown series cached against the previous channel or range must not survive into this one.
    this.usageStatService.clearSeriesCache();
    this.isLoading.set(true);
    this.errorMessage.set(null);
    // A selection-pruned notice (#94) names emotes from the *previous* channel/range's selection —
    // this method is the constructor effect's only entry point, so it runs on every channel switch
    // and every date-range change. A channel switch clears the selection outright in `loadTotals`
    // below (see its non-`preserveSelection` branch), which would otherwise misattribute a standing
    // notice to whatever channel happens to be on screen when its timeout fires (#94 follow-up P3).
    // A date-range change or the refresh button does NOT clear since the Konzept "Auswahl überlebt
    // Suche und Filter" (überarbeitet 2026-09-19): a narrower or wider range is exactly how a user
    // checks whether a marked emote is still dead, and `loadTotals` reconciles against the new
    // payload instead — resetting the notice here first still matters, because that reconciliation
    // may leave nothing pruned (the old notice would otherwise linger for a range it no longer
    // describes) or produce its own fresh one.
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
          // Un-claims the channel (never claims it — see setStatusChannel's own comment): the
          // status on hand is gone, so nothing may still read as "this channel's set is known" —
          // importScopeCurrent() above all, which would otherwise keep the push and the import
          // doors open with no active set to reason about.
          if (this.setStatusChannel() === channelName) {
            this.setStatusChannel.set(null);
          }
        },
      });
    }

    // Under "all time" the range is still the placeholder span until the status above names the
    // tracking start. Asking now would aggregate a year of rows for a channel counted for days and
    // then discard the answer the moment the corrected range re-runs this effect. The subscription
    // above is what flips rangeResolved, so this always resumes.
    //
    // awaitingEmoteSetId is the same idea for the set dropdown (T4.2 decision #2): a URL carrying a
    // `emoteSetId` this page cannot yet confirm or reject must not fire one request for the active
    // set only to immediately refetch for the URL's real target the moment the set list answers —
    // selectedEmoteSetId() re-running is what resumes this once it does.
    if (!rangeResolved || awaitingEmoteSetId) {
      return;
    }

    this.loadTotals(channelName, from, to, emoteSetId);
    this.loadChannelSeries(channelName, from, to, emoteSetId);
  }

  // No error surface of its own: the sidecar simply carries no curve, and the page's numbers are
  // unaffected. Raising a banner here would report a failure of the secondary readout as a failure
  // of the page.
  private loadChannelSeries(
    channelName: string,
    from: string,
    to: string,
    emoteSetId: string | null,
  ): void {
    this.channelSeries.set(null);
    this.seriesFailed.set(false);
    this.usageStatService
      .getChannelSeries(channelName, from, to, emoteSetId)
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

  /**
   * #94's reconciliation of the selection against the rows now loaded. In a non-active set's view
   * the rows are only complete once the member list is there too (a marked live member without a
   * counted row has no `/totals` row to survive on), so while that list is still loading this only
   * records that a reconciliation is owed — the constructor's effect pays it once the list settles,
   * whichever way. A failed list reconciles against the DB rows alone: that is the view on screen.
   */
  private reconcileSelection(): void {
    if (this.liveMembersSettling()) {
      this.selectionReconcilePending.set(true);
      return;
    }
    this.selectionReconcilePending.set(false);
    const removedCount = this.selection.retainAmong(this.emotes());
    if (removedCount > 0) {
      this.showSelectionPrunedFeedback(removedCount);
    }
  }

  /**
   * A loud reload of the member list (spec 8.3: `channel.synced`, the refresh button) — asks the Api
   * to bypass its cache for exactly this request (`liveMembersRefreshFor`). A no-op while the active
   * set is selected (no list is ever fetched there) or while a load is already in flight, in which
   * case nothing is left marked for a later, unrelated request.
   */
  private reloadLiveMembers(): void {
    const emoteSetId = this.selectedEmoteSetId();
    if (emoteSetId === null || emoteSetId === this.activeEmoteSetId()) {
      return;
    }
    this.liveMembersRefreshFor = { channelName: this.channelName(), emoteSetId };
    if (!this.liveMembersResource.reload()) {
      this.liveMembersRefreshFor = null;
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
        this.setStatusChannel.set(channelName);
        // Range read now rather than closed over when the wait started: a range change keeps the
        // wait alive (see load()), and a stale from/to would fetch the wrong window — the same
        // reasoning as in the failure-reason recheck. Not while a URL-carried set is unconfirmed.
        if (status.activeEmoteSetId && !this.awaitingEmoteSetId()) {
          this.loadTotals(channelName, this.from(), this.to(), this.selectedEmoteSetId());
        }
      });
  }

  /**
   * `preserveSelection` and `silent` are what separates a *pushed* load (live reload, sync-failure
   * recheck) from everything else: a pushed update must not throw away a half-built delete
   * selection, and must not flash the skeleton over numbers the user is currently reading. Neither
   * flag distinguishes a channel switch from a date-range change/refresh among the *user-triggered*
   * callers (initial load, range change, refresh button) that leave both unset — see the
   * `previousTotalsChannel` comparison below for that, and Konzept "Auswahl überlebt Suche und
   * Filter" (überarbeitet 2026-09-19) Abschnitt 2.3 for why it needs to exist at all.
   */
  private loadTotals(
    channelName: string,
    from: string,
    to: string,
    emoteSetId: string | null,
    options: { preserveSelection?: boolean; silent?: boolean } = {},
  ): void {
    // The kind of view these rows are requested for, frozen now: a sync can move the active id
    // before they land, and `viewKindStale` has to notice exactly that. Untracked — this runs
    // inside the load effect, which must not start re-running on every active-id change.
    const activeAtRequest = untracked(() => this.activeEmoteSetId());
    const nonActive = emoteSetId !== null && emoteSetId !== activeAtRequest;
    this.usageStatService
      .getTotals(channelName, from, to, emoteSetId)
      .pipe(this.latestTotals)
      .subscribe({
        next: (emotes) => {
          // Read before totalsChannel is overwritten below: this is the last channel whose totals
          // actually landed, which is exactly what tells a same-channel reload (date-range change,
          // refresh button — retain) apart from a genuine channel switch (clear) once
          // `preserveSelection` is off. `null` on the very first load for this component instance
          // always takes the channel-switch branch, which is correct: there is nothing to retain yet.
          const previousTotalsChannel = this.totalsChannel();
          this.totalsRows.set(emotes);
          this.totalsSetId.set(emoteSetId ?? activeAtRequest);
          this.totalsNonActive.set(nonActive);
          // Written next to the rows themselves, never before: until this line runs, the grid still
          // shows the previous channel's emotes (see totalsChannel's declaration).
          this.totalsChannel.set(channelName);
          this.totalsRange.set({ from, to });
          // The rows on screen are now the answer to the latest request — an error left by an
          // earlier one (a failed set switch that a later reload recovered from) no longer describes
          // them, and would otherwise keep `setSwitchFailed` and the banner up over good rows.
          this.errorMessage.set(null);
          if (options.preserveSelection || previousTotalsChannel === channelName) {
            // Reconciles against the freshly loaded, UNFILTERED view — not atlasOrder()/
            // retainVisible(), which read the filtered view and would wrongly drop a row that
            // merely fell outside the current min/max-usage or name filter this reload changed the
            // numbers under (#94). Against the merged view (`emotes()`), not the bare payload: in a
            // non-active set's view a marked live member without a counted row exists only there
            // (spec #200, 7.1) — see reconcileSelection for the one case that has to wait.
            //
            // The `previousTotalsChannel === channelName` arm is what makes a date-range change and
            // the refresh button reconcile too, not just a pushed reload: both are a different SIGHT
            // of the same channel, not a different context, and changing the range is precisely how
            // a mod checks whether a marked-dead emote is still dead under a wider or narrower
            // window (live-test finding, 2026-09-19 — the Konzept originally kept these on `clear()`
            // and was corrected after this feedback). An emote the narrower range does not return is
            // pruned here like any other data-driven removal, with the existing #94 notice. A set
            // switch in the dropdown is the same kind of change — a different sight of the same
            // channel — and lands here too (AK 51).
            this.reconcileSelection();
          } else {
            // A genuine channel switch: different emotes, different grounding set entirely — nothing
            // in the old selection can even resolve against the new payload in a meaningful sense
            // (see the Konzept, same section: an id collision across channels is coincidence, not
            // continuity), so it is not a pruning case but a hard reset.
            this.selectionReconcilePending.set(false);
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
