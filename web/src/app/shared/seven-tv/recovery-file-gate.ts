import { DestroyRef, Signal, WritableSignal, computed, linkedSignal, signal } from '@angular/core';
import { Observable, Subscription, timeout } from 'rxjs';

/** Same budget as the other live reads of a set before a destructive step (`mass-delete-panel.ts`,
 *  `seven-tv-import.service.ts`). */
export const LIVE_READ_TIMEOUT_MS = 20_000;

/**
 * The executor's state for a plan that removes target entries — three states, one button: `idle`
 * offers to save the recovery file, `verifying` reads the target set live, and `saved` means the
 * file was handed to the browser and the button now starts the run with `stampedPlan`. `verifying`
 * and `saved` hold the plan they were entered for; once the current plan is a different one, the
 * state reads as `idle` again — the file on disk describes the old plan.
 */
export type RecoveryFileGateState<TPlan> =
  | { kind: 'idle' }
  | { kind: 'verifying'; plan: TPlan }
  | { kind: 'saved'; plan: TPlan; stampedPlan: TPlan };

/** Why the last live read did not release the run. The drifts are handed over as they came from
 *  `verify` — naming them (a row's source name, say) is the consumer's business. */
export type RecoveryFileNotice<TDrift> =
  /** At least one target the plan relies on changed. */
  | { kind: 'drifted'; drifted: readonly TDrift[] }
  /** The read failed, timed out or stopped short (or a consumer callback threw on it) — nothing it
   *  says can vouch for a removal. */
  | { kind: 'readFailed' }
  /** The browser refused the download, so no recovery file exists. */
  | { kind: 'saveFailed' };

/** What `verify` makes of one read: `available: false` when the read cannot vouch for the plan at
 *  all (an incomplete set, say), otherwise the targets that no longer match — none means clean. */
export type RecoveryFileVerification<TDrift> =
  { available: true; drifted: readonly TDrift[] } | { available: false };

/** What the saver gets for the one file it writes. */
export interface RecoveryFileSave<TTarget, TPlan, TRead> {
  target: TTarget;
  /** The plan as `stamp` made it from the read — the one the run starts with. */
  stampedPlan: TPlan;
  read: TRead;
  /** When the read that vouched for the plan was verified (epoch ms). */
  verifiedAt: number;
}

/** Everything the gate needs from its consumer, as plain signals and functions — nothing injected. */
export interface RecoveryFileGateDeps<TPlan, TDrift, TTarget, TRead> {
  /** The plan as it is now. A state entered for another plan object reads as `idle`. */
  plan: Signal<TPlan | null>;
  /** Any change of this signal clears the notice — the consumer's "the target was reloaded". */
  noticeResetSource: Signal<unknown>;
  /** Cancels the read in flight and silences every later answer. */
  destroyRef: DestroyRef;
  /** The live read of the target — what `reload` asks for, and `verifyAndSave` too unless
   *  `verifyRead` is given. Bounded by {@link LIVE_READ_TIMEOUT_MS}. */
  read: (target: TTarget) => Observable<TRead>;
  /** The read `verifyAndSave` verifies and saves from, when it is not a fresh `read` — the undo's
   *  held read (`of(lastRead)`), which only "Ziel neu laden" replaces. Same rules as `read`. */
  verifyRead?: (target: TTarget) => Observable<TRead>;
  /** Whether a read covers the whole target — `reload` only counts a complete one as a success.
   *  (`verifyAndSave` asks `verify` instead, which answers `available: false` for such a read.) */
  isComplete: (read: TRead) => boolean;
  verify: (read: TRead, plan: TPlan) => RecoveryFileVerification<TDrift>;
  /** The plan the run starts with, carrying what the read found (aliases, default names, …). */
  stamp: (plan: TPlan, read: TRead) => TPlan;
  /** Writes the recovery file. Throws when the browser refuses the download. */
  save: (file: RecoveryFileSave<TTarget, TPlan, TRead>) => void;
  /** Called with the drifts of a read before the `drifted` notice is set — the consumer lays the
   *  live state over its rows and takes back the decisions that no longer hold. */
  onDrift?: (drifted: readonly TDrift[]) => void;
  /** Sees every successful live answer exactly once: from `verifyAndSave` while its plan is still
   *  current, before it is verified (whichever branch follows), and from `reload` when complete. */
  onLiveRead?: (read: TRead) => void;
  /** The read the consumer already holds when the gate is built (the undo flow's own read) — the
   *  first value of `lastRead`. */
  initialRead?: TRead | null;
}

