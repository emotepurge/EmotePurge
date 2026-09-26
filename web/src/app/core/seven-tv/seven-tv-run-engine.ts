import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Signal, computed, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import {
  EMPTY,
  Observable,
  Subscription,
  catchError,
  concatMap,
  defaultIfEmpty,
  defer,
  delayWhen,
  from,
  interval,
  map,
  of,
  retry,
  take,
  tap,
  throwError,
  timer,
} from 'rxjs';

import { SevenTvTokenService } from './seven-tv-token.service';

const SEVEN_TV_GQL_ENDPOINT = 'https://7tv.io/v4/gql';
// Starting pace only — the run re-paces itself from 7TV's own numbers the first time it is rate
// limited (see onRateLimited). Deliberately kept aggressive: 7TV's actual quota for the
// `emote_set_change` bucket lives in their database, not in their open-source tree, so the only way
// to learn it is to reach it once. The backoff below makes that safe.
export const RUN_DELAY_MS = 275;

/** 7TV's rate-limit bucket guarding every emote-set mutation, one ticket per call — REMOVE and ADD
 *  alike, which is why one engine paces both the delete and the restore run.
 *  Source: SevenTV/SevenTV, apps/api/src/http/v3/gql/mutations/emote_sets/mod.rs. */
const RATE_LIMIT_RESOURCE = 'emote_set_change';
const RATE_LIMIT_ERROR_CODE = 'RATE_LIMIT_EXCEEDED';
/** Added on top of the server-reported reset so a clock skew of a few hundred ms cannot make the
 *  first request after the pause land inside the still-closed window. */
const RATE_LIMIT_BUFFER_MS = 500;
/** Used when 7TV rejects without telling us when to come back (HTTP 429 from the *global* bucket —
 *  that one is enforced at the HTTP layer, and its headers are not CORS-exposed). Their windows are
 *  60s, so a full minute is the safe assumption. */
const RATE_LIMIT_FALLBACK_WAIT_MS = 60_000;
const MAX_RATE_LIMIT_RETRIES = 5;
/** Aim slightly *below* the measured quota. 7TV uses a fixed window, so pacing exactly at the limit
 *  puts every window's last request on the boundary. */
const RATE_LIMIT_PACING_MARGIN = 1.1;

export interface RunQueueEmote {
  /** Identity of this row within one run's queue — what `updateRow` matches on and what
   *  `RunResult.doneKeys` reports. Set by the calling service (spec #200, 7.2): a delete run keys
   *  by `sevenTvEmoteId` (one row per set-view cell, a #74 duplicate included — one `REMOVE` takes
   *  every entry), a restore run by `${sevenTvEmoteId}#${alias}` (one `ADD` per alias), an import
   *  run (K2) by `sevenTvEmoteId` too. Must be unique within a run; the engine never deduplicates
   *  (the calling service does). */
  key: string;
  /** Internal `Emote.Id`, carried through for the purge protocol only — nothing in the run or its
   *  bookkeeping reads it any more. Absent for an import run (the emote does not exist in our
   *  database yet) and for a set-view row that never had a local row at all (spec #200, 7.1). */
  emoteId?: string;
  sevenTvEmoteId: string;
  /** What the operation sends as the name: for a restore row the alias its `ADD` restores. */
  name: string;
  /** Delete runs only: every alias the cell sat under in the set — two for a #74 duplicate, whose
   *  one `REMOVE` takes both entries. Written into the purge protocol, which is the only place a
   *  later restore can learn them from. Absent means `[name]`. */
  aliases?: readonly string[];
}

/** `unknown` is the outcome of a step whose answer never came out of 7TV's GraphQL layer — no
 *  answer at all, an HTTP 5xx, or a `cancel()` that aborted the step's request — on an operation
 *  that asks for it (`transportLossIsUnknown`).
 *  7TV may or may not have applied that step; nothing in the run can tell. */
export type RunItemStatus = 'pending' | 'in-progress' | 'done' | 'failed' | 'cancelled' | 'unknown';

export interface RunQueueItem extends RunQueueEmote {
  status: RunItemStatus;
  errorMessage?: string;
  /** How many of this row's steps 7TV confirmed with a success answer, whatever the row's final
   *  status — a two-step row that ends `failed` or `unknown` with `completedSteps >= 1` had its first
   *  mutation applied. `0` until the first answer. A row a `beforeStep` hook skips at step >= 1 ends
   *  `cancelled` with this left at whatever it already reached — a named gap, not a loss (see
   *  `RunOperation.beforeStep`). */
  completedSteps: number;
  /** Index of the step the row ended on when it ended `failed` or `unknown` — `0` for a
   *  single-step row, never missing on those two statuses. For a row cancelled after a confirmed
   *  step it is the first step 7TV never confirmed; for a row cancelled while a request was in
   *  flight, the step of that request. `null` on every other status — including a row a
   *  `beforeStep` hook skipped, which is `cancelled` without ever having had a failed step of its
   *  own (`completedSteps` alone tells that story there). */
  failedStep: number | null;
}

