import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, Observable, of, tap, throwError } from 'rxjs';

import { apiErrorTranslationKey } from '../i18n/api-error';
import { ChannelService } from '../channels/channel.service';
import { SevenTvTokenService } from '../seven-tv/seven-tv-token.service';
import { AuthUser } from './auth.model';

export type LoginNotice = 'deletionSessionEnded' | 'deletionUnknown';

/**
 * Where an account deletion stands, owned here rather than by the account menu: the menu exists
 * once per page and is destroyed with it, while a request started from one page can finish after
 * the visitor has moved on. `failed` is a confirmed rejection (nothing was deleted); `unconfirmed`
 * means the outcome is unknown because the answer never (reliably) arrived; `mismatch` means the
 * session belongs to another account than the one the user confirmed (another tab signed in as
 * someone else) and nothing was deleted.
 */
export type DeletionState =
  | { status: 'idle' }
  | { status: 'pending' }
  | { status: 'failed'; errorKey: string }
  | { status: 'unconfirmed' }
  | { status: 'mismatch' };

/**
 * Whether a status says nothing about the deletion having committed: 0 is a dropped or aborted
 * connection, and every 5xx can follow a commit — the API's own 500 (an uncertain commit: the
 * connection drops before the database acknowledges it, `CommitAsync` throws although the
 * transaction went through) as much as a proxy's 502/503/504 or a CDN's 520–527. Only a 4xx other
 * than 401/410 is a confirmed rejection.
 */
function isUnknownOutcome(status: number): boolean {
  return status === 0 || status >= 500;
}

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

  /** A notice waiting for the login page — read by it, consumed through takeLoginNotice(). */
  readonly pendingLoginNotice = this.loginNotice.asReadonly();

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
  deleteAccount(expectedTwitchUserId: string): Observable<void> {
    return this.http.delete<void>('/api/auth/me', { params: { expectedTwitchUserId } }).pipe(
      catchError((error: unknown) =>
        error instanceof HttpErrorResponse && error.status === 410
          ? of(undefined as void)
          : throwError(() => error),
      ),
      tap(() => this.resetClientSession('/welcome')),
    );
  }

  /**
   * Runs the deletion of the account the user confirmed (`expectedTwitchUserId` is that account's
   * immutable id, captured when the dialog opened — the server refuses when the session belongs to
   * someone else by now) and records the outcome in `deletionState`, so it is not lost when the
   * account menu that started it is gone by the time the answer arrives. Success and 410 reset the
   * client (see deleteAccount); 401 goes to the login page with a notice; a lost answer or any 5xx
   * is `unconfirmed` and leaves the session alone; a mismatch refreshes the cached account and says
   * so; any other 4xx is a confirmed rejection (`failed`). A session reset by some other request
   * while this one is pending does not clear it, and an outcome that arrives with nobody signed in
   * any more cannot be shown by the menu — it goes to the login page as a notice instead.
   */
  startAccountDeletion(expectedTwitchUserId: string): void {
    if (this.deletion().status === 'pending') {
      return;
    }
    this.deletion.set({ status: 'pending' });
    this.deleteAccount(expectedTwitchUserId).subscribe({
      complete: () => this.deletion.set({ status: 'idle' }),
      error: (error: unknown) => this.settleFailedDeletion(error),
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
  handleDeletionSessionEnded(): void {
    this.deletion.set({ status: 'idle' });
    this.loginNotice.set('deletionSessionEnded');
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

  private settleFailedDeletion(error: unknown): void {
    const status = error instanceof HttpErrorResponse ? error.status : 0;
    if (status === 401) {
      this.handleDeletionSessionEnded();
      return;
    }
    const mismatch =
      error instanceof HttpErrorResponse &&
      status === 409 &&
      (error.error as { errorCode?: string } | null)?.errorCode === 'account_mismatch';
    const unknown = isUnknownOutcome(status);

    if (!this.currentUser()) {
      // Nobody is signed in any more (a concurrent 401 reset the client), so the menu is not there
      // to say it. An unknown outcome gets its own notice; a rejection says the session had ended.
      this.deletion.set({ status: 'idle' });
      this.loginNotice.set(unknown ? 'deletionUnknown' : 'deletionSessionEnded');
      return;
    }
    if (unknown) {
      this.deletion.set({ status: 'unconfirmed' });
    } else if (mismatch) {
      this.refreshAfterMismatch();
    } else {
      this.deletion.set({
        status: 'failed',
        errorKey: apiErrorTranslationKey(error as HttpErrorResponse),
      });
    }
  }

  /** The cached account is not the session's: reload it, so the menu shows who is really signed in. */
  private refreshAfterMismatch(): void {
    this.http
      .get<AuthUser>('/api/auth/me')
      .pipe(catchError(() => of(null)))
      .subscribe((user) => {
        if (user) {
          this.currentUser.set(user);
          this.deletion.set({ status: 'mismatch' });
        } else {
          this.handleDeletionSessionEnded();
        }
      });
  }

  private resetClientSession(target = '/login'): void {
    // A running deletion keeps its state: it settles it (or its outcome is routed to the login page).
    this.currentUser.set(null);
    this.isLoaded.set(true);
    this.sevenTvTokenService.clearToken();
    this.channelService.invalidatePermissions();
    // Fire-and-forget on purpose: nothing here depends on the navigation having finished, and a
    // rejected navigation (guard cancel) is not an error worth surfacing.
    void this.router.navigateByUrl(target);
  }
}
