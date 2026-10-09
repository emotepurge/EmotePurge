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
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { of, Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { channelLiveUrl, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { CHANNEL_RELOAD_DEBOUNCE_MS } from '../../core/live/live-reload';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import {
  ForeignEmoteRow,
  ForeignEmoteSetResponse,
} from '../../core/seven-tv/foreign-emote-set.model';
import {
  EmoteSetListResponse,
  EmoteSetSummary,
} from '../../core/seven-tv/seven-tv-emote-set.model';
import { DeleteRunInfo, SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { ImportRunInfo, SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import {
  RestoreRunInfo,
  SevenTvRestoreService,
} from '../../core/seven-tv/seven-tv-restore.service';
import { REFUSED_START_FEEDBACK_MS } from '../../core/seven-tv/seven-tv-run-arbiter';
import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvUndoService, UndoRunInfo } from '../../core/seven-tv/seven-tv-undo.service';
import { EmoteTagEntry, EmoteTagSummary } from '../../core/tags/emote-tag.model';
import { ImportCoverage } from '../../core/usage/import-coverage.model';
import { ImportCoverageService } from '../../core/usage/import-coverage.service';
import { mergeSetView } from '../../core/usage-stats/merge-set-view';
import { EmoteUsageTotal, EmoteUsageTotalDto } from '../../core/usage-stats/usage-stat.model';
import { UsageStatService } from '../../core/usage-stats/usage-stat.service';
import { EmoteDrilldownData } from '../../shared/emotes/emote-drilldown-dialog';
import { CSV_MIME } from '../../shared/export/csv';
import { ExportDialogData } from '../../shared/export/export-dialog';
import { JSON_MIME } from '../../shared/export/export-envelope';
import { ExportPurposeId } from '../../shared/export/usage-export-purposes';
import { TagAssignDialogData, TagAssignDialogResult } from '../../shared/tags/tag-assign-dialog';
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

/** What a channel without any imported day answers. */
const NO_IMPORT: ImportCoverage = {
  emoteSetId: null,
  sources: [],
  importedFrom: null,
  importedTo: null,
  hasGaps: false,
  contiguousFrom: null,
  intervals: [],
};

// "All time" waits for the import-coverage answer before it asks for the grid (its start can move to
// the first imported day), so every block below that does not exercise the coverage itself gets the
// answer for free: nothing imported, answered at once. The blocks about the coverage opt out and
// drive the real HTTP route (`useRealImportCoverage`).
function stubNoImportCoverage(): void {
  TestBed.overrideProvider(ImportCoverageService, {
    useValue: { getCoverage: () => of(NO_IMPORT) },
  });
}

beforeEach(stubNoImportCoverage);

function useRealImportCoverage(): void {
  TestBed.overrideProvider(ImportCoverageService, {
    useFactory: () => new ImportCoverageService(),
  });
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

function emote(id: string, name: string, totalUseCount = 10): EmoteUsageTotalDto {
  return {
    emoteId: id,
    emoteName: name,
    sevenTvEmoteId: `7tv-${id}`,
    imageUrl: '',
    totalUseCount,
    lastUsedDate: null,
    previousWindowUseCount: 0,
    firstSeenAt: null,
    isArchived: false,
    nameTwinEmoteSetIds: [],
  };
}

/**
 * The page's grid row for a `/totals` fixture in the ACTIVE set's view — the lossless 1:1 mapping
 * `mergeSetView` does there (spec #200, 7.1). The selection and every grid-facing method take the
 * merged row type; the rows the page builds from a flushed payload are exactly this shape.
 */
function asRow(dto: EmoteUsageTotalDto): EmoteUsageTotal {
  return mergeSetView([dto], null, true)[0];
}

/** Fixture for the set-dropdown's own list (spec #200, 6.1) — one entry, override for anything else
 *  (a second set, a `kind` other than `NORMAL`, an inactive one). */
function emoteSet(overrides: Partial<EmoteSetSummary> = {}): EmoteSetSummary {
  return {
    id: 'set-a',
    name: 'Hauptset',
    capacity: 250,
    kind: 'NORMAL',
    isActive: true,
    isPersonal: false,
    ownerDisplayName: null,
    observations: [],
    ...overrides,
  };
}

function emoteSetList(sets: EmoteSetSummary[]): EmoteSetListResponse {
  return {
    activeEmoteSetId: sets.find((set) => set.isActive)?.id ?? '',
    sets,
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

// The tracked-channel preview route (#220): the set id is a path segment, so the exact-path
// regex cannot also catch the dropdown request `/api/channels/a/emote-sets`.
const LIVE_LIST_URL = /^\/api\/channels\/a\/emote-sets\/[^/]+\/emotes$/;

function liveListUrl(setId: string): string {
  return `/api/channels/a/emote-sets/${encodeURIComponent(setId)}/emotes`;
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

    // No totals to drain here: since the O2 review fix a sync asks for the rows only once its
    // status has answered, and this status never answers for A.
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

    // As above: the sync's rows wait for its status, which never answers for A.
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

    // No totals before the status (O2 review fix): the rows this sync causes are the explicit
    // set-a request below, issued by the load effect once the status has moved the selected set.

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
    // The rows on screen were answered through the endpoint's fallback while no active set was
    // known — their identity stays unknown and is never relabelled as 'set-a' after the fact
    // (#200 K4, second review round): the scope only turns current once rows answered for the now
    // known set have landed.
    expect(component['importScopeCurrent']()).toBe(false);
    fixture.detectChanges();
    const explicit = httpMock.match(
      (r) =>
        r.url === '/api/channels/a/usage-stats/totals' && r.params.get('emoteSetId') === 'set-a',
    );
    expect(explicit.length).toBeGreaterThan(0);
    explicit.forEach((request) => request.flush([]));
    expect(component['importScopeCurrent']()).toBe(true);
  });

  // Gates the automatic-sync note: the worker no longer re-reads a deactivated channel.
  it.each([
    [true, true],
    [false, false],
  ])(
    'botActive() follows isBotActive (%s) once permissions have loaded',
    async (isBotActive, expected) => {
      expect(component['botActive']()).toBe(false);

      httpMock
        .expectOne('/api/channels/a/permissions')
        .flush({ canManage: false, canViewUsageStats: true, isBotActive });
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();

      expect(component['botActive']()).toBe(expected);
    },
  );

  it('botActive() stays false when the permissions request failed', async () => {
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush(null, { status: 500, statusText: 'Server Error' });
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    expect(component['botActive']()).toBe(false);
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
 * #94: a silent reload of the channel on screen must reconcile the selection against the
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
  function mount(totals: EmoteUsageTotalDto[]): void {
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

  /** Fires one `usageFlushed` burst (the silent reload path) and flushes its totals response — the
   *  same round trip the live subscription in the constructor produces through `loadTotals`. */
  function silentReload(totals: EmoteUsageTotalDto[]): void {
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
    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(asRow(c), { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-c']);
    expect(component['selectionPrunedFeedback']()).toBeNull();

    // The silent reload comes back without 'c' — deleted externally on 7TV between loads.
    silentReload([a, b]);

    // 'a' survives, 'c' is gone from the authoritative key set, and the transient feedback names
    // exactly one dropped emote.
    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
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
    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);

    silentReload([a, b]);

    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
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
    component['selection'].onRowClick(asRow(c), { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['7tv-c']);
    expect(component['atlasOrder']().map((e: EmoteUsageTotal) => e.emoteId)).toContain('c');

    // The reload drops 'c's count under the filter's floor — atlasOrder() will no longer include
    // it — but 'c' itself is still present in the reloaded payload.
    silentReload([a, emote('c', 'PeepoC', 1)]);

    // Confirms the filter really did narrow atlasOrder() past 'c' — otherwise this test would not
    // be exercising the case it claims to.
    expect(component['atlasOrder']().map((e: EmoteUsageTotal) => e.emoteId)).not.toContain('c');
    // ...yet the selection and the feedback are both untouched: 'c' was never actually removed
    // from the set, only filtered out of the current view.
    expect(component['selection'].selectedKeys()).toEqual(['7tv-c']);
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
  function mount(totals: EmoteUsageTotalDto[]): void {
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

    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);

    component['from'].set('2026-02-01');
    component['to'].set('2026-02-28');
    fixture.detectChanges();
    // Same channel as totalsChannel() named after mount() — no active-set request this time (see
    // requestedSetStatusFor's channel-keyed guard), only a fresh totals/series round trip.
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [a, b]);

    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
    expect(component['selectionPrunedFeedback']()).toBeNull();
  });

  it('prunes a selected emote missing from the new range, with the existing #94 notice', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    mount([a, b]);

    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(asRow(b), { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-b']);

    component['from'].set('2026-02-01');
    component['to'].set('2026-02-28');
    fixture.detectChanges();
    // 'b' has no usage at all in the narrower window and drops out of the response entirely — a
    // data-driven removal like any other, reconciled (not treated as a context switch) and
    // surfaced through the same #94 notice a silent reload would show.
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [a]);

    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
    expect(component['selectionPrunedFeedback']()).toEqual({
      key: 'usageStats.selectionPruned.one',
      count: 1,
    });
  });

  it('the refresh button retains the selection like a date-range change, not like a channel switch', () => {
    const a = emote('a', 'PeepoA');
    mount([a]);

    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);

    component['refresh']();
    fixture.detectChanges();
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [a]);

    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
  });

  it('clears the selection outright on a channel switch, even when the new channel reuses the same emote id', () => {
    const a = emote('a', 'PeepoA');
    mount([a]);

    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);

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

  function mount(totals: EmoteUsageTotalDto[]): void {
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

    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(asRow(c), { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-c']);

    // Narrows atlasOrder() to just 'b' — both marked rows drop out of view.
    component['usageFilter'].setNameFilter('PeepoB');

    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-c']);
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

    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
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

    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
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

    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-b']);
    expect(component['selection'].isSelected(asRow(c))).toBe(false);
  });

  it('is disabled once the current view is fully marked, and re-enables the moment a row is unmarked', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    mount([a, b]);

    expect(component['markAllDisabled']()).toBe(false);

    component['markAll']();
    expect(component['markAllDisabled']()).toBe(true);

    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
    expect(component['markAllDisabled']()).toBe(false);
  });

  it('is correctly disabled, not a false positive, once a filter narrows the view down to already-marked rows', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    mount([a, b]);

    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
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

      component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
      expect(component['selection'].selectedKeys()).toHaveLength(1);
      // An individual mark already announces itself via its own cell — this region must stay
      // silent for it, unlike the pre-review behaviour that mirrored the live count here too.
      expect(component['dockMarkedCount']()).toBe(0);

      component['selection'].onRowClick(asRow(b), { shiftKey: false } as MouseEvent);
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
      component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
      component['selection'].onRowClick(asRow(b), { shiftKey: false } as MouseEvent);
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
      component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
      expect(component['selection'].selectedKeys()).toHaveLength(1);
      expect(component['dockMarkedCount']()).toBe(0);
    });

    it('re-announces a bulk gesture even when it lands back on the same total the last one left behind', () => {
      const a = emote('a', 'PeepoA');
      const b = emote('b', 'PeepoB');
      mount([a, b]);

      component['markAll']();
      expect(component['dockMarkedCount']()).toBe(2);

      // A single deselect is not a bulk gesture — it must retire the announcement even though the
      // selection stays non-empty, unlike the fully-emptied case above.
      component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
      expect(component['selection'].selectedKeys()).toHaveLength(1);
      expect(component['dockMarkedCount']()).toBe(0);

      // "Mark all" again re-adds the same row and lands on the same total (2) the first press
      // already announced. `role="status"` only reacts to a DOM mutation, so if this stayed masked
      // at "the same number as before" nothing would be spoken for a gesture that really happened —
      // a screen-reader user marking everything twice in a row would hear it only the first time.
      component['markAll']();
      expect(component['selection'].selectedKeys()).toHaveLength(2);
      expect(component['dockMarkedCount']()).toBe(2);
    });
  });

  it('marking a band, changing the filter, marking again and clearing the filter unions both groups', () => {
    const dead1 = emote('d1', 'Dead1', 0);
    const dead2 = emote('d2', 'Dead2', 0);
    const heavy = emote('h1', 'Heavy1', 500);
    mount([dead1, dead2, heavy]);

    // "select all" on the dead band while nothing is filtered.
    component['selectBand']('dead');
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-d1', '7tv-d2']);

    // Narrows to the heavy emote (an unrelated band) and marks it too — the filter change above
    // must not have dropped 'd1'/'d2' for this to still be additive.
    component['usageFilter'].setNameFilter('Heavy1');
    component['selection'].onRowClick(asRow(heavy), { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-d1', '7tv-d2', '7tv-h1']);

    component['usageFilter'].reset();

    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-d1', '7tv-d2', '7tv-h1']);
  });

  it('a sort-key change keeps the selection and only resets the shift anchor', () => {
    const a = emote('a', 'PeepoA', 5);
    const b = emote('b', 'PeepoB', 10);
    const c = emote('c', 'PeepoC', 15);
    mount([a, b, c]);

    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent); // anchor 'a'

    component['setSortKey']('lastUsed');

    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
    // The anchor is gone — a further shift-click degrades to a plain toggle instead of ranging
    // from 'a' in the (now differently ordered) list.
    component['selection'].onRowClick(asRow(c), { shiftKey: true } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-c']);
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

  function mount(totals: EmoteUsageTotalDto[]): void {
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

    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
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

  // #280: a confirmed start (of any run) whose last live read is still out locks every start trigger the
  // arbiter gates — the header's "Übertragen" and the dock's copy shortcut too, not only the import
  // trigger and the delete CTA.
  it('locks the transfer button and the dock copy shortcut while a confirmed start is still being checked', () => {
    const a = emote('a', 'PeepoA');
    mount([a]);
    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
    expect(component['transferButtonDisabled']()).toBe(false);
    expect(component['importShortcutLocked']()).toBe(false);

    const startCheckPending = signal(false);
    component['arbiter'].register({
      kind: 'undo',
      isRunning: signal(false),
      isSettling: signal(false),
      destructiveOpen: signal(false),
      startCheckPending,
    });
    startCheckPending.set(true);

    expect(component['arbiter'].activeRun()).toBeNull();
    expect(component['transferButtonDisabled']()).toBe(true);
    expect(component['importShortcutLocked']()).toBe(true);
    // The export writes nothing to 7TV and has no reason to wait.
    expect(component['exportButtonDisabled']()).toBe(false);

    startCheckPending.set(false);
    expect(component['transferButtonDisabled']()).toBe(false);
    expect(component['importShortcutLocked']()).toBe(false);
  });

  // #280, Festlegung Nr. 8: a click outracing that lock opens no target picker and says nothing.
  it('opens no target picker for a click that outraces the lock while a confirmed start is checked', () => {
    const a = emote('a', 'PeepoA');
    mount([a]);
    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
    const openSpy = vi.spyOn(TestBed.inject(Dialog), 'open').mockReturnValue({
      closed: new Subject<unknown>(),
    } as unknown as ReturnType<Dialog['open']>);
    const startCheckPending = signal(true);
    component['arbiter'].register({
      kind: 'restore',
      isRunning: signal(false),
      isSettling: signal(false),
      destructiveOpen: signal(false),
      startCheckPending,
    });

    component['openImportTarget']();
    component['openImportTarget']('selection');
    expect(openSpy).not.toHaveBeenCalled();

    startCheckPending.set(false);
    component['openImportTarget']();
    expect(openSpy).toHaveBeenCalledTimes(1);
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
 * `refusedStartNotice` (#256 T4) is the page's own projection of `SevenTvRunArbiter.refusedStart()`
 * — asserted on the signal directly (Regel 12, "Verhalten ja, Vorlage nein"), like every other
 * transient-feedback signal in this file. `component['arbiter']` is the real, root-provided
 * `SevenTvRunArbiter` (nothing in this spec file overrides it); a bare stub participant registered
 * directly through its public `register()` is enough to give it something to be busy with, without
 * driving a real delete/restore/import run through HTTP.
 */
describe("UsageStatsPage — refusedStartNotice projects the arbiter's transient notice (#256 T4)", () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;

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

    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('is null while the arbiter is free', () => {
    expect(component['refusedStartNotice']()).toBeNull();
  });

  it("projects a refused start into the message key by phase and the blocking kind's noun", () => {
    component['arbiter'].register({
      kind: 'delete',
      isRunning: signal(true),
      isSettling: signal(false),
      destructiveOpen: signal(false),
    });

    component['arbiter'].noteRefusedStart('import');

    // Untranslated fallback (`langs: { de: {} }`), same idiom as every other missing-translation
    // assertion in this file — the point here is which keys `refusedStartMessage` picks, not their
    // German wording (Regel 12).
    expect(component['refusedStartNotice']()).toEqual({
      messageKey: 'sevenTvRun.notStarted.running',
      kind: 'sevenTvRun.kind.delete',
    });
  });

  it('names the settling phase once the blocking run has stopped running but not yet reported', () => {
    component['arbiter'].register({
      kind: 'restore',
      isRunning: signal(false),
      isSettling: signal(true),
      destructiveOpen: signal(false),
    });

    component['arbiter'].noteRefusedStart('delete');

    expect(component['refusedStartNotice']()).toEqual({
      messageKey: 'sevenTvRun.notStarted.settling',
      kind: 'sevenTvRun.kind.restore',
    });
  });

  it("clears itself once the arbiter's own REFUSED_START_FEEDBACK_MS window elapses — no timer of this page's own", () => {
    component['arbiter'].register({
      kind: 'import',
      isRunning: signal(true),
      isSettling: signal(false),
      destructiveOpen: signal(false),
    });
    component['arbiter'].noteRefusedStart('delete');
    expect(component['refusedStartNotice']()).not.toBeNull();

    vi.advanceTimersByTime(REFUSED_START_FEEDBACK_MS - 1);
    expect(component['refusedStartNotice']()).not.toBeNull();

    vi.advanceTimersByTime(1);
    expect(component['refusedStartNotice']()).toBeNull();
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
 * AK 33–34 (spec #253, E22): the import entry stays visible on a channel page without a selected
 * set — before its first 7TV sync, or after a replace into the untracked (DECISIONS "the file
 * determines the target") — while the copy button ("Übertragen") is gone, since it has nothing of
 * THIS channel's own to copy. Real template, same reasoning as the block above: the actual markup
 * (which `@if` wraps which element) is what is under test, not signal plumbing — `ImportTrigger`'s
 * own `disabled()`/`setId()` inputs are `import-trigger.spec.ts`'s job.
 */
describe('UsageStatsPage — import entry without a selected set (spec #253, AK 33–34)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let httpMock: HttpTestingController;

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

    fixture = TestBed.createComponent(UsageStatsPage);
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  /** Mounts the page on a channel with no active 7TV set — `no_active_emote_set` rather than a
   *  `null` reason keeps `load()`'s `awaitSync` branch (which polls) from firing, irrelevant to
   *  what is under test here. */
  function mountWithoutActiveSet(channelName: string): void {
    httpMock
      .expectOne(`/api/channels/${channelName}/permissions`)
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock.expectOne(`/api/channels/${channelName}/emotes/active-set`).flush(
      setStatus({
        activeEmoteSetId: '',
        trackedSince: '2026-01-01T00:00:00Z',
        syncFailureReason: 'no_active_emote_set',
      }),
    );
    fixture.detectChanges();
    flushByPath(httpMock, `/api/channels/${channelName}/usage-stats/totals`, []);
    flushByPath(httpMock, `/api/channels/${channelName}/usage-stats/series`, {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
  }

  it('shows the import entry even without a selected set', () => {
    mountWithoutActiveSet('a');

    expect(fixture.nativeElement.querySelector('app-import-trigger')).not.toBeNull();
  });

  it('shows no copy ("Übertragen") button without a selected set — it has nothing of this channel to copy', () => {
    mountWithoutActiveSet('a');

    const host: HTMLElement = fixture.nativeElement;
    const copyButton = Array.from(host.querySelectorAll('button')).find(
      (button) => button.textContent?.trim() === 'import.copyButton',
    );
    expect(copyButton).toBeUndefined();
  });

  // Fix round 1 (Critical, confirmed review finding): before this fix `dockVisible()`
  // (`actionDockHasContent` via `action-dock.ts`) gated `restoreShown` behind `hasActiveSet`, so a
  // running restore with nothing to report yet (no skipped/name-taken/unavailable notice —
  // `duplicateNoticePending` stays false for a clean run, see `showDuplicateNotice`) never mounted
  // `.app-dock` at all on a page like this one — AK 33's "das Restore-Dock ist sichtbar" failed for
  // exactly this, the ordinary case. `isRunning` is the real, writable signal `SevenTvRestoreService`
  // exposes (readonly binding, not a readonly signal — same pattern this file already uses for
  // `SevenTvDeleteService.lastRun` above); no HTTP round trip needed to drive it, and `run()` stays
  // null on purpose — this pins dock visibility, not `RestoreProgressSection`'s own target-line
  // rendering, which is that component's own spec's job.
  it('shows the dock for a running, notice-free restore even without a selected set (AK 33, fix round 1)', () => {
    mountWithoutActiveSet('a');

    const restoreService = TestBed.inject(SevenTvRestoreService);
    expect(restoreService.duplicateNoticePending()).toBe(false);
    restoreService.isRunning.set(true);
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('.app-dock')).not.toBeNull();
    expect(host.querySelector('app-restore-progress-section')).not.toBeNull();
  });

  // #254 spec 6.6: an undo writes into the set its transfer file names, whichever set the page
  // shows — a shown undo run mounts the dock and its section without a selected set, like the
  // restore above; a start that skipped everything mounts it for its notice alone.
  it('shows the dock with the undo section for a shown undo run or its notice, even without a selected set', () => {
    mountWithoutActiveSet('a');
    const undoService = TestBed.inject(SevenTvUndoService);

    undoService.noticePending.set(true);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('.app-dock')).not.toBeNull();
    expect(host.querySelector('app-undo-progress-section')).not.toBeNull();

    undoService.noticePending.set(false);
    fixture.detectChanges();
    expect(host.querySelector('.app-dock')).toBeNull();
  });

  // The other half of the same fix: marking-only content (nothing running, nothing pending) must
  // still not conjure a dock out of an active-set-less page — action-dock.spec.ts already pins this
  // at the unit level; this is the page-level twin using the real template.
  it('still shows no dock for a page without a selected set and no restore/import activity', () => {
    mountWithoutActiveSet('a');

    expect(fixture.nativeElement.querySelector('.app-dock')).toBeNull();
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
  function mountWithActiveSet(totals: EmoteUsageTotalDto[]): void {
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
  function mountWithoutActiveSet(totals: EmoteUsageTotalDto[]): void {
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
      /^emotepurge_a_usage_set-a_\d{4}-\d{2}-\d{2}_\d{4}-\d{2}-\d{2}\.csv$/,
    );
    expect(downloads[0].mimeType).toBe(CSV_MIME);
  });

  it('choosing usage-json downloads a JSON usage export', () => {
    mountWithActiveSet([emote('a', 'PeepoA')]);
    openSpy.mockReturnValue({ closed: of({ optionId: 'usage-json', scope: 'visible' }) });

    component['openExport']();

    expect(downloads).toHaveLength(1);
    expect(downloads[0].filename).toMatch(
      /^emotepurge_a_usage_set-a_\d{4}-\d{2}-\d{2}_\d{4}-\d{2}-\d{2}\.json$/,
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
    expect(downloads[0].filename).toBe(`emotepurge_a_usage_set-a_${loadedFrom}_${loadedTo}.csv`);
  });

  it('the "selection" export scope ignores an active filter and reports filtered = false, unlike "visible" (Konzept 2.6)', async () => {
    const a = emote('a', 'PeepoA', 50);
    const b = emote('b', 'PeepoB', 5);
    mountWithActiveSet([a, b]);

    // Narrows atlasOrder() to just 'a' — 'b' stays marked regardless (Konzept "Auswahl überlebt
    // Suche und Filter"), which is exactly what this exercises for the export path: the selection
    // scope must carry 'b' through even though the filter is currently hiding it.
    component['usageFilter'].setMinCount('10');
    component['selection'].onRowClick(asRow(b), { shiftKey: false } as MouseEvent);

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

  function mount(totals: EmoteUsageTotalDto[]): void {
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
    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(asRow(b), { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-b']);

    openSpy.mockReturnValue({ closed: of(undefined) });
    component['openCreateVoteSession']();

    expect(openSpy).toHaveBeenCalledTimes(1);
    const data = openSpy.mock.calls[0][1].data as CreateVoteSessionDialogData;
    // A live signal derived from the page's selection — not a copy taken at call time — is what
    // makes a later prune of the selection visible to an already-open dialog. Its values are the
    // rows' Emote.Id Guids, resolved from the 7TV-keyed selection when read (spec #200, E4, 7.2).
    expect(data.emoteIds).toBe(component['voteBallotEmoteIds']);
    expect(data.emoteIds()).toEqual(['a', 'b']);

    // A silent reload that prunes 'b' (e.g. archived on 7TV) after the dialog has already opened —
    // ListSelection.retainAmong() directly, the same call loadTotals()'s same-channel branch
    // makes; the full live-event pipeline that reaches it is #94's own describe block's job.
    component['selection'].retainAmong([asRow(a)]);

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

  function mount(totals: EmoteUsageTotalDto[]): void {
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

  function mount(totals: EmoteUsageTotalDto[]): void {
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

/**
 * T4.2 (spec #200, 8.1; operator decisions 2026-09-21): the set dropdown's URL state, its fallback
 * rules and everything a set switch triggers. Stub template like most of this file — none of these
 * assertions read the DOM, they read the state the dropdown is built on top of (Regel 12).
 *
 * `router.navigate([], { queryParams })` reaches `ActivatedRoute.queryParamMap` even for a component
 * created directly via `TestBed.createComponent` rather than through a routed outlet — query params
 * live on the router's root state, which every injected `ActivatedRoute` in the same injector shares.
 * Seeding them *before* `TestBed.createComponent` is what lets a test simulate a deep link (the URL
 * already carries `?emoteSetId=…` the moment `listQueryState`'s field initializer first reads it).
 *
 * `settle()` does double duty: it lets a fire-and-forget `router.navigate` (setParams,
 * onEmoteSetSelected) finish, the same as core/routing/list-query-state.spec.ts's own helper, AND it
 * gives `emoteSetListResource` (an `rxResource`, unlike the plain `HttpClient` calls the rest of this
 * file flushes) the extra microtask its status/value need after a synchronous `.flush()` — a bare
 * `fixture.detectChanges()` right after `flush()` observably still reports `status() === 'loading'`
 * (checked directly against a minimal `rxResource` in isolation while writing this suite).
 */
describe('UsageStatsPage — set dropdown, URL fallback rules and retainAmong (T4.2)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;
  let router: Router;

  function configure(): void {
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

    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });
  }

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  /** Flushes permissions + active-set + the set list, then settles — the same choreography every
   *  other describe block in this file drives by hand, plus the new set-list request T4.2 adds.
   *  Totals/series are NOT flushed here: whether/when they even fire is what half of this block's
   *  tests are about. */
  async function mountUpTo(
    channelName: string,
    sets: EmoteSetSummary[],
    options: { setsUnavailable?: boolean } = {},
  ): Promise<void> {
    httpMock
      .expectOne(`/api/channels/${channelName}/permissions`)
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock.expectOne(`/api/channels/${channelName}/emotes/active-set`).flush(
      setStatus({
        activeEmoteSetId: sets.find((set) => set.isActive)?.id ?? '',
        trackedSince: '2026-01-01T00:00:00Z',
      }),
    );
    // The "all time" correction against trackedSince reruns the load effect a second time (see that
    // effect's own comment in usage-stats-page.ts).
    fixture.detectChanges();

    const setsReq = httpMock.expectOne(`/api/channels/${channelName}/emote-sets`);
    if (options.setsUnavailable) {
      setsReq.flush(
        { errorCode: 'foreign_channel_seventv_unavailable' },
        { status: 503, statusText: 'Service Unavailable' },
      );
    } else {
      setsReq.flush(emoteSetList(sets));
    }
    await settle();
  }

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('an unknown emoteSetId in the URL silently falls back to the active set and cleans the URL (T4.0 decision 3)', async () => {
    configure();
    router = TestBed.inject(Router);
    await router.navigate([], { queryParams: { emoteSetId: 'does-not-exist' } });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    await mountUpTo('a', [emoteSet({ id: 'set-a', isActive: true })]);
    // The list is readable and rejects the id — /totals fires for the active set right away, the
    // same tick, not held back (only a still-pending list holds it, see the waiting-phase test).
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['selectedEmoteSetId']()).toBe('set-a');

    await settle();
    expect(router.routerState.snapshot.root.queryParamMap.get('emoteSetId')).toBeNull();
  });

  it('a PERSONAL set id in the URL is treated exactly like an unknown id (decision 4)', async () => {
    configure();
    router = TestBed.inject(Router);
    await router.navigate([], { queryParams: { emoteSetId: 'set-personal' } });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    await mountUpTo('a', [
      emoteSet({ id: 'set-a', isActive: true }),
      emoteSet({ id: 'set-personal', name: 'Personal Emotes', kind: 'PERSONAL', isPersonal: true }),
    ]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['selectedEmoteSetId']()).toBe('set-a');

    await settle();
    expect(router.routerState.snapshot.root.queryParamMap.get('emoteSetId')).toBeNull();
  });

  it('holds the totals/series request while the set list for a URL-carried emoteSetId is still pending (decision 2)', async () => {
    configure();
    router = TestBed.inject(Router);
    void router.navigate([], { queryParams: { emoteSetId: 'set-b' } });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
    // The query-param navigation above is fire-and-forget too — let it land before relying on it.
    await settle();

    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();
    fixture.detectChanges();

    // Range is resolved and the channel is known, but the set list itself has not answered yet —
    // no premature request for the active set, no #94 notice for an answer nobody asked for.
    expect(component['awaitingEmoteSetId']()).toBe(true);
    httpMock.expectNone('/api/channels/a/usage-stats/totals');
    httpMock.expectNone('/api/channels/a/usage-stats/series');

    httpMock
      .expectOne('/api/channels/a/emote-sets')
      .flush(
        emoteSetList([
          emoteSet({ id: 'set-a', isActive: true }),
          emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
        ]),
      );
    await settle();

    expect(component['awaitingEmoteSetId']()).toBe(false);
    expect(component['selectedEmoteSetId']()).toBe('set-b');
    const totalsReq = httpMock.expectOne(
      (r) =>
        r.url === '/api/channels/a/usage-stats/totals' && r.params.get('emoteSetId') === 'set-b',
    );
    totalsReq.flush([]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
  });

  it('pins the display to the active set for the rest of the channel session once the set list fails to load (decision 3)', async () => {
    configure();

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    await mountUpTo('a', [emoteSet({ id: 'set-a', isActive: true })], { setsUnavailable: true });
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['selectedEmoteSetId']()).toBe('set-a');
    expect(component['emoteSetListUnavailable']()).toBe(true);
    expect(component['isPinnedToActiveSet']()).toBe(true);
  });

  /**
   * Regression (found live via a coordinator-driven e2e run, 2026-09-21, matching MEMORY.md's own
   * "unmocked route falls through the dev proxy" trap for `/api/**`): `resource()`'s `.value()`
   * *re-throws* the load error once `status()` is `'error'` — a plain `emoteSetListResource.value()
   * ?? null` still crashes on read, because the throw happens before `??` ever sees anything to fall
   * back on. The real template reads `emoteSetList()` UNCONDITIONALLY (`[sets]="emoteSetList()?.sets
   * ?? []"` on `<app-emote-set-menu>`), regardless of whether the URL carries an `emoteSetId` at all
   * — every stub-template test in this block, and the "pins the display" test right above, happens
   * to dodge the crash because `selectedEmoteSetId()`'s `param === ''` guard short-circuits before it
   * ever reads `emoteSetList()`. This test calls it directly, the way the template does, and is what
   * would have caught the regression before it reached e2e — a live channel with no `?emoteSetId=` in
   * its URL at all (the ordinary case) still rendered nothing at all once `/emote-sets` answered
   * 503, because reading the signal to feed the dropdown took the whole page's change detection down
   * with it.
   */
  it('reading emoteSetList() does not throw once the set list has failed to load, and /totals still loads without any id in the URL (decision 2)', async () => {
    configure();

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    await mountUpTo('a', [emoteSet({ id: 'set-a', isActive: true })], { setsUnavailable: true });

    // The exact read the real template performs unconditionally, regardless of the URL — must not
    // throw, and must degrade to null rather than surface the resource's error.
    expect(() => component['emoteSetList']()).not.toThrow();
    expect(component['emoteSetList']()).toBeNull();

    // No id in the URL at all (decision 2's own wording: "ohne id in der URL, don't wait") — /totals
    // and /series fire despite the set list never having answered successfully.
    expect(component['awaitingEmoteSetId']()).toBe(false);
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [emote('a', 'PeepoA')]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['emotes']()).toHaveLength(1);
  });

  it('retainAmong (not clear) survives a dropdown set switch, like a date-range change on the same channel (AK 51)', async () => {
    configure();

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    await mountUpTo('a', [
      emoteSet({ id: 'set-a', isActive: true }),
      emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
    ]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [a, b]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    component['selection'].onRowClick(asRow(a), { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(asRow(b), { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-b']);

    component['onEmoteSetSelected']('set-b');
    await settle();

    // Only 'a' has a row under set-b and is a member of it — a genuine channel switch would have
    // cleared the whole selection outright instead of reconciling it.
    const totalsReq = httpMock.expectOne(
      (r) =>
        r.url === '/api/channels/a/usage-stats/totals' && r.params.get('emoteSetId') === 'set-b',
    );
    totalsReq.flush([a]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
    // The non-active view is only complete with its member list — the reconciliation waits for it
    // (see the deferred-reconciliation cases in the set-view block), so it has to be flushed here
    // for the pruning to be real rather than merely postponed.
    httpMock
      .expectOne((r) => r.url === liveListUrl('set-b'))
      .flush({
        channelName: 'a',
        sevenTvUserId: null,
        emoteSetId: 'set-b',
        emoteSetName: 'Halloween',
        capacity: 1000,
        totalCount: 1,
        truncated: false,
        emotes: [
          {
            sevenTvEmoteId: '7tv-a',
            name: 'PeepoA',
            defaultName: 'PeepoA',
            imageUrl: '',
            topAllTime: null,
            trending: null,
          },
        ],
      });
    await settle();

    // Retained, not cleared: 'a' stays, 'b' — gone from set-b entirely — is pruned with the #94
    // notice.
    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
    expect(component['selectionPrunedFeedback']()?.count).toBe(1);
  });

  it('an explicit dropdown choice lifts the pin once the list is readable again — even for the id the URL already carried (decision 2026-09-22)', async () => {
    configure();
    router = TestBed.inject(Router);
    await router.navigate([], { queryParams: { emoteSetId: 'set-b' } });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    const sets = [
      emoteSet({ id: 'set-a', isActive: true }),
      emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
    ];
    await mountUpTo('a', sets, { setsUnavailable: true });
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
    expect(component['isPinnedToActiveSet']()).toBe(true);
    expect(component['selectedEmoteSetId']()).toBe('set-a');

    // A later read succeeds (the loud channel.synced reload) — the pin still holds the view.
    component['emoteSetListResource'].reload();
    await settle();
    httpMock.expectOne('/api/channels/a/emote-sets').flush(emoteSetList(sets));
    await settle();
    expect(component['isPinnedToActiveSet']()).toBe(true);
    expect(component['selectedEmoteSetId']()).toBe('set-a');
    httpMock.expectNone((r) => r.url === '/api/channels/a/usage-stats/totals');

    // A deliberate choice is never ignored — not even when it names the id the URL still holds.
    component['onEmoteSetSelected']('set-b');
    await settle();

    expect(component['isPinnedToActiveSet']()).toBe(false);
    expect(component['selectedEmoteSetId']()).toBe('set-b');
    httpMock.expectOne(
      (r) =>
        r.url === '/api/channels/a/usage-stats/totals' && r.params.get('emoteSetId') === 'set-b',
    );
  });

  it('clears the series cache and does not re-fetch the set list on a dropdown set switch (AK 51, E19)', async () => {
    configure();

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    const clearSeriesCache = vi.spyOn(TestBed.inject(UsageStatService), 'clearSeriesCache');
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    await mountUpTo('a', [
      emoteSet({ id: 'set-a', isActive: true }),
      emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
    ]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    clearSeriesCache.mockClear();
    component['onEmoteSetSelected']('set-b');
    await settle();

    expect(clearSeriesCache).toHaveBeenCalledTimes(1);
    // No second /emote-sets request pending — the list is bound to the channel (E19), not to the
    // chosen set.
    httpMock.expectNone('/api/channels/a/emote-sets');

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
  });

  it('choosing the active set writes no emoteSetId back into the URL', async () => {
    configure();
    router = TestBed.inject(Router);

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    await mountUpTo('a', [
      emoteSet({ id: 'set-a', isActive: true }),
      emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
    ]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    // Switch away, then explicitly back to the active set — not a no-op click, the URL genuinely
    // carries an id at this point that the second choice must remove again.
    component['onEmoteSetSelected']('set-b');
    await settle();
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
    expect(router.routerState.snapshot.root.queryParamMap.get('emoteSetId')).toBe('set-b');

    component['onEmoteSetSelected']('set-a');
    await settle();
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(router.routerState.snapshot.root.queryParamMap.get('emoteSetId')).toBeNull();
  });
});

/**
 * E19 / AK 52 (partial): the set list is bound to the channel, never to a reload cadence. Its own
 * describe block because it needs the FakeEventSource + fake-timer choreography every live-reload
 * test in this file uses, which the block above deliberately avoids (real timers, for the
 * `setTimeout(0)` `settle()` helper).
 */
describe('UsageStatsPage — usage.flushed never reloads the set list, channel.synced does (T4.2, E19)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
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
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  /** Fake-timer counterpart of the other block's `settle()` — an `rxResource`'s status/value need a
   *  microtask after a synchronous `.flush()`, and under `vi.useFakeTimers()` a bare `setTimeout(0)`
   *  never fires on its own. */
  async function settle(): Promise<void> {
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
  }

  it('counts /emote-sets requests across a usage.flushed burst (none) and a channel.synced burst (one)', async () => {
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock.expectOne('/api/channels/a/emotes/active-set').flush(
      setStatus({
        activeEmoteSetId: 'set-a',
        trackedSince: '2026-01-01T00:00:00Z',
        // Both dates set so SetStatusFlushProbeGate.shouldRefreshOn short-circuits to false — this
        // test is about the SET LIST's own request count, not about the status probe's, which the
        // usageFlushed branch would otherwise also fire (see the #94 mount() helper's own comment
        // further up in this file for the same fix).
        botsExcludedSince: '2026-01-01T00:00:00Z',
        sharedChatSeparatedSince: '2026-01-01T00:00:00Z',
      }),
    );
    fixture.detectChanges();
    httpMock
      .expectOne('/api/channels/a/emote-sets')
      .flush(emoteSetList([emoteSet({ id: 'set-a', isActive: true })]));
    await settle();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    const source = FakeEventSource.instances[0];

    source.emit({ type: LIVE_EVENT_TYPES.usageFlushed, channel: 'a' });
    vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
    fixture.detectChanges();

    // The silent reload's own totals refetch — draining it is not what this test is about.
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    httpMock.expectNone('/api/channels/a/emote-sets');

    source.emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: 'a' });
    vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
    fixture.detectChanges();

    httpMock.expectOne('/api/channels/a/emotes/active-set').flush(
      setStatus({
        activeEmoteSetId: 'set-a',
        trackedSince: '2026-01-01T00:00:00Z',
        botsExcludedSince: '2026-01-01T00:00:00Z',
        sharedChatSeparatedSince: '2026-01-01T00:00:00Z',
      }),
    );
    // The sync's own totals refetch, which since the O2 review fix follows the status rather than
    // preceding it — draining it is not what this test is about.
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    // The loud reload's own re-fetch — E19: channel.synced DOES reload the set list, unlike
    // usage.flushed above.
    httpMock
      .expectOne('/api/channels/a/emote-sets')
      .flush(emoteSetList([emoteSet({ id: 'set-a', isActive: true })]));
    await settle();
  });
});

/**
 * AK 50: the one DOM-level assertion in this file's T4.2 coverage — everything else reads state, not
 * markup (Regel 12), but "the dropdown offers these sets, this one preselected, that one hidden" is
 * genuinely about what renders. Real template, like the mark-all block above, for the same reason:
 * the dropdown is a real descendant of it, not something a stub template could stand in for.
 */
describe('UsageStatsPage — set dropdown renders the radiogroup with the active set preselected and PERSONAL hidden (AK 50)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
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
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  async function settle(): Promise<void> {
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
  }

  it('offers only the NORMAL sets, active one checked and named', async () => {
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();
    httpMock.expectOne('/api/channels/a/emote-sets').flush(
      emoteSetList([
        emoteSet({ id: 'set-a', name: 'Hauptset', isActive: true }),
        emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
        emoteSet({
          id: 'set-personal',
          name: 'Personal Emotes',
          kind: 'PERSONAL',
          isPersonal: true,
          isActive: false,
        }),
      ]),
    );
    await settle();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
    fixture.detectChanges();

    // Scoped by its own trigger label — `aria-haspopup="dialog"` alone would also match
    // DateRangeMenu's trigger sitting right next to it in the same toolbar row.
    const trigger = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((button) => button.textContent?.includes('emoteSetMenu.label'));
    expect(trigger).not.toBeUndefined();
    trigger!.click();
    fixture.detectChanges();

    const radios = Array.from(
      fixture.nativeElement.querySelectorAll(
        '[role="radiogroup"][aria-label="emoteSetMenu.menuLabel"] [role="radio"]',
      ) as NodeListOf<HTMLElement>,
    );
    // Two, not three — the PERSONAL set is hidden entirely, not shown disabled (decision 4).
    expect(radios).toHaveLength(2);
    expect(radios.map((radio) => radio.textContent?.trim())).toEqual([
      expect.stringContaining('Hauptset'),
      expect.stringContaining('Halloween'),
    ]);

    const checked = radios.find((radio) => radio.getAttribute('aria-checked') === 'true');
    expect(checked?.textContent).toContain('Hauptset');
  });
});

/**
 * Spec #200, T4.3 + T4.4: the grid keyed by 7TV id, and a non-active set's view — the member list
 * loaded beside `/totals`/`/series`, the row classes it produces (8.2), the caption's two
 * independent statements (8.4, AK 60 as a matrix), the delete locks with their reasons (8.3, AK 62)
 * and the preset (8.5). Real timers like the T4.2 block above (the query-param navigation and the
 * resources settle on microtasks), fake ones only where a live event has to fire.
 */
describe('UsageStatsPage — set view: row identity, non-active loading, classes, captions, locks (T4.3/T4.4)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;
  let router: Router;

  const SERIES = { from: '2026-01-01', to: '2026-09-08', liveDays: [], emotes: [] };

  function member(sevenTvEmoteId: string, name: string): ForeignEmoteRow {
    return {
      sevenTvEmoteId,
      name,
      defaultName: name,
      imageUrl: '',
      topAllTime: null,
      trending: null,
    };
  }

  function memberList(
    emotes: ForeignEmoteRow[],
    overrides: Partial<ForeignEmoteSetResponse> = {},
  ): ForeignEmoteSetResponse {
    return {
      channelName: 'a',
      sevenTvUserId: null,
      emoteSetId: 'set-b',
      emoteSetName: 'Halloween',
      capacity: 1000,
      totalCount: emotes.length,
      truncated: false,
      emotes,
      ...overrides,
    };
  }

  function configure(): void {
    // Several tests open more than one view; each gets a fresh module, not a reconfigured one.
    TestBed.resetTestingModule();
    stubNoImportCoverage();
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
    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });
  }

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  function liveListRequests(): TestRequest[] {
    return httpMock.match((r) => LIVE_LIST_URL.test(r.url));
  }

  /**
   * Mounts channel 'a' (active set `set-a`, tracked since 2026-01-01) with the given set in the URL
   * and drives it up to the point where the rows are on screen: permissions, status, set list,
   * `/totals`, `/series` and — for a non-active set — its member list. `members: 'unavailable'`
   * answers the member list with a 503. Returns nothing; the tests read the page's state.
   */
  async function openView(options: {
    emoteSetId?: string;
    totals: EmoteUsageTotalDto[];
    members?: ForeignEmoteSetResponse | 'unavailable';
    observations?: EmoteSetSummary['observations'];
    extraSets?: EmoteSetSummary[];
    // addendum N2: a run already settled in the service before the page's constructor ever
    // runs — `watchRunSettle`'s `seen` captures it as the starting point, so it must not replay.
    presettledRestoreRun?: RestoreRunInfo;
  }): Promise<void> {
    configure();
    router = TestBed.inject(Router);
    if (options.emoteSetId) {
      await router.navigate([], { queryParams: { emoteSetId: options.emoteSetId } });
    }
    if (options.presettledRestoreRun) {
      TestBed.inject(SevenTvRestoreService).run.set(options.presettledRestoreRun);
    }

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
    await settle();

    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock.expectOne('/api/channels/a/emotes/active-set').flush(
      setStatus({
        activeEmoteSetId: 'set-a',
        capacity: 600,
        occupiedSlots: 10,
        trackedSince: '2026-01-01T00:00:00Z',
        botsExcludedSince: '2026-01-02T00:00:00Z',
        sharedChatSeparatedSince: '2026-01-02T00:00:00Z',
      }),
    );
    fixture.detectChanges();
    fixture.detectChanges();
    httpMock.expectOne('/api/channels/a/emote-sets').flush(
      emoteSetList([
        emoteSet({
          id: 'set-a',
          isActive: true,
          observations: [{ fromUtc: '2026-01-01T00:00:00Z', toUtc: null }],
        }),
        emoteSet({
          id: 'set-b',
          name: 'Halloween',
          isActive: false,
          observations: options.observations ?? [],
        }),
        ...(options.extraSets ?? []),
      ]),
    );
    await settle();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', options.totals);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    if (options.members === 'unavailable') {
      liveListRequests().forEach((request) =>
        request.flush(
          { errorCode: 'foreign_channel_seventv_unavailable' },
          { status: 503, statusText: 'Service Unavailable' },
        ),
      );
    } else if (options.members) {
      const members = options.members;
      liveListRequests().forEach((request) => request.flush(members));
    }
    await settle();
  }

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  // --- T4.3: the key switch ------------------------------------------------------------------

  it('keeps two Guid-less live members as two separate, individually selectable rows, and retainAmong keeps the right one (AK 54)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [],
      members: memberList([member('7tv-x', 'PumpkinX'), member('7tv-y', 'PumpkinY')]),
    });
    const [x, y] = component['emotes']();
    expect(x.emoteId).toBeNull();
    expect(y.emoteId).toBeNull();

    component['selection'].onRowClick(x, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['7tv-x']);
    expect(component['selection'].isSelected(y)).toBe(false);
    component['selection'].onRowClick(y, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-x', '7tv-y']);

    // A reconciliation against a view where only 'y' survived drops exactly 'x'.
    component['selection'].retainAmong(component['emotes']().filter((row) => row === y));
    expect(component['selection'].selectedKeys()).toEqual(['7tv-y']);
    // AK 55 at the model level: the inner @for tracks sevenTvEmoteId, unique per row.
    const keys = component['atlasOrder']().map((row) => row.sevenTvEmoteId);
    expect(new Set(keys).size).toBe(keys.length);
  });

  it('opens the drilldown only for a row with a Guid and counts, freezing the shown set into its data (7.2 drilldown gate)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('gone', 'OldPumpkin', 30)],
      members: memberList([member('7tv-x', 'PumpkinX'), member('7tv-gone', 'OldPumpkin')]),
    });
    const openSpy = vi.spyOn(TestBed.inject(Dialog), 'open').mockReturnValue({
      closed: of(undefined),
    } as ReturnType<Dialog['open']>);

    const uncounted = component['emotes']().find((row) => row.sevenTvEmoteId === '7tv-x')!;
    const left = component['emotes']().find((row) => row.sevenTvEmoteId === '7tv-gone')!;

    expect(component['canDrilldown'](uncounted)).toBe(false);
    component['openDrilldown'](uncounted);
    expect(openSpy).not.toHaveBeenCalled();

    expect(component['canDrilldown'](left)).toBe(true);
    component['openDrilldown'](left);
    expect(openSpy).toHaveBeenCalledTimes(1);
    const data = openSpy.mock.calls[0][1]?.data as EmoteDrilldownData;
    expect(data.emoteId).toBe('gone');
    expect(data.emoteSetId).toBe('set-b');
  });

  it('resolves a null-session ballot to Guids in the active view, and a set-session ballot to 7TV ids in a settled non-active view (E4, spec 6.9, K6)', async () => {
    await openView({ totals: [emote('a', 'PeepoA'), emote('b', 'PeepoB')] });
    const [a, b] = component['emotes']();
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(b, { shiftKey: false } as MouseEvent);

    // Keys are 7TV ids, the null-session ballot the dialog would submit is Guids.
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-b']);
    expect([...component['voteBallotEmoteIds']()].sort()).toEqual(['a', 'b']);
    expect(component['voteLocked']()).toBe(false);

    // A fresh, direct mount on a non-active set — settled, not mid-switch (spec §36) — no longer
    // locks voting at all (T6.3 lifts the "set sessions are K6" interim lock): only a view that is
    // switching, still loading, or whose member list came back unavailable/truncated still does,
    // see the "K4 fix round" describe block above. Its ballot speaks 7TV ids.
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA'), member('7tv-c', 'PeepoC')]),
    });
    const nonActiveRows = component['emotes']();
    component['selection'].onRowClick(nonActiveRows[0], { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(nonActiveRows[1], { shiftKey: false } as MouseEvent);

    expect(component['voteLocked']()).toBe(false);
    // Both rows contribute their 7TV id, including 'c' — a class-2b, Guid-less live member (spec
    // 7.2) that a null-session ballot would have silently dropped (voteBallotEmoteIds.flatMap).
    expect([...component['voteBallotSevenTvEmoteIds']()].sort()).toEqual(['7tv-a', '7tv-c']);
  });

  describe('import obeys the shared set-view lock', () => {
    function spyOnPicker() {
      return vi.spyOn(TestBed.inject(Dialog), 'open').mockReturnValue({
        closed: new Subject<unknown>(),
      } as unknown as ReturnType<Dialog['open']>);
    }

    it.each([
      ['unavailable', 'unavailable' as const],
      ['truncated', memberList([member('7tv-a', 'PeepoA')], { truncated: true, totalCount: 900 })],
    ])('is locked like delete and vote while the member list is %s', async (_label, members) => {
      await openView({
        emoteSetId: 'set-b',
        totals: [emote('a', 'PeepoA')],
        members,
      });
      component['markAll']();
      expect(component['selection'].selectedKeys()).toHaveLength(1);
      expect(component['deleteLockReasonKey']()).not.toBeNull();
      const openSpy = spyOnPicker();

      expect(component['importShortcutLocked']()).toBe(true);
      expect(component['transferButtonDisabled']()).toBe(true);
      component['openImportTarget']();
      expect(openSpy).not.toHaveBeenCalled();
    });

    it('stays open for a settled view with a whole member list', async () => {
      await openView({
        emoteSetId: 'set-b',
        totals: [emote('a', 'PeepoA')],
        members: memberList([member('7tv-a', 'PeepoA')]),
      });
      component['markAll']();

      expect(component['importShortcutLocked']()).toBe(false);
      expect(component['transferButtonDisabled']()).toBe(false);
    });
  });

  // --- T4.4: loading and reloads ---------------------------------------------------------------

  it('loads the member list beside /totals and /series for a non-active set, and holds the union until it is there (8.3, AK 51)', async () => {
    configure();
    router = TestBed.inject(Router);
    await router.navigate([], { queryParams: { emoteSetId: 'set-b' } });
    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
    await settle();
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();
    fixture.detectChanges();
    httpMock
      .expectOne('/api/channels/a/emote-sets')
      .flush(
        emoteSetList([
          emoteSet({ id: 'set-a', isActive: true }),
          emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
        ]),
      );
    await settle();

    const liveRequest = httpMock.expectOne((r) => r.url === liveListUrl('set-b'));
    // A params-driven load after choosing the set may use the Api's cache; only a loud reload
    // bypasses it (see the refresh-button case below).
    expect(liveRequest.request.params.get('refresh')).toBeNull();
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [emote('a', 'PeepoA')]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    fixture.detectChanges();

    // The counted rows are in, the member list is not — skeleton, not a half-built union.
    expect(component['liveMembersState']()).toBe('loading');
    expect(component['viewLoading']()).toBe(true);

    liveRequest.flush(memberList([member('7tv-a', 'PeepoA'), member('7tv-x', 'PumpkinX')]));
    await settle();

    expect(component['viewLoading']()).toBe(false);
    expect(component['emotes']().map((row) => row.sevenTvEmoteId)).toEqual(['7tv-a', '7tv-x']);
  });

  it('usage.flushed reloads only the numbers, channel.synced the member list too (AK 52)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA')]),
    });
    vi.useFakeTimers();
    const source = FakeEventSource.instances[0];

    source.emit({ type: LIVE_EVENT_TYPES.usageFlushed, channel: 'a' });
    vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
    fixture.detectChanges();
    httpMock.expectOne(
      (r) =>
        r.url === '/api/channels/a/usage-stats/totals' && r.params.get('emoteSetId') === 'set-b',
    );
    expect(liveListRequests()).toHaveLength(0);

    source.emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: 'a' });
    vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
    const reloaded = liveListRequests();
    expect(reloaded).toHaveLength(1);
    expect(reloaded[0].request.url).toBe(liveListUrl('set-b'));
    // A loud reload asks the Api to bypass its cache (spec 8.3) — only this one request.
    expect(reloaded[0].request.params.get('refresh')).toBe('true');
  });

  // --- T4.4: row classes (8.2) ----------------------------------------------------------------

  it('keeps rows without counts out of sums, bands and the strip, as a trailing group in name order (AK 56)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'Alpha', 40), emote('z', 'Zulu', 60)],
      members: memberList([
        member('7tv-a', 'Alpha'),
        member('7tv-z', 'Zulu'),
        member('7tv-q', 'Quebec'),
        member('7tv-c', 'Charlie'),
      ]),
    });

    expect(component['totalUsage']()).toBe(100);
    expect(component['bands']().flatMap((band) => band.items.map((row) => row.emoteName))).toEqual([
      'Zulu',
      'Alpha',
    ]);
    expect(component['distribution']()).toHaveLength(2);
    // The null group comes last, alphabetical, whatever the toolbar's sort says.
    expect(component['atlasOrder']().map((row) => row.emoteName)).toEqual([
      'Zulu',
      'Alpha',
      'Charlie',
      'Quebec',
    ]);
    const groups = component['rows']().filter((row) => row.kind === 'band');
    expect(groups.at(-1)).toMatchObject({ kind: 'band', band: 'uncounted', count: 2 });
    // The label hangs on the missing count (E17), not on the missing Guid.
    component['inspect'](component['atlasOrder']()[2]);
    expect(component['inspectedBand']()).toBe('uncounted');
    expect(component['inspectedShare']()).toBeNull();
    // …and no curve: a zero-filled baseline would claim "counted, never used".
    expect(component['inspectedPoints']()).toEqual([]);
    component['inspect'](component['atlasOrder']()[0]);
    expect(component['inspectedPoints']().length).toBeGreaterThan(0);
  });

  it('hides a counted row that left the set: it is not in the grid, the count or the denominator, while a live row without counts stays (E23 reversed)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('gone', 'OldPumpkin', 30), emote('a', 'Alpha', 70)],
      members: memberList([member('7tv-a', 'Alpha'), member('7tv-fresh', 'Fresh')]),
    });

    const ids = component['emotes']().map((row) => row.sevenTvEmoteId);
    expect(ids).toEqual(['7tv-a', '7tv-fresh']);
    expect(component['atlasOrder']().map((row) => row.sevenTvEmoteId)).toEqual(ids);
    expect(component['emotes']().every((row) => row.membership === 'live')).toBe(true);
    // 70, not 100: the departed row's 30 uses are not part of the shown set any more.
    expect(component['totalUsage']()).toBe(70);

    component['markAll']();
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-fresh']);
  });

  it('keeps a counted row visible when the member list is truncated — departure is only inferred from a complete list', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('maybe', 'MaybeStillIn', 30), emote('a', 'Alpha', 70)],
      members: memberList([member('7tv-a', 'Alpha')], { truncated: true, totalCount: 900 }),
    });

    const ids = component['emotes']().map((row) => row.sevenTvEmoteId);
    expect(ids).toContain('7tv-maybe');
    expect(ids).toContain('7tv-a');
    expect(component['totalUsage']()).toBe(100);
    // The actions stay off while the list is partial.
    expect(component['deleteLockReasonKey']()).toBe('usageStats.setView.lock.truncated');
  });

  it("counts a #74 duplicate cell as two slots of the shown set's own budget (AK 58)", async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('d', 'Dupe', 5)],
      members: memberList(
        [member('7tv-d', 'Dupe'), member('7tv-d', 'DupeAlias'), member('7tv-e', 'Echo')],
        { capacity: 1000, totalCount: 3 },
      ),
    });
    const dupe = component['emotes']().find((row) => row.sevenTvEmoteId === '7tv-d')!;
    expect(dupe.slotCount).toBe(2);
    expect(dupe.aliases).toEqual(['Dupe', 'DupeAlias']);

    component['selection'].onRowClick(dupe, { shiftKey: false } as MouseEvent);
    expect(component['pendingRemovalSlots']()).toBe(2);
    // The budget is the member list's, not the active set's status (600 / 10).
    expect(component['slotBudget']()).toEqual({ capacity: 1000, occupied: 3 });
    expect(component['projectedSlots']()).toEqual({ projected: 1, capacity: 1000 });
  });

  // --- T5.1: the delete run speaks 7TV ids (spec 7.2, E18, AK 72) ------------------------------

  it('hands a live member without a Guid and a duplicate cell with both aliases to the delete run', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('d', 'Dupe', 5)],
      members: memberList([
        member('7tv-d', 'Dupe'),
        member('7tv-d', 'DupeAlias'),
        member('7tv-x', 'PumpkinX'),
      ]),
    });
    for (const row of component['emotes']()) {
      component['selection'].onRowClick(row, { shiftKey: false } as MouseEvent);
    }

    const forDelete = component['selectedForDelete']();
    expect(forDelete.map((row) => [row.sevenTvEmoteId, row.emoteId, row.aliases])).toEqual([
      ['7tv-d', 'd', ['Dupe', 'DupeAlias']],
      ['7tv-x', undefined, ['PumpkinX']],
    ]);
  });

  it('drops the deleted cells by their 7TV id and frees their slots once the panel reports them (AK 72)', async () => {
    await openView({ totals: [emote('a', 'Alpha', 40), emote('b', 'Beta', 60)] });
    // The panel's `deleted` names the run's keys; onDeleted edits the rows only for the run of the
    // set and channel on screen.
    TestBed.inject(SevenTvDeleteService).lastRun.set({
      setId: 'set-a',
      channelName: 'a',
      targetOwnerTwitchId: null,
      result: { doneKeys: ['7tv-a'], items: [], startedAt: 0, finishedAt: 1 },
    });

    component['onDeleted'](['7tv-a']);

    // The Guid of this row is 'a' — a Guid-keyed filter would have kept it.
    expect(component['emotes']().map((row) => row.sevenTvEmoteId)).toEqual(['7tv-b']);
    expect(component['slotBudget']()).toEqual({ capacity: 600, occupied: 9 });
  });

  // Operator decision 2026-09-22: the active view knows one name per id (slotCount 1), but the
  // panel recorded both aliases of a #74 duplicate from its live read — the one REMOVE freed two.
  it('frees every entry the run recorded for a cell in the active view, not just its one slot', async () => {
    await openView({ totals: [emote('a', 'Alpha', 40), emote('b', 'Beta', 60)] });
    TestBed.inject(SevenTvDeleteService).lastRun.set({
      setId: 'set-a',
      channelName: 'a',
      targetOwnerTwitchId: null,
      result: {
        doneKeys: ['7tv-a'],
        items: [
          {
            key: '7tv-a',
            emoteId: 'a',
            sevenTvEmoteId: '7tv-a',
            name: 'Alpha',
            aliases: ['Alpha', 'AlphaTwo'],
            status: 'done',
            completedSteps: 1,
            failedStep: null,
          },
        ],
        startedAt: 0,
        finishedAt: 1,
      },
    });

    component['onDeleted'](['7tv-a']);

    expect(component['slotBudget']()).toEqual({ capacity: 600, occupied: 8 });
  });

  // #275, Plan-275 Festlegung 14: `deleteRunActive` used to end the moment the engine stopped
  // (`isRunning() === false`) and the closing report had not yet started (`syncReport === 'idle'`)
  // — exactly the `settling` window a run with an `unknown` row now spends re-reading the set before
  // it knows what to report. Without this the set dropdown unlocked mid-read.
  describe('deleteRunActive (#275)', () => {
    function settlingRun(overrides: Partial<DeleteRunInfo> = {}): DeleteRunInfo {
      return {
        runId: 'delete-1',
        phase: 'settling',
        destructive: true,
        channelName: 'a',
        expectedChannelName: 'a',
        setId: 'set-a',
        targetOwnerTwitchId: null,
        result: null,
        syncReport: 'idle',
        syncReportReason: null,
        ...overrides,
      };
    }

    it('is true while the shown run is settling, even though the engine already stopped and nothing is pending', async () => {
      await openView({ totals: [emote('a', 'Alpha', 40)] });

      TestBed.inject(SevenTvDeleteService).run.set(settlingRun());

      expect(component['deleteRunActive']()).toBe(true);
    });

    it('is false again once the settled run has closed with an idle report', async () => {
      await openView({ totals: [emote('a', 'Alpha', 40)] });

      TestBed.inject(SevenTvDeleteService).run.set(
        settlingRun({
          phase: 'closed',
          result: { doneKeys: [], items: [], startedAt: 0, finishedAt: 1 },
        }),
      );

      expect(component['deleteRunActive']()).toBe(false);
    });

    // An existing case, unchanged by #275 — pinned so a future refactor of the
    // `computed()` cannot silently drop this disjunct.
    it('stays true while the closing sync-deleted report is still pending (unchanged)', async () => {
      await openView({ totals: [emote('a', 'Alpha', 40)] });

      TestBed.inject(SevenTvDeleteService).run.set(
        settlingRun({
          phase: 'reporting',
          syncReport: 'pending',
          result: { doneKeys: ['7tv-a'], items: [], startedAt: 0, finishedAt: 1 },
        }),
      );

      expect(component['deleteRunActive']()).toBe(true);
    });
  });

  it('names a name twin by the set the dropdown list calls it, and never adds its numbers (AK 59)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [{ ...emote('a', 'Alpha', 12), nameTwinEmoteSetIds: ['set-a', 'zzzzzz-unknown'] }],
      members: memberList([member('7tv-a', 'Alpha')]),
    });
    const twin = component['emotes']()[0];

    expect(component['nameTwinSetNames'](twin)).toBe('Hauptset, nknown');
    expect(component['totalUsage']()).toBe(12);
  });

  // --- addendum N2 (AK 37): a run's own settle reloads the chosen non-active set's member list ---

  /** A finished run's engine result with `done` rows for the given keys, plus any other rows
   *  (#279: a failed or unknown row that still confirmed a step) a case needs alongside them. */
  function runResult(doneKeys: string[], items: RunQueueItem[] = []) {
    return { doneKeys, items, startedAt: 0, finishedAt: 1 };
  }

  /** A non-done queue row for the #279 cases — defaults to a Replace row whose REMOVE confirmed
   *  before its ADD failed (`completedSteps: 1, failedStep: 1`). */
  function runItem(overrides: Partial<RunQueueItem> = {}): RunQueueItem {
    return {
      key: '7tv-z',
      sevenTvEmoteId: '7tv-z',
      name: 'Zeta',
      status: 'failed',
      completedSteps: 1,
      failedStep: 1,
      ...overrides,
    };
  }

  /** Settles a restore run into `setId` the way `SevenTvRestoreService.onRunComplete` does. */
  function settleRestore(setId: string, doneKeys: string[], items: RunQueueItem[] = []): void {
    const run: RestoreRunInfo = {
      runId: 'restore-1',
      phase: 'reporting',
      destructive: false,
      targetSetId: setId,
      expectedChannelName: null,
      resyncChannelName: 'a',
      hostChannelName: 'a',
      setName: setId,
      ownerOrChannelLabel: 'a',
      targetOwnerTwitchId: null,
      result: runResult(doneKeys, items),
      syncReport: 'pending',
      syncReportReason: null,
      resyncTrigger: 'idle',
    };
    TestBed.inject(SevenTvRestoreService).run.set(run);
  }

  function settleImport(setId: string, doneKeys: string[], items: RunQueueItem[] = []): void {
    TestBed.inject(SevenTvImportService).run.set({
      runId: 'import-1',
      phase: 'reporting',
      destructive: false,
      targetSetId: setId,
      settlement: 'settled',
      result: runResult(doneKeys, items),
      syncReport: 'pending',
    } as unknown as ImportRunInfo);
  }

  function settleDelete(setId: string, doneKeys: string[], items: RunQueueItem[] = []): void {
    TestBed.inject(SevenTvDeleteService).lastRun.set({
      setId,
      channelName: 'a',
      targetOwnerTwitchId: null,
      result: runResult(doneKeys, items),
    });
  }

  /** Settles an undo run into `setId` the way `SevenTvUndoService.settleRun` does — `doneKeys` with
   *  its `partial` rows, which are `done` for the engine. */
  function settleUndo(setId: string, doneKeys: string[], items: RunQueueItem[] = []): void {
    TestBed.inject(SevenTvUndoService).run.set({
      runId: 'undo-1',
      phase: 'reporting',
      destructive: true,
      targetSetId: setId,
      settlement: 'settled',
      result: runResult(doneKeys, items),
      removalReport: 'pending',
    } as unknown as UndoRunInfo);
  }

  function liveListRequestsFor(setId: string): TestRequest[] {
    return liveListRequests().filter((r) => r.request.url === liveListUrl(setId));
  }

  it.each([
    ['restore', settleRestore],
    ['delete', settleDelete],
    ['import', settleImport],
    ['undo', settleUndo],
  ] as const)(
    'reloads the chosen non-active set’s members bypassing the cache once a %s run into it settles',
    async (_kind, settleRun) => {
      await openView({
        emoteSetId: 'set-b',
        totals: [],
        members: memberList([member('7tv-x', 'PumpkinX')]),
      });

      settleRun('set-b', ['7tv-y']);
      await settle();

      const reloaded = liveListRequests();
      expect(reloaded).toHaveLength(1);
      expect(reloaded[0].request.url).toBe(liveListUrl('set-b'));
      expect(reloaded[0].request.params.get('refresh')).toBe('true');
    },
  );

  // #256: a run's report states live on its record, so each report answer replaces the record
  // while the settled result stays the same object — one settle, one reload.
  it('reloads once per settled import, not again for each report answer on the same run', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [],
      members: memberList([member('7tv-x', 'PumpkinX')]),
    });
    const importService = TestBed.inject(SevenTvImportService);

    settleImport('set-b', ['7tv-y']);
    await settle();
    const reloaded = liveListRequests();
    expect(reloaded).toHaveLength(1);
    reloaded[0].flush(memberList([member('7tv-x', 'PumpkinX'), member('7tv-y', 'PumpkinY')]));
    await settle();

    const settled = importService.run();
    if (settled === null) {
      throw new Error('run expected');
    }
    importService.run.set({ ...settled, phase: 'closed', syncReport: 'succeeded' });
    await settle();

    expect(liveListRequests()).toHaveLength(0);
  });

  // The undo's result exists from the engine's end on, before its re-read settles it (spec 4.4
  // point 12) — the member list follows the settled outcome, not the snapshot.
  it('waits for an undo run to settle before it reloads the members of its non-active target', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [],
      members: memberList([member('7tv-x', 'PumpkinX')]),
    });
    const undoService = TestBed.inject(SevenTvUndoService);

    undoService.run.set({
      runId: 'undo-1',
      phase: 'settling',
      destructive: true,
      targetSetId: 'set-b',
      settlement: 'pending',
      result: runResult(['7tv-y']),
    } as unknown as UndoRunInfo);
    await settle();
    expect(liveListRequests()).toHaveLength(0);

    settleUndo('set-b', ['7tv-y']);
    await settle();
    expect(liveListRequests()).toHaveLength(1);
  });

  it('sends nothing for a run without a row that may have changed the set, or one into the active set', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [],
      members: memberList([member('7tv-x', 'PumpkinX')]),
    });

    settleRestore('set-b', []);
    settleImport('set-a', ['7tv-y']);
    await settle();

    expect(liveListRequests()).toHaveLength(0);
  });

  it('marks a non-active target that is not chosen, so exactly its next load bypasses the cache', async () => {
    await openView({ totals: [] });

    settleRestore('set-b', ['7tv-y']);
    await settle();
    expect(liveListRequests()).toHaveLength(0);

    component['onEmoteSetSelected']('set-b');
    await settle();
    const request = liveListRequestsFor('set-b');
    expect(request).toHaveLength(1);
    expect(request[0].request.params.get('refresh')).toBe('true');
  });

  it('drops the mark on the next load of another set', async () => {
    await openView({
      totals: [],
      extraSets: [emoteSet({ id: 'set-c', name: 'Winter', isActive: false })],
    });

    settleDelete('set-b', ['7tv-y']);
    await settle();

    component['onEmoteSetSelected']('set-c');
    await settle();
    const other = liveListRequestsFor('set-c');
    expect(other).toHaveLength(1);
    expect(other[0].request.params.get('refresh')).toBeNull();
    other[0].flush(memberList([], { emoteSetId: 'set-c' }));
    await settle();

    component['onEmoteSetSelected']('set-b');
    await settle();
    const target = liveListRequestsFor('set-b');
    expect(target).toHaveLength(1);
    expect(target[0].request.params.get('refresh')).toBeNull();
  });

  it('drops the mark on a channel switch', async () => {
    await openView({ totals: [] });

    settleRestore('set-b', ['7tv-y']);
    await settle();
    // The mark left by the settle above, for channel 'a' — read directly rather than through a
    // second channel's full bootstrap, which this describe block's other tests do not exercise.
    expect(component['liveMembersRefreshFor']).toEqual({ channelName: 'a', emoteSetId: 'set-b' });

    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();

    expect(component['liveMembersRefreshFor']).toBeNull();
  });

  it('does not replay a run that had already settled when the page mounted', async () => {
    // Set before the component's constructor ever runs: watchRunSettle's `seen` captures this as
    // its starting point, so the settle effect must never fire for it.
    await openView({
      totals: [],
      presettledRestoreRun: {
        runId: 'restore-presettled',
        phase: 'closed',
        destructive: false,
        targetSetId: 'set-b',
        expectedChannelName: null,
        resyncChannelName: 'a',
        hostChannelName: 'a',
        setName: 'set-b',
        ownerOrChannelLabel: 'a',
        targetOwnerTwitchId: null,
        result: runResult(['7tv-y']),
        syncReport: 'succeeded',
        syncReportReason: null,
        resyncTrigger: 'idle',
      },
    });

    component['onEmoteSetSelected']('set-b');
    await settle();
    const request = liveListRequestsFor('set-b');
    expect(request).toHaveLength(1);
    expect(request[0].request.params.get('refresh')).toBeNull();
  });

  // #279: import and undo are multi-step per row, so a row can change the target set on 7TV
  // without ever reaching `done` — the reload must not depend on `doneKeys` alone for these two.
  it.each([
    ['import', settleImport],
    ['undo', settleUndo],
  ] as const)(
    'reloads the non-active target of a %s run when a row failed after a confirmed step, even without a done key',
    async (_kind, settleRun) => {
      await openView({
        emoteSetId: 'set-b',
        totals: [],
        members: memberList([member('7tv-x', 'PumpkinX')]),
      });

      settleRun('set-b', [], [runItem({ status: 'failed', completedSteps: 1, failedStep: 1 })]);
      await settle();

      const reloaded = liveListRequests();
      expect(reloaded).toHaveLength(1);
      expect(reloaded[0].request.url).toBe(liveListUrl('set-b'));
      expect(reloaded[0].request.params.get('refresh')).toBe('true');
    },
  );

  it.each([
    ['restore', settleRestore],
    ['delete', settleDelete],
    ['import', settleImport],
    ['undo', settleUndo],
  ] as const)(
    'reloads the non-active target of a %s run when a row is still unknown after the re-read',
    async (_kind, settleRun) => {
      await openView({
        emoteSetId: 'set-b',
        totals: [],
        members: memberList([member('7tv-x', 'PumpkinX')]),
      });

      settleRun('set-b', [], [runItem({ status: 'unknown', completedSteps: 0, failedStep: 0 })]);
      await settle();

      const reloaded = liveListRequests();
      expect(reloaded).toHaveLength(1);
      expect(reloaded[0].request.url).toBe(liveListUrl('set-b'));
      expect(reloaded[0].request.params.get('refresh')).toBe('true');
    },
  );

  it.each([
    ['restore', settleRestore],
    ['delete', settleDelete],
    ['import', settleImport],
    ['undo', settleUndo],
  ] as const)(
    'sends nothing for a %s run whose rows never confirmed a step',
    async (_kind, settleRun) => {
      await openView({
        emoteSetId: 'set-b',
        totals: [],
        members: memberList([member('7tv-x', 'PumpkinX')]),
      });

      settleRun(
        'set-b',
        [],
        [
          runItem({ status: 'failed', completedSteps: 0, failedStep: 0 }),
          runItem({
            key: '7tv-w',
            sevenTvEmoteId: '7tv-w',
            status: 'cancelled',
            completedSteps: 0,
            failedStep: null,
          }),
        ],
      );
      await settle();

      expect(liveListRequests()).toHaveLength(0);
    },
  );

  // --- #293: a second loud reload landing before the first one's loader ran -------------------

  it("keeps the earlier loud reload's refresh flag when a second one lands in the same turn, before the resource's loader ran", async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA')]),
    });

    // Two refresh-button clicks back-to-back, with nothing flushed in between (no `settle()`, no
    // `detectChanges()`): `rxResource.reload()` flips the resource's internal status to loading
    // synchronously, before its own load effect has actually run `stream` — the one place that reads
    // and clears `liveMembersRefreshFor`. So the second call's `reload()` already sees the resource
    // loading and returns `false`, in the very same synchronous turn as the first call's — no need
    // for two different components' effects to interleave to reproduce the race.
    // Expire the 60s preview cache so a lost flag shows up as a request without `refresh=true` (the
    // issue's symptom), not as no request at all.
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(Date.now() + 61_000);

    component['refresh']();
    component['refresh']();
    httpMock
      .match((r) => r.url === '/api/channels/a/emotes/active-set')
      .forEach((request) =>
        request.flush(
          setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }),
        ),
      );
    await settle();

    // Exactly one GET went out for the loud reload (the second reload() call was a no-op), and it
    // still carries refresh=true — before the fix, the second call's failed reload() blanked the flag
    // the first call had just set, and this request went out without it.
    const reloaded = liveListRequests();
    expect(reloaded).toHaveLength(1);
    expect(reloaded[0].request.url).toBe(liveListUrl('set-b'));
    expect(reloaded[0].request.params.get('refresh')).toBe('true');
  });

  it('adds no extra request for a loud reload landing while the first one is already in flight, and leaves nothing marked for the next params-driven load', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA')]),
    });

    // The first loud reload: by the time its HTTP request sits here unflushed, its `stream` has
    // already run — it read and cleared the flag into this request's `refresh=true`.
    component['refresh']();
    httpMock
      .match((r) => r.url === '/api/channels/a/emotes/active-set')
      .forEach((request) =>
        request.flush(
          setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }),
        ),
      );
    await settle();
    // `httpMock.match()` removes what it finds from the harness's own open-request list (that is how
    // `expectOne` can tell "found none" from "found more than one"), so this reference is the only
    // way to reach this request again — a second `liveListRequests()` call would no longer see it,
    // flushed or not.
    const inFlight = liveListRequests();
    expect(inFlight).toHaveLength(1);
    expect(inFlight[0].request.params.get('refresh')).toBe('true');

    // A second loud reload lands while that request is still outstanding: reload() again returns
    // false (the resource is still loading), but this time "the value before this call" is already
    // null — the first stream run consumed it — so restoring it is a no-op: no extra request, and
    // nothing left marked.
    component['refresh']();
    httpMock
      .match((r) => r.url === '/api/channels/a/emotes/active-set')
      .forEach((request) =>
        request.flush(
          setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }),
        ),
      );
    await settle();
    // No new request besides the one already captured above.
    expect(liveListRequests()).toHaveLength(0);
    expect(component['liveMembersRefreshFor']).toBeNull();

    inFlight[0].flush(memberList([member('7tv-a', 'PeepoA')]));
    await settle();

    // The next params-driven load must land on set-b again: switching to a *different* set would
    // have its own `stream` run clear the field unconditionally no matter what was in it, so it
    // cannot tell the fix apart from a naive variant that simply never resets the field on a refused
    // reload. Going through set-a first — the active set, which `liveMembersResource` never
    // fetches — reselects set-b without any intervening load having already cleared a leaked mark.
    component['onEmoteSetSelected']('set-a');
    await settle();
    component['onEmoteSetSelected']('set-b');
    await settle();
    // The cache is warm, so nothing goes out; a leaked mark would bypass it and show up here.
    expect(liveListRequestsFor('set-b')).toHaveLength(0);
  });

  it('keeps a mark left for a non-chosen target through a refused loud reload of the chosen set', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA')]),
      extraSets: [emoteSet({ id: 'set-c', name: 'Winter', isActive: false })],
    });

    // The chosen set's own loud reload, its request already in flight — `stream` has already run and
    // consumed the flag it was given.
    component['refresh']();
    httpMock
      .match((r) => r.url === '/api/channels/a/emotes/active-set')
      .forEach((request) =>
        request.flush(
          setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }),
        ),
      );
    await settle();

    // A run settles into a different, non-chosen, non-active target: onOwnRunSettled's third branch
    // marks it directly (no reload() call of its own).
    settleRestore('set-c', ['7tv-y']);
    await settle();

    // A second loud reload of the still-chosen set 'set-b' — refused, since the first one's request
    // is still outstanding — must not clear the mark for 'set-c' it finds in the field.
    component['refresh']();
    httpMock
      .match((r) => r.url === '/api/channels/a/emotes/active-set')
      .forEach((request) =>
        request.flush(
          setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }),
        ),
      );
    await settle();

    component['onEmoteSetSelected']('set-c');
    await settle();
    const request = liveListRequestsFor('set-c');
    expect(request).toHaveLength(1);
    expect(request[0].request.params.get('refresh')).toBe('true');
  });

  // --- T4.4: the caption matrix (8.4, AK 60) ----------------------------------------------------

  function captionKeys(): string[] {
    return component['setViewCaptions']().map((sentence) => sentence.key);
  }

  it('not observed and no counts: B− then Z− (AK 60, case 1)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [],
      members: memberList([member('7tv-x', 'PumpkinX')]),
      observations: [],
    });

    expect(captionKeys()).toEqual([
      'usageStats.setView.facts.notObserved',
      'usageStats.setView.facts.noCounts',
    ]);
  });

  it('not observed but with counts: B− alone, nothing said about the numbers (AK 60, case 2 — the migration/rejoin case)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'Alpha', 12)],
      members: memberList([member('7tv-a', 'Alpha')]),
      observations: [],
    });

    expect(captionKeys()).toEqual(['usageStats.setView.facts.notObserved']);
  });

  it('observed from inside the range: B~ with the interval start; plus Z− when there are no counts (AK 60, case 3)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'Alpha', 0)],
      members: memberList([member('7tv-a', 'Alpha')]),
      observations: [{ fromUtc: '2026-03-01T12:00:00Z', toUtc: null }],
    });

    expect(captionKeys()).toEqual([
      'usageStats.setView.facts.countedSince',
      'usageStats.setView.facts.noCounts',
    ]);
    expect(component['setViewCaptions']()[0].params).toEqual({
      date: component['formatDate']('2026-03-01T12:00:00Z'),
    });
  });

  it('the active set with an open interval since tracking start: no set sentence at all, as today (AK 60, case 4)', async () => {
    await openView({ totals: [emote('a', 'Alpha', 0)] });

    expect(component['isNonActiveView']()).toBe(false);
    expect(captionKeys()).toEqual([]);
  });

  // --- T4.4: locks (8.3, AK 62) and the preset (8.5) -------------------------------------------

  it('shows the counted rows without a readable member list, says so, and locks deleting with that reason (AK 62)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'Alpha', 12)],
      members: 'unavailable',
      observations: [{ fromUtc: '2025-12-01T00:00:00Z', toUtc: null }],
    });

    expect(component['liveMembersState']()).toBe('unavailable');
    expect(component['viewLoading']()).toBe(false);
    expect(component['emotes']().map((row) => row.sevenTvEmoteId)).toEqual(['7tv-a']);
    expect(component['deleteLockReasonKey']()).toBe('usageStats.setView.lock.membersUnavailable');
    expect(captionKeys()).toEqual(['usageStats.setView.membersUnavailable']);
    // No budget to project against — never the active set's numbers under this set's name.
    expect(component['slotBudget']()).toBeNull();
  });

  it('a truncated member list locks deleting and voting with its own reason; a whole one locks neither (K5/T5.3, K6)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [],
      members: memberList([member('7tv-a', 'Alpha')], { truncated: true, totalCount: 1200 }),
    });
    expect(component['deleteLockReasonKey']()).toBe('usageStats.setView.lock.truncated');
    // A ballot the page cannot read in full is not one the server's live-membership check backs.
    expect(component['voteLockReasonKey']()).toBe('usageStats.setView.lock.truncated');
    expect(captionKeys()).toContain('usageStats.setView.truncated');

    await openView({
      emoteSetId: 'set-b',
      totals: [],
      members: memberList([member('7tv-a', 'Alpha')]),
    });
    // K5/T5.3 (spec 8.8): the run is set-aware and both confirmations name the set, so a plain
    // non-active view with a good member list no longer locks deleting; K6 (spec 9): voting there
    // creates a set-session over the shown set, so it is not locked either.
    expect(component['deleteLockReasonKey']()).toBeNull();
    expect(component['voteLockReasonKey']()).toBeNull();

    await openView({ totals: [emote('a', 'Alpha')] });
    expect(component['deleteLockReasonKey']()).toBeNull();
  });

  it("offers the 'set-observed' range of the chosen set only, and follows a set switch while that preset is on (8.5)", async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [],
      members: memberList([]),
      observations: [],
    });
    expect(component['setObservedPresetRange']()).toBeNull();

    component['onEmoteSetSelected']('set-a');
    await settle();
    expect(component['setObservedPresetRange']()).toMatchObject({ from: '2026-01-01' });

    component['rangePreset'].set('set-observed');
    component['onEmoteSetSelected']('set-b');
    await settle();
    // Halloween was never observed: the dates stay, the preset turns into what they now are.
    expect(component['rangePreset']()).toBe('custom');
  });

  // --- K4 fix round (2026-09-22) ---------------------------------------------------------------

  const TOTALS_URL = '/api/channels/a/usage-stats/totals';

  function failTotals(status = 500): void {
    httpMock
      .match((r) => r.url === TOTALS_URL)
      .forEach((request) => request.flush({}, { status, statusText: 'Error' }));
  }

  it('locks delete and vote with the switch reason while the chosen set is not on screen yet, with the dock still up (finding A)', async () => {
    await openView({ totals: [emote('a', 'PeepoA')] });
    component['selection'].onRowClick(component['emotes']()[0], {
      shiftKey: false,
    } as MouseEvent);
    expect(component['deleteLockReasonKey']()).toBeNull();
    expect(component['voteLocked']()).toBe(false);

    component['onEmoteSetSelected']('set-b');
    await settle();

    // The dock stays mounted across the switch — which is exactly why it has to be locked.
    expect(component['dockVisible']()).toBe(true);
    expect(component['viewSwitching']()).toBe(true);
    expect(component['deleteLockReasonKey']()).toBe('usageStats.setView.lock.switching');
    expect(component['voteLocked']()).toBe(true);
    expect(component['voteLockReasonKey']()).toBe('usageStats.setView.lock.switching');

    flushByPath(httpMock, TOTALS_URL, [emote('a', 'PeepoA')]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    liveListRequests().forEach((request) => request.flush(memberList([member('7tv-a', 'PeepoA')])));
    await settle();

    // Landed: the member list loaded clean, so deleting (K5/T5.3, spec 8.8) and voting (K6, spec 9)
    // are both unlocked.
    expect(component['viewSwitching']()).toBe(false);
    expect(component['deleteLockReasonKey']()).toBeNull();
    expect(component['voteLockReasonKey']()).toBeNull();
  });

  it('hands the vote dialog a live lock, so a switch started behind the open dialog blocks its submit (finding A)', async () => {
    await openView({ totals: [emote('a', 'PeepoA')] });
    component['selection'].onRowClick(component['emotes']()[0], {
      shiftKey: false,
    } as MouseEvent);
    const openSpy = vi.spyOn(TestBed.inject(Dialog), 'open').mockReturnValue({
      closed: new Subject(),
    } as unknown as ReturnType<Dialog['open']>);

    component['openCreateVoteSession']();
    const data = openSpy.mock.calls[0][1]?.data as CreateVoteSessionDialogData;
    expect(data.lockReasonKey?.()).toBeNull();

    component['onEmoteSetSelected']('set-b');
    await settle();

    expect(data.lockReasonKey?.()).toBe('usageStats.setView.lock.switching');
  });

  it('a failed /totals after a switch shows the error state — no endless skeleton, refresh free — and a retry recovers (finding B)', async () => {
    await openView({ totals: [emote('a', 'PeepoA')] });
    component['onEmoteSetSelected']('set-b');
    await settle();

    failTotals();
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    liveListRequests().forEach((request) =>
      request.flush(memberList([member('7tv-x', 'PumpkinX')])),
    );
    await settle();

    // The refresh button is disabled by viewLoading() — it must be free again.
    expect(component['viewLoading']()).toBe(false);
    expect(component['setSwitchFailed']()).toBe(true);
    expect(component['errorMessage']()).not.toBeNull();
    // The previous set's rows are still loaded, but nothing may present them as the chosen set's.
    expect(component['inspected']()).toBeNull();
    expect(component['slotBudget']()).toBeNull();
    expect(component['deleteLockReasonKey']()).toBe('usageStats.setView.lock.switching');

    component['refresh']();
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    httpMock
      .expectOne((r) => r.url === TOTALS_URL && r.params.get('emoteSetId') === 'set-b')
      .flush([]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    liveListRequests().forEach((request) =>
      request.flush(memberList([member('7tv-x', 'PumpkinX')])),
    );
    await settle();

    expect(component['setSwitchFailed']()).toBe(false);
    expect(component['errorMessage']()).toBeNull();
    expect(component['isNonActiveView']()).toBe(true);
  });

  it('a failed /totals on the way back to the active set settles into the error state too (finding B, idle member list)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA')]),
    });

    component['onEmoteSetSelected']('set-a');
    await settle();
    failTotals();
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    await settle();

    expect(component['viewLoading']()).toBe(false);
    expect(component['setSwitchFailed']()).toBe(true);
  });

  it('a failed background reload of an already read set list keeps the view — no pin, no fallback, no reload, no prune (finding C)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA'), member('7tv-x', 'PumpkinX')]),
    });
    const pumpkin = component['emotes']().find((row) => row.sevenTvEmoteId === '7tv-x')!;
    component['selection'].onRowClick(pumpkin, { shiftKey: false } as MouseEvent);

    // The loud channel.synced reload of the list, failing this time.
    component['emoteSetListResource'].reload();
    await settle();
    httpMock
      .expectOne('/api/channels/a/emote-sets')
      .flush(
        { errorCode: 'foreign_channel_seventv_unavailable' },
        { status: 503, statusText: 'Service Unavailable' },
      );
    await settle();

    expect(component['isPinnedToActiveSet']()).toBe(false);
    expect(component['emoteSetListUnavailable']()).toBe(false);
    expect(component['emoteSetList']()?.sets.map((set) => set.id)).toEqual(['set-a', 'set-b']);
    expect(component['selectedEmoteSetId']()).toBe('set-b');
    expect(router.routerState.snapshot.root.queryParamMap.get('emoteSetId')).toBe('set-b');
    httpMock.expectNone((r) => r.url === TOTALS_URL);
    expect(component['selection'].selectedKeys()).toEqual(['7tv-x']);
    expect(component['selectionPrunedFeedback']()).toBeNull();
  });

  it('a failed status request un-claims the channel: the push/import scope locks and no active set is assumed (finding E)', async () => {
    await openView({ totals: [emote('a', 'PeepoA')] });
    expect(component['importScopeCurrent']()).toBe(true);

    component['refresh']();
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush({}, { status: 429, statusText: 'Too Many Requests' });
    await settle();

    expect(component['activeEmoteSetId']()).toBeNull();
    expect(component['importScopeCurrent']()).toBe(false);
  });

  it("a channel switch never asks the new channel for the previous channel's set (finding F)", async () => {
    await openView({ totals: [emote('a', 'PeepoA')] });
    // A chosen range needs no tracking start, so the new channel's rows are requested at once —
    // the window in which the previous channel's active id used to leak into the request.
    component['rangePreset'].set('custom');

    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();

    const totalsForB = httpMock.match((r) => r.url === '/api/channels/b/usage-stats/totals');
    expect(totalsForB.length).toBeGreaterThan(0);
    for (const request of totalsForB) {
      expect(request.request.params.get('emoteSetId')).toBeNull();
    }
  });

  it('a live reload requests no rows while a URL-carried set is still unconfirmed (finding F)', async () => {
    configure();
    router = TestBed.inject(Router);
    await router.navigate([], { queryParams: { emoteSetId: 'set-b' } });
    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
    await settle();
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();
    fixture.detectChanges();
    expect(component['awaitingEmoteSetId']()).toBe(true);

    vi.useFakeTimers();
    FakeEventSource.instances[0].emit({ type: LIVE_EVENT_TYPES.usageFlushed, channel: 'a' });
    vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
    fixture.detectChanges();

    httpMock.expectNone((r) => r.url === TOTALS_URL);
  });

  it('when the viewed set becomes the active one, delete stays locked until its rows are reloaded as the active view (finding H)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA')]),
    });

    // A sync made Halloween the channel's active set.
    component['setStatus'].set(
      setStatus({ activeEmoteSetId: 'set-b', trackedSince: '2026-01-01T00:00:00Z' }),
    );
    fixture.detectChanges();

    // Still the rows merged as a non-active view — no flash of an unlocked delete over them.
    expect(component['isNonActiveView']()).toBe(true);
    expect(component['deleteLockReasonKey']()).toBe('usageStats.setView.lock.switching');

    httpMock
      .expectOne((r) => r.url === TOTALS_URL && r.params.get('emoteSetId') === 'set-b')
      .flush([emote('a', 'PeepoA')]);
    await settle();

    expect(component['isNonActiveView']()).toBe(false);
    expect(component['deleteLockReasonKey']()).toBeNull();
  });

  it('the refresh button reloads the member list bypassing the Api cache (finding I)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA')]),
    });

    component['refresh']();
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    await settle();

    const reloaded = liveListRequests();
    expect(reloaded).toHaveLength(1);
    expect(reloaded[0].request.params.get('refresh')).toBe('true');
  });

  it('a set switch reconciles the selection only once the member list is in: a marked live member survives, a vanished one is pruned (#94 deferred)', async () => {
    await openView({ totals: [emote('a', 'PeepoA'), emote('b', 'PeepoB')] });
    for (const row of component['emotes']()) {
      component['selection'].onRowClick(row, { shiftKey: false } as MouseEvent);
    }
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-b']);

    component['onEmoteSetSelected']('set-b');
    await settle();
    // Neither emote has counts under Halloween — against the /totals rows alone both would go.
    flushByPath(httpMock, TOTALS_URL, []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    fixture.detectChanges();

    expect(component['selectionReconcilePending']()).toBe(true);
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-b']);
    expect(component['selectionPrunedFeedback']()).toBeNull();

    liveListRequests().forEach((request) => request.flush(memberList([member('7tv-a', 'PeepoA')])));
    await settle();

    expect(component['selectionReconcilePending']()).toBe(false);
    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
    expect(component['selectionPrunedFeedback']()?.count).toBe(1);
  });

  it('rows answered through the fallback while no active set was known keep that unknown identity: writes stay locked until rows for the recovered set land (second review, P1)', async () => {
    configure();
    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
    await settle();
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush({}, { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();
    httpMock
      .expectOne('/api/channels/a/emote-sets')
      .flush(
        emoteSetList([
          emoteSet({ id: 'set-a', isActive: true }),
          emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
        ]),
      );
    await settle();
    // No set known: /totals goes out without one and the endpoint answers for whatever is active.
    const fallback = httpMock.match((r) => r.url === TOTALS_URL);
    expect(fallback.map((r) => r.request.params.get('emoteSetId'))).toEqual([null]);
    fallback[0].flush([emote('a', 'PeepoA')]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    await settle();

    // The status recovers — and names set-b: 7TV switched sets in between.
    component['refreshSetStatus']();
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-b', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();

    // The fallback rows are not relabelled as set-b's: every write path stays locked.
    expect(component['selectedEmoteSetId']()).toBe('set-b');
    expect(component['viewSwitching']()).toBe(true);
    expect(component['deleteLockReasonKey']()).toBe('usageStats.setView.lock.switching');
    expect(component['importScopeCurrent']()).toBe(false);

    // …and stay locked when the explicit request for set-b fails.
    failTotals();
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    await settle();
    expect(component['setSwitchFailed']()).toBe(true);
    expect(component['deleteLockReasonKey']()).toBe('usageStats.setView.lock.switching');
  });

  it('a loud reload defers the reconciliation until the refreshed member list is in, even while the old one stays on screen (second review, P2)', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA'), member('7tv-x', 'PumpkinX')]),
    });
    const pumpkin = component['emotes']().find((row) => row.sevenTvEmoteId === '7tv-x')!;
    component['selection'].onRowClick(pumpkin, { shiftKey: false } as MouseEvent);

    component['refresh']();
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    // The refreshed numbers land first; the refreshed member list is still out, the old one still
    // renders (no skeleton) — but it must not be what the selection is reconciled against.
    flushByPath(httpMock, TOTALS_URL, [emote('a', 'PeepoA')]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    fixture.detectChanges();
    expect(component['liveMembersState']()).toBe('ready');
    expect(component['selectionReconcilePending']()).toBe(true);

    // PumpkinX left the set on 7TV.
    liveListRequests().forEach((request) => request.flush(memberList([member('7tv-a', 'PeepoA')])));
    await settle();

    expect(component['selectionReconcilePending']()).toBe(false);
    expect(component['selection'].selectedKeys()).toEqual([]);
    expect(component['selectionPrunedFeedback']()?.count).toBe(1);
  });

  it('a failed member list pays the deferred reconciliation against the counted rows alone (#94 deferred)', async () => {
    await openView({ totals: [emote('a', 'PeepoA'), emote('b', 'PeepoB')] });
    for (const row of component['emotes']()) {
      component['selection'].onRowClick(row, { shiftKey: false } as MouseEvent);
    }

    component['onEmoteSetSelected']('set-b');
    await settle();
    flushByPath(httpMock, TOTALS_URL, [emote('a', 'PeepoA')]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    fixture.detectChanges();
    expect(component['selectionReconcilePending']()).toBe(true);

    liveListRequests().forEach((request) =>
      request.flush(
        { errorCode: 'foreign_channel_seventv_unavailable' },
        { status: 503, statusText: 'Service Unavailable' },
      ),
    );
    await settle();

    expect(component['selectionReconcilePending']()).toBe(false);
    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
    expect(component['selectionPrunedFeedback']()?.count).toBe(1);
  });

  // --- Client-side member-list cache (operator decision 2026-09-22) -----------------------------
  // A fast A→B→A switch hit 429 on the shared ForeignEmoteLookup limiter even though the backend's
  // own 60 s cache sat behind it — a cache hit there still spends a permit. liveMembersResource now
  // reads through SevenTvEmoteSetService.loadCachedEmoteSetPreview, which mirrors that TTL
  // client-side for a params-driven load only; see that method's own spec for the cache's rules in
  // isolation (fresh hit, expiry, refresh bypass, no caching of an error, separate keys).

  it('serves a set switch back to a recently-shown set from the client cache — A, B, A issues exactly two live-list requests', async () => {
    configure();
    router = TestBed.inject(Router);
    await router.navigate([], { queryParams: { emoteSetId: 'set-b' } });
    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
    await settle();
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();
    fixture.detectChanges();
    httpMock
      .expectOne('/api/channels/a/emote-sets')
      .flush(
        emoteSetList([
          emoteSet({ id: 'set-a', isActive: true }),
          emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
          emoteSet({ id: 'set-c', name: 'Winter', isActive: false }),
        ]),
      );
    await settle();

    // Initial load of B (set-b) — a real request.
    flushByPath(httpMock, TOTALS_URL, [emote('a', 'PeepoA')]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    let requests = liveListRequests();
    expect(requests).toHaveLength(1);
    expect(requests[0].request.url).toBe(liveListUrl('set-b'));
    requests[0].flush(memberList([member('7tv-a', 'PeepoA')], { emoteSetId: 'set-b' }));
    await settle();

    // Switch to a different, never-loaded non-active set (set-c) — also a real request.
    component['onEmoteSetSelected']('set-c');
    await settle();
    flushByPath(httpMock, TOTALS_URL, []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    requests = liveListRequests();
    expect(requests).toHaveLength(1);
    expect(requests[0].request.url).toBe(liveListUrl('set-c'));
    requests[0].flush(memberList([member('7tv-z', 'Zulu')], { emoteSetId: 'set-c' }));
    await settle();

    // Back to set-b, within the 60 s TTL: no live-list request at all — served from the cache.
    component['onEmoteSetSelected']('set-b');
    await settle();
    flushByPath(httpMock, TOTALS_URL, [emote('a', 'PeepoA')]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    await settle();

    expect(liveListRequests()).toHaveLength(0);
    expect(component['liveMembersState']()).toBe('ready');
    expect(component['emotes']().map((row) => row.sevenTvEmoteId)).toEqual(['7tv-a']);
  });

  it('a loud channel.synced reload still bypasses a warm client cache and fetches fresh', async () => {
    await openView({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA')]),
    });
    // The cache is now warm for (a, set-b) from openView's own initial load.
    vi.useFakeTimers();
    const source = FakeEventSource.instances[0];

    source.emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: 'a' });
    vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    const reloaded = liveListRequests();
    expect(reloaded).toHaveLength(1);
    expect(reloaded[0].request.url).toBe(liveListUrl('set-b'));
    expect(reloaded[0].request.params.get('refresh')).toBe('true');
  });

  // #220 T4: a deep link must cost one request per set view, not one per recomputed `params`.
  // Every request to the tracked route is counted, cancelled ones included — a cancelled request
  // has still passed the Api's limiter, so it is a spent permit.
  describe('deep link: the member list is requested once (#220)', () => {
    const STATUS_URL = '/api/channels/a/emotes/active-set';
    let seen: TestRequest[];

    function collectLiveRequests(): void {
      seen.push(...liveListRequests());
    }

    async function mountDeepLink(
      emoteSetId: string,
      order: 'list-first' | 'status-first' | 'status-fails',
    ): Promise<void> {
      seen = [];
      configure();
      router = TestBed.inject(Router);
      await router.navigate([], { queryParams: { emoteSetId } });
      fixture = TestBed.createComponent(UsageStatsPage);
      component = fixture.componentInstance;
      httpMock = TestBed.inject(HttpTestingController);
      fixture.componentRef.setInput('channelName', 'a');
      fixture.detectChanges();
      await settle();
      httpMock
        .expectOne('/api/channels/a/permissions')
        .flush({ canManage: true, canViewUsageStats: true });
      await settle();
      collectLiveRequests();

      const flushList = async (): Promise<void> => {
        httpMock
          .expectOne('/api/channels/a/emote-sets')
          .flush(
            emoteSetList([
              emoteSet({ id: 'set-a', isActive: true }),
              emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
            ]),
          );
        await settle();
        collectLiveRequests();
      };
      const flushStatus = async (): Promise<void> => {
        const request = httpMock.expectOne(STATUS_URL);
        if (order === 'status-fails') {
          request.flush({}, { status: 500, statusText: 'Server Error' });
        } else {
          request.flush(
            setStatus({
              activeEmoteSetId: 'set-a',
              capacity: 600,
              occupiedSlots: 10,
              trackedSince: '2026-01-01T00:00:00Z',
            }),
          );
        }
        fixture.detectChanges();
        await settle();
        collectLiveRequests();
      };

      if (order === 'status-first') {
        await flushStatus();
        await flushList();
      } else {
        await flushList();
        await flushStatus();
      }
    }

    function requestsFor(setId: string): TestRequest[] {
      return seen.filter((r) => r.request.url === liveListUrl(setId));
    }

    it('(a) a deep link to a non-active set asks once when the set list lands before the status', async () => {
      await mountDeepLink('set-b', 'list-first');

      expect(seen).toHaveLength(1);
      expect(requestsFor('set-b')).toHaveLength(1);
      expect(requestsFor('set-b')[0].cancelled).toBe(false);
    });

    it('(b) a failed set status still lets the deep-linked set load, once', async () => {
      await mountDeepLink('set-b', 'status-fails');

      expect(seen).toHaveLength(1);
      expect(requestsFor('set-b')).toHaveLength(1);
      expect(requestsFor('set-b')[0].cancelled).toBe(false);
    });

    it('(c) a deep link to the active set never asks the tracked route, in either order', async () => {
      await mountDeepLink('set-a', 'list-first');
      expect(seen).toHaveLength(0);

      await mountDeepLink('set-a', 'status-first');
      expect(seen).toHaveLength(0);
    });

    it('control: status before list asks once for the deep-linked set', async () => {
      await mountDeepLink('set-b', 'status-first');

      expect(seen).toHaveLength(1);
      expect(requestsFor('set-b')[0].cancelled).toBe(false);
    });

    it("reports 'loading', not 'unavailable', while the set status is still pending", async () => {
      seen = [];
      configure();
      router = TestBed.inject(Router);
      await router.navigate([], { queryParams: { emoteSetId: 'set-b' } });
      fixture = TestBed.createComponent(UsageStatsPage);
      component = fixture.componentInstance;
      httpMock = TestBed.inject(HttpTestingController);
      fixture.componentRef.setInput('channelName', 'a');
      fixture.detectChanges();
      await settle();
      httpMock
        .expectOne('/api/channels/a/permissions')
        .flush({ canManage: true, canViewUsageStats: true });
      await settle();
      httpMock
        .expectOne('/api/channels/a/emote-sets')
        .flush(
          emoteSetList([
            emoteSet({ id: 'set-a', isActive: true }),
            emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
          ]),
        );
      await settle();

      // The status request is still open: the gate is closed and nothing has been asked.
      expect(liveListRequests()).toHaveLength(0);
      expect(component['liveMembersState']()).toBe('loading');
    });
  });

  // Review follow-up to the gate above: a status answer belongs to the channel that asked. Without
  // that, a slow answer for X landing after the switch to Y re-claims `setStatusChannel` for X and
  // closes Y's gate for good (or, failing, wipes Y's status).
  describe('the status gate survives an in-route channel switch (#220 review)', () => {
    const LIVE_B = /^\/api\/channels\/b\/emote-sets\/[^/]+\/emotes$/;

    function liveRequestsForB(): TestRequest[] {
      return httpMock.match((r) => LIVE_B.test(r.url));
    }

    function statusFor(channel: string): TestRequest {
      return httpMock.expectOne(`/api/channels/${channel}/emotes/active-set`);
    }

    function activeStatus(): EmoteSetStatus {
      return setStatus({
        activeEmoteSetId: 'set-a',
        capacity: 600,
        occupiedSlots: 10,
        trackedSince: '2026-01-01T00:00:00Z',
      });
    }

    async function flushSetList(channel: string): Promise<void> {
      httpMock
        .expectOne(`/api/channels/${channel}/emote-sets`)
        .flush(
          emoteSetList([
            emoteSet({ id: 'set-a', isActive: true }),
            emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
          ]),
        );
      await settle();
    }

    async function mountOn(channel: string, emoteSetId?: string): Promise<void> {
      configure();
      router = TestBed.inject(Router);
      if (emoteSetId) {
        await router.navigate([], { queryParams: { emoteSetId } });
      }
      fixture = TestBed.createComponent(UsageStatsPage);
      component = fixture.componentInstance;
      httpMock = TestBed.inject(HttpTestingController);
      fixture.componentRef.setInput('channelName', channel);
      fixture.detectChanges();
      await settle();
      httpMock
        .expectOne(`/api/channels/${channel}/permissions`)
        .flush({ canManage: true, canViewUsageStats: true });
      await settle();
    }

    async function switchTo(channel: string): Promise<void> {
      fixture.componentRef.setInput('channelName', channel);
      fixture.detectChanges();
      await settle();
      httpMock
        .expectOne(`/api/channels/${channel}/permissions`)
        .flush({ canManage: true, canViewUsageStats: true });
      await settle();
    }

    async function flushStatus(request: TestRequest, ok: boolean): Promise<void> {
      if (ok) {
        request.flush(activeStatus());
      } else {
        request.flush({}, { status: 500, statusText: 'Server Error' });
      }
      fixture.detectChanges();
      await settle();
    }

    it("a late answer for the channel left behind does not close the new channel's gate", async () => {
      await mountOn('a');
      const late = statusFor('a');
      await switchTo('b');
      await flushStatus(statusFor('b'), true);

      await flushStatus(late, true);

      expect(component['setStatusChannel']()).toBe('b');
      await flushSetList('b');
      component['onEmoteSetSelected']('set-b');
      await settle();
      expect(liveRequestsForB()).toHaveLength(1);
      expect(component['setStatusOutcomeKnown']()).toBe(true);
    });

    it("a late failure for the channel left behind leaves the new channel's status alone", async () => {
      await mountOn('a');
      const late = statusFor('a');
      await switchTo('b');
      await flushStatus(statusFor('b'), true);

      await flushStatus(late, false);

      expect(component['setStatus']()?.activeEmoteSetId).toBe('set-a');
      expect(component['setStatusChannel']()).toBe('b');
      // set-a is b's active set: it must not be mistaken for a foreign one and asked for.
      component['onEmoteSetSelected']('set-a');
      await settle();
      expect(liveRequestsForB()).toHaveLength(0);
    });

    it('a channel switch with a set chosen reports loading, then asks exactly once after the status', async () => {
      await mountOn('a', 'set-b');
      await flushSetList('a');
      await flushStatus(statusFor('a'), true);
      liveListRequests().forEach((r) => r.flush(memberList([member('7tv-a', 'PeepoA')])));

      await switchTo('b');
      await flushSetList('b');
      expect(component['liveMembersState']()).toBe('loading');
      expect(liveRequestsForB()).toHaveLength(0);

      await flushStatus(statusFor('b'), true);

      const asked = liveRequestsForB();
      expect(asked).toHaveLength(1);
      expect(asked[0].request.params.get('refresh')).toBeNull();
    });
  });

  describe('a reload while the status gate is closed (#220 review)', () => {
    async function flushSetList(channel: string): Promise<void> {
      httpMock
        .expectOne(`/api/channels/${channel}/emote-sets`)
        .flush(
          emoteSetList([
            emoteSet({ id: 'set-a', isActive: true }),
            emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
          ]),
        );
      await settle();
    }

    async function mountDeepLinkWithPendingStatus(): Promise<TestRequest> {
      configure();
      router = TestBed.inject(Router);
      await router.navigate([], { queryParams: { emoteSetId: 'set-b' } });
      fixture = TestBed.createComponent(UsageStatsPage);
      component = fixture.componentInstance;
      httpMock = TestBed.inject(HttpTestingController);
      fixture.componentRef.setInput('channelName', 'a');
      fixture.detectChanges();
      await settle();
      httpMock
        .expectOne('/api/channels/a/permissions')
        .flush({ canManage: true, canViewUsageStats: true });
      await settle();
      return httpMock.expectOne('/api/channels/a/emotes/active-set');
    }

    it('asks nothing while closed and exactly one ordinary request once it opens', async () => {
      const first = await mountDeepLinkWithPendingStatus();
      await flushSetList('a');

      component['refresh']();
      await settle();
      expect(liveListRequests()).toHaveLength(0);

      // The refresh's own status request answers first and opens the gate.
      const second = httpMock.expectOne('/api/channels/a/emotes/active-set');
      second.flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
      fixture.detectChanges();
      await settle();
      const asked = liveListRequests();
      expect(asked).toHaveLength(1);
      expect(asked[0].request.params.get('refresh')).toBeNull();

      // The original status arriving afterwards changes nothing about the params.
      first.flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
      fixture.detectChanges();
      await settle();
      expect(liveListRequests()).toHaveLength(0);
    });

    it('once open, a refresh asks exactly once with refresh=true despite equal params', async () => {
      const first = await mountDeepLinkWithPendingStatus();
      await flushSetList('a');
      first.flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
      fixture.detectChanges();
      await settle();
      liveListRequests().forEach((r) => r.flush(memberList([member('7tv-a', 'PeepoA')])));
      await settle();

      component['refresh']();
      await settle();
      httpMock
        .expectOne('/api/channels/a/emotes/active-set')
        .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
      fixture.detectChanges();
      await settle();

      const asked = liveListRequests();
      expect(asked).toHaveLength(1);
      expect(asked[0].request.params.get('refresh')).toBe('true');
    });
  });
});

/**
 * The vote button's own `aria-describedby` (a11y fix, docs/UI-Designsprache.md §10 "Disabled
 * explains itself"): before this fix only the delete button pointed at the mass-delete panel's
 * visible reason paragraph, leaving the vote button's disabled state unexplained to a screen
 * reader. Unlike every block above, this one keeps the real template rather than overriding it to
 * two bare `<div>`s — the vote button and the paragraph it must reference (`MassDeletePanel`'s
 * `deleteLockReasonId`, made public for exactly this) only exist there. The selection is still
 * driven through `ListSelection` directly rather than a DOM click on a grid cell, same as the
 * `openCreateVoteSession()` block above — `cdk-virtual-scroll-viewport` renders nothing meaningful
 * in jsdom's zero-size layout, but the dock and its buttons sit outside the viewport and only need
 * the selection *signal* to be non-empty, not a rendered cell to click.
 */
describe('UsageStatsPage — the locked vote button shares the delete lock reason paragraph (a11y fix)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;
  let router: Router;

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
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  function findVoteButton(): HTMLButtonElement | undefined {
    return Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((button) => button.textContent?.trim().startsWith('usageStats.createVoteSession'));
  }

  it('locks the vote button in a non-active view whose member list is unreadable, pointing at the same reason paragraph as delete (spec §36, K6)', async () => {
    router = TestBed.inject(Router);
    await router.navigate([], { queryParams: { emoteSetId: 'set-b' } });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();
    fixture.detectChanges();
    httpMock.expectOne('/api/channels/a/emote-sets').flush(
      emoteSetList([
        emoteSet({
          id: 'set-a',
          isActive: true,
          observations: [{ fromUtc: '2026-01-01T00:00:00Z', toUtc: null }],
        }),
        emoteSet({ id: 'set-b', name: 'Halloween', isActive: false, observations: [] }),
      ]),
    );
    await settle();

    // A real-looking imageUrl: this describe block renders the actual template, so
    // NgOptimizedImage runs for real and rejects the shared emote() helper's default '' (NG02952,
    // see the "toolbar mark-all" block's own comment on the same trap).
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [
      { ...emote('a', 'Alpha', 12), imageUrl: 'https://cdn.7tv.app/emote/x/1x.webp' },
    ]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
    httpMock
      .match((request) => LIVE_LIST_URL.test(request.url))
      .forEach((request) =>
        request.flush(
          { errorCode: 'foreign_channel_seventv_unavailable' },
          { status: 503, statusText: 'Service Unavailable' },
        ),
      );
    await settle();

    // Deleting and voting share one lock since K6 (`sharedSetViewLockReasonKey`): the set-session
    // create validates its ballot against the live 7TV membership server-side, and a member list
    // this page cannot read cannot back a ballot that check would accept (no row is 'live' here).
    expect(component['deleteLockReasonKey']()).toBe('usageStats.setView.lock.membersUnavailable');
    expect(component['voteLockReasonKey']()).toBe('usageStats.setView.lock.membersUnavailable');
    expect(component['voteLocked']()).toBe(true);

    // Nothing marked yet — the dock (panel and both buttons) does not exist until something is.
    expect(findVoteButton()).toBeUndefined();

    const [row] = component['emotes']();
    component['selection'].onRowClick(row, { shiftKey: false } as MouseEvent);
    fixture.detectChanges();

    const button = findVoteButton();
    expect(button).toBeDefined();
    expect(button!.disabled).toBe(true);

    const reasonParagraph = fixture.nativeElement.querySelector(
      'p[id^="mass-delete-lock-reason-"]',
    ) as HTMLParagraphElement | null;
    expect(reasonParagraph).not.toBeNull();
    expect(reasonParagraph!.textContent?.trim()).toBe('usageStats.setView.lock.membersUnavailable');
    // The locked vote button explains itself through the delete button's own paragraph.
    expect(button!.getAttribute('aria-describedby')).toBe(reasonParagraph!.id);
  });

  it('states the set-view facts and member-list warnings even when the set status (and its tracking start) could not be read (second review, P2)', async () => {
    router = TestBed.inject(Router);
    await router.navigate([], { queryParams: { emoteSetId: 'set-b' } });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush({}, { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();
    httpMock
      .expectOne('/api/channels/a/emote-sets')
      .flush(
        emoteSetList([
          emoteSet({ id: 'set-a', isActive: true }),
          emoteSet({ id: 'set-b', name: 'Halloween', isActive: false, observations: [] }),
        ]),
      );
    await settle();
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [
      { ...emote('a', 'Alpha', 12), imageUrl: 'https://cdn.7tv.app/emote/x/1x.webp' },
    ]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
    httpMock
      .match((request) => LIVE_LIST_URL.test(request.url))
      .forEach((request) =>
        request.flush(
          { errorCode: 'foreign_channel_seventv_unavailable' },
          { status: 503, statusText: 'Service Unavailable' },
        ),
      );
    await settle();

    expect(component['trackedSince']()).toBeNull();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('usageStats.setView.membersUnavailable');
    expect(text).toContain('usageStats.setView.facts.notObserved');
  });

  it('leaves the vote button without an aria-describedby in the active view, where nothing is locked', async () => {
    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [
      { ...emote('a', 'Alpha', 12), imageUrl: 'https://cdn.7tv.app/emote/x/1x.webp' },
    ]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
    // Genuine microtask tick, not just detectChanges(): canManage() reads permissionsResource, an
    // rxResource whose value a bare synchronous detectChanges() right after flush() does not yet
    // reflect (see this file's own header comment on rxResource vs. plain HttpClient flushes) — the
    // vote button is entirely gated on it (`@if (canManage())`), unlike the mark-all button the
    // "toolbar mark-all" block above checks, which does not depend on it.
    await settle();

    expect(component['voteLocked']()).toBe(false);
    expect(component['canManage']()).toBe(true);

    const [row] = component['emotes']();
    component['selection'].onRowClick(row, { shiftKey: false } as MouseEvent);
    fixture.detectChanges();

    const button = findVoteButton();
    expect(button).toBeDefined();
    expect(button!.disabled).toBe(false);
    expect(button!.getAttribute('aria-describedby')).toBeNull();
  });

  it('locks the vote button with the switch reason during a mid-switch, pointing at the same paragraph as delete (a11y, T6.3 fix round 1)', async () => {
    // The positive case this describe block's own title promises (since K6 the vote lock IS
    // `sharedSetViewLockReasonKey`, so every vote lock shares the delete button's paragraph):
    // this asserts the DOM wiring actually holds for it, not just the underlying signals (the
    // "K4 fix round" describe block checks those, never the rendered button/paragraph pair).
    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();
    fixture.detectChanges();
    // onEmoteSetSelected resolves its id against this list (usage-stats-page.ts's own
    // `selectedEmoteSetId` comment) — without it flushed, the switch below never registers and
    // viewSwitching() stays false, which is what silently broke this exact assertion.
    httpMock.expectOne('/api/channels/a/emote-sets').flush(
      emoteSetList([
        emoteSet({
          id: 'set-a',
          isActive: true,
          observations: [{ fromUtc: '2026-01-01T00:00:00Z', toUtc: null }],
        }),
        emoteSet({ id: 'set-b', name: 'Halloween', isActive: false, observations: [] }),
      ]),
    );
    await settle();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [
      { ...emote('a', 'Alpha', 12), imageUrl: 'https://cdn.7tv.app/emote/x/1x.webp' },
    ]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
    await settle();

    const [row] = component['emotes']();
    component['selection'].onRowClick(row, { shiftKey: false } as MouseEvent);
    fixture.detectChanges();

    // A dropdown choice lands behind the marked selection — the view has not caught up yet.
    component['onEmoteSetSelected']('set-b');
    await settle();

    expect(component['viewSwitching']()).toBe(true);
    expect(component['voteLocked']()).toBe(true);

    const button = findVoteButton();
    expect(button).toBeDefined();
    expect(button!.disabled).toBe(true);

    const reasonParagraph = fixture.nativeElement.querySelector(
      'p[id^="mass-delete-lock-reason-"]',
    ) as HTMLParagraphElement | null;
    expect(reasonParagraph).not.toBeNull();
    expect(reasonParagraph!.textContent?.trim()).toBe('usageStats.setView.lock.switching');
    // The positive a11y assertion: the locked button still points at the SAME paragraph the
    // delete button uses, in the one case that still locks both together.
    expect(button!.getAttribute('aria-describedby')).toBe(reasonParagraph!.id);
  });
});

/**
 * T4.5, AK 64 (second part): the export dialog and the push/import-target dialog each read the
 * *shown* set once, at the moment they open — the same capture discipline `CapturedExportScope`/
 * `CapturedImportScope`'s own docs describe, already exercised for a range change by the openExport()
 * block above (#143 P2) but not yet for a *set* switch, which is what T4.5 adds. A set switch while
 * either dialog is still on screen must not retarget what it already captured — the header's own
 * dropdown keeps working underneath it, and only the *next* open sees the new set.
 */
describe('UsageStatsPage — export/import scope capture reads the shown set once, at open (#200, T4.5, AK 64)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;
  let router: Router;

  const SERIES = { from: '2026-01-01', to: '2026-09-08', liveDays: [], emotes: [] };

  function member(sevenTvEmoteId: string, name: string): ForeignEmoteRow {
    return {
      sevenTvEmoteId,
      name,
      defaultName: name,
      imageUrl: '',
      topAllTime: null,
      trending: null,
    };
  }

  function memberList(emotes: ForeignEmoteRow[]): ForeignEmoteSetResponse {
    return {
      channelName: 'a',
      sevenTvUserId: null,
      emoteSetId: 'set-b',
      emoteSetName: 'Halloween',
      capacity: 1000,
      totalCount: emotes.length,
      truncated: false,
      emotes,
    };
  }

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  /** Same shape as the T4.3/T4.4 block's own `openView` — mounts channel 'a' (active set `set-a`)
   *  on the Halloween set `set-b`, with a matching live member list, and settles every request the
   *  set-b view needs before the test drives a dialog open. */
  async function openHalloweenView(): Promise<void> {
    TestBed.resetTestingModule();
    stubNoImportCoverage();
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
    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });

    router = TestBed.inject(Router);
    await router.navigate([], { queryParams: { emoteSetId: 'set-b' } });
    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
    await settle();

    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();
    fixture.detectChanges();
    httpMock
      .expectOne('/api/channels/a/emote-sets')
      .flush(
        emoteSetList([
          emoteSet({ id: 'set-a', isActive: true }),
          emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
        ]),
      );
    await settle();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [emote('a', 'PumpkinA', 5)]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    httpMock
      .match((r) => LIVE_LIST_URL.test(r.url))
      .forEach((request) => request.flush(memberList([member('7tv-a', 'PumpkinA')])));
    await settle();
  }

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('captures the export scope once, at open — a set switch while the dialog is still open does not retarget the download (AK 64)', async () => {
    await openHalloweenView();
    const closedSubject = new Subject<
      { optionId: ExportPurposeId; scope: 'visible' } | undefined
    >();
    vi.spyOn(TestBed.inject(Dialog), 'open').mockReturnValue({
      closed: closedSubject,
    } as unknown as ReturnType<Dialog['open']>);
    const downloads = captureDownloads();

    component['openExport']();

    // The header dropdown keeps working underneath the still-open dialog (#94's own reasoning).
    component['onEmoteSetSelected']('set-a');
    await settle();

    closedSubject.next({ optionId: 'usage-csv', scope: 'visible' });

    expect(downloads).toHaveLength(1);
    // Names Halloween's set — the one the dialog was opened for — not the one the header now shows.
    expect(downloads[0].filename).toContain('set-b');
  });

  it('captures the push/import-target scope once, at open — a set switch while it is still open does not retarget the source (AK 64)', async () => {
    await openHalloweenView();
    const openSpy = vi.spyOn(TestBed.inject(Dialog), 'open').mockReturnValue({
      closed: new Subject<unknown>(),
    } as unknown as ReturnType<Dialog['open']>);

    component['openImportTarget']();

    expect(openSpy).toHaveBeenCalledTimes(1);
    const data = openSpy.mock.calls[0][1]?.data as { sourceEmoteSetId: string };
    expect(data.sourceEmoteSetId).toBe('set-b');

    component['onEmoteSetSelected']('set-a');
    await settle();

    // Still the set the dialog was opened for — the capture never re-reads a live signal.
    expect(data.sourceEmoteSetId).toBe('set-b');
  });
});

