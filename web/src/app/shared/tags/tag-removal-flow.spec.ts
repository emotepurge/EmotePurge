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
import { SevenTvRunArbiter, SevenTvRunClaim } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { EmoteTagEntries, EmoteTagEntry, TagRemovalResult } from '../../core/tags/emote-tag.model';
import { EmoteTagService } from '../../core/tags/emote-tag.service';
import { SevenTvTokenPromptDialog } from '../seven-tv/seven-tv-token-prompt-dialog';
import { TagRunRequest } from './tag-play-in-flow';
import {
  TagRemovalConfirmDialog,
  TagRemovalConfirmDialogData,
  TagRemovalConfirmResult,
} from './tag-removal-confirm-dialog';
import {
  confirmTimeEntriesDrift,
  splitOwnPlacements,
  startTagRemovalFlow,
} from './tag-removal-flow';
import { OrphanedTagRunEvent, TagRunNotice, TagRunNoticeSink } from './tag-run-notice-sink';

/*
 * Same approach as the play-in spec: the real `resolveDeleteTarget`, `readLiveSetAliases`,
 * `proposeTagRemoval`, `confirmTimeRefusal` and `toDeleteQueueEmotes` run against faked services,
 * every call that matters is logged in `calls`, and `dialog.open` stands in for the token prompt
 * and the clear-out confirmation (told apart by component).
 */

const CHANNEL = 'handofblood';
const TAG = { id: 7, name: 'Stronghold' };
const OP_1 = '00000000-0000-4000-8000-000000000001';
const OP_2 = '00000000-0000-4000-8000-000000000002';

function entry(id: string, over: Partial<EmoteTagEntry> = {}): EmoteTagEntry {
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
    ...over,
  };
}

function own(id: string, over: Partial<EmoteTagEntry> = {}): EmoteTagEntry {
  return entry(id, {
    placedByThisTag: true,
    placedAtUtc: '2026-10-01T10:00:00Z',
    placementOperationId: `rev-${id}`,
    ...over,
  });
}

/**
 * - `placed`: own placement, proposed (ticked).
 * - `held`: own placement another active tag still needs — not ticked.
 * - `before`: in the set, nobody placed it — not ticked.
 * - `gone`: own placement no longer in the set — no row, but in the snapshot.
 */
const ENTRIES: EmoteTagEntries = {
  emoteSetId: 'set-active',
  isActiveSet: true,
  activationOperationId: 'act-1',
  entries: [
    own('placed'),
    own('held', { heldByActiveTags: [{ id: 9, name: 'Halloween' }] }),
    entry('before'),
    own('gone'),
  ],
};

/** One complete page of a 7TV set read. `before` sits there only under an aliasless entry. */
function setPage(truncated = false) {
  const items = [
    { alias: 'live-placed', emote: { id: 'placed', defaultName: 'Placed' } },
    { alias: 'live-held', emote: { id: 'held', defaultName: 'Held' } },
    { alias: null, emote: { id: 'before', defaultName: 'Before' } },
  ];
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: { totalCount: items.length + (truncated ? 1 : 0), pageCount: 1, items },
        },
      },
    },
  };
}

