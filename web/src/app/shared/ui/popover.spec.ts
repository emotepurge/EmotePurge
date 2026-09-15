import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import { Popover } from './popover';

/**
 * Realistic host: the `data-popover-anchor` wrapper sits around both the trigger and the popover,
 * exactly as `docs/UI-Designsprache.md` §7.1 prescribes and as `date-range-menu.ts` builds it. The
 * trigger toggles `open`; `closeCount` and `open` both react to the panel's `closed` output, so a
 * test can check either "was close requested" (`closeCount`) or "did the host actually remove the
 * panel" (`panel()` returning null) — the panel itself never does the removing.
 */
@Component({
  imports: [Popover],
  template: `
    <div data-popover-anchor>
      <button type="button" id="trigger" (click)="toggle()">Trigger</button>
      @if (open()) {
        <app-popover ariaLabel="Menu" (closed)="close()">
          <button type="button" id="panel-item">Inside</button>
        </app-popover>
      }
    </div>
    <button type="button" id="outside">Outside</button>
  `,
})
class WiredHost {
  readonly open = signal(false);
  closeCount = 0;

  toggle(): void {
    this.open.update((value) => !value);
  }

  close(): void {
    this.closeCount += 1;
    this.open.set(false);
  }
}

/**
 * Popover with no host-owned visibility at all: always rendered, and `closed` only counts —
 * nothing ever removes the panel. This is the shape that isolates the §7.1 contract itself
 * ("rendered = open, the panel never hides itself") from how a real host chooses to react to it.
 */
@Component({
  imports: [Popover],
  template: `
    <app-popover ariaLabel="Menu label" (closed)="closeCount = closeCount + 1">
      <button type="button" id="panel-item">Inside</button>
    </app-popover>
    <button type="button" id="outside">Outside</button>
  `,
})
class AlwaysOpenHost {
  closeCount = 0;
}

/**
 * No `data-popover-anchor` marker anywhere: exercises the `closest(...) ?? this.elementRef`
 * fallback, where the popover's own element stands in as the anchor.
 */
@Component({
  imports: [Popover],
  template: `
    <button type="button" id="trigger" (click)="toggle()">Trigger</button>
    @if (open()) {
      <app-popover ariaLabel="Menu" (closed)="close()">
        <button type="button" id="panel-item">Inside</button>
      </app-popover>
    }
    <button type="button" id="outside">Outside</button>
  `,
})
class UnanchoredHost {
  readonly open = signal(false);
  closeCount = 0;

  toggle(): void {
    this.open.update((value) => !value);
  }

  close(): void {
    this.closeCount += 1;
    this.open.set(false);
  }
}

function click(element: Element): void {
  element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
}

function escape(): void {
  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
}

interface HostView<T extends { closeCount: number }> {
  fixture: ComponentFixture<T>;
  host: T;
  trigger: HTMLButtonElement;
  outside: HTMLButtonElement;
  panel: () => HTMLElement | null;
  panelItem: () => HTMLButtonElement;
}

function renderWired(): HostView<WiredHost> {
  const fixture = TestBed.createComponent(WiredHost);
  fixture.detectChanges();
  const native: HTMLElement = fixture.nativeElement;
  return {
    fixture,
    host: fixture.componentInstance,
    trigger: native.querySelector('#trigger') as HTMLButtonElement,
    outside: native.querySelector('#outside') as HTMLButtonElement,
    panel: () => native.querySelector('[role="dialog"]'),
    panelItem: () => native.querySelector('#panel-item') as HTMLButtonElement,
  };
}

function renderUnanchored(): HostView<UnanchoredHost> {
  const fixture = TestBed.createComponent(UnanchoredHost);
  fixture.detectChanges();
  const native: HTMLElement = fixture.nativeElement;
  return {
    fixture,
    host: fixture.componentInstance,
    trigger: native.querySelector('#trigger') as HTMLButtonElement,
    outside: native.querySelector('#outside') as HTMLButtonElement,
    panel: () => native.querySelector('[role="dialog"]'),
    panelItem: () => native.querySelector('#panel-item') as HTMLButtonElement,
  };
}

