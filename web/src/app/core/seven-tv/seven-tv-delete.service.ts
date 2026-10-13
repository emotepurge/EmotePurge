import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, linkedSignal, signal, WritableSignal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import {
  MonoTypeOperatorFunction,
  Observable,
  catchError,
  defer,
  map,
  of,
  retry,
  switchMap,
  throwError,
  timeout,
  timer,
} from 'rxjs';

import { ChannelService } from '../channels/channel.service';
import { EmoteTagService } from '../tags/emote-tag.service';
import { SevenTvEntryGeneration, SevenTvEntryTarget } from './seven-tv-entry-target';
import {
  SyncDeletedInSetResponse,
  SyncInSetBody,
  SyncInSetEntry,
} from './seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from './seven-tv-emote-set.service';
import {
  RunItemStatus,
  RunOperation,
  RunQueueEmote,
  RunQueueItem,
  RunResult,
  SevenTvMutation,
  SevenTvRunEngine,
  StepGate,
  SkipReasonKey,
} from './seven-tv-run-engine';
import { SevenTvRunArbiter } from './seven-tv-run-arbiter';
import { RunRecordBase, SevenTvRunLifecycle } from './seven-tv-run-lifecycle';
import {
  CANCEL_SETTLE_GRACE_MS,
  EntryBaseline,
  SET_ENTRIES_READ_TIMEOUT_MS,
  UnexpectedEntryRemoval,
  entryPostCheckOwed,
  entryTimeKey,
  findUnexpectedRemovals,
  freezeBaselineGroup,
  mergeIntoBaseline,
  mergeUnexpectedRemovals,
  settleDeleteResult,
  settleEntryDeleteRun,
  unknownCount,
} from './seven-tv-run-settlement';
import { SevenTvSetEntries, SevenTvSetEntry, loadSevenTvSetEntries } from './seven-tv-set-entries';
import { SevenTvTokenService } from './seven-tv-token.service';
import {
  SyncReportOutcome,
  SyncReportReason,
  SyncReportState,
  classifySyncInSetFailure,
  classifyTagReportFailure,
  classifySyncInSetResponse,
  isChannelMismatch,
} from './sync-report-outcome';
import { DeleteTagContext, deriveTagKeptIds } from './tag-run-settlement';

/** Kept under its historical name — the engine's constant is the same value. */
export { RUN_DELAY_MS as DELETE_DELAY_MS } from './seven-tv-run-engine';
// Exported for the restore service, which reports its run with the identical policy.
export const MAX_AUTOMATIC_SYNC_RETRIES = 2;
// Multiplied by the attempt number, so the two automatic attempts land at 2s and 4s. Kept short on
// purpose: the deletions themselves are already done, the admin is waiting on a verdict, and a
// manual retry button covers the cases a short backoff cannot.
export const SYNC_RETRY_DELAY_MS = 2000;
/** Time budget of one attempt of a closing report (`sync-imported`, `sync-deleted`,
 *  `sync-restored`) — #256, Plan-256 Festlegung 15. A report that never answers would otherwise
 *  keep its run open for good: never `closed`, never closable, and for a destructive run the tab's
 *  unload guard armed forever. More generous than the 20 s re-read, because the report kicks off
 *  server-side resync steps. An attempt that runs out counts as a transient failure (see
 *  {@link timeoutReportAttempt}), so the same retries follow, then `failed`/`unavailable`. */
export const REPORT_TIMEOUT_MS = 30_000;

/** Ends one attempt of a closing report after {@link REPORT_TIMEOUT_MS} with a status-`0`
 *  `HttpErrorResponse` — the shape of a network failure, so the retry policy retries it and
 *  `classifySyncInSetFailure` reads it as `'unavailable'`. Placed *before* the retry operator, so
 *  every attempt gets its own budget. */
export function timeoutReportAttempt<T>(): MonoTypeOperatorFunction<T> {
  return timeout<T, Observable<never>>({
    first: REPORT_TIMEOUT_MS,
    with: () =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 0,
            statusText: `No answer within ${REPORT_TIMEOUT_MS} ms`,
          }),
      ),
  });
}

/** How long a confirmed delete that never became a run keeps `confirmedRunPending` set, so the
 *  panel's abort notice ("Nothing was deleted." plus the reason) can still be read after the host's
 *  dock lost every other reason to stay mounted. Longer than the restore/import services' 4 s
 *  duplicate notice: this one reports that an irreversible action the user explicitly confirmed did
 *  *not* happen, and it is two sentences rather than a count. Self-clearing for the same reason
 *  those are (docs/UI-Designsprache.md §4.5) — there is no run or queue for a dismiss button to
 *  attach to. */
export const ABORTED_DELETE_NOTICE_MS = 8000;

/** How long a slot delete run reuses its last read of the set before an entry row's attempt
 *  (Plan-378 4.4 rule 2 b, D21) — measured from the read's request start, which is conservative.
 *  Only for an emote the read shows with a single entry; one with two or more is read afresh before
 *  every attempt, whatever the age (rule 2 c, D33, AK 51). */
export const ENTRY_READ_REUSE_MS = 5000;

/** Time budget of the read in front of an entry row's attempt (Plan-378 4.4 rule 3). The engine
 *  gives a `beforeStep` hook no deadline, so the read brings its own — the same 20 s as the undo's
 *  recheck (`RECHECK_READ_TIMEOUT_MS`), and like that one its own constant: a gate in front of a
 *  mutation, not a settlement after the run. A read that runs out is a read failure (fail-closed). */
export const ENTRY_BINDING_READ_TIMEOUT_MS = 20_000;

/** After this many failed or incomplete binding reads in a row, every remaining entry row is
 *  skipped without another read (Plan-378 4.4 rule 1, the mirror of the undo's
 *  `MAX_CONSECUTIVE_RECHECK_FAILURES`); id rows keep running. */
export const MAX_CONSECUTIVE_ENTRY_READ_FAILURES = 3;

const PROCEED: StepGate = { kind: 'proceed' };

/** v4 dropped the `action` enum in favour of one field per operation; the emote travels inside the
 *  `EmoteSetEmoteId` input object rather than as a sibling argument, and variable types are `Id!`
 *  instead of `ObjectID!`. Removal has no alias, unlike `addEmote` in the import/restore services.
 *  One `removeEmote` takes every entry of the id. Shared with the import run's replace rows, the
 *  undo, the vote page's removals and the delete run's id rows; an entry row of a slot delete run
 *  uses {@link REMOVE_SLOT_ENTRY_MUTATION} instead. */
export const REMOVE_EMOTE_MUTATION: SevenTvMutation = {
  query: `
  mutation RemoveEmote($setId: Id!, $emoteId: Id!) {
    emoteSets {
      emoteSet(id: $setId) {
        removeEmote(id: { emoteId: $emoteId }) {
          id
        }
      }
    }
  }
`,
  resultPath: ['emoteSets', 'emoteSet', 'removeEmote'],
};

/** The slot-delete counterpart of {@link REMOVE_EMOTE_MUTATION} (Plan-378 [E1]): removes only the
 *  entry of the id that sits under `alias`. Its own constant — not an optional `$alias` on the one
 *  above — so every other caller keeps sending today's bytes, and `$alias` is **required**
 *  (`String!`): a request for one entry can never leave without an alias, which 7TV would read as
 *  "every entry of the id". */
export const REMOVE_SLOT_ENTRY_MUTATION: SevenTvMutation = {
  query: `
  mutation RemoveSlotEntry($setId: Id!, $emoteId: Id!, $alias: String!) {
    emoteSets {
      emoteSet(id: $setId) {
        removeEmote(id: { emoteId: $emoteId, alias: $alias }) {
          id
        }
      }
    }
  }
`,
  resultPath: ['emoteSets', 'emoteSet', 'removeEmote'],
};

