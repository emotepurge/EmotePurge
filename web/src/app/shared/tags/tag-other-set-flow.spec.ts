import { Dialog } from '@angular/cdk/dialog';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, computed, signal, WritableSignal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { Observable, Subject, of, throwError } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { EmoteTagEntries, EmoteTagEntry } from '../../core/tags/emote-tag.model';
import { EmoteTagService } from '../../core/tags/emote-tag.service';
import {
  ImportConfirmDialog,
  ImportConfirmDialogData,
  ImportConfirmOutcome,
} from '../seven-tv/import-confirm-dialog';
import { ImportTargetChoice } from '../seven-tv/import-target-dialog';
import { routeTagIntoChosenSet, startTagOtherSetFlow } from './tag-other-set-flow';
import { TagRunRequest } from './tag-play-in-flow';
import { TagRunNotice, TagRunNoticeSink } from './tag-run-notice-sink';

/*
 * The run into another set (#328) against fakes one level below its building blocks, like
 * `tag-play-in-flow.spec.ts`: the real `readLiveSetAliases`, `loadImportTarget` and
 * `startImportFlow` drive the faked services and every call that matters is logged.
 */

const CHANNEL = 'handofblood';
const TAG = { id: 7, name: 'Stronghold' };
const ACTIVE = 'set-active';
const TARGET = 'set-halloween';

function entry(id: string): EmoteTagEntry {
  return {
    sevenTvEmoteId: id,
    alias: `alias-${id}`,
    imageUrl: `https://cdn.example/${id}`,
    inSet: null,
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
    emoteSetId: TARGET,
    isActiveSet: false,
    entries: ids.map(entry),
    activationOperationId: null,
    ...over,
  };
}

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

function choice(over: Partial<ImportTargetChoice> = {}): ImportTargetChoice {
  return {
    scope: 'selection',
    emoteSetId: TARGET,
    channelName: CHANNEL,
    ownerDisplayName: 'HandOfBlood',
    ownerTwitchChannelId: 'tw-owner',
    setName: 'Halloween',
    isTracked: true,
    twitchLogin: CHANNEL,
    activeEmoteSetId: ACTIVE,
    ...over,
  };
}

function addPlan(ids: string[]) {
  return {
    rows: ids.map((id) => ({
      action: 'add' as const,
      source: { sevenTvEmoteId: id, name: `alias-${id}`, imageUrl: null },
      alias: `alias-${id}`,
    })),
  };
}

interface Harness {
  request: TagRunRequest;
  pending: WritableSignal<boolean>;
  notice: WritableSignal<TagRunNotice | null>;
  onFeedback: ReturnType<typeof vi.fn>;
  onRunCommitted: ReturnType<typeof vi.fn>;
  listEntries: ReturnType<typeof vi.fn>;
  registerOperation: ReturnType<typeof vi.fn>;
  reportPlacements: ReturnType<typeof vi.fn>;
  loadEmoteSetPreview: ReturnType<typeof vi.fn>;
  getSetStatus: ReturnType<typeof vi.fn>;
  dialogOpen: ReturnType<typeof vi.fn>;
  startImport: ReturnType<typeof vi.fn>;
  run(picked?: ImportTargetChoice): void;
  route(picked: ImportTargetChoice, active: { playInUsable: boolean; entryCount: number }): void;
  confirmData(): ImportConfirmDialogData;
  confirm(outcome: ImportConfirmOutcome | undefined): void;
}

