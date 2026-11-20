import { HttpErrorResponse } from '@angular/common/http';
import { NgTemplateOutlet } from '@angular/common';
import {
  afterRenderEffect,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  signal,
  untracked,
  viewChildren,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { EMPTY, interval, pairwise, filter, switchMap } from 'rxjs';

import { apiErrorCode, apiErrorTranslationKey } from '../../core/i18n/api-error';
import { pageVisible } from '../../core/page-visibility/page-visibility';
import { SevenTvEmoteLookup } from '../../core/seven-tv/seven-tv-emote-lookup';
import type { WishOverviewItem } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { WishEmotePreviewService } from '../../core/wishes/wish-emote-preview.service';
import {
  groupWishes,
  hasEmote,
  isProbation,
  needsEmotePreview,
  wishTitle,
} from '../../core/wishes/wish-view';
import { BackLink } from '../../shared/ui/back-link';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StatusBadge, StatusBadgeTone } from '../../shared/ui/status-badge';
import { WishImage } from '../wishes/wish-image';
import {
  TextPart,
  wishReasonKey,
  wishRemainingPart,
  wishStatusKey,
  wishStatusTone,
} from '../wishes/wish-labels';

/** Same five-minute rhythm as the channel page's safety reload (plan 2.4); `my-wishes` has no stream. */
export const MY_WISHES_RELOAD_MS = 5 * 60_000;
const CLOCK_TICK_MS = 60_000;

/**
 * All wishes that need a decision or are in their probation, across every channel the user manages
 * (#485, plan T11, AK42). Read-only: each row is one stretched link into the channel's wishes tab with
 * the `wish` jump target (plan 2.4), where the actions live. The grouping and the time rules are
 * `core/wishes/wish-view.ts`; nothing here decides them.
 *
 * **Picture and name of an open wish's emote** follow the channel page's work list (plan 2.10): a row
 * in view whose open wish knows its emote but lacks a stored picture or a name asks the session's
 * `WishEmotePreviewService` (one read at a time, once per emote and session, stopped by a throttle),
 * and asks again after every reload and when the window becomes visible again. The picture rule is
 * K3's: the plain rendition only once a read of this session proved the emote still.
 */
