import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, Observable, of, tap, throwError } from 'rxjs';

import { apiErrorTranslationKey } from '../i18n/api-error';
import { ChannelService } from '../channels/channel.service';
import { SevenTvTokenService } from '../seven-tv/seven-tv-token.service';
import { AuthUser } from './auth.model';

export type LoginNotice = 'deletionUnconfirmed';

/**
 * Where an account deletion stands, owned here rather than by the account menu: the menu exists
 * once per page and is destroyed with it, while a request started from one page can finish after
 * the visitor has moved on. `failed` is a confirmed rejection (nothing was deleted); `unconfirmed`
 * means the outcome is unknown because the answer never (reliably) arrived.
 */
export type DeletionState =
  | { status: 'idle' }
  | { status: 'pending' }
  | { status: 'failed'; errorKey: string }
  | { status: 'unconfirmed' };

/**
 * Statuses that say nothing about whether the deletion committed: 0 is a dropped or aborted
 * connection, 502/503/504 come from a proxy in front of the API, which can answer them after the
 * API committed. A 500 is deliberately not here: it comes from the API itself, and every step after
 * the commit in the handler (Redis cleanup, token revocation) swallows its own failures, so an
 * unhandled exception can only precede the commit, which rolls back.
 */
const UNKNOWN_OUTCOME_STATUSES = new Set([0, 502, 503, 504]);