/** The one thing that makes this run a *delete* — everything else lives in the engine.
 *  `transportLossIsUnknown` (#275, Plan-275 Festlegung 1): a `REMOVE` whose answer never came out
 *  of 7TV's GraphQL layer — no answer, a 5xx, a 200 without the `removeEmote` result (#285), or a
 *  `cancel()` that aborted it in flight — may still
 *  have taken the emote out of the set, so its row ends `unknown` rather than `failed`/`cancelled`
 *  and is cleared up by the run's one re-read (`SevenTvDeleteService.onRunComplete`). A 4xx and a
 *  GraphQL rejection stay `failed`, a cancel between two rows or in a rate-limit pause stays
 *  `cancelled`. */
const REMOVE_OPERATION: RunOperation = {
  label: 'mass delete',
  // By row type: an entry row removes its one entry by alias, an id row takes every entry of the id.
  buildRequest: (setId, emote) =>
    emote.entry
      ? {
          mutation: REMOVE_SLOT_ENTRY_MUTATION,
          variables: { setId, emoteId: emote.sevenTvEmoteId, alias: emote.entry.alias },
        }
      : {
          mutation: REMOVE_EMOTE_MUTATION,
          variables: { setId, emoteId: emote.sevenTvEmoteId },
        },
  transportLossIsUnknown: true,
};

/**
 * What one slot delete run keeps between the attempts of its entry rows (Plan-378 4.4) — in the
 * run's own closure, never on the engine or a record (the pattern of `SevenTvUndoService`'s
 * `createOperation`). Exists only for a run with at least one entry row.
 */
interface EntryRunContext {
  /** The 7TV ids of the run's entry rows — the only emotes the baseline follows. */
  readonly entryEmoteIds: ReadonlySet<string>;
  /** The comparison baseline per emote (4.4 step 5) — see `mergeIntoBaseline`. */
  readonly baseline: EntryBaseline;
  /** Row key → the entry an attempt of that row was let through for. Dropped again when a later
   *  attempt of the same row (after a rate-limit pause) is skipped. */
  readonly attempted: Map<string, { sevenTvEmoteId: string; timeKey: string; alias: string }>;
  /** Unexpected removals a binding read already noticed (4.4 step 6) — kept even when the re-read
   *  after the run fails, merged with what that re-read finds. */
  earlyUnexpected: UnexpectedEntryRemoval[];
  /** Per 7TV id of an entry row, the emote's default name as the last read that listed one gave it
   *  — what an unexpectedly removed entry without an alias is called by (the read that notices it
   *  gone may no longer list the emote at all). */
  readonly defaultNames: Map<string, string>;
  /** The last complete read and when its request started; `null` before the first one. */
  held: { read: SevenTvSetEntries; startedAt: number } | null;
  consecutiveReadFailures: number;
  /** What the last failed read was — the reason every row skipped after three in a row carries. */
  lastReadFailure: 'unavailable' | 'truncated';
}

/** How an entry row's live entries at its add date decide its attempt (Plan-378 4.4 step 4). */
type EntryBinding =
  | { kind: 'proceed' }
  | { kind: 'departed' }
  | { kind: 'renamed'; newName: string }
  | { kind: 'noAlias' };

/** The public input contract for a delete/restore run — deliberately its own interface, not an
 *  alias of the engine's `RunQueueEmote`: a caller here never has to think about a queue `key`.
 *  `startDelete`/`startRestore` mint it themselves from the 7TV id (spec #200, 7.2), so the run's
 *  identity never depends on whether the row has a local `Emote.Id`. */
export interface DeleteQueueEmote {
  /** Local `Emote.Id` — optional (spec #200, 7.2): a live member of a non-active set may never
   *  have had a row here. Only ever written into the purge protocol; the run does not read it. */
  emoteId?: string;
  sevenTvEmoteId: string;
  name: string;
  /** Every alias the emote sits under in the set — two for a #74 duplicate cell. The delete
   *  records them in the protocol; the restore sends one `ADD` per alias. Omitted means `[name]`. */
  aliases?: readonly string[];
  /** Set on an entry row: removes exactly this one set entry (see `SevenTvEntryTarget`), keyed
   *  `${sevenTvEmoteId}#${entry.cardKey}`. Absent: an id row, one per 7TV id, as always. */
  entry?: SevenTvEntryTarget;
}
/** Historical aliases — the panel and both host pages import these names. */
export type DeleteItemStatus = RunItemStatus;
export type DeleteQueueItem = RunQueueItem;

/**
 * One delete run, from the moment it starts to the moment its closing report reaches an end state
 * (#256, `SevenTvRunLifecycle`): `running → (settling →) reporting → closed` on its own record. A
 * run that ends with at least one `unknown` row — a `REMOVE` whose answer was lost or that a cancel
 * aborted in flight — is `settling` while the set is re-read once (#275, Plan-275 Festlegung 2,
 * lifting Plan-256 Festlegung 5); a run without one goes straight to `reporting`/`closed` as
 * before. `runId` is the identity a late answer or a manual retry finds it by; the record is
 * replaced by a new object on every change, never mutated. `destructive` is always `true` (Plan-256
 * Festlegung 6): every delete row removes something from the set, so a delete run arms the tab's
 * unload guard from `startDelete` until `closed` — `settling` included — the same way an import's
 * `replace` plan does.
 */
export interface DeleteRunInfo extends RunRecordBase {
  /** The channel of the page the run was started on — the purge protocol's envelope
   *  `channelName`, its filename and `resetIfChannelChanged` read it (spec 6.5). No longer the
   *  addressee of the report: that is the set (`setId`), with `expectedChannelName` beside it. */
  channelName: string;
  /** The tracked channel the report expects to touch (spec 4.6 point 21, E18): the page's channel
   *  when the run's set is its active one, otherwise `null`. Frozen with the run, sent with the
   *  report and every retry, and the channel of the fallback resync after a first report that
   *  failed for good (addendum N1). */
  expectedChannelName: string | null;
  /** The set the run removes from, frozen when it starts (spec #200, 7.2, AK 71): the first
   *  report and every retry name this set, whatever the page's set dropdown shows by then. */
  setId: string;
  /** The set owner's Twitch id, from the pre-check that started this run (owner-hint design 3.6) —
   *  `null` when the pre-check found none, or when the run started without one (the pre-check
   *  always runs in `openConfirmDialog`, so this is the no-owner-found case only). Sent with every report and retry, and carried into the
   *  purge protocol as `PurgeRunMeta.targetOwnerTwitchId`, never re-resolved later. */
  targetOwnerTwitchId: string | null;
  /** `null` while the run is in flight **and while it is `settling`**; set once, to the settled
   *  outcome, in the same update that moves the run to `reporting` (Plan-275 Festlegung 10) — so
   *  every reader (`lastRun`, the protocol download, the page's `watchRunSettle`) only ever sees
   *  the one final result, never an interim snapshot with rows the re-read is about to clear up. */
  result: RunResult | null;
  /** This run's `sync-deleted` report — `SevenTvDeleteService.syncReport` projects it for the
   *  shown run. */
  syncReport: SyncReportState;
  /** Projected by `syncReportReason`. */
  syncReportReason: SyncReportReason | null;
  /** The tag removal this run executes (#201 T-C, spec 7.2/7) — the registered operation, the
   *  snapshot of the placements read in the preview and the ticked/unticked own ids, frozen at
   *  `startDelete`; absent for every other delete. Optional (like the two report fields) so the
   *  run records T-A's specs build by hand stay valid. */
  tag?: DeleteTagContext;
  /** This run's removal report (#201 T-C, spec E14) — `'idle'` for every run without `tag`, and
   *  then never anything else. A tag run always sends it once settled, with nothing removed too: the
   *  report is also what moves the unremoved own placements and deactivates the tag. Projected by
   *  `tagRemovalReport`. */
  tagRemovalReport?: SyncReportState;
  /** Projected by `tagRemovalReportReason`. */
  tagRemovalReportReason?: SyncReportReason | null;
  /** Slot delete runs only (Plan-378 4.5, D33, AK 40): entries of an emote the run removed from
   *  that are gone from the set although none of the run's own removals accounts for them —
   *  possibly taken by one of its requests (another editor renamed between the read and the
   *  request), possibly by someone else. Set in the same update as `result` (empty when there are
   *  none), never reported to the server: the run did not confirm them, and REST or an EventAPI pull
   *  reconciles them. Absent on a run without entry rows. */
  unexpectedRemovals?: readonly UnexpectedEntryRemoval[];
  /** Slot delete runs only: the run owed the post-check, but its re-read failed, ran out or came
   *  back incomplete — nobody can tell whether other entries were affected. Set with `result`, no
   *  second attempt. Absent on a run without entry rows. */
  postCheckUnavailable?: boolean;
}

