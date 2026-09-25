import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, EnvironmentProviders, Provider, WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { Subject, of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import {
  EditableSetTarget,
  EmoteSetTargetsResponse,
} from '../../core/seven-tv/seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { RunQueueItem, RunResult } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { SyncReportReason, SyncReportState } from '../../core/seven-tv/sync-report-outcome';
import { CSV_MIME } from '../export/csv';
import { JSON_MIME } from '../export/export-envelope';
import { DeleteConfirmDialog, DeleteConfirmDialogData } from './delete-confirm-dialog';
import { DeletableEmote, MassDeletePanel } from './mass-delete-panel';
import { RestoreConfirmDialogData } from './restore-confirm-dialog';

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

/**
 * Only the row-composition contract (design doc §8.7): constructive group before the destructive
 * action, a neutral exit after it, and no leftover gap when the host page has nothing to project —
 * checked through accessible button names/order, never through the Tailwind classes that produce
 * the spacing (Regel 12). The delete/restore *flows* this panel also drives are exercised in the
 * specs of the pieces that own them (`restore-flow.spec.ts`, `import-trigger.spec.ts`), not
 * here.
 */

const DE_TRANSLATIONS = {
  massDelete: {
    deleteButton: 'Löschen ({{ count }})',
    clearSelection: 'Auswahl aufheben',
  },
  // Real text (matches public/i18n/de.json) — needed for the "and N more" tail
  // `missingRowsReasonParams` builds via `TranslocoService.translate` directly, not the template
  // pipe, so a missing key here would not fall back to a key string the way the pipe's own missing
  // translations do elsewhere in this spec file.
  common: {
    andMore: {
      one: '… und 1 weiteres',
      other: '… und {{count}} weitere',
    },
  },
};

const DELETE_LABEL = 'Löschen (2)';
const CLEAR_LABEL = 'Auswahl aufheben';
const VOTE_LABEL = 'Zur Abstimmung stellen';

const EMOTES: DeletableEmote[] = [
  { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', hidden: false },
  { emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'KEKW', hidden: false },
];

@Component({
  selector: 'app-host',
  imports: [MassDeletePanel],
  template: `
    <app-mass-delete-panel
      [setId]="setId"
      [channelName]="channelName"
      [selectedEmotes]="emotes"
      [leadingActionsPresent]="leadingActionsPresent"
    >
      @if (projectVoteButton) {
        <button type="button" selection-actions>{{ voteLabel }}</button>
      }
    </app-mass-delete-panel>
  `,
})
class HostComponent {
  setId = 'set-1';
  channelName = 'somechannel';
  emotes = EMOTES;
  leadingActionsPresent = false;
  projectVoteButton = false;
  voteLabel = VOTE_LABEL;
}

describe('MassDeletePanel row composition', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        HostComponent,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        {
          provide: EmoteAdminService,
          useValue: {} as unknown as EmoteAdminService,
        },
        {
          provide: SevenTvDeleteService,
          useValue: {
            isRunning: signal(false),
            queue: signal([]),
            syncReport: signal('idle'),
            syncReportReason: signal(null),
            rateLimitPauseSeconds: signal(0),
            lastRun: signal(null),
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
          } as unknown as SevenTvRestoreService,
        },
        {
          provide: SevenTvRunArbiter,
          useValue: {
            activeRun: signal<SevenTvRunKind | null>(null),
          } as unknown as SevenTvRunArbiter,
        },
        {
          provide: SevenTvTokenService,
          useValue: { hasToken: signal(true) } as unknown as SevenTvTokenService,
        },
        { provide: Dialog, useValue: { open: vi.fn() } as unknown as Dialog },
      ],
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');
  });

  function render(setup: Partial<HostComponent> = {}): {
    fixture: ComponentFixture<HostComponent>;
    buttonNames(): string[];
  } {
    const fixture = TestBed.createComponent(HostComponent);
    Object.assign(fixture.componentInstance, setup);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    return {
      fixture,
      buttonNames: () =>
        Array.from(host.querySelectorAll('button'))
          .map((button) => button.textContent?.trim() ?? '')
          .filter((text) => text.length > 0),
    };
  }

  it('renders only the destructive action and the neutral exit when nothing is projected (voting-detail default)', () => {
    const { buttonNames } = render({ leadingActionsPresent: false, projectVoteButton: false });

    expect(buttonNames()).toEqual([DELETE_LABEL, CLEAR_LABEL]);
  });

  it('keeps the destructive action and exit unchanged even if leadingActionsPresent is true but nothing was actually projected', () => {
    // leadingActionsPresent only controls the gap/grouping wrapper, not what appears inside the
    // slot — an empty projection still shows no leading button, just (invisibly) reserves the gap.
    const { buttonNames } = render({ leadingActionsPresent: true, projectVoteButton: false });

    expect(buttonNames()).toEqual([DELETE_LABEL, CLEAR_LABEL]);
  });

  it('orders the projected constructive group before the destructive action, and the neutral exit after it', () => {
    const { buttonNames } = render({ leadingActionsPresent: true, projectVoteButton: true });

    expect(buttonNames()).toEqual([VOTE_LABEL, DELETE_LABEL, CLEAR_LABEL]);
  });

  it('omits the neutral exit once the selection is empty, keeping the destructive action last among the rest', () => {
    const { buttonNames } = render({
      leadingActionsPresent: true,
      projectVoteButton: true,
      emotes: [],
    });

    // With an empty selection, "Löschen (0)" itself is still rendered (disabled elsewhere), but
    // "Auswahl aufheben" — the clear-selection escape hatch — has nothing left to clear.
    expect(buttonNames()).toEqual([VOTE_LABEL, 'Löschen (0)']);
  });
});

/**
 * `openProtocolExport()`'s dialog-choice handling (#141 follow-up, Regel 12): which download a
 * csv/json choice produces, that a cancel produces none, and that `protocolSaved` — the reminder
 * next to Close — only flips once a choice actually closed the dialog. Mounts `MassDeletePanel`
 * directly rather than through `HostComponent`, since `openProtocolExport` is called on the
 * component instance directly (same style as `usage-stats-page.spec.ts` calling protected
 * members) instead of driving the real `app-run-progress-panel` markup just to click a button.
 */
describe('MassDeletePanel — protocol export choice handling (#141)', () => {
  let fixture: ComponentFixture<MassDeletePanel>;
  let panel: MassDeletePanel;
  let openSpy: ReturnType<typeof vi.fn>;
  let lastRun: WritableSignal<{ setId: string; channelName: string; result: RunResult } | null>;
  let downloads: CapturedDownload[];

  beforeEach(async () => {
    downloads = captureDownloads();
    openSpy = vi.fn();
    lastRun = signal({
      setId: 'set-1',
      channelName: 'somechannel',
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
        MassDeletePanel,
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
          } as unknown as SevenTvRestoreService,
        },
        {
          provide: SevenTvRunArbiter,
          useValue: {
            activeRun: signal<SevenTvRunKind | null>(null),
          } as unknown as SevenTvRunArbiter,
        },
        {
          provide: SevenTvTokenService,
          useValue: { hasToken: signal(true) } as unknown as SevenTvTokenService,
        },
        { provide: Dialog, useValue: { open: openSpy } as unknown as Dialog },
      ],
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');

    fixture = TestBed.createComponent(MassDeletePanel);
    panel = fixture.componentInstance;
    fixture.componentRef.setInput('setId', 'set-1');
    fixture.componentRef.setInput('channelName', 'somechannel');
    fixture.componentRef.setInput('selectedEmotes', []);
    fixture.detectChanges();
  });

  afterEach(() => {
    // Spies only (URL.createObjectURL/revokeObjectURL, document.createElement) — matching
    // file-download.spec.ts's own cleanup, never replacing the global URL object outright.
    vi.restoreAllMocks();
  });

  it('downloads the CSV protocol and marks it saved when the csv option is chosen', () => {
    openSpy.mockReturnValue({ closed: of({ optionId: 'csv', scope: 'visible' }) });

    panel['openProtocolExport']();

    expect(downloads).toHaveLength(1);
    expect(downloads[0].filename).toBe('emotepurge_somechannel_purge_2026-09-01-1205.csv');
    expect(downloads[0].mimeType).toBe(CSV_MIME);
    expect(panel['protocolSaved']()).toBe(true);
  });

  it('downloads the JSON protocol and marks it saved when the json option is chosen', () => {
    openSpy.mockReturnValue({ closed: of({ optionId: 'json', scope: 'visible' }) });

    panel['openProtocolExport']();

    expect(downloads).toHaveLength(1);
    expect(downloads[0].filename).toBe('emotepurge_somechannel_purge_2026-09-01-1205.json');
    expect(downloads[0].mimeType).toBe(JSON_MIME);
    expect(panel['protocolSaved']()).toBe(true);
  });

  // Spec #200, F3/AK 72: this panel used to filter rows without an emoteId out of the protocol
  // ("a silently short protocol") — which, with set-view rows that have none, would make their
  // deletion irreversible and traceless. Every row of the run is written.
  it('writes every row of the run into the protocol, a row without an emoteId included', async () => {
    openSpy.mockReturnValue({ closed: of({ optionId: 'json', scope: 'visible' }) });

    panel['openProtocolExport']();

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

    panel['openProtocolExport']();

    expect(downloads).toHaveLength(0);
    expect(panel['protocolSaved']()).toBe(false);
  });
});

/**
 * Shared, correctly-typed fakes for the #89 blocks below, replacing repeated ~50-line provider
 * arrays. `Pick`ing straight off the real service classes means every field here is a
 * `WritableSignal<T>` of the exact `T` the real class declares (e.g. `rateLimitPauseSeconds` starts
 * `null`, not `0` — see `SevenTvRunEngine.rateLimitPauseSeconds`, seven-tv-run-engine.ts:221 — and
 * `syncReport` is typed against its real union) — a renamed or mistyped field becomes a compile
 * error instead of silently not mattering inside an `as unknown` cast. `SevenTvRunArbiter.activeRun`
 * is the one exception: the real class types it as a read-only `Signal` (it is a `computed()`, see
 * that class's doc), so its fake gets its own tiny interface with a writable signal a test can
 * actually drive.
 */
type DeleteServiceFake = Pick<
  SevenTvDeleteService,
  | 'isRunning'
  | 'queue'
  | 'syncReport'
  | 'syncReportReason'
  | 'rateLimitPauseSeconds'
  | 'lastRun'
  | 'confirmedRunPending'
  | 'beginConfirmedRun'
  | 'endConfirmedRun'
  | 'clearConfirmedRun'
>;

function fakeDeleteService(overrides: Partial<DeleteServiceFake> = {}): DeleteServiceFake {
  return {
    isRunning: signal(false),
    queue: signal<RunQueueItem[]>([]),
    syncReport: signal<SyncReportState>('idle'),
    syncReportReason: signal<SyncReportReason | null>(null),
    rateLimitPauseSeconds: signal<number | null>(null),
    lastRun: signal<{ setId: string; channelName: string; result: RunResult } | null>(null),
    // The dock's claim on a confirmed-but-not-yet-running delete. Spied rather than implemented:
    // what the *service* does with them (drop at once for a started run, hold for the abort notice
    // otherwise, drop outright when nothing was confirmed) is pinned in
    // seven-tv-delete.service.spec.ts; what the panel owes is that every exit of the confirmation
    // reaches one of them.
    confirmedRunPending: signal(false),
    beginConfirmedRun: vi.fn(),
    endConfirmedRun: vi.fn(),
    clearConfirmedRun: vi.fn(),
    ...overrides,
  };
}

type RestoreServiceFake = Pick<
  SevenTvRestoreService,
  | 'isRunning'
  | 'queue'
  | 'syncReport'
  | 'syncReportReason'
  | 'rateLimitPauseSeconds'
  | 'resyncTrigger'
  | 'skippedDuplicates'
  | 'skippedNameTaken'
  | 'duplicateCheckAvailable'
  | 'duplicateNoticePending'
>;

function fakeRestoreService(overrides: Partial<RestoreServiceFake> = {}): RestoreServiceFake {
  return {
    isRunning: signal(false),
    queue: signal<RunQueueItem[]>([]),
    syncReport: signal<SyncReportState>('idle'),
    syncReportReason: signal<SyncReportReason | null>(null),
    rateLimitPauseSeconds: signal<number | null>(null),
    resyncTrigger: signal('idle'),
    skippedDuplicates: signal(0),
    skippedNameTaken: signal(0),
    duplicateCheckAvailable: signal(true),
    duplicateNoticePending: signal(false),
    ...overrides,
  };
}

