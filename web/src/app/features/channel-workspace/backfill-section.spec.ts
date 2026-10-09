import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { Observable, of, Subject, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  BackfillCoverageInterval,
  BackfillOption,
  BackfillRun,
  BackfillStatus,
} from '../../core/channels/backfill.model';
import { BackfillService } from '../../core/channels/backfill.service';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { EmoteSetListResponse } from '../../core/seven-tv/seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import en from '../../../../public/i18n/en.json';
import { BackfillSection, backfillStartErrorKey } from './backfill-section';

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

const RUN: BackfillRun = {
  id: 1,
  status: 'queued',
  requestedMonths: 3,
  windowFrom: '2026-07-08',
  windowTo: '2026-10-08',
  weeksDone: 0,
  weeksTotal: 14,
  queuePosition: 1,
  pausedUntilUtc: null,
  requestedAtUtc: '2026-10-09T18:02:11Z',
  startedAtUtc: null,
  finishedAtUtc: null,
  requestedByLogin: 'sensitron',
  emoteSetId: 'set-other',
  emoteSetName: 'Other',
  emoteCount: 10,
  errorCode: null,
  errorHttpStatus: null,
  bytesReceived: 0,
  messagesRead: 0,
};

function httpError(status: number, errorCode?: string): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: errorCode ? { errorCode } : null });
}

