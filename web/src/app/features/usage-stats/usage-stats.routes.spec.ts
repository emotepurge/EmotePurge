/**
 * Issue #264 split `usage-stats` out of app.routes.ts into its own lazy `loadChildren` module (this
 * file's sibling, usage-stats.routes.ts) purely to shrink the initial bundle — the leave-guard's
 * dependency on `SevenTvImportService` was pulling the whole import engine into every page load.
 * The issue's own stated risk is that `usageStatsLeaveGuard`'s `leadsToSameRoute` helper compares
 * `routeConfig` object identity, and reshaping the route into a child module changes the shape of
 * the objects Angular hands back — "it needs an explicit check, not just 'the build still
 * compiles'". This file is that check, in two parts:
 *
 * - **Wiring test** (no router): reads the real, live `routes` export from app.routes.ts and
 *   `USAGE_STATS_ROUTES` from the sibling file directly — not a hand-typed stand-in of their shape
 *   — and asserts app.routes.ts's `usage-stats` entry still delegates to exactly this module's
 *   export (`toBe`, not `toEqual`: the loader must resolve to the very same array, not a lookalike)
 *   and still carries `canActivate` rather than `canDeactivate`. A future rename, a route moved to
 *   a different parent, or a guard swapped back to `canActivate`/`loadComponent` breaks this
 *   directly.
 * - **Router-behavior tests**: drive a real `Router`/`RouterTestingHarness` through a route tree
 *   built by splicing the REAL `channels/:channelName` child routes (`usage-stats`, and the
 *   redirect that sends a bare channel URL to it) out of the actual `routes` export — not copied,
 *   the exact same objects, including the real `usageStatsAccessGuard` and the real
 *   `loadChildren`/`usageStatsLeaveGuard` chain underneath it. Only the parts these tests have no
 *   stake in (the app shell, the channel workspace layout, the vote-sessions page) are replaced
 *   with bare stand-ins, the same way legal-page.spec.ts and admin-users-page.spec.ts already stand
 *   routed pages up next to dummy siblings rather than mounting the whole app. `UsageStatsPage`
 *   itself is real too, with only its template swapped for two bare `<div>`s — the same technique
 *   usage-stats-page.spec.ts already uses, for the same reason (its constructor's
 *   `viewChild.required` refs need something to resolve against).
 */
import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRoute,
  provideRouter,
  Route,
  Router,
  RouterOutlet,
  Routes,
} from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { By } from '@angular/platform-browser';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ROUTER_FEATURES } from '../../app.config';
import { routes } from '../../app.routes';
import { AuthUser } from '../../core/auth/auth.model';
import { AuthService } from '../../core/auth/auth.service';
import { usageStatsAccessGuard } from '../../core/channels/usage-stats-access.guard';
import { ChannelPermissions } from '../../core/channels/channel.model';
import { ChannelService } from '../../core/channels/channel.service';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { USAGE_STATS_ROUTES } from './usage-stats.routes';
import { UsageStatsPage } from './usage-stats-page';

/** jsdom ships no EventSource — same stand-in as core/live/live-reload.spec.ts and
 *  usage-stats-page.spec.ts. ChannelWorkspaceLayout/OverviewPage would need it too, but this file
 *  never mounts either — only UsageStatsPage, through `liveReload`/`liveEvents`. */
class FakeEventSource {
  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;

  constructor(readonly url: string) {}

  close(): void {
    /* no-op */
  }
}

/** jsdom implements no ResizeObserver either — usage-stats-page.spec.ts's own stand-in. */
class FakeResizeObserver {
  observe(): void {
    /* no-op */
  }
  unobserve(): void {
    /* no-op */
  }
  disconnect(): void {
    /* no-op */
  }
}

@Component({ selector: 'test-outlet', template: '<router-outlet />', imports: [RouterOutlet] })
class Outlet {}

@Component({ selector: 'test-other', template: 'other' })
class Other {}

const USER: AuthUser = {
  twitchUserId: '1',
  login: 'sensitron',
  displayName: 'Sensitron',
  tokenExpiresAtUtc: '2026-07-28T00:00:00Z',
  isGlobalAdmin: false,
  profileImageUrl: null,
};

const PERMISSIONS: ChannelPermissions = {
  canManage: true,
  canViewUsageStats: true,
  isGlobalAdmin: false,
  isTracked: true,
  isBotActive: true,
};

const DE_TRANSLATIONS = {
  import: {
    leaveWhileRunning: {
      message: 'Der Kopierlauf läuft noch.',
      confirm: 'Verlassen',
    },
  },
};

/** Finds a child by its exact `path`, or fails loudly — used to pull the real route objects out of
 *  `routes` instead of retyping their shape. A future rename/move of the node this looks for is
 *  exactly the kind of reshape this file exists to catch. */
function findChild(parent: Route, path: string): Route {
  const match = parent.children?.find((child) => child.path === path);
  if (!match) {
    throw new Error(
      `app.routes.ts: expected a child with path '${path}' under '${parent.path}' — ` +
        'the route tree was restructured; update this test to match the new shape.',
    );
  }
  return match;
}

function findTop(all: Routes, path: string): Route {
  const match = all.find((route) => route.path === path);
  if (!match) {
    throw new Error(`app.routes.ts: expected a top-level route with path '${path}'.`);
  }
  return match;
}

const shell = findTop(routes, '');
const channelWorkspace = findChild(shell, 'channels/:channelName');
const usageStatsNode = findChild(channelWorkspace, 'usage-stats');
const bareChannelRedirect = findChild(channelWorkspace, '');

