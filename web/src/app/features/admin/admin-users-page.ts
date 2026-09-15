import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { AdminUser } from '../../core/admin/admin.model';
import { AdminService } from '../../core/admin/admin.service';
import { AuthService } from '../../core/auth/auth.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { PagedResult } from '../../core/models/paged-result.model';
import { listQueryState } from '../../core/routing/list-query-state';
import { Pager } from '../../shared/pagination/pager';
import { Button } from '../../shared/ui/button';
import { ConfirmDialogData, openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StateDot } from '../../shared/ui/state-dot';

const PAGE_SIZE = 25;

/** How long the "role cache cleared" hint stays on the row (same window as the channel page's
 *  resync hint — both confirm a request whose effect is invisible in the row itself). */
const ROLE_CACHE_FEEDBACK_MS = 4000;

const EMPTY_PAGE: PagedResult<AdminUser> = {
  items: [],
  page: 1,
  pageSize: PAGE_SIZE,
  totalCount: 0,
  totalPages: 0,
};

/**
 * Every user who ever logged in, with derived token status and one action: revoking their sessions
 * (a forced logout — the server also drops their stored Twitch tokens). The plain ConfirmDialog is
 * enough here, unlike the purge's typed confirmation: revoking is recoverable (the user just logs
 * in again), so the typed-name barrier would be ceremony without a matching risk.
 */
