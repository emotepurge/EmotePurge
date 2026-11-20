import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { apiErrorCode, GENERIC_ERROR_TRANSLATION_KEY } from '../../core/i18n/api-error';
import { wishErrorKey } from '../../core/wishes/wish-error';
import {
  parseWishDurationDays,
  validateWishAlias,
  WISH_ALIAS_MAX_LENGTH,
  WISH_DURATION_DAYS_MAX,
} from '../../core/wishes/wish-input';
import type { UpdateWishBody, Wish } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { hasEmote, wishTitle } from '../../core/wishes/wish-view';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';
import { NoticeBanner } from '../../shared/ui/notice-banner';

export interface WishEditDialogData {
  /** Normalised. */
  channelName: string;
  /** The wish as the page knows it; the fields start with its alias and duration. */
  wish: Wish;
}

/**
 * `edited`: the wish the server answered with. `stale`: the wish is no longer in the state the row
 * showed (409 `wish_state_conflict`, 404 `wish_not_found`); the page shows the text at the section and
 * reloads. `undefined`: dismissed.
 */
export type WishEditDialogResult =
  { kind: 'edited'; wish: Wish } | { kind: 'stale'; errorKey: string };

/**
 * The PATCH body for the fields that differ from the wish: an unchanged field is not sent. `null`
 * when nothing differs (sending is then blocked). Both values must already be valid.
 */
export function wishEditBody(
  wish: Wish,
  alias: string,
  durationDays: number,
): UpdateWishBody | null {
  const body: UpdateWishBody = {
    ...(alias !== wish.alias ? { alias } : {}),
    ...(durationDays !== wish.durationDays ? { durationDays } : {}),
  };
  return Object.keys(body).length === 0 ? null : body;
}

/**
 * "Bearbeiten" (#485, plan T7, AK35): alias and duration of an `approved` or `failed` wish, filled in
 * with the wish's own values. **Only fields the manager changed are sent**, and without a change
 * sending is blocked with the reason. A conflict that says the wish moved on closes the dialog with
 * `stale`: the page owns the section banner and the reload (as for the row's other commands).
 *
 * **An open wish (K6).** Without an emote there is no alias to set (Sol-P3-12): the dialog shows only
 * the duration, checks no alias and sends exactly `{ durationDays }`. An open wish that knows its
 * emote but has no alias yet (captured from a link) keeps the alias field; left empty it stays unset
 * and is not sent.
 */