/**
 * `channel.synced` reads the set status before the rows, and a status that cannot be read locks
 * deleting and voting rather than passing silently (#200 review fix, O2). Before this, the sync's
 * silent `/totals` went out under the page's own, possibly outdated active id — rows for the old
 * set stamped as the active view, a selection pruned against them — and a failed status refetch
 * left that old id standing as "active" for the dock, unlocked. The lock keeps the last known set
 * (second review round): the channel stays claimed, so neither the dock nor a running delete's
 * panel is taken away. Same `FakeEventSource` pattern as the #112 block at the top of
 * this file, with fake timers throughout (the live reload is debounced) and the E19 block's
 * fake-timer `settle()` for the `rxResource`s.
 */
describe('UsageStatsPage — channel.synced reads the set status before the rows (#200 review, O2)', () => {
  const STATUS_URL = '/api/channels/a/emotes/active-set';
  const TOTALS_URL = '/api/channels/a/usage-stats/totals';
  const SERIES_URL = '/api/channels/a/usage-stats/series';
  const SERIES = { from: '2026-01-01', to: '2026-09-08', liveDays: [], emotes: [] };

  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;

  function member(sevenTvEmoteId: string, name: string): ForeignEmoteRow {
    return {
      sevenTvEmoteId,
      name,
      defaultName: name,
      imageUrl: '',
      topAllTime: null,
      trending: null,
    };
  }

  function memberList(emotes: ForeignEmoteRow[]): ForeignEmoteSetResponse {
    return {
      channelName: 'a',
      sevenTvUserId: null,
      emoteSetId: 'set-b',
      emoteSetName: 'Halloween',
      capacity: 1000,
      totalCount: emotes.length,
      truncated: false,
      emotes,
    };
  }

  /** Both "since" dates set, so `SetStatusFlushProbeGate` stays closed and a `usage.flushed` burst
   *  never asks for the status on its own — every status request below is one the test caused. */
  function statusFor(activeEmoteSetId: string): EmoteSetStatus {
    return setStatus({
      activeEmoteSetId,
      trackedSince: '2026-01-01T00:00:00Z',
      botsExcludedSince: '2026-01-02T00:00:00Z',
      sharedChatSeparatedSince: '2026-01-02T00:00:00Z',
    });
  }

  function setList(): EmoteSetListResponse {
    return emoteSetList([
      emoteSet({ id: 'set-a', isActive: true }),
      emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
    ]);
  }

  async function settle(): Promise<void> {
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
  }

  function totalsRequests(): TestRequest[] {
    return httpMock.match((r) => r.url === TOTALS_URL);
  }

  function flushOpenSetListReloads(): void {
    httpMock.match('/api/channels/a/emote-sets').forEach((request) => request.flush(setList()));
  }

  async function emit(type: string, channel = 'a'): Promise<void> {
    FakeEventSource.instances[FakeEventSource.instances.length - 1].emit({ type, channel });
    vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
    await settle();
  }

  const STATUS_URL_B = '/api/channels/b/emotes/active-set';
  const TOTALS_URL_B = '/api/channels/b/usage-stats/totals';

  /** Switches the page from 'a' to 'b' (active set `set-y`) and answers b's permissions and set
   *  list — b's own status request stays out for the test to drive. */
  async function switchToB(): Promise<void> {
    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();
    await settle();
    httpMock
      .expectOne('/api/channels/b/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    flushOpenSetListReloadsB();
    await settle();
  }

  function flushOpenSetListReloadsB(): void {
    httpMock
      .match('/api/channels/b/emote-sets')
      .forEach((request) =>
        request.flush(emoteSetList([emoteSet({ id: 'set-y', name: 'Main', isActive: true })])),
      );
  }

  function statusForB(): EmoteSetStatus {
    return setStatus({
      activeEmoteSetId: 'set-y',
      trackedSince: '2026-02-01T00:00:00Z',
      botsExcludedSince: '2026-02-02T00:00:00Z',
      sharedChatSeparatedSince: '2026-02-02T00:00:00Z',
    });
  }

  function totalsRequestsB(): TestRequest[] {
    return httpMock.match((r) => r.url === TOTALS_URL_B);
  }

  const UNAVAILABLE = 'usageStats.setView.lock.statusUnavailable';
  const FAILED = { status: 503, statusText: 'Service Unavailable' };

  /** Mounts channel 'a' (active set `set-a`) with the given set in the URL and drives it until its
   *  rows — and, for `set-b`, its member list — are on screen. */
  async function mount(options: {
    emoteSetId?: string;
    totals: EmoteUsageTotalDto[];
    members?: ForeignEmoteSetResponse;
    status?: EmoteSetStatus;
  }): Promise<void> {
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
    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });
    if (options.emoteSetId) {
      await TestBed.inject(Router).navigate([], {
        queryParams: { emoteSetId: options.emoteSetId },
      });
    }
    vi.useFakeTimers();

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
    await settle();

    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock.expectOne(STATUS_URL).flush(options.status ?? statusFor('set-a'));
    fixture.detectChanges();
    fixture.detectChanges();
    httpMock.expectOne('/api/channels/a/emote-sets').flush(setList());
    await settle();

    flushByPath(httpMock, TOTALS_URL, options.totals);
    flushByPath(httpMock, SERIES_URL, SERIES);
    if (options.members) {
      const members = options.members;
      httpMock.match((r) => LIVE_LIST_URL.test(r.url)).forEach((r) => r.flush(members));
    }
    await settle();
  }

  function mark(sevenTvEmoteId: string): void {
    const row = component['emotes']().find(
      (candidate) => candidate.sevenTvEmoteId === sevenTvEmoteId,
    )!;
    component['selection'].onRowClick(row, { shiftKey: false } as MouseEvent);
  }

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('(1) a sync that moves the active set asks for no rows before the status, then exactly once for the new set — the selection survives', async () => {
    await mount({ totals: [emote('a', 'PeepoA'), emote('c', 'PeepoC')] });
    mark('7tv-c');

    await emit(LIVE_EVENT_TYPES.channelSynced);
    expect(totalsRequests()).toHaveLength(0);
    flushOpenSetListReloads();

    httpMock.expectOne(STATUS_URL).flush(statusFor('set-b'));
    await settle();

    const asked = totalsRequests();
    expect(asked).toHaveLength(1);
    expect(asked[0].request.params.get('emoteSetId')).toBe('set-b');
    asked[0].flush([emote('a', 'PeepoA'), emote('c', 'PeepoC')]);
    flushByPath(httpMock, SERIES_URL, SERIES);
    await settle();

    expect(component['activeEmoteSetId']()).toBe('set-b');
    expect(component['selection'].selectedKeys()).toEqual(['7tv-c']);
    expect(component['isLoading']()).toBe(false);
  });

  it('(2) a sync that keeps the active set asks for the rows once, silently, after the status', async () => {
    await mount({ totals: [emote('a', 'PeepoA'), emote('c', 'PeepoC')] });
    mark('7tv-c');

    await emit(LIVE_EVENT_TYPES.channelSynced);
    expect(totalsRequests()).toHaveLength(0);
    flushOpenSetListReloads();

    httpMock.expectOne(STATUS_URL).flush(statusFor('set-a'));
    await settle();

    const asked = totalsRequests();
    expect(asked).toHaveLength(1);
    expect(asked[0].request.params.get('emoteSetId')).toBe('set-a');
    expect(component['isLoading']()).toBe(false);
    asked[0].flush([emote('a', 'PeepoA'), emote('c', 'PeepoC')]);
    await settle();

    expect(component['isLoading']()).toBe(false);
    expect(component['selection'].selectedKeys()).toEqual(['7tv-c']);
  });

  it('(3) a status that fails after a sync mid delete run keeps the set, its panel and the selection, and only locks', async () => {
    await mount({ totals: [emote('a', 'PeepoA')] });
    mark('7tv-a');
    TestBed.inject(SevenTvDeleteService).isRunning.set(true);

    await emit(LIVE_EVENT_TYPES.channelSynced);
    flushOpenSetListReloads();
    httpMock.expectOne(STATUS_URL).flush({}, FAILED);
    await settle();

    // Still claimed: the last known set stays the selected one, so the dock — and the
    // mass-delete panel it renders for the selected set (usage-stats-page.html) — stays mounted
    // under the running delete.
    expect(component['setStatusChannel']()).toBe('a');
    expect(component['selectedEmoteSetId']()).toBe('set-a');
    expect(component['dockVisible']()).toBe(true);
    expect(component['deleteLockReasonKey']()).toBe(UNAVAILABLE);
    expect(component['voteLockReasonKey']()).toBe(UNAVAILABLE);
    // No rows asked for, no skeleton, no selection change.
    expect(totalsRequests()).toHaveLength(0);
    expect(component['isLoading']()).toBe(false);
    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
  });

  it('(3b) a status that fails after a sync without a run does the same: set kept, lock on, nothing reloaded', async () => {
    await mount({ totals: [emote('a', 'PeepoA')] });
    mark('7tv-a');

    await emit(LIVE_EVENT_TYPES.channelSynced);
    flushOpenSetListReloads();
    httpMock.expectOne(STATUS_URL).flush({}, FAILED);
    await settle();

    expect(component['selectedEmoteSetId']()).toBe('set-a');
    expect(component['dockVisible']()).toBe(true);
    expect(component['deleteLockReasonKey']()).toBe(UNAVAILABLE);
    expect(totalsRequests()).toHaveLength(0);
    expect(component['isLoading']()).toBe(false);
    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
  });

  it('(3c) a failed flush-probe refresh only locks: the set stays, and the flush reloads its rows once, as always', async () => {
    // Without sharedChatSeparatedSince the probe gate lets a `usage.flushed` burst ask again.
    await mount({
      totals: [emote('a', 'PeepoA')],
      status: setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }),
    });
    mark('7tv-a');

    await emit(LIVE_EVENT_TYPES.usageFlushed);
    httpMock.expectOne(STATUS_URL).flush({}, FAILED);
    await settle();

    expect(component['setStatusChannel']()).toBe('a');
    expect(component['selectedEmoteSetId']()).toBe('set-a');
    expect(component['deleteLockReasonKey']()).toBe(UNAVAILABLE);
    // The flush's own silent reload, nothing on top of it.
    const asked = totalsRequests();
    expect(asked.map((r) => r.request.params.get('emoteSetId'))).toEqual(['set-a']);
    expect(component['isLoading']()).toBe(false);
    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
  });

  it('(4) the next successful status lifts the lock; the channel stayed claimed throughout and the selection is unchanged', async () => {
    await mount({ totals: [emote('a', 'PeepoA')] });
    mark('7tv-a');

    await emit(LIVE_EVENT_TYPES.channelSynced);
    flushOpenSetListReloads();
    httpMock.expectOne(STATUS_URL).flush({}, FAILED);
    await settle();
    expect(component['deleteLockReasonKey']()).toBe(UNAVAILABLE);
    expect(component['setStatusChannel']()).toBe('a');
    expect(totalsRequests()).toHaveLength(0);

    await emit(LIVE_EVENT_TYPES.channelSynced);
    flushOpenSetListReloads();
    httpMock.expectOne(STATUS_URL).flush(statusFor('set-b'));
    await settle();
    flushByPath(httpMock, TOTALS_URL, [emote('a', 'PeepoA')]);
    flushByPath(httpMock, SERIES_URL, SERIES);
    await settle();

    expect(component['setStatusChannel']()).toBe('a');
    expect(component['activeEmoteSetId']()).toBe('set-b');
    expect(component['selectedEmoteSetId']()).toBe('set-b');
    expect(component['deleteLockReasonKey']()).toBeNull();
    expect(component['selection'].selectedKeys()).toEqual(['7tv-a']);
  });

  it("(5) a refresh that overtakes the initial status after a channel switch and fails drops the previous channel's status: no tracking start, range resolved, locked", async () => {
    await mount({ totals: [emote('a', 'PeepoA')] });

    await switchToB();
    const initial = httpMock.expectOne(STATUS_URL_B);
    await emit(LIVE_EVENT_TYPES.channelSynced, 'b');
    flushOpenSetListReloadsB();
    httpMock.expectOne(STATUS_URL_B).flush({}, FAILED);
    await settle();

    // Channel a's DTO must not keep standing in for b's: "all time" would start at a's date.
    expect(component['trackedSince']()).toBeNull();
    expect(component['rangeResolved']()).toBe(true);
    expect(component['deleteLockReasonKey']()).toBe(UNAVAILABLE);

    // The overtaken initial answer, landing late, changes nothing.
    initial.flush(statusForB());
    await settle();
    expect(component['trackedSince']()).toBeNull();
    expect(component['deleteLockReasonKey']()).toBe(UNAVAILABLE);
  });
  it('(6) usage.flushed alone reloads the rows once, silently, and asks for no status', async () => {
    await mount({ totals: [emote('a', 'PeepoA')] });

    await emit(LIVE_EVENT_TYPES.usageFlushed);

    const asked = totalsRequests();
    expect(asked).toHaveLength(1);
    expect(component['isLoading']()).toBe(false);
    httpMock.expectNone(STATUS_URL);
  });

  it('(7) a URL-chosen set that becomes active, then stops being active, reloads its rows exactly once per change', async () => {
    await mount({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA')]),
    });

    // set-a → set-b: the viewed set is now the active one.
    await emit(LIVE_EVENT_TYPES.channelSynced);
    flushOpenSetListReloads();
    httpMock.expectOne(STATUS_URL).flush(statusFor('set-b'));
    await settle();
    let asked = totalsRequests();
    expect(asked).toHaveLength(1);
    expect(asked[0].request.params.get('emoteSetId')).toBe('set-b');
    asked[0].flush([emote('a', 'PeepoA')]);
    httpMock.match((r) => LIVE_LIST_URL.test(r.url)).forEach((r) => r.flush(memberList([])));
    await settle();
    expect(component['isNonActiveView']()).toBe(false);

    // set-b → set-a: the viewed set stops being the active one.
    await emit(LIVE_EVENT_TYPES.channelSynced);
    flushOpenSetListReloads();
    httpMock.expectOne(STATUS_URL).flush(statusFor('set-a'));
    await settle();
    asked = totalsRequests();
    expect(asked).toHaveLength(1);
    expect(asked[0].request.params.get('emoteSetId')).toBe('set-b');
  });

  it('(8) a failed status over a URL-chosen set with its member list in locks deleting and voting with its own reason, until a status succeeds', async () => {
    await mount({
      emoteSetId: 'set-b',
      totals: [emote('a', 'PeepoA')],
      members: memberList([member('7tv-a', 'PeepoA')]),
    });
    expect(component['deleteLockReasonKey']()).toBeNull();
    expect(component['voteLocked']()).toBe(false);

    await emit(LIVE_EVENT_TYPES.channelSynced);
    flushOpenSetListReloads();
    httpMock.expectOne(STATUS_URL).flush({}, { status: 503, statusText: 'Service Unavailable' });
    httpMock
      .match((r) => LIVE_LIST_URL.test(r.url))
      .forEach((r) => r.flush(memberList([member('7tv-a', 'PeepoA')])));
    await settle();

    expect(component['selectedEmoteSetId']()).toBe('set-b');
    expect(component['deleteLockReasonKey']()).toBe('usageStats.setView.lock.statusUnavailable');
    expect(component['voteLocked']()).toBe(true);
    expect(component['voteLockReasonKey']()).toBe('usageStats.setView.lock.statusUnavailable');
    // set-b was already a non-active view: nothing about the rows on screen changed kind.
    expect(totalsRequests()).toHaveLength(0);

    await emit(LIVE_EVENT_TYPES.channelSynced);
    flushOpenSetListReloads();
    httpMock.expectOne(STATUS_URL).flush(statusFor('set-a'));
    await settle();
    flushByPath(httpMock, TOTALS_URL, [emote('a', 'PeepoA')]);
    httpMock
      .match((r) => LIVE_LIST_URL.test(r.url))
      .forEach((r) => r.flush(memberList([member('7tv-a', 'PeepoA')])));
    await settle();

    expect(component['deleteLockReasonKey']()).toBeNull();
    expect(component['voteLocked']()).toBe(false);
  });

  it('(9a) an older failure landing after a newer success does not lock', async () => {
    await mount({ totals: [emote('a', 'PeepoA')] });

    await emit(LIVE_EVENT_TYPES.channelSynced);
    const older = httpMock.expectOne(STATUS_URL);
    await emit(LIVE_EVENT_TYPES.channelSynced);
    const newer = httpMock.expectOne(STATUS_URL);
    flushOpenSetListReloads();

    newer.flush(statusFor('set-a'));
    await settle();
    older.flush({}, FAILED);
    await settle();

    expect(component['setStatusChannel']()).toBe('a');
    expect(component['activeEmoteSetId']()).toBe('set-a');
    expect(component['deleteLockReasonKey']()).toBeNull();
  });

  it('(9b) an older success landing after a newer failure does not lift the lock', async () => {
    await mount({ totals: [emote('a', 'PeepoA')] });

    await emit(LIVE_EVENT_TYPES.channelSynced);
    const older = httpMock.expectOne(STATUS_URL);
    await emit(LIVE_EVENT_TYPES.channelSynced);
    const newer = httpMock.expectOne(STATUS_URL);
    flushOpenSetListReloads();

    newer.flush({}, FAILED);
    await settle();
    older.flush(statusFor('set-b'));
    await settle();

    // The last known set stays (a refresh failure never un-claims); the stale success neither
    // moves it nor lifts the lock.
    expect(component['setStatusChannel']()).toBe('a');
    expect(component['activeEmoteSetId']()).toBe('set-a');
    expect(component['deleteLockReasonKey']()).toBe(UNAVAILABLE);
  });

  it('(9c) an older success landing after a newer success does not overwrite it', async () => {
    await mount({ totals: [emote('a', 'PeepoA')] });

    await emit(LIVE_EVENT_TYPES.channelSynced);
    const older = httpMock.expectOne(STATUS_URL);
    await emit(LIVE_EVENT_TYPES.channelSynced);
    const newer = httpMock.expectOne(STATUS_URL);
    flushOpenSetListReloads();

    newer.flush(statusFor('set-b'));
    await settle();
    older.flush(statusFor('set-a'));
    await settle();

    expect(component['activeEmoteSetId']()).toBe('set-b');
  });

  describe('(9d) a sync-failure recheck tick never overtakes a sync status read in flight', () => {
    const RECHECK_INTERVAL_MS = 60000; // SYNC_FAILURE_RECHECK_INTERVAL_MS

    /** An old active set plus a failure reason: the recheck poll runs. */
    function failingStatus(): EmoteSetStatus {
      return { ...statusFor('set-a'), syncFailureReason: 'seventv_unavailable' };
    }

    /** channel.synced → its status read goes out; then a recheck tick fires before it answers and
     *  whatever poll request that produced is answered first. Returns the sync's own request. */
    async function syncThenTick(pollAnswer: 'fail' | 'succeed'): Promise<TestRequest> {
      await mount({ totals: [emote('a', 'PeepoA')], status: failingStatus() });
      await emit(LIVE_EVENT_TYPES.channelSynced);
      flushOpenSetListReloads();
      const refresh = httpMock.expectOne(STATUS_URL);

      vi.advanceTimersByTime(RECHECK_INTERVAL_MS);
      await settle();
      httpMock
        .match(STATUS_URL)
        .forEach((poll) =>
          pollAnswer === 'fail' ? poll.flush({}, FAILED) : poll.flush(failingStatus()),
        );
      await settle();
      return refresh;
    }

    it('a failed tick, then a failed sync read: the sync read still locks', async () => {
      const refresh = await syncThenTick('fail');
      refresh.flush({}, FAILED);
      await settle();

      expect(component['deleteLockReasonKey']()).toBe(UNAVAILABLE);
      expect(component['voteLockReasonKey']()).toBe(UNAVAILABLE);
      expect(totalsRequests()).toHaveLength(0);
    });

    it('a failed tick, then a successful sync read: adopted, and its rows reloaded once', async () => {
      const refresh = await syncThenTick('fail');
      refresh.flush(failingStatus());
      await settle();

      expect(component['setStatusUnavailableFor']()).toBeNull();
      const asked = totalsRequests();
      expect(asked.map((r) => r.request.params.get('emoteSetId'))).toEqual(['set-a']);
    });

    it('a successful tick, then a successful sync read: the rows are reloaded exactly once', async () => {
      const refresh = await syncThenTick('succeed');
      refresh.flush(failingStatus());
      await settle();

      expect(component['setStatusUnavailableFor']()).toBeNull();
      const asked = totalsRequests();
      expect(asked.map((r) => r.request.params.get('emoteSetId'))).toEqual(['set-a']);
    });
  });

  it('(10) a silent reload that overtakes a loud one takes the skeleton down when it lands', async () => {
    await mount({ totals: [emote('a', 'PeepoA')] });

    // A loud reload: the refresh button (status, rows and curve, skeleton up).
    component['refresh']();
    await settle();
    expect(component['isLoading']()).toBe(true);
    httpMock.expectOne(STATUS_URL).flush(statusFor('set-a'));
    await settle();

    // A flush burst while the loud rows are still out — its silent request is now the latest.
    await emit(LIVE_EVENT_TYPES.usageFlushed);
    const asked = totalsRequests();
    expect(asked.length).toBeGreaterThanOrEqual(2);
    asked[asked.length - 1].flush([emote('a', 'PeepoA')]);
    await settle();

    expect(component['isLoading']()).toBe(false);

    // The superseded loud answer, landing late, changes nothing.
    asked.slice(0, -1).forEach((request) => request.flush([]));
    flushByPath(httpMock, SERIES_URL, SERIES);
    await settle();
    expect(component['isLoading']()).toBe(false);
    expect(component['emotes']().map((row) => row.sevenTvEmoteId)).toEqual(['7tv-a']);
  });

  it('(11) under "all time", a flush after a channel switch asks for no rows before the status; then exactly one request with the corrected range', async () => {
    await mount({ totals: [emote('a', 'PeepoA')] });

    await switchToB();
    const status = httpMock.expectOne(STATUS_URL_B);
    expect(component['isLoading']()).toBe(true);

    await emit(LIVE_EVENT_TYPES.usageFlushed, 'b');
    expect(totalsRequestsB()).toHaveLength(0);
    expect(component['isLoading']()).toBe(true);

    status.flush(statusForB());
    await settle();
    fixture.detectChanges();
    await settle();

    const asked = totalsRequestsB();
    expect(asked).toHaveLength(1);
    expect(asked[0].request.params.get('from')).toBe('2026-02-01');
    expect(component['isLoading']()).toBe(true);
    asked[0].flush([emote('b', 'PeepoB')]);
    await settle();
    expect(component['isLoading']()).toBe(false);
  });

  it("(12) a channel.synced reload for the new channel that lands before its loud load clears the previous channel's marks, even for a 7TV id both channels share", async () => {
    await mount({ totals: [emote('a', 'PeepoA')] });
    mark('7tv-a');

    await switchToB();
    httpMock.expectOne(STATUS_URL_B).flush(statusForB());
    await settle();
    fixture.detectChanges();
    await settle();
    // b's loud load is out; the sync's reload overtakes it.
    const loud = totalsRequestsB();
    expect(loud).toHaveLength(1);

    await emit(LIVE_EVENT_TYPES.channelSynced, 'b');
    flushOpenSetListReloadsB();
    httpMock.expectOne(STATUS_URL_B).flush(statusForB());
    await settle();
    const pushed = totalsRequestsB();
    expect(pushed).toHaveLength(1);
    pushed[0].flush([emote('a', 'PeepoA')]);
    await settle();

    expect(component['selection'].selectedKeys()).toEqual([]);

    loud[0].flush([emote('a', 'PeepoA')]);
    await settle();
    expect(component['selection'].selectedKeys()).toEqual([]);
  });
});

