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
import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { VoteSessionResult, VoteSessionResults } from '../../core/voting/vote-session.model';
import { EmoteDrilldownData } from '../../shared/emotes/emote-drilldown-dialog';
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
    // Matches isArchived's default here (spec section 9: eligible = !isArchived for a null-session
    // row) — a test that only cares about isArchived, like the archived-ballot-member cases below,
    // leaves this alone and gets the same result it always did.
    eligible: true,
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
    // null-session default; set-session tests pass their own emoteSetId.
    emoteSetId: null,
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

    silentReload(results([resultEmote('a', { isArchived: true, eligible: false }), b]));

    expect(component['selection'].selectedKeys()).toEqual(['a']);
  });

  // Opus review P2-b: the card stays selected/markable (the assertion above), but a null-session's
  // own archived row must not reach the delete run — before this filter existed, a confirmed
  // selection that included it blocked the ENTIRE run on every attempt once #227 P1 started
  // fail-closing on any row a live read cannot find (an archived row never left results.emotes for a
  // fixed ballot, so no reload ever cleared it).
  it("excludes a null-session's own archived row from selectedForDelete, even while it stays selected", () => {
    const a = resultEmote('a');
    const b = resultEmote('b');

    mount(results([a, b]));
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(b, { shiftKey: true } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'b']);
    expect(
      component['selectedForDelete']()
        .map((row) => row.emoteId)
        .sort(),
    ).toEqual(['a', 'b']);

    silentReload(results([resultEmote('a', { isArchived: true, eligible: false }), b]));

    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'b']);
    expect(component['selectedForDelete']().map((row) => row.emoteId)).toEqual(['b']);
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

  // Spec #200, E18/F12/AK 72: the delete panel reports 7TV ids now, so this page drops the rows by
  // `sevenTvEmoteId` — while its own selection stays keyed by the Guid every ballot row carries.
  it('drops the rows the delete panel reports by their 7TV id and keeps its selection keyed by the Guid', () => {
    const a = resultEmote('a');
    const b = resultEmote('b');

    mount(results([a, b]));
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['a']);

    component['onDeleted'](['7tv-a']);

    expect(component['results']()?.emotes.map((emote) => emote.emoteId)).toEqual(['b']);
    expect(component['selection'].selectedKeys()).toEqual([]);
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

/**
 * Spec section 9 (T6.3, AK 81/82): `canSelectForDelete` follows `canManage` rather than
 * `hasUsageData` — a manager must see the mass-delete panel even over a set-session ballot whose
 * every row is `totalUseCount: null` (never used under that set, GetTotalsByEmoteIdsAsync's honest
 * answer, not a permission gap) — and the vote lock/badge follow `eligible` rather than
 * `isArchived`, so a set-session's archived member stays votable while a null-session's ineligible
 * member does not.
 *
 * Real timers throughout (no `vi.useFakeTimers()`, unlike the two describe blocks above): every
 * test here awaits `settle()` for `permissionsResource` (an `rxResource`, unlike `results`/
 * `activeEmoteSetId`, which loadResults()/loadActiveEmoteSetId() write to directly from a plain
 * HttpClient `.subscribe()` and so update synchronously on `flush()`) — a bare
 * `fixture.detectChanges()` right after `flush()` still observably reports `canManage() === false`
 * (checked directly while writing this suite; same idiom as usage-stats-page.spec.ts's own
 * `settle()` for its `rxResource`-backed `emoteSetListResource`).
 */
describe('VoteSessionDetailPage — canSelectForDelete and the vote lock follow their own gates (spec section 9, AK 81/82)', () => {
  let fixture: ComponentFixture<VoteSessionDetailPage>;
  let component: VoteSessionDetailPage;
  let httpMock: HttpTestingController;

  const CHANNEL = 'sensitron';
  const SESSION_ID = '7';

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);

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
    vi.unstubAllGlobals();
  });

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  async function mount(initial: VoteSessionResults, canManage: boolean): Promise<void> {
    flushByPath(httpMock, `/api/channels/${CHANNEL}/vote-sessions/${SESSION_ID}/results`, initial);
    flushByPath(httpMock, `/api/channels/${CHANNEL}`, {
      channelId: 'c1',
      channelName: CHANNEL,
      isBotActive: true,
      activeEmoteSetId: 'set-1',
    });
    flushByPath(httpMock, `/api/channels/${CHANNEL}/permissions`, {
      canManage,
      canViewUsageStats: canManage,
      isGlobalAdmin: false,
      isTracked: true,
      isBotActive: true,
    });
    flushByPath(httpMock, `/api/channels/${CHANNEL}/emote-sets`, {
      activeEmoteSetId: 'set-1',
      sets: [
        { id: 'set-1', name: 'Main set', isActive: true, kind: 'NORMAL' },
        { id: 'halloween-1', name: 'Halloween 2026', isActive: false, kind: 'NORMAL' },
      ],
    });
    await settle();
  }

  it("a manager sees the panel over an all-null-usage set-session ballot, targeting the session's own set", async () => {
    await mount(
      results([resultEmote('a', { totalUseCount: null })], { emoteSetId: 'halloween-1' }),
      true,
    );

    // hasUsageData reads this exact shape as "not a manager" (its own doc comment) — the point of
    // AK 81 is that canSelectForDelete no longer inherits that misreading.
    expect(component['hasUsageData']()).toBe(false);
    expect(component['canSelectForDelete']()).toBe(true);
    expect(component['massDeletePanelSetId']()).toBe('halloween-1');
  });

  it("resolves the panel's set name for a set session over a non-active set (spec 8.8's delete confirmation)", async () => {
    await mount(
      results([resultEmote('a', { totalUseCount: null })], { emoteSetId: 'halloween-1' }),
      true,
    );

    expect(component['massDeletePanelSetName']()).toBe('Halloween 2026');
  });

  it('a non-manager gets no panel even with usage data present', async () => {
    await mount(results([resultEmote('a', { totalUseCount: 5 })]), false);

    expect(component['hasUsageData']()).toBe(true);
    expect(component['canSelectForDelete']()).toBe(false);
  });

  it('a null-session falls back to the active set for the panel when the session carries no set of its own', async () => {
    await mount(results([resultEmote('a')]), true);

    expect(component['massDeletePanelSetId']()).toBe('set-1'); // channel status' activeEmoteSetId
  });

  it('the vote lock and its title follow eligible, not isArchived', async () => {
    await mount(
      results([
        // A set-session member that has left 7TV since the ballot was frozen: still votable.
        resultEmote('archived-but-eligible', { isArchived: true, eligible: true, keepVotes: 0 }),
        // A null-session member the vote lock still has to close: not eligible.
        resultEmote('not-eligible', { isArchived: false, eligible: false, keepVotes: 0 }),
      ]),
      true,
    );
    const [eligible, ineligible] = component['results']()!.emotes;

    // The normal label carries the tally in parentheses; the disabled message does not.
    expect(component['keepButtonTitle'](eligible)).toContain('(0)');
    expect(component['keepButtonTitle'](ineligible)).not.toContain('(0)');
  });

  it("openDrilldown charts the SESSION's own set, not whatever the channel is showing as active (T6.3 fix round 1)", async () => {
    await mount(
      results([resultEmote('a', { totalUseCount: 5 })], { emoteSetId: 'halloween-1' }),
      true,
    );
    const openSpy = vi
      .spyOn(TestBed.inject(Dialog), 'open')
      .mockReturnValue({ closed: of(undefined) } as ReturnType<Dialog['open']>);

    component['openDrilldown'](component['results']()!.emotes[0]);

    expect(openSpy).toHaveBeenCalledTimes(1);
    const data = openSpy.mock.calls[0][1]?.data as EmoteDrilldownData;
    // NOT the channel's active set (activeEmoteSetId() would be 'set-1' per this describe block's
    // mount()) — a set-session's numbers must chart under its own, frozen set.
    expect(data.emoteSetId).toBe('halloween-1');
  });

  it('openDrilldown omits emoteSetId for a null-session (falls back to the active set inside the dialog)', async () => {
    await mount(results([resultEmote('a', { totalUseCount: 5 })]), true);
    const openSpy = vi
      .spyOn(TestBed.inject(Dialog), 'open')
      .mockReturnValue({ closed: of(undefined) } as ReturnType<Dialog['open']>);

    component['openDrilldown'](component['results']()!.emotes[0]);

    const data = openSpy.mock.calls[0][1]?.data as EmoteDrilldownData;
    expect(data.emoteSetId).toBeNull();
  });

  it("targets a set-session's own NON-active set with the real active set beside it, unlocked once its live membership read lands clean (K5's set-scoped sync-deleted lifted Ruling D)", async () => {
    await mount(results([resultEmote('a')], { emoteSetId: 'halloween-1' }), true);

    // The panel's [setId] and [activeSetId] bindings: the session's own set to delete from, the
    // channel's real active set ('set-1' per this describe block's mount()) so the run's
    // bookkeeping knows the two differ. The temporary Ruling D lock existed only until sync-deleted
    // carried { emoteSetId, sevenTvEmoteIds } (K5) — deleting is locked again since #227, but only
    // for as long as the session-set membership read (below) has not landed clean.
    expect(component['canSelectForDelete']()).toBe(true);
    expect(component['massDeletePanelSetId']()).toBe('halloween-1');
    expect(component['activeEmoteSetId']()).toBe('set-1');
    expect(component['massDeleteLockReasonKey']()).toBe('massDelete.memberRead.lock.loading');

    flushByPath(httpMock, `/api/seventv/channels/${CHANNEL}/emotes`, {
      channelName: CHANNEL,
      sevenTvUserId: null,
      emoteSetId: 'halloween-1',
      emoteSetName: 'Halloween 2026',
      capacity: 500,
      totalCount: 1,
      truncated: false,
      emotes: [{ sevenTvEmoteId: '7tv-a', name: 'Emotea', defaultName: 'Emotea', imageUrl: '' }],
    });
    await settle();

    expect(component['massDeleteLockReasonKey']()).toBeNull();
  });
});