/** What `abortOn` gets for a failed step — see the hook's own documentation for each field. */
export interface RunFailure {
  message: string;
  httpStatus: number | null;
  errorCode: string | null;
  gqlStatus: number | null;
}

/** What a `beforeStep` hook returns for one attempt of a step — see the hook's own documentation
 *  on `RunOperation` for what each outcome does. */
export type StepGate = { kind: 'proceed' } | { kind: 'skip'; errorMessage: string };

/** What a run does per emote. The engine owns pacing, retries, token handling and the queue; the
 *  operation owns only the mutations — REMOVE for the delete, ADD for the restore and the import.
 *
 *  A row can take more than one mutation (`stepCount`). The engine runs a row's steps strictly in
 *  order and treats each one like a whole row of a single-step run: the same pacing delay before
 *  the next step as before the next row, its own rate-limit backoff (a retry repeats only the step
 *  that was rejected), its own entry in the closing request count. A step that fails ends the row —
 *  no later step of that row is sent. `progress` keeps counting rows, not steps. */
export interface RunOperation {
  /** Goes into the closing console measurement: `[EmotePurge] 7TV <label> finished`. */
  readonly label: string;
  /** How many mutations this row takes. Omitted means one — the delete, the restore and the plain
   *  import. A value below one is treated as one. */
  stepCount?(emote: RunQueueEmote): number;
  /** The request for one step of one row; `step` counts from `0` and a single-step operation can
   *  ignore it. Called again for every rate-limit retry of that step. */
  buildRequest(
    setId: string,
    emote: RunQueueEmote,
    step: number,
  ): {
    query: string;
    variables: Record<string, unknown>;
  };
  /**
   * Asked before every attempt of this step's request — the first try and, because the engine
   * calls it again whenever `retry()` resubscribes, every retry after a rate-limit pause too.
   * Lets the calling service gate a step on an asynchronous precondition `buildRequest` cannot
   * check on its own (the undo run's per-REMOVE freshness read, spec #254 E19, is the reason this
   * exists).
   *
   * `{ kind: 'proceed' }` sends the request exactly as it would without a hook. `{ kind: 'skip',
   * errorMessage }` ends the row `cancelled` with that message and sends no request at all:
   * `completedSteps` is left exactly as it was — a multi-step row skipped past its first step
   * keeps whatever it already confirmed, a named gap rather than a loss (Plan #254 §T3) —,
   * `abortOn` is not consulted, and the run paces itself for the next row exactly as it does after
   * any other row.
   *
   * **Contract: exactly one value, then whatever happens after is ignored.** The engine takes only
   * the first emission (`take(1)`) and unsubscribes right after — a second, synchronous emission
   * never reaches it (no duplicate REMOVE), and a hook that never completes on its own does not
   * hang the row: one `next` is all the engine needs. An observable that *completes* without ever
   * emitting is treated as `skip` with a generic, engine-owned message, the same as a hook that
   * throws synchronously or whose observable errors — all three are fail-closed, logged via
   * `console.error` the same way a throwing `abortOn` hook is (below). The engine does **not** time
   * a slow hook out; an observable that neither emits, errors nor completes leaves the row waiting
   * forever, and giving it a deadline is the hook owner's job (`timeout()` upstream of returning
   * it), not this one's.
   *
   * Not asked again for the same attempt once its request is out: this hook decides whether to
   * *make* the request, `abortOn` decides whether to keep going after one that *failed*. A
   * `cancel()` while the hook is still pending ends the row exactly as a cancel during a request
   * in flight does — the hook's own answer, however it resolves, is simply never observed.
   *
   * Omitted means every attempt proceeds straight away, with no detour through this hook at all —
   * delete, restore and import, none of which set it, are unaffected byte-for-byte.
   */
  beforeStep?(setId: string, emote: RunQueueEmote, step: number): Observable<StepGate>;

