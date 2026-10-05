import { Dialog } from '@angular/cdk/dialog';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, WritableSignal, computed, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { NEVER, Observable, Subject, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import {
  REPORT_TIMEOUT_MS,
  SevenTvDeleteService,
} from '../../core/seven-tv/seven-tv-delete.service';
import { EditableSetResolution } from '../../core/seven-tv/seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import {
  EmoteTagEntries,
  EmoteTagEntry,
  TagPlacementsResult,
} from '../../core/tags/emote-tag.model';
import { EmoteTagService } from '../../core/tags/emote-tag.service';
import {
  ImportConfirmDialog,
  ImportConfirmDialogData,
  ImportConfirmOutcome,
} from '../seven-tv/import-confirm-dialog';
import { TagRunRequest, raiseNotice, startTagPlayInFlow } from './tag-play-in-flow';
import { OrphanedTagRunEvent, TagRunNotice, TagRunNoticeSink } from './tag-run-notice-sink';

/*
 * The play-in flow runs against fakes one level below its building blocks: the real
 * `resolveDeleteTarget`, `readLiveSetAliases` and `startImportFlow` drive the faked services, and
 * every call that matters is logged in `calls`, so the step order is asserted on what actually went
 * out. `dialog.open` stands in for the import confirmation.
 */

const CHANNEL = 'handofblood';
const TAG = { id: 7, name: 'Stronghold' };
const OP_1 = '00000000-0000-4000-8000-000000000001';
const OP_2 = '00000000-0000-4000-8000-000000000002';

function entry(id: string): EmoteTagEntry {
  return {
    sevenTvEmoteId: id,
    alias: `alias-${id}`,
    imageUrl: `https://cdn.example/${id}`,
    inSet: true,
    currentName: null,
    placedByThisTag: false,
    placedAtUtc: null,
    placementOperationId: null,
    heldByActiveTags: [],
    placedByOtherTags: [],
  };
}

function entriesFor(ids: string[], over: Partial<EmoteTagEntries> = {}): EmoteTagEntries {
  return {
    emoteSetId: 'set-active',
    isActiveSet: true,
    entries: ids.map(entry),
    activationOperationId: null,
    ...over,
  };
}

/** One complete page of a 7TV set read, each id under one alias. `truncated` promises one more. */
function setPage(ids: string[], truncated = false) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount: ids.length + (truncated ? 1 : 0),
            pageCount: 1,
            items: ids.map((id) => ({ alias: `live-${id}`, emote: { id } })),
          },
        },
      },
    },
  };
}

function placementsResult(over: Partial<TagPlacementsResult> = {}): TagPlacementsResult {
  return {
    replayed: false,
    recordedCount: 0,
    alreadyRecordedCount: 0,
    notTaggedIds: [],
    discardedStaleIds: [],
    ...over,
  };
}

const EDITABLE: EditableSetResolution = {
  status: 'editable',
  target: {
    emoteSetId: 'set-active',
    setName: 'Main',
    ownerDisplayName: 'HandOfBlood',
    twitchLogin: CHANNEL,
    trackedChannelName: CHANNEL,
    isActiveSet: true,
    ownerTwitchChannelId: 'tw-owner',
  },
};

interface Harness {
  calls: string[];
  request: TagRunRequest;
  active: WritableSignal<string | null>;
  /** Whether the host still shows the request's tag. */
  current: WritableSignal<boolean>;
  pending: WritableSignal<boolean>;
  notice: WritableSignal<TagRunNotice | null>;
  onFeedback: ReturnType<typeof vi.fn>;
  onCompleted: ReturnType<typeof vi.fn>;
  /** The page-level surface the flow falls back to once the host is gone. */
  sink: TagRunNoticeSink;
  sinkEvents: OrphanedTagRunEvent[];
  listEntries: ReturnType<typeof vi.fn>;
  resolveEditableSet: ReturnType<typeof vi.fn>;
  registerOperation: ReturnType<typeof vi.fn>;
  reportPlacements: ReturnType<typeof vi.fn>;
  loadEmoteSetPreview: ReturnType<typeof vi.fn>;
  getSetStatus: ReturnType<typeof vi.fn>;
  httpPost: ReturnType<typeof vi.fn>;
  dialogOpen: ReturnType<typeof vi.fn>;
  startImport: ReturnType<typeof vi.fn>;
  run(): void;
  /** Tears the host down, as an `@if` unmounting `TagRunActions` behind an open dialog would. */
  destroy(): void;
}

