import { Dialog } from '@angular/cdk/dialog';
import { Signal, WritableSignal, computed, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom, of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ResyncTriggerState } from '../../core/seven-tv/seven-tv-restore.service';
import {
  SevenTvUndoService,
  UndoRunInfo,
  UndoRunItem,
  UndoRunSummary,
  summarizeUndoRun,
} from '../../core/seven-tv/seven-tv-undo.service';
import { SyncReportReason, SyncReportState } from '../../core/seven-tv/sync-report-outcome';
import { UndoCandidate } from '../../core/seven-tv/undo-candidate';
import { UndoSkipReason, UndoSkippedRow } from '../../core/seven-tv/undo-plan';
import { UndoProgressSection } from './undo-progress-section';
import de from '../../../../public/i18n/de.json';
import en from '../../../../public/i18n/en.json';

/*
 * What the section decides (rule 12: behaviour, not template): which counters and lines it shows
 * for a run, that a skipped row is named once and never as a cancellation, which report offers a
 * retry, when the protocol and Close are offered, and what the transient notice does. The service's
 * counting itself is `seven-tv-undo.service.spec.ts`'s — here the fake's `summary` is the real
 * `summarizeUndoRun` over the fixture rows, so the section is tested against the real numbers.
 * Real German texts (the app's own de.json), so an assertion reads as the sentence the user gets.
 */

/** The fake stands in for the service: `run` drives every per-run signal, exactly as the real
 *  service's `linkedSignal`s follow its `run()`. */
interface FakeUndoService {
  run: WritableSignal<UndoRunInfo | null>;
  items: WritableSignal<UndoRunItem[]>;
  progress: WritableSignal<{ finished: number; total: number }>;
  summary: Signal<UndoRunSummary>;
  isRunning: WritableSignal<boolean>;
  rateLimitPauseSeconds: WritableSignal<number | null>;
  settlement: Signal<UndoRunInfo['settlement'] | null>;
  removalReport: Signal<SyncReportState>;
  removalReportReason: Signal<SyncReportReason | null>;
  restoreReport: Signal<SyncReportState>;
  restoreReportReason: Signal<SyncReportReason | null>;
  resyncTrigger: Signal<ResyncTriggerState>;
  abortedForPrivileges: Signal<boolean>;
  protocolSaved: Signal<boolean>;
  noticePending: WritableSignal<boolean>;
  noticeSkipped: WritableSignal<readonly UndoSkippedRow[]>;
  markProtocolSaved: ReturnType<typeof vi.fn>;
  cancel: ReturnType<typeof vi.fn>;
  reset: ReturnType<typeof vi.fn>;
  retryRemovalReport: ReturnType<typeof vi.fn>;
  retryRestoreReport: ReturnType<typeof vi.fn>;
}

function createFakeUndoService(): FakeUndoService {
  const run = signal<UndoRunInfo | null>(null);
  const items = signal<UndoRunItem[]>([]);
  return {
    run,
    items,
    progress: signal({ finished: 0, total: 0 }),
    summary: computed(() => summarizeUndoRun(items(), run()?.skipped ?? [])),
    isRunning: signal(false),
    rateLimitPauseSeconds: signal<number | null>(null),
    settlement: computed(() => run()?.settlement ?? null),
    removalReport: computed(() => run()?.removalReport ?? 'idle'),
    removalReportReason: computed(() => run()?.removalReportReason ?? null),
    restoreReport: computed(() => run()?.restoreReport ?? 'idle'),
    restoreReportReason: computed(() => run()?.restoreReportReason ?? null),
    resyncTrigger: computed(() => run()?.resyncTrigger ?? 'idle'),
    abortedForPrivileges: computed(() => run()?.abortedForPrivileges ?? false),
    protocolSaved: computed(() => run()?.protocolSaved ?? false),
    noticePending: signal(false),
    noticeSkipped: signal<readonly UndoSkippedRow[]>([]),
    markProtocolSaved: vi.fn(),
    cancel: vi.fn(),
    reset: vi.fn(),
    retryRemovalReport: vi.fn(),
    retryRestoreReport: vi.fn(),
  };
}

