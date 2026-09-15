/**
 * The first spec in this repo that mounts a routed *page* component rather than a service, guard,
 * dialog or a shared/ presentational piece — no established pattern existed for this (see the
 * feasibility spike this file grew out of), so a few choices here are worth spelling out for
 * whoever touches `UsageStatsPage` next.
 *
 * **The template is replaced, not the logic.** `TestBed.overrideComponent` swaps the real 825-line
 * template — the whole atlas/sidecar/dock component graph, each with its own dependencies — for two
 * bare `<div>`s. That is not disabling behaviour under test: it exists purely so the constructor's
 * `viewChild.required<ElementRef>('sheet')`/`('stickyBar')` reads succeed and their `ResizeObserver`
 * effects can attach to *something*, instead of throwing `NG0951` for a ref the real template would
 * only ever satisfy through components this test has no reason to construct. Every signal, computed,
 * effect and HTTP call in the class itself runs unmodified.
 *
 * **The HTTP round sequence and the two `fixture.detectChanges()` calls are load()'s choreography,
 * not this spec's.** `load()` fires `getSetStatus` unconditionally, then only fires `getTotals`/
 * `getChannelSeries` once `rangeResolved` is true — which for the "all time" default requires a
 * *second* effect run after the tracked-since date corrects `from()` (see that effect's own comment
 * in usage-stats-page.ts). This spec has to flush requests and tick change detection in the same
 * order and count `load()` actually produces. A refactor of `load()`, the "all time" effect, or
 * `refreshSetStatus()` itself will very likely change how many HTTP round trips happen and in what
 * order — when that happens, adjust the flushing choreography below to match the new sequence, not
 * the assertions at the end of the test, which are the actual thing under test.
 *
 * There are three cases below, not one. The first two cover the discard branch — A's stale answer
 * can land in either of two windows, before B's own load has finished or after, and only the
 * second is the shape #112 actually reported: it is the one where `setStatusChannel` has already
 * been bumped to `'b'` by B's own success before A's answer arrives, which is what let the pre-fix
 * code's unconditional `setStatus.set(status)` slip A's set id in under a
 * `setStatusChannel`/`importScopeCurrent` that still read as correct. The third case covers the
 * other branch of the same `if` — the successful silent refresh that *does* get to claim the
 * channel, which is the other half of the fix (`setStatusChannel` being written at all, not just
 * being guarded).
 */
import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { channelLiveUrl, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { CHANNEL_RELOAD_DEBOUNCE_MS } from '../../core/live/live-reload';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { EmoteUsageTotal } from '../../core/usage-stats/usage-stat.model';
import { CSV_MIME } from '../../shared/export/csv';
import { ExportDialogData } from '../../shared/export/export-dialog';
import { JSON_MIME } from '../../shared/export/export-envelope';
import { ExportPurposeId } from '../../shared/export/usage-export-purposes';
import { CreateVoteSessionDialogData } from './create-vote-session-dialog';
import { UsageStatsPage } from './usage-stats-page';

/**
 * `openExport()`'s `downloadFile(...)` call (usage-stats-page.ts) is a real `<a download>` click
 * against a real `Blob`/object URL — the Angular unit-test system refuses `vi.mock` for relative
 * imports, so this spy sits at the same seam `file-download.spec.ts` already uses (`URL
 * .createObjectURL`, `document.createElement('a')`) rather than mocking the module. What each
 * purpose *serializes* is `usage-export-purposes.spec.ts`'s job; this only pins which download a
 * given dialog choice produces (filename shape + MIME type) and that a cancel produces none.
 */
interface CapturedDownload {
  filename: string;
  mimeType: string;
}

/** Spies on the same two seams `downloadFile` touches — restore via `vi.restoreAllMocks()` in
 *  `afterEach`, matching `file-download.spec.ts`'s own pattern. */
function captureDownloads(): CapturedDownload[] {
  const downloads: CapturedDownload[] = [];
  if (!('createObjectURL' in URL)) {
    Object.assign(URL, { createObjectURL: () => '', revokeObjectURL: () => undefined });
  }
  vi.spyOn(URL, 'createObjectURL').mockImplementation((blob: Blob | MediaSource) => {
    downloads.push({ filename: '', mimeType: (blob as Blob).type });
    return 'blob:test';
  });
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);

  const originalCreateElement = document.createElement.bind(document);
  vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
    const element = originalCreateElement(tag);
    if (tag === 'a') {
      vi.spyOn(element as HTMLAnchorElement, 'click').mockImplementation(() => {
        // download is set before click() in downloadFile — the entry pushed by createObjectURL
        // just above is the one this click belongs to.
        const pending = downloads[downloads.length - 1];
        if (pending) {
          pending.filename = (element as HTMLAnchorElement).download;
        }
      });
    }
    return element;
  });
  return downloads;
}

