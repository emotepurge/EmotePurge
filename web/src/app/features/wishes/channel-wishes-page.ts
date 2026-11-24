import { Dialog, DialogRef } from '@angular/cdk/dialog';
import { NgTemplateOutlet } from '@angular/common';
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
  signal,
  untracked,
  viewChild,
  WritableSignal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { EMPTY, filter, interval, map, Observable, pairwise, switchMap } from 'rxjs';

import { normalizeChannelName } from '../../core/channels/channel-name';
import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import {
  apiErrorCode,
  apiErrorTranslationKey,
  GENERIC_ERROR_TRANSLATION_KEY,
} from '../../core/i18n/api-error';
import { channelLiveUrl, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { CHANNEL_RELOAD_DEBOUNCE_MS, liveReload, liveReopened } from '../../core/live/live-reload';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { DockClearanceService } from '../../core/layout/dock-clearance.service';
import { PagedResult } from '../../core/models/paged-result.model';
import { pageVisible } from '../../core/page-visibility/page-visibility';
import { PointerModeService } from '../../core/pointer/pointer-mode.service';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvEmoteLookup } from '../../core/seven-tv/seven-tv-emote-lookup';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import {
  SEVEN_TV_RUN_KIND_LABEL_KEY,
  SevenTvRunArbiter,
} from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { WishAddService, WishAddState } from '../../core/wishes/wish-add.service';
import { WishEmotePreviewService } from '../../core/wishes/wish-emote-preview.service';
import { wishRemovalBlockKey } from '../../core/wishes/wish-removal';
import { WishRemovalTracker } from '../../core/wishes/wish-removal-tracker';
import { wishErrorKey } from '../../core/wishes/wish-error';
import type { Wish, WishListView } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { WishSummaryService } from '../../core/wishes/wish-summary.service';
import {
  hasEmote,
  isConfirmedAction,
  isSevenTvAction,
  needsEmotePreview,
  WishAction,
  wishTitle,
} from '../../core/wishes/wish-view';
import { Pager } from '../../shared/pagination/pager';
import { DeleteAbortNotice } from '../../shared/seven-tv/delete-flow';
import { DeleteProgressSection } from '../../shared/seven-tv/delete-progress-section';
import { DockOutcomeAnnouncer } from '../../shared/seven-tv/dock-outcome-announcer';
import { RestoreProgressSection } from '../../shared/seven-tv/restore-progress-section';
import { openSevenTvTokenPromptDialog } from '../../shared/seven-tv/seven-tv-token-prompt-dialog';
import { Button } from '../../shared/ui/button';
import { openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import {
  openWishAdoptDialog,
  WishAdoptDialogData,
  WishAdoptDialogResult,
} from './wish-adopt-dialog';
import { openWishApproveDialog, WishApproveDialogResult } from './wish-approve-dialog';
import { openWishEditDialog } from './wish-edit-dialog';
import { openWishEntryDialog, WishEntryDialogResult } from './wish-entry-dialog';
import {
  wishAddOperationView,
  wishRemovalOperationView,
  WishOperationView,
} from './wish-operation';
import { openWishReAddDialog } from './wish-readd-dialog';
import { openWishRemovalConfirmDialog } from './wish-removal-confirm-dialog';
import { startWishRemovalFlow, WishRemovalFlowDeps } from './wish-removal-flow';
import { WishRow } from './wish-row';
import { WishUsageStore } from './wish-usage';

const PAGE_SIZE = 20;

/** Safety net (plan 2.4 e): a missed event is caught up at the latest after this long. */
export const WISH_SAFETY_RELOAD_MS = 5 * 60_000;

/** The "now" every row reads moves on once a minute (plan 2.2; no clock in a template). */
const CLOCK_TICK_MS = 60_000;

/**
 * The actions this page carries out — every one of the action table. `adopt`, `edit`, `reAdd` and
 * `approve` open a dialog instead of sending a command (T6, T7, K6); `add7tv` starts the wish add (T8),
 * `remove` the removal (T9). `reject` and `unlink` (K6) are confirmed commands; none of the K6 actions
 * writes to 7TV. `delete` (K7a) is confirmed too and removes an ended wish for good, nothing in 7TV.
 */
const OFFERED_ACTIONS: ReadonlySet<WishAction> = new Set<WishAction>([
  'add7tv',
  'remove',
  'extend',
  'retry',
  'keep',
  'close',
  'adopt',
  'edit',
  'reAdd',
  'approve',
  'reject',
  'unlink',
  'delete',
]);

/** An approve the server booked as an extension of the running wish, said once at the page top. */
interface MergedNotice {
  runningWishId: number | null;
  /** The running wish's new expiry; `null` when the answer did not carry one. */
  expiresAtUtc: string | null;
}

/**
 * What the page says after its own delete (K7a, plan 2.6). `kept`: a kept wish with an entry was
 * deleted, so the entry is free again and "Bestand übernehmen" opens the take-over for its whole emote
 * with that entry preselected. `done`: the pinned row was deleted without such a way back.
 */
type DeletedNotice = { kind: 'kept'; sevenTvEmoteId: string; slotId: string } | { kind: 'done' };

/**
 * The notice a delete leaves, from the wish as the confirmation showed it and where it stood: a kept
 * wish with an entry always gets the way back, the pinned row at least "Wunsch gelöscht" (its row is
 * gone and the focus needs a place), a row of a list nothing (the next row takes the focus).
 */
export function deletedNoticeFor(wish: Wish, pinned: boolean): DeletedNotice | null {
  if (wish.status === 'kept' && wish.slotId !== null && hasEmote(wish)) {
    return { kind: 'kept', sevenTvEmoteId: wish.sevenTvEmoteId, slotId: wish.slotId };
  }
  return pinned ? { kind: 'done' } : null;
}

/**
 * The confirmation text of a delete (PR1-6): only a kept wish that still names its entry promises the
 * way back through "Bestand übernehmen"; a kept one the reconcile released, and every other ended
 * state, get the general text.
 */
export function deleteConfirmKey(wish: Wish): string {
  return wish.status === 'kept' && wish.slotId !== null
    ? 'wishes.confirm.deleteKept'
    : 'wishes.confirm.delete';
}

/**
 * Where the focus goes once a row of a list is deleted (plan 2.6): the row after it, else the one
 * before, else `null` for the section heading. `ids` is the list as shown before the delete;
 * `stillShown` says which rows are still on screen now.
 */
export function focusAfterDelete(
  ids: readonly number[],
  deletedId: number,
  stillShown: (id: number) => boolean,
): number | null {
  const index = ids.indexOf(deletedId);
  if (index < 0) {
    return null;
  }
  const candidates = [...ids.slice(index + 1), ...ids.slice(0, index).reverse()];
  return candidates.find((id) => id !== deletedId && stillShown(id)) ?? null;
}

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
    DeleteProgressSection,
    DockOutcomeAnnouncer,
    EmptyState,
    NgTemplateOutlet,
    NoticeBanner,
    Pager,
    RestoreProgressSection,
    RouterLink,
    SkeletonRows,
    TranslocoPipe,
    WishRow,
  ],
  template: `
    <!-- The bottom padding follows the run dock's measured height, so the last row is never under
         the fixed bar; nothing while there is no dock. -->
    <div
      class="flex flex-col gap-8"
      [style.paddingBottom.px]="dockHeightPx() > 0 ? dockHeightPx() : null"
    >
      <!-- The two side ways of filing a wish (plan T6). Both only book in EmotePurge, so they stand
           on a coarse pointer too. -->
      <div class="flex flex-col gap-3">
        <div class="flex flex-wrap gap-2">
          <button type="button" appButton="primary" (click)="openEntry()">
            {{ 'wishes.page.header.entry' | transloco }}
          </button>
          <button type="button" appButton="neutral" data-header-adopt (click)="openAdopt({})">
            {{ 'wishes.page.header.adopt' | transloco }}
          </button>
        </div>
        <!-- Quiet pointer to where the wish settings live. Always mounted, no async state: the page
             is only reachable for managers with the wish switch on, which is exactly who the
             settings tab lets in (channelSettingsGuard), so the link never leads nowhere. -->
        <!-- Underline in the text colour (a border-strong underline fails 3:1). The ::after widens
             the hit area to 24px height without moving the line (§10 touch targets). -->
        <p class="text-sm text-fg-muted">
          {{ 'wishes.page.settingsHint.text' | transloco }}
          <a
            [routerLink]="['/channels', channel(), 'settings']"
            class="relative text-fg-secondary underline underline-offset-2 after:absolute after:-inset-y-1 after:inset-x-0 after:content-[''] hover:text-fg"
            >{{ 'wishes.page.settingsHint.link' | transloco }}</a
          >.
        </p>
        <!-- The set-full hint arrives after the dialog closed: the permanently mounted region speaks
             it (§4.4), the visible banner is aria-hidden so it is not read twice. -->
        <p role="status" class="sr-only">
          @if (setFullNotice()) {
            {{ 'wishes.page.setFull' | transloco }}
          }
        </p>
        @if (setFullNotice()) {
          <app-notice-banner variant="warning" aria-hidden="true">{{
            'wishes.page.setFull' | transloco
          }}</app-notice-banner>
        }
        <!-- A removal whose booking was refused: said here as well as at its row, which the reload
             may move out of sight (plan 2.6). Same pair of regions as the set-full hint. -->
        <p role="status" class="sr-only">
          @if (removalNotice(); as key) {
            {{ key | transloco }}
          }
        </p>
        @if (removalNotice(); as key) {
          <app-notice-banner variant="info" aria-hidden="true">{{
            key | transloco
          }}</app-notice-banner>
        }
        <!-- An approve the server booked as an extension of the running wish (plan 2.6 (1)): said
             after the dialog closed, with the way to that wish. Same pair of regions; the visible
             text is aria-hidden, its button is not. -->
        <p role="status" class="sr-only">
          @if (mergedNotice(); as notice) {
            <ng-container
              [ngTemplateOutlet]="mergedText"
              [ngTemplateOutletContext]="{ $implicit: notice }"
            />
          }
        </p>
        @if (mergedNotice(); as notice) {
          <app-notice-banner variant="info">
            <span aria-hidden="true">
              <ng-container
                [ngTemplateOutlet]="mergedText"
                [ngTemplateOutletContext]="{ $implicit: notice }"
              />
            </span>
            @if (notice.runningWishId !== null) {
              <button
                notice-action
                type="button"
                appButton="outline"
                (click)="goToWish(notice.runningWishId)"
              >
                {{ 'wishes.dialog.toWish' | transloco }}
              </button>
            }
          </app-notice-banner>
        }
        <!-- What the page's own delete left (K7a, plan 2.6). When the focus moves onto the notice (the
             pinned row went) the focus says it, through the text or as the button's description;
             otherwise the mounted region does (§4.4). Never both, so it is not read twice. -->
        <p role="status" class="sr-only">
          @if (deletedNotice(); as notice) {
            @if (!deletedNoticeFocused()) {
              {{ deletedTextKey(notice) | transloco }}
            }
          }
        </p>
        @if (deletedNotice(); as notice) {
          <app-notice-banner variant="info">
            <!-- Text and buttons stacked in the content slot, not in [notice-action]: two buttons
                 there squeeze the text into a narrow column on a phone (the #217 pattern of
                 import-target-dialog.ts). The text is read through the focus when it moved here,
                 else hidden: the region above speaks it. -->
            <div class="flex flex-col gap-3">
              <p
                id="wishes-deleted-text"
                data-deleted-notice
                [attr.tabindex]="deletedNoticeFocused() ? -1 : null"
                [attr.aria-hidden]="deletedNoticeFocused() ? null : 'true'"
              >
                {{ deletedTextKey(notice) | transloco }}
              </p>
              <div class="flex flex-wrap gap-2">
                @if (notice.kind === 'kept') {
                  <button
                    type="button"
                    appButton="outline"
                    data-deleted-adopt
                    aria-describedby="wishes-deleted-text"
                    (click)="adoptFreedEntry(notice)"
                  >
                    {{ 'wishes.page.header.adopt' | transloco }}
                  </button>
                }
                <button type="button" appButton="outline" (click)="dismissDeletedNotice()">
                  {{ 'wishes.deleted.dismiss' | transloco }}
                </button>
              </div>
            </div>
          </app-notice-banner>
        }
      </div>

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
        @if (dockNotice(); as notice) {
          {{ notice.leadKey | transloco }}
          {{ notice.reasonKey | transloco: notice.reasonParams ?? {} }}
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
      <!-- What stopped the dock's "Wiederherstellen" entry; stays until a new run starts. -->
      @if (dockNotice(); as notice) {
        <p aria-hidden="true" class="text-sm text-fg-secondary">
          {{ notice.leadKey | transloco }}
          {{ notice.reasonKey | transloco: notice.reasonParams ?? {} }}
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

    <!-- Permanently mounted (§4.5): speaks the end states of what the dock shows. -->
    <app-dock-outcome-announcer />

    <!-- The run dock (plan 2.6, the tags page's pattern): a removal is a delete run, its
         "Wiederherstellen" a restore run. Only on a fine pointer, like every 7TV write surface. -->
    @if (dockVisible() && !isCoarse()) {
      <div class="app-dock" #dock>
        <div class="mx-auto flex max-h-[70vh] max-w-7xl flex-col gap-2 overflow-y-auto px-4 py-3">
          <app-delete-progress-section
            [hostSelectedSetId]="dockSetId()"
            (notice)="dockNotice.set($event)"
          />
          <app-restore-progress-section />
        </div>
      </div>
    }

    <ng-template #mergedText let-notice>
      @if (notice.expiresAtUtc) {
        {{ 'wishes.approve.merged' | transloco: { date: formatDateTime(notice.expiresAtUtc) } }}
      } @else {
        {{ 'wishes.approve.mergedNoDate' | transloco }}
      }
    </ng-template>

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
          <button
            notice-action
            type="button"
            appButton="outline"
            [attr.data-retry-view]="section.view"
            (click)="retryLoad(section)"
          >
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
          @if (section.view === 'open' && openGroups(); as groups) {
            <!-- Open wishes captured in chat come first (the server orders them so, plan 2.6) and
                 form their own named group; what else needs a decision follows as a second one. -->
            <div role="group" aria-labelledby="wishes-pending-title" class="flex flex-col gap-2">
              <h3 id="wishes-pending-title" class="text-base font-semibold">
                {{ 'wishes.pending.title' | transloco }}
              </h3>
              <ul class="-mx-3 divide-y divide-border border-y border-border">
                @for (wish of groups.pending; track wish.id) {
                  <!-- The group heading says "Offen" already: the rows leave out the same pill. -->
                  <ng-container
                    [ngTemplateOutlet]="row"
                    [ngTemplateOutletContext]="{
                      $implicit: wish,
                      section: section.view,
                      statusImplied: true,
                    }"
                  />
                }
              </ul>
            </div>
            @if (groups.rest.length > 0) {
              <div role="group" aria-labelledby="wishes-decide-title" class="flex flex-col gap-2">
                <h3 id="wishes-decide-title" class="text-base font-semibold">
                  {{ 'wishes.pending.others' | transloco }}
                </h3>
                <ul class="-mx-3 divide-y divide-border border-y border-border">
                  @for (wish of groups.rest; track wish.id) {
                    <ng-container
                      [ngTemplateOutlet]="row"
                      [ngTemplateOutletContext]="{ $implicit: wish, section: section.view }"
                    />
                  }
                </ul>
              </div>
            }
          } @else {
            <ul class="-mx-3 divide-y divide-border border-y border-border">
              @for (wish of result.items; track wish.id) {
                <ng-container
                  [ngTemplateOutlet]="row"
                  [ngTemplateOutletContext]="{ $implicit: wish, section: section.view }"
                />
              }
            </ul>
          }
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

    <ng-template #row let-wish let-section="section" let-statusImplied="statusImplied">
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
          [knownStill]="isKnownStill(wish.sevenTvEmoteId)"
          [previewName]="previewNameOf(wish)"
          [statusImplied]="statusImplied === true"
          [usage]="usageStore.blockFor(wish)"
          [trackUsage]="section !== 'ended'"
          [operation]="operationOf(wish)"
          [removeBlockKey]="removeBlockOf(wish)"
          (seen)="onRowSeen(wish, $event)"
          (action)="onAction(section, wish, $event)"
          (retryOperation)="retryRemovalReport(wish)"
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
  private readonly wishSummary = inject(WishSummaryService);
  private readonly dialog = inject(Dialog);
  private readonly transloco = inject(TranslocoService);
  private readonly arbiter = inject(SevenTvRunArbiter);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly lookup = inject(SevenTvEmoteLookup);
  private readonly wishAdd = inject(WishAddService);
  private readonly removals = inject(WishRemovalTracker);
  private readonly deleteService = inject(SevenTvDeleteService);
  private readonly restoreService = inject(SevenTvRestoreService);
  private readonly dockClearance = inject(DockClearanceService);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly preview = inject(WishEmotePreviewService);
  private readonly languageService = inject(LanguageService);
  private readonly removalDeps: WishRemovalFlowDeps = {
    emoteSetService: inject(SevenTvEmoteSetService),
    emoteAdminService: this.emoteAdminService,
    wishService: this.wishService,
    tokenService: inject(SevenTvTokenService),
    arbiter: this.arbiter,
    deleteService: this.deleteService,
    openConfirmation: (data) =>
      openWishRemovalConfirmDialog(this.dialog, data).closed.pipe(map((done) => done === true)),
  };

  /** The rendered `.app-dock`, absent whenever the template's gate does not mount it. */
  private readonly dockRef = viewChild<ElementRef<HTMLElement>>('dock');

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

  /** A wish filed by hand landed in a full set (`setFull`); stays until the next filing. */
  protected readonly setFullNotice = signal(false);

  /** A removal of this channel left 7TV but was not booked (`notBooked`, plan 2.6), as the key of what
   *  to say (moved on, or deleted meanwhile, K7a); stays until the next removal starts or the channel
   *  changes. */
  protected readonly removalNotice = signal<string | null>(null);

  /** What the page's own last delete left (K7a); goes with the channel, its dialog or the next delete. */
  protected readonly deletedNotice = signal<DeletedNotice | null>(null);
  /** The focus was moved onto the deleted notice, which then speaks through the focus alone. */
  protected readonly deletedNoticeFocused = signal(false);

  /** The last approve was booked as an extension of the running wish; stays until the next approve. */
  protected readonly mergedNotice = signal<MergedNotice | null>(null);

  /**
   * The open wishes of the "needs action" page and the rest, in the server's order; `null` while the
   * page holds no open wish, so the list stays the single one of K3.
   */
  protected readonly openGroups = computed(() => {
    const items = this.open.result()?.items ?? [];
    const pending = items.filter((wish) => wish.status === 'pending');
    return pending.length === 0
      ? null
      : { pending, rest: items.filter((wish) => wish.status !== 'pending') };
  });

  /** Wishes whose action request is on the way; their row's buttons wait. */
  protected readonly pendingIds = signal<ReadonlySet<number>>(new Set());

  /** Open rows that last reported "in view" (wish ids), asked for their preview again (plan 2.10). */
  private readonly openRowsInView = new Set<number>();

  /** The target already focused once; a reload must not pull the focus back. */
  private focusedTarget: number | null = null;

  /**
   * The jump target whose wish this page deleted: it is not fetched again (it would only say "no
   * longer there") while the query parameter is being cleared.
   */
  private clearedTarget: number | null = null;

  /** The open approve dialog; it shows the redeemer's text and login, so a 403 reload closes it. */
  private approveDialog: DialogRef<WishApproveDialogResult | undefined> | null = null;

  /** Set once the page is gone; a 7TV start that is still deciding then aborts (plan 2.5, 3b i). */
  private destroyed = false;

  private readonly targetId = computed(() => parseWishId(this.wish()));

  /** The two lists loaded on entry have answered (with rows or an error). */
  private readonly listsSettled = computed(() =>
    this.alwaysLoaded.every((section) => section.result() !== null || section.loadError() !== null),
  );

  private readonly loadedIds = computed(() => {
    const ids = new Set<number>();
    const sections = this.endedOpen() ? [this.open, this.running, this.ended] : this.alwaysLoaded;
    for (const section of sections) {
      section.result()?.items.forEach((wish) => ids.add(wish.id));
    }
    return ids;
  });

  /** Every wish on screen: the loaded lists and the pinned row. */
  private readonly shownWishes = computed<Wish[]>(() => [
    ...this.usageRows(),
    ...(this.endedOpen() ? (this.ended.result()?.items ?? []) : []),
  ]);

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

  /** The dock shows a delete or restore run of this app, or a restore's refusal notice. */
  protected readonly dockVisible = computed(
    () =>
      this.deleteService.isRunning() ||
      this.deleteService.queue().length > 0 ||
      this.restoreService.isRunning() ||
      this.restoreService.queue().length > 0 ||
      this.restoreService.duplicateNoticePending(),
  );

  /**
   * The set this page stands for in the dock's restore check (restore spec E21): the active set of
   * the channel on screen, which is what the page lists — `null` while it is unknown. Never the shown
   * run's own set: a run that started on another channel's page would otherwise compare its set with
   * itself, and the restore would lose its "not the set on screen" warning.
   */
  protected readonly dockSetId = computed(() => {
    const known = this.activeSet();
    return known !== null && known.channel === this.channel() ? known.setId : null;
  });

  /** The channel's active set as the last sync stored it (`…/emotes/active-set`), per channel. */
  private readonly activeSet = signal<{ channel: string; setId: string | null } | null>(null);
  private activeSetSeq = 0;
  /** A read of the active set is on its way; a list reload does not start a second one. */
  private activeSetPending = false;

  /** The dock's rendered height — the page's bottom padding and the shell's clearance. */
  protected readonly dockHeightPx = signal(0);

  /** What stopped the dock's restore entry (its `notice` output), until a new run starts. */
  protected readonly dockNotice = signal<DeleteAbortNotice | null>(null);

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
      const settled = this.listsSettled();
      const pinned = this.pinned();
      untracked(() => this.resolveTarget(id, loaded, settled, pinned));
    });

    // A row that scrolled into view gets its usage here, and a state change reaches it as a new key.
    effect(() => {
      const rows = this.usageRows();
      untracked(() => this.usageStore.sync(rows));
    });

    // Every list reload, and the window coming back, asks the preview again for the open rows that
    // reported "in view" (T19): a read that did not start when the row came into view (its wish then
    // needed none, or it was dropped) or that failed once gets its chance without the row having to
    // leave and re-enter the view. `request` skips what is read or waiting, so this costs no second
    // read. Nothing is asked while the window is hidden; a row that is gone is released anyway.
    effect(() => {
      const rows = this.usageRows();
      const ask = this.visible();
      untracked(() => this.requestPreviewsInView(rows, ask));
    });

    // An add result says what it had to once its wish left `approved`; drop it then (plan 2.5a).
    // A removal line likewise once its wish is neither running nor removed any more (plan 2.6).
    effect(() => {
      const channel = this.channel();
      const shown = this.shownWishes();
      untracked(() => {
        this.wishAdd.prune(channel, shown);
        this.removals.prune(channel, shown);
      });
    });

    // The dock's real height, mirrored into the page padding and the shell's clearance (the tags
    // page's measurement: a run with many rows grows the dock up to 70vh).
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

    // A run starting makes an old restore refusal stale.
    effect(() => {
      if (this.arbiter.activeRun() !== null) {
        untracked(() => this.dockNotice.set(null));
      }
    });

    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      // No row of this page waits any more (the session service outlives it, plan 2.10).
      this.openRowsInView.forEach((id) => this.preview.release(previewConsumer(id)));
      // Give the clearance back at once — the next page may have no dock to release it.
      this.dockClearance.release();
    });

    // A removal of this channel that ended: show the server's state (plan 2.5, 2.6) — after a
    // stop "nothing was changed", after a booking or a refused booking the wish moved. A refused
    // booking (`notBooked`) is said at the page as well: the reload may move the wish into a list
    // that is not shown (a collapsed "Beendet"), and its row with it.
    this.removals.ended.pipe(takeUntilDestroyed()).subscribe((end) => {
      if (end.channelName === this.channel()) {
        if (end.state.kind === 'notBooked') {
          this.removalNotice.set(
            end.state.deleted ? 'wishes.remove.notBookedDeleted' : 'wishes.remove.notBooked',
          );
        }
        this.reloadAfterChange();
      }
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
    }).subscribe(() => this.reloadAfterChange());

    // A set switch in 7TV reaches the stored active set with a sync: the dock's set follows it.
    liveReload(this.liveUrl, {
      accept: [LIVE_EVENT_TYPES.channelSynced],
      debounceMs: CHANNEL_RELOAD_DEBOUNCE_MS,
    }).subscribe(() => this.loadActiveSet());

    // Reopened stream and a window that is visible again: counts may have moved unseen, so every
    // held usage value runs out (plan 2.4 c, d) before the lists reload. A reopened stream may also
    // have swallowed a `wish.changed`, so the header count asks again; a visible window it does not
    // need to, the count asks by itself then.
    liveReopened(this.liveUrl).subscribe(() => {
      this.reloadAfterGap();
      this.wishSummary.refresh();
    });

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
    if (action === 'adopt') {
      // An open wish without an emote lists every entry: the manager picks the one it means.
      this.openAdopt(hasEmote(wish) ? { wish, sevenTvEmoteId: wish.sevenTvEmoteId } : { wish });
      return;
    }
    if (action === 'approve') {
      this.openApprove(wish, sectionId);
      return;
    }
    if (action === 'add7tv') {
      this.startAdd(sectionId, wish);
      return;
    }
    if (action === 'remove') {
      this.startRemove(sectionId, wish);
      return;
    }
    if (action === 'edit') {
      this.openEdit(wish, sectionId);
      return;
    }
    if (action === 'reAdd') {
      this.openReAdd(wish, sectionId);
      return;
    }
    if (action === 'delete') {
      this.confirmDelete(sectionId, wish);
      return;
    }
    if (!isConfirmedAction(action)) {
      this.runAction(sectionId, wish, action);
      return;
    }
    const name =
      wishTitle(wish, this.previewNameOf(wish)) ||
      this.transloco.translate('wishes.pending.untitled');
    openConfirmDialog(this.dialog, {
      message: this.transloco.translate(`wishes.confirm.${action}`, { name }),
      confirmLabel: this.transloco.translate(`wishes.actions.${action}`),
    }).closed.subscribe((confirmed) => {
      if (confirmed) {
        this.runAction(sectionId, wish, action);
      }
    });
  }

  /** The row's own 7TV operation — the removal (T9) or the add (T8), from the root services that
   *  outlive this page. A wish has at most one of them: the add only while `approved`. */
  protected operationOf(wish: Wish): WishOperationView | null {
    return (
      wishRemovalOperationView(this.removals.stateFor(this.channel(), wish)) ??
      wishAddOperationView(this.wishAdd.stateFor(this.channel(), wish))
    );
  }

  /** Why "Entfernen" is locked on this row: a removal of it whose booking is out or owed, or a
   *  generation the removal cannot be bound to (plan 2.6). A removal under way holds the arbiter. */
  protected removeBlockOf(wish: Wish): string | null {
    return this.removals.owesReport(this.channel(), wish.id)
      ? 'wishes.remove.blocked.pending'
      : wishRemovalBlockKey(wish);
  }

  /** "Erneut melden" of an owed removal report: the identical body again. */
  protected retryRemovalReport(wish: Wish): void {
    this.removals.retry(this.channel(), wish.id);
  }

  protected isKnownStill(sevenTvEmoteId: string): boolean {
    return hasEmote({ sevenTvEmoteId }) && this.lookup.isKnownStill(sevenTvEmoteId);
  }

  /** 7TV's default name of an open wish's emote, from this session's preview read; else `null`. */
  protected previewNameOf(wish: Wish): string | null {
    return wish.status === 'pending' && hasEmote(wish)
      ? this.preview.nameOf(wish.sevenTvEmoteId)
      : null;
  }

  /**
   * A row came into view or left it. Usage is for wishes with a window (not open ones); the preview
   * read (plan 2.10) only for an open wish that knows its emote but lacks a name or a stored picture.
   */
  protected onRowSeen(wish: Wish, visible: boolean): void {
    if (wish.status !== 'pending') {
      this.usageStore.setVisible(wish, visible);
      return;
    }
    if (visible) {
      this.openRowsInView.add(wish.id);
    } else {
      this.openRowsInView.delete(wish.id);
      // Released whatever the wish needs now: a reload may have brought the picture since it asked.
      this.preview.release(previewConsumer(wish.id));
      return;
    }
    if (needsEmotePreview(wish)) {
      this.preview.request(wish.sevenTvEmoteId, previewConsumer(wish.id));
    }
  }

  protected deletedTextKey(notice: DeletedNotice): string {
    return notice.kind === 'kept' ? 'wishes.deleted.kept' : 'wishes.deleted.done';
  }

  /**
   * "Bestand übernehmen" of the deleted notice (B3): the take-over for the whole emote, all its entries
   * and aliases, with the freed entry preselected while it is still pickable. No wish: a new one.
   */
  protected adoptFreedEntry(notice: DeletedNotice): void {
    if (notice.kind !== 'kept') {
      return;
    }
    this.clearDeletedNotice();
    this.openAdopt(
      { sevenTvEmoteId: notice.sevenTvEmoteId, preselectSlotId: notice.slotId },
      { refocusWhenLost: true },
    );
  }

  protected dismissDeletedNotice(): void {
    this.clearDeletedNotice();
    this.focusHeaderAdoptWhenLost();
  }

  /** "Zum Wunsch" of the extension notice: the jump target of plan 2.4. */
  protected goToWish(id: number): void {
    this.jumpTo(id);
  }

  protected formatDateTime(iso: string): string {
    return new Date(iso).toLocaleString(toLocale(this.languageService.lang()), {
      dateStyle: 'medium',
      timeStyle: 'short',
    });
  }

  protected openEntry(): void {
    const channel = this.channel();
    this.setFullNotice.set(false);
    openWishEntryDialog(this.dialog, { channelName: channel }).closed.subscribe((result) => {
      if (channel === this.channel()) {
        this.onEntryClosed(result);
      }
    });
  }

  /**
   * The take-over dialog: from the header (all entries), a row (that wish), the 409 of the entry or
   * the deleted notice. `refocusWhenLost`: its trigger is gone once it closes (the notice), so the
   * focus goes to the header's "Bestand übernehmen" unless it landed elsewhere.
   */
  protected openAdopt(
    options: Omit<WishAdoptDialogData, 'channelName'>,
    behaviour: { refocusWhenLost?: boolean } = {},
  ): void {
    const channel = this.channel();
    openWishAdoptDialog(this.dialog, { channelName: channel, ...options }).closed.subscribe(
      (result) => {
        if (channel !== this.channel()) {
          return;
        }
        this.onAdoptClosed(result);
        if (behaviour.refocusWhenLost && result?.kind !== 'jump') {
          this.focusHeaderAdoptWhenLost();
        }
      },
    );
  }

  /** "Bearbeiten": from a row, or from the entry dialog when the running wish is `approved`/`failed`. */
  protected openEdit(wish: Wish, sectionId: SectionId = 'open'): void {
    const channel = this.channel();
    openWishEditDialog(this.dialog, { channelName: channel, wish }).closed.subscribe((result) => {
      if (channel !== this.channel()) {
        return;
      }
      if (result?.kind === 'edited') {
        this.reloadAfterChange();
      } else if (result?.kind === 'stale') {
        this.onStale(sectionId, result.errorKey);
      }
    });
  }

  /** "Freigeben" (K6, plan 2.10): the dialog decides with the server; the page shows what came of it. */
  protected openApprove(wish: Wish, sectionId: SectionId = 'open'): void {
    const channel = this.channel();
    this.mergedNotice.set(null);
    const ref = openWishApproveDialog(this.dialog, { channelName: channel, wish });
    this.approveDialog = ref;
    ref.closed.subscribe((result) => {
      if (this.approveDialog === ref) {
        this.approveDialog = null;
      }
      if (channel === this.channel()) {
        this.onApproveClosed(wish, sectionId, result);
      }
    });
  }

  protected openReAdd(wish: Wish, sectionId: SectionId = 'open'): void {
    const channel = this.channel();
    openWishReAddDialog(this.dialog, { channelName: channel, wish }).closed.subscribe((result) => {
      if (channel !== this.channel()) {
        return;
      }
      if (result?.kind === 'readded') {
        this.reloadAfterChange();
      } else if (result?.kind === 'jump') {
        this.jumpTo(result.wishId);
      } else if (result?.kind === 'stale') {
        this.onStale(sectionId, result.errorKey);
      }
    });
  }

  /**
   * "In 7TV hinzufügen" (plan 2.5a): the start contract runs in `WishAddService`; this page hands it
   * the token prompt and tells it whether it still lives and which channel it shows (3b i). An
   * abort or a skipped send says "nothing was changed" at the row and reloads the lists (plan 2.5);
   * the outcome itself stays at the row, also after leaving and coming back.
   */
  private startAdd(sectionId: SectionId, wish: Wish): void {
    const channel = this.channel();
    this.actionErrorOf(sectionId).set(null);
    this.wishAdd.start({
      channelName: channel,
      wish,
      caller: { alive: () => !this.destroyed, channelName: () => this.channel() },
      promptToken: () =>
        openSevenTvTokenPromptDialog(this.dialog).closed.pipe(map((done) => done === true)),
      onEnd: (state: WishAddState) => {
        const stopped = state.kind === 'notStarted' || state.kind === 'skipped';
        if (stopped && !this.destroyed && channel === this.channel()) {
          this.reloadAll();
        }
      },
    });
  }

  /**
   * "Entfernen" (plan 2.6): the flow runs the start contract with its confirmation and starts the
   * delete run with the frozen wish's entry; the tracker keeps the outcome and books it, also after
   * leaving the page. The page hands over the token prompt and tells whether it still lives and which
   * channel it shows (3b i).
   */
  private startRemove(sectionId: SectionId, wish: Wish): void {
    const channel = this.channel();
    if (this.removeBlockOf(wish) !== null) {
      return;
    }
    this.actionErrorOf(sectionId).set(null);
    this.removalNotice.set(null);
    startWishRemovalFlow(this.removalDeps, {
      channelName: channel,
      wish,
      caller: { alive: () => !this.destroyed, channelName: () => this.channel() },
      promptToken: () =>
        openSevenTvTokenPromptDialog(this.dialog).closed.pipe(map((done) => done === true)),
      progress: this.removals.progressFor(channel, wish.id),
    });
  }

  /**
   * "Löschen" (K7a, plan 2.6): the confirmation, then `DELETE` with the end of the row it showed —
   * captured when it opens, the DTO string unchanged, even when a reload replaced the row meanwhile.
   */
  private confirmDelete(sectionId: SectionId, wish: Wish): void {
    const channel = this.channel();
    const name =
      wishTitle(wish, this.previewNameOf(wish)) ||
      this.transloco.translate('wishes.pending.untitled');
    openConfirmDialog(this.dialog, {
      message: this.transloco.translate(deleteConfirmKey(wish), { name }),
      confirmLabel: this.transloco.translate('wishes.actions.delete'),
    }).closed.subscribe((confirmed) => {
      // A channel switch while it was open: the row is no longer this page's.
      if (confirmed && channel === this.channel() && !this.pendingIds().has(wish.id)) {
        this.runDelete(sectionId, wish);
      }
    });
  }

  /**
   * Sends the delete. 204, and 404 `wish_not_found` (gone already, by another manager or the
   * retention), are both done: no error, the lists reload (E38). Every other refusal shows at the
   * row's section like any command; 409 `wish_state_conflict` also reloads (the wish changed, or a
   * merged child was locked).
   */
  private runDelete(sectionId: SectionId, wish: Wish): void {
    const channel = this.channel();
    const actionError = this.actionErrorOf(sectionId);
    // The list as shown when the delete went out: the focus goes to a neighbour of the row.
    const shownIds =
      sectionId === 'pinned'
        ? []
        : (this.sectionOf(sectionId)?.result()?.items ?? []).map((w) => w.id);
    actionError.set(null);
    this.clearDeletedNotice();
    this.setPending(wish.id, true);
    // Every ended state carries its end (E44); an empty value would come back as a refusal (400).
    this.wishService.deleteWish(channel, wish.id, wish.endedAtUtc ?? '').subscribe({
      next: () => {
        this.setPending(wish.id, false);
        this.onDeleted(channel, sectionId, wish, shownIds);
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
        const code = apiErrorCode(error);
        if (error.status === 404 && code === 'wish_not_found') {
          this.onDeleted(channel, sectionId, wish, shownIds);
          return;
        }
        actionError.set(wishErrorKey(error, 'action'));
        if (code === 'wish_state_conflict') {
          this.reloadAfterChange();
        }
      },
    });
  }

  /**
   * The wish is gone: the notice, the pin, the focus (B2), and the lists. A pinned row's pin is
   * cleared (no "no longer there") and the focus goes onto the notice, its "Bestand übernehmen" when
   * there is one; a row of a list hands the focus to its neighbour, else to the section heading.
   */
  private onDeleted(
    channel: string,
    sectionId: SectionId,
    wish: Wish,
    shownIds: readonly number[],
  ): void {
    if (channel !== this.channel() || this.destroyed) {
      return;
    }
    const pinned = sectionId === 'pinned';
    const notice = deletedNoticeFor(wish, pinned);
    this.deletedNotice.set(notice);
    this.deletedNoticeFocused.set(pinned && notice !== null);
    if (pinned) {
      this.clearPin(wish.id);
    }
    this.reloadAfterChange();
    if (pinned) {
      // Its way back when there is one, else the notice's text itself.
      const host = this.host.nativeElement;
      this.focusAfterRender(
        () =>
          host.querySelector<HTMLElement>('[data-deleted-adopt]') ??
          host.querySelector<HTMLElement>('[data-deleted-notice]'),
      );
      return;
    }
    const view = sectionId as WishListView;
    const next = focusAfterDelete(shownIds, wish.id, (id) => this.rowOf(view, id) !== null);
    this.focusAfterRender(() =>
      next !== null
        ? this.rowOf(view, next)
        : this.host.nativeElement.querySelector<HTMLElement>(
            view === 'ended' ? '#wishes-ended-title button' : `#wishes-${view}-title`,
          ),
    );
  }

  /** The pinned wish was deleted here: drop the pin and the query parameter that named it. */
  private clearPin(id: number): void {
    this.clearedTarget = id;
    this.focusedTarget = null;
    this.pinnedSeq++;
    this.pinned.set(null);
    this.pinnedActionError.set(null);
    if (this.targetId() === id) {
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { wish: null },
        queryParamsHandling: 'merge',
      });
    }
  }

  private clearDeletedNotice(): void {
    this.deletedNotice.set(null);
    this.deletedNoticeFocused.set(false);
  }

  /** The row of a wish in a list section, while it is on screen. */
  private rowOf(view: WishListView, id: number): HTMLElement | null {
    return this.host.nativeElement.querySelector<HTMLElement>(
      `section[aria-labelledby="wishes-${view}-title"] [data-wish-id="${id}"]`,
    );
  }

  /** Focuses what `target` finds once the page has rendered the change. */
  private focusAfterRender(target: () => HTMLElement | null): void {
    afterNextRender(() => target()?.focus(), { injector: this.injector });
  }

  /** The trigger went with the notice: focus left on nothing goes to the header's "Bestand übernehmen". */
  private focusHeaderAdoptWhenLost(): void {
    if (this.destroyed) {
      return;
    }
    afterNextRender(
      () => {
        const active = document.activeElement;
        if (active !== null && active !== document.body) {
          return;
        }
        this.host.nativeElement.querySelector<HTMLElement>('[data-header-adopt]')?.focus();
      },
      { injector: this.injector },
    );
  }

  /** The row showed a state the server no longer has: say so at its section and show the current one. */
  private onStale(sectionId: SectionId, errorKey: string): void {
    this.actionErrorOf(sectionId).set(errorKey);
    this.reloadAfterChange();
  }

  private onEntryClosed(result: WishEntryDialogResult | undefined): void {
    switch (result?.kind) {
      case 'created':
        this.setFullNotice.set(result.setFull);
        this.reloadAfterChange();
        return;
      case 'extended':
        this.reloadAfterChange();
        return;
      case 'edit':
        this.openEdit(result.wish);
        return;
      case 'jump':
        this.jumpTo(result.wishId);
        return;
      case 'adopt':
        this.openAdopt({
          sevenTvEmoteId: result.sevenTvEmoteId,
          slotIds: result.slotIds,
          ...(result.wish ? { wish: result.wish } : {}),
        });
        return;
      default:
        return;
    }
  }

  private onApproveClosed(
    wish: Wish,
    sectionId: SectionId,
    result: WishApproveDialogResult | undefined,
  ): void {
    switch (result?.kind) {
      case 'approved':
        this.reloadAfterChange();
        return;
      case 'merged':
        this.mergedNotice.set({
          runningWishId: result.runningWishId,
          expiresAtUtc: result.extendedExpiresAtUtc,
        });
        this.reloadAfterChange();
        return;
      case 'adopt':
        this.openAdopt({ wish, sevenTvEmoteId: result.sevenTvEmoteId, slotIds: result.slotIds });
        return;
      case 'jump':
        this.jumpTo(result.wishId);
        return;
      case 'stale':
        this.onStale(sectionId, result.errorKey);
        return;
      default:
        return;
    }
  }

  private onAdoptClosed(result: WishAdoptDialogResult | undefined): void {
    if (result?.kind === 'adopted') {
      this.reloadAfterChange();
    } else if (result?.kind === 'jump') {
      this.jumpTo(result.wishId);
    }
  }

  /**
   * "Zum Wunsch" from a dialog: the jump target of plan 2.4 (`?wish=`), which focuses a loaded row or
   * pins the wish above the sections when no loaded list holds it. The same id again focuses again.
   */
  private jumpTo(id: number): void {
    this.focusedTarget = null;
    this.clearedTarget = null;
    if (this.targetId() === id) {
      this.resolveTarget(id, this.loadedIds(), this.listsSettled(), this.pinned());
      return;
    }
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { wish: id },
      queryParamsHandling: 'merge',
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
    this.setFullNotice.set(false);
    this.mergedNotice.set(null);
    this.focusedTarget = null;
    this.clearedTarget = null;
    this.usageStore.reset();
    this.removalNotice.set(null);
    this.clearDeletedNotice();
    if (channel) {
      this.load(this.open);
      this.load(this.running);
    }
    this.loadActiveSet();
  }

  /**
   * The stored active set of the channel on screen. What was known is dropped first: while the read
   * is out, and after a failed one (429, 5xx, network), the dock stands for "unknown" and the restore
   * warns (E21) instead of comparing against a set a sync may have left. The next list reload asks
   * again (`reloadAll`).
   */
  private loadActiveSet(): void {
    const seq = ++this.activeSetSeq;
    this.activeSet.set(null);
    this.activeSetPending = false;
    const channel = this.channel();
    if (!channel) {
      return;
    }
    this.activeSetPending = true;
    this.emoteAdminService.getSetStatus(channel).subscribe({
      next: (status) => {
        if (seq === this.activeSetSeq && channel === this.channel()) {
          this.activeSetPending = false;
          this.activeSet.set({ channel, setId: status.activeEmoteSetId || null });
        }
      },
      error: (error: unknown) => {
        if (seq === this.activeSetSeq) {
          this.activeSetPending = false;
        }
        // 404: the channel has no row (yet), so no set either.
        if (
          seq === this.activeSetSeq &&
          channel === this.channel() &&
          error instanceof HttpErrorResponse &&
          error.status === 404
        ) {
          this.activeSet.set({ channel, setId: null });
        }
      },
    });
  }

  /**
   * The open rows in view ask for their preview again, with the wish as the reload brought it (only
   * while `ask`, the window visible). A row whose wish is no longer shown, or no longer open, is
   * forgotten and releases what it asked for: its row is gone or reports anew.
   */
  private requestPreviewsInView(rows: readonly Wish[], ask: boolean): void {
    const shown = new Map(rows.map((row) => [row.id, row]));
    for (const id of [...this.openRowsInView]) {
      const wish = shown.get(id);
      if (wish === undefined || wish.status !== 'pending') {
        // Its row is gone or no longer asks: what it asked for goes, by the id it asked with.
        this.openRowsInView.delete(id);
        this.preview.release(previewConsumer(id));
      } else if (ask && needsEmotePreview(wish)) {
        this.preview.request(wish.sevenTvEmoteId, previewConsumer(id));
      }
    }
  }

  private reloadAfterGap(): void {
    this.usageStore.expireAll();
    // A sync may have gone by unseen, and with it a set switch (first, so `reloadAll` sees it pending).
    this.loadActiveSet();
    this.reloadAll();
  }

  /**
   * A wish changed — by a command here, a dialog, a finished removal or a live event: the lists
   * reload, and the header count asks again (plan 2.4: one extra request, debounced in the service,
   * so a command and its own `wish.changed` make one).
   */
  private reloadAfterChange(): void {
    this.reloadAll();
    this.wishSummary.refresh();
  }

  /** Every loaded view once, plus the pinned wish; the ended list only while it is open. */
  private reloadAll(): void {
    // The set read failed earlier: ask again with the lists (also on the five-minute safety tick).
    if (this.activeSet() === null && !this.activeSetPending && this.channel()) {
      this.loadActiveSet();
    }
    this.load(this.open);
    this.load(this.running);
    if (this.endedOpen()) {
      this.load(this.ended);
    }
    // A pinned wish still loading is asked again as well: the new request supersedes the one on the
    // way, which may have read the state before the change (plan 2.4: the same reload rules).
    const pinned = this.pinnedView();
    if (pinned !== null) {
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
        if (!current()) {
          return;
        }
        section.loadError.set(errorKey(error));
        if (error instanceof HttpErrorResponse && error.status === 403) {
          // No longer allowed to see this list: what it showed (the redeemers' texts and logins
          // among it) goes with it, and so does an approve dialog holding one of them.
          const focusAtRisk =
            this.approveDialog !== null || this.host.nativeElement.contains(document.activeElement);
          section.result.set(null);
          this.approveDialog?.close(undefined);
          if (focusAtRisk) {
            this.focusRetryWhenLost(section.view);
          }
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
      this.clearedTarget = null;
      if (pinned !== null) {
        this.pinnedSeq++;
        this.pinned.set(null);
      }
      return;
    }
    if (id === this.clearedTarget) {
      // Deleted here; the query parameter is on its way out.
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

  /**
   * The dialog hands focus back to its trigger and the cleared list takes that (or a focused row
   * control) with it: once rendered, focus left on nothing goes to the list's "Neu laden" beside the
   * refusal (web/.claude/CLAUDE.md, focus management). Focus that landed elsewhere stays.
   */
  private focusRetryWhenLost(view: WishListView): void {
    if (this.destroyed) {
      return;
    }
    afterNextRender(
      () => {
        const active = document.activeElement;
        if (active !== null && active !== document.body) {
          return;
        }
        this.host.nativeElement.querySelector<HTMLElement>(`[data-retry-view="${view}"]`)?.focus();
      },
      { injector: this.injector },
    );
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
          this.reloadAfterChange();
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
          this.reloadAfterChange();
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
      case 'reject':
        return this.wishService.reject(channel, id);
      case 'unlink':
        return this.wishService.unlink(channel, id);
      default:
        // Unreachable: onAction lets through only what OFFERED_ACTIONS names.
        throw new Error(`Wish action ${action} is not offered on this page.`);
    }
  }

  private sectionOf(sectionId: SectionId): ListSection | null {
    switch (sectionId) {
      case 'open':
        return this.open;
      case 'running':
        return this.running;
      case 'ended':
        return this.ended;
      default:
        return null;
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

/** The preview consumer of one work-list row (`WishEmotePreviewService`): one per wish. */
function previewConsumer(wishId: number): string {
  return `wishes-row:${wishId}`;
}
