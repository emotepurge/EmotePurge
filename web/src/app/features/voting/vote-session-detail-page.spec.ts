/**
 * The first spec for `VoteSessionDetailPage` (#133 — none existed before). Mounts the real page
 * class the same way `usage-stats-page.spec.ts` established for `UsageStatsPage`: `TestBed` with
 * `HttpClientTestingModule` and a fake `EVENT_SOURCE_FACTORY`, the real 380-line template swapped for
 * a bare `<div #sheet></div>` — the constructor only needs that ref to resolve so its `ResizeObserver`
 * effect can attach to something; every signal, computed and HTTP call in the class runs unmodified.
 *
 * What is under test: `applyResults()` now reconciles `selection` against `results.emotes` (the
 * reload's own unfiltered payload) via `ListSelection.retainAmong()`, instead of leaving a dead
 * selection key sitting in the set forever. See the class's own comment on `applyResults()` and the
 * 2026-09-11 DECISIONS.md entry for the full reasoning — in short: a dynamic (whole-set) session's
 * results filter archived emotes out entirely, so an emote archived from outside the tab drops out of
 * `results.emotes` and must be pruned; a fixed-ballot session keeps an archived member listed
 * throughout, so nothing should be pruned for it.
 */
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { VoteSessionResult, VoteSessionResults } from '../../core/voting/vote-session.model';
import { VoteSessionDetailPage } from './vote-session-detail-page';

/** Same stand-in as usage-stats-page.spec.ts / core/live/live-reload.spec.ts — jsdom ships no
 *  EventSource at all. */
class FakeEventSource {
  static instances: FakeEventSource[] = [];

  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  closeCount = 0;

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  close(): void {
    this.closeCount++;
  }

  emit(event: { type: string; channel?: string; sessionId?: number }): void {
    this.onmessage?.({ data: JSON.stringify(event) } as MessageEvent);
  }
}

/** jsdom implements no ResizeObserver either — the constructor's sheetWidth effect touches it. */
class FakeResizeObserver {
  observe(): void {
    /* no-op */
  }
  unobserve(): void {
    /* no-op */
  }
  disconnect(): void {
    /* no-op */
  }
}

// Matches VOTE_RELOAD_DEBOUNCE_MS in vote-session-detail-page.ts — not exported (module-local
// constant), so the value is duplicated here the same way usage-stats-page.spec.ts duplicates
// CHANNEL_RELOAD_DEBOUNCE_MS's sibling for its own page (that one happens to be exported and
// imported instead, purely because it is shared across two pages; this one is not).
const VOTE_RELOAD_DEBOUNCE_MS = 500;

function resultEmote(id: string, overrides: Partial<VoteSessionResult> = {}): VoteSessionResult {
  return {
    emoteId: id,
    emoteName: `Emote${id}`,
    sevenTvEmoteId: `7tv-${id}`,
    imageUrl: '',
    totalUseCount: 10,
    keepVotes: 0,
    deleteVotes: 0,
    score: 0,
    isArchived: false,
    myVote: null,
    ...overrides,
  };
}

function results(
  emotes: VoteSessionResult[],
  overrides: Partial<VoteSessionResults> = {},
): VoteSessionResults {
  return {
    sessionId: 7,
    title: 'Test session',
    allowedVoterRoles: 1,
    isActive: true,
    startedAt: '2026-01-01T00:00:00Z',
    endedAt: null,
    voterCount: 3,
    hideResultsUntilEnd: false,
    emotes,
    ...overrides,
  };
}

/** `HttpTestingController.match(path)` compares against `urlWithParams` — this matches on the exact
 *  pathname the way every call site below actually means it (none of these three endpoints carry a
 *  query string). */
function flushByPath(mock: HttpTestingController, path: string, body: unknown): void {
  mock.match((req) => req.url === path).forEach((testReq) => testReq.flush(body as object));
}

