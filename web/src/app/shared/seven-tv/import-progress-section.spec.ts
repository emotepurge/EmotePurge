import { Dialog } from '@angular/cdk/dialog';
import { Signal, WritableSignal, computed, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ImportRunInfo,
  ImportRunItem,
  SevenTvImportService,
} from '../../core/seven-tv/seven-tv-import.service';
import { ResyncTriggerState } from '../../core/seven-tv/seven-tv-restore.service';
import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { SyncReportReason, SyncReportState } from '../../core/seven-tv/sync-report-outcome';
import { TransferRow } from '../../core/seven-tv/transfer-plan';
import { ImportProgressSection } from './import-progress-section';

// Only the keys this component and the `RunProgressPanel` it wraps actually translate — not the
// full app translation file.
const DE_TRANSLATIONS = {
  common: { cancel: 'Abbrechen', close: 'Schließen' },
  import: {
    duplicateCheckUnavailable:
      'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
    skippedDuplicates: {
      one: '{{ count }} Emote war beim Start bereits im Zielset und wurde übersprungen.',
      other: '{{ count }} Emotes waren beim Start bereits im Zielset und wurden übersprungen.',
    },
    progress: '{{ finished }} / {{ total }} kopiert',
    settling: 'Wird abgeschlossen…',
    deleteFailedFallback: 'Kopieren fehlgeschlagen',
    rateLimitPaused: '7TV-Rate-Limit erreicht.',
    syncFailedTitle: 'Rückmeldung fehlgeschlagen',
    syncFailed: 'Rückmeldung an EmotePurge fehlgeschlagen.',
    syncRetry: 'Erneut melden',
    syncRetrySucceeded: 'Rückmeldung erfolgreich.',
    removalSyncFailedTitle: 'Entfernungs-Rückmeldung fehlgeschlagen',
    removalSyncFailed: 'Entfernungs-Rückmeldung an EmotePurge fehlgeschlagen.',
    removalSyncRetry: 'Entfernung erneut melden',
    removalSyncSucceeded: 'Entfernungs-Rückmeldung erfolgreich.',
    summary: {
      counts: '{{done}} kopiert · {{failed}} fehlgeschlagen · {{cancelled}} abgebrochen',
      target: 'Ziel: {{ channel }}',
      targetWithSet: 'Ziel: {{ channel }} · Set {{ setName }}',
      targetSet: 'Ziel: Set {{ setName }} von {{ owner }}',
      openTarget: 'Zielkanal öffnen',
      copiedNotActive:
        "In Set ‚{{ setName }}' kopiert — es ist nicht das aktive Set von {{ channel }}, die Kanalseite zeigt es deshalb nicht.",
      insufficientPrivileges: 'Das 7TV-Token hat im Zielset kein Schreibrecht.',
      downloadProtocol: 'Protokoll herunterladen',
      protocolNotSaved: 'Protokoll noch nicht gespeichert.',
      removed: {
        one: '{{ count }} Emote aus dem Zielset entfernt.',
        other: '{{ count }} Emotes aus dem Zielset entfernt.',
      },
      replaceSkippedDrift: {
        one: '{{ count }} Ersetzung übersprungen — Ziel hat sich verändert.',
        other: '{{ count }} Ersetzungen übersprungen — Ziel hat sich verändert.',
      },
      unknownRows: {
        one: 'Bei {{ count }} Zeile unklar, ob übernommen.',
        other: 'Bei {{ count }} Zeilen unklar, ob übernommen.',
      },
    },
    resync: {
      pending: 'Abgleich des Zielkanals wird angestoßen…',
      succeeded: 'Abgleich angestoßen — der Zielkanal zeigt die Emotes gleich.',
      cooldown: 'Der Zielkanal übernimmt die Emotes beim nächsten Abgleich.',
      failed: 'Abgleich konnte nicht angestoßen werden.',
    },
  },
  syncReportReason: {
    setNotFound: 'Grund: Das Set gibt es bei 7TV nicht mehr.',
  },
};

