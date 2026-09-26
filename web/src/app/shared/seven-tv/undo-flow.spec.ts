import { Dialog } from '@angular/cdk/dialog';
import { HttpClient, HttpRequest, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DestroyRef, WritableSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { Observable, Subject, firstValueFrom, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { RUN_DELAY_MS } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { SevenTvUndoService } from '../../core/seven-tv/seven-tv-undo.service';
import { UndoCandidate, UndoSourceFileInfo } from '../../core/seven-tv/undo-candidate';
import { UndoSkippedRow, classifyUndoRows } from '../../core/seven-tv/undo-plan';
import { LIVE_READ_TIMEOUT_MS } from './recovery-file-gate';
import { ResolvedRestoreTarget } from './restore-flow';
import { UndoConfirmDialogData, UndoConfirmOutcome } from './undo-confirm-dialog';
import { TransferUndoFileResult, UndoFlowDeps, startUndoFlow, undoRunTarget } from './undo-flow';

/**
 * `startUndoFlow` opens dialogs through the plain `Dialog` it is handed and injects nothing — so,
 * like `restore-flow.spec.ts`, every case but the last block runs without a `TestBed`: `dialog.open`
 * is one `vi.fn()` standing in for the token prompt and the confirmation alike, told apart by call
 * order; `httpClient.post` answers the flow's two set reads in order. The last block drives the real
 * `SevenTvUndoService` behind `HttpTestingController` (plan T6, Codex finding 1).
 */

const SET_ID = 'set-t';
const CHANNEL = 'kanal_t';

const SOURCE_FILE: UndoSourceFileInfo = {
  stage: 'finished',
  exportedAt: '2026-09-25T10:00:00.000Z',
  verifiedAt: null,
  finishedAt: '2026-09-25T10:00:00.000Z',
  origin: null,
};

function target(overrides: Partial<ResolvedRestoreTarget> = {}): ResolvedRestoreTarget {
  return {
    emoteSetId: SET_ID,
    setName: 'Hauptset',
    ownerDisplayName: 'Olaf',
    twitchLogin: CHANNEL,
    trackedChannelName: CHANNEL,
    isActiveSet: true,
    hostChannelName: 'host_channel',
    hostSelectedSetId: SET_ID,
    ...overrides,
  };
}

/** Candidate `n`: source `src-n` under `An`, its replaced target `tgt-n` once held exactly `An`. */
function cand(n: string, provenance: UndoCandidate['provenance'] = 'confirmed'): UndoCandidate {
  return {
    sourceSevenTvEmoteId: `src-${n}`,
    sourceName: `Source${n}`,
    alias: `A${n}`,
    fileStatus: 'done',
    target: { sevenTvEmoteId: `tgt-${n}`, entries: [{ alias: `A${n}` }], defaultName: null },
    provenance,
  };
}

function result(
  candidates: UndoCandidate[],
  overrides: Partial<TransferUndoFileResult> = {},
): TransferUndoFileResult {
  return {
    kind: 'transfer-undo',
    candidates,
    target: target(),
    sourceFile: SOURCE_FILE,
    ...overrides,
  };
}

interface LiveEntry {
  id: string;
  alias: string | null;
}

/** The live state in which candidate `n` runs `full` (source under `An`, target gone). */
const fullState = (n: string): LiveEntry[] => [{ id: `src-${n}`, alias: `A${n}` }];

/** One page of the tokenless set read; `complete: false` makes its total disagree with its items. */
function readPage(entries: LiveEntry[], complete = true) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount: entries.length + (complete ? 0 : 1),
            pageCount: 1,
            items: entries.map((entry) => ({ alias: entry.alias, emote: { id: entry.id } })),
          },
        },
      },
    },
  };
}

/** The typed read `loadSevenTvSetEntries` makes of `readPage(entries)`. */
function typedRead(entries: LiveEntry[]): SevenTvSetEntries {
  const aliasesById = new Map<string, string[]>();
  const aliaslessIds = new Set<string>();
  for (const entry of entries) {
    const aliases = aliasesById.get(entry.id) ?? [];
    if (entry.alias === null) {
      aliaslessIds.add(entry.id);
    } else {
      aliases.push(entry.alias);
    }
    aliasesById.set(entry.id, aliases);
  }
  return {
    aliasesById,
    aliaslessIds,
    defaultNameById: new Map(entries.map((entry) => [entry.id, ''])),
    animatedById: new Map(entries.map((entry) => [entry.id, false])),
    occupiedSlots: entries.length,
    complete: true,
  };
}

