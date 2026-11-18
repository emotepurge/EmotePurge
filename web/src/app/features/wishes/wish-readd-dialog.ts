import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { apiErrorCode, GENERIC_ERROR_TRANSLATION_KEY } from '../../core/i18n/api-error';
import { wishErrorKey } from '../../core/wishes/wish-error';
import { parseWishDurationDays, WISH_DURATION_DAYS_MAX } from '../../core/wishes/wish-input';
import type { ReAddWishBody, Wish, WishReAddMode } from '../../core/wishes/wish.model';
import { wishConflict, WishService } from '../../core/wishes/wish.service';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';
import { NoticeBanner } from '../../shared/ui/notice-banner';

export interface WishReAddDialogData {
  /** Normalised. */
  channelName: string;
  wish: Wish;
}

/**
 * `readded`: the wish the server answered with. `jump`: the manager asked to go to the wish that runs
 * for the emote. `stale`: the wish is no longer in the state the row showed; the page shows the text at
 * the section and reloads.
 */
export type WishReAddDialogResult =
  | { kind: 'readded'; wish: Wish }
  | { kind: 'jump'; wishId: number }
  | { kind: 'stale'; errorKey: string };

/** The re-add body: `permanent` never carries a duration, `timed` always does. */
export function wishReAddBody(
  mode: WishReAddMode,
  durationDays: number | null,
): ReAddWishBody | null {
  if (mode === 'permanent') {
    return { mode };
  }
  return durationDays === null ? null : { mode, durationDays };
}

/**
 * "Wieder hinzufügen" (#485, plan T7, AK35) for a `removed` or `removed_externally` wish: timed, with a
 * duration that starts with the channel default, or permanent (no duration; the wish is then carried
 * as kept). `wish_already_running` (a wish for the emote runs again) stays in the dialog with the way
 * to that wish; a conflict that says the wish moved on closes it with `stale`.
 */
