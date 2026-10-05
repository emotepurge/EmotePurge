import { ListRange } from '@angular/cdk/collections';
import { CdkVirtualScrollViewport, ScrollingModule } from '@angular/cdk/scrolling';
import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { NgTemplateOutlet } from '@angular/common';
import {
  Component,
  ElementRef,
  Signal,
  afterEveryRender,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import { EmoteSprite } from '../emotes/emote-sprite';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NamePreviewList } from '../ui/name-preview-list';
import { NoticeBanner } from '../ui/notice-banner';
import { TagRemovalProposal, TagRemovalRow } from './tag-removal';

/** Above this many rows the list is virtualised (spec 7.2/6). */
const TAG_REMOVAL_VIRTUAL_THRESHOLD = 50;

/** One uniform height for rows and headings: CDK's fixed-size strategy needs a single item size,
 *  and a row carries two lines (alias, then date or reason). */
const ITEM_HEIGHT_PX = 52;

export interface TagRemovalConfirmDialogData {
  tagName: string;
  /** The set the run deletes from, already named (the same convention as the delete dialog). */
  setName: string;
  proposal: TagRemovalProposal;
  /** Live, like the delete dialog's: the check finishes while the dialog may already be open. */
  warning: Signal<EmoteSetWarning | null>;
  warningLoading: Signal<boolean>;
}

/** Only the ticks go back: the snapshot and the own ids are the flow's, it knows them from the
 *  proposal. `undefined` (Escape, backdrop, Cancel) means nothing was confirmed. */
export interface TagRemovalConfirmResult {
  checkedIds: string[];
}

type ListItem =
  | { kind: 'heading'; id: string; labelKey: string }
  | { kind: 'row'; id: string; row: TagRemovalRow; checked: boolean; active: boolean };

/**
 * Preview and confirmation of a tag clear-out in one dialog (spec E19) — the last screen before
 * emotes leave 7TV. Built from the delete dialog's pieces: set line, shared-set banner, quiet
 * sentences, `NamePreviewList`.
 *
 * Every row can be ticked either way (the human decides, PRODUCT principle 1). The two blocks
 * ("proposed" / "not proposed") are the system's proposal, fixed when the dialog opens: a toggle
 * changes the checkbox and the summary, never the row's place, so nothing jumps under the pointer
 * and a keyboard user keeps their position. Past 50 rows the list is virtualised with a roving
 * tabindex (arrow keys, Home/End, `scrollToIndex`), because Tab alone never reaches a row outside
 * the CDK buffer (Designsprache 7.2). The confirm button is never disabled because
 * of n = 0 (E26: a tag with nothing to remove is still cleared out, without a delete run) — only
 * while the shared-set check is still running, as in the delete dialog.
 */