const RETURN_URL_STORAGE_KEY = 'ep_return_url';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  // Unrelated token (see seven-tv-token.service.ts), cleared here anyway as cheap hygiene —
  // no reason for a 7TV write-token to outlive the EmotePurge session that granted access to it.
  private readonly sevenTvTokenService = inject(SevenTvTokenService);
  // Same hygiene one step further: cached channel permissions describe what THIS session may do.
  // Today a login is always a full page load (login() sets window.location), so the cache could
  // not survive a user change anyway — but that is an accident of the OAuth redirect, not a
  // property of the cache, and an in-app login later would turn it into a real leak.
  private readonly channelService = inject(ChannelService);

  readonly currentUser = signal<AuthUser | null>(null);
  private readonly isLoaded = signal(false);
  private readonly loginNotice = signal<LoginNotice | null>(null);
  private readonly deletion = signal<DeletionState>({ status: 'idle' });

  /** Progress and outcome of the current account deletion; survives the account menu being destroyed. */
  readonly deletionState = this.deletion.asReadonly();

  /**
   * False until /api/auth/me has answered once, whichever way it answered. `currentUser()` alone
   * cannot express this: it is null both before the request and for a logged-out visitor, and the
   * account menu has to draw a different trigger for each — a gear appearing and then flipping to
   * an avatar is a visible swap in the middle of the header.
   */
  readonly isResolved = this.isLoaded.asReadonly();

  /** Fetches /api/auth/me once and caches the result in `currentUser` until logout/401. */
  ensureLoaded(): Observable<AuthUser | null> {
    if (this.isLoaded()) {
      return of(this.currentUser());
    }

    return this.http.get<AuthUser>('/api/auth/me').pipe(
      catchError(() => of(null)),
      tap((user) => {
        this.currentUser.set(user);
        this.isLoaded.set(true);
      }),
    );
  }

  /**
   * Full browser navigation, not an HttpClient call — Twitch OAuth needs a real redirect.
   * `returnUrl` is stashed so the visitor lands back where they started (e.g. a vote-session page
   * they were redirected away from by a guard) instead of the fixed post-login redirect the
   * backend always uses.
   */
  login(returnUrl?: string): void {
    if (returnUrl) {
      this.stashReturnUrl(returnUrl);
    }
    window.location.href = '/api/auth/twitch/login';
  }

  /** Stashes a URL to return to after login — used by route guards before redirecting to /login. */
  stashReturnUrl(returnUrl: string): void {
    sessionStorage.setItem(RETURN_URL_STORAGE_KEY, returnUrl);
  }

  /** Reads and clears the stashed return URL, if any — consumed once by AppShell after login. */
  consumeReturnUrl(): string | null {
    const returnUrl = sessionStorage.getItem(RETURN_URL_STORAGE_KEY);
    if (returnUrl) {
      sessionStorage.removeItem(RETURN_URL_STORAGE_KEY);
    }
    return returnUrl;
  }

  /**
   * A failed server-side logout (network error, 5xx) must not leave the client believing the
   * session is still active — that would strand a mod's own session open on a shared/streamer
   * machine. Both outcomes reset the same client-side state; only the request itself may fail.
   */
  logout(): void {
    this.http.post('/api/auth/logout', {}).subscribe({
      next: () => this.resetClientSession(),
      error: () => this.resetClientSession(),
    });
  }

  /**
   * Self-service account deletion (GDPR Art. 17). Unlike logout, the client state is reset only once
   * the server confirmed: a failed deletion leaves the account, the session and the user's data
   * exactly where they were, so the caller must be able to show the error and let them retry. The
   * landing page rather than /login afterwards — there is no account left to log in to, and /welcome
   * is the public page that explains what the app is.
   *
   * Only 204 and 410 confirm the account is gone (410 is the server's answer to a retry or
   * double submit once the user row has vanished, scoped to this route). A 401 is **not** inferred
   * to mean "deleted": it can equally mean the session was revoked (logout in another tab, admin
   * revoke), the cookie expired, or it predates session tracking — in all of those the account
   * still exists. It propagates like any other error; the caller decides how to tell the user. The
   * interceptor exempts `/api/auth/me` from its expiry handling, so the error reaches here.
   */
  deleteAccount(): Observable<void> {
    return this.http.delete<void>('/api/auth/me').pipe(
      catchError((error: unknown) =>
        error instanceof HttpErrorResponse && error.status === 410
          ? of(undefined as void)
          : throwError(() => error),
      ),
      tap(() => this.resetClientSession('/welcome')),
    );
  }

  /**
   * Runs the deletion and records its outcome in `deletionState`, so the result is not lost when
   * the account menu that started it is gone by the time the answer arrives. Success and 410 reset
   * the client (see deleteAccount); a 401 goes to the login page with a notice; a lost or
   * proxy-answered request (UNKNOWN_OUTCOME_STATUSES) is `unconfirmed` and leaves the session
   * alone — a reload settles it, because `/api/auth/me` answers 401 once the account is gone;
   * anything else is a confirmed rejection (`failed`).
   */
  startAccountDeletion(): void {
    if (this.deletion().status === 'pending') {
      return;
    }
    this.deletion.set({ status: 'pending' });
    this.deleteAccount().subscribe({
      error: (error: unknown) => {
        if (!(error instanceof HttpErrorResponse)) {
          this.deletion.set({ status: 'unconfirmed' });
        } else if (error.status === 401) {
          // Session already ended: nothing was deleted, and a 401 does not prove otherwise.
          this.handleDeletionUnconfirmed();
        } else if (UNKNOWN_OUTCOME_STATUSES.has(error.status)) {
          this.deletion.set({ status: 'unconfirmed' });
        } else {
          this.deletion.set({ status: 'failed', errorKey: apiErrorTranslationKey(error) });
        }
      },
    });
  }

  /** Clears a shown `failed`/`unconfirmed` outcome; a running request is left alone. */
  dismissDeletionOutcome(): void {
    if (this.deletion().status !== 'pending') {
      this.deletion.set({ status: 'idle' });
    }
  }

  /**
   * The deletion could not be confirmed because the session had already ended: resets the client
   * like an expired session and leaves a one-shot notice for the login page, which is where the
   * user lands — the account menu that asked is unmounted by the reset.
   */
  handleDeletionUnconfirmed(): void {
    this.loginNotice.set('deletionUnconfirmed');
    this.resetClientSession('/login');
  }

  /** Returns the pending login-page notice and clears it, so a reload or later visit shows none. */
  takeLoginNotice(): LoginNotice | null {
    const notice = this.loginNotice();
    this.loginNotice.set(null);
    return notice;
  }

  /** Called when a request 401s mid-session (cookie expired) — resets state and sends the user back to /login. */
  handleSessionExpired(): void {
    this.resetClientSession();
  }

  private resetClientSession(target = '/login'): void {
    this.deletion.set({ status: 'idle' });
    this.currentUser.set(null);
    this.isLoaded.set(true);
    this.sevenTvTokenService.clearToken();
    this.channelService.invalidatePermissions();
    // Fire-and-forget on purpose: nothing here depends on the navigation having finished, and a
    // rejected navigation (guard cancel) is not an error worth surfacing.
    void this.router.navigateByUrl(target);
  }
}