function setup(
  options: {
    entries?: Observable<EmoteTagEntries>;
    /** Ids the live read of the target finds in it. */
    liveIds?: string[];
    liveTruncated?: boolean;
    markedIds?: string[];
  } = {},
): Harness {
  const listEntries = vi.fn(() => options.entries ?? of(entriesFor(['in-1', 'new-1', 'new-2'])));
  const registerOperation = vi.fn(() => of({ registeredAtUtc: '2026-11-02T10:00:00Z' }));
  const reportPlacements = vi.fn(() => of({}));
  const tagService = {
    listEntries,
    registerOperation,
    reportPlacements,
  } as unknown as EmoteTagService;

  const loadEmoteSetPreview = vi.fn(() =>
    of({
      channelName: CHANNEL,
      sevenTvUserId: null,
      emoteSetId: TARGET,
      emoteSetName: 'Halloween',
      capacity: 1000,
      totalCount: 1,
      truncated: false,
      emotes: [],
    }),
  );
  const emoteSetService = {
    loadEmoteSetPreview,
    resolveEditableSet: vi.fn(),
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

  const httpClient = {
    post: vi.fn(() => of(setPage(options.liveIds ?? ['in-1'], options.liveTruncated))),
  } as unknown as HttpClient;

  const dialogOpen = vi.fn((..._args: unknown[]) => ({ closed: new Subject<unknown>() }));
  const startImport = vi.fn((): boolean => true);
  const importService = {
    startImport,
    reportTargetCheckBlocked: vi.fn(),
    startCheckPending: signal(false),
  } as unknown as SevenTvImportService;
  const activeRun = signal<SevenTvRunKind | null>(null);
  const arbiter = {
    activeRun,
    activeClaim: signal(null),
    noteRefusedStart: vi.fn(),
    startLocked: computed(() => activeRun() !== null),
  } as unknown as SevenTvRunArbiter;

  const pending = signal(false);
  const notice = signal<TagRunNotice | null>(null);
  const onFeedback = vi.fn();
  const onRunCommitted = vi.fn();
  const destroyRef = {
    destroyed: false,
    onDestroy: () => () => undefined,
  } as unknown as DestroyRef;
  const request: TagRunRequest = {
    channelName: CHANNEL,
    tag: TAG,
    setName: 'Main',
    activeEmoteSetId: signal<string | null>(ACTIVE),
    pending,
    notice,
    hostAlive: () => true,
    sink: new TagRunNoticeSink(),
    onFeedback,
    onCompleted: vi.fn(),
    markedIds: options.markedIds,
    onRunCommitted,
  };
  const deps = {
    dialog: { open: dialogOpen } as unknown as Dialog,
    emoteAdminService,
    emoteSetService,
    httpClient,
    tokenService: { hasToken: signal(true) } as unknown as SevenTvTokenService,
    importService,
    arbiter,
    deleteService: {} as SevenTvDeleteService,
    tagService,
    translocoService: { translate: (key: string) => key } as unknown as TranslocoService,
    destroyRef,
  };
  const confirmCall = () =>
    dialogOpen.mock.calls.findIndex(([component]) => component === ImportConfirmDialog);

  return {
    request,
    pending,
    notice,
    onFeedback,
    onRunCommitted,
    listEntries,
    registerOperation,
    reportPlacements,
    loadEmoteSetPreview,
    getSetStatus,
    dialogOpen,
    startImport,
    run: (picked = choice()) => startTagOtherSetFlow(deps, request, picked),
    route: (picked, active) => routeTagIntoChosenSet(deps, request, picked, active),
    confirmData: () =>
      (dialogOpen.mock.calls[confirmCall()][1] as { data: ImportConfirmDialogData }).data,
    confirm: (outcome) => dialogOpen.mock.results[confirmCall()].value.closed.next(outcome),
  };
}

describe('startTagOtherSetFlow', () => {
  afterEach(() => vi.restoreAllMocks());

  // protects=a run into another set leaves no trace of the tag: no registration, no placement
  //   report, and the import run carries no tag context (so none is ever reported);
  // fails_when=the flow registers an operation, reports placements, or hands the run a tag hook;
  // no_claim=the real report endpoint and the audit entry (a Api.Tests case and the live run);
  //   `startImport` is a fake that only records what it was given.
  it('registers nothing, reports nothing and starts the run without a tag context', () => {
    const harness = setup();
    harness.run();

    expect(harness.listEntries).toHaveBeenCalledExactlyOnceWith(CHANNEL, TAG.id, TARGET);
    expect(harness.confirmData().emptyConfirmAllowed).toBe(false);
    expect(harness.confirmData().source.rows.map((row) => row.sevenTvEmoteId)).toEqual([
      'new-1',
      'new-2',
    ]);

    harness.confirm({
      targetSetId: TARGET,
      targetSetName: 'Halloween',
      plan: addPlan(['new-1', 'new-2']),
    });

    expect(harness.startImport).toHaveBeenCalledOnce();
    const runTarget = harness.startImport.mock.calls[0][0];
    expect(runTarget).toMatchObject({ setId: TARGET, isActiveSet: false });
    expect(runTarget).not.toHaveProperty('tag');
    expect(harness.registerOperation).not.toHaveBeenCalled();
    expect(harness.reportPlacements).not.toHaveBeenCalled();
  });

  // protects=the writes stay on the picked set although it is its account's active set: the loader
  //   reads exactly that id live instead of whichever set the server holds as active by then;
  // fails_when=`pinSetId` is dropped, so the `trackedActive` fast path asks for the set status
  //   (and would load the account's active set of that moment);
  // no_claim=a real switch of the active set on 7TV between pick and load — the fake status
  //   request never answers, so only the request contract is proven.
  it('reads exactly the picked set even when the picked account has it as its active set', () => {
    const harness = setup();
    harness.run(choice({ activeEmoteSetId: TARGET }));

    expect(harness.getSetStatus).not.toHaveBeenCalled();
    expect(harness.loadEmoteSetPreview).toHaveBeenCalledOnce();
    expect(harness.loadEmoteSetPreview.mock.calls[0]).toContain(TARGET);

    harness.confirm({
      targetSetId: TARGET,
      targetSetName: 'Halloween',
      plan: addPlan(['new-1']),
    });
    expect(harness.startImport.mock.calls[0][0]).toMatchObject({ setId: TARGET });
  });

  // protects=a blocked or inconsistent read changes nothing and says why, with a way to retry the
  //   read-type blocks;
  // fails_when=a half-known set (truncated read, failed read) leads to a dialog or a write, or a
  //   mismatching entries answer is accepted;
  // no_claim=the wording of the notices; the timeout of a request that never answers.
  it.each([
    [
      'the live read is incomplete',
      { liveTruncated: true },
      { key: 'tags.errors.setReadIncomplete', retry: true },
    ],
    [
      'the entries read fails',
      { entries: throwError(() => new HttpErrorResponse({ status: 500 })) },
      { key: 'tags.errors.entriesUnavailable', retry: true },
    ],
    [
      'the entries describe another set',
      { entries: of(entriesFor(['new-1'], { emoteSetId: 'set-other' })) },
      { key: 'tags.errors.otherSetChanged', retry: false },
    ],
  ])('blocks with a notice and writes nothing when %s', (_, options, expected) => {
    const harness = setup(options);
    harness.run();

    expect(harness.notice()?.key).toBe(expected.key);
    expect(harness.notice()?.retry !== undefined).toBe(expected.retry);
    expect(harness.dialogOpen).not.toHaveBeenCalled();
    expect(harness.startImport).not.toHaveBeenCalled();
    expect(harness.pending()).toBe(false);
    expect(harness.onRunCommitted).not.toHaveBeenCalled();
  });

  // protects=nothing to add means no dialog, no write, no report, and feedback that names the
  //   target set instead of claiming the tag counts as played in;
  // fails_when=the flow opens the confirm dialog or reports for a set that already holds
  //   everything, or reuses the active-set wording;
  // no_claim=that the marking is dropped in the host (the host's own `runCommitted` handler).
  it('says so and does nothing else when the target already holds every emote', () => {
    const harness = setup({ liveIds: ['in-1', 'new-1', 'new-2'] });
    harness.run();

    expect(harness.onFeedback).toHaveBeenCalledExactlyOnceWith(
      'tags.feedback.otherSetAllPresent.other',
      { count: 3, tag: TAG.name, set: 'Halloween' },
    );
    expect(harness.dialogOpen).not.toHaveBeenCalled();
    expect(harness.startImport).not.toHaveBeenCalled();
    expect(harness.reportPlacements).not.toHaveBeenCalled();
    expect(harness.onRunCommitted).toHaveBeenCalledOnce();
    expect(harness.pending()).toBe(false);
  });

  // protects=a grid marking narrows the run to the marked entries, and a marking the tag no longer
  //   has any entry for stops the flow;
  // fails_when=unmarked entries are added, or a marking of vanished emotes adds everything;
  // no_claim=the marking's count label on the button (a component concern).
  it('adds only the marked entries, and stops when none of them is still in the tag', () => {
    const marked = setup({ markedIds: ['new-2', 'gone'] });
    marked.run();
    expect(marked.confirmData().source.rows.map((row) => row.sevenTvEmoteId)).toEqual(['new-2']);

    const gone = setup({ markedIds: ['gone'] });
    gone.run();
    expect(gone.notice()).toEqual({ key: 'tags.errors.markedGone' });
    expect(gone.dialogOpen).not.toHaveBeenCalled();
  });

  // protects=the marking is released exactly when the run was started, not for a dismissed dialog;
  // fails_when=a cancelled confirmation clears the marking, or a started run keeps it;
  // no_claim=a refused start (the fake `startImport` returns true); `import-flow.spec.ts` covers
  //   `runHooks.onStarted` for a refused start.
  it('lets go of the marking once the run started, not when the confirmation is dismissed', () => {
    const dismissed = setup();
    dismissed.run();
    dismissed.confirm(undefined);
    expect(dismissed.onRunCommitted).not.toHaveBeenCalled();

    const started = setup();
    started.run();
    started.confirm({
      targetSetId: TARGET,
      targetSetName: 'Halloween',
      plan: addPlan(['new-1']),
    });
    expect(started.onRunCommitted).toHaveBeenCalledOnce();
  });
});

const USABLE = { playInUsable: true, entryCount: 3 };

describe('routeTagIntoChosenSet', () => {
  afterEach(() => vi.restoreAllMocks());

  // protects=picking the tag channel's current active set still registers and tracks placements
  //   (the ordinary play-in), while any other set takes the plain add;
  // fails_when=the active set is sent down the plain path (placements lost), or another set
  //   registers an operation;
  // no_claim=the rest of the play-in, which `tag-play-in-flow.spec.ts` covers.
  it('sends the active set to the play-in and every other set to the plain add', () => {
    const active = setup({ entries: of(entriesFor(['new-1'], { emoteSetId: ACTIVE })) });
    active.route(choice({ emoteSetId: ACTIVE, activeEmoteSetId: ACTIVE }), USABLE);
    expect(active.listEntries).toHaveBeenCalledWith(CHANNEL, TAG.id, ACTIVE);
    // The play-in's set check fails on the fake's non-active entries — what matters is that it
    // took the play-in path, whose first act is the entries read for the frozen active set.
    expect(active.notice()).toEqual({ key: 'tags.errors.setChanged' });

    const other = setup();
    other.route(choice(), USABLE);
    expect(other.listEntries).toHaveBeenCalledExactlyOnceWith(CHANNEL, TAG.id, TARGET);
    expect(other.registerOperation).not.toHaveBeenCalled();
    expect(other.confirmData().targetIsActiveSet).toBe(false);
  });

  // protects=picking the active set while "Ins Set holen" is hidden or locked (nothing missing, or
  //   a marking with nothing missing) sends no request, so no empty report can activate the tag;
  //   it says the set already holds them, naming the set;
  // fails_when=the pick reaches the play-in (entries read, registration, empty placement report) or
  //   the feedback is missing or uses the "counts as added" wording;
  // no_claim=that the host computes `playInUsable` right (tag-run-actions.spec.ts).
  it('sends no request and says it is complete when the active set is picked without a usable play-in', () => {
    const plain = setup();
    plain.route(choice({ emoteSetId: ACTIVE, activeEmoteSetId: ACTIVE }), {
      playInUsable: false,
      entryCount: 3,
    });
    expect(plain.onFeedback).toHaveBeenCalledExactlyOnceWith(
      'tags.feedback.otherSetAllPresent.other',
      { count: 3, tag: TAG.name, set: 'Halloween' },
    );

    const marked = setup({ markedIds: ['a', 'b'] });
    marked.route(choice({ emoteSetId: ACTIVE, activeEmoteSetId: ACTIVE }), {
      playInUsable: false,
      entryCount: 3,
    });
    expect(marked.onFeedback).toHaveBeenCalledExactlyOnceWith(
      'tags.feedback.otherSetAllMarkedPresent.other',
      { count: 2, tag: TAG.name, set: 'Halloween' },
    );

    for (const harness of [plain, marked]) {
      expect(harness.listEntries).not.toHaveBeenCalled();
      expect(harness.registerOperation).not.toHaveBeenCalled();
      expect(harness.reportPlacements).not.toHaveBeenCalled();
      expect(harness.dialogOpen).not.toHaveBeenCalled();
    }
  });
});
