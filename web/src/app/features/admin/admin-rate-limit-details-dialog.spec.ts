import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { beforeEach, describe, expect, it } from 'vitest';

import { RateLimitPolicySnapshot } from '../../core/admin/admin.model';
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
