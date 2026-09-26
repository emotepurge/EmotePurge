import { Signal, WritableSignal, computed, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ResyncTriggerState,
  RestoreRunInfo,
  SevenTvRestoreService,
} from '../../core/seven-tv/seven-tv-restore.service';
import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { SyncReportReason, SyncReportState } from '../../core/seven-tv/sync-report-outcome';
import { RestoreProgressSection } from './restore-progress-section';

// Only the keys this component and the `RunProgressPanel` it wraps actually translate — not the
// full app translation file. Same convention as `import-progress-section.spec.ts`.
const DE_TRANSLATIONS = {
  common: { cancel: 'Abbrechen', close: 'Schließen' },
  restore: {
    duplicateCheckUnavailable:
      'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
    skippedDuplicates: {
      one: '{{ count }} Eintrag war bereits im Zielset und wurde übersprungen.',
      other: '{{ count }} Einträge waren bereits im Zielset und wurden übersprungen.',
    },
    skippedNameTaken: {
      one: '{{ count }} Alias übersprungen — der Name gehört inzwischen einem anderen Emote.',
      other: '{{ count }} Aliase übersprungen — die Namen gehören inzwischen anderen Emotes.',
    },
    progress: '{{ finished }} / {{ total }} verarbeitet',
    progressBarLabel: 'Wiederherstellungsfortschritt',
    settling: 'Wird abgeschlossen…',
    deleteFailedFallback: 'Wiederherstellen fehlgeschlagen',
    unknownOutcome:
      'Unklar, ob wiederhergestellt — 7TV hat nicht eindeutig geantwortet. Bitte im Set nachsehen.',
    rateLimitPaused: '7TV-Rate-Limit erreicht — es geht in {{ seconds }} s automatisch weiter.',
    syncFailedTitle: 'Rückmeldung an EmotePurge fehlgeschlagen',
    syncFailed:
      'Die Emotes sind bei 7TV wiederhergestellt, aber EmotePurge konnte es nicht vermerken.',
    syncPartialTitle: 'Rückmeldung an EmotePurge unvollständig',
    syncPartial:
      'Die Emotes sind bei 7TV wiederhergestellt und bei EmotePurge vermerkt — aber nicht vollständig.',
    syncRetry: 'Erneut melden',
    syncRetrySucceeded: 'Rückmeldung erfolgreich.',
    summary: {
      counts: '{{done}} wiederhergestellt · {{failed}} fehlgeschlagen · {{cancelled}} abgebrochen',
    },
    targetLine: {
      channel: 'Ziel: {{ channel }} · Set {{ setName }}',
      owner: 'Ziel: Set {{ setName }} von {{ owner }}',
    },
    resync: {
      pending: 'Synchronisierung wird angestoßen…',
      succeeded: 'Synchronisierung angestoßen — die Liste aktualisiert sich gleich.',
      cooldown:
        'Sync-Cooldown aktiv — die Liste aktualisiert sich innerhalb einer Minute von selbst.',
      failed:
        'Synchronisierung konnte nicht angestoßen werden — der periodische Sync holt es innerhalb einer Minute nach.',
      backendTriggered: 'Abgleich läuft bereits — die Liste aktualisiert sich gleich.',
    },
  },
  syncReportReason: {
    forbidden: 'Grund: Dein Konto darf dieses Set laut 7TV nicht mehr bearbeiten.',
    setNotFound: 'Grund: Das Set gibt es bei 7TV nicht mehr.',
    unavailable: 'Grund: EmotePurge oder 7TV war gerade nicht erreichbar.',
    channelMismatchNotTracked:
      'Grund: Der erwartete Kanal ist bei EmotePurge gerade nicht getrackt.',
    channelMismatchActiveSetDiffers:
      'Grund: Der erwartete Kanal nutzt dieses Set laut EmotePurge gerade nicht als aktives Set.',
    shortfall: 'Grund: Nicht alle Emotes waren in EmotePurge vermerkt.',
    other: 'Grund: Unerwarteter Fehler.',
  },
};

function runInfo(overrides: Partial<RestoreRunInfo> = {}): RestoreRunInfo {
  return {
    runId: 'restore-1',
    phase: 'running',
    destructive: false,
    targetSetId: 'set-1',
    expectedChannelName: 'zielkanal',
    resyncChannelName: null,
    hostChannelName: 'hostkanal',
    setName: 'Set-1',
    ownerOrChannelLabel: 'zielkanal',
    result: null,
    syncReport: 'idle',
    syncReportReason: null,
    resyncTrigger: 'idle',
    ...overrides,
  };
}

/** The fake stands in for the whole service — every field the template reads is a signal this spec
 *  drives directly, exactly the shape `SevenTvRestoreService` presents (Regel 12: behaviour, not
 *  the service's own internals, which have their own tests, `seven-tv-restore.service.spec.ts`). */