@Component({
  selector: 'app-admin-users-page',
  imports: [Button, EmptyState, NoticeBanner, Pager, SkeletonRows, StateDot, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-4">
      <!-- The heading doubles as the pager's scroll/focus target (§8.4): tabindex="-1" lets it take
           focus without becoming a tab stop, scroll-mt-24 clears the two sticky layers above it
           (shell header h-14 + admin tabs h-10, §8.5) so it does not land behind them. -->
      <header class="flex flex-wrap items-center justify-between gap-3">
        <h2 #resultsTop tabindex="-1" class="scroll-mt-24 text-lg font-semibold">
          {{ 'admin.users.title' | transloco }}
        </h2>
        <button
          type="button"
          appButton="outline"
          [disabled]="isLoading()"
          (click)="reload()"
          [title]="'admin.users.refreshTitle' | transloco"
        >
          {{ 'admin.users.refresh' | transloco }}
        </button>
      </header>

      @if (errorMessage(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      }

      @if (isLoading()) {
        <app-skeleton-rows [count]="5" />
      } @else if (rows().length === 0) {
        <app-empty-state [title]="'admin.users.empty' | transloco" />
      } @else {
        <!-- Hairline-divided rows, same recipe as the other lists. Nothing on a user row is
             clickable as a whole, so there is no hover wash either — it would promise a drilldown
             that does not exist. -->
        <ul class="-mx-3 divide-y divide-border border-y border-border">
          @for (row of rows(); track row.twitchUserId) {
            <li class="flex flex-col gap-2 px-3 py-3">
              <div class="flex flex-wrap items-center gap-x-3 gap-y-2">
                <span class="max-w-full truncate font-medium text-fg">
                  {{ row.displayName }}
                  @if (row.showLogin) {
                    <span class="font-normal text-fg-muted">({{ row.twitchUsername }})</span>
                  }
                </span>

                <div class="ml-auto flex flex-wrap items-center justify-end gap-3">
                  <!-- Whether we still hold a usable Twitch token for this user: the row's own
                       condition, and "connected" on twenty-five rows in a row, so a dot and a word
                       rather than a green pill per row. -->
                  <app-state-dot [tone]="row.hasRefreshToken ? 'on' : 'off'">
                    {{
                      (row.hasRefreshToken
                        ? 'admin.users.token.connected'
                        : 'admin.users.token.none'
                      ) | transloco
                    }}
                  </app-state-dot>
                  <!-- A role="status" region that enters the DOM together with its content
                       announces nothing to most screen reader/browser pairings — only a mutation
                       *inside* an already-mounted region is announced. So the sr-only region below
                       is permanent (one per row) and only its text comes and goes via @if; the
                       visible twin stays an @if (it must not occupy layout space when there is
                       nothing to say) and is aria-hidden so the message is not spoken twice
                       (docs/UI-Designsprache.md §4.5). -->
                  <span role="status" class="sr-only">
                    @if (roleCacheFeedback(); as feedback) {
                      @if (feedback.twitchUserId === row.twitchUserId) {
                        {{
                          'admin.users.roleCache.cleared'
                            | transloco: { count: feedback.removedEntries }
                        }}
                      }
                    }
                  </span>
                  @if (roleCacheFeedback(); as feedback) {
                    @if (feedback.twitchUserId === row.twitchUserId) {
                      <!-- Transient inline confirmation: dropping cache entries changes nothing this
                           row displays, so the removed count is the only observable outcome. -->
                      <span aria-hidden="true" class="text-xs text-success-fg">
                        {{
                          'admin.users.roleCache.cleared'
                            | transloco: { count: feedback.removedEntries }
                        }}
                      </span>
                    }
                  }
                  <div class="flex flex-wrap items-center gap-2">
                    <!-- Not destructive at all: nothing is lost, the next role check just resolves
                         live again — so no confirmation step either (design doc §4.2). -->
                    <button
                      type="button"
                      appButton="neutral"
                      [disabled]="pendingUserId() === row.twitchUserId"
                      (click)="invalidateRoleCache(row)"
                    >
                      {{ 'admin.users.roleCache.button' | transloco }}
                    </button>
                    <!-- Quiet destructive tier for the same reason as the channel list's purge: one
                         per row, twenty-five rows to a page. The confirmation dialog is unchanged. -->
                    <button
                      type="button"
                      appButton="danger-quiet"
                      [disabled]="pendingUserId() === row.twitchUserId"
                      (click)="confirmRevoke(row)"
                    >
                      {{ 'admin.users.revoke.button' | transloco }}
                    </button>
                  </div>
                </div>
              </div>

              <p class="flex flex-wrap gap-x-2 gap-y-1 text-xs text-fg-muted">
                <span>{{ 'admin.users.id' | transloco: { id: row.twitchUserId } }}</span>
                <span aria-hidden="true">·</span>
                <span>{{
                  'admin.users.lastLogin' | transloco: { date: row.lastLoginFormatted }
                }}</span>
                @if (row.tokenExpiresFormatted; as expires) {
                  <span aria-hidden="true">·</span>
                  <span>{{ 'admin.users.tokenExpires' | transloco: { date: expires } }}</span>
                }
                @if (row.sessionsRevokedFormatted; as revoked) {
                  <span aria-hidden="true">·</span>
                  <span>{{ 'admin.users.sessionsRevokedAt' | transloco: { date: revoked } }}</span>
                }
                @if (row.twitchTokenScopes; as scopes) {
                  <span aria-hidden="true">·</span>
                  <span class="break-all">{{ 'admin.users.scopes' | transloco: { scopes } }}</span>
                }
              </p>
            </li>
          }
        </ul>

        <app-pager
          [page]="page()"
          [totalPages]="totalPages()"
          [scrollTarget]="resultsTop"
          (pageChange)="onPageChange($event)"
        />
      }
    </div>
  `,
})
export class AdminUsersPage {
  private readonly adminService = inject(AdminService);
  private readonly authService = inject(AuthService);
  private readonly dialog = inject(Dialog);
  private readonly languageService = inject(LanguageService);
  private readonly translocoService = inject(TranslocoService);

  // Page in the URL, not in a plain signal: the rows carry links out of this list, and coming back
  // used to land on page 1 (core/routing).
  private readonly query = listQueryState();

  protected readonly page = this.query.page;

  private readonly usersResource = rxResource({
    params: () => ({ page: this.page() }),
    stream: ({ params }) => this.adminService.listUsers(params.page, PAGE_SIZE),
    defaultValue: EMPTY_PAGE,
  });

  protected readonly isLoading = computed(() => this.usersResource.isLoading());
  protected readonly totalPages = computed(() => this.usersResource.value().totalPages);

  /** Blocks a second click on the row an action is already running against — shared by both
   *  actions, so revoking and clearing the cache can never run against the same user at once. */
  protected readonly pendingUserId = signal<string | null>(null);

  /** The user whose role cache was just cleared plus how many entries went, or null. */
  protected readonly roleCacheFeedback = signal<{
    twitchUserId: string;
    removedEntries: number;
  } | null>(null);
  private roleCacheFeedbackTimeout?: ReturnType<typeof setTimeout>;

  // Separate from the resource's error so a failed action survives the reload after it (same
  // reasoning as admin-channels-page.ts).
  private readonly actionError = signal<string | null>(null);

  protected readonly errorMessage = computed(() => {
    const actionError = this.actionError();
    if (actionError) {
      return actionError;
    }
    const loadError = this.usersResource.error();
    return loadError instanceof HttpErrorResponse ? apiErrorTranslationKey(loadError) : null;
  });

  // Reads `lang()` so a language switch re-formats the dates (LOCALE_ID is bootstrap-static, same
  // as the other admin pages).
  protected readonly rows = computed(() => {
    const locale = toLocale(this.languageService.lang());
    const format = (iso: string) =>
      new Date(iso).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' });
    return this.usersResource.value().items.map((user) => ({
      ...user,
      // Hide the login when it is just the lowercased display name — that is Twitch's normal case,
      // and "HandOfBlood (handofblood)" twice per row is noise.
      showLogin: user.twitchUsername.toLowerCase() !== user.displayName.toLowerCase(),
      lastLoginFormatted: format(user.lastLogin),
      tokenExpiresFormatted: user.twitchAccessTokenExpiresAtUtc
        ? format(user.twitchAccessTokenExpiresAtUtc)
        : null,
      sessionsRevokedFormatted: user.sessionsValidFromUtc
        ? format(user.sessionsValidFromUtc)
        : null,
    }));
  });

  protected reload(): void {
    this.usersResource.reload();
  }

  protected onPageChange(newPage: number): void {
    this.query.goToPage(newPage);
  }

  /** No confirmation dialog on purpose: the cache is derived state that rebuilds itself on the next
   *  role check, so the worst outcome of a mis-click is one slower permission lookup. Also no list
   *  reload — nothing in the user row is fed by the role cache. */
  protected invalidateRoleCache(user: AdminUser): void {
    this.actionError.set(null);
    this.pendingUserId.set(user.twitchUserId);
    this.adminService.invalidateRoleCache(user.twitchUserId).subscribe({
      next: ({ removedEntries }) => {
        this.pendingUserId.set(null);
        clearTimeout(this.roleCacheFeedbackTimeout);
        this.roleCacheFeedback.set({ twitchUserId: user.twitchUserId, removedEntries });
        this.roleCacheFeedbackTimeout = setTimeout(
          () => this.roleCacheFeedback.set(null),
          ROLE_CACHE_FEEDBACK_MS,
        );
      },
      error: (error: HttpErrorResponse) => {
        this.pendingUserId.set(null);
        this.actionError.set(apiErrorTranslationKey(error));
      },
    });
  }

  protected confirmRevoke(user: AdminUser): void {
    let message = this.translocoService.translate('admin.users.revoke.message', {
      name: user.displayName,
    });
    // Revoking one's own sessions is allowed — it is a normal logout, everywhere — but the dialog
    // says so, because "the admin page just logged me out" reads as a bug without the hint.
    if (user.twitchUserId === this.authService.currentUser()?.twitchUserId) {
      message += '\n\n' + this.translocoService.translate('admin.users.revoke.selfHint');
    }

    const data: ConfirmDialogData = {
      message,
      confirmLabel: this.translocoService.translate('admin.users.revoke.confirm'),
    };

    openConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }
      this.actionError.set(null);
      this.pendingUserId.set(user.twitchUserId);
      this.adminService.revokeSessions(user.twitchUserId).subscribe({
        next: () => {
          this.pendingUserId.set(null);
          // Full reload: the row's sessionsValidFromUtc and token badge both changed server-side.
          this.usersResource.reload();
        },
        error: (error: HttpErrorResponse) => {
          this.pendingUserId.set(null);
          this.actionError.set(apiErrorTranslationKey(error));
        },
      });
    });
  }
}
