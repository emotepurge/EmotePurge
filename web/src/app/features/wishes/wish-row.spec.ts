import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { beforeEach, describe, expect, it } from 'vitest';

import de from '../../../../public/i18n/de.json';
import type { Wish, WishUsage } from '../../core/wishes/wish.model';
import type { WishAction } from '../../core/wishes/wish-view';
import type { WishOperationView } from './wish-operation';
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
  let retries: number;

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
      operation?: WishOperationView | null;
      removeBlockKey?: string | null;
      previewName?: string | null;
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
    fixture.componentRef.setInput('operation', inputs.operation ?? null);
    fixture.componentRef.setInput('removeBlockKey', inputs.removeBlockKey ?? null);
    fixture.componentRef.setInput('previewName', inputs.previewName ?? null);
    emitted = [];
    retries = 0;
    fixture.componentInstance.action.subscribe((action) => emitted.push(action));
    fixture.componentInstance.retryOperation.subscribe(() => retries++);
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

  describe('its own 7TV operation', () => {
    function region(): HTMLElement {
      return fixture.nativeElement.querySelector('[role="status"]');
    }

    it('keeps an empty status region mounted while there is no operation', () => {
      render(wish({ status: 'approved', slotId: null, expiresAtUtc: null }));

      expect(region()).not.toBeNull();
      expect(region().textContent?.trim()).toBe('');
    });

    it('speaks the operation in the mounted region, a stopped one behind its lead', () => {
      const approved = wish({ status: 'approved', slotId: null, expiresAtUtc: null });
      render(approved);

      fixture.componentRef.setInput('operation', {
        tone: 'problem',
        leadKey: 'wishes.start.nothingChanged',
        key: 'wishes.start.wishChanged',
      } satisfies WishOperationView);
      fixture.detectChanges();

      const spoken = region().textContent?.replace(/\s+/g, ' ').trim();
      expect(spoken).toBe(`${de.wishes.start.nothingChanged} ${de.wishes.start.wishChanged}`);
    });
  });

  it('leaves a line the page speaks out of its own status region but keeps its text readable', () => {
    render(wish({ status: 'approved', slotId: null, expiresAtUtc: null }));
    fixture.componentRef.setInput('operation', {
      tone: 'problem',
      leadKey: null,
      key: 'wishes.remove.notBooked',
      spokenByPage: true,
    } satisfies WishOperationView);
    fixture.detectChanges();

    const region = fixture.nativeElement.querySelector('[role="status"]') as HTMLElement;
    expect(region.textContent?.trim()).toBe('');
    const line = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('p')).find(
      (p) => p.textContent?.includes(de.wishes.remove.notBooked),
    );
    expect(line).toBeDefined();
    expect(line?.getAttribute('aria-hidden')).toBeNull();
  });

  it('offers the add only beside an approved wish and only on a fine pointer', () => {
    const approved = wish({ status: 'approved', slotId: null, expiresAtUtc: null });
    render(approved);
    expect(names()[0]).toBe(LABEL.add7tv);
    button(LABEL.add7tv).click();
    expect(emitted).toEqual(['add7tv']);

    render(approved, { coarse: true });
    expect(names()).not.toContain(LABEL.add7tv);
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

  // T9 (plan 2.6): "Entfernen" locked with its reason, and the follow-up of an owed report.
  describe('the removal', () => {
    const BLOCK = 'wishes.remove.blocked.provisional';

    it('keeps a locked removal focusable, points it at its reason and emits nothing', () => {
      render(wish(), { removeBlockKey: BLOCK });

      const remove = button(LABEL.remove);
      expect(remove.getAttribute('aria-disabled')).toBe('true');
      const reason = fixture.nativeElement.querySelector(
        `#${remove.getAttribute('aria-describedby')}`,
      );
      expect(reason?.textContent?.trim()).toBe(de.wishes.remove.blocked.provisional);
      remove.click();
      expect(emitted).toEqual([]);
      // Only the removal is locked.
      button(LABEL.extend).click();
      expect(emitted).toEqual(['extend']);
    });

    it('names both the page line and its own reason while a run also holds the start', () => {
      render(wish(), {
        removeBlockKey: BLOCK,
        sevenTvLocked: true,
        lockDescriptionId: 'wishes-run-lock',
      });

      expect(button(LABEL.remove).getAttribute('aria-describedby')).toBe(
        'wishes-run-lock wish-remove-block-7',
      );
    });

    it('says nothing about the lock where the removal is not on screen', () => {
      render(wish(), { removeBlockKey: BLOCK, coarse: true });

      expect(names()).not.toContain(LABEL.remove);
      expect(fixture.nativeElement.textContent).not.toContain(de.wishes.remove.blocked.provisional);
    });

    it('offers the operation’s follow-up as a button and gives it out', () => {
      render(wish(), {
        operation: {
          tone: 'problem',
          leadKey: null,
          key: 'wishes.remove.reportPending',
          retryKey: 'wishes.remove.retryReport',
        },
      });

      button(de.wishes.remove.retryReport).click();
      expect(retries).toBe(1);
      expect(emitted).toEqual([]);
    });
  });

  describe('an open wish captured in chat (K6)', () => {
    const K6_ACTIONS: ReadonlySet<WishAction> = new Set<WishAction>([
      ...ALL_ACTIONS,
      'approve',
      'reject',
      'unlink',
    ]);

    function open(overrides: Partial<Wish> = {}): Wish {
      return wish({
        status: 'pending',
        source: 'irc',
        linkOrigin: null,
        sevenTvEmoteId: '',
        emoteName: '',
        alias: '',
        approvedAtUtc: null,
        startsAtUtc: null,
        startsAtAuthoritative: false,
        probationStartsAtUtc: null,
        expiresAtUtc: null,
        slotId: null,
        slotAddedAtUtc: null,
        createdAtUtc: '2026-11-16T09:00:00Z',
        requesterLogin: 'viewer1',
        requestText: 'catJAM pls',
        requestedName: 'catJAM',
        matchKind: 'name',
        rewardLabel: 'Emote wünschen',
        ...overrides,
      });
    }

    function group(): HTMLElement {
      return fixture.nativeElement.querySelector('[role="group"]') as HTMLElement;
    }

    function groupName(): string {
      const id = group().getAttribute('aria-labelledby') ?? '';
      return fixture.nativeElement.querySelector(`#${id}`)?.textContent?.trim() ?? '';
    }

    it.each([
      [null, [LABEL.approve, LABEL.adopt, LABEL.edit, LABEL.reject]],
      ['unparsed', [LABEL.approve, LABEL.adopt, LABEL.reject]],
      ['ambiguous_match', [LABEL.adopt, LABEL.approve, LABEL.reject]],
      ['already_in_set', [LABEL.adopt, LABEL.reject]],
      ['already_running', [LABEL.approveExtend, LABEL.reject]],
      ['something_new', [LABEL.approve, LABEL.adopt, LABEL.edit, LABEL.reject]],
    ])('offers, for the hint %s, the actions of the table', (hint, expected) => {
      render(open({ statusReason: hint }), { offered: K6_ACTIONS });

      expect(names()).toEqual(expected);
    });

    it('names the action group after what the viewer typed while no emote is known', () => {
      render(open(), { offered: K6_ACTIONS });

      expect(groupName()).toBe('catJAM');
    });

    it('names it after the redemption text without a typed name, and neutrally without either', () => {
      render(open({ requestedName: null, matchKind: 'none', requestText: 'irgendwas lustiges' }), {
        offered: K6_ACTIONS,
      });
      expect(groupName()).toBe('irgendwas lustiges');

      render(open({ requestedName: null, requestText: null, requesterLogin: null }), {
        offered: K6_ACTIONS,
      });
      expect(groupName()).toBe(de.wishes.pending.untitled);
    });

    it('takes the 7TV default name for a link wish without alias', () => {
      const link = open({
        sevenTvEmoteId: '01F6MZGCNG000255K4X1K7NTHR',
        requestedName: null,
        matchKind: 'link',
        requestText: 'https://7tv.app/emotes/01F6MZGCNG000255K4X1K7NTHR',
      });
      render(link, { offered: K6_ACTIONS, previewName: 'catJAM' });

      expect(groupName()).toBe('catJAM');
    });

    it('shows neither a picture nor a 7TV link without an emote', () => {
      render(open(), { offered: K6_ACTIONS });

      expect(fixture.nativeElement.querySelector('app-wish-image')).toBeNull();
      expect(fixture.nativeElement.querySelector('a')).toBeNull();
    });

    it('keeps picture and 7TV link once the wish knows its emote', () => {
      render(open({ sevenTvEmoteId: 'emote7', matchKind: 'link' }), { offered: K6_ACTIONS });

      expect(fixture.nativeElement.querySelector('app-wish-image')).not.toBeNull();
      expect(fixture.nativeElement.querySelector('a')?.getAttribute('href')).toContain('emote7');
    });

    it('renders the redemption text as plain text, an address or markup included', () => {
      const text = '<b>hi</b> https://evil.example/x <script>alert(1)</script>';
      render(open({ requestText: text }), { offered: K6_ACTIONS });

      const line = fixture.nativeElement.querySelector('q') as HTMLElement;
      expect(line.textContent).toBe(text);
      expect(line.querySelector('b, script, a')).toBeNull();
      expect(fixture.nativeElement.querySelector('a[href*="evil.example"]')).toBeNull();
    });

    it('keeps the full text of an overlong redemption in its tooltip', () => {
      const text = 'x'.repeat(500);
      render(open({ requestText: text }), { offered: K6_ACTIONS });

      expect(fixture.nativeElement.querySelector('[title]')?.getAttribute('title')).toBe(text);
    });

    it('says the hint as a sentence', () => {
      render(open({ statusReason: 'already_running' }), { offered: K6_ACTIONS });

      expect(fixture.nativeElement.textContent).toContain(de.wishes.pending.hint.already_running);
    });

    it('names viewer, reward and age, and leaves the reward out once it was forgotten', () => {
      render(open(), { offered: K6_ACTIONS });
      const text = fixture.nativeElement.textContent as string;
      expect(text).toContain('gewünscht von viewer1');
      expect(text).toContain('über „Emote wünschen“');
      expect(text).toContain('vor 2 Tagen eingelöst');

      render(open({ rewardLabel: null }), { offered: K6_ACTIONS });
      expect(fixture.nativeElement.textContent).not.toContain('über „');
    });

    it('offers "Zuordnung lösen" on an automatic chat link only', () => {
      render(wish({ source: 'irc', linkOrigin: 'auto' }), { offered: K6_ACTIONS });
      expect(names()).toContain(LABEL.unlink);

      render(wish({ source: 'irc', linkOrigin: 'adopted' }), { offered: K6_ACTIONS });
      expect(names()).not.toContain(LABEL.unlink);
    });

    it('keeps approve and reject on a coarse pointer: neither writes to 7TV', () => {
      render(open(), { offered: K6_ACTIONS, coarse: true });

      expect(names()).toEqual([LABEL.approve, LABEL.adopt, LABEL.edit, LABEL.reject]);
    });

    it('gives out the chosen action', () => {
      render(open(), { offered: K6_ACTIONS });

      button(LABEL.reject).click();
      button(LABEL.approve).click();
      expect(emitted).toEqual(['reject', 'approve']);
    });
  });
});
