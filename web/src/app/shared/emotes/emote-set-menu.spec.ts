import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { describe, expect, it } from 'vitest';

import { EmoteSetSummary } from '../../core/seven-tv/seven-tv-emote-set.model';
import { EmoteSetMenu, selectableEmoteSets } from './emote-set-menu';

function set(overrides: Partial<EmoteSetSummary> = {}): EmoteSetSummary {
  return {
    id: 'set-1',
    name: 'Hauptset',
    capacity: 250,
    kind: 'NORMAL',
    isActive: true,
    isPersonal: false,
    ownerDisplayName: 'Owner',
    observations: [],
    ...overrides,
  };
}

describe('selectableEmoteSets', () => {
  it('keeps NORMAL sets', () => {
    const normal = set({ id: 'set-1', kind: 'NORMAL' });

    expect(selectableEmoteSets([normal])).toEqual([normal]);
  });

  it('hides a PERSONAL set entirely rather than showing it disabled (decision 2026-09-21)', () => {
    const personal = set({ id: 'set-2', kind: 'PERSONAL', isPersonal: true });

    expect(selectableEmoteSets([personal])).toEqual([]);
  });

  it('hides GLOBAL and SPECIAL sets too — this dropdown offers only NORMAL, unlike the source/target pickers', () => {
    const global = set({ id: 'set-3', kind: 'GLOBAL' });
    const special = set({ id: 'set-4', kind: 'SPECIAL' });

    expect(selectableEmoteSets([global, special])).toEqual([]);
  });

  it('keeps ordinal input order, filtering out only the non-NORMAL entries', () => {
    const a = set({ id: 'a', name: 'A', kind: 'NORMAL' });
    const personal = set({ id: 'p', kind: 'PERSONAL', isPersonal: true });
    const b = set({ id: 'b', name: 'B', kind: 'NORMAL' });

    expect(selectableEmoteSets([a, personal, b])).toEqual([a, b]);
  });

  it('returns an empty list for an account with nothing but non-NORMAL sets', () => {
    expect(selectableEmoteSets([set({ kind: 'PERSONAL', isPersonal: true })])).toEqual([]);
  });

  it('returns an empty list for an empty input', () => {
    expect(selectableEmoteSets([])).toEqual([]);
  });
});

/**
 * #200 K4 fix round (finding G): the host locks the dropdown while a delete run is still writing
 * into the set on screen. Only the blocking decision and its reason are pinned here (Regel 12) —
 * never the trigger's styling.
 */
describe('EmoteSetMenu — a host lock disables the trigger and explains itself', () => {
  function render(lockedReasonKey: string | null) {
    TestBed.configureTestingModule({
      imports: [
        EmoteSetMenu,
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
    });
    const fixture = TestBed.createComponent(EmoteSetMenu);
    fixture.componentRef.setInput('sets', [set(), set({ id: 'set-2', isActive: false })]);
    fixture.componentRef.setInput('selectedEmoteSetId', 'set-1');
    fixture.componentRef.setInput('lockedReasonKey', lockedReasonKey);
    fixture.detectChanges();
    const trigger = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    return { fixture, trigger };
  }

  it('disables the trigger and points it at the visible reason', () => {
    const { trigger } = render('emoteSetMenu.lockedDuringDelete');

    expect(trigger.disabled).toBe(true);
    const reason = trigger.ownerDocument.getElementById(trigger.getAttribute('aria-describedby')!);
    expect(reason?.textContent).toContain('emoteSetMenu.lockedDuringDelete');
  });

  it('carries no reason and stays usable without a lock', () => {
    const { trigger } = render(null);

    expect(trigger.disabled).toBe(false);
    expect(trigger.getAttribute('aria-describedby')).toBeNull();
  });

  it('drops a choice made in a popover that was already open when the lock landed', () => {
    const { fixture } = render(null);
    const emitted: string[] = [];
    fixture.componentInstance.emoteSetIdChange.subscribe((id) => emitted.push(id));

    fixture.componentRef.setInput('lockedReasonKey', 'emoteSetMenu.lockedDuringDelete');
    fixture.detectChanges();
    fixture.componentInstance['select']('set-2');

    expect(emitted).toEqual([]);
  });
});
