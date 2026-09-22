import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { Subject, of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvDeleteService, SyncReportState } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { RunQueueItem, RunResult } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { CSV_MIME } from '../export/csv';
import { JSON_MIME } from '../export/export-envelope';
import { DeleteConfirmDialog, DeleteConfirmDialogData } from './delete-confirm-dialog';
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
        doneKeys: ['7tv-1', '7tv-live'],
        items: [
          { key: '7tv-1', emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', status: 'done' },
          // A set-view row without a local emote (spec #200, 7.1) — see the no-filter case below.
          { key: '7tv-live', sevenTvEmoteId: '7tv-live', name: 'LiveOnly', status: 'done' },
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
    emoteAdminService?: Partial<EmoteAdminService>;
  } = {},
) {
  return [
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
          { key: '7tv-1', emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', status: 'done' },
          { key: '7tv-live', sevenTvEmoteId: '7tv-live', name: 'LiveOnly', status: 'done' },
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

    expect(startDelete).toHaveBeenCalledWith('set-1', 'somechannel', [
      { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU', 'PogU2'] },
      { emoteId: undefined, sevenTvEmoteId: '7tv-live', name: 'LiveOnly', aliases: undefined },
    ]);
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

  function entriesPage(entries: { id: string; alias: string }[], pageCount = 1) {
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
    const deleteService = { ...fakeDeleteService(), startDelete };
    const providers = panelProviders({
      deleteService,
      arbiter: fakeRunArbiter(activeRun),
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

    expect(startDelete).toHaveBeenCalledWith('set-1', 'somechannel', [
      { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU', 'PogU2'] },
      { emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'KEKW', aliases: ['KEKW'] },
    ]);
  });

  it('keeps the host aliases of a cell the live read does not know', () => {
    confirm();
    httpMock.expectOne(GQL).flush(entriesPage([{ id: '7tv-1', alias: 'PogU' }]));

    expect(startDelete.mock.calls[0][2][1].aliases).toEqual(['KEKW']);
  });

  it('deletes nothing when the live read fails, and says why', () => {
    confirm();
    httpMock.expectOne(GQL).error(new ProgressEvent('network error'));
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('massDelete.abortedByMemberRead');
    expect(statusText()).toContain('usageStats.setView.lock.membersUnavailable');
  });

  it('deletes nothing when 7TV answers the read with a GraphQL error disguised as HTTP 200', () => {
    confirm();
    httpMock.expectOne(GQL).flush({ errors: [{ message: 'rate limited' }] });
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('usageStats.setView.lock.membersUnavailable');
  });

  it('deletes nothing when the live read only knows part of the set', () => {
    confirm();
    for (let page = 1; page <= 10; page++) {
      httpMock.expectOne(GQL).flush(entriesPage([], 11));
    }
    fixture.detectChanges();

    expect(startDelete).not.toHaveBeenCalled();
    expect(statusText()).toContain('usageStats.setView.lock.truncated');
  });

  it('keeps the delete button disabled while the read is out', () => {
    confirm();
    expect(deleteButton().disabled).toBe(true);

    httpMock.expectOne(GQL).flush(entriesPage([]));
    fixture.detectChanges();
    expect(deleteButton().disabled).toBe(false);
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

  it('starts nothing when another run claimed the arbiter while the read was out', () => {
    confirm();
    activeRun.set('import');
    httpMock.expectOne(GQL).flush(entriesPage([]));

    expect(startDelete).not.toHaveBeenCalled();
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
    expect(startDelete).toHaveBeenCalledWith('set-1', 'somechannel', [
      { emoteId: undefined, sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU', 'PogU2'] },
    ]);
  });

  // The vote-session page does not opt in until K6: its rows stay on [name].
  it('makes no read when the host did not opt in', () => {
    fixture.componentRef.setInput('readLiveAliasesFromActiveSet', false);
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
describe("MassDeletePanel — the restore-confirm path reads the run's own channelName, not the live input (#200 K5 finding F)", () => {
  let fixture: ComponentFixture<MassDeletePanel>;
  let httpMock: HttpTestingController;
  let getSetStatus: ReturnType<typeof vi.fn>;
  let startRestore: ReturnType<typeof vi.fn>;
  let closed: Subject<boolean | undefined>;

  const RUN_CHANNEL = 'runchannel';
  const LIVE_CHANNEL = 'livechannel';

  beforeEach(async () => {
    closed = new Subject<boolean | undefined>();
    getSetStatus = vi.fn().mockReturnValue(of({ occupiedSlots: 1, capacity: 100 }));
    startRestore = vi.fn();
    const lastRun: WritableSignal<{
      setId: string;
      channelName: string;
      result: RunResult;
    } | null> = signal({
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
      dialogOpen: vi.fn().mockReturnValue({ closed }),
      emoteAdminService,
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

  it("reads the slot-status check from the run's channelName, not the panel's live one", () => {
    fixture.componentInstance['openRestoreConfirm']();

    expect(getSetStatus).toHaveBeenCalledWith(RUN_CHANNEL);
    expect(getSetStatus).not.toHaveBeenCalledWith(LIVE_CHANNEL);
  });

  it("starts the restore against the run's channelName, not the panel's live one", () => {
    fixture.componentInstance['openRestoreConfirm']();
    closed.next(true);

    // filterAlreadyPresent's own 7TV read — fails open, same as a network hiccup would.
    httpMock.expectOne('https://7tv.io/v4/gql').error(new ProgressEvent('error'));

    expect(startRestore).toHaveBeenCalledTimes(1);
    expect(startRestore.mock.calls[0][1]).toBe(RUN_CHANNEL);
  });
});