interface FakeRestoreService {
  queue: WritableSignal<RunQueueItem[]>;
  isRunning: WritableSignal<boolean>;
  rateLimitPauseSeconds: WritableSignal<number | null>;
  run: WritableSignal<RestoreRunInfo | null>;
  syncReport: WritableSignal<SyncReportState>;
  syncReportReason: WritableSignal<SyncReportReason | null>;
  resyncTrigger: WritableSignal<ResyncTriggerState>;
  skippedDuplicates: WritableSignal<number>;
  skippedNameTaken: WritableSignal<number>;
  duplicateCheckAvailable: WritableSignal<boolean>;
  duplicateNoticePending: WritableSignal<boolean>;
  cancel: ReturnType<typeof vi.fn>;
  reset: ReturnType<typeof vi.fn>;
  retrySyncReport: ReturnType<typeof vi.fn>;
}

function createFakeRestoreService(): FakeRestoreService {
  return {
    queue: signal<RunQueueItem[]>([]),
    isRunning: signal(false),
    rateLimitPauseSeconds: signal<number | null>(null),
    run: signal<RestoreRunInfo | null>(null),
    syncReport: signal<SyncReportState>('idle'),
    syncReportReason: signal<SyncReportReason | null>(null),
    resyncTrigger: signal<ResyncTriggerState>('idle'),
    skippedDuplicates: signal(0),
    skippedNameTaken: signal(0),
    duplicateCheckAvailable: signal(true),
    duplicateNoticePending: signal(false),
    cancel: vi.fn(),
    reset: vi.fn(),
    retrySyncReport: vi.fn(),
  };
}

function doneItem(overrides: Partial<RunQueueItem> = {}): RunQueueItem {
  return {
    key: 'a',
    sevenTvEmoteId: '7tv-a',
    name: 'A',
    status: 'done',
    completedSteps: 1,
    failedStep: null,
    ...overrides,
  };
}

