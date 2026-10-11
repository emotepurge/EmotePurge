import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { RateLimitPolicySnapshot, RateLimitTelemetrySnapshot } from '../../core/admin/admin.model';
import { AdminRateLimitDetailsDialog } from './admin-rate-limit-details-dialog';

function policy(overrides: Partial<RateLimitPolicySnapshot>): RateLimitPolicySnapshot {
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

describe('AdminRateLimitDetailsDialog — token-bucket vs. fixed-window are mutually exclusive', () => {
  let dialog: AdminRateLimitDetailsDialog;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        {
          provide: DIALOG_DATA,
          useValue: {
            snapshot: {
              telemetryAvailable: false,
              policies: [],
              lastLocalRejection: null,
              caches: [],
              providers: [],
            },
          },
        },
        { provide: DialogRef, useValue: { close: () => undefined } },
      ],
    });
    dialog = TestBed.createComponent(AdminRateLimitDetailsDialog).componentInstance;
  });

  it('picks the token-bucket key and params', () => {
    expect(dialog['refillDescriptionKey'](policy({ type: 'token-bucket' }))).toBe(
      'admin.rateLimits.policies.refillTokenBucket',
    );
    expect(
      dialog['refillParams'](
        policy({ type: 'token-bucket', tokensPerPeriod: 40, replenishmentPeriodSeconds: 60 }),
      ),
    ).toEqual({ tokensPerPeriod: '40', periodSeconds: '60' });
  });

  it('picks the fixed-window key and params', () => {
    expect(
      dialog['refillDescriptionKey'](policy({ type: 'fixed-window', windowSeconds: 10 })),
    ).toBe('admin.rateLimits.policies.refillFixedWindow');
    expect(dialog['refillParams'](policy({ type: 'fixed-window', windowSeconds: 10 }))).toEqual({
      windowSeconds: '10',
    });
  });
});

