import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';
import { concatMap, filter, from, map, merge } from 'rxjs';

import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { pluralKey } from '../../core/i18n/plural';
import { EmoteTagSummary } from '../../core/tags/emote-tag.model';
import { EmoteTagService } from '../../core/tags/emote-tag.service';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NoticeBanner } from '../ui/notice-banner';
import { SkeletonRows } from '../ui/skeleton-rows';
import { TagNameField } from './tag-name-field';

export interface TagAssignDialogData {
  channelName: string;
  /** 7TV emote ids of the selection, as the grid keys them. */
  sevenTvEmoteIds: readonly string[];
}

/**
 * What the caller needs for its status message. `undefined` instead of a result when nothing was
 * assigned (dismissed, or the first request failed).
 */
export interface TagAssignDialogResult {
  /** Names of the tags the emotes were assigned to, in list order. */
  tagNames: string[];
  /**
   * Emotes that are now tagged, counted once — not summed over the tags: the same selection goes to
   * every chosen tag, so "3 emotes to 2 tags" is 3, not 6. Includes emotes a tag already carried.
   */
  emoteCount: number;
  /** Selected emotes that are no longer in the active set, counted once across all tags. */
  skippedNotInSetCount: number;
}

/**
 * "Tag zuweisen…" from a usage-stats selection (spec 7.0): tick tags, optionally create one inline,
 * confirm. Add-only, nothing is pre-ticked. One `addEntries` per ticked tag, strictly in list order;
 * a failure part-way keeps what already succeeded and says so in a banner.
 */