/** Same stand-in as core/live/live-reload.spec.ts — jsdom ships no EventSource at all. */
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

  emit(event: { type: string; channel?: string }): void {
    this.onmessage?.({ data: JSON.stringify(event) } as MessageEvent);
  }
}

/** jsdom implements no ResizeObserver either — two of the constructor's effects touch it. */
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

function setStatus(overrides: Partial<EmoteSetStatus>): EmoteSetStatus {
  return {
    activeEmoteSetId: 'set-a',
    capacity: 600,
    occupiedSlots: 10,
    trackedSince: '2026-01-01T00:00:00Z',
    syncFailureReason: null,
    lastSyncAttemptAtUtc: null,
    botsExcludedSince: null,
    sharedChatSeparatedSince: null,
    duplicateNames: [],
    ...overrides,
  };
}

function emote(id: string, name: string, totalUseCount = 10): EmoteUsageTotal {
  return {
    emoteId: id,
    emoteName: name,
    sevenTvEmoteId: `7tv-${id}`,
    imageUrl: '',
    totalUseCount,
    lastUsedDate: null,
    previousWindowUseCount: 0,
    firstSeenAt: null,
  };
}

/**
 * `HttpTestingController.match('/some/path')` compares the string against `urlWithParams` — so it
 * silently matches nothing (and flushes nothing) for `getTotals`/`getChannelSeries`, which both
 * carry a `?from=&to=` query string. This matches on the path alone, the way every call site below
 * actually means it.
 */
function flushByPath(mock: HttpTestingController, path: string, body: object | unknown[]): void {
  mock.match((req) => req.url === path).forEach((testReq) => testReq.flush(body));
}

