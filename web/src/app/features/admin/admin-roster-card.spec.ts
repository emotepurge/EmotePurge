import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { NEVER, Observable, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { AdminRoster } from '../../core/admin/admin.model';
import { AdminService } from '../../core/admin/admin.service';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { AdminRosterCard } from './admin-roster-card';

/**
 * jsdom ships no `EventSource` at all (same stand-in as usage-stats-page.spec.ts and
 * live-reload.spec.ts). The constructor subscribes to the admin live stream immediately on
 * construction, so this must be provided before `TestBed.createComponent` runs even though this
 * spec never emits an event on it — none of the covered decisions depend on the live push.
 */
class FakeEventSource {
  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  constructor(readonly url: string) {}
  close(): void {
    /* no-op */
  }
}

/** A healthy, fully-reconciled snapshot — every test overrides only what it needs to change. */
function roster(overrides: Partial<AdminRoster> = {}): AdminRoster {
  return {
    snapshotAvailable: true,
    trackedChannelCount: 10,
    ceilings: {
      twitchConcurrentChannelLimit: 100,
      maxActiveChannels: 80,
    },
    bootRecoveryCompleted: true,
    ageSeconds: 0,
    ircConfirmedCount: 10,
    sevenTvAcknowledgedCount: 10,
    unknownToDatabaseTotal: 0,
    ...overrides,
  };
}

describe('AdminRosterCard', () => {
  let fixture: ComponentFixture<AdminRosterCard>;
  let component: AdminRosterCard;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        AdminRosterCard,
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
    }).compileComponents();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  /** Stubs `AdminService.getRoster()` directly rather than going through `HttpTestingController` —
   *  the decisions under test all live downstream of the resource's value, so what matters is the
   *  shape of `AdminRoster` it resolves (or fails) with, not the wire request. */
  function render(stream: Observable<AdminRoster>): void {
    vi.spyOn(TestBed.inject(AdminService), 'getRoster').mockReturnValue(stream);
    fixture = TestBed.createComponent(AdminRosterCard);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  describe('capacity (our cap as denominator, 90 / 100 thresholds)', () => {
    function withTracked(tracked: number, max = 100): AdminRoster {
      return roster({
        trackedChannelCount: tracked,
        ceilings: { twitchConcurrentChannelLimit: 100, maxActiveChannels: max },
      });
    }

    it('is silent below 90 %', () => {
      render(of(withTracked(89)));

      expect(component['capacityTone']()).toBe('success');
      expect(component['capacityWarningKey']()).toBeNull();
    });

    it('warns exactly at 90 %', () => {
      render(of(withTracked(90)));

      expect(component['capacityTone']()).toBe('warning');
      expect(component['capacityWarningKey']()).toBe('admin.roster.capacity.near');
    });

    it('reports "reached" exactly at 100 %', () => {
      render(of(withTracked(100)));

      expect(component['capacityTone']()).toBe('danger');
      expect(component['capacityWarningKey']()).toBe('admin.roster.capacity.reached');
    });

    it('reports "over" past the cap and caps the bar width at 100', () => {
      render(of(withTracked(130)));

      expect(component['capacityWarningKey']()).toBe('admin.roster.capacity.over');
      expect(component['capacityPercent']()).toBe(100);
    });

    it('grades nothing without a usable denominator', () => {
      render(of(withTracked(10, 0)));

      expect(component['capacityTone']()).toBeNull();
      expect(component['capacityWarningKey']()).toBeNull();
      expect(component['capacityPercent']()).toBe(0);
    });

    it('exposes the bar with an accessible name and the cap as its maximum', () => {
      render(of(withTracked(50, 80)));

      const bar = (fixture.nativeElement as HTMLElement).querySelector('[role="progressbar"]');
      expect(bar?.getAttribute('aria-label')).toBe('admin.roster.capacity.label');
      expect(bar?.getAttribute('aria-valuemax')).toBe('80');
      expect(bar?.getAttribute('aria-valuenow')).toBe('50');
    });

    it('is still shown when no snapshot exists (the counts come from the database)', () => {
      render(of(roster({ snapshotAvailable: false })));

      expect(
        (fixture.nativeElement as HTMLElement).querySelector('[role="progressbar"]'),
      ).not.toBeNull();
    });
  });

  describe('statusKey (priority order across the whole snapshot)', () => {
    it('is unknown before the roster has resolved at all', () => {
      render(NEVER);

      expect(component['statusKey']()).toBe('unknown');
    });

    it('is unavailable when the snapshot itself is missing', () => {
      render(of(roster({ snapshotAvailable: false })));

      expect(component['statusKey']()).toBe('unavailable');
    });

    it('reports starting during boot recovery, ahead of what would otherwise read as incomplete', () => {
      render(
        of(
          roster({
            bootRecoveryCompleted: false,
            ircConfirmedCount: 0,
            sevenTvAcknowledgedCount: 0,
          }),
        ),
      );

      expect(component['statusKey']()).toBe('starting');
    });

    it('reports failing from joinHealth, ahead of boot recovery', () => {
      render(
        of(
          roster({
            bootRecoveryCompleted: false,
            joinHealth: { status: 'failing', alarmingTotal: 1 },
          }),
        ),
      );

      expect(component['statusKey']()).toBe('failing');
      expect(component['tone']()).toBe('danger');
    });

    it('reports starting from joinHealth while boot recovery is done', () => {
      render(of(roster({ joinHealth: { status: 'starting' } })));

      expect(component['statusKey']()).toBe('starting');
    });

    it('reports unavailable when the join evaluation is unavailable, even with a snapshot', () => {
      render(of(roster({ joinHealth: { status: 'unavailable' } })));

      expect(component['statusKey']()).toBe('unavailable');
    });

    it('lets stale outrank failing', () => {
      render(of(roster({ ageSeconds: 400, joinHealth: { status: 'failing' } })));

      expect(component['statusKey']()).toBe('stale');
    });

    it('reports stale ahead of what would otherwise read as complete', () => {
      render(
        of(
          roster({
            ageSeconds: 300,
            ircConfirmedCount: 10,
            sevenTvAcknowledgedCount: 10,
            unknownToDatabaseTotal: 0,
          }),
        ),
      );

      expect(component['statusKey']()).toBe('stale');
    });

    it('reports complete once every count reconciles and nothing is stale', () => {
      render(
        of(
          roster({
            ageSeconds: 50,
            ircConfirmedCount: 10,
            sevenTvAcknowledgedCount: 10,
            unknownToDatabaseTotal: 0,
          }),
        ),
      );

      expect(component['statusKey']()).toBe('complete');
      expect(component['tone']()).toBe('ok');
    });

    it('reports incomplete on an IRC deficit', () => {
      render(of(roster({ ircConfirmedCount: 8, sevenTvAcknowledgedCount: 10 })));

      expect(component['statusKey']()).toBe('incomplete');
    });

    it('reports incomplete on a channel unknown to the database, even with both counts reconciled', () => {
      render(
        of(
          roster({
            ircConfirmedCount: 10,
            sevenTvAcknowledgedCount: 10,
            unknownToDatabaseTotal: 1,
          }),
        ),
      );

      expect(component['statusKey']()).toBe('incomplete');
      expect(component['tone']()).toBe('warning');
    });
  });

  describe('isStale', () => {
    it('is false when ageSeconds is absent', () => {
      render(of(roster({ ageSeconds: undefined })));

      expect(component['isStale']()).toBe(false);
    });

    it('is false exactly at the stale threshold', () => {
      render(of(roster({ ageSeconds: 180 })));

      expect(component['isStale']()).toBe(false);
    });

    it('is true just past the stale threshold', () => {
      render(of(roster({ ageSeconds: 181 })));

      expect(component['isStale']()).toBe(true);
    });
  });

  describe('errorMessage', () => {
    it('is null while there is no error', () => {
      render(of(roster()));

      expect(component['errorMessage']()).toBeNull();
    });

    it('translates an HttpErrorResponse via the shared api-error mapping', () => {
      render(
        throwError(
          () =>
            new HttpErrorResponse({
              status: 404,
              error: { errorCode: 'channel_not_found' },
            }),
        ),
      );

      expect(component['errorMessage']()).toBe('errors.api.channel_not_found');
    });

    it('stays null for a non-HTTP failure rather than inventing a generic message', () => {
      render(throwError(() => new Error('boom')));

      expect(component['errorMessage']()).toBeNull();
    });
  });

  describe('missing-channel lists default to empty rather than undefined', () => {
    it('defaults all three lists to [] when the snapshot omits them', () => {
      render(of(roster()));

      expect(component['notInWorker']()).toEqual([]);
      expect(component['missingFromSevenTv']()).toEqual([]);
      expect(component['unknownToDatabase']()).toEqual([]);
    });

    it('passes the lists through unchanged when present', () => {
      render(
        of(
          roster({
            missingFromSevenTv: ['b'],
            unknownToDatabase: ['c'],
          }),
        ),
      );

      expect(component['missingFromSevenTv']()).toEqual(['b']);
      expect(component['unknownToDatabase']()).toEqual(['c']);
    });
  });

  describe('join status list and "not in the worker" section', () => {
    const row = (
      channelName: string,
      overrides: Partial<NonNullable<AdminRoster['ircUnjoined']>[number]> = {},
    ) => ({
      channelName,
      unconfirmedSeconds: 30,
      reason: 'noConfirmation' as const,
      overdue: false,
      countsForAlarm: true,
      ...overrides,
    });

    it('keeps the server order of the join-status rows', () => {
      render(
        of(
          roster({
            ircUnjoined: [
              row('alarming', { overdue: true, unconfirmedSeconds: 900 }),
              row('excluded', { reason: 'banned', overdue: true, countsForAlarm: false }),
              row('waiting'),
            ],
            ircUnjoinedTotal: 3,
          }),
        ),
      );

      expect(component['unjoinedRows']().map((r) => r.name)).toEqual([
        'alarming',
        'excluded',
        'waiting',
      ]);
    });

    it('states overdue, waiting and excluded as text and marks only the alarming row', () => {
      // As the server sends them: within the grace `countsForAlarm` is always false.
      render(
        of(
          roster({
            ircUnjoined: [
              row('a', { overdue: true }),
              row('b', { countsForAlarm: false }),
              row('c', { overdue: true, countsForAlarm: false, reason: 'channelSuspended' }),
            ],
            ircUnjoinedTotal: 3,
          }),
        ),
      );

      const rows = component['unjoinedRows']();
      expect(rows.map((r) => r.stateKey)).toEqual([
        'admin.roster.unjoined.overdue',
        'admin.roster.unjoined.waiting',
        'admin.roster.unjoined.excluded',
      ]);
      expect(rows.map((r) => r.alarming)).toEqual([true, false, false]);
    });

    it('shows a rejected join that is still within its grace as waiting, not as excluded', () => {
      render(
        of(
          roster({
            ircUnjoined: [row('fresh', { reason: 'banned', countsForAlarm: false })],
            ircUnjoinedTotal: 1,
          }),
        ),
      );

      const [only] = component['unjoinedRows']();
      expect(only.stateKey).toBe('admin.roster.unjoined.waiting');
      expect(only.alarming).toBe(false);
    });

    it('falls back to the generic reason for a missing or unknown code', () => {
      render(
        of(
          roster({
            ircUnjoined: [
              row('a', { reason: null }),
              row('b', { reason: 'somethingNew' as never }),
            ],
            ircUnjoinedTotal: 2,
          }),
        ),
      );

      expect(component['unjoinedRows']().map((r) => r.reasonKey)).toEqual([
        'admin.roster.joinFailure.reason.unknown',
        'admin.roster.joinFailure.reason.unknown',
      ]);
    });

    it('switches the duration to minutes from 60 s on', () => {
      render(
        of(
          roster({
            ircUnjoined: [
              row('a', { unconfirmedSeconds: 59 }),
              row('b', { unconfirmedSeconds: 125 }),
            ],
            ircUnjoinedTotal: 2,
          }),
        ),
      );

      const rows = component['unjoinedRows']();
      expect([rows[0].durationKey, rows[0].durationValue]).toEqual([
        'admin.roster.unjoined.seconds',
        59,
      ]);
      expect([rows[1].durationKey, rows[1].durationValue]).toEqual([
        'admin.roster.unjoined.minutes',
        2,
      ]);
    });

    it('reports the rows beyond a capped list', () => {
      render(of(roster({ ircUnjoined: [row('a')], ircUnjoinedTotal: 120 })));

      expect(component['unjoinedMore']()).toBe(119);
    });

    const missing = (channelName: string, overdue: boolean) => ({
      channelName,
      waitingSeconds: overdue ? 900 : 30,
      overdue,
    });

    it("shows exactly the server's missing-intent list in its order, independent of the join status", () => {
      // With a capped join-status list the client cannot tell which of the remaining channels the
      // worker holds a wish for; the server's list is the only source.
      render(
        of(
          roster({
            ircUnjoined: [row('wished')],
            ircUnjoinedTotal: 80,
            missingIntent: [missing('late', true), missing('ghost', true), missing('fresh', false)],
            missingIntentTotal: 3,
          }),
        ),
      );

      expect(component['notInWorker']()).toEqual([
        { name: 'late', overdue: true },
        { name: 'ghost', overdue: true },
        { name: 'fresh', overdue: false },
      ]);
      expect(component['notInWorkerTotal']()).toBe(3);
      expect(component['notInWorkerTruncated']()).toBe(false);
    });

    it('flags a truncated not-in-worker list and counts its untruncated total', () => {
      render(of(roster({ missingIntent: [missing('a', true)], missingIntentTotal: 40 })));

      expect(component['notInWorkerTotal']()).toBe(40);
      expect(component['notInWorkerTruncated']()).toBe(true);
    });
  });

  describe('freshness (age advances from receipt, refreshed once a minute while visible)', () => {
    let visibility: string;

    beforeEach(() => {
      vi.useFakeTimers();
      visibility = 'visible';
      vi.spyOn(document, 'visibilityState', 'get').mockImplementation(
        () => visibility as DocumentVisibilityState,
      );
    });

    afterEach(() => {
      vi.useRealTimers();
    });

    function renderWithSpy(): ReturnType<typeof vi.spyOn> {
      const spy = vi
        .spyOn(TestBed.inject(AdminService), 'getRoster')
        .mockImplementation(() => of(roster({ ageSeconds: 10 })));
      fixture = TestBed.createComponent(AdminRosterCard);
      component = fixture.componentInstance;
      fixture.detectChanges();
      return spy;
    }

    it('lets the visible age grow with elapsed time and turns stale after 180 s when no refresh lands', () => {
      vi.spyOn(TestBed.inject(AdminService), 'getRoster').mockReturnValue(
        of(roster({ ageSeconds: 10 })),
      );
      fixture = TestBed.createComponent(AdminRosterCard);
      component = fixture.componentInstance;
      fixture.detectChanges();
      // No detectChanges from here on: the resource's reload effect never flushes, so only elapsed
      // time may move the age in this case.
      expect(component['statusKey']()).toBe('complete');

      vi.advanceTimersByTime(165_000);
      expect(component['ageSeconds']()).toBeGreaterThanOrEqual(175);
      vi.advanceTimersByTime(20_000);
      expect(component['ageSeconds']()).toBeGreaterThan(180);
    });

    it('reloads once a minute while visible and not at all while hidden', () => {
      const spy = renderWithSpy();
      expect(spy).toHaveBeenCalledTimes(1);

      vi.advanceTimersByTime(60_000);
      fixture.detectChanges();
      expect(spy).toHaveBeenCalledTimes(2);

      visibility = 'hidden';
      document.dispatchEvent(new Event('visibilitychange'));
      fixture.detectChanges();
      vi.advanceTimersByTime(180_000);
      fixture.detectChanges();
      expect(spy).toHaveBeenCalledTimes(2);
    });
  });

  describe('reload()', () => {
    it('re-issues the request against AdminService and picks up the new answer', () => {
      const getRoster = vi
        .spyOn(TestBed.inject(AdminService), 'getRoster')
        .mockReturnValueOnce(of(roster({ trackedChannelCount: 10 })))
        .mockReturnValueOnce(of(roster({ trackedChannelCount: 20 })));

      fixture = TestBed.createComponent(AdminRosterCard);
      component = fixture.componentInstance;
      fixture.detectChanges();
      expect(component['roster']()?.trackedChannelCount).toBe(10);

      component.reload();
      fixture.detectChanges();

      expect(getRoster).toHaveBeenCalledTimes(2);
      expect(component['roster']()?.trackedChannelCount).toBe(20);
    });
  });
});