function cand(n: string): UndoCandidate {
  return {
    sourceSevenTvEmoteId: `src-${n}`,
    sourceName: `Source${n}`,
    alias: `A${n}`,
    fileStatus: 'done',
    target: { sevenTvEmoteId: `tgt-${n}`, entries: [{ alias: `A${n}` }], defaultName: null },
    provenance: 'confirmed',
  };
}

/** A settled row: `full` by default, one ADD, all steps confirmed. */
function item(n: string, overrides: Partial<UndoRunItem> = {}): UndoRunItem {
  const candidate = cand(n);
  return {
    key: candidate.sourceSevenTvEmoteId,
    sevenTvEmoteId: candidate.sourceSevenTvEmoteId,
    name: candidate.alias,
    status: 'done',
    completedSteps: 2,
    failedStep: null,
    candidate,
    mode: 'full',
    adds: [{ alias: candidate.alias }],
    provenance: 'confirmed',
    omittedEntries: [],
    notes: [],
    undoStatus: overrides.status ?? 'done',
    skippedReason: null,
    sourceEntriesAtRemove: [{ alias: candidate.alias }],
    ...overrides,
  };
}

function skippedRow(n: string, reason: UndoSkipReason): UndoSkippedRow {
  return {
    candidate: cand(n),
    reason,
    live: { sourceEntries: [], targetEntries: [] },
    omittedEntries: [],
  };
}

/** A settled, closed run into the tracked channel's active set, nothing to report by default. */
function runInfo(overrides: Partial<UndoRunInfo> = {}): UndoRunInfo {
  return {
    runId: 'undo-1',
    phase: 'closed',
    destructive: true,
    targetSetId: 'set-t',
    expectedChannelName: 'kanal_t',
    hostChannelName: 'kanal_t',
    setName: 'tttt',
    ownerOrChannelLabel: 'kanal_t',
    trackedChannelName: 'kanal_t',
    ownerDisplayName: 'Olaf',
    sourceFile: {
      stage: 'finished',
      exportedAt: '2026-09-25T10:00:00.000Z',
      verifiedAt: null,
      finishedAt: '2026-09-25T10:00:00.000Z',
      origin: null,
    },
    acknowledgedUnproven: false,
    rows: [],
    skipped: [],
    recheck: {},
    settlement: 'settled',
    result: { doneKeys: [], items: [], startedAt: 0, finishedAt: 1 },
    removalReport: 'idle',
    removalReportReason: null,
    restoreReport: 'idle',
    restoreReportReason: null,
    resyncTrigger: 'idle',
    abortedForPrivileges: false,
    protocolSaved: false,
    ...overrides,
  };
}

