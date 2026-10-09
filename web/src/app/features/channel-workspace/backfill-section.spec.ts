import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Observable, of, Subject, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { BackfillOption, BackfillRun, BackfillStatus } from '../../core/channels/backfill.model';
import { BackfillService } from '../../core/channels/backfill.service';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { EmoteSetListResponse } from '../../core/seven-tv/seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { BackfillSection, backfillStartErrorKey, lastDayOf } from './backfill-section';

class FakeEventSource {
  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  close(): void {
    /* no-op */
  }
}

function option(months: number, over: Partial<BackfillOption> = {}): BackfillOption {
  return {
    months,
    windowFrom: '2026-07-08',
    windowTo: '2026-10-08',
    days: 92,
    weeks: 14,
    available: true,
    reason: null,
    ...over,
  };
}

function status(over: Partial<BackfillStatus> = {}): BackfillStatus {
  return {
    countingSince: '2026-10-08',
    archive: { name: 'logs.cyex.app', url: 'https://logs.cyex.app/' },
    requestDelaySeconds: 10,
    options: [option(1), option(3), option(6)],
    activeEmoteSetId: 'set-active',
    coverage: [],
    activeRun: null,
    lastRun: null,
    importedFrom: null,
    importedTo: null,
    importedContiguous: false,
    cooldownUntilUtc: null,
    ...over,
  };
}

function set(id: string, over: Partial<EmoteSetListResponse['sets'][number]> = {}) {
  return {
    id,
    name: id.toUpperCase(),
    capacity: 1000,
    kind: 'NORMAL',
    isActive: false,
    isPersonal: false,
    ownerDisplayName: null,
    observations: [],
    ...over,
  };
}

const LIST: EmoteSetListResponse = {
  activeEmoteSetId: 'set-active',
  sets: [
    set('set-active', { isActive: true }),
    set('set-other'),
    set('set-personal', { isPersonal: true }),
  ],
};

const RUN = { id: 1, status: 'queued' } as BackfillRun;

function httpError(status: number, errorCode?: string): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: errorCode ? { errorCode } : null });
}

