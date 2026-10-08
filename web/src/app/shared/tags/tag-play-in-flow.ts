import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Signal, WritableSignal, computed, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoService } from '@jsverse/transloco';
import { Observable } from 'rxjs';

import {
  SevenTvDeleteService,
  timeoutReportAttempt,
} from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { TargetCheckBlockReason } from '../../core/seven-tv/sync-report-outcome';
import { EmoteTagEntries, TagOperationKind } from '../../core/tags/emote-tag.model';
import { EmoteTagService } from '../../core/tags/emote-tag.service';
import { pluralKey } from '../../core/i18n/plural';
import {
  DeleteTargetResolution,
  MEMBER_READ_TRUNCATED_REASON_KEY,
  MEMBER_READ_UNAVAILABLE_REASON_KEY,
  readLiveSetAliases,
  resolveDeleteTarget,
} from '../seven-tv/delete-flow';
import { ImportFlowDeps, startImportFlow } from '../seven-tv/import-flow';
import { toImportTarget } from '../seven-tv/import-trigger';
import { buildTagImportSource, partitionTagPlayIn, playInCandidates } from './tag-play-in';
import { TagRunNotice, TagRunNoticeSink } from './tag-run-notice-sink';

/*
 * The tag play-in (#201 T-C, spec 7.1): from the click to the hand-over to the ordinary import flow,
 * or — when the set already holds every entry — straight to the empty placement report.
 *
 * Both tag flows (this one and `tag-removal-flow.ts`) share the steps before their own part
 * (`prepareTagRun`): freeze the active set → read the tag's entries for exactly that set → the
 * shared set pre-check → register the operation → read the set live from 7TV. The order is fixed
 * for three reasons:
 *
 * - **Pre-check (2b) before registration (2a):** the registration carries the set owner's Twitch id,
 *   and only the pre-check knows it (spec 7.1/2a).
 * - **Registration before anything else touches 7TV or opens a dialog:** the server stamps the
 *   operation's time on registration, and a later placement report is judged against that stamp
 *   (E27, E33). A 403 there is also the first moment the user learns they may not write this set —
 *   before any dialog, and before the first ADD or REMOVE would have said so.
 * - **The live read blocks:** a read that failed or came back incomplete knows only half the set,
 *   and a half-known set must decide nothing (E28): the play-in would ADD what is already there
 *   (7TV does not dedupe by id), the clear-out would propose from a list that misses entries.
 *
 * The confirm/start set guards (the clear-out's confirm, the import hook's start) never read the
 * host's raw `activeEmoteSetId` input: both dialogs outlive the host, and a host destroyed behind
 * them keeps its input at the old id forever — exactly when a set switch unmounts it before the
 * new id arrives. They read `PreparedTagRun.activeEmoteSetId` instead
 * (`hostBoundActiveSet`), which turns `null` with the host's teardown: an unknown set, so the guard
 * treats it as a switch and aborts — the tag flows' counterpart of the delete chain's
 * `destroyRef.destroyed` abort in `delete-flow.ts`. A destroyed host never authorises a 7TV write.
 *
 * What a flow still says after its host is gone — that abort's notice, an in-flight report's
 * failure banner with its retry, a report's acknowledgement — goes to the page-level
 * `TagRunNoticeSink` instead of the destroyed component (`raiseNotice`, `sendTagReport`,
 * `tellFeedback`, `tellCompleted`): an abort nobody sees reads as success.
 *
 * None of these steps holds `startCheckPending`: like the delete chain in `delete-flow.ts`, the
 * reads before a dialog are not a confirmed start, so they lock only this component's own buttons
 * (`pending`), not every 7TV trigger on the page.
 */

/** Everything the two tag flows need, handed in by `TagRunActions` (same reasoning as
 *  `ImportFlowDeps`). */
export interface TagRunFlowDeps extends ImportFlowDeps {
  deleteService: SevenTvDeleteService;
  tagService: EmoteTagService;
  /** For the confirm-time refusal's run-kind noun (`confirmTimeRefusal`). */
  translocoService: TranslocoService;
  /** The caller's teardown: a late answer before the dialog or the import is dropped with it. */
  destroyRef: DestroyRef;
}

