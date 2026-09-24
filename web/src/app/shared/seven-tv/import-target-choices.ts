import {
  EmoteSetTargetAccount,
  EmoteSetTargetsResponse,
} from '../../core/seven-tv/seven-tv-emote-set.model';

/** Why a set's radio is disabled — spec 8.6, second/third bullet; `notEditable` added by spec 5.8/
 *  E19 (#253, AK 29): a set the response lists but marks `editable: false` (F16 — no owner id
 *  7TV would let the backend match, or matched to none of the caller's own readable accounts).
 *  Positively named after the rule that makes a set unselectable ("is NORMAL"), not after every
 *  kind that fails it: a future fifth `kind` value must land as `notNormalKind` without a code
 *  change here. */
export type ImportTargetDisabledReason = 'notNormalKind' | 'isSourceSet' | 'notEditable';

/** One selectable set row of the target picker (spec 8.6, addendum 39). Carries everything the
 *  dialog needs at selection time to build the eventual `ImportTargetChoice` it closes with —
 *  `channelName` is the account's `channelName` (`null` for an untracked account), not a per-set
 *  field. Never a `PERSONAL` set — {@link toAccountGroup} filters those out before this shape ever
 *  gets built (spec addendum 39, mirroring the source picker's own addendum 34), so there is no
 *  `isPersonal` field left to carry: the only `disabledReason` a rendered set can still have besides
 *  `isSourceSet` is `notNormalKind`, and the only kinds left that trigger it (`GLOBAL`/`SPECIAL`)
 *  share one label either way. */
export interface ImportTargetSetChoice {
  emoteSetId: string;
  setName: string;
  /** Already resolved (see {@link resolveOwnerLabel}) — never `null` and never blank, unlike the
   *  wire `EmoteSetTargetSummary.ownerDisplayName` this is built from. Every later consumer of an
   *  `ImportTargetChoice` (the picker's own untracked-confirmation banner, the copy confirm dialog,
   *  the progress section) reads this field rather than the raw one, so the fallback only has to be
   *  decided once, here, instead of at each place that renders an owner (Codex round 3 P2). */
  ownerDisplayName: string;
  /** Labeled "aktiv" (spec 8.6) — `EmoteSetTargetSummary.isActive`, from `activeEmoteSetId` (E21). */
  isActive: boolean;
  disabled: boolean;
  disabledReason: ImportTargetDisabledReason | null;
}

/** One account (own 7TV account, or an `editor_of` grant), with its sets in the order 6.2 already
 *  returns them in (active first, then ordinal by name — `ToEmoteSetTargetSummary` on the backend). */