/**
 * #227 (K6 follow-up): `eligible` never reflects live departure for a set-session row (see the
 * describe block above), so a member 7TV no longer carries under the session's own set used to stay
 * selectable for delete forever — confirming issued a `RemoveEmote` for something no longer there.
 * `selectedForDelete()` now drops such a row once the live-membership read (`sessionSetMembersResource`)
 * confirms it, mirroring the usage page's `membership === 'live'` filter. The read itself is gated on
 * `canSelectForDelete()` (real `rxResource`, hence the same real-timer `settle()` idiom the block
 * above uses) so a plain voter's page view never spends a permit off the shared `ForeignEmoteLookup`
 * bucket for a check whose only consumer — the mass-delete panel — they cannot even see.
 */
describe('VoteSessionDetailPage — departed set-session members are excluded from the delete selection (#227)', () => {
  let fixture: ComponentFixture<VoteSessionDetailPage>;
  let component: VoteSessionDetailPage;
  let httpMock: HttpTestingController;

  const CHANNEL = 'sensitron';
  const SESSION_ID = '7';
  const EMOTE_SET_PATH = `/api/seventv/channels/${CHANNEL}/emotes`;

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);

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
    vi.unstubAllGlobals();
  });

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  async function mount(initial: VoteSessionResults, canManage: boolean): Promise<void> {
    flushByPath(httpMock, `/api/channels/${CHANNEL}/vote-sessions/${SESSION_ID}/results`, initial);
    flushByPath(httpMock, `/api/channels/${CHANNEL}`, {
      channelId: 'c1',
      channelName: CHANNEL,
      isBotActive: true,
      activeEmoteSetId: 'set-1',
    });
    flushByPath(httpMock, `/api/channels/${CHANNEL}/permissions`, {
      canManage,
      canViewUsageStats: canManage,
      isGlobalAdmin: false,
      isTracked: true,
      isBotActive: true,
    });
    flushByPath(httpMock, `/api/channels/${CHANNEL}/emote-sets`, {
      activeEmoteSetId: 'set-1',
      sets: [{ id: 'set-1', name: 'Main set', isActive: true, kind: 'NORMAL' }],
    });
    // A settle() alone only clears the first hop (permissionsResource -> canManage/
    // canSelectForDelete); sessionSetMembersResource is a second rxResource reacting to that
    // computed, so its own request needs a further tick to actually go out.
    await settle();
    await settle();
  }

  function flushSessionSetMembers(sevenTvEmoteIds: string[]): void {
    flushByPath(httpMock, EMOTE_SET_PATH, {
      channelName: CHANNEL,
      sevenTvUserId: null,
      emoteSetId: 'halloween-1',
      emoteSetName: 'Halloween 2026',
      capacity: 500,
      totalCount: sevenTvEmoteIds.length,
      truncated: false,
      emotes: sevenTvEmoteIds.map((id) => ({
        sevenTvEmoteId: id,
        name: id,
        defaultName: id,
        imageUrl: '',
        topAllTime: null,
        trending: null,
      })),
    });
  }

  /** Predicate-based, unlike a plain-string `httpMock.expectNone(EMOTE_SET_PATH)` would be: the
   *  request always carries `?emoteSetId=…`, and Angular's string matcher compares against
   *  `urlWithParams` — a bare path string therefore never matches it and `expectNone`/`expectOne`
   *  would trivially "pass" regardless of whether a request actually went out. `req.url` (unlike
   *  `urlWithParams`) excludes the query string, same idiom `flushByPath` already uses below. */
  function matchesEmoteSetPath(req: { url: string }): boolean {
    return req.url === EMOTE_SET_PATH;
  }

  // Opus review P1/P2 (#227): while the read is still out (or has failed/truncated), deleting is
  // now locked at the page level — this used to be pinned as "fail-open" (selectedForDelete still
  // returned both rows, relying only on MassDeletePanel's own confirm-time backstop to keep a
  // phantom delete from actually running). The panel-level backstop still exists (P1), but the
  // primary, visible behaviour a user meets here is now the lock, not a selection quietly staying
  // wrong until confirm time.
  it('locks deleting while the live-membership read is out, then excludes the departed member once it lands', async () => {
    const gone = resultEmote('gone', { totalUseCount: null });
    const stays = resultEmote('stays', { totalUseCount: null });
    await mount(results([gone, stays], { emoteSetId: 'halloween-1' }), true);

    component['selection'].onRowClick(gone, { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(stays, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['gone', 'stays']);

    // The read is still out: deleting is locked, so the selection's own fail-open content no
    // longer matters for whether a delete could actually start.
    expect(component['massDeleteLockReasonKey']()).toBe('massDelete.memberRead.lock.loading');

    flushSessionSetMembers(['7tv-stays']); // 7tv-gone is no longer a live member
    await settle();

    expect(component['massDeleteLockReasonKey']()).toBeNull();
    expect(component['selectedForDelete']().map((row) => row.emoteId)).toEqual(['stays']);
    // The card selection itself is untouched — clicking it still works, only the delete run drops
    // the departed member from what it actually sends.
    expect(component['selection'].selectedKeys().sort()).toEqual(['gone', 'stays']);
  });

  it('locks deleting when the live-membership read fails (429/503)', async () => {
    const a = resultEmote('a', { totalUseCount: null });
    await mount(results([a], { emoteSetId: 'halloween-1' }), true);

    httpMock
      .match(matchesEmoteSetPath)
      .forEach((req) =>
        req.flush('service unavailable', { status: 503, statusText: 'Service Unavailable' }),
      );
    await settle();

    expect(component['massDeleteLockReasonKey']()).toBe('massDelete.memberRead.lock.unavailable');
    // Fail-open at the data level, same reasoning as the loading case above: the panel-level
    // confirm-time read (readLiveAliasesFromSet) is the actual backstop; the lock is what stops
    // the user from reaching it in the first place.
    expect(component['departedSevenTvEmoteIds']().size).toBe(0);
  });

  it('locks deleting when the live-membership read is truncated', async () => {
    const a = resultEmote('a', { totalUseCount: null });
    await mount(results([a], { emoteSetId: 'halloween-1' }), true);

    flushByPath(httpMock, EMOTE_SET_PATH, {
      channelName: CHANNEL,
      sevenTvUserId: null,
      emoteSetId: 'halloween-1',
      emoteSetName: 'Halloween 2026',
      capacity: 500,
      totalCount: 1000,
      truncated: true,
      emotes: [],
    });
    await settle();

    expect(component['massDeleteLockReasonKey']()).toBe('massDelete.memberRead.lock.truncated');
  });

  it('makes no live-membership read at all for a null-session', async () => {
    const a = resultEmote('a');
    await mount(results([a]), true);

    httpMock.expectNone(matchesEmoteSetPath);
  });

  it('makes no live-membership read for a viewer who cannot select for delete', async () => {
    const a = resultEmote('a');
    await mount(results([a], { emoteSetId: 'halloween-1' }), false);

    httpMock.expectNone(matchesEmoteSetPath);
  });

  // Opus review P2-a: `sessionSetMembersResource`'s `params` used to read `results()` directly —
  // `results` is replaced wholesale on every reload, a new object reference each time, so the
  // resource was retriggered (and, past its 60 s cache, spent a fresh ForeignEmoteLookup permit) on
  // every one of them, not only when the session's own set actually changed (it never does,
  // mid-session). `onDeleted([])` exercises exactly that shape of replacement — its own
  // `results.update()` — without needing the SSE/debounce pipeline at all.
  // Opus review P3-a: a plain httpMock.expectNone() here would pass even on the OLD, buggy `params`
  // (reading `results()` directly) too — within loadCachedEmoteSetPreview's own 60 s TTL, a
  // retriggered, non-refresh call is served from ITS cache without ever reaching HTTP, so the
  // resource-level retrigger this asserts against is invisible at the network layer. Spying on the
  // service method itself (which the resource's `stream` always calls, cache hit or not) is what
  // actually distinguishes "the resource re-ran its stream" from "no HTTP happened to fire" — and
  // this was verified live: reverting `params` to read `results()` directly turns this red
  // (`toHaveBeenCalledTimes(1)` sees `2`), confirming the spy is not equally vacuous.
  it('does not re-request the live-membership list on a wholesale results() replacement that leaves the session set unchanged', async () => {
    const loadSpy = vi.spyOn(TestBed.inject(SevenTvEmoteSetService), 'loadCachedEmoteSetPreview');

    const a = resultEmote('a', { totalUseCount: null });
    await mount(results([a], { emoteSetId: 'halloween-1' }), true);
    flushSessionSetMembers(['7tv-a']);
    await settle();
    expect(loadSpy).toHaveBeenCalledTimes(1);

    component['onDeleted']([]); // removes nothing — the point is the new `results` reference alone
    await settle();

    expect(loadSpy).toHaveBeenCalledTimes(1);
  });

  // Opus review P2-b: with P2-a's fix, the reload `loadResults()` triggers on `channel.synced` no
  // longer changes `sessionSetMembersResource`'s params by itself — so the explicit
  // `sessionSetMembersResource.reload()` right after it is the only thing that can still trigger a
  // refetch, and it actually reaches the network rather than being silently swallowed by (or racing)
  // a spurious params-driven one.
  it('re-requests the live-membership list after a channel.synced live event', async () => {
    const a = resultEmote('a', { totalUseCount: null });
    await mount(results([a], { emoteSetId: 'halloween-1' }), true);
    flushSessionSetMembers(['7tv-a']);
    await settle();
    httpMock.expectNone(matchesEmoteSetPath);

    FakeEventSource.instances[0].emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: CHANNEL });
    // Real time, not fake timers: sessionSetMembersResource's own settling already needs real
    // microtask ticks in this describe block (see settle()), and mixing that with faked timers is
    // exactly the tension this file's other describe blocks avoid by choosing one or the other.
    await new Promise((resolve) => setTimeout(resolve, VOTE_RELOAD_DEBOUNCE_MS + 50));
    flushByPath(
      httpMock,
      `/api/channels/${CHANNEL}/vote-sessions/${SESSION_ID}/results`,
      results([a], { emoteSetId: 'halloween-1' }),
    );
    flushByPath(httpMock, `/api/channels/${CHANNEL}`, {
      channelId: 'c1',
      channelName: CHANNEL,
      isBotActive: true,
      activeEmoteSetId: 'set-1',
    });
    await settle();

    // Exactly one fresh request — proves the reload() actually reached the network.
    const refreshReq = httpMock.expectOne(matchesEmoteSetPath);
    // And that it actually bypasses loadCachedEmoteSetPreview's own cache (Opus review P3-b) — the
    // `refresh: true` SevenTvEmoteSetService.loadEmoteSetPreview turns into a `?refresh=true` query
    // param, not merely "a request happened to go out again" (which could equally be a
    // cache-serving one, if this describe block's mocks did not go through the real HTTP layer).
    expect(refreshReq.request.params.get('refresh')).toBe('true');
    refreshReq.flush({
      channelName: CHANNEL,
      sevenTvUserId: null,
      emoteSetId: 'halloween-1',
      emoteSetName: 'Halloween 2026',
      capacity: 500,
      totalCount: 1,
      truncated: false,
      emotes: [{ sevenTvEmoteId: '7tv-a', name: 'Emotea', defaultName: 'Emotea', imageUrl: '' }],
    });
  });
});

