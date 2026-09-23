import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { computed, signal } from '@angular/core';
import { Observable, map } from 'rxjs';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import {
  ImportTargetLoadState,
  ImportTargetSelection,
  loadImportTarget,
} from '../../core/emotes/import-target-loader';
import { ImportSource } from '../../core/seven-tv/import-source';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { TransferPlan, TransferRow } from '../../core/seven-tv/transfer-plan';
import { filterAlreadyPresent, verifyReplaceTargets } from './already-present-filter';
import { ImportConfirmOutcome, openImportConfirmDialog } from './import-confirm-dialog';
import { ImportTargetChoice } from './import-target-dialog';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

/**
 * Everything the flow needs, handed in rather than injected. The flow opens dialogs, which live in
 * `shared/`, while the services it drives live in `core/` — and `core/` may not import from
 * `shared/`. A service here would therefore have to sit in `shared/` and be provided there, for a
 * piece of code that holds no state between calls; a function that takes its collaborators keeps
 * the dependency edges pointing the one legal way and lets both entry points (the usage-stats page
 * and the restore panel) pass their own injected instances.
 */
export interface ImportFlowDeps {
  dialog: Dialog;
  emoteAdminService: EmoteAdminService;
  /** `loadImportTarget`'s live-list collaborator for a `'chosen'` target (spec F5) — the three
   *  channel-only entry points (file, foreign channel, leaderboard) never reach it, since they
   *  always resolve `'activeSet'`. */
  emoteSetService: SevenTvEmoteSetService;
  /** Only for the direct reads against 7TV — `filterAlreadyPresent` right before the run (#149 P1
   *  fix), and the confirm dialog's live check of the replace targets before it saves the recovery
   *  file. Every other read in this flow goes through `emoteAdminService`. */
  httpClient: HttpClient;
  tokenService: SevenTvTokenService;
  importService: SevenTvImportService;
  arbiter: SevenTvRunArbiter;
}

/**
 * What `startImportFlow` targets (spec F5, 8.6). Two shapes, not one, because the flow has two
 * different callers with two different amounts of knowledge:
 *
 * - `'activeSet'` — a caller with no set of its own to name, always the channel's active one.
 *   Resolves exactly like before this spec: `EmoteSetStatus`/`listEmotes`/`getSetWarning`,
 *   unchanged requests (AK 36). Nothing on this page still uses it (T4.5 gave the three
 *   channel-only doors — file, foreign channel, leaderboard — a real set to name, see below), but
 *   it stays: a caller that genuinely has no more specific answer than "this channel's active set"
 *   is not a wrong caller.
 * - `'chosen'` — a full `ImportTargetChoice` (minus the `scope` the picker also returns, which the
 *   flow never needs — the caller has already turned that into `source`'s rows before calling
 *   here). Carries a tracked channel's *specific* set (active or not) or an untracked account's
 *   set; `loadImportTarget`'s `'trackedSet'`/`'untrackedSet'` cases read it live rather than assume
 *   it is the channel's active one — that assumption is exactly the bug this type exists to close
 *   (F5: "der Ziel-Loader liest das aktive Set, und der Dialog schließt mit dessen ID", the reason
 *   T2.5a and T2.5b were one commit to begin with). Two callers build one now: the K2 target
 *   picker's own answer, and — since T4.5 — `import-trigger.ts`'s own `toImportTarget`, which
 *   fabricates the same shape for the page's *selected* set (active or not) without ever having
 *   asked a picker. `emoteSetId === activeEmoteSetId` still takes the identical `'trackedActive'`
 *   fast path below regardless of which of the two built the choice — the picker's "getracktes
 *   Ziel und aktiv" case and the page's "selected set happens to be the active one" case are the
 *   same request contract (AK 36), not two.
 *
 * A plain `Omit<ImportTargetChoice, 'scope'>` (no `kind` tag) was the first draft, distinguished
 * from `'activeSet'` structurally (an `emoteSetId` field's presence). The explicit tag reads better
 * at every call site below and makes a third, future shape (K4's set dropdown, maybe) a compile
 * error at every `switch` here instead of a silent fall-through.
 */
export type ImportFlowTarget =
  | { kind: 'activeSet'; channelName: string }
  | { kind: 'chosen'; choice: Omit<ImportTargetChoice, 'scope'> };