function runInfo(overrides: Partial<ImportRunInfo> = {}): ImportRunInfo {
  return {
    targetChannelName: 'zielkanal',
    targetOwnerDisplayName: null,
    targetSetId: 'set-1',
    targetSetName: 'Set-1',
    // Active by default so the existing "Ziel: zielkanal" behaviour keeps working unchanged —
    // findings 2/3 tests below override this explicitly.
    targetIsActiveSet: true,
    origin: { kind: 'channel', channelName: 'quellkanal' },
    plan: { rows: [] },
    settlement: 'pending',
    removedCount: 0,
    unknownCount: 0,
    result: null,
    ...overrides,
  };
}

/** The fake stands in for the whole service — every field the template reads is a signal this
 *  spec drives directly, exactly the shape `SevenTvImportService` presents (Regel 12: behaviour,
 *  not the service's own internals, which have their own tests). `items` is a plain mirror of
 *  `queue` (not the real service's isRunning-gated computed — that distinction is
 *  `seven-tv-import.service.spec.ts`'s job, e.g. its R15-guard tests) so every existing `queue.set(…)`
 *  call in this file keeps driving `app-run-progress-panel`'s `[items]` binding unchanged, the
 *  `queue()` → `items()` rebind included. */
interface FakeImportService {
  queue: WritableSignal<RunQueueItem[]>;
  items: Signal<RunQueueItem[]>;
  isRunning: WritableSignal<boolean>;
  rateLimitPauseSeconds: WritableSignal<number | null>;
  run: WritableSignal<ImportRunInfo | null>;
  syncReport: WritableSignal<SyncReportState>;
  removalReport: WritableSignal<SyncReportState>;
  removalReportReason: WritableSignal<SyncReportReason | null>;
  resyncTrigger: WritableSignal<ResyncTriggerState>;
  abortedForPrivileges: WritableSignal<boolean>;
  skippedDuplicates: WritableSignal<number>;
  replaceSkippedDrift: WritableSignal<number>;
  duplicateCheckAvailable: WritableSignal<boolean>;
  duplicateNoticePending: WritableSignal<boolean>;
  protocolSaved: WritableSignal<boolean>;
  cancel: ReturnType<typeof vi.fn>;
  reset: ReturnType<typeof vi.fn>;
  retrySyncReport: ReturnType<typeof vi.fn>;
  retryRemovalReport: ReturnType<typeof vi.fn>;
}

function createFakeImportService(): FakeImportService {
  const queue = signal<RunQueueItem[]>([]);
  return {
    queue,
    items: computed(() => queue()),
    isRunning: signal(false),
    rateLimitPauseSeconds: signal<number | null>(null),
    run: signal<ImportRunInfo | null>(null),
    syncReport: signal<SyncReportState>('idle'),
    removalReport: signal<SyncReportState>('idle'),
    removalReportReason: signal<SyncReportReason | null>(null),
    resyncTrigger: signal<ResyncTriggerState>('idle'),
    abortedForPrivileges: signal(false),
    skippedDuplicates: signal(0),
    replaceSkippedDrift: signal(0),
    duplicateCheckAvailable: signal(true),
    duplicateNoticePending: signal(false),
    protocolSaved: signal(false),
    cancel: vi.fn(),
    reset: vi.fn(),
    retrySyncReport: vi.fn(),
    retryRemovalReport: vi.fn(),
  };
}

const SOURCE_A_TRANSFER: TransferRow = {
  action: 'add',
  source: { sevenTvEmoteId: '7tv-a', name: 'A', imageUrl: null },
  alias: 'A',
};

/** A settled run item — a `RunQueueItem` is all `app-run-progress-panel` needs, but `run.result`
 *  is typed `ImportRunItem[]`, so every fixture carries a `transfer` row too. */