function removalResult(over: Partial<TagRemovalResult> = {}): TagRemovalResult {
  return {
    replayed: false,
    deletedCount: 0,
    transferredCount: 0,
    droppedCount: 0,
    sweptCount: 0,
    deactivated: true,
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
  active: WritableSignal<string | null>;
  /** Whether the host still shows the request's tag. */
  current: WritableSignal<boolean>;
  hasToken: WritableSignal<boolean>;
  claim: WritableSignal<SevenTvRunClaim | null>;
  pending: WritableSignal<boolean>;
  notice: WritableSignal<TagRunNotice | null>;
  onFeedback: ReturnType<typeof vi.fn>;
  onCompleted: ReturnType<typeof vi.fn>;
  onRunCommitted: ReturnType<typeof vi.fn>;
  /** The page-level surface the flow falls back to once the host is gone. */
  sink: TagRunNoticeSink;
  sinkEvents: OrphanedTagRunEvent[];
  listEntries: ReturnType<typeof vi.fn>;
  registerOperation: ReturnType<typeof vi.fn>;
  reportRemoval: ReturnType<typeof vi.fn>;
  getSetWarning: ReturnType<typeof vi.fn>;
  httpPost: ReturnType<typeof vi.fn>;
  dialogOpen: ReturnType<typeof vi.fn>;
  startDelete: ReturnType<typeof vi.fn>;
  beginConfirmedRun: ReturnType<typeof vi.fn>;
  clearConfirmedRun: ReturnType<typeof vi.fn>;
  endConfirmedRun: ReturnType<typeof vi.fn>;
  noteRefusedStart: ReturnType<typeof vi.fn>;
  /** `SevenTvDeleteService.startCheckPending` (#280). */
  startCheckPending: WritableSignal<boolean>;
  /** `arbiter.startLocked()` as each `startDelete` call saw it. */
  startLockedAtStart: boolean[];
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
    report?: () => Observable<TagRemovalResult>;
    /** The entry read at confirm time (every read after the first); default: the first one's. */
    reread?: () => Observable<EmoteTagEntries>;
    /** The host grid's marking at the click. */
    markedIds?: string[];
  } = {},
): Harness {
  const calls: string[] = [];
  let entryReads = 0;
  const listEntries = vi.fn(() => {
    calls.push('entries');
    entryReads++;
    if (entryReads > 1 && options.reread !== undefined) {
      return options.reread();
    }
    return options.entries ?? of(ENTRIES);
  });
  const registerOperation = vi.fn(() => {
    calls.push('register');
    return options.registration?.() ?? of({ registeredAtUtc: '2026-10-05T10:00:00Z' });
  });
  const reportRemoval = vi.fn(() => {
    calls.push('report');
    return options.report?.() ?? of(removalResult());
  });
  const tagService = {
    listEntries,
    registerOperation,
    reportRemoval,
  } as unknown as EmoteTagService;

  const resolveEditableSet = vi.fn(() => {
    calls.push('precheck');
    return options.resolution ?? of(EDITABLE);
  });
  const emoteSetService = { resolveEditableSet } as unknown as SevenTvEmoteSetService;

  const getSetWarning = vi.fn(() => {
    calls.push('warning');
    return of({
      available: true,
      isOwnSet: true,
      otherTrackedChannelsSharingSet: [],
      otherModeratedChannelsSharingSet: [],
    });
  });
  const emoteAdminService = { getSetWarning } as unknown as EmoteAdminService;

  const httpPost = vi.fn(() => {
    calls.push('gql');
    return options.live?.() ?? of(setPage());
  });
  const httpClient = { post: httpPost } as unknown as HttpClient;

  const dialogOpen = vi.fn((component: unknown) => {
    calls.push(component === SevenTvTokenPromptDialog ? 'tokenPrompt' : 'dialog');
    return { closed: new Subject<unknown>() };
  });
  const dialog = { open: dialogOpen } as unknown as Dialog;

  const claim = signal<SevenTvRunClaim | null>(null);
  const startCheckPending = signal(false);
  const noteRefusedStart = vi.fn();
  // As the real arbiter derives it: a run's claim or a participant's start check.
  const startLocked = computed(() => claim() !== null || startCheckPending());
  const arbiter = {
    activeClaim: claim,
    activeRun: computed(() => claim()?.kind ?? null),
    startLocked,
    noteRefusedStart,
  } as unknown as SevenTvRunArbiter;

  const startLockedAtStart: boolean[] = [];
  const startDelete = vi.fn(() => {
    startLockedAtStart.push(startLocked());
    calls.push('startDelete');
  });
  const beginConfirmedRun = vi.fn(() => calls.push('beginClaim'));
  const clearConfirmedRun = vi.fn(() => calls.push('clearClaim'));
  const endConfirmedRun = vi.fn(() => calls.push('endClaim'));
  const deleteService = {
    startDelete,
    beginConfirmedRun,
    clearConfirmedRun,
    endConfirmedRun,
    startCheckPending,
  } as unknown as SevenTvDeleteService;

  const hasToken = signal(true);
  const active = signal<string | null>('set-active');
  const current = signal(true);
  const pending = signal(false);
  const notice = signal<TagRunNotice | null>(null);
  const onFeedback = vi.fn();
  const onCompleted = vi.fn();
  const onRunCommitted = vi.fn();
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
    markedIds: options.markedIds,
    onRunCommitted,
  };
  const deps = {
    dialog,
    emoteAdminService,
    emoteSetService,
    httpClient,
    tokenService: { hasToken } as unknown as SevenTvTokenService,
    importService: {} as SevenTvImportService,
    arbiter,
    deleteService,
    tagService,
    translocoService: { translate: (key: string) => `«${key}»` } as unknown as TranslocoService,
    destroyRef: host.destroyRef,
  };

  return {
    calls,
    active,
    current,
    hasToken,
    claim,
    pending,
    notice,
    onFeedback,
    onCompleted,
    onRunCommitted,
    sink,
    sinkEvents,
    listEntries,
    registerOperation,
    reportRemoval,
    getSetWarning,
    httpPost,
    dialogOpen,
    startDelete,
    beginConfirmedRun,
    clearConfirmedRun,
    endConfirmedRun,
    noteRefusedStart,
    startCheckPending,
    startLockedAtStart,
    destroy: host.destroy,
    run: () => startTagRemovalFlow(deps, request),
  };
}

function dialogCount(harness: Harness): number {
  return harness.dialogOpen.mock.calls.filter(([opened]) => opened === TagRemovalConfirmDialog)
    .length;
}

function dialogIndex(harness: Harness, component: unknown): number {
  return harness.dialogOpen.mock.calls.findIndex(([opened]) => opened === component);
}

function confirmationData(harness: Harness): TagRemovalConfirmDialogData {
  return harness.dialogOpen.mock.calls[dialogIndex(harness, TagRemovalConfirmDialog)][1]
    .data as TagRemovalConfirmDialogData;
}

function confirmationClosed(harness: Harness): Subject<TagRemovalConfirmResult | undefined> {
  return harness.dialogOpen.mock.results[dialogIndex(harness, TagRemovalConfirmDialog)].value
    .closed;
}