/** `loadImportTarget`'s own input (spec F5) from an `ImportFlowTarget` — the one place this flow
 *  decides *how* to resolve a target, so every caller below shares the same rule.
 *
 *  A tracked choice whose `emoteSetId` is the account's own `activeEmoteSetId` resolves via
 *  `'trackedActive'` — the *same* "today" path an `'activeSet'` target takes, on purpose (spec 8.6,
 *  fourth bullet: "getracktes Ziel **und** `emoteSetId === activeEmoteSetId` ⇒ heutiger Weg").
 *  AK 36 pins the reason as a *request* contract, not a style preference: the active case must fire
 *  exactly the same requests as before this spec, no more — and per the E6 permit budget, the
 *  live-list read this function's other branch takes is not free, it draws on the same 7TV-preview
 *  budget the target-set preview itself does. Comparing `emoteSetId === activeEmoteSetId` here
 *  (rather than reusing `ImportTargetSetChoice.isActive`, computed one step earlier and already
 *  discarded by the time a choice reaches this function) is what makes that comparison legible at
 *  the exact point it decides anything.
 *
 *  A tracked choice's `channelName` is asserted non-null rather than defended with a fallback:
 *  `ImportTargetChoice`'s own contract (`import-target-dialog.ts`) is that `channelName` is the
 *  tracked class's attribute, never absent when `isTracked` is `true` — a `null` there would be a
 *  bug in the picker, not a legitimate state this function should quietly paper over.
 *
 *  An untracked choice routes through `twitchLogin` (spec 6.2), never `ownerDisplayName`: the
 *  latter is a display name, unfit for a URL path segment and explicitly barred from any
 *  comparison/routing use (E7, spec 8.6) — `twitchLogin` is the machine-readable field 6.2 carries
 *  for exactly this. */
/** The chosen set's own name, but only for the case where the picked set turns out to be the
 *  account's active one (`toTargetSelection`'s `'trackedActive'` branch, spec F5/AK 36 above): that
 *  branch fires the same three "today" requests as an `'activeSet'` target and never fetches a set
 *  name at all (`loadImportTarget`'s own doc — `setName` stays `null` there by contract), so the
 *  only name available for that set is the one the picker's own click already carried
 *  (`ImportTargetChoice.setName`, third Codex round P2: "Preserve the selected active set name").
 *  `null` for every other case — a non-active tracked choice or an untracked one both name their set
 *  from the live list `loadImportTarget` itself reads, which already has a real answer to use. */
function chosenActiveSetName(
  target: ImportFlowTarget,
): { emoteSetId: string; setName: string } | null {
  if (target.kind !== 'chosen') {
    return null;
  }
  const choice = target.choice;
  if (!choice.isTracked || choice.emoteSetId !== choice.activeEmoteSetId) {
    return null;
  }
  return { emoteSetId: choice.emoteSetId, setName: choice.setName };
}

/** Applies {@link chosenActiveSetName} to a freshly loaded state, guarded by an id comparison done
 *  *here*, at the point the loaded answer actually exists — not any earlier. `choice` is a snapshot
 *  taken when the picker closed; the account's active set can have moved on by the time
 *  `getSetStatus` answers (the same stale-snapshot question spec F5 raised for the id itself), so
 *  attaching the chosen name to a `setId` it no longer describes would show the wrong name for the
 *  right set. When the ids disagree this leaves `state` untouched — `targetSetLabel` in the confirm
 *  dialog then falls back to the id, exactly as it already does for every other unnamed case. */
function withChosenSetName(
  state: ImportTargetLoadState,
  chosen: { emoteSetId: string; setName: string } | null,
): ImportTargetLoadState {
  if (chosen === null || state.status !== 'ready' || state.setName !== null) {
    return state;
  }
  return state.setId === chosen.emoteSetId ? { ...state, setName: chosen.setName } : state;
}

function toTargetSelection(target: ImportFlowTarget): ImportTargetSelection {
  if (target.kind === 'activeSet') {
    return { kind: 'trackedActive', channelName: target.channelName };
  }
  const choice = target.choice;
  if (choice.isTracked) {
    if (choice.channelName === null) {
      throw new Error(
        'ImportTargetChoice.isTracked without a channelName — picker contract broken',
      );
    }
    if (choice.emoteSetId === choice.activeEmoteSetId) {
      return { kind: 'trackedActive', channelName: choice.channelName };
    }
    return { kind: 'trackedSet', channelName: choice.channelName, emoteSetId: choice.emoteSetId };
  }
  return { kind: 'untrackedSet', channelName: choice.twitchLogin, emoteSetId: choice.emoteSetId };
}

/** The confirm dialog's `targetChannelName` from an `ImportFlowTarget` — `null` only for an
 *  untracked choice, which the header then names by owner instead (spec 8.6, AK 39). */
function toTargetChannelName(target: ImportFlowTarget): string | null {
  return target.kind === 'activeSet' ? target.channelName : target.choice.channelName;
}

/** What `recheckTransferPlan` hands to `startImport`. */
export interface TransferPlanRecheck {
  /** The plan minus every row the fresh read dropped, in the plan's own order. */
  plan: TransferPlan;
  /** Rows dropped because their source id is already in the target set. */
  skippedDuplicates: number;
  /** Whether the read succeeded — `false` means the duplicate check could not run. */
  duplicateCheckAvailable: boolean;
  /** `replace` rows held back because their target drifted from the confirmed state, or because
   *  the read could not vouch for it (failed or incomplete). */
  replaceSkippedDrift: number;
}