/** What one click hands a flow: frozen values, and the caller's signals and callbacks. */
export interface TagRunRequest {
  channelName: string;
  tag: { id: number; name: string };
  /** The active set's display name, `null` to fall back to its id. */
  setName: string | null;
  /** The host page's live view of the channel's active set — frozen on the click. The later
   *  comparisons read `PreparedTagRun.activeEmoteSetId`, never this one directly. */
  activeEmoteSetId: Signal<string | null>;
  /** `true` from the click until the flow hands over to a run or a dialog it does not own, or ends. */
  pending: WritableSignal<boolean>;
  notice: WritableSignal<TagRunNotice | null>;
  /** Whether the host still shows this request's tag and channel. A banner raised after the host
   *  moved on to another tag is dropped (`raiseNotice`) — its retry would replay this request under
   *  the other tag's buttons. Absent: always current. */
  isCurrent?(): boolean;
  /** Whether the host component still lives. Once it is torn down, `notice`, `onFeedback` and
   *  `onCompleted` belong to a dead component; everything goes to `sink` instead. */
  hostAlive(): boolean;
  /** The page-level surface for what the flow says after its host is gone. */
  sink: TagRunNoticeSink;
  /** A transient message for the host's own status region. */
  onFeedback(key: string, params: Record<string, unknown>): void;
  /** A report without a run succeeded — the host reloads the tag. */
  onCompleted(): void;
  /** The host grid's marking (emote ids), copied at the click so nothing done to the grid while the
   *  flow runs reaches it. Empty or absent: no marking. The clear-out proposes the marked emotes in
   *  the set; the play-in considers only the marked entries (`playInCandidates`). */
  markedIds?: readonly string[];
  /** A flow went ahead — a confirmed clear-out's run was handed over or its report for nothing
   *  ticked went out; a play-in's import run was started or its report for nothing to add went out.
   *  The host lets go of the marking the flow came from. Not called on a cancel or on any abort:
   *  there the marking still stands for the next attempt. */
  onRunCommitted?(): void;
}

type NoticeRouting = Pick<
  TagRunRequest,
  'channelName' | 'notice' | 'isCurrent' | 'hostAlive' | 'sink'
>;

/**
 * Shows a flow's failure banner under the host's buttons — unless the host has moved on to another
 * tag or channel since the click, whose buttons it would then appear under (dropped), or is gone
 * altogether (handed to the page-level sink). A retry that restarts the flow needs the host and
 * goes with it; only `keepRetryWithoutHost` (a report resend, which needs nothing from the host)
 * survives into the sink.
 */
export function raiseNotice(
  request: NoticeRouting,
  notice: TagRunNotice,
  keepRetryWithoutHost = false,
): void {
  if (!request.hostAlive()) {
    const { retry, ...rest } = notice;
    request.sink.raise(
      request.channelName,
      keepRetryWithoutHost && retry !== undefined ? notice : rest,
    );
    return;
  }
  if (request.isCurrent?.() ?? true) {
    request.notice.set(notice);
  }
}

/** Clears a stale banner before a new attempt — the host's, or the sink's once the host is gone. */
function clearNotice(request: NoticeRouting): void {
  if (request.hostAlive()) {
    request.notice.set(null);
  } else {
    request.sink.clear();
  }
}

/** A flow's acknowledgement: the host's output while it lives, the sink's event afterwards. */
export function tellFeedback(
  request: Pick<TagRunRequest, 'channelName' | 'hostAlive' | 'sink' | 'onFeedback'>,
  key: string,
  params: Record<string, unknown>,
): void {
  if (request.hostAlive()) {
    request.onFeedback(key, params);
  } else {
    request.sink.feedback(request.channelName, key, params);
  }
}

/** A report without a run succeeded: the same routing as `tellFeedback`. */
function tellCompleted(
  request: Pick<TagRunRequest, 'channelName' | 'hostAlive' | 'sink' | 'onCompleted'>,
): void {
  if (request.hostAlive()) {
    request.onCompleted();
  } else {
    request.sink.completed(request.channelName);
  }
}

/** What the shared steps hand over: the frozen operation, the entries read for it, the set owner
 *  and the complete live read. */
export interface PreparedTagRun {
  operationId: string;
  frozenSetId: string;
  /** The host's live active set for every later set guard — `null` once the host is torn down
   *  (`hostBoundActiveSet`), which every guard reads as a set switch. */
  activeEmoteSetId: Signal<string | null>;
  ownerTwitchChannelId: string | null;
  entries: EmoteTagEntries;
  live: SevenTvSetEntries;
}

