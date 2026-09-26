import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import {
  DestroyRef,
  Service,
  Signal,
  computed,
  effect,
  inject,
  linkedSignal,
  signal,
} from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { catchError, map, of, retry, throwError, timeout, timer } from 'rxjs';

import { ChannelService } from '../channels/channel.service';
import { EmoteAdminService } from '../emotes/emote-admin.service';
import {
  ImportOrigin,
  ImportRow,
  importOriginLeaderboardSort,
  importOriginSourceChannelName,
} from './import-source';
import {
  MAX_AUTOMATIC_SYNC_RETRIES,
  REMOVE_EMOTE_MUTATION,
  SYNC_RETRY_DELAY_MS,
  timeoutReportAttempt,
} from './seven-tv-delete.service';
import { SevenTvEmoteSetService } from './seven-tv-emote-set.service';
import { ResyncTriggerState } from './seven-tv-restore.service';
import {
  RunOperation,
  RunQueueEmote,
  RunQueueItem,
  RunResult,
  SevenTvRunEngine,
} from './seven-tv-run-engine';
import { RunRecordBase, SevenTvRunLifecycle } from './seven-tv-run-lifecycle';
import { SevenTvSetEntries, loadSevenTvSetEntries } from './seven-tv-set-entries';
import { SevenTvTokenService } from './seven-tv-token.service';
import {
  SyncReportReason,
  SyncReportState,
  TargetCheckBlockReason,
  classifySyncInSetFailure,
  classifySyncInSetResponse,
  isChannelMismatch,
} from './sync-report-outcome';
import { TransferPlan, TransferRow } from './transfer-plan';

// #149 P2 (independent review): how long `duplicateNoticePending` stays true after a `startImport`
// call that had something to report. Same 4000 ms convention as every other transient status in
// this app (docs/UI-Designsprache.md §4.5 — usage-stats-page's SELECTION_PRUNED_FEEDBACK_MS,
// channel-workspace-layout's RESYNC_FEEDBACK_MS). Lives here rather than on the page that renders
// it because the *visibility* of the page's own dock depends on this flag (see
// `dockVisible`/`action-dock.ts`) — a refused, all-duplicates run leaves no run/queue for the dock
// to mount on otherwise, which is exactly the bug this exists to fix.
const DUPLICATE_NOTICE_MS = 4000;

/** The same ADD the restore run uses — an import *is* an ADD, only with rows that come from
 *  somewhere else. `alias` carries the plan row's alias: the source alias for an `add` row, so the
 *  copy keeps the name the source channel knew it by, the user-typed alias for a `renameSource`
 *  row; without it 7TV would fall back to the emote's default name. It travels *inside* the
 *  `EmoteSetEmoteId` input object, not as a sibling argument — v4's `addEmote` field replaces v3's
 *  single `emotes(action: ADD, name:)` mutation with one field per operation (see docs/DECISIONS.md,
 *  #149). */
const ADD_EMOTE_MUTATION = `
  mutation AddEmote($setId: Id!, $emoteId: Id!, $alias: String) {
    emoteSets {
      emoteSet(id: $setId) {
        addEmote(id: { emoteId: $emoteId, alias: $alias }) {
          id
        }
      }
    }
  }
`;

/** An `adoptSourceName` row's one mutation: renames an existing entry in place. The *current* alias
 *  inside the `EmoteSetEmoteId` input selects the entry — even on a #74 duplicate — and `alias` is
 *  the new name. A name that is taken comes back over HTTP 200 as a GraphQL error with
 *  `extensions.status = 409` (live probe 2026-09-23; the text differs from `addEmote`'s collision,
 *  so it is matched by status, never by text). */
const UPDATE_EMOTE_ALIAS_MUTATION = `
  mutation UpdateEmoteAlias($setId: Id!, $emoteId: Id!, $currentAlias: String!, $alias: String!) {
    emoteSets {
      emoteSet(id: $setId) {
        updateEmoteAlias(id: { emoteId: $emoteId, alias: $currentAlias }, alias: $alias) {
          alias
        }
      }
    }
  }
`;

/** Time budget for the one re-read after a run with an unanswered step — the same 20 s the delete
 *  run's live alias read allows (`mass-delete-panel.ts`, `LIVE_ALIAS_READ_TIMEOUT_MS`). The re-read
 *  follows a transport loss, exactly when a request is likely to hang, and every report of the run
 *  waits for it; a read that runs out settles the run like a failed read. */
const SETTLE_READ_TIMEOUT_MS = 20_000;

/** `extensions.status` of a GraphQL rejection for a name that is already taken in the set — on
 *  `addEmote` and on `updateEmoteAlias` alike. */
const NAME_TAKEN_GQL_STATUS = 409;

/**
 * Whether a failed row means "this token may not write this set at all". True for it stops the run:
 * every remaining row would fail identically, and 7TV counts each attempt against the rate-limit
 * bucket. Deliberately narrow — a rate-limit give-up (translated text, `null` status) and a network
 * error (status `0`) are row failures, not privilege failures, and the run keeps going for them.
 *
 * v4 reports a missing-permission mutation as HTTP 200 with `extensions.code = 'LACKING_PRIVILEGES'`
 * — a structured field, not a guess at their frontend's wording, which is why the `errorCode` check
 * below is the whole story now. The `httpStatus` check stays for an invalid/expired token: that still
 * fails at the HTTP layer with a real 401, whose body sits outside the GraphQL schema
 * (`{"status":"Unauthorized","error_code":1000,"error":"invalid session"}`, live-measured) and carries
 * no `extensions.code` to read.
 */
function abortsForMissingPrivileges(failure: {
  message: string;
  httpStatus: number | null;
  errorCode: string | null;
}): boolean {
  return (
    failure.httpStatus === 401 ||
    failure.httpStatus === 403 ||
    failure.errorCode === 'LACKING_PRIVILEGES'
  );
}

/** One row of an import run as the service shows and reports it: the engine's queue row plus the
 *  plan row it runs (action, alias, target). The engine never reads `transfer`; the dock and the
 *  run protocol do.
 *
 *  `errorMessage` is the text to *display*. For a row that ends with an import-specific reason —
 *  `import.errors.nameTakenNow`, `import.errors.removedButNotAdded` or `import.errors.unknownOutcome`
 *  — the settled result replaces it with that translated reason and keeps the text the engine had
 *  in `sevenTvErrorMessage` (7TV's raw GraphQL message, or the engine's transport text). Every other
 *  row keeps the engine's `errorMessage` untouched and has no `sevenTvErrorMessage`. A protocol
 *  that wants 7TV's own words therefore writes `sevenTvErrorMessage ?? errorMessage`. */
export interface ImportRunItem extends RunQueueItem {
  transfer: TransferRow;
  sevenTvErrorMessage?: string | null;
}

/** The engine's `RunResult` with the rows the service shows — see `ImportRunItem`. */
export interface ImportRunResult extends RunResult {
  items: ImportRunItem[];
}

