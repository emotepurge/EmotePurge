import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Observable, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  AdminHealth,
  FlushHealth,
  RateLimitPolicySnapshot,
  RateLimitTelemetrySnapshot,
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
  ): void {
    vi.spyOn(TestBed.inject(AdminService), 'getHealth').mockReturnValue(healthStream);
    vi.spyOn(TestBed.inject(AdminService), 'getRateLimits').mockReturnValue(rateLimitsStream);
    fixture = TestBed.createComponent(AdminMonitoringPage);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  describe('subscription utilization — null vs. zero denominator guard', () => {
    it('grades no utilization at all (null) when 7TV never reported a subscription count, while the bar still renders at 0 rather than blank', () => {
      render(of(health({ sevenTv: { desiredSubscriptionCount: 0 } })));

      expect(component['rawUtilizationPercent']()).toBeNull();
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
    it('is null when the channel count is zero', () => {
      render(of(health({ sevenTv: { desiredChannelCount: 0 } })));

      expect(component['restRequestRate']()).toBeNull();
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

  describe('refillDescriptionKey / refillParams — token-bucket vs. fixed-window are mutually exclusive', () => {
    it('picks the token-bucket key and params', () => {
      render(of(health()));

      expect(component['refillDescriptionKey'](policy({ type: 'token-bucket' }))).toBe(
        'admin.rateLimits.policies.refillTokenBucket',
      );
      expect(
        component['refillParams'](
          policy({ type: 'token-bucket', tokensPerPeriod: 40, replenishmentPeriodSeconds: 60 }),
        ),
      ).toEqual({ tokensPerPeriod: '40', periodSeconds: '60' });
    });

    it('picks the fixed-window key and params', () => {
      render(of(health()));

      expect(
        component['refillDescriptionKey'](policy({ type: 'fixed-window', windowSeconds: 10 })),
      ).toBe('admin.rateLimits.policies.refillFixedWindow');
      expect(
        component['refillParams'](policy({ type: 'fixed-window', windowSeconds: 10 })),
      ).toEqual({ windowSeconds: '10' });
    });
  });
});