describe('UsageStatsPage — refreshSetStatus channel race (#112 regression)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;

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

    // The real 825-line template pulls in the whole atlas/sidecar/dock component graph. Nothing
    // under test here reads from the DOM — only the two `viewChild.required` refs the constructor's
    // ResizeObserver effects need to resolve without throwing NG0951.
    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("discards A's late refreshSetStatus() answer arriving before B's own load has finished", () => {
    // --- Mount on channel A: permissions + the initial getSetStatus + totals/series once the
    // "all time" range resolves against A's trackedSince. ---
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });

    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    // The trackedSince above corrects from(), which reruns the load effect and fires totals/series.
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['setStatusChannel']()).toBe('a');

    // --- A channel.synced burst on A's live stream fires refreshSetStatus() for A. Leave that
    // request pending — this is the in-flight request the switch below must outrun. ---
    expect(FakeEventSource.instances).toHaveLength(1);
    const sourceA = FakeEventSource.instances[0];
    expect(sourceA.url).toBe(channelLiveUrl('a'));

    sourceA.emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: 'a' });
    vi.advanceTimersByTime(1000); // CHANNEL_RELOAD_DEBOUNCE_MS
    fixture.detectChanges();

    // The liveReload burst also re-triggers a quiet totals reload for A — drain it, it is not part
    // of what this test is pinning down.
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);

    const staleStatusReq = httpMock.expectOne('/api/channels/a/emotes/active-set');

    // --- Switch channels within the same route before A's refresh answers. ---
    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();

    // The switch closes A's live connection and opens B's.
    expect(sourceA.closeCount).toBe(1);
    expect(FakeEventSource.instances).toHaveLength(2);
    expect(FakeEventSource.instances[1].url).toBe(channelLiveUrl('b'));

    // load() fires B's own getSetStatus immediately (rangeResolved is false for the new channel).
    const freshStatusReqB = httpMock.expectOne('/api/channels/b/emotes/active-set');
    httpMock
      .expectOne('/api/channels/b/permissions')
      .flush({ canManage: true, canViewUsageStats: true });

    // --- Now A's stale answer lands. refreshSetStatus() must discard it: channelName() is 'b'. ---
    staleStatusReq.flush(
      setStatus({ activeEmoteSetId: 'set-a-REFRESHED', trackedSince: '2026-01-01T00:00:00Z' }),
    );

    // Still A's *pre-refresh* status — the stale answer must not have landed under B's name, and it
    // must not have landed at all (setStatusChannel is unchanged, still naming A from the mount-time
    // fetch, not bumped to 'b' by the discarded response).
    expect(component['setStatusChannel']()).toBe('a');
    expect(component['setStatus']()?.activeEmoteSetId).toBe('set-a');

    // --- B's own answer lands and is what the page adopts. ---
    freshStatusReqB.flush(
      setStatus({ activeEmoteSetId: 'set-b', trackedSince: '2026-02-01T00:00:00Z' }),
    );
    // Two ticks: the first runs the "all time" effect that corrects from() against B's trackedSince,
    // the second reruns the load effect (which reads the now-corrected from()) and fires B's totals
    // and series requests — see the "all time" effect's own comment for why this is a second run
    // rather than the same one.
    fixture.detectChanges();
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/b/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/b/usage-stats/series', {
      from: '2026-02-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['setStatus']()?.activeEmoteSetId).toBe('set-b');
    expect(component['setStatusChannel']()).toBe('b');
    expect(component['importScopeCurrent']()).toBe(true);
  });

  it("discards A's late refreshSetStatus() answer arriving after B's own load has already finished", () => {
    // --- Mount on channel A: permissions + the initial getSetStatus + totals/series once the
    // "all time" range resolves against A's trackedSince. Identical to the "before" case above —
    // duplicated rather than shared, so the flush order at each call site stays visible. ---
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });

    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    // --- A channel.synced burst on A's live stream fires refreshSetStatus() for A. Leave that
    // request pending — this is the in-flight request B's full load below must outrun. ---
    const sourceA = FakeEventSource.instances[0];
    sourceA.emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: 'a' });
    vi.advanceTimersByTime(1000); // CHANNEL_RELOAD_DEBOUNCE_MS
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);

    const staleStatusReq = httpMock.expectOne('/api/channels/a/emotes/active-set');

    // --- Switch channels within the same route before A's refresh answers. ---
    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();

    // --- Unlike the "before" case, B's own load is driven all the way to completion here: status,
    // both change-detection ticks, and totals/series. This is what puts setStatusChannel on 'b'
    // *before* A's stale answer lands, and it is the ordering #112 actually reported. ---
    httpMock
      .expectOne('/api/channels/b/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-b', trackedSince: '2026-02-01T00:00:00Z' }));
    httpMock
      .expectOne('/api/channels/b/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    fixture.detectChanges();
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/b/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/b/usage-stats/series', {
      from: '2026-02-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    // B has genuinely, fully landed — importScopeCurrent() is true here for the right reason,
    // before A's stale answer is even in the picture.
    expect(component['setStatus']()?.activeEmoteSetId).toBe('set-b');
    expect(component['setStatusChannel']()).toBe('b');
    expect(component['importScopeCurrent']()).toBe(true);

    // --- Only now does A's stale answer land. Pre-fix, this is the dangerous window: the old code
    // wrote setStatus unconditionally but never touched setStatusChannel, so setStatusChannel was
    // already 'b' here and importScopeIsCurrent('b', 'b', 'b') kept reporting true — over A's set id.
    // The fix's channel-freeze guard must drop this answer instead. ---
    staleStatusReq.flush(
      setStatus({ activeEmoteSetId: 'set-a-REFRESHED', trackedSince: '2026-01-01T00:00:00Z' }),
    );

    expect(component['setStatus']()?.activeEmoteSetId).toBe('set-b');
    expect(component['setStatusChannel']()).toBe('b');
    expect(component['importScopeCurrent']()).toBe(true);
  });

  it('lets a channel.synced-triggered refreshSetStatus() claim the channel after the initial status fetch failed', () => {
    // --- Mount on channel A, but the initial getSetStatus fails. load()'s error branch sets
    // setStatus to null and setStatusFailedChannel to 'a', deliberately leaving setStatusChannel
    // untouched (see its own comment) — nothing has "claimed" the channel yet. ---
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });

    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(null, { status: 500, statusText: 'Server Error' });
    // setStatusFailedChannel flips rangeResolved true for this channel (see its own comment), so
    // the same effect rerun that absorbed the error also fires totals/series against the
    // still-placeholder range — nothing ever corrected from() since setStatus stayed null.
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2025-09-09',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['setStatusChannel']()).toBeNull();
    expect(component['importScopeCurrent']()).toBe(false);

    // --- A channel.synced burst on A's live stream fires refreshSetStatus() for A. ---
    const sourceA = FakeEventSource.instances[0];
    sourceA.emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: 'a' });
    vi.advanceTimersByTime(1000); // CHANNEL_RELOAD_DEBOUNCE_MS
    fixture.detectChanges();

    // The same burst also re-triggers a quiet totals reload — drain it, it is not part of what
    // this test is pinning down.
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);

    // --- The silent refresh succeeds this time. ---
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));

    // The successful refresh is what finally gets to name the channel — setStatusChannel was
    // deliberately left null by the earlier failure (load()'s error branch, see its own comment),
    // and this write is the fix's other half: refreshSetStatus()'s success branch mirrors load()'s
    // own, writing setStatusChannel alongside setStatus rather than leaving it stale.
    expect(component['setStatus']()?.activeEmoteSetId).toBe('set-a');
    expect(component['setStatusChannel']()).toBe('a');
    // totalsChannel was already 'a' — set alongside the totals fired earlier once the failure
    // resolved rangeResolved — so this is also where importScopeCurrent() turns true.
    expect(component['importScopeCurrent']()).toBe(true);
  });
});

