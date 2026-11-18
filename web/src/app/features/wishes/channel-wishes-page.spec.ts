import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import de from '../../../../public/i18n/de.json';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { LIVE_CONNECTION_RELEASE_FACTORY } from '../../core/live/live-connection-release.factory';
import { CHANNEL_RELOAD_DEBOUNCE_MS } from '../../core/live/live-reload';
import { PagedResult } from '../../core/models/paged-result.model';
import type { Wish, WishDetail, WishListView } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { ChannelWishesPage, WISH_SAFETY_RELOAD_MS } from './channel-wishes-page';

const READY_STATE_OPEN = 1;
const READY_STATE_CONNECTING = 0;

/** jsdom ships no EventSource; the same stand-in as the live specs. */
class FakeEventSource {
  static instances: FakeEventSource[] = [];

  readyState = READY_STATE_OPEN;
  onopen: (() => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onerror: (() => void) | null = null;

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  close(): void {
    this.readyState = 2;
  }

  emit(type: string): void {
    this.onmessage?.({ data: JSON.stringify({ type }) } as MessageEvent);
  }

  /** The browser's own reconnect of this EventSource: an error while connecting, then open. */
  reopen(): void {
    this.readyState = READY_STATE_CONNECTING;
    this.onerror?.();
    this.readyState = READY_STATE_OPEN;
    this.onopen?.();
  }
}

interface ListCall {
  channel: string;
  view: WishListView;
  page: number;
  answer: Subject<PagedResult<Wish>>;
}

interface GetCall {
  channel: string;
  id: number;
  answer: Subject<WishDetail>;
}

function wish(id: number, overrides: Partial<Wish> = {}): Wish {
  return {
    id,
    sevenTvEmoteId: `emote${id}`,
    emoteName: `name${id}`,
    alias: `alias${id}`,
    source: 'manual',
    status: 'active',
    statusReason: null,
    requesterLogin: null,
    durationDays: 14,
    createdAtUtc: '2026-11-01T10:00:00Z',
    approvedAtUtc: '2026-11-01T10:00:00Z',
    startsAtUtc: '2026-11-02T10:00:00Z',
    startsAtAuthoritative: true,
    probationStartsAtUtc: null,
    expiresAtUtc: '2026-12-16T10:00:00Z',
    endedAtUtc: null,
    slotId: `slot-${id}`,
    slotAddedAtUtc: '2026-11-02T10:00:00Z',
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

function page(
  items: Wish[],
  options: { page?: number; totalPages?: number } = {},
): PagedResult<Wish> {
  const totalPages = options.totalPages ?? (items.length > 0 ? 1 : 0);
  return {
    items,
    page: options.page ?? 1,
    pageSize: 20,
    totalCount: Math.max(items.length, totalPages * 20 - 10),
    totalPages,
  };
}

function setVisibility(state: DocumentVisibilityState): void {
  Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
  document.dispatchEvent(new Event('visibilitychange'));
}

describe('ChannelWishesPage', () => {
  let listCalls: ListCall[];
  let getCalls: GetCall[];
  let actions: { name: string; id: number; answer: Subject<Wish> }[];
  let dialogs: Subject<boolean>[];
  let openDialog: ReturnType<typeof vi.fn>;
  const fixtures: ComponentFixture<ChannelWishesPage>[] = [];

  beforeEach(() => {
    vi.useFakeTimers();
    FakeEventSource.instances = [];
    listCalls = [];
    getCalls = [];
    actions = [];
    dialogs = [];
    openDialog = vi.fn(() => {
      const closed = new Subject<boolean>();
      dialogs.push(closed);
      return { closed };
    });
    // jsdom has no layout, so the pager's reposition on a page change needs a stand-in.
    Element.prototype.scrollIntoView = vi.fn();

    const action = (name: string) =>
      vi.fn((_channel: string, id: number) => {
        const answer = new Subject<Wish>();
        actions.push({ name, id, answer });
        return answer;
      });

    TestBed.configureTestingModule({
      imports: [
        ChannelWishesPage,
        TranslocoTestingModule.forRoot({
          langs: { de },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        {
          provide: WishService,
          useValue: {
            list: vi.fn((channel: string, view: WishListView, pageNumber: number) => {
              const answer = new Subject<PagedResult<Wish>>();
              listCalls.push({ channel, view, page: pageNumber, answer });
              return answer;
            }),
            get: vi.fn((channel: string, id: number) => {
              const answer = new Subject<WishDetail>();
              getCalls.push({ channel, id, answer });
              return answer;
            }),
            extend: action('extend'),
            retry: action('retry'),
            keep: action('keep'),
            close: action('close'),
          },
        },
        { provide: Dialog, useValue: { open: openDialog } },
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
        { provide: LIVE_CONNECTION_RELEASE_FACTORY, useValue: vi.fn() },
      ],
    });
  });

  afterEach(() => {
    fixtures.splice(0).forEach((fixture) => fixture.destroy());
    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
    delete (Element.prototype as Partial<Element>).scrollIntoView;
    vi.useRealTimers();
  });

  function create(channelName = 'Chan', wishParam?: string): ComponentFixture<ChannelWishesPage> {
    const fixture = TestBed.createComponent(ChannelWishesPage);
    fixtures.push(fixture);
    fixture.componentRef.setInput('channelName', channelName);
    if (wishParam !== undefined) {
      fixture.componentRef.setInput('wish', wishParam);
    }
    fixture.detectChanges();
    return fixture;
  }

  function calls(view: WishListView, channel = 'chan'): ListCall[] {
    return listCalls.filter((call) => call.view === view && call.channel === channel);
  }

  function last(view: WishListView, channel = 'chan'): ListCall {
    const found = calls(view, channel);
    expect(found.length).toBeGreaterThan(0);
    return found[found.length - 1];
  }

  /** Answers the latest open and running requests, the way the page sees on entry. */
  function settle(
    fixture: ComponentFixture<ChannelWishesPage>,
    open: Wish[] = [],
    running: Wish[] = [],
    channel = 'chan',
  ): void {
    last('open', channel).answer.next(page(open));
    last('running', channel).answer.next(page(running));
    fixture.detectChanges();
  }

  function close(dialog: Subject<boolean>, confirmed: boolean): void {
    dialog.next(confirmed);
    dialog.complete();
  }

  function section(fixture: ComponentFixture<ChannelWishesPage>, id: string): HTMLElement {
    return fixture.nativeElement.querySelector(`section[aria-labelledby="wishes-${id}-title"]`);
  }

  function rowIds(fixture: ComponentFixture<ChannelWishesPage>, id: string): number[] {
    const host = section(fixture, id);
    return host
      ? Array.from(host.querySelectorAll('[data-wish-id]')).map((row) =>
          Number(row.getAttribute('data-wish-id')),
        )
      : [];
  }

  function alerts(fixture: ComponentFixture<ChannelWishesPage>, id: string): string[] {
    return Array.from(section(fixture, id).querySelectorAll('[role="alert"]')).map(
      (alert) => alert.textContent?.trim() ?? '',
    );
  }

  function buttonIn(host: HTMLElement, label: string): HTMLButtonElement {
    const found = Array.from(host.querySelectorAll('button')).find(
      (button) => button.textContent?.trim() === label,
    );
    if (!found) {
      throw new Error(`No button "${label}"`);
    }
    return found as HTMLButtonElement;
  }

  function streamOf(channel: string): FakeEventSource {
    const found = FakeEventSource.instances.filter(
      (source) => source.url === `/api/channels/${channel}/live`,
    );
    expect(found.length).toBeGreaterThan(0);
    return found[found.length - 1];
  }

  it('loads "needs action" and "running" for the normalised channel on entry, "ended" not yet', () => {
    create('Chan');

    expect(calls('open').map((call) => call.page)).toEqual([1]);
    expect(calls('running').map((call) => call.page)).toEqual([1]);
    expect(calls('ended')).toEqual([]);
  });

  describe('which answer counts (Sol-P2-8)', () => {
    it('drops the older of two answers of one view when it arrives last', () => {
      const fixture = create();
      settle(fixture, [wish(1)]);
      streamOf('chan').emit('wish.changed');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
      streamOf('chan').emit('wish.changed');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
      const [, older, younger] = calls('open');

      younger.answer.next(page([wish(3)]));
      older.answer.next(page([wish(2)]));
      fixture.detectChanges();

      expect(rowIds(fixture, 'open')).toEqual([3]);
    });

    it('drops the answer for page 1 once the view has moved to page 2', () => {
      const fixture = create();
      last('open').answer.next(page([wish(1)], { totalPages: 2 }));
      last('running').answer.next(page([]));
      fixture.detectChanges();
      streamOf('chan').emit('wish.changed');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
      const staleFirstPage = last('open');

      buttonIn(section(fixture, 'open'), de.pager.next).click();
      fixture.detectChanges();
      const secondPage = last('open');
      expect(secondPage.page).toBe(2);

      staleFirstPage.answer.next(page([wish(9)], { totalPages: 2 }));
      fixture.detectChanges();
      expect(rowIds(fixture, 'open')).toEqual([]);

      secondPage.answer.next(page([wish(21)], { page: 2, totalPages: 2 }));
      fixture.detectChanges();
      expect(rowIds(fixture, 'open')).toEqual([21]);
    });

    it('drops the answer for the previous channel after a channel switch', () => {
      const fixture = create('a');
      const oldOpen = last('open', 'a');

      fixture.componentRef.setInput('channelName', 'b');
      fixture.detectChanges();
      last('open', 'b').answer.next(page([wish(2)]));
      oldOpen.answer.next(page([wish(1)]));
      fixture.detectChanges();

      expect(rowIds(fixture, 'open')).toEqual([2]);
    });

    it('lets a dropped error set no banner', () => {
      const fixture = create();
      settle(fixture, [wish(1)]);
      streamOf('chan').emit('wish.changed');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
      const older = last('open');
      streamOf('chan').emit('wish.changed');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);

      last('open').answer.next(page([wish(1)]));
      older.answer.error(new HttpErrorResponse({ status: 500 }));
      fixture.detectChanges();

      expect(alerts(fixture, 'open')).toEqual([]);
      expect(rowIds(fixture, 'open')).toEqual([1]);
    });

    it('goes back to the last remaining page when the shown one has emptied', () => {
      const fixture = create();
      last('open').answer.next(page([wish(1)], { totalPages: 2 }));
      last('running').answer.next(page([]));
      fixture.detectChanges();
      buttonIn(section(fixture, 'open'), de.pager.next).click();
      fixture.detectChanges();

      last('open').answer.next(page([], { page: 2, totalPages: 1 }));
      fixture.detectChanges();

      expect(last('open').page).toBe(1);
    });
  });

  describe('catching up without wish.changed', () => {
    it('reloads on channel.synced', () => {
      const fixture = create();
      settle(fixture);

      streamOf('chan').emit('channel.synced');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);

      expect(calls('open')).toHaveLength(2);
      expect(calls('running')).toHaveLength(2);
    });

    it('reloads when its own stream opens again, not on the first open', () => {
      const fixture = create();
      settle(fixture);
      const stream = streamOf('chan');

      stream.onopen?.();
      expect(calls('open')).toHaveLength(1);

      stream.reopen();
      expect(calls('open')).toHaveLength(2);
      expect(calls('running')).toHaveLength(2);
    });

    it('lets two pages on two streams catch up independently', () => {
      const pageA = create('a');
      const pageB = create('b');
      settle(pageA, [], [], 'a');
      settle(pageB, [], [], 'b');
      streamOf('a').onopen?.();
      streamOf('b').onopen?.();

      streamOf('a').reopen();

      expect(calls('open', 'a')).toHaveLength(2);
      expect(calls('open', 'b')).toHaveLength(1);
    });

    it('ignores a reopen of a stream it does not listen to', () => {
      const fixture = create('a');
      settle(fixture, [], [], 'a');
      const elsewhere = new FakeEventSource('/api/channels/z/live');

      elsewhere.reopen();

      expect(calls('open', 'a')).toHaveLength(1);
    });

    it('reloads when the tab becomes visible again', () => {
      const fixture = create();
      settle(fixture);

      setVisibility('hidden');
      fixture.detectChanges();
      expect(calls('open')).toHaveLength(1);
      setVisibility('visible');
      fixture.detectChanges();

      expect(calls('open')).toHaveLength(2);
    });

    it('reloads every five minutes while visible, and not while hidden', () => {
      const fixture = create();
      settle(fixture);

      vi.advanceTimersByTime(WISH_SAFETY_RELOAD_MS);
      expect(calls('open')).toHaveLength(2);

      setVisibility('hidden');
      fixture.detectChanges();
      vi.advanceTimersByTime(WISH_SAFETY_RELOAD_MS * 2);
      expect(calls('open')).toHaveLength(2);
    });
  });

  describe('live reloads', () => {
    it('reloads "needs action" and "running" on wish.changed, "ended" only while open', () => {
      const fixture = create();
      settle(fixture);

      streamOf('chan').emit('wish.changed');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
      expect(calls('ended')).toHaveLength(0);

      section(fixture, 'ended').querySelector<HTMLButtonElement>('button[aria-expanded]')?.click();
      fixture.detectChanges();
      expect(calls('ended')).toHaveLength(1);

      streamOf('chan').emit('wish.changed');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
      expect(calls('ended')).toHaveLength(2);
      expect(calls('open')).toHaveLength(3);
    });

    it('turns a burst of events into one request per view', () => {
      const fixture = create();
      settle(fixture);

      for (let i = 0; i < 5; i++) {
        streamOf('chan').emit('wish.changed');
        vi.advanceTimersByTime(100);
      }
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);

      expect(calls('open')).toHaveLength(2);
      expect(calls('running')).toHaveLength(2);
    });
  });

  describe('booking actions', () => {
    it('reloads at once after its own command', () => {
      const fixture = create();
      settle(fixture, [], [wish(1)]);

      buttonIn(section(fixture, 'running'), de.wishes.actions.extend).click();
      expect(actions).toEqual([expect.objectContaining({ name: 'extend', id: 1 })]);
      actions[0].answer.next(wish(1));
      fixture.detectChanges();

      expect(calls('open')).toHaveLength(2);
      expect(calls('running')).toHaveLength(2);
    });

    it('sends keep and close only after the confirmation', () => {
      const fixture = create();
      settle(fixture, [], [wish(1)]);

      buttonIn(section(fixture, 'running'), de.wishes.actions.keep).click();
      expect(openDialog).toHaveBeenCalledTimes(1);
      expect(actions).toEqual([]);
      close(dialogs[0], false);
      expect(actions).toEqual([]);

      buttonIn(section(fixture, 'running'), de.wishes.actions.keep).click();
      close(dialogs[1], true);
      expect(actions).toEqual([expect.objectContaining({ name: 'keep', id: 1 })]);
    });

    it('shows a state conflict at the row’s section and reloads', () => {
      const fixture = create();
      settle(fixture, [], [wish(1)]);

      buttonIn(section(fixture, 'running'), de.wishes.actions.extend).click();
      actions[0].answer.error(
        new HttpErrorResponse({ status: 409, error: { errorCode: 'wish_state_conflict' } }),
      );
      fixture.detectChanges();

      expect(alerts(fixture, 'running')).toEqual([de.errors.api.wish_state_conflict]);
      expect(alerts(fixture, 'open')).toEqual([]);
      expect(calls('running')).toHaveLength(2);
    });

    it('reloads after a vanished wish, but not after another refusal', () => {
      const fixture = create();
      settle(fixture, [], [wish(1)]);

      buttonIn(section(fixture, 'running'), de.wishes.actions.extend).click();
      actions[0].answer.error(new HttpErrorResponse({ status: 500 }));
      fixture.detectChanges();
      expect(calls('running')).toHaveLength(1);

      buttonIn(section(fixture, 'running'), de.wishes.actions.extend).click();
      actions[1].answer.error(
        new HttpErrorResponse({ status: 404, error: { errorCode: 'wish_not_found' } }),
      );
      fixture.detectChanges();
      expect(calls('running')).toHaveLength(2);
    });

    it('keeps the other sections standing when one fails to load', () => {
      const fixture = create();
      last('open').answer.error(new HttpErrorResponse({ status: 500 }));
      last('running').answer.next(page([wish(1)]));
      fixture.detectChanges();

      expect(alerts(fixture, 'open')).toHaveLength(1);
      expect(alerts(fixture, 'running')).toEqual([]);
      expect(rowIds(fixture, 'running')).toEqual([1]);
    });
  });

  describe('jump target ?wish= (Sol-R2-P2-5)', () => {
    it('focuses the row when the wish is in a loaded list', async () => {
      const fixture = create('chan', '1');
      settle(fixture, [], [wish(1), wish(2)]);
      await fixture.whenStable();

      expect(document.activeElement?.getAttribute('data-wish-id')).toBe('1');
      expect(getCalls).toEqual([]);
    });

    it('waits for the lists before fetching the wish on its own', () => {
      create('chan', '5');

      expect(getCalls).toEqual([]);
    });

    it('pins a wish that no loaded list holds, with its actions and the focus', async () => {
      const fixture = create('chan', '5');
      settle(fixture, [], [wish(1)]);
      expect(getCalls.map((call) => call.id)).toEqual([5]);

      getCalls[0].answer.next({ ...wish(5), usage: null });
      fixture.detectChanges();
      await fixture.whenStable();

      expect(rowIds(fixture, 'pinned')).toEqual([5]);
      expect(() => buttonIn(section(fixture, 'pinned'), de.wishes.actions.extend)).not.toThrow();
      expect(document.activeElement?.getAttribute('data-wish-id')).toBe('5');
    });

    it('says when the wish no longer exists', () => {
      const fixture = create('chan', '5');
      settle(fixture);

      getCalls[0].answer.error(
        new HttpErrorResponse({ status: 404, error: { errorCode: 'wish_not_found' } }),
      );
      fixture.detectChanges();

      expect(section(fixture, 'pinned').textContent).toContain(de.wishes.page.pinned.gone);
    });

    it('drops the pinned row once a loaded list holds the wish', () => {
      const fixture = create('chan', '5');
      settle(fixture);
      getCalls[0].answer.next({ ...wish(5), usage: null });
      fixture.detectChanges();

      streamOf('chan').emit('wish.changed');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
      last('open').answer.next(page([]));
      last('running').answer.next(page([wish(5)]));
      fixture.detectChanges();

      expect(section(fixture, 'pinned')).toBeNull();
      expect(rowIds(fixture, 'running')).toEqual([5]);
    });

    it('reloads the pinned wish together with the lists', () => {
      const fixture = create('chan', '5');
      settle(fixture);
      getCalls[0].answer.next({ ...wish(5), usage: null });
      fixture.detectChanges();

      streamOf('chan').emit('wish.changed');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);

      expect(getCalls.map((call) => call.id)).toEqual([5, 5]);
    });
  });
});
