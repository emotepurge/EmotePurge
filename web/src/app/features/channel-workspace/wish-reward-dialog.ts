import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { apiErrorCode, GENERIC_ERROR_TRANSLATION_KEY } from '../../core/i18n/api-error';
import { wishErrorKey } from '../../core/wishes/wish-error';
import {
  parseWishDurationDays,
  validateRewardLabel,
  WISH_DURATION_DAYS_MAX,
  WISH_REWARD_LABEL_MAX_LENGTH,
} from '../../core/wishes/wish-input';
import type { WishReward } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import {
  confirmRewardBody,
  rewardEditBody,
  type WishRewardLearningWithCandidate,
} from './wish-reward-learning';

/** How often the confirm dialog looks at the session's end time. */
const CLOCK_TICK_MS = 1000;

/**
 * `confirm`: the candidate the section showed when the manager chose "Bestätigen", with the
 * session's ids; the dialog sends exactly these (plan 2.4, Sol-P2-4). `edit`: a learned reward.
 */
export type WishRewardDialogData =
  | {
      mode: 'confirm';
      /** Normalised. */
      channelName: string;
      learning: WishRewardLearningWithCandidate;
      /** The channel's default duration; the field starts with it. */
      defaultDurationDays: number;
    }
  | { mode: 'edit'; channelName: string; reward: WishReward };

/**
 * `saved`: the reward the server answered with (created or changed). `stale`: the learning the
 * dialog showed was replaced (409 `wish_learning_stale`); the manager has read the explanation and
 * asked for the new state. `gone`: what the dialog showed no longer exists (`wish_reward_no_candidate`,
 * `wish_reward_not_found`); the section names `errorKey` and reloads. `restricted`: the caller may not
 * write (403 `wish_settings_restricted`). `undefined`: dismissed, nothing saved.
 */
export type WishRewardDialogResult =
  | { kind: 'saved'; reward: WishReward }
  | { kind: 'stale' }
  | { kind: 'gone'; errorKey: string }
  | { kind: 'restricted' };

/**
 * Confirms a learned reward or edits one (K6, plan 2.10): label and default duration. Saving happens
 * here, and only on "Speichern" — showing a candidate saves nothing (AK69). A learning that moved on
 * under the dialog is refused by the server; the dialog says so and offers to reload, instead of
 * closing silently.
 */
