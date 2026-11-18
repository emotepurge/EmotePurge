import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { signal } from '@angular/core';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import de from '../../../../public/i18n/de.json';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { LIVE_CONNECTION_RELEASE_FACTORY } from '../../core/live/live-connection-release.factory';
import { CHANNEL_RELOAD_DEBOUNCE_MS } from '../../core/live/live-reload';
import { PagedResult } from '../../core/models/paged-result.model';
import { SevenTvEmoteLookup } from '../../core/seven-tv/seven-tv-emote-lookup';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { WishAddRequest, WishAddService, WishAddState } from '../../core/wishes/wish-add.service';
import {
  WishRemovalEnd,
  WishRemovalState,
  WishRemovalTracker,
} from '../../core/wishes/wish-removal-tracker';
import { WishRemovalProgress } from '../../core/wishes/wish-removal';
import type { Wish, WishDetail, WishListView, WishUsage } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { WishSummaryService } from '../../core/wishes/wish-summary.service';
import { ChannelWishesPage, WISH_RUN_LOCK_ID, WISH_SAFETY_RELOAD_MS } from './channel-wishes-page';
import { WishAdoptDialog } from './wish-adopt-dialog';
import { WishEntryDialog } from './wish-entry-dialog';
import { WishEditDialog } from './wish-edit-dialog';
import { WishReAddDialog } from './wish-readd-dialog';
import { WishRow } from './wish-row';
import { WISH_USAGE_TTL_MS } from './wish-usage';

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

/** jsdom has no layout: rows report "in view" only when a test says so. */
class FakeIntersectionObserver {
  static instances: FakeIntersectionObserver[] = [];

  readonly targets = new Set<Element>();

  constructor(private readonly callback: (hits: { isIntersecting: boolean }[]) => void) {
    FakeIntersectionObserver.instances.push(this);
  }

  observe(target: Element): void {
    this.targets.add(target);
  }

  disconnect(): void {
    this.targets.clear();
  }

  show(isIntersecting: boolean): void {
    this.callback([{ isIntersecting }]);
  }
}