@Component({
  selector: 'app-tag-removal-confirm-dialog',
  imports: [
    Button,
    DialogShell,
    EmoteSprite,
    NamePreviewList,
    NgTemplateOutlet,
    NoticeBanner,
    ScrollingModule,
    TranslocoPipe,
  ],
  template: `
    <app-dialog-shell [dialogTitle]="'tags.removalDialog.title' | transloco: { tag: data.tagName }">
      <!-- A status region on purpose, unlike the delete dialog's counts: here the sentence is the
           only thing that answers a tick, and nothing else changes loudly. Present from the start,
           so it announces changes only, not its own arrival (§4.5). It is the dialog's ONLY
           status region: the "ownership check unavailable" notice below is a plain paragraph. -->
      <p role="status" class="text-sm font-medium text-fg">
        {{ removeKey() | transloco: { count: removeCount() } }},
        {{ keepKey() | transloco: { count: keepCount() } }}
      </p>
      <p class="text-sm text-fg-secondary">
        {{ 'massDelete.confirmSetLine' | transloco: { setName: data.setName } }}
      </p>

      @if (hasSharedSetWarning(); as warning) {
        <app-notice-banner variant="error">
          <span class="flex flex-col gap-1">
            <span class="font-medium">{{ 'massDelete.sharedSetWarningTitle' | transloco }}</span>
            @if (!warning.isOwnSet) {
              <span>{{ 'massDelete.notOwnSet' | transloco }}</span>
            }
            @if (warning.otherTrackedChannelsSharingSet.length > 0) {
              <span>
                {{
                  'massDelete.knownAffected'
                    | transloco: { list: warning.otherTrackedChannelsSharingSet.join(', ') }
                }}
              </span>
            }
            @if (warning.otherModeratedChannelsSharingSet.length > 0) {
              <span>
                {{
                  'massDelete.moderatedAffected'
                    | transloco: { list: warning.otherModeratedChannelsSharingSet.join(', ') }
                }}
              </span>
            }
          </span>
        </app-notice-banner>
      } @else if (ownershipCheckUnavailable()) {
        <p class="rounded-md bg-warning-wash px-4 py-3 text-sm text-warning-fg">
          {{ 'massDelete.ownershipCheckUnavailable' | transloco }}
        </p>
      }

      @if (!data.proposal.tagActive && items().length > 0) {
        <!-- Security model rule 3: the human is the last safeguard, so a preview whose ticks follow
             another rule than the familiar one says so. Without a play-in there is no "not played in by
             this tag" to leave unticked: everything of the tag is proposed. -->
        <p class="text-sm text-fg-secondary">
          {{ 'tags.removalDialog.notPlayedInLead' | transloco }}
        </p>
      }

      @if (items().length > 0) {
        <!-- One scroll container for the list (§7): the virtual viewport, or a capped box for short
             lists, both sized against dvh so the pane does not scroll as well. The wrapper rule
             pins CDK's shrink-to-fit content wrapper to the viewport width, otherwise a long name
             could widen every row instead of truncating. -->
        @if (virtual()) {
          <cdk-virtual-scroll-viewport
            [itemSize]="itemHeight"
            [minBufferPx]="itemHeight * 4"
            [maxBufferPx]="itemHeight * 8"
            [style.height]="viewportHeight"
            class="-mx-6 border-y border-border [&_.cdk-virtual-scroll-content-wrapper]:w-full"
          >
            <div
              *cdkVirtualFor="let item of items(); trackBy: trackItem"
              [style.height.px]="itemHeight"
            >
              <ng-container *ngTemplateOutlet="itemTpl; context: { $implicit: item }" />
            </div>
          </cdk-virtual-scroll-viewport>
        } @else {
          <div
            class="-mx-6 overflow-y-auto border-y border-border"
            [style.max-height]="viewportHeight"
          >
            @for (item of items(); track item.id) {
              <div [style.height.px]="itemHeight">
                <ng-container *ngTemplateOutlet="itemTpl; context: { $implicit: item }" />
              </div>
            }
          </div>
        }
      }

      <ng-template #itemTpl let-item>
        @if (item.kind === 'heading') {
          <!-- The two blocks are told apart by a hairline and a label, the band-header recipe of
               §2.5/§6.2, not by colour: nothing here is a warning, it is a sort order. -->
          <h3
            class="flex h-full items-end px-6 pb-1.5 text-[11px] font-semibold tracking-[0.13em] text-fg-secondary uppercase"
          >
            {{ item.labelKey | transloco }}
          </h3>
        } @else {
          <!-- Touch target: the whole 52 px row is the <label>, so a tap anywhere on it toggles the
               16 px checkbox — the equivalent-target exception of §10, not a small target. The
               checkbox is the roving tab stop; arrow keys move between rows. -->
          <label
            class="flex h-full cursor-pointer items-center gap-3 border-t border-border px-6 text-sm"
          >
            <input
              type="checkbox"
              class="h-4 w-4 shrink-0 accent-accent-solid"
              [attr.data-emote-id]="item.row.sevenTvEmoteId"
              [tabindex]="item.active ? 0 : -1"
              [checked]="item.checked"
              (change)="toggle(item.row.sevenTvEmoteId)"
              (focusin)="activeId.set(item.row.sevenTvEmoteId)"
              (keydown)="onKeydown($event, item.row.sevenTvEmoteId)"
            />
            <span class="app-sprite-cell flex size-8 shrink-0 items-center justify-center">
              @if (item.row.imageUrl; as url) {
                <app-emote-sprite [url]="url" [size]="32" />
              }
            </span>
            <span class="flex min-w-0 flex-col">
              <span class="truncate font-medium text-fg">{{ item.row.displayName }}</span>
              <span class="truncate text-xs text-fg-muted" [attr.title]="lineTitle(item.row)">
                @switch (item.row.reason) {
                  @case ('placed') {
                    @if (item.row.placedAtUtc; as placedAt) {
                      {{
                        'tags.removalDialog.placedAt' | transloco: { date: formatDate(placedAt) }
                      }}
                    }
                  }
                  @case ('heldBy') {
                    {{
                      'tags.removalDialog.reason.heldBy'
                        | transloco: { tag: tagNames(item.row.heldBy) }
                    }}
                    @if (item.row.placedAtUtc; as placedAt) {
                      ·
                      {{
                        'tags.removalDialog.placedAt' | transloco: { date: formatDate(placedAt) }
                      }}
                    }
                  }
                  @case ('alreadyPresent') {
                    {{ 'tags.removalDialog.reason.alreadyPresent' | transloco }}
                  }
                  <!-- 'tagged' (a tag that is not played in): proposed for its tagging alone,
                       nothing to add under the name — the sentence above the list says why. -->
                }
              </span>
            </span>
          </label>
        }
      </ng-template>

      @if (data.proposal.notInSetCount > 0) {
        <p class="text-xs text-fg-muted">
          {{ notInSetKey() | transloco: { count: data.proposal.notInSetCount } }}
        </p>
      }

      @if (removeCount() === 0) {
        <p class="text-sm text-fg-secondary">
          {{ nothingToDeleteKey() | transloco }}
        </p>
      } @else {
        <app-name-preview-list [names]="checkedNames()" [cap]="null" />
      }

      <div class="flex flex-col gap-1">
        <p class="text-sm text-fg-secondary">{{ 'massDelete.irreversibleNotice' | transloco }}</p>
        <p class="text-xs text-fg-muted">
          {{ 'massDelete.undetectableChannelsNotice' | transloco }}
        </p>
      </div>

      @if (data.warningLoading()) {
        <p dialog-actions id="tag-removal-confirm-hint" class="mr-auto text-xs text-fg-muted">
          {{ 'massDelete.checkingSharedSets' | transloco }}
        </p>
      }
      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        (click)="dialogRef.close(undefined)"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      <button
        dialog-actions
        type="button"
        appButton="danger-solid"
        buttonSize="lg"
        class="disabled:cursor-not-allowed"
        [disabled]="data.warningLoading()"
        [attr.aria-describedby]="data.warningLoading() ? 'tag-removal-confirm-hint' : null"
        (click)="confirm()"
      >
        {{ 'tags.removalDialog.confirm' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class TagRemovalConfirmDialog {
  protected readonly data = inject<TagRemovalConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<TagRemovalConfirmResult | undefined>>(DialogRef);
  private readonly languageService = inject(LanguageService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly viewport = viewChild(CdkVirtualScrollViewport);

  protected readonly itemHeight = ITEM_HEIGHT_PX;
  /** Against dvh like the other virtualised dialog tables (§7.2): the pane must not scroll too. */
  protected readonly viewportHeight = 'min(20rem, max(8rem, calc(100dvh - 24rem)))';

  /** The row holding the roving tab stop; null means the first row. */
  protected readonly activeId = signal<string | null>(null);

  private pendingFocusId: string | null = null;

  /** Ids the user flipped away from the proposal's start state. A set of flips, not of ticks, so
   *  the start state stays the proposal's and a second flip restores it. */
  protected readonly flipped = signal<ReadonlySet<string>>(new Set());

  /** Block membership is the proposal's own start state and never changes; only the ticks do. */
  private readonly proposedRows = computed(() => this.data.proposal.rows.filter((r) => r.checked));
  private readonly notProposedRows = computed(() =>
    this.data.proposal.rows.filter((r) => !r.checked),
  );

  protected readonly checkedRows = computed(() =>
    this.data.proposal.rows.filter((row) => this.isChecked(row)),
  );
  protected readonly removeCount = computed(() => this.checkedRows().length);
  protected readonly keepCount = computed(
    () => this.data.proposal.rows.length - this.removeCount(),
  );
  protected readonly removeKey = computed(() =>
    pluralKey(this.removeCount(), 'tags.removalDialog.summary.remove'),
  );
  protected readonly keepKey = computed(() =>
    pluralKey(this.keepCount(), 'tags.removalDialog.summary.keep'),
  );
  /** n = 0 deactivates a played-in tag; a tag that is not played in has nothing to lose — the
   *  sentence must not promise a change of state that does not happen. */
  protected readonly nothingToDeleteKey = computed(() =>
    this.data.proposal.tagActive
      ? 'tags.removalDialog.nothingToDelete'
      : 'tags.removalDialog.nothingToDeleteNotPlayedIn',
  );

  private readonly rowOrder = computed(() => [...this.proposedRows(), ...this.notProposedRows()]);
  /** The item ids in list order (headings included) — the positions the viewport renders by. */
  private readonly itemIds = computed(() => {
    const ids: (string | null)[] = [];
    if (this.proposedRows().length > 0) {
      ids.push(null, ...this.proposedRows().map((r) => r.sevenTvEmoteId));
    }
    if (this.notProposedRows().length > 0) {
      ids.push(null, ...this.notProposedRows().map((r) => r.sevenTvEmoteId));
    }
    return ids;
  });
  /** The virtual viewport's rendered item range; `null` for the short, fully rendered list. */
  private readonly renderedRange = signal<ListRange | null>(null);
  /**
   * The roving tab stop. The chosen row when it is in the DOM; once the viewport has scrolled it
   * out (wheel, scrollbar), the nearest rendered row takes over — otherwise Tab into the list would
   * find no stop at all until the user scrolled back (Task 11 re-review).
   */
  private readonly tabStopId = computed(() => {
    const preferred = this.activeId() ?? this.rowOrder()[0]?.sevenTvEmoteId ?? null;
    const range = this.renderedRange();
    if (preferred === null || range === null) {
      return preferred;
    }
    const ids = this.itemIds();
    const at = ids.indexOf(preferred);
    if (at >= range.start && at < range.end) {
      return preferred;
    }
    const target = at < range.start ? range.start : range.end - 1;
    let best: string | null = null;
    let bestDistance = Infinity;
    for (let index = range.start; index < Math.min(range.end, ids.length); index++) {
      const id = ids[index];
      const distance = Math.abs(index - target);
      if (id !== null && distance < bestDistance) {
        best = id;
        bestDistance = distance;
      }
    }
    return best ?? preferred;
  });

  protected readonly items = computed<ListItem[]>(() => {
    const tabStop = this.tabStopId();
    const toItem = (row: TagRemovalRow): ListItem => ({
      kind: 'row',
      id: row.sevenTvEmoteId,
      row,
      checked: this.isChecked(row),
      active: row.sevenTvEmoteId === tabStop,
    });
    const out: ListItem[] = [];
    const proposed = this.proposedRows();
    const notProposed = this.notProposedRows();
    if (proposed.length > 0) {
      out.push({
        kind: 'heading',
        id: 'h:proposed',
        labelKey: 'tags.removalDialog.proposedHeading',
      });
      out.push(...proposed.map(toItem));
    }
    if (notProposed.length > 0) {
      out.push({
        kind: 'heading',
        id: 'h:notProposed',
        labelKey: 'tags.removalDialog.notProposedHeading',
      });
      out.push(...notProposed.map(toItem));
    }
    return out;
  });

  protected readonly virtual = computed(
    () => this.data.proposal.rows.length > TAG_REMOVAL_VIRTUAL_THRESHOLD,
  );

  protected readonly checkedNames = computed(() => this.checkedRows().map((r) => r.displayName));
  protected readonly notInSetKey = computed(() =>
    pluralKey(this.data.proposal.notInSetCount, 'tags.removalDialog.notInSet'),
  );

  protected readonly hasSharedSetWarning = computed<EmoteSetWarning | null>(() => {
    const w = this.data.warning();
    if (!w?.available) {
      return null;
    }
    const flagged =
      !w.isOwnSet ||
      w.otherTrackedChannelsSharingSet.length > 0 ||
      w.otherModeratedChannelsSharingSet.length > 0;
    return flagged ? w : null;
  });
  protected readonly ownershipCheckUnavailable = computed(() => {
    const w = this.data.warning();
    return w !== null && !w.available;
  });

  constructor() {
    // Lands a focus request whose row the viewport had not rendered yet; runs after every render
    // pass until the row is in the DOM, then stops.
    afterEveryRender(() => this.landPendingFocus());

    // Follows the virtual viewport's rendered range for the tab stop's fallback above.
    effect((onCleanup) => {
      const viewport = this.viewport();
      if (!viewport) {
        this.renderedRange.set(null);
        return;
      }
      const subscription = viewport.renderedRangeStream.subscribe((range) =>
        this.renderedRange.set(range),
      );
      onCleanup(() => subscription.unsubscribe());
    });
  }

  protected readonly trackItem = (_: number, item: ListItem): string => item.id;

  protected toggle(id: string): void {
    const next = new Set(this.flipped());
    if (!next.delete(id)) {
      next.add(id);
    }
    this.flipped.set(next);
  }

  protected onKeydown(event: KeyboardEvent, id: string): void {
    const order = this.rowOrder();
    const index = order.findIndex((r) => r.sevenTvEmoteId === id);
    const last = order.length - 1;
    const next =
      event.key === 'ArrowDown'
        ? Math.min(index + 1, last)
        : event.key === 'ArrowUp'
          ? Math.max(index - 1, 0)
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? last
              : null;
    if (next === null || index < 0) {
      return;
    }
    event.preventDefault();
    this.focusRow(order[next].sevenTvEmoteId);
  }

  protected lineTitle(row: TagRemovalRow): string | null {
    return row.reason === 'heldBy' && row.heldBy.length > 0 ? this.tagNames(row.heldBy) : null;
  }

  protected confirm(): void {
    this.dialogRef.close({ checkedIds: this.checkedRows().map((r) => r.sevenTvEmoteId) });
  }

  protected formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString(toLocale(this.languageService.lang()), {
      dateStyle: 'short',
    });
  }

  protected tagNames(tags: readonly { name: string }[]): string {
    return tags.map((t) => t.name).join(', ');
  }

  /** Makes the row the tab stop and focuses it; one past the CDK buffer is scrolled to first and
   *  focused once a render pass has put it in the DOM. */
  private focusRow(id: string): void {
    this.activeId.set(id);
    this.pendingFocusId = id;
    this.landPendingFocus();
    if (this.pendingFocusId !== null) {
      const index = this.items().findIndex((i) => i.id === id);
      this.viewport()?.scrollToIndex(index);
    }
  }

  private landPendingFocus(): void {
    const id = this.pendingFocusId;
    if (id === null) {
      return;
    }
    const box = this.host.nativeElement.querySelector<HTMLInputElement>(
      `input[data-emote-id="${CSS.escape(id)}"]`,
    );
    if (box) {
      this.pendingFocusId = null;
      box.focus();
    }
  }

  private isChecked(row: TagRemovalRow): boolean {
    return row.checked !== this.flipped().has(row.sevenTvEmoteId);
  }
}

export function openTagRemovalConfirmDialog(
  dialog: Dialog,
  data: TagRemovalConfirmDialogData,
): DialogRef<TagRemovalConfirmResult | undefined> {
  return openAppDialog<TagRemovalConfirmResult | undefined, TagRemovalConfirmDialogData>(
    dialog,
    TagRemovalConfirmDialog,
    { data },
  );
}