function setup(
  options: {
    entries?: Observable<EmoteTagEntries>;
    live?: () => Observable<unknown>;
    resolution?: Observable<EditableSetResolution>;
    registration?: () => Observable<unknown>;
    report?: () => Observable<TagPlacementsResult>;
  } = {},
): Harness {
  const calls: string[] = [];
  const listEntries = vi.fn(() => {
    calls.push('entries');
    return options.entries ?? of(entriesFor(['in-1', 'in-2', 'new-1']));
  });
  const registerOperation = vi.fn(() => {
    calls.push('register');
    return options.registration?.() ?? of({ registeredAtUtc: '2026-10-05T10:00:00Z' });
  });
  const reportPlacements = vi.fn(() => {
    calls.push('report');
    return options.report?.() ?? of(placementsResult());
  });
  const tagService = {
    listEntries,
    registerOperation,
    reportPlacements,
  } as unknown as EmoteTagService;

  const resolveEditableSet = vi.fn(() => {
    calls.push('precheck');
    return options.resolution ?? of(EDITABLE);
  });
  const loadEmoteSetPreview = vi.fn(() => {
    calls.push('preview');
    return of({
      channelName: CHANNEL,
      sevenTvUserId: null,
      emoteSetId: 'set-active',
      emoteSetName: 'Main',
      capacity: 1000,
      totalCount: 2,
      truncated: false,
      emotes: [],
    });
  });
  const emoteSetService = {
    resolveEditableSet,
    loadEmoteSetPreview,
  } as unknown as SevenTvEmoteSetService;

  const getSetStatus = vi.fn(() => new Subject());
  const emoteAdminService = {
    getSetStatus,
    listEmotes: vi.fn(() => of([])),
    getSetWarning: vi.fn(() =>
      of({
        available: true,
        isOwnSet: true,
        otherTrackedChannelsSharingSet: [],
        otherModeratedChannelsSharingSet: [],
      }),
    ),
  } as unknown as EmoteAdminService;

  // Every direct 7TV read: the flow's live read, and the import flow's last re-check before a run.
  const httpPost = vi.fn(() => {
    calls.push('gql');
    return options.live?.() ?? of(setPage(['in-1', 'in-2']));
  });
  const httpClient = { post: httpPost } as unknown as HttpClient;

  const dialogOpen = vi.fn(() => {
    calls.push('dialog');
    return { closed: new Subject<unknown>() };
  });
  const dialog = { open: dialogOpen } as unknown as Dialog;

  const startImport = vi.fn(() => calls.push('startImport'));
  const importStartCheckPending = signal(false);
  const importService = {
    startImport,
    reportTargetCheckBlocked: vi.fn(),
    startCheckPending: importStartCheckPending,
  } as unknown as SevenTvImportService;

  const activeRun = signal<SevenTvRunKind | null>(null);
  const arbiter = {
    activeRun,
    activeClaim: signal(null),
    noteRefusedStart: vi.fn(),
    startLocked: computed(() => activeRun() !== null || importStartCheckPending()),
  } as unknown as SevenTvRunArbiter;

  const active = signal<string | null>('set-active');
  const current = signal(true);
  const pending = signal(false);
  const notice = signal<TagRunNotice | null>(null);
  const onFeedback = vi.fn();
  const onCompleted = vi.fn();
  const host = fakeHost();
  const sink = new TagRunNoticeSink();
  const sinkEvents: OrphanedTagRunEvent[] = [];
  sink.events.subscribe((event) => sinkEvents.push(event));
  const request: TagRunRequest = {
    channelName: CHANNEL,
    tag: TAG,
    setName: 'Main',
    activeEmoteSetId: active,
    pending,
    notice,
    isCurrent: () => current(),
    hostAlive: () => !host.destroyRef.destroyed,
    sink,
    onFeedback,
    onCompleted,
  };
  const deps = {
    dialog,
    emoteAdminService,
    emoteSetService,
    httpClient,
    tokenService: { hasToken: signal(true) } as unknown as SevenTvTokenService,
    importService,
    arbiter,
    deleteService: {} as SevenTvDeleteService,
    tagService,
    translocoService: { translate: (key: string) => key } as unknown as TranslocoService,
    destroyRef: host.destroyRef,
  };

  return {
    calls,
    request,
    active,
    current,
    pending,
    notice,
    onFeedback,
    onCompleted,
    sink,
    sinkEvents,
    listEntries,
    resolveEditableSet,
    registerOperation,
    reportPlacements,
    loadEmoteSetPreview,
    getSetStatus,
    httpPost,
    dialogOpen,
    startImport,
    destroy: host.destroy,
    run: () => startTagPlayInFlow(deps, request),
  };
}