interface RunArbiterFake {
  activeRun: WritableSignal<SevenTvRunKind | null>;
}

function fakeRunArbiter(
  activeRun: WritableSignal<SevenTvRunKind | null> = signal(null),
): RunArbiterFake {
  return { activeRun };
}

/** A resolved target every editable-stub answer carries — irrelevant to the delete confirmation
 *  itself (spec 4.6 point 20: the pre-check only gates, it never feeds `DeleteConfirmDialogData`),
 *  so its exact field values are never asserted on by the blocks below. */
const EDITABLE_STUB_TARGET: EditableSetTarget = {
  emoteSetId: 'set-1',
  setName: 'set-1',
  ownerDisplayName: 'owner',
  twitchLogin: 'owner',
  trackedChannelName: 'somechannel',
  isActiveSet: true,
};

type EmoteSetServiceFake = Pick<SevenTvEmoteSetService, 'resolveEditableSet'>;

/** #253 AK 31/20: `MassDeletePanel` now runs the shared pre-check (`resolveEditableSet`) before
 *  every delete confirmation opens. Defaults to an immediate `'editable'` answer so every block
 *  below that does not itself test the pre-check keeps opening the confirmation synchronously,
 *  exactly as it did before that pre-check existed — the dedicated pre-check block further down
 *  overrides this with the real service instead (`panelProviders({ emoteSetService: null })`) to
 *  drive `HttpTestingController` directly. */
function fakeEmoteSetService(overrides: Partial<EmoteSetServiceFake> = {}): EmoteSetServiceFake {
  return {
    resolveEditableSet: vi
      .fn()
      .mockReturnValue(of({ status: 'editable', target: EDITABLE_STUB_TARGET })),
    ...overrides,
  };
}

/** The provider list every #89 block below needs, differing only in which fakes a test wants to
 *  drive — the rest default to an idle/untouched instance. `emoteSetService: null` opts out of the
 *  default editable stub and leaves `SevenTvEmoteSetService` real, for a block that drives it
 *  through its own `HttpTestingController` (the restore-confirm-path and delete-pre-check blocks). */
function panelProviders(
  options: {
    deleteService?: DeleteServiceFake;
    restoreService?: RestoreServiceFake;
    arbiter?: RunArbiterFake;
    dialogOpen?: ReturnType<typeof vi.fn>;
    emoteAdminService?: Partial<EmoteAdminService>;
    emoteSetService?: EmoteSetServiceFake | null;
  } = {},
) {
  const providers: (Provider | EnvironmentProviders)[] = [
    provideHttpClient(),
    {
      provide: EmoteAdminService,
      useValue: (options.emoteAdminService ?? {}) as unknown as EmoteAdminService,
    },
    {
      provide: SevenTvDeleteService,
      useValue: (options.deleteService ?? fakeDeleteService()) as unknown as SevenTvDeleteService,
    },
    {
      provide: SevenTvRestoreService,
      useValue: (options.restoreService ??
        fakeRestoreService()) as unknown as SevenTvRestoreService,
    },
    {
      provide: SevenTvRunArbiter,
      useValue: (options.arbiter ?? fakeRunArbiter()) as unknown as SevenTvRunArbiter,
    },
    {
      provide: SevenTvTokenService,
      useValue: { hasToken: signal(true) } as unknown as SevenTvTokenService,
    },
    { provide: Dialog, useValue: { open: options.dialogOpen ?? vi.fn() } as unknown as Dialog },
  ];
  if (options.emoteSetService !== null) {
    providers.push({
      provide: SevenTvEmoteSetService,
      useValue: (options.emoteSetService ??
        fakeEmoteSetService()) as unknown as SevenTvEmoteSetService,
    });
  }
  return providers;
}

/** `Löschen (n)` — the same wording `DELETE_LABEL` pins for n=2, generalised so the lock block can
 *  look the button up by its accessible name at any selection size instead of by DOM position. */
function deleteButtonLabel(count: number): string {
  return `Löschen (${count})`;
}

function findButtonByLabel(host: HTMLElement, label: string): HTMLButtonElement {
  const button = Array.from(host.querySelectorAll('button')).find(
    (candidate) => candidate.textContent?.trim() === label,
  );
  if (!button) {
    throw new Error(`button with accessible name "${label}" not found`);
  }
  return button;
}

/**
 * #89: the per-run latch that decides whether the host gets `deleted` (an optimistic client-side
 * drop) or `reloadRequested` (a forced refetch) once a delete run settles — per the constructor
 * effect's own comment, this is the highest-stakes decision in the component: emitting `deleted`
 * before the backend actually confirmed the archival is the exact regression the latch exists to
 * prevent ("showed a cleaned-up list while the database still held every emote"). Unaffected by
 * #134/#176 (that PR only touched the resync/duplicate notices and their aria-hidden state). Mounts
 * `MassDeletePanel` directly, same style as the protocol-export/duplicate-check blocks above, so
 * `deleteService`'s `isRunning`/`syncReport`/`lastRun` can be driven straight from the test.
 */
describe('MassDeletePanel — delete latch: deleted vs reloadRequested (#89)', () => {
  let fixture: ComponentFixture<MassDeletePanel>;
  let panel: MassDeletePanel;
  let isRunning: WritableSignal<boolean>;
  let syncReport: WritableSignal<SyncReportState>;
  let lastRun: WritableSignal<{ setId: string; channelName: string; result: RunResult } | null>;

  // A delete run keys every row by its 7TV id, so key and sevenTvEmoteId are the same value.
  function runResult(doneKeys: string[]): RunResult {
    return {
      doneKeys,
      items: doneKeys.map((id) => ({
        key: id,
        emoteId: `guid-${id}`,
        sevenTvEmoteId: id,
        name: id,
        status: 'done',
        completedSteps: 1,
        failedStep: null,
      })),
      startedAt: Date.parse('2026-09-01T12:00:00Z'),
      finishedAt: Date.parse('2026-09-01T12:05:00Z'),
    };
  }

  beforeEach(async () => {
    isRunning = signal(false);
    syncReport = signal<SyncReportState>('idle');
    lastRun = signal<{ setId: string; channelName: string; result: RunResult } | null>(null);

    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: panelProviders({
        deleteService: fakeDeleteService({ isRunning, syncReport, lastRun }),
      }),
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');

    fixture = TestBed.createComponent(MassDeletePanel);
    panel = fixture.componentInstance;
    fixture.componentRef.setInput('setId', 'set-1');
    fixture.componentRef.setInput('channelName', 'somechannel');
    fixture.componentRef.setInput('selectedEmotes', []);
    fixture.detectChanges();
  });

  it('emits deleted with the run doneKeys once the closing sync report succeeds', () => {
    const deleted: string[][] = [];
    panel.deleted.subscribe((ids) => deleted.push(ids));

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    syncReport.set('succeeded');
    lastRun.set({ setId: 'set-1', channelName: 'somechannel', result: runResult(['e1', 'e2']) });
    fixture.detectChanges();

    expect(deleted).toEqual([['e1', 'e2']]);
  });

  it('asks the host to reload instead when the closing sync report fails', () => {
    const deleted: string[][] = [];
    const reloads: void[] = [];
    panel.deleted.subscribe((ids) => deleted.push(ids));
    panel.reloadRequested.subscribe(() => reloads.push(undefined));

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    syncReport.set('failed');
    lastRun.set({ setId: 'set-1', channelName: 'somechannel', result: runResult(['e1']) });
    fixture.detectChanges();

    expect(deleted).toEqual([]);
    expect(reloads).toHaveLength(1);
  });

  it('asks the host to reload instead when the closing sync report only partially archived the run', () => {
    const reloads: void[] = [];
    panel.reloadRequested.subscribe(() => reloads.push(undefined));

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    syncReport.set('partial');
    lastRun.set({ setId: 'set-1', channelName: 'somechannel', result: runResult(['e1']) });
    fixture.detectChanges();

    expect(reloads).toHaveLength(1);
  });

  it('waits for a terminal report while idle/pending, then emits deleted once the report actually succeeds', () => {
    const deleted: string[][] = [];
    const reloads: void[] = [];
    panel.deleted.subscribe((ids) => deleted.push(ids));
    panel.reloadRequested.subscribe(() => reloads.push(undefined));

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    fixture.detectChanges();
    syncReport.set('pending');
    fixture.detectChanges();

    expect(deleted).toEqual([]);
    expect(reloads).toEqual([]);

    syncReport.set('succeeded');
    lastRun.set({ setId: 'set-1', channelName: 'somechannel', result: runResult(['e1']) });
    fixture.detectChanges();

    expect(deleted).toEqual([['e1']]);
    expect(reloads).toEqual([]);
  });

  it('emits nothing at all when the run succeeded but nothing was actually deleted', () => {
    // doneKeys.length === 0: there is nothing to drop from the host list and nothing wrong to
    // report either, so neither output is the right call.
    const deleted: string[][] = [];
    const reloads: void[] = [];
    panel.deleted.subscribe((ids) => deleted.push(ids));
    panel.reloadRequested.subscribe(() => reloads.push(undefined));

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    syncReport.set('succeeded');
    lastRun.set({ setId: 'set-1', channelName: 'somechannel', result: runResult([]) });
    fixture.detectChanges();

    expect(deleted).toEqual([]);
    expect(reloads).toEqual([]);
  });

  it('does not retroactively emit deleted when a manual retry of a failed sync report later succeeds', () => {
    // The real path this guards: retrySyncReport() (seven-tv-delete.service.ts ~162-219) re-sends
    // the closing report and walks syncReport from 'pending' to 'succeeded' without isRunning ever
    // becoming true again. The panel already chose reloadRequested for this run the moment it saw
    // 'failed' — a later, unrelated success on the same run must not flip that choice into a late
    // (and now double) deleted emission; the host already reloaded and moved on.
    const deleted: string[][] = [];
    const reloads: void[] = [];
    panel.deleted.subscribe((ids) => deleted.push(ids));
    panel.reloadRequested.subscribe(() => reloads.push(undefined));

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    syncReport.set('failed');
    lastRun.set({ setId: 'set-1', channelName: 'somechannel', result: runResult(['e1']) });
    fixture.detectChanges();
    expect(reloads).toHaveLength(1);
    expect(deleted).toEqual([]);

    // A manual retry (retrySyncReport) succeeds this time — same run, isRunning stays false.
    syncReport.set('pending');
    fixture.detectChanges();
    syncReport.set('succeeded');
    fixture.detectChanges();

    expect(reloads).toHaveLength(1);
    expect(deleted).toEqual([]);
  });

  // AK 72 / E18: `deleted` speaks 7TV ids — the run's keys — and a row that never had a local
  // emote is among them like any other, so the host can drop its cell.
  it('emits deleted as the 7TV-id keys of the run, a row without an emoteId included', () => {
    const deleted: string[][] = [];
    panel.deleted.subscribe((ids) => deleted.push(ids));

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    syncReport.set('succeeded');
    lastRun.set({
      setId: 'set-1',
      channelName: 'somechannel',
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
          {
            key: '7tv-live',
            sevenTvEmoteId: '7tv-live',
            name: 'LiveOnly',
            status: 'done',
            completedSteps: 1,
            failedStep: null,
          },
        ],
        startedAt: 0,
        finishedAt: 1,
      },
    });
    fixture.detectChanges();

    expect(deleted).toEqual([['7tv-1', '7tv-live']]);
  });

  it('re-arms the latch and resets protocolSaved once a new run starts', () => {
    const deleted: string[][] = [];
    panel.deleted.subscribe((ids) => deleted.push(ids));

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    syncReport.set('succeeded');
    lastRun.set({ setId: 'set-1', channelName: 'somechannel', result: runResult(['e1']) });
    fixture.detectChanges();
    expect(deleted).toEqual([['e1']]);

    // Simulates the admin having downloaded the first run's protocol (same seam as the
    // protocol-export block above) — this is the reminder state a new run has to clear.
    panel['protocolSaved'].set(true);
    expect(panel['protocolSaved']()).toBe(true);

    // A second run over the same panel instance (the service is a root singleton, see the class
    // doc) — the latch must fire again for its own outcome instead of staying spent forever, and
    // the "not yet saved" reminder must not carry over from the previous run's protocol.
    isRunning.set(true);
    fixture.detectChanges();
    expect(panel['protocolSaved']()).toBe(false);

    isRunning.set(false);
    syncReport.set('succeeded');
    lastRun.set({ setId: 'set-1', channelName: 'somechannel', result: runResult(['e2']) });
    fixture.detectChanges();

    expect(deleted).toEqual([['e1'], ['e2']]);
  });
});

