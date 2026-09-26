import {
  DestroyRef,
  Service,
  Signal,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';

/** The 7TV-writing runs, one engine instance each — see the class doc. A new run service adds its
 *  name here and registers itself; nothing else in this file changes (#256, contract P4). */
export type SevenTvRunKind = 'delete' | 'restore' | 'import';

/** Why the arbiter is busy: a run is `running` (its engine works a queue) or `settling` (the engine
 *  is done, but a re-read or a report of that run has no end state yet). */
export type SevenTvRunClaimPhase = 'running' | 'settling';

/** The reason behind `activeRun`: which kind holds the arbiter, and in which phase. */
export interface SevenTvRunClaim {
  kind: SevenTvRunKind;
  phase: SevenTvRunClaimPhase;
}

/** A start that a start point refused because the arbiter was busy — what `refusedStart` shows
 *  for `REFUSED_START_FEEDBACK_MS`. */
export interface SevenTvRefusedStart {
  attempted: SevenTvRunKind;
  blockedBy: SevenTvRunClaim;
}

/** What a run service hands the arbiter when it registers (#256, contract P1/P4). All three
 *  signals are the service's own projections over every run it has open, not only the shown one. */
export interface SevenTvRunParticipant {
  kind: SevenTvRunKind;
  /** The service's engine works a queue. */
  isRunning: Signal<boolean>;
  /** A run of the service re-reads or waits for a report — its engine is done, the run is not. */
  isSettling: Signal<boolean>;
  /** A run of the service with at least one destructive row is not `closed` yet. */
  destructiveOpen: Signal<boolean>;
}

/** How long `refusedStart` stays set — the transient status message of `docs/UI-Designsprache.md`
 *  §4.5, the same window as the other transient notices of the 7TV runs. */
export const REFUSED_START_FEEDBACK_MS = 4000;

/**
 * Answers one question for every 7TV-writing start point: may a run start right now, and if not,
 * why? It exists because delete, restore and import each run over their *own*
 * `SevenTvRunEngine` instance, so no single engine's `isRunning` can speak for all of them.
 *
 * **Registration, not a service list (#256, contract P4).** Each run service registers itself in
 * its constructor (`register(...)`) with its kind and three signals. The arbiter injects no run
 * service and imports none of their files, so the DI edge now points service → arbiter only — the
 * reverse of the 2026-09-06 decision (arbiter → services, "the services do not know the arbiter").
 * That is why no cycle arises: the arbiter's only dependency is `@angular/core`. A fourth run kind
 * is one value in `SevenTvRunKind` and one `register(...)` call in its own service. Registration
 * is lazy on purpose: a run service that has never been constructed has never started a run, so
 * there is nothing for it to claim yet — no app initializer is needed, and none may pull a run
 * service (and with it the import engine) into the initial bundle. The registry is a signal, so
 * every derivation below also sees a service that registers after it was first read.
 *
 * **Derived, not locked (R1, 2026-09-05, unchanged).** There is still no `tryAcquire`/`release`:
 * `activeRun` is a `computed` over the participants' own signals, so it can never outlive the run
 * it describes — a start the engine refuses leaves no trace here, exactly as before.
 *
 * **Busy means running or settling (contract P2).** `activeClaim` names the kind and phase that
 * holds the arbiter: a running participant before a settling one, otherwise registration order.
 * Two claims at once are constructed only — every start point checks `activeRun() === null` first
 * — so the order is a display rule, not an exclusion rule; the same kind registered twice is not
 * refused, both simply count.
 *
 * **Busy always resolves (contract P6).** `isSettling`/`destructiveOpen` are derived, so "busy" is
 * only ever as live as the report chain behind it: every report a run opens (`sync-deleted`,
 * `sync-imported`, `sync-restored`) is guaranteed to reach `succeeded | partial | failed` within
 * `REPORT_TIMEOUT_MS` of its last attempt, because the response is classified (`map`) *before*
 * `retry` sees it — a throw reaching `retry` unclassified used to leave a run `reporting` forever,
 * which is exactly the gap that would have kept a claim here past any window. The arbiter itself
 * holds no timer; it only ever reflects what the report chain has already resolved.
 *
 * **One unload guard for all runs (contract P3).** `destructiveOpen` is the union over every
 * participant, and the `beforeunload` guard hangs off it — armed while *any* service has a
 * destructive run open, whether or not a dock still shows that run (it used to live in the import
 * service, reading that service's runs only; a delete now protects the tab as well).
 *
 * **The refusal notice (contract P2).** A start point that refuses because the arbiter is busy
 * calls `noteRefusedStart(itsOwnKind)`; `refusedStart` then carries what was attempted and what
 * blocked it for `REFUSED_START_FEEDBACK_MS`, for the page or panel to show as a transient notice.
 */
@Service()
export class SevenTvRunArbiter {
  private readonly participants = signal<readonly SevenTvRunParticipant[]>([]);

  private readonly refusedStartState = signal<SevenTvRefusedStart | null>(null);

  /** Which kind holds the arbiter, and in which phase — `null` while nothing runs or settles. */
  readonly activeClaim: Signal<SevenTvRunClaim | null> = computed(
    () => {
      const participants = this.participants();
      const running = participants.find((participant) => participant.isRunning());
      if (running !== undefined) {
        return { kind: running.kind, phase: 'running' };
      }
      const settling = participants.find((participant) => participant.isSettling());
      if (settling !== undefined) {
        return { kind: settling.kind, phase: 'settling' };
      }
      return null;
    },
    { equal: sameClaim },
  );

  /** The kind that runs **or settles** right now, `null` when a start may go ahead. */
  readonly activeRun: Signal<SevenTvRunKind | null> = computed(
    () => this.activeClaim()?.kind ?? null,
  );

  /** True while any participant has a destructive run open — the union the unload guard reads. */
  readonly destructiveOpen: Signal<boolean> = computed(() =>
    this.participants().some((participant) => participant.destructiveOpen()),
  );

  /** The last refused start, for `REFUSED_START_FEEDBACK_MS` after `noteRefusedStart`. */
  readonly refusedStart: Signal<SevenTvRefusedStart | null> = this.refusedStartState.asReadonly();

  private refusedStartTimeout: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    // The `beforeunload` guard: added when the union turns true, removed when it turns false —
    // only on a transition, so a free arbiter never touches the window at all. `preventUnload` is
    // a module-level function, so `removeEventListener` always targets the exact reference
    // `addEventListener` registered; an inline arrow would silently fail to remove itself.
    let armed = false;
    effect(() => {
      const open = this.destructiveOpen();
      if (open === armed) {
        return;
      }
      armed = open;
      if (open) {
        window.addEventListener('beforeunload', preventUnload);
      } else {
        window.removeEventListener('beforeunload', preventUnload);
      }
    });
    inject(DestroyRef).onDestroy(() => {
      window.removeEventListener('beforeunload', preventUnload);
      clearTimeout(this.refusedStartTimeout);
    });
  }

  /** Adds a run service to the arbiter. Called once, from the service's own constructor, and only
   *  by a root (`providedIn: 'root'`) run service — there is no unregister, so a participant from a
   *  torn-down instance would stay registered, and keep counting toward `activeRun`/
   *  `destructiveOpen`, forever. The write is wrapped in `untracked` defensively, not because
   *  construction itself needs it: Angular's DI already builds a provider outside any reactive
   *  consumer, so this call was never actually at risk of being read as a dependency of an
   *  enclosing `computed`/`effect` — the wrapper costs nothing and keeps that guarantee explicit
   *  should a future caller ever invoke `register` from somewhere other than a constructor. */
  register(participant: SevenTvRunParticipant): void {
    untracked(() => this.participants.update((current) => [...current, participant]));
  }

  /** A start point refused to start `attempted` because the arbiter is busy. Sets `refusedStart`
   *  to what was attempted and what blocked it, for `REFUSED_START_FEEDBACK_MS`; a second call
   *  inside the window starts a fresh one. On a free arbiter nothing blocked anything, so nothing is
   *  noted — the notice never claims a reason that is not there. */
  noteRefusedStart(attempted: SevenTvRunKind): void {
    const blockedBy = untracked(this.activeClaim);
    if (blockedBy === null) {
      return;
    }
    clearTimeout(this.refusedStartTimeout);
    this.refusedStartState.set({ attempted, blockedBy });
    this.refusedStartTimeout = setTimeout(() => {
      this.refusedStartTimeout = undefined;
      this.refusedStartState.set(null);
    }, REFUSED_START_FEEDBACK_MS);
  }
}