describe('UndoProgressSection', () => {
  let undo: FakeUndoService;
  let dialogOpen: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    undo = createFakeUndoService();
    dialogOpen = vi.fn();

    await TestBed.configureTestingModule({
      imports: [
        UndoProgressSection,
        TranslocoTestingModule.forRoot({
          langs: { de },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: SevenTvUndoService, useValue: undo },
        { provide: Dialog, useValue: { open: dialogOpen } },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  /** Shows `rows` as the shown run's settled rows, with the engine's progress over them. */
  function settle(rows: UndoRunItem[], overrides: Partial<UndoRunInfo> = {}): void {
    undo.items.set(rows);
    undo.progress.set({
      finished: rows.filter(
        (row) =>
          row.status === 'done' ||
          row.status === 'failed' ||
          row.status === 'unknown' ||
          row.skippedReason !== null,
      ).length,
      total: rows.length,
    });
    undo.run.set(
      runInfo({ result: { doneKeys: [], items: rows, startedAt: 0, finishedAt: 1 }, ...overrides }),
    );
  }

  function render(): { fixture: ComponentFixture<UndoProgressSection>; text: () => string } {
    const fixture = TestBed.createComponent(UndoProgressSection);
    fixture.detectChanges();
    return { fixture, text: () => (fixture.nativeElement as HTMLElement).textContent ?? '' };
  }

  function button(fixture: ComponentFixture<UndoProgressSection>, label: string) {
    return Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('button'),
    ).find((candidate) => candidate.textContent?.trim() === label);
  }

  it('renders nothing without a shown run and without a pending notice', () => {
    expect(render().text().trim()).toBe('');
  });

  it('names a tracked target by its channel and an untracked one by its owner, the set always', () => {
    settle([item('1')]);
    expect(render().text()).toContain('Ziel: kanal_t · Set tttt');

    undo.run.set(runInfo({ trackedChannelName: null, ownerOrChannelLabel: 'Olaf' }));
    expect(render().text()).toContain('Ziel: Set tttt von Olaf');
  });

  it('counts a row the recheck skipped as finished while running, and offers no protocol yet', () => {
    const rows = [
      item('1'),
      item('2', {
        status: 'cancelled',
        undoStatus: 'cancelled',
        completedSteps: 0,
        skippedReason: 'skippedDrift',
      }),
      item('3', { status: 'pending', undoStatus: 'pending', completedSteps: 0 }),
    ];
    undo.items.set(rows);
    undo.progress.set({ finished: 2, total: 3 });
    undo.isRunning.set(true);
    undo.run.set(runInfo({ phase: 'running', settlement: 'pending', result: null }));

    const { fixture, text } = render();

    expect(text()).toContain('2 / 3 verarbeitet');
    expect(button(fixture, 'Ergebnisprotokoll herunterladen')).toBeUndefined();
    expect(button(fixture, 'Abbrechen')).toBeDefined();
  });

  // The double-count proof: a recheck skip is `cancelled` for the engine, yet it appears once,
  // under its reason — never in the sentence's cancelled count and never in the failure list; a
  // dialog skip is named the same way; a `partial` row is in the done count, with its own line.
  it('names every skipped candidate once, under its reason, and counts a partial row as undone', () => {
    const omitted = [{ alias: 'B3', reason: 'targetNameTaken' as const }];
    settle(
      [
        item('1'),
        item('2', {
          status: 'cancelled',
          undoStatus: 'cancelled',
          completedSteps: 0,
          skippedReason: 'skippedDrift',
          errorMessage: 'ENGINE-SKIP-TEXT',
        }),
        item('3', {
          mode: 'addOnly',
          completedSteps: 1,
          undoStatus: 'partial',
          omittedEntries: omitted,
          sourceEntriesAtRemove: null,
        }),
        item('4', { status: 'cancelled', undoStatus: 'cancelled', completedSteps: 0 }),
      ],
      { skipped: [skippedRow('5', 'nothingToDo')] },
    );

    const { text } = render();

    expect(text()).toContain('2 zurückgenommen · 0 fehlgeschlagen · 1 abgebrochen');
    expect(text()).toContain('1 übersprungen: seit der Prüfung geändert');
    expect(text()).toContain('1 übersprungen: nichts zu tun');
    expect(text()).toContain('Von den zurückgenommenen Zeilen ist 1 nur teilweise zurück');
    expect(text()).toContain('1 Ziel-Eintrag blieb aus');
    expect(text()).not.toContain('ENGINE-SKIP-TEXT');
    expect(text().match(/übersprungen: seit der Prüfung geändert/g)).toHaveLength(1);
  });

  it('shows the confirmed counters, the gap hint and where an unclear removal is recorded', () => {
    settle([
      item('1'),
      item('2', { status: 'failed', undoStatus: 'failed', completedSteps: 1, failedStep: 1 }),
      item('3', { status: 'unknown', undoStatus: 'unknown', completedSteps: 0, failedStep: 0 }),
      item('4', {
        mode: 'addOnly',
        completedSteps: 1,
        notes: ['targetHasForeignEntries'],
        sourceEntriesAtRemove: null,
      }),
    ]);

    const { text } = render();

    expect(text()).toContain('2 Quell-Emotes wurden entfernt.');
    expect(text()).toContain('2 Ziel-Einträge sind wieder da.');
    expect(text()).toContain('Bei 1 Zeile ist unklar, ob 7TV sie übernommen hat');
    expect(text()).toContain('nur die Rückweg-Datei der Rücknahme deckt sie ab');
    expect(text()).toContain('Dieselbe Rücknahme aus derselben Datei erneut starten schließt sie.');
    expect(text()).toContain('1 Ziel trug schon einen Eintrag, den die Datei nicht kennt');
  });

  it('shows no counters, no skipped lines and no hints for a clean run beyond what it did', () => {
    settle([item('1')]);

    const { text } = render();

    expect(text()).toContain('1 Quell-Emote wurde entfernt.');
    expect(text()).not.toContain('übersprungen');
    expect(text()).not.toContain('Lücke');
    expect(text()).not.toContain('unklar');
  });

  describe('protocol and Close', () => {
    it('offers the protocol from settled on and keeps the not-saved hint until it was saved', () => {
      settle([item('1')], { phase: 'reporting', removalReport: 'pending' });
      const reporting = render();
      expect(button(reporting.fixture, 'Ergebnisprotokoll herunterladen')).toBeDefined();
      expect(reporting.text()).toContain('Ergebnisprotokoll noch nicht gespeichert');
      // Close waits for the run to close — a report is still out.
      expect(button(reporting.fixture, 'Schließen')).toBeUndefined();

      undo.run.set({
        ...undo.run()!,
        phase: 'closed',
        removalReport: 'succeeded',
        protocolSaved: true,
      });
      const closed = render();
      expect(closed.text()).not.toContain('Ergebnisprotokoll noch nicht gespeichert');
      button(closed.fixture, 'Schließen')?.click();
      expect(undo.reset).toHaveBeenCalledTimes(1);
    });

    it('saves the finished protocol as JSON under the target channel and marks it saved', async () => {
      settle([item('1')]);
      dialogOpen.mockReturnValue({ closed: of({ optionId: 'json', scope: 'all' }) });
      if (!('createObjectURL' in URL)) {
        Object.assign(URL, { createObjectURL: () => '', revokeObjectURL: () => undefined });
      }
      const blobs: Blob[] = [];
      vi.spyOn(URL, 'createObjectURL').mockImplementation((blob: Blob | MediaSource) => {
        blobs.push(blob as Blob);
        return 'blob:test';
      });
      vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
      const clicked: string[] = [];
      vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
        this: HTMLAnchorElement,
      ) {
        clicked.push(this.download);
      });

      const { fixture } = render();
      button(fixture, 'Ergebnisprotokoll herunterladen')?.click();

      expect(clicked).toHaveLength(1);
      expect(clicked[0]).toMatch(/^emotepurge_kanal_t_transfer-undo_.*\.json$/);
      const protocol = JSON.parse(await blobs[0].text()) as {
        kind: string;
        meta: { stage: string };
      };
      expect(protocol.kind).toBe('transfer-undo');
      expect(protocol.meta.stage).toBe('finished');
      expect(undo.markProtocolSaved).toHaveBeenCalledTimes(1);
    });
  });

  describe('the two reports', () => {
    it('offers a retry for a failed removal report in the panel, none for a channel mismatch', () => {
      settle([item('1')], { removalReport: 'failed', removalReportReason: 'unavailable' });
      const failed = render();
      expect(failed.text()).toContain('Rückmeldung über die Entfernung fehlgeschlagen');
      button(failed.fixture, 'Entfernung erneut melden')?.click();
      expect(undo.retryRemovalReport).toHaveBeenCalledTimes(1);

      undo.run.set({ ...undo.run()!, removalReportReason: 'channelMismatchNotTracked' });
      const mismatch = render();
      expect(mismatch.text()).toContain('Rückmeldung über die Entfernung fehlgeschlagen');
      expect(button(mismatch.fixture, 'Entfernung erneut melden')).toBeUndefined();
    });

    it('shows the restore report after the removal report, with its own reason and retry', () => {
      settle([item('1')], {
        removalReport: 'failed',
        removalReportReason: 'unavailable',
        restoreReport: 'partial',
        restoreReportReason: 'shortfall',
      });
      const { fixture, text } = render();

      const all = text();
      expect(all).toContain('Rückmeldung über die Wiederherstellung unvollständig');
      expect(all).toContain('Grund: Nicht alle Emotes waren in EmotePurge vermerkt.');
      expect(all.indexOf('Rückmeldung über die Entfernung')).toBeLessThan(
        all.indexOf('Rückmeldung über die Wiederherstellung'),
      );
      button(fixture, 'Wiederherstellung erneut melden')?.click();
      expect(undo.retryRestoreReport).toHaveBeenCalledTimes(1);

      undo.run.set({ ...undo.run()!, restoreReportReason: 'channelMismatchActiveSetDiffers' });
      expect(button(render().fixture, 'Wiederherstellung erneut melden')).toBeUndefined();
    });

    it('offers no report line for a run that confirmed nothing to report', () => {
      settle(
        [item('1', { status: 'failed', undoStatus: 'failed', completedSteps: 0, failedStep: 0 })],
        {
          removalReport: 'failed',
          restoreReport: 'failed',
        },
      );

      const { fixture, text } = render();

      expect(text()).not.toContain('Rückmeldung über');
      expect(button(fixture, 'Entfernung erneut melden')).toBeUndefined();
      expect(button(fixture, 'Wiederherstellung erneut melden')).toBeUndefined();
    });
  });

  it('shows the resync line only once there is one, aria-hidden, and the privileges banner after an abort', () => {
    settle([item('1')]);
    expect(render().text()).not.toContain('Abgleich läuft bereits');

    undo.run.set({ ...undo.run()!, resyncTrigger: 'backendTriggered', abortedForPrivileges: true });
    const { fixture, text } = render();
    const resync = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('[aria-hidden="true"]'),
    ).find((element) => element.textContent?.includes('Abgleich läuft bereits'));
    expect(resync).toBeDefined();
    expect(text()).toContain('Das 7TV-Token hat im Zielset kein Schreibrecht');
  });

  describe('transient skipped notice', () => {
    it('shows the skipped candidates of a start that ran nothing, aria-hidden, without a run', () => {
      undo.noticeSkipped.set([skippedRow('1', 'nothingToDo'), skippedRow('2', 'nothingToDo')]);
      undo.noticePending.set(true);

      const { fixture, text } = render();

      expect(text()).toContain('2 übersprungen: nichts zu tun');
      const line = (fixture.nativeElement as HTMLElement).querySelector('p[aria-hidden="true"]');
      expect(line?.textContent).toContain('2 übersprungen: nichts zu tun');
    });

    it('gives way to the run summary once the run it started has stopped running', () => {
      const skipped = [skippedRow('9', 'skippedUnproven')];
      undo.noticeSkipped.set(skipped);
      undo.noticePending.set(true);
      undo.isRunning.set(true);
      undo.items.set([item('1', { status: 'pending', undoStatus: 'pending', completedSteps: 0 })]);
      undo.progress.set({ finished: 0, total: 1 });
      undo.run.set(runInfo({ phase: 'running', settlement: 'pending', result: null, skipped }));
      const running = render();
      expect(running.text().match(/1 übersprungen: unbelegt und nicht bestätigt/g)).toHaveLength(1);

      undo.isRunning.set(false);
      settle([item('1')], { skipped });
      const settled = render();
      expect(settled.text().match(/1 übersprungen: unbelegt und nicht bestätigt/g)).toHaveLength(1);
      expect(settled.fixture.nativeElement.querySelector('p[aria-hidden="true"]')).toBeNull();
    });
  });

  it('has every undo dock key in both locales', () => {
    const keys = (value: object, prefix = ''): string[] =>
      Object.entries(value).flatMap(([key, child]) =>
        typeof child === 'object' && child !== null
          ? keys(child as object, `${prefix}${key}.`)
          : [`${prefix}${key}`],
      );
    const dockKeys = (locale: { undo: object }) =>
      keys(locale.undo).filter((key) => !key.startsWith('confirm.'));
    expect(dockKeys(en)).toEqual(dockKeys(de));
  });
});