/** What the confirm dialog hands back for `candidates` classified against `live`, confirmed as is. */
function outcomeFor(
  candidates: UndoCandidate[],
  live: LiveEntry[],
  acknowledgedUnproven = false,
): UndoConfirmOutcome {
  const read = typedRead(live);
  const plan = classifyUndoRows(candidates, read);
  return { runnable: plan.rows, skipped: plan.skipped, acknowledgedUnproven, read };
}

interface FakeDestroyRef {
  onDestroy(callback: () => void): () => void;
  triggerDestroy(): void;
}

function fakeDestroyRef(): FakeDestroyRef {
  const callbacks = new Set<() => void>();
  return {
    onDestroy: (callback) => {
      callbacks.add(callback);
      return () => callbacks.delete(callback);
    },
    triggerDestroy: () => {
      for (const callback of callbacks) {
        callback();
      }
    },
  };
}

interface Harness {
  deps: UndoFlowDeps;
  dialogOpen: ReturnType<typeof vi.fn>;
  /** The flow's set reads, in order — each a `loadSevenTvSetEntries` page request. */
  httpPost: ReturnType<typeof vi.fn>;
  startUndo: ReturnType<typeof vi.fn>;
  hasToken: WritableSignal<boolean>;
  activeRun: WritableSignal<SevenTvRunKind | null>;
  noteRefusedStart: ReturnType<typeof vi.fn>;
  firstReadPending: WritableSignal<boolean>;
  destroyRef: FakeDestroyRef;
}

function setup(): Harness {
  const httpPost = vi.fn<(url: string, body: unknown) => Observable<unknown>>();
  const hasToken = signal(true);
  const startUndo = vi.fn();
  const activeRun = signal<SevenTvRunKind | null>(null);
  const noteRefusedStart = vi.fn();
  const dialogOpen = vi.fn(() => ({ closed: new Subject<unknown>() }));
  const firstReadPending = signal(false);
  const destroyRef = fakeDestroyRef();
  return {
    deps: {
      dialog: { open: dialogOpen } as unknown as Dialog,
      httpClient: { post: httpPost } as unknown as HttpClient,
      tokenService: { hasToken } as unknown as SevenTvTokenService,
      undoService: { startUndo } as unknown as SevenTvUndoService,
      arbiter: { activeRun, noteRefusedStart } as unknown as SevenTvRunArbiter,
      firstReadPending,
      destroyRef: destroyRef as unknown as DestroyRef,
    },
    dialogOpen,
    httpPost,
    startUndo,
    hasToken,
    activeRun,
    noteRefusedStart,
    firstReadPending,
    destroyRef,
  };
}

function closedAt<T>(dialogOpen: ReturnType<typeof vi.fn>, index: number): Subject<T> {
  return dialogOpen.mock.results[index].value.closed as Subject<T>;
}

function dataAt(dialogOpen: ReturnType<typeof vi.fn>, index: number): UndoConfirmDialogData {
  return dialogOpen.mock.calls[index][1].data as UndoConfirmDialogData;
}