@Component({
  selector: 'app-my-wishes-page',
  imports: [
    BackLink,
    Button,
    EmptyState,
    NgTemplateOutlet,
    NoticeBanner,
    RouterLink,
    SkeletonRows,
    StatusBadge,
    TranslocoPipe,
    WishImage,
  ],
  template: `
    <div class="flex flex-col gap-6">
      <div class="flex flex-wrap items-center gap-x-4 gap-y-2">
        <app-back-link link="/" [label]="'nav.overview' | transloco" />
        <h2 tabindex="-1" class="scroll-mt-14 text-2xl font-bold tracking-tight">
          {{ 'myWishes.title' | transloco }}
        </h2>
      </div>

      @if (errorKey(); as key) {
        <app-notice-banner variant="error">
          <span class="flex flex-wrap items-center gap-x-3 gap-y-2">
            {{ key | transloco }}
            <button type="button" appButton="outline" (click)="reload()">
              {{ 'myWishes.retry' | transloco }}
            </button>
          </span>
        </app-notice-banner>
      }

      @if (items(); as list) {
        @if (list.length === 0) {
          <app-empty-state
            [title]="'myWishes.empty' | transloco"
            [description]="'myWishes.emptyHint' | transloco"
          />
        } @else {
          @if (groups().actionNeeded.length > 0) {
            <section aria-labelledby="my-wishes-action-title" class="flex flex-col gap-3">
              <h3 id="my-wishes-action-title" class="text-lg font-semibold">
                {{ 'myWishes.actionNeeded' | transloco }}
              </h3>
              <ul class="-mx-3 divide-y divide-border border-y border-border">
                @for (item of groups().actionNeeded; track trackKey(item)) {
                  <ng-container *ngTemplateOutlet="row; context: { $implicit: item }" />
                }
              </ul>
            </section>
          }
          @if (groups().probation.length > 0) {
            <section aria-labelledby="my-wishes-probation-title" class="flex flex-col gap-3">
              <h3 id="my-wishes-probation-title" class="text-lg font-semibold">
                {{ 'myWishes.probation' | transloco }}
              </h3>
              <ul class="-mx-3 divide-y divide-border border-y border-border">
                @for (item of groups().probation; track trackKey(item)) {
                  <ng-container *ngTemplateOutlet="row; context: { $implicit: item }" />
                }
              </ul>
            </section>
          }
        }
      } @else if (!errorKey()) {
        <app-skeleton-rows [count]="4" />
      }
    </div>

    <ng-template #row let-item>
      <li
        #wishRow
        class="relative flex items-start gap-3 px-3 py-3 transition-colors hover:bg-surface-inset"
        [attr.data-wish-key]="trackKey(item)"
      >
        @if (hasEmote(item)) {
          <app-wish-image
            [imageUrl]="item.wish.imageUrl"
            [sevenTvEmoteId]="item.wish.sevenTvEmoteId"
            [knownStill]="isKnownStill(item)"
          />
        } @else {
          <!-- An open wish without an emote has no picture yet; the empty plate keeps the column. -->
          <span class="flex size-10 shrink-0 bg-emote-canvas" aria-hidden="true"></span>
        }
        <div class="flex min-w-0 flex-1 flex-col gap-1">
          <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
            <a
              [routerLink]="['/channels', item.channelName, 'wishes']"
              [queryParams]="{ wish: item.wish.id }"
              class="app-card-link max-w-full truncate font-medium hover:underline"
              >{{ title(item) || ('wishes.pending.untitled' | transloco) }}</a
            >
            <app-status-badge [tone]="tone(item)">{{
              statusKey(item) | transloco
            }}</app-status-badge>
            @if (probation(item)) {
              <app-status-badge tone="accent">{{
                'wishes.row.probation' | transloco
              }}</app-status-badge>
            }
            @if (remaining(item); as part) {
              <span class="text-sm text-fg-secondary">{{ part.key | transloco: part.params }}</span>
            }
          </div>
          @if (reasonKey(item); as key) {
            <p class="text-sm text-fg-body">{{ key | transloco }}</p>
          }
          <p class="text-xs text-fg-muted">#{{ item.channelName }}</p>
        </div>
      </li>
    </ng-template>
  `,
})
export class MyWishesPage {
  private readonly wishService = inject(WishService);
  private readonly router = inject(Router);
  private readonly preview = inject(WishEmotePreviewService);
  private readonly lookup = inject(SevenTvEmoteLookup);
  private readonly visible = pageVisible();
  private readonly rowElements = viewChildren<ElementRef<HTMLElement>>('wishRow');
  /** Rows (`trackKey`) that last reported "in view"; asked for their preview again on a reload. */
  private readonly rowsInView = new Set<string>();

  protected readonly items = signal<WishOverviewItem[] | null>(null);
  protected readonly errorKey = signal<string | null>(null);
  private readonly now = signal(new Date());

  protected readonly groups = computed(() =>
    groupWishes(this.items() ?? [], (item) => item.wish, this.now()),
  );

  /** Only the latest request counts: an older answer must not overwrite a newer one. */
  private requestNumber = 0;

  constructor() {
    this.reload();

    // One observer for the rendered rows, made anew when they change; it reports every row once at
    // the start. Without IntersectionObserver no row reports and nothing is read.
    afterRenderEffect((onCleanup) => {
      const rows = this.rowElements();
      if (rows.length === 0 || typeof IntersectionObserver === 'undefined') {
        return;
      }
      const observer = new IntersectionObserver((hits) => {
        for (const hit of hits) {
          this.onRowSeen((hit.target as HTMLElement).dataset['wishKey'] ?? '', hit.isIntersecting);
        }
      });
      rows.forEach((row) => observer.observe(row.nativeElement));
      onCleanup(() => observer.disconnect());
    });

    // Every reload, and the window coming back, asks again for the rows in view (as on the channel
    // page, T19); `request` skips what is read or waiting. Nothing is asked while the window is hidden.
    effect(() => {
      const items = this.items() ?? [];
      if (this.visible()) {
        untracked(() => this.requestPreviewsInView(items));
      }
    });

    interval(CLOCK_TICK_MS)
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.now.set(new Date()));