/**
 * `canDrilldown(emote)`/`rowAction(emote)` (arbitrated review round 2): a per-row narrowing of
 * `cellAction()` so a set-session row with no chartable usage number never claims the drilldown —
 * `emote.totalUseCount === null` alone is not the test (a null-session's archived row can carry
 * that for an unrelated reason and must keep its drilldown), only `results().emoteSetId != null`
 * together with it is. Mounted on a COARSE pointer, where `cellAction()` is `'drilldown'` for the
 * whole page whenever `hasUsageData()` holds (same stub as usage-stats-page.spec.ts's own
 * "mark-all does not exist on a coarse pointer" block) — on a fine pointer `cellAction()` is
 * `'select'` instead, which would never exercise the downgrade this checks.
 */
describe('VoteSessionDetailPage — canDrilldown/rowAction withhold the drilldown from a rowless usage number (arbitrated review round 2)', () => {
  let fixture: ComponentFixture<VoteSessionDetailPage>;
  let component: VoteSessionDetailPage;
  let httpMock: HttpTestingController;

  const CHANNEL = 'sensitron';
  const SESSION_ID = '7';

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    vi.stubGlobal('matchMedia', () => ({
      matches: true,
      media: '',
      onchange: null,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      addListener: () => undefined,
      removeListener: () => undefined,
      dispatchEvent: () => false,
    }));

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
    vi.unstubAllGlobals();
  });

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  async function mount(initial: VoteSessionResults): Promise<void> {
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
    flushByPath(httpMock, `/api/channels/${CHANNEL}/emote-sets`, {
      activeEmoteSetId: 'set-1',
      sets: [
        { id: 'set-1', name: 'Main set', isActive: true, kind: 'NORMAL' },
        { id: 'halloween-1', name: 'Halloween 2026', isActive: false, kind: 'NORMAL' },
      ],
    });
    await settle();
  }

  it('withholds the drilldown from a set-session row with no chartable usage number, keeps it for the counted row', async () => {
    const openSpy = vi
      .spyOn(TestBed.inject(Dialog), 'open')
      .mockReturnValue({ closed: of(undefined) } as ReturnType<Dialog['open']>);
    await mount(
      results(
        [
          resultEmote('counted', { totalUseCount: 5 }),
          resultEmote('uncounted', { totalUseCount: null }),
        ],
        { emoteSetId: 'halloween-1' },
      ),
    );
    const [counted, uncounted] = component['results']()!.emotes;

    expect(component['canDrilldown'](counted)).toBe(true);
    expect(component['rowAction'](counted)).toBe('drilldown');
    expect(component['canDrilldown'](uncounted)).toBe(false);
    expect(component['rowAction'](uncounted)).toBe('none');

    component['openDrilldown'](uncounted);
    expect(openSpy).not.toHaveBeenCalled();

    component['openDrilldown'](counted);
    expect(openSpy).toHaveBeenCalledTimes(1);
  });

  it("keeps the drilldown for a null-session's archived row with null usage — a withheld tally, not a set-session gap", async () => {
    const openSpy = vi
      .spyOn(TestBed.inject(Dialog), 'open')
      .mockReturnValue({ closed: of(undefined) } as ReturnType<Dialog['open']>);
    await mount(
      results([
        resultEmote('a', { totalUseCount: 5 }),
        resultEmote('archived', { totalUseCount: null, isArchived: true, eligible: false }),
      ]),
    );
    const archived = component['results']()!.emotes.find((emote) => emote.emoteId === 'archived')!;

    expect(component['canDrilldown'](archived)).toBe(true);
    expect(component['rowAction'](archived)).toBe('drilldown');

    component['openDrilldown'](archived);
    expect(openSpy).toHaveBeenCalledTimes(1);
  });
});

