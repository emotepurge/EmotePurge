import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, Signal, computed, inject, signal } from '@angular/core';
import {
  AbstractControl,
  FormControl,
  ReactiveFormsModule,
  ValidationErrors,
} from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';

import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { pluralKey } from '../../core/i18n/plural';
import { AllowedRoles, VoteSessionSummary } from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import { DateTimePicker } from '../../shared/datetime/datetime-picker';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';
import { NoticeBanner } from '../../shared/ui/notice-banner';

// Whitespace-only titles count as empty.
function requiredTrimmed(control: AbstractControl<string>): ValidationErrors | null {
  return control.value?.trim().length > 0 ? null : { required: true };
}

function toLocalDateTimeInputValue(date: Date): string {
  const pad = (n: number) => n.toString().padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

export interface CreateVoteSessionDialogData {
  channelName: string;
  // The LIVE selection, not a snapshot (#132) — the host page keeps reloading while this dialog is
  // open (a silent usage.flushed/channel.synced reload can prune an emote that got archived from
  // outside the tab), and a frozen array would keep offering ids the backend's all-or-nothing check
  // (VoteSessionService.CreateAsync) would reject with emote_ids_invalid. Passed as a signal the page
  // derives from its selection, so a shrink is visible to the dialog the instant it happens. The
  // values are `Emote.Id` Guids: the page's grid is keyed by 7TV id since spec #200 (7.2), and it
  // resolves those keys into Guids whenever this signal is read — which, for `create()`, is the
  // moment of submitting (E4: a null session keeps speaking Guids).
  emoteIds: Signal<readonly string[]>;
  // ISO date (YYYY-MM-DD): the from-date of the range filter active when the dialog was opened.
  // Prefills the "count usage from" picker so the session's usage figures cover the same window
  // as the numbers that informed the selection.
  usageFromDate: string;
  /**
   * Why the host page no longer allows creating a session from this selection, as a translation
   * key, or `null` while it does — LIVE, like `emoteIds`, because the dialog outlives the moment its
   * button was enabled: a set switch started behind it (the usage page's `voteLockReasonKey`) must
   * block the submit, with the reason shown next to it, rather than create a session over the active
   * set while another set is chosen. Optional: a host without such a lock simply omits it.
   */
  lockReasonKey?: Signal<string | null>;
}

/**
 * "Zur Abstimmung stellen" from a usage-stats selection: the same four fields every voting session
 * is created with (title, audience, count-from date, hide-results toggle) — assembled here from
 * existing primitives rather than as a separate form component — plus the fixed ballot from the
 * selection. This is the only surface that creates a voting session (2026-09-19; the voting list's
 * own inline form is gone). Closes with the created session on success.
 */
@Component({
  selector: 'app-create-vote-session-dialog',
  imports: [Button, DateTimePicker, DialogShell, NoticeBanner, ReactiveFormsModule, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="'voting.create.dialogTitle' | transloco">
      <p class="text-sm text-fg-muted">
        {{ 'voting.create.selectedCount' | transloco: { count: currentCount() } }}
      </p>
      <!-- Live announcement for a selection shrink while this dialog is open (#132) — same
           two-element split as UsageStatsPage's own pruned-selection notice (usage-stats-page.html)
           and for the same reason: a live region that only exists together with its content is not
           announced by most screen reader/browser pairings, which only announce a *mutation inside*
           an already-mounted region. The sr-only span is therefore permanent and only its text comes
           and goes; the visible banner stays an @if (it must not occupy layout space when there is
           nothing to say) and is aria-hidden so nothing is spoken twice. -->
      <span role="status" class="sr-only">
        @if (shrinkNoticeKey(); as key) {
          {{ key | transloco: { count: removedCount(), remaining: currentCount() } }}
        }
      </span>
      @if (shrinkNoticeKey(); as key) {
        <app-notice-banner variant="warning" aria-hidden="true">
          {{ key | transloco: { count: removedCount(), remaining: currentCount() } }}
        </app-notice-banner>
      }
      <input
        id="create-vote-session-title-input"
        type="text"
        [formControl]="titleControl"
        [placeholder]="'voting.list.titlePlaceholder' | transloco"
        [attr.aria-label]="'voting.list.titlePlaceholder' | transloco"
        [attr.aria-invalid]="titleControl.invalid && titleControl.touched ? 'true' : null"
        [attr.aria-describedby]="
          titleControl.invalid && titleControl.touched ? 'create-vote-session-title-error' : null
        "
        class="app-input"
      />
      @if (titleControl.invalid && titleControl.touched) {
        <p id="create-vote-session-title-error" class="text-sm text-danger-fg">
          {{ 'voting.list.titleRequired' | transloco }}
        </p>
      }
      <div class="flex flex-wrap gap-4 text-sm text-fg-secondary">
        <label class="flex items-center gap-2 py-1">
          <input
            type="radio"
            class="h-4 w-4 accent-accent-solid"
            name="dialog-audience"
            [checked]="selectedAudience() === 'everyone'"
            (change)="selectedAudience.set('everyone')"
          />
          {{ 'voting.list.audienceEveryone' | transloco }}
        </label>
        <label class="flex items-center gap-2 py-1">
          <input
            type="radio"
            class="h-4 w-4 accent-accent-solid"
            name="dialog-audience"
            [checked]="selectedAudience() === 'subs'"
            (change)="selectedAudience.set('subs')"
          />
          {{ 'voting.list.audienceSubs' | transloco }}
        </label>
        <label class="flex items-center gap-2 py-1">
          <input
            type="radio"
            class="h-4 w-4 accent-accent-solid"
            name="dialog-audience"
            [checked]="selectedAudience() === 'mods'"
            (change)="selectedAudience.set('mods')"
          />
          {{ 'voting.list.audienceMods' | transloco }}
        </label>
      </div>
      <label class="flex flex-col gap-1 text-sm text-fg-secondary">
        {{ 'voting.list.startCountingLabel' | transloco }}
        <app-datetime-picker [(value)]="customStartedAt" [max]="maxStartedAt" />
        <span class="text-xs text-fg-muted">
          {{ 'voting.create.startPrefillHint' | transloco }}
        </span>
      </label>
      <div class="flex flex-col gap-1 text-sm text-fg-secondary">
        <label class="flex items-center gap-2 py-1">
          <input
            type="checkbox"
            class="h-4 w-4 accent-accent-solid"
            [checked]="hideResultsUntilEnd()"
            (change)="hideResultsUntilEnd.set($any($event.target).checked)"
            aria-describedby="create-vote-session-hide-results-hint"
          />
          {{ 'voting.list.hideResultsLabel' | transloco }}
        </label>
        <span id="create-vote-session-hide-results-hint" class="text-xs text-fg-muted">
          {{ 'voting.list.hideResultsHint' | transloco }}
        </span>
      </div>
      @if (errorMessage(); as message) {
        <app-notice-banner variant="error">{{ message | transloco }}</app-notice-banner>
      }
      <!-- The reason submit is blocked, in text next to it (docs/UI-Designsprache.md §7) — same
           pattern as TypedConfirmDialog's mr-auto hint, connected to the button via
           aria-describedby instead of leaving the greyed-out state as the only signal. -->
      @if (blockedReasonKey(); as reasonKey) {
        <p
          dialog-actions
          id="create-vote-session-blocked-hint"
          class="mr-auto text-xs text-fg-muted"
        >
          {{ reasonKey | transloco }}
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
        appButton="primary"
        buttonSize="lg"
        [disabled]="isSubmitting() || blockedReasonKey() !== null"
        [attr.aria-describedby]="
          blockedReasonKey() !== null ? 'create-vote-session-blocked-hint' : null
        "
        (click)="create()"
      >
        {{ 'voting.create.submit' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class CreateVoteSessionDialog {
  protected readonly data = inject<CreateVoteSessionDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<VoteSessionSummary | undefined>>(DialogRef);
  private readonly voteSessionService = inject(VoteSessionService);

  protected readonly titleControl = new FormControl('', {
    nonNullable: true,
    validators: [requiredTrimmed],
  });
  protected readonly selectedAudience = signal<'everyone' | 'subs' | 'mods'>('everyone');
  // Prefilled with the start of the range the creator was just filtering on (midnight local time);
  // clearing it falls back to "count from now" (empty startedAt), same as VoteSessionCreateRequest's
  // own default.
  protected readonly customStartedAt = signal(`${this.data.usageFromDate}T00:00`);
  protected readonly maxStartedAt = toLocalDateTimeInputValue(new Date());
  // Secret ballot, off by default and fixed once the session exists — see CreateVoteSessionRequest.
  protected readonly hideResultsUntilEnd = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly isSubmitting = signal(false);

  // Captured once, at construction — the count the ballot had when the dialog opened. Comparing the
  // live count against this fixed baseline (not against the previous live count) is what makes
  // removedCount monotonic: it only ever grows as more of the original ballot disappears, and a
  // partial recovery (an emote un-archived again) does not make the notice's own numbers wobble.
  private readonly initialCount = this.data.emoteIds().length;
  protected readonly currentCount = computed(() => this.data.emoteIds().length);
  protected readonly removedCount = computed(() =>
    Math.max(this.initialCount - this.currentCount(), 0),
  );

  // null once nothing has left the ballot since open time. The zero-remaining case gets its own,
  // non-pluralized key (voting.create.selectionEmpty) rather than reusing selectionShrunk with
  // remaining: 0 — "0 remain" reads as a stray number where "nothing left to vote on" is the point,
  // and it is also the sentence that has to explain why submit is now disabled.
  protected readonly shrinkNoticeKey = computed(() => {
    if (this.removedCount() === 0) {
      return null;
    }
    return this.currentCount() === 0
      ? 'voting.create.selectionEmpty'
      : pluralKey(this.removedCount(), 'voting.create.selectionShrunk');
  });

  /** Why submit is blocked right now, or `null`: the host's own lock first (it names the bigger
   *  problem — the whole view moved), then an emptied ballot. */
  protected readonly blockedReasonKey = computed(() => {
    const hostLock = this.data.lockReasonKey?.() ?? null;
    if (hostLock !== null) {
      return hostLock;
    }
    return this.currentCount() === 0 ? 'voting.create.selectionEmpty' : null;
  });

  protected create(): void {
    // Re-checked here, not only through the disabled button: this is the last moment before the
    // session is created, and the host's lock can arrive between render and click.
    if (this.blockedReasonKey() !== null) {
      return;
    }
    if (this.titleControl.invalid) {
      this.titleControl.markAsTouched();
      return;
    }

    // The CURRENT live list, not a value captured earlier — a retry after the backend's
    // emote_ids_invalid backstop (the remaining race this closes, not removes) must send whatever
    // the ballot looks like right now, not repeat the same rejected array.
    const emoteIds = this.data.emoteIds();
    if (emoteIds.length === 0) {
      return;
    }

    const audience = this.selectedAudience();
    let roles: AllowedRoles;
    if (audience === 'subs') {
      roles = AllowedRoles.Subs;
    } else if (audience === 'mods') {
      roles = AllowedRoles.Mods | AllowedRoles.Broadcaster;
    } else {
      roles = AllowedRoles.Everyone;
    }
    const startedAtLocal = this.customStartedAt();
    const startedAt = startedAtLocal ? new Date(startedAtLocal).toISOString() : undefined;

    this.errorMessage.set(null);
    this.isSubmitting.set(true);
    this.voteSessionService
      .create(this.data.channelName, {
        title: this.titleControl.value.trim(),
        allowedVoterRoles: roles,
        startedAt,
        emoteIds: [...emoteIds],
        hideResultsUntilEnd: this.hideResultsUntilEnd(),
      })
      .subscribe({
        next: (session) => this.dialogRef.close(session),
        error: (error: HttpErrorResponse) => {
          this.isSubmitting.set(false);
          this.errorMessage.set(apiErrorTranslationKey(error));
        },
      });
  }
}

export function openCreateVoteSessionDialog(
  dialog: Dialog,
  data: CreateVoteSessionDialogData,
): DialogRef<VoteSessionSummary | undefined> {
  return openAppDialog<VoteSessionSummary | undefined, CreateVoteSessionDialogData>(
    dialog,
    CreateVoteSessionDialog,
    { data },
  );
}