/**
 * duplicateNames()/duplicatesExpanded() (#45). duplicateNames() is a computed derived from
 * setStatus()/setStatusChannel(), guarded by the same channel-freeze check importScopeCurrent()
 * relies on (see the #112 block above) — setStatus() keeps the outgoing channel's last answer on
 * screen until the incoming channel's own request lands (see setStatusChannel's own comment), and
 * duplicateNames() must not read through that window. duplicatesExpanded() gets its own, narrower
 * effect, keyed on channelName() alone, so a same-channel rerun of load()'s constructor effect
 * cannot collapse a panel the user just opened — only a genuine channel switch does.
 */
describe('UsageStatsPage — duplicateNames() channel guard and expand-state reset (#45)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;

  const DUPES_A = [
    { name: 'ApuDrums', emotes: [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', imageUrl: '' }] },
  ];
  const DUPES_B = [
    { name: 'PogU', emotes: [{ emoteId: 'e2', sevenTvEmoteId: '7tv-2', imageUrl: '' }] },
  ];

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

    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("does not surface the outgoing channel's collisions while the incoming channel's own active-set answer is still in flight", () => {
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ trackedSince: '2026-01-01T00:00:00Z', duplicateNames: DUPES_A }));
    fixture.detectChanges();
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['duplicateNames']()).toEqual(DUPES_A);

    // Switch channels before B's own active-set answer lands.
    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();

    // setStatusChannel is still 'a' — A's collisions must not show up under B's heading, even
    // though setStatus() itself still holds A's last answer (see setStatusChannel's own comment).
    expect(component['setStatusChannel']()).toBe('a');
    expect(component['duplicateNames']()).toEqual([]);

    httpMock
      .expectOne('/api/channels/b/emotes/active-set')
      .flush(setStatus({ trackedSince: '2026-02-01T00:00:00Z', duplicateNames: DUPES_B }));
    httpMock
      .expectOne('/api/channels/b/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    fixture.detectChanges();
    fixture.detectChanges();
    flushByPath(httpMock, '/api/channels/b/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/b/usage-stats/series', {
      from: '2026-02-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['duplicateNames']()).toEqual(DUPES_B);
  });

  it('does not collapse an already-expanded details panel on a same-channel rerun of load() (the "all time" range correction), but a genuine channel switch does', () => {
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });

    // Expanded before this channel's own active-set answer lands: there is nothing to collapse yet,
    // so the only way this could read false below is a reset that fired on the rerun exercised next.
    component['duplicatesExpanded'].set(true);
    expect(component['duplicatesExpanded']()).toBe(true);

    // rangePreset() defaults to "all", so from() starts at the placeholder span (see its own
    // declaration) until this answer names the tracking start. Applying that correction reruns
    // load()'s constructor effect a second time for the *same* channel — the range-only rerun the
    // dedicated reset effect (keyed on channelName() alone) must not react to, unlike a channel
    // switch.
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ trackedSince: '2026-01-01T00:00:00Z', duplicateNames: DUPES_A }));
    fixture.detectChanges();
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['duplicatesExpanded']()).toBe(true);

    // A genuine channel switch does collapse it, even before the new channel's own data lands.
    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();
    expect(component['duplicatesExpanded']()).toBe(false);
  });
});