/**
 * #89: the restore side's own latch (the constructor's second effect) — differently shaped than
 * the delete latch above: binary rather than three-way, it always asks the host to reload and has
 * no `deleted`-style optimistic branch, because a restore changes the inventory back in a way the
 * host cannot safely mirror locally (see that effect's own comment). Same once-per-run and
 * empty-queue guard shape as the delete latch, tested independently since the two effects share no
 * state with each other. Unaffected by #134/#176.
 */
describe('MassDeletePanel — restore latch (#89)', () => {
  let fixture: ComponentFixture<MassDeletePanel>;
  let isRunning: WritableSignal<boolean>;
  let queue: WritableSignal<RunQueueItem[]>;

  function item(key: string): RunQueueItem {
    return {
      key,
      emoteId: key,
      sevenTvEmoteId: `7tv-${key}`,
      name: key,
      status: 'done',
      completedSteps: 1,
      failedStep: null,
    };
  }

  beforeEach(async () => {
    isRunning = signal(false);
    queue = signal<RunQueueItem[]>([]);

    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: panelProviders({ restoreService: fakeRestoreService({ isRunning, queue }) }),
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');

    fixture = TestBed.createComponent(MassDeletePanel);
    fixture.componentRef.setInput('setId', 'set-1');
    fixture.componentRef.setInput('channelName', 'somechannel');
    fixture.componentRef.setInput('selectedEmotes', []);
    fixture.detectChanges();
  });

  it('asks the host to reload once the restore run settles with a non-empty queue', () => {
    const panel = fixture.componentInstance;
    const reloads: void[] = [];
    panel.reloadRequested.subscribe(() => reloads.push(undefined));

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    queue.set([item('e1')]);
    fixture.detectChanges();

    expect(reloads).toHaveLength(1);
  });

  it('does not ask the host to reload when the run ends without ever queuing anything', () => {
    const panel = fixture.componentInstance;
    const reloads: void[] = [];
    panel.reloadRequested.subscribe(() => reloads.push(undefined));

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    fixture.detectChanges();

    expect(reloads).toEqual([]);
  });

  it('fires only once per run even if the queue signal changes again afterwards', () => {
    const panel = fixture.componentInstance;
    const reloads: void[] = [];
    panel.reloadRequested.subscribe(() => reloads.push(undefined));

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    queue.set([item('e1')]);
    fixture.detectChanges();
    expect(reloads).toHaveLength(1);

    queue.set([item('e1'), item('e2')]);
    fixture.detectChanges();

    expect(reloads).toHaveLength(1);
  });

  it('re-arms once a new restore run starts, so a second run gets its own reload request', () => {
    const panel = fixture.componentInstance;
    const reloads: void[] = [];
    panel.reloadRequested.subscribe(() => reloads.push(undefined));

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    queue.set([item('e1')]);
    fixture.detectChanges();
    expect(reloads).toHaveLength(1);

    isRunning.set(true);
    fixture.detectChanges();
    isRunning.set(false);
    queue.set([item('e2')]);
    fixture.detectChanges();

    expect(reloads).toHaveLength(2);
  });
});

/**
 * #89: the delete button's own lock — the gate in front of what the class doc calls the panel's
 * "only unumkehrbare Aktion" (the only irreversible action). Three independent sources disable it
 * (`selectedEmotes().length === 0`, `deleteService.isRunning()`, `arbiter.activeRun() !== null`),
 * each checked in isolation here: a bug that accidentally ORs two of them together, or silently
 * drops one, would otherwise only surface once two conditions happen to overlap. Unaffected by
 * #134/#176.
 */
describe('MassDeletePanel — delete button lock, three independent sources (#89)', () => {
  let isRunning: WritableSignal<boolean>;
  let activeRun: WritableSignal<SevenTvRunKind | null>;

  async function render(
    selectedEmotes: DeletableEmote[],
    deleteLockReasonKey: string | null = null,
  ): Promise<HTMLButtonElement> {
    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: panelProviders({
        deleteService: fakeDeleteService({ isRunning }),
        arbiter: fakeRunArbiter(activeRun),
      }),
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');

    const fixture = TestBed.createComponent(MassDeletePanel);
    fixture.componentRef.setInput('setId', 'set-1');
    fixture.componentRef.setInput('channelName', 'somechannel');
    fixture.componentRef.setInput('selectedEmotes', selectedEmotes);
    fixture.componentRef.setInput('deleteLockReasonKey', deleteLockReasonKey);
    fixture.detectChanges();

    return findButtonByLabel(fixture.nativeElement, deleteButtonLabel(selectedEmotes.length));
  }

  beforeEach(() => {
    isRunning = signal(false);
    activeRun = signal<SevenTvRunKind | null>(null);
  });

  it('disables the button when the selection is empty, even with nothing else blocking', async () => {
    const button = await render([]);

    expect(button.disabled).toBe(true);
  });

  it('disables the button while the delete run itself is in progress, even with a selection and no other active run', async () => {
    isRunning.set(true);
    const button = await render(EMOTES);

    expect(button.disabled).toBe(true);
  });

  it('disables the button while a different 7TV run is active, even with a selection and this run idle', async () => {
    activeRun.set('import');
    const button = await render(EMOTES);

    expect(button.disabled).toBe(true);
  });

  it('enables the button once none of the three sources blocks it', async () => {
    const button = await render(EMOTES);

    expect(button.disabled).toBe(false);
  });

  it('a host lock disables the button and names its reason as text the button is described by (spec #200, 8.3)', async () => {
    const button = await render(EMOTES, 'usageStats.setView.lock.membersUnavailable');

    expect(button.disabled).toBe(true);
    const reasonId = button.getAttribute('aria-describedby');
    expect(reasonId).not.toBeNull();
    const reason = button.ownerDocument.getElementById(reasonId!);
    expect(reason?.textContent).toContain('usageStats.setView.lock.membersUnavailable');
  });
});

/**
 * Konzept "Auswahl überlebt Suche und Filter" 2.1/6: the split into a visible and a hidden name
 * list is this panel's job, not the dialog's — `DeleteConfirmDialog`'s own spec only ever hands it
 * pre-split fixtures, so a wiring mistake here (e.g. handing the dialog the raw, unsplit selection)
 * would pass every dialog-only test. 60 visible plus 3 hidden is deliberately past
 * `NamePreviewList`'s 50-name cap, to prove the hidden block is unaffected by it. Captures the real
 * `DIALOG_DATA` object `openConfirm()` hands to `Dialog.open(...)` and renders the real
 * `DeleteConfirmDialog` off it, rather than asserting on the panel's private signals directly.
 */