describe('Popover', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WiredHost, AlwaysOpenHost, UnanchoredHost],
    }).compileComponents();
  });

  describe('outside-click and Escape dismissal (§7.1: emits closed, never hides itself)', () => {
    it('requests close on a click outside the anchor', () => {
      const view = renderWired();
      click(view.trigger);
      view.fixture.detectChanges();
      expect(view.panel()).not.toBeNull();

      click(view.outside);
      view.fixture.detectChanges();

      expect(view.host.closeCount).toBe(1);
      expect(view.panel()).toBeNull();
    });

    it('requests close on Escape', () => {
      const view = renderWired();
      click(view.trigger);
      view.fixture.detectChanges();

      escape();
      view.fixture.detectChanges();

      expect(view.host.closeCount).toBe(1);
      expect(view.panel()).toBeNull();
    });

    it('does not request close for a click on the trigger while the panel is already open', () => {
      // #trigger is itself a toggle (as it is in date-range-menu.ts), so a second click on it
      // does close the panel — but only via the host's own toggle, not via the anchor logic under
      // test here. What the contract promises is that the panel does not *additionally* ask to be
      // closed just because the click landed inside the anchor: closeCount must stay untouched.
      const view = renderWired();
      click(view.trigger);
      view.fixture.detectChanges();

      click(view.trigger);
      view.fixture.detectChanges();

      expect(view.host.closeCount).toBe(0);
    });

    it('does not request close for a click inside the panel content', () => {
      const view = renderWired();
      click(view.trigger);
      view.fixture.detectChanges();

      click(view.panelItem());
      view.fixture.detectChanges();

      expect(view.host.closeCount).toBe(0);
      expect(view.panel()).not.toBeNull();
    });

    it(
      'does not request close for the very click that opens it, even though the panel attaches ' +
        'its document listener mid-dispatch (the same-dispatch race the anchor marker exists to ' +
        'prevent: a listener added to an ancestor while a bubbling event is still in flight still ' +
        'fires for that event once it reaches that ancestor)',
      () => {
        const view = renderWired();
        // Angular's own (click) handler on #trigger runs first (toggling `open`); this listener,
        // registered after it on the same node, runs next and forces the popover to mount — and
        // its (document:click) host listener to attach — before the event bubbles past #trigger.
        view.trigger.addEventListener('click', () => view.fixture.detectChanges());

        click(view.trigger);
        view.fixture.detectChanges();

        expect(view.host.closeCount).toBe(0);
        expect(view.panel()).not.toBeNull();
      },
    );

    it(
      'negative control for the race above: run the identical sequence on a host whose trigger ' +
        'sits outside the popover (no anchor wrapper at all — UnanchoredHost) and it DOES request ' +
        'close. This proves the harness actually reproduces the same-dispatch race (the document ' +
        'listener is provably attached and observing this very click — otherwise closeCount would ' +
        'stay 0 here too, for the wrong reason) and that the positive test above is caused by the ' +
        'anchor check, not by the listener attaching too late to see the event at all',
      () => {
        const view = renderUnanchored();
        let panelMountedDuringDispatch: HTMLElement | null = null;
        // Identical trick as the positive test: force the popover to mount, and its
        // (document:click) host listener to attach, mid-dispatch of the very click that opens it.
        view.trigger.addEventListener('click', () => {
          view.fixture.detectChanges();
          panelMountedDuringDispatch = view.panel();
        });

        click(view.trigger);
        view.fixture.detectChanges();

        // Captured (not asserted) inside the listener: throwing there wouldn't fail the test, since
        // a listener exception doesn't propagate up through dispatchEvent() to this call stack.
        expect(panelMountedDuringDispatch).not.toBeNull();
        expect(view.host.closeCount).toBe(1);
      },
    );

    it('never removes its own dialog element — closing is the host’s decision, not the panel’s', () => {
      const fixture = TestBed.createComponent(AlwaysOpenHost);
      fixture.detectChanges();
      const native: HTMLElement = fixture.nativeElement;
      const panel = () => native.querySelector('[role="dialog"]');
      const outside = native.querySelector('#outside') as HTMLButtonElement;

      expect(panel()).not.toBeNull();

      click(outside);
      fixture.detectChanges();
      escape();
      fixture.detectChanges();

      expect(fixture.componentInstance.closeCount).toBe(2);
      // Rendered = open, unconditionally: two close requests landed and the panel is still there.
      expect(panel()).not.toBeNull();
    });
  });

  describe('fallback anchor (no data-popover-anchor wrapper)', () => {
    it('does not request close for a click inside its own element when nothing marks an anchor', () => {
      const view = renderUnanchored();
      click(view.trigger);
      view.fixture.detectChanges();

      click(view.panelItem());
      view.fixture.detectChanges();

      expect(view.host.closeCount).toBe(0);
      expect(view.panel()).not.toBeNull();
    });

    it('still requests close for a click outside its own element', () => {
      const view = renderUnanchored();
      click(view.trigger);
      view.fixture.detectChanges();

      click(view.outside);
      view.fixture.detectChanges();

      expect(view.host.closeCount).toBe(1);
      expect(view.panel()).toBeNull();
    });
  });

  describe('accessibility semantics', () => {
    it('exposes the panel as a dialog labelled by the ariaLabel input', () => {
      const fixture = TestBed.createComponent(AlwaysOpenHost);
      fixture.detectChanges();
      const panel = fixture.nativeElement.querySelector('[role="dialog"]') as HTMLElement;

      expect(panel).not.toBeNull();
      expect(panel.getAttribute('aria-label')).toBe('Menu label');
    });

    it('is a non-modal dialog: no aria-modal, matching the §7.1 demarcation from a CDK dialog', () => {
      const fixture = TestBed.createComponent(AlwaysOpenHost);
      fixture.detectChanges();
      const panel = fixture.nativeElement.querySelector('[role="dialog"]') as HTMLElement;

      expect(panel.hasAttribute('aria-modal')).toBe(false);
    });
  });
});
