import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Subject } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import en from '../../../../public/i18n/en.json';
import { SavedLogFilter } from '../../core/admin/admin-logs.model';
import { AdminLogFacetsDialog, LogFacetsDialogData } from './admin-log-facets-dialog';

const FILTER: SavedLogFilter = { id: 4, name: 'Alle Fehler', filter: 'level>=error', sortOrder: 4 };

describe('AdminLogFacetsDialog saved filters', () => {
  let fixture: ComponentFixture<AdminLogFacetsDialog>;
  let log: string[];
  let closed: Subject<void>;

  beforeEach(() => {
    log = [];
    closed = new Subject<void>();
    const data: LogFacetsDialogData = {
      rangeOptions: [],
      rangeValue: signal('24h'),
      backRange: signal(null),
      summary: signal(null),
      setRange: () => undefined,
      goBack: () => undefined,
      addTerm: () => undefined,
      savedFilters: signal([FILTER]),
      currentQ: signal('level>=warn'),
      savedFiltersFailed: signal(false),
      selectFilter: () => log.push('select'),
      saveCurrent: () => log.push('save'),
      editFilter: () => log.push('edit'),
      overwriteFilter: () => log.push('overwrite'),
      deleteFilter: () => log.push('delete'),
      retryFilters: () => log.push('retry'),
    };
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { en },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
      providers: [
        { provide: DIALOG_DATA, useValue: data },
        {
          provide: DialogRef,
          useValue: {
            closed,
            close: () => {
              log.push('closing');
              closed.next();
            },
          },
        },
      ],
    });
    fixture = TestBed.createComponent(AdminLogFacetsDialog);
    fixture.detectChanges();
  });

  const root = () => fixture.nativeElement as HTMLElement;
  const named = (name: string) =>
    Array.from(root().querySelectorAll('button')).find(
      (b) => (b.getAttribute('aria-label') ?? b.textContent?.trim()) === name,
    )!;

  it('shows the chips and applies one after the sheet has closed', () => {
    named('Alle Fehler').click();
    expect(log).toEqual(['closing', 'select']);
  });

  it('closes the sheet before "save" opens its dialog', () => {
    named('+ Save filter').click();
    expect(log).toEqual(['closing', 'save']);
  });

  it.each([
    ['Edit', 'edit'],
    ['Overwrite with current filter', 'overwrite'],
    ['Delete', 'delete'],
  ])('closes the sheet before the menu action "%s" runs', (label, action) => {
    named('Menu for filter Alle Fehler').click();
    fixture.detectChanges();
    named(label).click();
    expect(log).toEqual(['closing', action]);
  });
});