@Injectable({ providedIn: 'root' })
export class SevenTvDeleteService {
  private readonly channelService = inject(ChannelService);
  private readonly emoteSetService = inject(SevenTvEmoteSetService);
  private readonly emoteTagService = inject(EmoteTagService);
  private readonly httpClient = inject(HttpClient);
  private readonly translocoService = inject(TranslocoService);

  /** Own engine instance (not a shared singleton), so `isRunning` can never mean "the *other*
   *  service is busy". All pacing/backoff/token mechanics live there — see SevenTvRunEngine. */
  private readonly engine = new SevenTvRunEngine(
    this.httpClient,
    inject(SevenTvTokenService),
    inject(TranslocoService),
  );

  /** Every open run of this service, by id, plus the one the dock shows (#256). */
  private readonly lifecycle = new SevenTvRunLifecycle<DeleteRunInfo>(
    'delete',
    // A tag run's removal report holds `closed` like the sync report does (#201 T-C, E14).
    (run) => run.syncReport === 'pending' || run.tagRemovalReport === 'pending',
  );

  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  /** The run this service is currently showing — in flight (`result === null`) or finished. Its
   *  record is replaced by a new object on every change; `runId` is its identity. Writable because
   *  specs drive the dock through it; production code only writes through the lifecycle. */
  readonly run: WritableSignal<DeleteRunInfo | null> = this.lifecycle.shown;

  /** The rows to show (Plan-275 Festlegung 11): the shown run's settled `result.items` once it has
   *  one, the engine's queue otherwise — live while a run is in flight, and the engine's own
   *  snapshot (its `unknown` rows included) while the shown run is `settling`. After the settle
   *  this is *that* run's outcome, with every row the re-read confirmed shown `done`. A
   *  `linkedSignal` rather than a `computed`, so specs can still drive a dock through
   *  `queue.set(...)` (Plan-256 Festlegung 14); production code never writes it. */
  readonly queue = linkedSignal<DeleteQueueItem[]>(
    () => this.run()?.result?.items ?? this.engine.queue(),
  );

  /** True while any run of this service re-reads its `unknown` rows or reports — shown or not
   *  (#256, contract P1; #275 for `settling`). The arbiter counts both as busy, so no other run can
   *  start until the re-read has settled and the report reached an end state. */
  readonly isSettling = this.lifecycle.isSettling;

  /** True while any run of this service is not `closed` — every delete row is destructive (Plan-256
   *  Festlegung 6), so this holds from `startDelete` to `closed`, shown or not (#256, contract P3),
   *  through a `settling` re-read too (Plan-275 Festlegung 20). Worst case after the last click:
   *  `CANCEL_SETTLE_GRACE_MS` + `SET_ENTRIES_READ_TIMEOUT_MS` + three `REPORT_TIMEOUT_MS` attempts
   *  with their 2 s and 4 s pauses ≈ 119 s.
   *  The arbiter's unload guard (the union over every run service) reads it: until the run
   *  closes, its report has not reached an end state, and closing the tab could lose it. */
  readonly destructiveOpen = this.lifecycle.destructiveOpen;

  /** State of the shown run's closing sync-deleted call. `linkedSignal` projection of the shown
   *  record (#256, Plan-256 Festlegung 14) — writable so specs can drive a dock directly; production
   *  code never writes it, only the record. Consumers must wait for a terminal value before
   *  optimistically removing rows: 'failed'/'partial' means the backend does not (fully) know about
   *  the deletion yet, so filtering the list client-side would show a state that isn't real. */
  readonly syncReport = linkedSignal<SyncReportState>(() => this.run()?.syncReport ?? 'idle');

  /** Why `syncReport` is `'failed'`/`'partial'` (spec E23), `null` otherwise — the dock shows it as
   *  its own line under the report notice. Projection, like `syncReport`. */
  readonly syncReportReason = linkedSignal<SyncReportReason | null>(
    () => this.run()?.syncReportReason ?? null,
  );

  /** State of the shown run's tag removal report (#201 T-C) — `'idle'` for a run that is not a tag
   *  removal. Projection of the shown record, like `syncReport`. */
  readonly tagRemovalReport = linkedSignal<SyncReportState>(
    () => this.run()?.tagRemovalReport ?? 'idle',
  );

  /** Why `tagRemovalReport` is `'failed'`, `null` otherwise. */
  readonly tagRemovalReportReason = linkedSignal<SyncReportReason | null>(
    () => this.run()?.tagRemovalReportReason ?? null,
  );

  /** The finished run, kept for the summary/protocol UI (A6) — unchanged shape for
   *  `mass-delete-panel.ts`/`usage-stats-page.ts` (#256 Naht 2.4). Projection of `run()`: `null`
   *  while a run is in flight, while it is `settling` or when nothing is shown, the frozen
   *  `{setId, channelName, result}` once the shown run has its settled result — `result` keeps the
   *  identity `settleRun` gave it across every later report patch, which is what
   *  `usage-stats-page.ts`'s `watchRunSettle` dedupes on. */
  readonly lastRun = linkedSignal<{
    setId: string;
    channelName: string;
    targetOwnerTwitchId: string | null;
    result: RunResult;
  } | null>(() => {
    const shown = this.run();
    return shown === null || shown.result === null
      ? null
      : {
          setId: shown.setId,
          channelName: shown.channelName,
          targetOwnerTwitchId: shown.targetOwnerTwitchId,
          result: shown.result,
        };
  });

  /**
   * A delete the user is deciding on, or has decided on, that is not (yet) a run: `MassDeletePanel`
   * has the confirmation open, its pre-run live alias read is out, or that read has just ended in an
   * abort whose notice is the only outcome there is to show. None of those show up in
   * `isRunning`/`queue`, which is the problem this exists to solve — the host dock's own gate
   * (`action-dock.ts`, `usage-stats-page.ts`'s `dockVisible`) counts marked items and shown panels,
   * so a pushed reload that prunes every marked key unmounts the dock and takes `MassDeletePanel`
   * down with it. The CDK dialog is opened without a `viewContainerRef`, so it survives that and the
   * user still clicks Delete — against a destroyed panel, which by contract starts nothing
   * (`abortReasonBeforeStart`) and has no view left to say so on: no `REMOVE` sent, no notice,
   * nothing. The dock treats this claim exactly like an in-flight run, the same role
   * `duplicateNoticePending` plays for a fully-refused restore/import.
   *
   * The claim is therefore taken when the **confirmation opens**, not when the read starts: the
   * window that must be survived begins with the modal, and the no-read branch has no read to hang
   * it on at all.
   *
   * Written only through `beginConfirmedRun`/`endConfirmedRun`/`clearConfirmedRun` below.
   */
  readonly confirmedRunPending = signal(false);

  /** A confirmed delete's live alias read (`readLiveAliasesThenDelete` in `delete-flow.ts`, spec
   *  §37/§38) is out: the confirmation is closed, the run does not exist yet, and the read may
   *  still block it (#280). Narrower than `confirmedRunPending`, which spans the open confirmation
   *  too. Registered with the arbiter, whose `startLocked` every 7TV start trigger binds to, and
   *  spoken by `DockOutcomeAnnouncer`. Root-level, not the panel's own, so the announcer the page
   *  mounts can read it; set and cleared by the panel around that one bounded read, released by its
   *  `finalize` on every exit — the read is not dropped with the panel, so a torn-down panel cannot
   *  leave it set. */
  readonly startCheckPending = signal(false);

