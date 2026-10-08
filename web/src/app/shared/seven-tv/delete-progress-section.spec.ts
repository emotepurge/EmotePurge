import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import {
  EnvironmentProviders,
  Provider,
  Signal,
  WritableSignal,
  computed,
  signal,
} from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { Subject, of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { DeleteRunInfo, SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { RunQueueItem, RunResult } from '../../core/seven-tv/seven-tv-run-engine';
import {
  SevenTvRunArbiter,
  SevenTvRunClaim,
  SevenTvRunKind,
} from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { SyncReportReason, SyncReportState } from '../../core/seven-tv/sync-report-outcome';
import { CSV_MIME } from '../export/csv';
import { JSON_MIME } from '../export/export-envelope';
import { DeleteAbortNotice } from './delete-flow';
import { DeleteProgressSection } from './delete-progress-section';
import { RestoreConfirmDialogData } from './restore-confirm-dialog';

/*
 * The cases of `mass-delete-panel.spec.ts` that drove the run surface's members straight over the
 * component instance — the protocol export, and the restore entry's chain wherever a case reads
 * nothing of the panel's own — moved here together with those members (#201 T-A): same titles,
 * same expectations, only the fixture changed from the panel to the section (with
 * `hostSelectedSetId` set to the set the panel's `setId` used to give). "The panel" in a moved
 * title is now this section's host; destroying it destroys the section, which is what those cases
 * do here.
 *
 * What stayed in the panel spec: every case that goes through the DOM (the section renders inside
 * the panel there), every restore case that asserts on the panel's own `abortNotice`, and the ones
 * whose point is that the panel's live `channelName` input does not leak into the restore.
 *
 * Two cases are new: `notice` carries a blocked restore's reason out of the section, and
 * `foreignToView` follows the host's current `hostSelectedSetId`, not the run's frozen set.
 */

const DE_TRANSLATIONS = {
  massDelete: {
    deleteButton: 'Löschen ({{ count }})',
    clearSelection: 'Auswahl aufheben',
    progress: '{{ finished }} / {{ total }} verarbeitet',
    progressBarLabel: 'Löschfortschritt',
    settling: 'Wird abgeschlossen…',
  },
  common: {
    cancel: 'Abbrechen',
    close: 'Schließen',
  },
};

/**
 * `openProtocolExport()`'s `downloadFile(...)` call is a real `<a download>` click against a real
 * `Blob`/object URL. The Angular unit-test system refuses `vi.mock` for relative imports, so this
 * spy sits at the same seam `file-download.spec.ts` already uses (`URL.createObjectURL`,
 * `document.createElement('a')`) rather than mocking the module — content is
 * `purge-run-export.spec.ts`'s job, this only pins which download a dialog choice produces.
 */
interface CapturedDownload {
  filename: string;
  mimeType: string;
  blob: Blob;
}

/** Spies on the same two seams `downloadFile` touches — restore via `vi.restoreAllMocks()` in
 *  `afterEach`, matching `file-download.spec.ts`'s own pattern. */
function captureDownloads(): CapturedDownload[] {
  const downloads: CapturedDownload[] = [];
  if (!('createObjectURL' in URL)) {
    Object.assign(URL, { createObjectURL: () => '', revokeObjectURL: () => undefined });
  }
  vi.spyOn(URL, 'createObjectURL').mockImplementation((blob: Blob | MediaSource) => {
    downloads.push({ filename: '', mimeType: (blob as Blob).type, blob: blob as Blob });
    return 'blob:test';
  });
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);

  const originalCreateElement = document.createElement.bind(document);
  vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
    const element = originalCreateElement(tag);
    if (tag === 'a') {
      vi.spyOn(element as HTMLAnchorElement, 'click').mockImplementation(() => {
        const pending = downloads[downloads.length - 1];
        if (pending) {
          pending.filename = (element as HTMLAnchorElement).download;
        }
      });
    }
    return element;
  });
  return downloads;
}

/** Typed fakes, cut down from `mass-delete-panel.spec.ts`'s to what this section reads. */
type DeleteServiceFake = Pick<
  SevenTvDeleteService,
  | 'isRunning'
  | 'queue'
  | 'run'
  | 'syncReport'
  | 'syncReportReason'
  | 'rateLimitPauseSeconds'
  | 'lastRun'
>;

function fakeDeleteService(overrides: Partial<DeleteServiceFake> = {}): DeleteServiceFake {
  return {
    isRunning: signal(false),
    queue: signal<RunQueueItem[]>([]),
    run: signal<DeleteRunInfo | null>(null),
    syncReport: signal<SyncReportState>('idle'),
    syncReportReason: signal<SyncReportReason | null>(null),
    rateLimitPauseSeconds: signal<number | null>(null),
    lastRun: signal<{
      setId: string;
      channelName: string;
      targetOwnerTwitchId: string | null;
      result: RunResult;
    } | null>(null),
    ...overrides,
  };
}

type RestoreServiceFake = Pick<
  SevenTvRestoreService,
  'isRunning' | 'queue' | 'restorePreCheckPending' | 'startCheckPending'
>;

function fakeRestoreService(overrides: Partial<RestoreServiceFake> = {}): RestoreServiceFake {
  return {
    isRunning: signal(false),
    queue: signal<RunQueueItem[]>([]),
    restorePreCheckPending: signal(false),
    startCheckPending: signal(false),
    ...overrides,
  };
}