function importDialogData(harness: Harness): ImportConfirmDialogData {
  const call = harness.dialogOpen.mock.calls.find(
    ([component]) => component === ImportConfirmDialog,
  );
  return call![1].data as ImportConfirmDialogData;
}

function importDialogClosed(harness: Harness): Subject<ImportConfirmOutcome | undefined> {
  const index = harness.dialogOpen.mock.calls.findIndex(
    ([component]) => component === ImportConfirmDialog,
  );
  return harness.dialogOpen.mock.results[index].value.closed;
}

/** A `DestroyRef` the test can trigger: its callbacks run, `destroyed` turns true. The host's
 *  `activeEmoteSetId` input is left alone — a destroyed component's input keeps its last value. */
function fakeHost(): { destroyRef: DestroyRef; destroy(): void } {
  const callbacks: (() => void)[] = [];
  let destroyed = false;
  const destroyRef = {
    get destroyed() {
      return destroyed;
    },
    onDestroy: (callback: () => void) => {
      callbacks.push(callback);
      return () => {
        const index = callbacks.indexOf(callback);
        if (index >= 0) {
          callbacks.splice(index, 1);
        }
      };
    },
  } as unknown as DestroyRef;
  return {
    destroyRef,
    destroy: () => {
      destroyed = true;
      for (const callback of [...callbacks]) {
        callback();
      }
    },
  };
}

function httpError(status: number): Observable<never> {
  return throwError(() => new HttpErrorResponse({ status }));
}

