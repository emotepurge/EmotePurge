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
    ceilings: { twitchConcurrentChannelLimit: 100, twitchJoinBudgetChannels: 100 },
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

  describe('budgetWarningKey (three-way, "exceeded" overrides the tone-based warning)', () => {
    it('reports the exceeded key once over budget, even though that count alone also grades tone "danger"', () => {
      render(
        of(
          roster({
            trackedChannelCount: 81,
            ceilings: { twitchConcurrentChannelLimit: 100, twitchJoinBudgetChannels: 80 },
          }),
        ),
      );

      // Sanity: without the exceeded-first check, the switch below would already read 'danger'
      // here and this test would not distinguish the override from the plain tone mapping.
      expect(component['budgetTone']()).toBe('danger');
      expect(component['budgetWarningKey']()).toBe('admin.roster.joinBudgetExceeded');
    });

    it('reports the critical key at the danger threshold while still within budget', () => {
      render(
        of(
          roster({
            trackedChannelCount: 96,
            ceilings: { twitchConcurrentChannelLimit: 100, twitchJoinBudgetChannels: 100 },
          }),
        ),
      );

      expect(component['budgetWarningKey']()).toBe('admin.roster.joinBudgetCritical');
    });

    it('reports the warning key in the warn band', () => {
      render(
        of(
          roster({
            trackedChannelCount: 85,
            ceilings: { twitchConcurrentChannelLimit: 100, twitchJoinBudgetChannels: 100 },
          }),
        ),
      );

      expect(component['budgetWarningKey']()).toBe('admin.roster.joinBudgetWarning');
    });

    it('is silent well under budget', () => {
      render(
        of(
          roster({
            trackedChannelCount: 10,
            ceilings: { twitchConcurrentChannelLimit: 100, twitchJoinBudgetChannels: 100 },
          }),
        ),
      );

      expect(component['budgetWarningKey']()).toBeNull();
    });

    it('is silent without a usable denominator (zero budget)', () => {
      render(
        of(
          roster({
            trackedChannelCount: 10,
            ceilings: { twitchConcurrentChannelLimit: 100, twitchJoinBudgetChannels: 0 },
          }),
        ),
      );

      expect(component['budgetWarningKey']()).toBeNull();
    });
  });

  describe('budgetPercent (capped bar width vs. the uncapped grading percentage)', () => {
    it('caps at 100 once over budget, even though the underlying share is higher', () => {
      render(
        of(
          roster({
            trackedChannelCount: 150,
            ceilings: { twitchConcurrentChannelLimit: 100, twitchJoinBudgetChannels: 100 },
          }),
        ),
      );

      expect(component['budgetPercent']()).toBe(100);
    });

    it('is 0 without a usable denominator', () => {
      render(
        of(
          roster({
            trackedChannelCount: 10,
            ceilings: { twitchConcurrentChannelLimit: 100, twitchJoinBudgetChannels: 0 },
          }),
        ),
      );

      expect(component['budgetPercent']()).toBe(0);
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

      expect(component['missingFromIrc']()).toEqual([]);
      expect(component['missingFromSevenTv']()).toEqual([]);
      expect(component['unknownToDatabase']()).toEqual([]);
    });

    it('passes the lists through unchanged when present', () => {
      render(
        of(
          roster({
            missingFromIrc: ['a'],
            missingFromSevenTv: ['b'],
            unknownToDatabase: ['c'],
          }),
        ),
      );

      expect(component['missingFromIrc']()).toEqual(['a']);
      expect(component['missingFromSevenTv']()).toEqual(['b']);
      expect(component['unknownToDatabase']()).toEqual(['c']);
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
