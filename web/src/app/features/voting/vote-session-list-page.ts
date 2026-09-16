import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, input, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  ReactiveFormsModule,
  ValidationErrors,
} from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Observable } from 'rxjs';

import { ChannelService } from '../../core/channels/channel.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { PagedResult } from '../../core/models/paged-result.model';
import { listQueryState } from '../../core/routing/list-query-state';
import { AllowedRoles, VoteSessionSummary } from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import { DateTimePicker } from '../../shared/datetime/datetime-picker';
import { Pager } from '../../shared/pagination/pager';
import { Button } from '../../shared/ui/button';
import { ConfirmDialogData, openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StateDot } from '../../shared/ui/state-dot';
import { VoteAudienceBadge } from '../../shared/voting/vote-audience-badge';

function requiredTrimmed(control: AbstractControl<string>): ValidationErrors | null {
  return control.value?.trim().length > 0 ? null : { required: true };
}

function toLocalDateTimeInputValue(date: Date): string {
  const pad = (n: number) => n.toString().padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

// Whole-set sessions get a 30-day usage window by default: "count from now" made every session
// start with all-zero usage context, which read as broken rather than as a fresh count. Midnight
// local time, mirroring the prefill of the usage-stats create dialog. Clearing the field still
// means "from now".
function defaultStartedAt(): string {
  const date = new Date();
  date.setDate(date.getDate() - 30);
  date.setHours(0, 0, 0, 0);
  return toLocalDateTimeInputValue(date);
}

const EMPTY_PAGE: PagedResult<VoteSessionSummary> = {
  items: [],
  page: 1,
  pageSize: 20,
  totalCount: 0,
  totalPages: 0,
};

@Component({
  selector: 'app-vote-session-list-page',
  imports: [
    Button,
    EmptyState,
    NoticeBanner,
    ReactiveFormsModule,
    RouterLink,
    DateTimePicker,
    Pager,
    SkeletonRows,
    StateDot,
    VoteAudienceBadge,
    TranslocoPipe,
  ],
  templateUrl: './vote-session-list-page.html',
})
export class VoteSessionListPage {
  readonly channelName = input.required<string>();

  private readonly voteSessionService = inject(VoteSessionService);
  private readonly channelService = inject(ChannelService);
  private readonly translocoService = inject(TranslocoService);
  private readonly dialog = inject(Dialog);

  // Page in the URL, not in a plain signal: every row links into a session, and the way back out of
  // one is the back button — which used to return to page 1 (core/routing).
  private readonly query = listQueryState();

  protected readonly page = this.query.page;

  /**
   * Pilot for the rxResource pattern (the other four loader `effect()`s follow once this holds up).
   * `params` reads `channelName()` and `page()`, so a change to either reloads — which is exactly what
   * the hand-written `effect(() => this.load())` did, minus its trap: `load()` reading a *new* signal
   * silently turned that signal into a reload trigger, and `onPageChange` calling `load()` on top of
   * the dirty effect fired every request twice.
   *
   * `params` also solves the reason the effect existed in the first place: it runs after Angular has
   * applied route inputs, so reading the required `channelName()` input cannot throw NG0950.
   */
  private readonly sessionsResource = rxResource({
    params: () => ({ channel: this.channelName(), page: this.page() }),
    stream: ({ params }) => this.voteSessionService.list(params.channel, params.page),
    defaultValue: EMPTY_PAGE,
  });

  // Was a probe: GET /api/channels/{c} read as 200 = can manage, 403 = plain voter. Now a field of the
  // dedicated /permissions response, which says so instead of implying it via a status code.
  private readonly permissionsResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => this.channelService.getPermissions(params),
  });

  protected readonly sessions = computed(() => this.sessionsResource.value().items);
  protected readonly totalPages = computed(() => this.sessionsResource.value().totalPages);
  protected readonly isLoading = computed(() => this.sessionsResource.isLoading());
  protected readonly canManage = computed(
    () => this.permissionsResource.value()?.canManage ?? false,
  );

  // Kept separate from the resource's own error so a failed create/end/delete does not get wiped out
  // by the next successful list reload, and vice versa. The action the user just took wins.
  private readonly actionError = signal<string | null>(null);

  protected readonly errorMessage = computed(() => {
    const actionError = this.actionError();
    if (actionError) {
      return actionError;
    }
    const loadError = this.sessionsResource.error();
    return loadError instanceof HttpErrorResponse ? apiErrorTranslationKey(loadError) : null;
  });

  protected readonly titleControl = new FormControl('', {
    nonNullable: true,
    validators: [requiredTrimmed],
  });
  protected readonly selectedAudience = signal<'everyone' | 'subs' | 'mods'>('everyone');
  protected readonly customStartedAt = signal(defaultStartedAt());
  protected readonly maxStartedAt = toLocalDateTimeInputValue(new Date());
  // Secret ballot, off by default and fixed once the session exists — see CreateVoteSessionRequest.
  protected readonly hideResultsUntilEnd = signal(false);

  protected readonly copyFeedback = signal<{
    sessionId: number;
    status: 'copied' | 'error';
  } | null>(null);
  private copyFeedbackTimeout?: ReturnType<typeof setTimeout>;

  protected onPageChange(newPage: number): void {
    // The navigation is the whole trigger — it moves `page()`, which `sessionsResource` reads in its
    // `params`. No scroll comes out of it either; the pager repositions to its own anchor (§8.4).
    this.query.goToPage(newPage);
  }

  protected createSession(): void {
    if (this.titleControl.invalid) {
      this.titleControl.markAsTouched();
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

    this.actionError.set(null);
    this.voteSessionService
      .create(this.channelName(), {
        title: this.titleControl.value.trim(),
        allowedVoterRoles: roles,
        startedAt,
        hideResultsUntilEnd: this.hideResultsUntilEnd(),
      })
      .subscribe({
        next: () => {
          // Reloads instead of prepending locally: a new session shifts the paging, and the old
          // optimistic prepend could push the visible page to pageSize + 1 rows or show the new session
          // on page 3 where it does not belong.
          this.sessionsResource.reload();
          this.titleControl.reset('');
          this.customStartedAt.set(defaultStartedAt());
          this.hideResultsUntilEnd.set(false);
        },
        error: (error: HttpErrorResponse) => this.handleError(error),
      });
  }

  // Confirmed for the same reason as deleteSession, and more urgently: the detail page already
  // asked before ending, while the list — where twenty rows sit one mis-click apart — did not, even
  // though ending cannot be undone either.
  protected endSession(session: VoteSessionSummary): void {
    this.confirm(
      this.translocoService.translate('voting.detail.endConfirm', { title: session.title }),
      this.translocoService.translate('voting.list.end'),
    ).subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }
      this.actionError.set(null);
      this.voteSessionService.end(this.channelName(), session.id).subscribe({
        // In-place field change on a row that is already visible — no paging effect, so patching the
        // loaded page locally is both correct and cheaper than a reload.
        next: (updated) =>
          this.sessionsResource.update((current) => ({
            ...current,
            items: current.items.map((item) => (item.id === updated.id ? updated : item)),
          })),
        error: (error: HttpErrorResponse) => this.handleError(error),
      });
    });
  }

  protected deleteSession(session: VoteSessionSummary): void {
    // Names the session in the confirmation dialog — with 20 sessions in the list, a mis-click one
    // row off had no way to notice before committing to an irreversible delete (all votes included).
    this.confirm(
      this.translocoService.translate('voting.list.deleteConfirm', { title: session.title }),
      this.translocoService.translate('voting.list.delete'),
    ).subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }
      this.actionError.set(null);
      this.voteSessionService.delete(this.channelName(), session.id).subscribe({
        // Reload rather than filter locally, for the same paging reason as createSession.
        next: () => this.sessionsResource.reload(),
        error: (error: HttpErrorResponse) => this.handleError(error),
      });
    });
  }

  protected copyShareLink(sessionId: number): void {
    const url = `${window.location.origin}/channels/${this.channelName()}/vote-sessions/${sessionId}`;
    const setFeedback = (status: 'copied' | 'error') => {
      clearTimeout(this.copyFeedbackTimeout);
      this.copyFeedback.set({ sessionId, status });
      this.copyFeedbackTimeout = setTimeout(() => this.copyFeedback.set(null), 2000);
    };

    if (!navigator.clipboard) {
      setFeedback('error');
      return;
    }
    navigator.clipboard.writeText(url).then(
      () => setFeedback('copied'),
      () => setFeedback('error'),
    );
  }

  // Both irreversible row actions open the same dialog; only the wording differs. Emits `undefined`
  // when the dialog was dismissed via Escape or backdrop, which callers treat as "not confirmed".
  private confirm(message: string, confirmLabel: string): Observable<boolean | undefined> {
    const data: ConfirmDialogData = { message, confirmLabel };
    return openConfirmDialog(this.dialog, data).closed;
  }

  // 401 is not handled here — apiAuthInterceptor resets the session and redirects for every
  // /api/ call in the app.
  private handleError(error: HttpErrorResponse): void {
    this.actionError.set(apiErrorTranslationKey(error));
  }
}
