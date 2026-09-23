import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SyncReportState } from '../../core/seven-tv/seven-tv-delete.service';
import { ImportRunInfo, SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { ResyncTriggerState } from '../../core/seven-tv/seven-tv-restore.service';
import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
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
    deleteFailedFallback: 'Kopieren fehlgeschlagen',
    rateLimitPaused: '7TV-Rate-Limit erreicht.',
    syncFailedTitle: 'Rückmeldung fehlgeschlagen',
    syncFailed: 'Rückmeldung an EmotePurge fehlgeschlagen.',
    syncRetry: 'Erneut melden',
    syncRetrySucceeded: 'Rückmeldung erfolgreich.',
    summary: {
      counts: '{{done}} kopiert · {{failed}} fehlgeschlagen · {{cancelled}} abgebrochen',
      target: 'Ziel: {{ channel }}',
      targetWithSet: 'Ziel: {{ channel }} · Set {{ setName }}',
      targetSet: 'Ziel: Set {{ setName }} von {{ owner }}',
      openTarget: 'Zielkanal öffnen',
      copiedNotActive:
        "In Set ‚{{ setName }}' kopiert — es ist nicht das aktive Set von {{ channel }}, die Kanalseite zeigt es deshalb nicht.",
      insufficientPrivileges: 'Das 7TV-Token hat im Zielset kein Schreibrecht.',
    },
    resync: {
      pending: 'Abgleich des Zielkanals wird angestoßen…',
      succeeded: 'Abgleich angestoßen — der Zielkanal zeigt die Emotes gleich.',
      cooldown: 'Der Zielkanal übernimmt die Emotes beim nächsten Abgleich.',
      failed: 'Abgleich konnte nicht angestoßen werden.',
    },
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
    result: null,
    ...overrides,
  };
}

/** The fake stands in for the whole service — every field the template reads is a signal this
 *  spec drives directly, exactly the shape `SevenTvImportService` presents (Regel 12: behaviour,
 *  not the service's own internals, which have their own tests). */
interface FakeImportService {
  queue: WritableSignal<RunQueueItem[]>;
  isRunning: WritableSignal<boolean>;
  rateLimitPauseSeconds: WritableSignal<number | null>;
  run: WritableSignal<ImportRunInfo | null>;
  syncReport: WritableSignal<SyncReportState>;
  resyncTrigger: WritableSignal<ResyncTriggerState>;
  abortedForPrivileges: WritableSignal<boolean>;
  skippedDuplicates: WritableSignal<number>;
  duplicateCheckAvailable: WritableSignal<boolean>;
  duplicateNoticePending: WritableSignal<boolean>;
  cancel: ReturnType<typeof vi.fn>;
  reset: ReturnType<typeof vi.fn>;
  retrySyncReport: ReturnType<typeof vi.fn>;
}

function createFakeImportService(): FakeImportService {
  return {
    queue: signal<RunQueueItem[]>([]),
    isRunning: signal(false),
    rateLimitPauseSeconds: signal<number | null>(null),
    run: signal<ImportRunInfo | null>(null),
    syncReport: signal<SyncReportState>('idle'),
    resyncTrigger: signal<ResyncTriggerState>('idle'),
    abortedForPrivileges: signal(false),
    skippedDuplicates: signal(0),
    duplicateCheckAvailable: signal(true),
    duplicateNoticePending: signal(false),
    cancel: vi.fn(),
    reset: vi.fn(),
    retrySyncReport: vi.fn(),
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
      providers: [provideRouter([]), { provide: SevenTvImportService, useValue: importService }],
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
});