  /**
   * `true` makes a step whose answer never came out of 7TV's GraphQL layer end its row `unknown`
   * instead of `failed`: no response at all (`httpStatus 0` — network loss, timeout) and every HTTP
   * `5xx`, `500` included, since such an answer says that something failed but not whether before
   * or after the write. Anything 7TV answered unambiguously keeps today's outcome: a GraphQL answer
   * (HTTP 200, with or without `errors`) is a success, a rate-limit backoff or `failed`, and any
   * HTTP `4xx` rejects the request before it is processed (`401`/`403` still clear the token, `429`
   * still backs off). An `unknown` row sends no further step, is not passed to `abortOn` (there is
   * no reason to weigh) and does not stop the run; `progress` counts it as finished, and it is not
   * among the done keys of the `RunResult`. A `cancel()` that aborts a step's request in flight
   * ends that row `unknown` too, for the same reason. Omitted means `false` — every existing run
   * keeps `failed`, and a cancelled request keeps today's `cancelled`.
   */
  readonly transportLossIsUnknown?: boolean;
  /**
   * Called once per step that ends its row `failed` — after the row's status is set on the queue,
   * before the run paces itself for the next row. The hook does not learn which step it was: whether
   * to abort depends on the reason, not on the position. Returning `true` aborts the run
   * synchronously: no further request is sent, every remaining `pending`/`in-progress` row becomes
   * `cancelled`, and `onComplete` fires exactly once, immediately — no `RUN_DELAY_MS` wait, not even
   * when the failed row was the run's last one.
   *
   * `message` is the raw 7TV GQL error text when 7TV rejected the mutation itself (untranslated —
   * matching on it is the caller's job), the already-translated text for an HTTP-layer failure
   * (`401`/`403`/`429`/network/generic — see `describeHttpError`), and the translated give-up text
   * once the rate-limit retries are exhausted. `httpStatus` is the HTTP status of a transport
   * failure — including Angular's `0` for a network error — and `null` for a GQL-level rejection or
   * a rate-limit give-up.
   *
   * `errorCode` is 7TV's structured `extensions.code` from the GQL error — v4's `LACKING_PRIVILEGES`
   * for a missing-permission mutation, for one, which is the reason this field exists: that rejection
   * arrives over HTTP 200, so there is no `httpStatus` to match on. It is `null` whenever there is
   * nothing to read a code from — a transport-layer failure (no GraphQL body at all), a GQL error
   * without an `extensions.code`, or the rate-limit give-up (synthesised locally after the retry
   * budget is spent, not read off a specific server error).
   *
   * `gqlStatus` is the HTTP-like `extensions.status` 7TV puts on a GraphQL rejection — `409` for a
   * name conflict on `updateEmoteAlias`, for one — and `null` for a transport failure, for the
   * rate-limit give-up and for a GraphQL error without a status. Match on it, never on the text.
   *
   * Not called for a successful step, for a rate-limited attempt that is still being retried (only
   * the retry's final outcome reaches this hook), for a row that is `cancelled`, or for a row that
   * ends `unknown`. A hook that throws is treated as `false` (the run continues) and the exception
   * is reported via `console.error` — a broken hook is a reason to log, not to leave the queue
   * half-finished.
   *
   * Delete and restore leave this unset, which reproduces today's behaviour exactly: every failure
   * is recorded and the run keeps going.
   */
  abortOn?(failure: RunFailure): boolean;
}

export interface RunResult {
  /** Queue keys (`RunQueueEmote.key`) of every 'done' row, in queue order — the one identity a
   *  finished run reports back (spec #200, E2). There is deliberately no Guid list beside it: a
   *  row without an `emoteId` would drop out of one, and every reader of such a list (report,
   *  retry, the panel's `deleted`) would then lose that row without a trace. */
  doneKeys: string[];
  items: RunQueueItem[];
  startedAt: number;
  finishedAt: number;
}

interface RunStepFailure {
  errorMessage: string;
  httpStatus: number | null;
  errorCode: string | null;
  gqlStatus: number | null;
}

/** The outcome of one step, after any rate-limit retries. `skip` is never seen by `retry` — it is
 *  an ordinary emitted value, not an error — so a `beforeStep` hook that skips one attempt does not
 *  prevent the engine from asking it again before the next. */
type RunOneResult =
  | { outcome: 'done' }
  | ({ outcome: 'failed' | 'unknown' } & RunStepFailure)
  | { outcome: 'skip'; errorMessage: string };

/** The rate-limit numbers 7TV mirrors into a rejected mutation's `extensions.headers`. All values
 *  arrive as strings; `reset` is in seconds. Any of them can be missing. */
interface RateLimitInfo {
  limit: number | null;
  remaining: number | null;
  reset: number | null;
  used: number | null;
}

/** Thrown internally so RxJS `retry` can drive the wait. Not an `Error` subclass on purpose — it is
 *  a control-flow signal, never surfaced to the user. */
class RateLimitHit {
  constructor(readonly info: RateLimitInfo) {}
}

/** Thrown internally when an `abortOn` hook asks to stop the run. Caught right after the
 *  `concatMap` (see `start`) so it never reaches the outer subscription's `error:` callback — that
 *  path is the pre-existing one that leaves non-terminal rows behind (see docs/DECISIONS.md). Not an
 *  `Error` subclass, same reasoning as `RateLimitHit`: a control-flow signal, never surfaced. */
class AbortRequested {}

interface SevenTvGqlError {
  message?: string;
  extensions?: {
    code?: string;
    status?: number;
    headers?: Record<string, string>;
  };
}

function parseHeaderNumber(headers: Record<string, string> | undefined, suffix: string) {
  const raw = headers?.[`x-ratelimit-${RATE_LIMIT_RESOURCE}-${suffix}`];
  if (raw === undefined) {
    return null;
  }
  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) ? parsed : null;
}

function isRateLimitError(error: SevenTvGqlError): boolean {
  return error.extensions?.code === RATE_LIMIT_ERROR_CODE || error.extensions?.status === 429;
}

function readRateLimitInfo(error: SevenTvGqlError): RateLimitInfo {
  const headers = error.extensions?.headers;
  return {
    limit: parseHeaderNumber(headers, 'limit'),
    remaining: parseHeaderNumber(headers, 'remaining'),
    reset: parseHeaderNumber(headers, 'reset'),
    used: parseHeaderNumber(headers, 'used'),
  };
}