describe('startTagPlayInFlow', () => {
  beforeEach(() => {
    vi.spyOn(crypto, 'randomUUID').mockReturnValueOnce(OP_1).mockReturnValueOnce(OP_2);
  });

  afterEach(() => vi.restoreAllMocks());

  describe('set freeze and the steps before the import', () => {
    it("reads the tag's entries for the active set frozen on the click", () => {
      const harness = setup();
      harness.run();

      expect(harness.listEntries).toHaveBeenCalledExactlyOnceWith(CHANNEL, TAG.id, 'set-active');
    });

    it.each([
      ['answers for another set', entriesFor(['in-1'], { emoteSetId: 'set-new' })],
      ['says the set is no longer active', entriesFor(['in-1'], { isActiveSet: false })],
    ])('stops with "set changed" and registers nothing when the entry read %s', (_, answer) => {
      const harness = setup({ entries: of(answer) });
      harness.run();

      expect(harness.notice()).toEqual({ key: 'tags.errors.setChanged' });
      expect(harness.resolveEditableSet).not.toHaveBeenCalled();
      expect(harness.registerOperation).not.toHaveBeenCalled();
      expect(harness.pending()).toBe(false);
    });

    it('checks the set before it registers, and registers with the owner the check resolved', () => {
      const harness = setup();
      harness.run();

      expect(harness.registerOperation).toHaveBeenCalledExactlyOnceWith(CHANNEL, TAG.id, {
        operationId: OP_1,
        kind: 'playIn',
        emoteSetId: 'set-active',
        targetOwnerTwitchId: 'tw-owner',
      });
      expect(harness.calls.indexOf('precheck')).toBeLessThan(harness.calls.indexOf('register'));
    });

    it('registers before the live read, and both before the import confirmation opens', () => {
      const harness = setup();
      harness.run();

      // `preview` is the import flow's own target load, started right before its dialog opens.
      expect(harness.calls).toEqual([
        'entries',
        'precheck',
        'register',
        'gql',
        'preview',
        'dialog',
      ]);
    });

    it('holds pending from the click until the hand-over to the import flow', () => {
      const pendingAtRegistration: boolean[] = [];
      const harness = setup({
        registration: () => {
          pendingAtRegistration.push(harness.pending());
          return of({ registeredAtUtc: '2026-10-05T10:00:00Z' });
        },
      });
      harness.run();

      expect(pendingAtRegistration).toEqual([true]);
      expect(harness.dialogOpen).toHaveBeenCalled();
      expect(harness.pending()).toBe(false);
    });

    it.each([
      ['notEditable', 'tags.errors.target.notEditable', false],
      ['notSelectable', 'tags.errors.target.notSelectable', false],
      ['unavailable', 'tags.errors.target.unavailable', true],
    ] as const)(
      'stops on a %s set with its neutral reason and registers nothing',
      (status, key, retryable) => {
        const harness = setup({ resolution: of({ status }) });
        harness.run();

        expect(harness.notice()?.key).toBe(key);
        expect(harness.notice()?.retry !== undefined).toBe(retryable);
        expect(harness.registerOperation).not.toHaveBeenCalled();
        expect(harness.httpPost).not.toHaveBeenCalled();
      },
    );
  });

  describe('the registration', () => {
    it('stops on a 403 with "no write right" — no live read, no dialog, no retry', () => {
      const harness = setup({ registration: () => httpError(403) });
      harness.run();

      expect(harness.notice()).toEqual({ key: 'tags.errors.noWriteRight' });
      expect(harness.httpPost).not.toHaveBeenCalled();
      expect(harness.dialogOpen).not.toHaveBeenCalled();
      expect(harness.pending()).toBe(false);
    });

    it('stops on a 503 with "ownership unavailable", and its retry starts over', () => {
      let answer: Observable<unknown> = httpError(503);
      const harness = setup({ registration: () => answer });
      harness.run();

      expect(harness.notice()?.key).toBe('tags.errors.ownershipUnavailable');
      expect(harness.httpPost).not.toHaveBeenCalled();

      answer = of({ registeredAtUtc: '2026-10-05T10:01:00Z' });
      harness.notice()!.retry!();

      expect(harness.listEntries).toHaveBeenCalledTimes(2);
      expect(harness.registerOperation.mock.calls[1][2]).toMatchObject({ operationId: OP_2 });
      expect(harness.dialogOpen).toHaveBeenCalled();
      expect(harness.notice()).toBeNull();
    });

    it('stops on any other failure with "registration failed" and a retry', () => {
      const harness = setup({ registration: () => httpError(500) });
      harness.run();

      expect(harness.notice()?.key).toBe('tags.errors.registrationFailed');
      expect(harness.notice()?.retry).toBeTypeOf('function');
      expect(harness.httpPost).not.toHaveBeenCalled();
    });
  });

  describe('the live read', () => {
    it.each([
      ['incomplete', () => of(setPage(['in-1'], true)), 'tags.errors.setReadIncomplete'],
      ['failed', () => throwError(() => new Error('offline')), 'tags.errors.setReadUnavailable'],
    ])('blocks when it is %s — no run, no report', (_, live, key) => {
      const harness = setup({ live });
      harness.run();

      expect(harness.notice()?.key).toBe(key);
      expect(harness.notice()?.retry).toBeTypeOf('function');
      expect(harness.dialogOpen).not.toHaveBeenCalled();
      expect(harness.reportPlacements).not.toHaveBeenCalled();
      expect(harness.pending()).toBe(false);
    });
  });

  describe('a failure that arrives after the host moved on to another tag', () => {
    it.each([
      ['a failed entry read', { entries: throwError(() => new Error('x')) }],
      ['a failed live read', { live: () => throwError(() => new Error('x')) }],
      ['a refused registration', { registration: () => httpError(503) }],
    ])('raises no banner for %s, and ends pending', (_, options) => {
      const harness = setup(options);
      harness.current.set(false);
      harness.run();

      expect(harness.notice()).toBeNull();
      expect(harness.pending()).toBe(false);
    });

    it('raises no banner for a failed report', () => {
      const harness = setup({ entries: of(entriesFor(['in-1'])), report: () => httpError(500) });
      harness.current.set(false);
      harness.run();

      expect(harness.notice()).toBeNull();
      expect(harness.pending()).toBe(false);
    });
  });

  describe('nothing to add', () => {
    it('sends the empty play-in report at once and says every emote is already there', () => {
      const harness = setup({ entries: of(entriesFor(['in-1', 'in-2'])) });
      harness.run();

      expect(harness.dialogOpen).not.toHaveBeenCalled();
      expect(harness.reportPlacements).toHaveBeenCalledExactlyOnceWith(CHANNEL, TAG.id, {
        operationId: OP_1,
        emoteSetId: 'set-active',
        targetOwnerTwitchId: 'tw-owner',
        sevenTvEmoteIds: [],
      });
      expect(harness.onFeedback).toHaveBeenCalledExactlyOnceWith('tags.feedback.allPresent.other', {
        count: 2,
        tag: TAG.name,
      });
      expect(harness.onCompleted).toHaveBeenCalledOnce();
      expect(harness.pending()).toBe(false);
    });

    it('sends no report and says "set changed" when the set was switched before it', () => {
      const live = new Subject<unknown>();
      const harness = setup({ entries: of(entriesFor(['in-1', 'in-2'])), live: () => live });
      harness.run();
      harness.active.set('set-other');
      live.next(setPage(['in-1', 'in-2']));
      live.complete();

      expect(harness.reportPlacements).not.toHaveBeenCalled();
      expect(harness.onFeedback).not.toHaveBeenCalled();
      expect(harness.onCompleted).not.toHaveBeenCalled();
      expect(harness.notice()).toEqual({ key: 'tags.errors.setChanged' });
      expect(harness.pending()).toBe(false);
    });

    it('offers a retry on a failed report that sends the very same report again', () => {
      let answer: Observable<TagPlacementsResult> = httpError(500);
      const harness = setup({ entries: of(entriesFor(['in-1'])), report: () => answer });
      harness.run();

      expect(harness.notice()?.key).toBe('tags.errors.reportFailed');
      expect(harness.onFeedback).not.toHaveBeenCalled();

      // A replay says nothing about the outcome; the feedback still comes from the entries.
      answer = of(placementsResult({ replayed: true }));
      harness.notice()!.retry!();

      expect(harness.reportPlacements).toHaveBeenCalledTimes(2);
      expect(harness.reportPlacements.mock.calls[1]).toEqual(
        harness.reportPlacements.mock.calls[0],
      );
      expect(harness.registerOperation).toHaveBeenCalledOnce();
      expect(harness.onFeedback).toHaveBeenCalledExactlyOnceWith('tags.feedback.allPresent.one', {
        count: 1,
        tag: TAG.name,
      });
      expect(harness.notice()).toBeNull();
    });

    it('puts the failure banner, retry included, into the sink when the host is torn down while the report is out', () => {
      const report = new Subject<TagPlacementsResult>();
      let answer: Observable<TagPlacementsResult> = report;
      const harness = setup({ entries: of(entriesFor(['in-1'])), report: () => answer });
      harness.run();
      expect(harness.reportPlacements).toHaveBeenCalledOnce();

      harness.destroy();
      report.error(new HttpErrorResponse({ status: 500 }));

      expect(harness.notice()).toBeNull();
      const orphaned = harness.sink.notice();
      expect(orphaned?.channelName).toBe(CHANNEL);
      expect(orphaned?.notice.key).toBe('tags.errors.reportFailed');

      // The retry needs nothing from the host: the very same report, and its outcome in the sink.
      answer = of(placementsResult());
      orphaned!.notice.retry!();

      expect(harness.reportPlacements).toHaveBeenCalledTimes(2);
      expect(harness.reportPlacements.mock.calls[1]).toEqual(
        harness.reportPlacements.mock.calls[0],
      );
      expect(harness.sink.notice()).toBeNull();
      expect(harness.onFeedback).not.toHaveBeenCalled();
      expect(harness.onCompleted).not.toHaveBeenCalled();
      expect(harness.sinkEvents).toEqual([
        {
          kind: 'feedback',
          channelName: CHANNEL,
          key: 'tags.feedback.allPresent.one',
          params: { count: 1, tag: TAG.name },
        },
        { kind: 'completed', channelName: CHANNEL },
      ]);
    });

    it('hands a success that lands after the host is gone to the sink, not to the dead outputs', () => {
      const report = new Subject<TagPlacementsResult>();
      const harness = setup({ entries: of(entriesFor(['in-1'])), report: () => report });
      harness.run();

      harness.destroy();
      report.next(placementsResult());
      report.complete();

      expect(harness.onFeedback).not.toHaveBeenCalled();
      expect(harness.onCompleted).not.toHaveBeenCalled();
      expect(harness.sinkEvents.map((event) => event.kind)).toEqual(['feedback', 'completed']);
      expect(harness.sink.notice()).toBeNull();
    });
  });

  describe('the hand-over to the import flow', () => {
    it('opens the import on the frozen set, pinned, with the tag origin and the resolved owner', () => {
      const harness = setup();
      harness.run();

      // Pinned: the set is read by its id (the live-list branch), never via the active-set route.
      expect(harness.loadEmoteSetPreview).toHaveBeenCalledExactlyOnceWith(CHANNEL, 'set-active');
      expect(harness.getSetStatus).not.toHaveBeenCalled();
      const data = importDialogData(harness);
      expect(data.source.origin).toEqual({
        kind: 'tag',
        tagId: TAG.id,
        tagName: TAG.name,
        channelName: CHANNEL,
        alreadyInSetCount: 2,
      });
      expect(data.source.rows.map((row) => row.sevenTvEmoteId)).toEqual(['new-1']);
      expect(data.targetIsActiveSet).toBe(true);
      expect(data.targetOwnerTwitchId).toBe('tw-owner');
      // Only a run with a tag hook may confirm a plan with nothing to add.
      expect(data.emptyConfirmAllowed).toBe(true);
    });

    it('carries the registered operation on the run it starts', () => {
      const harness = setup();
      harness.run();

      importDialogClosed(harness).next({
        targetSetId: 'set-active',
        targetSetName: 'Main',
        plan: {
          rows: [
            {
              action: 'add',
              source: { sevenTvEmoteId: 'new-1', name: 'alias-new-1', imageUrl: null },
              alias: 'alias-new-1',
            },
          ],
        },
      });

      expect(harness.startImport).toHaveBeenCalledOnce();
      expect(harness.startImport.mock.calls[0][0]).toMatchObject({
        setId: 'set-active',
        targetOwnerTwitchId: 'tw-owner',
        tag: { tagId: TAG.id, operationId: OP_1 },
      });
    });

    it('says "set changed" and starts nothing when the active set moved during the confirmation', () => {
      const harness = setup();
      harness.run();

      harness.active.set('set-new');
      importDialogClosed(harness).next({
        targetSetId: 'set-active',
        targetSetName: 'Main',
        plan: { rows: [] },
        nothingToAdd: true,
      });

      expect(harness.notice()).toEqual({ key: 'tags.errors.setChanged' });
      expect(harness.reportPlacements).not.toHaveBeenCalled();
      expect(harness.startImport).not.toHaveBeenCalled();
    });

    it('starts nothing when the host was torn down behind the open import confirmation', () => {
      const harness = setup();
      harness.run();

      // The host's input keeps its last value — the very set the flow froze — so only the host's
      // teardown can tell the guard that nobody vouches for that set any more.
      harness.destroy();
      expect(harness.active()).toBe('set-active');
      importDialogClosed(harness).next({
        targetSetId: 'set-active',
        targetSetName: 'Main',
        plan: {
          rows: [
            {
              action: 'add',
              source: { sevenTvEmoteId: 'new-1', name: 'alias-new-1', imageUrl: null },
              alias: 'alias-new-1',
            },
          ],
        },
      });

      expect(harness.startImport).not.toHaveBeenCalled();
      expect(harness.reportPlacements).not.toHaveBeenCalled();
      // The host's banner went with the host; the page-level sink says it instead.
      expect(harness.notice()).toBeNull();
      expect(harness.sink.notice()).toEqual({
        channelName: CHANNEL,
        notice: { key: 'tags.errors.setChanged' },
      });
    });

    it('sends no empty report either when the host was torn down before nothing was left to add', () => {
      const harness = setup();
      harness.run();

      harness.destroy();
      importDialogClosed(harness).next({
        targetSetId: 'set-active',
        targetSetName: 'Main',
        plan: { rows: [] },
        nothingToAdd: true,
      });

      expect(harness.reportPlacements).not.toHaveBeenCalled();
      expect(harness.startImport).not.toHaveBeenCalled();
      expect(harness.notice()).toBeNull();
      expect(harness.sink.notice()?.notice).toEqual({ key: 'tags.errors.setChanged' });
    });

    it('sends the empty report for the same operation when the import finds nothing left to add', () => {
      const harness = setup();
      harness.run();

      importDialogClosed(harness).next({
        targetSetId: 'set-active',
        targetSetName: 'Main',
        plan: { rows: [] },
        nothingToAdd: true,
      });

      expect(harness.registerOperation).toHaveBeenCalledOnce();
      expect(harness.reportPlacements).toHaveBeenCalledExactlyOnceWith(CHANNEL, TAG.id, {
        operationId: OP_1,
        emoteSetId: 'set-active',
        targetOwnerTwitchId: 'tw-owner',
        sevenTvEmoteIds: [],
      });
      expect(harness.onFeedback).toHaveBeenCalledExactlyOnceWith('tags.feedback.allPresent.other', {
        count: 3,
        tag: TAG.name,
      });
      expect(harness.onCompleted).toHaveBeenCalledOnce();
    });

    it('ends a report that never answers in the failure banner, and its retry sends the same report', () => {
      vi.useFakeTimers();
      try {
        let answer: Observable<TagPlacementsResult> = NEVER;
        const harness = setup({ report: () => answer });
        harness.run();

        importDialogClosed(harness).next({
          targetSetId: 'set-active',
          targetSetName: 'Main',
          plan: { rows: [] },
          nothingToAdd: true,
        });
        vi.advanceTimersByTime(REPORT_TIMEOUT_MS - 1);
        expect(harness.pending()).toBe(true);
        expect(harness.notice()).toBeNull();

        vi.advanceTimersByTime(1);
        expect(harness.pending()).toBe(false);
        expect(harness.notice()?.key).toBe('tags.errors.reportFailed');

        answer = of(placementsResult());
        harness.notice()!.retry!();
        expect(harness.reportPlacements).toHaveBeenCalledTimes(2);
        expect(harness.reportPlacements.mock.calls[1]).toEqual(
          harness.reportPlacements.mock.calls[0],
        );
        expect(harness.reportPlacements.mock.calls[1][2]).toMatchObject({ operationId: OP_1 });
      } finally {
        vi.useRealTimers();
      }
    });

    it('offers a retry with the same operation when that empty report fails', () => {
      let answer: Observable<TagPlacementsResult> = httpError(503);
      const harness = setup({ report: () => answer });
      harness.run();

      importDialogClosed(harness).next({
        targetSetId: 'set-active',
        targetSetName: 'Main',
        plan: { rows: [] },
        nothingToAdd: true,
      });
      expect(harness.notice()?.key).toBe('tags.errors.reportFailed');

      answer = of(placementsResult());
      harness.notice()!.retry!();

      expect(harness.reportPlacements).toHaveBeenCalledTimes(2);
      expect(harness.reportPlacements.mock.calls[1][2]).toMatchObject({ operationId: OP_1 });
      expect(harness.onFeedback).toHaveBeenCalledOnce();
    });

    it('reports nothing back for a dismissed import confirmation', () => {
      const harness = setup();
      harness.run();

      importDialogClosed(harness).next(undefined);

      expect(harness.reportPlacements).not.toHaveBeenCalled();
      expect(harness.notice()).toBeNull();
      expect(harness.pending()).toBe(false);
    });
  });
});

