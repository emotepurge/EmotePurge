import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { beforeEach, describe, expect, it } from 'vitest';

import en from '../../../../public/i18n/en.json';
import { SavedLogFilter } from '../../core/admin/admin-logs.model';
import {
  AdminLogFilterChips,
  currentFilterBlockedKey,
  isLogFilterChipActive,
} from './admin-log-filter-chips';

const FILTERS: SavedLogFilter[] = [
  { id: 1, name: 'Alle Fehler', filter: 'level>=error', sortOrder: 4 },
  { id: 2, name: 'Reconnects', filter: 'source:worker  cat:"Twitch Chat"', sortOrder: 2 },
];

@Component({
  imports: [AdminLogFilterChips],
  template: `
    <app-admin-log-filter-chips
      [filters]="filters"
      [currentQ]="q()"
      [loadFailed]="failed()"
      (selected)="events.push(['select', $event.id])"
      (saveRequested)="events.push(['save'])"
      (editRequested)="events.push(['edit', $event.id])"
      (overwriteRequested)="events.push(['overwrite', $event.id])"
      (deleteRequested)="events.push(['delete', $event.id])"
      (retryRequested)="events.push(['retry'])"
    />
  `,
})
class Host {
  filters = FILTERS;
  q = signal('');
  failed = signal(false);
  events: unknown[][] = [];
}

describe('isLogFilterChipActive', () => {
  it('compares after whitespace normalization, not literally', () => {
    expect(isLogFilterChipActive('level>=error', '  level>=error ')).toBe(true);
    expect(isLogFilterChipActive('source:worker  cat:a', 'source:worker cat:a')).toBe(true);
  });

  it('keeps whitespace inside quotes significant and is exact otherwise', () => {
    expect(isLogFilterChipActive('"a  b"', '"a b"')).toBe(false);
    expect(isLogFilterChipActive('level>=error', 'level>=warn')).toBe(false);
    expect(isLogFilterChipActive('level>=error', 'level>=error source:api')).toBe(false);
  });
});

describe('currentFilterBlockedKey', () => {
  it('names a reason for an empty or blank filter and none for a filled one', () => {
    expect(currentFilterBlockedKey('')).toBe('admin.logs.savedFilters.blockedEmptyFilter');
    expect(currentFilterBlockedKey('   ')).toBe('admin.logs.savedFilters.blockedEmptyFilter');
    expect(currentFilterBlockedKey('level>=error')).toBeNull();
  });
});

describe('AdminLogFilterChips', () => {
  let fixture: ComponentFixture<Host>;
  let host: Host;
  const root = () => fixture.nativeElement as HTMLElement;
  const buttons = () => Array.from(root().querySelectorAll('button'));
  const named = (name: string) =>
    buttons().find((b) => (b.getAttribute('aria-label') ?? b.textContent?.trim()) === name)!;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { en },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
    });
    fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('marks exactly the chip whose filter equals q after normalization', () => {
    host.q.set('source:worker cat:"Twitch Chat"');
    fixture.detectChanges();
    expect(named('Reconnects').getAttribute('aria-pressed')).toBe('true');
    expect(named('Alle Fehler').getAttribute('aria-pressed')).toBe('false');
  });

  it('emits the chip on click', () => {
    named('Alle Fehler').click();
    expect(host.events).toEqual([['select', 1]]);
  });

  it('locks "save" with a visible reason while the filter is empty, and unlocks it with one', () => {
    const save = named('+ Save filter');
    expect(save.disabled).toBe(true);
    const hint = root().querySelector('#log-save-blocked-hint');
    expect(hint?.textContent).toContain('Enter a filter before saving.');
    expect(save.getAttribute('aria-describedby')).toBe('log-save-blocked-hint');

    host.q.set('level>=error');
    fixture.detectChanges();
    expect(save.disabled).toBe(false);
    expect(root().querySelector('#log-save-blocked-hint')).toBeNull();
    save.click();
    expect(host.events).toEqual([['save']]);
  });

  it('opens a per-chip menu with edit, overwrite and delete, and closes it on a choice', () => {
    host.q.set('level>=warn');
    fixture.detectChanges();
    const trigger = named('Menu for filter Alle Fehler');
    expect(trigger.getAttribute('aria-haspopup')).toBe('dialog');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
    trigger.click();
    fixture.detectChanges();
    expect(trigger.getAttribute('aria-expanded')).toBe('true');

    named('Overwrite with current filter').click();
    fixture.detectChanges();
    expect(host.events).toEqual([['overwrite', 1]]);
    expect(root().querySelector('app-popover')).toBeNull();

    trigger.click();
    fixture.detectChanges();
    named('Edit').click();
    trigger.click();
    fixture.detectChanges();
    named('Delete').click();
    expect(host.events.map((e) => e[0])).toEqual(['overwrite', 'edit', 'delete']);
  });

  it('locks "overwrite" while the current filter is empty and says why', () => {
    named('Menu for filter Alle Fehler').click();
    fixture.detectChanges();
    expect(named('Overwrite with current filter').disabled).toBe(true);
    expect(root().querySelector('app-popover')?.textContent).toContain(
      'Enter a filter before saving.',
    );
  });

  it('offers a retry when the list could not be loaded', () => {
    host.failed.set(true);
    fixture.detectChanges();
    expect(root().querySelector('[role="alert"]')?.textContent).toContain(
      'saved filters could not be loaded',
    );
    named('Try again').click();
    expect(host.events).toEqual([['retry']]);
  });
});