/**
 * The run mechanics behind both 7TV mass mutations (delete since 2026-07-26, restore since A6):
 * sequential queue, adaptive pacing learned from 7TV's rate-limit answers, rate-limit backoff with
 * countdown, token handling. Extracted verbatim from SevenTvDeleteService — the ~350 lines here are
 * identical for REMOVE and ADD because both draw tickets from the same `emote_set_change` bucket.
 *
 * Deliberately a plain class, not a root singleton: the delete and the restore service each hold
 * their *own* instance (`new SevenTvRunEngine(inject(...), ...)`), so `isRunning` stays unambiguous
 * per service. Error message keys stay under `massDelete.errors.*` — shared, not duplicated.
 */
export class SevenTvRunEngine {
  private runSubscription: Subscription | null = null;
  private countdownSubscription: Subscription | null = null;
  private operation: RunOperation | null = null;
  /** The row and step whose request is out and unanswered right now, `null` otherwise. Only
   *  `cancel()` reads it: unsubscribing aborts that request, so its answer never arrives. */
  private inFlight: { key: string; step: number } | null = null;
  private onComplete: ((result: RunResult) => void) | null = null;

  /** Pacing state for the running job. `currentDelayMs` is the only one the queue reads; the rest
   *  exist to derive it from 7TV's answer on the first rejection. */
  private currentDelayMs = RUN_DELAY_MS;
  private windowStartedAt = 0;
  private requestsInWindow = 0;
  private roundTripTotalMs = 0;
  private roundTripCount = 0;
  private pacingAdapted = false;
  private runStartedAt = 0;
  private rateLimitHits = 0;
  /** Timestamp of every attempt, kept for the closing measurement. 7TV's limiter is a fixed 60s
   *  window, so the number that matters is the peak within any 60s span — not the run's average. */
  private requestTimestamps: number[] = [];

  readonly queue = signal<RunQueueItem[]>([]);
  readonly isRunning = signal(false);

  /** Seconds left on a rate-limit pause, or null when not waiting. Without this the progress bar
   *  simply stops moving for up to a minute, which is indistinguishable from a hang. */
  readonly rateLimitPauseSeconds = signal<number | null>(null);

  readonly progress: Signal<{ finished: number; total: number }> = computed(() => {
    const items = this.queue();
    const finished = items.filter(
      (item) => item.status === 'done' || item.status === 'failed' || item.status === 'unknown',
    ).length;
    return { finished, total: items.length };
  });

  constructor(
    private readonly http: HttpClient,
    private readonly tokenService: SevenTvTokenService,
    private readonly translocoService: TranslocoService,
  ) {}

  /** Returns false when the run did not start (already running, empty list, no token) — the owning
   *  service must not set up its own per-run state in that case. */
  start(
    setId: string,
    emotes: RunQueueEmote[],
    operation: RunOperation,
    onComplete: (result: RunResult) => void,
  ): boolean {
    if (this.isRunning() || emotes.length === 0) {
      return false;
    }

    const token = this.tokenService.getToken();
    if (!token) {
      return false;
    }

    this.operation = operation;
    this.onComplete = onComplete;
    this.queue.set(
      emotes.map((emote) => ({
        ...emote,
        status: 'pending' as RunItemStatus,
        completedSteps: 0,
        failedStep: null,
      })),
    );
    this.isRunning.set(true);
    this.resetPacing();

    this.runSubscription = from(emotes)
      .pipe(
        concatMap((emote) => {
          this.updateRow(emote.key, { status: 'in-progress' });
          const stepCount = operation.stepCount?.(emote) ?? 1;
          return this.runRowFrom(0, stepCount, setId, emote, operation, token).pipe(
            // delayWhen, not delay: the pace is re-derived mid-run once 7TV tells us its real quota,
            // and a plain delay() would have captured the starting value forever. An abort throws
            // before this runs, so the aborting row never waits out the pacing delay either.
            delayWhen(() => timer(this.currentDelayMs)),
          );
        }),
        catchError((error) => {
          if (!(error instanceof AbortRequested)) {
            return throwError(() => error);
          }
          // Turn the abort into a graceful completion *here*, one level above the outer
          // subscription: stop any rate-limit countdown, cancel the rest of the queue ourselves,
          // then let `complete:` — not `error:` — call `finish()` exactly once, the same way every
          // normal run ends.
          this.endPause();
          this.cancelRemainingRows();
          return EMPTY;
        }),
      )
      .subscribe({
        complete: () => this.finish(),
        error: () => this.finish(),
      });
    return true;
  }

  /** Cancel = unsubscribing the RxJS chain — idiomatic and simpler than hand-rolled cooperative
   *  cancellation. It does not wait for a request in flight: Angular's HttpClient aborts it on
   *  teardown, so its answer never arrives. Terminal rows keep their outcome; what happens to the
   *  others is `cancelRemainingRows`' rule. */
  cancel(): void {
    if (!this.isRunning()) {
      return;
    }
    this.runSubscription?.unsubscribe();
    this.runSubscription = null;
    // Unsubscribing already kills a pending backoff timer; this only clears its countdown display.
    this.endPause();
    this.cancelRemainingRows();
    this.finish();
  }