/**
 * The last check before a run starts, from **one** fresh read of the target set (#149/T5 for the
 * duplicate half, docs/plans/Plan-230-Namenskonflikte.md section 2 point 5 for the replace half):
 *
 * - `add`, `renameSource` and `replace` rows whose *source* id is already in the set drop out —
 *   the duplicate filter, failing open on a failed read like it always did. A `replace` row drops
 *   out whole: no REMOVE without its ADD.
 * - `adoptSourceName` rows never go through the duplicate filter: the source id is in the set by
 *   definition, that is the entry the row renames.
 * - `replace` rows are verified a second time against the confirmed target (`verifyReplaceTargets`);
 *   one that drifted drops out and is counted. A failed or incomplete read lets **no** `replace` row
 *   through — deleting on an unchecked basis is worse than not deleting.
 *
 * Exported on its own so it can be tested apart from the dialogs around it.
 */
export function recheckTransferPlan(
  httpClient: HttpClient,
  targetSetId: string,
  plan: TransferPlan,
): Observable<TransferPlanRecheck> {
  const checked = plan.rows
    .filter((row) => row.action !== 'adoptSourceName')
    .map((row) => ({ sevenTvEmoteId: row.source.sevenTvEmoteId, row }));
  return filterAlreadyPresent(httpClient, targetSetId, checked).pipe(
    map(({ rows, skipped, available, entries }) => {
      const notPresent = new Set<TransferRow>(rows.map(({ row }) => row));
      const verification = entries === null ? null : verifyReplaceTargets(entries, plan);
      const drifted = new Set(
        verification?.available ? verification.drifted.map((drift) => drift.key) : [],
      );
      let replaceSkippedDrift = 0;
      const kept = plan.rows.filter((row) => {
        if (row.action === 'adoptSourceName') {
          return true;
        }
        if (!notPresent.has(row)) {
          return false;
        }
        if (row.action !== 'replace') {
          return true;
        }
        const passes = verification?.available === true && !drifted.has(row.source.sevenTvEmoteId);
        if (!passes) {
          replaceSkippedDrift++;
        }
        return passes;
      });
      return {
        plan: { rows: kept },
        skippedDuplicates: skipped,
        duplicateCheckAvailable: available,
        replaceSkippedDrift,
      };
    }),
  );
}

/**
 * Confirms and starts one copy run: target data → confirmation → 7TV token → run.
 *
 * **The token prompt comes after the confirmation**, unlike the delete and the restore flows, which
 * ask for the token first. That is deliberate (#72, R2): the picker and this preview are pure reads,
 * and demanding a write secret before the user has seen what would happen asks for trust in the
 * wrong order — here, unlike in a restore, the preview *is* where the decision is made. Do not
 * "align" this with the other two flows.
 *
 * The target load starts before the dialog opens but the dialog does not wait for it: it renders
 * its skeleton immediately and fills in on the single emission `loadImportTarget` guarantees (R8).
 */