/**
 * #94: a silent reload (`preserveSelection: true`) must reconcile the selection against the
 * emotes it actually loaded, not leave a since-deleted emote's key sitting in `selectedKeys()`
 * forever — and it must not overcorrect by dropping a row that only fell out of the *filtered*
 * view, which is a completely different, already-solved case (`retainVisible()`, S2-16).
 *
 * Separate `describe`/`beforeEach` from the block above, matching this spec's own established
 * choice to duplicate mount choreography per scenario rather than share it (see the file doc) —
 * each test's totals/status payload differs enough that a shared setup would obscure more than it
 * saves.
 */
describe('UsageStatsPage — silent reload reconciles the selection (#94)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;

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

    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  /**
   * Drives the page through a full mount on channel 'a' with the given totals. `botsExcludedSince`/
   * `sharedChatSeparatedSince` are both given a date so `SetStatusFlushProbeGate.shouldRefreshOn`
   * short-circuits to `false` and the reload each test fires afterwards produces no extra
   * `/emotes/active-set` request — the scenario under test is the totals reconciliation alone.
   */
  function mount(totals: EmoteUsageTotal[]): void {
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });

    httpMock.expectOne('/api/channels/a/emotes/active-set').flush(
      setStatus({
        activeEmoteSetId: 'set-a',
        trackedSince: '2026-01-01T00:00:00Z',
        botsExcludedSince: '2026-01-02T00:00:00Z',
        sharedChatSeparatedSince: '2026-01-02T00:00:00Z',
      }),
    );
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', totals);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
  }

  /** Fires one `usageFlushed` burst (the silent, `preserveSelection`d reload path) and flushes its
   *  totals response — the same round trip `loadTotals(..., {preserveSelection: true, silent:
   *  true})` produces from the live subscription in the constructor. */
  function silentReload(totals: EmoteUsageTotal[]): void {
    FakeEventSource.instances[0].emit({ type: LIVE_EVENT_TYPES.usageFlushed, channel: 'a' });
    vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
    fixture.detectChanges();
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', totals);
  }

  it('reconciles a full pre-reload selection against what the reload actually returned', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    const c = emote('c', 'PeepoC');

    mount([a, b, c]);
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(c, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'c']);
    expect(component['selectionPrunedFeedback']()).toBeNull();

    // The silent reload comes back without 'c' — deleted externally on 7TV between loads.
    silentReload([a, b]);

    // 'a' survives, 'c' is gone from the authoritative key set, and the transient feedback names
    // exactly one dropped emote.
    expect(component['selection'].selectedKeys()).toEqual(['a']);
    expect(component['selectionPrunedFeedback']()).toEqual({
      key: 'usageStats.selectionPruned.one',
      count: 1,
    });

    // The feedback is transient — it clears itself after SELECTION_PRUNED_FEEDBACK_MS.
    vi.advanceTimersByTime(4000);
    expect(component['selectionPrunedFeedback']()).toBeNull();
  });

  it('a silent reload that loses nothing selected leaves the selection and the feedback alone', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');

    mount([a, b]);
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);

    silentReload([a, b]);

    expect(component['selection'].selectedKeys()).toEqual(['a']);
    expect(component['selectionPrunedFeedback']()).toBeNull();
  });

  it('a merely filtered-out but still-loaded emote survives a silent reload and shows no feedback', () => {
    // This is the case that decides retainAmong(emotes) over retainVisible()/atlasOrder(): 'c'
    // starts above the min-usage filter, gets selected, and the reload lowers its count below that
    // same filter — so it drops out of atlasOrder() (the filtered view) while still being part of
    // the reloaded, unfiltered set. Reconciling against atlasOrder() would wrongly report it as
    // "gone" (#94); reconciling against the raw reload payload must not.
    const a = emote('a', 'PeepoA', 10);
    const c = emote('c', 'PeepoC', 10);

    mount([a, c]);

    // A min-usage filter of 5 — both emotes currently clear it, so retainVisible() (fired by the
    // filter change itself, unrelated to the reload below) does not touch the selection made next.
    component['usageFilter'].setRange(5, null);
    component['selection'].onRowClick(c, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['c']);
    expect(component['atlasOrder']().map((e: EmoteUsageTotal) => e.emoteId)).toContain('c');

    // The reload drops 'c's count under the filter's floor — atlasOrder() will no longer include
    // it — but 'c' itself is still present in the reloaded payload.
    silentReload([a, emote('c', 'PeepoC', 1)]);

    // Confirms the filter really did narrow atlasOrder() past 'c' — otherwise this test would not
    // be exercising the case it claims to.
    expect(component['atlasOrder']().map((e: EmoteUsageTotal) => e.emoteId)).not.toContain('c');
    // ...yet the selection and the feedback are both untouched: 'c' was never actually removed
    // from the set, only filtered out of the current view.
    expect(component['selection'].selectedKeys()).toEqual(['c']);
    expect(component['selectionPrunedFeedback']()).toBeNull();
  });
});