describe('MassDeletePanel — hidden-by-filter names reach the delete-confirm dialog uncapped', () => {
  const HIDDEN_NAMES = ['HiddenOne', 'HiddenTwo', 'HiddenThree'];

  function buildEmotes(): DeletableEmote[] {
    const visible: DeletableEmote[] = Array.from({ length: 60 }, (_, index) => ({
      emoteId: `v${index}`,
      sevenTvEmoteId: `7tv-v${index}`,
      name: `Visible${index}`,
      hidden: false,
    }));
    const hidden: DeletableEmote[] = HIDDEN_NAMES.map((name, index) => ({
      emoteId: `h${index}`,
      sevenTvEmoteId: `7tv-h${index}`,
      name,
      hidden: true,
    }));
    return [...visible, ...hidden];
  }

  // Only the keys DeleteConfirmDialog itself translates, same reasoning as its own spec's
  // DE_TRANSLATIONS — plus massDelete.deleteButton/clearSelection for the panel's own buttons.
  const TRANSLATIONS = {
    common: { cancel: 'Abbrechen' },
    massDelete: {
      deleteButton: 'Löschen ({{ count }})',
      clearSelection: 'Auswahl aufheben',
      confirmTitle: {
        one: '{{ count }} Emote von 7TV löschen?',
        other: '{{ count }} Emotes von 7TV löschen?',
      },
      startDelete: 'Löschen starten',
      checkingSharedSets: 'Prüfe geteilte Sets…',
      irreversibleNotice:
        'Das kann nicht rückgängig gemacht werden. Löschen läuft danach automatisch nacheinander mit kurzer Verzögerung zwischen den Emotes.',
      undetectableChannelsNotice:
        'Hinweis: Fremde Channels, die weder von uns getrackt werden noch von dir moderiert werden, aber ebenfalls dieses Set nutzen, können wir grundsätzlich nicht erkennen.',
      hiddenByFilter: {
        one: '1 davon ist durch den aktuellen Filter ausgeblendet.',
        other: '{{count}} davon sind durch den aktuellen Filter ausgeblendet.',
      },
    },
  };

  let openSpy: ReturnType<typeof vi.fn>;
  // DIALOG_DATA is resolved once per injector (see delete-confirm-dialog.spec.ts's own render()
  // comment) — this indirection lets a test capture what openConfirm() actually built before the
  // dialog component that reads it is created.
  let dialogData: DeleteConfirmDialogData;

  beforeEach(async () => {
    openSpy = vi.fn();

    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        DeleteConfirmDialog,
        TranslocoTestingModule.forRoot({
          langs: { de: TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        {
          provide: EmoteAdminService,
          useValue: {
            getSetWarning: () =>
              of({
                available: true,
                isOwnSet: true,
                otherTrackedChannelsSharingSet: [],
                otherModeratedChannelsSharingSet: [],
              }),
          } as unknown as EmoteAdminService,
        },
        {
          provide: SevenTvDeleteService,
          useValue: fakeDeleteService() as unknown as SevenTvDeleteService,
        },
        {
          provide: SevenTvRestoreService,
          useValue: fakeRestoreService() as unknown as SevenTvRestoreService,
        },
        { provide: SevenTvRunArbiter, useValue: fakeRunArbiter() as unknown as SevenTvRunArbiter },
        {
          provide: SevenTvTokenService,
          useValue: { hasToken: signal(true) } as unknown as SevenTvTokenService,
        },
        // #253 AK 31/20: the pre-check before the confirmation opens — an immediate `'editable'`
        // answer, same reasoning as `panelProviders`' own default (this block does not use that
        // helper, it builds its providers manually).
        {
          provide: SevenTvEmoteSetService,
          useValue: fakeEmoteSetService() as unknown as SevenTvEmoteSetService,
        },
        { provide: Dialog, useValue: { open: openSpy } as unknown as Dialog },
        // Only needed once DeleteConfirmDialog itself is instantiated below — resolved lazily via
        // the factory so each test can shape `dialogData` first, same pattern as
        // delete-confirm-dialog.spec.ts's own render().
        { provide: DIALOG_DATA, useFactory: () => dialogData },
        { provide: DialogRef, useValue: { close: vi.fn() } as unknown as DialogRef<boolean> },
      ],
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');
  });

  /** Mounts the panel, triggers openConfirm() and returns what it handed to Dialog.open(...). */
  function captureDialogData(
    selectedEmotes: DeletableEmote[],
    options: { setName?: string; activeSetId?: string | null } = {},
  ): DeleteConfirmDialogData {
    const fixture = TestBed.createComponent(MassDeletePanel);
    fixture.componentRef.setInput('setId', 'set-1');
    fixture.componentRef.setInput('channelName', 'somechannel');
    fixture.componentRef.setInput('selectedEmotes', selectedEmotes);
    if (options.setName !== undefined) {
      fixture.componentRef.setInput('setName', options.setName);
    }
    if (options.activeSetId !== undefined) {
      fixture.componentRef.setInput('activeSetId', options.activeSetId);
    }
    fixture.detectChanges();

    openSpy.mockReturnValue({ closed: of(undefined) });
    fixture.componentInstance['openConfirm']();

    expect(openSpy).toHaveBeenCalledTimes(1);
    const [component, config] = openSpy.mock.calls[0] as [unknown, { data: unknown }];
    expect(component).toBe(DeleteConfirmDialog);
    return config.data as DeleteConfirmDialogData;
  }

  it('lists exactly the hidden names in their own uncapped block, even though the visible list hits its 50-name cap', () => {
    dialogData = captureDialogData(buildEmotes());
    expect(dialogData.emotes()).toHaveLength(60);
    expect(dialogData.hiddenEmotes()).toEqual(HIDDEN_NAMES);

    const dialogFixture = TestBed.createComponent(DeleteConfirmDialog);
    dialogFixture.detectChanges();
    const host: HTMLElement = dialogFixture.nativeElement;

    const lists = host.querySelectorAll('ul');
    expect(lists).toHaveLength(2);
    // The visible list: 50 capped names plus its own "and 10 more" row.
    expect(lists[0].querySelectorAll('li')).toHaveLength(51);
    // The hidden block: uncapped, all three named, unaffected by that cap.
    const hiddenListed = Array.from(lists[1].querySelectorAll('li')).map((li) =>
      li.textContent?.trim(),
    );
    expect(hiddenListed).toEqual(HIDDEN_NAMES);

    // Introduced by the paragraph above the hidden list — deliberately not a live region, see
    // DeleteConfirmDialog's own template comment and its spec.
    expect(host.textContent).toContain('3 davon sind durch den aktuellen Filter ausgeblendet.');
  });

  it('renders no hidden-by-filter block at all when nothing is hidden', () => {
    dialogData = captureDialogData(EMOTES);

    const dialogFixture = TestBed.createComponent(DeleteConfirmDialog);
    dialogFixture.detectChanges();
    const host: HTMLElement = dialogFixture.nativeElement;

    expect(host.querySelectorAll('ul')).toHaveLength(1);
    expect(host.textContent).not.toContain('durch den aktuellen Filter ausgeblendet');
  });

  // spec #200, 8.8: the dialog's set name/active flag come from the panel's own `setId`/`setName`
  // inputs — the page's *selected* set (bound to `selectedEmoteSetId()` since T5.3) — never from
  // `activeEmoteSetId()` directly, which the panel does not even read.
  it("takes the delete-confirm dialog's set name from its own setName input, and marks it active when it matches activeSetId", () => {
    dialogData = captureDialogData(EMOTES, { setName: 'Halloween', activeSetId: 'set-1' });
    expect(dialogData.setName).toBe('Halloween');
    expect(dialogData.isActiveSet).toBe(true);
  });

  it("marks the delete-confirm dialog's set not active when activeSetId names a different set", () => {
    dialogData = captureDialogData(EMOTES, { setName: 'Halloween', activeSetId: 'set-other' });
    expect(dialogData.isActiveSet).toBe(false);
  });

  // #200 K5 finding B: the vote-session-detail page mounted this panel without ever binding
  // `[activeSetId]` at all — an omitted input defaulted to `null`, read as a *known* "not active",
  // so its delete confirmation wrongly said "this set is not currently active" even though that
  // page's run is always against the active set. An omission must not read the same as an explicit
  // `null` ("we checked and don't know"); it folds onto `setId()` instead.
  it('marks the delete-confirm dialog set active when the host never bound activeSetId at all', () => {
    dialogData = captureDialogData(EMOTES);
    expect(dialogData.isActiveSet).toBe(true);
  });

  it('still marks the delete-confirm dialog set not active for an explicit null activeSetId (a known unknown, not an omission)', () => {
    dialogData = captureDialogData(EMOTES, { activeSetId: null });
    expect(dialogData.isActiveSet).toBe(false);
  });
});

/**
 * #200 K4 fix round (finding A): the confirm dialog outlives the view it was opened on. A host lock
 * that lands while it is open — the usage page's set switch — must stop the delete at the moment
 * of confirming, not only at the moment of opening, and say so instead of doing nothing silently.
 */
describe('MassDeletePanel — the host lock is re-checked at confirm time (#200, K4)', () => {
  let fixture: ComponentFixture<MassDeletePanel>;
  let startDelete: ReturnType<typeof vi.fn>;
  let closed: Subject<boolean | undefined>;

  beforeEach(async () => {
    startDelete = vi.fn();
    closed = new Subject<boolean | undefined>();
    const deleteService = { ...fakeDeleteService(), startDelete };
    const providers = panelProviders({
      deleteService,
      dialogOpen: vi.fn().mockReturnValue({ closed }),
      emoteAdminService: {
        getSetWarning: () =>
          of({
            available: true,
            isOwnSet: true,
            otherTrackedChannelsSharingSet: [],
            otherModeratedChannelsSharingSet: [],
          }),
      },
    });
    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers,
    }).compileComponents();

    fixture = TestBed.createComponent(MassDeletePanel);
    fixture.componentRef.setInput('setId', 'set-1');
    fixture.componentRef.setInput('channelName', 'somechannel');
    fixture.componentRef.setInput('selectedEmotes', EMOTES);
    fixture.componentRef.setInput('deleteLockReasonKey', null);
    fixture.detectChanges();
  });

  function statusRegion(): HTMLElement {
    const region = (fixture.nativeElement as HTMLElement).querySelector('[role="status"]');
    expect(region).not.toBeNull();
    return region as HTMLElement;
  }

  it('starts the run when nothing locked it while the dialog was open', () => {
    fixture.componentInstance['openConfirm']();
    closed.next(true);

    expect(startDelete).toHaveBeenCalledTimes(1);
    expect(startDelete.mock.calls[0][0]).toBe('set-1');
  });

  it('aborts a confirmed delete when the host locked it behind the open dialog, and says why in a status region', () => {
    fixture.componentInstance['openConfirm']();
    // The set switch lands while the confirm dialog is still open.
    fixture.componentRef.setInput('deleteLockReasonKey', 'usageStats.setView.lock.switching');
    fixture.detectChanges();
    closed.next(true);
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    // Mounted before the abort (so a screen reader hears the mutation), filled by it.
    expect(statusRegion().textContent).toContain('massDelete.abortedByLock');
    expect(statusRegion().textContent).toContain('usageStats.setView.lock.switching');
  });

  // #200 K5 finding A: the host's lock only catches a switch still *in progress* — once it
  // settles (channel.synced moved the page's selected set while the dialog was open), the lock
  // clears again with nothing else to say the dialog no longer names the set on screen.
  it('aborts a confirmed delete when the set switched and settled behind the open dialog, with the host lock never engaging', () => {
    fixture.componentInstance['openConfirm']();
    // The switch has already settled by the time the dialog closes: the input moved, but no lock
    // was ever set — the case a plain re-check of deleteLockReasonKey() alone would miss.
    fixture.componentRef.setInput('setId', 'set-2');
    fixture.detectChanges();
    closed.next(true);
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusRegion().textContent).toContain('massDelete.abortedByLock');
    expect(statusRegion().textContent).toContain('massDelete.setChangedDuringConfirm');
  });

  it('keeps the status region mounted but empty until an abort, and clears it on the next attempt', () => {
    expect(statusRegion().textContent?.trim()).toBe('');

    fixture.componentInstance['openConfirm']();
    fixture.componentRef.setInput('deleteLockReasonKey', 'usageStats.setView.lock.switching');
    fixture.detectChanges();
    closed.next(true);
    fixture.detectChanges();
    expect(statusRegion().textContent).toContain('massDelete.abortedByLock');

    fixture.componentRef.setInput('deleteLockReasonKey', null);
    fixture.detectChanges();
    fixture.componentInstance['openConfirm']();
    fixture.detectChanges();
    expect(statusRegion().textContent?.trim()).toBe('');
  });

  // Sonde 5, branch A (spec 7.2/8.9, AK 68): a #74 duplicate cell and a row without a local emote
  // go into the run like any other row — no exception group, both aliases carried along.
  it('hands a duplicate cell and a row without an emoteId to the run like any other row', () => {
    fixture.componentRef.setInput('selectedEmotes', [
      {
        emoteId: 'e1',
        sevenTvEmoteId: '7tv-1',
        name: 'PogU',
        aliases: ['PogU', 'PogU2'],
        hidden: false,
      },
      { sevenTvEmoteId: '7tv-live', name: 'LiveOnly', hidden: false },
    ]);
    fixture.detectChanges();

    fixture.componentInstance['openConfirm']();
    closed.next(true);

    expect(startDelete).toHaveBeenCalledWith(
      'set-1',
      'somechannel',
      [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU', 'PogU2'] },
        { emoteId: undefined, sevenTvEmoteId: '7tv-live', name: 'LiveOnly', aliases: undefined },
      ],
      'somechannel',
    );
  });

  it('starts nothing once the panel itself is gone when the dialog confirms', () => {
    fixture.componentInstance['openConfirm']();
    fixture.destroy();
    closed.next(true);

    expect(startDelete).not.toHaveBeenCalled();
  });
});