@Component({
  selector: 'app-wish-readd-dialog',
  imports: [Button, DialogShell, NoticeBanner, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="'wishes.readd.title' | transloco">
      <p class="text-sm text-fg-secondary">
        {{ 'wishes.readd.intro' | transloco: { name: data.wish.alias || data.wish.emoteName } }}
      </p>

      <fieldset class="flex flex-col gap-1">
        <legend class="mb-1 text-sm text-fg-secondary">
          {{ 'wishes.readd.mode.legend' | transloco }}
        </legend>
        <label class="flex items-center gap-2 py-1 text-sm">
          <input
            type="radio"
            name="wish-readd-mode"
            class="h-4 w-4 accent-accent-solid"
            [checked]="mode() === 'timed'"
            [disabled]="saving()"
            (change)="setMode('timed')"
          />
          {{ 'wishes.readd.mode.timed' | transloco }}
        </label>
        <label class="flex items-center gap-2 py-1 text-sm">
          <input
            type="radio"
            name="wish-readd-mode"
            class="h-4 w-4 accent-accent-solid"
            [checked]="mode() === 'permanent'"
            [disabled]="saving()"
            (change)="setMode('permanent')"
          />
          {{ 'wishes.readd.mode.permanent' | transloco }}
        </label>
      </fieldset>

      @if (mode() === 'timed') {
        <div class="flex flex-col gap-1">
          <label for="wish-readd-duration" class="text-sm text-fg-secondary">
            {{ 'wishes.dialog.duration' | transloco }}
          </label>
          <input
            id="wish-readd-duration"
            type="text"
            inputmode="numeric"
            class="app-input"
            autocomplete="off"
            [value]="duration()"
            [attr.aria-invalid]="durationError() ? 'true' : null"
            [attr.aria-describedby]="durationError() ? 'wish-readd-duration-error' : null"
            (input)="setDuration($any($event.target).value)"
            (blur)="durationTouched.set(true)"
          />
          @if (durationError(); as key) {
            <p id="wish-readd-duration-error" class="text-sm text-danger-fg">
              {{ key | transloco: { max: durationMax } }}
            </p>
          }
        </div>
      } @else {
        <p class="text-sm text-fg-body">{{ 'wishes.readd.permanentHint' | transloco }}</p>
      }

      <!-- The running-wish notice arrives while the dialog stays open: the permanently mounted region
           speaks it (§4.4); the visible text is aria-hidden, its button is not. -->
      <p role="status" class="sr-only">
        @if (running()) {
          {{ 'wishes.readd.running' | transloco }}
        }
      </p>
      @if (running(); as notice) {
        <app-notice-banner variant="info">
          <span aria-hidden="true">{{ 'wishes.readd.running' | transloco }}</span>
          <button
            notice-action
            type="button"
            appButton="outline"
            [disabled]="saving()"
            (click)="jump(notice.wishId)"
          >
            {{ 'wishes.dialog.toWish' | transloco }}
          </button>
        </app-notice-banner>
      }

      @if (disabledFeature()) {
        <app-notice-banner variant="error">{{
          'errors.api.wishes_disabled' | transloco
        }}</app-notice-banner>
      } @else if (errorKey(); as key) {
        <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
      }

      @if (blockedReason(); as reason) {
        <p dialog-actions id="wish-readd-blocked" class="mr-auto text-xs text-fg-muted">
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
        [attr.aria-describedby]="blockedReason() ? 'wish-readd-blocked' : null"
        (click)="submit()"
      >
        {{ 'wishes.readd.submit' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class WishReAddDialog {
  protected readonly data = inject<WishReAddDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<WishReAddDialogResult | undefined>>(DialogRef);
  private readonly wishService = inject(WishService);

  protected readonly durationMax = WISH_DURATION_DAYS_MAX;

  protected readonly mode = signal<WishReAddMode>('timed');
  protected readonly duration = signal('');
  protected readonly durationTouched = signal(false);
  private readonly serverDurationError = signal(false);
  protected readonly running = signal<{ wishId: number } | null>(null);
  protected readonly errorKey = signal<string | null>(null);
  protected readonly disabledFeature = signal(false);
  protected readonly saving = signal(false);

  private readonly durationResult = computed(() => parseWishDurationDays(this.duration()));

  protected readonly durationError = computed(() => {
    if (this.serverDurationError()) {
      return 'wishes.dialog.durationInvalid.out_of_range';
    }
    const result = this.durationResult();
    return !result.ok && this.durationTouched()
      ? `wishes.dialog.durationInvalid.${result.reason}`
      : null;
  });

  /** The body to send, `null` while the chosen mode lacks a valid duration. */
  private readonly body = computed(() => {
    const duration = this.durationResult();
    return wishReAddBody(this.mode(), duration.ok ? duration.value : null);
  });

  protected readonly blockedReason = computed(() => {
    if (this.disabledFeature()) {
      return 'wishes.dialog.blocked.disabled';
    }
    return this.body() === null ? 'wishes.dialog.blocked.duration' : null;
  });

  constructor() {
    this.wishService.getSettings(this.data.channelName).subscribe({
      next: (settings) => {
        if (this.duration() === '' && !this.durationTouched()) {
          this.duration.set(String(settings.defaultDurationDays));
        }
      },
      // Without the default the field stays empty and asks for a value.
      error: () => undefined,
    });
  }

  protected setMode(mode: WishReAddMode): void {
    this.mode.set(mode);
    this.errorKey.set(null);
    this.running.set(null);
  }

  protected setDuration(value: string): void {
    this.duration.set(value);
    this.durationTouched.set(true);
    this.serverDurationError.set(false);
    this.errorKey.set(null);
  }

  protected jump(wishId: number): void {
    if (!this.saving()) {
      this.dialogRef.close({ kind: 'jump', wishId });
    }
  }

  protected submit(): void {
    const body = this.body();
    if (this.saving() || this.blockedReason() !== null || body === null) {
      return;
    }
    this.errorKey.set(null);
    this.running.set(null);
    this.saving.set(true);
    this.dialogRef.disableClose = true;
    this.wishService.reAdd(this.data.channelName, this.data.wish.id, body).subscribe({
      next: (wish) => this.dialogRef.close({ kind: 'readded', wish }),
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
        this.dialogRef.close({ kind: 'stale', errorKey: wishErrorKey(error, 're-add') });
        return;
      case 'wish_already_running': {
        const wishId = wishConflict(error).wishId ?? null;
        if (wishId === null) {
          this.errorKey.set(wishErrorKey(error, 're-add'));
        } else {
          this.running.set({ wishId });
        }
        return;
      }
      case 'wish_duration_out_of_range':
        this.serverDurationError.set(true);
        return;
      case 'wishes_disabled':
        this.disabledFeature.set(true);
        return;
      default:
        this.errorKey.set(wishErrorKey(error, 're-add'));
    }
  }
}

export function openWishReAddDialog(
  dialog: Dialog,
  data: WishReAddDialogData,
): DialogRef<WishReAddDialogResult | undefined> {
  return openAppDialog<WishReAddDialogResult | undefined, WishReAddDialogData>(
    dialog,
    WishReAddDialog,
    { data },
  );
}
