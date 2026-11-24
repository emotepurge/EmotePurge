import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Observable } from 'rxjs';

import { AdminService } from '../../core/admin/admin.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { Button } from '../../shared/ui/button';
import { ConfirmDialogData, openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StateDot } from '../../shared/ui/state-dot';
import {
  TypedConfirmDialogData,
  openTypedConfirmDialog,
} from '../../shared/ui/typed-confirm-dialog';
import {
  BOT_TOKEN_DELETE_CONFIRM_TEXT,
  botTokenExpiryNotice,
  canSubmitBotToken,
} from './admin-bot-account-view';

/**
 * The wish bot's 7TV account (#487, spec K4): whether a token is stored and for how long, the kill
 * switch, and the bot's queue. The token is only ever typed in: the field is cleared the moment it
 * is sent (success or failure), nothing is stored in the component beyond the field's own value,
 * and no route returns the token, so there is nothing to show back. Text inputs are `type=password`
 * without autocomplete so a browser does not keep the value either.
 */
@Component({
  selector: 'app-admin-bot-account-page',
  imports: [Button, NoticeBanner, SkeletonRows, StateDot, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-6">
      <header class="flex flex-wrap items-center justify-between gap-3">
        <h2 class="text-lg font-semibold">{{ 'admin.botAccount.title' | transloco }}</h2>
        <button type="button" appButton="outline" [disabled]="isLoading()" (click)="reload()">
          {{ 'admin.botAccount.refresh' | transloco }}
        </button>
      </header>

      @if (errorMessage(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      }
      @if (saved()) {
        <app-notice-banner variant="info">{{
          'admin.botAccount.tokenSaved' | transloco
        }}</app-notice-banner>
      }

      @if (state(); as account) {
        @switch (expiryNotice()) {
          @case ('expired') {
            <app-notice-banner variant="error">{{
              'admin.botAccount.expiredNotice' | transloco
            }}</app-notice-banner>
          }
          @case ('soon') {
            <app-notice-banner variant="warning">{{
              'admin.botAccount.expiresSoonNotice' | transloco: { days: account.expiresInDays }
            }}</app-notice-banner>
          }
        }
        @if (account.killSwitch) {
          <app-notice-banner variant="warning">{{
            'admin.botAccount.killSwitchOnNotice' | transloco
          }}</app-notice-banner>
        }

        <section class="flex flex-col gap-3" aria-labelledby="bot-account-state">
          <h3 id="bot-account-state" class="text-base font-semibold">
            {{ 'admin.botAccount.stateHeading' | transloco }}
          </h3>
          <dl class="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-2 text-sm">
            <dt class="text-fg-muted">{{ 'admin.botAccount.status.label' | transloco }}</dt>
            <dd>
              <app-state-dot [tone]="account.status === 'valid' ? 'on' : 'off'">
                {{ 'admin.botAccount.status.' + account.status | transloco }}
              </app-state-dot>
            </dd>
            <dt class="text-fg-muted">{{ 'admin.botAccount.expires' | transloco }}</dt>
            <dd>{{ expiresText() }}</dd>
            <dt class="text-fg-muted">{{ 'admin.botAccount.botUserId' | transloco }}</dt>
            <dd class="break-all">{{ account.botSevenTvUserId ?? '–' }}</dd>
            <dt class="text-fg-muted">{{ 'admin.botAccount.lastVerified' | transloco }}</dt>
            <dd>{{ lastVerifiedText() }}</dd>
            @if (account.lastErrorCode) {
              <dt class="text-fg-muted">{{ 'admin.botAccount.lastError' | transloco }}</dt>
              <dd class="font-mono break-all">{{ account.lastErrorCode }}</dd>
            }
            @if (pausedText(); as paused) {
              <dt class="text-fg-muted">{{ 'admin.botAccount.pausedUntil' | transloco }}</dt>
              <dd>{{ paused }}</dd>
            }
          </dl>
        </section>

        <section class="flex flex-col gap-3" aria-labelledby="bot-account-token">
          <h3 id="bot-account-token" class="text-base font-semibold">
            {{ 'admin.botAccount.tokenHeading' | transloco }}
          </h3>
          <p class="text-sm text-fg-secondary">{{ 'admin.botAccount.tokenHelp' | transloco }}</p>
          @if (account.tokenPresent) {
            <p class="text-sm text-fg-secondary">
              {{ 'admin.botAccount.changedBotHint' | transloco }}
            </p>
          }
          <form class="flex flex-col gap-2" (submit)="submitToken($event)">
            <label class="text-sm text-fg-secondary" for="bot-account-token-input">
              {{ 'admin.botAccount.tokenLabel' | transloco }}
            </label>
            <input
              id="bot-account-token-input"
              type="password"
              class="app-input w-full"
              autocomplete="off"
              autocapitalize="off"
              autocorrect="off"
              spellcheck="false"
              [value]="tokenInput()"
              (input)="onTokenInput($event)"
            />
            <div class="flex flex-wrap gap-2">
              <button type="submit" appButton="primary" [disabled]="!canSubmit()">
                {{
                  (account.tokenPresent
                    ? 'admin.botAccount.replaceToken'
                    : 'admin.botAccount.setToken'
                  ) | transloco
                }}
              </button>
              @if (account.tokenPresent) {
                <button
                  type="button"
                  appButton="danger-quiet"
                  [disabled]="busy()"
                  (click)="confirmDeleteToken()"
                >
                  {{ 'admin.botAccount.deleteToken.button' | transloco }}
                </button>
              }
            </div>
          </form>
        </section>

        <section class="flex flex-col gap-3" aria-labelledby="bot-account-kill">
          <h3 id="bot-account-kill" class="text-base font-semibold">
            {{ 'admin.botAccount.killSwitch.heading' | transloco }}
          </h3>
          <p class="text-sm text-fg-secondary">
            {{ 'admin.botAccount.killSwitch.help' | transloco }}
          </p>
          <div class="flex flex-wrap items-center gap-3">
            <app-state-dot [tone]="account.killSwitch ? 'off' : 'on'">
              {{
                (account.killSwitch
                  ? 'admin.botAccount.killSwitch.stopped'
                  : 'admin.botAccount.killSwitch.running'
                ) | transloco
              }}
            </app-state-dot>
            <button
              type="button"
              [appButton]="account.killSwitch ? 'outline' : 'danger-quiet'"
              [disabled]="busy()"
              (click)="confirmKillSwitch(!account.killSwitch)"
            >
              {{
                (account.killSwitch
                  ? 'admin.botAccount.killSwitch.release'
                  : 'admin.botAccount.killSwitch.engage'
                ) | transloco
              }}
            </button>
          </div>
        </section>

        <section class="flex flex-col gap-3" aria-labelledby="bot-account-wishes">
          <h3 id="bot-account-wishes" class="text-base font-semibold">
            {{ 'admin.botAccount.wishes.heading' | transloco }}
          </h3>
          <dl class="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-2 text-sm">
            <dt class="text-fg-muted">{{ 'admin.botAccount.wishes.botChannels' | transloco }}</dt>
            <dd>{{ account.wishes.botChannels }}</dd>
            <dt class="text-fg-muted">
              {{ 'admin.botAccount.wishes.suspendedChannels' | transloco }}
            </dt>
            <dd>{{ account.wishes.suspendedChannels }}</dd>
            <dt class="text-fg-muted">{{ 'admin.botAccount.wishes.queueAdds' | transloco }}</dt>
            <dd>{{ account.wishes.queue.adds }}</dd>
            <dt class="text-fg-muted">{{ 'admin.botAccount.wishes.queueConfirms' | transloco }}</dt>
            <dd>{{ account.wishes.queue.confirms }}</dd>
            <dt class="text-fg-muted">
              {{ 'admin.botAccount.wishes.queueRemovals' | transloco }}
            </dt>
            <dd>{{ account.wishes.queue.removals }}</dd>
            <dt class="text-fg-muted">
              {{ 'admin.botAccount.wishes.openRepairChecks' | transloco }}
            </dt>
            <dd>{{ account.wishes.openRepairChecks }}</dd>
          </dl>
        </section>
      } @else if (isLoading()) {
        <app-skeleton-rows [count]="5" />
      }
    </div>
  `,
})
export class AdminBotAccountPage {
  private readonly adminService = inject(AdminService);
  private readonly dialog = inject(Dialog);
  private readonly languageService = inject(LanguageService);
  private readonly translocoService = inject(TranslocoService);

  private readonly stateResource = rxResource({
    stream: () => this.adminService.getBotAccount(),
  });

  protected readonly state = computed(() =>
    this.stateResource.hasValue() ? this.stateResource.value() : null,
  );
  protected readonly isLoading = computed(() => this.stateResource.isLoading());
  protected readonly expiryNotice = computed(() => {
    const state = this.state();
    return state ? botTokenExpiryNotice(state) : null;
  });

  /** The token field's value — the only place the token lives, emptied on every send. */
  protected readonly tokenInput = signal('');
  protected readonly busy = signal(false);
  protected readonly saved = signal(false);
  protected readonly canSubmit = computed(() => canSubmitBotToken(this.tokenInput(), this.busy()));

  // Separate from the resource's error so a failed action survives the reload after it.
  private readonly actionError = signal<string | null>(null);
  protected readonly errorMessage = computed(() => {
    const actionError = this.actionError();
    if (actionError) {
      return actionError;
    }
    const loadError = this.stateResource.error();
    return loadError instanceof HttpErrorResponse ? apiErrorTranslationKey(loadError) : null;
  });

  // Reads `lang()` so a language switch re-formats the dates.
  private readonly formatDate = computed(() => {
    const locale = toLocale(this.languageService.lang());
    return (iso: string) =>
      new Date(iso).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' });
  });

  protected readonly expiresText = computed(() => {
    const iso = this.state()?.expiresAtUtc;
    return iso ? this.formatDate()(iso) : '–';
  });
  protected readonly lastVerifiedText = computed(() => {
    const iso = this.state()?.lastVerifiedAtUtc;
    return iso ? this.formatDate()(iso) : '–';
  });
  protected readonly pausedText = computed(() => {
    const iso = this.state()?.pausedUntilUtc;
    return iso ? this.formatDate()(iso) : null;
  });

  protected reload(): void {
    this.stateResource.reload();
  }

  protected onTokenInput(event: Event): void {
    this.tokenInput.set((event.target as HTMLInputElement).value);
  }

  protected submitToken(event: Event): void {
    event.preventDefault();
    if (!this.canSubmit()) {
      return;
    }
    // Taken and cleared before the request: the field is empty whatever the answer is.
    const token = this.tokenInput().trim();
    this.tokenInput.set('');
    this.run(this.adminService.setBotAccountToken(token), true);
  }

  protected confirmDeleteToken(): void {
    const data: TypedConfirmDialogData = {
      title: this.translocoService.translate('admin.botAccount.deleteToken.title'),
      message: this.translocoService.translate('admin.botAccount.deleteToken.message'),
      requiredText: BOT_TOKEN_DELETE_CONFIRM_TEXT,
      inputLabel: this.translocoService.translate('admin.botAccount.deleteToken.inputLabel', {
        text: BOT_TOKEN_DELETE_CONFIRM_TEXT,
      }),
      confirmLabel: this.translocoService.translate('admin.botAccount.deleteToken.confirm'),
    };
    openTypedConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (confirmed) {
        this.run(this.adminService.deleteBotAccountToken(), false);
      }
    });
  }

  protected confirmKillSwitch(enabled: boolean): void {
    const key = enabled ? 'engage' : 'release';
    const data: ConfirmDialogData = {
      message: this.translocoService.translate(`admin.botAccount.killSwitch.${key}Message`),
      confirmLabel: this.translocoService.translate(`admin.botAccount.killSwitch.${key}Confirm`),
    };
    openConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (confirmed) {
        this.run(this.adminService.setBotAccountKillSwitch(enabled), false);
      }
    });
  }

  private run(request: Observable<void>, announceSaved: boolean): void {
    this.actionError.set(null);
    this.saved.set(false);
    this.busy.set(true);
    request.subscribe({
      next: () => {
        this.busy.set(false);
        this.saved.set(announceSaved);
        this.stateResource.reload();
      },
      error: (error: HttpErrorResponse) => {
        this.busy.set(false);
        this.actionError.set(apiErrorTranslationKey(error));
      },
    });
  }
}