@Component({
  selector: 'app-wish-edit-dialog',
  imports: [Button, DialogShell, NoticeBanner, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="'wishes.edit.title' | transloco">
      <p class="text-sm break-words text-fg-secondary">
        @if (aliasEditable) {
          {{
            'wishes.edit.intro'
              | transloco: { name: name || ('wishes.pending.untitled' | transloco) }
          }}
        } @else {
          {{
            'wishes.edit.introDuration'
              | transloco: { name: name || ('wishes.pending.untitled' | transloco) }
          }}
        }
      </p>

      @if (aliasEditable) {
        <div class="flex flex-col gap-1">
          <label for="wish-edit-alias" class="text-sm text-fg-secondary">
            {{ 'wishes.entry.alias.label' | transloco }}
          </label>
          <input
            id="wish-edit-alias"
            type="text"
            class="app-input"
            autocomplete="off"
            spellcheck="false"
            [value]="alias()"
            [attr.aria-invalid]="aliasError() ? 'true' : null"
            [attr.aria-describedby]="aliasError() ? 'wish-edit-alias-error' : null"
            (input)="setAlias($any($event.target).value)"
          />
          @if (aliasError(); as key) {
            <p id="wish-edit-alias-error" class="text-sm text-danger-fg">
              {{ key | transloco: { max: aliasMax } }}
            </p>
          }
        </div>
      }

      <div class="flex flex-col gap-1">
        <label for="wish-edit-duration" class="text-sm text-fg-secondary">
          {{ 'wishes.dialog.duration' | transloco }}
        </label>
        <input
          id="wish-edit-duration"
          type="text"
          inputmode="numeric"
          class="app-input"
          autocomplete="off"
          [value]="duration()"
          [attr.aria-invalid]="durationError() ? 'true' : null"
          [attr.aria-describedby]="durationError() ? 'wish-edit-duration-error' : null"
          (input)="setDuration($any($event.target).value)"
        />
        @if (durationError(); as key) {
          <p id="wish-edit-duration-error" class="text-sm text-danger-fg">
            {{ key | transloco: { max: durationMax } }}
          </p>
        }
      </div>

      @if (disabledFeature()) {
        <app-notice-banner variant="error">{{
          'errors.api.wishes_disabled' | transloco
        }}</app-notice-banner>
      } @else if (errorKey(); as key) {
        <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
      }

      @if (blockedReason(); as reason) {
        <p dialog-actions id="wish-edit-blocked" class="mr-auto text-xs text-fg-muted">
          {{ reason | transloco }}
        </p>
      }
      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        [disabled]="saving()"
        (click)="dialogRef.close(undefined)"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      <button
        dialog-actions
        type="button"
        appButton="primary"
        buttonSize="lg"
        [disabled]="saving() || blockedReason() !== null"
        [attr.aria-describedby]="blockedReason() ? 'wish-edit-blocked' : null"
        (click)="submit()"
      >
        {{ 'wishes.edit.submit' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class WishEditDialog {
  protected readonly data = inject<WishEditDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<WishEditDialogResult | undefined>>(DialogRef);
  private readonly wishService = inject(WishService);

  protected readonly aliasMax = WISH_ALIAS_MAX_LENGTH;
  protected readonly durationMax = WISH_DURATION_DAYS_MAX;

  /** An alias needs an emote (plan 2.6 PATCH, Sol-P3-12); an open wish without one edits only its duration. */
  protected readonly aliasEditable = hasEmote(this.data.wish);
  /** An empty alias may stay empty: the wish never had one (an open link wish). */
  private readonly aliasMayStayEmpty = this.data.wish.alias === '';
  protected readonly name = wishTitle(this.data.wish);

  protected readonly alias = signal(this.data.wish.alias);
  protected readonly duration = signal(String(this.data.wish.durationDays));
  /** A field the server refused, as the translation key to show there; cleared by the next edit. */
  private readonly serverFieldError = signal<{ field: 'alias' | 'duration'; key: string } | null>(
    null,
  );
  protected readonly errorKey = signal<string | null>(null);
  protected readonly disabledFeature = signal(false);
  protected readonly saving = signal(false);

  private readonly aliasResult = computed(() =>
    !this.aliasEditable || (this.aliasMayStayEmpty && this.alias() === '')
      ? { ok: true as const, alias: this.data.wish.alias }
      : validateWishAlias(this.alias()),
  );
  private readonly durationResult = computed(() => parseWishDurationDays(this.duration()));

  protected readonly aliasError = computed(() => {
    const server = this.serverFieldError();
    if (server?.field === 'alias') {
      return server.key;
    }
    const result = this.aliasResult();
    return result.ok ? null : `wishes.entry.alias.${result.reason}`;
  });

  protected readonly durationError = computed(() => {
    const server = this.serverFieldError();
    if (server?.field === 'duration') {
      return server.key;
    }
    const result = this.durationResult();
    return result.ok ? null : `wishes.dialog.durationInvalid.${result.reason}`;
  });

  /** The body to send, `null` while a field is invalid or nothing differs from the wish. */
  private readonly body = computed(() => {
    const alias = this.aliasResult();
    const duration = this.durationResult();
    return alias.ok && duration.ok
      ? wishEditBody(this.data.wish, alias.alias, duration.value)
      : null;
  });

  /** The first reason the change cannot be sent, as a translation key; `null` when it can. */
  protected readonly blockedReason = computed(() => {
    if (this.disabledFeature()) {
      return 'wishes.dialog.blocked.disabled';
    }
    if (!this.aliasResult().ok) {
      return 'wishes.entry.blocked.alias';
    }
    if (!this.durationResult().ok) {
      return 'wishes.dialog.blocked.duration';
    }
    if (this.body() === null) {
      return this.aliasEditable
        ? 'wishes.edit.blocked.unchanged'
        : 'wishes.edit.blocked.durationUnchanged';
    }
    return null;
  });

  protected setAlias(value: string): void {
    this.alias.set(value);
    this.clearServerError('alias');
  }

  protected setDuration(value: string): void {
    this.duration.set(value);
    this.clearServerError('duration');
  }

  protected submit(): void {
    const body = this.body();
    if (this.saving() || this.blockedReason() !== null || body === null) {
      return;
    }
    this.errorKey.set(null);
    this.saving.set(true);
    // Not dismissible while the request is out: the page would never hear of a change made after all.
    this.dialogRef.disableClose = true;
    this.wishService.update(this.data.channelName, this.data.wish.id, body).subscribe({
      next: (wish) => this.dialogRef.close({ kind: 'edited', wish }),
      error: (error: unknown) => this.onError(error),
    });
  }

  private onError(error: unknown): void {
    this.saving.set(false);
    this.dialogRef.disableClose = false;
    if (!(error instanceof HttpErrorResponse)) {
      this.errorKey.set(GENERIC_ERROR_TRANSLATION_KEY);
      return;
    }
    switch (apiErrorCode(error)) {
      case 'wish_state_conflict':
      case 'wish_not_found':
        this.dialogRef.close({ kind: 'stale', errorKey: wishErrorKey(error, 'update') });
        return;
      case 'wish_alias_invalid':
        this.serverFieldError.set({ field: 'alias', key: 'errors.api.wish_alias_invalid' });
        return;
      case 'wish_duration_out_of_range':
        this.serverFieldError.set({
          field: 'duration',
          key: 'wishes.dialog.durationInvalid.out_of_range',
        });
        return;
      case 'wishes_disabled':
        this.disabledFeature.set(true);
        return;
      default:
        this.errorKey.set(wishErrorKey(error, 'update'));
    }
  }

  private clearServerError(field: 'alias' | 'duration'): void {
    if (this.serverFieldError()?.field === field) {
      this.serverFieldError.set(null);
    }
    this.errorKey.set(null);
  }
}

export function openWishEditDialog(
  dialog: Dialog,
  data: WishEditDialogData,
): DialogRef<WishEditDialogResult | undefined> {
  return openAppDialog<WishEditDialogResult | undefined, WishEditDialogData>(
    dialog,
    WishEditDialog,
    { data },
  );
}