/**
 * #201 T-B (spec 7.0, 7.0a, 9.1, 9.2, 8): the tag filter in row two, the two dock actions and their acknowledgement. Template replaced as in the blocks above —
 * what is under test is the page's own decisions (what is shown, what is sent, what is said), read
 * from its signals; which `@if` renders them is the audit harness's and the e2e suite's job.
 */
describe('UsageStatsPage — tags: filter, dock actions, messages (#201 T-B)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;
  let openSpy: ReturnType<typeof vi.fn>;

  const SERIES = { from: '2026-01-01', to: '2026-09-08', liveDays: [], emotes: [] };
  const TAGS_URL = '/api/channels/a/tags';

  function tag(
    id: number,
    name: string,
    overrides: Partial<EmoteTagSummary> = {},
  ): EmoteTagSummary {
    return {
      id,
      name,
      entryCount: 5,
      inSetCount: 3,
      placedCount: 0,
      active: false,
      activatedAtUtc: null,
      ...overrides,
    };
  }

  function entry(sevenTvEmoteId: string): EmoteTagEntry {
    return {
      sevenTvEmoteId,
      alias: sevenTvEmoteId,
      imageUrl: '',
      inSet: true,
      currentName: null,
      placedByThisTag: false,
      placedAtUtc: null,
      placementOperationId: null,
      heldByActiveTags: [],
      placedByOtherTags: [],
    };
  }

  function configure(coarse: boolean, realTemplate = false): void {
    TestBed.resetTestingModule();
    stubNoImportCoverage();
    FakeEventSource.instances = [];
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    if (coarse) {
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
    }
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
    if (!realTemplate) {
      TestBed.overrideComponent(UsageStatsPage, {
        set: { template: '<div #sheet></div><div #stickyBar></div>' },
      });
    }
  }

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  function tagListRequests(): TestRequest[] {
    return httpMock.match((r) => r.url === TAGS_URL);
  }

  function flushTags(tags: EmoteTagSummary[]): void {
    for (const request of tagListRequests().filter((candidate) => !candidate.cancelled)) {
      request.flush({ emoteSetId: 'set-a', isActiveSet: true, tags });
    }
  }

  function flushEntries(tagId: number, ids: string[]): void {
    httpMock
      .expectOne((r) => r.url === `${TAGS_URL}/${tagId}/entries`)
      .flush({ emoteSetId: 'set-a', isActiveSet: true, entries: ids.map(entry) });
  }

  /**
   * Mounts channel 'a' (active set `set-a`) up to the rows being on screen and answers the tag list
   * with `tags`. `emoteSetId: 'set-b'` opens a view of another set (its member list answered with
   * the same rows); `activeEmoteSetId: ''` a channel without an active set.
   */
  async function open(options: {
    tags: EmoteTagSummary[];
    totals?: EmoteUsageTotalDto[];
    canManage?: boolean;
    coarse?: boolean;
    emoteSetId?: string;
    activeEmoteSetId?: string;
    realTemplate?: boolean;
    /** How the shown set's live member list is answered (default: complete). */
    liveMembers?: 'truncated' | 'unavailable';
  }): Promise<void> {
    configure(options.coarse ?? false, options.realTemplate ?? false);
    if (options.emoteSetId) {
      await TestBed.inject(Router).navigate([], {
        queryParams: { emoteSetId: options.emoteSetId },
      });
    }
    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
    await settle();

    httpMock.expectOne('/api/channels/a/permissions').flush({
      canManage: options.canManage ?? true,
      canViewUsageStats: true,
      tagRunsEnabled: false,
    });
    httpMock.expectOne('/api/channels/a/emotes/active-set').flush(
      setStatus({
        activeEmoteSetId: options.activeEmoteSetId ?? 'set-a',
        trackedSince: '2026-01-01T00:00:00Z',
      }),
    );
    fixture.detectChanges();
    fixture.detectChanges();
    httpMock
      .match((r) => r.url === '/api/channels/a/emote-sets')
      .forEach((request) =>
        request.flush(
          emoteSetList([
            emoteSet({ id: 'set-a', isActive: true }),
            emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
          ]),
        ),
      );
    await settle();

    const totals = options.totals ?? [
      emote('a', 'PeepoA'),
      emote('b', 'PeepoB'),
      emote('c', 'PeepoC'),
    ];
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', totals);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    httpMock
      .match((r) => LIVE_LIST_URL.test(r.url))
      .forEach((request) =>
        options.liveMembers === 'unavailable'
          ? request.flush(null, { status: 503, statusText: 'Service Unavailable' })
          : request.flush({
              channelName: 'a',
              sevenTvUserId: null,
              emoteSetId: 'set-b',
              emoteSetName: 'Halloween',
              capacity: 1000,
              totalCount: totals.length,
              truncated: options.liveMembers === 'truncated',
              emotes: totals.map((row) => ({
                sevenTvEmoteId: row.sevenTvEmoteId,
                name: row.emoteName,
                defaultName: row.emoteName,
                imageUrl: '',
                topAllTime: null,
                trending: null,
              })),
            }),
      );
    await settle();
    flushTags(options.tags);
    await settle();
  }

  function mark(...ids: string[]): void {
    for (const id of ids) {
      const row = component['emotes']().find((emote) => emote.sevenTvEmoteId === id);
      if (!row) {
        throw new Error(`no row ${id}`);
      }
      component['selection'].onRowClick(row, { shiftKey: false } as MouseEvent);
    }
  }

  async function chooseTag(tagId: number, memberIds: string[]): Promise<void> {
    component['onTagFilterChange'](String(tagId));
    await settle();
    flushEntries(tagId, memberIds);
    await settle();
  }

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('asks for the tag list against the active set, and offers the select only once there is a tag (E18)', async () => {
    configure(false);
    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
    await settle();
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    await settle();
    // Not before the set status is in — otherwise it would be asked twice.
    expect(tagListRequests()).toEqual([]);
    httpMock.expectOne('/api/channels/a/emotes/active-set').flush(setStatus({}));
    await settle();

    const requests = tagListRequests();
    expect(requests.map((request) => request.request.params.get('emoteSetId'))).toEqual(['set-a']);
    requests[0].flush({ emoteSetId: 'set-a', isActiveSet: true, tags: [] });
    await settle();
    expect(component['tagFilterShown']()).toBe(false);

    await open({ tags: [tag(4, 'Stronghold')] });
    expect(component['tagFilterShown']()).toBe(true);
  });

  it('choosing a tag sets the filter, loads its keys and narrows the grid to its members; "Alle Tags" undoes it', async () => {
    await open({ tags: [tag(4, 'Stronghold', { entryCount: 5, inSetCount: 2 })] });

    component['onTagFilterChange']('4');
    expect(component['usageFilter'].tagId()).toBe(4);
    // Keys not loaded yet: the view holds nothing meanwhile, never the whole set (tagFilterPending).
    expect(component['filteredEmotes']()).toHaveLength(0);
    await settle();
    flushEntries(4, ['7tv-a', '7tv-c']);
    await settle();

    expect([...(component['usageFilter'].tagKeys() ?? [])].sort()).toEqual(['7tv-a', '7tv-c']);
    expect(component['filteredEmotes']().map((row) => row.sevenTvEmoteId)).toEqual([
      '7tv-a',
      '7tv-c',
    ]);
    expect(component['selectedTag']()?.name).toBe('Stronghold');

    component['onTagFilterChange']('');
    expect(component['usageFilter'].tagId()).toBeNull();
    expect(component['filteredEmotes']()).toHaveLength(3);
  });

  it('"Filter zurücksetzen" clears the tag with the other filters', async () => {
    await open({ tags: [tag(4, 'Stronghold')] });
    await chooseTag(4, ['7tv-a']);

    component['usageFilter'].reset();

    expect(component['usageFilter'].tagId()).toBeNull();
    expect(component['selectedTag']()).toBeNull();
    expect(component['filteredEmotes']()).toHaveLength(3);
  });

  it('drops a chosen tag the reloaded list no longer names (deleted elsewhere)', async () => {
    await open({ tags: [tag(4, 'Stronghold'), tag(5, 'Spooky')] });
    await chooseTag(4, ['7tv-a']);

    component['tagsResource'].reload();
    await settle();
    flushTags([tag(5, 'Spooky')]);
    await settle();

    expect(component['usageFilter'].tagId()).toBeNull();
    expect(component['filteredEmotes']()).toHaveLength(3);
  });

  it('offers "Tag zuweisen…" for a manager on a fine pointer with a selection in the active view', async () => {
    await open({ tags: [] });
    expect(component['tagAssignShown']()).toBe(false);

    mark('7tv-a');

    expect(component['tagAssignShown']()).toBe(true);
  });

  it('has no tag action without the management right', async () => {
    await open({ tags: [tag(4, 'Stronghold')], canManage: false });
    mark('7tv-a');

    expect(component['tagAssignShown']()).toBe(false);
  });

  it('has no tag action on a coarse pointer', async () => {
    await open({ tags: [tag(4, 'Stronghold')], coarse: true });
    // Marked behind the page's back — the coarse-pointer effect would clear it on the next pass;
    // read before it can, so the pointer is the only reason left.
    mark('7tv-a');

    expect(component['selection'].selectedItems()).toHaveLength(1);
    expect(component['tagAssignShown']()).toBe(false);
  });

  it('has no tag action on a channel without an active set', async () => {
    await open({ tags: [tag(4, 'Stronghold')], activeEmoteSetId: '' });
    mark('7tv-a');

    expect(component['tagAssignShown']()).toBe(false);
  });

  it('in a view of another set with a complete member list: offers the tag actions, the filter works', async () => {
    await open({ tags: [tag(4, 'Stronghold')], emoteSetId: 'set-b' });
    mark('7tv-a');

    expect(component['tagAssignShown']()).toBe(true);

    await chooseTag(4, ['7tv-a', '7tv-b']);

    expect(component['filteredEmotes']().map((row) => row.sevenTvEmoteId)).toEqual([
      '7tv-a',
      '7tv-b',
    ]);
    expect(component['tagUnassignShown']()).toBe(true);
  });

  it.each(['truncated', 'unavailable'] as const)(
    'in a view of another set whose member list is %s: no tag action',
    async (liveMembers) => {
      await open({ tags: [tag(4, 'Stronghold')], emoteSetId: 'set-b', liveMembers });
      mark('7tv-a');

      expect(component['tagAssignShown']()).toBe(false);
      expect(component['tagUnassignShown']()).toBe(false);
    },
  );

  it('in a view of another set that is still loading its member list: no tag action', async () => {
    await open({ tags: [tag(4, 'Stronghold')] });
    mark('7tv-a');
    component['onEmoteSetSelected']('set-b');
    await settle();

    expect(component['sharedSetViewLockReasonKey']()).not.toBeNull();
    expect(component['selection'].selectedItems().length).toBeGreaterThan(0);
    expect(component['tagAssignShown']()).toBe(false);
  });

  it('hands the dialog the shown set id only for a set that is not the active one', async () => {
    await open({ tags: [tag(4, 'Stronghold')], emoteSetId: 'set-b' });
    mark('7tv-a');
    openSpy.mockReturnValue({ closed: of(undefined) });

    component['assignTags']();

    expect(openSpy.mock.calls[0][1].data).toEqual({
      channelName: 'a',
      sevenTvEmoteIds: ['7tv-a'],
      emoteSetId: 'set-b',
    });
  });

  it('offers "Aus dem Tag entfernen" only with a tag filter, counting the marked emotes that are in the tag', async () => {
    await open({ tags: [tag(4, 'Stronghold')] });
    mark('7tv-a', '7tv-b');
    expect(component['tagUnassignShown']()).toBe(false);

    await chooseTag(4, ['7tv-a', '7tv-c']);

    expect(component['tagUnassignShown']()).toBe(true);
    expect(component['tagUnassignCount']()).toBe(1);
  });

  it('after "Tag zuweisen…": hands the dialog the marked ids, acknowledges for 4 s, reloads the tags and keeps the selection', async () => {
    await open({ tags: [tag(4, 'Stronghold')] });
    mark('7tv-a', '7tv-b');
    const result: TagAssignDialogResult = {
      tagNames: ['Stronghold'],
      emoteCount: 2,
      skippedNotInSetCount: 0,
    };
    openSpy.mockReturnValue({ closed: of(result) });
    vi.useFakeTimers();

    component['assignTags']();

    const data = openSpy.mock.calls[0][1].data as TagAssignDialogData;
    expect(data).toEqual({
      channelName: 'a',
      sevenTvEmoteIds: ['7tv-a', '7tv-b'],
      emoteSetId: undefined,
    });
    expect(component['tagFeedback']()).toEqual([
      { key: 'tags.feedback.assigned.other', params: { count: 2, tag: 'Stronghold' } },
    ]);
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-b']);
    fixture.detectChanges();
    expect(tagListRequests()).toHaveLength(1);

    vi.advanceTimersByTime(3999);
    expect(component['tagFeedback']()).not.toBeNull();
    vi.advanceTimersByTime(1);
    expect(component['tagFeedback']()).toBeNull();
  });

  it('names the number of tags for several, and adds the skipped emotes as a second sentence', async () => {
    await open({ tags: [tag(4, 'Stronghold'), tag(5, 'Spooky')] });
    mark('7tv-a', '7tv-b', '7tv-c');
    openSpy.mockReturnValue({
      closed: of({ tagNames: ['Stronghold', 'Spooky'], emoteCount: 2, skippedNotInSetCount: 1 }),
    });

    component['assignTags']();

    expect(component['tagFeedback']()).toEqual([
      { key: 'tags.feedback.assignedMany.other', params: { count: 2, tags: 2 } },
      { key: 'tags.feedback.skippedNotInSet.one', params: { count: 1 } },
    ]);
  });

  it('reloads the tags even when the dialog closes without assigning — a tag may have been created in it', async () => {
    await open({ tags: [] });
    mark('7tv-a');
    openSpy.mockReturnValue({ closed: of(undefined) });

    component['assignTags']();
    fixture.detectChanges();

    expect(tagListRequests()).toHaveLength(1);
    expect(component['tagFeedback']()).toBeNull();
  });

  it("reloads the chosen tag's keys after an assignment while the tag filter is set", async () => {
    await open({ tags: [tag(4, 'Stronghold')] });
    await chooseTag(4, ['7tv-a']);
    mark('7tv-a');
    openSpy.mockReturnValue({
      closed: of({ tagNames: ['Stronghold'], emoteCount: 1, skippedNotInSetCount: 0 }),
    });

    component['assignTags']();
    fixture.detectChanges();

    flushEntries(4, ['7tv-a', '7tv-b']);
    await settle();
    expect(component['filteredEmotes']().map((row) => row.sevenTvEmoteId)).toEqual([
      '7tv-a',
      '7tv-b',
    ]);
  });

  it('removes only the marked emotes that are in the tag, acknowledges, and reloads tags and keys', async () => {
    await open({ tags: [tag(4, 'Stronghold')] });
    await chooseTag(4, ['7tv-a', '7tv-c']);
    mark('7tv-a', '7tv-b');

    component['removeFromTag']();

    const request = httpMock.expectOne(`${TAGS_URL}/4/entries/remove`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ sevenTvEmoteIds: ['7tv-a'] });
    expect(component['tagRemovalPending']()).toBe(true);
    request.flush({ removedCount: 1 });

    expect(component['tagRemovalPending']()).toBe(false);
    expect(component['tagFeedback']()).toEqual([
      { key: 'tags.feedback.unassigned.one', params: { count: 1, tag: 'Stronghold' } },
    ]);
    await settle();
    expect(tagListRequests()).toHaveLength(1);
    flushEntries(4, ['7tv-c']);
    await settle();
    expect(component['filteredEmotes']().map((row) => row.sevenTvEmoteId)).toEqual(['7tv-c']);
    // Still marked — only hidden by the filter now.
    expect(component['selection'].selectedKeys().sort()).toEqual(['7tv-a', '7tv-b']);
  });

  it('says why a removal failed, in a banner rather than a fading message', async () => {
    await open({ tags: [tag(4, 'Stronghold')] });
    await chooseTag(4, ['7tv-a']);
    mark('7tv-a');

    component['removeFromTag']();
    httpMock
      .expectOne(`${TAGS_URL}/4/entries/remove`)
      .flush({ errorCode: 'emote_ids_invalid' }, { status: 400, statusText: 'Bad Request' });

    expect(component['tagErrorKey']()).toBe('errors.api.emote_ids_invalid');
    expect(component['tagFeedback']()).toBeNull();

    // A new tag choice retires it.
    component['onTagFilterChange']('');
    expect(component['tagErrorKey']()).toBeNull();
  });

  it('reloads the tag list when a removal answers tag_not_found, so the dead tag goes', async () => {
    await open({ tags: [tag(4, 'Stronghold')] });
    await chooseTag(4, ['7tv-a']);
    mark('7tv-a');

    component['removeFromTag']();
    httpMock
      .expectOne(`${TAGS_URL}/4/entries/remove`)
      .flush({ errorCode: 'tag_not_found' }, { status: 404, statusText: 'Not Found' });
    await settle();

    expect(component['tagErrorKey']()).toBe('errors.api.tag_not_found');
    expect(tagListRequests()).toHaveLength(1);
  });

  it("while a chosen tag's emotes are loading, nothing acts on the unfiltered list: no mark-all, no transfer or export of the set, no count of it", async () => {
    await open({ tags: [tag(4, 'Stronghold')] });
    expect(component['showMarkAll']()).toBe(true);
    expect(component['tagFilterPending']()).toBe(false);
    expect(component['transferButtonDisabled']()).toBe(false);
    expect(component['exportButtonDisabled']()).toBe(false);

    component['onTagFilterChange']('4');
    await settle();
    // Keys in flight: the filter would let everything through, the page must not act on that.
    expect(component['tagFilterPending']()).toBe(true);
    expect(component['showMarkAll']()).toBe(false);
    expect(component['atlasOrder']()).toHaveLength(0);
    // "Übertragen" pushes the visible grid and the export's default scope is it: neither may
    // carry the whole set for a tag that may hold nothing.
    expect(component['transferButtonDisabled']()).toBe(true);
    expect(component['exportButtonDisabled']()).toBe(true);

    // A tag without emotes: it ends on the empty state, mark-all never came back in between.
    flushEntries(4, []);
    await settle();
    expect(component['tagFilterPending']()).toBe(false);
    expect(component['atlasOrder']()).toHaveLength(0);
    expect(component['showMarkAll']()).toBe(false);
  });

  /** The default rows with an image each: the real template renders the sidecar's sprite. */
  function withImages(): EmoteUsageTotalDto[] {
    return [emote('a', 'PeepoA'), emote('b', 'PeepoB'), emote('c', 'PeepoC')].map((row) => ({
      ...row,
      imageUrl: `https://cdn.example/${row.sevenTvEmoteId}.webp`,
    }));
  }

  it("keeps the count line quiet, the sidecar and the dock's hidden-by-filter count steady while a tag's emotes load", async () => {
    await open({ tags: [tag(4, 'Stronghold')], realTemplate: true, totals: withImages() });
    mark('7tv-a', '7tv-b');
    fixture.detectChanges();
    const inspectedBefore = component['inspected']();
    expect(inspectedBefore).not.toBeNull();
    expect(component['shownHiddenSelectedCount']()).toBe(0);
    const countLine = () =>
      Array.from(
        fixture.nativeElement.querySelectorAll('p[role="status"]') as NodeListOf<HTMLElement>,
      ).find((candidate) => candidate.textContent?.includes('emoteCount'));
    expect(countLine()?.hasAttribute('aria-busy')).toBe(false);

    component['onTagFilterChange']('4');
    await settle();
    expect(component['tagFilterPending']()).toBe(true);
    // The empty view would read both marks as hidden for one request — not shown, not spoken.
    expect(component['selection'].hiddenSelectedCount()).toBe(2);
    expect(component['shownHiddenSelectedCount']()).toBe(0);
    expect(component['dockHiddenSelectedCount']()).toBe(0);
    // The sidecar keeps its emote, so its column does not collapse and come back.
    expect(component['inspected']()).toBe(inspectedBefore);
    // The count line announces the tag's count once it is known, not the window's "0 of 3".
    expect(countLine()?.getAttribute('aria-busy')).toBe('true');

    flushEntries(4, ['7tv-a']);
    await settle();
    expect(component['shownHiddenSelectedCount']()).toBe(1);
    expect(component['inspected']()?.sevenTvEmoteId).toBe('7tv-a');
    expect(countLine()?.hasAttribute('aria-busy')).toBe(false);
  });

  it('a held hidden-by-filter count cannot outlive a selection cleared while a tag loads', async () => {
    await open({ tags: [tag(4, 'Stronghold')], totals: withImages() });
    mark('7tv-a', '7tv-b');
    component['usageFilter'].setNameFilter('PeepoC');
    expect(component['shownHiddenSelectedCount']()).toBe(2);

    component['onTagFilterChange']('4');
    await settle();
    expect(component['tagFilterPending']()).toBe(true);
    expect(component['shownHiddenSelectedCount']()).toBe(2);

    component['selection'].clear();
    await settle();
    expect(component['shownHiddenSelectedCount']()).toBe(0);
  });

  it("a failed set switch outranks a tag's pending emotes: its retry shows instead of the skeleton", async () => {
    await open({ tags: [tag(4, 'Stronghold')], realTemplate: true, totals: withImages() });
    component['onTagFilterChange']('4');
    await settle();
    expect(component['tagFilterPending']()).toBe(true);

    component['onEmoteSetSelected']('set-b');
    await settle();
    httpMock
      .match((r) => r.url === '/api/channels/a/usage-stats/totals')
      .forEach((request) => request.flush({}, { status: 500, statusText: 'Error' }));
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', SERIES);
    httpMock
      .match((r) => LIVE_LIST_URL.test(r.url))
      .forEach((request) => request.flush({}, { status: 500, statusText: 'Error' }));
    await settle();

    expect(component['setSwitchFailed']()).toBe(true);
    expect(component['tagFilterPending']()).toBe(true);
    const sheet = fixture.nativeElement as HTMLElement;
    expect(sheet.querySelector('[aria-label="usageStats.loading"]')).toBeNull();
    expect(sheet.textContent).toContain('usageStats.setView.loadFailedTitle');
  });

  it('a tag with emotes offers mark-all only once its keys narrowed the grid; "Alle Tags" is never pending', async () => {
    await open({ tags: [tag(4, 'Stronghold')] });

    component['onTagFilterChange']('4');
    await settle();
    expect(component['showMarkAll']()).toBe(false);
    flushEntries(4, ['7tv-a']);
    await settle();
    expect(component['tagFilterPending']()).toBe(false);
    expect(component['atlasOrder']()).toHaveLength(1);
    expect(component['showMarkAll']()).toBe(true);

    component['onTagFilterChange']('');
    expect(component['tagFilterPending']()).toBe(false);
    expect(component['showMarkAll']()).toBe(true);
  });

  it('a failed entries load is not "pending": the filter lets every row through and the banner explains', async () => {
    await open({ tags: [tag(4, 'Stronghold')] });
    component['onTagFilterChange']('4');
    await settle();
    httpMock
      .expectOne((r) => r.url === `${TAGS_URL}/4/entries`)
      .flush({ errorCode: 'internal' }, { status: 500, statusText: 'Server Error' });
    await settle();

    expect(component['tagFilterPending']()).toBe(false);
    expect(component['showMarkAll']()).toBe(true);
  });

  it('drops a chosen tag on a channel switch, before the new channel has answered its tag list', async () => {
    await open({ tags: [tag(4, 'Stronghold')] });
    await chooseTag(4, ['7tv-a']);

    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();

    // Channel A's keys must never narrow channel B's rows — 7TV ids are shared across channels.
    expect(component['usageFilter'].tagId()).toBeNull();
    expect(component['usageFilter'].tagKeys()).toBeNull();
    expect(httpMock.match((r) => r.url === '/api/channels/b/tags')).toEqual([]);
    expect(httpMock.match((r) => r.url.startsWith('/api/channels/b/tags/'))).toEqual([]);
  });

  it('never narrows the grid silently: a failed tag list with a tag chosen is explained by the banner', async () => {
    await open({ tags: [tag(4, 'Stronghold')] });
    await chooseTag(4, ['7tv-a']);

    component['tagsResource'].reload();
    await settle();
    for (const request of tagListRequests()) {
      request.flush(null, { status: 503, statusText: 'Service Unavailable' });
    }
    await settle();

    // The select and the summary need the list and are gone; the filter still narrows the grid.
    expect(component['selectedTag']()).toBeNull();
    expect(component['filteredEmotes']().map((row) => row.sevenTvEmoteId)).toEqual(['7tv-a']);
    // ...so the banner (gated on the filter's tag id) has to say why.
    expect(component['usageFilter'].tagId()).toBe(4);
    expect(component['tagErrorKey']()).not.toBeNull();
  });

  it('keeps the full tag name in the accessible name and title of the "Aus dem Tag entfernen" button', async () => {
    const image = 'https://cdn.7tv.app/emote/x/1x.webp';
    const longName = 'Fuer-die-Halloween-Wochen-Auswahl-2026-xx';
    await open({
      tags: [tag(4, longName)],
      totals: [{ ...emote('a', 'PeepoA'), imageUrl: image }],
      realTemplate: true,
    });
    TestBed.inject(TranslocoService).setTranslation(
      {
        'tags.actions.unassignNamed': 'Aus dem Tag entfernen ({{count}}) – {{tag}}',
        'tags.actions.unassignTitle': 'Erklärung',
      },
      'de',
    );
    await chooseTag(4, ['7tv-a']);
    mark('7tv-a');
    fixture.detectChanges();

    const button = fixture.nativeElement.querySelector(
      'button[aria-label^="Aus"]',
    ) as HTMLButtonElement | null;
    expect(button?.getAttribute('aria-label')).toBe(`Aus dem Tag entfernen (1) – ${longName}`);
    expect(button?.title).toContain(`Aus dem Tag entfernen (1) – ${longName}`);
  });

  it('describes the locked "Aus dem Tag entfernen" button with its reason when none of the marked emotes is in the tag (§10)', async () => {
    // Real template: the subject is the button's accessible description. A real-looking imageUrl,
    // since NgOptimizedImage runs for real here (NG02952 on '').
    const image = 'https://cdn.7tv.app/emote/x/1x.webp';
    await open({
      tags: [tag(4, 'Stronghold')],
      totals: [
        { ...emote('a', 'PeepoA'), imageUrl: image },
        { ...emote('b', 'PeepoB'), imageUrl: image },
      ],
      realTemplate: true,
    });
    await chooseTag(4, ['7tv-a']);
    mark('7tv-b');
    fixture.detectChanges();

    const button = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((candidate) => candidate.getAttribute('aria-label') === 'tags.actions.unassignNamed');
    expect(button).toBeDefined();
    expect(button?.disabled).toBe(true);
    const describedBy = button?.getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();
    const reason = fixture.nativeElement.querySelector(`#${describedBy}`) as HTMLElement | null;
    expect(reason?.textContent?.trim()).toBe('tags.actions.unassignLockReason.noneInTag');

    // Once a marked emote is in the tag the lock — and its description — are gone.
    mark('7tv-a');
    fixture.detectChanges();
    expect(button?.disabled).toBe(false);
    expect(button?.hasAttribute('aria-describedby')).toBe(false);
  });
});