/**
 * The state machine in front of a run that removes target entries (#256 point 5): `idle →
 * verifying → saved`, with the recovery file as the gate between "Rückweg sichern" and "Starten".
 * Extracted from `ImportConfirmDialog`; the undo confirmation of #254 runs on the same machine with
 * its own plan, read and drift types.
 *
 * `verifyAndSave(target, plan)` reads the target live, hands the answer to `onLiveRead`, verifies the
 * plan against it and, when every target still matches, stamps the plan and saves the file. Its
 * rules:
 * - **A different current plan ⇒ `idle`.** `state` derives from the raw state and the current plan,
 *   so a plan change during `verifying` or after `saved` reads as `idle` at once.
 * - **Only the newest read answers.** Starting a read (either method) cancels the one before it,
 *   and each answer first checks that it still is the current read. An answer for a plan that is
 *   gone by then settles to `idle` without calling anything and without a notice.
 * - **Nothing fails open.** A read error, a timeout, an unavailable verification and a refused
 *   download each end `idle` with their notice; a drift ends `idle` after `onDrift`; a consumer
 *   callback that throws on an answer ends `idle` with `readFailed` (and is logged).
 * - **Destroying the consumer** cancels the read in flight; no callback runs after that.
 *
 * `reload(target)` is the read without a file, for a consumer that re-reads the target itself —
 * same newest-read, timeout, teardown and `readFailed` rules, but it never enters `verifying` or
 * `saved` and never saves: it sets the state to `idle`, clears `lastRead` while it runs, and on a
 * complete answer publishes it on `lastRead` (and to `onLiveRead`). A failed or incomplete reload
 * leaves `lastRead` empty with `readFailed`.
 *
 * **Intended use in the undo dialog (#254 T5, K3):** pass the flow's read as `initialRead` and
 * derive the classification — and with it `plan` — from `lastRead()`; "Ziel neu laden" calls
 * `reload(target)`, so the dialog shows nothing releasable while `lastRead()` is `null`; "Rückweg
 * sichern" calls `verifyAndSave(target, plan)`, with `verifyRead` handing back the held read (no second
 * request) and `verify` returning `{ available: true, drifted: [] }` when the
 * undo has no drift notion. `verifyAndSave`'s own answers never touch `lastRead`, so a plan derived
 * from it is not invalidated by the very read that saves the file.
 *
 * Plain class with signals, not injectable: the consumer constructs it with its own dependencies.
 */
export class RecoveryFileGate<TPlan, TDrift, TTarget, TRead> {
  /** The state as it holds for the current plan. */
  readonly state: Signal<RecoveryFileGateState<TPlan>>;
  readonly isVerifying: Signal<boolean>;
  /** Cleared by the next read of either kind and by every change of `noticeResetSource`. */
  readonly notice: Signal<RecoveryFileNotice<TDrift> | null>;
  /** True while a `reload` read is in flight. */
  readonly isReloading: Signal<boolean>;
  /** The last complete read `reload` delivered, else `initialRead`; `null` while a reload runs,
   *  after one failed, and after one was cancelled by a newer read before it answered. */
  readonly lastRead: Signal<TRead | null>;

  private readonly deps: RecoveryFileGateDeps<TPlan, TDrift, TTarget, TRead>;
  private readonly rawState = signal<RecoveryFileGateState<TPlan>>({ kind: 'idle' });
  private readonly noticeSlot: WritableSignal<RecoveryFileNotice<TDrift> | null>;
  private readonly reloading = signal(false);
  private readonly lastReadSlot: WritableSignal<TRead | null>;

  /** The live read in flight, if any — cancelled when a newer one starts. */
  private inFlight: Subscription | null = null;
  /** Identity of the newest read; an answer of any older one is dropped. */
  private currentRead: object | null = null;
  private destroyed = false;