export interface TagRunPreparation {
  kind: TagOperationKind;
  /** The banner key for a blocked set pre-check. */
  targetBlockedKey(resolution: Extract<DeleteTargetResolution, { status: 'blocked' }>): string;
  /** Starts the whole flow again with a fresh operation — the banner's retry for every block
   *  before a report. */
  restart(): void;
  /** Runs between the registration and the live read; `false` stops the flow quietly. */
  beforeLiveRead?(): Observable<boolean>;
}

/**
 * Starts a tag play-in. Steps after the shared ones (`prepareTagRun`):
 *
 * - **A grid marking** (`request.markedIds`, operator decision 2026-10-05) narrows the play-in to the
 *   marked entries before anything below: only they are partitioned, added and counted. A marking
 *   none of whose ids the tag still has an entry for stops the flow (`markedGone`) — the play-in
 *   would otherwise mark the tag as played in on the strength of nothing the person chose.
 * - **Nothing to add** (every entry is in the set under some entry): no run. The play-in report goes
 *   out at once with an empty id list, because that report is what marks the tag as played in (E26)
 *   — there is no run whose settlement could send it.
 * - **Otherwise** the ordinary import flow takes over, on the frozen set (`pinSetId`) and with the
 *   tag hook: its set guard right before the start, and `onNothingToImport` when the import's own
 *   checks find nothing left to add, which sends the same empty report for the same operation.
 *   `pending` ends at that hand-over: the import flow reports nothing back for a dismissed dialog,
 *   a cancelled token prompt, a refused start or a blocked pre-check, so holding it longer would
 *   lock the buttons for good.
 * - **The marking goes** once the play-in went ahead (`onRunCommitted`): right after the import run
 *   was started (the hook's `onStarted`), or when the report for nothing to add goes out. A dismissed
 *   confirmation, a cancelled token prompt or any abort keeps it.
 */
export function startTagPlayInFlow(deps: TagRunFlowDeps, request: TagRunRequest): void {
  prepareTagRun(
    deps,
    request,
    {
      kind: 'playIn',
      targetBlockedKey: (resolution) => playInTargetBlockedKey(resolution.reason),
      restart: () => startTagPlayInFlow(deps, request),
    },
    (prepared) => {
      const marking = request.markedIds ?? [];
      const candidates = playInCandidates(prepared.entries.entries, marking);
      if (marking.length > 0 && candidates.length === 0) {
        request.pending.set(false);
        raiseNotice(request, { key: 'tags.errors.markedGone' });
        return;
      }
      const candidateCount = candidates.length;
      const allPresentKey =
        marking.length > 0 ? 'tags.feedback.allMarkedPresent' : 'tags.feedback.allPresent';
      const reportNothingToAdd = (): void => {
        request.onRunCommitted?.();
        sendTagReport(
          request,
          () =>
            deps.tagService.reportPlacements(request.channelName, request.tag.id, {
              operationId: prepared.operationId,
              emoteSetId: prepared.frozenSetId,
              targetOwnerTwitchId: prepared.ownerTwitchChannelId,
              sevenTvEmoteIds: [],
            }),
          // From the entries this flow read, never from the answer: a replayed report carries no
          // outcome (docs/DECISIONS.md, #201 T-C: a replay is a success that says nothing else).
          () =>
            tellFeedback(request, pluralKey(candidateCount, allPresentKey), {
              count: candidateCount,
              tag: request.tag.name,
            }),
        );
      };

      const partition = partitionTagPlayIn(candidates, prepared.live);
      if (partition.toAdd.length === 0) {
        // No dialog and no import hook here, so no later set guard: the report marks the tag as
        // played in for the frozen set, which must still be the active one (host-bound read).
        if (prepared.activeEmoteSetId() !== prepared.frozenSetId) {
          request.pending.set(false);
          raiseNotice(request, { key: 'tags.errors.setChanged' });
          return;
        }
        reportNothingToAdd();
        return;
      }
      const source = buildTagImportSource(partition, request.tag, request.channelName);
      const target = toImportTarget(
        request.channelName,
        prepared.frozenSetId,
        prepared.frozenSetId,
        request.setName,
        { ownerTwitchChannelId: prepared.ownerTwitchChannelId, pinSetId: true },
      );
      request.pending.set(false);
      startImportFlow(deps, source, target, {
        context: { tagId: request.tag.id, operationId: prepared.operationId },
        frozenSetId: prepared.frozenSetId,
        activeEmoteSetId: prepared.activeEmoteSetId,
        onSetChanged: () => raiseNotice(request, { key: 'tags.errors.setChanged' }),
        onNothingToImport: reportNothingToAdd,
        onStarted: () => request.onRunCommitted?.(),
      });
    },
  );
}

