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
 * given dialog choice produces (filename shape + MIME type) and that a cancel produces none. The
 * `blob` field is the one exception — the "Auswahl" scope's `filtered` flag (Konzept "Auswahl
 * überlebt Suche und Filter" 2.6) is a decision `usage-stats-page.ts`'s `openExport()` itself makes
 * (not `usage-export-purposes.ts`, which only serializes whatever `filtered` it is handed), so
 * pinning it needs the actual JSON body, not just the filename/MIME shape.
 */
interface CapturedDownload {
  filename: string;
  mimeType: string;
  blob: Blob;
}

/** Spies on the same two seams `downloadFile` touches — restore via `vi.restoreAllMocks()` in
 *  `afterEach`, matching `file-download.spec.ts`'s own pattern. */
function captureDownloads(): CapturedDownload[] {
  const downloads: CapturedDownload[] = [];
  if (!('createObjectURL' in URL)) {
    Object.assign(URL, { createObjectURL: () => '', revokeObjectURL: () => undefined });
  }
  vi.spyOn(URL, 'createObjectURL').mockImplementation((blob: Blob | MediaSource) => {
    downloads.push({ filename: '', mimeType: (blob as Blob).type, blob: blob as Blob });
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
 * view, which is a different case entirely and, since the 2026-09-18 "Auswahl überlebt Suche und
 * Filter" Konzept, not this class's problem at all any more: a filter change never touches the
 * selection, full stop.
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
    // This is the case that decides retainAmong(emotes) over atlasOrder(): 'c' starts above the
    // min-usage filter, gets selected, and the reload lowers its count below that same filter — so
    // it drops out of atlasOrder() (the filtered view) while still being part of the reloaded,
    // unfiltered set. Reconciling against atlasOrder() would wrongly report it as "gone" (#94);
    // reconciling against the raw reload payload must not.
    const a = emote('a', 'PeepoA', 10);
    const c = emote('c', 'PeepoC', 10);

    mount([a, c]);

    // A min-usage filter of 5 — a filter change never touches the selection at all any more
    // (Konzept "Auswahl überlebt Suche und Filter"), so this is only here to narrow atlasOrder()
    // for the reload assertion below, not to exercise any pruning of its own.
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
 * The 2026-09-19 correction to the "Auswahl überlebt Suche und Filter" Konzept: a live test found
 * that clearing the selection on a date-range change punished exactly the workflow the whole
 * Konzept exists for — narrowing or widening the range to check whether a marked-dead emote is
 * still dead. `rangePreset` is pinned to `'custom'` throughout so `rangeResolved` is trivially true
 * from the first tick (see its own comment in usage-stats-page.ts) and every request in a test fires
 * in one `fixture.detectChanges()` instead of the two-tick "all time" placeholder dance the other
 * describe blocks in this file have to choreograph — the case under test here is the
 * `previousTotalsChannel` comparison in `loadTotals`, not that placeholder resolution.
 */
describe('UsageStatsPage — a date-range change or refresh retains the selection, a channel switch clears it (2026-09-19)', () => {
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
    component['rangePreset'].set('custom');
    component['from'].set('2026-01-01');
    component['to'].set('2026-01-31');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  /** Mounts channel 'a' with the given totals under the fixed 2026-01-01..2026-01-31 range set in
   *  beforeEach — all four requests `load()` fires are already pending after the constructor's
   *  first tick, since a `'custom'` preset never waits on trackedSince to resolve the range. */
  function mount(totals: EmoteUsageTotal[]): void {
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', totals);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-01-31',
      liveDays: [],
      emotes: [],
    });
  }

  it('keeps a selection whose emotes are all still present after a date-range change', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    mount([a, b]);

    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['a']);

    component['from'].set('2026-02-01');
    component['to'].set('2026-02-28');
    fixture.detectChanges();
    // Same channel as totalsChannel() named after mount() — no active-set request this time (see
    // requestedSetStatusFor's channel-keyed guard), only a fresh totals/series round trip.
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [a, b]);

    expect(component['selection'].selectedKeys()).toEqual(['a']);
    expect(component['selectionPrunedFeedback']()).toBeNull();
  });

  it('prunes a selected emote missing from the new range, with the existing #94 notice', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    mount([a, b]);

    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(b, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'b']);

    component['from'].set('2026-02-01');
    component['to'].set('2026-02-28');
    fixture.detectChanges();
    // 'b' has no usage at all in the narrower window and drops out of the response entirely — a
    // data-driven removal like any other, reconciled (not treated as a context switch) and
    // surfaced through the same #94 notice a silent reload would show.
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [a]);

    expect(component['selection'].selectedKeys()).toEqual(['a']);
    expect(component['selectionPrunedFeedback']()).toEqual({
      key: 'usageStats.selectionPruned.one',
      count: 1,
    });
  });

  it('the refresh button retains the selection like a date-range change, not like a channel switch', () => {
    const a = emote('a', 'PeepoA');
    mount([a]);

    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['a']);

    component['refresh']();
    fixture.detectChanges();
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [a]);

    expect(component['selection'].selectedKeys()).toEqual(['a']);
  });

  it('clears the selection outright on a channel switch, even when the new channel reuses the same emote id', () => {
    const a = emote('a', 'PeepoA');
    mount([a]);

    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['a']);

    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();

    httpMock
      .expectOne('/api/channels/b/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/b/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-b', trackedSince: '2026-01-01T00:00:00Z' }));
    // Channel b happens to reuse the id 'a' for a wholly unrelated emote — a coincidence the
    // Konzept's invariant is formal and blind to (Abschnitt 1), which is exactly why a channel
    // switch is a hard clear() rather than a retainAmong() reconciliation.
    flushByPath(httpMock, '/api/channels/b/usage-stats/totals', [emote('a', 'UnrelatedEmote')]);

    expect(component['selection'].selectedKeys()).toEqual([]);
  });
});