  constructor(deps: RecoveryFileGateDeps<TPlan, TDrift, TTarget, TRead>) {
    this.deps = deps;
    this.state = computed(() => {
      const state = this.rawState();
      return state.kind !== 'idle' && state.plan !== deps.plan() ? { kind: 'idle' } : state;
    });
    this.isVerifying = computed(() => this.state().kind === 'verifying');
    this.noticeSlot = linkedSignal<unknown, RecoveryFileNotice<TDrift> | null>({
      source: deps.noticeResetSource,
      computation: () => null,
    });
    this.notice = this.noticeSlot.asReadonly();
    this.isReloading = this.reloading.asReadonly();
    this.lastReadSlot = signal<TRead | null>(deps.initialRead ?? null);
    this.lastRead = this.lastReadSlot.asReadonly();
    deps.destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.currentRead = null;
      this.inFlight?.unsubscribe();
      this.inFlight = null;
    });
  }

  /** Reads `target` live for `plan`; see the class doc for what each answer does. */
  verifyAndSave(target: TTarget, plan: TPlan): void {
    const verifying: RecoveryFileGateState<TPlan> = { kind: 'verifying', plan };
    this.startRead(
      target,
      verifying,
      false,
      () => this.rawState() === verifying,
      (read) => this.onVerifyRead(target, plan, read),
      // A failed read vouches for nothing, so it releases nothing — and an error about a plan that
      // is gone by now names nothing either.
      () => this.settle(this.deps.plan() === plan ? { kind: 'readFailed' } : null),
    );
  }

  /** Re-reads `target` without saving anything; see the class doc. */
  reload(target: TTarget): void {
    this.startRead(
      target,
      { kind: 'idle' },
      true,
      () => true,
      (read) => this.onReloadRead(read),
      () => this.settleReload({ kind: 'readFailed' }),
    );
  }

  /** Starts the one current read: cancels the previous one, clears the notice, enters `state` (and,
   *  for a reload, empties `lastRead`), and routes only this read's answers — and only while
   *  `stillMine` — to the handlers. Everything is set before subscribing, so a read that answers
   *  synchronously lands on the new state. */
  private startRead(
    target: TTarget,
    state: RecoveryFileGateState<TPlan>,
    isReload: boolean,
    stillMine: () => boolean,
    onAnswer: (read: TRead) => void,
    onError: () => void,
  ): void {
    if (this.destroyed) {
      return;
    }
    this.inFlight?.unsubscribe();
    const token = {};
    this.currentRead = token;
    const isCurrent = () => this.currentRead === token && stillMine();
    this.reloading.set(isReload);
    if (isReload) {
      this.lastReadSlot.set(null);
    }
    this.noticeSlot.set(null);
    this.rawState.set(state);
    const source = isReload ? this.deps.read : (this.deps.verifyRead ?? this.deps.read);
    this.inFlight = source(target)
      .pipe(timeout(LIVE_READ_TIMEOUT_MS))
      .subscribe({
        next: (read) => {
          if (isCurrent()) {
            this.currentRead = null;
            onAnswer(read);
          }
        },
        error: () => {
          if (isCurrent()) {
            this.currentRead = null;
            onError();
          }
        },
      });
  }

  private onVerifyRead(target: TTarget, plan: TPlan, read: TRead): void {
    // The plan changed while the read ran: the answer is about a plan that is gone. Checked before
    // the observer, so an old read never overwrites what a reload of the target has just reset.
    if (this.deps.plan() !== plan) {
      this.settle(null);
      return;
    }
    let stampedPlan: TPlan;
    try {
      this.deps.onLiveRead?.(read);
      const verification = this.deps.verify(read, plan);
      if (!verification.available) {
        this.settle({ kind: 'readFailed' });
        return;
      }
      if (verification.drifted.length > 0) {
        this.deps.onDrift?.(verification.drifted);
        this.settle({ kind: 'drifted', drifted: verification.drifted });
        return;
      }
      stampedPlan = this.deps.stamp(plan, read);
    } catch (error) {
      console.error('RecoveryFileGate: a callback threw on a live read', error);
      this.settle({ kind: 'readFailed' });
      return;
    }

    try {
      this.deps.save({ target, stampedPlan, read, verifiedAt: Date.now() });
    } catch (error) {
      // Mostly a refused download — but a bug while building the record lands here too, and must
      // not pass silently as "the browser said no".
      console.error('RecoveryFileGate: saving the recovery file failed', error);
      this.settle({ kind: 'saveFailed' });
      return;
    }
    this.rawState.set({ kind: 'saved', plan, stampedPlan });
  }

  private onReloadRead(read: TRead): void {
    try {
      if (!this.deps.isComplete(read)) {
        this.settleReload({ kind: 'readFailed' });
        return;
      }
      this.deps.onLiveRead?.(read);
    } catch (error) {
      console.error('RecoveryFileGate: a callback threw on a reload', error);
      this.settleReload({ kind: 'readFailed' });
      return;
    }
    this.lastReadSlot.set(read);
    this.settleReload(null);
  }

  /** Back to `idle`, with `notice` when there is one to give (`null` keeps the current notice). */
  private settle(notice: RecoveryFileNotice<TDrift> | null): void {
    this.rawState.set({ kind: 'idle' });
    if (notice !== null) {
      this.noticeSlot.set(notice);
    }
  }

  private settleReload(notice: RecoveryFileNotice<TDrift> | null): void {
    this.reloading.set(false);
    this.settle(notice);
  }
}