describe('BackfillSection', () => {
  let getStatus: ReturnType<typeof vi.fn<(channel: string) => Observable<BackfillStatus>>>;
  let start: ReturnType<
    typeof vi.fn<(channel: string, setId: string, months: number) => Observable<BackfillRun>>
  >;
  let listSets: ReturnType<typeof vi.fn<(channel: string) => Observable<EmoteSetListResponse>>>;
  let cancel: ReturnType<typeof vi.fn<(channel: string) => Observable<void>>>;
  /** What the confirm dialog answers; `undefined` = dismissed. */
  let dialogAnswer: boolean | undefined;
  let openDialog: ReturnType<typeof vi.fn>;

  async function create(settle = true): Promise<ComponentFixture<BackfillSection>> {
    TestBed.configureTestingModule({
      imports: [
        BackfillSection,
        TranslocoTestingModule.forRoot({
          langs: { en },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
      providers: [
        { provide: BackfillService, useValue: { getStatus, start, cancel } },
        { provide: Dialog, useValue: { open: openDialog } },
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
    cancel = vi.fn(() => of(undefined));
    dialogAnswer = true;
    openDialog = vi.fn(() => ({ closed: of(dialogAnswer) }));
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

  it('never offers a personal or non-NORMAL set, except the active one', async () => {
    getStatus.mockReturnValue(of(status({ activeEmoteSetId: 'set-personal' })));
    listSets.mockReturnValue(
      of({
        activeEmoteSetId: 'set-personal',
        sets: [
          set('set-active', { kind: 'GLOBAL' }),
          set('set-personal', { isPersonal: true, kind: 'PERSONAL', isActive: true }),
          set('set-other'),
        ],
      }),
    );
    const section = (await create()).componentInstance;

    expect(section.offeredSets().map((s) => s.id)).toEqual(['set-personal', 'set-other']);
    expect(section.selectedSetId()).toBe('set-personal');
  });

  it('adds the active set as a synthetic option when a stale list does not name it', async () => {
    getStatus.mockReturnValue(of(status({ activeEmoteSetId: 'set-new' })));
    const section = (await create()).componentInstance;

    expect(section.offeredSets()[0]).toMatchObject({
      id: 'set-new',
      label: 'set-new',
      isActive: true,
    });
    expect(section.selectedSetId()).toBe('set-new');
    expect(section.canStart()).toBe(true);
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
    expect(views[0].showRange).toBe(false);
    expect(views[1].showRange).toBe(true);
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

  it('keeps start disabled between the 202 and the refetch, so a double click sends one POST', async () => {
    const fixture = await create();
    getStatus.mockReturnValue(new Subject<BackfillStatus>());

    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    button.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.canStart()).toBe(false);
    button.click();
    expect(start).toHaveBeenCalledTimes(1);
  });

  it.each([0, 500, 503])('refetches the status after an unknown outcome (%i)', async (code) => {
    start.mockReturnValue(throwError(() => httpError(code)));
    const fixture = await create();

    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(getStatus).toHaveBeenCalledTimes(2);
  });

  it('clears the start error once the refetched status shows the run', async () => {
    start.mockReturnValue(throwError(() => httpError(409, 'backfill_already_active')));
    const fixture = await create();
    getStatus.mockReturnValue(of(status({ activeRun: RUN })));

    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(fixture.componentInstance['startErrorKey']()).toBeNull();
  });

  it('keeps the last good status and says so when a refetch fails', async () => {
    const fixture = await create();
    getStatus.mockReturnValue(throwError(() => httpError(503)));

    fixture.componentInstance['statusResource'].reload();
    await fixture.whenStable();

    expect(fixture.componentInstance['status']()).not.toBeNull();
    expect(fixture.componentInstance.statusRefetchFailed()).toBe(true);
    expect(fixture.componentInstance.canStart()).toBe(true);
  });

  it('forgets the picks and the start error when the channel changes', async () => {
    const fixture = await create();
    const section = fixture.componentInstance;
    section['setChoice'].set('set-other');
    section['selectedMonthsChoice'].set(3);
    section['startErrorKey'].set('errors.api.backfill_set_empty');
    expect(section.selectedSetId()).toBe('set-other');

    fixture.componentRef.setInput('channelName', 'other');
    fixture.detectChanges();
    await fixture.whenStable();

    expect(section.selectedSetId()).toBe('set-active');
    expect(section.selectedMonths()).toBe(1);
    expect(section['startErrorKey']()).toBeNull();
  });

  it('ignores the answer of a start issued for a channel the user has since left', async () => {
    const pending = new Subject<BackfillRun>();
    start.mockReturnValue(pending);
    const fixture = await create();
    const section = fixture.componentInstance;

    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    fixture.componentRef.setInput('channelName', 'other');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(section['starting']()).toBe(false);
    const callsBefore = getStatus.mock.calls.length;

    pending.next(RUN);
    pending.complete();

    expect(section['status']()?.activeRun).toBeNull();
    expect(section['starting']()).toBe(false);
    expect(getStatus.mock.calls.length).toBe(callsBefore);
  });

  it('does not show the error of a start issued for a channel the user has since left', async () => {
    const pending = new Subject<BackfillRun>();
    start.mockReturnValue(pending);
    const fixture = await create();

    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    fixture.componentRef.setInput('channelName', 'other');
    fixture.detectChanges();
    await fixture.whenStable();
    pending.error(httpError(409, 'backfill_set_empty'));

    expect(fixture.componentInstance['startErrorKey']()).toBeNull();
  });

  it('locks start after a 202 even when the preceding refetch had failed', async () => {
    const fixture = await create();
    getStatus.mockReturnValue(throwError(() => httpError(503)));
    fixture.componentInstance['statusResource'].reload();
    await fixture.whenStable();
    expect(fixture.componentInstance.statusRefetchFailed()).toBe(true);
    getStatus.mockReturnValue(new Subject<BackfillStatus>());

    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    button.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.canStart()).toBe(false);
    button.click();
    expect(start).toHaveBeenCalledTimes(1);
  });

  it('does not bring a lost-race error back when the run later ends', async () => {
    start.mockReturnValue(throwError(() => httpError(409, 'backfill_already_active')));
    const fixture = await create();
    const section = fixture.componentInstance;
    getStatus.mockReturnValue(of(status({ activeRun: { ...RUN, id: 7 } })));

    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(section['startErrorKey']()).toBeNull();

    getStatus.mockReturnValue(of(status({ activeRun: null })));
    section['statusResource'].reload();
    await fixture.whenStable();

    expect(section['status']()?.activeRun).toBeNull();
    expect(section['startErrorKey']()).toBeNull();
  });

  describe('replace warning and the start confirmation', () => {
    const HALLOWEEN = (over: Partial<BackfillCoverageInterval> = {}): BackfillCoverageInterval => ({
      from: '2026-08-01',
      to: '2026-09-01',
      emoteSetId: 'set-other',
      emoteSetName: 'Halloween',
      archiveHost: 'logs.cyex.app',
      ...over,
    });

    it('has nothing to replace without coverage', async () => {
      const section = (await create()).componentInstance;

      expect(section.replaceWarning()).toEqual([]);
    });

    it('names the intervals of other sets inside the selected window only', async () => {
      getStatus.mockReturnValue(
        of(
          status({
            coverage: [
              HALLOWEEN(),
              HALLOWEEN({ emoteSetId: 'set-active', emoteSetName: 'Normal' }),
              HALLOWEEN({ from: '2026-01-01', to: '2026-02-01', emoteSetId: 'set-old' }),
            ],
          }),
        ),
      );
      const section = (await create()).componentInstance;

      // 1 month = the same default window in this fixture; only the Halloween interval is a
      // replacement for the preselected active set.
      expect(section.replaceWarning()).toHaveLength(1);
      expect(section.replaceWarning()[0].set).toBe('Halloween');
    });

    it('names the set by id when its name is unknown and ends the range on the inclusive last day', async () => {
      getStatus.mockReturnValue(
        of(
          status({
            coverage: [HALLOWEEN({ emoteSetName: null, from: '2026-09-01', to: '2026-10-08' })],
          }),
        ),
      );
      const section = (await create()).componentInstance;

      const [sentence] = section.replaceWarning();
      expect(sentence.set).toBe('set-other');
      expect(sentence.to).toBe(section['formatDay']('2026-10-07'));
    });

    it('follows the set the user picks: a set that holds the interval replaces nothing', async () => {
      getStatus.mockReturnValue(of(status({ coverage: [HALLOWEEN()] })));
      const fixture = await create();
      const section = fixture.componentInstance;
      expect(section.replaceWarning()).toHaveLength(1);

      section['setChoice'].set('set-other');

      expect(section.replaceWarning()).toEqual([]);
    });

    it('follows the window: a window that misses the interval replaces nothing', async () => {
      getStatus.mockReturnValue(
        of(
          status({
            options: [option(1, { windowFrom: '2026-09-08', days: 30 }), option(3), option(6)],
            coverage: [HALLOWEEN({ from: '2026-07-08', to: '2026-08-20' })],
          }),
        ),
      );
      const section = (await create()).componentInstance;
      expect(section.selectedMonths()).toBe(1);
      expect(section.replaceWarning()).toEqual([]);

      section['selectedMonthsChoice'].set(3);

      expect(section.replaceWarning()).toHaveLength(1);
    });

    it('starts at once, without a dialog, when nothing would be replaced', async () => {
      const fixture = await create();

      (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();

      expect(openDialog).not.toHaveBeenCalled();
      expect(start).toHaveBeenCalledTimes(1);
    });

    it('asks first when imported days would be replaced, and the dialog repeats the warning sentences', async () => {
      getStatus.mockReturnValue(of(status({ coverage: [HALLOWEEN()] })));
      const fixture = await create();
      const transloco = TestBed.inject(TranslocoService);

      (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();

      expect(openDialog).toHaveBeenCalledTimes(1);
      const data = openDialog.mock.calls[0][1].data as { message: string };
      const inline = fixture.componentInstance
        .replaceWarning()
        .map((sentence) => transloco.translate('backfill.replaceWarning', sentence));
      for (const sentence of inline) {
        expect(data.message).toContain(sentence);
      }
      // ...and the inline notice shows the very same sentences.
      const notice = (fixture.nativeElement as HTMLElement).querySelector('[role="status"]');
      for (const sentence of inline) {
        expect(notice?.textContent).toContain(sentence);
      }
    });

    it('starts after the confirmation and not after a dismissal', async () => {
      getStatus.mockReturnValue(of(status({ coverage: [HALLOWEEN()] })));
      const fixture = await create();
      const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;

      dialogAnswer = undefined;
      button.click();
      dialogAnswer = false;
      button.click();
      expect(start).not.toHaveBeenCalled();

      dialogAnswer = true;
      button.click();
      expect(start).toHaveBeenCalledTimes(1);
    });
  });

  describe('cancel', () => {
    const RUNNING = { ...RUN, id: 5, status: 'running' } as BackfillRun;

    function cancelButton(fixture: ComponentFixture<BackfillSection>): HTMLButtonElement | null {
      return (
        ([...fixture.nativeElement.querySelectorAll('button')] as HTMLButtonElement[]).find(
          (button) => button.textContent?.trim() === en.backfill.cancel,
        ) ?? null
      );
    }

    it.each([
      ['queued', true],
      ['running', true],
      ['paused', true],
      ['completed', false],
      ['failed', false],
      ['cancelled', false],
    ] as const)('%s run: cancellable = %s', async (runStatus, expected) => {
      getStatus.mockReturnValue(
        of(status({ activeRun: { ...RUNNING, status: runStatus } as BackfillRun })),
      );
      const fixture = await create();

      expect(fixture.componentInstance.canCancel()).toBe(expected);
      expect(cancelButton(fixture) !== null).toBe(expected);
    });

    it('cannot be cancelled without a run, and the last run has no cancel button', async () => {
      getStatus.mockReturnValue(
        of(status({ lastRun: { ...RUNNING, status: 'completed' } as BackfillRun })),
      );
      const fixture = await create();

      expect(fixture.componentInstance.canCancel()).toBe(false);
      expect(cancelButton(fixture)).toBeNull();
    });

    it('asks first, sends DELETE after the confirmation and refetches the status', async () => {
      getStatus.mockReturnValue(of(status({ activeRun: RUNNING })));
      const fixture = await create();

      cancelButton(fixture)?.click();
      await fixture.whenStable();

      expect(openDialog).toHaveBeenCalledTimes(1);
      expect(cancel).toHaveBeenCalledWith('sensitron');
      expect(getStatus).toHaveBeenCalledTimes(2);
      expect(fixture.componentInstance['cancelErrorKey']()).toBeNull();
    });

    it('sends nothing when the dialog is dismissed or declined', async () => {
      getStatus.mockReturnValue(of(status({ activeRun: RUNNING })));
      const fixture = await create();

      dialogAnswer = undefined;
      cancelButton(fixture)?.click();
      dialogAnswer = false;
      cancelButton(fixture)?.click();

      expect(cancel).not.toHaveBeenCalled();
    });

    it('sends nothing when the run the user was asked about has been replaced meanwhile', async () => {
      getStatus.mockReturnValue(of(status({ activeRun: RUNNING })));
      const fixture = await create();
      const closed = new Subject<boolean>();
      openDialog.mockReturnValue({ closed });

      cancelButton(fixture)?.click();
      getStatus.mockReturnValue(of(status({ activeRun: { ...RUNNING, id: 6 } })));
      fixture.componentInstance['statusResource'].reload();
      await fixture.whenStable();
      closed.next(true);

      expect(cancel).not.toHaveBeenCalled();
    });

    it('treats 404 backfill_no_active_run as "the run ended meanwhile": a silent refetch, no error', async () => {
      getStatus.mockReturnValue(of(status({ activeRun: RUNNING })));
      cancel.mockReturnValue(throwError(() => httpError(404, 'backfill_no_active_run')));
      const fixture = await create();

      cancelButton(fixture)?.click();
      await fixture.whenStable();

      expect(fixture.componentInstance['cancelErrorKey']()).toBeNull();
      expect(getStatus).toHaveBeenCalledTimes(2);
    });

    it('shows the translated error of any other failure and does not refetch after a rejection', async () => {
      getStatus.mockReturnValue(of(status({ activeRun: RUNNING })));
      cancel.mockReturnValue(throwError(() => httpError(403)));
      const fixture = await create();

      cancelButton(fixture)?.click();
      fixture.detectChanges();

      expect(fixture.componentInstance['cancelErrorKey']()).toBe('errors.status.forbidden');
      expect(fixture.nativeElement.querySelector('[role="alert"]')).not.toBeNull();
      expect(getStatus).toHaveBeenCalledTimes(1);
      expect(fixture.componentInstance['cancelling']()).toBe(false);
    });

    it.each([0, 500, 503])('also refetches after an unknown outcome (%i)', async (code) => {
      getStatus.mockReturnValue(of(status({ activeRun: RUNNING })));
      cancel.mockReturnValue(throwError(() => httpError(code)));
      const fixture = await create();

      cancelButton(fixture)?.click();
      await fixture.whenStable();

      expect(fixture.componentInstance['cancelErrorKey']()).not.toBeNull();
      expect(getStatus).toHaveBeenCalledTimes(2);
    });

    it('clears the error on the next attempt', async () => {
      getStatus.mockReturnValue(of(status({ activeRun: RUNNING })));
      cancel.mockReturnValue(throwError(() => httpError(403)));
      const fixture = await create();
      cancelButton(fixture)?.click();
      expect(fixture.componentInstance['cancelErrorKey']()).not.toBeNull();

      cancel.mockReturnValue(new Subject<void>());
      cancelButton(fixture)?.click();

      expect(fixture.componentInstance['cancelErrorKey']()).toBeNull();
      expect(fixture.componentInstance['cancelling']()).toBe(true);
    });

    it('locks the button while the DELETE is pending, so a double click sends one', async () => {
      getStatus.mockReturnValue(of(status({ activeRun: RUNNING })));
      cancel.mockReturnValue(new Subject<void>());
      const fixture = await create();

      cancelButton(fixture)?.click();
      fixture.detectChanges();
      cancelButton(fixture)?.click();

      expect(cancelButton(fixture)?.disabled).toBe(true);
      expect(cancel).toHaveBeenCalledTimes(1);
    });

    it('ignores the answer of a cancel issued for a channel the user has since left', async () => {
      getStatus.mockReturnValue(of(status({ activeRun: RUNNING })));
      const pending = new Subject<void>();
      cancel.mockReturnValue(pending);
      const fixture = await create();

      cancelButton(fixture)?.click();
      fixture.componentRef.setInput('channelName', 'other');
      fixture.detectChanges();
      await fixture.whenStable();
      const callsBefore = getStatus.mock.calls.length;
      pending.error(httpError(403));

      expect(fixture.componentInstance['cancelErrorKey']()).toBeNull();
      expect(getStatus.mock.calls.length).toBe(callsBefore);
    });
  });

  describe('start answers that arrive after a channel round trip', () => {
    it('A → B → A while the POST is pending: the stale answer touches nothing', async () => {
      const pending = new Subject<BackfillRun>();
      start.mockReturnValue(pending);
      const fixture = await create();
      const section = fixture.componentInstance;

      (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
      expect(section['starting']()).toBe(true);
      fixture.componentRef.setInput('channelName', 'other');
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.componentRef.setInput('channelName', 'sensitron');
      fixture.detectChanges();
      await fixture.whenStable();
      const callsBefore = getStatus.mock.calls.length;

      pending.next(RUN);
      pending.complete();

      expect(section['status']()?.activeRun).toBeNull();
      expect(getStatus.mock.calls.length).toBe(callsBefore);
    });

    it('A → B → A: a stale error neither shows nor unlocks a newer start', async () => {
      const first = new Subject<BackfillRun>();
      const second = new Subject<BackfillRun>();
      start.mockReturnValueOnce(first).mockReturnValueOnce(second);
      const fixture = await create();
      const section = fixture.componentInstance;
      const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;

      button.click();
      for (const name of ['other', 'sensitron']) {
        fixture.componentRef.setInput('channelName', name);
        fixture.detectChanges();
        await fixture.whenStable();
      }
      button.click();
      expect(section['starting']()).toBe(true);

      first.error(httpError(409, 'backfill_already_active'));

      expect(section['startErrorKey']()).toBeNull();
      expect(section['starting']()).toBe(true);
    });
  });
});

describe('backfillStartErrorKey', () => {
  it('gives channel_excluded the backfill-specific sentence', () => {
    expect(backfillStartErrorKey(httpError(409, 'channel_excluded'))).toBe(
      'backfill.errors.channel_excluded',
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