describe('BackfillSection (start half)', () => {
  let getStatus: ReturnType<typeof vi.fn<(channel: string) => Observable<BackfillStatus>>>;
  let start: ReturnType<
    typeof vi.fn<(channel: string, setId: string, months: number) => Observable<BackfillRun>>
  >;
  let listSets: ReturnType<typeof vi.fn<(channel: string) => Observable<EmoteSetListResponse>>>;

  async function create(settle = true): Promise<ComponentFixture<BackfillSection>> {
    TestBed.configureTestingModule({
      imports: [
        BackfillSection,
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: BackfillService, useValue: { getStatus, start } },
        { provide: SevenTvEmoteSetService, useValue: { listChannelEmoteSets: listSets } },
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: () => new FakeEventSource() as unknown as EventSource,
        },
      ],
    });
    const fixture = TestBed.createComponent(BackfillSection);
    fixture.componentRef.setInput('channelName', 'sensitron');
    fixture.detectChanges();
    if (settle) {
      await fixture.whenStable();
    } else {
      // A never-answering list keeps the zone unstable; let the status settle on its own.
      await new Promise((resolve) => setTimeout(resolve));
    }
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    getStatus = vi.fn(() => of(status()));
    start = vi.fn(() => of(RUN));
    listSets = vi.fn(() => of(LIST));
  });

  it('preselects the active set and offers every non-personal set', async () => {
    const section = (await create()).componentInstance;

    expect(section.preselectedSetId()).toBe('set-active');
    expect(section.selectedSetId()).toBe('set-active');
    expect(section.offeredSets().map((s) => s.id)).toEqual(['set-active', 'set-other']);
  });

  it('preselects nothing and blocks start while no sync has completed ("" on the wire)', async () => {
    getStatus.mockReturnValue(of(status({ activeEmoteSetId: '' })));
    const section = (await create()).componentInstance;

    expect(section.preselectedSetId()).toBeNull();
    expect(section.selectedSetId()).toBeNull();
    expect(section.canStart()).toBe(false);
  });

  it('does not preselect an active set that is personal (not offered)', async () => {
    getStatus.mockReturnValue(of(status({ activeEmoteSetId: 'set-personal' })));
    const section = (await create()).componentInstance;

    expect(section.selectedSetId()).toBeNull();
    expect(section.canStart()).toBe(false);
  });

  it('can start with the active set and the first available window', async () => {
    const section = (await create()).componentInstance;

    expect(section.selectedMonths()).toBe(1);
    expect(section.canStart()).toBe(true);
  });

  it('falls to the first available option when the options before it are unavailable', async () => {
    getStatus.mockReturnValue(
      of(
        status({
          options: [
            option(1, { available: false, reason: 'no_days_before_counting' }),
            option(3),
            option(6),
          ],
        }),
      ),
    );
    const section = (await create()).componentInstance;

    const views = section.optionViews();
    expect(views[0].available).toBe(false);
    expect(views[0].reasonKey).toBe('backfill.reasons.noDaysBeforeCounting');
    expect(views[1].reasonKey).toBeNull();
    expect(section.selectedMonths()).toBe(3);
  });

  it('gives an unknown reason a generic sentence and cannot start without any available option', async () => {
    getStatus.mockReturnValue(
      of(status({ options: [option(1, { available: false, reason: 'something_new' })] })),
    );
    const section = (await create()).componentInstance;

    expect(section.optionViews()[0].reasonKey).toBe('backfill.reasons.unknown');
    expect(section.selectedMonths()).toBeNull();
    expect(section.canStart()).toBe(false);
  });

  it('cannot start while a run is active', async () => {
    getStatus.mockReturnValue(of(status({ activeRun: RUN })));
    const section = (await create()).componentInstance;

    expect(section.canStart()).toBe(false);
  });

  it('locks the picker and start when the set list is unreadable, and names the reason', async () => {
    listSets.mockReturnValue(
      throwError(() => httpError(503, 'foreign_channel_seventv_unavailable')),
    );
    const section = (await create()).componentInstance;

    expect(section.setListLoaded()).toBe(false);
    expect(section.setListErrorKey()).toBe('errors.api.foreign_channel_seventv_unavailable');
    expect(section.canStart()).toBe(false);
  });

  it('keeps start locked while the set list is still loading', async () => {
    listSets.mockReturnValue(new Subject<EmoteSetListResponse>());
    const section = (await create(false)).componentInstance;

    expect(section.setListLoaded()).toBe(false);
    expect(section.setListErrorKey()).toBeNull();
    expect(section.canStart()).toBe(false);
  });

  it('starts with the id the user picked, not the preselected one, and refetches the status', async () => {
    const fixture = await create();
    const select = fixture.nativeElement.querySelector('select') as HTMLSelectElement;
    select.value = 'set-other';
    select.dispatchEvent(new Event('change'));
    fixture.componentInstance['selectedMonthsChoice'].set(3);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();

    await fixture.whenStable();

    expect(start).toHaveBeenCalledWith('sensitron', 'set-other', 3);
    expect(typeof start.mock.calls[0][2]).toBe('number');
    expect(getStatus).toHaveBeenCalledTimes(2);
  });

  it('shows the translated error of a failed start and refetches after a lost race', async () => {
    start.mockReturnValue(throwError(() => httpError(409, 'backfill_already_active')));
    const fixture = await create();

    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();

    await fixture.whenStable();

    expect(fixture.componentInstance['startErrorKey']()).toBe('errors.api.backfill_already_active');
    expect(getStatus).toHaveBeenCalledTimes(2);
  });

  it('does not refetch after an ordinary rejection', async () => {
    start.mockReturnValue(throwError(() => httpError(409, 'backfill_set_empty')));
    const fixture = await create();

    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();

    expect(fixture.componentInstance['startErrorKey']()).toBe('errors.api.backfill_set_empty');
    expect(getStatus).toHaveBeenCalledTimes(1);
  });
});

describe('backfillStartErrorKey', () => {
  it('gives channel_excluded the backfill-specific sentence', () => {
    expect(backfillStartErrorKey(httpError(409, 'channel_excluded'))).toBe(
      'backfill.errors.channelExcluded',
    );
  });

  it('keeps the generic mapping for every other code and for bare statuses', () => {
    expect(backfillStartErrorKey(httpError(409, 'channel_not_joined'))).toBe(
      'errors.api.channel_not_joined',
    );
    expect(backfillStartErrorKey(httpError(503))).toBe('errors.status.server');
    expect(backfillStartErrorKey(httpError(0))).toBe('errors.status.offline');
  });
});

describe('lastDayOf', () => {
  it('turns the exclusive window end into the last imported day, across month and year ends', () => {
    expect(lastDayOf('2026-10-08')).toBe('2026-10-07');
    expect(lastDayOf('2026-10-01')).toBe('2026-09-30');
    expect(lastDayOf('2027-01-01')).toBe('2026-12-31');
  });
});
