import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, input, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Observable } from 'rxjs';

import { ChannelService } from '../../core/channels/channel.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { PagedResult } from '../../core/models/paged-result.model';
import { listQueryState } from '../../core/routing/list-query-state';
import { VoteSessionSummary } from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import { Pager } from '../../shared/pagination/pager';
import { Button } from '../../shared/ui/button';
import { ConfirmDialogData, openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StateDot } from '../../shared/ui/state-dot';
import { VoteAudienceBadge } from '../../shared/voting/vote-audience-badge';

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
    RouterLink,
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
        // Reload rather than filter locally: a deleted row shifts the paging the same way a
        // created one used to.
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