  /** Clears the queue after the owning service has torn down its own per-run state. Never called
   *  while a run is in flight: `finish()` builds the run's result from this queue, and a run that is
   *  merely no longer shown still has to report every row 7TV confirmed (#256). */
  reset(): void {
    this.queue.set([]);
  }

  /** Puts a finished run's rows back on the queue — the owning service showing a run again whose
   *  queue was already cleared (#256: a detached run whose report did not succeed). Refused while
   *  a run is in flight, whose live queue must not be overwritten. */
  showFinishedRows(items: RunQueueItem[]): boolean {
    if (this.isRunning()) {
      return false;
    }
    this.queue.set(items);
    return true;
  }

  /** Runs one row's steps from `step` on, strictly in order, and settles the row on the queue. Emits
   *  once when the row is terminal; throws `AbortRequested` (caught in `start`) when `abortOn` says
   *  to stop. Between two steps of the same row it waits the same pacing delay as between rows. */
  private runRowFrom(
    step: number,
    stepCount: number,
    setId: string,
    emote: RunQueueEmote,
    operation: RunOperation,
    token: string,
  ): Observable<void> {
    return this.runWithBackoff(setId, emote, step, operation, token).pipe(
      concatMap((result) => {
        if (result.outcome === 'done') {
          const completedSteps = step + 1;
          if (completedSteps >= stepCount) {
            this.updateRow(emote.key, { status: 'done', errorMessage: undefined, completedSteps });
            return of(undefined);
          }
          this.updateRow(emote.key, { completedSteps });
          return timer(this.currentDelayMs).pipe(
            concatMap(() =>
              this.runRowFrom(completedSteps, stepCount, setId, emote, operation, token),
            ),
          );
        }
        if (result.outcome === 'skip') {
          // The beforeStep hook declined this attempt: no request went out, so nothing 7TV
          // confirmed changes — completedSteps and failedStep are left exactly as they were. A
          // multi-step row skipped past its first step keeps whatever it already confirmed, a
          // named gap rather than a loss (Plan #254 §T3). abortOn is never consulted here.
          this.updateRow(emote.key, { status: 'cancelled', errorMessage: result.errorMessage });
          return of(undefined);
        }
        // A step that fails or stays unknown ends the row: a later step would build on a state 7TV
        // did not confirm (an ADD onto a name the REMOVE may not have freed is a 409 waiting).
        this.updateRow(emote.key, {
          status: result.outcome,
          errorMessage: result.errorMessage,
          failedStep: step,
        });
        if (result.outcome === 'failed') {
          // Throws when the hook asks to stop — caught in `start`, never by the plain `error:`
          // callback there. An `unknown` row has no reason to weigh, so the hook never sees it.
          this.evaluateAbort(operation, result);
        }
        return of(undefined);
      }),
    );
  }

  /** One step of one row, including waiting out any rate limit 7TV imposes. A rate-limited attempt
   *  is *not* a failure: the step is retried after the server-stated reset, so a large run finishes
   *  instead of burning through its queue against a closed window. */
  private runWithBackoff(
    setId: string,
    emote: RunQueueEmote,
    step: number,
    operation: RunOperation,
    token: string,
  ): Observable<RunOneResult> {
    return this.runGatedOne(setId, emote, step, operation, token).pipe(
      retry({
        count: MAX_RATE_LIMIT_RETRIES,
        delay: (error) => {
          if (!(error instanceof RateLimitHit)) {
            return throwError(() => error);
          }
          const waitMs = this.onRateLimited(error.info);
          return timer(waitMs).pipe(tap(() => this.endPause()));
        },
      }),
      catchError(() =>
        // Only a RateLimitHit can get here — runGatedOne turns everything else into a result
        // value. httpStatus, errorCode and gqlStatus are all null: this is a give-up after
        // retries, synthesised locally, not a single server response to read any of them off. 7TV
        // did answer every attempt, so it is `failed`, never `unknown`.
        of<RunOneResult>({
          outcome: 'failed',
          errorMessage: this.translocoService.translate('massDelete.errors.rateLimitedGaveUp'),
          httpStatus: null,
          errorCode: null,
          gqlStatus: null,
        }),
      ),
    );
  }