function sameClaim(a: SevenTvRunClaim | null, b: SevenTvRunClaim | null): boolean {
  return a === b || (a !== null && b !== null && a.kind === b.kind && a.phase === b.phase);
}

/** Translation key for a run kind's own noun (`sevenTvRun.kind.*`, #256 contract P2) — a `Record`,
 *  not a key built from the kind at the call site (T3 review, P3-7): the compiler then flags a
 *  missing branch the moment a new `SevenTvRunKind` value appears (#254 adds `'undo'`), instead of
 *  silently producing an unresolved key at runtime. */
export const SEVEN_TV_RUN_KIND_LABEL_KEY: Record<SevenTvRunKind, string> = {
  delete: 'sevenTvRun.kind.delete',
  restore: 'sevenTvRun.kind.restore',
  import: 'sevenTvRun.kind.import',
};

/** The transient status message for a refused start (contract P2, `docs/UI-Designsprache.md`
 *  §4.5) — the message key (by phase) plus the blocking kind's noun, already translated:
 *  transloco's own interpolation only substitutes a param's literal text, never a second, nested
 *  key, so the noun has to be resolved before it becomes a param. `translate` is the caller's own
 *  `TranslocoService.translate`, taken as a parameter rather than injected — this file otherwise
 *  imports nothing beyond `@angular/core` (contract P4's DI-edge doc above) — and both renderers
 *  that show this message (`usage-stats-page.ts`'s own transient region, `mass-delete-panel.ts`'s
 *  `abortNotice`) share it, so the sentence and the kind noun cannot drift between them. Takes the
 *  claim directly rather than a `SevenTvRefusedStart`: `mass-delete-panel.ts` reads `activeClaim()`
 *  for its own, panel-local notice and never calls `noteRefusedStart` itself (its `abortNotice` is
 *  already the visible, persistent explanation — routing the same refusal through the arbiter's
 *  4-second transient notice too would announce it twice on a page that mounts both). */
export function refusedStartMessage(
  claim: SevenTvRunClaim,
  translate: (key: string) => string,
): { messageKey: string; kind: string } {
  return {
    messageKey: `sevenTvRun.notStarted.${claim.phase}`,
    kind: translate(SEVEN_TV_RUN_KIND_LABEL_KEY[claim.kind]),
  };
}

/** The standard `beforeunload` incantation (MDN): calling `preventDefault()` and setting a
 *  non-undefined `returnValue` is what makes the browser show its own confirmation prompt — neither
 *  Chromium, Firefox nor Safari display a custom string any more, so the exact value assigned here
 *  is irrelevant, only that one is set. */
function preventUnload(event: BeforeUnloadEvent): void {
  event.preventDefault();
  event.returnValue = '';
}