function usageOf(total: number): WishUsage {
  return {
    windowFromUtc: '2026-11-11T12:00:00Z',
    windowToUtc: '2026-11-18T12:00:00Z',
    total,
    perDayMean: 1,
    rank: null,
    rankOf: null,
    coverageGap: false,
    daily: [],
  };
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
  let stillIds: Set<string>;
  let addRequests: WishAddRequest[];
  let addStates: ReturnType<typeof signal<ReadonlyMap<number, WishAddState>>>;
  let pruned: { channel: string; ids: number[] }[];
  let removalStates: ReturnType<typeof signal<ReadonlyMap<number, WishRemovalState>>>;
  let busyRemovals: Set<number>;
  let removalEnds: Subject<WishRemovalEnd>;
  let removalCalls: { channel: string; id: number; progress: WishRemovalProgress }[];
  let reportRetries: { channel: string; id: number }[];
  let prunedRemovals: { channel: string; ids: number[] }[];
  let summaryRefresh: ReturnType<typeof vi.fn>;
  const fixtures: ComponentFixture<ChannelWishesPage>[] = [];

  beforeEach(() => {
    vi.useFakeTimers();
    summaryRefresh = vi.fn();
    FakeEventSource.instances = [];
    listCalls = [];
    getCalls = [];
    actions = [];
    dialogs = [];
    stillIds = new Set();
    addRequests = [];
    addStates = signal<ReadonlyMap<number, WishAddState>>(new Map());
    pruned = [];
    removalStates = signal<ReadonlyMap<number, WishRemovalState>>(new Map());
    busyRemovals = new Set();
    removalEnds = new Subject<WishRemovalEnd>();
    removalCalls = [];
    reportRetries = [];
    prunedRemovals = [];
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
        { provide: WishSummaryService, useValue: { refresh: summaryRefresh } },
        {
          provide: WishAddService,
          useValue: {
            start: (request: WishAddRequest) => addRequests.push(request),
            stateFor: (_channel: string, target: Wish) => addStates().get(target.id) ?? null,
            prune: (channel: string, wishes: Wish[]) =>
              pruned.push({ channel, ids: wishes.map((shown) => shown.id) }),
          },
        },
        {
          provide: WishRemovalTracker,
          useValue: {
            progressFor: (channel: string, id: number) => {
              const progress = {
                starting: vi.fn(),
                notStarted: vi.fn(),
                dismissed: vi.fn(),
                started: vi.fn(),
                settled: vi.fn(),
              };
              removalCalls.push({ channel, id, progress });
              return progress;
            },
            stateFor: (_channel: string, target: Wish) => removalStates().get(target.id) ?? null,
            owesReport: (_channel: string, id: number) => busyRemovals.has(id),
            retry: (channel: string, id: number) => reportRetries.push({ channel, id }),
            prune: (channel: string, wishes: Wish[]) =>
              prunedRemovals.push({ channel, ids: wishes.map((shown) => shown.id) }),
            ended: removalEnds,
          },
        },
        // The removal flow's own services (set reads, pre-check) are real and hit this backend.
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: SevenTvEmoteLookup,
          useValue: { isKnownStill: (id: string) => stillIds.has(id) },
        },
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

    it('leaves the header count to its own poll on the safety reload and a visible window', () => {
      const fixture = create();
      settle(fixture);

      vi.advanceTimersByTime(WISH_SAFETY_RELOAD_MS);
      setVisibility('hidden');
      fixture.detectChanges();
      setVisibility('visible');
      fixture.detectChanges();

      expect(calls('open')).toHaveLength(3);
      expect(summaryRefresh).not.toHaveBeenCalled();
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

    it('asks the header count again on a live change of this channel', () => {
      const fixture = create();
      settle(fixture);

      streamOf('chan').emit('wish.changed');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);

      expect(summaryRefresh).toHaveBeenCalledOnce();
    });
  });

  describe('booking actions', () => {
    it('reloads at once after its own command, and the header count asks again', () => {
      const fixture = create();
      settle(fixture, [], [wish(1)]);

      buttonIn(section(fixture, 'running'), de.wishes.actions.extend).click();
      expect(actions).toEqual([expect.objectContaining({ name: 'extend', id: 1 })]);
      expect(summaryRefresh).not.toHaveBeenCalled();
      actions[0].answer.next(wish(1));
      fixture.detectChanges();

      expect(calls('open')).toHaveLength(2);
      expect(calls('running')).toHaveLength(2);
      expect(summaryRefresh).toHaveBeenCalledOnce();
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

  describe('usage in the rows (T5, Sol-R2-P2-6)', () => {
    /** Every row's observer reports "in view"; returns how many rows there were. */
    function showRows(fixture: ComponentFixture<ChannelWishesPage>, visible = true): void {
      FakeIntersectionObserver.instances
        .filter((observer) => observer.targets.size > 0)
        .forEach((observer) => observer.show(visible));
      fixture.detectChanges();
    }

    function answerGet(index: number, wishValue: Wish, total: number): void {
      getCalls[index].answer.next({ ...wishValue, usage: usageOf(total) });
    }

    beforeEach(() => {
      FakeIntersectionObserver.instances = [];
      vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    });

    afterEach(() => {
      vi.unstubAllGlobals();
    });

    it('asks nothing for a row that has not been in view, then once for the visible one', () => {
      const fixture = create();
      settle(fixture, [wish(1)], []);
      expect(getCalls).toEqual([]);

      showRows(fixture);
      expect(getCalls.map((call) => call.id)).toEqual([1]);
      answerGet(0, wish(1), 471);
      fixture.detectChanges();
      expect(section(fixture, 'open').textContent).toContain('471 Nutzungen');
    });

    it('does not ask for "ended" rows', () => {
      const fixture = create();
      settle(fixture);
      fixture.componentInstance['toggleEnded']();
      last('ended').answer.next(page([wish(3, { status: 'closed' })]));
      fixture.detectChanges();
      showRows(fixture);

      expect(getCalls).toEqual([]);
    });

    it('shows changed counts for the same key only after the five minutes', () => {
      const fixture = create();
      settle(fixture, [wish(1)], []);
      showRows(fixture);
      answerGet(0, wish(1), 10);
      fixture.detectChanges();

      vi.advanceTimersByTime(WISH_USAGE_TTL_MS - 60_000);
      expect(getCalls).toHaveLength(1);

      vi.advanceTimersByTime(60_000);
      expect(getCalls).toHaveLength(2);
      // The old value stands until the new one arrives.
      expect(section(fixture, 'open').textContent).toContain('10 Nutzungen');
      answerGet(1, wish(1), 25);
      fixture.detectChanges();
      expect(section(fixture, 'open').textContent).toContain('25 Nutzungen');
    });

    it('loads again at once for a changed key from a list reload', () => {
      const fixture = create();
      settle(fixture, [wish(1)], []);
      showRows(fixture);
      answerGet(0, wish(1), 10);

      streamOf('chan').emit('wish.changed');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
      last('open').answer.next(page([wish(1, { expiresAtUtc: '2026-12-30T10:00:00Z' })]));
      last('running').answer.next(page([]));
      fixture.detectChanges();

      expect(getCalls).toHaveLength(2);
    });

    it('lets held values run out when its stream opens again, and not on a plain reload', () => {
      const fixture = create();
      settle(fixture, [wish(1)], []);
      const stream = streamOf('chan');
      stream.onopen?.();
      showRows(fixture);
      answerGet(0, wish(1), 10);

      streamOf('chan').emit('wish.changed');
      vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
      last('open').answer.next(page([wish(1)]));
      last('running').answer.next(page([]));
      expect(getCalls).toHaveLength(1);

      stream.reopen();
      expect(getCalls).toHaveLength(2);
    });

    it('lets held values run out when the window becomes visible again', () => {
      const fixture = create();
      settle(fixture, [wish(1)], []);
      showRows(fixture);
      answerGet(0, wish(1), 10);

      setVisibility('hidden');
      fixture.detectChanges();
      vi.advanceTimersByTime(WISH_USAGE_TTL_MS * 2);
      expect(getCalls).toHaveLength(1);

      setVisibility('visible');
      fixture.detectChanges();
      expect(getCalls).toHaveLength(2);
    });

    it('takes the pinned row’s usage from its own detail without a second request', () => {
      const fixture = create('chan', '5');
      settle(fixture);
      answerGet(0, wish(5), 77);
      fixture.detectChanges();
      showRows(fixture);

      expect(getCalls).toHaveLength(1);
      expect(section(fixture, 'pinned').textContent).toContain('77 Nutzungen');
    });

    it('forgets everything on a channel switch', () => {
      const fixture = create('a');
      settle(fixture, [wish(1)], [], 'a');
      showRows(fixture);
      answerGet(0, wish(1), 10);

      fixture.componentRef.setInput('channelName', 'b');
      fixture.detectChanges();
      settle(fixture, [wish(1)], [], 'b');

      expect(section(fixture, 'open').textContent).not.toContain('10 Nutzungen');
    });
  });
  describe('header and the two filing dialogs', () => {
    function openCall(index: number): { component: unknown; data: Record<string, unknown> } {
      const [component, config] = openDialog.mock.calls[index] as [
        unknown,
        { data: Record<string, unknown> },
      ];
      return { component, data: config.data };
    }

    function closeWith(index: number, result: unknown): void {
      (dialogs[index] as Subject<unknown>).next(result);
      dialogs[index].complete();
    }

    function header(fixture: ComponentFixture<ChannelWishesPage>): HTMLElement {
      return fixture.nativeElement as HTMLElement;
    }

    it('opens the manual entry and the take-over of all entries for the normalised channel', () => {
      const fixture = create('Chan');
      settle(fixture);
      buttonIn(header(fixture), de.wishes.page.header.entry).click();
      buttonIn(header(fixture), de.wishes.page.header.adopt).click();
      expect(openCall(0)).toEqual({ component: WishEntryDialog, data: { channelName: 'chan' } });
      expect(openCall(1)).toEqual({ component: WishAdoptDialog, data: { channelName: 'chan' } });
    });

    it('reloads after a filed wish and says when the set was full', () => {
      const fixture = create();
      settle(fixture);
      const before = listCalls.length;
      buttonIn(header(fixture), de.wishes.page.header.entry).click();
      closeWith(0, { kind: 'created', wish: wish(9, { status: 'approved' }), setFull: true });
      fixture.detectChanges();
      expect(calls('open').length + calls('running').length).toBe(before + 2);
      const status = fixture.nativeElement.querySelector('p[role="status"].sr-only');
      expect(status.textContent).toContain(de.wishes.page.setFull);
    });

    it('turns the take-over exit of the entry into the take-over dialog with its entries and wish', () => {
      const fixture = create();
      settle(fixture);
      buttonIn(header(fixture), de.wishes.page.header.entry).click();
      const approved = wish(4, { status: 'approved', slotId: null });
      closeWith(0, { kind: 'adopt', sevenTvEmoteId: 'emoteX', slotIds: ['s1'], wish: approved });
      expect(openCall(1)).toEqual({
        component: WishAdoptDialog,
        data: { channelName: 'chan', sevenTvEmoteId: 'emoteX', slotIds: ['s1'], wish: approved },
      });
    });

    it('opens the take-over for the row’s wish, narrowed to its emote', () => {
      const fixture = create();
      const approved = wish(4, { status: 'approved', slotId: null, expiresAtUtc: null });
      settle(fixture, [approved]);
      buttonIn(section(fixture, 'open'), de.wishes.actions.adopt).click();
      expect(openCall(0)).toEqual({
        component: WishAdoptDialog,
        data: { channelName: 'chan', wish: approved, sevenTvEmoteId: 'emote4' },
      });
      closeWith(0, { kind: 'adopted', wish: { ...approved, status: 'active' } });
      expect(calls('open')).toHaveLength(2);
    });

    it('jumps to a wish a dialog names through the query parameter', () => {
      const fixture = create();
      settle(fixture);
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
      buttonIn(header(fixture), de.wishes.page.header.adopt).click();
      closeWith(0, { kind: 'jump', wishId: 12 });
      expect(navigate).toHaveBeenCalledWith(
        [],
        expect.objectContaining({ queryParams: { wish: 12 }, queryParamsHandling: 'merge' }),
      );
    });

    it('focuses the row again when the dialog names the wish already targeted', async () => {
      const fixture = create('chan', '1');
      settle(fixture, [wish(1)]);
      await fixture.whenStable();
      (document.activeElement as HTMLElement | null)?.blur();
      buttonIn(header(fixture), de.wishes.page.header.adopt).click();
      closeWith(0, { kind: 'jump', wishId: 1 });
      fixture.detectChanges();
      await fixture.whenStable();
      expect(document.activeElement?.getAttribute('data-wish-id')).toBe('1');
    });

    it('tells a row that this session’s 7TV read found its emote not animated', () => {
      stillIds.add('emote2');
      const fixture = create();
      settle(fixture, [], [wish(1), wish(2)]);
      const known = fixture.debugElement
        .queryAll(By.directive(WishRow))
        .map((row) => (row.componentInstance as WishRow).knownStill());
      expect(known).toEqual([false, true]);
    });
  });
  describe('edit and re-add dialogs (T7)', () => {
    function openCall(index: number): { component: unknown; data: Record<string, unknown> } {
      const [component, config] = openDialog.mock.calls[index] as [
        unknown,
        { data: Record<string, unknown> },
      ];
      return { component, data: config.data };
    }

    function closeWith(index: number, result: unknown): void {
      (dialogs[index] as Subject<unknown>).next(result);
      dialogs[index].complete();
    }

    it('opens the edit dialog for the row’s wish, and reloads after an edit', () => {
      const fixture = create('Chan');
      const approved = wish(4, { status: 'approved', slotId: null });
      settle(fixture, [approved]);
      buttonIn(section(fixture, 'open'), de.wishes.actions.edit).click();
      expect(openCall(0)).toEqual({
        component: WishEditDialog,
        data: { channelName: 'chan', wish: approved },
      });
      closeWith(0, { kind: 'edited', wish: { ...approved, durationDays: 10 } });
      expect(calls('open')).toHaveLength(2);
    });

    it('opens the re-add dialog for a removed wish', () => {
      const fixture = create();
      const removed = wish(5, {
        status: 'removed',
        slotId: 's',
        slotAddedAtUtc: '2026-11-01T00:00:00Z',
      });
      settle(fixture, [removed]);
      buttonIn(section(fixture, 'open'), de.wishes.actions.reAdd).click();
      expect(openCall(0)).toEqual({
        component: WishReAddDialog,
        data: { channelName: 'chan', wish: removed },
      });
      closeWith(0, { kind: 'readded', wish: { ...removed, status: 'active' } });
      expect(calls('open')).toHaveLength(2);
    });

    it('does nothing after a dismissed dialog', () => {
      const fixture = create();
      settle(fixture, [wish(4, { status: 'approved', slotId: null })]);
      buttonIn(section(fixture, 'open'), de.wishes.actions.edit).click();
      closeWith(0, undefined);
      expect(calls('open')).toHaveLength(1);
    });

    it('shows a stale answer of either dialog at the row’s section and reloads', () => {
      const fixture = create();
      settle(fixture, [wish(4, { status: 'approved', slotId: null })]);
      buttonIn(section(fixture, 'open'), de.wishes.actions.edit).click();
      closeWith(0, { kind: 'stale', errorKey: 'errors.api.wish_state_conflict' });
      fixture.detectChanges();
      expect(alerts(fixture, 'open')).toEqual([de.errors.api.wish_state_conflict]);
      expect(calls('open')).toHaveLength(2);
    });

    it('jumps to the running wish the re-add dialog names', () => {
      const fixture = create();
      settle(fixture, [wish(5, { status: 'removed' })]);
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
      buttonIn(section(fixture, 'open'), de.wishes.actions.reAdd).click();
      closeWith(0, { kind: 'jump', wishId: 12 });
      expect(navigate).toHaveBeenCalledWith(
        [],
        expect.objectContaining({ queryParams: { wish: 12 } }),
      );
    });

    it('opens the edit dialog for the wish the entry dialog names', () => {
      const fixture = create();
      settle(fixture);
      buttonIn(fixture.nativeElement, de.wishes.page.header.entry).click();
      const approved = wish(7, { status: 'approved', slotId: null });
      closeWith(0, { kind: 'edit', wish: approved });
      expect(openCall(1)).toEqual({
        component: WishEditDialog,
        data: { channelName: 'chan', wish: approved },
      });
      closeWith(1, { kind: 'edited', wish: approved });
      expect(calls('open')).toHaveLength(2);
    });
  });
  describe('add in 7TV (T8)', () => {
    const approved = (id: number) =>
      wish(id, { status: 'approved', slotId: null, expiresAtUtc: null, startsAtUtc: null });

    function rowOf(fixture: ComponentFixture<ChannelWishesPage>, id: number): HTMLElement {
      return fixture.nativeElement.querySelector(`[data-wish-id="${id}"]`);
    }

    it('hands the wish and the normalised channel to the add, and tells it when the page is gone', () => {
      const fixture = create('Chan');
      settle(fixture, [approved(7)]);

      buttonIn(rowOf(fixture, 7), de.wishes.actions.add7tv).click();

      expect(addRequests).toHaveLength(1);
      const [request] = addRequests;
      expect(request.channelName).toBe('chan');
      expect(request.wish.id).toBe(7);
      expect(request.caller.alive()).toBe(true);
      expect(request.caller.channelName()).toBe('chan');

      fixture.componentRef.setInput('channelName', 'other');
      fixture.detectChanges();
      expect(request.caller.channelName()).toBe('other');

      fixture.destroy();
      expect(request.caller.alive()).toBe(false);
    });

    it('opens the token prompt through the add when it asks for one', () => {
      const fixture = create();
      settle(fixture, [approved(7)]);
      buttonIn(rowOf(fixture, 7), de.wishes.actions.add7tv).click();

      let answered: boolean | undefined;
      addRequests[0].promptToken().subscribe((done) => (answered = done));
      expect(openDialog).toHaveBeenCalledOnce();
      close(dialogs[0], true);

      expect(answered).toBe(true);
    });

    it('reloads the lists after an add that stopped, and not after one that went through', () => {
      const fixture = create();
      settle(fixture, [approved(7)]);
      buttonIn(rowOf(fixture, 7), de.wishes.actions.add7tv).click();

      addRequests[0].onEnd?.({ kind: 'added' });
      expect(calls('open')).toHaveLength(1);

      addRequests[0].onEnd?.({ kind: 'notStarted', reason: 'wishChanged' });
      expect(calls('open')).toHaveLength(2);
      expect(calls('running')).toHaveLength(2);
    });

    it('reloads nothing for an add that ends after the page is gone', () => {
      const fixture = create();
      settle(fixture, [approved(7)]);
      buttonIn(rowOf(fixture, 7), de.wishes.actions.add7tv).click();
      fixture.destroy();

      addRequests[0].onEnd?.({ kind: 'skipped', reason: 'setChanged' });

      expect(calls('open')).toHaveLength(1);
    });

    it('shows the add state of a row at that row only', () => {
      const fixture = create();
      settle(fixture, [approved(7), approved(8)]);

      addStates.set(new Map([[7, { kind: 'notStarted', reason: 'pageLeft' }]]));
      fixture.detectChanges();

      const spoken = (id: number) =>
        rowOf(fixture, id)
          .querySelector('[role="status"]')
          ?.textContent?.replace(/\s+/g, ' ')
          .trim();
      expect(spoken(7)).toBe(`${de.wishes.start.nothingChanged} ${de.wishes.start.pageLeft}`);
      expect(spoken(8)).toBe('');
    });

    it('hands every shown wish to the add, so results of wishes that moved on are dropped', () => {
      const fixture = create();
      settle(fixture, [approved(7)], [wish(9)]);

      expect(pruned.at(-1)).toEqual({ channel: 'chan', ids: [7, 9] });
    });

    it('locks the add while a 7TV run holds the start and names that run in the one status line', () => {
      const running = signal(false);
      TestBed.inject(SevenTvRunArbiter).register({
        kind: 'import',
        isRunning: running,
        isSettling: signal(false),
        destructiveOpen: signal(false),
      });
      const fixture = create();
      settle(fixture, [approved(7)]);

      running.set(true);
      fixture.detectChanges();

      const add = buttonIn(rowOf(fixture, 7), de.wishes.actions.add7tv);
      expect(add.getAttribute('aria-disabled')).toBe('true');
      expect(add.getAttribute('aria-describedby')).toBe(WISH_RUN_LOCK_ID);
      const line = fixture.nativeElement.querySelector(`#${WISH_RUN_LOCK_ID}`);
      expect(line?.textContent?.trim()).toBe(
        de.wishes.lock.running.replace('{{ kind }}', de.sevenTvRun.kind.import),
      );
      add.click();
      expect(addRequests).toEqual([]);
    });
  });

  describe('remove from 7TV (T9)', () => {
    function rowOf(fixture: ComponentFixture<ChannelWishesPage>, id: number): HTMLElement {
      return fixture.nativeElement.querySelector(`[data-wish-id="${id}"]`);
    }

    function spoken(fixture: ComponentFixture<ChannelWishesPage>, id: number): string {
      return (
        rowOf(fixture, id)
          .querySelector('[role="status"]')
          ?.textContent?.replace(/\s+/g, ' ')
          .trim() ?? ''
      );
    }

    it('hands the wish and the normalised channel to the removal, which reads the active set past the cache', () => {
      const fixture = create('Chan');
      settle(fixture, [], [wish(9)]);

      buttonIn(rowOf(fixture, 9), de.wishes.actions.remove).click();

      expect(removalCalls.map(({ channel, id }) => ({ channel, id }))).toEqual([
        { channel: 'chan', id: 9 },
      ]);
      expect(removalCalls[0].progress.starting).toHaveBeenCalledOnce();
      const http = TestBed.inject(HttpTestingController);
      expect(
        http.match(
          (request) =>
            request.url === '/api/seventv/channels/chan/emote-sets' &&
            request.params.get('refresh') === 'true',
        ),
      ).toHaveLength(1);
    });

    it.each([
      [
        'a date 7TV has not confirmed',
        { startsAtAuthoritative: false },
        de.wishes.remove.blocked.provisional,
      ],
      ['no generation', { slotAddedAtUtc: null }, de.wishes.remove.blocked.noEntry],
    ])('locks "Entfernen" with its reason for %s and starts nothing', (_label, change, text) => {
      const fixture = create();
      settle(fixture, [], [wish(9, change)]);

      const remove = buttonIn(rowOf(fixture, 9), de.wishes.actions.remove);
      expect(remove.getAttribute('aria-disabled')).toBe('true');
      const reason = fixture.nativeElement.querySelector(
        `#${remove.getAttribute('aria-describedby')}`,
      );
      expect(reason?.textContent?.trim()).toBe(text);
      remove.click();
      expect(removalCalls).toEqual([]);
    });

    it('locks "Entfernen" while the removal of the wish owes its booking', () => {
      busyRemovals.add(9);
      const fixture = create();
      settle(fixture, [], [wish(9)]);

      const remove = buttonIn(rowOf(fixture, 9), de.wishes.actions.remove);
      expect(remove.getAttribute('aria-disabled')).toBe('true');
      remove.click();
      expect(removalCalls).toEqual([]);
    });

    it('shows the removal state at its row, and the owed report’s "Erneut melden"', () => {
      const fixture = create();
      settle(fixture, [], [wish(9), wish(10)]);

      removalStates.set(
        new Map<number, WishRemovalState>([
          [9, { kind: 'notBooked' }],
          [10, { kind: 'reportPending' }],
        ]),
      );
      fixture.detectChanges();

      expect(spoken(fixture, 9)).toBe(de.wishes.remove.notBooked);
      expect(spoken(fixture, 10)).toBe(de.wishes.remove.reportPending);
      buttonIn(rowOf(fixture, 10), de.wishes.remove.retryReport).click();
      expect(reportRetries).toEqual([{ channel: 'chan', id: 10 }]);
    });

    it('reloads the lists when a removal of this channel ended, not for another channel', () => {
      const fixture = create();
      settle(fixture, [], [wish(9)]);

      removalEnds.next({ channelName: 'other', wishId: 9, state: { kind: 'booked' } });
      expect(calls('open')).toHaveLength(1);

      removalEnds.next({ channelName: 'chan', wishId: 9, state: { kind: 'booked' } });
      expect(calls('open')).toHaveLength(2);
      expect(calls('running')).toHaveLength(2);
    });

    it('hands every shown wish to the removal, so lines of wishes that moved on are dropped', () => {
      const fixture = create();
      settle(fixture, [wish(7, { status: 'approved' })], [wish(9)]);

      expect(prunedRemovals.at(-1)).toEqual({ channel: 'chan', ids: [7, 9] });
    });
  });
});