  /** Asks the operation's `beforeStep` hook, if any, for this attempt of the step — `retry()`
   *  resubscribes to whatever this returns on every rate-limit retry, so a hook is asked again
   *  before each one. `skip`, a throwing hook, an erroring observable, or one that completes empty
   *  all short-circuit straight to a `skip` result without ever calling `runOne` — no request goes
   *  out. `take(1)` enforces the hook's contract at the engine's end: only the first emission is
   *  observed (a second, synchronous one is dropped, so a double emission cannot send the request
   *  twice), and unsubscribing right after means a hook that never completes on its own does not
   *  keep this chain alive. Without a hook this *is* `runOne`, with no detour, so an operation that
   *  never sets `beforeStep` runs exactly as it did before this method existed. */
  private runGatedOne(
    setId: string,
    emote: RunQueueEmote,
    step: number,
    operation: RunOperation,
    token: string,
  ): Observable<RunOneResult> {
    if (!operation.beforeStep) {
      return this.runOne(setId, emote, step, operation, token);
    }
    return defer(() => {
      let gate$: Observable<StepGate>;
      try {
        // Called as operation.beforeStep(...), like buildRequest and abortOn, so a hook that reads
        // its own `this` off the operation object sees the same one they do.
        gate$ = operation.beforeStep!(setId, emote, step);
      } catch (error) {
        console.error('[EmotePurge] 7TV run beforeStep hook threw — skipping the step', error);
        return of<RunOneResult>({ outcome: 'skip', errorMessage: this.beforeStepFailedMessage() });
      }
      return gate$.pipe(
        take(1),
        // An observable that completes without ever emitting is exactly as unusable as one that
        // errors — both fail closed rather than send a request blind.
        defaultIfEmpty<StepGate, StepGate>({
          kind: 'skip',
          errorMessage: this.beforeStepFailedMessage(),
        }),
        catchError((error) => {
          console.error('[EmotePurge] 7TV run beforeStep hook errored — skipping the step', error);
          return of<StepGate>({ kind: 'skip', errorMessage: this.beforeStepFailedMessage() });
        }),
        concatMap((gate) =>
          gate.kind === 'skip'
            ? of<RunOneResult>({ outcome: 'skip', errorMessage: gate.errorMessage })
            : this.runOne(setId, emote, step, operation, token),
        ),
      );
    });
  }

  /** Fail-closed fallback text for a `beforeStep` hook that throws, errors, or completes without
   *  ever emitting — a shipped hook is written not to do any of those, so this is not expected to
   *  reach a user in practice; it exists only so a broken hook still skips the step instead of
   *  sending a request blind. Translated like the engine's other own texts (`rateLimitedGaveUp`,
   *  `cancelledMidRow`), not read off the hook, which never gets to say anything in this case. */
  private beforeStepFailedMessage(): string {
    return this.translocoService.translate('massDelete.errors.beforeStepFailed');
  }

  private runOne(
    setId: string,
    emote: RunQueueEmote,
    step: number,
    operation: RunOperation,
    token: string,
  ): Observable<RunOneResult> {
    // defer, so every retry re-runs the request *and* re-stamps its own timing.
    return defer(() => {
      const startedAt = Date.now();
      this.requestsInWindow += 1;
      this.requestTimestamps.push(startedAt);
      this.inFlight = { key: emote.key, step };

      return this.http
        .post<{ errors?: SevenTvGqlError[] }>(
          SEVEN_TV_GQL_ENDPOINT,
          operation.buildRequest(setId, emote, step),
          { headers: { Authorization: `Bearer ${token}` } },
        )
        .pipe(
          // Cleared on any answer, success or error — but not on teardown, which is exactly the
          // case `cancel()` needs to recognise.
          tap({
            next: () => (this.inFlight = null),
            error: () => (this.inFlight = null),
          }),
          tap(() => this.recordRoundTrip(startedAt)),
          map((response): RunOneResult => {
            const gqlError = response?.errors?.[0];
            if (!gqlError) {
              return { outcome: 'done' };
            }
            // 7TV answers a rate-limited mutation with HTTP 200 and the rejection inside `errors`
            // (async-graphql never touches the status code), so this is the only place it surfaces.
            if (isRateLimitError(gqlError)) {
              throw new RateLimitHit(readRateLimitInfo(gqlError));
            }
            // httpStatus is null: 7TV rejected the mutation itself over HTTP 200, there is no
            // transport status to report. errorCode carries extensions.code verbatim — v4's
            // structured rejection reason (e.g. LACKING_PRIVILEGES) — or null when the error has
            // none; gqlStatus likewise extensions.status (e.g. 409 for a name conflict). A GraphQL
            // answer is never `unknown`: 7TV processed the request and said how it went.
            return {
              outcome: 'failed',
              errorMessage: gqlError.message ?? '',
              httpStatus: null,
              errorCode: gqlError.extensions?.code ?? null,
              gqlStatus: gqlError.extensions?.status ?? null,
            };
          }),
          catchError((error) => {
            if (error instanceof RateLimitHit) {
              return throwError(() => error);
            }
            const httpError = error as HttpErrorResponse;
            // The *global* bucket is enforced at the HTTP layer and does yield a real 429. Its
            // headers are not CORS-exposed, so we back off blind rather than give up.
            if (httpError.status === 429) {
              return throwError(
                () => new RateLimitHit({ limit: null, remaining: null, reset: null, used: null }),
              );
            }
            // httpStatus carries Angular's real status here, including 0 for a network error —
            // describeHttpError has already consumed it for the message, this just passes it along.
            // errorCode and gqlStatus are null: a transport failure never reaches 7TV's GraphQL
            // layer, so there is no extensions to read.
            //
            // A 4xx is an HTTP-layer rejection before the mutation ran — unambiguous, `failed`.
            // Everything else (no answer at all, any 5xx, a body that is no GraphQL answer) leaves
            // open whether the mutation was applied; an operation that asks for it gets `unknown`.
            const rejectedBeforeProcessing = httpError.status >= 400 && httpError.status < 500;
            return of<RunOneResult>({
              outcome:
                operation.transportLossIsUnknown && !rejectedBeforeProcessing
                  ? 'unknown'
                  : 'failed',
              errorMessage: this.describeHttpError(httpError),
              httpStatus: httpError.status,
              errorCode: null,
              gqlStatus: null,
            });
          }),
        );
    });
  }