/**
 * The core behaviour of the 2026-09-18 "Auswahl überlebt Suche und Filter" Konzept: a filter
 * change must never touch `selectedKeys` — the old `retainVisible()` (S2-16) is gone without a
 * replacement, on purpose. What used to prune the selection now only shows up as
 * `hiddenSelectedCount()`, and every consumer downstream (the delete path, a band's "select all",
 * a sort-key change) keeps reading the full, unfiltered selection regardless of what the filter
 * currently hides.
 */
describe('UsageStatsPage — the selection survives filter and sort-key changes (2026-09-18)', () => {
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

  it('a name filter that hides marked rows leaves selectedKeys and the delete path whole, only hiddenSelectedCount rises', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    const c = emote('c', 'PeepoC');
    mount([a, b, c]);

    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(c, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'c']);

    // Narrows atlasOrder() to just 'b' — both marked rows drop out of view.
    component['usageFilter'].setNameFilter('PeepoB');

    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'c']);
    expect(component['selection'].hiddenSelectedCount()).toBe(2);
    expect(
      component['selectedForDelete']()
        .map((row) => row.emoteId)
        .sort(),
    ).toEqual(['a', 'c']);
    // Both marked rows are filtered out of the current view — DeletableEmote.hidden must say so
    // for each (Konzept "Auswahl überlebt Suche und Filter" 2.1), feeding the delete-confirm
    // dialog's hidden-by-filter block.
    expect(component['selectedForDelete']().every((row) => row.hidden)).toBe(true);
    expect(component['selectionPrunedFeedback']()).toBeNull();
  });

  it('the dock hidden-by-filter row is gated on hiddenSelectedCount and resetting the filter clears it (2.2)', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    mount([a, b]);

    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    // Nothing hidden yet — the template gates the whole row on this, no permanent control
    // (Frontend-Zurückhaltung).
    expect(component['selection'].hiddenSelectedCount()).toBe(0);

    component['usageFilter'].setNameFilter('PeepoB');
    expect(component['selection'].hiddenSelectedCount()).toBe(1);
    expect(component['hiddenSelectedFilterKey']()).toBe('usageStats.dock.hiddenByFilter.one');

    // The dock's "Filter zurücksetzen" button calls exactly this — the way back the Konzept
    // requires next to the count (2.2).
    component['usageFilter'].reset();
    expect(component['selection'].hiddenSelectedCount()).toBe(0);
  });

  /**
   * The dock row is created by the same `@if` that fills it, so it cannot announce itself
   * (docs/UI-Designsprache.md §4.5). Its text is `aria-hidden` and spoken by the permanently
   * mounted `DockOutcomeAnnouncer` instead — which must say exactly what the dock shows, so this
   * number mirrors that row's own gates rather than being the raw count.
   */
  it('hands the announcer the hidden count only while the dock row that shows it is on screen', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    mount([a, b]);

    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    expect(component['dockHiddenSelectedCount']()).toBe(0);

    component['usageFilter'].setNameFilter('PeepoB');
    expect(component['dockHiddenSelectedCount']()).toBe(1);

    // No active 7TV set means no marking half of the dock, so no row — and therefore nothing to
    // speak, even though the selection is still hidden behind the filter.
    component['setStatus'].set(null);
    expect(component['selection'].hiddenSelectedCount()).toBe(1);
    expect(component['dockHiddenSelectedCount']()).toBe(0);
  });

  /**
   * The toolbar's "mark all" control (2026-09-19, docs/DECISIONS.md). Its whole point is scope:
   * `atlasOrder()`, the filtered/sorted/banded view the sheet is actually showing, never
   * `emotes()`, the channel's unfiltered universe underneath it.
   */
  it('marks exactly the current filtered view, leaving filtered-out rows unmarked', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    const c = emote('c', 'Other');
    mount([a, b, c]);

    component['usageFilter'].setNameFilter('Peepo');
    expect(
      component['atlasOrder']()
        .map((emote) => emote.emoteId)
        .sort(),
    ).toEqual(['a', 'b']);

    component['markAll']();

    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'b']);
    expect(component['selection'].isSelected(c)).toBe(false);
  });

  it('is disabled once the current view is fully marked, and re-enables the moment a row is unmarked', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    mount([a, b]);

    expect(component['markAllDisabled']()).toBe(false);

    component['markAll']();
    expect(component['markAllDisabled']()).toBe(true);

    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    expect(component['markAllDisabled']()).toBe(false);
  });

  it('is correctly disabled, not a false positive, once a filter narrows the view down to already-marked rows', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    mount([a, b]);

    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    expect(component['markAllDisabled']()).toBe(false);

    // Narrows atlasOrder() to just the already-marked 'a' — mark-all over that view could add
    // nothing, so it is correctly disabled here, not a false positive of the check.
    component['usageFilter'].setNameFilter('PeepoA');
    expect(component['markAllDisabled']()).toBe(true);
  });

  /**
   * Codex/Opus review: `load()` sets `isLoading` before `loadTotals()`'s response writes
   * `emotes.set(...)` — a range change or the refresh button therefore leaves `atlasOrder()` (and
   * the selection) still describing the OUTGOING query for the whole in-flight window. `showMarkAll`
   * used to stay true through that window (it only checked `atlasOrder().length > 0`), which is
   * exactly the "sync pending" bug fix 3 of the same review closed — the toolbar control now
   * requires `sheetShowsRows()`, which folds `!isLoading()` in, so the button leaves the DOM for the
   * same window that used to need a separate `markingSuspendedByLoad()` gate on `markAllDisabled`
   * (removed as dead weight once this subsumed it — see `markAllDisabled`'s own comment). The
   * template-level absence of the button is asserted against the real template in the dedicated
   * describe block below; this only pins the computed signal `showMarkAll` reads to reach it.
   */
  it('hides the toolbar control while a reload is in flight, even though the outgoing atlasOrder() is still non-empty', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    mount([a, b]);

    expect(component['showMarkAll']()).toBe(true);

    // Off the 'all' preset first — otherwise the "all time" correction effect (see its own comment
    // in usage-stats-page.ts) would snap `from` straight back the moment it changes below, since
    // that effect re-fires on every `from()`/`to()`/`rangePreset()` write and 'all' is what makes it
    // active. This mirrors the setup the dedicated date-range-change describe block gives itself
    // from the start; here it happens mid-test since the surrounding suite needs 'all' for mount().
    component['rangePreset'].set('custom');

    // A range change re-enters load(): isLoading flips true immediately, but atlasOrder() still
    // shows the previous response until loadTotals()'s next 'next' callback lands.
    component['from'].set('2026-02-01');
    component['to'].set('2026-02-28');
    fixture.detectChanges();

    expect(component['isLoading']()).toBe(true);
    // The outgoing view is still non-empty — without sheetShowsRows() folding in isLoading(), this
    // would still read true.
    expect(component['atlasOrder']().length).toBeGreaterThan(0);
    expect(component['showMarkAll']()).toBe(false);

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [a, b]);

    expect(component['isLoading']()).toBe(false);
    expect(component['showMarkAll']()).toBe(true);
  });

  /**
   * The dock's own marked-count row is created by the same `@if` that fills it, so a bulk mark can
   * take it from unmounted to a double-digit count with nothing announced — the same defect
   * `dockHiddenSelectedCount` above exists to close, now for the count it sits next to
   * (docs/UI-Designsprache.md §4.5).
   *
   * Opus review (2026-09-19) narrowed this further: an individual mark or unmark already announces
   * itself through its own cell's `aria-pressed`, so mirroring the live selection count here too
   * spoke every one of those a second time. Only `markAll()`/`selectBand()` write
   * `dockMarkedCount()` now — see the tests below for the two failure modes that guarded against:
   * an individual click must never move it, and it must not resurrect a stale bulk count once the
   * selection it described has actually emptied.
   */
  describe('dockMarkedCount (bulk gestures only)', () => {
    it('is fed only by a bulk mark, and clears once the dock row it feeds is off screen', () => {
      const a = emote('a', 'PeepoA');
      const b = emote('b', 'PeepoB');
      mount([a, b]);

      expect(component['dockMarkedCount']()).toBe(0);

      component['markAll']();
      expect(component['dockMarkedCount']()).toBe(2);

      // No active 7TV set means no marking half of the dock, so no row — and therefore nothing to
      // speak, even though the selection itself is untouched.
      component['setStatus'].set(null);
      expect(component['selection'].selectedKeys()).toHaveLength(2);
      expect(component['dockMarkedCount']()).toBe(0);
    });

    it('does not move for an individual click, only for the bulk gestures', () => {
      const a = emote('a', 'PeepoA');
      const b = emote('b', 'PeepoB');
      mount([a, b]);

      component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
      expect(component['selection'].selectedKeys()).toHaveLength(1);
      // An individual mark already announces itself via its own cell — this region must stay
      // silent for it, unlike the pre-review behaviour that mirrored the live count here too.
      expect(component['dockMarkedCount']()).toBe(0);

      component['selection'].onRowClick(b, { shiftKey: false } as MouseEvent);
      expect(component['selection'].selectedKeys()).toHaveLength(2);
      expect(component['dockMarkedCount']()).toBe(0);
    });

    it('does not resurrect a stale bulk count once the selection it described has fully emptied', () => {
      const a = emote('a', 'PeepoA');
      const b = emote('b', 'PeepoB');
      mount([a, b]);

      component['markAll']();
      expect(component['dockMarkedCount']()).toBe(2);

      // Unmarked by hand, down to nothing — no further bulk gesture in between.
      component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
      component['selection'].onRowClick(b, { shiftKey: false } as MouseEvent);
      expect(component['selection'].selectedKeys()).toHaveLength(0);
      expect(component['dockMarkedCount']()).toBe(0);

      // Lets the constructor's reset effect (see dockMarkedCount's own comment) actually run before
      // the next click — effects are scheduled, not synchronous with the signal write that woke
      // them, and detectChanges() is this file's established way to flush them (see the reload-in-
      // flight test above).
      fixture.detectChanges();

      // A single individual click marks one row again. Without that effect having reset the stored
      // bulk count back to 0 while the selection was empty, this would read 2 again — the stale
      // "mark all" outcome — instead of staying silent for what is, on its own, just another
      // individual mark.
      component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
      expect(component['selection'].selectedKeys()).toHaveLength(1);
      expect(component['dockMarkedCount']()).toBe(0);
    });
  });

  it('marking a band, changing the filter, marking again and clearing the filter unions both groups', () => {
    const dead1 = emote('d1', 'Dead1', 0);
    const dead2 = emote('d2', 'Dead2', 0);
    const heavy = emote('h1', 'Heavy1', 500);
    mount([dead1, dead2, heavy]);

    // "select all" on the dead band while nothing is filtered.
    component['selectBand']('dead');
    expect(component['selection'].selectedKeys().sort()).toEqual(['d1', 'd2']);

    // Narrows to the heavy emote (an unrelated band) and marks it too — the filter change above
    // must not have dropped 'd1'/'d2' for this to still be additive.
    component['usageFilter'].setNameFilter('Heavy1');
    component['selection'].onRowClick(heavy, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['d1', 'd2', 'h1']);

    component['usageFilter'].reset();

    expect(component['selection'].selectedKeys().sort()).toEqual(['d1', 'd2', 'h1']);
  });

  it('a sort-key change keeps the selection and only resets the shift anchor', () => {
    const a = emote('a', 'PeepoA', 5);
    const b = emote('b', 'PeepoB', 10);
    const c = emote('c', 'PeepoC', 15);
    mount([a, b, c]);

    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent); // anchor 'a'

    component['setSortKey']('lastUsed');

    expect(component['selection'].selectedKeys()).toEqual(['a']);
    // The anchor is gone — a further shift-click degrades to a plain toggle instead of ranging
    // from 'a' in the (now differently ordered) list.
    component['selection'].onRowClick(c, { shiftKey: true } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'c']);
  });
});

