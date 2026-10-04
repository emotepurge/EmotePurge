import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { EmoteTag } from '../../core/tags/emote-tag.model';
import { EmoteTagService } from '../../core/tags/emote-tag.service';
import { TagNameField } from '../../shared/tags/tag-name-field';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';
import { NoticeBanner } from '../../shared/ui/notice-banner';

/** `tag` absent = create a new tag; present = rename that one (its name prefills the field). */
export interface TagNameDialogData {
  channelName: string;
  tag?: EmoteTag;
}

/**
 * "Neuer Tag" and "Umbenennen" on the tags page: one name field, one request. The field and its
 * error mapping are the shared `TagNameField` (the assign dialog uses the same one), so a name the
 * server rejects reads the same in both places. Closes with the stored tag, or `undefined` when
 * dismissed.
 */
@Component({
  selector: 'app-tag-name-dialog',
  imports: [Button, DialogShell, NoticeBanner, TagNameField, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="titleKey() | transloco">
      <app-tag-name-field
        [label]="'tags.nameDialog.label' | transloco"
        inputId="tag-name-dialog-input"
        [initialName]="data.tag?.name ?? ''"
        (submitted)="save($event)"
      />

      @if (errorKey(); as key) {
        <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
      }

      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        [disabled]="isSaving()"
        (click)="dialogRef.close(undefined)"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      <!-- Disabled only while the request is out: a transient pending state, which per §6.1 keeps
           its label and needs no reason text. -->
      <button
        dialog-actions
        type="button"
        appButton="primary"
        buttonSize="lg"
        [disabled]="isSaving()"
        (click)="nameField().submit()"
      >
        {{ confirmKey() | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class TagNameDialog {
  protected readonly data = inject<TagNameDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<EmoteTag | undefined>>(DialogRef);
  private readonly tagService = inject(EmoteTagService);
  private readonly nameFieldRef = viewChild.required(TagNameField);

  protected readonly isSaving = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  protected readonly titleKey = computed(() =>
    this.data.tag ? 'tags.nameDialog.renameTitle' : 'tags.nameDialog.createTitle',
  );
  protected readonly confirmKey = computed(() =>
    this.data.tag ? 'tags.nameDialog.confirmRename' : 'tags.nameDialog.confirmCreate',
  );

  protected nameField(): TagNameField {
    return this.nameFieldRef();
  }

  protected save(name: string): void {
    // Enter in the field reaches here without passing the disabled button.
    if (this.isSaving()) {
      return;
    }
    this.errorKey.set(null);
    this.isSaving.set(true);
    // Not dismissible while the request is out: the page would never hear about a tag that was
    // created after all.
    this.dialogRef.disableClose = true;
    const tag = this.data.tag;
    const request = tag
      ? this.tagService.rename(this.data.channelName, tag.id, name)
      : this.tagService.create(this.data.channelName, name);
    request.subscribe({
      next: (saved) => this.dialogRef.close(saved),
      error: (error: HttpErrorResponse) => {
        this.isSaving.set(false);
        this.dialogRef.disableClose = false;
        if (!this.nameFieldRef().applyServerError(error)) {
          this.errorKey.set(apiErrorTranslationKey(error));
        }
      },
    });
  }
}

export function openTagNameDialog(
  dialog: Dialog,
  data: TagNameDialogData,
): DialogRef<EmoteTag | undefined> {
  return openAppDialog<EmoteTag | undefined, TagNameDialogData>(dialog, TagNameDialog, { data });
}