/**
 * The header's "Übertragen" button and the set-view lock (review of #336, P2): the lock can disable
 * it on a loaded active-set view with nothing selected, where the dock that carries the delete/vote
 * reason is not mounted — so the button must explain itself next to it. Real template.
 */
describe('UsageStatsPage — the locked header transfer button explains itself', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;

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
    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();
    // A real imageUrl: the actual template renders the sprite (NgOptimizedImage rejects '').
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', [
      { ...emote('a', 'PeepoA'), imageUrl: 'https://cdn.7tv.app/emote/x/1x.webp' },
    ]);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function transferButton(): HTMLButtonElement | undefined {
    return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find(
      (button) => button.textContent?.trim() === 'import.copyButton',
    );
  }

  it('is enabled and carries no description while nothing locks it', () => {
    const button = transferButton()!;
    expect(button.disabled).toBe(false);
    expect(button.getAttribute('aria-describedby')).toBeNull();
  });

  it('is disabled and described by the reason text once a failed status read locks the view', () => {
    component['setStatusUnavailableFor'].set('a');
    fixture.detectChanges();

    const button = transferButton()!;
    expect(button.disabled).toBe(true);
    const reasonId = button.getAttribute('aria-describedby');
    expect(reasonId).not.toBeNull();
    const reason = (fixture.nativeElement as HTMLElement).querySelector(`#${reasonId}`);
    expect(reason?.textContent?.trim()).toBe('usageStats.setView.importLock.statusUnavailable');
  });
});

