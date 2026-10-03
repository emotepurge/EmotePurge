import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, Observable, of, tap, throwError } from 'rxjs';

import { ChannelService } from '../channels/channel.service';
import { SevenTvTokenService } from '../seven-tv/seven-tv-token.service';
import { AuthUser } from './auth.model';

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
   * A 401 counts as success: once the user row is gone the cookie scheme rejects the session before
   * the handler runs, so a retry, a double submit or a concurrent admin/retention deletion answers
   * 401 instead of 204. The interceptor exempts `/api/auth/me` from its expiry handling, so the
   * error reaches this method — and "not signed in any more" is exactly the state the user asked for.
   */
  deleteAccount(): Observable<void> {
    return this.http.delete<void>('/api/auth/me').pipe(
      catchError((error: unknown) =>
        error instanceof HttpErrorResponse && error.status === 401
          ? of(undefined as void)
          : throwError(() => error),
      ),
      tap(() => this.resetClientSession('/welcome')),
    );
  }

  /** Called when a request 401s mid-session (cookie expired) — resets state and sends the user back to /login. */
  handleSessionExpired(): void {
    this.resetClientSession();
  }

  private resetClientSession(target = '/login'): void {
    this.currentUser.set(null);
    this.isLoaded.set(true);
    this.sevenTvTokenService.clearToken();
    this.channelService.invalidatePermissions();
    // Fire-and-forget on purpose: nothing here depends on the navigation having finished, and a
    // rejected navigation (guard cancel) is not an error worth surfacing.
    void this.router.navigateByUrl(target);
  }
}