describe('VoteSessionDetailPage — selection reconciliation on a silent reload (#133)', () => {
  let fixture: ComponentFixture<VoteSessionDetailPage>;
  let component: VoteSessionDetailPage;
  let httpMock: HttpTestingController;

  const CHANNEL = 'sensitron';
  const SESSION_ID = '7';

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    vi.useFakeTimers();

    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
      ],
    });

    TestBed.overrideComponent(VoteSessionDetailPage, {
      set: { template: '<div #sheet></div>' },
    });

    fixture = TestBed.createComponent(VoteSessionDetailPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', CHANNEL);
    fixture.componentRef.setInput('sessionId', SESSION_ID);
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  /** Drives the page through its initial mount: results, the channel's active-set status
   *  (loadActiveEmoteSetId) and the permissions probe (permissionsResource) — the three requests
   *  `load()`'s constructor effect fires on first run. */
  function mount(initial: VoteSessionResults): void {
    flushByPath(httpMock, `/api/channels/${CHANNEL}/vote-sessions/${SESSION_ID}/results`, initial);
    flushByPath(httpMock, `/api/channels/${CHANNEL}`, {
      channelId: 'c1',
      channelName: CHANNEL,
      isBotActive: true,
      activeEmoteSetId: 'set-1',
    });
    flushByPath(httpMock, `/api/channels/${CHANNEL}/permissions`, {
      canManage: true,
      canViewUsageStats: true,
      isGlobalAdmin: false,
      isTracked: true,
      isBotActive: true,
    });
  }

  /** Fires one live event on this session's channel stream and flushes the debounced reload's
   *  `/results` response — the same round trip the constructor's merged live pipeline produces for
   *  `usage.flushed`/`channel.synced`. */
  function silentReload(next: VoteSessionResults): void {
    FakeEventSource.instances[0].emit({ type: LIVE_EVENT_TYPES.usageFlushed, channel: CHANNEL });
    vi.advanceTimersByTime(VOTE_RELOAD_DEBOUNCE_MS);
    fixture.detectChanges();
    flushByPath(httpMock, `/api/channels/${CHANNEL}/vote-sessions/${SESSION_ID}/results`, next);
  }

  it('prunes a selected emote once a dynamic session stops listing it (archived from outside the tab)', () => {
    const a = resultEmote('a');
    const b = resultEmote('b');

    mount(results([a, b]));
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['a']);

    // 'a' gets archived on 7TV from outside this tab. A dynamic session's results are `!IsArchived`
    // server-side, so the reload's payload no longer carries 'a' at all — not even badged.
    silentReload(results([b]));

    expect(component['selection'].selectedKeys()).toEqual([]);
  });

  it('does not silently re-select an emote that comes back after being archived (the actual point of #133)', () => {
    const a = resultEmote('a');
    const b = resultEmote('b');

    mount(results([a, b]));
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);

    silentReload(results([b])); // 'a' archived, pruned from the selection
    expect(component['selection'].selectedKeys()).toEqual([]);

    // 'a' is un-archived again and reappears in the next reload's payload. Its id resurfacing must
    // not resurrect the old selection — the user never re-marked it.
    silentReload(results([a, b]));
    expect(component['selection'].selectedKeys()).toEqual([]);
  });

  it('keeps a selected, mid-session-archived member of a FIXED ballot selected — its row never leaves results.emotes', () => {
    const a = resultEmote('a');
    const b = resultEmote('b');

    // emoteCount on the summary is a list-page concern; what makes THIS a fixed ballot from
    // applyResults()'s point of view is simply that results.emotes keeps 'a' listed once archived —
    // exactly what the backend does for a curated ballot (design doc, DECISIONS 2026-08-01).
    mount(results([a, b]));
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['a']);

    silentReload(results([resultEmote('a', { isArchived: true }), b]));

    expect(component['selection'].selectedKeys()).toEqual(['a']);
  });

  it('a selected row only hidden by the usage filter (not removed from the session) survives a reload untouched', () => {
    // This is the case that decides retainAmong(results.emotes) over emotes(): 'c' starts above
    // the min-usage filter, gets selected, and the reload lowers its count below that same filter
    // — dropping it out of emotes() (the filtered view) while it is still part of the reloaded,
    // unfiltered ballot. A filter change itself never touches the selection at all any more
    // (Konzept "Auswahl überlebt Suche und Filter"), so setRange() below only narrows emotes() for
    // the assertion at the end, it does not exercise any pruning of its own.
    const a = resultEmote('a', { totalUseCount: 10 });
    const c = resultEmote('c', { totalUseCount: 10 });

    mount(results([a, c]));
    component['usageFilter'].setRange(5, null);
    component['selection'].onRowClick(c, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['c']);
    expect(component['emotes']().map((emote: VoteSessionResult) => emote.emoteId)).toContain('c');

    silentReload(results([a, resultEmote('c', { totalUseCount: 1 })]));

    // Confirms the filter really did narrow emotes() past 'c' — otherwise this test would not be
    // exercising the case it claims to.
    expect(component['emotes']().map((emote: VoteSessionResult) => emote.emoteId)).not.toContain(
      'c',
    );
    // ...yet the selection is untouched: 'c' was never actually removed from the ballot, only
    // filtered out of the current view.
    expect(component['selection'].selectedKeys()).toEqual(['c']);
  });

  it('a name filter that hides a marked row leaves selectedKeys whole, raising only hiddenSelectedCount', () => {
    const a = resultEmote('a');
    const b = resultEmote('b');
    const c = resultEmote('c');

    mount(results([a, b, c]));
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(c, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'c']);

    // Narrows emotes() to just 'b' — both marked rows drop out of view, exactly the case #133/S2-16
    // used to prune for.
    component['usageFilter'].setNameFilter('EmoteB');

    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'c']);
    expect(component['selection'].hiddenSelectedCount()).toBe(2);
    expect(
      component['selectedForDelete']()
        .map((row) => row.emoteId)
        .sort(),
    ).toEqual(['a', 'c']);
    // Both marked rows are filtered out of the current view — DeletableEmote.hidden must say so
    // for each (Konzept "Auswahl überlebt Suche und Filter" 2.1/3).
    expect(component['selectedForDelete']().every((row) => row.hidden)).toBe(true);
  });

  it('a silent reload that loses nothing selected leaves the selection alone', () => {
    const a = resultEmote('a');
    const b = resultEmote('b');

    mount(results([a, b]));
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);

    silentReload(results([a, b]));

    expect(component['selection'].selectedKeys()).toEqual(['a']);
  });
});

