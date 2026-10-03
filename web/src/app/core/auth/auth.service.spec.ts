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
      service.deleteAccount().subscribe({ complete: () => (completed = true) });
      const req = httpMock.expectOne('/api/auth/me');
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
      service.deleteAccount().subscribe({
        complete: () => (completed = true),
        error: () => (failed = true),
      });
      httpMock.expectOne('/api/auth/me').flush(null, { status: 410, statusText: 'Gone' });

      expect(completed).toBe(true);
      expect(failed).toBe(false);
      expect(service.currentUser()).toBeNull();
      expect(navigateSpy).toHaveBeenCalledWith('/welcome');
    });

    it('propagates a 401 as an error: no reset, no navigation (a 401 does not prove deletion)', () => {
      service.currentUser.set(USER);
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

      let status: number | undefined;
      service.deleteAccount().subscribe({ error: (error) => (status = error.status) });
      httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(status).toBe(401);
      expect(service.currentUser()).toEqual(USER);
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('keeps the session and does not navigate when the server fails, and surfaces the error', () => {
      service.currentUser.set(USER);
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

      let status: number | undefined;
      service.deleteAccount().subscribe({ error: (error) => (status = error.status) });
      httpMock
        .expectOne('/api/auth/me')
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
      service.startAccountDeletion();
      expect(service.deletionState()).toEqual({ status: 'pending' });
      return httpMock.expectOne('/api/auth/me');
    }

    it.each([0, 500, 502, 503, 504])(
      'status %i is an unconfirmed outcome that keeps the session',
      (status) => {
        const req = start();
        req.error(new ProgressEvent('error'), { status, statusText: 'x' });

        expect(service.deletionState()).toEqual({ status: 'unconfirmed' });
        expect(service.currentUser()).toEqual(USER);
      },
    );

    it.each([400, 403, 429])('status %i is a confirmed rejection', (status) => {
      start().flush(null, { status, statusText: 'x' });

      expect(service.deletionState().status).toBe('failed');
      expect(service.currentUser()).toEqual(USER);
    });

    it('401 resets to the login page with the one-shot notice and no lingering state', () => {
      start().flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(service.deletionState()).toEqual({ status: 'idle' });
      expect(service.currentUser()).toBeNull();
      expect(service.takeLoginNotice()).toBe('deletionUnconfirmed');
    });

    it('204 resets the session and ends idle', () => {
      start().flush(null, { status: 204, statusText: 'No Content' });

      expect(service.deletionState()).toEqual({ status: 'idle' });
      expect(service.currentUser()).toBeNull();
    });

    it('ignores a second start while pending and can dismiss an outcome but not a running request', () => {
      const req = start();
      service.startAccountDeletion();
      service.dismissDeletionOutcome();
      expect(service.deletionState()).toEqual({ status: 'pending' });

      req.error(new ProgressEvent('error'), { status: 0, statusText: '' });
      service.dismissDeletionOutcome();
      expect(service.deletionState()).toEqual({ status: 'idle' });
    });
  });

  describe('handleDeletionUnconfirmed', () => {
    it('resets the session, goes to /login and leaves a notice that is handed out exactly once', () => {
      service.currentUser.set(USER);
      const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

      service.handleDeletionUnconfirmed();

      expect(service.currentUser()).toBeNull();
      expect(navigateSpy).toHaveBeenCalledWith('/login');
      expect(service.takeLoginNotice()).toBe('deletionUnconfirmed');
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
