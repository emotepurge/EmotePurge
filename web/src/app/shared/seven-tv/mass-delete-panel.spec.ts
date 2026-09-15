import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { Component, WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { RunResult } from '../../core/seven-tv/seven-tv-run-engine';
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
 * #134: a role="status" region that enters the DOM together with its content announces nothing to
 * most screen reader/browser pairings — only a mutation *inside* an already-mounted region is
 * announced. Pins the fix (design doc §4.5): the sr-only status node for the resync notice and for
 * the duplicate-check-unavailable notice already exists before either notice has anything to say,
 * and the visible copy next to it is a separate aria-hidden twin.
 */
describe('MassDeletePanel — resync and duplicate-check status regions stay mounted (#134)', () => {
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

  it('keeps the same sr-only status node when the resync notice appears, with an aria-hidden visible twin', () => {
    fixture.detectChanges();

    const statusNodeAtRest = fixture.nativeElement.querySelector('span[role="status"]');
    expect(statusNodeAtRest).not.toBeNull();
    expect(statusNodeAtRest?.textContent.trim()).toBe('');

    resyncTrigger.set('pending');
    fixture.detectChanges();

    const statusNodeAfter = fixture.nativeElement.querySelector('span[role="status"]');
    expect(statusNodeAfter).toBe(statusNodeAtRest);
    expect(statusNodeAfter?.textContent.trim()).toBe('Synchronisierung wird angestoßen…');

    expect(
      fixture.nativeElement.querySelector('span[aria-hidden="true"]')?.textContent.trim(),
    ).toBe('Synchronisierung wird angestoßen…');
  });

  it('keeps the same sr-only status node when the duplicate-check-unavailable notice appears, with an aria-hidden visible twin', () => {
    fixture.detectChanges();

    const statusNodes = (): HTMLElement[] =>
      Array.from(fixture.nativeElement.querySelectorAll('p[role="status"]'));
    // The second <p role="status"> in document order is the duplicate-check-unavailable region —
    // the first is the skipped-count one above it.
    const statusNodeAtRest = statusNodes()[1];
    expect(statusNodeAtRest).toBeDefined();
    expect(statusNodeAtRest.textContent?.trim()).toBe('');

    duplicateCheckAvailable.set(false);
    duplicateNoticePending.set(true);
    fixture.detectChanges();

    const statusNodeAfter = statusNodes()[1];
    expect(statusNodeAfter).toBe(statusNodeAtRest);
    expect(statusNodeAfter.textContent?.trim()).toBe(
      'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
    );

    const visibleTwin = fixture.nativeElement.querySelector('p[aria-hidden="true"]');
    expect(visibleTwin?.textContent?.trim()).toBe(
      'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
    );
  });
});
