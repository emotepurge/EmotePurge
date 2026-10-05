import { ScrollingModule } from '@angular/cdk/scrolling';
import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { NgTemplateOutlet } from '@angular/common';
import {
  Component,
  Injector,
  Signal,
  afterNextRender,
  computed,
  inject,
  signal,
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
export const TAG_REMOVAL_VIRTUAL_THRESHOLD = 50;

/** One uniform height for rows and headings: CDK's fixed-size strategy needs a single item size,
 *  and a row carries two lines (alias, then date or reason). */
const ITEM_HEIGHT_PX = 52;

/** A proposal row plus the two display fields the dialog needs. Declared here until Task 10's row
 *  carries them itself (then this collapses to `TagRemovalRow`); Task 12 maps rows into it. */
export type TagRemovalDialogRow = TagRemovalRow & {
  /** What the row is called: never empty (entry alias / current name, else the set's default name). */
  displayName: string;
  /** Sprite still; `null` shows the empty plate. */
  imageUrl: string | null;
};
export type TagRemovalDialogProposal = Omit<TagRemovalProposal, 'rows'> & {
  rows: TagRemovalDialogRow[];
};

export interface TagRemovalConfirmDialogData {
  tagName: string;
  /** The set the run deletes from, already named (the same convention as the delete dialog). */
  setName: string;
  /** Gates the "this set is not currently active" sentence. */
  isActiveSet: boolean;
  proposal: TagRemovalDialogProposal;
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
  | { kind: 'row'; id: string; row: TagRemovalDialogRow; checked: boolean };

/**
 * Preview and confirmation of a tag clear-out in one dialog (spec E19) — the last screen before
 * emotes leave 7TV. Built from the delete dialog's pieces: set line, shared-set banner, quiet
 * sentences, `NamePreviewList`.
 *
 * Every row can be ticked either way (the human decides, PRODUCT principle 1). Ticked rows come
 * first, the unticked ones follow under "not proposed" with their reason, so toggling moves a row
 * across the hairline; focus is put back on the toggled checkbox afterwards, otherwise the move
 * would drop a keyboard user to the top of the dialog. The confirm button is never disabled because
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
           so it announces changes only, not its own arrival (§4.5). -->
      <p role="status" class="text-sm font-medium text-fg">
        {{
          'tags.removalDialog.summary'
            | transloco: { removeCount: removeCount(), keepCount: keepCount() }
        }}
      </p>
      <p class="text-sm text-fg-secondary">
        {{ 'massDelete.confirmSetLine' | transloco: { setName: data.setName } }}
      </p>
      @if (!data.isActiveSet) {
        <p class="text-sm text-fg-secondary">{{ 'massDelete.confirmSetNotActive' | transloco }}</p>
      }

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
        <app-notice-banner variant="warning">
          {{ 'massDelete.ownershipCheckUnavailable' | transloco }}
        </app-notice-banner>
      }

      @if (items().length > 0) {
        @if (virtual()) {
          <cdk-virtual-scroll-viewport
            [itemSize]="itemHeight"
            [minBufferPx]="itemHeight * 4"
            [maxBufferPx]="itemHeight * 8"
            class="-mx-6 h-72 border-y border-border"
          >
            <div
              *cdkVirtualFor="let item of items(); trackBy: trackItem"
              [style.height.px]="itemHeight"
            >
              <ng-container *ngTemplateOutlet="itemTpl; context: { $implicit: item }" />
            </div>
          </cdk-virtual-scroll-viewport>
        } @else {
          <div class="-mx-6 max-h-72 overflow-y-auto border-y border-border">
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
          <label
            class="flex h-full cursor-pointer items-center gap-3 border-t border-border px-6 text-sm"
          >
            <input
              type="checkbox"
              class="h-4 w-4 shrink-0 accent-accent-solid"
              [attr.data-emote-id]="item.row.sevenTvEmoteId"
              [checked]="item.checked"
              (change)="toggle(item.row.sevenTvEmoteId)"
            />
            <span class="app-sprite-cell flex size-8 shrink-0 items-center justify-center">
              @if (item.row.imageUrl; as url) {
                <app-emote-sprite [url]="url" [size]="32" />
              }
            </span>
            <span class="flex min-w-0 flex-col">
              <span class="truncate font-medium text-fg">{{ item.row.displayName }}</span>
              <span class="truncate text-xs text-fg-muted">
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
                  }
                  @case ('alreadyPresent') {
                    {{ 'tags.removalDialog.reason.alreadyPresent' | transloco }}
                  }
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
          {{ 'tags.removalDialog.nothingToDelete' | transloco }}
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
  private readonly injector = inject(Injector);

  protected readonly itemHeight = ITEM_HEIGHT_PX;

  /** Ids the user flipped away from the proposal's start state. A set of flips, not of ticks, so
   *  the start state stays the proposal's and a second flip restores it. */
  protected readonly flipped = signal<ReadonlySet<string>>(new Set());

  protected readonly checkedRows = computed(() =>
    this.data.proposal.rows.filter((row) => this.isChecked(row)),
  );
  protected readonly uncheckedRows = computed(() =>
    this.data.proposal.rows.filter((row) => !this.isChecked(row)),
  );
  protected readonly removeCount = computed(() => this.checkedRows().length);
  protected readonly keepCount = computed(() => this.uncheckedRows().length);

  protected readonly items = computed<ListItem[]>(() => {
    const checked = this.checkedRows();
    const unchecked = this.uncheckedRows();
    const out: ListItem[] = [];
    if (checked.length > 0) {
      out.push({
        kind: 'heading',
        id: 'h:proposed',
        labelKey: 'tags.removalDialog.proposedHeading',
      });
      out.push(
        ...checked.map((row): ListItem => ({
          kind: 'row',
          id: row.sevenTvEmoteId,
          row,
          checked: true,
        })),
      );
    }
    if (unchecked.length > 0) {
      out.push({
        kind: 'heading',
        id: 'h:notProposed',
        labelKey: 'tags.removalDialog.notProposedHeading',
      });
      out.push(
        ...unchecked.map((row): ListItem => ({
          kind: 'row',
          id: row.sevenTvEmoteId,
          row,
          checked: false,
        })),
      );
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

  protected readonly trackItem = (_: number, item: ListItem): string => item.id;

  protected toggle(id: string): void {
    const next = new Set(this.flipped());
    if (!next.delete(id)) {
      next.add(id);
    }
    this.flipped.set(next);
    // The row just changed blocks; give a keyboard user their place back.
    afterNextRender(
      () => {
        const box = document.querySelector<HTMLInputElement>(
          `app-tag-removal-confirm-dialog input[data-emote-id="${CSS.escape(id)}"]`,
        );
        box?.focus();
      },
      { injector: this.injector },
    );
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

  private isChecked(row: TagRemovalDialogRow): boolean {
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