    const visible$ = toObservable(this.visible);
    visible$
      .pipe(
        pairwise(),
        filter(([was, is]) => !was && is),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.reload());
    visible$
      .pipe(
        switchMap((isVisible) => (isVisible ? interval(MY_WISHES_RELOAD_MS) : EMPTY)),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.reload());

    inject(DestroyRef).onDestroy(() => (this.requestNumber = -1));
  }

  protected reload(): void {
    const mine = ++this.requestNumber;
    this.wishService.listMine().subscribe({
      next: (list) => {
        if (mine !== this.requestNumber) {
          return;
        }
        this.now.set(new Date());
        this.items.set(list);
        this.errorKey.set(null);
      },
      error: (error: unknown) => {
        if (mine !== this.requestNumber) {
          return;
        }
        if (error instanceof HttpErrorResponse && apiErrorCode(error) === 'wishes_disabled') {
          void this.router.navigateByUrl('/');
          return;
        }
        // The last list stays; 401 is the interceptor's business.
        this.errorKey.set(
          error instanceof HttpErrorResponse
            ? apiErrorTranslationKey(error)
            : 'myWishes.loadFailed',
        );
      },
    });
  }

  protected trackKey(item: WishOverviewItem): string {
    return `${item.channelName}#${item.wish.id}`;
  }

  protected hasEmote(item: WishOverviewItem): boolean {
    return hasEmote(item.wish);
  }

  /**
   * The alias, else the emote name, else (an open link wish) the preview's default name; an open wish
   * without an emote by what the viewer typed (K6).
   */
  protected title(item: WishOverviewItem): string {
    const wish = item.wish;
    return wishTitle(
      wish,
      wish.status === 'pending' && hasEmote(wish) ? this.preview.nameOf(wish.sevenTvEmoteId) : null,
    );
  }

  /** A read of this session proved the emote still: the picture may be its plain rendition (K3). */
  protected isKnownStill(item: WishOverviewItem): boolean {
    return hasEmote(item.wish) && this.lookup.isKnownStill(item.wish.sevenTvEmoteId);
  }

  protected statusKey(item: WishOverviewItem): string {
    return wishStatusKey(item.wish);
  }

  protected tone(item: WishOverviewItem): StatusBadgeTone {
    return wishStatusTone(item.wish);
  }

  protected probation(item: WishOverviewItem): boolean {
    return isProbation(item.wish, this.now());
  }

  protected reasonKey(item: WishOverviewItem): string | null {
    return wishReasonKey(item.wish);
  }

  protected remaining(item: WishOverviewItem): TextPart | null {
    return wishRemainingPart(item.wish, this.now());
  }

  private onRowSeen(key: string, visible: boolean): void {
    if (visible) {
      this.rowsInView.add(key);
    } else {
      this.rowsInView.delete(key);
    }
    const item = this.items()?.find((shown) => this.trackKey(shown) === key);
    if (item === undefined || !needsEmotePreview(item.wish)) {
      return;
    }
    if (visible) {
      this.preview.request(item.wish.sevenTvEmoteId);
    } else {
      this.preview.withdraw(item.wish.sevenTvEmoteId);
    }
  }

  /** The rows in view ask again, with the wish as the reload brought it; a row no longer listed is forgotten. */
  private requestPreviewsInView(items: readonly WishOverviewItem[]): void {
    const shown = new Map(items.map((item) => [this.trackKey(item), item]));
    for (const key of [...this.rowsInView]) {
      const item = shown.get(key);
      if (item === undefined) {
        this.rowsInView.delete(key);
      } else if (needsEmotePreview(item.wish)) {
        this.preview.request(item.wish.sevenTvEmoteId);
      }
    }
  }
}