describe('app.routes.ts wiring for usage-stats (issue #264)', () => {
  it('delegates to the lazy child-routes module and keeps only canActivate on the parent', async () => {
    expect(usageStatsNode.canActivate).toEqual([usageStatsAccessGuard]);
    expect(usageStatsNode.canDeactivate).toBeUndefined();
    expect(usageStatsNode.component).toBeUndefined();
    expect(usageStatsNode.loadComponent).toBeUndefined();
    expect(typeof usageStatsNode.loadChildren).toBe('function');

    const loaded = (await usageStatsNode.loadChildren!()) as Routes;
    expect(loaded).toBe(USAGE_STATS_ROUTES);
  });
});

/** Splices the real `usage-stats` and `channels/:channelName -> ''` redirect nodes into a router
 *  config whose only invented parts are the shell/workspace scaffolding and a stand-in sibling for
 *  vote-sessions — none of which this issue touched. */
function testRoutes(): Routes {
  return [
    {
      path: '',
      component: Outlet,
      children: [
        // Stand-in destination for "leaving the whole channel workspace" below — any route outside
        // the channels/:channelName subtree does, the concrete path doesn't matter.
        { path: 'overview', component: Other },
        {
          path: 'channels/:channelName',
          component: Outlet,
          children: [
            bareChannelRedirect,
            usageStatsNode,
            { path: 'vote-sessions', component: Other },
          ],
        },
      ],
    },
  ];
}

describe('router behavior through USAGE_STATS_ROUTES', () => {
  let isRunning: ReturnType<typeof signal<boolean>>;
  let dialogOpen: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    isRunning = signal(true);
    dialogOpen = vi.fn().mockReturnValue({ closed: of(false) });

    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter(testRoutes(), ...ROUTER_FEATURES),
        { provide: SevenTvImportService, useValue: { isRunning, run: signal(null) } },
        { provide: Dialog, useValue: { open: dialogOpen } },
        {
          provide: AuthService,
          useValue: { ensureLoaded: () => of(USER), stashReturnUrl: vi.fn() },
        },
        { provide: ChannelService, useValue: { getPermissions: () => of(PERMISSIONS) } },
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
      ],
    });

    // Same technique as usage-stats-page.spec.ts: the real 825-line template pulls in the whole
    // atlas/sidecar/dock component graph. Nothing here reads from the DOM — the two named divs only
    // exist so the constructor's viewChild.required refs resolve instead of throwing NG0951.
    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function activePage(harness: RouterTestingHarness): UsageStatsPage {
    const debugEl = harness.fixture.debugElement.query(By.directive(UsageStatsPage));
    if (!debugEl) {
      throw new Error('UsageStatsPage did not activate for this navigation');
    }
    return debugEl.componentInstance as UsageStatsPage;
  }

  it('channel switch during a run: guard runs, exempts, component reused, input follows', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/channels/a/usage-stats');
    const p1 = activePage(harness);
    expect(p1.channelName()).toBe('a');

    const router = TestBed.inject(Router);
    const ok = await router.navigateByUrl('/channels/b/usage-stats');
    harness.detectChanges();

    expect(ok).toBe(true);
    expect(dialogOpen).not.toHaveBeenCalled();
    expect(router.url).toBe('/channels/b/usage-stats');
    const p2 = activePage(harness);
    expect(p2).toBe(p1);
    expect(p2.channelName()).toBe('b');
  });

  it('a bare channel URL redirects to usage-stats, and a channel switch still works from there', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/channels/a');
    const router = TestBed.inject(Router);
    expect(router.url).toBe('/channels/a/usage-stats');

    expect(await router.navigateByUrl('/channels/b')).toBe(true);
    expect(router.url).toBe('/channels/b/usage-stats');
    expect(dialogOpen).not.toHaveBeenCalled();
  });

  it('leaving to another page during a run asks, and dismissing it blocks the navigation', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/channels/a/usage-stats');
    const router = TestBed.inject(Router);

    expect(await router.navigateByUrl('/channels/a/vote-sessions')).toBe(false);
    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(router.url).toBe('/channels/a/usage-stats');

    dialogOpen.mockReturnValue({ closed: of(true) });
    expect(await router.navigateByUrl('/channels/b/vote-sessions')).toBe(true);
    expect(dialogOpen).toHaveBeenCalledTimes(2);
  });

  it('a relative query-param navigation (listQueryState shape) stays on the page without asking', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/channels/a/usage-stats');
    const router = TestBed.inject(Router);
    const relativeTo = harness.fixture.debugElement
      .query(By.directive(UsageStatsPage))
      .injector.get(ActivatedRoute);

    const ok = await router.navigate([], {
      relativeTo,
      queryParams: { emoteSetId: 'x' },
      queryParamsHandling: 'merge',
    });

    expect(ok).toBe(true);
    expect(router.url).toBe('/channels/a/usage-stats?emoteSetId=x');
    expect(dialogOpen).not.toHaveBeenCalled();
  });

  it('leaving the whole channel workspace asks too', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/channels/a/usage-stats');
    expect(await TestBed.inject(Router).navigateByUrl('/overview')).toBe(false);
    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  it('no run: nothing asks', async () => {
    isRunning.set(false);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/channels/a/usage-stats');
    expect(await TestBed.inject(Router).navigateByUrl('/channels/a/vote-sessions')).toBe(true);
    expect(dialogOpen).not.toHaveBeenCalled();
  });
});
