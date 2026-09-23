import { Component, WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { ImportRunInfo, SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import {
  ResyncTriggerState,
  SevenTvRestoreService,
} from '../../core/seven-tv/seven-tv-restore.service';
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
    resync: {
      pending: 'Synchronisierung wird angestoßen…',
      succeeded: 'Synchronisierung angestoßen.',
      cooldown: 'Sync-Cooldown aktiv.',
      failed: 'Synchronisierung fehlgeschlagen.',
    },
  },
  import: {
    duplicateCheckUnavailable: 'Import-Prüfung nicht möglich.',
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
    },
  },
};

interface FakeOutcomeSource {
  resyncTrigger: WritableSignal<ResyncTriggerState>;
  skippedDuplicates: WritableSignal<number>;
  duplicateCheckAvailable: WritableSignal<boolean>;
  duplicateNoticePending: WritableSignal<boolean>;
  /** Only `SevenTvImportService` actually has this (finding 3, Live-Verifikation K2 2026-09-21,
   *  `copiedNotActiveNotice`) — carried on the shared fake shape anyway since both services are
   *  built from the same factory; the restore fake's copy is simply never read. */
  run: WritableSignal<ImportRunInfo | null>;
}

function createFakeSource(): FakeOutcomeSource {
  return {
    resyncTrigger: signal<ResyncTriggerState>('idle'),
    skippedDuplicates: signal(0),
    duplicateCheckAvailable: signal(true),
    duplicateNoticePending: signal(false),
    run: signal<ImportRunInfo | null>(null),
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

  // Finding 3 (Live-Verifikation K2 2026-09-21): a copy into a tracked non-active set never sets
  // resyncTrigger away from 'idle' (SevenTvImportService.onRunComplete skips the call outright) —
  // this notice takes the resync acknowledgement's own slot in the reading order instead.
  it('speaks the copied-not-active notice in the resync slot when the run settled on a non-active target', () => {
    importService.run.set({
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
      result: { doneKeys: ['7tv-1'], items: [], startedAt: 0, finishedAt: 1 },
    });
    importService.resyncTrigger.set('idle');
    fixture.detectChanges();

    expect(spoken()).toEqual([
      "In Set ‚wegwerf' kopiert — es ist nicht das aktive Set von zielkanal, die Kanalseite zeigt es deshalb nicht.",
    ]);
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