/**
 * Unlike every other describe block above, this one does NOT override the template with bare
 * `<div>`s — the whole point here is the actual markup in usage-stats-page.html, not the signal
 * behind it (that reconciliation logic is what the block above already covers). Mounting the real
 * 825-line template turned out to work cleanly against the same providers the other blocks already
 * set up (no extra DI needed for the child component graph), so there was no reason to duplicate the
 * bare-div trick just for these two elements.
 */
describe('UsageStatsPage — selection-pruned notice accessibility (#94 follow-up)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;

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

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  /** Mounts the page on the given channel with empty totals — the markup under test here does not
   *  depend on any row being present. */
  function mount(channelName: string): void {
    httpMock
      .expectOne(`/api/channels/${channelName}/permissions`)
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne(`/api/channels/${channelName}/emotes/active-set`)
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();
    flushByPath(httpMock, `/api/channels/${channelName}/usage-stats/totals`, []);
    flushByPath(httpMock, `/api/channels/${channelName}/usage-stats/series`, {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
  }

  it('mounts the sr-only role="status" region even without a standing message, and fills it once there is one', () => {
    mount('a');

    // Permanent: present in the DOM before anything was ever pruned, per the app-shell.ts precedent
    // (a live region that only comes into existence together with its content announces nothing to
    // most screen reader/browser pairings, which only announce a *mutation* inside an
    // already-existing region — see usage-stats-page.html's comment on this element).
    const region = fixture.nativeElement.querySelector('span[role="status"]');
    expect(region).not.toBeNull();
    expect(region.textContent.trim()).toBe('');

    component['showSelectionPrunedFeedback'](1);
    fixture.detectChanges();

    // Same element, now carrying the message — not a second region that replaced it.
    const regionAfter = fixture.nativeElement.querySelector('span[role="status"]');
    expect(regionAfter).toBe(region);
    expect(regionAfter.textContent.trim().length).toBeGreaterThan(0);
  });

  it('keeps the visible companion span aria-hidden, with no role of its own, so the message is not announced twice', () => {
    mount('a');
    component['showSelectionPrunedFeedback'](1);
    fixture.detectChanges();

    const region = fixture.nativeElement.querySelector('span[role="status"]');
    // The visible span is the region's immediate next sibling in the template — see the comment on
    // this element in usage-stats-page.html and its record in docs/UI-Designsprache.md §4.5.
    const visible = region.nextElementSibling as HTMLElement;
    expect(visible).not.toBeNull();
    expect(visible.getAttribute('aria-hidden')).toBe('true');
    // Removed deliberately (it used to carry role="status" before this fix) — a role here would be
    // redundant with the sr-only region and, worse, would risk announcing the message a second time.
    expect(visible.getAttribute('role')).toBeNull();
    // Carries the same message, just for sighted users this time.
    expect(visible.textContent?.trim()).toBe(region.textContent.trim());
  });

  it("clears a standing notice immediately on a channel switch, and the old channel's timer never fires on the new one", () => {
    mount('a');

    component['showSelectionPrunedFeedback'](1);
    expect(component['selectionPrunedFeedback']()).not.toBeNull();

    // One second into channel A's 4-second window, the moderator switches channels.
    vi.advanceTimersByTime(1000);
    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();

    // Cleared synchronously by load() — not left standing until A's leftover timer would have fired
    // at the 4-second mark (#94 follow-up P3).
    expect(component['selectionPrunedFeedback']()).toBeNull();

    // A genuine new notice arrives on channel B, starting its own, independent 4-second window.
    component['showSelectionPrunedFeedback'](2);
    expect(component['selectionPrunedFeedback']()).toEqual({
      key: 'usageStats.selectionPruned.other',
      count: 2,
    });

    // Advance to just before channel A's ORIGINAL timeout would have fired (4000ms after it was
    // started, i.e. 3000ms after the switch at the 1000ms mark above). If A's timeout had survived
    // the switch uncleared, this is where it would wrongly null out B's still-valid notice a full
    // second before B's own timer is due.
    vi.advanceTimersByTime(2999);
    expect(component['selectionPrunedFeedback']()).not.toBeNull();
    vi.advanceTimersByTime(2);
    // Past A's original deadline now — B's notice must still stand, proving A's timeout was actually
    // cleared rather than merely superseded by a later write that happened to agree with it.
    expect(component['selectionPrunedFeedback']()).not.toBeNull();

    // B's own timer, started fresh 1000ms into this test, is due 4000ms later — advance the
    // remaining distance from where the previous two advances left off (2999 + 2 = 3001 so far).
    vi.advanceTimersByTime(4000 - 3001);
    expect(component['selectionPrunedFeedback']()).toBeNull();
  });
});

/**
 * `openExport()` (#141): capture, open the dialog, hand the choice to `usage-export-purposes.ts`
 * and — unless it closed with nothing, or the emote-list branch's unreachable null-download case
 * — trigger exactly one download (Regel 12: dialog return values are behaviour worth pinning).
 * What each purpose *serializes* is `usage-export-purposes.spec.ts`'s job; this only pins which
 * download a given choice produces and that a cancel produces none. `Dialog` is stubbed at the DI
 * boundary (same pattern as `mass-delete-panel.spec.ts`) rather than driving the real CDK overlay,
 * so `openExportDialog`'s own wrapper code still runs for real — only `Dialog.open` itself is a
 * spy, returning a `DialogRef`-shaped stand-in whose `closed` is under the test's control.
 */
describe('UsageStatsPage — openExport() (#141)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;
  let openSpy: ReturnType<typeof vi.fn>;
  let downloads: CapturedDownload[];

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    vi.useFakeTimers();
    downloads = captureDownloads();
    openSpy = vi.fn();

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
        { provide: Dialog, useValue: { open: openSpy } as unknown as Dialog },
      ],
    });

    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    // Spies only (URL.createObjectURL/revokeObjectURL, document.createElement) — matching
    // file-download.spec.ts's own cleanup, never replacing the global URL object outright.
    vi.restoreAllMocks();
  });

  /** Mounts channel 'a' with an active 7TV set (E3 offers the emote-list purpose) and given
   *  totals — otherwise identical to the "silent reload" describe block's own `mount()`. */
  function mountWithActiveSet(totals: EmoteUsageTotal[]): void {
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', totals);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
  }

  /** Same mount, but the channel has no active 7TV set — E3 must not offer the emote-list
   *  purpose, and `openExport()` must not fail trying to build it. */
  function mountWithoutActiveSet(totals: EmoteUsageTotal[]): void {
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      // '' — not null — is how EmoteSetStatus.activeEmoteSetId (a required string) says "no active
      // set"; the page's own `activeEmoteSetId` computed treats it as null via `|| null`.
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: '', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', totals);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
  }

  /** The `ExportDialogData` the page handed to `Dialog.open` — the second argument's `data`
   *  field, per `openAppDialog`. */
  function openedDialogData(): ExportDialogData<ExportPurposeId> {
    expect(openSpy).toHaveBeenCalledTimes(1);
    return openSpy.mock.calls[0][1].data as ExportDialogData<ExportPurposeId>;
  }

  it('offers all three purposes once there is an active set, and choosing usage-csv downloads a CSV usage export', () => {
    mountWithActiveSet([emote('a', 'PeepoA')]);
    openSpy.mockReturnValue({ closed: of({ optionId: 'usage-csv', scope: 'visible' }) });

    component['openExport']();

    expect(openedDialogData().options.map((option) => option.id)).toEqual([
      'usage-csv',
      'usage-json',
      'emote-list',
    ]);
    expect(downloads).toHaveLength(1);
    expect(downloads[0].filename).toMatch(
      /^emotepurge_a_usage_\d{4}-\d{2}-\d{2}_\d{4}-\d{2}-\d{2}\.csv$/,
    );
    expect(downloads[0].mimeType).toBe(CSV_MIME);
  });

  it('choosing usage-json downloads a JSON usage export', () => {
    mountWithActiveSet([emote('a', 'PeepoA')]);
    openSpy.mockReturnValue({ closed: of({ optionId: 'usage-json', scope: 'visible' }) });

    component['openExport']();

    expect(downloads).toHaveLength(1);
    expect(downloads[0].filename).toMatch(
      /^emotepurge_a_usage_\d{4}-\d{2}-\d{2}_\d{4}-\d{2}-\d{2}\.json$/,
    );
    expect(downloads[0].mimeType).toBe(JSON_MIME);
  });

  it('choosing emote-list downloads the reimportable emote list', () => {
    mountWithActiveSet([emote('a', 'PeepoA')]);
    openSpy.mockReturnValue({ closed: of({ optionId: 'emote-list', scope: 'visible' }) });

    component['openExport']();

    expect(downloads).toHaveLength(1);
    expect(downloads[0].filename).toMatch(/^emotepurge_a_emote-list_\d{4}-\d{2}-\d{2}\.json$/);
    expect(downloads[0].mimeType).toBe(JSON_MIME);
  });

  it('cancelling (the dialog closes with nothing) triggers no download', () => {
    mountWithActiveSet([emote('a', 'PeepoA')]);
    openSpy.mockReturnValue({ closed: of(undefined) });

    component['openExport']();

    expect(downloads).toHaveLength(0);
  });

  it('does not offer the emote-list purpose without an active 7TV set (E3), and still handles a choice among the other two', () => {
    mountWithoutActiveSet([emote('a', 'PeepoA')]);
    openSpy.mockReturnValue({ closed: of({ optionId: 'usage-csv', scope: 'visible' }) });

    component['openExport']();

    expect(openedDialogData().options.map((option) => option.id)).toEqual([
      'usage-csv',
      'usage-json',
    ]);
    expect(downloads).toHaveLength(1);
    expect(downloads[0].mimeType).toBe(CSV_MIME);
  });

  it('exports the range that produced the loaded rows, not a range signal that has since moved on (Codex #143 P2)', () => {
    mountWithActiveSet([emote('a', 'PeepoA')]);
    const loadedFrom = component['from']();
    const loadedTo = component['to']();

    // A range-menu change fires load() again — same as the constructor effect's own trigger — but
    // nothing here flushes the resulting /usage-stats/totals request, so emotes()/totalsChannel()/
    // totalsRange() all still describe the range loaded above. This is the same in-flight window a
    // live usageFlushed reload or a channel switch opens (see totalsRange's declaration).
    component['rangePreset'].set('custom');
    component['from'].set('2026-03-01');
    component['to'].set('2026-03-31');
    fixture.detectChanges();

    openSpy.mockReturnValue({ closed: of({ optionId: 'usage-csv', scope: 'visible' }) });
    component['openExport']();

    expect(downloads).toHaveLength(1);
    // The filename embeds from/to verbatim (usageExportFilename) — proves the download describes
    // the range the rows actually came from, not '2026-03-01'/'2026-03-31' set above.
    expect(downloads[0].filename).toBe(`emotepurge_a_usage_${loadedFrom}_${loadedTo}.csv`);
  });
});