/**
 * The steps both tag flows share, in their fixed order (see the file comment): freeze → entries →
 * pre-check → registration → (`beforeLiveRead`) → live read → `done`. Every block ends `pending`
 * and leaves a notice; `done` takes `pending` over.
 */
export function prepareTagRun(
  deps: TagRunFlowDeps,
  request: TagRunRequest,
  preparation: TagRunPreparation,
  done: (prepared: PreparedTagRun) => void,
): void {
  const frozenSetId = request.activeEmoteSetId();
  // The host offers the buttons only with a known active set; a click that outraces it losing that
  // has nothing to target.
  if (frozenSetId === null) {
    return;
  }
  const operationId = crypto.randomUUID();
  const activeEmoteSetId = hostBoundActiveSet(deps.destroyRef, request.activeEmoteSetId);
  const { channelName, tag } = request;
  const block = (notice: TagRunNotice): void => {
    request.pending.set(false);
    raiseNotice(request, notice);
  };
  clearNotice(request);
  request.pending.set(true);

  const readLive = (ownerTwitchChannelId: string | null, entries: EmoteTagEntries): void => {
    readLiveSetAliases(deps.httpClient, frozenSetId)
      .pipe(takeUntilDestroyed(deps.destroyRef))
      .subscribe((read) => {
        if (read.status === 'blocked') {
          block({ key: setReadReasonKey(read.reasonKey), retry: preparation.restart });
          return;
        }
        done({
          operationId,
          frozenSetId,
          activeEmoteSetId,
          ownerTwitchChannelId,
          entries,
          live: read.entries,
        });
      });
  };

  const register = (ownerTwitchChannelId: string | null, entries: EmoteTagEntries): void => {
    deps.tagService
      .registerOperation(channelName, tag.id, {
        operationId,
        kind: preparation.kind,
        emoteSetId: frozenSetId,
        targetOwnerTwitchId: ownerTwitchChannelId,
      })
      .pipe(timeoutReportAttempt(), takeUntilDestroyed(deps.destroyRef))
      .subscribe({
        next: () => {
          if (preparation.beforeLiveRead === undefined) {
            readLive(ownerTwitchChannelId, entries);
            return;
          }
          preparation.beforeLiveRead().subscribe((proceed) => {
            // The step before (a token prompt) is a dialog that outlives the caller; a caller gone
            // by its answer reads nothing more.
            if (proceed && !deps.destroyRef.destroyed) {
              readLive(ownerTwitchChannelId, entries);
            } else {
              // The registered operation stays unapplied on the server — harmless (spec 5.4).
              request.pending.set(false);
            }
          });
        },
        error: (error: unknown) => block(registrationFailureNotice(error, preparation.restart)),
      });
  };

  deps.tagService
    .listEntries(channelName, tag.id, frozenSetId)
    // Bounded like the reports and live reads: a request that never answers ends in the error
    // notice below instead of holding `pending` (and both buttons) forever.
    .pipe(timeoutReportAttempt(), takeUntilDestroyed(deps.destroyRef))
    .subscribe({
      next: (entries) => {
        // Set freeze (E29): the entries must describe the very set that was frozen, and it must
        // still be the active one — otherwise nothing is registered.
        if (entries.emoteSetId !== frozenSetId || !entries.isActiveSet) {
          block({ key: 'tags.errors.setChanged' });
          return;
        }
        resolveDeleteTarget(deps, frozenSetId, channelName)
          .pipe(takeUntilDestroyed(deps.destroyRef))
          .subscribe((resolution) => {
            if (resolution.status === 'blocked') {
              block({
                key: preparation.targetBlockedKey(resolution),
                // Only "could not be checked right now" is worth another try.
                ...(resolution.reason === 'unavailable' ? { retry: preparation.restart } : {}),
              });
              return;
            }
            // No second look at the page's active set here, unlike the delete chain after its
            // own pre-check (`startDeleteFlow`, Codex C3): nothing is written to 7TV before a
            // set guard, and the guards are the gate — the clear-out's confirm, the import hook's
            // start, and for "all present" the check right before its report. A switch while this
            // check was out costs at most a dialog for a set that already moved on, whose
            // confirmation then aborts with "set changed".
            register(resolution.ownerTwitchChannelId, entries);
          });
      },
      error: () => block({ key: 'tags.errors.entriesUnavailable', retry: preparation.restart }),
    });
}

