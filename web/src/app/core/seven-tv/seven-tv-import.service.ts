import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Service, Signal, computed, effect, inject, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { catchError, of, retry, throwError, timeout, timer } from 'rxjs';

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
import { SevenTvSetEntries, loadSevenTvSetEntries } from './seven-tv-set-entries';
import { SevenTvTokenService } from './seven-tv-token.service';
import {
  SyncReportReason,
  SyncReportState,
  TargetCheckBlockReason,
  classifySyncInSetFailure,
  classifySyncInSetResponse,
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
 */
export type ImportSettlement = 'pending' | 'settled';

/**
 * One import run, from the moment it starts to the moment its bookkeeping is done. Everything the
 * closing calls need hangs off *this* object, never off a field next to the service (R15, #72):
 * the engine sets `isRunning` back to `false` inside `finish()`, i.e. *before* `onComplete` fires
 * the asynchronous follow-up, and the arbiter derives "a run is active" from exactly that signal —
 * so a second import can legitimately start while the first one's `sync-imported`/`resync` are
 * still in flight. With a target channel in one field and the reported keys in another, a late
 * answer (or a manual retry) of run 1 could be applied to run 2's target: emote rows pushed into a
 * third channel and an audit entry naming the wrong origin. Bound to the record, a late answer is
 * simply no longer `run()` and is dropped.
 */
export interface ImportRunInfo {
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
  /** `null` while the run is in flight; the engine's snapshot once it completes, replaced by the
   *  settled outcome when `settlement` turns `'settled'`. */
  result: ImportRunResult | null;
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
 * - **The follow-up hangs off `run()`, not off loose fields** — see `ImportRunInfo`.
 *
 * A run executes a `TransferPlan`: one queue row per plan row, and per action the mutations the
 * row needs — an ADD for `add`/`renameSource`, a REMOVE of the target and then an ADD for
 * `replace`, an alias UPDATE for `adoptSourceName`. A plan with a `replace` row is the one run of
 * this service that deletes, and three things follow from it: its rows end `unknown` on a lost
 * answer instead of `failed`, the run is re-read before anything is reported, and the confirmed
 * REMOVEs are reported through the set-centric `sync-deleted` next to the `sync-imported` of
 * the ADDs. A plan of `add` rows only runs exactly as a plain copy always did.
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

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  /** The run this service is currently showing — in flight (`result === null`) or finished. The
   *  identity of this object is what every asynchronous follow-up checks itself against. */
  readonly run = signal<ImportRunInfo | null>(null);

  /** The rows to show: the engine's live queue while a run is in flight, the shown run's own
   *  result once it is not. After a run this is the settled outcome of *that* run — never the
   *  engine's queue, which may already belong to a newer run. */
  readonly items: Signal<ImportRunItem[]> = computed(() => {
    if (this.engine.isRunning()) {
      return this.withTransferRows(this.engine.queue());
    }
    return this.run()?.result?.items ?? [];
  });

  /** True while a run that deletes (its plan has a `replace` row) is in flight *or* still waiting
   *  for its re-read — what a `beforeunload` guard hangs off. The pending window counts: until the
   *  run settles, the report of its confirmed REMOVEs has not gone out, and closing the tab then
   *  would lose it. */
  readonly destructiveRunActive = computed(() => {
    const run = this.run();
    const active = this.engine.isRunning() || run?.settlement === 'pending';
    return active && (run?.plan.rows.some((row) => row.action === 'replace') ?? false);
  });

  /** State of the closing sync-imported call. Never 'partial': the endpoint answers 204 without a
   *  body, so there is no per-id outcome to compare against. */
  readonly syncReport = signal<SyncReportState>('idle');

  /** State of the closing sync-deleted call for the replace rows' confirmed REMOVEs — the same
   *  vocabulary and evaluation as the delete run's own report. Stays `'idle'` for a run without
   *  a confirmed REMOVE. */
  readonly removalReport = signal<SyncReportState>('idle');

  /** Why `removalReport` is `'failed'`/`'partial'` (spec E23), `null` otherwise — the dock shows it
   *  as its own line under the removal-report notice. */
  readonly removalReportReason = signal<SyncReportReason | null>(null);

  readonly resyncTrigger = signal<ResyncTriggerState>('idle');

  /** True once a row failed for missing 7TV write privileges and the run gave up because of it —
   *  the summary says so instead of listing every cancelled row as an ordinary failure. */
  readonly abortedForPrivileges = signal(false);

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

  /** Whether the shown run's transfer-run protocol was downloaded at least once — the reminder next
   *  to the dock's Close button (`import.summary.protocolNotSaved`), since `reset()` leaves the
   *  downloaded file as the only durable artifact. Reset to `false` by every `startImport()` call
   *  that actually starts a run and by `reset()`. */
  readonly protocolSaved = signal(false);

  private duplicateNoticeTimeout: ReturnType<typeof setTimeout> | undefined;

  /** The plan rows of the shown run by queue key — what `items` attaches to the engine's rows. */
  private readonly transferRowsByKey = computed(() => indexPlanRows(this.run()?.plan ?? null));

  constructor() {
    // The `beforeunload` guard: registered exactly while `destructiveRunActive` is
    // `true`, removed the moment it flips back — never after the run (settlement clears it), never
    // for a plan without a replace row. `preventUnload` is a module-level function, not a closure
    // created here, so `removeEventListener` always targets the exact reference `addEventListener`
    // registered; an inline arrow function would silently fail to remove itself.
    effect(() => {
      if (this.destructiveRunActive()) {
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
   *  this call at all. */
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
      result: null,
    };

    if (
      !this.engine.start(
        target.setId,
        queueEmotes,
        this.createOperation(context, deletes),
        (result) => this.onRunComplete(started, context, result),
      )
    ) {
      // Refused (already running, empty list, no token) — leave every signal as it was, except
      // skippedDuplicates, duplicateCheckAvailable and replaceSkippedDrift above: an
      // all-duplicates import is a legitimate "refused" case whose count (and whether it is even
      // trustworthy) the caller still needs to see.
      return;
    }

    this.run.set(started);
    this.syncReport.set('idle');
    this.removalReport.set('idle');
    this.removalReportReason.set(null);
    this.resyncTrigger.set('idle');
    this.abortedForPrivileges.set(false);
    this.protocolSaved.set(false);
  }

  cancel(): void {
    this.engine.cancel();
  }

  /** Clears what the dock shows. A re-read still in flight for the run shown so far keeps going:
   *  its outcome is not published any more, but its reports are still sent (see `settleRun`). */
  reset(): void {
    this.engine.reset();
    this.run.set(null);
    this.syncReport.set('idle');
    this.removalReport.set('idle');
    this.removalReportReason.set(null);
    this.resyncTrigger.set('idle');
    this.abortedForPrivileges.set(false);
    this.skippedDuplicates.set(0);
    this.replaceSkippedDrift.set(0);
    this.duplicateCheckAvailable.set(true);
    this.targetCheckBlockReason.set(null);
    this.showDuplicateNotice(false);
    this.protocolSaved.set(false);
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

  /** Manual retry for the closing report — the 7TV adds are long done, so this only re-sends the
   *  bookkeeping call. Safe to repeat: the endpoint only writes an audit entry. Target *and* keys
   *  come from the same record, so a retry can never mix one run's keys with another's channel. */
  retrySyncReport(): void {
    const current = this.run();
    if (
      this.syncReport() === 'pending' ||
      current?.settlement !== 'settled' ||
      importedKeys(current).length === 0
    ) {
      return;
    }

    this.reportImported(current);
  }

  /** Manual retry for the removal report — same rules as `retrySyncReport`, same record. */
  retryRemovalReport(): void {
    const current = this.run();
    if (
      this.removalReport() === 'pending' ||
      current?.settlement !== 'settled' ||
      removedTargetIds(current).length === 0
    ) {
      return;
    }

    this.reportRemoved(current);
  }

  /** The operation for one run. Built per run rather than once per service: every request depends
   *  on the row's plan entry, and `abortOn` records into this run's own context. */
  private createOperation(context: ImportRunContext, deletes: boolean): RunOperation {
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
          this.abortedForPrivileges.set(true);
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
   * Turns the engine's snapshot into the run's settled outcome before anything is reported.
   *
   * Without an `unknown` row the snapshot settles at once. With one, the target set is read live
   * once (tokenless, 7TV's global bucket) and each `unknown` row is cleared up on a *copy* of the
   * snapshot's rows (`settleUnknownRow`); the settled rows and the reports are then published
   * together. A read that fails, runs out of time (`SETTLE_READ_TIMEOUT_MS`) or comes back
   * `complete: false` leaves those rows `unknown` — the result settles all the same.
   *
   * The re-read is bound to this run, not to the service: when its answer arrives, the settled
   * outcome replaces `run()` only if `run()` is still this run's pending record. A second import
   * started in the meantime, or a `reset()`, keeps the outcome off the dock — but the reports are
   * sent regardless, because they record 7TV changes that happened (a confirmed REMOVE always
   * reaches the removal report), and their state signals are only ever written for the run on
   * screen (`applyIfCurrent`).
   */
  private onRunComplete(
    started: ImportRunInfo,
    context: ImportRunContext,
    result: RunResult,
  ): void {
    if (this.run() !== started) {
      // Only reachable via reset() during the run: the shown run is not this one any more, so
      // neither its result nor its bookkeeping belong on screen.
      return;
    }

    const snapshot: ImportRunResult = {
      ...result,
      items: result.items.map((item) => ({
        ...item,
        transfer: transferRowOf(context.rowsByKey, item.key),
      })),
    };
    // A new object rather than a mutation, so consumers of `run()` actually see the result.
    const pending: ImportRunInfo = { ...started, result: snapshot, ...outcomeCounts(snapshot) };
    this.run.set(pending);

    if (!snapshot.items.some((item) => item.status === 'unknown')) {
      this.settleRun(pending, context, null);
      return;
    }

    loadSevenTvSetEntries(this.httpClient, started.targetSetId)
      .pipe(
        timeout(SETTLE_READ_TIMEOUT_MS),
        catchError(() => of(null)),
      )
      .subscribe((entries) => this.settleRun(pending, context, entries));
  }

  private settleRun(
    pending: ImportRunInfo,
    context: ImportRunContext,
    entries: SevenTvSetEntries | null,
  ): void {
    const snapshot = pending.result;
    if (snapshot === null) {
      return;
    }
    const translate = (key: string): string => this.translocoService.translate(key);
    const result = settleRunResult(snapshot, context, entries, translate);
    const settled: ImportRunInfo = {
      ...pending,
      result,
      settlement: 'settled',
      ...outcomeCounts(result),
    };
    if (this.run() === pending) {
      // From here on this is the record the follow-up is bound to.
      this.run.set(settled);
    }
    this.sendFollowUp(settled);
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
      this.reportImported(run);
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
      this.reportRemoved(run, (resyncTriggered) => {
        if (resyncChannel !== null && !includesChannel(resyncTriggered, resyncChannel)) {
          this.triggerResync(run, resyncChannel);
        }
      });
      return;
    }
    if (resyncChannel !== null) {
      this.triggerResync(run, resyncChannel);
    }
  }

  private triggerResync(run: ImportRunInfo, channelName: string): void {
    this.applyIfCurrent(run, () => this.resyncTrigger.set('pending'));
    this.channelService.resync(channelName).subscribe({
      next: () => this.applyIfCurrent(run, () => this.resyncTrigger.set('succeeded')),
      error: (error: HttpErrorResponse) =>
        // 429 = the per-channel cooldown: a sync just ran or will run — "coming on its own",
        // reported as such rather than as an error.
        this.applyIfCurrent(run, () =>
          this.resyncTrigger.set(error.status === 429 ? 'cooldown' : 'failed'),
        ),
    });
  }

  private reportImported(run: ImportRunInfo): void {
    this.applyIfCurrent(run, () => this.syncReport.set('pending'));

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

    report$.pipe(retryTransientSyncFailures()).subscribe({
      next: () => this.applyIfCurrent(run, () => this.syncReport.set('succeeded')),
      error: () => this.applyIfCurrent(run, () => this.syncReport.set('failed')),
    });
  }

  /** The removal report: the set-centric `sync-deleted` (spec 6.5), the delete run's own
   *  bookkeeping call — addressed to the set the run wrote into, tracked or not, with the target's
   *  channel as the expected hit only when that set is the channel's active one (E18).
   *  `afterReport` (the first report only, never a manual retry) runs once it has settled either
   *  way, with the answer's `resyncTriggered` or, on failure, an empty list; it runs even for a
   *  superseded run, only the state written here is guarded (`applyIfCurrent`). */
  private reportRemoved(
    run: ImportRunInfo,
    afterReport?: (resyncTriggered: readonly string[]) => void,
  ): void {
    const sevenTvEmoteIds = [...new Set(removedTargetIds(run))];
    this.applyIfCurrent(run, () => {
      this.removalReport.set('pending');
      this.removalReportReason.set(null);
    });

    this.emoteSetService
      .reportDeletedInSet(run.targetSetId, {
        sevenTvEmoteIds,
        expectedChannelName: run.targetIsActiveSet ? run.targetChannelName : null,
      })
      .pipe(retryTransientSyncFailures())
      .subscribe({
        next: (answer) => {
          const outcome = classifySyncInSetResponse(answer, sevenTvEmoteIds.length);
          this.applyIfCurrent(run, () => {
            this.removalReport.set(outcome.state);
            this.removalReportReason.set(outcome.reason);
          });
          afterReport?.(answer.resyncTriggered);
        },
        error: (error: HttpErrorResponse) => {
          const outcome = classifySyncInSetFailure(error.status);
          this.applyIfCurrent(run, () => {
            this.removalReport.set(outcome.state);
            this.removalReportReason.set(outcome.reason);
          });
          afterReport?.([]);
        },
      });
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

  /** The R15 guard in one place: an answer that belongs to a superseded run is dropped silently —
   *  no error state, nothing written. The run it belongs to is not on screen any more, and the one
   *  that is must not inherit its outcome. */
  private applyIfCurrent(run: ImportRunInfo, apply: () => void): void {
    if (this.run() !== run) {
      return;
    }
    apply();
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

/** Same policy as the delete's and the restore's report: waiting can fix a 429/5xx, not a
 *  401/403. */
/** Whether `channelName` is among the channels a report's answer says the backend resynced —
 *  case-insensitive, since the backend answers with normalized names. */
function includesChannel(channels: readonly string[], channelName: string): boolean {
  const normalized = channelName.toLowerCase();
  return channels.some((channel) => channel.toLowerCase() === normalized);
}

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

/** Source ids of every `done` row that added an emote — what `sync-imported` names. */
function importedKeys(run: ImportRunInfo): string[] {
  return (run.result?.items ?? [])
    .filter((item) => item.status === 'done' && item.transfer.action !== 'adoptSourceName')
    .map((item) => item.key);
}

/** Target ids of every replace row whose REMOVE 7TV confirmed, whatever the row ended as — what
 *  the removal report names. */
function removedTargetIds(run: ImportRunInfo): string[] {
  return (run.result?.items ?? []).flatMap((item) =>
    item.transfer.action === 'replace' && item.completedSteps >= 1
      ? [item.transfer.target.sevenTvEmoteId]
      : [],
  );
}

function outcomeCounts(result: ImportRunResult): { removedCount: number; unknownCount: number } {
  return {
    removedCount: result.items.filter(
      (item) => item.transfer.action === 'replace' && item.completedSteps >= 1,
    ).length,
    unknownCount: result.items.filter((item) => item.status === 'unknown').length,
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
