import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { convertToParamMap, provideRouter, Router, UrlTree } from '@angular/router';
import { firstValueFrom, Observable } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AuthService } from '../auth/auth.service';
import { ChannelPermissions } from './channel.model';
import { usageStatsAccessGuard } from './usage-stats-access.guard';

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
};

describe('usageStatsAccessGuard', () => {
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
    url = '/channels/sensitron/usage-stats',
  ): Observable<boolean | UrlTree> {
    return TestBed.runInInjectionContext(() =>
      usageStatsAccessGuard(
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
    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/login');
    expect(sessionStorage.getItem('ep_return_url')).toBe('/channels/sensitron/usage-stats');
  });

  it('allows navigation when canViewUsageStats is true', async () => {
    const resultPromise = firstValueFrom(runGuard('sensitron'));

    httpMock.expectOne('/api/auth/me').flush(USER);
    httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);

    expect(await resultPromise).toBe(true);
  });

  // The 7TV-editor-less, non-managing viewer: the endpoint answers 200, the flag says no. The old
  // probe-based guard could not tell this apart from a server error.
  it('redirects to the channel vote-sessions list when canViewUsageStats is false', async () => {
    const resultPromise = firstValueFrom(runGuard('sensitron'));

    httpMock.expectOne('/api/auth/me').flush(USER);
    httpMock
      .expectOne('/api/channels/sensitron/permissions')
      .flush({ ...PERMISSIONS, canManage: false, canViewUsageStats: false });

    const result = await resultPromise;
    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/channels/sensitron/vote-sessions');
  });

  it('redirects to the channel vote-sessions list when the permissions call fails', async () => {
    const resultPromise = firstValueFrom(runGuard('sensitron'));

    httpMock.expectOne('/api/auth/me').flush(USER);
    httpMock
      .expectOne('/api/channels/sensitron/permissions')
      .flush(null, { status: 500, statusText: 'Server Error' });

    const result = await resultPromise;
    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/channels/sensitron/vote-sessions');
  });
});