describe('raiseNotice', () => {
  function routing(alive: boolean, current = true) {
    const notice = signal<TagRunNotice | null>(null);
    const sink = new TagRunNoticeSink();
    return {
      notice,
      sink,
      request: {
        channelName: CHANNEL,
        notice,
        isCurrent: () => current,
        hostAlive: () => alive,
        sink,
      },
    };
  }

  it('shows the banner under a live host that still shows the tag', () => {
    const { notice, sink, request } = routing(true);
    raiseNotice(request, { key: 'tags.errors.setChanged' });

    expect(notice()).toEqual({ key: 'tags.errors.setChanged' });
    expect(sink.notice()).toBeNull();
  });

  it('drops it under a live host that moved on to another tag', () => {
    const { notice, sink, request } = routing(true, false);
    raiseNotice(request, { key: 'tags.errors.setChanged' });

    expect(notice()).toBeNull();
    expect(sink.notice()).toBeNull();
  });

  it('hands it to the sink once the host is gone, without a retry that would restart the flow', () => {
    const { notice, sink, request } = routing(false);
    raiseNotice(request, { key: 'tags.errors.entriesUnavailable', retry: () => undefined });

    expect(notice()).toBeNull();
    expect(sink.notice()).toEqual({
      channelName: CHANNEL,
      notice: { key: 'tags.errors.entriesUnavailable' },
    });
  });

  it('keeps a report retry in the sink', () => {
    const { sink, request } = routing(false);
    const retry = (): void => undefined;
    raiseNotice(request, { key: 'tags.errors.reportFailed', retry }, true);

    expect(sink.notice()?.notice.retry).toBe(retry);
  });
});
