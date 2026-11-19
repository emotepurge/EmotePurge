import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { convertToParamMap, provideRouter, Router, UrlTree } from '@angular/router';
import { firstValueFrom, Observable } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { channelWishesGuard } from './channel-wishes.guard';
import { ChannelPermissions } from './channel.model';
import { AuthService } from '../auth/auth.service';

const USER = {
  twitchUserId: '1',
  login: 'sensitron',
  displayName: 'Sensitron',
  tokenExpiresAtUtc: '2026-07-28T00:00:00Z',
};

const PERMISSIONS: ChannelPermissions = {
  canManage: true,
  canViewUsageStats: true,
  isGlobalAdmin: false,
  isTracked: true,
  isBotActive: true,
  tagRunsEnabled: false,
  canPurgeAsBroadcaster: false,
  chatLogBackfillEnabled: false,
  wishesEnabled: false,
  wishCaptureEnabled: false,
};

describe('channelWishesGuard', () => {
  let httpMock: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    TestBed.inject(AuthService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  function runGuard(
    channelName: string | null,
    url = '/channels/sensitron/wishes',
  ): Observable<boolean | UrlTree> {
    return TestBed.runInInjectionContext(() =>
      channelWishesGuard(
        { paramMap: convertToParamMap(channelName ? { channelName } : {}) } as never,
        { url } as never,
      ),
    ) as Observable<boolean | UrlTree>;
  }

  it('redirects to "/" when the route has no channelName', async () => {
    const result = await firstValueFrom(runGuard(null));

    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/');
    httpMock.expectNone(() => true);
  });

  it('stashes the URL and redirects to /login when logged out', async () => {
    const resultPromise = firstValueFrom(runGuard('sensitron'));

    httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    const result = await resultPromise;
    expect(router.serializeUrl(result as UrlTree)).toBe('/login');
    expect(sessionStorage.getItem('ep_return_url')).toBe('/channels/sensitron/wishes');
  });

  // The whole matrix: only a manager with the switch on gets in; the fallback is the vote list.
  it.each([
    { canManage: true, wishesEnabled: true, allowed: true },
    { canManage: true, wishesEnabled: false, allowed: false },
    { canManage: false, wishesEnabled: true, allowed: false },
    { canManage: false, wishesEnabled: false, allowed: false },
  ])('manage=$canManage, switch=$wishesEnabled -> allowed=$allowed', async (c) => {
    const resultPromise = firstValueFrom(runGuard('sensitron'));

    httpMock.expectOne('/api/auth/me').flush(USER);
    httpMock
      .expectOne('/api/channels/sensitron/permissions')
      .flush({ ...PERMISSIONS, canManage: c.canManage, wishesEnabled: c.wishesEnabled });

    const result = await resultPromise;
    if (c.allowed) {
      expect(result).toBe(true);
    } else {
      expect(router.serializeUrl(result as UrlTree)).toBe('/channels/sensitron/vote-sessions');
    }
  });

  // The backfill switch must not open the wish page.
  it('redirects a manager while only the backfill switch is on', async () => {
    const resultPromise = firstValueFrom(runGuard('sensitron'));

    httpMock.expectOne('/api/auth/me').flush(USER);
    httpMock
      .expectOne('/api/channels/sensitron/permissions')
      .flush({ ...PERMISSIONS, chatLogBackfillEnabled: true, wishesEnabled: false });

    const result = await resultPromise;
    expect(router.serializeUrl(result as UrlTree)).toBe('/channels/sensitron/vote-sessions');
  });

  it('redirects to the vote-sessions list when the permissions call fails', async () => {
    const resultPromise = firstValueFrom(runGuard('sensitron'));

    httpMock.expectOne('/api/auth/me').flush(USER);
    httpMock
      .expectOne('/api/channels/sensitron/permissions')
      .flush(null, { status: 500, statusText: 'Server Error' });

    const result = await resultPromise;
    expect(router.serializeUrl(result as UrlTree)).toBe('/channels/sensitron/vote-sessions');
  });
});