interface RunArbiterFake {
  activeRun: WritableSignal<SevenTvRunKind | null>;
  activeClaim: () => SevenTvRunClaim | null;
  startPending: Signal<boolean>;
  otherStartPending: WritableSignal<boolean>;
}

/** Same derivation as the panel spec's: `activeClaim` is a `'running'` claim of `activeRun`, and
 *  `startPending` is the restore's own start check or any other participant's. */
function fakeRunArbiter(
  activeRun: WritableSignal<SevenTvRunKind | null> = signal(null),
  ...ownStartChecks: Signal<boolean>[]
): RunArbiterFake {
  const activeClaim = computed<SevenTvRunClaim | null>(() => {
    const kind = activeRun();
    return kind === null ? null : { kind, phase: 'running' };
  });
  const otherStartPending = signal(false);
  return {
    activeRun,
    activeClaim,
    startPending: computed(() => ownStartChecks.some((check) => check()) || otherStartPending()),
    otherStartPending,
  };
}

/** The panel spec's `panelProviders` with `emoteSetService: null`: `SevenTvEmoteSetService` stays
 *  real, so each test drives the pre-check through `HttpTestingController`. */
function sectionProviders(options: {
  deleteService: DeleteServiceFake;
  restoreService: RestoreServiceFake;
  arbiter?: RunArbiterFake;
  dialogOpen: ReturnType<typeof vi.fn>;
  emoteAdminService: Partial<EmoteAdminService>;
}): (Provider | EnvironmentProviders)[] {
  return [
    provideHttpClient(),
    provideHttpClientTesting(),
    {
      provide: EmoteAdminService,
      useValue: options.emoteAdminService as unknown as EmoteAdminService,
    },
    {
      provide: SevenTvDeleteService,
      useValue: options.deleteService as unknown as SevenTvDeleteService,
    },
    {
      provide: SevenTvRestoreService,
      useValue: options.restoreService as unknown as SevenTvRestoreService,
    },
    {
      provide: SevenTvRunArbiter,
      useValue: (options.arbiter ??
        fakeRunArbiter(
          signal(null),
          options.restoreService.startCheckPending,
        )) as unknown as SevenTvRunArbiter,
    },
    {
      provide: SevenTvTokenService,
      useValue: { hasToken: signal(true) } as unknown as SevenTvTokenService,
    },
    { provide: Dialog, useValue: { open: options.dialogOpen } as unknown as Dialog },
  ];
}

/** See the panel spec's own copies: the pre-check route's editable answer and its URL. */
function preCheckEditableBody(setId: string, trackedChannel: string): Record<string, unknown> {
  return {
    status: 'editable',
    target: {
      emoteSetId: setId,
      setName: setId,
      ownerDisplayName: null,
      twitchLogin: trackedChannel,
      twitchChannelId: 'tw-1',
      trackedChannelName: trackedChannel,
      isActiveSet: true,
    },
  };
}

function preCheckUrl(emoteSetId: string): string {
  return `/api/seventv/me/emote-set-targets/${emoteSetId}`;
}

