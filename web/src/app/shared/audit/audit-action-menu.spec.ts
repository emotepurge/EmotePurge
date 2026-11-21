import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { describe, expect, it } from 'vitest';

import de from '../../../../public/i18n/de.json';
import { AuditActionMenu } from './audit-action-menu';

function render(value = '') {
  TestBed.configureTestingModule({
    imports: [
      AuditActionMenu,
      TranslocoTestingModule.forRoot({
        langs: { de },
        preloadLangs: true,
        translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
      }),
    ],
  });
  const fixture = TestBed.createComponent(AuditActionMenu);
  fixture.componentRef.setInput('actions', [
    'channel.join',
    'voteSession.create',
    'wish.rewardAdd',
    'wish.create',
  ]);
  fixture.componentRef.setInput('value', value);
  fixture.componentRef.setInput('ariaLabel', 'Nach Aktion filtern');
  fixture.componentRef.setInput('allLabelKey', 'admin.audit.filter.all');
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const emitted: string[] = [];
  fixture.componentInstance.valueChange.subscribe((v) => emitted.push(v));
  const trigger = host.querySelector('button')!;
  const radios = () => Array.from(host.querySelectorAll<HTMLButtonElement>('[role="radio"]'));
  const open = () => {
    trigger.click();
    fixture.detectChanges();
  };
  const key = (el: HTMLElement, k: string) => {
    el.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true }));
    fixture.detectChanges();
  };
  return { fixture, host, trigger, radios, open, key, emitted };
}

describe('AuditActionMenu', () => {
  it('names the current setting on the trigger, with the filter label as context', () => {
    const { trigger } = render('wish.create');

    expect(trigger.textContent).toContain('Wunsch nachgetragen');
    expect(trigger.getAttribute('aria-label')).toContain('Nach Aktion filtern');
    expect(trigger.getAttribute('aria-label')).toContain('Wunsch nachgetragen');
  });

  it('shows "all" while nothing is selected', () => {
    const { trigger } = render('');

    expect(trigger.textContent).toContain('Alle');
  });

  it('offers a radiogroup with "all" first, and marks the selection', () => {
    const { host, trigger, radios, open } = render('channel.join');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');

    open();

    expect(trigger.getAttribute('aria-expanded')).toBe('true');
    expect(host.querySelector('[role="radiogroup"]')).not.toBeNull();
    expect(radios()).toHaveLength(5);
    expect(radios()[0].textContent).toContain('Alle');
    expect(radios().filter((r) => r.getAttribute('aria-checked') === 'true')).toHaveLength(1);
    expect(radios()[1].getAttribute('aria-checked')).toBe('true');
  });

  it('keeps only the selected option in the tab order', () => {
    const { radios, open } = render('voteSession.create');

    open();

    expect(radios().map((r) => r.tabIndex)).toEqual([-1, -1, 0, -1, -1]);
  });

  it('moves focus on arrow keys without committing, and wraps around', () => {
    const { fixture, radios, open, key, emitted } = render('');
    open();

    key(radios()[0], 'ArrowDown');
    expect(document.activeElement).toBe(radios()[1]);
    expect(radios()[1].tabIndex).toBe(0);

    key(radios()[0], 'ArrowUp');
    expect(document.activeElement).toBe(radios()[4]);
    expect(emitted).toEqual([]);
    fixture.destroy();
  });

  it('commits on click (Enter and Space fire click) and closes, returning focus to the trigger', () => {
    const { fixture, trigger, radios, open, emitted } = render('');
    document.body.appendChild(fixture.nativeElement);
    open();
    radios()[1].focus();

    radios()[1].click();
    fixture.detectChanges();

    expect(emitted).toEqual(['channel.join']);
    expect(radios()).toHaveLength(0);
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(trigger);
    fixture.nativeElement.remove();
  });

  it('can commit "all" again', () => {
    const { radios, open, emitted } = render('channel.join');
    open();

    radios()[0].click();

    expect(emitted).toEqual(['']);
  });

  it('closes on Escape and returns focus to the trigger', () => {
    const { fixture, trigger, radios, open, key, emitted } = render('');
    document.body.appendChild(fixture.nativeElement);
    open();
    radios()[0].focus();

    key(radios()[0], 'Escape');

    expect(radios()).toHaveLength(0);
    expect(document.activeElement).toBe(trigger);
    expect(emitted).toEqual([]);
    fixture.nativeElement.remove();
  });
});