/**
 * `'pending'` from the start of a run until its outcome is final; `'settled'` once it is. A run
 * whose snapshot has no `unknown` row settles the moment the engine completes; one with an
 * `unknown` row stays `pending` until the one live re-read of the target set has cleared up what
 * it can (`SevenTvImportService.onRunComplete`). Nothing is reported to our Api before `'settled'`.
 * Derived from the run's `phase` since #256 — `'settled'` exactly while it is `reporting` or
 * `closed` — and kept as its own field because the dock, the usage-stats page and #254 read it.
 */
export type ImportSettlement = 'pending' | 'settled';

/**
 * One import run, from the moment it starts to the moment its bookkeeping is done. Everything the
 * closing calls need hangs off *this* record, never off a field next to the service (R15, #72):
 * the engine sets `isRunning` back to `false` inside `finish()`, i.e. *before* `onComplete` fires
 * the asynchronous follow-up, so a second import can legitimately start while the first one's
 * re-read and reports are still out. Since #256 the record also carries its own phase and report
 * states (`RunRecordBase`, `SevenTvRunLifecycle`): a late answer of run 1 lands on run 1's record —
 * found by `runId` — and never on the dock's signals, which project whichever run is shown.
 */
export interface ImportRunInfo extends RunRecordBase {
  /** `null` for a run into an *untracked* account's set (T2.6, spec 8.6) — there is no channel of
   *  ours to resync afterwards or to report the channel-scoped `sync-imported` against; see
   *  `reportImported` and `onRunComplete` for what each of the two cases does instead. */
  targetChannelName: string | null;
  /** The set's owner, 7TV display name — only ever set alongside `targetChannelName === null`
   *  (E7: never compared against anything, display only). `import-progress-section.ts` is what
   *  reads it: with no target channel to name, the dock's own "Ziel: …" line and its "open target
   *  channel" link both need a different, channel-free way to say what this run wrote into. */
  targetOwnerDisplayName: string | null;
  targetSetId: string;
  /** Resolved display name of `targetSetId` (falls back to the id) — what the dock's own "Ziel: …"
   *  line names alongside the channel/owner (finding 2, Live-Verifikation K2 2026-09-21), instead of
   *  the earlier untracked branch's raw `targetSetId`. */
  targetSetName: string;
  /** Whether this run's target is the tracked channel's currently *active* 7TV set — `true` for
   *  every legacy "today" door and for a picker choice whose set equals the account's own
   *  `activeEmoteSetId` (spec 8.6 fourth bullet), `false` for a tracked non-active set and for every
   *  untracked one. Decides two things downstream (finding 3, Live-Verifikation K2 2026-09-21):
   *  whether `onRunComplete` may fire the channel's active-set resync at all (a resync only ever
   *  syncs the *active* set — firing it for a non-active target would claim a channel-page update
   *  that never happens), and, together with `targetChannelName`, which post-run message and which
   *  "open target channel" affordance the dock shows. Defaults to `true` in `startImport` when a
   *  caller omits it, matching every caller that predates this flag (the resync always fired for
   *  them, correctly, since they only ever targeted the active set). */
  targetIsActiveSet: boolean;
  origin: ImportOrigin;
  /** The plan this run executes, one queue row per plan row, keyed by the source 7TV id. */
  plan: TransferPlan;
  /** See `ImportSettlement`. `result` is the engine's snapshot while `'pending'`, the settled
   *  outcome once `'settled'`. */
  settlement: ImportSettlement;
  /** Replace rows whose REMOVE 7TV confirmed — directly, or through the re-read — whatever the
   *  row's final status. Exactly the rows the removal report names. `0` while the run is in
   *  flight. */
  removedCount: number;
  /** Rows whose outcome is still `unknown` in `result`. `0` while the run is in flight. */
  unknownCount: number;
  /** Replace rows whose REMOVE is still `unknown` in `result` (`failedStep === 0`) — a subset of
   *  `unknownCount` (#256, issue point 4): the re-read in `settleUnknownRow` always turns an
   *  answered REMOVE into `failed@0`/`failed@1`, so this only survives a read that itself failed,
   *  timed out or came back incomplete. Exactly the rows the result log never counts as a
   *  confirmed removal — `import-progress-section.ts` names them so the recovery file is not the
   *  only place that says so. `0` while the run is in flight. */
  unknownRemovalCount: number;
  /** `null` while the run is in flight; the engine's snapshot once it completes, replaced by the
   *  settled outcome when `settlement` turns `'settled'`. */
  result: ImportRunResult | null;
  /** This run's `sync-imported` report — `SevenTvImportService.syncReport` projects it for the
   *  shown run. */
  syncReport: SyncReportState;
  /** This run's removal report (`sync-deleted`) — projected by `removalReport`. */
  removalReport: SyncReportState;
  /** Projected by `removalReportReason`. */
  removalReportReason: SyncReportReason | null;
  /** Projected by `resyncTrigger`. Not a report (#256, Plan-256 Festlegung 4): never holds the run
   *  open. */
  resyncTrigger: ResyncTriggerState;
  /** Projected by `abortedForPrivileges`. */
  abortedForPrivileges: boolean;
  /** Projected by `protocolSaved`; set through `markProtocolSaved()`. */
  protocolSaved: boolean;
}

/** Per-run state the run's operation and its settlement share, never exposed. */
interface ImportRunContext {
  rowsByKey: ReadonlyMap<string, TransferRow>;
  /** `extensions.status` of each row's failing step, keyed by row — recorded in `abortOn`, the
   *  one place the engine hands it out. */
  gqlStatusByKey: Map<string, number | null>;
}

/**
 * The import half of #38/K3: copies emotes into *another* channel's 7TV set, over the same run
 * engine (pacing, backoff, token, queue) as the delete and the restore — ADD draws tickets from the
 * same `emote_set_change` bucket. Zero-knowledge holds: the write token never leaves the browser.
 *
 * Two things separate it from `SevenTvRestoreService`, both deliberate:
 * - **No `resetIfChannelChanged`.** A restore resets a *finished* run when the page's channel moves
 *   away from the one it was started on (`hostChannelName`, spec #253 E13) — since #253 that is no
 *   longer the same as the channel it wrote into (a restore can target any set, tracked or not); an
 *   import writes into a *different* channel on purpose from the start, so resetting the run when
 *   the page follows the user would throw away the very run they started (R9).
 * - **The follow-up hangs off the run's own record, not off loose fields** — see `ImportRunInfo`.
 *
 * A run executes a `TransferPlan`: one queue row per plan row, and per action the mutations the
 * row needs — an ADD for `add`/`renameSource`, a REMOVE of the target and then an ADD for
 * `replace`, an alias UPDATE for `adoptSourceName`. A plan with a `replace` row is the one run of
 * this service that deletes, and three things follow from it: its rows end `unknown` on a lost
 * answer instead of `failed`, the run is re-read before anything is reported, and the confirmed
 * REMOVEs are reported through the set-centric `sync-deleted` next to the `sync-imported` of
 * the ADDs. A plan of `add` rows only runs exactly as a plain copy always did.
 *
 * Every run completes run-bound (#256, `SevenTvRunLifecycle`): `running → settling → reporting →
 * closed` on its own record, whether or not the dock still shows it. `reset()` and a newer run only
 * change what is shown. `isSettling` and `destructiveOpen` look across every open run of this
 * service, not just the shown one.
 *
 * It does not know the `SevenTvRunArbiter`, and does not report to it: the arbiter derives its
 * answer from this service's own `isRunning` signal (its third branch), exactly as it does for
 * delete and restore. Checking whether a run may start is the caller's job.
 */