  private confirmedRunTimeout: ReturnType<typeof setTimeout> | undefined;

  /** `true` only for the synchronous span of this service's own `cancel()`: `engine.cancel()` calls
   *  `onRunComplete` synchronously (`finish()`), and nothing else ever cancels this engine — so
   *  `onRunComplete` reading `true` here means "this run ended because the user cancelled it"
   *  (Plan-275 Festlegung 5). Per run by construction, no engine field. */
  private cancelInProgress = false;

  constructor() {
    // The one run-service → arbiter edge (#256, contract P4): the arbiter derives "busy" and the
    // tab's unload guard from these three signals; it does not know this service otherwise.
    inject(SevenTvRunArbiter).register({
      kind: 'delete',
      isRunning: this.isRunning,
      isSettling: this.isSettling,
      destructiveOpen: this.destructiveOpen,
      startCheckPending: this.startCheckPending,
    });
  }

  /** The delete confirmation is open — hold the dock (and the panel inside it) until one of the two
   *  releases below. Every exit of the confirmation has to reach one of them. */
  beginConfirmedRun(): void {
    clearTimeout(this.confirmedRunTimeout);
    this.confirmedRunPending.set(true);
  }

  /**
   * The confirmed delete has been attempted and was either started or aborted. A started run carries
   * the dock by itself from here (`isRunning`/`queue`), so the claim is dropped at once; an abort has
   * nothing but its notice, so the claim is held for `ABORTED_DELETE_NOTICE_MS` and then dropped.
   * Asking `isRunning()` rather than taking the answer as a parameter keeps every caller from having
   * to agree on what "started" means.
   */
  endConfirmedRun(): void {
    clearTimeout(this.confirmedRunTimeout);
    // Only a claim that is still held may be extended into a notice window. Something else can have
    // dropped it from under this delete while its read was out — `reset()`, or the workspace
    // switching channel — and in both cases the dock it belonged to is gone; re-arming a timer here
    // would pin an empty one somewhere the aborted delete never belonged.
    if (!this.confirmedRunPending()) {
      return;
    }
    if (this.isRunning()) {
      this.confirmedRunPending.set(false);
      return;
    }
    this.confirmedRunTimeout = setTimeout(
      () => this.confirmedRunPending.set(false),
      ABORTED_DELETE_NOTICE_MS,
    );
  }

  /** Drops the claim at once, without the notice window `endConfirmedRun` grants: nothing was
   *  confirmed and nothing has to be read, so keeping an otherwise empty dock up for 8 s would be
   *  exactly the empty bar `actionDockHasContent` exists to prevent. Three cases: the dismissed
   *  confirmation, `reset()`, and the channel workspace moving to another channel — the last two
   *  because an abort notice belongs to the dock it was raised in and to no other. */
  clearConfirmedRun(): void {
    clearTimeout(this.confirmedRunTimeout);
    this.confirmedRunPending.set(false);
  }

  /** `expectedChannelName` is `channelName` when `setId` is the page's active set, `null`
   *  otherwise (spec 6.5) — the caller knows which, this service does not. `targetOwnerTwitchId` is
   *  the owner hint the pre-check that led to this call resolved (owner-hint design 3.6) — frozen
   *  onto the record here, never re-resolved by this service. A run that starts is shown at once;
   *  the run shown before it goes on to close on its own record (#256). `tag` is only for a tag
   *  removal (#201 T-C, `tag-removal-flow.ts`): frozen onto the record, it makes the settled run
   *  send the removal report as a second report beside `sync-deleted`. */
  startDelete(
    setId: string,
    channelName: string,
    emotes: DeleteQueueEmote[],
    expectedChannelName: string | null,
    targetOwnerTwitchId: string | null,
    tag?: DeleteTagContext,
  ): void {
    const previousShown = this.lifecycle.shown();
    const runId = this.lifecycle.createRunId();
    const started: DeleteRunInfo = {
      runId,
      phase: 'running',
      destructive: true,
      channelName,
      expectedChannelName,
      setId,
      targetOwnerTwitchId,
      result: null,
      syncReport: 'idle',
      syncReportReason: null,
      ...(tag === undefined ? {} : { tag, tagRemovalReport: 'idle', tagRemovalReportReason: null }),
    };
    // Opened *before* the engine is asked to start (#256 review finding): the engine answers
    // asynchronously in practice, but only this ordering guarantees that a synchronous
    // `onComplete` — however unlikely — always finds its record already registered, rather than
    // updating a run the lifecycle does not know about yet, which would then never close.
    this.lifecycle.open(started);
    const queue = toDeleteQueue(emotes);
    // A run of id rows only (every run with the flag off) gets no hook at all — the operation, its
    // requests and its reads are byte for byte what they were (Plan-378 1).
    const context = createEntryRunContext(queue);
    const engineStarted = this.engine.start(
      setId,
      queue,
      context === null ? REMOVE_OPERATION : this.createEntryOperation(context),
      (result) => this.onRunComplete(runId, result, context),
    );
    if (!engineStarted) {
      // Refused (already running, empty list, no token) — take the just-opened record back and
      // restore whatever was shown before it, which may be another run still settling its report.
      this.lifecycle.discardUnstarted(runId, previousShown);
    }
  }

  /** Stops the run. A `REMOVE` still in flight ends its row `unknown` (`REMOVE_OPERATION`), and
   *  the run's re-read then waits `CANCEL_SETTLE_GRACE_MS` first — see `onRunComplete`. */
  cancel(): void {
    this.cancelInProgress = true;
    try {
      this.engine.cancel();
    } finally {
      this.cancelInProgress = false;
    }
  }

  /** Clears what the dock shows — and only that (#256, Plan-256 Festlegung 3). The shown run goes
   *  on on its own record: a run still in flight runs to its end (never cancelled here: a request
   *  7TV may already have applied must still be reported), a re-read still out settles it (#275),
   *  and its report goes out and is answered.
   *  The engine's queue belongs to a run in flight until `finish()` has built its result from it,
   *  so it is cleared then (`onRunComplete`), not here. */
  reset(): void {
    if (!this.engine.isRunning()) {
      this.engine.reset();
    }
    this.lifecycle.detach();
    // The restore service clears its own transient notice flag here for the same reason: whatever
    // this dock was still holding open, the user has dismissed it.
    this.clearConfirmedRun();
  }

  /** The panel is a root-service singleton, so a finished run used to follow the user into the
   *  next channel's workspace, still showing the previous channel's counts. Since #256 (Plan-256
   *  Festlegung 13) this only fires for a `closed` run: a run still reporting follows the user for
   *  the few seconds until its report reaches an end state — dropping it mid-report would leave a
   *  later `failed` with no dock to show it and no retry to reach it (Codex-Befund 2). */
  resetIfChannelChanged(channelName: string): void {
    const current = this.run();
    if (current === null || current.phase !== 'closed' || current.channelName === channelName) {
      return;
    }
    this.reset();
  }

  /** Manual retry for the closing report. The 7TV deletions are long done at this point, so this
   *  only re-sends the bookkeeping call — safe to repeat, ids already archived still count.
   *  Set, expected channel *and* keys come from the same record, so a retry can never mix one
   *  run's ids with another's target or with a set chosen after the run started (R15, AK 71). A
   *  retry on a closed run does not reopen it (#256): it is a new report on a closed run, and
   *  neither the arbiter nor the unload guard sees it. */
  retrySyncReport(): void {
    const current = this.run();
    if (
      current === null ||
      current.syncReport === 'pending' ||
      // addendum N4, AK 40: either channel-mismatch reason is recorded and, for
      // activeSetDiffers, its resync already runs — a retry could only write the same mismatch
      // again.
      isChannelMismatch(current.syncReportReason) ||
      current.result === null ||
      current.result.doneKeys.length === 0
    ) {
      return;
    }

    // Derived from the result on the record, never from the page: the body is the first one's.
    this.reportDeleted(current.runId, deletedReportOf(current.result));
  }

