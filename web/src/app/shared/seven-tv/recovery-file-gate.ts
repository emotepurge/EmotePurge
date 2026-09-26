import { DestroyRef, Signal, WritableSignal, computed, linkedSignal, signal } from '@angular/core';
import { Observable, Subscription, timeout } from 'rxjs';

import { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';

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
  /** The read failed, timed out or stopped short — nothing it says can vouch for a removal. */
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
  /** The live read of the target. Bounded by {@link LIVE_READ_TIMEOUT_MS}. */
  read: (target: TTarget) => Observable<TRead>;
  verify: (read: TRead, plan: TPlan) => RecoveryFileVerification<TDrift>;
  /** The plan the run starts with, carrying what the read found (aliases, default names, …). */
  stamp: (plan: TPlan, read: TRead) => TPlan;
  /** Writes the recovery file. Throws when the browser refuses the download. */
  save: (file: RecoveryFileSave<TTarget, TPlan, TRead>) => void;
  /** Called with the drifts of a read before the `drifted` notice is set — the consumer lays the
   *  live state over its rows and takes back the decisions that no longer hold. */
  onDrift: (drifted: readonly TDrift[]) => void;
  /** Sees every live answer that still belongs to the current plan, exactly once, before it is
   *  verified — whichever branch follows (unavailable, drifted, saved, save refused). */
  onLiveRead?: (read: TRead) => void;
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
 * - **Only the newest read answers.** Starting a read cancels the one before it, and each answer
 *   first checks that the `verifying` state it set is still the current one. An answer for a plan
 *   that is gone by then settles to `idle` without calling anything and without a notice.
 * - **Nothing fails open.** A read error, a timeout, an unavailable verification and a refused
 *   download each end `idle` with their notice; a drift ends `idle` after `onDrift`.
 * - **Destroying the consumer** cancels the read in flight; no callback runs after that.
 *
 * Plain class with signals, not injectable: the consumer constructs it with its own dependencies.
 */
export class RecoveryFileGate<TPlan, TDrift, TTarget = unknown, TRead = SevenTvSetEntries> {
  /** The state as it holds for the current plan. */
  readonly state: Signal<RecoveryFileGateState<TPlan>>;
  readonly isVerifying: Signal<boolean>;
  /** Cleared by the next `verifyAndSave` and by every change of `noticeResetSource`. */
  readonly notice: Signal<RecoveryFileNotice<TDrift> | null>;

  private readonly deps: RecoveryFileGateDeps<TPlan, TDrift, TTarget, TRead>;
  private readonly rawState = signal<RecoveryFileGateState<TPlan>>({ kind: 'idle' });
  private readonly noticeSlot: WritableSignal<RecoveryFileNotice<TDrift> | null>;

  /** The live read in flight, if any — cancelled when a newer one starts. */
  private inFlight: Subscription | null = null;
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
    deps.destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.inFlight?.unsubscribe();
      this.inFlight = null;
    });
  }

  /** Reads `target` live for `plan`; see the class doc for what each answer does. */
  verifyAndSave(target: TTarget, plan: TPlan): void {
    if (this.destroyed) {
      return;
    }
    this.inFlight?.unsubscribe();
    const verifying: RecoveryFileGateState<TPlan> = { kind: 'verifying', plan };
    const isCurrent = () => this.rawState() === verifying;
    this.noticeSlot.set(null);
    this.rawState.set(verifying);
    this.inFlight = this.deps
      .read(target)
      .pipe(timeout(LIVE_READ_TIMEOUT_MS))
      .subscribe({
        next: (read) => {
          if (isCurrent()) {
            this.onRead(target, plan, read);
          }
        },
        // A failed read vouches for nothing, so it releases nothing — and an error about a plan
        // that is gone by now names nothing either.
        error: () => {
          if (!isCurrent()) {
            return;
          }
          this.settle(this.deps.plan() === plan ? { kind: 'readFailed' } : null);
        },
      });
  }

  private onRead(target: TTarget, plan: TPlan, read: TRead): void {
    // The plan changed while the read ran: the answer is about a plan that is gone. Checked before
    // the observer, so an old read never overwrites what a reload of the target has just reset.
    if (this.deps.plan() !== plan) {
      this.settle(null);
      return;
    }
    this.deps.onLiveRead?.(read);
    const verification = this.deps.verify(read, plan);
    if (!verification.available) {
      this.settle({ kind: 'readFailed' });
      return;
    }
    if (verification.drifted.length > 0) {
      this.deps.onDrift(verification.drifted);
      this.settle({ kind: 'drifted', drifted: verification.drifted });
      return;
    }

    const stampedPlan = this.deps.stamp(plan, read);
    try {
      this.deps.save({ target, stampedPlan, read, verifiedAt: Date.now() });
    } catch {
      this.settle({ kind: 'saveFailed' });
      return;
    }
    this.rawState.set({ kind: 'saved', plan, stampedPlan });
  }

  /** Back to `idle`, with `notice` when there is one to give (`null` keeps the current notice). */
  private settle(notice: RecoveryFileNotice<TDrift> | null): void {
    this.rawState.set({ kind: 'idle' });
    if (notice !== null) {
      this.noticeSlot.set(notice);
    }
  }
}