describe('RestoreProgressSection', () => {
  let restoreService: FakeRestoreService;

  beforeEach(async () => {
    restoreService = createFakeRestoreService();

    await TestBed.configureTestingModule({
      imports: [
        RestoreProgressSection,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [{ provide: SevenTvRestoreService, useValue: restoreService }],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(): ComponentFixture<RestoreProgressSection> {
    const fixture = TestBed.createComponent(RestoreProgressSection);
    fixture.detectChanges();
    return fixture;
  }

  it('renders nothing without a run or a pending notice', () => {
    const fixture = render();

    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  // Notizen: the three transient pre-run duplicate-check notices this section took over verbatim
  // from `MassDeletePanel` (Plan-253 §6, Nr. 3) — same gating (`duplicateNoticePending`), same
  // texts.
  describe('duplicate-check notices', () => {
    it('shows nothing while the check is available (the default)', () => {
      const fixture = render();

      expect(fixture.nativeElement.textContent).not.toContain('Wir konnten gerade nicht prüfen');
    });

    it('shows the skipped-duplicates count while its window is open', () => {
      restoreService.skippedDuplicates.set(2);
      restoreService.duplicateNoticePending.set(true);

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain(
        '2 Einträge waren bereits im Zielset und wurden übersprungen.',
      );
    });

    it('shows the skipped-name-taken count while its window is open, apart from the duplicates line', () => {
      restoreService.skippedNameTaken.set(1);
      restoreService.duplicateNoticePending.set(true);

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain(
        '1 Alias übersprungen — der Name gehört inzwischen einem anderen Emote.',
      );
    });

    it('shows the check-unavailable notice while its window is open', () => {
      restoreService.duplicateCheckAvailable.set(false);
      restoreService.duplicateNoticePending.set(true);

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain(
        'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
      );
    });

    it('hides the check-unavailable notice again once its pending window has elapsed', () => {
      restoreService.duplicateCheckAvailable.set(false);
      restoreService.duplicateNoticePending.set(false);

      const fixture = render();

      expect(fixture.nativeElement.textContent).not.toContain('Wir konnten gerade nicht prüfen');
    });

    it('shows every duplicate notice even with no run at all — a fully-refused restore', () => {
      restoreService.run.set(null);
      restoreService.skippedDuplicates.set(1);
      restoreService.duplicateNoticePending.set(true);

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain(
        '1 Eintrag war bereits im Zielset und wurde übersprungen.',
      );
    });
  });

  // Zielzeile: the dock's target line (spec 4.4 point 12), new to this section — the panel this was
  // extracted from never needed one, because it only ever showed a restore into its own bound set.
  describe('target line', () => {
    it('names the channel with its set, for a target tracked as the active set (expectedChannelName)', () => {
      restoreService.isRunning.set(true);
      restoreService.run.set(
        runInfo({ expectedChannelName: 'handofblood', resyncChannelName: null, setName: 'Main' }),
      );

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain('Ziel: handofblood · Set Main');
    });

    it('names the channel with its set, for a target tracked as a non-active set (resyncChannelName)', () => {
      restoreService.isRunning.set(true);
      restoreService.run.set(
        runInfo({
          expectedChannelName: null,
          resyncChannelName: 'handofblood',
          setName: 'Wegwerf-Set',
        }),
      );

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain('Ziel: handofblood · Set Wegwerf-Set');
    });

    it('names the owner instead of a channel for an untracked target', () => {
      restoreService.isRunning.set(true);
      restoreService.run.set(
        runInfo({
          expectedChannelName: null,
          resyncChannelName: null,
          ownerOrChannelLabel: 'Stranger',
          setName: 'Wegwerf-Set',
        }),
      );

      const fixture = render();

      const host: HTMLElement = fixture.nativeElement;
      expect(host.textContent).toContain('Ziel: Set Wegwerf-Set von Stranger');
      expect(host.textContent).not.toContain('handofblood');
    });
  });

  // Grundzeile: the settled sync report's failure reason (spec E23), the same `syncReportReason`
  // input `RunProgressPanel` already renders for the delete/import families — pinned here too, so
  // this section's own wiring of the input is covered, not only `RunProgressPanel`'s own rendering.
  it('shows the sync-report reason line under a failed report', () => {
    restoreService.isRunning.set(false);
    restoreService.queue.set([doneItem()]);
    restoreService.run.set(runInfo());
    restoreService.syncReport.set('failed');
    restoreService.syncReportReason.set('setNotFound');

    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain(
      'Grund: Das Set gibt es bei 7TV nicht mehr.',
    );
  });

  // "wird abgeglichen": the resync acknowledgement, including the backend-triggered state only
  // restore ever takes (spec 6.4/E12/F15) — the import family never reaches it.
  it.each([
    ['pending', 'Synchronisierung wird angestoßen…'],
    ['succeeded', 'Synchronisierung angestoßen — die Liste aktualisiert sich gleich.'],
    [
      'cooldown',
      'Sync-Cooldown aktiv — die Liste aktualisiert sich innerhalb einer Minute von selbst.',
    ],
    [
      'failed',
      'Synchronisierung konnte nicht angestoßen werden — der periodische Sync holt es innerhalb einer Minute nach.',
    ],
    ['backendTriggered', 'Abgleich läuft bereits — die Liste aktualisiert sich gleich.'],
  ] as const)(
    'maps the settled resyncTrigger %s to its own notice text',
    (trigger, expectedText) => {
      restoreService.isRunning.set(false);
      restoreService.queue.set([doneItem()]);
      restoreService.run.set(runInfo());
      restoreService.resyncTrigger.set(trigger);

      const fixture = render();

      const notice = fixture.nativeElement.querySelector('[aria-hidden="true"]');
      expect(notice?.textContent?.trim()).toBe(expectedText);
    },
  );

  it('shows no resync notice while resyncTrigger is idle', () => {
    restoreService.isRunning.set(false);
    restoreService.queue.set([doneItem()]);
    restoreService.run.set(runInfo());
    restoreService.resyncTrigger.set('idle');

    const fixture = render();

    expect(fixture.nativeElement.textContent).not.toContain('Abgleich');
    expect(fixture.nativeElement.textContent).not.toContain('Synchronisierung');
  });

  it('stays visible after the run has settled, as long as the queue is not empty', () => {
    restoreService.isRunning.set(false);
    restoreService.queue.set([doneItem()]);
    restoreService.run.set(runInfo());

    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain('Ziel: zielkanal · Set Set-1');
  });

  it('renders nothing once the queue is cleared, no run in flight and no notice pending', () => {
    restoreService.isRunning.set(false);
    restoreService.queue.set([]);
    restoreService.run.set(runInfo());
    restoreService.duplicateNoticePending.set(false);

    const fixture = render();

    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('renders nothing when the outer condition holds but no run object exists yet', () => {
    // isRunning() true would normally imply a run() — this pins down that the inner `@if
    // (restoreService.run(); as run)` guard is load-bearing on its own, not merely redundant.
    restoreService.isRunning.set(true);
    restoreService.run.set(null);

    const fixture = render();

    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  // #256, Plan-256 Festlegung 13: Close is offered exactly once the run's own record is `closed`,
  // not merely once the engine stops — a report still `reporting` must keep its dock (and its
  // eventual retry) reachable.
  describe('Schließen-Gate (#256)', () => {
    it('offers no Close button while the run is only reporting, not yet closed', () => {
      restoreService.isRunning.set(false);
      restoreService.queue.set([doneItem()]);
      restoreService.run.set(runInfo({ phase: 'reporting', syncReport: 'pending' }));

      const fixture = render();

      const buttons = [...fixture.nativeElement.querySelectorAll('button')].map(
        (button: HTMLElement) => button.textContent?.trim(),
      );
      expect(buttons).not.toContain('Schließen');
      expect(fixture.nativeElement.textContent).toContain('Wird abgeschlossen…');
    });

    it('offers Close once the run has closed', () => {
      restoreService.isRunning.set(false);
      restoreService.queue.set([doneItem()]);
      restoreService.run.set(runInfo({ phase: 'closed', syncReport: 'succeeded' }));

      const fixture = render();

      const buttons = [...fixture.nativeElement.querySelectorAll('button')].map(
        (button: HTMLElement) => button.textContent?.trim(),
      );
      expect(buttons).toContain('Schließen');
    });
  });
});