/**
 * The export button and the low-participation notice are mod-team-only (`canViewUsageStats`, a
 * superset of `canManage` that also admits 7TV editors — see DECISIONS). Unlike the other blocks
 * in this file, the real template is kept (no `TestBed.overrideComponent`) — same pattern as
 * `usage-stats-page.spec.ts`'s "selection-pruned notice accessibility" block — because presence in
 * the DOM, not a signal's value, is what these two lock decisions are actually about. Mounted with
 * an empty ballot throughout: `hasUsageData()`/`canSelectForDelete()` then stay false, so neither
 * the mass-delete panel nor the sprite grid renders, and the only thing left to assert on is the
 * header button and the notice banner.
 */
describe('VoteSessionDetailPage — export button and low-participation notice are mod-team-only', () => {
  let fixture: ComponentFixture<VoteSessionDetailPage>;
  let httpMock: HttpTestingController;

  const CHANNEL = 'sensitron';
  const SESSION_ID = '7';
  // TranslocoTestingModule below is configured with an empty `de` catalog, so every `| transloco`
  // renders its raw key rather than real copy (same pattern as usage-stats-page.spec.ts asserting
  // `hiddenSelectedFilterKey()` against a key, not a sentence) — matches Rule 12: identifying which
  // notice/button appeared, not pinning a translation's wording.
  const EXPORT_ARIA_LABEL = 'export.buttonAriaLabel';

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);

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

    fixture = TestBed.createComponent(VoteSessionDetailPage);
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', CHANNEL);
    fixture.componentRef.setInput('sessionId', SESSION_ID);
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  /**
   * Voter count under LOW_PARTICIPATION_THRESHOLD on an active, non-secret session, so
   * `showLowParticipation()` is true independently of the permission this block is about — every
   * case here isolates `canViewUsageStats` as the only varying input.
   *
   * `withWithheldTallies` additionally sets `hideResultsUntilEnd` and gives the one emote a
   * null tally, which — independently of `canViewUsageStats` — makes `talliesWithheld()` true and
   * renders the `resultsHiddenNotice` banner alongside whatever the low-participation assertion is
   * checking. Codex review [P3]: without a second real notice on screen, a test asserting "no
   * `app-notice-banner` at all" cannot tell a correctly-gated low-participation notice apart from a
   * mount that renders no banner for an unrelated reason.
   *
   * `permissionsResource` (`rxResource`) settles its `value()`/`status()` signals through a
   * microtask rather than inside `TestRequest.flush()` itself — unlike the plain `HttpClient`
   * calls this page also makes, flushing it and calling `detectChanges()` right after is not
   * enough for the DOM to reflect it yet. The other describe blocks in this file never hit this,
   * because none of them assert on anything downstream of `canManage()`/`canViewUsageStats()`.
   * A zero-delay `setTimeout` round-trip drains that microtask before the final `detectChanges()`.
   */
  async function mount(
    canViewUsageStats: boolean,
    { withWithheldTallies = false }: { withWithheldTallies?: boolean } = {},
  ): Promise<void> {
    flushByPath(httpMock, `/api/channels/${CHANNEL}/vote-sessions/${SESSION_ID}/results`, {
      sessionId: Number(SESSION_ID),
      title: 'Test session',
      allowedVoterRoles: 1,
      isActive: true,
      startedAt: '2026-01-01T00:00:00Z',
      endedAt: null,
      voterCount: 2,
      hideResultsUntilEnd: withWithheldTallies,
      emotes: withWithheldTallies
        ? [
            {
              emoteId: 'e1',
              emoteName: 'Emote1',
              sevenTvEmoteId: '7tv-e1',
              // Not '' — this describe block keeps the real template (unlike the rest of this
              // file), so the sprite actually mounts and NgOptimizedImage rejects an empty ngSrc
              // (NG02952).
              imageUrl: 'https://cdn.7tv.app/emote/1/1x.webp',
              totalUseCount: null,
              keepVotes: null,
              deleteVotes: null,
              score: null,
              isArchived: false,
              myVote: null,
            },
          ]
        : [],
    });
    flushByPath(httpMock, `/api/channels/${CHANNEL}`, {
      channelId: 'c1',
      channelName: CHANNEL,
      isBotActive: true,
      activeEmoteSetId: 'set-1',
    });
    flushByPath(httpMock, `/api/channels/${CHANNEL}/permissions`, {
      canManage: false,
      canViewUsageStats,
      isGlobalAdmin: false,
      isTracked: true,
      isBotActive: true,
    });
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  /**
   * Finds a `role="status"` element by its rendered content rather than by tag — `app-notice-banner`
   * is not the only thing that carries the role (the emote-count paragraph and
   * `DockOutcomeAnnouncer`'s permanent sr-only region do too), and more than one *notice banner* can
   * be on screen at once (Codex review [P3]; see `withWithheldTallies` above). Matching on the
   * rendered key, not a tag, is what actually identifies which notice this is — the tag alone
   * cannot.
   */
  function noticeWithText(text: string): Element | null {
    const candidates: Element[] = Array.from(
      fixture.nativeElement.querySelectorAll('[role="status"]'),
    );
    return candidates.find((element) => element.textContent?.includes(text)) ?? null;
  }

  it('shows the export button to a mod-team viewer', async () => {
    await mount(true);

    const button: HTMLButtonElement | null = fixture.nativeElement.querySelector(
      `button[aria-label="${EXPORT_ARIA_LABEL}"]`,
    );
    expect(button).not.toBeNull();
  });

  it('hides the export button from a plain voter', async () => {
    await mount(false);

    const button: HTMLButtonElement | null = fixture.nativeElement.querySelector(
      `button[aria-label="${EXPORT_ARIA_LABEL}"]`,
    );
    expect(button).toBeNull();
  });

  it('shows the low-participation notice to a mod-team viewer', async () => {
    await mount(true);

    // voterCount: 2 selects the plural form via pluralKey() — see the key's own definition.
    expect(noticeWithText('voting.detail.lowParticipation.other')).not.toBeNull();
  });

  it('hides the low-participation notice from a plain voter, even though the count is under the threshold and another notice is showing', async () => {
    await mount(false, { withWithheldTallies: true });

    // Sanity check: a real, different notice really is on screen — proves the assertion below is
    // about this specific notice, not merely "no banner rendered at all" (Codex review [P3]).
    expect(noticeWithText('voting.detail.resultsHiddenNotice')).not.toBeNull();
    expect(noticeWithText('voting.detail.lowParticipation')).toBeNull();
  });
});
