import { Dialog } from '@angular/cdk/dialog';
import { NgTemplateOutlet } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  afterNextRender,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  Injector,
  input,
  signal,
  untracked,
  WritableSignal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { EMPTY, filter, interval, Observable, pairwise, switchMap } from 'rxjs';

import { normalizeChannelName } from '../../core/channels/channel-name';
import {
  apiErrorCode,
  apiErrorTranslationKey,
  GENERIC_ERROR_TRANSLATION_KEY,
} from '../../core/i18n/api-error';
import { channelLiveUrl, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { CHANNEL_RELOAD_DEBOUNCE_MS, liveReload, liveReopened } from '../../core/live/live-reload';
import { PagedResult } from '../../core/models/paged-result.model';
import { pageVisible } from '../../core/page-visibility/page-visibility';
import { PointerModeService } from '../../core/pointer/pointer-mode.service';
import {
  SEVEN_TV_RUN_KIND_LABEL_KEY,
  SevenTvRunArbiter,
} from '../../core/seven-tv/seven-tv-run-arbiter';
import { wishErrorKey } from '../../core/wishes/wish-error';
import type { Wish, WishListView } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { isConfirmedAction, isSevenTvAction, WishAction } from '../../core/wishes/wish-view';
import { Pager } from '../../shared/pagination/pager';
import { Button } from '../../shared/ui/button';
import { openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { WishRow } from './wish-row';
import { WishUsageStore } from './wish-usage';

const PAGE_SIZE = 20;

/** Safety net (plan 2.4 e): a missed event is caught up at the latest after this long. */
export const WISH_SAFETY_RELOAD_MS = 5 * 60_000;

/** The "now" every row reads moves on once a minute (plan 2.2; no clock in a template). */
const CLOCK_TICK_MS = 60_000;

/**
 * The actions this page carries out. The action table knows more; a row renders only what is in
 * here, so nothing offers an action that is not built yet: T6 adds `adopt`, T7 `edit` and `reAdd`,
 * T8 `add7tv`, T9 `remove`.
 */
const OFFERED_ACTIONS: ReadonlySet<WishAction> = new Set<WishAction>([
  'extend',
  'retry',
  'keep',
  'close',
]);

/** Id of the page's one status line that names a blocking 7TV run (plan 2.7). */
export const WISH_RUN_LOCK_ID = 'wishes-run-lock';

type SectionId = WishListView | 'pinned';

/** One of the three lists: its page, its last accepted answer and its two kinds of error. */
interface ListSection {
  readonly view: WishListView;
  readonly page: WritableSignal<number>;
  readonly result: WritableSignal<PagedResult<Wish> | null>;
  readonly loadError: WritableSignal<string | null>;
  readonly actionError: WritableSignal<string | null>;
  /** Number of the latest request of this view, given out when the request is issued. */
  seq: number;
}

/** The wish named by `?wish=` while no loaded list holds it. */
type PinnedState =
  | { id: number; kind: 'loading' }
  | { id: number; kind: 'found'; wish: Wish }
  | { id: number; kind: 'gone' }
  | { id: number; kind: 'error'; key: string };

/**
 * The wish work list of a channel (#485, plan T4): "Handlungsbedarf", "Laufend" and "Beendet", each
 * its own paged list, plus the wish a link points to (`?wish=`).
 *
 * **Which answer counts (plan 2.4, Sol-P2-8).** Every list request belongs to its channel, its view
 * and its page, and gets a number per view when it is issued. An answer is taken only while the
 * channel is still the page's, the view still shows that page and no younger request of the view
 * has been issued; a dropped answer changes neither the rows nor the error. Requests are not
 * cancelled: the check is the contract, not the timing.
 *
 * **When the lists reload:** `wish.changed` and `channel.synced` (one debounced burst, one request
 * per loaded view; the reconcile runs after every sync, so a lost `wish.changed` is caught up there),
 * a reopen of *this channel's* stream (Sol-R2-P2-6: per stream, not the service-wide status), the
 * tab becoming visible again, a safety tick every five minutes while visible, and right after an
 * action of the page itself. Nothing is re-sorted optimistically.
 */
@Component({
  selector: 'app-channel-wishes-page',
  imports: [
    Button,
    EmptyState,
    NgTemplateOutlet,
    NoticeBanner,
    Pager,
    SkeletonRows,
    TranslocoPipe,
    WishRow,
  ],
  template: `
    <div class="flex flex-col gap-8">
      <!-- The one place that names a blocking 7TV run; the locked 7TV buttons point at the visible
           line via aria-describedby (plan 2.7). The sr-only region stays mounted so the text is
           announced when it appears (§4.5); the visible twin is aria-hidden so it is not read twice.
           Both sit outside the flex flow while empty: an sr-only box is absolutely positioned. -->
      <p role="status" class="sr-only">
        @if (lockMessage(); as message) {
          <ng-container
            [ngTemplateOutlet]="lockText"
            [ngTemplateOutletContext]="{ $implicit: message }"
          />
        }
      </p>
      @if (lockMessage(); as message) {
        <p [id]="lockId" aria-hidden="true" class="text-sm text-fg-secondary">
          <ng-container
            [ngTemplateOutlet]="lockText"
            [ngTemplateOutletContext]="{ $implicit: message }"
          />
        </p>
      }

      @if (pinnedView(); as pinned) {
        <section class="flex flex-col gap-3" aria-labelledby="wishes-pinned-title">
          <h2 id="wishes-pinned-title" class="text-lg font-semibold">
            {{ 'wishes.page.pinned.title' | transloco }}
          </h2>
          @if (pinnedActionError(); as key) {
            <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
          }
          @switch (pinned.kind) {
            @case ('loading') {
              <app-skeleton-rows [count]="1" />
            }
            @case ('found') {
              @if (pinnedWish(); as wish) {
                <ul class="-mx-3 divide-y divide-border border-y border-border">
                  <ng-container
                    [ngTemplateOutlet]="row"
                    [ngTemplateOutletContext]="{ $implicit: wish, section: 'pinned' }"
                  />
                </ul>
              }
            }
            @case ('gone') {
              <app-notice-banner variant="info">{{
                'wishes.page.pinned.gone' | transloco
              }}</app-notice-banner>
            }
            @case ('error') {
              @if (pinnedErrorKey(); as key) {
                <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
              }
            }
          }
        </section>
      }

      @for (section of alwaysLoaded; track section.view) {
        <section
          class="flex flex-col gap-3"
          [attr.aria-labelledby]="'wishes-' + section.view + '-title'"
        >
          <h2
            #top
            [id]="'wishes-' + section.view + '-title'"
            tabindex="-1"
            class="scroll-mt-24 text-lg font-semibold"
          >
            {{ 'wishes.page.' + section.view + '.title' | transloco }}
            @if (section.result(); as result) {
              <span class="ml-1 font-normal text-fg-muted">{{ result.totalCount }}</span>
            }
          </h2>
          <ng-container
            [ngTemplateOutlet]="listBody"
            [ngTemplateOutletContext]="{ $implicit: section, top: top }"
          />
        </section>
      }

      <section class="flex flex-col gap-3" aria-labelledby="wishes-ended-title">
        <h2
          #endedTop
          id="wishes-ended-title"
          tabindex="-1"
          class="scroll-mt-24 text-lg font-semibold"
        >
          <button
            type="button"
            class="inline-flex items-center gap-2"
            [attr.aria-expanded]="endedOpen()"
            aria-controls="wishes-ended-list"
            (click)="toggleEnded()"
          >
            <svg
              aria-hidden="true"
              viewBox="0 0 20 20"
              class="size-4 shrink-0 text-fg-muted transition-transform motion-reduce:transition-none"
              [class.rotate-90]="endedOpen()"
            >
              <path d="M7 4l6 6-6 6" fill="none" stroke="currentColor" stroke-width="2" />
            </svg>
            {{ 'wishes.page.ended.title' | transloco }}
            @if (endedOpen() && ended.result(); as result) {
              <span class="font-normal text-fg-muted">{{ result.totalCount }}</span>
            }
          </button>
        </h2>
        <div id="wishes-ended-list" class="flex flex-col gap-3" [hidden]="!endedOpen()">
          @if (endedOpen()) {
            <ng-container
              [ngTemplateOutlet]="listBody"
              [ngTemplateOutletContext]="{ $implicit: ended, top: endedTop }"
            />
          }
        </div>
      </section>
    </div>

    <ng-template #lockText let-message>
      @if (message.kindKey) {
        {{ message.key | transloco: { kind: message.kindKey | transloco } }}
      } @else {
        {{ message.key | transloco }}
      }
    </ng-template>

    <ng-template #listBody let-section let-top="top">
      @if (section.actionError(); as key) {
        <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
      }
      @if (section.loadError(); as key) {
        <app-notice-banner variant="error">
          {{ key | transloco }}
          <button notice-action type="button" appButton="outline" (click)="retryLoad(section)">
            {{ 'wishes.page.reload' | transloco }}
          </button>
        </app-notice-banner>
      }
      @if (section.result(); as result) {
        @if (result.items.length === 0) {
          <app-empty-state
            [title]="'wishes.page.' + section.view + '.empty' | transloco"
            [description]="'wishes.page.' + section.view + '.emptyHint' | transloco"
          />
        } @else {
          <ul class="-mx-3 divide-y divide-border border-y border-border">
            @for (wish of result.items; track wish.id) {
              <ng-container
                [ngTemplateOutlet]="row"
                [ngTemplateOutletContext]="{ $implicit: wish, section: section.view }"
              />
            }
          </ul>
          <app-pager
            [page]="section.page()"
            [totalPages]="result.totalPages"
            [scrollTarget]="top"
            (pageChange)="goToPage(section, $event)"
          />
        }
      } @else if (!section.loadError()) {
        <app-skeleton-rows [count]="3" />
      }
    </ng-template>

    <ng-template #row let-wish let-section="section">
      <!-- tabindex="-1": the jump target of ?wish= takes focus without becoming a tab stop. -->
      <li class="px-3 py-3" tabindex="-1" [attr.data-wish-id]="wish.id">
        <app-wish-row
          [wish]="wish"
          [now]="now()"
          [offered]="offered"
          [coarse]="isCoarse()"
          [sevenTvLocked]="sevenTvLocked()"
          [lockDescriptionId]="lockDescriptionId()"
          [busy]="pendingIds().has(wish.id)"
          [usage]="usageStore.blockFor(wish)"
          [trackUsage]="section !== 'ended'"
          (seen)="usageStore.setVisible(wish, $event)"
          (action)="onAction(section, wish, $event)"
        />
      </li>
    </ng-template>
  `,
})
export class ChannelWishesPage {
  readonly channelName = input.required<string>();
  /** Query parameter `wish`: the id of the wish a link points to (plan 2.4, Sol-R2-P2-5). */
  readonly wish = input<string>();

  private readonly wishService = inject(WishService);
  private readonly dialog = inject(Dialog);
  private readonly transloco = inject(TranslocoService);
  private readonly arbiter = inject(SevenTvRunArbiter);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly isCoarse = inject(PointerModeService).isCoarse;

  protected readonly offered = OFFERED_ACTIONS;
  protected readonly lockId = WISH_RUN_LOCK_ID;

  private readonly channel = computed(() => normalizeChannelName(this.channelName()));
  private readonly liveUrl = computed(() => channelLiveUrl(this.channel()));
  private readonly visible = pageVisible();

  protected readonly now = signal(new Date());

  /** Usage of the visible open/running rows, one `GET /{id}` each, valid five minutes (T5). */
  protected readonly usageStore = new WishUsageStore(
    (id) => this.wishService.get(this.channel(), id),
    () => Date.now(),
    () => this.visible(),
  );

  private readonly open = createSection('open');
  private readonly running = createSection('running');
  protected readonly ended = createSection('ended');
  protected readonly alwaysLoaded: readonly ListSection[] = [this.open, this.running];

  protected readonly endedOpen = signal(false);

  private readonly pinned = signal<PinnedState | null>(null);
  private pinnedSeq = 0;
  protected readonly pinnedActionError = signal<string | null>(null);

  /** Wishes whose action request is on the way; their row's buttons wait. */
  protected readonly pendingIds = signal<ReadonlySet<number>>(new Set());

  /** The target already focused once; a reload must not pull the focus back. */
  private focusedTarget: number | null = null;

  private readonly targetId = computed(() => parseWishId(this.wish()));

  private readonly loadedIds = computed(() => {
    const ids = new Set<number>();
    const sections = this.endedOpen() ? [this.open, this.running, this.ended] : this.alwaysLoaded;
    for (const section of sections) {
      section.result()?.items.forEach((wish) => ids.add(wish.id));
    }
    return ids;
  });

  /** The wishes on screen that may show usage: the two loaded lists and the pinned row. */
  private readonly usageRows = computed<Wish[]>(() => [
    ...(this.open.result()?.items ?? []),
    ...(this.running.result()?.items ?? []),
    ...(this.pinnedWish() ? [this.pinnedWish() as Wish] : []),
  ]);

  /** The pinned state for the current target, unless a loaded list holds the wish by now. */
  protected readonly pinnedView = computed(() => {
    const pinned = this.pinned();
    const id = this.targetId();
    return pinned !== null && pinned.id === id && !this.loadedIds().has(id) ? pinned : null;
  });

  protected readonly pinnedWish = computed(() => {
    const pinned = this.pinnedView();
    return pinned?.kind === 'found' ? pinned.wish : null;
  });

  protected readonly pinnedErrorKey = computed(() => {
    const pinned = this.pinnedView();
    return pinned?.kind === 'error' ? pinned.key : null;
  });

  protected readonly sevenTvLocked = this.arbiter.startLocked;

  /**
   * What the status line says while a 7TV run holds the start. Only while the page offers a 7TV
   * action at all: without one there is no locked button for the line to explain.
   */
  protected readonly lockMessage = computed(() => {
    if (![...this.offered].some(isSevenTvAction) || !this.arbiter.startLocked()) {
      return null;
    }
    const claim = this.arbiter.activeClaim();
    return claim
      ? { key: `wishes.lock.${claim.phase}`, kindKey: SEVEN_TV_RUN_KIND_LABEL_KEY[claim.kind] }
      : { key: 'wishes.lock.starting', kindKey: null };
  });

  protected readonly lockDescriptionId = computed(() =>
    this.lockMessage() ? WISH_RUN_LOCK_ID : null,
  );

  constructor() {
    // A channel switch reuses this component (same route): every list starts over, and every
    // request still on the way for the old channel is outdated by the bumped numbers.
    effect(() => {
      const channel = this.channel();
      untracked(() => this.startChannel(channel));
    });

    // The jump target: focus its row in a loaded list, or load it on its own and pin it above the
    // sections. Decided only once the two lists loaded on entry have answered, so a wish on the
    // first page never flashes up pinned first.
    effect(() => {
      const id = this.targetId();
      const loaded = this.loadedIds();
      const settled = this.alwaysLoaded.every(
        (section) => section.result() !== null || section.loadError() !== null,
      );
      const pinned = this.pinned();
      untracked(() => this.resolveTarget(id, loaded, settled, pinned));
    });

    // A row that scrolled into view gets its usage here, and a state change reaches it as a new key.
    effect(() => {
      const rows = this.usageRows();
      untracked(() => this.usageStore.sync(rows));
    });

    interval(CLOCK_TICK_MS)
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        this.now.set(new Date());
        // Safety net for the five-minute value life: a visible row whose value ran out loads again.
        this.usageStore.tick();
      });

    liveReload(this.liveUrl, {
      accept: [LIVE_EVENT_TYPES.wishChanged, LIVE_EVENT_TYPES.channelSynced],
      debounceMs: CHANNEL_RELOAD_DEBOUNCE_MS,
    }).subscribe(() => this.reloadAll());

    // Reopened stream and a window that is visible again: counts may have moved unseen, so every
    // held usage value runs out (plan 2.4 c, d) before the lists reload.
    liveReopened(this.liveUrl).subscribe(() => this.reloadAfterGap());

    const visible$ = toObservable(this.visible);
    visible$
      .pipe(
        pairwise(),
        filter(([was, is]) => !was && is),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.reloadAfterGap());
    visible$
      .pipe(
        switchMap((isVisible) => (isVisible ? interval(WISH_SAFETY_RELOAD_MS) : EMPTY)),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.reloadAll());
  }

  protected toggleEnded(): void {
    const opening = !this.endedOpen();
    this.endedOpen.set(opening);
    if (opening) {
      this.load(this.ended);
    }
  }

  protected goToPage(section: ListSection, page: number): void {
    section.page.set(page);
    section.result.set(null);
    this.load(section);
  }

  protected retryLoad(section: ListSection): void {
    this.load(section);
  }

  protected onAction(sectionId: SectionId, wish: Wish, action: WishAction): void {
    if (!this.offered.has(action) || this.pendingIds().has(wish.id)) {
      return;
    }
    if (!isConfirmedAction(action)) {
      this.runAction(sectionId, wish, action);
      return;
    }
    const name = wish.alias || wish.emoteName;
    openConfirmDialog(this.dialog, {
      message: this.transloco.translate(`wishes.confirm.${action}`, { name }),
      confirmLabel: this.transloco.translate(`wishes.actions.${action}`),
    }).closed.subscribe((confirmed) => {
      if (confirmed) {
        this.runAction(sectionId, wish, action);
      }
    });
  }

  private startChannel(channel: string): void {
    for (const section of [this.open, this.running, this.ended]) {
      section.seq++;
      section.page.set(1);
      section.result.set(null);
      section.loadError.set(null);
      section.actionError.set(null);
    }
    this.endedOpen.set(false);
    this.pinnedSeq++;
    this.pinned.set(null);
    this.pinnedActionError.set(null);
    this.focusedTarget = null;
    this.usageStore.reset();
    if (channel) {
      this.load(this.open);
      this.load(this.running);
    }
  }

  private reloadAfterGap(): void {
    this.usageStore.expireAll();
    this.reloadAll();
  }

  /** Every loaded view once, plus the pinned wish; the ended list only while it is open. */
  private reloadAll(): void {
    this.load(this.open);
    this.load(this.running);
    if (this.endedOpen()) {
      this.load(this.ended);
    }
    const pinned = this.pinnedView();
    if (pinned !== null && pinned.kind !== 'loading') {
      this.loadPinned(pinned.id, false);
    }
  }

  private load(section: ListSection): void {
    const seq = ++section.seq;
    const channel = this.channel();
    const page = section.page();
    const current = () =>
      seq === section.seq && channel === this.channel() && page === section.page();

    this.wishService.list(channel, section.view, page, PAGE_SIZE).subscribe({
      next: (result) => {
        if (!current()) {
          return;
        }
        // The last page emptied under us (an action, another manager): go to the one that is left.
        if (result.items.length === 0 && page > 1 && result.totalPages < page) {
          section.page.set(Math.max(1, result.totalPages));
          this.load(section);
          return;
        }
        section.result.set(result);
        section.loadError.set(null);
      },
      error: (error: unknown) => {
        if (current()) {
          section.loadError.set(errorKey(error));
        }
      },
    });
  }

  private resolveTarget(
    id: number | null,
    loaded: ReadonlySet<number>,
    settled: boolean,
    pinned: PinnedState | null,
  ): void {
    if (id === null) {
      this.focusedTarget = null;
      if (pinned !== null) {
        this.pinnedSeq++;
        this.pinned.set(null);
      }
      return;
    }
    if (this.focusedTarget !== null && this.focusedTarget !== id) {
      this.focusedTarget = null;
    }
    if (loaded.has(id)) {
      // A pinned copy would go stale behind the list row; should the wish leave the loaded lists
      // again, it is fetched afresh.
      if (pinned !== null) {
        this.pinnedSeq++;
        this.pinned.set(null);
      }
      this.focusOnce(id);
      return;
    }
    if (!settled) {
      return;
    }
    if (pinned === null || pinned.id !== id) {
      this.loadPinned(id, true);
    } else if (pinned.kind === 'found') {
      this.focusOnce(id);
    }
  }

  /** `GET /{id}` for the jump target; `fresh` shows the skeleton, a reload keeps the row standing. */
  private loadPinned(id: number, fresh: boolean): void {
    const seq = ++this.pinnedSeq;
    const channel = this.channel();
    const current = () =>
      seq === this.pinnedSeq && channel === this.channel() && id === this.targetId();
    if (fresh) {
      this.pinned.set({ id, kind: 'loading' });
    }
    this.wishService.get(channel, id).subscribe({
      next: (wish) => {
        if (current()) {
          // The detail carries the usage of its window: the pinned row needs no second request.
          this.usageStore.seed(wish, wish.usage);
          this.pinned.set({ id, kind: 'found', wish });
        }
      },
      error: (error: unknown) => {
        if (!current()) {
          return;
        }
        this.pinned.set(
          error instanceof HttpErrorResponse && error.status === 404
            ? { id, kind: 'gone' }
            : { id, kind: 'error', key: errorKey(error) },
        );
      },
    });
  }

  private focusOnce(id: number): void {
    if (this.focusedTarget === id) {
      return;
    }
    this.focusedTarget = id;
    afterNextRender(
      () => {
        const row = this.host.nativeElement.querySelector<HTMLElement>(`[data-wish-id="${id}"]`);
        row?.focus();
      },
      { injector: this.injector },
    );
  }

  private runAction(sectionId: SectionId, wish: Wish, action: WishAction): void {
    const channel = this.channel();
    const actionError = this.actionErrorOf(sectionId);
    actionError.set(null);
    this.setPending(wish.id, true);
    this.command(action, channel, wish.id).subscribe({
      next: () => {
        this.setPending(wish.id, false);
        if (channel === this.channel()) {
          this.reloadAll();
        }
      },
      error: (error: unknown) => {
        this.setPending(wish.id, false);
        if (channel !== this.channel()) {
          return;
        }
        if (!(error instanceof HttpErrorResponse)) {
          actionError.set(GENERIC_ERROR_TRANSLATION_KEY);
          return;
        }
        actionError.set(wishErrorKey(error, 'action'));
        // The row showed a state the server no longer has: show the current one.
        const code = apiErrorCode(error);
        if (code === 'wish_state_conflict' || code === 'wish_not_found') {
          this.reloadAll();
        }
      },
    });
  }

  private command(action: WishAction, channel: string, id: number): Observable<Wish> {
    switch (action) {
      case 'extend':
        return this.wishService.extend(channel, id);
      case 'retry':
        return this.wishService.retry(channel, id);
      case 'keep':
        return this.wishService.keep(channel, id);
      case 'close':
        return this.wishService.close(channel, id);
      default:
        // Unreachable: onAction lets through only what OFFERED_ACTIONS names.
        throw new Error(`Wish action ${action} is not offered on this page.`);
    }
  }

  private actionErrorOf(sectionId: SectionId): WritableSignal<string | null> {
    switch (sectionId) {
      case 'open':
        return this.open.actionError;
      case 'running':
        return this.running.actionError;
      case 'ended':
        return this.ended.actionError;
      default:
        return this.pinnedActionError;
    }
  }

  private setPending(id: number, pending: boolean): void {
    this.pendingIds.update((ids) => {
      const next = new Set(ids);
      if (pending) {
        next.add(id);
      } else {
        next.delete(id);
      }
      return next;
    });
  }
}

function createSection(view: WishListView): ListSection {
  return {
    view,
    page: signal(1),
    result: signal<PagedResult<Wish> | null>(null),
    loadError: signal<string | null>(null),
    actionError: signal<string | null>(null),
    seq: 0,
  };
}

/** A positive whole number, or `null` for anything else (the parameter is user input). */
function parseWishId(raw: string | undefined): number | null {
  if (!raw || !/^\d+$/.test(raw)) {
    return null;
  }
  const id = Number(raw);
  return Number.isSafeInteger(id) && id > 0 ? id : null;
}

function errorKey(error: unknown): string {
  return error instanceof HttpErrorResponse
    ? apiErrorTranslationKey(error)
    : GENERIC_ERROR_TRANSLATION_KEY;
}
