import {
  EmoteSetTargetAccount,
  EmoteSetTargetsResponse,
} from '../../core/seven-tv/seven-tv-emote-set.model';

/** Why a set's radio is disabled — spec 8.6, second/third bullet. Positively named after the rule
 *  that makes a set unselectable ("is NORMAL"), not after every kind that fails it: a future fifth
 *  `kind` value must land as `notNormalKind` without a code change here. */
export type ImportTargetDisabledReason = 'notNormalKind' | 'isSourceSet';

/** One selectable set row of the target picker (spec 8.6). Carries everything the dialog needs at
 *  selection time to build the eventual `ImportTargetChoice` it closes with — `channelName` is the
 *  account's `channelName` (`null` for an untracked account), not a per-set field. */
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
  /** Only feeds the label, never selectability by itself (E7) — see {@link disabledReason}. */
  isPersonal: boolean;
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
  /** `EmoteSetTargetAccount.activeEmoteSetId` (spec 6.2) — carried through so `ImportTargetChoice`
   *  can tell a click on this account's active set apart from any other, *without* re-deriving it
   *  from `ImportTargetSetChoice.isActive` a second time at the point that decision actually
   *  matters (`import-flow.ts`'s `toTargetSelection`, spec 8.6/F5/AK 36). `null` for an untracked
   *  account, which has no channel-scoped "our" notion of active at all. */
  activeEmoteSetId: string | null;
  /** This account's own set list could not be read — render the account with a visible reason
   *  instead of a silently empty flyout (spec Falle, 8.6). */
  setsUnavailable: boolean;
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
 * three bullets). Replaces `import-target-options.ts`'s `importTargetOptions` — the picker chooses
 * *sets*, not *channels*, so the unit of selection changed, not just the data source.
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
  return {
    twitchChannelId: account.twitchChannelId,
    twitchLogin: account.twitchLogin,
    channelName: account.trackedChannelName,
    isTracked: account.trackedChannelName !== null,
    activeEmoteSetId: account.activeEmoteSetId,
    setsUnavailable: account.setsUnavailable,
    sets: account.sets.map((set) => ({
      emoteSetId: set.id,
      setName: set.name,
      ownerDisplayName: resolveOwnerLabel(
        set.ownerDisplayName,
        account.twitchLogin,
        unknownOwnerLabel,
      ),
      isActive: set.isActive,
      isPersonal: set.isPersonal,
      disabled: set.id === sourceEmoteSetId || set.kind !== 'NORMAL',
      disabledReason:
        // The source takes precedence when (implausibly) both apply — "this is where it came
        // from" is the more specific, more actionable reason of the two.
        set.id === sourceEmoteSetId
          ? 'isSourceSet'
          : set.kind !== 'NORMAL'
            ? 'notNormalKind'
            : null,
    })),
  };
}
