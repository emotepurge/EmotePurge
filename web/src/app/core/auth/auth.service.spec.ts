import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';

import { AuthUser } from './auth.model';
import { AuthService } from './auth.service';

const USER: AuthUser = {
  twitchUserId: '123',
  login: 'sensitron',
  displayName: 'Sensitron',
  tokenExpiresAtUtc: '2026-07-28T00:00:00Z',
  isGlobalAdmin: false,
  profileImageUrl: 'https://static-cdn.jtvnw.net/jtv_user_pictures/abc-profile_image-70x70.png',
};

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => {
    httpMock.verify();
  });

  describe('ensureLoaded', () => {
    it('fetches /api/auth/me and caches the user', () => {
      let emitted: AuthUser | null | undefined;
      service.ensureLoaded().subscribe((user) => (emitted = user));

      const req = httpMock.expectOne('/api/auth/me');
      expect(req.request.method).toBe('GET');
      req.flush(USER);

      expect(emitted).toEqual(USER);
      expect(service.currentUser()).toEqual(USER);
    });

    it('does not issue a second request once loaded', () => {
      service.ensureLoaded().subscribe();
      httpMock.expectOne('/api/auth/me').flush(USER);

      let emitted: AuthUser | null | undefined;
      service.ensureLoaded().subscribe((user) => (emitted = user));

      httpMock.expectNone('/api/auth/me');
      expect(emitted).toEqual(USER);
    });

    it('resolves to null and caches that on a request error (e.g. 401)', () => {
      let emitted: AuthUser | null | undefined;
      service.ensureLoaded().subscribe((user) => (emitted = user));

      httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(emitted).toBeNull();
      expect(service.currentUser()).toBeNull();

      service.ensureLoaded().subscribe();
      httpMock.expectNone('/api/auth/me');
    });

    it('reports isResolved only once /api/auth/me has answered', () => {
      expect(service.isResolved()).toBe(false);

      service.ensureLoaded().subscribe();
      expect(service.isResolved()).toBe(false);

      httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

      // A logged-out visitor is a resolved state, not a pending one — the account menu draws a
      // different trigger for each and must not be able to confuse them.
      expect(service.isResolved()).toBe(true);
      expect(service.currentUser()).toBeNull();
    });
  });

  describe('login', () => {
    it('navigates the browser to the Twitch login endpoint', () => {
      const original = window.location;
      // window.location.href assignment can't be spied on directly in jsdom without a stub object.
      Object.defineProperty(window, 'location', {
        value: { ...original, href: '' },
        writable: true,
      });

      service.login();

      expect(window.location.href).toBe('/api/auth/twitch/login');
      Object.defineProperty(window, 'location', { value: original, writable: true });
    });

    it('stashes the return URL before redirecting when one is given', () => {
      const original = window.location;
      Object.defineProperty(window, 'location', {
        value: { ...original, href: '' },
        writable: true,
      });

      service.login('/channels/sensitron/vote-sessions/5');

      expect(sessionStorage.getItem('ep_return_url')).toBe('/channels/sensitron/vote-sessions/5');
      Object.defineProperty(window, 'location', { value: original, writable: true });
    });
  });

  describe('return URL stashing', () => {
    it('consumeReturnUrl returns and clears the stashed value', () => {
      service.stashReturnUrl('/my-votings');

      expect(service.consumeReturnUrl()).toBe('/my-votings');
      expect(service.consumeReturnUrl()).toBeNull();
    });

    it('consumeReturnUrl returns null when nothing was stashed', () => {
      expect(service.consumeReturnUrl()).toBeNull();
    });
  });

  describe('logout', () => {
    it('clears currentUser and navigates to /login after the server confirms', () => {
      service.currentUser.set(USER);
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

      service.logout();
      httpMock.expectOne('/api/auth/logout').flush({});

      expect(service.currentUser()).toBeNull();
      expect(navigateSpy).toHaveBeenCalledWith('/login');
    });

    it('still clears currentUser and navigates to /login when the server call fails', () => {
      service.currentUser.set(USER);
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

      service.logout();
      httpMock
        .expectOne('/api/auth/logout')
        .flush(null, { status: 500, statusText: 'Internal Server Error' });

      expect(service.currentUser()).toBeNull();
      expect(navigateSpy).toHaveBeenCalledWith('/login');
    });
  });

  describe('deleteAccount', () => {
    it('sends DELETE /api/auth/me, then clears the user and navigates to the landing page', () => {
      service.currentUser.set(USER);
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

      let completed = false;
      service.deleteAccount(USER.twitchUserId).subscribe({ complete: () => (completed = true) });
      const req = httpMock.expectOne('/api/auth/me?expectedTwitchUserId=123');
      expect(req.request.method).toBe('DELETE');
      expect(service.currentUser()).toEqual(USER); // nothing is reset before the server answers
      req.flush(null, { status: 204, statusText: 'No Content' });

      expect(completed).toBe(true);
      expect(service.currentUser()).toBeNull();
      expect(service.isResolved()).toBe(true);
      expect(navigateSpy).toHaveBeenCalledWith('/welcome');
    });

    it('treats 410 as confirmed gone: resets the session and navigates to the landing page', () => {
      service.currentUser.set(USER);
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

      let completed = false;
      let failed = false;
      service.deleteAccount(USER.twitchUserId).subscribe({
        complete: () => (completed = true),
        error: () => (failed = true),
      });
      httpMock
        .expectOne('/api/auth/me?expectedTwitchUserId=123')
        .flush(null, { status: 410, statusText: 'Gone' });

      expect(completed).toBe(true);
      expect(failed).toBe(false);
      expect(service.currentUser()).toBeNull();
      expect(navigateSpy).toHaveBeenCalledWith('/welcome');
    });

    it('propagates a 401 as an error: no reset, no navigation (a 401 does not prove deletion)', () => {
      service.currentUser.set(USER);
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

      let status: number | undefined;
      service
        .deleteAccount(USER.twitchUserId)
        .subscribe({ error: (error) => (status = error.status) });
      httpMock
        .expectOne('/api/auth/me?expectedTwitchUserId=123')
        .flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(status).toBe(401);
      expect(service.currentUser()).toEqual(USER);
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('keeps the session and does not navigate when the server fails, and surfaces the error', () => {
      service.currentUser.set(USER);
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

      let status: number | undefined;
      service
        .deleteAccount(USER.twitchUserId)
        .subscribe({ error: (error) => (status = error.status) });
      httpMock
        .expectOne('/api/auth/me?expectedTwitchUserId=123')
        .flush(null, { status: 500, statusText: 'Internal Server Error' });

      expect(status).toBe(500);
      expect(service.currentUser()).toEqual(USER);
      expect(navigateSpy).not.toHaveBeenCalled();
    });
  });

  describe('startAccountDeletion', () => {
    function start(): ReturnType<HttpTestingController['expectOne']> {
      service.currentUser.set(USER);
      vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
      service.startAccountDeletion(USER.twitchUserId);
      expect(service.deletionState()).toEqual({ status: 'pending' });
      return httpMock.expectOne('/api/auth/me?expectedTwitchUserId=123');
    }

    it.each([0, 500, 502, 503, 504, 520, 524])(
      'status %i is an unconfirmed outcome that keeps the session',
      (status) => {
        const req = start();
        req.error(new ProgressEvent('error'), { status, statusText: 'x' });

        expect(service.deletionState()).toEqual({ status: 'unconfirmed' });
        expect(service.currentUser()).toEqual(USER);
      },
    );

    it.each([400, 403, 404, 429])('status %i is a confirmed rejection', (status) => {
      start().flush(null, { status, statusText: 'x' });

      expect(service.deletionState().status).toBe('failed');
      expect(service.currentUser()).toEqual(USER);
    });

    it('binds the DELETE to the confirmed account id', () => {
      const req = start();
      expect(req.request.params.get('expectedTwitchUserId')).toBe(USER.twitchUserId);
    });

    it('409 account_mismatch reports the mismatch without switching accounts or re-reading /me', () => {
      start().flush({ errorCode: 'account_mismatch' }, { status: 409, statusText: 'Conflict' });

      expect(service.deletionState()).toEqual({ status: 'mismatch' });
      expect(service.currentUser()).toEqual(USER);
      httpMock.expectNone('/api/auth/me');
    });

    it('a mismatch that arrives after a session reset is a login-page notice and restores no user', () => {
      const req = start();
      service.handleSessionExpired();
      req.flush({ errorCode: 'account_mismatch' }, { status: 409, statusText: 'Conflict' });

      expect(service.currentUser()).toBeNull();
      expect(service.takeLoginNotice()).toBe('deletionSessionEnded');
      httpMock.expectNone('/api/auth/me');
    });

    it('a session reset by another request does not clear a pending deletion, and a later unknown outcome goes to the login page', () => {
      const req = start();
      service.handleSessionExpired(); // some other request 401'd meanwhile
      expect(service.deletionState()).toEqual({ status: 'pending' });

      req.error(new ProgressEvent('error'), { status: 0, statusText: '' });

      expect(service.deletionState()).toEqual({ status: 'idle' });
      expect(service.takeLoginNotice()).toBe('deletionUnknown');
    });

    it('a rejection that arrives with nobody signed in any more says the session had ended', () => {
      const req = start();
      service.handleSessionExpired();
      req.flush(null, { status: 403, statusText: 'Forbidden' });

      expect(service.takeLoginNotice()).toBe('deletionSessionEnded');
    });

    it('a deletion that succeeds after a concurrent session reset still ends idle', () => {
      const req = start();
      service.handleSessionExpired();
      req.flush(null, { status: 204, statusText: 'No Content' });

      expect(service.deletionState()).toEqual({ status: 'idle' });
      expect(service.takeLoginNotice()).toBeNull();
    });

    it('an unconfirmed outcome survives another request ending the session, as a login notice shown once', () => {
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
      start().error(new ProgressEvent('error'), { status: 0, statusText: '' });
      expect(service.deletionState()).toEqual({ status: 'unconfirmed' });

      service.handleSessionExpired(); // some other request 401'd

      expect(service.deletionState()).toEqual({ status: 'idle' });
      expect(navigateSpy).toHaveBeenLastCalledWith('/login');
      expect(service.takeLoginNotice()).toBe('deletionUnknown');
      expect(service.takeLoginNotice()).toBeNull();
    });

    it('a dismissed unconfirmed notice still reaches the login page when another request ends the session', () => {
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
      start().error(new ProgressEvent('error'), { status: 0, statusText: '' });
      service.dismissDeletionOutcome();
      expect(service.deletionState()).toEqual({ status: 'idle' });

      service.handleSessionExpired();

      expect(navigateSpy).toHaveBeenLastCalledWith('/login');
      expect(service.takeLoginNotice()).toBe('deletionUnknown');
    });

    it('a /me that finds the user signed in settles an unconfirmed deletion: a later expiry is a plain /login', () => {
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
      start().error(new ProgressEvent('error'), { status: 0, statusText: '' });
      service.dismissDeletionOutcome();
      // A fresh page would probe /me; here the cache is reset to let the service ask again.
      service['isLoaded'].set(false);
      service.ensureLoaded().subscribe();
      httpMock.expectOne('/api/auth/me').flush(USER);

      service.handleSessionExpired();

      expect(navigateSpy).toHaveBeenLastCalledWith('/login');
      expect(service.takeLoginNotice()).toBeNull();
    });

    it('a rejected deletion leaves no notice when the session ends afterwards', () => {
      start().flush(null, { status: 403, statusText: 'Forbidden' });
      service.handleSessionExpired();

      expect(service.takeLoginNotice()).toBeNull();
    });

    it('a late 401 from another request after a confirmed deletion does not leave /welcome', () => {
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
      start().flush(null, { status: 204, statusText: 'No Content' });
      navigateSpy.mockClear();

      service.handleSessionExpired();

      expect(navigateSpy).not.toHaveBeenCalled();
      expect(service.takeLoginNotice()).toBeNull();
    });

    it('a genuine expiry after a fresh sign-in still redirects to /login', () => {
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
      start().flush(null, { status: 204, statusText: 'No Content' });
      const original = window.location;
      Object.defineProperty(window, 'location', {
        value: { ...original, href: '' },
        writable: true,
      });
      service.login();
      Object.defineProperty(window, 'location', { value: original, writable: true });
      service.currentUser.set(USER);
      navigateSpy.mockClear();

      service.handleSessionExpired();

      expect(navigateSpy).toHaveBeenCalledWith('/login');
    });

    it('a retry that gets 401 after an unconfirmed attempt says unknown, never "nothing was deleted"', () => {
      start().error(new ProgressEvent('error'), { status: 0, statusText: '' });
      expect(service.deletionState()).toEqual({ status: 'unconfirmed' });

      service.startAccountDeletion(USER.twitchUserId);
      httpMock
        .expectOne('/api/auth/me?expectedTwitchUserId=123')
        .flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(service.takeLoginNotice()).toBe('deletionUnknown');
    });

    it('a rejected retry leaves the earlier unknown attempt unresolved for the next session reset', () => {
      start().error(new ProgressEvent('error'), { status: 0, statusText: '' });
      service.startAccountDeletion(USER.twitchUserId);
      httpMock
        .expectOne('/api/auth/me?expectedTwitchUserId=123')
        .flush(null, { status: 403, statusText: 'Forbidden' });
      expect(service.deletionState().status).toBe('failed');

      service.handleSessionExpired();

      expect(service.takeLoginNotice()).toBe('deletionUnknown');
    });

    it('a retry that is answered 204 resolves the earlier unknown attempt', () => {
      start().error(new ProgressEvent('error'), { status: 0, statusText: '' });
      service.startAccountDeletion(USER.twitchUserId);
      httpMock
        .expectOne('/api/auth/me?expectedTwitchUserId=123')
        .flush(null, { status: 204, statusText: 'No Content' });
      service.currentUser.set(USER); // a later sign-in
      service.handleSessionExpired();

      expect(service.takeLoginNotice()).toBeNull();
    });

    it('a session ended by another request while the deletion is pending leaves a pending notice and blocks sign-in', () => {
      const req = start();
      service.handleSessionExpired();

      expect(service.takeLoginNotice()).toBe('deletionPending');
      expect(service.deletionState()).toEqual({ status: 'pending' });
      const hrefBefore = window.location.href;
      service.login();
      expect(window.location.href).toBe(hrefBefore);
      req.error(new ProgressEvent('error'), { status: 0, statusText: '' });
    });

    it('the pending notice is replaced by the final outcome when the answer arrives', () => {
      let req = start();
      service.handleSessionExpired();
      req.error(new ProgressEvent('error'), { status: 0, statusText: '' });
      expect(service.deletionState()).toEqual({ status: 'idle' });
      expect(service.takeLoginNotice()).toBe('deletionUnknown');

      service.currentUser.set(USER);
      req = start();
      service.handleSessionExpired();
      req.flush(null, { status: 204, statusText: 'No Content' });
      expect(service.pendingLoginNotice()).toBeNull();
      expect(service.deletionState()).toEqual({ status: 'idle' });
    });

    it('401 resets to the login page with the one-shot notice and no lingering state', () => {
      start().flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(service.deletionState()).toEqual({ status: 'idle' });
      expect(service.currentUser()).toBeNull();
      expect(service.takeLoginNotice()).toBe('deletionSessionEnded');
    });

    it('204 resets the session and ends idle', () => {
      start().flush(null, { status: 204, statusText: 'No Content' });

      expect(service.deletionState()).toEqual({ status: 'idle' });
      expect(service.currentUser()).toBeNull();
    });

    it('ignores a second start while pending and can dismiss an outcome but not a running request', () => {
      const req = start();
      service.startAccountDeletion(USER.twitchUserId);
      service.dismissDeletionOutcome();
      expect(service.deletionState()).toEqual({ status: 'pending' });

      req.error(new ProgressEvent('error'), { status: 0, statusText: '' });
      service.dismissDeletionOutcome();
      expect(service.deletionState()).toEqual({ status: 'idle' });
    });
  });

  describe('handleDeletionSessionEnded', () => {
    it('resets the session, goes to /login and leaves a notice that is handed out exactly once', () => {
      service.currentUser.set(USER);
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

      service.handleDeletionSessionEnded();

      expect(service.currentUser()).toBeNull();
      expect(navigateSpy).toHaveBeenCalledWith('/login');
      expect(service.takeLoginNotice()).toBe('deletionSessionEnded');
      expect(service.takeLoginNotice()).toBeNull();
    });
  });

  describe('handleSessionExpired', () => {
    it('resets state and navigates to /login without a server call', () => {
      service.currentUser.set(USER);
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

      service.handleSessionExpired();

      expect(service.currentUser()).toBeNull();
      expect(navigateSpy).toHaveBeenCalledWith('/login');
      httpMock.expectNone('/api/auth/logout');
    });
  });
});
