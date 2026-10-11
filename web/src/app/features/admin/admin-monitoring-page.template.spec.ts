import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { NEVER, Observable, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  AdminDashboardActivity,
  AdminHealth,
  AdminRoster,
  RateLimitPolicySnapshot,
  RateLimitProviderCounters,
  RateLimitTelemetrySnapshot,
  RateLimitTelemetryTrend,
} from '../../core/admin/admin.model';
import { AdminService } from '../../core/admin/admin.service';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { AdminMonitoringPage } from './admin-monitoring-page';

// These specs render the page's real template (the sibling spec stubs it to test the computed()s
// in isolation). They assert what an admin can perceive — which regions exist, what is announced,
// what a control does — not markup.

class FakeEventSource {
  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  constructor(readonly url: string) {}
  close(): void {
    /* no-op */
  }
}

function health(overrides: Partial<AdminHealth> = {}): AdminHealth {
  return {
    snapshotAvailable: true,
    status: 'connected',
    isConnected: true,
    lastMessageReceivedUtc: '2026-10-11T11:59:00Z',
    connectAttemptedUtc: null,
    secondsSinceLastMessage: 3,
    sevenTv: {
      status: 'connected',
      enabled: true,
      connected: true,
      lastFrameUtc: null,
      lastDispatchUtc: null,
      connectAttemptedUtc: null,
      secondsSinceLastFrame: null,
      desiredChannelCount: 10,
      desiredSubscriptionCount: 20,
      unacknowledgedCount: 0,
      subscriptionLimit: 500,
      resyncIntervalSeconds: 300,
    },
    flush: {
      consecutiveFailures: 0,
      lastSuccessUtc: null,
      lastRowCount: null,
      pendingEmoteCount: null,
    },
    worker: { instanceId: 'worker-1', processStartedUtc: null },
    ...overrides,
  };
}

function policy(overrides: Partial<RateLimitPolicySnapshot> = {}): RateLimitPolicySnapshot {
  return {
    name: 'usage-stats',
    type: 'token-bucket',
    capacity: 40,
    tokensPerPeriod: 40,
    replenishmentPeriodSeconds: 60,
    windowSeconds: null,
    partition: 'per-user',
    queueLimit: 0,
    acceptedLastMinute: 0,
    rejectedLastMinute: 0,
    acceptedLast24Hours: 0,
    rejectedLast24Hours: 0,
    ...overrides,
  };
}

function provider(overrides: Partial<RateLimitProviderCounters> = {}): RateLimitProviderCounters {
  return {
    providerName: 'twitch',
    callSource: 'live-poll',
    requestsLastMinute: 0,
    requestsLast24Hours: 0,
    rateLimitedLastMinute: 0,
    rateLimitedLast24Hours: 0,
    lastRetryAfterSeconds: null,
    lastRateLimitedAtUtc: null,
    lastHeaderSample: null,
    ...overrides,
  };
}

function rateLimits(
  overrides: Partial<RateLimitTelemetrySnapshot> = {},
): RateLimitTelemetrySnapshot {
  return {
    telemetryAvailable: true,
    policies: [],
    lastLocalRejection: null,
    caches: [],
    providers: [],
    ...overrides,
  };
}

function trend(overrides: Partial<RateLimitTelemetryTrend> = {}): RateLimitTelemetryTrend {
  return {
    telemetryAvailable: true,
    fromUtc: '2026-10-10T12:00:00Z',
    toUtc: '2026-10-11T12:00:00Z',
    minuteStartsUtc: ['2026-10-11T11:58:00Z', '2026-10-11T11:59:00Z'],
    localRejections: [0, 2],
    providers: [],
    ...overrides,
  };
}

const ACTIVITY: AdminDashboardActivity = {
  range: '30d',
  fromDate: '2026-10-10',
  toDate: '2026-10-11',
  todayIsPartial: true,
  generatedAtUtc: '2026-10-11T12:00:00Z',
  days: [
    { date: '2026-10-10', liveChannels: 3 },
    { date: '2026-10-11', liveChannels: 4 },
  ],
};

const ROSTER: AdminRoster = {
  snapshotAvailable: true,
  trackedChannelCount: 12,
  ceilings: {
    twitchConcurrentChannelLimit: 100,
    twitchJoinBudgetChannels: 100,
    maxActiveChannels: 80,
  },
  bootRecoveryCompleted: true,
  ageSeconds: 0,
  ircConfirmedCount: 12,
  sevenTvAcknowledgedCount: 12,
  unknownToDatabaseTotal: 0,
};