function tokenPromptClosed(harness: Harness): Subject<boolean> {
  return harness.dialogOpen.mock.results[dialogIndex(harness, SevenTvTokenPromptDialog)].value
    .closed;
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

describe('startTagRemovalFlow', () => {
  beforeEach(() => {
    vi.spyOn(crypto, 'randomUUID').mockReturnValueOnce(OP_1).mockReturnValueOnce(OP_2);
  });

  afterEach(() => vi.restoreAllMocks());

  describe('the steps before the confirmation', () => {
    it.each([
      ['answers for another set', { ...ENTRIES, emoteSetId: 'set-new' }],
      ['says the set is no longer active', { ...ENTRIES, isActiveSet: false }],
    ])('stops with "set changed" and registers nothing when the entry read %s', (_, answer) => {
      const harness = setup({ entries: of(answer) });
      harness.run();

      expect(harness.listEntries).toHaveBeenCalledExactlyOnceWith(CHANNEL, TAG.id, 'set-active');
      expect(harness.notice()).toEqual({ key: 'tags.errors.setChanged' });
      expect(harness.registerOperation).not.toHaveBeenCalled();
      expect(harness.pending()).toBe(false);
    });

    it('registers a removal with the resolved owner, before the live read and the dialog', () => {
      const harness = setup();
      harness.run();

      expect(harness.registerOperation).toHaveBeenCalledExactlyOnceWith(CHANNEL, TAG.id, {
        operationId: OP_1,
        kind: 'removal',
        emoteSetId: 'set-active',
        targetOwnerTwitchId: 'tw-owner',
      });
      expect(harness.calls).toEqual([
        'entries',
        'precheck',
        'register',
        'gql',
        'warning',
        'beginClaim',
        'dialog',
      ]);
    });

    it('asks for a missing token after the registration and before the live read', () => {
      const harness = setup();
      harness.hasToken.set(false);
      harness.run();

      expect(harness.calls).toEqual(['entries', 'precheck', 'register', 'tokenPrompt']);
      tokenPromptClosed(harness).next(true);

      expect(harness.calls.slice(4)).toEqual(['gql', 'warning', 'beginClaim', 'dialog']);
    });

    it('stops quietly when the token prompt is cancelled — the registration stays, nothing else goes out', () => {
      const harness = setup();
      harness.hasToken.set(false);
      harness.run();

      tokenPromptClosed(harness).next(false);

      expect(harness.registerOperation).toHaveBeenCalledOnce();
      expect(harness.httpPost).not.toHaveBeenCalled();
      expect(dialogIndex(harness, TagRemovalConfirmDialog)).toBe(-1);
      expect(harness.reportRemoval).not.toHaveBeenCalled();
      expect(harness.notice()).toBeNull();
      expect(harness.pending()).toBe(false);
    });

    it('stops on a 403 with "no write right" before any token prompt, read or dialog', () => {
      const harness = setup({ registration: () => httpError(403) });
      harness.hasToken.set(false);
      harness.run();

      expect(harness.notice()).toEqual({ key: 'tags.errors.noWriteRight' });
      expect(harness.dialogOpen).not.toHaveBeenCalled();
      expect(harness.httpPost).not.toHaveBeenCalled();
    });

    it('stops on a 503 with "ownership unavailable" and a retry that starts over', () => {
      let answer: Observable<unknown> = httpError(503);
      const harness = setup({ registration: () => answer });
      harness.run();

      expect(harness.notice()?.key).toBe('tags.errors.ownershipUnavailable');
      answer = of({ registeredAtUtc: '2026-10-05T10:01:00Z' });
      harness.notice()!.retry!();

      expect(harness.registerOperation.mock.calls[1][2]).toMatchObject({ operationId: OP_2 });
      expect(dialogIndex(harness, TagRemovalConfirmDialog)).not.toBe(-1);
    });

    it.each([
      ['notEditable', 'massDelete.errors.targetNotEditable'],
      ['notSelectable', 'massDelete.errors.targetNotSelectable'],
      ['unavailable', 'massDelete.errors.targetCheckUnavailable'],
    ] as const)(
      'stops on a %s set with the delete wording and registers nothing',
      (status, key) => {
        const harness = setup({ resolution: of({ status }) });
        harness.run();

        expect(harness.notice()?.key).toBe(key);
        expect(harness.registerOperation).not.toHaveBeenCalled();
      },
    );

    it.each([
      ['incomplete', () => of(setPage(true)), 'tags.errors.setReadIncomplete'],
      ['failed', () => throwError(() => new Error('offline')), 'tags.errors.setReadUnavailable'],
    ])('blocks when the live read is %s — no dialog, no claim', (_, live, key) => {
      const harness = setup({ live });
      harness.run();

      expect(harness.notice()?.key).toBe(key);
      expect(harness.dialogOpen).not.toHaveBeenCalled();
      expect(harness.beginConfirmedRun).not.toHaveBeenCalled();
      expect(harness.pending()).toBe(false);
    });

    it('hands the proposal, the set and the live shared-set warning to the dialog', () => {
      const harness = setup();
      harness.run();

      const data = confirmationData(harness);
      expect(harness.getSetWarning).toHaveBeenCalledExactlyOnceWith(CHANNEL, 'set-active');
      expect(data.tagName).toBe(TAG.name);
      expect(data.setName).toBe('Main');
      expect(data.warningLoading()).toBe(false);
      expect(data.proposal.rows.map((row) => [row.sevenTvEmoteId, row.checked])).toEqual([
        ['placed', true],
        ['held', false],
        ['before', false],
      ]);
      expect(data.proposal.ownInLiveIds).toEqual(['placed', 'held']);
      expect(harness.pending()).toBe(true);
    });

    it('drops the claim and ends pending when the confirmation is dismissed', () => {
      const harness = setup();
      harness.run();

      confirmationClosed(harness).next(undefined);

      expect(harness.clearConfirmedRun).toHaveBeenCalledOnce();
      expect(harness.startDelete).not.toHaveBeenCalled();
      expect(harness.reportRemoval).not.toHaveBeenCalled();
      expect(harness.pending()).toBe(false);
    });
  });

  describe('a failure that arrives after the host moved on to another tag', () => {
    it('raises no banner for a failed entry read, and ends pending', () => {
      const entries = new Subject<EmoteTagEntries>();
      const harness = setup({ entries });
      harness.run();
      harness.current.set(false);
      entries.error(new Error('offline'));

      expect(harness.notice()).toBeNull();
      expect(harness.pending()).toBe(false);
    });

    it('raises no abort banner for a confirmation behind a switched set', () => {
      const harness = setup();
      harness.run();
      harness.current.set(false);
      harness.active.set('set-other');
      confirmationClosed(harness).next({ checkedIds: [] });

      expect(harness.notice()).toBeNull();
      expect(harness.pending()).toBe(false);
      expect(harness.startDelete).not.toHaveBeenCalled();
    });
  });

  describe('confirmed with nothing ticked', () => {
    it('sends the removal report itself, keeping every own placement in the set', () => {
      const harness = setup();
      harness.run();

      confirmationClosed(harness).next({ checkedIds: [] });

      expect(harness.startDelete).not.toHaveBeenCalled();
      expect(harness.reportRemoval).toHaveBeenCalledExactlyOnceWith(CHANNEL, TAG.id, {
        operationId: OP_1,
        emoteSetId: 'set-active',
        targetOwnerTwitchId: 'tw-owner',
        activationOperationId: 'act-1',
        snapshot: [
          { sevenTvEmoteId: 'placed', placementOperationId: 'rev-placed' },
          { sevenTvEmoteId: 'held', placementOperationId: 'rev-held' },
          { sevenTvEmoteId: 'gone', placementOperationId: 'rev-gone' },
        ],
        removedIds: [],
        keptIds: ['placed', 'held'],
      });
      expect(harness.onFeedback).toHaveBeenCalledExactlyOnceWith('tags.feedback.removedNothing', {
        tag: TAG.name,
      });
      expect(harness.onCompleted).toHaveBeenCalledOnce();
      expect(harness.clearConfirmedRun).toHaveBeenCalledOnce();
      expect(harness.pending()).toBe(false);
    });

    it('offers a retry on a failed report that sends the same operation again', () => {
      let answer: Observable<TagRemovalResult> = httpError(500);
      const harness = setup({ report: () => answer });
      harness.run();
      confirmationClosed(harness).next({ checkedIds: [] });

      expect(harness.notice()?.key).toBe('tags.errors.reportFailed');
      expect(harness.onFeedback).not.toHaveBeenCalled();

      // A replay carries no outcome — the feedback does not depend on it.
      answer = of(removalResult({ replayed: true, deactivated: false }));
      harness.notice()!.retry!();

      expect(harness.reportRemoval).toHaveBeenCalledTimes(2);
      expect(harness.reportRemoval.mock.calls[1]).toEqual(harness.reportRemoval.mock.calls[0]);
      expect(harness.registerOperation).toHaveBeenCalledOnce();
      expect(harness.onFeedback).toHaveBeenCalledOnce();
      expect(harness.notice()).toBeNull();
    });
    it('ends a report that never answers in the failure banner, and its retry sends the same operation', () => {
      vi.useFakeTimers();
      try {
        let answer: Observable<TagRemovalResult> = NEVER;
        const harness = setup({ report: () => answer });
        harness.run();
        confirmationClosed(harness).next({ checkedIds: [] });
        vi.advanceTimersByTime(REPORT_TIMEOUT_MS - 1);
        expect(harness.pending()).toBe(true);

        vi.advanceTimersByTime(1);
        expect(harness.pending()).toBe(false);
        expect(harness.notice()?.key).toBe('tags.errors.reportFailed');

        answer = of(removalResult());
        harness.notice()!.retry!();
        expect(harness.reportRemoval).toHaveBeenCalledTimes(2);
        expect(harness.reportRemoval.mock.calls[1]).toEqual(harness.reportRemoval.mock.calls[0]);
      } finally {
        vi.useRealTimers();
      }
    });
  });

  describe('confirmed with ticked rows', () => {
    it('starts the delete with the ticked rows and the tag context, then ends the claim', () => {
      const harness = setup();
      harness.run();

      // `held` stays unticked (an own placement kept); `before` was already there and is ticked.
      confirmationClosed(harness).next({ checkedIds: ['placed', 'before'] });

      expect(harness.reportRemoval).not.toHaveBeenCalled();
      expect(harness.startDelete).toHaveBeenCalledExactlyOnceWith(
        'set-active',
        CHANNEL,
        [
          {
            emoteId: undefined,
            sevenTvEmoteId: 'placed',
            name: 'live-placed',
            aliases: ['live-placed'],
          },
          // Aliasless only: no live alias, so the row's display name stands in for the entry.
          {
            emoteId: undefined,
            sevenTvEmoteId: 'before',
            name: 'alias-before',
            aliases: ['alias-before'],
          },
        ],
        CHANNEL,
        'tw-owner',
        {
          tagId: TAG.id,
          operationId: OP_1,
          activationOperationId: 'act-1',
          snapshot: [
            { sevenTvEmoteId: 'placed', placementOperationId: 'rev-placed' },
            { sevenTvEmoteId: 'held', placementOperationId: 'rev-held' },
            { sevenTvEmoteId: 'gone', placementOperationId: 'rev-gone' },
          ],
          checkedOwnIds: ['placed'],
          uncheckedOwnIds: ['held'],
          channelName: CHANNEL,
        },
      );
      expect(harness.calls.slice(-2)).toEqual(['startDelete', 'endClaim']);
      expect(harness.pending()).toBe(false);
    });

    // Operator decision 2026-10-05: a tag whose emotes were all in the set already was never played
    // in, and must still be clearable.
    it('clears out a tag that is not played in: everything in the set but what another active tag needs is proposed, and the context carries no activation and no placements', () => {
      const harness = setup({
        entries: of({
          ...ENTRIES,
          activationOperationId: null,
          entries: [
            entry('placed'),
            entry('held', { heldByActiveTags: [{ id: 9, name: 'Halloween' }] }),
            entry('before'),
          ],
        }),
      });
      harness.run();

      const proposal = confirmationData(harness).proposal;
      expect(proposal.tagActive).toBe(false);
      expect(proposal.rows.map((row) => [row.sevenTvEmoteId, row.checked, row.reason])).toEqual([
        ['placed', true, 'tagged'],
        ['held', false, 'heldBy'],
        ['before', true, 'tagged'],
      ]);
      confirmationClosed(harness).next({
        checkedIds: proposal.rows.filter((row) => row.checked).map((row) => row.sevenTvEmoteId),
      });

      expect(harness.startDelete).toHaveBeenCalledExactlyOnceWith(
        'set-active',
        CHANNEL,
        [
          expect.objectContaining({ sevenTvEmoteId: 'placed' }),
          expect.objectContaining({ sevenTvEmoteId: 'before' }),
        ],
        CHANNEL,
        'tw-owner',
        {
          tagId: TAG.id,
          operationId: OP_1,
          activationOperationId: null,
          snapshot: [],
          checkedOwnIds: [],
          uncheckedOwnIds: [],
          channelName: CHANNEL,
        },
      );
    });

    describe('the entry read right before the delete (R4: a holder appearing behind the dialog)', () => {
      /** The tag of the operator's case: not played in, so every emote of it in the set is ticked. */
      const INACTIVE: EmoteTagEntries = {
        ...ENTRIES,
        activationOperationId: null,
        entries: [entry('placed'), entry('before')],
      };

      it('reads the entries again after the confirmation and starts the delete when nothing changed', () => {
        const harness = setup({ entries: of(INACTIVE) });
        harness.run();

        confirmationClosed(harness).next({ checkedIds: ['placed', 'before'] });

        expect(harness.listEntries).toHaveBeenCalledTimes(2);
        expect(harness.listEntries).toHaveBeenLastCalledWith(CHANNEL, TAG.id, 'set-active');
        expect(harness.calls.slice(-3)).toEqual(['entries', 'startDelete', 'endClaim']);
        expect(harness.notice()).toBeNull();
        expect(harness.pending()).toBe(false);
      });

      it('aborts with nothing deleted when a ticked emote is held by another active tag by now, and its retry opens the clear-out afresh', () => {
        const harness = setup({
          entries: of(INACTIVE),
          // Another tab played in "Halloween", which has an entry for `before`.
          reread: () =>
            of({
              ...INACTIVE,
              entries: [
                entry('placed'),
                entry('before', { heldByActiveTags: [{ id: 9, name: 'Halloween' }] }),
              ],
            }),
        });
        harness.run();

        confirmationClosed(harness).next({ checkedIds: ['placed', 'before'] });

        expect(harness.startDelete).not.toHaveBeenCalled();
        expect(harness.reportRemoval).not.toHaveBeenCalled();
        expect(harness.notice()).toEqual({
          leadKey: 'massDelete.nothingDeleted',
          key: 'tags.errors.changedDuringConfirm',
          retry: expect.any(Function),
        });
        expect(harness.endConfirmedRun).toHaveBeenCalledOnce();
        expect(harness.pending()).toBe(false);

        harness.notice()!.retry!();
        expect(dialogCount(harness)).toBe(2);
      });

      it('aborts as well for a played-in tag when a ticked own placement gained a holder', () => {
        const harness = setup({
          reread: () =>
            of({
              ...ENTRIES,
              entries: ENTRIES.entries.map((current) =>
                current.sevenTvEmoteId === 'placed'
                  ? { ...current, placedByOtherTags: [{ id: 11, name: 'Lieblinge' }] }
                  : current,
              ),
            }),
        });
        harness.run();

        confirmationClosed(harness).next({ checkedIds: ['placed'] });

        expect(harness.startDelete).not.toHaveBeenCalled();
        expect(harness.notice()?.key).toBe('tags.errors.changedDuringConfirm');
      });

      it('lets a row through whose holder the dialog already showed — the person ticked it knowingly', () => {
        const harness = setup();
        harness.run();

        confirmationClosed(harness).next({ checkedIds: ['placed', 'held'] });

        expect(harness.startDelete).toHaveBeenCalledOnce();
        expect(harness.notice()).toBeNull();
      });

      it('aborts when the set moved behind the re-read, with the set-switch notice', () => {
        const reread = new Subject<EmoteTagEntries>();
        const harness = setup({ entries: of(INACTIVE), reread: () => reread });
        harness.run();

        confirmationClosed(harness).next({ checkedIds: ['placed'] });
        expect(harness.pending()).toBe(true);
        harness.active.set('set-new');
        reread.next(INACTIVE);

        expect(harness.startDelete).not.toHaveBeenCalled();
        expect(harness.notice()).toEqual({
          leadKey: 'massDelete.abortedByLock',
          key: 'massDelete.setChangedDuringConfirm',
        });
        expect(harness.endConfirmedRun).toHaveBeenCalledOnce();
        expect(harness.pending()).toBe(false);
      });

      it('fails closed when the re-read fails: nothing deleted, a retry that opens it again', () => {
        const harness = setup({
          entries: of(INACTIVE),
          reread: () => throwError(() => new HttpErrorResponse({ status: 500 })),
        });
        harness.run();

        confirmationClosed(harness).next({ checkedIds: ['placed', 'before'] });

        expect(harness.startDelete).not.toHaveBeenCalled();
        expect(harness.notice()).toEqual({
          leadKey: 'massDelete.nothingDeleted',
          key: 'tags.errors.entriesUnavailable',
          retry: expect.any(Function),
        });
        expect(harness.endConfirmedRun).toHaveBeenCalledOnce();
        expect(harness.pending()).toBe(false);
      });

      it('fails closed when the re-read answers malformed: nothing deleted, claim and pending released', () => {
        const harness = setup({
          entries: of(INACTIVE),
          reread: () => of({ ...INACTIVE, entries: null } as unknown as EmoteTagEntries),
        });
        harness.run();

        confirmationClosed(harness).next({ checkedIds: ['placed'] });

        expect(harness.startDelete).not.toHaveBeenCalled();
        expect(harness.notice()?.key).toBe('tags.errors.entriesUnavailable');
        expect(harness.endConfirmedRun).toHaveBeenCalledOnce();
        expect(harness.pending()).toBe(false);
      });

      it('fails closed when the re-read never answers', () => {
        vi.useFakeTimers();
        try {
          const harness = setup({ entries: of(INACTIVE), reread: () => NEVER });
          harness.run();

          confirmationClosed(harness).next({ checkedIds: ['placed'] });
          vi.advanceTimersByTime(REPORT_TIMEOUT_MS);

          expect(harness.startDelete).not.toHaveBeenCalled();
          expect(harness.notice()?.key).toBe('tags.errors.entriesUnavailable');
          expect(harness.pending()).toBe(false);
        } finally {
          vi.useRealTimers();
        }
      });

      describe('holds the delete start check (#280) while the re-read is out', () => {
        it('sets it once the confirmation closed and keeps the tag buttons locked meanwhile', () => {
          const reread = new Subject<EmoteTagEntries>();
          const harness = setup({ entries: of(INACTIVE), reread: () => reread });
          harness.run();
          expect(harness.startCheckPending()).toBe(false);

          confirmationClosed(harness).next({ checkedIds: ['placed'] });

          expect(harness.startCheckPending()).toBe(true);
          expect(harness.pending()).toBe(true);
        });

        it('starts the delete while it is still set — it never refuses its own start — and releases it after', () => {
          const reread = new Subject<EmoteTagEntries>();
          const harness = setup({ entries: of(INACTIVE), reread: () => reread });
          harness.run();
          confirmationClosed(harness).next({ checkedIds: ['placed'] });

          reread.next(INACTIVE);
          reread.complete();

          expect(harness.startDelete).toHaveBeenCalledOnce();
          expect(harness.startLockedAtStart).toEqual([true]);
          expect(harness.notice()).toBeNull();
          expect(harness.startCheckPending()).toBe(false);
        });

        it('releases it when the re-read finds the set switched', () => {
          const harness = setup({
            entries: of(INACTIVE),
            reread: () => of({ ...INACTIVE, isActiveSet: false }),
          });
          harness.run();
          confirmationClosed(harness).next({ checkedIds: ['placed'] });

          expect(harness.notice()?.key).toBe('massDelete.setChangedDuringConfirm');
          expect(harness.startCheckPending()).toBe(false);
        });

        it('releases it when the re-read finds the state changed', () => {
          const harness = setup({
            entries: of(INACTIVE),
            reread: () => of({ ...INACTIVE, entries: [entry('before')] }),
          });
          harness.run();
          confirmationClosed(harness).next({ checkedIds: ['placed'] });

          expect(harness.notice()?.key).toBe('tags.errors.changedDuringConfirm');
          expect(harness.startCheckPending()).toBe(false);
        });

        it('releases it when the re-read fails', () => {
          const harness = setup({
            entries: of(INACTIVE),
            reread: () => throwError(() => new HttpErrorResponse({ status: 500 })),
          });
          harness.run();
          confirmationClosed(harness).next({ checkedIds: ['placed'] });

          expect(harness.notice()?.key).toBe('tags.errors.entriesUnavailable');
          expect(harness.startCheckPending()).toBe(false);
        });

        it('releases it when the re-read times out', () => {
          vi.useFakeTimers();
          try {
            const harness = setup({ entries: of(INACTIVE), reread: () => NEVER });
            harness.run();
            confirmationClosed(harness).next({ checkedIds: ['placed'] });
            expect(harness.startCheckPending()).toBe(true);

            vi.advanceTimersByTime(REPORT_TIMEOUT_MS);

            expect(harness.notice()?.key).toBe('tags.errors.entriesUnavailable');
            expect(harness.startCheckPending()).toBe(false);
          } finally {
            vi.useRealTimers();
          }
        });

        it('is never set for a confirmation with nothing ticked — that one has no read', () => {
          const harness = setup();
          const seen: boolean[] = [];
          harness.reportRemoval.mockImplementation(() => {
            seen.push(harness.startCheckPending());
            return of(removalResult());
          });
          harness.run();

          confirmationClosed(harness).next({ checkedIds: [] });

          expect(seen).toEqual([false]);
          expect(harness.startCheckPending()).toBe(false);
        });
      });

      it('makes no second read when nothing is ticked — nothing goes to 7TV', () => {
        const harness = setup();
        harness.run();

        confirmationClosed(harness).next({ checkedIds: [] });

        expect(harness.listEntries).toHaveBeenCalledOnce();
        expect(harness.reportRemoval).toHaveBeenCalledOnce();
      });
    });

    it('aborts when the active set moved behind the open dialog', () => {
      const harness = setup();
      harness.run();

      harness.active.set('set-new');
      confirmationClosed(harness).next({ checkedIds: ['placed'] });

      expect(harness.notice()).toEqual({
        leadKey: 'massDelete.abortedByLock',
        key: 'massDelete.setChangedDuringConfirm',
      });
      expect(harness.startDelete).not.toHaveBeenCalled();
      expect(harness.reportRemoval).not.toHaveBeenCalled();
      expect(harness.endConfirmedRun).toHaveBeenCalledOnce();
      expect(harness.pending()).toBe(false);
    });

    it('deletes nothing when the host was torn down behind the open dialog', () => {
      const harness = setup();
      harness.run();

      // The host's input keeps its last value — the very set the flow froze — so only the host's
      // teardown can tell the guard that nobody vouches for that set any more.
      harness.destroy();
      expect(harness.active()).toBe('set-active');
      confirmationClosed(harness).next({ checkedIds: ['placed'] });

      expect(harness.startDelete).not.toHaveBeenCalled();
      expect(harness.reportRemoval).not.toHaveBeenCalled();
      // The abort is said on the page, not under the buttons that went with the host.
      expect(harness.notice()).toBeNull();
      expect(harness.sink.notice()).toEqual({
        channelName: CHANNEL,
        notice: {
          leadKey: 'massDelete.abortedByLock',
          key: 'massDelete.setChangedDuringConfirm',
        },
      });
      expect(harness.endConfirmedRun).toHaveBeenCalledOnce();
      expect(harness.pending()).toBe(false);
    });

    it('sends no report for nothing ticked either when the host was torn down behind the dialog', () => {
      const harness = setup();
      harness.run();

      harness.destroy();
      confirmationClosed(harness).next({ checkedIds: [] });

      expect(harness.reportRemoval).not.toHaveBeenCalled();
      expect(harness.startDelete).not.toHaveBeenCalled();
      expect(harness.notice()).toBeNull();
      expect(harness.sink.notice()?.notice.key).toBe('massDelete.setChangedDuringConfirm');
    });

    it('puts the failure of a report for nothing ticked into the sink, retry included, when the host goes while it is out', () => {
      const report = new Subject<TagRemovalResult>();
      let answer: Observable<TagRemovalResult> = report;
      const harness = setup({ report: () => answer });
      harness.run();
      confirmationClosed(harness).next({ checkedIds: [] });
      expect(harness.reportRemoval).toHaveBeenCalledOnce();

      harness.destroy();
      report.error(new HttpErrorResponse({ status: 503 }));

      expect(harness.notice()).toBeNull();
      const orphaned = harness.sink.notice();
      expect(orphaned?.notice.key).toBe('tags.errors.reportFailed');

      answer = of(removalResult());
      orphaned!.notice.retry!();

      expect(harness.reportRemoval).toHaveBeenCalledTimes(2);
      expect(harness.reportRemoval.mock.calls[1]).toEqual(harness.reportRemoval.mock.calls[0]);
      expect(harness.onFeedback).not.toHaveBeenCalled();
      expect(harness.onCompleted).not.toHaveBeenCalled();
      expect(harness.sinkEvents).toEqual([
        {
          kind: 'feedback',
          channelName: CHANNEL,
          key: 'tags.feedback.removedNothing',
          params: { tag: TAG.name },
        },
        { kind: 'completed', channelName: CHANNEL },
      ]);
    });

    it("refuses a start while another run holds the arbiter, with the delete chain's notice and no noteRefusedStart", () => {
      const harness = setup();
      harness.run();

      harness.claim.set({ kind: 'import', phase: 'running' });
      confirmationClosed(harness).next({ checkedIds: ['placed'] });

      expect(harness.notice()).toEqual({
        leadKey: 'massDelete.nothingDeleted',
        key: 'sevenTvRun.notStarted.running',
        params: { kind: '«sevenTvRun.kind.import»' },
      });
      expect(harness.noteRefusedStart).not.toHaveBeenCalled();
      expect(harness.startDelete).not.toHaveBeenCalled();
      expect(harness.endConfirmedRun).toHaveBeenCalledOnce();
    });

    it('refuses a start when the token went away behind the open dialog', () => {
      const harness = setup();
      harness.run();

      harness.hasToken.set(false);
      confirmationClosed(harness).next({ checkedIds: ['placed'] });

      expect(harness.notice()).toEqual({
        leadKey: 'massDelete.nothingDeleted',
        key: 'massDelete.tokenGoneDuringConfirm',
      });
      expect(harness.startDelete).not.toHaveBeenCalled();
    });

    it('applies the same confirm-time checks to a confirmation with nothing ticked', () => {
      const harness = setup();
      harness.run();

      harness.active.set('set-new');
      confirmationClosed(harness).next({ checkedIds: [] });

      expect(harness.reportRemoval).not.toHaveBeenCalled();
      expect(harness.notice()?.key).toBe('massDelete.setChangedDuringConfirm');
    });

    it('refuses a confirmation with nothing ticked while another run holds the arbiter', () => {
      const harness = setup();
      harness.run();

      harness.claim.set({ kind: 'import', phase: 'running' });
      confirmationClosed(harness).next({ checkedIds: [] });

      expect(harness.reportRemoval).not.toHaveBeenCalled();
      expect(harness.notice()?.key).toBe('sevenTvRun.notStarted.running');
    });

    it('sends the report for nothing ticked even when the token went away — it writes nothing to 7TV', () => {
      const harness = setup();
      harness.run();

      harness.hasToken.set(false);
      confirmationClosed(harness).next({ checkedIds: [] });

      expect(harness.reportRemoval).toHaveBeenCalledOnce();
      expect(harness.reportRemoval.mock.calls[0][2]).toMatchObject({
        operationId: OP_1,
        removedIds: [],
        keptIds: ['placed', 'held'],
      });
      expect(harness.onFeedback).toHaveBeenCalledExactlyOnceWith('tags.feedback.removedNothing', {
        tag: TAG.name,
      });
      expect(harness.onCompleted).toHaveBeenCalledOnce();
      expect(harness.notice()).toBeNull();
      expect(harness.startDelete).not.toHaveBeenCalled();
    });
  });

  // Operator decision 2026-10-05: a marking on the tags page grid is the proposal.
  describe('with a grid marking', () => {
    it('proposes the marked emotes, and the re-read and the run treat the ticks as always', () => {
      const harness = setup({ markedIds: ['before', 'gone'] });
      harness.run();

      const proposal = confirmationData(harness).proposal;
      expect(proposal.fromMarking).toBe(true);
      expect(proposal.rows.map((row) => [row.sevenTvEmoteId, row.checked, row.reason])).toEqual([
        ['placed', false, 'notMarked'],
        ['held', false, 'heldBy'],
        ['before', true, 'alreadyPresent'],
      ]);
      confirmationClosed(harness).next({ checkedIds: ['before'] });

      expect(harness.listEntries).toHaveBeenCalledTimes(2);
      expect(harness.startDelete).toHaveBeenCalledExactlyOnceWith(
        'set-active',
        CHANNEL,
        [expect.objectContaining({ sevenTvEmoteId: 'before' })],
        CHANNEL,
        'tw-owner',
        expect.objectContaining({ checkedOwnIds: [], uncheckedOwnIds: ['placed', 'held'] }),
      );
      // After the hand-over, before the claim ends.
      const order = (mock: ReturnType<typeof vi.fn>) => mock.mock.invocationCallOrder[0];
      expect(order(harness.onRunCommitted)).toBeGreaterThan(order(harness.startDelete));
      expect(order(harness.onRunCommitted)).toBeLessThan(order(harness.endConfirmedRun));
    });

    it('keeps the marking on a cancel', () => {
      const harness = setup({ markedIds: ['before'] });
      harness.run();

      confirmationClosed(harness).next(undefined);

      expect(harness.onRunCommitted).not.toHaveBeenCalled();
    });

    it('keeps the marking when the re-read aborts the confirmed clear-out', () => {
      const harness = setup({
        markedIds: ['before'],
        reread: () =>
          of({
            ...ENTRIES,
            entries: ENTRIES.entries.map((e) =>
              e.sevenTvEmoteId === 'before'
                ? { ...e, heldByActiveTags: [{ id: 9, name: 'Halloween' }] }
                : e,
            ),
          }),
      });
      harness.run();

      confirmationClosed(harness).next({ checkedIds: ['before'] });

      expect(harness.startDelete).not.toHaveBeenCalled();
      expect(harness.notice()?.key).toBe('tags.errors.changedDuringConfirm');
      expect(harness.onRunCommitted).not.toHaveBeenCalled();
    });

    it('lets go of it when a confirmation with nothing ticked sends its report', () => {
      const harness = setup({ markedIds: ['before'] });
      harness.run();

      confirmationClosed(harness).next({ checkedIds: [] });

      expect(harness.onRunCommitted).toHaveBeenCalledOnce();
      expect(harness.reportRemoval).toHaveBeenCalledOnce();
      expect(harness.startDelete).not.toHaveBeenCalled();
    });
  });
});

describe('confirmTimeEntriesDrift', () => {
  it('reads an unchanged tag as no drift', () => {
    expect(
      confirmTimeEntriesDrift('set-active', ENTRIES, ENTRIES, ['placed', 'before']),
    ).toBeNull();
  });

  it('reads another set, or a set that is no longer active, as a set switch', () => {
    expect(
      confirmTimeEntriesDrift('set-active', ENTRIES, { ...ENTRIES, emoteSetId: 'set-new' }, []),
    ).toBe('setChanged');
    expect(
      confirmTimeEntriesDrift('set-active', ENTRIES, { ...ENTRIES, isActiveSet: false }, []),
    ).toBe('setChanged');
  });

  it('reads another activation, another placement revision, or a ticked entry gone as a changed state', () => {
    expect(
      confirmTimeEntriesDrift(
        'set-active',
        ENTRIES,
        { ...ENTRIES, activationOperationId: 'act-2' },
        [],
      ),
    ).toBe('stateChanged');
    const revised = {
      ...ENTRIES,
      entries: ENTRIES.entries.map((current) =>
        current.sevenTvEmoteId === 'gone'
          ? { ...current, placementOperationId: 'rev-new' }
          : current,
      ),
    };
    expect(confirmTimeEntriesDrift('set-active', ENTRIES, revised, [])).toBe('stateChanged');
    const unassigned = {
      ...ENTRIES,
      entries: ENTRIES.entries.filter((current) => current.sevenTvEmoteId !== 'before'),
    };
    expect(confirmTimeEntriesDrift('set-active', ENTRIES, unassigned, ['placed'])).toBeNull();
    expect(confirmTimeEntriesDrift('set-active', ENTRIES, unassigned, ['before'])).toBe(
      'stateChanged',
    );
  });

  it('looks at new holders of ticked rows only', () => {
    const held = {
      ...ENTRIES,
      entries: ENTRIES.entries.map((current) =>
        current.sevenTvEmoteId === 'before'
          ? { ...current, heldByActiveTags: [{ id: 9, name: 'Halloween' }] }
          : current,
      ),
    };
    expect(confirmTimeEntriesDrift('set-active', ENTRIES, held, ['placed'])).toBeNull();
    expect(confirmTimeEntriesDrift('set-active', ENTRIES, held, ['before'])).toBe('stateChanged');
  });
});

describe('splitOwnPlacements', () => {
  it('splits the own placements in the set by the ticks, in their own order', () => {
    expect(splitOwnPlacements({ ownInLiveIds: ['a', 'b', 'c'] }, ['c', 'x', 'a'])).toEqual({
      checkedOwnIds: ['a', 'c'],
      uncheckedOwnIds: ['b'],
    });
  });
});
