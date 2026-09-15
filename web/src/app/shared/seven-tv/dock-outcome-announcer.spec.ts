import { Component, WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import {
  ResyncTriggerState,
  SevenTvRestoreService,
} from '../../core/seven-tv/seven-tv-restore.service';
import { DockOutcomeAnnouncer, resyncNoticeKey } from './dock-outcome-announcer';

const DE_TRANSLATIONS = {
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
  },
};

interface FakeOutcomeSource {
  resyncTrigger: WritableSignal<ResyncTriggerState>;
  skippedDuplicates: WritableSignal<number>;
  duplicateCheckAvailable: WritableSignal<boolean>;
  duplicateNoticePending: WritableSignal<boolean>;
}

function createFakeSource(): FakeOutcomeSource {
  return {
    resyncTrigger: signal<ResyncTriggerState>('idle'),
    skippedDuplicates: signal(0),
    duplicateCheckAvailable: signal(true),
    duplicateNoticePending: signal(false),
  };
}

/** Stands in for a host page: the announcer mounted once, with the page's own `withImport`. */
@Component({
  imports: [DockOutcomeAnnouncer],
  template: `<app-dock-outcome-announcer [withImport]="withImport()" />`,
})
class HostPage {
  readonly withImport = signal(true);
}

describe('resyncNoticeKey', () => {
  it('has nothing to say while idle, and names the family and state otherwise', () => {
    expect(resyncNoticeKey('idle', 'import')).toBeNull();
    expect(resyncNoticeKey('pending', 'import')).toBe('import.resync.pending');
    expect(resyncNoticeKey('cooldown', 'restore')).toBe('restore.resync.cooldown');
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