describe('DeleteProgressSection — protocol export choice handling (#141)', () => {
  let fixture: ComponentFixture<DeleteProgressSection>;
  let section: DeleteProgressSection;
  let openSpy: ReturnType<typeof vi.fn>;
  let lastRun: WritableSignal<{
    setId: string;
    channelName: string;
    targetOwnerTwitchId: string | null;
    result: RunResult;
  } | null>;
  let downloads: CapturedDownload[];

  beforeEach(async () => {
    downloads = captureDownloads();
    openSpy = vi.fn();
    lastRun = signal({
      setId: 'set-1',
      channelName: 'somechannel',
      targetOwnerTwitchId: null,
      result: {
        doneKeys: ['7tv-1', '7tv-live'],
        items: [
          {
            key: '7tv-1',
            emoteId: 'e1',
            sevenTvEmoteId: '7tv-1',
            name: 'PogU',
            status: 'done',
            completedSteps: 1,
            failedStep: null,
          },
          // A set-view row without a local emote (spec #200, 7.1) — see the no-filter case below.
          {
            key: '7tv-live',
            sevenTvEmoteId: '7tv-live',
            name: 'LiveOnly',
            status: 'done',
            completedSteps: 1,
            failedStep: null,
          },
        ],
        startedAt: Date.parse('2026-09-01T12:00:00Z'),
        finishedAt: Date.parse('2026-09-01T12:05:00Z'),
      },
    });

    await TestBed.configureTestingModule({
      imports: [
        DeleteProgressSection,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        { provide: EmoteAdminService, useValue: {} as unknown as EmoteAdminService },
        {
          provide: SevenTvDeleteService,
          useValue: {
            isRunning: signal(false),
            queue: signal([]),
            syncReport: signal('idle'),
            syncReportReason: signal(null),
            rateLimitPauseSeconds: signal(0),
            lastRun,
            startCheckPending: signal(false),
          } as unknown as SevenTvDeleteService,
        },
        {
          provide: SevenTvRestoreService,
          useValue: {
            isRunning: signal(false),
            queue: signal([]),
            syncReport: signal('idle'),
            syncReportReason: signal(null),
            rateLimitPauseSeconds: signal(0),
            resyncTrigger: signal('idle'),
            skippedDuplicates: signal(0),
            skippedNameTaken: signal(0),
            duplicateCheckAvailable: signal(true),
            duplicateNoticePending: signal(false),
            // #255 P2 (Codex review): the shared cross-entry pre-check gate `restoreConfirmPending`
            // now aliases — read as soon as the component is constructed, not just once a restore
            // pre-check actually starts.
            restorePreCheckPending: signal(false),
            startCheckPending: signal(false),
          } as unknown as SevenTvRestoreService,
        },
        {
          provide: SevenTvRunArbiter,
          useValue: fakeRunArbiter() as unknown as SevenTvRunArbiter,
        },
        {
          provide: SevenTvTokenService,
          useValue: { hasToken: signal(true) } as unknown as SevenTvTokenService,
        },
        { provide: Dialog, useValue: { open: openSpy } as unknown as Dialog },
      ],
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');

    fixture = TestBed.createComponent(DeleteProgressSection);
    section = fixture.componentInstance;
    fixture.componentRef.setInput('hostSelectedSetId', 'set-1');
    fixture.detectChanges();
  });

  afterEach(() => {
    // Spies only (URL.createObjectURL/revokeObjectURL, document.createElement) — matching
    // file-download.spec.ts's own cleanup, never replacing the global URL object outright.
    vi.restoreAllMocks();
  });

  it('downloads the CSV protocol and marks it saved when the csv option is chosen', () => {
    openSpy.mockReturnValue({ closed: of({ optionId: 'csv', scope: 'visible' }) });

    section['openProtocolExport']();

    expect(downloads).toHaveLength(1);
    expect(downloads[0].filename).toBe('emotepurge_somechannel_purge_2026-09-01-1205.csv');
    expect(downloads[0].mimeType).toBe(CSV_MIME);
    expect(section['protocolSaved']()).toBe(true);
  });

  it('downloads the JSON protocol and marks it saved when the json option is chosen', () => {
    openSpy.mockReturnValue({ closed: of({ optionId: 'json', scope: 'visible' }) });

    section['openProtocolExport']();

    expect(downloads).toHaveLength(1);
    expect(downloads[0].filename).toBe('emotepurge_somechannel_purge_2026-09-01-1205.json');
    expect(downloads[0].mimeType).toBe(JSON_MIME);
    expect(section['protocolSaved']()).toBe(true);
  });

  // Only the JSON protocol can be read back in (restore); the dialog preselects and lists
  // `options[0]` (ExportDialog's own contract), so passing JSON first is the whole fix.
  it('offers JSON first, ahead of CSV, since only JSON can be restored', () => {
    openSpy.mockReturnValue({ closed: of(undefined) });

    section['openProtocolExport']();

    const data = openSpy.mock.calls[0][1].data as { options: { id: string }[] };
    expect(data.options.map((option) => option.id)).toEqual(['json', 'csv']);
  });

  // Spec #200, F3/AK 72: this panel used to filter rows without an emoteId out of the protocol
  // ("a silently short protocol") — which, with set-view rows that have none, would make their
  // deletion irreversible and traceless. Every row of the run is written.
  it('writes every row of the run into the protocol, a row without an emoteId included', async () => {
    openSpy.mockReturnValue({ closed: of({ optionId: 'json', scope: 'visible' }) });

    section['openProtocolExport']();

    const written = JSON.parse(await downloads[0].blob.text());
    expect(written.rows.map((row: { sevenTvEmoteId: string }) => row.sevenTvEmoteId)).toEqual([
      '7tv-1',
      '7tv-live',
    ]);
    expect(written.rows[1].emoteId).toBeNull();
    expect(written.meta.counts.succeeded).toBe(2);
  });

  it('downloads nothing and leaves protocolSaved alone when the dialog closes with nothing', () => {
    openSpy.mockReturnValue({ closed: of(undefined) });

    section['openProtocolExport']();

    expect(downloads).toHaveLength(0);
    expect(section['protocolSaved']()).toBe(false);
  });
});

describe('DeleteProgressSection — the restore-confirm path resolves its target fresh (#253 spec E13/E16, revised by #256 P3-3)', () => {
  let fixture: ComponentFixture<DeleteProgressSection>;
  let httpMock: HttpTestingController;
  let getSetStatus: ReturnType<typeof vi.fn>;
  let startRestore: ReturnType<typeof vi.fn>;
  let dialogOpen: ReturnType<typeof vi.fn>;
  let closed: Subject<boolean | undefined>;
  let restoreService: RestoreServiceFake & { startRestore: ReturnType<typeof vi.fn> };
  let lastRun: WritableSignal<{
    setId: string;
    channelName: string;
    targetOwnerTwitchId: string | null;
    result: RunResult;
  } | null>;

  // The delete run's own frozen channel — see the panel spec's block of the same name for why it
  // differs from the live page there. This section has no channel input at all.
  const RUN_CHANNEL = 'runchannel';

  function flushTargetsResponse(): void {
    httpMock
      .expectOne((r) => r.url === preCheckUrl('set-1'))
      .flush(preCheckEditableBody('set-1', RUN_CHANNEL));
  }

  beforeEach(async () => {
    closed = new Subject<boolean | undefined>();
    getSetStatus = vi.fn().mockReturnValue(of({ occupiedSlots: 1, capacity: 100 }));
    startRestore = vi.fn();
    dialogOpen = vi.fn().mockReturnValue({ closed });
    lastRun = signal({
      setId: 'set-1',
      channelName: RUN_CHANNEL,
      targetOwnerTwitchId: null,
      result: {
        doneKeys: ['7tv-1'],
        items: [
          {
            key: '7tv-1',
            emoteId: 'e1',
            sevenTvEmoteId: '7tv-1',
            name: 'PogU',
            status: 'done' as const,
            completedSteps: 1,
            failedStep: null,
          },
        ],
        startedAt: Date.parse('2026-09-01T12:00:00Z'),
        finishedAt: Date.parse('2026-09-01T12:05:00Z'),
      },
    });
    restoreService = { ...fakeRestoreService(), startRestore };

    await TestBed.configureTestingModule({
      imports: [
        DeleteProgressSection,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: sectionProviders({
        deleteService: fakeDeleteService({ lastRun }),
        restoreService,
        dialogOpen,
        emoteAdminService: { getSetStatus } as unknown as Partial<EmoteAdminService>,
      }),
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');
    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent(DeleteProgressSection);
    fixture.componentRef.setInput('hostSelectedSetId', 'set-1');
    fixture.detectChanges();
  });

  afterEach(() => {
    httpMock.verify();
  });

  // Operator decision 2026-09-22 ("middle rule"): the restore offered from a finished run runs the
  // same per-alias check as the file restore — here, the run's one alias is already back. #255:
  // since that is also the *only* row, the open-time check already leaves nothing to confirm, so
  // no dialog opens at all — the existing "everything already there" notice reports it directly.
  it('skips an alias of the run that is already back in the set, opening no dialog', () => {
    fixture.componentInstance['openRestoreConfirm']();
    flushTargetsResponse();

    httpMock.expectOne('https://7tv.io/v4/gql').flush({
      data: {
        emoteSets: {
          emoteSet: {
            emotes: {
              totalCount: 1,
              pageCount: 1,
              items: [{ alias: 'PogU', emote: { id: '7tv-1' } }],
            },
          },
        },
      },
    });

    expect(dialogOpen).not.toHaveBeenCalled();
    expect(startRestore).toHaveBeenCalledWith(
      expect.objectContaining({ setId: 'set-1', hostChannelName: RUN_CHANNEL }),
      [],
      1,
      true,
      0,
    );
  });

  // #255: the open-time check's own read can fail too — the confirmation still opens (there is no
  // verified "nothing to do" here), but its count is marked an upper bound rather than exact.
  it('marks the confirmation count an upper bound when the open-time check fails', () => {
    fixture.componentInstance['openRestoreConfirm']();
    flushTargetsResponse();

    httpMock.expectOne('https://7tv.io/v4/gql').error(new ProgressEvent('error'));

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    const data = dialogOpen.mock.calls[0][1].data as RestoreConfirmDialogData;
    expect(data.countIsUpperBound).toBe(true);
    expect(data.addCount).toBe(1);
    expect(data.names).toEqual(['PogU']);
  });

  // Review round 1, finding 4: an answer landing after this panel is torn down must not open a
  // restore confirmation nobody can see or answer any more.
  it('cancels the pre-check request once the panel is destroyed', () => {
    fixture.componentInstance['openRestoreConfirm']();
    const req = httpMock.expectOne((r) => r.url === preCheckUrl('set-1'));
    expect(req.cancelled).toBeFalsy();

    fixture.destroy();

    expect(req.cancelled).toBe(true);
    expect(startRestore).not.toHaveBeenCalled();
    // #255 P2 (Codex review, second finding): `takeUntilDestroyed` unsubscribes here without ever
    // calling `next` or `error`, so a reset reachable only from those never ran — and since this
    // flag aliases the shared, root-level `restorePreCheckPending`, leaving it `true` would have
    // disabled both restore entries until a full page reload, not just this destroyed panel.
    expect(fixture.componentInstance['restoreConfirmPending']()).toBe(false);
  });

  // #255 P3(11): a run with more than one done row, where the open-time check finds only some of
  // them already present — the confirmation must name and count exactly the survivors, not the
  // whole run and not nothing.
  it('shows only the row the open-time check found missing, filtering out the one already present', () => {
    lastRun.set({
      setId: 'set-1',
      channelName: RUN_CHANNEL,
      targetOwnerTwitchId: null,
      result: {
        doneKeys: ['7tv-1', '7tv-2'],
        items: [
          {
            key: '7tv-1',
            emoteId: 'e1',
            sevenTvEmoteId: '7tv-1',
            name: 'PogU',
            status: 'done' as const,
            completedSteps: 1,
            failedStep: null,
          },
          {
            key: '7tv-2',
            emoteId: 'e2',
            sevenTvEmoteId: '7tv-2',
            name: 'KEKW',
            status: 'done' as const,
            completedSteps: 1,
            failedStep: null,
          },
        ],
        startedAt: Date.parse('2026-09-01T12:00:00Z'),
        finishedAt: Date.parse('2026-09-01T12:05:00Z'),
      },
    });
    fixture.componentInstance['openRestoreConfirm']();
    flushTargetsResponse();

    // 7tv-1 (PogU) is already back in the target set under its own alias; 7tv-2 (KEKW) is not.
    httpMock.expectOne('https://7tv.io/v4/gql').flush({
      data: {
        emoteSets: {
          emoteSet: {
            emotes: {
              totalCount: 1,
              pageCount: 1,
              items: [{ alias: 'PogU', emote: { id: '7tv-1' } }],
            },
          },
        },
      },
    });

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    const data = dialogOpen.mock.calls[0][1].data as RestoreConfirmDialogData;
    expect(data.names).toEqual(['KEKW']);
    expect(data.addCount).toBe(1);
    expect(data.countIsUpperBound).toBe(false);
  });

  // #255 P2 (Codex review): a read that succeeds but only sees part of the target set
  // (`SevenTvSetEntries.complete: false` — here, 7TV's own `totalCount` promising one more entry
  // than this single page delivered) must not let the confirmation claim an exact count it never
  // verified — same hedge as a failed read, but the filtering itself is unaffected: the
  // found-present row still drops out, the genuinely-missing one still shows.
  it('marks the count an upper bound, while still filtering rows normally, when the open-time read is truncated', () => {
    lastRun.set({
      setId: 'set-1',
      channelName: RUN_CHANNEL,
      targetOwnerTwitchId: null,
      result: {
        doneKeys: ['7tv-1', '7tv-2'],
        items: [
          {
            key: '7tv-1',
            emoteId: 'e1',
            sevenTvEmoteId: '7tv-1',
            name: 'PogU',
            status: 'done' as const,
            completedSteps: 1,
            failedStep: null,
          },
          {
            key: '7tv-2',
            emoteId: 'e2',
            sevenTvEmoteId: '7tv-2',
            name: 'KEKW',
            status: 'done' as const,
            completedSteps: 1,
            failedStep: null,
          },
        ],
        startedAt: Date.parse('2026-09-01T12:00:00Z'),
        finishedAt: Date.parse('2026-09-01T12:05:00Z'),
      },
    });
    fixture.componentInstance['openRestoreConfirm']();
    flushTargetsResponse();

    // 7tv-1 (PogU) is already back in the target set under its own alias; 7tv-2 (KEKW) is not —
    // same setup as the test above, but the read's own totalCount does not match what this single
    // page delivered.
    httpMock.expectOne('https://7tv.io/v4/gql').flush({
      data: {
        emoteSets: {
          emoteSet: {
            emotes: {
              totalCount: 2,
              pageCount: 1,
              items: [{ alias: 'PogU', emote: { id: '7tv-1' } }],
            },
          },
        },
      },
    });

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    const data = dialogOpen.mock.calls[0][1].data as RestoreConfirmDialogData;
    expect(data.names).toEqual(['KEKW']);
    expect(data.addCount).toBe(1);
    expect(data.countIsUpperBound).toBe(true);
  });

  // #255 P1 (Codex review): a row the open-time check already found present is hidden from the
  // confirmation entirely — it must stay hidden from the run too, even if it goes missing from the
  // target set again before the user confirms (another editor, or the confirmation simply left open
  // a while). Without the fix, the confirm-time re-check's own fresh read — which has to query the
  // full row set to apply its per-alias rule correctly — would see the row as newly missing and
  // resend it as an `ADD` the user never saw or agreed to.
  it('never sends a row the open-time check already hid, even if it goes missing again before confirm', () => {
    lastRun.set({
      setId: 'set-1',
      channelName: RUN_CHANNEL,
      targetOwnerTwitchId: null,
      result: {
        doneKeys: ['7tv-1', '7tv-2'],
        items: [
          {
            key: '7tv-1',
            emoteId: 'e1',
            sevenTvEmoteId: '7tv-1',
            name: 'PogU',
            status: 'done' as const,
            completedSteps: 1,
            failedStep: null,
          },
          {
            key: '7tv-2',
            emoteId: 'e2',
            sevenTvEmoteId: '7tv-2',
            name: 'KEKW',
            status: 'done' as const,
            completedSteps: 1,
            failedStep: null,
          },
        ],
        startedAt: Date.parse('2026-09-01T12:00:00Z'),
        finishedAt: Date.parse('2026-09-01T12:05:00Z'),
      },
    });
    fixture.componentInstance['openRestoreConfirm']();
    flushTargetsResponse();

    // Open-time: 7tv-1 (PogU) is already present -> hidden from the dialog; 7tv-2 (KEKW) is not.
    httpMock.expectOne('https://7tv.io/v4/gql').flush({
      data: {
        emoteSets: {
          emoteSet: {
            emotes: {
              totalCount: 1,
              pageCount: 1,
              items: [{ alias: 'PogU', emote: { id: '7tv-1' } }],
            },
          },
        },
      },
    });

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect((dialogOpen.mock.calls[0][1].data as RestoreConfirmDialogData).names).toEqual(['KEKW']);

    closed.next(true);

    // Confirm-time: 7tv-1 has since been removed from the set too — a full re-check now finds
    // BOTH rows missing.
    httpMock.expectOne('https://7tv.io/v4/gql').flush({
      data: { emoteSets: { emoteSet: { emotes: { totalCount: 0, pageCount: 1, items: [] } } } },
    });

    // 'PogU' (7tv-1) never appeared in the confirmation and must not appear in the run either,
    // however the confirm-time read now classifies it.
    expect(startRestore).toHaveBeenCalledWith(
      expect.objectContaining({ setId: 'set-1', hostChannelName: RUN_CHANNEL }),
      [{ emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'KEKW', aliases: ['KEKW'] }],
      0,
      true,
      0,
    );
  });

  // #255 P2a: the open-time duplicate check (`loadRestoreConfirmPreview`, run once the pre-check
  // above has already resolved editable) gets the same timeout budget as every other read in this
  // panel — a hung request must not leave the restore button disabled forever, and the
  // confirmation still opens, its count hedged as an upper bound rather than a silent hang.
  it('opens the confirmation with an upper-bound count when the open-time duplicate check hangs past its timeout', () => {
    vi.useFakeTimers();
    try {
      fixture.componentInstance['openRestoreConfirm']();
      flushTargetsResponse();
      const req = httpMock.expectOne('https://7tv.io/v4/gql');
      expect(req.cancelled).toBeFalsy();
      expect(fixture.componentInstance['restoreConfirmPending']()).toBe(true);

      vi.advanceTimersByTime(20_000);

      expect(req.cancelled).toBe(true);
      expect(dialogOpen).toHaveBeenCalledTimes(1);
      const data = dialogOpen.mock.calls[0][1].data as RestoreConfirmDialogData;
      expect(data.countIsUpperBound).toBe(true);
      expect(data.names).toEqual(['PogU']);
      expect(fixture.componentInstance['restoreConfirmPending']()).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  // #255 P2a: a late answer to the open-time duplicate check, arriving after the panel is torn
  // down, must not open a confirmation nobody can see or answer any more — same discipline as the
  // pre-check's own `takeUntilDestroyed` above.
  it('cancels the open-time duplicate check once the panel is destroyed', () => {
    fixture.componentInstance['openRestoreConfirm']();
    flushTargetsResponse();
    const req = httpMock.expectOne('https://7tv.io/v4/gql');
    expect(req.cancelled).toBeFalsy();

    fixture.destroy();

    expect(req.cancelled).toBe(true);
    expect(dialogOpen).not.toHaveBeenCalled();
    // #255 P2 (Codex review, second finding): same gap, the second read in the chain — teardown
    // must release the shared gate here too, not just from a settled answer.
    expect(fixture.componentInstance['restoreConfirmPending']()).toBe(false);
  });

  // #255 P2a: a second click while the pre-check chain (this method's own `resolveEditableSet`
  // through the open-time duplicate check) is still out must not start a second read racing
  // towards a second confirmation — `restoreConfirmPending` refuses re-entry, belt and suspenders
  // next to the button's own `[disabled]`.
  it('ignores a second click on the restore entry while its own pre-check is still out', () => {
    fixture.componentInstance['openRestoreConfirm']();
    expect(fixture.componentInstance['restoreConfirmPending']()).toBe(true);

    // The second click lands before the target-list pre-check has even answered. Were the guard
    // not there, this would fire a second `resolveEditableSet` request, and `flushTargetsResponse`
    // below (which expects exactly one) would fail with "found 2" instead.
    fixture.componentInstance['openRestoreConfirm']();
    flushTargetsResponse();
    httpMock.expectOne('https://7tv.io/v4/gql').error(new ProgressEvent('error'));

    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  // Same guard, the other gap: a second click landing after the pre-check resolved but while the
  // open-time duplicate check is still out.
  it('ignores a second click on the restore entry while the open-time duplicate check is still out', () => {
    fixture.componentInstance['openRestoreConfirm']();
    flushTargetsResponse();
    expect(fixture.componentInstance['restoreConfirmPending']()).toBe(true);

    fixture.componentInstance['openRestoreConfirm']();

    httpMock.expectOne('https://7tv.io/v4/gql').error(new ProgressEvent('error'));

    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  // #255 P2 (Codex review): the two restore entries — this panel's own button and
  // `ImportTrigger`'s restore-file door — used to keep separate pending flags, so a click on
  // *this* panel while the *other* entry's pre-check chain was still out was not caught by either
  // guard: this panel's own `restoreConfirmPending` was still `false`, and the click landed before
  // any request of this panel's own ever went out. `restoreConfirmPending` now aliases
  // `SevenTvRestoreService.restorePreCheckPending`, so the fix closes the gap by making the two
  // entries share the very same flag — setting it here, without going through this panel's own
  // `openRestoreConfirm` at all, stands in for `ImportTrigger` having claimed it first.
  it("ignores a click while the other restore entry's own pre-check already holds the shared gate", () => {
    restoreService.restorePreCheckPending.set(true);

    fixture.componentInstance['openRestoreConfirm']();

    httpMock.expectNone((r) => r.url === preCheckUrl('set-1'));
    expect(dialogOpen).not.toHaveBeenCalled();

    // Released once the other entry's own chain settles — the panel's button works normally again.
    restoreService.restorePreCheckPending.set(false);
    fixture.componentInstance['openRestoreConfirm']();
    flushTargetsResponse();
    httpMock.expectOne('https://7tv.io/v4/gql').error(new ProgressEvent('error'));

    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  // #201 T-A: the restore chain used to set the panel's `abortNotice` directly; the section hands
  // the same notice to its host through `notice` instead — the panel binds it to that very signal,
  // the tag page to its own status region.
  it('emits the restore.errors.* notice through its notice output when the pre-check blocks the restore', () => {
    const notices: DeleteAbortNotice[] = [];
    fixture.componentInstance.notice.subscribe((notice) => notices.push(notice));

    fixture.componentInstance['openRestoreConfirm']();
    httpMock
      .expectOne((r) => r.url === preCheckUrl('set-1'))
      .flush({ status: 'notEditable', target: null });

    expect(notices).toEqual([
      { leadKey: 'restore.nothingRestored', reasonKey: 'restore.errors.targetNotEditable' },
    ]);
    expect(startRestore).not.toHaveBeenCalled();
  });

  // Spec E21, #201 T-A: "not the set on screen" is about the host's *current* view, so the warning
  // follows `hostSelectedSetId` — never the run's own frozen set, which is the same in both rounds.
  it("warns about a foreign set when the host shows a different set than the finished run's, and stops once it shows the run's set again", () => {
    fixture.componentRef.setInput('hostSelectedSetId', 'set-other');
    fixture.detectChanges();

    fixture.componentInstance['openRestoreConfirm']();
    flushTargetsResponse();
    httpMock.expectOne('https://7tv.io/v4/gql').error(new ProgressEvent('error'));

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect((dialogOpen.mock.calls[0][1].data as RestoreConfirmDialogData).foreignToView).toBe(true);

    fixture.componentRef.setInput('hostSelectedSetId', 'set-1');
    fixture.detectChanges();

    // The pre-check's answer for `set-1` is cached by now (`resolveEditableSet`), so only the
    // open-time duplicate check goes out again.
    fixture.componentInstance['openRestoreConfirm']();
    httpMock.expectOne('https://7tv.io/v4/gql').error(new ProgressEvent('error'));

    expect(dialogOpen).toHaveBeenCalledTimes(2);
    expect((dialogOpen.mock.calls[1][1].data as RestoreConfirmDialogData).foreignToView).toBe(
      false,
    );
  });
});

describe('DeleteProgressSection — unclear rows of a finished delete run are offered for restore, fail-closed (#275)', () => {
  const RUN_CHANNEL = 'runchannel';
  const GQL = 'https://7tv.io/v4/gql';

  let fixture: ComponentFixture<DeleteProgressSection>;
  let httpMock: HttpTestingController;
  let startRestore: ReturnType<typeof vi.fn>;
  let dialogOpen: ReturnType<typeof vi.fn>;
  let closed: Subject<boolean | undefined>;
  let getSetStatus: ReturnType<typeof vi.fn>;

  function item(
    sevenTvEmoteId: string,
    name: string,
    status: RunQueueItem['status'],
  ): RunQueueItem {
    return {
      key: sevenTvEmoteId,
      emoteId: `e-${sevenTvEmoteId}`,
      sevenTvEmoteId,
      name,
      status,
      completedSteps: status === 'done' ? 1 : 0,
      failedStep: status === 'failed' || status === 'unknown' ? 0 : null,
    };
  }

  const DONE = item('7tv-1', 'PogU', 'done');
  const UNKNOWN = item('7tv-2', 'KEKW', 'unknown');

  /** Mounts the section over a finished run of `items` — also shown as the dock's queue. */
  async function mount(items: RunQueueItem[]): Promise<void> {
    startRestore = vi.fn();
    closed = new Subject<boolean | undefined>();
    dialogOpen = vi.fn().mockReturnValue({ closed });
    getSetStatus = vi.fn().mockReturnValue(of({ occupiedSlots: 1, capacity: 100 }));
    const lastRun = signal({
      setId: 'set-1',
      channelName: RUN_CHANNEL,
      targetOwnerTwitchId: null,
      result: {
        doneKeys: items.filter((entry) => entry.status === 'done').map((entry) => entry.key),
        items,
        startedAt: Date.parse('2026-09-01T12:00:00Z'),
        finishedAt: Date.parse('2026-09-01T12:05:00Z'),
      },
    });

    await TestBed.configureTestingModule({
      imports: [
        DeleteProgressSection,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: sectionProviders({
        deleteService: fakeDeleteService({ lastRun, queue: signal(items) }),
        restoreService: { ...fakeRestoreService(), startRestore } as unknown as RestoreServiceFake,
        dialogOpen,
        emoteAdminService: { getSetStatus } as unknown as Partial<EmoteAdminService>,
      }),
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');
    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent(DeleteProgressSection);
    fixture.componentRef.setInput('hostSelectedSetId', 'set-1');
    fixture.detectChanges();
  }

  /** Clicks the restore entry and answers its target pre-check, leaving the open-time duplicate
   *  check's read for the test to answer. */
  function openRestore(): void {
    fixture.componentInstance['openRestoreConfirm']();
    httpMock
      .expectOne((r) => r.url === preCheckUrl('set-1'))
      .flush(preCheckEditableBody('set-1', RUN_CHANNEL));
  }

  /** A single, last page of `entries`; `truncated` makes 7TV's `totalCount` promise one more
   *  entry than it delivers — a read that succeeds with `complete: false`. */
  function entriesPage(entries: { id: string; alias: string }[], truncated = false) {
    return {
      data: {
        emoteSets: {
          emoteSet: {
            emotes: {
              totalCount: entries.length + (truncated ? 1 : 0),
              pageCount: 1,
              items: entries.map(({ id, alias }) => ({ alias, emote: { id } })),
            },
          },
        },
      },
    };
  }

  function confirmData(): RestoreConfirmDialogData {
    return dialogOpen.mock.calls[0][1].data as RestoreConfirmDialogData;
  }

  function sentIds(): string[] {
    return (startRestore.mock.calls[0][1] as { sevenTvEmoteId: string }[]).map(
      (row) => row.sevenTvEmoteId,
    );
  }

  afterEach(() => {
    httpMock.verify();
  });

  it('offers the unknown row alongside the done one once a complete read vouches for both', async () => {
    await mount([DONE, UNKNOWN]);
    openRestore();

    httpMock.expectOne(GQL).flush(entriesPage([]));

    expect(confirmData().names).toEqual(['PogU', 'KEKW']);
    expect(confirmData().uncertainDropped).toBe(0);
  });

  it('marks the unknown row uncertain, so an incomplete open-time read leaves it out and the confirmation counts it', async () => {
    await mount([DONE, UNKNOWN]);
    openRestore();

    httpMock.expectOne(GQL).flush(entriesPage([], true));

    expect(confirmData().names).toEqual(['PogU']);
    expect(confirmData().addCount).toBe(1);
    expect(confirmData().uncertainDropped).toBe(1);
  });

  it('keeps a done row fail-open next to a dropped unknown one when the open-time read fails', async () => {
    await mount([DONE, UNKNOWN]);
    openRestore();

    httpMock.expectOne(GQL).error(new ProgressEvent('error'));

    expect(confirmData().names).toEqual(['PogU']);
    expect(confirmData().uncertainDropped).toBe(1);
    expect(confirmData().countIsUpperBound).toBe(true);
  });

  it('opens the confirmation with nothing to add, not the "everything already there" shortcut, when every restorable row was unknown and the read fails', async () => {
    await mount([UNKNOWN]);
    openRestore();

    httpMock.expectOne(GQL).error(new ProgressEvent('error'));

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(confirmData().addCount).toBe(0);
    expect(confirmData().uncertainDropped).toBe(1);
    expect(startRestore).not.toHaveBeenCalled();
  });

  it('opens the confirmation with nothing to add when every restorable row was unknown and the read is incomplete', async () => {
    await mount([UNKNOWN]);
    openRestore();

    httpMock.expectOne(GQL).flush(entriesPage([], true));

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(confirmData().addCount).toBe(0);
    expect(confirmData().uncertainDropped).toBe(1);
    expect(startRestore).not.toHaveBeenCalled();
  });

  it('spends no slot read on a confirmation with nothing to add', async () => {
    await mount([UNKNOWN]);
    openRestore();

    httpMock.expectOne(GQL).flush(entriesPage([], true));

    expect(getSetStatus).not.toHaveBeenCalled();
  });

  // Plan Festlegung 16, unchanged fallback: a confirm-time read that *fails* reuses the open-time
  // rows, which already include the unknown row a complete open-time read vouched for.
  it('keeps an unknown row the open-time read vouched for when only the confirm-time read fails', async () => {
    await mount([DONE, UNKNOWN]);
    openRestore();
    httpMock.expectOne(GQL).flush(entriesPage([]));

    closed.next(true);
    httpMock.expectOne(GQL).error(new ProgressEvent('error'));

    expect(sentIds()).toEqual(['7tv-1', '7tv-2']);
  });

  // Accepted on purpose (operator decision 2026-09-27): a confirm-time read that succeeds but is
  // incomplete does not fall back — the unknown row the dialog showed is dropped silently, the
  // done row still goes. It can only ever send less, never a blind ADD.
  it('drops an unknown row the dialog showed when the confirm-time read succeeds but is incomplete, still sending the done row', async () => {
    await mount([DONE, UNKNOWN]);
    openRestore();
    httpMock.expectOne(GQL).flush(entriesPage([]));
    expect(confirmData().names).toEqual(['PogU', 'KEKW']);

    closed.next(true);
    httpMock.expectOne(GQL).flush(entriesPage([], true));

    expect(sentIds()).toEqual(['7tv-1']);
    expect(startRestore.mock.calls[0][3]).toBe(true);
  });

  // No special rule once the read is complete: the unknown row's emote is still in the set (its
  // delete never landed), so it is "already present" like any other and the shortcut is taken.
  it('takes the "everything already there" shortcut when a complete read finds the unknown row still present', async () => {
    await mount([UNKNOWN]);
    openRestore();

    httpMock.expectOne(GQL).flush(entriesPage([{ id: '7tv-2', alias: 'KEKW' }]));

    expect(dialogOpen).not.toHaveBeenCalled();
    expect(startRestore).toHaveBeenCalledWith(expect.anything(), [], 1, true, 0);
  });

  it('gives up on a confirm-time check that hangs past its timeout and starts as for a failed check', async () => {
    await mount([DONE]);
    openRestore();
    httpMock.expectOne(GQL).flush(entriesPage([]));
    vi.useFakeTimers();
    try {
      closed.next(true);
      const req = httpMock.expectOne(GQL);

      vi.advanceTimersByTime(20_000);

      expect(req.cancelled).toBe(true);
      expect(sentIds()).toEqual(['7tv-1']);
      expect(startRestore.mock.calls[0][3]).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  // #280: the window after the confirmation, before anything runs. The cases of this group that
  // read the restore entry's own DOM stayed in the panel spec.
  describe('the confirm-time check holds the restore entry (#280)', () => {
    /** `SevenTvRestoreService.startCheckPending` on the fake `mount` provided — read, not held. */
    function startCheckPending(): boolean {
      return startCheckPendingSignal()();
    }

    function startCheckPendingSignal(): WritableSignal<boolean> {
      return (TestBed.inject(SevenTvRestoreService) as unknown as RestoreServiceFake)
        .startCheckPending;
    }

    afterEach(() => {
      vi.useRealTimers();
    });

    it('ignores a click on the restore entry while a confirmed restore is still being checked', async () => {
      await mount([DONE]);
      startCheckPendingSignal().set(true);

      fixture.componentInstance['openRestoreConfirm']();

      httpMock.expectNone((r) => r.url === preCheckUrl('set-1'));
      expect(dialogOpen).not.toHaveBeenCalled();
    });

    it('releases the start check when the confirm-time check hangs past its timeout', async () => {
      await mount([DONE]);
      openRestore();
      httpMock.expectOne(GQL).flush(entriesPage([]));
      vi.useFakeTimers();

      closed.next(true);
      httpMock.expectOne(GQL);
      expect(startCheckPending()).toBe(true);

      vi.advanceTimersByTime(20_000);

      expect(startCheckPending()).toBe(false);
      expect(startRestore).toHaveBeenCalledTimes(1);
    });
  });
});