@Service()
export class SevenTvImportService {
  private readonly channelService = inject(ChannelService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly emoteSetService = inject(SevenTvEmoteSetService);
  private readonly httpClient = inject(HttpClient);
  private readonly translocoService = inject(TranslocoService);

  /** Own engine instance — see the identical note in SevenTvDeleteService. */
  private readonly engine = new SevenTvRunEngine(
    this.httpClient,
    inject(SevenTvTokenService),
    this.translocoService,
  );

  /** Every open run of this service, by id, plus the one the dock shows (#256). */
  private readonly lifecycle = new SevenTvRunLifecycle<ImportRunInfo>(
    'import',
    (run) => run.syncReport === 'pending' || run.removalReport === 'pending',
  );

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  /** The run this service is currently showing — in flight (`result === null`) or finished. Its
   *  record is replaced by a new object on every change; `runId` is its identity. Writable because
   *  specs drive the dock through it; production code only writes through the lifecycle. */
  readonly run = this.lifecycle.shown;

  /** True while any run of this service re-reads its unknown rows or waits for a report — shown or
   *  not (#256, contract P1). */
  readonly isSettling = this.lifecycle.isSettling;

  /** True while any run whose plan deletes (a `replace` row) is not closed — running, re-reading or
   *  reporting, shown or not (#256, contract P3). What the `beforeunload` guard hangs off: until the
   *  run closes, the report of its confirmed REMOVEs has not been answered, and closing the tab then
   *  could lose it. */
  readonly destructiveOpen = this.lifecycle.destructiveOpen;

  /** The rows to show: the engine's live queue while a run is in flight, the shown run's own
   *  result once it is not. After a run this is the settled outcome of *that* run — never the
   *  engine's queue, which may already belong to a newer run. */
  readonly items: Signal<ImportRunItem[]> = computed(() => {
    if (this.engine.isRunning()) {
      return this.withTransferRows(this.engine.queue());
    }
    return this.run()?.result?.items ?? [];
  });

  /** How many of `items()` are a `done` adopt — an existing target entry renamed in place rather
   *  than a new one added. What `RunProgressPanel.renamedCount` (spec #255) splits out of the
   *  dock's "N kopiert" into its own "M umbenannt". */
  readonly doneAdoptCount = computed(
    () =>
      this.items().filter(
        (item) => item.status === 'done' && item.transfer.action === 'adoptSourceName',
      ).length,
  );

  // The dock's signals below are projections of the shown run's record (#256, Plan-256
  // Festlegung 14): `linkedSignal`, so they follow `run()` and stay writable for the specs that
  // drive a dock directly. Production code never writes them — it writes the record.

  /** State of the shown run's closing sync-imported call. Never 'partial': the endpoint answers
   *  204 without a body, so there is no per-id outcome to compare against. */
  readonly syncReport = linkedSignal<SyncReportState>(() => this.run()?.syncReport ?? 'idle');

  /** State of the shown run's closing sync-deleted call for the replace rows' confirmed REMOVEs —
   *  the same vocabulary and evaluation as the delete run's own report. Stays `'idle'` for a run
   *  without a confirmed REMOVE. */
  readonly removalReport = linkedSignal<SyncReportState>(() => this.run()?.removalReport ?? 'idle');

  /** Why `removalReport` is `'failed'`/`'partial'` (spec E23), `null` otherwise — the dock shows it
   *  as its own line under the removal-report notice. */
  readonly removalReportReason = linkedSignal<SyncReportReason | null>(
    () => this.run()?.removalReportReason ?? null,
  );

  readonly resyncTrigger = linkedSignal<ResyncTriggerState>(
    () => this.run()?.resyncTrigger ?? 'idle',
  );

  /** True once a row of the shown run failed for missing 7TV write privileges and the run gave up
   *  because of it — the summary says so instead of listing every cancelled row as an ordinary
   *  failure. */
  readonly abortedForPrivileges = linkedSignal<boolean>(
    () => this.run()?.abortedForPrivileges ?? false,
  );

  /** Whether the shown run's transfer-run protocol was downloaded at least once — the reminder next
   *  to the dock's Close button (`import.summary.protocolNotSaved`), since `reset()` leaves the
   *  downloaded file as the only durable artifact. */
  readonly protocolSaved = linkedSignal<boolean>(() => this.run()?.protocolSaved ?? false);

  /** How many rows the caller's fresh pre-run duplicate check (#149/T5, `already-present-filter.ts`,
   *  run from `import-flow.ts` right before this call) dropped on top of the dialog-time
   *  `buildImportPreview` filter — surfaced so a run where the fresh check caught everything is not
   *  a silent no-op. Set unconditionally, even when the engine then refuses to start. */
  readonly skippedDuplicates = signal(0);

  /** How many `replace` rows the caller's fresh pre-run check held back because their target no
   *  longer matched what the user confirmed (`verifyReplaceTargets`), or could not be checked at
   *  all. Set unconditionally, like `skippedDuplicates`. */
  readonly replaceSkippedDrift = signal(0);

  /** Whether the caller's fresh pre-send duplicate check (#149/T5, `already-present-filter.ts`)
   *  actually ran — `false` means its fetch failed, so `rows` passed through unfiltered and an
   *  undetected duplicate is possible in this run. Same vocabulary as
   *  `AlreadyPresentFilterResult.available`; see that type's doc for why a failed check must not
   *  read as a clean `skippedDuplicates: 0`. Defaults to `true` so existing callers/tests that omit
   *  it keep reading as "checked, nothing to skip". */
  readonly duplicateCheckAvailable = signal(true);

  /** #149 P2 (independent review): whether the notice built from the signals above should
   *  currently be shown — true for `DUPLICATE_NOTICE_MS` after any `startImport` call that had
   *  something to report (`skippedDuplicates > 0 || !duplicateCheckAvailable || replaceSkippedDrift
   *  > 0`), including a refused (all-duplicates, or all-drift) call.
   *  `dockVisible()` (`usage-stats-page.ts`, via `action-dock.ts`) treats this
   *  exactly like an active run, which is what lets `import-progress-section` mount at all in that
   *  refused case — without it the section's own gate (`isRunning() || queue().length > 0`) would
   *  never fire, since a refused call leaves both false, and the notice that is the run's *only*
   *  outcome would be unreachable. Self-clearing rather than requiring a manual dismiss for the same
   *  reason `usage-stats-page`'s `selectionPrunedFeedback` is (design doc §4.5): a refused call has
   *  no run/queue for a dismiss button to attach to, and a persistent flag would otherwise be able
   *  to sit next to an unrelated *later* run's details with nothing to clear it. */
  readonly duplicateNoticePending = signal(false);

  /** Why the shared pre-check (spec 4.2, 6.2, E19) blocked a plan with at least one replace row
   *  before `import-flow.ts`'s `start()` ever reached `recheckTransferPlan` (spec 4.5 point 17) —
   *  `null` whenever nothing is currently blocked. Set by {@link reportTargetCheckBlocked}, which
   *  hangs the abort on `duplicateNoticePending`'s own transient-notice mechanic (0.2 of the plan):
   *  a blocked pre-check starts no run either, so this is what keeps the dock mounted long enough to
   *  show the reason. Cleared by the next `startImport()` call, whatever it does, and by `reset()`. */
  readonly targetCheckBlockReason = signal<TargetCheckBlockReason | null>(null);

  private duplicateNoticeTimeout: ReturnType<typeof setTimeout> | undefined;

  /** The plan rows of the shown run by queue key — what `items` attaches to the engine's rows. */
  private readonly transferRowsByKey = computed(() => indexPlanRows(this.run()?.plan ?? null));

  constructor() {
    // The `beforeunload` guard: registered exactly while `destructiveOpen` is `true`, removed the
    // moment it flips back — never after every destructive run has closed, never for a plan
    // without a replace row. `preventUnload` is a module-level function, not a closure created
    // here, so `removeEventListener` always targets the exact reference `addEventListener`
    // registered; an inline arrow function would silently fail to remove itself.
    effect(() => {
      if (this.destructiveOpen()) {
        window.addEventListener('beforeunload', preventUnload);
      } else {
        window.removeEventListener('beforeunload', preventUnload);
      }
    });
    this.destroyRef.onDestroy(() => window.removeEventListener('beforeunload', preventUnload));
  }

  /** `plan` is expected to come from `buildTransferPlan` — deduplicated per source id, validated —
   *  and already re-checked against the live target set right before this call (`import-flow.ts`);
   *  this method does no filtering of its own. `target.channelName` is `null` for an untracked
   *  target (T2.6, spec 8.6) — see `ImportRunInfo.targetChannelName` for what that changes
   *  downstream. `target.setName` and `target.isActiveSet` default to the id and to `true`
   *  respectively — every caller written before these two params existed omits both and keeps
   *  reading exactly as it did (an active-set target, named by its id until a real name is known),
   *  since every one of them only ever targeted the channel's active set. `skippedDuplicates` is
   *  the caller's own count from the *fresh* re-check it ran just before
   *  this call (see `already-present-filter.ts`) — defaults to 0. `duplicateCheckAvailable` mirrors
   *  the same call's `available` and defaults to `true` for the same reason. `replaceSkippedDrift`
   *  is the same re-check's count of held-back `replace` rows, default 0.
   *
   *  No guard against a `replace` row targeting an untracked account any more (#253, spec 4.5
   *  point 16/6.6, DECISIONS "The replace lock for an untracked target falls"): the removal report
   *  is set-centric (spec 6.5) regardless of whether the target is tracked, so there is nothing left
   *  here to refuse it for. The caller's own pre-check (`import-flow.ts`'s `start()`,
   *  {@link reportTargetCheckBlocked}) already kept an unreadable/inaccessible target from reaching
   *  this call at all.
   *
   *  A run that starts is shown at once; the run shown before it goes on to close on its own record
   *  (#256). */
  startImport(
    target: {
      setId: string;
      channelName: string | null;
      ownerDisplayName?: string | null;
      setName?: string;
      isActiveSet?: boolean;
    },
    origin: ImportOrigin,
    plan: TransferPlan,
    skippedDuplicates = 0,
    duplicateCheckAvailable = true,
    replaceSkippedDrift = 0,
  ): void {
    const deletes = plan.rows.some((row) => row.action === 'replace');

    this.targetCheckBlockReason.set(null);
    this.skippedDuplicates.set(skippedDuplicates);
    this.duplicateCheckAvailable.set(duplicateCheckAvailable);
    this.replaceSkippedDrift.set(replaceSkippedDrift);
    // Also fires for a run held back entirely by drift (every replace row skipped, nothing
    // queued) — without this, such a run would leave no run/queue behind at all *and* no notice,
    // which is exactly the silence AK 16's "the protocol appears after every run" exists against.
    this.showDuplicateNotice(
      skippedDuplicates > 0 || !duplicateCheckAvailable || replaceSkippedDrift > 0,
    );
    // The 7TV id is the only identity an imported row has — the emote does not exist in our
    // database yet, so there is no internal `emoteId` to mirror the key from. Unique per run: the
    // preview deduplicates by id, and no action makes two rows of one source id.
    const queueEmotes: RunQueueEmote[] = plan.rows.map((row) => ({
      key: row.source.sevenTvEmoteId,
      sevenTvEmoteId: row.source.sevenTvEmoteId,
      name: row.source.name,
    }));
    const context: ImportRunContext = {
      rowsByKey: indexPlanRows(plan),
      gqlStatusByKey: new Map(),
    };
    const started: ImportRunInfo = {
      runId: this.lifecycle.createRunId(),
      phase: 'running',
      destructive: deletes,
      targetChannelName: target.channelName,
      targetOwnerDisplayName: target.ownerDisplayName ?? null,
      targetSetId: target.setId,
      targetSetName: target.setName ?? target.setId,
      targetIsActiveSet: target.isActiveSet ?? true,
      origin,
      plan,
      settlement: 'pending',
      removedCount: 0,
      unknownCount: 0,
      unknownRemovalCount: 0,
      result: null,
      syncReport: 'idle',
      removalReport: 'idle',
      removalReportReason: null,
      resyncTrigger: 'idle',
      abortedForPrivileges: false,
      protocolSaved: false,
    };

    // Opened *before* the engine is asked to start (#256 review finding): the engine answers
    // asynchronously in practice, but only this ordering guarantees that a synchronous
    // `onComplete` — however unlikely — always finds its record already registered, rather than
    // updating a run the lifecycle does not know about yet, which would then never close.
    const previousShown = this.lifecycle.shown();
    this.lifecycle.open(started);
    if (
      !this.engine.start(
        target.setId,
        queueEmotes,
        this.createOperation(started.runId, context, deletes),
        (result) => this.onRunComplete(started.runId, context, result),
      )
    ) {
      // Refused (already running, empty list, no token) — take the just-opened record back and
      // restore whatever was shown before it, which may be another run still settling its reports.
      // Every signal above (skippedDuplicates, duplicateCheckAvailable, replaceSkippedDrift) stays
      // as set: an all-duplicates import is a legitimate "refused" case whose count (and whether it
      // is even trustworthy) the caller still needs to see.
      this.lifecycle.discardUnstarted(started.runId, previousShown);
    }
  }

  cancel(): void {
    this.engine.cancel();
  }

  /** Clears what the dock shows — and only that (#256, Plan-256 Festlegung 3). The shown run goes
   *  on on its own record: a run still in flight runs to its end (never cancelled here: a request
   *  7TV may already have applied must still be reported), a re-read still out settles it, and its
   *  reports go out and are answered. The engine's queue belongs to a run in flight until
   *  `finish()` has built its result from it, so it is cleared then (`onRunComplete`), not here. */
  reset(): void {
    if (!this.engine.isRunning()) {
      this.engine.reset();
    }
    this.lifecycle.detach();
    this.skippedDuplicates.set(0);
    this.replaceSkippedDrift.set(0);
    this.duplicateCheckAvailable.set(true);
    this.targetCheckBlockReason.set(null);
    this.showDuplicateNotice(false);
  }

  /** Called by `import-flow.ts`'s `start()` when the shared pre-check (spec 4.2, 6.2) blocks a plan
   *  with at least one replace row, before `recheckTransferPlan` even runs (spec 4.5 point 17) —
   *  nothing starts, and the reason is shown at the same transient spot a drift abort uses
   *  (`duplicateNoticePending`, plan 0.2): a blocked pre-check leaves no run/queue behind either.
   *  Final fix wave A7: also resets `skippedDuplicates`, `duplicateCheckAvailable` and
   *  `replaceSkippedDrift` to their neutral values, same as `startImport` does on every call —
   *  without this, a still-shown finished run's counts survived into this call's own notice and
   *  re-announced "N skipped as duplicate" next to a block reason that has nothing to do with it. */
  reportTargetCheckBlocked(reason: TargetCheckBlockReason): void {
    this.targetCheckBlockReason.set(reason);
    this.skippedDuplicates.set(0);
    this.duplicateCheckAvailable.set(true);
    this.replaceSkippedDrift.set(0);
    this.showDuplicateNotice(true);
  }

  /** Records on the shown run that its transfer-run protocol was downloaded. */
  markProtocolSaved(): void {
    const shown = this.run();
    if (shown !== null) {
      this.lifecycle.update(shown.runId, (run) => ({ ...run, protocolSaved: true }));
    }
  }

  /** Manual retry for the closing report of the shown run — the 7TV adds are long done, so this
   *  only re-sends the bookkeeping call. Safe to repeat: the endpoint only writes an audit entry.
   *  Target *and* keys come from the same record, so a retry can never mix one run's keys with
   *  another's channel. A retry on a closed run does not reopen it (#256): it is a new report on a
   *  closed run, and neither the arbiter nor the unload guard sees it. */
  retrySyncReport(): void {
    const current = this.run();
    if (
      current === null ||
      current.syncReport === 'pending' ||
      current.settlement !== 'settled' ||
      importedKeys(current).length === 0
    ) {
      return;
    }

    this.reportImported(current.runId);
  }

  /** Manual retry for the removal report — same rules as `retrySyncReport`, same record, and none
   *  for either channel-mismatch reason (addendum N4, AK 40): it is recorded and, for
   *  activeSetDiffers, its resync already runs, so a retry could only write the same mismatch
   *  again. */
  retryRemovalReport(): void {
    const current = this.run();
    if (
      current === null ||
      current.removalReport === 'pending' ||
      isChannelMismatch(current.removalReportReason) ||
      current.settlement !== 'settled' ||
      removedTargetIds(current).length === 0
    ) {
      return;
    }

    this.reportRemoved(current.runId);
  }

  /** The operation for one run. Built per run rather than once per service: every request depends
   *  on the row's plan entry, and `abortOn` records into this run's own context and record. */
  private createOperation(
    runId: string,
    context: ImportRunContext,
    deletes: boolean,
  ): RunOperation {
    const rowOf = (emote: RunQueueEmote): TransferRow =>
      transferRowOf(context.rowsByKey, emote.key);
    return {
      label: 'import',
      // Only a run that deletes asks for `unknown`: a lost answer there does not mean "not
      // applied", for any of its rows. A plan of `add` rows keeps `failed`, exactly as before.
      transportLossIsUnknown: deletes,
      stepCount: (emote) => (rowOf(emote).action === 'replace' ? 2 : 1),
      buildRequest: (setId, emote, step) => buildTransferRequest(setId, rowOf(emote), step),
      abortOn: (failure) => {
        this.recordFailedStepStatus(context, failure.gqlStatus);
        const abort = abortsForMissingPrivileges(failure);
        if (abort) {
          this.lifecycle.update(runId, (run) => ({ ...run, abortedForPrivileges: true }));
        }
        return abort;
      },
    };
  }

  /** `abortOn` does not name the row it is called for, but the engine calls it right after it set
   *  that row `failed` on the queue and before any other row moves (`RunOperation.abortOn`) — so
   *  the one `failed` row without a recorded status is the row this failure belongs to. */
  private recordFailedStepStatus(context: ImportRunContext, gqlStatus: number | null): void {
    const row = this.engine
      .queue()
      .find((item) => item.status === 'failed' && !context.gqlStatusByKey.has(item.key));
    if (row) {
      context.gqlStatusByKey.set(row.key, gqlStatus);
    }
  }

  /**
   * Turns the engine's snapshot into the run's settled outcome before anything is reported —
   * always on the run's own record, whether or not the dock still shows it (#256: there is no early
   * return for a run that is no longer shown; its confirmed changes are reported all the same).
   *
   * Without an `unknown` row the snapshot settles at once and the run goes straight to `reporting`
   * (or `closed`). With one, the run is `settling` while the target set is read live once
   * (tokenless, 7TV's global bucket) and each `unknown` row is cleared up on a *copy* of the
   * snapshot's rows (`settleUnknownRow`). A read that fails, runs out of time
   * (`SETTLE_READ_TIMEOUT_MS`) or comes back `complete: false` leaves those rows `unknown` — the
   * result settles all the same.
   *
   * A run `reset()` detached while it was in flight has left its queue on the engine until now,
   * because `finish()` builds this very result from it; nothing shows that queue any more, so it
   * is cleared here.
   */
  private onRunComplete(runId: string, context: ImportRunContext, result: RunResult): void {
    const snapshot: ImportRunResult = {
      ...result,
      items: result.items.map((item) => ({
        ...item,
        transfer: transferRowOf(context.rowsByKey, item.key),
      })),
    };
    const hasUnknown = snapshot.items.some((item) => item.status === 'unknown');
    const pending = this.lifecycle.update(runId, (run) => ({
      ...run,
      result: snapshot,
      ...outcomeCounts(snapshot),
      phase: hasUnknown ? 'settling' : run.phase,
    }));
    if (!this.lifecycle.isShown(runId)) {
      this.engine.reset();
    }
    if (pending === null) {
      // Unreachable: a run is only ever dropped once it is closed, and it cannot close before this.
      return;
    }

    if (!hasUnknown) {
      this.settleRun(runId, context, null);
      return;
    }

    loadSevenTvSetEntries(this.httpClient, pending.targetSetId)
      .pipe(
        timeout(SETTLE_READ_TIMEOUT_MS),
        catchError(() => of(null)),
      )
      .subscribe((entries) => this.settleRun(runId, context, entries));
  }

  /** Publishes the settled outcome on the run's record and opens its reports in the same step, so
   *  the record goes from `settling`/`running` to `reporting` — or straight to `closed` when there
   *  is nothing to report — without a moment in which it looks closed with a report still to come. */
  private settleRun(
    runId: string,
    context: ImportRunContext,
    entries: SevenTvSetEntries | null,
  ): void {
    const snapshot = this.lifecycle.get(runId)?.result ?? null;
    if (snapshot === null) {
      return;
    }
    const translate = (key: string): string => this.translocoService.translate(key);
    const result = settleRunResult(snapshot, context, entries, translate);
    const reportsImported = importedKeysOf(result).length > 0;
    const reportsRemoved = removedTargetIdsOf(result).length > 0;
    const settled = this.lifecycle.update(runId, (run) => ({
      ...run,
      result,
      settlement: 'settled',
      phase: 'reporting',
      ...outcomeCounts(result),
      syncReport: reportsImported ? 'pending' : run.syncReport,
      removalReport: reportsRemoved ? 'pending' : run.removalReport,
      removalReportReason: reportsRemoved ? null : run.removalReportReason,
    }));
    if (settled !== null) {
      this.sendFollowUp(settled);
    }
  }

  /** The reports and the resync of a settled run. `sync-imported` names every `done` row that
   *  added an emote (`add`, `renameSource`, `replace`); the removal report names the target of
   *  every replace row whose REMOVE 7TV confirmed, whatever the row ended as; an adopt row reports
   *  nothing. */
  private sendFollowUp(run: ImportRunInfo): void {
    const imported = importedKeys(run);
    const removed = removedTargetIds(run);

    // The report is the audit trail for exactly these ids, always sent — this call is never gated
    // on `targetIsActiveSet` (finding 3, Live-Verifikation K2 2026-09-21): the audit entry and the
    // "which set did this land in" bookkeeping (`targetEmoteSetId`) are correct regardless of which
    // set that is.
    if (imported.length > 0) {
      this.reportImported(run.runId);
    }

    // The resync is what actually pulls the changed emote rows into the *channel's active* set
    // view — only meaningful for a *tracked target on its active set* (T2.6/8.6 for the channel
    // half, finding 3 for the active-set half): an untracked target has no `Channel` of ours to
    // resync at all, and a tracked *non*-active target has one, but resyncing it would re-sync the
    // channel's active set, not the set this run actually wrote to — a request that succeeds while
    // confirming nothing the user cares about. It fires for any change 7TV confirmed: an added, a
    // removed or a renamed (adopted) entry.
    const adopted = run.result?.items.some(
      (item) => item.status === 'done' && item.transfer.action === 'adoptSourceName',
    );
    const resyncChannel =
      (imported.length > 0 || removed.length > 0 || adopted === true) && run.targetIsActiveSet
        ? run.targetChannelName
        : null;

    if (removed.length > 0) {
      // With a removal report, the resync waits for its answer (spec 6.5, F15): the backend resyncs
      // every channel that report touched and names it in `resyncTriggered` — a second resync of
      // ours would only run into the per-channel cooldown. A failed report names nothing, so the
      // resync runs as it always did.
      this.reportRemoved(run.runId, (resyncTriggered) => {
        if (resyncChannel !== null && !includesChannel(resyncTriggered, resyncChannel)) {
          this.triggerResync(run.runId, resyncChannel);
        }
      });
      return;
    }
    if (resyncChannel !== null) {
      this.triggerResync(run.runId, resyncChannel);
    }
  }

  private triggerResync(runId: string, channelName: string): void {
    this.patchRun(runId, { resyncTrigger: 'pending' });
    this.channelService.resync(channelName).subscribe({
      next: () => this.patchRun(runId, { resyncTrigger: 'succeeded' }),
      error: (error: HttpErrorResponse) =>
        // 429 = the per-channel cooldown: a sync just ran or will run — "coming on its own",
        // reported as such rather than as an error.
        this.patchRun(runId, { resyncTrigger: error.status === 429 ? 'cooldown' : 'failed' }),
    });
  }

  private reportImported(runId: string): void {
    const run = this.patchRun(runId, { syncReport: 'pending' });
    if (run === null) {
      return;
    }

    // Through the two exhaustive helpers, never through a `=== 'channel'`/`=== 'seventv-leaderboard'`
    // test: this call runs after the 7TV mutations, so a kind that silently loses its source name
    // or sort here is answered with a 400 when the emotes are already copied and the provenance is
    // unrecoverable (spec F6/F1). A file still sends `null` for the source channel even when it
    // names one, and every non-leaderboard origin sends `null` for the sort — both rules live in
    // the helpers, not here.
    const bodyBase = {
      sevenTvEmoteIds: importedKeys(run),
      sourceChannelName: importOriginSourceChannelName(run.origin),
      sourceKind: run.origin.kind,
      leaderboardSort: importOriginLeaderboardSort(run.origin),
    };

    // Tracked target → the channel-scoped endpoint, `targetEmoteSetId` sent on every call this
    // client makes (spec 6.7/E5, AK 44) — the loaded target's own set, known by the time a run even
    // started (`ImportRunInfo.targetSetId`), never omitted just because the server keeps the field
    // optional for an older client. Untracked target → the set-centric endpoint (spec 6.7, T2.6):
    // the route already names the set, so the body carries no `targetEmoteSetId` at all, and there
    // is no channel of ours for this call to write the entry against (`ChannelName = null`).
    const report$ =
      run.targetChannelName !== null
        ? this.emoteAdminService.syncImported(run.targetChannelName, {
            ...bodyBase,
            targetEmoteSetId: run.targetSetId,
          })
        : this.emoteSetService.reportImportedToSet(run.targetSetId, bodyBase);

    report$.pipe(timeoutReportAttempt(), retryTransientSyncFailures()).subscribe({
      next: () => this.endReport(runId, 'sync-imported', { syncReport: 'succeeded' }),
      error: () => this.endReport(runId, 'sync-imported', { syncReport: 'failed' }),
    });
  }

  /** The removal report: the set-centric `sync-deleted` (spec 6.5), the delete run's own
   *  bookkeeping call — addressed to the set the run wrote into, tracked or not, with the target's
   *  channel as the expected hit only when that set is the channel's active one (E18).
   *  `afterReport` (the first report only, never a manual retry) runs once it has settled either
   *  way, with the answer's `resyncTriggered` or, on failure, an empty list; it runs for a run that
   *  is no longer shown just the same — its answer lands on its own record. */
  private reportRemoved(
    runId: string,
    afterReport?: (resyncTriggered: readonly string[]) => void,
  ): void {
    const run = this.patchRun(runId, { removalReport: 'pending', removalReportReason: null });
    if (run === null) {
      return;
    }
    const sevenTvEmoteIds = [...new Set(removedTargetIds(run))];

    this.emoteSetService
      .reportDeletedInSet(run.targetSetId, {
        sevenTvEmoteIds,
        expectedChannelName: run.targetIsActiveSet ? run.targetChannelName : null,
      })
      .pipe(
        timeoutReportAttempt(),
        // The threeway reading (`map`, not inside `next:`) lives ahead of `retryTransientSyncFailures`
        // so a malformed 200 answer that makes `classifySyncInSetResponse` throw ends the report
        // like any other transient failure — an uncaught throw inside a `next:` callback would
        // otherwise leave this run `reporting` forever, never `closed` (#256 review finding).
        map((answer) => ({
          outcome: classifySyncInSetResponse(answer, sevenTvEmoteIds.length),
          resyncTriggered: answer.resyncTriggered,
        })),
        retryTransientSyncFailures(),
      )
      .subscribe({
        next: ({ outcome, resyncTriggered }) => {
          this.endReport(runId, 'sync-deleted', {
            removalReport: outcome.state,
            removalReportReason: outcome.reason,
          });
          afterReport?.(resyncTriggered);
        },
        error: (error: HttpErrorResponse) => {
          const outcome = classifySyncInSetFailure(error.status);
          this.endReport(runId, 'sync-deleted', {
            removalReport: outcome.state,
            removalReportReason: outcome.reason,
          });
          afterReport?.([]);
        },
      });
  }

  /** Writes a report's end state onto its run's record — which closes the run once it was the last
   *  report out — and brings a run nobody shows back onto the dock when that end state is not a
   *  success (Plan-256 Festlegung 13): a failed or partial report needs a place with its reason and
   *  a retry. Shown again only when nothing else is shown and no run is in flight; otherwise the
   *  failure stays on the record and in the console.
   *
   *  `reshow` is attempted before `showFinishedRows` and rolled back with `detach()` if that then
   *  refuses (#256 review finding): reshowing must not leave the dock pointing at a run whose queue
   *  never actually reappeared, and checking `showFinishedRows` first would risk pushing a
   *  to-be-rejected run's rows onto the engine's queue for an already-shown run to inherit. */
  private endReport(
    runId: string,
    report: 'sync-imported' | 'sync-deleted',
    patch: Pick<Partial<ImportRunInfo>, 'syncReport' | 'removalReport' | 'removalReportReason'>,
  ): void {
    const run = this.patchRun(runId, patch);
    const state = report === 'sync-imported' ? run?.syncReport : run?.removalReport;
    if (run === null || state === 'succeeded' || this.lifecycle.isShown(runId)) {
      return;
    }
    if (run.result !== null && !this.engine.isRunning() && this.lifecycle.reshow(run)) {
      if (this.engine.showFinishedRows(run.result.items)) {
        return;
      }
      this.lifecycle.detach();
    }
    console.warn('[EmotePurge] 7TV import report of a run no longer shown did not succeed', {
      runId,
      report,
      state,
      reason: report === 'sync-deleted' ? run.removalReportReason : null,
    });
  }

  /** Merges `patch` into the record of `runId` — see `SevenTvRunLifecycle.update`. */
  private patchRun(runId: string, patch: Partial<ImportRunInfo>): ImportRunInfo | null {
    return this.lifecycle.update(runId, (run) => ({ ...run, ...patch }));
  }

  /** The engine's live rows with their plan rows attached — the shape `items` promises. Total: a
   *  row the shown plan does not know is left out rather than thrown on, since a throw inside a
   *  computed a template reads breaks the whole panel. */
  private withTransferRows(queue: RunQueueItem[]): ImportRunItem[] {
    const rowsByKey = this.transferRowsByKey();
    return queue.flatMap((item) => {
      const transfer = rowsByKey.get(item.key);
      return transfer === undefined ? [] : [{ ...item, transfer }];
    });
  }

  /** #149 P2: `hasSomethingToReport` clears any earlier timer first — a second call within
   *  `DUPLICATE_NOTICE_MS` of the first must not let the first timer's clear race the new one and
   *  hide a still-current notice out from under it. */
  private showDuplicateNotice(hasSomethingToReport: boolean): void {
    clearTimeout(this.duplicateNoticeTimeout);
    if (!hasSomethingToReport) {
      this.duplicateNoticePending.set(false);
      return;
    }
    this.duplicateNoticePending.set(true);
    this.duplicateNoticeTimeout = setTimeout(
      () => this.duplicateNoticePending.set(false),
      DUPLICATE_NOTICE_MS,
    );
  }
}

/** The standard `beforeunload` incantation (MDN): calling `preventDefault()` and setting a
 *  non-undefined `returnValue` is what makes the browser show its own confirmation prompt — neither
 *  Chromium, Firefox nor Safari display a custom string any more, so the exact value assigned here
 *  is irrelevant, only that one is set. A plain module-level function, not a closure created inside
 *  the constructor's `effect()`, so `removeEventListener` always targets the exact function
 *  reference `addEventListener` registered — an inline arrow recreated on every effect run would
 *  silently fail to remove itself. */
function preventUnload(event: BeforeUnloadEvent): void {
  event.preventDefault();
  event.returnValue = '';
}

/** Whether `channelName` is among the channels a report's answer says the backend resynced —
 *  case-insensitive, since the backend answers with normalized names. */
function includesChannel(channels: readonly string[], channelName: string): boolean {
  const normalized = channelName.toLowerCase();
  return channels.some((channel) => channel.toLowerCase() === normalized);
}

/** Same policy as the delete's and the restore's report: waiting can fix a 429/5xx, not a
 *  401/403. */
function retryTransientSyncFailures<T>() {
  return retry<T>({
    count: MAX_AUTOMATIC_SYNC_RETRIES,
    delay: (error: HttpErrorResponse, attempt) =>
      error.status === 401 || error.status === 403
        ? throwError(() => error)
        : timer(SYNC_RETRY_DELAY_MS * attempt),
  });
}

function indexPlanRows(plan: TransferPlan | null): ReadonlyMap<string, TransferRow> {
  return new Map((plan?.rows ?? []).map((row) => [row.source.sevenTvEmoteId, row]));
}

function transferRowOf(rowsByKey: ReadonlyMap<string, TransferRow>, key: string): TransferRow {
  const row = rowsByKey.get(key);
  if (row === undefined) {
    throw new Error(`No transfer plan row for queue key ${key}.`);
  }
  return row;
}

/** The request for one step of one plan row. Only a `replace` row has two steps: the REMOVE of the
 *  target id (which takes every entry of that id), then the ADD of the source under the freed
 *  name, against the same set. */
function buildTransferRequest(
  setId: string,
  row: TransferRow,
  step: number,
): { query: string; variables: Record<string, unknown> } {
  switch (row.action) {
    case 'add':
    case 'renameSource':
      return addRequest(setId, row.source.sevenTvEmoteId, row.alias);
    case 'replace':
      return step === 0
        ? {
            query: REMOVE_EMOTE_MUTATION,
            variables: { setId, emoteId: row.target.sevenTvEmoteId },
          }
        : addRequest(setId, row.source.sevenTvEmoteId, row.alias);
    case 'adoptSourceName':
      // `adoptBlocked === null` guarantees the target has at most one named alias
      // (`'duplicateTarget'` rules out two) and at least one (`'aliaslessTarget'` rules out zero) —
      // so exactly one, which is why this reads `row.target.aliases[0]` unconditionally.
      return {
        query: UPDATE_EMOTE_ALIAS_MUTATION,
        variables: {
          setId,
          emoteId: row.target.sevenTvEmoteId,
          currentAlias: row.target.aliases[0],
          alias: row.alias,
        },
      };
    default:
      return assertUnreachableTransferRow(row);
  }
}

function addRequest(
  setId: string,
  emoteId: string,
  alias: string,
): { query: string; variables: Record<string, unknown> } {
  return { query: ADD_EMOTE_MUTATION, variables: { setId, emoteId, alias } };
}

/** Source ids of every `done` row of `run` that added an emote — what `sync-imported` names. */
function importedKeys(run: ImportRunInfo): string[] {
  return importedKeysOf(run.result);
}

function importedKeysOf(result: ImportRunResult | null): string[] {
  return (result?.items ?? [])
    .filter((item) => item.status === 'done' && item.transfer.action !== 'adoptSourceName')
    .map((item) => item.key);
}

/** Target ids of every replace row of `run` whose REMOVE 7TV confirmed, whatever the row ended as
 *  — what the removal report names. */
function removedTargetIds(run: ImportRunInfo): string[] {
  return removedTargetIdsOf(run.result);
}

function removedTargetIdsOf(result: ImportRunResult | null): string[] {
  return (result?.items ?? []).flatMap((item) =>
    item.transfer.action === 'replace' && item.completedSteps >= 1
      ? [item.transfer.target.sevenTvEmoteId]
      : [],
  );
}

function outcomeCounts(result: ImportRunResult): {
  removedCount: number;
  unknownCount: number;
  unknownRemovalCount: number;
} {
  return {
    removedCount: result.items.filter(
      (item) => item.transfer.action === 'replace' && item.completedSteps >= 1,
    ).length,
    unknownCount: result.items.filter((item) => item.status === 'unknown').length,
    unknownRemovalCount: result.items.filter(
      (item) =>
        item.status === 'unknown' && item.transfer.action === 'replace' && item.failedStep === 0,
    ).length,
  };
}

/**
 * The settled outcome of a run: every `unknown` row cleared up against `entries` where the read
 * allows it, every `failed` row given its import-specific reason, `doneKeys` recomputed. Works on
 * copies; the snapshot stays as it was. `entries` is `null` when there was no read (nothing was
 * `unknown`) or the read failed; an incomplete read counts as none.
 */
function settleRunResult(
  snapshot: ImportRunResult,
  context: ImportRunContext,
  entries: SevenTvSetEntries | null,
  translate: (key: string) => string,
): ImportRunResult {
  const readable = entries?.complete === true ? entries : null;
  const items = snapshot.items.map((item) => {
    if (item.status === 'unknown') {
      return readable === null ? item : settleUnknownRow(item, readable, translate);
    }
    if (item.status === 'failed') {
      return withFailureReason(item, context.gqlStatusByKey.get(item.key) ?? null, translate);
    }
    return item;
  });
  return {
    ...snapshot,
    items,
    doneKeys: items.filter((item) => item.status === 'done').map((item) => item.key),
  };
}

/** The reason a `failed` row shows. A replace row that failed after its REMOVE (`failedStep 1`,
 *  whatever 7TV said, a mid-row cancel included) left a gap in the set, and that is what it says.
 *  A name taken by the time the row ran (`extensions.status` 409 on an ADD or an alias UPDATE) says
 *  so in plain words. Every other failure keeps the engine's text. */
function withFailureReason(
  item: ImportRunItem,
  gqlStatus: number | null,
  translate: (key: string) => string,
): ImportRunItem {
  const action = item.transfer.action;
  if (action === 'replace' && item.failedStep === 1) {
    return withReason(item, translate('import.errors.removedButNotAdded'));
  }
  if (action !== 'replace' && gqlStatus === NAME_TAKEN_GQL_STATUS) {
    return withReason(item, translate('import.errors.nameTakenNow'));
  }
  return item;
}

/**
 * Clears up one `unknown` row against the re-read set:
 * - an unanswered ADD (`add`, `renameSource`, a replace's second step): the source id under the
 *   plan alias means it was applied (`done`); the source id not in the set at all means it was not
 *   (`failed` — with the gap reason for a replace, the generic unknown-outcome reason otherwise);
 *   the source id in the set under some other name cannot be told apart from a third party's
 *   entry and stays `unknown`;
 * - an unanswered REMOVE (a replace's first step): the target id still in the set means nothing
 *   happened (`failed` at step 0); gone means the REMOVE was applied and the ADD never sent —
 *   `failed` with the gap reason, and the REMOVE counts as confirmed (`completedSteps` 1);
 * - an unanswered alias UPDATE: the target id under the source name means `done`, under its old
 *   alias `failed`; anything else cannot be told apart and stays `unknown`.
 * A third party who added the same source id under the plan alias meanwhile is indistinguishable
 * from our own ADD; the emote *is* in the set under that name, which is what the report says.
 */
function settleUnknownRow(
  item: ImportRunItem,
  entries: SevenTvSetEntries,
  translate: (key: string) => string,
): ImportRunItem {
  const row = item.transfer;
  const holds = (id: string, alias: string): boolean =>
    entries.aliasesById.get(id)?.includes(alias) ?? false;
  const settleAdd = (completedSteps: number, notApplied: () => ImportRunItem): ImportRunItem => {
    if (holds(row.source.sevenTvEmoteId, row.alias)) {
      return asDone(item, completedSteps);
    }
    return entries.aliasesById.has(row.source.sevenTvEmoteId) ? item : notApplied();
  };
  const unknownOutcome = (): ImportRunItem => {
    const reason = translate('import.errors.unknownOutcome');
    return withReason(
      { ...item, status: 'failed' },
      item.errorMessage ? `${reason} (${item.errorMessage})` : reason,
    );
  };

  switch (row.action) {
    case 'add':
    case 'renameSource':
      return settleAdd(1, unknownOutcome);
    case 'replace':
      if (item.failedStep === 0) {
        if (entries.aliasesById.has(row.target.sevenTvEmoteId)) {
          return unknownOutcome();
        }
        return withReason(
          { ...item, status: 'failed', completedSteps: 1, failedStep: 1 },
          translate('import.errors.removedButNotAdded'),
        );
      }
      return settleAdd(2, () =>
        withReason({ ...item, status: 'failed' }, translate('import.errors.removedButNotAdded')),
      );
    case 'adoptSourceName':
      if (holds(row.target.sevenTvEmoteId, row.alias)) {
        return asDone(item, 1);
      }
      return holds(row.target.sevenTvEmoteId, row.target.aliases[0]) ? unknownOutcome() : item;
    default:
      return assertUnreachableTransferRow(row);
  }
}

function asDone(item: ImportRunItem, completedSteps: number): ImportRunItem {
  return { ...item, status: 'done', completedSteps, failedStep: null, errorMessage: undefined };
}

/** Replaces the displayed text with an import-specific reason and keeps what the engine had. */
function withReason(item: ImportRunItem, reason: string): ImportRunItem {
  return { ...item, errorMessage: reason, sevenTvErrorMessage: item.errorMessage ?? null };
}

function assertUnreachableTransferRow(row: never): never {
  throw new Error(`Unhandled transfer row: ${JSON.stringify(row)}`);
}