/**
 * The Bestandsfehler this Konzept closes (Abschnitt 3, Codex Befund 2): the page is reused across
 * a direct navigation between sessions (see `guardHandoffKey`'s own comment), and now that
 * `selectedItems()` resolves against the unfiltered universe (`orderedEmotes()`) rather than the
 * filtered display list, an emote id that happens to exist in BOTH sessions would otherwise stay
 * selected AND stay resolvable to the delete run — worse than before this Konzept, which only
 * accidentally guarded against it by resolving against the filtered view. `applyResults()` now
 * keys on `channelName:sessionId` (`selectionContextKey`, deliberately its own field, not a reuse
 * of `guardHandoffKey`) and clears the selection outright on a mismatch.
 */
describe('VoteSessionDetailPage — the selection is scoped to channel:session (Konzept "Auswahl überlebt Suche und Filter" §3)', () => {
  let fixture: ComponentFixture<VoteSessionDetailPage>;
  let component: VoteSessionDetailPage;
  let httpMock: HttpTestingController;

  const CHANNEL = 'sensitron';

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    vi.useFakeTimers();

    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
      ],
    });

    TestBed.overrideComponent(VoteSessionDetailPage, {
      set: { template: '<div #sheet></div>' },
    });

    fixture = TestBed.createComponent(VoteSessionDetailPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  /** Initial mount, both required inputs set together before the first `detectChanges()` — same
   *  requirement the other describe block's own `beforeEach` follows. */
  function mount(sessionId: string, initial: VoteSessionResults): void {
    fixture.componentRef.setInput('channelName', CHANNEL);
    fixture.componentRef.setInput('sessionId', sessionId);
    fixture.detectChanges();
    flushByPath(httpMock, `/api/channels/${CHANNEL}/vote-sessions/${sessionId}/results`, initial);
    flushByPath(httpMock, `/api/channels/${CHANNEL}`, {
      channelId: 'c1',
      channelName: CHANNEL,
      isBotActive: true,
      activeEmoteSetId: 'set-1',
    });
    flushByPath(httpMock, `/api/channels/${CHANNEL}/permissions`, {
      canManage: true,
      canViewUsageStats: true,
      isGlobalAdmin: false,
      isTracked: true,
      isBotActive: true,
    });
  }

  /** Navigates the SAME component instance to a different session — the direct-navigation case
   *  (component reused, only the `sessionId` input changes) that `guardHandoffKey` and
   *  `selectionContextKey` both exist for. */
  function navigateTo(sessionId: string, next: VoteSessionResults): void {
    fixture.componentRef.setInput('sessionId', sessionId);
    fixture.detectChanges();
    flushByPath(httpMock, `/api/channels/${CHANNEL}/vote-sessions/${sessionId}/results`, next);
    flushByPath(httpMock, `/api/channels/${CHANNEL}`, {
      channelId: 'c1',
      channelName: CHANNEL,
      isBotActive: true,
      activeEmoteSetId: 'set-1',
    });
  }

  it('clears the selection on navigation to a different session, even when the same emote id is marked in both', () => {
    const a = resultEmote('a');

    mount('7', results([a], { sessionId: 7 }));
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['a']);

    // Session 8 happens to carry the very same emote id — under the pre-Konzept resolution
    // (against the filtered display list) this could still leak through a coincidence; under
    // selectedItems() resolving against orderedEmotes() it would now reach the delete run outright
    // if the switch did not clear the selection.
    navigateTo('8', results([a], { sessionId: 8 }));

    expect(component['selection'].selectedKeys()).toEqual([]);
  });

  it('keeps the selection across a reload of the SAME session', () => {
    const a = resultEmote('a');
    const b = resultEmote('b');

    mount('7', results([a, b], { sessionId: 7 }));
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);

    FakeEventSource.instances[0].emit({ type: LIVE_EVENT_TYPES.usageFlushed, channel: CHANNEL });
    vi.advanceTimersByTime(VOTE_RELOAD_DEBOUNCE_MS);
    fixture.detectChanges();
    flushByPath(
      httpMock,
      `/api/channels/${CHANNEL}/vote-sessions/7/results`,
      results([a, b], { sessionId: 7 }),
    );

    expect(component['selection'].selectedKeys()).toEqual(['a']);
  });
});