describe('startUndoFlow', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  describe('order: arbiter → token → first read → confirmation (spec 4.2 point 5, 17 K3)', () => {
    it('notes a busy arbiter and does nothing else — no prompt, no read, no dialog (AK 32)', () => {
      const h = setup();
      h.activeRun.set('import');
      h.hasToken.set(false);

      startUndoFlow(h.deps, result([cand('1')]));

      expect(h.noteRefusedStart).toHaveBeenCalledWith('undo');
      expect(h.dialogOpen).not.toHaveBeenCalled();
      expect(h.httpPost).not.toHaveBeenCalled();
    });

    it('prompts for a missing token before reading anything, and reads once it is saved', () => {
      const h = setup();
      h.hasToken.set(false);
      h.httpPost.mockReturnValue(of(readPage(fullState('1'))));

      startUndoFlow(h.deps, result([cand('1')]));
      expect(h.dialogOpen).toHaveBeenCalledTimes(1);
      expect(h.httpPost).not.toHaveBeenCalled();

      closedAt<boolean>(h.dialogOpen, 0).next(true);

      expect(h.httpPost).toHaveBeenCalledTimes(1);
      expect(dataAt(h.dialogOpen, 1).initialRead).not.toBeNull();
    });

    it('ends quietly when the token prompt is cancelled', () => {
      const h = setup();
      h.hasToken.set(false);

      startUndoFlow(h.deps, result([cand('1')]));
      closedAt<boolean>(h.dialogOpen, 0).next(false);

      expect(h.httpPost).not.toHaveBeenCalled();
      expect(h.dialogOpen).toHaveBeenCalledTimes(1);
      expect(h.startUndo).not.toHaveBeenCalled();
    });

    it("opens the confirmation on the flow's own read: the file's candidates, its target, where it came from, and when the read arrived", () => {
      const h = setup();
      vi.spyOn(Date, 'now').mockReturnValue(4711);
      h.httpPost.mockReturnValue(of(readPage(fullState('1'))));
      const file = result([cand('1')]);

      startUndoFlow(h.deps, file);

      expect(h.httpPost).toHaveBeenCalledTimes(1);
      expect((h.httpPost.mock.calls[0][1] as { variables: { id: string } }).variables.id).toBe(
        SET_ID,
      );
      expect(dataAt(h.dialogOpen, 0)).toEqual({
        candidates: file.candidates,
        target: file.target,
        sourceFile: SOURCE_FILE,
        initialRead: typedRead(fullState('1')),
        initialReadAt: 4711,
      });
      expect(h.startUndo).not.toHaveBeenCalled();
    });

    // AK 5, 21, E18: the same file a second time — every source gone, every target back.
    it('opens no dialog when nothing would run, and hands every skipped candidate to the service notice — one read, no run', () => {
      const h = setup();
      h.httpPost.mockReturnValue(
        of(
          readPage([
            { id: 'tgt-1', alias: 'A1' },
            { id: 'tgt-2', alias: 'A2' },
          ]),
        ),
      );
      const file = result([cand('1'), cand('2')]);

      startUndoFlow(h.deps, file);

      expect(h.dialogOpen).not.toHaveBeenCalled();
      expect(h.httpPost).toHaveBeenCalledTimes(1);
      expect(h.startUndo).toHaveBeenCalledTimes(1);
      const [runTarget, runnable, skipped, acknowledged] = h.startUndo.mock.calls[0];
      expect(runTarget).toEqual(undoRunTarget(file.target, SOURCE_FILE));
      expect(runnable).toEqual([]);
      expect((skipped as UndoSkippedRow[]).map((row) => [row.candidate.alias, row.reason])).toEqual(
        [
          ['A1', 'nothingToDo'],
          ['A2', 'nothingToDo'],
        ],
      );
      expect(acknowledged).toBe(false);
    });

    // AK 7: the dialog opens in its error state rather than not at all.
    it.each([
      ['fails', () => throwError(() => new Error('down'))],
      ['comes back incomplete', () => of(readPage(fullState('1'), false))],
    ])('opens the confirmation with no read when the first read %s', (_, answer) => {
      const h = setup();
      vi.spyOn(Date, 'now').mockReturnValue(99);
      h.httpPost.mockReturnValue(answer());

      startUndoFlow(h.deps, result([cand('1')]));

      expect(dataAt(h.dialogOpen, 0).initialRead).toBeNull();
      expect(dataAt(h.dialogOpen, 0).initialReadAt).toBe(99);
      expect(h.startUndo).not.toHaveBeenCalled();
    });

    it('marks the first read pending while it is out, refuses a second flow meanwhile, and clears the mark when it answers', () => {
      const h = setup();
      const pending = new Subject<unknown>();
      h.httpPost.mockReturnValue(pending);

      startUndoFlow(h.deps, result([cand('1')]));
      expect(h.firstReadPending()).toBe(true);
      startUndoFlow(h.deps, result([cand('1')]));
      expect(h.httpPost).toHaveBeenCalledTimes(1);
      // Not even a token prompt for the second one.
      h.hasToken.set(false);
      startUndoFlow(h.deps, result([cand('1')]));
      expect(h.dialogOpen).not.toHaveBeenCalled();
      h.hasToken.set(true);

      pending.next(readPage(fullState('1')));
      pending.complete();

      expect(h.firstReadPending()).toBe(false);
      expect(h.dialogOpen).toHaveBeenCalledTimes(1);
    });

    it('gives up on a first read that hangs past its timeout: releases the pending mark and opens the confirmation with no read', () => {
      vi.useFakeTimers();
      try {
        const h = setup();
        h.httpPost.mockReturnValue(new Subject<unknown>());

        startUndoFlow(h.deps, result([cand('1')]));
        expect(h.firstReadPending()).toBe(true);
        vi.advanceTimersByTime(LIVE_READ_TIMEOUT_MS);

        expect(h.firstReadPending()).toBe(false);
        expect(h.dialogOpen).toHaveBeenCalledTimes(1);
        expect(dataAt(h.dialogOpen, 0).initialRead).toBeNull();
      } finally {
        vi.useRealTimers();
      }
    });

    it('drops a first read that answers after the caller is gone, and clears the pending mark', () => {
      const h = setup();
      const pending = new Subject<unknown>();
      h.httpPost.mockReturnValue(pending);

      startUndoFlow(h.deps, result([cand('1')]));
      h.destroyRef.triggerDestroy();
      pending.next(readPage(fullState('1')));

      expect(h.dialogOpen).not.toHaveBeenCalled();
      expect(h.firstReadPending()).toBe(false);
    });
  });

  describe('after the confirmation: arbiter → token → freshness check → start (E14, AK 8)', () => {
    /** Runs the flow over `candidates` — first read `live`, the confirmation answered with
     *  `outcome`, the freshness read answered with `fresh` (by default `live` again). */
    function confirmed(
      h: Harness,
      candidates: UndoCandidate[],
      live: LiveEntry[],
      options: {
        outcome?: UndoConfirmOutcome | null | undefined;
        fresh?: Observable<unknown>;
      } = {},
    ): TransferUndoFileResult {
      const outcome = 'outcome' in options ? options.outcome : outcomeFor(candidates, live);
      h.httpPost
        .mockReturnValueOnce(of(readPage(live)))
        .mockReturnValueOnce(options.fresh ?? of(readPage(live)));
      const file = result(candidates);
      startUndoFlow(h.deps, file);
      closedAt<UndoConfirmOutcome | null | undefined>(h.dialogOpen, 0).next(outcome);
      return file;
    }

    it.each([null, undefined])(
      'does nothing more when the confirmation closes with %s',
      (value) => {
        const h = setup();

        confirmed(h, [cand('1')], fullState('1'), { outcome: value });

        expect(h.httpPost).toHaveBeenCalledTimes(1);
        expect(h.startUndo).not.toHaveBeenCalled();
        expect(h.noteRefusedStart).not.toHaveBeenCalled();
      },
    );

    it('starts the fresh rows with the dialog’s skipped rows and its confirmation flag after an unchanged freshness read', () => {
      const h = setup();
      const candidates = [cand('1'), cand('2'), cand('3')];
      // 1 runs full, 2 runs addOnly (source and target gone), 3 is already done (target back).
      const live: LiveEntry[] = [...fullState('1'), { id: 'tgt-3', alias: 'A3' }];
      h.httpPost.mockReturnValueOnce(of(readPage(live))).mockReturnValueOnce(of(readPage(live)));
      const file = result(candidates);
      const outcome = outcomeFor(candidates, live, true);

      startUndoFlow(h.deps, file);
      closedAt<UndoConfirmOutcome>(h.dialogOpen, 0).next(outcome);

      expect(h.httpPost).toHaveBeenCalledTimes(2);
      expect(h.startUndo).toHaveBeenCalledTimes(1);
      expect(h.startUndo).toHaveBeenCalledWith(
        undoRunTarget(file.target, SOURCE_FILE),
        classifyUndoRows(candidates, typedRead(live)).rows,
        outcome.skipped,
        true,
      );
    });

    it('passes the confirmation flag through unchanged when it was not given', () => {
      const h = setup();

      confirmed(h, [cand('1', 'unproven')], fullState('1'));

      expect(h.startUndo.mock.calls[0][3]).toBe(false);
    });

    // AK 8: a later source got a second alias between the dialog and the start.
    it('skips a row whose classification drifted as skippedDrift, described by the fresh read, and starts the rest', () => {
      const h = setup();
      const candidates = [cand('1'), cand('2')];
      const live = [...fullState('1'), ...fullState('2')];
      const fresh: LiveEntry[] = [...live, { id: 'src-2', alias: 'Second' }];

      confirmed(h, candidates, live, { fresh: of(readPage(fresh)) });

      const [, runnable, skipped] = h.startUndo.mock.calls[0];
      expect(runnable.map((row: { candidate: UndoCandidate }) => row.candidate.alias)).toEqual([
        'A1',
      ]);
      expect(skipped).toEqual([
        {
          candidate: candidates[1],
          reason: 'skippedDrift',
          live: { sourceEntries: ['A2', 'Second'], targetEntries: [] },
          omittedEntries: [],
        },
      ]);
    });

    // AK 8: no removal without a complete read right before the start; the ADD-only rows still run.
    it.each([
      ['fails', () => throwError(() => new Error('down'))],
      ['comes back incomplete', () => of(readPage([], false))],
    ])(
      'lets no full row run when the freshness read %s — recheckUnavailable — and runs the addOnly rows as confirmed',
      (_, answer) => {
        const h = setup();
        const candidates = [cand('1'), cand('2')];
        const live = fullState('1');
        const outcome = outcomeFor(candidates, live);
        h.httpPost.mockReturnValueOnce(of(readPage(live))).mockReturnValueOnce(answer());
        startUndoFlow(h.deps, result(candidates));
        closedAt<UndoConfirmOutcome>(h.dialogOpen, 0).next(outcome);

        const [, runnable, skipped] = h.startUndo.mock.calls[0];
        expect(runnable).toEqual([outcome.runnable[1]]);
        expect(runnable[0].mode).toBe('addOnly');
        expect(skipped).toEqual([
          {
            candidate: candidates[0],
            reason: 'recheckUnavailable',
            live: { sourceEntries: ['A1'], targetEntries: [] },
            omittedEntries: [],
          },
        ]);
      },
    );

    it("keeps the file's order across the dialog's skipped rows and the freshness check's", () => {
      const h = setup();
      const candidates = [cand('1'), cand('2'), cand('3')];
      // 1 runs full, 2 is already done in the dialog, 3 runs full.
      const live: LiveEntry[] = [
        ...fullState('1'),
        { id: 'tgt-2', alias: 'A2' },
        ...fullState('3'),
      ];
      // 1 drifts before the start.
      const fresh = of(readPage([...live, { id: 'src-1', alias: 'Other' }]));

      confirmed(h, candidates, live, { fresh });

      const skipped = h.startUndo.mock.calls[0][2] as UndoSkippedRow[];
      expect(skipped.map((row) => [row.candidate.alias, row.reason])).toEqual([
        ['A1', 'skippedDrift'],
        ['A2', 'nothingToDo'],
      ]);
    });

    it('notes the refusal and reads nothing more when another run claims the arbiter while the dialog is open', () => {
      const h = setup();
      h.httpPost.mockReturnValueOnce(of(readPage(fullState('1'))));
      startUndoFlow(h.deps, result([cand('1')]));

      h.activeRun.set('delete');
      closedAt<UndoConfirmOutcome>(h.dialogOpen, 0).next(outcomeFor([cand('1')], fullState('1')));

      expect(h.noteRefusedStart).toHaveBeenCalledWith('undo');
      expect(h.httpPost).toHaveBeenCalledTimes(1);
      expect(h.startUndo).not.toHaveBeenCalled();
    });

    it('notes the refusal and starts nothing when another run claims the arbiter while the freshness read is out', () => {
      const h = setup();
      const fresh = new Subject<unknown>();
      h.httpPost.mockReturnValueOnce(of(readPage(fullState('1')))).mockReturnValueOnce(fresh);
      startUndoFlow(h.deps, result([cand('1')]));
      closedAt<UndoConfirmOutcome>(h.dialogOpen, 0).next(outcomeFor([cand('1')], fullState('1')));

      h.activeRun.set('restore');
      fresh.next(readPage(fullState('1')));

      expect(h.noteRefusedStart).toHaveBeenCalledWith('undo');
      expect(h.startUndo).not.toHaveBeenCalled();
    });

    it('asks for the token again when it was cleared while the dialog was open, and only then reads fresh', () => {
      const h = setup();
      h.httpPost.mockReturnValue(of(readPage(fullState('1'))));
      startUndoFlow(h.deps, result([cand('1')]));

      h.hasToken.set(false);
      closedAt<UndoConfirmOutcome>(h.dialogOpen, 0).next(outcomeFor([cand('1')], fullState('1')));
      expect(h.dialogOpen).toHaveBeenCalledTimes(2);
      expect(h.httpPost).toHaveBeenCalledTimes(1);

      h.hasToken.set(true);
      closedAt<boolean>(h.dialogOpen, 1).next(true);

      expect(h.httpPost).toHaveBeenCalledTimes(2);
      expect(h.startUndo).toHaveBeenCalledTimes(1);
    });
  });

  describe('what the freshness check lets run (E14, spec 17 K2)', () => {
    // The central safety property: only rows the dialog let run can run. A candidate the dialog
    // skipped for any reason stays out, even when the fresh read would now classify it `full`.
    it('never starts a candidate the dialog skipped, even when the fresh read would now make it a full row', () => {
      const h = setup();
      const candidates = [cand('1'), cand('2')];
      // 1 runs full; 2 is skipped in the dialog — its source holds a second name.
      const live: LiveEntry[] = [
        ...fullState('1'),
        ...fullState('2'),
        { id: 'src-2', alias: 'Second' },
      ];
      const outcome = outcomeFor(candidates, live);
      expect(outcome.skipped.map((row) => [row.candidate.alias, row.reason])).toEqual([
        ['A2', 'sourceHasMoreEntries'],
      ]);
      // By the start, the second name is gone: 2 alone would now classify `full`.
      const fresh = [...fullState('1'), ...fullState('2')];
      h.httpPost.mockReturnValueOnce(of(readPage(live))).mockReturnValueOnce(of(readPage(fresh)));

      startUndoFlow(h.deps, result(candidates));
      closedAt<UndoConfirmOutcome>(h.dialogOpen, 0).next(outcome);

      const [, runnable, skipped] = h.startUndo.mock.calls[0];
      expect(runnable.map((row: { candidate: UndoCandidate }) => row.candidate.alias)).toEqual([
        'A1',
      ]);
      expect((skipped as UndoSkippedRow[]).map((row) => [row.candidate.alias, row.reason])).toEqual(
        [['A2', 'sourceHasMoreEntries']],
      );
    });

    it('starts nothing but the service notice when every confirmed row drifted', () => {
      const h = setup();
      const candidates = [cand('1'), cand('2')];
      const live = [...fullState('1'), ...fullState('2')];
      const fresh: LiveEntry[] = [
        ...live,
        { id: 'src-1', alias: 'Other1' },
        { id: 'src-2', alias: 'Other2' },
      ];
      h.httpPost.mockReturnValueOnce(of(readPage(live))).mockReturnValueOnce(of(readPage(fresh)));
      const file = result(candidates);

      startUndoFlow(h.deps, file);
      closedAt<UndoConfirmOutcome>(h.dialogOpen, 0).next(outcomeFor(candidates, live, true));

      expect(h.startUndo).toHaveBeenCalledTimes(1);
      const [runTarget, runnable, skipped, acknowledged] = h.startUndo.mock.calls[0];
      expect(runTarget).toEqual(undoRunTarget(file.target, SOURCE_FILE));
      expect(runnable).toEqual([]);
      expect((skipped as UndoSkippedRow[]).map((row) => [row.candidate.alias, row.reason])).toEqual(
        [
          ['A1', 'skippedDrift'],
          ['A2', 'skippedDrift'],
        ],
      );
      expect(acknowledged).toBe(true);
    });

    it('asks for the token again when it was cleared while the freshness read was out, and reads fresh once more after it', () => {
      const h = setup();
      const freshRead = new Subject<unknown>();
      h.httpPost
        .mockReturnValueOnce(of(readPage(fullState('1'))))
        .mockReturnValueOnce(freshRead)
        .mockReturnValueOnce(of(readPage(fullState('1'))));
      startUndoFlow(h.deps, result([cand('1')]));
      closedAt<UndoConfirmOutcome>(h.dialogOpen, 0).next(outcomeFor([cand('1')], fullState('1')));

      h.hasToken.set(false);
      freshRead.next(readPage(fullState('1')));

      expect(h.startUndo).not.toHaveBeenCalled();
      expect(h.dialogOpen).toHaveBeenCalledTimes(2);

      h.hasToken.set(true);
      closedAt<boolean>(h.dialogOpen, 1).next(true);

      expect(h.httpPost).toHaveBeenCalledTimes(3);
      expect(h.startUndo).toHaveBeenCalledTimes(1);
    });
  });

  // Spec 6.5: the restore's target derivation without `resyncChannelName`, plus the protocol's three.
  describe('undoRunTarget', () => {
    it('expects the tracked channel for its active set', () => {
      expect(undoRunTarget(target(), SOURCE_FILE)).toEqual({
        setId: SET_ID,
        expectedChannelName: CHANNEL,
        hostChannelName: 'host_channel',
        setName: 'Hauptset',
        ownerOrChannelLabel: CHANNEL,
        trackedChannelName: CHANNEL,
        ownerDisplayName: 'Olaf',
        sourceFile: SOURCE_FILE,
      });
    });

    it('expects no channel for a non-active set of a tracked channel, but still names it', () => {
      expect(undoRunTarget(target({ isActiveSet: false }), SOURCE_FILE)).toMatchObject({
        expectedChannelName: null,
        trackedChannelName: CHANNEL,
        ownerOrChannelLabel: CHANNEL,
      });
    });

    it('labels an untracked target by its owner and expects no channel (AK 22)', () => {
      expect(
        undoRunTarget(target({ trackedChannelName: null, isActiveSet: false }), SOURCE_FILE),
      ).toMatchObject({
        expectedChannelName: null,
        trackedChannelName: null,
        ownerOrChannelLabel: 'Olaf',
        ownerDisplayName: 'Olaf',
      });
    });
  });
});

