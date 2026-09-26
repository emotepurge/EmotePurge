import { Component, WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import {
  ImportRunInfo,
  ImportRunItem,
  SevenTvImportService,
} from '../../core/seven-tv/seven-tv-import.service';
import {
  ResyncTriggerState,
  SevenTvRestoreService,
} from '../../core/seven-tv/seven-tv-restore.service';
import { TargetCheckBlockReason } from '../../core/seven-tv/sync-report-outcome';
import {
  DockOutcomeAnnouncer,
  hiddenByFilterNoticeKey,
  markedCountNoticeKey,
  resyncNoticeKey,
} from './dock-outcome-announcer';

const DE_TRANSLATIONS = {
  usageStats: {
    dock: {
      markedAnnounced: {
        one: '{{ count }} Emote markiert',
        other: '{{ count }} Emotes markiert',
      },
      hiddenByFilter: {
        one: '1 davon durch den Filter ausgeblendet',
        other: '{{ count }} davon durch den Filter ausgeblendet',
      },
    },
  },
  restore: {
    duplicateCheckUnavailable: 'Restore-Prüfung nicht möglich.',
    skippedDuplicates: {
      one: '{{ count }} Emote ist bereits im Zielset und wurde übersprungen.',
      other: '{{ count }} Emotes sind bereits im Zielset und wurden übersprungen.',
    },
    skippedNameTaken: {
      one: '{{ count }} Alias übersprungen — der Name gehört inzwischen einem anderen Emote.',
      other: '{{ count }} Aliase übersprungen — die Namen gehören inzwischen anderen Emotes.',
    },
    resync: {
      pending: 'Synchronisierung wird angestoßen…',
      succeeded: 'Synchronisierung angestoßen.',
      cooldown: 'Sync-Cooldown aktiv.',
      failed: 'Synchronisierung fehlgeschlagen.',
      backendTriggered: 'Wird abgeglichen.',
    },
  },
  import: {
    duplicateCheckUnavailable: 'Import-Prüfung nicht möglich.',
    errors: {
      targetNotEditable: 'Das Zielset ist nicht (mehr) bearbeitbar oder existiert nicht mehr.',
      targetNotSelectable: 'Das Zielset ist kein normales Emote-Set.',
      targetCheckUnavailable: 'Das Zielset konnte gerade nicht geprüft werden.',
    },
    skippedDuplicates: {
      one: '{{ count }} Emote war beim Start bereits im Zielset und wurde übersprungen.',
      other: '{{ count }} Emotes waren beim Start bereits im Zielset und wurden übersprungen.',
    },
    resync: {
      pending: 'Abgleich des Zielkanals wird angestoßen…',
      succeeded: 'Abgleich angestoßen.',
      cooldown: 'Abgleich beim nächsten Mal.',
      failed: 'Abgleich fehlgeschlagen.',
    },
    summary: {
      copiedNotActive:
        "In Set ‚{{ setName }}' kopiert — es ist nicht das aktive Set von {{ channel }}, die Kanalseite zeigt es deshalb nicht.",
      renamedNotActive:
        "In Set ‚{{ setName }}' umbenannt — es ist nicht das aktive Set von {{ channel }}, die Kanalseite zeigt es deshalb nicht.",
      replaceSkippedDrift: {
        one: '{{ count }} Ersetzung wurde nicht ausgeführt — das Ziel hatte sich seit der Bestätigung verändert.',
        other:
          '{{ count }} Ersetzungen wurden nicht ausgeführt — das Ziel hatte sich seit der Bestätigung verändert.',
      },
    },
  },
};

interface FakeOutcomeSource {
  resyncTrigger: WritableSignal<ResyncTriggerState>;
  skippedDuplicates: WritableSignal<number>;
  /** Only `SevenTvRestoreService` actually has this — shared shape, the import fake's copy is
   *  never read. */
  skippedNameTaken: WritableSignal<number>;
  duplicateCheckAvailable: WritableSignal<boolean>;
  duplicateNoticePending: WritableSignal<boolean>;
  /** Only `SevenTvImportService` actually has this (finding 3, Live-Verifikation K2 2026-09-21,
   *  `copiedNotActiveNotice`) — carried on the shared fake shape anyway since both services are
   *  built from the same factory; the restore fake's copy is simply never read. */
  run: WritableSignal<ImportRunInfo | null>;
  /** Only `SevenTvImportService` actually has this — a restore run never carries a replace row.
   *  Same reasoning as `run` above: shared shape, the restore fake's copy is never read. */
  replaceSkippedDrift: WritableSignal<number>;
  /** Only `SevenTvImportService` actually has this (spec 4.5 point 17, AK 32) — the shared
   *  pre-check's block reason on a replace-carrying start has no restore counterpart (the restore
   *  entry's own pre-check has no notice at all, spec E16, 4.6 point 22). Same reasoning as `run`
   *  and `replaceSkippedDrift` above: shared shape, the restore fake's copy is never read. */
  targetCheckBlockReason: WritableSignal<TargetCheckBlockReason | null>;
}

function createFakeSource(): FakeOutcomeSource {
  return {
    resyncTrigger: signal<ResyncTriggerState>('idle'),
    skippedDuplicates: signal(0),
    skippedNameTaken: signal(0),
    duplicateCheckAvailable: signal(true),
    duplicateNoticePending: signal(false),
    run: signal<ImportRunInfo | null>(null),
    replaceSkippedDrift: signal(0),
    targetCheckBlockReason: signal<TargetCheckBlockReason | null>(null),
  };
}

/** Stands in for a host page: the announcer mounted once, with the page's own `withImport`. */
@Component({
  imports: [DockOutcomeAnnouncer],
  template: `<app-dock-outcome-announcer
    [withImport]="withImport()"
    [markedCount]="markedCount()"
    [hiddenSelectedCount]="hiddenSelectedCount()"
  />`,
})
class HostPage {
  readonly withImport = signal(true);
  readonly markedCount = signal(0);
  readonly hiddenSelectedCount = signal(0);
}

// Keyed by the type rather than hand-listed, so a new ResyncTriggerState member (other than
// 'idle', handled separately below) fails this file to compile until it is added here too.
const SPOKEN_STATES = {
  pending: true,
  succeeded: true,
  cooldown: true,
  failed: true,
  backendTriggered: true,
} satisfies Record<Exclude<ResyncTriggerState, 'idle'>, true>;

describe('resyncNoticeKey', () => {
  it('has nothing to say while idle, regardless of family', () => {
    expect(resyncNoticeKey('idle', 'import')).toBeNull();
    expect(resyncNoticeKey('idle', 'restore')).toBeNull();
  });

  it.each(
    Object.keys(SPOKEN_STATES).flatMap((state) =>
      (['import', 'restore'] as const).map((family) => [state, family] as const),
    ),
  )('names the family and state for %s/%s', (state, family) => {
    expect(resyncNoticeKey(state as ResyncTriggerState, family)).toBe(`${family}.resync.${state}`);
  });
});

describe('hiddenByFilterNoticeKey', () => {
  it('picks the plural form the dock line and its announcement share', () => {
    expect(hiddenByFilterNoticeKey(1)).toBe('usageStats.dock.hiddenByFilter.one');
    expect(hiddenByFilterNoticeKey(4)).toBe('usageStats.dock.hiddenByFilter.other');
  });
});

describe('markedCountNoticeKey', () => {
  it('picks the plural form the dock row and its announcement share', () => {
    expect(markedCountNoticeKey(1)).toBe('usageStats.dock.markedAnnounced.one');
    expect(markedCountNoticeKey(4)).toBe('usageStats.dock.markedAnnounced.other');
  });
});

/**
 * #134: the announcer is the only voice for the dock's outcome notices (docs/UI-Designsprache.md
 * §4.5) — its region must stand before any of them exists, and each outcome must enter it once.
 */
describe('DockOutcomeAnnouncer', () => {
  let restoreService: FakeOutcomeSource;
  let importService: FakeOutcomeSource;
  let fixture: ComponentFixture<HostPage>;

  beforeEach(async () => {
    restoreService = createFakeSource();
    importService = createFakeSource();

    await TestBed.configureTestingModule({
      imports: [
        HostPage,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: SevenTvRestoreService, useValue: restoreService },
        { provide: SevenTvImportService, useValue: importService },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    fixture = TestBed.createComponent(HostPage);
    fixture.detectChanges();
  });

  const regions = (): HTMLElement[] =>
    Array.from(fixture.nativeElement.querySelectorAll('[role="status"]'));
  const spoken = (): string[] =>
    Array.from(regions()[0].children).map((child) => child.textContent?.trim() ?? '');

  it('stands as one empty status region while there is nothing to announce', () => {
    expect(regions()).toHaveLength(1);
    expect(regions()[0].textContent?.trim()).toBe('');
  });

  it('sets aria-atomic to false so a new paragraph is not read together with standing ones', () => {
    expect(regions()[0].getAttribute('aria-atomic')).toBe('false');
  });

  it('fills the region that was already standing when a fully refused import reports its skips', () => {
    const regionAtRest = regions()[0];

    importService.skippedDuplicates.set(2);
    importService.duplicateNoticePending.set(true);
    fixture.detectChanges();

    expect(regions()).toEqual([regionAtRest]);
    expect(spoken()).toEqual([
      '2 Emotes waren beim Start bereits im Zielset und wurden übersprungen.',
    ]);
  });

  // A run where every replace row drifted since confirmation queues nothing at all — this notice
  // is the only feedback such a run ever produces, so it needs the same already-standing region
  // its skipped-duplicates sibling above relies on.
  it('fills the region that was already standing when an all-drift import reports its skipped replacements', () => {
    const regionAtRest = regions()[0];

    importService.replaceSkippedDrift.set(3);
    importService.duplicateNoticePending.set(true);
    fixture.detectChanges();

    expect(regions()).toEqual([regionAtRest]);
    expect(spoken()).toEqual([
      '3 Ersetzungen wurden nicht ausgeführt — das Ziel hatte sich seit der Bestätigung verändert.',
    ]);
  });

  // #253, spec 4.5 point 17, AK 32: the shared pre-check blocked a replace-carrying start before
  // anything ran — same reachable-with-no-run shape as the drift notice above, same window.
  it('fills the region that was already standing when the shared pre-check blocks a replace-carrying start', () => {
    const regionAtRest = regions()[0];

    importService.targetCheckBlockReason.set('notEditable');
    importService.duplicateNoticePending.set(true);
    fixture.detectChanges();

    expect(regions()).toEqual([regionAtRest]);
    expect(spoken()).toEqual([
      'Das Zielset ist nicht (mehr) bearbeitbar oder existiert nicht mehr.',
    ]);
  });

  it('speaks each duplicate notice only while its pending window is open', () => {
    restoreService.skippedDuplicates.set(1);
    restoreService.duplicateCheckAvailable.set(false);
    restoreService.duplicateNoticePending.set(true);
    fixture.detectChanges();
    expect(spoken()).toEqual([
      '1 Emote ist bereits im Zielset und wurde übersprungen.',
      'Restore-Prüfung nicht möglich.',
    ]);

    restoreService.duplicateNoticePending.set(false);
    fixture.detectChanges();
    expect(spoken()).toEqual([]);
  });

  // A restore's aliases left out because another emote holds the name: spoken on their own line,
  // right after the "already present" count, within the same pending window.
  it('speaks the name-taken count after the restore skip count, only while the window is open', () => {
    restoreService.skippedDuplicates.set(1);
    restoreService.skippedNameTaken.set(2);
    restoreService.duplicateNoticePending.set(true);
    fixture.detectChanges();
    expect(spoken()).toEqual([
      '1 Emote ist bereits im Zielset und wurde übersprungen.',
      '2 Aliase übersprungen — die Namen gehören inzwischen anderen Emotes.',
    ]);

    restoreService.duplicateNoticePending.set(false);
    fixture.detectChanges();
    expect(spoken()).toEqual([]);
  });

  it('lists several outcomes in the dock reading order: restore before import, skips, check, resync', () => {
    importService.resyncTrigger.set('succeeded');
    importService.skippedDuplicates.set(3);
    importService.duplicateCheckAvailable.set(false);
    importService.duplicateNoticePending.set(true);
    restoreService.resyncTrigger.set('pending');
    restoreService.skippedDuplicates.set(1);
    restoreService.duplicateNoticePending.set(true);
    fixture.detectChanges();

    expect(spoken()).toEqual([
      '1 Emote ist bereits im Zielset und wurde übersprungen.',
      'Synchronisierung wird angestoßen…',
      '3 Emotes waren beim Start bereits im Zielset und wurden übersprungen.',
      'Import-Prüfung nicht möglich.',
      'Abgleich angestoßen.',
    ]);
  });

  /** Minimal `ImportRunItem` fixture for the copied/renamed-not-active tests below — only
   *  `status` and `transfer.action` are what `hasAddDone`/`hasAdoptDone` (`dock-outcome-
   *  announcer.ts`) read, so the rest is filled in with values that satisfy the type without
   *  meaning anything on their own. */
  function doneItem(action: 'add' | 'adoptSourceName'): ImportRunItem {
    return {
      key: '7tv-1',
      sevenTvEmoteId: '7tv-1',
      name: 'PogU',
      status: 'done',
      completedSteps: 1,
      failedStep: null,
      transfer: { action, source: {}, alias: 'PogU', target: {} },
    } as unknown as ImportRunItem;
  }

  /** A settled run into a tracked non-active target — the one case that fires either of the two
   *  notices below (#255 P2-2) — with `items` the only thing each test varies. */
  function nonActiveRun(items: ImportRunItem[]): ImportRunInfo {
    return {
      runId: 'import-1',
      phase: 'closed',
      destructive: false,
      syncReport: 'idle',
      removalReport: 'idle',
      removalReportReason: null,
      resyncTrigger: 'idle',
      abortedForPrivileges: false,
      protocolSaved: false,
      targetChannelName: 'zielkanal',
      targetOwnerDisplayName: null,
      targetSetId: 'set-1',
      targetSetName: 'wegwerf',
      targetIsActiveSet: false,
      origin: { kind: 'channel', channelName: 'quellkanal' },
      plan: { rows: [] },
      settlement: 'settled',
      removedCount: 0,
      unknownCount: 0,
      result: { doneKeys: items.map((item) => item.key), items, startedAt: 0, finishedAt: 1 },
    };
  }

  // Finding 3 (Live-Verifikation K2 2026-09-21): a copy into a tracked non-active set never sets
  // resyncTrigger away from 'idle' (SevenTvImportService.onRunComplete skips the call outright) —
  // this notice takes the resync acknowledgement's own slot in the reading order instead. Needs at
  // least one done ADD (#255 P2-2, review finding) — a run with only a plain `add` qualifies.
  it('speaks the copied-not-active notice in the resync slot when the run settled on a non-active target with a done add', () => {
    importService.run.set(nonActiveRun([doneItem('add')]));
    importService.resyncTrigger.set('idle');
    fixture.detectChanges();

    expect(spoken()).toEqual([
      "In Set ‚wegwerf' kopiert — es ist nicht das aktive Set von zielkanal, die Kanalseite zeigt es deshalb nicht.",
    ]);
  });

  // #255 P2-2 (review finding): a rename-only run (every done row an adopt, no ADD at all) copied
  // nothing in, so the "kopiert" notice above would misdescribe it — a run into a non-active
  // target gets its own "umbenannt" wording instead, in the same resync slot.
  it('speaks the renamed-not-active notice instead when every done row is an adopt', () => {
    importService.run.set(nonActiveRun([doneItem('adoptSourceName')]));
    importService.resyncTrigger.set('idle');
    fixture.detectChanges();

    expect(spoken()).toEqual([
      "In Set ‚wegwerf' umbenannt — es ist nicht das aktive Set von zielkanal, die Kanalseite zeigt es deshalb nicht.",
    ]);
  });

  // #255 P2-2 (review finding): a run where nothing at all succeeded has nothing true to say about
  // what landed in the target set — neither notice fires, and the resync slot stays empty (the
  // resync itself never ran either, since onRunComplete skips it for a non-active target).
  it('speaks neither notice when the run settled on a non-active target with nothing done', () => {
    importService.run.set(nonActiveRun([]));
    importService.resyncTrigger.set('idle');
    fixture.detectChanges();

    expect(spoken()).toEqual([]);
  });

  it('adds a later outcome without replacing the node of one already standing', () => {
    restoreService.resyncTrigger.set('pending');
    fixture.detectChanges();
    const standing = regions()[0].children[0];

    importService.skippedDuplicates.set(2);
    importService.duplicateNoticePending.set(true);
    fixture.detectChanges();

    expect(regions()[0].children[0]).toBe(standing);
    expect(spoken()).toHaveLength(2);
  });

  /**
   * The dock's marked-count row (2026-09-19, docs/DECISIONS.md). Same defect as the hidden-by-
   * filter line below and the same fix: the visible row is created by the same `@if` that fills
   * it, so a bulk mark ("mark all") can take it from unmounted to a double-digit count with
   * nothing announced — this region is its only voice for that gesture (§4.5).
   *
   * This component itself only ever renders whatever `markedCount` it is handed — it has no notion
   * of "bulk gesture" vs. "individual click" of its own, that distinction is entirely the host's
   * job (`UsageStatsPage.dockMarkedCount`, updated only from `markAll()`/`selectBand()` since the
   * 2026-09-19 Opus review, see its own comment). So the tests below still exercise this component
   * the same way as before that review — feeding it a bare number and checking what it announces —
   * they just no longer stand for "the live selection count" the way they used to; they stand for
   * "whatever number the host last decided was worth a bulk-mark announcement".
   */
  describe('marked-count row', () => {
    it('says nothing while nothing is marked', () => {
      expect(spoken()).toEqual([]);
    });

    it('fills the region that was already standing when a bulk mark lands', () => {
      const regionAtRest = regions()[0];

      fixture.componentInstance.markedCount.set(2);
      fixture.detectChanges();

      expect(regions()).toEqual([regionAtRest]);
      expect(spoken()).toEqual(['2 Emotes markiert']);
    });

    it('uses the singular wording for exactly one marked row', () => {
      fixture.componentInstance.markedCount.set(1);
      fixture.detectChanges();

      expect(spoken()).toEqual(['1 Emote markiert']);
    });

    it('rewrites the standing sentence in place when the count changes, rather than adding a second one', () => {
      fixture.componentInstance.markedCount.set(3);
      fixture.detectChanges();
      const standing = regions()[0].children[0];

      fixture.componentInstance.markedCount.set(2);
      fixture.detectChanges();

      expect(regions()[0].children[0]).toBe(standing);
      expect(spoken()).toEqual(['2 Emotes markiert']);
    });

    it('falls silent again once nothing is marked, without unmounting the region', () => {
      const regionAtRest = regions()[0];
      fixture.componentInstance.markedCount.set(2);
      fixture.detectChanges();

      fixture.componentInstance.markedCount.set(0);
      fixture.detectChanges();

      expect(regions()).toEqual([regionAtRest]);
      expect(spoken()).toEqual([]);
    });

    it('speaks before the hidden-by-filter line, the restore and the import outcomes', () => {
      fixture.componentInstance.markedCount.set(4);
      fixture.componentInstance.hiddenSelectedCount.set(2);
      restoreService.resyncTrigger.set('succeeded');
      importService.resyncTrigger.set('pending');
      fixture.detectChanges();

      expect(spoken()).toEqual([
        '4 Emotes markiert',
        '2 davon durch den Filter ausgeblendet',
        'Synchronisierung angestoßen.',
        'Abgleich des Zielkanals wird angestoßen…',
      ]);
    });
  });

  /**
   * The dock's hidden-by-filter line (Konzept "Auswahl überlebt Suche und Filter" 2.2). Its visible
   * row is created by the same `@if` that fills it, so it cannot announce itself — this region,
   * which already stood before the filter was touched, is its only voice (§4.5).
   */
  describe('hidden-by-filter line', () => {
    it('says nothing while the filter hides nothing', () => {
      expect(spoken()).toEqual([]);
    });

    it('fills the region that was already standing when a filter starts hiding marked rows', () => {
      const regionAtRest = regions()[0];

      fixture.componentInstance.hiddenSelectedCount.set(2);
      fixture.detectChanges();

      expect(regions()).toEqual([regionAtRest]);
      expect(spoken()).toEqual(['2 davon durch den Filter ausgeblendet']);
    });

    it('uses the singular wording for exactly one hidden row', () => {
      fixture.componentInstance.hiddenSelectedCount.set(1);
      fixture.detectChanges();

      expect(spoken()).toEqual(['1 davon durch den Filter ausgeblendet']);
    });

    it('rewrites the standing sentence in place when the count changes, rather than adding a second one', () => {
      fixture.componentInstance.hiddenSelectedCount.set(3);
      fixture.detectChanges();
      const standing = regions()[0].children[0];

      fixture.componentInstance.hiddenSelectedCount.set(2);
      fixture.detectChanges();

      expect(regions()[0].children[0]).toBe(standing);
      expect(spoken()).toEqual(['2 davon durch den Filter ausgeblendet']);
    });

    it('falls silent again once the filter is reset, without unmounting the region', () => {
      const regionAtRest = regions()[0];
      fixture.componentInstance.hiddenSelectedCount.set(2);
      fixture.detectChanges();

      fixture.componentInstance.hiddenSelectedCount.set(0);
      fixture.detectChanges();

      expect(regions()).toEqual([regionAtRest]);
      expect(spoken()).toEqual([]);
    });

    it('speaks before the restore and import outcomes, matching the dock reading order', () => {
      fixture.componentInstance.hiddenSelectedCount.set(2);
      restoreService.resyncTrigger.set('succeeded');
      importService.resyncTrigger.set('pending');
      fixture.detectChanges();

      expect(spoken()).toEqual([
        '2 davon durch den Filter ausgeblendet',
        'Synchronisierung angestoßen.',
        'Abgleich des Zielkanals wird angestoßen…',
      ]);
    });
  });

  it('does not speak for an import on a page that shows no import section', () => {
    fixture.componentInstance.withImport.set(false);
    importService.resyncTrigger.set('failed');
    importService.skippedDuplicates.set(2);
    importService.duplicateNoticePending.set(true);
    restoreService.resyncTrigger.set('cooldown');
    fixture.detectChanges();

    expect(spoken()).toEqual(['Sync-Cooldown aktiv.']);
  });
});
