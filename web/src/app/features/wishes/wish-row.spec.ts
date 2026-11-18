import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { beforeEach, describe, expect, it } from 'vitest';

import de from '../../../../public/i18n/de.json';
import type { Wish, WishUsage } from '../../core/wishes/wish.model';
import type { WishAction } from '../../core/wishes/wish-view';
import { WishRow } from './wish-row';
import type { WishUsageBlock } from './wish-usage';

const NOW = new Date('2026-11-18T12:00:00Z');

const ALL_ACTIONS: ReadonlySet<WishAction> = new Set<WishAction>([
  'add7tv',
  'adopt',
  'edit',
  'close',
  'retry',
  'extend',
  'keep',
  'remove',
  'reAdd',
]);

const LABEL = de.wishes.actions;

function wish(overrides: Partial<Wish> = {}): Wish {
  return {
    id: 7,
    sevenTvEmoteId: 'emote7',
    emoteName: 'peepoHappy',
    alias: 'peepoHappy',
    source: 'manual',
    status: 'active',
    statusReason: null,
    requesterLogin: 'viewer',
    durationDays: 14,
    createdAtUtc: '2026-11-01T10:00:00Z',
    approvedAtUtc: '2026-11-01T10:00:00Z',
    startsAtUtc: '2026-11-02T10:00:00Z',
    startsAtAuthoritative: true,
    probationStartsAtUtc: '2026-11-13T10:00:00Z',
    expiresAtUtc: '2026-11-16T10:00:00Z',
    endedAtUtc: null,
    slotId: 'slot-7',
    slotAddedAtUtc: '2026-11-02T10:00:00.123456Z',
    imageUrl: null,
    linkOrigin: 'auto',
    extensionCount: 0,
    permanent: false,
    botOwned: false,
    botWritePending: false,
    botNextAttemptAtUtc: null,
    removalQueuedAtUtc: null,
    mode: 'manual',
    ...overrides,
  };
}