export interface ImportTargetAccountGroup {
  twitchChannelId: string;
  twitchLogin: string;
  /** `EmoteSetTargetAccount.trackedChannelName` — the single field the tracked/untracked grouping
   *  reads (spec 6.2, 8.6). `null` for an untracked account. */
  channelName: string | null;
  isTracked: boolean;
  /** `EmoteSetTargetAccount.isOwnAccount` — "is this the caller's own Twitch identity", passed
   *  through unchanged. Answers a different question from `isTracked` and can disagree with it in
   *  either direction (the account's doc explains why) — the picker's load-time preselection
   *  (`import-target-dialog.ts`'s `firstPreselectableTarget`) is the one consumer that needs it: a
   *  live bug (finding 4, Live-Verifikation K2 2026-09-21) fell through to a *different* tracked
   *  account's active set whenever the caller's own one was disabled (it being the copy's source),
   *  because the old code preselected "whichever tracked account comes first", not "the caller's
   *  own account specifically". */
  isOwnAccount: boolean;
  /** `EmoteSetTargetAccount.activeEmoteSetId` (spec 6.2) — carried through so `ImportTargetChoice`
   *  can tell a click on this account's active set apart from any other, *without* re-deriving it
   *  from `ImportTargetSetChoice.isActive` a second time at the point that decision actually
   *  matters (`import-flow.ts`'s `toTargetSelection`, spec 8.6/F5/AK 36). `null` for an untracked
   *  account, which has no channel-scoped "our" notion of active at all — **and** whenever the
   *  account's reported active set turns out to be `PERSONAL` (spec addendum 39, mirroring the
   *  source picker's P2-2 fix), even though nothing downstream actually needs that: preselection
   *  ({@link ImportTargetDialog}'s `firstPreselectableTarget`) already keys on
   *  `ImportTargetSetChoice.isActive` over the (already PERSONAL-filtered) {@link sets}, never on
   *  this raw id, and `import-flow.ts`'s `toTargetSelection` only ever compares this field against
   *  an `emoteSetId` that came from a rendered, selectable set to begin with — never a `PERSONAL`
   *  one. Nulling it here is a defensive consistency measure, not a fix for a reachable bug: the
   *  invariant is simply that this field never names a set the picker does not also offer a row
   *  for. */
  activeEmoteSetId: string | null;
  /** This account's own set list could not be read — render the account with a visible reason
   *  instead of a silently empty flyout (spec Falle, 8.6). */
  setsUnavailable: boolean;
  /** {@link sets} is empty *and* {@link setsUnavailable} is `false` — the account's set list was
   *  read successfully but held nothing offerable, either because it was genuinely empty or because
   *  every set on it was `PERSONAL` and got filtered out (spec addendum 39, operator decision
   *  2026-09-22: both read the same to the picker, and distinguishing them would cost a case nobody
   *  asked for). Distinct from {@link setsUnavailable} — that one means "we couldn't even read the
   *  list", this one means "we read it, and there is nothing here to offer". The account still
   *  renders (its heading stays), with a short notice in place of any radio. */
  noUsableSets: boolean;
  sets: ImportTargetSetChoice[];
}

/** The picker's two groups, in render order — tracked first, then untracked (spec 8.6). */
export interface ImportTargetChoices {
  tracked: ImportTargetAccountGroup[];
  untracked: ImportTargetAccountGroup[];
}

/**
 * Resolves one set's displayed owner (Codex round 3 P2: "Provide a label when the owner display
 * name is absent"). 7TV intentionally omits `ownerDisplayName` for an account with no
 * `owner.mainConnection` (E7) — the caller of this transform (`ImportTargetDialog`) still has to
 * offer that set, so falling straight through to a blank quote (`"from '' "`) is not an option.
 *
 * Order of preference: the display name, if it is genuinely there (a value present but only
 * whitespace counts as absent, same treatment); otherwise the account's own Twitch login
 * (`EmoteSetTargetAccount.twitchLogin`, spec 6.2) — it identifies the account uniquely and is never
 * compared against anything here, only shown (E7 bars that role for identity checks, not display);
 * otherwise `unknownOwnerLabel`, the one case 6.2 says "sollte es laut Vertrag nicht geben" (an
 * empty login), so the caller supplies its own translated text rather than this pure function
 * inventing untranslated wording.
 */
function resolveOwnerLabel(
  ownerDisplayName: string | null,
  twitchLogin: string,
  unknownOwnerLabel: string,
): string {
  if (ownerDisplayName !== null && ownerDisplayName.trim().length > 0) {
    return ownerDisplayName;
  }
  if (twitchLogin.trim().length > 0) {
    return twitchLogin;
  }
  return unknownOwnerLabel;
}