interface Streams {
  health?: Observable<AdminHealth>;
  rateLimits?: Observable<RateLimitTelemetrySnapshot>;
  trend?: Observable<RateLimitTelemetryTrend>;
  activity?: Observable<AdminDashboardActivity>;
}

describe('AdminMonitoringPage — real template', () => {
  let fixture: ComponentFixture<AdminMonitoringPage>;
  let service: AdminService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [
        AdminMonitoringPage,
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideRouter([]),
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
      ],
    });
    service = TestBed.inject(AdminService);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  function render(streams: Streams = {}): HTMLElement {
    vi.spyOn(service, 'getHealth').mockReturnValue(streams.health ?? of(health()));
    vi.spyOn(service, 'getRateLimits').mockReturnValue(streams.rateLimits ?? of(rateLimits()));
    vi.spyOn(service, 'getRateLimitTrend').mockReturnValue(streams.trend ?? of(trend()));
    vi.spyOn(service, 'getDashboardActivity').mockReturnValue(streams.activity ?? of(ACTIVITY));
    vi.spyOn(service, 'getRoster').mockReturnValue(of(ROSTER));
    fixture = TestBed.createComponent(AdminMonitoringPage);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const region = (root: HTMLElement, id: string) =>
    root.querySelector<HTMLElement>(`[data-testid="${id}"]`);

  describe('regions', () => {
    it('renders the activity, rate-limit and worker-health regions once everything loaded', () => {
      const root = render();

      expect(region(root, 'admin-activity')).not.toBeNull();
      expect(region(root, 'admin-rate-limits')).not.toBeNull();
      expect(region(root, 'admin-health')).not.toBeNull();
      expect(root.querySelector('app-admin-roster-card')).not.toBeNull();
    });

    it('shows skeletons on the first load and no cards yet', () => {
      const root = render({ health: NEVER, rateLimits: NEVER, activity: NEVER });

      expect(region(root, 'admin-rate-limits')).toBeNull();
      expect(region(root, 'admin-health')?.querySelector('app-skeleton-sections')).not.toBeNull();
      expect(root.querySelector('app-admin-roster-card')).toBeNull();
      expect(root.querySelector('app-line-chart')).toBeNull();
    });

    it('disables the refresh button while a resource is loading', () => {
      const root = render({ health: NEVER });
      const buttons = Array.from(root.querySelectorAll('header button'));

      expect(buttons).toHaveLength(1);
      expect((buttons[0] as HTMLButtonElement).disabled).toBe(true);
    });

    it('puts a health error inside the health region, beside no snapshot', () => {
      const root = render({
        health: throwError(() => new HttpErrorResponse({ status: 500, error: null })),
      });

      expect(region(root, 'admin-health')?.querySelector('app-notice-banner')).not.toBeNull();
      expect(region(root, 'admin-health')?.querySelector('section')).toBeNull();
    });

    it('warns that no worker snapshot exists while still listing the sections', () => {
      const root = render({ health: of(health({ snapshotAvailable: false })) });

      expect(region(root, 'admin-health')?.querySelector('app-notice-banner')).not.toBeNull();
    });
  });

  describe('7TV subscription utilization', () => {
    function progress(root: HTMLElement): HTMLElement {
      return root.querySelector<HTMLElement>(
        '[role="progressbar"][aria-label="admin.monitoring.sevenTv.utilization"]',
      ) as HTMLElement;
    }

    it('exposes the figures through an ARIA progressbar and stays quiet at low utilization', () => {
      const root = render();

      expect(progress(root).getAttribute('aria-valuenow')).toBe('20');
      expect(progress(root).getAttribute('aria-valuemax')).toBe('500');
      expect(progress(root).getAttribute('aria-label')).toBe(
        'admin.monitoring.sevenTv.utilization',
      );
      expect(root.textContent).not.toContain('utilizationWarning');
      expect(root.textContent).not.toContain('utilizationCritical');
    });

    it('adds a text line next to the colour once utilization is critical', () => {
      const h = health();
      const root = render({
        health: of({
          ...h,
          sevenTv: { ...h.sevenTv, desiredSubscriptionCount: 480, subscriptionLimit: 500 },
        }),
      });

      expect(root.textContent).toContain('admin.monitoring.sevenTv.utilizationCritical');
    });

    it('marks unacknowledged subscriptions with a badge only above zero', () => {
      const quiet = render();
      expect(quiet.querySelector('app-status-badge')).toBeNull();
      fixture.destroy();

      const h = health();
      const loud = render({
        health: of({ ...h, sevenTv: { ...h.sevenTv, unacknowledgedCount: 4 } }),
      });
      expect(loud.querySelector('app-status-badge')?.textContent).toContain('4');
    });

    it('states the REST request rate only when both figures are known', () => {
      const withRate = render();
      expect(withRate.textContent).toContain('admin.monitoring.sevenTv.restRate');
      fixture.destroy();

      const h = health();
      const without = render({
        health: of({ ...h, sevenTv: { ...h.sevenTv, resyncIntervalSeconds: null } }),
      });
      expect(without.textContent).not.toContain('admin.monitoring.sevenTv.restRate');
    });
  });

  describe('rate limits — exception first', () => {
    it('names the quiet case with an empty state instead of listing healthy policies', () => {
      const root = render({ rateLimits: of(rateLimits({ policies: [policy()] })) });
      const section = region(root, 'admin-rate-limits') as HTMLElement;

      expect(section.querySelector('app-empty-state')).not.toBeNull();
      expect(section.querySelector('h4')).toBeNull();
    });

    it('warns instead of listing rows when the telemetry store was unreachable', () => {
      const root = render({
        rateLimits: of(
          rateLimits({
            telemetryAvailable: false,
            policies: [policy({ rejectedLast24Hours: 9 })],
          }),
        ),
      });
      const section = region(root, 'admin-rate-limits') as HTMLElement;

      expect(section.querySelector('app-notice-banner')).not.toBeNull();
      expect(section.querySelector('h4')).toBeNull();
      expect(section.querySelector('app-empty-state')).toBeNull();
    });

    it('lists rejecting policies with an acute marker, a share, and "no requests" for an empty window', () => {
      const root = render({
        rateLimits: of(
          rateLimits({
            policies: [
              policy({
                name: 'usage-stats',
                rejectedLastMinute: 2,
                rejectedLast24Hours: 5,
                acceptedLast24Hours: 95,
              }),
              policy({ name: 'auth', type: 'fixed-window', rejectedLast24Hours: 1 }),
            ],
          }),
        ),
      });
      const rows = (region(root, 'admin-rate-limits') as HTMLElement).querySelectorAll(
        'h4 + ul > li',
      );

      expect(rows).toHaveLength(2);
      // Acute first; only the acute row carries the badge and the last-minute count.
      expect(rows[0].textContent).toContain('admin.rateLimits.policies.names.usage-stats');
      expect(rows[0].querySelector('app-status-badge')).not.toBeNull();
      expect(rows[0].textContent).toContain('admin.dashboard.rateLimits.policies.share');
      expect(rows[0].textContent).toContain('admin.dashboard.rateLimits.lastMinute');
      expect(rows[1].querySelector('app-status-badge')).toBeNull();
      expect(rows[1].textContent).toContain('admin.dashboard.rateLimits.policies.share');
    });

    it('lists rate-limited providers with their 24 h count', () => {
      const root = render({
        rateLimits: of(
          rateLimits({
            providers: [
              provider({ rateLimitedLastMinute: 1, rateLimitedLast24Hours: 3 }),
              provider({
                providerName: 'seven-tv',
                callSource: 'resync',
                rateLimitedLast24Hours: 1,
              }),
            ],
          }),
        ),
      });
      const rows = (region(root, 'admin-rate-limits') as HTMLElement).querySelectorAll(
        'h4 + ul > li',
      );

      expect(rows).toHaveLength(2);
      expect(rows[0].querySelector('app-status-badge')).not.toBeNull();
      expect(rows[0].textContent).toContain('admin.dashboard.rateLimits.providers.count');
      expect(rows[1].querySelector('app-status-badge')).toBeNull();
    });

    it('keeps the last snapshot and the trend independent of each other when one fails', () => {
      const root = render({
        rateLimits: throwError(() => new HttpErrorResponse({ status: 500, error: null })),
      });
      const section = region(root, 'admin-rate-limits') as HTMLElement;

      expect(section.querySelectorAll('app-notice-banner')).toHaveLength(1);
      expect(section.querySelector('app-line-chart')).not.toBeNull();
      // No snapshot yet: nothing to open details for.
      expect(section.querySelector('button')).toBeNull();
    });

    it('draws the trend with one series and says so when no provider was rate-limited', () => {
      const root = render();
      const section = region(root, 'admin-rate-limits') as HTMLElement;

      expect(section.querySelector('app-line-chart')).not.toBeNull();
      expect(section.textContent).toContain(
        'admin.dashboard.rateLimits.trend.noProviderRejections',
      );
    });

    it('adds a provider series to the trend and drops the explanatory note', () => {
      const root = render({
        trend: of(
          trend({
            providers: [
              { providerName: 'twitch', callSource: 'live-poll', rateLimited: [0, 1] },
              { providerName: 'seven-tv', callSource: 'resync', rateLimited: [0, 0] },
            ],
          }),
        ),
      });
      const section = region(root, 'admin-rate-limits') as HTMLElement;

      expect(section.textContent).not.toContain(
        'admin.dashboard.rateLimits.trend.noProviderRejections',
      );
      expect(fixture.componentInstance['trendSeries']()).toHaveLength(2);
    });

    it('omits the chart when the trend has no buckets, and shows a trend error beside the rows', () => {
      const empty = render({ trend: of(trend({ minuteStartsUtc: [], localRejections: [] })) });
      expect(region(empty, 'admin-rate-limits')?.querySelector('app-line-chart')).toBeNull();
      fixture.destroy();

      const failed = render({
        trend: throwError(() => new HttpErrorResponse({ status: 500, error: null })),
      });
      expect(
        region(failed, 'admin-rate-limits')?.querySelectorAll('app-notice-banner'),
      ).toHaveLength(1);
    });

    it('hands the current snapshot to the details dialog', () => {
      const snapshot = rateLimits({ policies: [policy()] });
      const root = render({ rateLimits: of(snapshot) });
      const open = vi.spyOn(TestBed.inject(Dialog), 'open').mockReturnValue(undefined as never);

      (region(root, 'admin-rate-limits') as HTMLElement).querySelector('button')?.click();

      expect(open).toHaveBeenCalledOnce();
      expect(open.mock.calls[0][1]).toMatchObject({ data: { snapshot } });
    });
  });

  describe('activity wiring', () => {
    it('passes the roster counts into the activity heading once the roster card loaded', () => {
      const root = render();
      fixture.detectChanges();

      expect(fixture.componentInstance['rosterContext']()).toEqual({ tracked: 12, max: 80 });
      expect(region(root, 'admin-activity')?.textContent).toContain(
        'admin.dashboard.activity.context',
      );
    });

    it('writes the range chosen in the activity section back and reloads the series', () => {
      const root = render();
      const radios = (region(root, 'admin-activity') as HTMLElement).querySelectorAll<HTMLElement>(
        '[role="radio"]',
      );

      radios[2].click();
      fixture.detectChanges();

      expect(fixture.componentInstance['range']()).toBe('13mo');
      expect(service.getDashboardActivity).toHaveBeenLastCalledWith('13mo');
    });
  });

  describe('refresh', () => {
    it('reloads health, roster and the dashboard resources together', () => {
      const root = render();
      fixture.detectChanges();
      const calls = (fn: unknown) => vi.mocked(fn as AdminService['getHealth']).mock.calls.length;
      const before = {
        health: calls(service.getHealth),
        roster: calls(service.getRoster),
        limits: calls(service.getRateLimits),
        trend: calls(service.getRateLimitTrend),
        activity: calls(service.getDashboardActivity),
      };

      root.querySelector<HTMLButtonElement>('header button')?.click();
      fixture.detectChanges();

      expect(calls(service.getHealth)).toBe(before.health + 1);
      expect(calls(service.getRoster)).toBe(before.roster + 1);
      expect(calls(service.getRateLimits)).toBe(before.limits + 1);
      expect(calls(service.getRateLimitTrend)).toBe(before.trend + 1);
      expect(calls(service.getDashboardActivity)).toBe(before.activity + 1);
    });
  });

  describe('formatting and tone helpers', () => {
    beforeEach(() => render());

    it('never shows a rejected share below 0.1 % as zero', () => {
      const page = fixture.componentInstance;
      expect(page['formatPercent'](0.04)).toMatch(/^<0[.,]1$/);
      expect(page['formatPercent'](12.34)).toMatch(/^12[.,]3$/);
    });

    it('shows a dash for absent numbers and dates', () => {
      const page = fixture.componentInstance;
      expect(page['formatNumber'](null)).toBe('—');
      expect(page['formatNumber'](0)).toBe('0');
      expect(page['formatDateTime'](null)).toBe('—');
      expect(page['formatDateTime']('2026-10-11T12:00:00Z')).not.toBe('—');
    });

    it('falls back to the idle tone for a status it does not know', () => {
      const page = fixture.componentInstance;
      expect(page['toneFor']('connected')).toBe('ok');
      expect(page['toneFor']('nonsense' as never)).toBe('idle');
    });

    it('maps the live stream state to a tone, treating "connecting" as not a warning', () => {
      expect(fixture.componentInstance['liveTone']()).toMatch(/^(idle|ok|danger)$/);
    });
  });
});