/**
 * `openCreateVoteSession()` (#132): the dialog now receives the LIVE `selection.selectedKeys`
 * signal itself, not a snapshot array copied out of it at call time — see that method's own comment
 * and `create-vote-session-dialog.ts`'s class doc. What the dialog does with a signal that shrinks
 * while it is open is `create-vote-session-dialog.spec.ts`'s job; this only pins what this page
 * hands it and when it clears the selection afterwards. Same `Dialog`-spy pattern as the
 * `openExport()` block above.
 */
describe('UsageStatsPage — openCreateVoteSession() (#132)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;
  let openSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    vi.useFakeTimers();
    openSpy = vi.fn();

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
        { provide: Dialog, useValue: { open: openSpy } as unknown as Dialog },
      ],
    });

    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  function mount(totals: EmoteUsageTotal[]): void {
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', totals);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
  }

  it('does nothing when nothing is selected', () => {
    mount([emote('a', 'PeepoA')]);

    component['openCreateVoteSession']();

    expect(openSpy).not.toHaveBeenCalled();
  });

  it('hands the dialog the LIVE selection signal, not a snapshot — a later prune is visible through it', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    mount([a, b]);
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(b, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'b']);

    openSpy.mockReturnValue({ closed: of(undefined) });
    component['openCreateVoteSession']();

    expect(openSpy).toHaveBeenCalledTimes(1);
    const data = openSpy.mock.calls[0][1].data as CreateVoteSessionDialogData;
    // The exact same signal reference the page's own selection exposes — not a copy taken at call
    // time — is what makes a later prune of the selection visible to an already-open dialog.
    expect(data.emoteIds).toBe(component['selection'].selectedKeys);
    expect(data.emoteIds()).toEqual(['a', 'b']);

    // A silent reload that prunes 'b' (e.g. archived on 7TV) after the dialog has already opened —
    // ListSelection.retainAmong() directly, the same call loadTotals()'s preserveSelection branch
    // makes; the full live-event pipeline that reaches it is #94's own describe block's job.
    component['selection'].retainAmong([a]);

    expect(data.emoteIds()).toEqual(['a']);
  });
});