/**
 * Spec 17 K2 across the seam dialog → flow → service (plan T6, Codex finding 1): a `planned` file
 * mixing an unproven `full` row with an `addOnly` row, confirmed without the origin confirmation,
 * sends the ADDs of the `addOnly` row and no REMOVE — with the real `SevenTvUndoService`, and even
 * when the dialog wrongly hands the unproven `full` row over as runnable.
 */
describe('startUndoFlow with the real undo service (spec 17 K2)', () => {
  const GQL_ENDPOINT = 'https://7tv.io/v4/gql';
  let httpMock: HttpTestingController;
  let dialogOpen: ReturnType<typeof vi.fn>;
  let deps: UndoFlowDeps;

  const isGql = (req: HttpRequest<unknown>, fragment: string) =>
    req.url === GQL_ENDPOINT &&
    ((req.body as { query?: string } | null)?.query ?? '').includes(fragment);
  const isRead = (req: HttpRequest<unknown>) => isGql(req, 'emotes(page: $page');
  const isRemove = (req: HttpRequest<unknown>) => isGql(req, 'removeEmote');
  const isAdd = (req: HttpRequest<unknown>) => isGql(req, 'addEmote');

  beforeEach(async () => {
    sessionStorage.clear();
    vi.useFakeTimers();
    vi.spyOn(console, 'info').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    httpMock = TestBed.inject(HttpTestingController);
    const tokenService = TestBed.inject(SevenTvTokenService);
    tokenService.setToken('write-token');
    dialogOpen = vi.fn(() => ({ closed: new Subject<unknown>() }));
    deps = {
      dialog: { open: dialogOpen } as unknown as Dialog,
      httpClient: TestBed.inject(HttpClient),
      tokenService,
      undoService: TestBed.inject(SevenTvUndoService),
      arbiter: TestBed.inject(SevenTvRunArbiter),
      firstReadPending: signal(false),
      destroyRef: fakeDestroyRef() as unknown as DestroyRef,
    };
  });

  afterEach(() => {
    try {
      httpMock.verify();
    } finally {
      vi.useRealTimers();
      vi.restoreAllMocks();
      TestBed.resetTestingModule();
    }
  });

  /** A mixed `planned` file: candidate 1 runs `full` (its source stands under `A1`), candidate 2
   *  runs `addOnly` (source and target gone) — both unproven. */
  const candidates = [cand('1', 'unproven'), cand('2', 'unproven')];
  const live = fullState('1');

  /** Answers the first read with `options.live`, confirms with `outcome`, answers the freshness
   *  read with `options.fresh`, and runs the engine to its end — every ADD accepted, every report
   *  answered, and no REMOVE (nor its recheck read) expected. Returns every mutation sent. */
  function runThrough(
    outcome: UndoConfirmOutcome,
    options: { candidates?: UndoCandidate[]; live?: LiveEntry[]; fresh?: LiveEntry[] } = {},
  ): string[] {
    startUndoFlow(deps, result(options.candidates ?? candidates));
    httpMock.expectOne(isRead).flush(readPage(options.live ?? live));
    closedAt<UndoConfirmOutcome>(dialogOpen, 0).next(outcome);
    httpMock.expectOne(isRead).flush(readPage(options.fresh ?? options.live ?? live));

    const sent: string[] = [];
    for (let step = 0; step < 5; step += 1) {
      httpMock.expectNone(isRemove);
      for (const add of httpMock.match(isAdd)) {
        sent.push(`add ${add.request.body.variables.emoteId} ${add.request.body.variables.alias}`);
        add.flush({});
      }
      vi.advanceTimersByTime(RUN_DELAY_MS);
    }
    for (const report of httpMock.match(() => true)) {
      sent.push(`report ${report.request.url}`);
      report.flush({
        reportedCount: 1,
        channels: [],
        unresolvedChannel: null,
        resyncTriggered: [],
      });
    }
    return sent;
  }

  it('sends exactly the ADD of the addOnly row and no REMOVE when the dialog leaves the unproven full row out', () => {
    const plan = classifyUndoRows(candidates, typedRead(live));
    const [full, addOnly] = plan.rows;
    expect([full.mode, addOnly.mode]).toEqual(['full', 'addOnly']);

    const sent = runThrough({
      runnable: [addOnly],
      skipped: [
        {
          candidate: full.candidate,
          reason: 'skippedUnproven',
          live: { sourceEntries: ['A1'], targetEntries: [] },
          omittedEntries: [],
        },
      ],
      acknowledgedUnproven: false,
      read: typedRead(live),
    });

    expect(sent).toEqual([
      'add tgt-2 A2',
      `report /api/seventv/emote-sets/${SET_ID}/sync-restored`,
    ]);
    // The dialog's own skip reaches the run once — the service's lock does not add a second one.
    expect(deps.undoService.run()?.skipped.map((row) => [row.candidate.alias, row.reason])).toEqual(
      [['A1', 'skippedUnproven']],
    );
  });

  it('sends no REMOVE for a candidate the dialog skipped, even when the fresh read would now make it a full row', () => {
    // 1 runs addOnly (source and target gone); 2's source holds a second name in the dialog's read
    // and is skipped — by the freshness read that name is gone, and 2 alone would classify `full`.
    const confirmed = [cand('1'), cand('2')];
    const dialogLive: LiveEntry[] = [...fullState('2'), { id: 'src-2', alias: 'Second' }];
    const outcome = outcomeFor(confirmed, dialogLive);
    expect(outcome.runnable.map((row) => [row.candidate.alias, row.mode])).toEqual([
      ['A1', 'addOnly'],
    ]);

    const sent = runThrough(outcome, {
      candidates: confirmed,
      live: dialogLive,
      fresh: fullState('2'),
    });

    expect(sent).toEqual([
      'add tgt-1 A1',
      `report /api/seventv/emote-sets/${SET_ID}/sync-restored`,
    ]);
    expect(deps.undoService.run()?.skipped.map((row) => [row.candidate.alias, row.reason])).toEqual(
      [['A2', 'sourceHasMoreEntries']],
    );
  });

  it('still sends no REMOVE when the dialog wrongly hands the unproven full row over as runnable', () => {
    const plan = classifyUndoRows(candidates, typedRead(live));

    const sent = runThrough({
      runnable: plan.rows,
      skipped: plan.skipped,
      acknowledgedUnproven: false,
      read: typedRead(live),
    });

    expect(sent).toEqual([
      'add tgt-2 A2',
      `report /api/seventv/emote-sets/${SET_ID}/sync-restored`,
    ]);
    expect(deps.undoService.run()?.skipped.map((row) => row.reason)).toEqual(['skippedUnproven']);
  });
});