  /** Manual retry for the removal report of the shown run (#201 T-C) — sends the same body again,
   *  the same `operationId` included, so the server answers a report it already applied as a
   *  replay. Not gated on any removed key: a report with nothing removed is what moves the own
   *  placements and deactivates the tag. Like `retrySyncReport` it never reopens a closed run. */
  retryTagRemovalReport(): void {
    const current = this.run();
    if (
      current === null ||
      current.tag === undefined ||
      current.tagRemovalReport === 'pending' ||
      current.result === null
    ) {
      return;
    }

    this.reportTagRemoval(current.runId);
  }

  /**
   * The operation of a run with at least one entry row: the removal itself is `REMOVE_OPERATION`'s,
   * plus a `beforeStep` hook that binds every attempt of an entry row to a read of the set
   * (Plan-378 4.4, D21). The engine asks the hook again before every rate-limit retry, so a retry is
   * bound afresh too (A4); should a future transport retry ever bypass `runGatedOne`, the binding
   * would go with it. Id rows pass straight through, without a read.
   */
  private createEntryOperation(context: EntryRunContext): RunOperation {
    return {
      ...REMOVE_OPERATION,
      beforeStep: (setId, emote) =>
        emote.entry ? this.bindBeforeRemove(context, setId, emote, emote.entry) : of(PROCEED),
    };
  }

  /**
   * The gate in front of one attempt of an entry row (Plan-378 4.4, rules 1 to 4). Reads the set
   * afresh when nothing is held yet, when the held read started more than `ENTRY_READ_REUSE_MS` ago,
   * or when it shows the row's emote with two or more entries (AK 51); otherwise binds against the
   * held read. Never throws and never errors: every failure is a skip with its reason, so no
   * request ever goes out unbound.
   */
  private bindBeforeRemove(
    context: EntryRunContext,
    setId: string,
    emote: RunQueueEmote,
    entry: SevenTvEntryGeneration,
  ): Observable<StepGate> {
    if (context.consecutiveReadFailures >= MAX_CONSECUTIVE_ENTRY_READ_FAILURES) {
      return of(this.skipEntry(context, emote, readFailureKey(context.lastReadFailure), {}));
    }
    const held = context.held;
    const fresh =
      held === null ||
      Date.now() - held.startedAt > ENTRY_READ_REUSE_MS ||
      (held.read.entriesById?.get(emote.sevenTvEmoteId)?.length ?? 0) >= 2;
    const read$ = fresh ? this.readForBinding(context, setId) : of(held.read);
    return read$.pipe(
      map((read) =>
        read === null
          ? this.skipEntry(context, emote, readFailureKey(context.lastReadFailure), {})
          : this.gateEntry(context, emote, entry, read),
      ),
    );
  }

  /** One read of the set with add dates, bounded by `ENTRY_BINDING_READ_TIMEOUT_MS`. A complete
   *  read resets the failure count, is checked against the baseline for entries gone without an own
   *  removal (4.4 step 6), joins the baseline and becomes the held read; a failed, timed-out or
   *  incomplete one counts as a failure and answers `null`. */
  private readForBinding(
    context: EntryRunContext,
    setId: string,
  ): Observable<SevenTvSetEntries | null> {
    return defer(() => {
      const startedAt = Date.now();
      return loadSevenTvSetEntries(this.httpClient, setId, { withAddedAt: true }).pipe(
        timeout(ENTRY_BINDING_READ_TIMEOUT_MS),
        map((read) => {
          if (!read.complete) {
            return failBindingRead(context, 'truncated');
          }
          context.consecutiveReadFailures = 0;
          noteDefaultNames(context, read);
          noteEarlyUnexpectedRemovals(context, read);
          for (const sevenTvEmoteId of context.entryEmoteIds) {
            mergeIntoBaseline(
              context.baseline,
              sevenTvEmoteId,
              read.entriesById?.get(sevenTvEmoteId) ?? [],
            );
          }
          context.held = { read, startedAt };
          return read;
        }),
        catchError(() => of(failBindingRead(context, 'unavailable'))),
      );
    });
  }

  /** Classifies the row against `read` (4.4 step 4) and answers the engine. A row let through is
   *  recorded as attempted and freezes its baseline date; a skipped one is no longer attempted. */
  private gateEntry(
    context: EntryRunContext,
    emote: RunQueueEmote,
    entry: SevenTvEntryGeneration,
    read: SevenTvSetEntries,
  ): StepGate {
    const binding = bindEntry(entry, read.entriesById?.get(emote.sevenTvEmoteId) ?? []);
    switch (binding.kind) {
      case 'proceed': {
        const timeKey = entryTimeKey(entry.addedAt);
        context.attempted.set(emote.key, {
          sevenTvEmoteId: emote.sevenTvEmoteId,
          timeKey,
          alias: entry.alias,
        });
        freezeBaselineGroup(context.baseline, emote.sevenTvEmoteId, timeKey);
        return PROCEED;
      }
      case 'renamed':
        return this.skipEntry(context, emote, 'usageStats.delete.renamedSinceLoad', {
          name: entry.alias,
          newName: binding.newName,
        });
      case 'noAlias':
        return this.skipEntry(context, emote, 'usageStats.delete.noAlias', { name: entry.alias });
      case 'departed':
        return this.skipEntry(context, emote, 'usageStats.delete.departedSinceLoad', {
          name: entry.alias,
        });
    }
  }

  private skipEntry(
    context: EntryRunContext,
    emote: RunQueueEmote,
    translationKey: SkipReasonKey,
    params: Record<string, string>,
  ): StepGate {
    context.attempted.delete(emote.key);
    return {
      kind: 'skip',
      errorMessage: this.translocoService.translate(translationKey, params),
      reason: { key: translationKey, params },
    };
  }

  /**
   * Turns the engine's snapshot into the run's outcome, always on the run's own record (#256:
   * there is no early return for a run that is no longer shown; its confirmed removals are
   * reported all the same).
   *
   * Without an `unknown` row and without an owed post-check the snapshot settles at once, exactly
   * as before #275. Otherwise the run is `settling` (Plan-275 Festlegung 2) and its `result` stays
   * `null` (Festlegung 10) while the set is read once, tokenless (Festlegung 6): after
   * `CANCEL_SETTLE_GRACE_MS` when the run ended through this service's own `cancel()` (Festlegung 5
   * — 7TV may still be finishing the aborted `REMOVE`), at once otherwise. A slot delete run
   * (`context`) owes that read also when a removal of an entry row went out and its emote's
   * baseline holds more entries than such removals (Plan-378 4.5, T-4), and reads with add dates; a
   * run of id rows reads as it always did. A read that fails or runs out of
   * `SET_ENTRIES_READ_TIMEOUT_MS` is `null`; an incomplete one confirms nothing
   * (`settleDeleteResult`). Every path ends in `settleRun`, so the run always
   * leaves `settling` (Festlegung 19, P6). The snapshot waits in this closure, not on the engine: the
   * engine's queue may belong to a newer run by then.
   *
   * A run `reset()` detached while in flight has left its queue on the engine until now, because
   * `finish()` builds this very result from it; nothing shows that queue any more, so it is
   * cleared here.
   */
  private onRunComplete(runId: string, result: RunResult, context: EntryRunContext | null): void {
    const afterCancel = this.cancelInProgress;
    const needsRead =
      unknownCount(result.items) > 0 ||
      (context !== null && entryPostCheckOwed(result, context.baseline));
    const settling = needsRead
      ? this.lifecycle.update(runId, (run) => ({ ...run, phase: 'settling' }))
      : null;
    if (!this.lifecycle.isShown(runId)) {
      this.engine.reset();
    }

    if (!needsRead) {
      this.settleRun(runId, result, null, context);
      return;
    }
    if (settling === null) {
      // Unreachable: a run is only ever dropped once it is closed, and it cannot close before this.
      return;
    }

    const read = (
      context === null
        ? loadSevenTvSetEntries(this.httpClient, settling.setId)
        : loadSevenTvSetEntries(this.httpClient, settling.setId, { withAddedAt: true })
    ).pipe(
      timeout(SET_ENTRIES_READ_TIMEOUT_MS),
      catchError(() => of(null)),
    );
    (afterCancel ? timer(CANCEL_SETTLE_GRACE_MS).pipe(switchMap(() => read)) : read).subscribe(
      (entries) => this.settleRun(runId, result, entries, context),
    );
  }