describe('WishRow', () => {
  let fixture: ComponentFixture<WishRow>;
  let emitted: WishAction[];

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [
        WishRow,
        TranslocoTestingModule.forRoot({
          langs: { de },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
    });
  });

  function render(
    value: Wish,
    inputs: {
      offered?: ReadonlySet<WishAction>;
      coarse?: boolean;
      sevenTvLocked?: boolean;
      lockDescriptionId?: string | null;
      busy?: boolean;
      now?: Date;
      usage?: WishUsageBlock | null;
    } = {},
  ): void {
    fixture = TestBed.createComponent(WishRow);
    fixture.componentRef.setInput('wish', value);
    fixture.componentRef.setInput('now', inputs.now ?? NOW);
    fixture.componentRef.setInput('offered', inputs.offered ?? ALL_ACTIONS);
    fixture.componentRef.setInput('coarse', inputs.coarse ?? false);
    fixture.componentRef.setInput('sevenTvLocked', inputs.sevenTvLocked ?? false);
    fixture.componentRef.setInput('lockDescriptionId', inputs.lockDescriptionId ?? null);
    fixture.componentRef.setInput('busy', inputs.busy ?? false);
    fixture.componentRef.setInput('usage', inputs.usage ?? null);
    emitted = [];
    fixture.componentInstance.action.subscribe((action) => emitted.push(action));
    fixture.detectChanges();
  }

  function buttons(): HTMLButtonElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('button'));
  }

  function names(): string[] {
    return buttons().map((button) => button.textContent?.trim() ?? '');
  }

  function button(label: string): HTMLButtonElement {
    const found = buttons().find((candidate) => candidate.textContent?.trim() === label);
    if (!found) {
      throw new Error(`No button "${label}" in ${JSON.stringify(names())}`);
    }
    return found;
  }

  it('renders the action table’s actions for the state, in its order, as named buttons', () => {
    render(wish({ status: 'presented', statusReason: 'expired' }));

    expect(names()).toEqual([LABEL.extend, LABEL.keep, LABEL.remove]);
  });

  it('renders only what the page offers', () => {
    render(wish({ status: 'approved', expiresAtUtc: null, slotId: null }), {
      offered: new Set<WishAction>(['close']),
    });

    expect(names()).toEqual([LABEL.close]);
  });

  it('gives out the chosen action', () => {
    render(wish({ status: 'failed', slotId: null, expiresAtUtc: null }));

    button(LABEL.retry).click();
    button(LABEL.close).click();

    expect(emitted).toEqual(['retry', 'close']);
  });

  it('labels its action group with the wish’s alias', () => {
    render(wish({ alias: 'catJAM' }));

    const group: HTMLElement = fixture.nativeElement.querySelector('[role="group"]');
    const label = fixture.nativeElement.querySelector(`#${group.getAttribute('aria-labelledby')}`);
    expect(label?.textContent?.trim()).toBe('catJAM');
  });

  it('drops the 7TV actions entirely on a coarse pointer', () => {
    render(wish({ status: 'approved', slotId: null, expiresAtUtc: null }), { coarse: true });

    expect(names()).toEqual([LABEL.adopt, LABEL.edit, LABEL.close]);
  });

  it('keeps a locked 7TV action focusable, describes it through the page line and emits nothing', () => {
    render(wish({ status: 'active' }), {
      sevenTvLocked: true,
      lockDescriptionId: 'wishes-run-lock',
    });

    const remove = button(LABEL.remove);
    expect(remove.disabled).toBe(false);
    expect(remove.getAttribute('aria-disabled')).toBe('true');
    expect(remove.getAttribute('aria-describedby')).toBe('wishes-run-lock');

    remove.click();
    expect(emitted).toEqual([]);

    // The booking actions do not hang on the arbiter.
    const extend = button(LABEL.extend);
    expect(extend.getAttribute('aria-disabled')).toBeNull();
    extend.click();
    expect(emitted).toEqual(['extend']);
  });

  it('disables every button of the row while its own request runs', () => {
    render(wish({ status: 'active' }), { busy: true });

    expect(buttons().every((candidate) => candidate.disabled)).toBe(true);
    button(LABEL.keep).click();
    expect(emitted).toEqual([]);
  });

  it('renders an unknown state neutrally, without actions', () => {
    render(wish({ status: 'something_new' }));

    expect(names()).toEqual([]);
    expect(fixture.nativeElement.textContent).toContain(de.wishes.status.unknown);
  });

  it('renders an unknown reason as the neutral line without changing the actions', () => {
    render(wish({ status: 'presented', statusReason: 'brand_new_reason' }));

    expect(fixture.nativeElement.textContent).toContain(de.wishes.reason.other);
    expect(names()).toEqual([LABEL.extend, LABEL.keep, LABEL.remove]);
  });

  it('marks probation only once the probation start has passed', () => {
    render(wish({ status: 'active', probationStartsAtUtc: '2026-11-18T12:00:01Z' }));
    expect(fixture.nativeElement.textContent).not.toContain(de.wishes.row.probation);

    render(wish({ status: 'active', probationStartsAtUtc: '2026-11-18T11:59:59Z' }));
    expect(fixture.nativeElement.textContent).toContain(de.wishes.row.probation);
  });

  it('points to doing it in 7TV beside an approved wish, and on a phone beside a due one', () => {
    const hint = de.wishes.row.sevenTvHint;

    render(wish({ status: 'approved', slotId: null, expiresAtUtc: null }));
    expect(fixture.nativeElement.textContent).toContain(hint);

    render(wish({ status: 'presented', statusReason: 'expired' }));
    expect(fixture.nativeElement.textContent).not.toContain(hint);

    render(wish({ status: 'presented', statusReason: 'expired' }), { coarse: true });
    expect(fixture.nativeElement.textContent).toContain(hint);
  });

  it('links to the emote’s 7TV page in a new tab', () => {
    render(wish());

    const link: HTMLAnchorElement = fixture.nativeElement.querySelector('a');
    expect(link.getAttribute('href')).toBe('https://7tv.app/emotes/emote7');
    expect(link.getAttribute('target')).toBe('_blank');
  });

  describe('usage block', () => {
    const usage: WishUsage = {
      windowFromUtc: '2026-11-11T12:00:00Z',
      windowToUtc: '2026-11-18T12:00:00Z',
      total: 234,
      perDayMean: 17,
      rank: 3,
      rankOf: 12,
      coverageGap: false,
      daily: [
        { date: '2026-11-16', useCount: 4 },
        { date: '2026-11-17', useCount: 9 },
        { date: '2026-11-18', useCount: 2 },
      ],
    };

    function line(): SVGPolylineElement | null {
      return fixture.nativeElement.querySelector('app-usage-sparkline polyline');
    }

    it('shows sum, mean, rank and the window beside a line of the same series', () => {
      render(wish({ startsAtUtc: '2026-10-01T00:00:00Z' }), {
        usage: { state: 'ready', usage },
      });

      const text: string = fixture.nativeElement.textContent;
      expect(text).toContain('234 Nutzungen');
      expect(text).toContain('Ø 17 pro Tag');
      expect(text).toContain('Rang 3 von 12');
      expect(text).toContain('letzte 7 Tage');
      expect(line()).not.toBeNull();
    });

    it('says "since the start" when the window opens at the wish start', () => {
      render(wish({ startsAtUtc: usage.windowFromUtc }), { usage: { state: 'ready', usage } });

      expect(fixture.nativeElement.textContent).toContain(de.wishes.usage.window.sinceStart);
    });

    it('names a rank only with both values, and a counting gap when there is one', () => {
      render(wish(), {
        usage: { state: 'ready', usage: { ...usage, rank: null, rankOf: null, coverageGap: true } },
      });

      const text: string = fixture.nativeElement.textContent;
      expect(text).not.toContain('Rang');
      expect(text).toContain(de.wishes.usage.gap);
    });

    it('shows the numbers without a line when the window has one or two days', () => {
      render(wish(), {
        usage: { state: 'ready', usage: { ...usage, daily: usage.daily?.slice(0, 2) } },
      });

      expect(fixture.nativeElement.textContent).toContain('234 Nutzungen');
      expect(line()).toBeNull();
    });

    it('shows no block without usage, and "later" when rate limited', () => {
      render(wish());
      expect(fixture.nativeElement.textContent).not.toContain('Nutzung');

      render(wish(), { usage: { state: 'limited', usage: null } });
      expect(fixture.nativeElement.textContent).toContain(de.wishes.usage.limited);
      expect(line()).toBeNull();
    });
  });
});
