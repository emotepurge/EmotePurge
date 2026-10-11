import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Observable, NEVER, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  AdminDashboardActivity,
  AdminHealth,
  FlushHealth,
  RateLimitPolicySnapshot,
  RateLimitTelemetrySnapshot,
  RateLimitTelemetryTrend,
  SevenTvHealth,
} from '../../core/admin/admin.model';
import { AdminService } from '../../core/admin/admin.service';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { AdminMonitoringPage } from './admin-monitoring-page';

/** jsdom ships no `EventSource` — the constructor subscribes to the admin live stream immediately
 *  (same as admin-roster-card.spec.ts). None of the decisions covered here depend on a live push. */
class FakeEventSource {
  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  constructor(readonly url: string) {}
  close(): void {
    /* no-op */
  }
}

/** A healthy snapshot with round, easy-to-reason-about numbers — every test overrides only the
 *  nested field it changes. `sevenTv`/`flush` are merged one level deep so a test doesn't have to
 *  restate the whole nested object to change one field. */
function health(
  overrides: {
    sevenTv?: Partial<SevenTvHealth>;
    flush?: Partial<FlushHealth>;
  } & Partial<Omit<AdminHealth, 'sevenTv' | 'flush'>> = {},
): AdminHealth {
  const { sevenTv, flush, ...rest } = overrides;
  return {
    snapshotAvailable: true,
    status: 'connected',
    isConnected: true,
    lastMessageReceivedUtc: null,
    connectAttemptedUtc: null,
    secondsSinceLastMessage: null,
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
      ...sevenTv,
    },
    flush: {
      consecutiveFailures: 0,
      lastSuccessUtc: null,
      lastRowCount: null,
      pendingEmoteCount: null,
      ...flush,
    },
    worker: { instanceId: 'worker-1', processStartedUtc: null },
    ...rest,
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

function activity(overrides: Partial<AdminDashboardActivity> = {}): AdminDashboardActivity {
  return {
    range: '30d',
    fromDate: '2026-09-12',
    toDate: '2026-10-11',
    todayIsPartial: true,
    generatedAtUtc: '2026-10-11T12:00:00Z',
    days: [{ date: '2026-10-11', liveChannels: 4 }],
    ...overrides,
  };
}

function trend(overrides: Partial<RateLimitTelemetryTrend> = {}): RateLimitTelemetryTrend {
  return {
    telemetryAvailable: true,
    fromUtc: '2026-10-10T12:00:00Z',
    toUtc: '2026-10-11T12:00:00Z',
    minuteStartsUtc: ['2026-10-11T11:59:00Z'],
    localRejections: [0],
    providers: [],
    ...overrides,
  };
}

describe('AdminMonitoringPage', () => {
  let fixture: ComponentFixture<AdminMonitoringPage>;
  let component: AdminMonitoringPage;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
      ],
    });

    // The full template mounts the whole AdminRosterCard subtree (its own resource, its own live
    // subscription) plus every other card on the page — none of it is what these decisions read
    // from. Replacing it keeps this spec scoped to AdminMonitoringPage's own computed()s, the same
    // technique usage-stats-page.spec.ts uses for its unrelated viewChild refs.
    TestBed.overrideComponent(AdminMonitoringPage, { set: { template: '<div></div>' } });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  /** Stubs both AdminService calls the page's two resources make. `rateLimitsStream` defaults to a
   *  quiet, available snapshot since most tests here are only about the health side. */
  function render(
    healthStream: Observable<AdminHealth>,
    rateLimitsStream: Observable<RateLimitTelemetrySnapshot> = of(rateLimits()),
    activityStream: Observable<AdminDashboardActivity> = of(activity()),
    trendStream: Observable<RateLimitTelemetryTrend> = of(trend()),
  ): void {
    const service = TestBed.inject(AdminService);
    vi.spyOn(service, 'getHealth').mockReturnValue(healthStream);
    vi.spyOn(service, 'getRateLimits').mockReturnValue(rateLimitsStream);
    vi.spyOn(service, 'getDashboardActivity').mockReturnValue(activityStream);
    vi.spyOn(service, 'getRateLimitTrend').mockReturnValue(trendStream);
    fixture = TestBed.createComponent(AdminMonitoringPage);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  describe('subscription utilization — null vs. zero denominator guard', () => {
    it('grades no utilization at all (null) when 7TV never reported a subscription count, while the bar still renders at 0 rather than blank', () => {
      render(of(health({ sevenTv: { desiredSubscriptionCount: null } })));

      expect(component['rawUtilizationPercent']()).toBeNull();
      expect(component['utilizationPercent']()).toBe(0);
    });

    it('grades a real 0 % utilization — not "unknown" — when 7TV reports zero desired subscriptions', () => {
      render(of(health({ sevenTv: { desiredSubscriptionCount: 0 } })));

      expect(component['rawUtilizationPercent']()).toBe(0);
      expect(component['utilizationPercent']()).toBe(0);
    });

    it('grades no utilization at all (null) when the subscription limit is zero', () => {
      render(of(health({ sevenTv: { subscriptionLimit: 0 } })));

      expect(component['rawUtilizationPercent']()).toBeNull();
      expect(component['utilizationPercent']()).toBe(0);
    });

    it('computes a real, uncapped percentage once both figures are known', () => {
      render(of(health({ sevenTv: { desiredSubscriptionCount: 250, subscriptionLimit: 500 } })));

      expect(component['rawUtilizationPercent']()).toBe(50);
      expect(component['utilizationPercent']()).toBe(50);
    });

    it('caps the bar width at 100 over the limit while the grading percentage stays uncapped', () => {
      render(of(health({ sevenTv: { desiredSubscriptionCount: 600, subscriptionLimit: 500 } })));

      expect(component['rawUtilizationPercent']()).toBe(120);
      expect(component['utilizationPercent']()).toBe(100);
    });
  });

  describe('subscriptionTone / subscriptionWarningKey composition (utilizationTone itself is tested in utilization-tone.spec.ts)', () => {
    it('is null without a usable denominator, and the warning line stays silent too', () => {
      render(of(health({ sevenTv: { subscriptionLimit: 0 } })));

      expect(component['subscriptionTone']()).toBeNull();
      expect(component['subscriptionWarningKey']()).toBeNull();
      expect(component['subscriptionWarningParams']()).toEqual({ percent: 0 });
    });

    it('wires the uncapped percentage into utilizationTone, not the capped bar width', () => {
      // 85 % sits in the warn band; proves the composition reads rawUtilizationPercent.
      render(of(health({ sevenTv: { desiredSubscriptionCount: 85, subscriptionLimit: 100 } })));

      expect(component['subscriptionTone']()).toBe('warning');
      expect(component['subscriptionWarningKey']()).toBe(
        'admin.monitoring.sevenTv.utilizationWarning',
      );
    });

    it('reports the critical key in the danger band', () => {
      render(of(health({ sevenTv: { desiredSubscriptionCount: 96, subscriptionLimit: 100 } })));

      expect(component['subscriptionTone']()).toBe('danger');
      expect(component['subscriptionWarningKey']()).toBe(
        'admin.monitoring.sevenTv.utilizationCritical',
      );
    });

    it('rounds the params percentage', () => {
      render(of(health({ sevenTv: { desiredSubscriptionCount: 1, subscriptionLimit: 3 } })));

      expect(component['subscriptionWarningParams']()).toEqual({ percent: 33 });
    });
  });

  describe('restRequestRate — no denominator, no rate stated', () => {
    it('is null when the channel count was never reported', () => {
      render(of(health({ sevenTv: { desiredChannelCount: null } })));

      expect(component['restRequestRate']()).toBeNull();
    });

    it('states a real 0/s rate — not null — when 7TV reports zero tracked channels', () => {
      render(of(health({ sevenTv: { desiredChannelCount: 0, resyncIntervalSeconds: 300 } })));

      expect(component['restRequestRate']()).toEqual({
        requests: 0,
        seconds: 300,
        perSecond: '0.00',
      });
    });

    it('is null when the resync interval is missing', () => {
      render(of(health({ sevenTv: { resyncIntervalSeconds: null } })));

      expect(component['restRequestRate']()).toBeNull();
    });

    it('composes the rate from channel count and resync interval once both are known', () => {
      render(of(health({ sevenTv: { desiredChannelCount: 10, resyncIntervalSeconds: 300 } })));

      expect(component['restRequestRate']()).toEqual({
        requests: 10,
        seconds: 300,
        perSecond: '0.03',
      });
    });
  });

  describe('hasUnacknowledged', () => {
    it('is false at zero', () => {
      render(of(health({ sevenTv: { unacknowledgedCount: 0 } })));

      expect(component['hasUnacknowledged']()).toBe(false);
    });

    it('is false when the worker never reported the field (null)', () => {
      render(of(health({ sevenTv: { unacknowledgedCount: null } })));

      expect(component['hasUnacknowledged']()).toBe(false);
    });

    it('is true above zero', () => {
      render(of(health({ sevenTv: { unacknowledgedCount: 3 } })));

      expect(component['hasUnacknowledged']()).toBe(true);
    });
  });

  describe('hasFlushFailures', () => {
    it('is false at zero', () => {
      render(of(health({ flush: { consecutiveFailures: 0 } })));

      expect(component['hasFlushFailures']()).toBe(false);
    });

    it('is false when null', () => {
      render(of(health({ flush: { consecutiveFailures: null } })));

      expect(component['hasFlushFailures']()).toBe(false);
    });

    it('is true above zero', () => {
      render(of(health({ flush: { consecutiveFailures: 2 } })));

      expect(component['hasFlushFailures']()).toBe(true);
    });
  });

  describe('the two resources fail independently', () => {
    it('surfaces the health error without the rate-limits resource being affected, and vice versa', () => {
      render(
        throwError(
          () => new HttpErrorResponse({ status: 404, error: { errorCode: 'channel_not_found' } }),
        ),
        of(rateLimits()),
      );

      expect(component['errorMessage']()).toBe('errors.api.channel_not_found');
      expect(component['rateLimitsErrorMessage']()).toBeNull();
    });

    it('stays null for a non-HTTP health failure rather than inventing a generic message', () => {
      render(
        throwError(() => new Error('boom')),
        of(rateLimits()),
      );

      expect(component['errorMessage']()).toBeNull();
    });

    it('surfaces the rate-limits error independently of a healthy health resource', () => {
      render(
        of(health()),
        throwError(() => new HttpErrorResponse({ status: 500, error: null })),
      );

      expect(component['errorMessage']()).toBeNull();
      expect(component['rateLimitsErrorMessage']()).toBe('errors.status.server');
    });
  });

  describe('activity range', () => {
    it('loads the 30 day series first and reloads with the new range when the menu changes', () => {
      render(of(health()));
      const getActivity = vi.mocked(TestBed.inject(AdminService).getDashboardActivity);
      expect(getActivity).toHaveBeenLastCalledWith('30d');

      component['range'].set('90d');
      fixture.detectChanges();

      expect(getActivity).toHaveBeenLastCalledWith('90d');
    });

    it('keeps the last series on screen while the next range loads', () => {
      render(of(health()));
      const first = component['activity']();
      expect(first?.days).toHaveLength(1);

      vi.mocked(TestBed.inject(AdminService).getDashboardActivity).mockReturnValue(NEVER);
      component['range'].set('13mo');
      fixture.detectChanges();

      expect(component['activity']()).toBe(first);
    });

    it('surfaces an activity failure without hiding the health snapshot', () => {
      render(
        of(health()),
        of(rateLimits()),
        throwError(
          () =>
            new HttpErrorResponse({ status: 400, error: { errorCode: 'invalid_dashboard_range' } }),
        ),
      );

      expect(component['activityErrorMessage']()).toBe('errors.api.invalid_dashboard_range');
      expect(component['health']()).toBeDefined();
      expect(component['errorMessage']()).toBeNull();
      expect(component['activity']()).toBeUndefined();
    });
  });

  describe('rate-limit exception sections', () => {
    it('orders policy rows acute first and then by 24 h rejections', () => {
      render(
        of(health()),
        of(
          rateLimits({
            policies: [
              policy({ name: 'Voting', rejectedLast24Hours: 50, acceptedLast24Hours: 50 }),
              policy({ name: 'Bookkeeping', rejectedLastMinute: 1, rejectedLast24Hours: 1 }),
              policy({ name: 'PublicHealth', acceptedLast24Hours: 99 }),
            ],
          }),
        ),
      );

      expect(component['policyRows']().map((r) => r.name)).toEqual(['Bookkeeping', 'Voting']);
    });

    it('draws a local series plus one danger series per rejecting dimension', () => {
      render(
        of(health()),
        of(rateLimits()),
        of(activity()),
        of(
          trend({
            providers: [
              { providerName: 'seventv', callSource: 'sync', rateLimited: [2] },
              { providerName: 'seventv', callSource: 'search', rateLimited: [1] },
              { providerName: 'twitch', callSource: 'api', rateLimited: [4] },
              { providerName: 'twitch', callSource: 'helix', rateLimited: [0] },
            ],
          }),
        ),
      );

      const series = component['trendSeries']();
      expect(series).toHaveLength(4);
      expect(new Set(series.map((s) => s.id)).size).toBe(4);
      expect(series.every((s) => s.tone === 'danger')).toBe(true);
    });

    it('keeps the snapshot and the empty exception list when telemetry is unavailable', () => {
      render(
        of(health()),
        of(
          rateLimits({ telemetryAvailable: false, policies: [policy({ rejectedLast24Hours: 9 })] }),
        ),
        of(activity()),
        of(trend({ telemetryAvailable: false, minuteStartsUtc: [], localRejections: [] })),
      );

      expect(component['rateLimits']()?.telemetryAvailable).toBe(false);
      expect(component['policyRows']()).toEqual([]);
      expect(component['trendSeries']()).toEqual([]);
    });

    it('opens the details dialog with the snapshot even when telemetry is unavailable', () => {
      const degraded = rateLimits({ telemetryAvailable: false });
      render(of(health()), of(degraded));
      const open = vi.spyOn(TestBed.inject(Dialog), 'open').mockReturnValue({} as never);

      component['openDetails'](degraded);

      expect(open).toHaveBeenCalledTimes(1);
      expect(open.mock.calls[0][1]).toMatchObject({ data: { snapshot: degraded } });
    });
  });

  describe('stability on errors and visibility', () => {
    it('keeps the last snapshot and trend when a later poll fails', () => {
      render(of(health()));
      const service = TestBed.inject(AdminService);
      const snapshotBefore = component['rateLimits']();
      const trendBefore = component['trend']();
      expect(snapshotBefore).toBeDefined();
      expect(trendBefore).toBeDefined();

      vi.mocked(service.getRateLimits).mockReturnValue(
        throwError(() => new HttpErrorResponse({ status: 500, error: null })),
      );
      vi.mocked(service.getRateLimitTrend).mockReturnValue(
        throwError(() => new HttpErrorResponse({ status: 500, error: null })),
      );
      component['reload']();
      fixture.detectChanges();

      expect(component['rateLimitsErrorMessage']()).toBe('errors.status.server');
      expect(component['trendErrorMessage']()).toBe('errors.status.server');
      expect(component['rateLimits']()).toBe(snapshotBefore);
      expect(component['trend']()).toBe(trendBefore);
    });

    it('still has the trend when the snapshot failed on first load, and vice versa', () => {
      render(
        of(health()),
        throwError(() => new HttpErrorResponse({ status: 500, error: null })),
        of(activity()),
        of(trend({ localRejections: [3] })),
      );

      expect(component['rateLimits']()).toBeUndefined();
      expect(component['showRateLimitsSkeleton']()).toBe(false);
      expect(component['trendSeries']()).toHaveLength(1);
    });

    it('shows no activity skeleton after a failed first load', () => {
      render(
        of(health()),
        of(rateLimits()),
        throwError(() => new HttpErrorResponse({ status: 500, error: null })),
      );

      expect(component['activity']()).toBeUndefined();
      expect(component['activityLoading']()).toBe(false);
    });

    it('formats a tiny non-zero share as "<0.1", never as 0', () => {
      render(of(health()));

      expect(component['formatPercent'](0.04)).toMatch(/^<0[.,]1$/);
      expect(component['formatPercent'](0)).toBe('0');
      expect(component['formatPercent'](25)).toBe('25');
    });

    it('reloads the dashboard once when a page that opened hidden becomes visible', () => {
      vi.useFakeTimers();
      const setVisibility = (state: string) =>
        Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
      try {
        setVisibility('hidden');
        render(of(health()));
        const service = TestBed.inject(AdminService);
        vi.advanceTimersByTime(1);
        const before = vi.mocked(service.getDashboardActivity).mock.calls.length;

        setVisibility('visible');
        document.dispatchEvent(new Event('visibilitychange'));
        fixture.detectChanges();
        vi.advanceTimersByTime(1);

        expect(vi.mocked(service.getDashboardActivity).mock.calls.length).toBe(before + 1);
        expect(vi.mocked(service.getRateLimitTrend).mock.calls.length).toBe(2);
        expect(vi.mocked(service.getRateLimits).mock.calls.length).toBe(2);
      } finally {
        setVisibility('visible');
        vi.useRealTimers();
      }
    });
  });
});
