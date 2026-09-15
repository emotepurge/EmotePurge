import { HttpErrorResponse } from '@angular/common/http';
import { Dialog } from '@angular/cdk/dialog';
import { ScrollingModule } from '@angular/cdk/scrolling';
import {
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Observable, Subject, debounceTime, merge, tap } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { ChannelService } from '../../core/channels/channel.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import { LIVE_EVENT_TYPES, LiveEvent, channelLiveUrl } from '../../core/live/live-event.model';
import { liveEvents } from '../../core/live/live-reload';
import { PointerModeService } from '../../core/pointer/pointer-mode.service';
import {
  VoteSessionResult,
  VoteSessionResults,
  VoteType,
} from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import {
  EmoteDrilldownData,
  openEmoteDrilldownDialog,
} from '../../shared/emotes/emote-drilldown-dialog';
import { EmoteSprite } from '../../shared/emotes/emote-sprite';
import { EmoteSpriteAnimated } from '../../shared/emotes/emote-sprite-animated';
import { CSV_MIME } from '../../shared/export/csv';
import {
  ExportDialogData,
  FORMAT_EXPORT_OPTIONS,
  openExportDialog,
} from '../../shared/export/export-dialog';
import { JSON_MIME } from '../../shared/export/export-envelope';
import { downloadFile } from '../../shared/export/file-download';
import {
  VotingExportInput,
  votingCsv,
  votingExportFilename,
  votingJson,
  withheldFields,
} from '../../shared/export/voting-export';
import { BackLink } from '../../shared/ui/back-link';
import { Button } from '../../shared/ui/button';
import { ConfirmDialogData, openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { VoteAudienceBadge } from '../../shared/voting/vote-audience-badge';
import { EmoteUsageFilter } from '../../shared/emotes/emote-usage-filter';
import { UsageRangeMenu } from '../../shared/emotes/usage-range-menu';
import {
  ATLAS_GAP_PX,
  ATLAS_STICKY_TOP_PX,
  SIDECAR_GAP_PX,
  atlasColumns,
} from '../../shared/grid/atlas-grid';
import { chunkIntoRows } from '../../shared/grid/grid-columns';
import { DockOutcomeAnnouncer } from '../../shared/seven-tv/dock-outcome-announcer';
import { DeletableEmote, MassDeletePanel } from '../../shared/seven-tv/mass-delete-panel';
import { ListSelection } from '../../shared/selection/list-selection';

/**
 * The ballot cell, in two sizes.
 *
 * This is not the usage atlas, and the difference is the task: there the cell is a thing to mark in
 * bulk, here it carries two vote targets that have to stay real targets. So the cell does NOT
 * shrink to 64 px everywhere — below roughly 600 px of room it grows instead, because the people
 * voting are viewers and viewers are on phones. At 96 px the emote is more than twice the size it
 * had on the old card (40 px), the vote buttons keep their 44 px height, and three fit across a
 * phone where two cards did.
 *
 * Above that width the pointer is precise and the reader is usually a moderator working through the
 * result, so the dense 64 px cell takes over.
 *
 * The threshold is measured against the CONTAINER, not the viewport — the sheet sits next to a
 * sidecar from `lg` up, so the window's width says nothing about the room the cells actually have.
 */
const CELL_WIDE_PX = 64;
const CELL_NARROW_PX = 96;
const STRIP_WIDE_PX = 24;
const STRIP_NARROW_PX = 44;
const NARROW_BELOW_PX = 600;

/** Ratio bar under the vote strip, plus the gutter between rows. */
const RATIO_BAR_PX = 2;

// Votes from other people arrive one by one. Half a second is short enough to feel live and long
// enough that a moderator clicking through ten emotes produces one refetch, not ten. The same
// window also collapses a `usage.flushed` that lands next to a vote into a single refetch.
const VOTE_RELOAD_DEBOUNCE_MS = 500;

// Below this many distinct voters the results view carries a "thin participation" notice — a
// handful of votes reads like a community verdict once it's sorted and scored, and it isn't one.
const LOW_PARTICIPATION_THRESHOLD = 5;

// A curated ballot of up to two grid rows is scanned faster than any filter is typed — the
// filter toolbar only earns its sticky row above that.
const FILTER_TOOLBAR_MIN_EMOTES = 13;

@Component({
  selector: 'app-vote-session-detail-page',
  imports: [
    BackLink,
    Button,
    EmptyState,
    NoticeBanner,
    VoteAudienceBadge,
    ScrollingModule,
    EmoteSprite,
    EmoteSpriteAnimated,
    DockOutcomeAnnouncer,
    MassDeletePanel,
    UsageRangeMenu,
    TranslocoPipe,
  ],
  templateUrl: './vote-session-detail-page.html',
})
export class VoteSessionDetailPage {
  readonly channelName = input.required<string>();
  readonly sessionId = input.required<string>();

  private readonly voteSessionService = inject(VoteSessionService);
  private readonly channelService = inject(ChannelService);
  private readonly authService = inject(AuthService);
  private readonly translocoService = inject(TranslocoService);
  private readonly languageService = inject(LanguageService);
  private readonly dialog = inject(Dialog);

  /** See UsageStatsPage: no 7TV write access without a mouse. */
  protected readonly isCoarse = inject(PointerModeService).isCoarse;

  // Lazy on purpose — reading the required channelName input during construction would throw
  // NG0950; the computed is first evaluated inside liveEvents' toObservable effect.
  private readonly liveUrl = computed(() => channelLiveUrl(this.channelName()));

  // Feeds the reload pipeline built in the constructor from vote()'s own success handler, so a
  // local vote and the server's `vote.changed` echo of it share one debounce window instead of each
  // firing their own reload — see the pipeline comment in the constructor.
  private readonly localVoteSuccess$ = new Subject<void>();

  protected readonly voteType = VoteType;
  protected readonly currentUser = this.authService.currentUser;

  // Measured, not derived from the window — see the note at CELL_WIDE_PX, and the same defect
  // fixed on the usage atlas: the shell capped content at 1024 px while window.innerWidth kept
  // counting to 2560, so a wide monitor got eight stretched cards in 992 px. Widening the cap to
  // 1280 px (#93) does not revive the defect — measuring the container is what fixed it.
  private readonly sheetRef = viewChild.required<ElementRef<HTMLElement>>('sheet');
  private readonly stickyBarRef = viewChild<ElementRef<HTMLElement>>('stickyBar');
  protected readonly sheetWidth = signal(0);

  protected readonly cellPx = computed(() =>
    this.sheetWidth() > 0 && this.sheetWidth() < NARROW_BELOW_PX ? CELL_NARROW_PX : CELL_WIDE_PX,
  );
  protected readonly stripPx = computed(() =>
    this.cellPx() === CELL_NARROW_PX ? STRIP_NARROW_PX : STRIP_WIDE_PX,
  );
  /** Enough room for the thumb icon beside the tally; below it the number carries the button. */
  protected readonly showVoteIcons = computed(() => this.stripPx() >= STRIP_NARROW_PX);
  protected readonly rowHeight = computed(
    () => this.cellPx() + this.stripPx() + RATIO_BAR_PX + ATLAS_GAP_PX,
  );
  protected readonly columns = computed(() => atlasColumns(this.sheetWidth(), this.cellPx()));
  protected readonly sidecarTop = signal(ATLAS_STICKY_TOP_PX + SIDECAR_GAP_PX);

  protected readonly results = signal<VoteSessionResults | null>(null);
  // See loadResults(): the "channelName:sessionId" this instance has already attempted a guard
  // handoff for, so a reload (vote, SSE, refresh) never re-attempts one, but a direct navigation
  // to a *different* session — same reused component, changed inputs — does.
  private guardHandoffKey: string | null = null;
  protected readonly skeletonCells = Array.from({ length: 10 }, (_, i) => i);
  protected readonly activeEmoteSetId = signal<string | null>(null);
  protected readonly errorMessage = signal<string | null>(null);

  // The one place on this page that asks for the permission instead of inferring it from the data,
  // and it has to: hasUsageData() below reads null-only rows as "not a manager", which is also what
  // a fully archived subset ballot looks like — a manager would then lose the end button on exactly
  // the session most likely to need ending. Same resource the list page uses for its own end button.
  private readonly permissionsResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => this.channelService.getPermissions(params),
  });

  protected readonly canManage = computed(
    () => this.permissionsResource.value()?.canManage ?? false,
  );

  // The server reports TotalUseCount as null to everyone CanManageChannelAsync rejects, so data
  // presence *is* the permission verdict — no separate GET /permissions round-trip needed. (An
  // all-archived subset ballot also yields null-only rows; hiding the usage UI is right there too,
  // since no usage is being computed for it.)
  protected readonly hasUsageData = computed(() =>
    (this.results()?.emotes ?? []).some((emote) => emote.totalUseCount !== null),
  );

  // Card selection exists solely to feed the mass-delete panel, so voters without delete power get
  // plain, non-interactive cards — a selection they can build but never act on is dead UI. The
  // usage verdict doubles as the gate (same CanManageChannelAsync behind both). Known trade-off: a
  // 7TV editor who is not also a channel manager gets no usage data either and loses the delete
  // entry point on this page — the usage-stats grid keeps it for them.
  protected readonly canSelectForDelete = this.hasUsageData;

  /**
   * What the sprite face does when it is touched or clicked. Two jobs on one surface was fine while
   * hover revealed a separate 20 px drilldown trigger; on a finger it was a coin toss. With the
   * delete engine gone on coarse pointers the face is free, so it carries the drilldown — gated on
   * hasUsageData for the same reason the trigger is: /usage-stats/daily sits behind the usage access
   * filter and a plain voter's tap could only earn a 403.
   */
  protected readonly cellAction = computed<'drilldown' | 'select' | 'none'>(() => {
    if (this.isCoarse()) {
      return this.hasUsageData() ? 'drilldown' : 'none';
    }
    return this.canSelectForDelete() ? 'select' : 'none';
  });

  // Same data-presence-as-permission reading as hasUsageData: the server nulls the tallies of a
  // running secret-ballot session for everyone it does not consider a manager.
  protected readonly talliesWithheld = computed(() =>
    (this.results()?.emotes ?? []).some((emote) => emote.keepVotes === null),
  );

  // The mirror image: the session is a secret ballot and still running, but this viewer sees
  // through it. Worth saying out loud — otherwise a manager has no way to tell that the numbers on
  // their screen are not the numbers the voters are looking at.
  protected readonly showManagerHiddenNotice = computed(() => {
    const results = this.results();
    return !!results && results.hideResultsUntilEnd && results.isActive && !this.talliesWithheld();
  });

  protected readonly voterCount = computed(() => this.results()?.voterCount ?? 0);
  protected readonly showLowParticipation = computed(
    () => this.voterCount() > 0 && this.voterCount() < LOW_PARTICIPATION_THRESHOLD,
  );
  // The caveat stays visible after the session ends — that is where it matters most, since the
  // final numbers are what the mass-delete acts on. Only the wording switches: "bisher"/"so far"
  // promises votes that may still come in, which is no longer true once voting is closed.
  protected readonly lowParticipationKey = computed(() =>
    pluralKey(
      this.voterCount(),
      this.results()?.isActive
        ? 'voting.detail.lowParticipation'
        : 'voting.detail.lowParticipationEnded',
    ),
  );

  protected readonly showFilterToolbar = computed(
    () => this.orderedEmotes().length >= FILTER_TOOLBAR_MIN_EMOTES,
  );

  // Freezes the card order (by emote id) across post-vote reloads, since the backend sorts by
  // score — without this, voting an emote's score down to the bottom instantly yanks its card to
  // the end of the list while the user is still looking at it. On a secret ballot the server sends
  // name order instead (score order would leak the ranking), and freezing that is equally right:
  // the reveal then happens through the numbers appearing, not through the cards jumping.
  protected readonly orderedEmoteIds = signal<string[] | null>(null);

  protected readonly orderedEmotes = computed(() => {
    const results = this.results();
    if (!results) {
      return [];
    }
    const order = this.orderedEmoteIds();
    if (!order) {
      return results.emotes;
    }

    const byId = new Map(results.emotes.map((emote) => [emote.emoteId, emote]));
    const ordered: VoteSessionResult[] = [];
    for (const id of order) {
      const emote = byId.get(id);
      if (emote) {
        ordered.push(emote);
        byId.delete(id);
      }
    }
    // Emotes not present in the frozen order (e.g. synced since the last freeze) are appended at
    // the end, in the backend's current order among themselves.
    for (const emote of results.emotes) {
      if (byId.has(emote.emoteId)) {
        ordered.push(emote);
      }
    }
    return ordered;
  });

  // Prune, don't clear (S2-16): narrowing a filter keeps the still-visible part of the selection,
  // while anything filtered out is dropped so the delete path never holds an off-screen emote.
  protected readonly usageFilter = new EmoteUsageFilter<VoteSessionResult>(() =>
    this.selection.retainVisible(),
  );

  protected readonly emotes = computed(() => this.usageFilter.apply(this.orderedEmotes()));

  protected readonly rows = computed(() => chunkIntoRows(this.emotes(), this.columns()));
  protected readonly selection = new ListSelection(this.emotes, (emote) => emote.emoteId);

  protected readonly emoteCountKey = computed(() =>
    pluralKey(this.orderedEmotes().length, 'emoteCount'),
  );

  // Resolved items rather than selection.selectedKeys(): the delete engine needs sevenTvEmoteId and
  // the display name, which only the loaded row carries. Should a selected emote vanish from the
  // list between selecting and deleting (archived by the periodic 7TV resync), it silently drops
  // out here — the conservative direction, since it can only ever delete fewer emotes than shown.
  protected readonly selectedForDelete = computed<DeletableEmote[]>(() =>
    this.selection.selectedItems().map((emote) => ({
      emoteId: emote.emoteId,
      sevenTvEmoteId: emote.sevenTvEmoteId,
      name: emote.emoteName,
    })),
  );

  /**
   * The emote the readout is describing, held by id rather than by object: every vote reloads the
   * results, and an object reference would pin the panel to a stale copy. Falls back to the first
   * row so the panel is never empty.
   */
  private readonly inspectedId = signal<string | null>(null);
  protected readonly inspected = computed(() => {
    const list = this.emotes();
    const id = this.inspectedId();
    return (id ? list.find((emote) => emote.emoteId === id) : undefined) ?? list[0] ?? null;
  });

  constructor() {
    // Deferred, not called directly — see the identical comment in VoteSessionListPage.
    effect(() => this.load());

    // A selection made in a desktop window would otherwise survive invisibly into the touch mode
    // and reappear on the way back — same reasoning as UsageStatsPage.
    effect(() => {
      if (this.isCoarse()) {
        this.selection.clear();
      }
    });

    effect((onCleanup) => {
      const element = this.sheetRef().nativeElement;
      this.sheetWidth.set(element.clientWidth);
      const observer = new ResizeObserver((entries) => {
        this.sheetWidth.set(entries[0].contentRect.width);
      });
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });

    // The toolbar only exists above FILTER_TOOLBAR_MIN_EMOTES, so this one is optional — without it
    // the sidecar pins directly under the workspace tabs, which is exactly right.
    effect((onCleanup) => {
      const element = this.stickyBarRef()?.nativeElement;
      if (!element) {
        this.sidecarTop.set(ATLAS_STICKY_TOP_PX + SIDECAR_GAP_PX);
        return;
      }
      const measure = () =>
        this.sidecarTop.set(ATLAS_STICKY_TOP_PX + element.offsetHeight + SIDECAR_GAP_PX);
      measure();
      const observer = new ResizeObserver(measure);
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });

    // Live tally *and* live usage, off the one channel stream this page already holds open — plus
    // vote()'s own success, merged into the same window rather than reloading on its own. A cast
    // vote must not wait on the Api's `vote.changed` echo of it (Redis publish, then SSE) to show
    // up, so it feeds this pipeline directly; the echo still arrives afterwards and, landing inside
    // the same debounce window, collapses into the same reload instead of causing a second one. The
    // channel-status side-load only runs when a `channel.synced` was among the *live* events — that
    // value changes on a 7TV sync, not on a vote or a flush, and a plain vote never carries it since
    // localVoteSuccess$ contributes no event type to `seen`.
    const seen = new Set<string>();
    merge(
      liveEvents(this.liveUrl, (event) => this.isRelevantLiveEvent(event)).pipe(
        tap((event) => seen.add(event.type)),
      ),
      this.localVoteSuccess$,
    )
      .pipe(debounceTime(VOTE_RELOAD_DEBOUNCE_MS), takeUntilDestroyed())
      .subscribe(() => {
        this.loadResults({ freeze: false });
        if (seen.has(LIVE_EVENT_TYPES.channelSynced)) {
          this.loadActiveEmoteSetId();
        }
        seen.clear();
      });
  }

  /**
   * Tracks the outer virtual-scroll rows by index instead of by object identity.
   *
   * `rows()` (chunkIntoRows()) rebuilds every row array from scratch on each recompute, so
   * CdkVirtualForOf's default identity-based differ sees a full remove+add on every reload and
   * recycles the detached views into different row positions. The inner `@for (… track
   * emote.emoteId)` then finds unfamiliar ids in those recycled views and rebuilds every cell —
   * including a brand-new `EmoteSprite` per cell, which stays hidden until its `<img>` fires
   * `load`, even for an image already cached. That was the double flash after casting a vote:
   * one flash per reload the vote triggers.
   *
   * Indexing by position instead keeps the row views themselves stable across a recompute, so CDK
   * only updates their context and the inner @for sees the same ids at the same positions — no
   * rebuild. A column-count change (resize) still renders correctly under this, because the inner
   * @for reconciles each row's actual content by emote id regardless of how many rows there now
   * are.
   */
  protected trackRow(index: number): number {
    return index;
  }

  // One guarded entry point for click/Enter/Space on the sprite, branched on cellAction. Both acting
  // branches swallow the keyboard default: the element carries role="button", and the ARIA button
  // pattern requires Space not to scroll the page as well as activate. On the drilldown branch that
  // is currently invisible — the CDK freezes background scrolling the moment the dialog opens — but
  // an element does not get to rely on what the thing it opens happens to do. The 'none' branch is
  // neither focusable nor a button and keeps every default.
  // Also pins the readout, so a tap on a touch screen (where nothing hovers) still tells the voter
  // which emote they are looking at.
  protected onCardActivate(emote: VoteSessionResult, event: MouseEvent | KeyboardEvent): void {
    this.inspectedId.set(emote.emoteId);
    const action = this.cellAction();
    if (action === 'none') {
      return;
    }
    if (event.type === 'keydown') {
      event.preventDefault();
    }
    if (action === 'drilldown') {
      this.openDrilldown(emote);
      return;
    }
    this.selection.onRowClick(emote, event as MouseEvent);
  }

  protected inspect(emote: VoteSessionResult): void {
    this.inspectedId.set(emote.emoteId);
  }

  /** Share of the keep votes in the ratio bar under the strip; null while a tally is withheld. */
  protected keepShare(emote: VoteSessionResult): number | null {
    if (emote.keepVotes === null || emote.deleteVotes === null) {
      return null;
    }
    const total = emote.keepVotes + emote.deleteVotes;
    return total === 0 ? null : (emote.keepVotes / total) * 100;
  }

  protected formatDateTime(iso: string): string {
    return new Date(iso).toLocaleString(toLocale(this.languageService.lang()), {
      dateStyle: 'short',
      timeStyle: 'short',
    });
  }

  // LOCALE_ID is never set app-wide (bootstrap-time static, can't react to a runtime language
  // switch), so DecimalPipe always formatted with 'en-US' regardless of the active UI language —
  // same reasoning as formatDateTime()/toLocale() above, just for numbers instead of dates.
  // Signed on purpose: the score is a net vote balance now, and a bare "2" hides whether the
  // community is two votes for or two votes against.
  protected formatScore(value: number): string {
    return new Intl.NumberFormat(toLocale(this.languageService.lang()), {
      maximumFractionDigits: 0,
      signDisplay: 'exceptZero',
    }).format(value);
  }

  // Which of the two drilldown labels the sprite carries. The count belongs in the accessible name
  // on a coarse pointer, because the readout row that states it is `pointer-coarse:hidden` there —
  // without it a screen reader on a phone cannot learn an emote's usage without opening the dialog.
  // The server withholds the count for a plain voter, and then there is nothing to name.
  protected drilldownLabelKey(emote: VoteSessionResult): string {
    return emote.totalUseCount === null
      ? 'usageStats.drilldown.open'
      : 'usageStats.drilldown.openWithCount';
  }

  // Unsigned counterpart to formatScore(), same locale reasoning. Returns '' for the withheld case,
  // where the key picked above has no {{count}} placeholder to fill.
  protected formatUseCount(count: number | null): string {
    return count === null
      ? ''
      : new Intl.NumberFormat(toLocale(this.languageService.lang())).format(count);
  }

  // Full, untruncated stats wording as a tooltip — the visible lines may still ellipsize on narrow
  // cards. Omits whichever half the server withheld, so the tooltip never states a number the card
  // deliberately doesn't have.
  protected statsTitle(emote: VoteSessionResult): string {
    const score =
      emote.score === null
        ? this.translocoService.translate('voting.detail.resultsHiddenShort')
        : `${this.translocoService.translate('voting.detail.scoreLabel')} ${this.formatScore(emote.score)}`;
    if (emote.totalUseCount === null) {
      return score;
    }
    const usage = this.translocoService.translate('usageStats.usageLabel');
    return `${emote.totalUseCount}x ${usage} · ${score}`;
  }

  protected keepButtonTitle(emote: VoteSessionResult): string {
    return this.voteButtonTitle(
      emote,
      emote.myVote === VoteType.Keep ? 'voting.detail.retractVote' : 'voting.detail.keepAriaLabel',
      emote.keepVotes,
    );
  }

  protected deleteButtonTitle(emote: VoteSessionResult): string {
    return this.voteButtonTitle(
      emote,
      emote.myVote === VoteType.Delete
        ? 'voting.detail.retractVote'
        : 'voting.detail.deleteAriaLabel',
      emote.deleteVotes,
    );
  }

  protected refresh(): void {
    this.load();
  }

  // Opened from the card's info icon; only rendered for viewers with usage access (hasUsageData),
  // since /usage-stats/daily sits behind the usage-stats authorization filter. The range is the
  // session's own usage window; the vote block carries the card's tallies — null inside stays
  // "withheld" and the dialog renders nothing for it.
  protected openDrilldown(emote: VoteSessionResult): void {
    const results = this.results();
    if (!results) {
      return;
    }
    const data: EmoteDrilldownData = {
      channelName: this.channelName(),
      from: results.startedAt.slice(0, 10),
      to: (results.endedAt ?? new Date().toISOString()).slice(0, 10),
      emoteId: emote.emoteId,
      emoteName: emote.emoteName,
      imageUrl: emote.imageUrl,
      vote: {
        keepVotes: emote.keepVotes,
        deleteVotes: emote.deleteVotes,
        score: emote.score,
        myVote: emote.myVote,
      },
    };
    openEmoteDrilldownDialog(this.dialog, data);
  }

  // Exports the *visible* list (filtered + frozen order). Client-side serialization of the loaded
  // read model on purpose (A12): the `null`s in it are the server's visibility verdict, so the
  // withheld tally/usage columns drop out without this page re-implementing the secret-ballot rule.
  protected openExport(): void {
    const results = this.results();
    if (!results) {
      return;
    }
    const input: VotingExportInput = {
      channelName: this.channelName(),
      results,
      rows: this.emotes(),
    };
    const withheld = withheldFields(input.rows);
    const noticeKeys: string[] = [];
    if (withheld.includes('keepVotes')) {
      noticeKeys.push('export.withheldTallies');
    }
    if (withheld.includes('totalUseCount')) {
      noticeKeys.push('export.withheldUsage');
    }
    const data: ExportDialogData = {
      rowCount: input.rows.length,
      filtered: input.rows.length !== this.orderedEmotes().length,
      // This page has no grid selection to export — the ballot itself already is the subset.
      selectionCount: null,
      noticeKeys,
      optionsLegendKey: 'export.formatLabel',
      options: FORMAT_EXPORT_OPTIONS,
    };
    openExportDialog(this.dialog, data).closed.subscribe((choice) => {
      if (choice?.optionId === 'csv') {
        downloadFile(votingExportFilename(input, 'csv'), votingCsv(input), CSV_MIME);
      } else if (choice?.optionId === 'json') {
        downloadFile(votingExportFilename(input, 'json'), votingJson(input), JSON_MIME);
      }
    });
  }

  /**
   * Confirmed, unlike the same action in the list: here the button sits right next to "refresh" in
   * the header, and ending a session cannot be undone. Names the session for the same reason the
   * delete dialog does — the title is the only thing distinguishing two sessions of one channel.
   */
  protected endSession(title: string): void {
    const data: ConfirmDialogData = {
      message: this.translocoService.translate('voting.detail.endConfirm', { title }),
      confirmLabel: this.translocoService.translate('voting.list.end'),
    };
    openConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }
      this.errorMessage.set(null);
      this.voteSessionService.end(this.channelName(), Number(this.sessionId())).subscribe({
        // Full reload, not a local patch of isActive: the endpoint answers with a summary while
        // this page holds results, and ending a secret ballot unseals every tally at once — the
        // page after the click shows materially more than the page before it.
        next: () => this.load({ freeze: false }),
        error: (error: HttpErrorResponse) => this.errorMessage.set(apiErrorTranslationKey(error)),
      });
    });
  }

  protected vote(emote: VoteSessionResult, type: VoteType): void {
    // Defensive fallback, not the primary gate — voteSessionAccessGuard already required a login
    // to reach this page, but the session cookie/token can still expire while already viewing it.
    if (!this.currentUser()) {
      this.authService.login(window.location.pathname);
      return;
    }

    this.errorMessage.set(null);

    // Clicking the same vote type again retracts it, returning the emote to the neutral state —
    // otherwise a Keep vote could only ever be overwritten by a Delete vote, never undone.
    const request$: Observable<unknown> =
      emote.myVote === type
        ? this.voteSessionService.retractVote(
            this.channelName(),
            Number(this.sessionId()),
            emote.emoteId,
          )
        : this.voteSessionService.castVote(
            this.channelName(),
            Number(this.sessionId()),
            emote.emoteId,
            type,
          );

    // What the button is about to become, applied to `results` the moment the request succeeds —
    // not "the moment the debounced reload runs". The vote buttons unlock right after this response
    // (they are disabled mid-mutation), so a second click landing inside the 500 ms reload window
    // that follows must already see this vote's own effect. Without it, `emote.myVote` stayed at
    // its pre-vote value until the debounced loadResults() below eventually ran — which could be
    // long after a busy session's own SSE traffic keeps resetting that shared timer — so pressing
    // the same button twice in a row cast a second time instead of retracting the first.
    const nextMyVote = emote.myVote === type ? null : type;

    request$.subscribe({
      next: () => {
        this.applyLocalMyVote(emote.emoteId, nextMyVote);
        // Feeds the shared reload pipeline built in the constructor rather than reloading here
        // directly — see that pipeline's comment. Only results are affected; the channel status
        // (loadActiveEmoteSetId) never reloads off a vote, only off `channel.synced`. This debounced
        // reload still runs (for tallies/score/order and other people's votes) — the fix above just
        // means it no longer has to be the thing that makes this voter's own click show up.
        this.localVoteSuccess$.next();
      },
      error: (error: HttpErrorResponse) => this.handleVoteError(error),
    });
  }

  protected onDeleted(deletedIds: string[]): void {
    this.results.update((results) =>
      results
        ? {
            ...results,
            emotes: results.emotes.filter((emote) => !deletedIds.includes(emote.emoteId)),
          }
        : results,
    );
    this.selection.clear();
  }

  // The delete run finished on 7TV, but the backend could not confirm it — refetch instead of
  // filtering locally, so the list never claims a state the server does not share.
  protected onReloadRequested(): void {
    this.selection.clear();
    this.load();
  }

  /**
   * `vote.changed` is per session and must match this one. `usage.flushed` is channel-scoped and
   * carries no session id — the stream is already this channel's, so its arrival alone is the
   * signal. Usage no longer feeds the score, but the flush event stays subscribed: managers see
   * the raw usage figures as context on every card (and filter on them), and chat usage only
   * moves on the worker's 30 s batch flush — without the refetch those numbers would sit at
   * whatever the first load happened to catch.
   *
   * `channel.synced` means the emote inventory itself moved (mass delete, 7TV add/remove, set
   * swap) — the ballot and the archived badges are stale, and so is the set id the mass-delete
   * panel binds to. The server only fires it when a sync actually changed something, so this does
   * not reintroduce the reload-on-a-timer the side-load comment in the constructor warns about.
   */
  private isRelevantLiveEvent(event: LiveEvent): boolean {
    if (
      event.type === LIVE_EVENT_TYPES.usageFlushed ||
      event.type === LIVE_EVENT_TYPES.channelSynced
    ) {
      return true;
    }
    return (
      event.type === LIVE_EVENT_TYPES.voteChanged && event.sessionId === Number(this.sessionId())
    );
  }

  // The tally in parentheses is dropped rather than shown as "(0)" or "(null)" when the server
  // withheld it — the tooltip is the one place a hidden number could still slip out.
  private voteButtonTitle(
    emote: VoteSessionResult,
    labelKey: string,
    tally: number | null,
  ): string {
    if (emote.isArchived) {
      return this.translocoService.translate('voting.detail.archivedVoteDisabled');
    }
    const label = this.translocoService.translate(labelKey);
    return tally === null ? label : `${label} (${tally})`;
  }

  // Was a second, competing status→message mapping alongside apiErrorTranslationKey; the codes it
  // re-derived by status (vote_session_ended, vote_session_not_found, emote_not_eligible) all exist
  // in the response bodies, so it can share the one mapping now. 401 is gone entirely —
  // apiAuthInterceptor handles it app-wide.
  //
  // 403 stays a special case on purpose: it comes from VoteEligibilityFilter and means one specific
  // thing here — the wrong role for *this session's* audience. The generic 403 message points at the
  // mod-role cache instead, which would be actively misleading in, say, a subs-only session.
  private handleVoteError(error: HttpErrorResponse): void {
    this.errorMessage.set(
      error.status === 403 ? 'voting.detail.errors.forbidden' : apiErrorTranslationKey(error),
    );
  }

  private load(options: { freeze: boolean } = { freeze: true }): void {
    this.loadResults(options);
    this.loadActiveEmoteSetId();
  }

  private loadResults(options: { freeze: boolean }): void {
    const channelName = this.channelName();
    const sessionId = Number(this.sessionId());

    // voteSessionAccessGuard already fetched /results once to verify login + audience eligibility
    // before this component was even mounted. The very first loadResults() run for a given
    // channel+session takes that response instead of asking again — see
    // VoteSessionService.takeGuardResults. Keyed rather than a one-time boolean so navigating
    // directly between two sessions (component reused, inputs change) still picks up the new
    // guard run's stash instead of being permanently skipped after the first entry.
    const handoffKey = `${channelName}:${sessionId}`;
    if (this.guardHandoffKey !== handoffKey) {
      this.guardHandoffKey = handoffKey;
      const stashed = this.voteSessionService.takeGuardResults(channelName, sessionId);
      if (stashed) {
        this.applyResults(stashed, options);
        return;
      }
    }

    this.voteSessionService.getResults(channelName, sessionId).subscribe({
      next: (results) => this.applyResults(results, options),
      error: () => this.errorMessage.set('voting.detail.errors.loadFailed'),
    });
  }

  private applyResults(results: VoteSessionResults, options: { freeze: boolean }): void {
    this.results.set(results);
    if (options.freeze) {
      this.orderedEmoteIds.set(results.emotes.map((emote) => emote.emoteId));
    }
    // No selection.clear() here on purpose: ListSelection keys by emote id, so the freshly
    // deserialized objects this assigns resolve back to the same selection. Clearing would
    // throw away a 50-emote selection on every single vote, since vote() reloads through here.

    // #133: reconcile against unfiltered `results.emotes`, not usageFilter's filtered view —
    // retainVisible() would wrongly drop rows a filter (not a reload) pushed out; same choice as
    // usage-stats-page.ts's #94 fix (see DECISIONS.md for both). Fixed-ballot sessions keep
    // archived members listed and thus selected; dynamic sessions drop them from `results.emotes`
    // and get pruned here. Silent by design: this page only reads selection.selectedItems(),
    // which already excludes a dead key.
    this.selection.retainAmong(results.emotes);
  }

  /**
   * Patches one emote's `myVote` into `results` right after vote() gets a successful response —
   * see the comment at that call site. Tallies/score/order stay whatever the last full load said
   * until the debounced reload catches up; only the pressed/retract state has to be immediate.
   */
  private applyLocalMyVote(emoteId: string, myVote: VoteType | null): void {
    this.results.update((results) =>
      results
        ? {
            ...results,
            emotes: results.emotes.map((emote) =>
              emote.emoteId === emoteId ? { ...emote, myVote } : emote,
            ),
          }
        : results,
    );
  }

  /** Split out of load() so the live-update path can refetch the tally alone — this value only
   *  changes when the 7TV set is resynced, never as a result of a vote. */
  private loadActiveEmoteSetId(): void {
    this.channelService.getStatus(this.channelName()).subscribe({
      next: (status) => this.activeEmoteSetId.set(status.activeEmoteSetId),
      error: () => this.activeEmoteSetId.set(null),
    });
  }
}