  /**
   * Clears the snapshot's `unknown` rows up against `entries` — positive only (Plan-275 N1,
   * `settleDeleteResult`): a row whose id the read shows gone becomes `done`, every other row stays
   * as it was — and publishes that outcome. `result`, `phase: 'reporting'` and the report state
   * move together in one update, so the lifecycle's auto-close guard never sees a `reporting`
   * record whose report has not been marked `pending` yet: a run with nothing to report goes
   * straight to `closed`. A slot delete run settles its entry rows per entry and adds its
   * unexpected removals and `postCheckUnavailable` in that same update (Plan-378 4.5) — no reader
   * ever sees a result without them.
   *
   * The report names the settled `doneKeys` only; a row still `unknown` is never reported
   * (Festlegung 12). What becomes of such a row (Festlegung 13, D6): next to a report, nothing
   * more — the backend resyncs every channel that holds the set, the active one included, and
   * that heals the row; should the report fail for good, the N1 fallback resync stands in for it.
   * Without a report, `fallbackResync` asks for that resync directly, for `expectedChannelName`
   * only.
   */
  private settleRun(
    runId: string,
    snapshot: RunResult,
    entries: SevenTvSetEntries | null,
    context: EntryRunContext | null,
  ): void {
    const entrySettlement = context === null ? null : settleEntryRun(snapshot, entries, context);
    const result = entrySettlement?.result ?? settleDeleteResult(snapshot, entries);
    const reportsDeleted = result.doneKeys.length > 0;
    const settled = this.lifecycle.update(runId, (run) => ({
      ...run,
      result,
      phase: 'reporting',
      syncReport: reportsDeleted ? 'pending' : run.syncReport,
      syncReportReason: reportsDeleted ? null : run.syncReportReason,
      // A tag run reports even with nothing removed (spec 7.2/8): a cancel before the first row, or
      // a run whose every row failed, still moves the own placements and deactivates the tag.
      ...(run.tag === undefined
        ? {}
        : { tagRemovalReport: 'pending', tagRemovalReportReason: null }),
      ...(entrySettlement === null
        ? {}
        : {
            unexpectedRemovals: entrySettlement.unexpectedRemovals,
            postCheckUnavailable: entrySettlement.postCheckUnavailable,
          }),
    }));
    if (settled === null) {
      // Unreachable: a run is only ever dropped once it is closed, and it cannot close before this.
      return;
    }

    if (reportsDeleted) {
      this.reportDeleted(runId, deletedReportOf(result), () => this.fallbackResync(settled));
    } else if (unknownCount(result.items) > 0) {
      this.fallbackResync(settled);
    }
    // Independent of the above: the two reports go to different books — what 7TV's set now holds,
    // and which emotes this tag put there — and either can fail without the other.
    if (settled.tag !== undefined) {
      this.reportTagRemoval(runId);
    }
  }

  /** Sends `sync-deleted` with `report` — see {@link deletedReportOf}; the first report and every
   *  retry derive it from the same settled result, so a retry sends the identical body (AK 41).
   *
   *  No resync of its own on an answer (spec 6.5): the backend resyncs every channel the report
   *  touched (E17), and the page lives off the resulting `channel.synced`. `afterFailure` runs once
   *  the report has failed for good — only the first report of a run passes one, never a manual
   *  retry (addendum N1).
   *
   *  Patches the record to `syncReport: 'pending'` first (#256 P2, Plan-256-Robustheit review),
   *  the same way the import's `reportImported`/`reportRemoved` do — the first call after
   *  `settleRun` finds it already `'pending'` (redundant but harmless), but a manual
   *  `retrySyncReport()` call needs exactly this: without it, the record stayed on its previous
   *  end state (e.g. `'failed'`) for the whole time the retry's request was out, so the retry
   *  button stayed visible for a second click (a parallel, redundant report) and, once `closed`,
   *  the record had nothing pending to keep it in the lifecycle's map for a late answer to find —
   *  a "Close" clicked mid-retry then left the eventual answer with no record to land on: no
   *  reshow, no `console.warn`. `syncReportReason` is cleared too, so a stale reason does not
   *  flash next to the fresh `'pending'` state. `closed` itself is not reopened here — the
   *  lifecycle's one-way door leaves the phase alone, and `reportsPending` (`syncReport ===
   *  'pending'`) is what keeps a `closed`-but-pending record in the map until this new attempt
   *  also reaches an end state. */
  private reportDeleted(runId: string, report: DeletedReport, afterFailure?: () => void): void {
    const run = this.patchRun(runId, { syncReport: 'pending', syncReportReason: null });
    if (run === null) {
      // Unreachable in practice: called right after the update that put the run into `reporting`,
      // or from a manual retry that just read the record — kept as a guard, not a silent no-op.
      return;
    }

    this.emoteSetService
      .reportDeletedInSet(run.setId, {
        sevenTvEmoteIds: report.sevenTvEmoteIds,
        expectedChannelName: run.expectedChannelName,
        targetOwnerTwitchId: run.targetOwnerTwitchId,
        ...(report.entries === undefined ? {} : { entries: report.entries }),
      })
      .pipe(
        timeoutReportAttempt(),
        // The threeway reading (`map`, not inside `next:`) lives ahead of `retry` so a malformed
        // 200 answer that makes `classifySyncInSetResponse` throw ends the report like any other
        // transient failure — an uncaught throw inside a `next:` callback would otherwise leave
        // this run `reporting` forever, never `closed` (#256 review finding). `retry`'s `delay`
        // below then also retries this throw (a plain `TypeError`, not an `HttpErrorResponse` —
        // its `status` reads `undefined`, so neither branch of the 401/403 check matches):
        // uncritical, because a retried `sync-deleted` call is idempotent either way.
        map((answer: SyncDeletedInSetResponse) =>
          classifySyncInSetResponse(answer, report.sevenTvEmoteIds.length),
        ),
        // A 429 is the realistic case: sync-deleted shares a rate-limit budget with other calls, and
        // a swallowed 429 used to look exactly like success. A 401 (session expired during a long
        // run) or a 403 (the right to the set is gone) cannot be fixed by waiting, so neither is
        // retried.
        retry({
          count: MAX_AUTOMATIC_SYNC_RETRIES,
          delay: (error: HttpErrorResponse, attempt) =>
            error.status === 401 || error.status === 403
              ? throwError(() => error)
              : timer(SYNC_RETRY_DELAY_MS * attempt),
        }),
      )
      .subscribe({
        // The threeway reading lives in one place for all three services (F8, E23, AK 15): a
        // channel short of the reported ids is 'partial'/'shortfall', a missed expected channel
        // 'partial'/'channelMismatchNotTracked' or 'partial'/'channelMismatchActiveSetDiffers'
        // (#255), a paper-only answer (`channels: []`) a plain success.
        next: (outcome) => this.endReport(runId, outcome),
        // A 404 (the set is gone) ends in 'failed'/'setNotFound' — never in 'succeeded' (#224).
        error: (error: HttpErrorResponse) => {
          this.endReport(runId, classifySyncInSetFailure(error.status));
          afterFailure?.();
        },
      });
  }