export function startImportFlow(
  deps: ImportFlowDeps,
  source: ImportSource,
  target: ImportFlowTarget,
): void {
  const targetState = signal<ImportTargetLoadState>({ status: 'loading' });
  const targetChannelName = toTargetChannelName(target);
  // Only meaningful for an untracked choice (spec 8.6, AK 39) — `targetChannelName` names the
  // destination for every tracked one, so this stays `null` there, on purpose, even though the
  // choice itself also carries an owner display name for a tracked set.
  const targetOwnerDisplayName =
    target.kind === 'chosen' && target.choice.channelName === null
      ? target.choice.ownerDisplayName
      : null;

  // A retry while an earlier load is still in flight must not be overwritten by that older answer
  // — every load claims a generation and drops itself if it is no longer the current one. Closing
  // the dialog bumps the generation too, so a late answer writes into nothing after that.
  let generation = 0;

  // Computed once, from the choice the picker closed with — never re-derived per load/retry, since
  // it names a set the picker already knew, not something a reload could learn anew.
  const activeSetName = chosenActiveSetName(target);

  // True for every legacy "today" door and for a 'chosen' pick that turned out to equal the
  // account's own active set (the same condition `activeSetName` above is already non-null for) —
  // false for a tracked non-active pick and for every untracked one (findings 1/2/3,
  // Live-Verifikation K2 2026-09-21). Drives the confirm dialog's title wording, the run's own
  // `targetIsActiveSet` flag, and therefore whether `onRunComplete` may resync the channel at all.
  const isActiveSet = target.kind === 'activeSet' || activeSetName !== null;

  // The set's own name for the title (finding 1), but only when the title needs to say so — an
  // active target keeps "nach {channel}" and never names the set at all. Read synchronously from
  // the picker's own choice, never from the (still loading) target state: `target.kind === 'chosen'`
  // is implied whenever `isActiveSet` is false (an 'activeSet' door is always active), but the
  // ternary spells that out for the type checker rather than asserting it.
  const titleSetName: string | null =
    !isActiveSet && target.kind === 'chosen' ? target.choice.setName : null;

  const load = (): void => {
    const mine = ++generation;
    targetState.set({ status: 'loading' });
    loadImportTarget(
      deps.emoteAdminService,
      deps.emoteSetService,
      toTargetSelection(target),
    ).subscribe((state) => {
      if (mine === generation) {
        targetState.set(withChosenSetName(state, activeSetName));
      }
    });
  };

  const start = (outcome: ImportConfirmOutcome): void => {
    // The engine only refuses *its own* second run; a delete or restore running elsewhere is
    // invisible to it, so the cross-kind check happens here — silently, because the progress of
    // that other run is already on screen and saying it twice would be the louder mistake.
    if (deps.arbiter.activeRun() !== null) {
      return;
    }
    // #149/T5: `outcome.plan` already passed `buildImportPreview`'s filter against the target set's
    // contents as of when the confirm dialog opened — that snapshot can be stale by the time the
    // user actually confirms (another editor, another tab, a long-open dialog). Re-check fresh,
    // right here, immediately before anything is sent, against 7TV itself rather than our database
    // (see `filterAlreadyPresent`'s doc for why that distinction matters and for the residual race
    // this does not close). The same read verifies every replace target a second time
    // (`recheckTransferPlan`).
    recheckTransferPlan(deps.httpClient, outcome.targetSetId, outcome.plan).subscribe(
      ({ plan, skippedDuplicates, duplicateCheckAvailable, replaceSkippedDrift }) => {
        // #149 P2 review fix: the arbiter check above ran *before* this fetch, which the mutual
        // exclusion contract (design doc §4.3, the SevenTvRunArbiter paragraph) does not actually
        // cover — a delete or restore can start in that window and this would otherwise start a
        // second, overlapping run against the same set. Re-checked here, right before the only
        // remaining call that actually starts anything. Silent on a block, same reasoning as the
        // pre-fetch check above: whichever run got there first is already visible in the dock, so
        // there is something on screen explaining what happened — just not from this confirmation.
        if (deps.arbiter.activeRun() !== null) {
          return;
        }
        deps.importService.startImport(
          // `outcome.targetSetId` is `target.setId` from the *loaded* state — the set the run
          // actually reads/writes against, whichever of the three loader cases produced it (spec
          // F5/AK 44). It is never re-derived from `target` here, on purpose: the load is the one
          // place that already resolved "which set", and re-deriving it a second time from the
          // selection is exactly how a stale assumption like F5's would creep back in.
          //
          // `targetChannelName` is `null` exactly for an untracked target (T2.6, spec 8.6) — the
          // picker's own confirmation step (`import-target-dialog.ts`) is what already made this
          // choice final before this flow ever opened, so there is nothing left to gate here.
          // `targetOwnerDisplayName` (already computed above for the confirm dialog's own header,
          // AK 39) rides along so the dock's post-run summary can name the same owner once the
          // confirm dialog itself is gone (`import-progress-section.ts`). `setName` is the outcome's
          // own resolved name (`ImportConfirmOutcome.targetSetName`, already id-falls-back) — not
          // re-derived here, for the same "the load already resolved it" reason as `setId` above.
          // `isActiveSet` is this function's own `isActiveSet` (findings 2/3): it decides whether
          // `onRunComplete` may resync the channel once this run finishes.
          {
            setId: outcome.targetSetId,
            channelName: targetChannelName,
            ownerDisplayName: targetOwnerDisplayName,
            setName: outcome.targetSetName,
            isActiveSet,
          },
          source.origin,
          plan,
          skippedDuplicates,
          duplicateCheckAvailable,
          replaceSkippedDrift,
        );
      },
    );
  };

  load();

  const confirmRef = openImportConfirmDialog(deps.dialog, {
    source,
    targetChannelName,
    targetOwnerDisplayName,
    targetIsActiveSet: isActiveSet,
    titleSetName,
    target: targetState.asReadonly(),
    retry: load,
    runBlocked: computed(() => deps.arbiter.activeRun() !== null),
    httpClient: deps.httpClient,
  });

  confirmRef.closed.subscribe((outcome) => {
    generation++;
    if (!outcome) {
      return;
    }
    if (deps.tokenService.hasToken()) {
      start(outcome);
      return;
    }
    openSevenTvTokenPromptDialog(deps.dialog).closed.subscribe((saved) => {
      if (saved === true) {
        start(outcome);
      }
    });
  });
}
