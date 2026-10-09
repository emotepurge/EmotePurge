import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { describe, expect, it } from 'vitest';

import { BackfillRun } from '../../core/channels/backfill.model';
import de from '../../../../public/i18n/de.json';
import en from '../../../../public/i18n/en.json';
import {
  BACKFILL_RUN_ERROR_CODES,
  BACKFILL_RUN_STATUSES,
  BackfillRunStatusView,
  backfillRunErrorKey,
  backfillRunStatusKey,
} from './backfill-run-status';

function run(over: Partial<BackfillRun> = {}): BackfillRun {
  return {
    id: 1,
    status: 'running',
    requestedMonths: 3,
    windowFrom: '2026-07-08',
    windowTo: '2026-10-08',
    weeksDone: 3,
    weeksTotal: 14,
    queuePosition: null,
    pausedUntilUtc: null,
    requestedAtUtc: '2026-10-09T18:02:11Z',
    startedAtUtc: '2026-10-09T18:03:00Z',
    finishedAtUtc: null,
    requestedByLogin: 'sensitron',
    emoteSetId: 'set-1',
    emoteSetName: 'Halloween',
    emoteCount: 10,
    errorCode: null,
    errorHttpStatus: null,
    bytesReceived: 0,
    messagesRead: 0,
    ...over,
  };
}

function create(
  value: BackfillRun,
  kind: 'active' | 'last' = 'active',
  cooldownUntilUtc: string | null = null,
) {
  // Several views in one test: each starts from a clean module.
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [
      BackfillRunStatusView,
      TranslocoTestingModule.forRoot({
        langs: { de: {} },
        translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
      }),
    ],
  });
  const fixture = TestBed.createComponent(BackfillRunStatusView);
  fixture.componentRef.setInput('run', value);
  fixture.componentRef.setInput('kind', kind);
  fixture.componentRef.setInput('cooldownUntilUtc', cooldownUntilUtc);
  fixture.detectChanges();
  return fixture;
}

describe('BackfillRunStatusView', () => {
  it('turns weeksDone of weeksTotal into a fraction, and treats a run with no weeks as not started', () => {
    expect(create(run({ weeksDone: 7, weeksTotal: 14 })).componentInstance.progressFraction()).toBe(
      0.5,
    );
    expect(create(run({ weeksDone: 0, weeksTotal: 0 })).componentInstance.progressFraction()).toBe(
      0,
    );
  });

  it('maps every status to its own key', () => {
    for (const status of BACKFILL_RUN_STATUSES) {
      expect(backfillRunStatusKey(status)).toBe(`backfill.status.${status}`);
    }
    expect(backfillRunStatusKey('from_the_future')).toBe('backfill.status.unknown');
  });

  it('shows the queue position of a queued run only', () => {
    expect(
      create(run({ status: 'queued', queuePosition: 2 })).componentInstance.queuePosition(),
    ).toBe(2);
    expect(
      create(run({ status: 'running', queuePosition: 2 })).componentInstance.queuePosition(),
    ).toBeNull();
  });

  it('says the archive asked us to wait for a paused run, until the later of deadline and cooldown', () => {
    const view = create(
      run({ status: 'paused', pausedUntilUtc: '2026-10-09T19:00:00Z' }),
      'active',
      '2026-10-09T20:00:00Z',
    ).componentInstance;

    expect(view.waitUntil()).toBe('2026-10-09T20:00:00Z');
  });

  it("keeps the paused run's own deadline when it is the later one", () => {
    const view = create(
      run({ status: 'paused', pausedUntilUtc: '2026-10-09T21:00:00Z' }),
      'active',
      '2026-10-09T20:00:00Z',
    ).componentInstance;

    expect(view.waitUntil()).toBe('2026-10-09T21:00:00Z');
  });

  it('uses whichever of the two is set for a paused run', () => {
    expect(
      create(
        run({ status: 'paused', pausedUntilUtc: null }),
        'active',
        '2026-10-09T20:00:00Z',
      ).componentInstance.waitUntil(),
    ).toBe('2026-10-09T20:00:00Z');
  });

  it('says it for a queued run too while the provider cooldown is set, and for nothing else', () => {
    const cooldown = '2026-10-09T20:00:00Z';

    expect(
      create(run({ status: 'queued' }), 'active', cooldown).componentInstance.waitUntil(),
    ).toBe(cooldown);
    expect(
      create(run({ status: 'queued' }), 'active', null).componentInstance.waitUntil(),
    ).toBeNull();
    expect(
      create(run({ status: 'running' }), 'active', cooldown).componentInstance.waitUntil(),
    ).toBeNull();
    expect(
      create(run({ status: 'completed' }), 'last', cooldown).componentInstance.waitUntil(),
    ).toBeNull();
  });

  it('names the set by name, by id when it has none, and shows the day before the exclusive window end', () => {
    const named = create(run()).componentInstance;
    const unnamed = create(run({ emoteSetName: null })).componentInstance;

    expect(named.setLabel()).toBe('Halloween');
    expect(unnamed.setLabel()).toBe('set-1');
    expect(named.lastDay()).toBe('2026-10-07');
  });

  it('shows the progress bar with a name, filled by the progress fraction, and no live region of its own', () => {
    const element = create(run()).nativeElement as HTMLElement;

    expect(element.querySelector('[role="status"]')).toBeNull();
    const bar = element.querySelector('progress') as HTMLProgressElement;
    expect(bar.position).toBeCloseTo(3 / 14);
    expect(bar.getAttribute('aria-label')).toBeTruthy();
  });

  it('has neither progress bar nor live status for the last run', () => {
    const element = create(run({ status: 'completed' }), 'last').nativeElement as HTMLElement;

    expect(element.querySelector('progress')).toBeNull();
    expect(element.querySelector('[role="status"]')).toBeNull();
  });

  it('gives a failed run its translated error key', () => {
    const view = create(
      run({ status: 'failed', errorCode: 'transport_failure' }),
      'last',
    ).componentInstance;

    expect(view.errorKey()).toBe('backfill.errors.transport_failure');
  });
});

describe('backfillRunErrorKey', () => {
  it('has no sentence for a run without an error', () => {
    expect(backfillRunErrorKey(null)).toBeNull();
  });

  it('falls back to a generic sentence for a code it does not know, never to the raw code', () => {
    expect(backfillRunErrorKey('brand_new_code')).toBe('backfill.errors.unknown');
  });
});

describe('run error vocabulary (spec 2026-10-09, §4.6)', () => {
  const locales = { de, en } as Record<string, { backfill: { errors: Record<string, string> } }>;

  it.each(Object.keys(locales))(
    '%s has a sentence for every error code and the fallback',
    (name) => {
      const translations = locales[name].backfill.errors;
      const missing = [...BACKFILL_RUN_ERROR_CODES, 'unknown'].filter(
        (code) => !translations[code]?.trim(),
      );

      expect(missing).toEqual([]);
    },
  );

  it.each(Object.keys(locales))('%s has a name for every run status', (name) => {
    const status = (locales[name].backfill as unknown as { status: Record<string, string> }).status;

    expect([...BACKFILL_RUN_STATUSES, 'unknown'].filter((key) => !status[key]?.trim())).toEqual([]);
  });

  it('carries no error sentence outside the vocabulary', () => {
    for (const locale of Object.values(locales)) {
      const stray = Object.keys(locale.backfill.errors).filter(
        (code) =>
          code !== 'unknown' && !(BACKFILL_RUN_ERROR_CODES as readonly string[]).includes(code),
      );
      expect(stray).toEqual([]);
    }
  });
});