/**
 * Pure transform from 6.2's wire response into the picker's two account groups (spec 8.6, first
 * three bullets; layout and PERSONAL-filtering per addendum 39). Replaces
 * `import-target-options.ts`'s `importTargetOptions` — the picker chooses *sets*, not *channels*,
 * so the unit of selection changed, not just the data source.
 *
 * `sourceEmoteSetId` is `CapturedImportScope.emoteSetId` — the run's own source set. It disables that
 * one set wherever it appears in the offer list (normally under the account whose `channelName`
 * equals the page's `currentChannelName`, but this function does not need to know that: matching by
 * id alone is exactly what makes "the source account isn't in the response at all" — a caller with
 * no editor rights on their own current channel — a no-op instead of a special case).
 *
 * Grouping (tracked vs. untracked) is a stable partition, not a re-sort: each side keeps the API's
 * own ordering (own account first, then `editor_of` ordinal by login, spec 6.2) among its members.
 *
 * `unknownOwnerLabel` is threaded through to {@link resolveOwnerLabel} — a translated string, since
 * this function itself has no `TranslocoService` to call (`core/` layering, and this file has no
 * Angular DI of its own either); the caller (`ImportTargetDialog`) is the one place that already has
 * one.
 */
export function importTargetChoices(
  response: EmoteSetTargetsResponse,
  sourceEmoteSetId: string,
  unknownOwnerLabel: string,
): ImportTargetChoices {
  const groups = response.accounts.map((account) =>
    toAccountGroup(account, sourceEmoteSetId, unknownOwnerLabel),
  );
  return {
    tracked: groups.filter((group) => group.isTracked),
    untracked: groups.filter((group) => !group.isTracked),
  };
}

function toAccountGroup(
  account: EmoteSetTargetAccount,
  sourceEmoteSetId: string,
  unknownOwnerLabel: string,
): ImportTargetAccountGroup {
  // Spec addendum 39 (#217): `PERSONAL` sets never reach the picker at all, not even disabled —
  // filtered here, before anything downstream (the dialog's template, its preselection) ever sees
  // one. Mirrors foreign-channel-step.ts's `selectableRadioSets` for the source picker (addendum
  // 34), just done once here instead of as a template-level computed, since this transform is
  // already the one place every set passes through.
  const offerableSets = account.sets.filter((set) => !set.isPersonal);

  // A reported active set that is itself PERSONAL counts as no active set at all (addendum 39,
  // mirroring the source picker's P2-2 fix) — it has no row in offerableSets to point at, so
  // nothing downstream may still treat its raw id as meaningful.
  const activeSetIsPersonal = account.sets.some(
    (set) => set.id === account.activeEmoteSetId && set.isPersonal,
  );

  return {
    twitchChannelId: account.twitchChannelId,
    twitchLogin: account.twitchLogin,
    channelName: account.trackedChannelName,
    isTracked: account.trackedChannelName !== null,
    isOwnAccount: account.isOwnAccount,
    activeEmoteSetId: activeSetIsPersonal ? null : account.activeEmoteSetId,
    setsUnavailable: account.setsUnavailable,
    noUsableSets: offerableSets.length === 0 && !account.setsUnavailable,
    sets: offerableSets.map((set) => ({
      emoteSetId: set.id,
      setName: set.name,
      ownerDisplayName: resolveOwnerLabel(
        set.ownerDisplayName,
        account.twitchLogin,
        unknownOwnerLabel,
      ),
      isActive: set.isActive,
      disabled: set.id === sourceEmoteSetId || set.kind !== 'NORMAL' || !set.editable,
      disabledReason:
        // The source takes precedence when (implausibly) both apply — "this is where it came
        // from" is the more specific, more actionable reason of the two. `notNormalKind` can now
        // only ever mean GLOBAL/SPECIAL — PERSONAL is filtered out above before this ternary runs.
        // `notEditable` is checked last (spec #253, brief T4): a non-NORMAL kind is already the
        // more specific reason a set never reaches this picker at all (E11), so a set that is
        // both wins on `notNormalKind`, not on the ownership question `editable` answers.
        set.id === sourceEmoteSetId
          ? 'isSourceSet'
          : set.kind !== 'NORMAL'
            ? 'notNormalKind'
            : !set.editable
              ? 'notEditable'
              : null,
    })),
  };
}
