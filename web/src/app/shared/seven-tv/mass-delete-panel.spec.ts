import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { Component, WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvDeleteService, SyncReportState } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { RunQueueItem, RunResult } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { CSV_MIME } from '../export/csv';
import { JSON_MIME } from '../export/export-envelope';
import { DeletableEmote, MassDeletePanel } from './mass-delete-panel';

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
}

/** Spies on the same two seams `downloadFile` touches — restore via `vi.restoreAllMocks()` in
 *  `afterEach`, matching `file-download.spec.ts`'s own pattern. */
function captureDownloads(): CapturedDownload[] {
  const downloads: CapturedDownload[] = [];
  if (!('createObjectURL' in URL)) {
    Object.assign(URL, { createObjectURL: () => '', revokeObjectURL: () => undefined });
  }
  vi.spyOn(URL, 'createObjectURL').mockImplementation((blob: Blob | MediaSource) => {
    downloads.push({ filename: '', mimeType: (blob as Blob).type });
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
};

const DELETE_LABEL = 'Löschen (2)';
const CLEAR_LABEL = 'Auswahl aufheben';
const VOTE_LABEL = 'Zur Abstimmung stellen';

const EMOTES: DeletableEmote[] = [
  { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
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
            rateLimitPauseSeconds: signal(0),
            resyncTrigger: signal('idle'),
            skippedDuplicates: signal(0),
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
        doneIds: ['e1'],
        doneKeys: ['e1'],
        items: [
          { key: 'e1', emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', status: 'done' },
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
            rateLimitPauseSeconds: signal(0),
            resyncTrigger: signal('idle'),
            skippedDuplicates: signal(0),
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

  it('downloads nothing and leaves protocolSaved alone when the dialog closes with nothing', () => {
    openSpy.mockReturnValue({ closed: of(undefined) });

    panel['openProtocolExport']();

    expect(downloads).toHaveLength(0);
    expect(panel['protocolSaved']()).toBe(false);
  });
});

/**
 * #149: the notice for a pre-run duplicate check that could not run at all
 * (`already-present-filter.ts`'s `available: false`) — distinct from, and independent of, the
 * `skippedDuplicates` notice above it. Mounts `MassDeletePanel` directly so `duplicateCheckAvailable`
 * and `duplicateNoticePending` can be driven straight from the test, same style as the
 * protocol-export block above. The real service always sets both together (`startRestore` calls
 * `showDuplicateNotice` right after setting `duplicateCheckAvailable`) — these tests drive them
 * independently on purpose, to pin the P2 fix (design doc §4.5's transient-notice convention) as its
 * own behaviour rather than assuming the coupling.
 */
describe('MassDeletePanel — duplicate-check-unavailable notice (#149)', () => {
  const DUPLICATE_CHECK_TRANSLATIONS = {
    ...DE_TRANSLATIONS,
    restore: {
      duplicateCheckUnavailable:
        'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
    },
  };

  let fixture: ComponentFixture<MassDeletePanel>;
  let duplicateCheckAvailable: WritableSignal<boolean>;
  let duplicateNoticePending: WritableSignal<boolean>;

  beforeEach(async () => {
    duplicateCheckAvailable = signal(true);
    duplicateNoticePending = signal(true);

    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: DUPLICATE_CHECK_TRANSLATIONS },
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
            rateLimitPauseSeconds: signal(0),
            resyncTrigger: signal('idle'),
            skippedDuplicates: signal(0),
            duplicateCheckAvailable,
            duplicateNoticePending,
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

    fixture = TestBed.createComponent(MassDeletePanel);
    fixture.componentRef.setInput('setId', 'set-1');
    fixture.componentRef.setInput('channelName', 'somechannel');
    fixture.componentRef.setInput('selectedEmotes', []);
  });

  it('shows nothing while the check is available (the default, and every run that verified fine)', () => {
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('Wir konnten gerade nicht prüfen');
  });

  it('shows the quiet notice once the check is reported unavailable, stating the consequence', () => {
    duplicateCheckAvailable.set(false);

    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain(
      'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
    );
  });

  // #149 P2 (independent review): the notice is transient (design doc §4.5), not a persistent flag
  // — once its window has elapsed (duplicateNoticePending flips back to false, e.g. a later,
  // unrelated run has since settled), it must not keep showing just because duplicateCheckAvailable
  // still happens to read false from a stale earlier run.
  it('hides the notice again once its pending window has elapsed, even while duplicateCheckAvailable still reads false', () => {
    duplicateCheckAvailable.set(false);
    duplicateNoticePending.set(false);

    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('Wir konnten gerade nicht prüfen');
  });
});

/**
 * #134: on the usage-stats page this panel lives in the action dock, which can mount in the same
 * change-detection pass that sets a notice — a status region created together with its text
 * announces nothing. So the panel's resync and duplicate-check notices are shown but aria-hidden,
 * and the host page's permanently mounted DockOutcomeAnnouncer speaks them instead
 * (docs/UI-Designsprache.md §4.5). Pinned here: the panel's own status regions (RunProgressPanel is
 * one) never announce these notices, so nothing is spoken twice.
 */
function announcedByStatusRegions(root: HTMLElement): string {
  return Array.from(root.querySelectorAll('[role="status"]'))
    .map((region) => {
      const copy = region.cloneNode(true) as HTMLElement;
      copy.querySelectorAll('[aria-hidden="true"]').forEach((hidden) => hidden.remove());
      return copy.textContent?.trim() ?? '';
    })
    .join(' ')
    .trim();
}

describe('MassDeletePanel — resync and duplicate-check notices are shown, not announced (#134)', () => {
  const STATUS_REGION_TRANSLATIONS = {
    ...DE_TRANSLATIONS,
    restore: {
      duplicateCheckUnavailable:
        'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
      resync: {
        pending: 'Synchronisierung wird angestoßen…',
        succeeded: 'Synchronisierung angestoßen — die Liste aktualisiert sich gleich.',
        cooldown:
          'Sync-Cooldown aktiv — die Liste aktualisiert sich innerhalb einer Minute von selbst.',
        failed:
          'Synchronisierung konnte nicht angestoßen werden — der periodische Sync holt es innerhalb einer Minute nach.',
      },
    },
  };

  let fixture: ComponentFixture<MassDeletePanel>;
  let resyncTrigger: WritableSignal<'idle' | 'pending' | 'succeeded' | 'cooldown' | 'failed'>;
  let duplicateCheckAvailable: WritableSignal<boolean>;
  let duplicateNoticePending: WritableSignal<boolean>;

  beforeEach(async () => {
    resyncTrigger = signal('idle');
    duplicateCheckAvailable = signal(true);
    duplicateNoticePending = signal(false);

    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: STATUS_REGION_TRANSLATIONS },
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
            rateLimitPauseSeconds: signal(0),
            lastRun: signal(null),
          } as unknown as SevenTvDeleteService,
        },
        {
          provide: SevenTvRestoreService,
          useValue: {
            isRunning: signal(false),
            // A non-empty queue, not running: the resync notice sits in the run-actions slot,
            // which RunProgressPanel only projects once the restore run has settled
            // (!isRunning() && total() > 0) — matching how resyncTrigger is only ever written from
            // onRunComplete in the real service.
            queue: signal([{ key: 'a', sevenTvEmoteId: '7tv-a', name: 'A', status: 'done' }]),
            syncReport: signal('idle'),
            rateLimitPauseSeconds: signal(0),
            resyncTrigger,
            skippedDuplicates: signal(0),
            duplicateCheckAvailable,
            duplicateNoticePending,
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

    fixture = TestBed.createComponent(MassDeletePanel);
    fixture.componentRef.setInput('setId', 'set-1');
    fixture.componentRef.setInput('channelName', 'somechannel');
    fixture.componentRef.setInput('selectedEmotes', []);
  });

  it('shows the resync notice aria-hidden, so no status region of the panel speaks it', () => {
    resyncTrigger.set('pending');
    fixture.detectChanges();

    const text = 'Synchronisierung wird angestoßen…';
    const notice: HTMLElement | undefined = Array.from<HTMLElement>(
      fixture.nativeElement.querySelectorAll('[aria-hidden="true"]'),
    ).find((element) => element.textContent?.trim() === text);
    expect(notice).toBeDefined();
    expect(announcedByStatusRegions(fixture.nativeElement)).not.toContain(text);
  });

  it('shows the duplicate-check-unavailable notice aria-hidden, so no status region of the panel speaks it', () => {
    duplicateCheckAvailable.set(false);
    duplicateNoticePending.set(true);
    fixture.detectChanges();

    const text =
      'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.';
    const notice: HTMLElement | undefined = Array.from<HTMLElement>(
      fixture.nativeElement.querySelectorAll('[aria-hidden="true"]'),
    ).find((element) => element.textContent?.trim() === text);
    expect(notice).toBeDefined();
    expect(announcedByStatusRegions(fixture.nativeElement)).not.toContain(text);
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
  'isRunning' | 'queue' | 'syncReport' | 'rateLimitPauseSeconds' | 'lastRun'
>;

function fakeDeleteService(overrides: Partial<DeleteServiceFake> = {}): DeleteServiceFake {
  return {
    isRunning: signal(false),
    queue: signal<RunQueueItem[]>([]),
    syncReport: signal<SyncReportState>('idle'),
    rateLimitPauseSeconds: signal<number | null>(null),
    lastRun: signal<{ setId: string; channelName: string; result: RunResult } | null>(null),
    ...overrides,
  };
}

type RestoreServiceFake = Pick<
  SevenTvRestoreService,
  | 'isRunning'
  | 'queue'
  | 'syncReport'
  | 'rateLimitPauseSeconds'
  | 'resyncTrigger'
  | 'skippedDuplicates'
  | 'duplicateCheckAvailable'
  | 'duplicateNoticePending'
>;

function fakeRestoreService(overrides: Partial<RestoreServiceFake> = {}): RestoreServiceFake {
  return {
    isRunning: signal(false),
    queue: signal<RunQueueItem[]>([]),
    syncReport: signal<SyncReportState>('idle'),
    rateLimitPauseSeconds: signal<number | null>(null),
    resyncTrigger: signal('idle'),
    skippedDuplicates: signal(0),
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

/** The provider list every #89 block below needs, differing only in which fakes a test wants to
 *  drive — the rest default to an idle/untouched instance. */
function panelProviders(
  options: {
    deleteService?: DeleteServiceFake;
    restoreService?: RestoreServiceFake;
    arbiter?: RunArbiterFake;
    dialogOpen?: ReturnType<typeof vi.fn>;
  } = {},
) {
  return [
    provideHttpClient(),
    { provide: EmoteAdminService, useValue: {} as unknown as EmoteAdminService },
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

  function runResult(doneIds: string[]): RunResult {
    return {
      doneIds,
      doneKeys: doneIds,
      items: doneIds.map((id) => ({
        key: id,
        emoteId: id,
        sevenTvEmoteId: `7tv-${id}`,
        name: id,
        status: 'done',
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

  it('emits deleted with the run doneIds once the closing sync report succeeds', () => {
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
    // doneIds.length === 0: there is nothing to drop from the host list and nothing wrong to
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
    return { key, emoteId: key, sevenTvEmoteId: `7tv-${key}`, name: key, status: 'done' };
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

  async function render(selectedEmotes: DeletableEmote[]): Promise<HTMLButtonElement> {
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
});
