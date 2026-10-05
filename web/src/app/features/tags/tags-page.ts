import { Dialog } from '@angular/cdk/dialog';
import { CdkVirtualScrollViewport, ScrollingModule } from '@angular/cdk/scrolling';
import { HttpErrorResponse } from '@angular/common/http';
import { NgTemplateOutlet } from '@angular/common';
import {
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  linkedSignal,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, convertToParamMap } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { ChannelService } from '../../core/channels/channel.service';
import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import { DockClearanceService } from '../../core/layout/dock-clearance.service';
import { WideViewportService } from '../../core/layout/wide-viewport.service';
import { channelLiveUrl, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { CHANNEL_RELOAD_DEBOUNCE_MS, liveReload } from '../../core/live/live-reload';
import { PointerModeService } from '../../core/pointer/pointer-mode.service';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import {
  SevenTvRunArbiter,
  SevenTvRunKind,
  refusedStartMessage,
} from '../../core/seven-tv/seven-tv-run-arbiter';
import { EmoteTagEntry, EmoteTagSummary } from '../../core/tags/emote-tag.model';
import { EmoteTagService } from '../../core/tags/emote-tag.service';
import { EmoteSprite } from '../../shared/emotes/emote-sprite';
import {
  ATLAS_CELL_PX,
  ATLAS_ROW_PX,
  AtlasRow,
  atlasColumns,
  atlasRowOfIndex,
  moveInAtlas,
} from '../../shared/grid/atlas-grid';
import { chunkIntoRows } from '../../shared/grid/grid-columns';
import { ListSelection } from '../../shared/selection/list-selection';
import { tagRunDockHasContent } from '../../shared/seven-tv/action-dock';
import { DeleteAbortNotice } from '../../shared/seven-tv/delete-flow';
import { DeleteProgressSection } from '../../shared/seven-tv/delete-progress-section';
import { DockOutcomeAnnouncer } from '../../shared/seven-tv/dock-outcome-announcer';
import { ImportProgressSection } from '../../shared/seven-tv/import-progress-section';
import { RestoreProgressSection } from '../../shared/seven-tv/restore-progress-section';
import {
  TagRunActions,
  TagRunFeedback,
  settledTagPlayIn,
  settledTagRemoval,
} from '../../shared/tags/tag-run-actions';
import { TagRunOrphanNotice } from '../../shared/tags/tag-run-orphan-notice';
import { BackLink } from '../../shared/ui/back-link';
import { Button } from '../../shared/ui/button';
import { openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { openTagNameDialog } from './tag-name-dialog';

/** Same lifetime as every other transient status message (design doc §4.5). */
export const TAG_FEEDBACK_MS = 4000;

/** Spec 9.4: the entries grid is virtualized (window-scrolled) only above this many entries. */
export const VIRTUALIZE_ABOVE = 200;

/** Cells of the grid-shaped skeleton: about three rows at desktop width. */
const SKELETON_CELLS = 36;

const TAG_NOT_FOUND_KEY = 'errors.api.tag_not_found';

/** The run kinds whose dock section this page does not mount: a lock by one of them is explained
 *  beside the tag buttons (plan 3.8). The undo is started on the usage page only. */
const UNSHOWN_RUN_KINDS: readonly SevenTvRunKind[] = ['undo'];

type GridRow = AtlasRow<EmoteTagEntry, never>;

interface TagsParams {
  readonly channelName: string;
  /** `undefined` = let the server resolve the active set (there is none, or it is unknown). */
  readonly emoteSetId: string | undefined;
}

interface EntriesParams extends TagsParams {
  readonly tagId: number;
}

interface Feedback {
  readonly key: string;
  readonly params: Record<string, unknown>;
}

/**
 * `?tag=` as a tag id: a positive integer, anything else (missing, `abc`, `-1`, `1.5`) is "no tag".
 * A malformed link is not worth an error — the list is the honest answer to it.
 */
export function parseTagParam(raw: string | null): number | null {
  if (raw === null || !/^\d+$/.test(raw)) {
    return null;
  }
  const id = Number(raw);
  return Number.isSafeInteger(id) && id > 0 ? id : null;
}

/**
 * The channel's tags (spec 9.3/9.4, #201 T-B): a ruled list and, for the chosen tag, its entries as
 * a sprite grid. From `lg` up list and detail stand side by side; below it the page drills down —
 * `?tag=` set means "detail", with an up-link back to the list (§8.6). The choice lives in the URL,
 * so a link from the usage page's filter row (`../tags?tag=<id>`) lands on it.
 *
 * Reading needs usage-stats access (route guard); every write needs channel management, so the
 * write controls are absent rather than locked for everybody else. On a coarse pointer the grid
 * selects nothing (spec 9.4): no 7TV-writing work starts from a phone, and the one
 * selection-bound action in T-B is not worth a selection mode of its own there.
 *
 * T-C (#201, spec 9.4/9.5) adds the tag's played-in state, the placement marks and the tag runs:
 * "Einspielen"/"Ausräumen" (`TagRunActions`) in the detail head, and — at page level, outside the
 * detail, so a run stays visible whichever tag (or the list) is on screen — the run dock with the
 * import, delete and restore sections, the permanently mounted `DockOutcomeAnnouncer` and the
 * page's run status region. The page follows `channel.synced` for its channel (the usage page's
 * mechanism), so a set switch on 7TV reaches the open flows' set guard here too.
 */
@Component({
  selector: 'app-tags-page',
  imports: [
    BackLink,
    Button,
    DeleteProgressSection,
    DockOutcomeAnnouncer,
    EmoteSprite,
    EmptyState,
    ImportProgressSection,
    NgTemplateOutlet,
    NoticeBanner,
    RestoreProgressSection,
    RouterLink,
    ScrollingModule,
    SkeletonRows,
    TagRunActions,
    TagRunOrphanNotice,
    TranslocoPipe,
  ],
  templateUrl: './tags-page.html',
})
export class TagsPage {
  readonly channelName = input.required<string>();

  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly dialog = inject(Dialog);
  private readonly transloco = inject(TranslocoService);
  private readonly languageService = inject(LanguageService);
  private readonly channelService = inject(ChannelService);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly emoteSetService = inject(SevenTvEmoteSetService);
  private readonly tagService = inject(EmoteTagService);
  private readonly arbiter = inject(SevenTvRunArbiter);
  private readonly importService = inject(SevenTvImportService);
  private readonly deleteService = inject(SevenTvDeleteService);
  private readonly restoreService = inject(SevenTvRestoreService);
  private readonly dockClearance = inject(DockClearanceService);
  /** Capability, not layout (see `PointerModeService`): no grid selection without a mouse. */
  readonly isCoarse = inject(PointerModeService).isCoarse;
  /** Structure, not styling: list and detail side by side, or a drilldown between them. */
  readonly isWide = inject(WideViewportService).isWide;
  /** Only rendered while a tag's entries are on screen, hence not `.required`. */
  private readonly sheetRef = viewChild<ElementRef<HTMLElement>>('sheet');
  private readonly viewport = viewChild(CdkVirtualScrollViewport);
  /** The rendered `.app-dock`, absent whenever the template's gate does not mount it. */
  private readonly dockRef = viewChild<ElementRef<HTMLElement>>('dock');

  /** A computed, not a field: `channelName` is a required input (NG0950 in the constructor). */
  private readonly liveUrl = computed(() => channelLiveUrl(this.channelName()));
  protected readonly unshownRunKinds = UNSHOWN_RUN_KINDS;

  private readonly queryParams = toSignal(this.route.queryParamMap, {
    initialValue: convertToParamMap({}),
  });

  /** The tag the URL asks for, `null` for none or a malformed value. Not yet checked against the list. */
  readonly selectedTagId = computed(() => parseTagParam(this.queryParams().get('tag')));

  /** Below `lg` with a tag chosen: the detail stands alone and carries the up-link to the list. */
  readonly isDrilldown = computed(() => this.selectedTagId() !== null && !this.isWide());
  protected readonly showList = computed(() => this.isWide() || this.selectedTagId() === null);
  protected readonly showDetail = computed(() => this.isWide() || this.selectedTagId() !== null);

  private readonly permissionsResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => this.channelService.getPermissions(params),
  });
  /** Create, rename, delete and taking emotes out need channel management (spec 6.3). */
  readonly canManage = computed(() =>
    this.permissionsResource.hasValue() ? this.permissionsResource.value().canManage : false,
  );
  /** The operator switch for tag runs (`Tags:RunsEnabled`), `false` until the answer is in. */
  readonly tagRunsEnabled = computed(() =>
    this.permissionsResource.hasValue() ? this.permissionsResource.value().tagRunsEnabled : false,
  );

  private readonly setStatusResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => this.emoteAdminService.getSetStatus(params),
  });
  /**
   * Answered either way — a failed status read means "no active set known", not "wait forever".
   * A *reload* (`channel.synced`) counts as settled: the answer on hand stays valid until the new
   * one lands, so the tag list and the entries keep their request (and their value) meanwhile and
   * the detail — with `TagRunActions` and any dialog its flow has open — is not torn down by a mere
   * reload, which would read as a set switch and abort that dialog. Only a first load (a new
   * channel) is unsettled.
   */
  private readonly setStatusSettled = computed(() => {
    const status = this.setStatusResource.status();
    return status !== 'idle' && status !== 'loading';
  });
  /**
   * The set the in-set numbers refer to; `null` when the channel has none (or it is unknown). A
   * status reload that *fails* keeps the last known id (a failed read says nothing about the set);
   * only an answer without a set, a 404, or a new channel clears it.
   */
  readonly activeEmoteSetId = linkedSignal<
    { has: boolean; id: string | null; notFound: boolean; loading: boolean },
    string | null
  >({
    source: () => {
      const resource = this.setStatusResource;
      const has = resource.hasValue();
      const error = resource.error();
      return {
        has,
        id: has ? resource.value().activeEmoteSetId || null : null,
        notFound: error instanceof HttpErrorResponse && error.status === 404,
        loading: resource.status() === 'loading' || resource.status() === 'idle',
      };
    },
    computation: (source, previous) => {
      if (source.has) {
        return source.id;
      }
      if (source.loading || source.notFound || previous === undefined) {
        return null;
      }
      return previous.value;
    },
  });

  /** Only for the set's name in the header line; never asked for without an active set. */
  private readonly emoteSetListResource = rxResource({
    params: () => (this.activeEmoteSetId() === null ? undefined : this.channelName()),
    stream: ({ params }) => this.emoteSetService.listChannelEmoteSets(params),
  });
  /**
   * The active set's name, its id when the list has no name for it or failed, and `null` while that
   * is still being found out — the header line waits rather than swapping an id for a name.
   */
  protected readonly activeSetName = computed(() => {
    const id = this.activeEmoteSetId();
    if (id === null) {
      return null;
    }
    // A list on hand names the set even while a live reload refreshes it.
    if (this.emoteSetListResource.hasValue()) {
      return this.emoteSetListResource.value().sets.find((set) => set.id === id)?.name ?? id;
    }
    return this.emoteSetListResource.isLoading() ? null : id;
  });
  /**
   * Said only when it is known: an empty active-set id, or the 404 the endpoint answers for a
   * channel without one. Any other failure leaves the line empty rather than claim "no set".
   */
  protected readonly noActiveSet = computed(() => {
    if (this.setStatusResource.hasValue()) {
      return !this.setStatusResource.value().activeEmoteSetId;
    }
    const error = this.setStatusResource.error();
    return error instanceof HttpErrorResponse && error.status === 404;
  });

  /** Waits for the set status, so the list is asked for once, already for the right set. */
  private readonly tagsParams = computed<TagsParams | undefined>(
    () =>
      this.setStatusSettled()
        ? { channelName: this.channelName(), emoteSetId: this.activeEmoteSetId() ?? undefined }
        : undefined,
    { equal: sameParams },
  );
  readonly tagsResource = rxResource({
    params: () => this.tagsParams(),
    stream: ({ params }) => this.tagService.list(params.channelName, params.emoteSetId),
  });
  protected readonly tags = computed<readonly EmoteTagSummary[]>(() =>
    this.tagsResource.hasValue() ? this.tagsResource.value().tags : [],
  );
  protected readonly tagsLoaded = computed(() => this.tagsResource.hasValue());
  protected readonly tagsErrorKey = computed(() => {
    const error = this.tagsResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  /** The chosen tag as the loaded list knows it; `null` while loading and for an unknown id. */
  readonly selectedTag = computed<EmoteTagSummary | null>(() => {
    const id = this.selectedTagId();
    return id === null ? null : (this.tags().find((tag) => tag.id === id) ?? null);
  });

  private readonly entriesParams = computed<EntriesParams | undefined>(
    () => {
      const tag = this.selectedTag();
      const tagsParams = this.tagsParams();
      return tag && tagsParams ? { ...tagsParams, tagId: tag.id } : undefined;
    },
    { equal: sameParams },
  );
  readonly entriesResource = rxResource({
    params: () => this.entriesParams(),
    stream: ({ params }) =>
      this.tagService.listEntries(params.channelName, params.tagId, params.emoteSetId),
  });
  protected readonly entries = computed<readonly EmoteTagEntry[]>(() =>
    this.entriesResource.hasValue() ? this.entriesResource.value().entries : [],
  );
  protected readonly entriesLoaded = computed(() => this.entriesResource.hasValue());
  protected readonly entriesErrorKey = computed(() => {
    const error = this.entriesResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  protected readonly cellPx = ATLAS_CELL_PX;
  protected readonly rowHeight = ATLAS_ROW_PX;
  protected readonly skeletonCells = Array.from({ length: SKELETON_CELLS }, (_, i) => i);
  private readonly sheetWidth = signal(0);
  protected readonly columns = computed(() => atlasColumns(this.sheetWidth()));
  protected readonly gridRows = computed<GridRow[]>(() => {
    const columns = this.columns();
    return chunkIntoRows(this.entries(), columns).map((items, row) => ({
      kind: 'cells',
      items,
      startIndex: row * columns,
    }));
  });
  protected readonly virtualized = computed(() => this.entries().length > VIRTUALIZE_ABOVE);

  /** The grid's one tab stop (roving tabindex, as on the usage atlas). */
  protected readonly activeIndex = signal(0);
  private readonly inspectedId = signal<string | null>(null);
  /** What the meta line under the grid describes: the hovered or focused cell. */
  protected readonly inspected = computed(() => {
    const id = this.inspectedId();
    return id === null
      ? null
      : (this.entries().find((entry) => entry.sevenTvEmoteId === id) ?? null);
  });

  /**
   * Whether the grid selects at all. Its only action in T-B is "Aus Tag entfernen", a management
   * action — offering a selection whose dock could only say "clear" would be a control with nothing
   * behind it.
   */
  readonly selectable = computed(() => !this.isCoarse() && this.canManage());
  readonly selection = new ListSelection<EmoteTagEntry>(
    () => this.entries(),
    (entry) => entry.sevenTvEmoteId,
  );
  protected readonly markedCount = computed(() => this.selection.selectedKeys().length);
  /** The small dock in the flow under the grid (spec 9.4, §8.7) — only while something is marked. */
  protected readonly dockShown = computed(() => this.selectable() && this.markedCount() > 0);

  /** Where "Einspielen"/"Ausräumen" exist at all (spec 8, 9.4): runs switched on, a fine pointer and
   *  a known active set. Rename and delete do not depend on it. */
  protected readonly tagRunsShown = computed(
    () => this.tagRunsEnabled() && !this.isCoarse() && this.activeEmoteSetId() !== null,
  );

  /**
   * The page's run dock (plan 3.8, spec 9.5): the same import, delete and restore sections as the
   * usage page's dock, fed by the same service signals, but through `tagRunDockHasContent` — this
   * page has no marking half, so no active-set gate may hide a clear-out. Page level: a run started
   * here outlives the detail it was started from.
   */
  protected readonly dockVisible = computed(() =>
    tagRunDockHasContent({
      deleteShown: this.deleteService.isRunning() || this.deleteService.queue().length > 0,
      restoreShown: this.restoreService.isRunning() || this.restoreService.queue().length > 0,
      restoreNoticePending: this.restoreService.duplicateNoticePending(),
      // `app-import-progress-section`'s own gate, `run()` included (it draws nothing without one).
      importShown:
        this.importService.run() !== null &&
        (this.importService.isRunning() || this.importService.queue().length > 0),
      // Also the drift notice of a tag play-in whose re-check held back replace rows — on this
      // page it has no other place to show.
      importNoticePending: this.importService.duplicateNoticePending(),
    }),
  );
  /** The dock's rendered height — the page's own bottom padding and the shell's clearance. */
  protected readonly dockHeightPx = signal(0);

  /** What stopped the delete section's restore entry (its `notice` output). Persists like the
   *  panel's abort notice until a new start clears it. */
  protected readonly runNotice = signal<DeleteAbortNotice | null>(null);

  /** The arbiter's transient "nothing started" notice, as on the usage page. Reads `lang()` so
   *  a language switch re-translates the blocking kind's noun. */
  protected readonly refusedStartNotice = computed(() => {
    this.languageService.lang();
    const refused = this.arbiter.refusedStart();
    return refused === null
      ? null
      : refusedStartMessage(refused.blockedBy, (key) => this.transloco.translate(key));
  });

  protected readonly removalPending = signal(false);
  protected readonly deletePending = signal(false);
  /** A failed write; persists until the next write or another tag (§4.4). */
  protected readonly actionErrorKey = signal<string | null>(null);
  protected readonly feedback = signal<Feedback | null>(null);

  private feedbackTimer: ReturnType<typeof setTimeout> | null = null;
  /**
   * The chosen tag id once a loaded list has confirmed it. A later list without it means the tag was
   * deleted while it was open (elsewhere, or through a 404) — that is worth a sentence. An id that
   * was never in any list (an old link) is just dropped.
   */
  private confirmedTagId: number | null = null;
  private previousChannel: string | null = null;

  constructor() {
    effect((onCleanup) => {
      const element = this.sheetRef()?.nativeElement;
      if (!element) {
        return;
      }
      this.sheetWidth.set(element.clientWidth);
      const observer = new ResizeObserver((entries) => {
        this.sheetWidth.set(entries[0].contentRect.width);
      });
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });

    // A channel switch reuses this component (same route config), so its per-channel state goes.
    effect(() => {
      const channel = this.channelName();
      untracked(() => {
        if (this.previousChannel !== null && this.previousChannel !== channel) {
          this.clearFeedback();
          this.actionErrorKey.set(null);
          this.runNotice.set(null);
          this.confirmedTagId = null;
        }
        this.previousChannel = channel;
      });
    });

    // Another tag (or channel) starts with nothing marked and the tab stop on its first cell.
    effect(() => {
      this.channelName();
      this.selectedTagId();
      untracked(() => {
        this.selection.clear();
        this.activeIndex.set(0);
        this.inspectedId.set(null);
        this.actionErrorKey.set(null);
      });
    });

    // A reload drops marks of entries that left the tag; a shorter grid moves the tab stop back.
    effect(() => {
      const entries = this.entries();
      untracked(() => {
        this.selection.retainAmong(entries);
        if (this.activeIndex() >= entries.length) {
          this.activeIndex.set(0);
        }
      });
    });

    effect(() => {
      if (!this.selectable()) {
        untracked(() => this.selection.clear());
      }
    });

    // Reconciles `?tag=` with the loaded list. Only against a settled list: right after an own
    // create, the reload is still out while the URL already names the new tag.
    effect(() => {
      const id = this.selectedTagId();
      if (id === null || !this.tagsResource.hasValue() || this.tagsResource.isLoading()) {
        return;
      }
      const present = this.tagsResource.value().tags.some((tag) => tag.id === id);
      untracked(() => {
        if (present) {
          this.confirmedTagId = id;
          return;
        }
        if (this.confirmedTagId === id) {
          this.showFeedback('tags.page.tagGone');
        }
        this.confirmedTagId = null;
        this.clearTagParam();
      });
    });

    // The open tag was deleted elsewhere: its entries answer 404, and the list reload lets the
    // reconciliation above fall back to the list.
    effect(() => {
      if (this.entriesErrorKey() === TAG_NOT_FOUND_KEY) {
        untracked(() => this.tagsResource.reload());
      }
    });

    // The dock's real height, mirrored into the page padding and the shell's clearance — measured,
    // not guessed, like the usage page's (a run with many rows grows the dock up to 70vh).
    effect((onCleanup) => {
      const element = this.dockRef()?.nativeElement;
      if (!element) {
        this.dockHeightPx.set(0);
        this.dockClearance.release();
        return;
      }
      const measure = () => {
        const height = element.offsetHeight;
        this.dockHeightPx.set(height);
        this.dockClearance.reserve(height);
      };
      measure();
      const observer = new ResizeObserver(measure);
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });

    // A run starting makes an old restore refusal stale. A flow start that has no run yet is
    // `TagRunActions`' `started` output (template).
    effect(() => {
      if (this.arbiter.activeRun() !== null) {
        untracked(() => this.runNotice.set(null));
      }
    });

    // A tag run or a restore that closes changes the tags' numbers (spec 9.6): reload. Also
    // while `TagRunActions` is not mounted (the list alone on a narrow screen). A run that was
    // already settled when the page opened is not news.
    let firstSettled = true;
    let lastSlots: string[] = [];
    effect(() => {
      const restore = this.restoreService.run();
      const key = [
        settledTagPlayIn(this.importService.run()),
        settledTagRemoval(this.deleteService.run()),
        restore !== null && restore.phase === 'closed' ? restore.runId : null,
      ].join('|');
      const slots = key.split('|');
      // Only a slot that turns into a NEW non-null value is news; one turning null (a new run
      // replacing it, a dismissal) is not.
      const opened = slots.some((slot, index) => slot !== '' && slot !== lastSlots[index]);
      lastSlots = slots;
      if (firstSettled) {
        firstSettled = false;
        return;
      }
      if (opened) {
        untracked(() => this.reloadTagState());
      }
    });

    // `channel.synced` for this channel (the usage page's mechanism; docs/DECISIONS.md, #201 T-C,
    // overriding spec 9.6): set status, set list, tags and entries are read again. The status
    // reload keeps its answer meanwhile (see `setStatusSettled`), so a reload of the same set changes no request key and tears nothing
    // down; a real set switch changes the key, the tags and entries follow it, and an open tag flow
    // sees the new active set at its confirm-time guard.
    liveReload(this.liveUrl, {
      accept: [LIVE_EVENT_TYPES.channelSynced],
      debounceMs: CHANNEL_RELOAD_DEBOUNCE_MS,
    }).subscribe(() => {
      this.setStatusResource.reload();
      this.emoteSetListResource.reload();
      this.reloadTagState();
    });

    inject(DestroyRef).onDestroy(() => {
      this.clearFeedbackTimer();
      // Give the clearance back at once — the next page may have no dock to release it.
      this.dockClearance.release();
    });
  }

  protected openCreate(): void {
    const channel = this.channelName();
    openTagNameDialog(this.dialog, { channelName: channel }).closed.subscribe((tag) => {
      if (!tag || this.channelName() !== channel) {
        return;
      }
      this.actionErrorKey.set(null);
      // Reload first: the reconciliation waits for it before it judges the new id.
      this.tagsResource.reload();
      this.selectTag(tag.id);
    });
  }

  protected openRename(): void {
    const tag = this.selectedTag();
    if (!tag) {
      return;
    }
    const channel = this.channelName();
    openTagNameDialog(this.dialog, {
      channelName: channel,
      tag: { id: tag.id, name: tag.name },
    }).closed.subscribe((renamed) => {
      if (!renamed || this.channelName() !== channel) {
        return;
      }
      this.actionErrorKey.set(null);
      this.tagsResource.reload();
    });
  }

  protected confirmDelete(): void {
    const tag = this.selectedTag();
    if (!tag || this.deletePending()) {
      return;
    }
    const channel = this.channelName();
    // Spec 8 "Tag löschen mit Platzierungen": allowed, but said first. The label stays "Tag
    // löschen" — the dialog asks about the tag, the placements are a consequence; the hint goes
    // into the message.
    const message = [this.transloco.translate('tags.deleteDialog.message', { tag: tag.name })];
    if (tag.placedCount > 0) {
      message.push(
        this.transloco.translate(pluralKey(tag.placedCount, 'tags.deleteDialog.placedHint'), {
          count: this.formatCount(tag.placedCount),
        }),
      );
    }
    openConfirmDialog(this.dialog, {
      message: message.join(' '),
      confirmLabel: this.transloco.translate('tags.deleteDialog.confirm'),
    }).closed.subscribe((confirmed) => {
      if (!confirmed || this.channelName() !== channel) {
        return;
      }
      this.deleteTag(channel, tag);
    });
  }

  protected removeMarked(): void {
    const tag = this.selectedTag();
    const ids = this.selection.selectedKeys();
    if (!tag || ids.length === 0 || this.removalPending()) {
      return;
    }
    const channel = this.channelName();
    this.actionErrorKey.set(null);
    this.removalPending.set(true);
    this.tagService.removeEntries(channel, tag.id, ids).subscribe({
      next: (result) => {
        this.removalPending.set(false);
        if (this.channelName() !== channel) {
          return;
        }
        this.selection.clear();
        this.showFeedback(pluralKey(result.removedCount, 'tags.feedback.unassigned'), {
          count: result.removedCount,
          tag: tag.name,
        });
        this.tagsResource.reload();
        this.entriesResource.reload();
      },
      error: (error: HttpErrorResponse) =>
        this.failWrite(channel, error, () => this.removalPending.set(false)),
    });
  }

  protected clearSelection(): void {
    this.selection.clear();
  }

  /** A tag flow started: an old restore refusal no longer describes anything. */
  protected onTagRunStarted(): void {
    this.runNotice.set(null);
  }

  /** A tag run settled, or its report did: the numbers and the entries are read again (9.6). */
  protected onTagRunCompleted(): void {
    this.reloadTagState();
  }

  protected onTagRunFeedback(feedback: TagRunFeedback): void {
    this.showFeedback(feedback.key, feedback.params);
  }

  protected retryTags(): void {
    this.tagsResource.reload();
  }

  protected retryEntries(): void {
    this.entriesResource.reload();
  }

  protected onCellClick(entry: EmoteTagEntry, index: number, event: MouseEvent): void {
    this.activeIndex.set(index);
    this.inspectedId.set(entry.sevenTvEmoteId);
    if (this.selectable()) {
      this.selection.onRowClick(entry, event);
    }
  }

  protected onCellFocus(entry: EmoteTagEntry, index: number): void {
    this.activeIndex.set(index);
    this.inspectedId.set(entry.sevenTvEmoteId);
  }

  protected inspect(entry: EmoteTagEntry): void {
    this.inspectedId.set(entry.sevenTvEmoteId);
  }

  protected onGridKeydown(event: KeyboardEvent): void {
    const next = moveInAtlas(this.gridRows(), this.activeIndex(), event.key);
    if (next === null) {
      return;
    }
    event.preventDefault();
    this.activeIndex.set(next);
    this.focusCell(next);
  }

  /** Index tracking keeps the row views across a reflow; the cells inside track by 7TV id. */
  protected trackRow(index: number): number {
    return index;
  }

  protected plural(count: number, baseKey: string): string {
    return pluralKey(count, baseKey);
  }

  protected formatCount(count: number): string {
    return count.toLocaleString(toLocale(this.languageService.lang()));
  }

  /** `heute: <name>` applies only to an emote that is in the set under another name today. */
  protected renamedTo(entry: EmoteTagEntry): string | null {
    return entry.currentName !== null && entry.currentName !== entry.alias
      ? entry.currentName
      : null;
  }

  /** The cell's accessible name: alias, then what the meta line would add in words. */
  protected entryLabel(entry: EmoteTagEntry): string {
    const parts = [entry.alias];
    const current = this.renamedTo(entry);
    if (current !== null) {
      parts.push(this.transloco.translate('tags.page.currentName', { name: current }));
    }
    if (entry.inSet === false) {
      parts.push(this.transloco.translate('tags.page.notInSetBadge'));
    }
    if (entry.placedByThisTag) {
      parts.push(this.transloco.translate('tags.page.placedMark'));
    }
    return parts.join(' · ');
  }

  protected formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString(toLocale(this.languageService.lang()), {
      dateStyle: 'short',
    });
  }

  protected usageStatsLink(): unknown[] {
    return ['/channels', this.channelName(), 'usage-stats'];
  }

  protected tagsLink(): unknown[] {
    return ['/channels', this.channelName(), 'tags'];
  }

  private deleteTag(channel: string, tag: EmoteTagSummary): void {
    this.actionErrorKey.set(null);
    this.deletePending.set(true);
    this.tagService.delete(channel, tag.id).subscribe({
      next: () => {
        this.deletePending.set(false);
        if (this.channelName() !== channel) {
          return;
        }
        // A tag picked while this request was in flight is not ours to drop.
        if (this.selectedTagId() === tag.id) {
          // Our own deletion: no "deleted elsewhere" sentence from the reconciliation.
          this.confirmedTagId = null;
          this.selection.clear();
          this.clearTagParam();
        }
        this.tagsResource.reload();
        this.showFeedback('tags.page.deleted', { tag: tag.name });
      },
      error: (error: HttpErrorResponse) =>
        this.failWrite(channel, error, () => this.deletePending.set(false)),
    });
  }

  /** A failed write shows its reason; a `tag_not_found` also reloads, so the page falls back. */
  private failWrite(channel: string, error: HttpErrorResponse, settle: () => void): void {
    settle();
    if (this.channelName() !== channel) {
      return;
    }
    const key = apiErrorTranslationKey(error);
    this.actionErrorKey.set(key);
    if (key === TAG_NOT_FOUND_KEY) {
      this.tagsResource.reload();
    }
  }

  /** Tags and the open tag's entries, read again; a resource that is already loading keeps its
   *  request (`reload()` is a no-op there), so two triggers in one tick cost one request each. */
  private reloadTagState(): void {
    this.tagsResource.reload();
    this.entriesResource.reload();
  }

  private selectTag(tagId: number): void {
    void this.router.navigate(this.tagsLink(), { queryParams: { tag: tagId } });
  }

  /** Drops `?tag=` in place: nobody should be able to step "back" onto a tag that is gone. */
  private clearTagParam(): void {
    void this.router.navigate(this.tagsLink(), { replaceUrl: true });
  }

  private showFeedback(key: string, params: Record<string, unknown> = {}): void {
    this.clearFeedbackTimer();
    this.feedback.set({ key, params });
    this.feedbackTimer = setTimeout(() => {
      this.feedback.set(null);
      this.feedbackTimer = null;
    }, TAG_FEEDBACK_MS);
  }

  private clearFeedback(): void {
    this.clearFeedbackTimer();
    this.feedback.set(null);
  }

  private clearFeedbackTimer(): void {
    if (this.feedbackTimer !== null) {
      clearTimeout(this.feedbackTimer);
      this.feedbackTimer = null;
    }
  }

  /** Focuses a cell, scrolling the virtual viewport first when the cell is not mounted. */
  private focusCell(index: number): void {
    const find = () =>
      this.sheetRef()?.nativeElement.querySelector<HTMLElement>(`[data-tag-cell-index="${index}"]`);
    const rendered = find();
    if (rendered) {
      rendered.focus();
      return;
    }
    const rowIndex = atlasRowOfIndex(this.gridRows(), index);
    if (rowIndex < 0) {
      return;
    }
    this.viewport()?.scrollToIndex(rowIndex);
    requestAnimationFrame(() => find()?.focus());
  }
}

/** Flat record equality, so a recomputed but unchanged request key does not refetch. */
function sameParams<T extends object>(a: T | undefined, b: T | undefined): boolean {
  if (a === undefined || b === undefined) {
    return a === b;
  }
  const keys = Object.keys(a) as (keyof T)[];
  return keys.length === Object.keys(b).length && keys.every((key) => a[key] === b[key]);
}