  /**
   * The removal report of a tag removal (#201 T-C, spec 7.2/8): `POST …/tags/{tagId}/placements/
   * removed` with the registered `operationId`, the run's own set and owner, the activation and
   * snapshot from the preview, `removedIds` = the run's `doneKeys` of id rows (a tag removal has no
   * other rows; an entry key is never a 7TV id) and `keptIds` derived from the
   * **run result** (`deriveTagKeptIds`): the unticked own placements plus every ticked one without a
   * `done` row — a removal 7TV did not confirm is still in the set, and the server must not forget
   * it. Same transport rules as `sync-deleted`: one `timeoutReportAttempt` per attempt, automatic
   * retries for anything but a 401/403, an end state on every path.
   *
   * A replayed answer (`replayed: true`) is a success that says nothing else (docs/DECISIONS.md,
   * #201 T-C): the server had already applied this operation, so no counts are read from it —
   * nothing is read from the answer at all.
   */
  private reportTagRemoval(runId: string): void {
    const run = this.patchRun(runId, { tagRemovalReport: 'pending', tagRemovalReportReason: null });
    if (run === null) {
      return;
    }
    // Both callers guarantee a tag and a result; should that ever break, end the report as a
    // failure rather than leave it pending, which would hold the run open forever.
    if (run.tag === undefined || run.result === null) {
      this.endTagReport(runId, { state: 'failed', reason: 'other' });
      return;
    }
    const tag = run.tag;

    this.emoteTagService
      .reportRemoval(tag.channelName, tag.tagId, {
        operationId: tag.operationId,
        emoteSetId: run.setId,
        targetOwnerTwitchId: run.targetOwnerTwitchId,
        activationOperationId: tag.activationOperationId,
        snapshot: tag.snapshot,
        removedIds: doneIdRowKeys(run.result),
        keptIds: deriveTagKeptIds(tag, run.result),
      })
      .pipe(
        timeoutReportAttempt(),
        map(() => undefined),
        retry({
          count: MAX_AUTOMATIC_SYNC_RETRIES,
          delay: (error: HttpErrorResponse, attempt) =>
            error.status === 401 || error.status === 403
              ? throwError(() => error)
              : timer(SYNC_RETRY_DELAY_MS * attempt),
        }),
      )
      .subscribe({
        next: () => this.endTagReport(runId, { state: 'succeeded', reason: null }),
        error: (error: HttpErrorResponse) =>
          this.endTagReport(runId, classifyTagReportFailure(error)),
      });
  }

  /** addendum N1, AK 36: a report that failed for good (any status, or a network error, after the
   *  retries) never reached the backend's resync stage, so nothing would pull the page's rows until
   *  the worker's periodic resync. The client stands in for it — for `expectedChannelName` only: a
   *  non-active or untracked set has no channel the backend would have resynced either. Since #275
   *  (Plan-275 Festlegung 13 (a)) also called straight from `settleRun` for a run that settled with
   *  `unknown` rows and nothing to report: there is no report whose backend resync would heal
   *  those rows, so the client asks for it — the same channel rule, the same silence. No dock line
   *  (the delete never had one; the `syncFailed` notice is already up, and the visible effect is the
   *  resync's `channel.synced`), so its outcome — a 429 cooldown included — is deliberately
   *  swallowed. Runs for a superseded run too, like the restore's: it is owed to 7TV's state. */
  private fallbackResync(run: DeleteRunInfo): void {
    const channelName = run.expectedChannelName;
    if (channelName === null) {
      return;
    }
    this.channelService.resync(channelName).subscribe({ error: () => undefined });
  }

  /** Writes a report's end state onto its run's record — which closes the run once it was the last
   *  (only) report out — and brings a run nobody shows back onto the dock when that end state is
   *  not a success (Plan-256 Festlegung 13): a failed or partial report needs a place with its
   *  reason and a retry. Shown again only when nothing else is shown and the engine is free;
   *  otherwise the failure stays on the record and in the console.
   *
   *  `reshow` is attempted before `showFinishedRows` and rolled back with `detach()` if that then
   *  refuses (#256 review finding: reshowing must not leave the dock pointing at a run whose queue
   *  never actually reappeared) — never the other way round, which could otherwise push a
   *  to-be-rejected run's rows onto the engine's queue where an already-shown run would inherit them. */
  private endReport(runId: string, outcome: SyncReportOutcome): void {
    const run = this.patchRun(runId, {
      syncReport: outcome.state,
      syncReportReason: outcome.reason,
    });
    this.reshowUnlessSucceeded(run, 'sync-deleted', outcome);
  }

  /** `endReport` for the tag removal report — the same lifecycle rules, its own record fields. */
  private endTagReport(runId: string, outcome: SyncReportOutcome): void {
    const run = this.patchRun(runId, {
      tagRemovalReport: outcome.state,
      tagRemovalReportReason: outcome.reason,
    });
    this.reshowUnlessSucceeded(run, 'tag-removal', outcome);
  }

  private reshowUnlessSucceeded(
    run: DeleteRunInfo | null,
    report: 'sync-deleted' | 'tag-removal',
    outcome: SyncReportOutcome,
  ): void {
    if (run === null || outcome.state === 'succeeded' || this.lifecycle.isShown(run.runId)) {
      return;
    }
    if (run.result !== null && !this.engine.isRunning() && this.lifecycle.reshow(run)) {
      if (this.engine.showFinishedRows(run.result.items)) {
        return;
      }
      this.lifecycle.detach();
    }
    console.warn('[EmotePurge] 7TV delete report of a run no longer shown did not succeed', {
      runId: run.runId,
      report,
      state: outcome.state,
      reason: outcome.reason,
    });
  }

  /** Merges `patch` into the record of `runId` — see `SevenTvRunLifecycle.update`. */
  private patchRun(runId: string, patch: Partial<DeleteRunInfo>): DeleteRunInfo | null {
    return this.lifecycle.update(runId, (run) => ({ ...run, ...patch }));
  }
}

/** The aliases a delete row removes: an entry row exactly its own, an id row its known aliases, or
 *  the emote's name when it has none. */
function deleteRowAliases(emote: DeleteQueueEmote): readonly string[] {
  if (emote.entry) {
    return [emote.entry.alias];
  }
  return emote.aliases && emote.aliases.length > 0 ? emote.aliases : [emote.name];
}

/** The delete queue. **Id rows** are one per 7TV id, keyed by it (spec #200, 7.2, Sonde 5 branch A):
 *  a #74 duplicate cell is one row, and its one `REMOVE` takes every entry. Should a caller hand in
 *  the same id twice, the rows merge — two rows with one key would make the engine's per-key status
 *  updates hit both, and the second `REMOVE` could only fail — keeping every alias for the protocol.
 *
 *  **Entry rows** (slot delete, Plan-378 4.3) are one per card, keyed `${id}#${cardKey}`, and record
 *  exactly their own alias — never enriched with the aliases the id has elsewhere in the set, or the
 *  protocol and a restore would bring back siblings that were never deleted. A second row with the
 *  same card key is dropped. An entry row whose id also has an id row in the same call (the page
 *  never builds that) goes up in the id row, whose alias-less `REMOVE` takes the entry anyway. */
function toDeleteQueue(emotes: readonly DeleteQueueEmote[]): RunQueueEmote[] {
  const idRowIds = new Set(
    emotes.filter((emote) => !emote.entry).map((emote) => emote.sevenTvEmoteId),
  );
  const rows = new Map<string, RunQueueEmote & { aliases: string[] }>();
  for (const emote of emotes) {
    const aliases = deleteRowAliases(emote);
    if (emote.entry && !idRowIds.has(emote.sevenTvEmoteId)) {
      const key = `${emote.sevenTvEmoteId}#${emote.entry.cardKey}`;
      if (!rows.has(key)) {
        rows.set(key, {
          key,
          emoteId: emote.emoteId,
          sevenTvEmoteId: emote.sevenTvEmoteId,
          name: emote.name,
          aliases: [...aliases],
          entry: emote.entry,
        });
      }
      continue;
    }
    const existing = rows.get(emote.sevenTvEmoteId);
    if (existing) {
      existing.aliases.push(...aliases.filter((alias) => !existing.aliases.includes(alias)));
      existing.emoteId ??= emote.emoteId;
      continue;
    }
    rows.set(emote.sevenTvEmoteId, {
      key: emote.sevenTvEmoteId,
      emoteId: emote.emoteId,
      sevenTvEmoteId: emote.sevenTvEmoteId,
      name: emote.name,
      aliases: [...new Set(aliases)],
    });
  }
  return [...rows.values()];
}

