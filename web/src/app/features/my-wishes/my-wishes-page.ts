import { HttpErrorResponse } from '@angular/common/http';
import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { EMPTY, interval, pairwise, filter, switchMap } from 'rxjs';

import { apiErrorCode, apiErrorTranslationKey } from '../../core/i18n/api-error';
import { pluralKey } from '../../core/i18n/plural';
import { pageVisible } from '../../core/page-visibility/page-visibility';
import type { WishOverviewItem } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { groupWishes, isProbation, remainingTime } from '../../core/wishes/wish-view';
import { BackLink } from '../../shared/ui/back-link';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StatusBadge, StatusBadgeTone } from '../../shared/ui/status-badge';
import { WishImage } from '../wishes/wish-image';

/** Same five-minute rhythm as the channel page's safety reload (plan 2.4); `my-wishes` has no stream. */
export const MY_WISHES_RELOAD_MS = 5 * 60_000;
const CLOCK_TICK_MS = 60_000;

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
const EXPLAINED_REASONS: ReadonlySet<string> = new Set(['expired', 'unconfirmed_start']);

interface TextPart {
  key: string;
  params?: Record<string, string | number>;
}

/**
 * All wishes that need a decision or are in their probation, across every channel the user manages
 * (#485, plan T11, AK42). Read-only: each row is one stretched link into the channel's wishes tab with
 * the `wish` jump target (plan 2.4), where the actions live. The grouping and the time rules are
 * `core/wishes/wish-view.ts`; nothing here decides them.
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
        class="relative flex items-start gap-3 px-3 py-3 transition-colors hover:bg-surface-inset"
      >
        <app-wish-image
          [imageUrl]="item.wish.imageUrl"
          [sevenTvEmoteId]="item.wish.sevenTvEmoteId"
          [knownStill]="false"
        />
        <div class="flex min-w-0 flex-1 flex-col gap-1">
          <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
            <a
              [routerLink]="['/channels', item.channelName, 'wishes']"
              [queryParams]="{ wish: item.wish.id }"
              class="app-card-link max-w-full truncate font-medium hover:underline"
              >{{ item.wish.alias || item.wish.emoteName }}</a
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
  private readonly visible = pageVisible();

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

  protected statusKey(item: WishOverviewItem): string {
    return KNOWN_STATUSES.has(item.wish.status)
      ? `wishes.status.${item.wish.status}`
      : 'wishes.status.unknown';
  }

  protected tone(item: WishOverviewItem): StatusBadgeTone {
    switch (item.wish.status) {
      case 'presented':
        return 'warning';
      case 'failed':
        return 'danger';
      default:
        return 'neutral';
    }
  }

  protected probation(item: WishOverviewItem): boolean {
    return isProbation(item.wish, this.now());
  }

  protected reasonKey(item: WishOverviewItem): string | null {
    const { status, statusReason } = item.wish;
    if ((status !== 'presented' && status !== 'failed') || statusReason === null) {
      return null;
    }
    return EXPLAINED_REASONS.has(statusReason)
      ? `wishes.reason.${statusReason}`
      : 'wishes.reason.other';
  }

  protected remaining(item: WishOverviewItem): TextPart | null {
    const wish = item.wish;
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
  }
}