  /** Called on every rejection. Logs what 7TV reported (this is how we learn the real quota — the
   *  numbers exist nowhere else, see docs/DECISIONS.md), re-paces the run once, and returns how long
   *  to wait. */
  private onRateLimited(info: RateLimitInfo): number {
    const waitMs =
      info.reset !== null ? info.reset * 1000 + RATE_LIMIT_BUFFER_MS : RATE_LIMIT_FALLBACK_WAIT_MS;
    const attemptsThisWindow = this.requestsInWindow;
    this.rateLimitHits += 1;

    // Re-pace only from a rejection that actually carries numbers. A blind HTTP 429 (global bucket,
    // headers not CORS-exposed) would otherwise "teach" us a quota of one request per minute.
    const informative = info.reset !== null && (info.limit !== null || attemptsThisWindow > 1);
    if (!this.pacingAdapted && informative) {
      this.adaptPacing(info, attemptsThisWindow);
    }

    console.info('[EmotePurge] 7TV rate limit reached', {
      resource: RATE_LIMIT_RESOURCE,
      reportedLimit: info.limit,
      reportedUsed: info.used,
      resetSeconds: info.reset,
      requestsSentThisWindow: attemptsThisWindow,
      msSinceWindowStart: Date.now() - this.windowStartedAt,
      averageRoundTripMs: Math.round(this.averageRoundTripMs()),
      newDelayMs: this.currentDelayMs,
      waitMs,
    });

    this.startPause(waitMs);
    return waitMs;
  }

  /** Derives a sustainable delay from the one data point 7TV gives us. `limit` comes from the
   *  header when present; otherwise the number of requests we got through this window *is* the
   *  quota. The window length is likewise derived: elapsed-so-far plus the remaining reset. */
  private adaptPacing(info: RateLimitInfo, attemptsThisWindow: number): void {
    const limit = info.limit ?? Math.max(1, attemptsThisWindow - 1);
    const elapsedMs = Date.now() - this.windowStartedAt;
    const windowMs =
      info.reset !== null ? elapsedMs + info.reset * 1000 : RATE_LIMIT_FALLBACK_WAIT_MS;

    // Subtract the observed round-trip: our delay sits *between* requests, so the achieved rate is
    // driven by delay + latency, not by the delay alone.
    const targetCycleMs = (windowMs / limit) * RATE_LIMIT_PACING_MARGIN;
    this.currentDelayMs = Math.max(0, Math.round(targetCycleMs - this.averageRoundTripMs()));
    this.pacingAdapted = true;
  }

  private recordRoundTrip(startedAt: number): void {
    this.roundTripTotalMs += Date.now() - startedAt;
    this.roundTripCount += 1;
  }

  private averageRoundTripMs(): number {
    return this.roundTripCount === 0 ? 0 : this.roundTripTotalMs / this.roundTripCount;
  }

  private resetPacing(): void {
    this.currentDelayMs = RUN_DELAY_MS;
    this.windowStartedAt = Date.now();
    this.runStartedAt = this.windowStartedAt;
    this.requestsInWindow = 0;
    this.roundTripTotalMs = 0;
    this.roundTripCount = 0;
    this.pacingAdapted = false;
    this.rateLimitHits = 0;
    this.requestTimestamps = [];
    this.inFlight = null;
    this.endPause();
  }

  /** How many requests we packed into the busiest 60-second span of the run — the figure that is
   *  directly comparable to a `requests / interval_seconds` quota. A run that ends without a single
   *  rate-limit hit proves 7TV tolerates at least this rate for the acting user's role, which is the
   *  only empirical handle we have on a quota that is not published anywhere. */
  private peakRequestsPer60s(): number {
    let peak = 0;
    let windowStart = 0;
    for (let i = 0; i < this.requestTimestamps.length; i++) {
      while (this.requestTimestamps[i] - this.requestTimestamps[windowStart] >= 60_000) {
        windowStart++;
      }
      peak = Math.max(peak, i - windowStart + 1);
    }
    return peak;
  }

  private logRunSummary(): void {
    if (this.requestTimestamps.length === 0) {
      return;
    }
    const statuses = this.queue().map((item) => item.status);
    console.info(`[EmotePurge] 7TV ${this.operation?.label ?? 'run'} finished`, {
      requested: statuses.length,
      succeeded: statuses.filter((status) => status === 'done').length,
      failed: statuses.filter((status) => status === 'failed').length,
      unknown: statuses.filter((status) => status === 'unknown').length,
      cancelled: statuses.filter((status) => status === 'cancelled').length,
      requestsSent: this.requestTimestamps.length,
      durationMs: Date.now() - this.runStartedAt,
      peakRequestsPer60s: this.peakRequestsPer60s(),
      averageRoundTripMs: Math.round(this.averageRoundTripMs()),
      rateLimitHits: this.rateLimitHits,
      delayMs: this.currentDelayMs,
    });
  }