/**
 * The header "Exportieren"/"Übertragen" locks used to read `atlasOrder().length === 0` alone —
 * the same mistake `retainVisible()` made one level down, fixed by S2-16's own nachtrag: once the
 * selection survives a filter, an empty *visible* list no longer means an empty *selection*, and
 * the two buttons must ask about the union of both, not the visible list on its own (see
 * `exportButtonDisabled`/`transferButtonDisabled` in usage-stats-page.ts for the full reasoning).
 */
describe('UsageStatsPage — header export/transfer locks ask about the union, not just what is visible (nachtrag 2026-09-19)', () => {
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

  it('neither button is locked once a filter hides every row but a selection survives underneath it', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    mount([a, b]);

    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    component['usageFilter'].setNameFilter('does-not-match-anything');

    expect(component['atlasOrder']()).toHaveLength(0);
    expect(component['selection'].selectedItems()).toHaveLength(1);
    // The transfer button's other two locks stay at their default "open" state here — this case
    // is only about the empty-scope half both buttons share.
    expect(component['arbiter'].activeRun()).toBeNull();
    expect(component['importScopeCurrent']()).toBe(true);

    expect(component['exportButtonDisabled']()).toBe(false);
    expect(component['transferButtonDisabled']()).toBe(false);
  });

  it('locks both buttons when the visible list AND the selection are both empty', () => {
    mount([]);

    expect(component['atlasOrder']()).toHaveLength(0);
    expect(component['selection'].selectedItems()).toHaveLength(0);

    expect(component['exportButtonDisabled']()).toBe(true);
    expect(component['transferButtonDisabled']()).toBe(true);
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

  it('the "selection" export scope ignores an active filter and reports filtered = false, unlike "visible" (Konzept 2.6)', async () => {
    const a = emote('a', 'PeepoA', 50);
    const b = emote('b', 'PeepoB', 5);
    mountWithActiveSet([a, b]);

    // Narrows atlasOrder() to just 'a' — 'b' stays marked regardless (Konzept "Auswahl überlebt
    // Suche und Filter"), which is exactly what this exercises for the export path: the selection
    // scope must carry 'b' through even though the filter is currently hiding it.
    component['usageFilter'].setMinCount('10');
    component['selection'].onRowClick(b, { shiftKey: false } as MouseEvent);

    openSpy.mockReturnValue({ closed: of({ optionId: 'usage-json', scope: 'selection' }) });
    component['openExport']();
    expect(downloads).toHaveLength(1);
    const selectionEnvelope = JSON.parse(await downloads[0].blob.text());
    expect(selectionEnvelope.meta.filtered).toBe(false);
    expect(selectionEnvelope.rows.map((row: { emoteName: string }) => row.emoteName)).toEqual([
      'PeepoB',
    ]);

    openSpy.mockReturnValue({ closed: of({ optionId: 'usage-json', scope: 'visible' }) });
    component['openExport']();
    expect(downloads).toHaveLength(2);
    const visibleEnvelope = JSON.parse(await downloads[1].blob.text());
    expect(visibleEnvelope.meta.filtered).toBe(true);
    expect(visibleEnvelope.rows.map((row: { emoteName: string }) => row.emoteName)).toEqual([
      'PeepoA',
    ]);
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

/**
 * The toolbar's "mark all" control does not exist at all on a coarse pointer — same reasoning as
 * every other selection surface (docs/UI-Designsprache.md §2.5): there is no 7TV write path off a
 * phone, so nothing a mark could ever lead to. `PointerModeService` reads `matchMedia` once at
 * construction, so the coarse device has to be in place before `TestBed.createComponent` runs
 * (see core/pointer/pointer-mode.service.spec.ts for the same fake shape).
 */
describe('UsageStatsPage — mark-all does not exist on a coarse pointer (2026-09-19)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;

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

  it('reports not shown even though the current view is non-empty', () => {
    mount([emote('a', 'PeepoA')]);

    expect(component['isCoarse']()).toBe(true);
    expect(component['atlasOrder']().length).toBeGreaterThan(0);
    expect(component['showMarkAll']()).toBe(false);
  });
});

/**
 * Fix 5, Opus review (2026-09-19): every test above for `showMarkAll()`/`markAllDisabled()` runs
 * against the two-`<div>` stub template every other describe block in this file uses, so neither the
 * template's `@if (showMarkAll())` nor its `[disabled]="markAllDisabled()"` binding ever actually
 * ran — removing either from usage-stats-page.html would still leave that suite green, only the
 * computed()s behind it were pinned. These tests mount the real template instead, the same way the
 * block this one replaces did (that one asserted `aria-hidden`/DOM-structure on the marked-count row
 * via `children[n]` navigation — itself flagged in the same review and gone along with the behaviour
 * it tested, since that row is reachable again, see usage-stats-page.html's own comment).
 *
 * Located by the button's rendered text, not a CSS class (Regel 12): with the empty `{}`
 * translations this file's real-template blocks use, Transloco's default missing-key handler
 * (`DefaultMissingHandler.handle`) returns the raw key itself, which is a stable, unique string here
 * — the same fallback `vote-session-list-page.spec.ts` already relies on for its own text assertions.
 */
describe("UsageStatsPage — the toolbar mark-all button's template binding (Opus review, 2026-09-19)", () => {
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
    // Unlike the stub-template blocks in this file, the real template only reflects the flushes
    // above once change detection actually runs — every assertion here reads the DOM, not a bare
    // computed(), so this cannot be left to whichever later `fixture.detectChanges()` a test
    // happens to call for its own reasons.
    fixture.detectChanges();
  }

  function markAllButton(): HTMLButtonElement | undefined {
    return Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((button) => button.textContent?.trim() === 'usageStats.markAll');
  }

  it('exists and is enabled while the view is not fully marked, and disables the moment it is', () => {
    // A real (non-empty) imageUrl: this describe block renders the actual template, unlike most of
    // this file's stub-template blocks, so the sprite's NgOptimizedImage directive now actually
    // runs and rejects the shared emote() helper's default '' (NG02952).
    const a = { ...emote('a', 'PeepoA'), imageUrl: 'https://cdn.7tv.app/emote/x/1x.webp' };
    mount([a]);

    const button = markAllButton();
    expect(button).not.toBeUndefined();
    expect(button!.disabled).toBe(false);

    button!.click();
    fixture.detectChanges();

    expect(markAllButton()!.disabled).toBe(true);
  });

  it('does not exist while a reload is in flight, even though the outgoing view is still non-empty, and reappears once it lands', () => {
    const a = { ...emote('a', 'PeepoA'), imageUrl: 'https://cdn.7tv.app/emote/x/1x.webp' };
    mount([a]);

    expect(markAllButton()).not.toBeUndefined();

    // Off the 'all' preset first — see the computed-level test of the same scenario above for why.
    component['rangePreset'].set('custom');
    component['from'].set('2026-02-01');
    component['to'].set('2026-02-28');
    fixture.detectChanges();

    expect(component['isLoading']()).toBe(true);
    expect(component['atlasOrder']().length).toBeGreaterThan(0);
    expect(markAllButton()).toBeUndefined();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [a]);
    fixture.detectChanges();

    expect(component['isLoading']()).toBe(false);
    expect(markAllButton()).not.toBeUndefined();
  });
});