describe('AdminRateLimitDetailsDialog — rendered with its real template', () => {
  const closeSpy = vi.fn();

  function snapshot(
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

  function render(data: RateLimitTelemetrySnapshot): HTMLElement {
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: DIALOG_DATA, useValue: { snapshot: data } },
        { provide: DialogRef, useValue: { close: closeSpy } },
      ],
    });
    const fixture = TestBed.createComponent(AdminRateLimitDetailsDialog);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const text = (root: HTMLElement) => root.textContent ?? '';

  beforeEach(() => closeSpy.mockReset());

  it('lists every registered policy, quiet or not, with its counters when telemetry is available', () => {
    const root = render(
      snapshot({
        policies: [
          policy({ name: 'usage-stats', acceptedLast24Hours: 1200, rejectedLast24Hours: 3 }),
          policy({ name: 'auth', type: 'fixed-window', windowSeconds: 10 }),
        ],
      }),
    );

    const rows = root.querySelectorAll('ul > li');
    expect(rows).toHaveLength(2);
    expect(text(rows[0] as HTMLElement)).toContain('admin.rateLimits.policies.names.usage-stats');
    expect(text(rows[0] as HTMLElement)).toContain('admin.rateLimits.policies.refillTokenBucket');
    expect(text(rows[0] as HTMLElement)).toContain('admin.rateLimits.policies.accepted24h');
    expect(text(rows[1] as HTMLElement)).toContain('admin.rateLimits.policies.refillFixedWindow');
    expect(root.querySelector('app-notice-banner')).toBeNull();
  });

  it('shows configuration only and warns when telemetry is unavailable — no fabricated zeros', () => {
    const root = render(
      snapshot({ telemetryAvailable: false, policies: [policy({ name: 'usage-stats' })] }),
    );

    expect(root.querySelector('app-notice-banner')).not.toBeNull();
    expect(text(root)).not.toContain('admin.rateLimits.policies.accepted24h');
    expect(text(root)).not.toContain('admin.rateLimits.policies.rejectedMinute');
    expect(text(root)).toContain('admin.rateLimits.lastRejection.unavailable');
    expect(text(root)).toContain('admin.rateLimits.caches.unavailable');
    expect(text(root)).toContain('admin.rateLimits.providers.unavailable');
  });

  it('names the empty states when telemetry is available but nothing was recorded', () => {
    const root = render(snapshot());

    expect(text(root)).toContain('admin.rateLimits.lastRejection.none');
    expect(text(root)).toContain('admin.rateLimits.caches.empty');
    expect(text(root)).toContain('admin.rateLimits.providers.empty');
  });

  it('renders the last rejection with a dash for an absent retry-after', () => {
    const root = render(
      snapshot({
        lastLocalRejection: {
          observedAtUtc: '2026-10-11T12:00:00Z',
          httpMethod: 'GET',
          routeTemplate: '/api/channels/{name}',
          policyName: 'usage-stats',
          partition: 'user:42',
          retryAfterSeconds: null,
        },
      }),
    );

    expect(text(root)).toContain('/api/channels/{name}');
    expect(text(root)).toContain('user:42');
    expect(text(root)).toContain('—');
  });

  it('renders cache counters and provider counters including the header sample', () => {
    const root = render(
      snapshot({
        caches: [
          {
            cacheName: 'permissions',
            hitsLastMinute: 5,
            missesLastMinute: 1,
            hitsLast24Hours: 900,
            missesLast24Hours: 30,
          },
        ],
        providers: [
          {
            providerName: 'twitch',
            callSource: 'live-poll',
            requestsLastMinute: 10,
            requestsLast24Hours: 14400,
            rateLimitedLastMinute: 0,
            rateLimitedLast24Hours: 2,
            lastRetryAfterSeconds: 7,
            lastRateLimitedAtUtc: '2026-10-11T11:00:00Z',
            lastHeaderSample: {
              observedAtUtc: '2026-10-11T11:59:00Z',
              limit: '800',
              remaining: null,
              reset: '12',
            },
          },
          {
            providerName: 'seven-tv',
            callSource: 'resync',
            requestsLastMinute: 0,
            requestsLast24Hours: 0,
            rateLimitedLastMinute: 0,
            rateLimitedLast24Hours: 0,
            lastRetryAfterSeconds: null,
            lastRateLimitedAtUtc: null,
            lastHeaderSample: null,
          },
        ],
      }),
    );

    expect(text(root)).toContain('admin.rateLimits.caches.names.permissions');
    expect(text(root)).toContain('admin.rateLimits.providers.names.twitch');
    expect(text(root)).toContain('admin.rateLimits.providers.callSources.live-poll');
    expect(text(root)).toContain('7 s');
    // Only the provider that has a sample gets a sample line.
    expect(text(root).match(/admin\.rateLimits\.providers\.headerSample\b/g)).toHaveLength(1);
  });

  it('closes its dialog reference from the close button', () => {
    const root = render(snapshot());
    const button = Array.from(root.querySelectorAll('button')).find((b) =>
      (b.textContent ?? '').includes('common.close'),
    );

    button?.click();

    expect(closeSpy).toHaveBeenCalledOnce();
  });
});

describe('AdminRateLimitDetailsDialog — formatting helpers', () => {
  let dialog: AdminRateLimitDetailsDialog;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: DIALOG_DATA, useValue: { snapshot: {} } },
        { provide: DialogRef, useValue: { close: () => undefined } },
      ],
    });
    dialog = TestBed.createComponent(AdminRateLimitDetailsDialog).componentInstance;
  });

  it('shows a dash for absent numbers, dates and retry-after values', () => {
    expect(dialog['formatNumber'](null)).toBe('—');
    expect(dialog['formatDateTime'](null)).toBe('—');
    expect(dialog['formatRetryAfterSeconds'](null)).toBe('—');
  });

  it('formats real values, including a real zero', () => {
    expect(dialog['formatNumber'](0)).toBe('0');
    expect(dialog['formatRetryAfterSeconds'](0)).toBe('0 s');
    expect(dialog['formatDateTime']('2026-10-11T12:00:00Z')).not.toBe('—');
  });

  it('fills missing header-sample fields with a dash', () => {
    expect(
      dialog['headerSampleParams']({
        observedAtUtc: '2026-10-11T12:00:00Z',
        limit: '800',
        remaining: null,
        reset: null,
      }),
    ).toMatchObject({ limit: '800', remaining: '—', reset: '—' });
  });
});