describe('UsageStatsPage — imported coverage: caption wording, scope and counting start (chat-log backfill)', () => {
  const COVERAGE_PATH = '/api/channels/a/usage-stats/import-coverage';

  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;

  function coverageBody(overrides: Partial<ImportCoverage> = {}): ImportCoverage {
    return {
      emoteSetId: 'set-a',
      sources: [{ name: 'logs.cyex.app', url: 'https://logs.cyex.app/' }],
      importedFrom: '2026-04-08',
      importedTo: '2026-10-08',
      hasGaps: false,
      contiguousFrom: '2026-04-08',
      intervals: [],
      ...overrides,
    };
  }

  const NOTHING_IMPORTED: ImportCoverage = {
    emoteSetId: 'set-b',
    sources: [],
    importedFrom: null,
    importedTo: null,
    hasGaps: false,
    contiguousFrom: null,
    intervals: [],
  };

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  function coverageRequests(): TestRequest[] {
    return httpMock.match((req) => req.url === COVERAGE_PATH);
  }

  /** Mounts the page on channel `a` up to the point where the coverage read is out, and flushes it. */
  async function mount(options: {
    trackedSince?: string;
    coverage?: ImportCoverage | 'fail';
    /** Stop after the coverage answer, leaving the grid requests it caused for the test to read. */
    holdGrid?: boolean;
  }): Promise<void> {
    useRealImportCoverage();
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
    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });
    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();

    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    httpMock.expectOne('/api/channels/a/emotes/active-set').flush(
      setStatus({
        activeEmoteSetId: 'set-a',
        trackedSince: options.trackedSince ?? '2026-10-08T09:30:00Z',
      }),
    );
    fixture.detectChanges();
    httpMock
      .expectOne('/api/channels/a/emote-sets')
      .flush(
        emoteSetList([
          emoteSet({ id: 'set-a', isActive: true }),
          emoteSet({ id: 'set-b', name: 'Halloween', isActive: false }),
        ]),
      );
    await settle();

    const [request, ...rest] = coverageRequests();
    expect(rest).toEqual([]);
    expect(request.request.params.get('emoteSetId')).toBe('set-a');
    // "All time" starts at the first imported day, which only the coverage knows: no grid request
    // may go out before it has answered.
    expect(totalsRequests()).toEqual([]);
    if (options.coverage === 'fail') {
      request.flush({}, { status: 500, statusText: 'Server Error' });
    } else {
      request.flush(options.coverage ?? coverageBody());
    }
    await settle();
    if (options.holdGrid) {
      return;
    }
    flushGrid();
    await settle();
  }

  function flushGrid(): void {
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-10-09',
      liveDays: [],
      emotes: [],
    });
  }

  function totalsRequests(): TestRequest[] {
    return httpMock.match((req) => req.url === '/api/channels/a/usage-stats/totals');
  }

  function pickRange(from: string): void {
    component['rangePreset'].set('custom');
    component['from'].set(from);
  }

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  describe('caption wording', () => {
    it('names the import with the plain tail while the covered days are consecutive', async () => {
      await mount({});

      expect(component['importCaption']()).toEqual({
        leadKey: 'usageStats.trackedSinceWithImport',
        tailKey: 'usageStats.trackedSinceWithImportEnd',
      });
    });

    it('takes the gaps tail whenever the coverage has gaps', async () => {
      await mount({ coverage: coverageBody({ hasGaps: true }) });

      expect(component['importCaption']()?.tailKey).toBe('usageStats.trackedSinceWithImportGaps');
    });

    it('keeps the plain sentence when nothing is imported', async () => {
      await mount({ coverage: { ...NOTHING_IMPORTED, emoteSetId: 'set-a' } });

      expect(component['importCaption']()).toBeNull();
    });

    it('discloses an import that does not reach the counting start', async () => {
      await mount({
        coverage: coverageBody({ importedTo: '2026-07-01', contiguousFrom: null, hasGaps: true }),
      });

      expect(component['importCaption']()).not.toBeNull();
    });

    it('keeps the plain sentence when the first read fails', async () => {
      await mount({ coverage: 'fail' });

      expect(component['importCaption']()).toBeNull();
    });
  });

  describe('scope', () => {
    it('refetches for the chosen set, and a set without imports of its own shows no import sentence', async () => {
      await mount({});
      expect(component['importCaption']()).not.toBeNull();

      component['onEmoteSetSelected']('set-b');
      await settle();

      // The previous set's answer is never read for the new set, not even while the request is out.
      expect(component['importCaption']()).toBeNull();
      const [request, ...rest] = coverageRequests();
      expect(rest).toEqual([]);
      expect(request.request.params.get('emoteSetId')).toBe('set-b');
      request.flush(NOTHING_IMPORTED);
      await settle();

      expect(component['importCaption']()).toBeNull();
    });

    it('refetches the disclosure on backfill.progress, but not the rows', async () => {
      await mount({});

      FakeEventSource.instances[0].emit({
        type: LIVE_EVENT_TYPES.backfillProgress,
        channel: 'a',
      });
      await new Promise((resolve) => setTimeout(resolve, CHANNEL_RELOAD_DEBOUNCE_MS + 20));
      fixture.detectChanges();

      const [request, ...rest] = coverageRequests();
      expect(rest).toEqual([]);
      httpMock.expectNone((req) => req.url === '/api/channels/a/usage-stats/totals');
      request.flush(coverageBody({ importedTo: '2026-10-08', hasGaps: true }));
      await settle();

      expect(component['importCaption']()?.tailKey).toBe('usageStats.trackedSinceWithImportGaps');
    });

    it('keeps serving the last good coverage when a refetch of the same scope fails', async () => {
      await mount({});

      FakeEventSource.instances[0].emit({
        type: LIVE_EVENT_TYPES.backfillProgress,
        channel: 'a',
      });
      await new Promise((resolve) => setTimeout(resolve, CHANNEL_RELOAD_DEBOUNCE_MS + 20));
      coverageRequests()[0].flush({}, { status: 429, statusText: 'Too Many Requests' });
      await settle();

      expect(component['importCaption']()).not.toBeNull();
    });
  });

  describe('"all time" start', () => {
    it('asks for the grid once, from the tracking start, when nothing is imported', async () => {
      await mount({ coverage: { ...NOTHING_IMPORTED, emoteSetId: 'set-a' }, holdGrid: true });

      const requests = totalsRequests();
      expect(requests).toHaveLength(1);
      expect(requests[0].request.params.get('from')).toBe('2026-10-08');
      expect(component['from']()).toBe('2026-10-08');
    });

    it('asks for the grid once, from the first imported day, when it lies before the tracking start', async () => {
      await mount({ holdGrid: true });

      const requests = totalsRequests();
      expect(requests).toHaveLength(1);
      expect(requests[0].request.params.get('from')).toBe('2026-04-08');
      expect(component['allTimeEarliest']()).toBe('2026-04-08');
    });

    it('falls back to the tracking start, still with one request, when the coverage read fails', async () => {
      await mount({ coverage: 'fail', holdGrid: true });

      const requests = totalsRequests();
      expect(requests).toHaveLength(1);
      expect(requests[0].request.params.get('from')).toBe('2026-10-08');
    });

    it('does not pull the start later than the tracking start for an import that lies after it', async () => {
      await mount({
        coverage: coverageBody({ importedFrom: '2026-10-09', importedTo: '2026-10-20' }),
        holdGrid: true,
      });

      expect(component['from']()).toBe('2026-10-08');
    });

    it('moves the start with a set switch while the preset is "all", asking once for the new set', async () => {
      await mount({});

      component['onEmoteSetSelected']('set-b');
      await settle();
      // The old rows stay until the new set's coverage is in; no request for set-b yet.
      expect(totalsRequests()).toEqual([]);
      const [request] = coverageRequests();
      expect(request.request.params.get('emoteSetId')).toBe('set-b');
      request.flush(coverageBody({ emoteSetId: 'set-b', importedFrom: '2026-07-09' }));
      await settle();

      const requests = totalsRequests();
      expect(requests).toHaveLength(1);
      expect(requests[0].request.params.get('emoteSetId')).toBe('set-b');
      expect(requests[0].request.params.get('from')).toBe('2026-07-09');
    });
  });

  describe('range warning wording', () => {
    it('names the counting start and says nothing was counted before it, without imports', async () => {
      await mount({ coverage: { ...NOTHING_IMPORTED, emoteSetId: 'set-a' } });

      expect(component['rangeBeforeTrackingKey']()).toBe('usageStats.rangeBeforeTracking');
    });

    it('names the start of the covered stretch, not the tracking start, once imports reach it', async () => {
      await mount({});

      expect(component['coverageStart']()).toBe('2026-04-08');
      expect(component['rangeBeforeTrackingKey']()).toBe('usageStats.rangeBeforeTracking');
    });

    it('warns under "all time" only for a gap: imports that do not reach the counting start', async () => {
      await mount({
        coverage: coverageBody({
          importedFrom: '2026-07-09',
          importedTo: '2026-07-23',
          contiguousFrom: null,
        }),
      });

      expect(component['rangePreset']()).toBe('all');
      expect(component['from']()).toBe('2026-07-09');
      expect(component['rangeStartsBeforeTracking']()).toBe(true);
      expect(component['rangeBeforeTrackingKey']()).toBe('usageStats.rangeBeforeTrackingPatchy');
    });

    it('does not warn under "all time" when the import is adjacent to the counting start', async () => {
      await mount({});

      expect(component['from']()).toBe('2026-04-08');
      expect(component['rangeStartsBeforeTracking']()).toBe(false);
    });

    it('does not warn under "all time" without imports', async () => {
      await mount({ coverage: { ...NOTHING_IMPORTED, emoteSetId: 'set-a' } });

      expect(component['rangeStartsBeforeTracking']()).toBe(false);
    });

    it('calls the stretch before the counting start patchy when imported days lie before it', async () => {
      await mount({
        coverage: coverageBody({
          importedFrom: '2026-07-09',
          importedTo: '2026-07-23',
          contiguousFrom: null,
          hasGaps: false,
        }),
      });

      expect(component['coverageStart']()).toBe('2026-10-08T09:30:00Z');
      expect(component['rangeBeforeTrackingKey']()).toBe('usageStats.rangeBeforeTrackingPatchy');
    });
  });

  describe('counting start for the warning and the trend', () => {
    it('does not warn from the start of the covered stretch, and warns for a range before it', async () => {
      await mount({});

      pickRange('2026-04-08');
      expect(component['rangeStartsBeforeTracking']()).toBe(false);

      pickRange('2026-04-07');
      expect(component['rangeStartsBeforeTracking']()).toBe(true);
    });

    it('anchors at the contiguous suffix when an older gap lies further back (spec example)', async () => {
      await mount({
        coverage: coverageBody({
          importedFrom: '2026-04-01',
          hasGaps: true,
          contiguousFrom: '2026-06-01',
        }),
      });

      pickRange('2026-06-01');
      expect(component['rangeStartsBeforeTracking']()).toBe(false);

      pickRange('2026-05-31');
      expect(component['rangeStartsBeforeTracking']()).toBe(true);
    });

    it('lets the live start govern when no covered day is adjacent to the counting start', async () => {
      await mount({
        coverage: coverageBody({ importedTo: '2026-07-01', contiguousFrom: null, hasGaps: true }),
      });

      pickRange('2026-10-01');
      expect(component['rangeStartsBeforeTracking']()).toBe(true);
    });

    it('keeps the trend suppressed when the import does not reach the counting start', async () => {
      await mount({
        coverage: coverageBody({ importedTo: '2026-07-01', contiguousFrom: null, hasGaps: true }),
      });
      component['from'].set('2026-09-01');
      component['to'].set('2026-09-30');

      expect(
        component['trendFor']({ totalUseCount: 20, previousWindowUseCount: 10, firstSeenAt: null }),
      ).toBe('unknown');
    });

    it('lets the live start govern after a rejoin gap', async () => {
      await mount({ trackedSince: '2026-10-20T00:00:00Z' });

      pickRange('2026-10-10');
      expect(component['rangeStartsBeforeTracking']()).toBe(true);
    });

    it('does not change a channel without imports: the live timestamp stays the start', async () => {
      await mount({ coverage: { ...NOTHING_IMPORTED, emoteSetId: 'set-a' } });

      expect(component['coverageStart']()).toBe('2026-10-08T09:30:00Z');
    });

    it('computes a trend across the imported stretch that the live start alone would suppress', async () => {
      const row = {
        totalUseCount: 20,
        previousWindowUseCount: 10,
        firstSeenAt: null,
      };
      await mount({});
      component['from'].set('2026-09-01');
      component['to'].set('2026-09-30');
      expect(component['trendFor'](row)).toBe('rising');

      TestBed.resetTestingModule();
      await mount({ coverage: { ...NOTHING_IMPORTED, emoteSetId: 'set-a' } });
      component['from'].set('2026-09-01');
      component['to'].set('2026-09-30');
      expect(component['trendFor'](row)).toBe('unknown');
    });
  });
});