  private startPause(waitMs: number): void {
    this.countdownSubscription?.unsubscribe();
    this.rateLimitPauseSeconds.set(Math.ceil(waitMs / 1000));
    this.countdownSubscription = interval(1000).subscribe(() => {
      const left = this.rateLimitPauseSeconds();
      if (left !== null) {
        this.rateLimitPauseSeconds.set(Math.max(0, left - 1));
      }
    });
  }

  /** Ends the pause and opens a fresh accounting window — 7TV's limiter is a fixed window, so once
   *  the reset has passed the previous window's usage is gone. */
  private endPause(): void {
    this.countdownSubscription?.unsubscribe();
    this.countdownSubscription = null;
    this.rateLimitPauseSeconds.set(null);
    this.windowStartedAt = Date.now();
    this.requestsInWindow = 0;
  }

  private describeHttpError(error: HttpErrorResponse): string {
    if (error.status === 401 || error.status === 403) {
      // Invalid/expired token — drop it so the UI falls back to the token-input prompt instead
      // of silently reusing the same bad token on the next attempt.
      this.tokenService.clearToken();
      return this.translocoService.translate('massDelete.errors.tokenInvalid');
    }
    if (error.status === 429) {
      return this.translocoService.translate('massDelete.errors.rateLimited');
    }
    if (error.status === 0) {
      return this.translocoService.translate('massDelete.errors.networkError');
    }
    return this.translocoService.translate('massDelete.errors.genericStatus', {
      status: error.status,
    });
  }

  private updateRow(key: string, patch: Partial<RunQueueItem>): void {
    this.queue.update((items) =>
      items.map((item) => (item.key === key ? { ...item, ...patch } : item)),
    );
  }

  /** Runs the operation's `abortOn` hook, if any, for a row that just became `failed`, and throws
   *  `AbortRequested` when it says to stop — caught in `start`. A throwing hook is logged and
   *  treated as `false`: a broken hook must not corrupt the run, only be visible. */
  private evaluateAbort(operation: RunOperation, result: RunStepFailure): void {
    if (!operation.abortOn) {
      return;
    }
    let shouldAbort: boolean;
    try {
      shouldAbort = operation.abortOn({
        message: result.errorMessage,
        httpStatus: result.httpStatus,
        errorCode: result.errorCode,
        gqlStatus: result.gqlStatus,
      });
    } catch (error) {
      console.error('[EmotePurge] 7TV run abortOn hook threw — continuing the run', error);
      shouldAbort = false;
    }
    if (shouldAbort) {
      throw new AbortRequested();
    }
  }

  /** Shared by `cancel()` and the `abortOn` detour in `start()`. Terminal rows keep their outcome;
   *  every row still `pending` or `in-progress` becomes `cancelled`, with two exceptions:
   *  - a row whose request was in flight, on an operation with `transportLossIsUnknown`, becomes
   *    `unknown` at that step — the request is aborted, its answer never arrives, and 7TV may have
   *    applied it all the same. No `abortOn`, and the step does not count as completed. Without the
   *    flag such a row falls through to the rules below, as it always did.
   *  - a row stopped after 7TV confirmed at least one of its steps becomes `failed` at the first
   *    unconfirmed step: 7TV already changed, and `cancelled` says in the protocol that nothing
   *    happened.
   *  On the abort path neither exception can arise: the aborting row is already `failed`, and its
   *  answer has cleared `inFlight`.
   *
   *  That second exception is about *this* method's own `cancelled`, reached via `cancel()` or an
   *  `abortOn` abort — it does not describe a `beforeStep` skip. A skip is applied directly in
   *  `runRowFrom`, never through here, and its `cancelled` can carry `completedSteps > 0`: there it
   *  says the step this attempt gated never ran, not that nothing in the row happened (see
   *  `RunOperation.beforeStep`). */
  private cancelRemainingRows(): void {
    const inFlight = this.operation?.transportLossIsUnknown ? this.inFlight : null;
    this.inFlight = null;
    this.queue.update((items) =>
      items.map((item) => {
        if (item.status !== 'pending' && item.status !== 'in-progress') {
          return item;
        }
        if (inFlight !== null && item.key === inFlight.key) {
          return { ...item, status: 'unknown', failedStep: inFlight.step };
        }
        if (item.completedSteps > 0) {
          return {
            ...item,
            status: 'failed',
            failedStep: item.completedSteps,
            errorMessage: this.translocoService.translate('massDelete.errors.cancelledMidRow'),
          };
        }
        return { ...item, status: 'cancelled' };
      }),
    );
  }

  private finish(): void {
    this.isRunning.set(false);
    this.runSubscription = null;
    this.endPause();
    this.logRunSummary();

    const items = this.queue();
    const doneItems = items.filter((item) => item.status === 'done');
    const result: RunResult = {
      doneKeys: doneItems.map((item) => item.key),
      items,
      startedAt: this.runStartedAt,
      finishedAt: Date.now(),
    };
    this.onComplete?.(result);
  }
}