// Operator decision 2026-09-22 (amending spec #200 E20): the active set's view keeps one name per
// 7TV id, but one REMOVE takes every entry of a #74 duplicate. A delete there reads the set's live
// entries from 7TV first and records every alias — or, if that read fails, deletes nothing.
describe('MassDeletePanel — an active-set delete records every alias from a live read', () => {
  const GQL = 'https://7tv.io/v4/gql';
  let fixture: ComponentFixture<MassDeletePanel>;
  let httpMock: HttpTestingController;
  let startDelete: ReturnType<typeof vi.fn>;
  let closed: Subject<boolean | undefined>;
  let activeRun: WritableSignal<SevenTvRunKind | null>;
  let dialogOpen: ReturnType<typeof vi.fn>;

  function entriesPage(entries: { id: string; alias?: string }[], pageCount = 1) {
    return {
      data: {
        emoteSets: {
          emoteSet: {
            emotes: {
              totalCount: entries.length,
              pageCount,
              items: entries.map(({ id, alias }) => ({ alias, emote: { id } })),
            },
          },
        },
      },
    };
  }

  beforeEach(async () => {
    startDelete = vi.fn();
    closed = new Subject<boolean | undefined>();
    activeRun = signal<SevenTvRunKind | null>(null);
    dialogOpen = vi.fn().mockReturnValue({ closed });
    const deleteService = { ...fakeDeleteService(), startDelete };
    const providers = panelProviders({
      deleteService,
      arbiter: fakeRunArbiter(activeRun),
      dialogOpen,
      emoteAdminService: {
        getSetWarning: () =>
          of({
            available: true,
            isOwnSet: true,
            otherTrackedChannelsSharingSet: [],
            otherModeratedChannelsSharingSet: [],
          }),
      },
    });
    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [...providers, provideHttpClientTesting()],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent(MassDeletePanel);
    fixture.componentRef.setInput('setId', 'set-1');
    fixture.componentRef.setInput('activeSetId', 'set-1');
    fixture.componentRef.setInput('channelName', 'somechannel');
    fixture.componentRef.setInput('readLiveAliasesFromActiveSet', true);
    // What the active view hands in: one name per id, the duplicate included.
    fixture.componentRef.setInput('selectedEmotes', [
      { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'], hidden: false },
      { emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'KEKW', aliases: ['KEKW'], hidden: false },
    ]);
    fixture.detectChanges();
  });

  afterEach(() => {
    httpMock.verify();
  });

  function statusText(): string {
    const region = (fixture.nativeElement as HTMLElement).querySelector('[role="status"]');
    return region?.textContent ?? '';
  }

  function deleteButton(): HTMLButtonElement {
    return (fixture.nativeElement as HTMLElement).querySelector('button') as HTMLButtonElement;
  }

  function confirm(): void {
    fixture.componentInstance['openConfirm']();
    closed.next(true);
    fixture.detectChanges();
  }

  it('reads the frozen set from 7TV only once the dialog is confirmed', () => {
    fixture.componentInstance['openConfirm']();
    httpMock.expectNone(GQL);

    closed.next(true);
    const req = httpMock.expectOne(GQL);
    expect(req.request.body.variables.id).toBe('set-1');
    req.flush(entriesPage([]));
  });

  it('hands a duplicate both of its live aliases, and leaves a single entry as it was', () => {
    confirm();
    httpMock.expectOne(GQL).flush(
      entriesPage([
        { id: '7tv-1', alias: 'PogU' },
        { id: '7tv-2', alias: 'KEKW' },
        { id: '7tv-1', alias: 'PogU2' },
      ]),
    );

    expect(startDelete).toHaveBeenCalledWith(
      'set-1',
      'somechannel',
      [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU', 'PogU2'] },
        { emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'KEKW', aliases: ['KEKW'] },
      ],
      'somechannel',
    );
  });

  // Superseded by #227 P1 (below, "blocks the whole run…"): a cell the live read does not know at
  // all used to keep its host aliases and still get deleted — exactly the defect #227 point 2
  // closes. What a *known* cell without any alias entry falls back to is still covered by "falls
  // back to the name for a cell that is entirely aliasless" above (that id IS in the read, via
  // `aliaslessIds`, so it is not "missing").

  // K5 fix round, spec §37/§38: an aliasless 7TV entry is a slot the one REMOVE also takes, but it
  // has no alias to restore under — the enrichment falls back to the emote's own display name for
  // that entry rather than losing it (F3: a protocol that looks complete but is not).
  it("falls back to the emote's own name for an aliasless entry, appended to the aliased entry the live read also finds under the same id", () => {
    confirm();
    httpMock
      .expectOne(GQL)
      .flush(
        entriesPage([
          { id: '7tv-1', alias: 'PogUOld' },
          { id: '7tv-1' },
          { id: '7tv-2', alias: 'KEKW' },
        ]),
      );

    expect(startDelete).toHaveBeenCalledWith(
      'set-1',
      'somechannel',
      [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogUOld', 'PogU'] },
        { emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'KEKW', aliases: ['KEKW'] },
      ],
      'somechannel',
    );
  });

  // The degenerate case: the fallback name happens to already be one of the live aliases — nothing
  // is appended a second time under the same string.
  it('does not duplicate an alias that already equals the fallback name', () => {
    confirm();
    httpMock
      .expectOne(GQL)
      .flush(
        entriesPage([
          { id: '7tv-1', alias: 'PogU' },
          { id: '7tv-1' },
          { id: '7tv-2', alias: 'KEKW' },
        ]),
      );

    expect(startDelete.mock.calls[0][2][0].aliases).toEqual(['PogU']);
  });

  it('falls back to the name for a cell that is entirely aliasless in the live set', () => {
    fixture.componentRef.setInput('selectedEmotes', [
      { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'], hidden: false },
    ]);
    fixture.detectChanges();
    confirm();
    httpMock.expectOne(GQL).flush(entriesPage([{ id: '7tv-1' }]));

    expect(startDelete).toHaveBeenCalledWith(
      'set-1',
      'somechannel',
      [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }],
      'somechannel',
    );
  });

  it('deletes nothing when the live read fails, and says why', () => {
    confirm();
    httpMock.expectOne(GQL).error(new ProgressEvent('network error'));
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.nothingDeleted');
    // K5 fix round: dedicated massDelete.memberRead.* keys, not the reused usageStats.setView.lock.*
    // texts ("Deleting and voting are locked: …"), which are wrong for this one-off abort notice.
    expect(statusText()).toContain('massDelete.memberRead.unavailable');
  });

  it('deletes nothing when 7TV answers the read with a GraphQL error disguised as HTTP 200', () => {
    confirm();
    httpMock.expectOne(GQL).flush({ errors: [{ message: 'rate limited' }] });
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.memberRead.unavailable');
  });

  it('deletes nothing when the live read only knows part of the set', () => {
    confirm();
    for (let page = 1; page <= 10; page++) {
      httpMock.expectOne(GQL).flush(entriesPage([], 11));
    }
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.memberRead.truncated');
  });

  // K5 fix round: `complete` now also compares the collected item count against the query's own
  // `totalCount` from the last page, not only the 10-page runaway guard — offset pagination
  // shifting between page fetches can silently miss an entry without ever hitting the guard.
  it('deletes nothing when the live read ends normally but under-counts against the reported total', () => {
    confirm();
    httpMock.expectOne(GQL).flush({
      data: {
        emoteSets: {
          emoteSet: {
            emotes: {
              totalCount: 5,
              pageCount: 1,
              items: [{ alias: 'PogU', emote: { id: '7tv-1' } }],
            },
          },
        },
      },
    });
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.memberRead.truncated');
  });

  // Opus review P1 (#227): a complete read that simply does not carry a confirmed row's id at all
  // (neither aliased nor aliasless) used to fall back to the host's own aliases and delete it
  // anyway — the vote page's frozen `[name]` fallback, still sent to 7TV for a member that had
  // already left. Fails closed: the WHOLE batch is blocked, not just the missing row, so a partial
  // run never records a protocol that disagrees with what the confirmation showed as a whole.
  it('blocks the whole run, with no read result used, when the complete read is missing one confirmed id', () => {
    confirm();
    // Knows 7tv-1 only — 7tv-2 (KEKW) is not there at all, aliased or not.
    httpMock.expectOne(GQL).flush(entriesPage([{ id: '7tv-1', alias: 'PogU' }]));
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.nothingDeleted');
    expect(statusText()).toContain('massDelete.memberRead.missingFromSet.one');
    // Opus review P2-c: the reason names the missing row, not only a count — the user has to know
    // which one to deselect before trying again.
    expect(fixture.componentInstance['abortNotice']()?.reasonParams).toEqual({ names: 'KEKW' });
  });

  it('picks the plural reason key when more than one confirmed id is missing from a complete read', () => {
    confirm();
    // Empty but complete (pageCount 1, totalCount 0) — neither 7tv-1 nor 7tv-2 is in the set.
    httpMock.expectOne(GQL).flush(entriesPage([]));
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.memberRead.missingFromSet.other');
    expect(fixture.componentInstance['abortNotice']()?.reasonParams).toEqual({
      names: 'PogU, KEKW',
    });
  });

  // Opus review P2-c: many missing rows still read as one line, not a wall of names — same cap and
  // "and N more" tail NamePreviewList uses for the identical problem in a dialog.
  it('caps the missing-row names and counts the rest, mirroring NamePreviewList', () => {
    const manyEmotes: DeletableEmote[] = Array.from({ length: 52 }, (_, i) => ({
      emoteId: `e${i}`,
      sevenTvEmoteId: `7tv-${i}`,
      name: `Emote${i}`,
      hidden: false,
    }));
    fixture.componentRef.setInput('selectedEmotes', manyEmotes);
    fixture.detectChanges();

    confirm();
    httpMock.expectOne(GQL).flush(entriesPage([])); // none of the 52 are known to the set

    expect(startDelete).not.toHaveBeenCalled();
    const params = fixture.componentInstance['abortNotice']()?.reasonParams as { names: string };
    expect(params.names.startsWith('Emote0, Emote1, ')).toBe(true);
    expect(params.names.endsWith('… und 2 weitere')).toBe(true);
    expect(params.names.split(', ')).toHaveLength(50); // 50 previewed names + the tail sentence
  });

  // An aliasless entry still counts as "known" (the id is a real member, just with no name to
  // record) — only a row absent from BOTH maps is missing, so this must not falsely block.
  it('does not block on an id that is only aliasless — that id is still known to the set', () => {
    confirm();
    httpMock.expectOne(GQL).flush(entriesPage([{ id: '7tv-1', alias: 'PogU' }, { id: '7tv-2' }]));

    expect(startDelete).toHaveBeenCalledWith(
      'set-1',
      'somechannel',
      [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] },
        { emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'KEKW', aliases: ['KEKW'] },
      ],
      'somechannel',
    );
  });

  /** The three dock-claim calls of the fake service, typed for the block below. */
  function claimCalls(): {
    beginConfirmedRun: ReturnType<typeof vi.fn>;
    endConfirmedRun: ReturnType<typeof vi.fn>;
    clearConfirmedRun: ReturnType<typeof vi.fn>;
  } {
    return TestBed.inject(SevenTvDeleteService) as unknown as ReturnType<typeof claimCalls>;
  }

  // Opus review P2-1: the claim used to start with the *read*, which left the whole life of the
  // modal uncovered — and the CDK dialog is opened without a viewContainerRef, so it outlives the
  // panel. A reload pruning every marked key while the confirmation is up unmounted the dock,
  // destroyed the panel under it, and the eventual Delete click then ran its checks against a
  // torn-down component: nothing deleted, nothing said. The claim therefore begins with the dialog.
  it('claims the dock the moment the confirmation opens, before anything is confirmed', () => {
    fixture.componentInstance['openConfirm']();

    expect(claimCalls().beginConfirmedRun).toHaveBeenCalledTimes(1);
    expect(claimCalls().endConfirmedRun).not.toHaveBeenCalled();
    expect(claimCalls().clearConfirmedRun).not.toHaveBeenCalled();
  });

  it('drops the claim outright, with no notice window, when the confirmation is dismissed', () => {
    fixture.componentInstance['openConfirm']();

    closed.next(false);

    // clearConfirmedRun, not endConfirmedRun: nothing was confirmed, so there is no notice to read
    // and an 8 s hold would be the empty dock actionDockHasContent exists to prevent.
    expect(claimCalls().clearConfirmedRun).toHaveBeenCalledTimes(1);
    expect(claimCalls().endConfirmedRun).not.toHaveBeenCalled();
    expect(startDelete).not.toHaveBeenCalled();
  });

  it('holds the claim across the read and releases it once the run was attempted', () => {
    fixture.componentInstance['openConfirm']();
    closed.next(true);
    expect(claimCalls().endConfirmedRun).not.toHaveBeenCalled();

    // Both confirmed ids known to the read (#227 P1: an id missing from a complete read now blocks
    // the whole run instead of falling back — not what this test is about).
    httpMock.expectOne(GQL).flush(
      entriesPage([
        { id: '7tv-1', alias: 'PogU' },
        { id: '7tv-2', alias: 'KEKW' },
      ]),
    );
    expect(claimCalls().endConfirmedRun).toHaveBeenCalledTimes(1);
    // Released only once the run was attempted, so the service can tell a started run (which keeps
    // the dock by itself) from an abort (which has nothing but its notice).
    expect(startDelete).toHaveBeenCalledTimes(1);
    expect(startDelete.mock.invocationCallOrder[0]).toBeLessThan(
      claimCalls().endConfirmedRun.mock.invocationCallOrder[0],
    );
  });

  it('releases the claim on the branch that makes no live read at all', () => {
    fixture.componentRef.setInput('readLiveAliasesFromActiveSet', false);
    fixture.detectChanges();

    confirm();

    httpMock.expectNone(GQL);
    expect(startDelete).toHaveBeenCalledTimes(1);
    expect(claimCalls().endConfirmedRun).toHaveBeenCalledTimes(1);
  });

  it('releases the claim for a confirmed delete whose selection the reload emptied', () => {
    fixture.componentInstance['openConfirm']();
    fixture.componentRef.setInput('selectedEmotes', []);
    fixture.detectChanges();

    closed.next(true);

    expect(startDelete).not.toHaveBeenCalled();
    // endConfirmedRun, not clearConfirmedRun: this exit has a notice, so it needs the window.
    expect(claimCalls().endConfirmedRun).toHaveBeenCalledTimes(1);
    expect(claimCalls().clearConfirmedRun).not.toHaveBeenCalled();
  });

  // The P2-1 scenario end to end. The panel is gone before the click — by a route change now that
  // the dock can no longer drop it — so the delete still starts nothing (abortReasonBeforeStart's
  // `destroyed` branch, deliberate: a torn-down panel has no selection left to vouch for). What
  // must not also happen is the claim outliving it and pinning an empty dock on whatever mounts
  // next.
  it('starts nothing and leaves no claim behind when the panel was destroyed while the modal was open', () => {
    fixture.componentInstance['openConfirm']();
    fixture.destroy();

    closed.next(true);

    httpMock.expectNone(GQL);
    expect(startDelete).not.toHaveBeenCalled();
    expect(claimCalls().endConfirmedRun).toHaveBeenCalledTimes(1);
  });

  it('releases the dock claim on a failed read too, after the abort notice is set', () => {
    const deleteService = TestBed.inject(SevenTvDeleteService) as unknown as {
      endConfirmedRun: ReturnType<typeof vi.fn>;
    };
    confirm();
    httpMock.expectOne(GQL).error(new ProgressEvent('error'));
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(deleteService.endConfirmedRun).toHaveBeenCalledTimes(1);
    expect(statusText()).toContain('massDelete.memberRead.unavailable');
  });

  it('keeps the delete button disabled while the read is out', () => {
    confirm();
    expect(deleteButton().disabled).toBe(true);

    httpMock.expectOne(GQL).flush(entriesPage([]));
    fixture.detectChanges();
    expect(deleteButton().disabled).toBe(false);
  });

  // K5 fix round item 5: a hung request used to leave liveAliasReadPending true forever, with the
  // delete button disabled and no way out short of reloading. A 20 s total budget treats a request
  // that never answers exactly like one that answers with an error.
  it('blocks the run and re-enables the button when the live read hangs past its timeout', () => {
    vi.useFakeTimers();
    try {
      confirm();
      const req = httpMock.expectOne(GQL);
      expect(deleteButton().disabled).toBe(true);
      expect(req.cancelled).toBeFalsy();

      vi.advanceTimersByTime(20_000);
      fixture.detectChanges();

      expect(startDelete).not.toHaveBeenCalled();
      expect(statusText()).toContain('massDelete.nothingDeleted');
      expect(statusText()).toContain('massDelete.memberRead.unavailable');
      expect(deleteButton().disabled).toBe(false);
      expect(req.cancelled).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it('aborts when the set switched while the read was out', () => {
    confirm();
    fixture.componentRef.setInput('setId', 'set-2');
    fixture.componentRef.setInput('activeSetId', 'set-2');
    httpMock.expectOne(GQL).flush(entriesPage([]));
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.setChangedDuringConfirm');
  });

  // K5 fix round item 4: this re-check used to abort silently, like the restore paths' identical
  // one — but there, the run that got there first is always visible in the *same* dock. Here the
  // competing run can be any of the three 7TV-writing kinds, started from elsewhere on the page, so
  // a silent return left nothing on screen explaining why a confirmed delete just vanished.
  it('starts nothing when another run claimed the arbiter while the read was out, and says so', () => {
    confirm();
    activeRun.set('import');
    httpMock.expectOne(GQL).flush(entriesPage([]));
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.nothingDeleted');
    expect(statusText()).toContain('massDelete.anotherRunStarted');
  });

  // Codex P3, K5 fix round 2: the same re-check, on the branch that has no read to hide behind. It
  // used to be qualified on `liveAliases !== null`, so a non-active-set delete (and an active one
  // whose host did not opt in) relied on deleteService.startDelete's own silent refusal — a
  // confirmed delete evaporating without a word. The confirmation is a modal the user can leave
  // open for minutes; a run started elsewhere lands behind it just as well as behind a read.
  it('starts nothing and says so when another run claimed the arbiter behind the confirmation, with no read involved', () => {
    fixture.componentRef.setInput('readLiveAliasesFromActiveSet', false);
    fixture.detectChanges();
    fixture.componentInstance['openConfirm']();
    activeRun.set('restore');
    closed.next(true);
    fixture.detectChanges();

    httpMock.expectNone(GQL);
    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.nothingDeleted');
    expect(statusText()).toContain('massDelete.anotherRunStarted');
  });

  // The near side of the same contract: the button is already disabled while a run holds the
  // arbiter, so this only catches a click that outraces one starting — silently, like the host-lock
  // guard next to it, since nothing has been confirmed yet and the winning run is already visible
  // in the dock.
  it('does not even open the confirmation while another 7TV run holds the arbiter', () => {
    activeRun.set('import');

    fixture.componentInstance['openConfirm']();

    expect(dialogOpen).not.toHaveBeenCalled();
    httpMock.expectNone(GQL);
    expect(startDelete).not.toHaveBeenCalled();
  });

  // Opus review P3-2: the third way deleteService.startDelete refuses in silence. A 401 from any
  // 7TV call behind the open confirmation clears the stored token, and the engine then declines
  // without a word — with the dock claim of this round holding an empty dock over it.
  it('says so instead of vanishing when the 7TV token was cleared behind the confirmation', () => {
    const tokenService = TestBed.inject(SevenTvTokenService) as unknown as {
      hasToken: WritableSignal<boolean>;
    };
    fixture.componentInstance['openConfirm']();
    tokenService.hasToken.set(false);

    closed.next(true);
    httpMock.expectOne(GQL).flush(entriesPage([]));
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.nothingDeleted');
    expect(statusText()).toContain('massDelete.tokenGoneDuringConfirm');
  });

  it('makes no read at all when the host lock already stops the delete', () => {
    fixture.componentInstance['openConfirm']();
    fixture.componentRef.setInput('deleteLockReasonKey', 'usageStats.setView.lock.switching');
    closed.next(true);
    fixture.detectChanges();

    httpMock.expectNone(GQL);
    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.abortedByLock');
  });

  // A non-active view's rows already carry every alias from the member list the view is built
  // from — no second read.
  it('makes no read in a non-active set, handing the host aliases through', () => {
    fixture.componentRef.setInput('activeSetId', 'set-active');
    fixture.componentRef.setInput('selectedEmotes', [
      {
        emoteId: undefined,
        sevenTvEmoteId: '7tv-1',
        name: 'PogU',
        aliases: ['PogU', 'PogU2'],
        hidden: false,
      },
    ]);
    fixture.detectChanges();

    confirm();

    httpMock.expectNone(GQL);
    // Spec 4.6 point 21: a delete from a non-active set expects no channel (`null`).
    expect(startDelete).toHaveBeenCalledWith(
      'set-1',
      'somechannel',
      [{ emoteId: undefined, sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU', 'PogU2'] }],
      null,
    );
  });

  // A caller that leaves this specific input false makes no active-set read regardless of any
  // other input — the vote-session page opts into `readLiveAliasesFromSet` instead, its own
  // unconditional-on-active-ness equivalent (#227), covered in that input's own describe block.
  it('makes no read when the host did not opt in', () => {
    fixture.componentRef.setInput('readLiveAliasesFromActiveSet', false);
    fixture.detectChanges();

    confirm();

    httpMock.expectNone(GQL);
    expect(startDelete).toHaveBeenCalledTimes(1);
  });

  // K5 fix round item 1: the dialog closes on confirm and nothing locks the grid, so the page's
  // live selection can change while this async read is still out. The run must delete exactly what
  // the dialog showed, not whatever the selection happens to be once the read answers.
  it('deletes the selection the dialog showed, unaffected by a shrink of the live selection while the read is pending', () => {
    confirm();
    // The selection loses one of its two entries while the read is still in flight — nothing on
    // screen prevented this once the dialog closed.
    fixture.componentRef.setInput('selectedEmotes', [
      { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'], hidden: false },
    ]);
    fixture.detectChanges();

    // Both confirmed ids known to the read (#227 P1) — including 7tv-2, which the shrunk live
    // selection above no longer carries but the confirmed snapshot still does.
    httpMock.expectOne(GQL).flush(
      entriesPage([
        { id: '7tv-1', alias: 'PogU' },
        { id: '7tv-2', alias: 'KEKW' },
      ]),
    );

    expect(startDelete).toHaveBeenCalledWith(
      'set-1',
      'somechannel',
      [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] },
        { emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'KEKW', aliases: ['KEKW'] },
      ],
      'somechannel',
    );
  });

  it('does not sweep in an id added to the live selection only after the dialog was confirmed', () => {
    confirm();
    fixture.componentRef.setInput('selectedEmotes', [
      ...EMOTES.map((emote) => ({ ...emote, aliases: [emote.name] })),
      { emoteId: 'e3', sevenTvEmoteId: '7tv-3', name: 'NEW', aliases: ['NEW'], hidden: false },
    ]);
    fixture.detectChanges();

    // Both confirmed ids known to the read (#227 P1) — 7tv-3 was never confirmed, so it must not
    // matter either way whether the read knows it; it isn't in this response at all.
    httpMock.expectOne(GQL).flush(
      entriesPage([
        { id: '7tv-1', alias: 'PogU' },
        { id: '7tv-2', alias: 'KEKW' },
      ]),
    );

    const deletedIds = startDelete.mock.calls[0][2].map(
      (emote: { sevenTvEmoteId: string }) => emote.sevenTvEmoteId,
    );
    expect(deletedIds).toEqual(['7tv-1', '7tv-2']);
  });

  // Operator decision 2026-09-22, replacing the K5 fix round's open-time freeze: the dialog renders
  // the panel's live name lists, so a pushed reload (channel.synced / usage.flushed -> retainAmong)
  // landing behind the open modal changes what the confirmation says. The snapshot is therefore
  // taken at confirm, from those same signals — displayed == deleted by construction. This is the
  // "no live read" branch, where a change between open and confirm is the only window there is to
  // observe the snapshot point at all.
  it('snapshots the selection at confirm, not at dialog open, on the no-read branch', () => {
    fixture.componentRef.setInput('readLiveAliasesFromActiveSet', false);
    fixture.detectChanges();
    fixture.componentInstance['openConfirm']();
    fixture.componentRef.setInput('selectedEmotes', [
      { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'], hidden: false },
    ]);
    fixture.detectChanges();
    closed.next(true);

    expect(startDelete).toHaveBeenCalledWith(
      'set-1',
      'somechannel',
      [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }],
      'somechannel',
    );
  });

  // The same thing on the branch that actually matters, checked against what the dialog itself last
  // rendered rather than against the input alone: the signals handed to DeleteConfirmDialogData are
  // the panel's own, so asking them after the reload landed is asking what is on screen. The live
  // alias read is then made for exactly that list.
  it('deletes what the confirmation last showed when a reload shrinks the selection behind the open dialog', () => {
    const shown = fixture.componentInstance as unknown as {
      visibleSelectedEmoteNames: () => string[];
    };
    fixture.componentInstance['openConfirm']();
    // The reload lands while the modal is still open: KEKW is gone from the grid, so the dialog —
    // which reads these very signals — has stopped naming it.
    fixture.componentRef.setInput('selectedEmotes', [
      { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'], hidden: false },
    ]);
    fixture.detectChanges();
    expect(shown.visibleSelectedEmoteNames()).toEqual(['PogU']);

    closed.next(true);
    httpMock.expectOne(GQL).flush(entriesPage([{ id: '7tv-1', alias: 'PogU' }]));

    expect(startDelete).toHaveBeenCalledWith(
      'set-1',
      'somechannel',
      [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }],
      'somechannel',
    );
  });

  // The extreme of the same reload: nothing is left to delete at confirm time. startDelete would
  // refuse the empty list without a word, which is the one outcome a confirmed delete must not
  // produce — before the snapshot moved to confirm time, an emptied selection at least started a
  // doomed run whose failed rows were visible.
  it('says so instead of silently doing nothing when the reload left nothing selected', () => {
    fixture.componentInstance['openConfirm']();
    fixture.componentRef.setInput('selectedEmotes', []);
    fixture.detectChanges();

    closed.next(true);
    fixture.detectChanges();

    httpMock.expectNone(GQL);
    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.abortedByLock');
    expect(statusText()).toContain('massDelete.selectionGoneDuringConfirm');
  });

  // K5 fix round item 7: the run used to read the live channelName() input at the point
  // deleteService.startDelete was finally called, instead of the value frozen at dialog open.
  it("freezes the run's channel name at dialog open", () => {
    confirm();
    fixture.componentRef.setInput('channelName', 'otherchannel');
    fixture.detectChanges();

    // Both confirmed ids known to the read (#227 P1) — this test is about the channel name, not
    // about the missing-id block.
    httpMock.expectOne(GQL).flush(
      entriesPage([
        { id: '7tv-1', alias: 'PogU' },
        { id: '7tv-2', alias: 'KEKW' },
      ]),
    );

    expect(startDelete.mock.calls[0][1]).toBe('somechannel');
  });
});

// #227 (fixing the #200 K6 known limitation): the vote-session page's rows are frozen at
// session-creation time (VoteSessionEmote.NameAtCreation) and can never carry a live alias — unlike
// the usage page's active-set flag, this one has to fire for the panel's own set regardless of
// whether that set happens to be the channel's active one, because the vote page's target is most
// often a non-active set-session's set (K6).
describe("MassDeletePanel — readLiveAliasesFromSet reads the panel's own set regardless of active-ness (#227)", () => {
  const GQL = 'https://7tv.io/v4/gql';
  let fixture: ComponentFixture<MassDeletePanel>;
  let httpMock: HttpTestingController;
  let startDelete: ReturnType<typeof vi.fn>;
  let closed: Subject<boolean | undefined>;

  function entriesPage(entries: { id: string; alias?: string }[], pageCount = 1) {
    return {
      data: {
        emoteSets: {
          emoteSet: {
            emotes: {
              totalCount: entries.length,
              pageCount,
              items: entries.map(({ id, alias }) => ({ alias, emote: { id } })),
            },
          },
        },
      },
    };
  }

  async function setUp(inputs: {
    setId: string;
    activeSetId: string | null;
    selectedEmotes: DeletableEmote[];
  }): Promise<void> {
    startDelete = vi.fn();
    closed = new Subject<boolean | undefined>();
    const deleteService = { ...fakeDeleteService(), startDelete };
    const providers = panelProviders({
      deleteService,
      dialogOpen: vi.fn().mockReturnValue({ closed }),
      emoteAdminService: {
        getSetWarning: () =>
          of({
            available: true,
            isOwnSet: true,
            otherTrackedChannelsSharingSet: [],
            otherModeratedChannelsSharingSet: [],
          }),
      },
    });
    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [...providers, provideHttpClientTesting()],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent(MassDeletePanel);
    fixture.componentRef.setInput('setId', inputs.setId);
    fixture.componentRef.setInput('activeSetId', inputs.activeSetId);
    fixture.componentRef.setInput('channelName', 'somechannel');
    fixture.componentRef.setInput('readLiveAliasesFromSet', true);
    fixture.componentRef.setInput('selectedEmotes', inputs.selectedEmotes);
    fixture.detectChanges();
  }

  function statusText(): string {
    const region = (fixture.nativeElement as HTMLElement).querySelector('[role="status"]');
    return region?.textContent ?? '';
  }

  function confirm(): void {
    fixture.componentInstance['openConfirm']();
    closed.next(true);
    fixture.detectChanges();
  }

  afterEach(() => {
    httpMock.verify();
  });

  it("reads the panel's own set, not the active one, once confirmed", async () => {
    await setUp({
      setId: 'set-halloween',
      activeSetId: 'set-active',
      selectedEmotes: [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-pump', name: 'PumpkinAtCreation', hidden: false },
      ],
    });

    confirm();

    const req = httpMock.expectOne(GQL);
    expect(req.request.body.variables.id).toBe('set-halloween');
    req.flush(entriesPage([{ id: '7tv-pump', alias: 'Pumpkin' }]));

    expect(startDelete).toHaveBeenCalledWith(
      'set-halloween',
      'somechannel',
      [
        {
          emoteId: 'e1',
          sevenTvEmoteId: '7tv-pump',
          name: 'PumpkinAtCreation',
          aliases: ['Pumpkin'],
        },
      ],
      null,
    );
  });

  it('records every alias of a #74 duplicate read live from the non-active set', async () => {
    await setUp({
      setId: 'set-halloween',
      activeSetId: 'set-active',
      selectedEmotes: [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-pump', name: 'PumpkinAtCreation', hidden: false },
      ],
    });

    confirm();
    httpMock.expectOne(GQL).flush(
      entriesPage([
        { id: '7tv-pump', alias: 'Pumpkin' },
        { id: '7tv-pump', alias: 'Pumpkin2' },
      ]),
    );

    expect(startDelete).toHaveBeenCalledWith(
      'set-halloween',
      'somechannel',
      [
        {
          emoteId: 'e1',
          sevenTvEmoteId: '7tv-pump',
          name: 'PumpkinAtCreation',
          aliases: ['Pumpkin', 'Pumpkin2'],
        },
      ],
      null,
    );
  });

  // The behaviour #227's issue explicitly asks for: a delete must not silently continue under the
  // frozen name when the live read cannot confirm it. Same dedicated massDelete.memberRead.* keys
  // as the active-set read.
  it('deletes nothing under the frozen name when the live read fails', async () => {
    await setUp({
      setId: 'set-halloween',
      activeSetId: 'set-active',
      selectedEmotes: [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-pump', name: 'PumpkinAtCreation', hidden: false },
      ],
    });

    confirm();
    httpMock.expectOne(GQL).error(new ProgressEvent('network error'));
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.nothingDeleted');
    expect(statusText()).toContain('massDelete.memberRead.unavailable');
  });

  it('deletes nothing when the live read only knows part of the non-active set', async () => {
    await setUp({
      setId: 'set-halloween',
      activeSetId: 'set-active',
      selectedEmotes: [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-pump', name: 'PumpkinAtCreation', hidden: false },
      ],
    });

    confirm();
    for (let page = 1; page <= 10; page++) {
      httpMock.expectOne(GQL).flush(entriesPage([], 11));
    }
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.memberRead.truncated');
  });

  // The flag is unconditional on active-ness (unlike readLiveAliasesFromActiveSet): it still fires
  // even when the panel's own set happens to equal the channel's active one.
  it("still reads live even when the panel's own set happens to be the active one", async () => {
    await setUp({
      setId: 'set-1',
      activeSetId: 'set-1',
      selectedEmotes: [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-pump', name: 'PumpkinAtCreation', hidden: false },
      ],
    });

    confirm();

    httpMock.expectOne(GQL).flush(entriesPage([{ id: '7tv-pump', alias: 'Pumpkin' }]));
    expect(startDelete).toHaveBeenCalledWith(
      'set-1',
      'somechannel',
      [
        {
          emoteId: 'e1',
          sevenTvEmoteId: '7tv-pump',
          name: 'PumpkinAtCreation',
          aliases: ['Pumpkin'],
        },
      ],
      'somechannel',
    );
  });

  // Opus review P1 (#227): the exact case the issue describes — Ghost was on the frozen ballot,
  // left the Halloween set, and the live read now confirms it is simply not there. Blocked, not
  // silently deleted under its frozen name.
  it("blocks the run instead of falling back to the frozen name when the vote page's own read is missing a confirmed id", async () => {
    await setUp({
      setId: 'set-halloween',
      activeSetId: 'set-active',
      selectedEmotes: [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-ghost', name: 'GhostAtCreation', hidden: false },
      ],
    });

    confirm();
    httpMock.expectOne(GQL).flush(entriesPage([])); // complete, empty: Ghost is gone
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.memberRead.missingFromSet.one');
  });

  it('makes no read at all when the host did not opt in', async () => {
    startDelete = vi.fn();
    closed = new Subject<boolean | undefined>();
    const deleteService = { ...fakeDeleteService(), startDelete };
    const providers = panelProviders({
      deleteService,
      dialogOpen: vi.fn().mockReturnValue({ closed }),
      emoteAdminService: {
        getSetWarning: () =>
          of({
            available: true,
            isOwnSet: true,
            otherTrackedChannelsSharingSet: [],
            otherModeratedChannelsSharingSet: [],
          }),
      },
    });
    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [...providers, provideHttpClientTesting()],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(MassDeletePanel);
    fixture.componentRef.setInput('setId', 'set-halloween');
    fixture.componentRef.setInput('activeSetId', 'set-active');
    fixture.componentRef.setInput('channelName', 'somechannel');
    // readLiveAliasesFromSet left at its false default — same as readLiveAliasesFromActiveSet's own
    // "did not opt in" case above.
    fixture.componentRef.setInput('selectedEmotes', [
      { emoteId: 'e1', sevenTvEmoteId: '7tv-pump', name: 'PumpkinAtCreation', hidden: false },
    ]);
    fixture.detectChanges();

    confirm();

    httpMock.expectNone(GQL);
    expect(startDelete).toHaveBeenCalledTimes(1);
  });
});

// #200 K5 finding F: the restore-confirm path used to read `this.channelName()` — the panel's
// live input — at three call sites, instead of the finished run's own frozen `channelName`
// (`DeleteRunInfo.channelName`). Harmless while a panel only ever sees one channel across a run's
// lifetime, which is true today, but the wrong source of truth all the same — the same class of
// bug finding A fixed for `setId`. Pinned here against a panel whose live `channelName` input
// disagrees with the run's own, which cannot happen in production today but must not silently
// resolve to the live value if it ever does.
/** `resolveEditableSet(setId)` finds `setId` under `trackedChannel`'s account, `kind: 'NORMAL'`
 *  and `editable: true` — the one account/set pair every test in the block below needs, since
 *  `SevenTvEmoteSetService` is never mocked at the service level here (real service, real
 *  `HttpClient`, intercepted by `HttpTestingController` like every other request in this block). */
function targetsResponse(setId: string, trackedChannel: string): EmoteSetTargetsResponse {
  return {
    accounts: [
      {
        twitchChannelId: 'tw-1',
        twitchLogin: trackedChannel,
        isOwnAccount: true,
        trackedChannelName: trackedChannel,
        activeEmoteSetId: setId,
        sets: [
          {
            id: setId,
            name: setId,
            capacity: null,
            kind: 'NORMAL',
            isActive: true,
            isPersonal: false,
            ownerDisplayName: null,
            ownerSevenTvUserId: 'owner-1',
            editable: true,
          },
        ],
        setsUnavailable: false,
        sevenTvUserId: 'owner-1',
      },
    ],
    sevenTvUnavailable: false,
  };
}

describe('MassDeletePanel — the restore-confirm path resolves its target fresh and attributes the dock to the live page (#253 spec E13/E16)', () => {
  let fixture: ComponentFixture<MassDeletePanel>;
  let httpMock: HttpTestingController;
  let getSetStatus: ReturnType<typeof vi.fn>;
  let startRestore: ReturnType<typeof vi.fn>;
  let dialogOpen: ReturnType<typeof vi.fn>;
  let closed: Subject<boolean | undefined>;
  /** Hoisted out of `beforeEach` (unlike most fields there) so individual tests can reshape the
   *  finished run — #255 P3(11)'s partial-filtering test needs a second done row. */
  let lastRun: WritableSignal<{ setId: string; channelName: string; result: RunResult } | null>;

  // The tracked channel the fresh pre-check resolves `set-1` to — deliberately equal to the
  // delete run's own frozen `channelName` (a realistic case: the account that owns the target set
  // is the same one that ran the delete), and deliberately distinct from `LIVE_CHANNEL` so a test
  // that asserted the *pre-#253* value would still fail if this leaked in by accident. `LIVE_CHANNEL`
  // is the panel's current page — since #253 that is `hostChannelName`, no longer the mutation's
  // expected channel (spec 6.3: `hostChannelName = channelName()`, `expectedChannelName` comes from
  // the resolved target instead).
  const RUN_CHANNEL = 'runchannel';
  const LIVE_CHANNEL = 'livechannel';

  /** Flushes the one pre-check request every test in this block triggers via `openRestoreConfirm`
   *  (spec E16, E19) before the rest of the chain can proceed. */
  function flushTargetsResponse(): void {
    httpMock
      .expectOne('/api/seventv/me/emote-set-targets')
      .flush(targetsResponse('set-1', RUN_CHANNEL));
  }

  beforeEach(async () => {
    closed = new Subject<boolean | undefined>();
    getSetStatus = vi.fn().mockReturnValue(of({ occupiedSlots: 1, capacity: 100 }));
    startRestore = vi.fn();
    dialogOpen = vi.fn().mockReturnValue({ closed });
    lastRun = signal({
      setId: 'set-1',
      channelName: RUN_CHANNEL,
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
    const restoreService = { ...fakeRestoreService(), startRestore };
    const emoteAdminService = { getSetStatus } as unknown as Partial<EmoteAdminService>;
    const providers = panelProviders({
      deleteService: fakeDeleteService({ lastRun }),
      restoreService,
      dialogOpen,
      emoteAdminService,
      // This block drives resolveEditableSet through the real service and HttpTestingController
      // (targetsResponse() below) — the default editable stub would answer before the test ever
      // gets to flush its own response.
      emoteSetService: null,
    });

    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [...providers, provideHttpClientTesting()],
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');
    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent(MassDeletePanel);
    fixture.componentRef.setInput('setId', 'set-1');
    fixture.componentRef.setInput('activeSetId', 'set-1');
    fixture.componentRef.setInput('channelName', LIVE_CHANNEL);
    fixture.componentRef.setInput('selectedEmotes', []);
    fixture.detectChanges();
  });

  afterEach(() => {
    httpMock.verify();
  });

  it("reads the slot-status check from the resolved target's tracked channel, not the live page", () => {
    fixture.componentInstance['openRestoreConfirm']();
    flushTargetsResponse();

    // #255 P3(10): the slot-status read only starts once the open-time duplicate check has
    // answered and a confirmation is actually going to open — draining it first, with nothing to
    // filter, is what lets the slot read fire at all here.
    httpMock.expectOne('https://7tv.io/v4/gql').flush({
      data: {
        emoteSets: { emoteSet: { emotes: { totalCount: 0, pageCount: 1, items: [] } } },
      },
    });

    expect(getSetStatus).toHaveBeenCalledWith(RUN_CHANNEL);
    expect(getSetStatus).not.toHaveBeenCalledWith(LIVE_CHANNEL);
  });

  it('starts the restore against the resolved target, attributing the dock to the live page', () => {
    fixture.componentInstance['openRestoreConfirm']();
    flushTargetsResponse();

    // #255: the open-time duplicate check runs, and fails open, before the confirmation opens at
    // all — same read, same failure handling as the confirm-time one below.
    httpMock.expectOne('https://7tv.io/v4/gql').error(new ProgressEvent('error'));

    closed.next(true);

    // filterAlreadyPresent's own fresh 7TV read at confirm time — fails open too.
    httpMock.expectOne('https://7tv.io/v4/gql').error(new ProgressEvent('error'));

    expect(startRestore).toHaveBeenCalledTimes(1);
    // The mutation target (expectedChannelName/resyncChannelName/ownerOrChannelLabel) comes from
    // the fresh pre-check (spec 6.2/6.4), never from the panel's live channelName — its set is the
    // resolved account's active one here, so its channel is the expected hit and there is no
    // client resync. `hostChannelName` is the live page instead (spec 6.3, E13): the dock belongs
    // to wherever the button was actually clicked, not to the delete run's frozen channel.
    expect(startRestore.mock.calls[0][0]).toEqual({
      setId: 'set-1',
      expectedChannelName: RUN_CHANNEL,
      resyncChannelName: null,
      hostChannelName: LIVE_CHANNEL,
      setName: 'set-1',
      ownerOrChannelLabel: RUN_CHANNEL,
    });
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
      expect.objectContaining({ setId: 'set-1', hostChannelName: LIVE_CHANNEL }),
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

  // Spec E16, 4.6 point 22; Plan-253 §6, Nr. 3: a blocked pre-check shows the panel's existing
  // abort notice with a restore-specific lead line and the `restore.errors.*` family — no
  // confirmation, no slot-status read, no run.
  it('shows the abort notice and starts nothing when the pre-check finds the set not editable', () => {
    fixture.componentInstance['openRestoreConfirm']();
    httpMock
      .expectOne('/api/seventv/me/emote-set-targets')
      .flush({ accounts: [], sevenTvUnavailable: false });

    expect(fixture.componentInstance['abortNotice']()).toEqual({
      leadKey: 'restore.nothingRestored',
      reasonKey: 'restore.errors.targetNotEditable',
    });
    expect(getSetStatus).not.toHaveBeenCalled();
    expect(startRestore).not.toHaveBeenCalled();
  });

  // restoreTargetCheckReasonKey's "notSelectable" branch (Plan-253 §6, Nr. 4) had no case of its
  // own here — the delete run's set can only ever have been NORMAL to begin with (the picker never
  // offers another kind), but the mapping stays total rather than assuming that at the call site.
  it('shows the abort notice and starts nothing when the pre-check finds the set no longer selectable', () => {
    fixture.componentInstance['openRestoreConfirm']();
    httpMock.expectOne('/api/seventv/me/emote-set-targets').flush({
      accounts: [
        {
          twitchChannelId: 'tw-1',
          twitchLogin: RUN_CHANNEL,
          isOwnAccount: true,
          trackedChannelName: RUN_CHANNEL,
          activeEmoteSetId: 'set-1',
          sets: [
            {
              id: 'set-1',
              name: 'set-1',
              capacity: null,
              kind: 'GLOBAL',
              isActive: true,
              isPersonal: false,
              ownerDisplayName: null,
              ownerSevenTvUserId: 'owner-1',
              editable: true,
            },
          ],
          setsUnavailable: false,
          sevenTvUserId: 'owner-1',
        },
      ],
      sevenTvUnavailable: false,
    });

    expect(fixture.componentInstance['abortNotice']()).toEqual({
      leadKey: 'restore.nothingRestored',
      reasonKey: 'restore.errors.targetNotSelectable',
    });
    expect(getSetStatus).not.toHaveBeenCalled();
    expect(startRestore).not.toHaveBeenCalled();
  });

  it('maps a degraded pre-check (list incomplete) to the "check unavailable" reason', () => {
    fixture.componentInstance['openRestoreConfirm']();
    httpMock
      .expectOne('/api/seventv/me/emote-set-targets')
      .flush({ accounts: [], sevenTvUnavailable: true });

    expect(fixture.componentInstance['abortNotice']()).toEqual({
      leadKey: 'restore.nothingRestored',
      reasonKey: 'restore.errors.targetCheckUnavailable',
    });
    expect(startRestore).not.toHaveBeenCalled();
  });

  // Review round 1, finding 4: before this fix the subscription had no `error` branch at all — a
  // failed request (429, 503, no connection, spec F3) surfaced nothing, leaving the restore entry
  // silently inert instead of showing the abort notice every other pre-check failure already does.
  it('shows "check unavailable" when the pre-check request itself fails (network error, not a degraded list)', () => {
    fixture.componentInstance['openRestoreConfirm']();
    httpMock.expectOne('/api/seventv/me/emote-set-targets').error(new ProgressEvent('error'));

    expect(fixture.componentInstance['abortNotice']()).toEqual({
      leadKey: 'restore.nothingRestored',
      reasonKey: 'restore.errors.targetCheckUnavailable',
    });
    expect(startRestore).not.toHaveBeenCalled();
  });

  // Review round 1, finding 4: a hung pre-check request used to leave the restore entry silently
  // inert forever — same 20 s budget and same treatment as the delete confirmation's own pre-check.
  it('shows "check unavailable" when the pre-check hangs past its timeout', () => {
    vi.useFakeTimers();
    try {
      fixture.componentInstance['openRestoreConfirm']();
      const req = httpMock.expectOne('/api/seventv/me/emote-set-targets');
      expect(req.cancelled).toBeFalsy();

      vi.advanceTimersByTime(20_000);

      expect(fixture.componentInstance['abortNotice']()).toEqual({
        leadKey: 'restore.nothingRestored',
        reasonKey: 'restore.errors.targetCheckUnavailable',
      });
      expect(startRestore).not.toHaveBeenCalled();
      expect(req.cancelled).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  // Review round 1, finding 4: an answer landing after this panel is torn down must not open a
  // restore confirmation nobody can see or answer any more.
  it('cancels the pre-check request once the panel is destroyed', () => {
    fixture.componentInstance['openRestoreConfirm']();
    const req = httpMock.expectOne('/api/seventv/me/emote-set-targets');
    expect(req.cancelled).toBeFalsy();

    fixture.destroy();

    expect(req.cancelled).toBe(true);
    expect(startRestore).not.toHaveBeenCalled();
  });

  // #255 P3(11): a run with more than one done row, where the open-time check finds only some of
  // them already present — the confirmation must name and count exactly the survivors, not the
  // whole run and not nothing.
  it('shows only the row the open-time check found missing, filtering out the one already present', () => {
    lastRun.set({
      setId: 'set-1',
      channelName: RUN_CHANNEL,
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
});

// #253, spec 4.6 point 20, AK 31: the shared pre-check now runs before the delete confirmation
// itself opens, not only before the panel's own restore entry (the block above). Real
// `SevenTvEmoteSetService` over `HttpTestingController` (`emoteSetService: null`, same reasoning as
// the restore-confirm-path block above) so each test can drive the pre-check's own answer.
describe('MassDeletePanel — the shared pre-check runs before the delete confirmation opens (#253 AK 31)', () => {
  let fixture: ComponentFixture<MassDeletePanel>;
  let httpMock: HttpTestingController;
  let dialogOpen: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    dialogOpen = vi.fn().mockReturnValue({ closed: of(undefined) });
    const providers = panelProviders({
      dialogOpen,
      emoteSetService: null,
      emoteAdminService: {
        getSetWarning: () =>
          of({
            available: true,
            isOwnSet: true,
            otherTrackedChannelsSharingSet: [],
            otherModeratedChannelsSharingSet: [],
          }),
      },
    });
    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [...providers, provideHttpClientTesting()],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent(MassDeletePanel);
    fixture.componentRef.setInput('setId', 'set-1');
    fixture.componentRef.setInput('channelName', 'somechannel');
    fixture.componentRef.setInput('selectedEmotes', EMOTES);
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  it('locks the delete button and opens no dialog while the pre-check is out, then opens it on an editable answer', () => {
    fixture.componentInstance['openConfirm']();
    fixture.detectChanges();

    // Rule 12: the observable contract is the button's own disabled state, not the internal
    // `deleteTargetCheckPending` signal that happens to drive it.
    expect(
      findButtonByLabel(fixture.nativeElement, deleteButtonLabel(EMOTES.length)).disabled,
    ).toBe(true);
    expect(dialogOpen).not.toHaveBeenCalled();

    httpMock
      .expectOne('/api/seventv/me/emote-set-targets')
      .flush(targetsResponse('set-1', 'somechannel'));
    fixture.detectChanges();

    expect(
      findButtonByLabel(fixture.nativeElement, deleteButtonLabel(EMOTES.length)).disabled,
    ).toBe(false);
    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance['abortNotice']()).toBeNull();
  });

  it('shows the abort notice and opens no dialog when the pre-check finds the set not editable', () => {
    fixture.componentInstance['openConfirm']();
    httpMock
      .expectOne('/api/seventv/me/emote-set-targets')
      .flush({ accounts: [], sevenTvUnavailable: false });

    expect(fixture.componentInstance['deleteTargetCheckPending']()).toBe(false);
    expect(fixture.componentInstance['abortNotice']()).toEqual({
      leadKey: 'massDelete.nothingDeleted',
      reasonKey: 'massDelete.errors.targetNotEditable',
    });
    expect(dialogOpen).not.toHaveBeenCalled();
  });

  it('maps a degraded pre-check (list incomplete) to the "check unavailable" reason', () => {
    fixture.componentInstance['openConfirm']();
    httpMock
      .expectOne('/api/seventv/me/emote-set-targets')
      .flush({ accounts: [], sevenTvUnavailable: true });

    expect(fixture.componentInstance['abortNotice']()).toEqual({
      leadKey: 'massDelete.nothingDeleted',
      reasonKey: 'massDelete.errors.targetCheckUnavailable',
    });
    expect(dialogOpen).not.toHaveBeenCalled();
  });

  it('maps a failed pre-check request (429) to the "check unavailable" reason too', () => {
    fixture.componentInstance['openConfirm']();
    httpMock
      .expectOne('/api/seventv/me/emote-set-targets')
      .flush(null, { status: 429, statusText: 'Too Many Requests' });

    expect(fixture.componentInstance['abortNotice']()).toEqual({
      leadKey: 'massDelete.nothingDeleted',
      reasonKey: 'massDelete.errors.targetCheckUnavailable',
    });
    expect(dialogOpen).not.toHaveBeenCalled();
  });

  // Review round 1, finding 3b: a hung pre-check request used to leave the delete button disabled
  // forever, with no way out short of reloading — same 20 s budget and same treatment (a timeout
  // reads exactly like any other failed check) as the active-set live alias read's own fix.
  it('unlocks the button and shows "check unavailable" when the pre-check hangs past its timeout', () => {
    vi.useFakeTimers();
    try {
      fixture.componentInstance['openConfirm']();
      const req = httpMock.expectOne('/api/seventv/me/emote-set-targets');
      expect(fixture.componentInstance['deleteTargetCheckPending']()).toBe(true);
      expect(req.cancelled).toBeFalsy();

      vi.advanceTimersByTime(20_000);

      expect(fixture.componentInstance['deleteTargetCheckPending']()).toBe(false);
      expect(fixture.componentInstance['abortNotice']()).toEqual({
        leadKey: 'massDelete.nothingDeleted',
        reasonKey: 'massDelete.errors.targetCheckUnavailable',
      });
      expect(dialogOpen).not.toHaveBeenCalled();
      // `timeout()` unsubscribes the source on expiry — the request is cancelled, not answered.
      expect(req.cancelled).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  // Review round 1, finding 3c: an answer that lands after this panel was torn down must not open
  // a confirmation nobody can see or answer any more. `takeUntilDestroyed` unsubscribes the whole
  // pipe synchronously on destroy, which cancels the still-open request outright — a stronger
  // guarantee than merely dropping a late answer, and proof the panel never even waits for one.
  it('cancels the pre-check request and opens no dialog once the panel is destroyed', () => {
    fixture.componentInstance['openConfirm']();
    const req = httpMock.expectOne('/api/seventv/me/emote-set-targets');
    expect(req.cancelled).toBeFalsy();

    fixture.destroy();

    expect(req.cancelled).toBe(true);
    expect(dialogOpen).not.toHaveBeenCalled();
  });

  // Codex C3 / final fix wave A6, superseding review round 1 finding 3a: opening a confirmation
  // silently for the set the pre-check happened to vouch for — even though the host has since
  // switched away from it — used to defer the abort until the dialog closed
  // (`abortReasonBeforeStart`). That left a confirmation open for a set nobody had selected any
  // more. A switch behind the still-open pre-check now aborts immediately, visibly, instead.
  it('aborts with setChangedDuringConfirm and opens no dialog when the set switches behind the still-open pre-check', () => {
    fixture.componentRef.setInput('setName', 'Set A');
    fixture.detectChanges();
    fixture.componentInstance['openConfirm']();
    const req = httpMock.expectOne('/api/seventv/me/emote-set-targets');
    expect(req.request.url).toContain('/api/seventv/me/emote-set-targets');

    // The set switches behind the still-open pre-check.
    fixture.componentRef.setInput('setId', 'set-2');
    fixture.componentRef.setInput('setName', 'Set B');
    fixture.detectChanges();

    // Answers for the originally checked set ('set-1'), editable — but no longer the one
    // selected by the time the answer arrives.
    req.flush(targetsResponse('set-1', 'somechannel'));

    expect(dialogOpen).not.toHaveBeenCalled();
    expect(fixture.componentInstance['abortNotice']()).toEqual({
      leadKey: 'massDelete.abortedByLock',
      reasonKey: 'massDelete.setChangedDuringConfirm',
    });
  });
});