@Component({
  selector: 'app-wish-reward-dialog',
  imports: [Button, DialogShell, NoticeBanner, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="titleKey | transloco">
      @if (candidate; as seen) {
        <div class="flex flex-col gap-1">
          <p class="text-sm text-fg-secondary">
            {{ 'wishes.settings.rewards.dialog.candidateIntro' | transloco }}
          </p>
          <p class="text-sm break-words text-fg-body">
            @if (seen.text !== '') {
              <q>{{ seen.text }}</q>
            } @else {
              {{ 'wishes.settings.rewards.learning.noText' | transloco }}
            }
          </p>
          <p class="text-xs text-fg-muted">
            {{
              'wishes.settings.rewards.learning.seenBy'
                | transloco: { login: seen.login, time: formatTime(seen.seenAtUtc) }
            }}
          </p>
        </div>
        <p class="text-sm text-fg-secondary">
          {{ 'wishes.settings.rewards.dialog.notAWish' | transloco }}
        </p>
      }

      @if (stale()) {
        <!-- The learning changed under the dialog: nothing was saved, the fields stay as typed but
             can no longer be sent. -->
        <app-notice-banner variant="error">
          {{ 'errors.api.wish_learning_stale' | transloco }}
          <button notice-action type="button" appButton="outline" (click)="reload()">
            {{ 'wishes.settings.rewards.reload' | transloco }}
          </button>
        </app-notice-banner>
      }

      <div class="flex flex-col gap-1">
        <label for="wish-reward-label" class="text-sm text-fg-secondary">
          {{ 'wishes.settings.rewards.dialog.label' | transloco }}
        </label>
        <input
          id="wish-reward-label"
          type="text"
          class="app-input"
          autocomplete="off"
          [value]="label()"
          [readOnly]="saving()"
          [attr.aria-invalid]="labelError() ? 'true' : null"
          [attr.aria-describedby]="
            labelError()
              ? 'wish-reward-label-hint wish-reward-label-error'
              : 'wish-reward-label-hint'
          "
          (input)="setLabel($any($event.target).value)"
          (blur)="labelTouched.set(true)"
        />
        <p id="wish-reward-label-hint" class="text-xs text-fg-muted">
          {{ 'wishes.settings.rewards.dialog.labelHint' | transloco }}
        </p>
        @if (labelError(); as key) {
          <p id="wish-reward-label-error" class="text-sm text-danger-fg">
            {{ key | transloco: { max: labelMax } }}
          </p>
        }
      </div>

      <div class="flex flex-col gap-1">
        <label for="wish-reward-duration" class="text-sm text-fg-secondary">
          {{ 'wishes.settings.rewards.dialog.duration' | transloco }}
        </label>
        <input
          id="wish-reward-duration"
          type="text"
          inputmode="numeric"
          class="app-input max-w-40"
          autocomplete="off"
          [value]="duration()"
          [readOnly]="saving()"
          [attr.aria-invalid]="durationError() ? 'true' : null"
          [attr.aria-describedby]="durationError() ? 'wish-reward-duration-error' : null"
          (input)="setDuration($any($event.target).value)"
        />
        @if (durationError(); as key) {
          <p id="wish-reward-duration-error" class="text-sm text-danger-fg">
            {{ key | transloco: { max: durationMax } }}
          </p>
        }
      </div>

      @if (errorKey(); as key) {
        <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
      }

      @if (blockedReason(); as reason) {
        <p dialog-actions id="wish-reward-blocked" class="mr-auto text-xs text-fg-muted">
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
        [attr.aria-describedby]="blockedReason() ? 'wish-reward-blocked' : null"
        (click)="submit()"
      >
        {{ 'wishes.settings.rewards.dialog.save' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class WishRewardDialog {
  protected readonly data = inject<WishRewardDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<WishRewardDialogResult | undefined>>(DialogRef);
  private readonly wishService = inject(WishService);
  private readonly languageService = inject(LanguageService);

  protected readonly labelMax = WISH_REWARD_LABEL_MAX_LENGTH;
  protected readonly durationMax = WISH_DURATION_DAYS_MAX;
  protected readonly titleKey =
    this.data.mode === 'confirm'
      ? 'wishes.settings.rewards.dialog.confirmTitle'
      : 'wishes.settings.rewards.dialog.editTitle';
  protected readonly candidate = this.data.mode === 'confirm' ? this.data.learning.candidate : null;

  protected readonly label = signal(this.data.mode === 'edit' ? this.data.reward.label : '');
  protected readonly duration = signal(
    String(
      this.data.mode === 'edit'
        ? this.data.reward.defaultDurationDays
        : this.data.defaultDurationDays,
    ),
  );
  /** The label field was left once: an empty label is named only after that, not on open. */
  protected readonly labelTouched = signal(false);
  protected readonly saving = signal(false);
  protected readonly errorKey = signal<string | null>(null);
  /** 409 `wish_learning_stale`: the shown candidate is gone, sending again cannot succeed. */
  protected readonly stale = signal(false);
  private readonly serverFieldError = signal<{ field: 'label' | 'duration'; key: string } | null>(
    null,
  );
  private readonly now = signal(new Date());

  private readonly labelResult = computed(() => validateRewardLabel(this.label()));
  private readonly durationResult = computed(() => parseWishDurationDays(this.duration()));

  protected readonly labelError = computed(() => {
    const server = this.serverFieldError();
    if (server?.field === 'label') {
      return server.key;
    }
    const result = this.labelResult();
    if (result.ok || (result.reason === 'empty' && !this.labelTouched())) {
      return null;
    }
    return `wishes.settings.rewards.dialog.labelInvalid.${result.reason}`;
  });

  protected readonly durationError = computed(() => {
    const server = this.serverFieldError();
    if (server?.field === 'duration') {
      return server.key;
    }
    const result = this.durationResult();
    return result.ok ? null : `wishes.dialog.durationInvalid.${result.reason}`;
  });

  /** The confirm session ran out by the dialog's clock: the server would refuse it anyway. */
  private readonly expired = computed(
    () =>
      this.data.mode === 'confirm' &&
      Date.parse(this.data.learning.expiresAtUtc) <= this.now().getTime(),
  );

  /** The first reason the dialog cannot send, as a translation key; `null` when it can. */
  protected readonly blockedReason = computed(() => {
    if (this.stale()) {
      return 'wishes.settings.rewards.dialog.blocked.stale';
    }
    if (this.expired()) {
      return 'wishes.settings.rewards.dialog.blocked.expired';
    }
    if (!this.labelResult().ok) {
      return 'wishes.settings.rewards.dialog.blocked.label';
    }
    if (!this.durationResult().ok) {
      return 'wishes.dialog.blocked.duration';
    }
    if (this.data.mode === 'edit' && this.editBody() === null) {
      return 'wishes.settings.rewards.dialog.blocked.unchanged';
    }
    return null;
  });

  private readonly editBody = computed(() => {
    const label = this.labelResult();
    const duration = this.durationResult();
    return this.data.mode === 'edit' && label.ok && duration.ok
      ? rewardEditBody(this.data.reward, label.label, duration.value)
      : null;
  });

  constructor() {
    if (this.data.mode === 'confirm') {
      const timer = setInterval(() => this.now.set(new Date()), CLOCK_TICK_MS);
      inject(DestroyRef).onDestroy(() => clearInterval(timer));
    }
  }

  protected setLabel(value: string): void {
    this.label.set(value);
    this.clearServerError('label');
  }

  protected setDuration(value: string): void {
    this.duration.set(value);
    this.clearServerError('duration');
  }

  protected formatTime(iso: string): string {
    return new Date(iso).toLocaleTimeString(toLocale(this.languageService.lang()), {
      timeStyle: 'short',
    });
  }

  protected submit(): void {
    const label = this.labelResult();
    const duration = this.durationResult();
    if (this.saving() || this.blockedReason() !== null || !label.ok || !duration.ok) {
      return;
    }
    const request =
      this.data.mode === 'confirm'
        ? this.wishService.confirmLearning(
            this.data.channelName,
            confirmRewardBody(this.data.learning, label.label, duration.value),
          )
        : this.wishService.updateReward(
            this.data.channelName,
            this.data.reward.id,
            this.editBody() ?? {},
          );
    this.errorKey.set(null);
    this.saving.set(true);
    // Not dismissible while the request is out: the section would never hear of a saved reward.
    this.dialogRef.disableClose = true;
    request.subscribe({
      next: (reward) => this.dialogRef.close({ kind: 'saved', reward }),
      error: (error: unknown) => this.onError(error),
    });
  }

  protected reload(): void {
    this.dialogRef.close({ kind: 'stale' });
  }

  private onError(error: unknown): void {
    this.saving.set(false);
    this.dialogRef.disableClose = false;
    if (!(error instanceof HttpErrorResponse)) {
      this.errorKey.set(GENERIC_ERROR_TRANSLATION_KEY);
      return;
    }
    switch (apiErrorCode(error)) {
      case 'wish_learning_stale':
        this.stale.set(true);
        return;
      case 'wish_reward_no_candidate':
      case 'wish_reward_not_found':
        this.dialogRef.close({ kind: 'gone', errorKey: wishErrorKey(error, 'action') });
        return;
      case 'wish_settings_restricted':
        this.dialogRef.close({ kind: 'restricted' });
        return;
      case 'invalid_request_body':
        // The only field the server reads freely is the label (the ids came from the server).
        this.serverFieldError.set({
          field: 'label',
          key: 'wishes.settings.rewards.dialog.labelInvalid.rejected',
        });
        return;
      case 'wish_duration_out_of_range':
        this.serverFieldError.set({
          field: 'duration',
          key: 'wishes.dialog.durationInvalid.out_of_range',
        });
        return;
      default:
        this.errorKey.set(wishErrorKey(error, 'action'));
    }
  }

  private clearServerError(field: 'label' | 'duration'): void {
    if (this.serverFieldError()?.field === field) {
      this.serverFieldError.set(null);
    }
    this.errorKey.set(null);
  }
}

export function openWishRewardDialog(
  dialog: Dialog,
  data: WishRewardDialogData,
): DialogRef<WishRewardDialogResult | undefined> {
  return openAppDialog<WishRewardDialogResult | undefined, WishRewardDialogData>(
    dialog,
    WishRewardDialog,
    { data },
  );
}