function doneItem(overrides: Partial<ImportRunItem> = {}): ImportRunItem {
  return {
    key: 'a',
    sevenTvEmoteId: '7tv-a',
    name: 'A',
    status: 'done',
    completedSteps: 1,
    failedStep: null,
    transfer: SOURCE_A_TRANSFER,
    ...overrides,
  };
}

/** What the component's status regions would announce: their text minus aria-hidden descendants. */
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

describe('ImportProgressSection', () => {
  let importService: FakeImportService;

  beforeEach(async () => {
    importService = createFakeImportService();

    await TestBed.configureTestingModule({
      imports: [
        ImportProgressSection,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideRouter([]),
        { provide: SevenTvImportService, useValue: importService },
        { provide: Dialog, useValue: { open: vi.fn() } as unknown as Dialog },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(): ComponentFixture<ImportProgressSection> {
    const fixture = TestBed.createComponent(ImportProgressSection);
    fixture.detectChanges();
    return fixture;
  }

  it('renders nothing while idle — no run in flight and nothing left to show', () => {
    const fixture = render();

    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('appears while a run is in flight, even before the queue has any items yet', () => {
    importService.isRunning.set(true);
    importService.run.set(runInfo());

    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain('Ziel: zielkanal');
  });

  // T2.6/spec 8.6: an untracked target's run has no channel of ours to name — the summary line and
  // the "open target channel" link both need a channel-free branch instead of reading targetChannelName
  // and crashing into "/channels/null/usage-stats".
  describe('untracked target (targetChannelName: null, T2.6)', () => {
    it('names the set and its owner instead of a channel, and offers no "open target channel" link', () => {
      importService.isRunning.set(true);
      importService.run.set(
        runInfo({
          targetChannelName: null,
          targetOwnerDisplayName: 'Stranger',
          targetSetId: 'set-untracked',
          // Named by its resolved name, not the raw id (finding 2, Live-Verifikation K2 2026-09-21).
          targetSetName: 'Wegwerf-Set',
        }),
      );

      const fixture = render();

      const host: HTMLElement = fixture.nativeElement;
      expect(host.textContent).toContain('Ziel: Set Wegwerf-Set von Stranger');
      expect(host.textContent).not.toContain('zielkanal');
      expect(
        Array.from(host.querySelectorAll<HTMLAnchorElement>('a')).some(
          (a) => a.textContent?.trim() === 'Zielkanal öffnen',
        ),
      ).toBe(false);
    });
  });

  // Finding 2/3 (Live-Verifikation K2 2026-09-21): a tracked target whose set is *not* the channel's
  // active one — the run writes into it, but the channel page (and its resync) never shows it.
  describe('tracked non-active target (targetIsActiveSet: false)', () => {
    it("names the channel and the set together, mirroring the confirm dialog's own line", () => {
      importService.isRunning.set(true);
      importService.run.set(
        runInfo({
          targetChannelName: 'zielkanal',
          targetSetName: 'wegwerf',
          targetIsActiveSet: false,
        }),
      );

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain('Ziel: zielkanal · Set wegwerf');
    });

    it('offers no "open target channel" link once the run has settled', () => {
      importService.isRunning.set(false);
      importService.queue.set([
        {
          key: 'a',
          sevenTvEmoteId: '7tv-a',
          name: 'A',
          status: 'done',
          completedSteps: 1,
          failedStep: null,
        },
      ]);
      importService.run.set(
        runInfo({
          targetChannelName: 'zielkanal',
          targetSetName: 'wegwerf',
          targetIsActiveSet: false,
          result: { doneKeys: ['7tv-a'], items: [], startedAt: 0, finishedAt: 1 },
        }),
      );

      const fixture = render();
      const host: HTMLElement = fixture.nativeElement;

      expect(
        Array.from(host.querySelectorAll<HTMLAnchorElement>('a')).some(
          (a) => a.textContent?.trim() === 'Zielkanal öffnen',
        ),
      ).toBe(false);
    });

    it('shows the copied-not-active notice instead of a resync notice once the run has settled', () => {
      importService.isRunning.set(false);
      importService.queue.set([
        {
          key: 'a',
          sevenTvEmoteId: '7tv-a',
          name: 'A',
          status: 'done',
          completedSteps: 1,
          failedStep: null,
        },
      ]);
      importService.run.set(
        runInfo({
          targetChannelName: 'zielkanal',
          targetSetName: 'wegwerf',
          targetIsActiveSet: false,
          result: { doneKeys: ['7tv-a'], items: [], startedAt: 0, finishedAt: 1 },
        }),
      );
      // The service never sets resyncTrigger away from 'idle' for a non-active target
      // (SevenTvImportService.onRunComplete) — pinned here too, not just assumed.
      importService.resyncTrigger.set('idle');

      const fixture = render();

      const notice = fixture.nativeElement.querySelector('[aria-hidden="true"]');
      expect(notice?.textContent.trim()).toBe(
        "In Set ‚wegwerf' kopiert — es ist nicht das aktive Set von zielkanal, die Kanalseite zeigt es deshalb nicht.",
      );
      expect(fixture.nativeElement.textContent).not.toContain('Abgleich');
    });

    it('shows nothing yet while the run is still in flight (no settled result)', () => {
      importService.isRunning.set(true);
      importService.run.set(
        runInfo({
          targetChannelName: 'zielkanal',
          targetSetName: 'wegwerf',
          targetIsActiveSet: false,
        }),
      );

      const fixture = render();

      expect(fixture.nativeElement.textContent).not.toContain('kopiert —');
    });
  });

  it('stays visible after the run has settled, as long as the queue is not empty', () => {
    importService.isRunning.set(false);
    importService.queue.set([
      {
        key: 'a',
        sevenTvEmoteId: '7tv-a',
        name: 'A',
        status: 'done',
        completedSteps: 1,
        failedStep: null,
      },
    ]);
    importService.run.set(runInfo());

    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain('Ziel: zielkanal');
  });

  it('renders nothing once the queue is cleared and no run is in flight', () => {
    importService.isRunning.set(false);
    importService.queue.set([]);
    importService.run.set(runInfo());

    const fixture = render();

    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('renders nothing when the outer condition holds but no run object exists yet', () => {
    // isRunning() true would normally imply a run() — this pins down that the inner `@if
    // (importService.run(); as run)` guard is load-bearing on its own, not merely redundant.
    importService.isRunning.set(true);
    importService.run.set(null);

    const fixture = render();

    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  // The resync notice lives in `RunProgressPanel`'s `[run-actions]` slot, which that panel only
  // projects once the run has settled (`!isRunning() && total() > 0`, see run-progress-panel.ts) —
  // so every case below is post-run, not mid-run, matching how the real service actually sets
  // `resyncTrigger` (only from `onRunComplete`, after `isRunning` has already gone back to false).
  // #134: this section lives in the usage-stats dock, which can mount in the same pass that sets a
  // notice — so it owns no status region at all. Its notices are visible but aria-hidden; the page's
  // permanently mounted DockOutcomeAnnouncer speaks them (docs/UI-Designsprache.md §4.5).
  it('shows no resync notice once settled while resyncTrigger is idle', () => {
    importService.isRunning.set(false);
    importService.queue.set([
      {
        key: 'a',
        sevenTvEmoteId: '7tv-a',
        name: 'A',
        status: 'done',
        completedSteps: 1,
        failedStep: null,
      },
    ]);
    importService.run.set(runInfo());
    importService.resyncTrigger.set('idle');

    const fixture = render();

    expect(fixture.nativeElement.textContent).not.toContain('Abgleich');
  });

  it.each([
    ['pending', 'Abgleich des Zielkanals wird angestoßen…'],
    ['succeeded', 'Abgleich angestoßen — der Zielkanal zeigt die Emotes gleich.'],
    ['cooldown', 'Der Zielkanal übernimmt die Emotes beim nächsten Abgleich.'],
    ['failed', 'Abgleich konnte nicht angestoßen werden.'],
  ] as const)(
    'maps the settled resyncTrigger %s to its own notice text, shown but not announced here',
    (trigger, expectedText) => {
      importService.isRunning.set(false);
      importService.queue.set([
        {
          key: 'a',
          sevenTvEmoteId: '7tv-a',
          name: 'A',
          status: 'done',
          completedSteps: 1,
          failedStep: null,
        },
      ]);
      importService.run.set(runInfo());
      importService.resyncTrigger.set(trigger);

      const fixture = render();

      const notice = fixture.nativeElement.querySelector('[aria-hidden="true"]');
      expect(notice?.textContent.trim()).toBe(expectedText);
      // RunProgressPanel is itself role="status" and this notice is projected into it — hidden
      // there, so that region does not speak it either.
      expect(announcedByStatusRegions(fixture.nativeElement)).not.toContain(expectedText);
    },
  );

  it('shows the insufficient-privileges banner only when the run aborted for that reason', () => {
    importService.isRunning.set(false);
    importService.queue.set([
      {
        key: 'a',
        sevenTvEmoteId: '7tv-a',
        name: 'A',
        status: 'failed',
        completedSteps: 0,
        failedStep: 0,
      },
    ]);
    importService.run.set(runInfo());
    importService.abortedForPrivileges.set(false);

    const withoutBanner = render();
    expect(withoutBanner.nativeElement.textContent).not.toContain(
      'Das 7TV-Token hat im Zielset kein Schreibrecht.',
    );

    importService.abortedForPrivileges.set(true);
    const withBanner = render();
    expect(withBanner.nativeElement.textContent).toContain(
      'Das 7TV-Token hat im Zielset kein Schreibrecht.',
    );
  });

  // #149: the fresh pre-send duplicate check's own fetch can fail — this notice is what tells the
  // user a duplicate may have slipped in undetected, independent of the run-progress panel (shown
  // even while idle, same reasoning as skippedDuplicatesKey above it). Since the #149 P2 fix these
  // are gated on `duplicateNoticePending` too (design doc §4.5's transient-notice convention), so
  // every case below sets it alongside `duplicateCheckAvailable`, matching how the real service
  // always sets both together (`startImport` calls `showDuplicateNotice` right after setting
  // `duplicateCheckAvailable`).
  it('shows nothing while the duplicate check is available (the default)', () => {
    importService.duplicateCheckAvailable.set(true);

    const fixture = render();

    expect(fixture.nativeElement.textContent).not.toContain('Wir konnten gerade nicht prüfen');
  });

  it('shows the quiet notice once the duplicate check is reported unavailable, stating the consequence', () => {
    importService.duplicateCheckAvailable.set(false);
    importService.duplicateNoticePending.set(true);

    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain(
      'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
    );
  });

  // #149 P2 (independent review): a P1 fix moved the duplicate check onto 7TV directly, and an
  // independent P2 finding on the same branch was that this notice, and the skipped-count one next
  // to it, were unreachable for the exact case they exist to report — a fully-refused (all-
  // duplicates) run leaves `run()` null and `queue()` empty, so nothing here ever mounted at the
  // page level (`usage-stats-page.ts`'s `dockVisible`, fixed via `action-dock.ts`). Pinned here at
  // the component's own level: the notice must render on its own merits, without any run object at
  // all — `run()` stays null throughout both cases below.
  it('shows the skipped-count notice even with no run at all — a fully refused import', () => {
    importService.run.set(null);
    importService.skippedDuplicates.set(3);
    importService.duplicateNoticePending.set(true);

    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain(
      '3 Emotes waren beim Start bereits im Zielset und wurden übersprungen.',
    );
  });

  it('shows the check-unavailable notice even with no run at all — a fully refused import', () => {
    importService.run.set(null);
    importService.duplicateCheckAvailable.set(false);
    importService.duplicateNoticePending.set(true);

    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain(
      'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
    );
  });

  // #134: same as the resync notice above — the duplicate notices are shown, not announced, here.
  it('shows both duplicate notices aria-hidden, with no status region of its own', () => {
    importService.run.set(null);
    importService.skippedDuplicates.set(3);
    importService.duplicateCheckAvailable.set(false);
    importService.duplicateNoticePending.set(true);

    const fixture = render();

    const notices: HTMLElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('[aria-hidden="true"]'),
    );
    expect(notices.map((notice) => notice.textContent?.trim())).toEqual([
      '3 Emotes waren beim Start bereits im Zielset und wurden übersprungen.',
      'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
    ]);
    expect(announcedByStatusRegions(fixture.nativeElement)).toBe('');
  });

  // The other half of the P2 fix: the notice is transient (design doc §4.5), not a persistent flag
  // that would otherwise be able to sit next to a *later*, unrelated run's stale details forever.
  it('hides the check-unavailable notice once its pending window has elapsed, even while duplicateCheckAvailable still reads false', () => {
    importService.duplicateCheckAvailable.set(false);
    importService.duplicateNoticePending.set(false);

    const fixture = render();

    expect(fixture.nativeElement.textContent).not.toContain('Wir konnten gerade nicht prüfen');
  });

  // The transfer-run protocol's dock offering, the removed/unknown-row summary lines, the drift
  // notice (including the all-drift case) and the removal report's own retry.
  describe('transfer-run protocol and removal report', () => {
    function findButton(fixture: ComponentFixture<ImportProgressSection>, text: string) {
      return Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('button'),
      ).find((button) => button.textContent?.trim() === text);
    }

    it('shows the download-protocol button once the run has settled', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem()]);
      importService.run.set(runInfo({ settlement: 'settled' }));

      const fixture = render();

      expect(findButton(fixture, 'Protokoll herunterladen')).toBeDefined();
    });

    it('shows no download-protocol button while the run is still pending settlement', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem({ status: 'unknown' })]);
      importService.run.set(runInfo({ settlement: 'pending' }));

      const fixture = render();

      expect(findButton(fixture, 'Protokoll herunterladen')).toBeUndefined();
      // Finding 2: Close shares the same settlement gate — a run that has stopped running but not
      // yet settled must not be closable, since Close (reset()) would drop the run's unload cover
      // and its protocol before either one exists.
      expect(findButton(fixture, 'Schließen')).toBeUndefined();
    });

    it('shows the protocolNotSaved hint until the protocol has been saved, once settled', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem()]);
      importService.run.set(runInfo({ settlement: 'settled' }));
      importService.protocolSaved.set(false);

      const notSaved = render();
      expect(notSaved.nativeElement.textContent).toContain('Protokoll noch nicht gespeichert.');

      importService.protocolSaved.set(true);
      const saved = render();
      expect(saved.nativeElement.textContent).not.toContain('Protokoll noch nicht gespeichert.');
    });

    it('closes without asking, calling reset() directly', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem()]);
      importService.run.set(runInfo({ settlement: 'settled' }));

      const fixture = render();
      findButton(fixture, 'Schließen')?.click();

      expect(importService.reset).toHaveBeenCalledTimes(1);
    });

    it('shows the removed-count row when the settled run confirmed a REMOVE', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem()]);
      importService.run.set(runInfo({ settlement: 'settled', removedCount: 2 }));

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain('2 Emotes aus dem Zielset entfernt.');
    });

    it('shows no removed-count row when nothing was confirmed removed', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem()]);
      importService.run.set(runInfo({ settlement: 'settled', removedCount: 0 }));

      const fixture = render();

      expect(fixture.nativeElement.textContent).not.toContain('entfernt.');
    });

    it('shows the unknown-rows row when the settled run has rows 7TV never clarified', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem({ status: 'unknown' })]);
      importService.run.set(runInfo({ settlement: 'settled', unknownCount: 1 }));

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain('Bei 1 Zeile unklar, ob übernommen.');
    });

    it('shows the drift notice for a run that still queued something', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem()]);
      importService.run.set(runInfo({ settlement: 'settled' }));
      importService.replaceSkippedDrift.set(1);
      importService.duplicateNoticePending.set(true);

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain(
        '1 Ersetzung übersprungen — Ziel hat sich verändert.',
      );
    });

    // The case the drift row exists for: every replace row drifted, nothing ran, no run object and
    // an empty queue — the same "no run at all" shape #149 P2's skipped-duplicates notice already
    // proves reachable.
    it('shows the drift notice even when every replace row drifted and nothing ran at all', () => {
      importService.run.set(null);
      importService.queue.set([]);
      importService.replaceSkippedDrift.set(3);
      importService.duplicateNoticePending.set(true);

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain(
        '3 Ersetzungen übersprungen — Ziel hat sich verändert.',
      );
    });

    it('shows no drift notice once its pending window has elapsed, even while the count is still set', () => {
      importService.replaceSkippedDrift.set(2);
      importService.duplicateNoticePending.set(false);

      const fixture = render();

      expect(fixture.nativeElement.textContent).not.toContain('übersprungen');
    });

    it('shows a retry banner on a failed removal report and calls retryRemovalReport on click', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem()]);
      importService.run.set(runInfo({ settlement: 'settled' }));
      importService.removalReport.set('failed');

      const fixture = render();
      expect(fixture.nativeElement.textContent).toContain('Entfernungs-Rückmeldung fehlgeschlagen');

      findButton(fixture, 'Entfernung erneut melden')?.click();

      expect(importService.retryRemovalReport).toHaveBeenCalledTimes(1);
    });

    // Spec E23: the removal banner carries the reason as its own line.
    it('shows the reason line under a failed removal report, and none without a reason', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem()]);
      importService.run.set(runInfo({ settlement: 'settled' }));
      importService.removalReport.set('failed');
      importService.removalReportReason.set('setNotFound');

      const withReason = render();
      expect(withReason.nativeElement.textContent).toContain(
        'Grund: Das Set gibt es bei 7TV nicht mehr.',
      );

      importService.removalReportReason.set(null);
      const withoutReason = render();
      expect(withoutReason.nativeElement.textContent).toContain(
        'Entfernungs-Rückmeldung fehlgeschlagen',
      );
      expect(withoutReason.nativeElement.textContent).not.toContain('Grund:');
    });

    it('shows the same retry banner for a partial removal report', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem()]);
      importService.run.set(runInfo({ settlement: 'settled' }));
      importService.removalReport.set('partial');

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain('Entfernungs-Rückmeldung fehlgeschlagen');
    });

    it('shows the succeeded note for a settled, successful removal report, not the retry banner', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem()]);
      importService.run.set(runInfo({ settlement: 'settled' }));
      importService.removalReport.set('succeeded');

      const fixture = render();

      expect(fixture.nativeElement.textContent).toContain('Entfernungs-Rückmeldung erfolgreich.');
      expect(fixture.nativeElement.textContent).not.toContain(
        'Entfernungs-Rückmeldung fehlgeschlagen',
      );
    });

    it('shows no removal-report notice at all while its state is idle', () => {
      importService.isRunning.set(false);
      importService.queue.set([doneItem()]);
      importService.run.set(runInfo({ settlement: 'settled' }));
      importService.removalReport.set('idle');

      const fixture = render();

      expect(fixture.nativeElement.textContent).not.toContain('Entfernungs-Rückmeldung');
    });
  });
});