/** The part of a `sync-deleted` body a run's result decides. */
type DeletedReport = Pick<SyncInSetBody, 'sevenTvEmoteIds' | 'entries'>;

/**
 * The `sync-deleted` body of a settled result (Plan-378 4.6, D32, AK 37): `sevenTvEmoteIds` are the
 * distinct 7TV ids of the `done` rows in queue order — id rows and entry rows mixed, an entry row by
 * its row's id, never by its `${id}#${cardKey}` key — so the server still counts `archivedCount`
 * against reported ids. `entries` names every `done` entry row with its alias, its slot (absent for
 * an entry without one) and its add date verbatim; it is omitted when there is none, so a run of id
 * rows sends exactly what it always sent.
 */
function deletedReportOf(result: RunResult): DeletedReport {
  const rowsByKey = new Map(result.items.map((item) => [item.key, item]));
  const sevenTvEmoteIds: string[] = [];
  const entries: SyncInSetEntry[] = [];
  for (const key of result.doneKeys) {
    const row = rowsByKey.get(key);
    // A key without its row cannot come from this service; it is reported as today, as an id.
    const sevenTvEmoteId = row?.sevenTvEmoteId ?? key;
    if (!sevenTvEmoteIds.includes(sevenTvEmoteId)) {
      sevenTvEmoteIds.push(sevenTvEmoteId);
    }
    if (row?.entry) {
      entries.push({
        sevenTvEmoteId,
        alias: row.entry.alias,
        ...(row.entry.slotId === undefined ? {} : { slotId: row.entry.slotId }),
        addedAt: row.entry.addedAt,
      });
    }
  }
  return entries.length > 0 ? { sevenTvEmoteIds, entries } : { sevenTvEmoteIds };
}

/** The `doneKeys` of id rows only — a 7TV id each. A tag removal runs id rows only, so for it this
 *  is its `doneKeys` unchanged; an entry row's key would never be a 7TV id. */
function doneIdRowKeys(result: RunResult): string[] {
  const entryKeys = new Set(
    result.items.filter((item) => item.entry !== undefined).map((item) => item.key),
  );
  return result.doneKeys.filter((key) => !entryKeys.has(key));
}

/** The per-run state of a slot delete run, or `null` for a run without an entry row. */
function createEntryRunContext(queue: readonly RunQueueEmote[]): EntryRunContext | null {
  const entryEmoteIds = new Set(
    queue.filter((row) => row.entry !== undefined).map((row) => row.sevenTvEmoteId),
  );
  if (entryEmoteIds.size === 0) {
    return null;
  }
  return {
    entryEmoteIds,
    baseline: new Map(),
    attempted: new Map(),
    earlyUnexpected: [],
    defaultNames: new Map(),
    held: null,
    consecutiveReadFailures: 0,
    lastReadFailure: 'unavailable',
  };
}

/**
 * Early collateral detection (Plan-378 4.4 step 6, S2): before a binding read joins the baseline,
 * every emote the run has already let a removal through for is checked against it — an entry of the
 * baseline gone from the read that no attempted own removal accounts for is noted at once, so it
 * cannot be forgotten later, whatever the re-read after the run finds or fails to find. An attempt
 * counts as own here even before its answer is in; the re-read after the run then judges by the
 * rows' final states.
 */
function noteEarlyUnexpectedRemovals(context: EntryRunContext, read: SevenTvSetEntries): void {
  for (const sevenTvEmoteId of context.entryEmoteIds) {
    const groups = context.baseline.get(sevenTvEmoteId);
    if (groups === undefined || ![...groups.values()].some((group) => group.frozen)) {
      continue;
    }
    const own = new Map<string, string[]>();
    for (const attempt of context.attempted.values()) {
      if (attempt.sevenTvEmoteId === sevenTvEmoteId) {
        own.set(attempt.timeKey, [...(own.get(attempt.timeKey) ?? []), attempt.alias]);
      }
    }
    context.earlyUnexpected = mergeUnexpectedRemovals(
      context.earlyUnexpected,
      findUnexpectedRemovals(
        sevenTvEmoteId,
        groups,
        read.entriesById?.get(sevenTvEmoteId) ?? [],
        own,
      ),
    );
  }
}

/** The settled outcome of a slot delete run: its rows per entry, the unexpected removals noticed
 *  early and by the re-read, and whether an owed post-check could not run (Plan-378 4.5). */
function settleEntryRun(
  snapshot: RunResult,
  entries: SevenTvSetEntries | null,
  context: EntryRunContext,
): {
  result: RunResult;
  unexpectedRemovals: UnexpectedEntryRemoval[];
  postCheckUnavailable: boolean;
} {
  const settled = settleEntryDeleteRun(snapshot, entries, context.baseline);
  if (entries !== null) {
    noteDefaultNames(context, entries);
  }
  return {
    result: settled.result,
    unexpectedRemovals: mergeUnexpectedRemovals(
      context.earlyUnexpected,
      settled.unexpectedRemovals ?? [],
    ).map((removal) => withDefaultName(removal, context.defaultNames)),
    postCheckUnavailable:
      settled.unexpectedRemovals === null && entryPostCheckOwed(snapshot, context.baseline),
  };
}

/** Keeps the default name `read` lists for each emote of the run's entry rows (an empty one is no
 *  name, see `SevenTvSetEntries.defaultNameById`). */
function noteDefaultNames(context: EntryRunContext, read: SevenTvSetEntries): void {
  for (const sevenTvEmoteId of context.entryEmoteIds) {
    const defaultName = read.defaultNameById.get(sevenTvEmoteId);
    if (defaultName) {
      context.defaultNames.set(sevenTvEmoteId, defaultName);
    }
  }
}

/** `removal` with its emote's default name when it has no alias of its own and a read named the
 *  emote; every other removal unchanged. */
function withDefaultName(
  removal: UnexpectedEntryRemoval,
  defaultNames: ReadonlyMap<string, string>,
): UnexpectedEntryRemoval {
  const defaultName = defaultNames.get(removal.sevenTvEmoteId);
  return removal.alias || defaultName === undefined ? removal : { ...removal, defaultName };
}

function failBindingRead(context: EntryRunContext, reason: 'unavailable' | 'truncated'): null {
  context.consecutiveReadFailures += 1;
  context.lastReadFailure = reason;
  return null;
}

function readFailureKey(reason: 'unavailable' | 'truncated'): SkipReasonKey {
  return reason === 'unavailable'
    ? 'massDelete.memberRead.unavailable'
    : 'massDelete.memberRead.truncated';
}

/**
 * The binding table of Plan-378 4.4 step 4: the row's emote's live entries at the row's add date
 * (compared in milliseconds) decide. One entry under the row's alias, or a D4 tie with one entry
 * under it, lets the attempt through — and it then sends exactly that alias, just verified (D21).
 * One entry under another alias is a rename; one without an alias is skipped fail-closed, because a
 * removal without an alias would take every entry of the id and whether one with the default name
 * hits an aliasless entry is not established. No entry, or a tie without the alias (D4: a rename
 * inside a tie reads as remove plus add), means the entry has left.
 */
function bindEntry(entry: SevenTvEntryGeneration, live: readonly SevenTvSetEntry[]): EntryBinding {
  if (!entry.alias) {
    // Never reached from the page (a card always has a name) — but a request for one entry must
    // never go out without an alias, which 7TV would read as "every entry of the id".
    return { kind: 'noAlias' };
  }
  const timeKey = entryTimeKey(entry.addedAt);
  const atTime = live.filter((candidate) => entryTimeKey(candidate.addedAt) === timeKey);
  if (atTime.some((candidate) => candidate.alias === entry.alias)) {
    return { kind: 'proceed' };
  }
  if (atTime.length !== 1) {
    return { kind: 'departed' };
  }
  const alias = atTime[0].alias;
  return alias ? { kind: 'renamed', newName: alias } : { kind: 'noAlias' };
}