@Component({
  selector: 'app-tag-assign-dialog',
  imports: [Button, DialogShell, NoticeBanner, SkeletonRows, TagNameField, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="'tags.assignDialog.title' | transloco">
      @if (tags(); as list) {
        @if (list.length === 0) {
          <p class="text-sm text-fg-muted">{{ 'tags.assignDialog.noTagsHint' | transloco }}</p>
        } @else {
          <fieldset class="m-0 min-w-0 border-0 p-0">
            <legend class="sr-only">{{ 'tags.assignDialog.listLabel' | transloco }}</legend>
            <ul class="-mx-1 max-h-60 overflow-y-auto px-1">
              @for (tag of list; track tag.id) {
                <li>
                  <label class="flex items-center gap-2 py-1.5 text-sm text-fg">
                    <input
                      type="checkbox"
                      class="h-4 w-4 shrink-0 accent-accent-solid"
                      [checked]="checked().has(tag.id)"
                      [disabled]="isSubmitting()"
                      (change)="toggle(tag.id)"
                    />
                    <span class="min-w-0 truncate">{{ tag.name }}</span>
                  </label>
                </li>
              }
            </ul>
          </fieldset>
        }
      } @else if (loadErrorKey(); as key) {
        <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
      } @else {
        <app-skeleton-rows [count]="3" />
      }

      <app-tag-name-field
        [label]="'tags.assignDialog.newTagLabel' | transloco"
        inputId="tag-assign-new-name"
        (submitted)="create($event)"
      >
        <button
          field-action
          type="button"
          appButton="outline"
          [disabled]="isCreating() || isSubmitting()"
          (click)="nameField().submit()"
        >
          {{ 'tags.assignDialog.createButton' | transloco }}
        </button>
      </app-tag-name-field>

      @if (errorKey(); as key) {
        <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
      }
      @if (partial(); as done) {
        <app-notice-banner variant="warning">
          {{ 'tags.assignDialog.partial' | transloco: { tags: done.tagNames.join(', ') } }}
        </app-notice-banner>
      }

      @if (lockReasonKey(); as reasonKey) {
        <p dialog-actions id="tag-assign-lock-hint" class="mr-auto text-xs text-fg-muted">
          {{ reasonKey | transloco }}
        </p>
      }
      <button dialog-actions type="button" appButton="outline" buttonSize="lg" (click)="dismiss()">
        {{ (partial() ? 'common.close' : 'common.cancel') | transloco }}
      </button>
      <button
        dialog-actions
        type="button"
        appButton="primary"
        buttonSize="lg"
        [disabled]="lockReasonKey() !== null"
        [attr.aria-describedby]="lockReasonKey() !== null ? 'tag-assign-lock-hint' : null"
        (click)="assign()"
      >
        {{ confirmKey() | transloco: { count: data.sevenTvEmoteIds.length } }}
      </button>
    </app-dialog-shell>
  `,
})
export class TagAssignDialog {
  protected readonly data = inject<TagAssignDialogData>(DIALOG_DATA);
  private readonly dialogRef = inject<DialogRef<TagAssignDialogResult | undefined>>(DialogRef);
  private readonly tagService = inject(EmoteTagService);

  private readonly nameFieldRef = viewChild.required(TagNameField);
  // Across retries after a partial failure, so the caller's count stays honest.
  private readonly skippedIds = new Set<string>();

  protected readonly tags = signal<readonly EmoteTagSummary[] | null>(null);
  protected readonly loadErrorKey = signal<string | null>(null);
  protected readonly checked = signal<ReadonlySet<number>>(new Set());
  protected readonly isCreating = signal(false);
  protected readonly isSubmitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);
  /** What already went through when a later tag failed; also what a dismiss hands back. */
  protected readonly partial = signal<TagAssignDialogResult | null>(null);

  protected readonly confirmKey = computed(() =>
    pluralKey(this.data.sevenTvEmoteIds.length, 'tags.assignDialog.confirm'),
  );

  /** Why confirming is blocked right now, or `null` (§10: a locked button names its reason). */
  protected readonly lockReasonKey = computed(() =>
    this.checked().size === 0 ? 'tags.assignDialog.lockReason.noneChecked' : null,
  );

  constructor() {
    this.tagService
      .list(this.data.channelName)
      .pipe(takeUntilDestroyed())
      .subscribe({
        next: (result) => this.tags.set(result.tags),
        error: (error: HttpErrorResponse) => {
          this.tags.set(null);
          this.loadErrorKey.set(apiErrorTranslationKey(error));
        },
      });

    // After a partial failure the dialog may not just vanish with `undefined`: the caller still has
    // to learn about, and reload for, what went through. Hence Escape and a backdrop click hand the
    // partial result back like the Close button does (the CDK closes by itself only without it).
    merge(
      this.dialogRef.keydownEvents.pipe(filter((event) => event.key === 'Escape')),
      this.dialogRef.backdropClick,
    )
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.dismiss());
  }

  protected nameField(): TagNameField {
    return this.nameFieldRef();
  }

  protected toggle(tagId: number): void {
    this.checked.update((current) => {
      const next = new Set(current);
      if (!next.delete(tagId)) {
        next.add(tagId);
      }
      return next;
    });
  }

  protected dismiss(): void {
    this.dialogRef.close(this.partial() ?? undefined);
  }

  protected create(name: string): void {
    this.errorKey.set(null);
    this.isCreating.set(true);
    this.tagService.create(this.data.channelName, name).subscribe({
      next: (tag) => {
        this.isCreating.set(false);
        this.tags.update((list) => [
          ...(list ?? []),
          { id: tag.id, name: tag.name, entryCount: 0, inSetCount: null },
        ]);
        this.checked.update((current) => new Set(current).add(tag.id));
        this.nameFieldRef().reset();
      },
      error: (error: HttpErrorResponse) => {
        this.isCreating.set(false);
        if (!this.nameFieldRef().applyServerError(error)) {
          this.errorKey.set(apiErrorTranslationKey(error));
        }
      },
    });
  }

  protected assign(): void {
    if (this.lockReasonKey() !== null || this.isSubmitting() || this.isCreating()) {
      return;
    }
    const chosen = (this.tags() ?? []).filter((tag) => this.checked().has(tag.id));
    const succeeded: EmoteTagSummary[] = [];
    let emoteCount = this.partial()?.emoteCount ?? 0;
    const skipped = this.skippedIds;

    this.errorKey.set(null);
    this.isSubmitting.set(true);
    // concatMap: one request at a time, in list order — the next starts when the previous completed.
    from(chosen)
      .pipe(
        concatMap((tag) =>
          this.tagService
            .addEntries(this.data.channelName, tag.id, this.data.sevenTvEmoteIds)
            .pipe(map((result) => ({ tag, result }))),
        ),
      )
      .subscribe({
        next: ({ tag, result }) => {
          succeeded.push(tag);
          emoteCount = Math.max(emoteCount, result.addedCount + result.alreadyTaggedCount);
          result.skippedNotInSetIds.forEach((id) => skipped.add(id));
        },
        error: (error: HttpErrorResponse) => {
          this.isSubmitting.set(false);
          this.errorKey.set(apiErrorTranslationKey(error));
          if (succeeded.length > 0) {
            this.keepPartial(succeeded, emoteCount, skipped);
          }
        },
        complete: () => {
          this.dialogRef.close(this.buildResult(this.partial(), succeeded, emoteCount, skipped));
        },
      });
  }

  /**
   * Records the tags that went through, unticks them so a retry only repeats the failed rest, and
   * keeps the dialog open (and away from the CDK's own close) until the user dismisses it.
   */
  private keepPartial(
    succeeded: readonly EmoteTagSummary[],
    emoteCount: number,
    skipped: ReadonlySet<string>,
  ): void {
    this.partial.set(this.buildResult(this.partial(), succeeded, emoteCount, skipped));
    this.checked.update((current) => {
      const next = new Set(current);
      succeeded.forEach((tag) => next.delete(tag.id));
      return next;
    });
    this.dialogRef.disableClose = true;
  }

  private buildResult(
    earlier: TagAssignDialogResult | null,
    succeeded: readonly EmoteTagSummary[],
    emoteCount: number,
    skipped: ReadonlySet<string>,
  ): TagAssignDialogResult {
    return {
      tagNames: [...(earlier?.tagNames ?? []), ...succeeded.map((tag) => tag.name)],
      emoteCount,
      skippedNotInSetCount: skipped.size,
    };
  }
}

export function openTagAssignDialog(
  dialog: Dialog,
  data: TagAssignDialogData,
): DialogRef<TagAssignDialogResult | undefined> {
  return openAppDialog<TagAssignDialogResult | undefined, TagAssignDialogData>(
    dialog,
    TagAssignDialog,
    { data },
  );
}