/**
 * The host's live active set, bound to the host's lifetime: the host's own value while it lives,
 * `null` from its teardown on (see the file comment). Built on the click, while the host is alive.
 */
function hostBoundActiveSet(
  destroyRef: DestroyRef,
  activeEmoteSetId: Signal<string | null>,
): Signal<string | null> {
  const alive = signal(!destroyRef.destroyed);
  if (alive()) {
    destroyRef.onDestroy(() => alive.set(false));
  }
  return computed(() => (alive() ? activeEmoteSetId() : null));
}

/**
 * Sends a report that has no run around it (the empty play-in, the clear-out with nothing ticked).
 * `pending` holds while it is out; a failure leaves a banner whose retry sends the **same** request
 * — the same operation id, so a report that did reach the server is only replayed (E27).
 *
 * Each attempt is bounded by the run-backed reports' timeout, so a report that never answers ends in
 * the same failure banner instead of leaving the buttons locked for good.
 *
 * The report is not bound to the host's lifetime: it is the only thing that marks the tag, so it
 * finishes even when the host is torn down meanwhile. Its outcome then goes to the sink — the
 * failure banner with its retry included, since nothing else could resend it.
 */
export function sendTagReport(
  request: Pick<
    TagRunRequest,
    | 'channelName'
    | 'pending'
    | 'notice'
    | 'onCompleted'
    | 'onFeedback'
    | 'isCurrent'
    | 'hostAlive'
    | 'sink'
  >,
  send: () => Observable<unknown>,
  onSuccess: () => void,
): void {
  clearNotice(request);
  request.pending.set(true);
  send()
    .pipe(timeoutReportAttempt())
    .subscribe({
      next: () => {
        request.pending.set(false);
        onSuccess();
        tellCompleted(request);
      },
      error: () => {
        request.pending.set(false);
        raiseNotice(
          request,
          {
            key: 'tags.errors.reportFailed',
            retry: () => sendTagReport(request, send, onSuccess),
          },
          true,
        );
      },
    });
}

/** The play-in's own, neutral wording for a blocked pre-check — the delete's `massDelete.errors.*`
 *  texts speak of deleting. */
function playInTargetBlockedKey(reason: TargetCheckBlockReason): string {
  switch (reason) {
    case 'notEditable':
      return 'tags.errors.target.notEditable';
    case 'notSelectable':
      return 'tags.errors.target.notSelectable';
    case 'unavailable':
      return 'tags.errors.target.unavailable';
  }
}

/** The live read's block reason (`readLiveSetAliases`) in the tag family. */
function setReadReasonKey(reasonKey: string): string {
  switch (reasonKey) {
    case MEMBER_READ_TRUNCATED_REASON_KEY:
      return 'tags.errors.setReadIncomplete';
    case MEMBER_READ_UNAVAILABLE_REASON_KEY:
      return 'tags.errors.setReadUnavailable';
    default:
      // Only the two above exist; an unknown one is still a read that must not decide anything.
      return 'tags.errors.setReadUnavailable';
  }
}

/** 403: the owner check says this user may not write the set — no retry, it would only say so
 *  again. 503: the owner check could not run — try again. Anything else: the registration failed. */
function registrationFailureNotice(error: unknown, restart: () => void): TagRunNotice {
  const status = error instanceof HttpErrorResponse ? error.status : 0;
  if (status === 403) {
    return { key: 'tags.errors.noWriteRight' };
  }
  if (status === 503) {
    return { key: 'tags.errors.ownershipUnavailable', retry: restart };
  }
  return { key: 'tags.errors.registrationFailed', retry: restart };
}
