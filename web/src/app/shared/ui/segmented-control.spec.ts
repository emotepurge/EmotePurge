import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { SegmentedControl, SegmentedControlOption } from './segmented-control';

// Only the keys this primitive translates — not the full app translation file.
const DE_TRANSLATIONS = {
  range: {
    day: 'Tag',
    week: 'Woche',
    month: 'Monat',
  },
};

const OPTIONS: SegmentedControlOption[] = [
  { value: 'day', labelKey: 'range.day' },
  { value: 'week', labelKey: 'range.week' },
  { value: 'month', labelKey: 'range.month' },
];

@Component({
  imports: [SegmentedControl],
  template: `
    <app-segmented-control
      [options]="options"
      [ariaLabel]="ariaLabel"
      [value]="value()"
      (valueChange)="value.set($event)"
    />
  `,
})
class Host {
  options: SegmentedControlOption[] = OPTIONS;
  ariaLabel = 'Zeitraum';
  readonly value = signal('day');
}

interface Harness {
  fixture: ComponentFixture<Host>;
  host: Host;
  group: HTMLElement;
  radios: () => HTMLButtonElement[];
}

describe('SegmentedControl', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        Host,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(initialValue: string): Harness {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.value.set(initialValue);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;
    const group = host.querySelector<HTMLElement>('[role="radiogroup"]');
    if (!group) {
      throw new Error('radiogroup not found');
    }
    return {
      fixture,
      host: fixture.componentInstance,
      group,
      radios: () => Array.from(group.querySelectorAll<HTMLButtonElement>('[role="radio"]')),
    };
  }

  function pressKey(target: HTMLElement, key: string): void {
    target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
  }

  it('makes exactly one option tabbable — the selected one — and pulls the rest out of tab order', () => {
    const { radios } = render('week');

    expect(radios().map((radio) => radio.tabIndex)).toEqual([-1, 0, -1]);
  });

  it('falls the tabstop back to the first option once the value matches none of them', () => {
    // The fallback branch (SegmentedControl.tabIndexFor): stays invisible whenever some option
    // matches the value, which is every case above — it only shows itself once the value names
    // something the option list doesn't carry.
    const { radios } = render('unknown-value');

    expect(radios().map((radio) => radio.tabIndex)).toEqual([0, -1, -1]);
  });

  it('reflects aria-checked from the value alone, true on the match and false on the rest', () => {
    const { radios } = render('month');

    expect(radios().map((radio) => radio.getAttribute('aria-checked'))).toEqual([
      'false',
      'false',
      'true',
    ]);
  });

  it('leaves every option unchecked during the fallback, even though one of them is tabbable', () => {
    const { radios } = render('unknown-value');

    expect(radios().map((radio) => radio.getAttribute('aria-checked'))).toEqual([
      'false',
      'false',
      'false',
    ]);
  });

  it('moves selection and focus forward on ArrowRight/ArrowDown, wrapping past the last option', () => {
    const { fixture, host, radios } = render('day');

    pressKey(radios()[0], 'ArrowRight');
    fixture.detectChanges();
    expect(host.value()).toBe('week');
    expect(document.activeElement).toBe(radios()[1]);

    pressKey(radios()[1], 'ArrowDown');
    fixture.detectChanges();
    expect(host.value()).toBe('month');
    expect(document.activeElement).toBe(radios()[2]);

    // Wrap-around: one more step forward from the last option lands back on the first.
    pressKey(radios()[2], 'ArrowRight');
    fixture.detectChanges();
    expect(host.value()).toBe('day');
    expect(document.activeElement).toBe(radios()[0]);
  });

  it('moves selection and focus backward on ArrowLeft/ArrowUp, wrapping past the first option', () => {
    const { fixture, host, radios } = render('month');

    pressKey(radios()[2], 'ArrowLeft');
    fixture.detectChanges();
    expect(host.value()).toBe('week');
    expect(document.activeElement).toBe(radios()[1]);

    pressKey(radios()[1], 'ArrowUp');
    fixture.detectChanges();
    expect(host.value()).toBe('day');
    expect(document.activeElement).toBe(radios()[0]);

    // Wrap-around: one more step backward from the first option lands on the last.
    pressKey(radios()[0], 'ArrowLeft');
    fixture.detectChanges();
    expect(host.value()).toBe('month');
    expect(document.activeElement).toBe(radios()[2]);
  });

  it('emits the clicked option through the value output', () => {
    const { fixture, host, radios } = render('day');

    radios()[2].click();
    fixture.detectChanges();

    expect(host.value()).toBe('month');
  });

  it('gives the radiogroup the given accessible name', () => {
    const { group } = render('day');

    expect(group.getAttribute('aria-label')).toBe('Zeitraum');
  });

  it('leaves a non-arrow key alone instead of swallowing it', () => {
    // Guards against a regression that calls preventDefault() for every key: that would trap Tab
    // inside the group and break the "the group is one tab stop" contract from the roving-tabindex
    // tests above.
    const { fixture, host, radios } = render('day');

    const event = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
    radios()[0].dispatchEvent(event);
    fixture.detectChanges();

    expect(host.value()).toBe('day');
    expect(event.defaultPrevented).toBe(false);
  });
});
