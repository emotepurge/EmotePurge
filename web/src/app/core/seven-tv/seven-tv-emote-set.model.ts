/**
 * Frontend twin of the three "list a 7TV account's/channel's emote sets" routes (spec 2026-09-20,
 * 6.1/6.2/6.3, E6) — one backend service (`ISevenTvEmoteSetListService.ListByTwitchIdAsync`) behind
 * all three, so the shapes here mirror the wire contract closely rather than each route inventing
 * its own.
 */

/** One observed interval a set was `Channel.ActiveEmoteSetId` (6.1). Only 6.1/6.3 carry this — the
 *  target picker's account-weight endpoint (6.2) never reads per-set history and omits the field
 *  entirely on the wire, not just leaves it empty. */
export interface EmoteSetObservationInterval {
  fromUtc: string;
  toUtc: string | null;
}

/**
 * One 7TV emote set, as `GET /api/channels/{c}/emote-sets` (6.1) and
 * `GET /api/seventv/channels/{c}/emote-sets` (6.3) each list it.
 *
 * `kind` is 7TV's `EmoteSetKind` passed through verbatim as a string, deliberately not narrowed to
 * a closed union of ours (E7's own reasoning, mirrored here): a fifth kind 7TV adds tomorrow must
 * reach the UI as "something we do not offer" via the `kind === 'NORMAL'` selectability check
 * (spec 8.6), never as a type error or a silently-true `NORMAL`.
 *
 * `ownerDisplayName` is `owner.mainConnection.platformDisplayName` (E7) — a display name, never a
 * login, and never compared against anything; every identity check runs on ids (`id` here).
 */
export interface EmoteSetSummary {
  id: string;
  name: string;
  capacity: number | null;
  kind: string;
  isActive: boolean;
  isPersonal: boolean;
  ownerDisplayName: string | null;
  observations: EmoteSetObservationInterval[];
}

/** Response of 6.1 (`GET /api/channels/{c}/emote-sets`) and 6.3
 *  (`GET /api/seventv/channels/{c}/emote-sets`) — same shape, different `activeEmoteSetId` source
 *  (E21: `Channel.ActiveEmoteSetId` for the tracked route, 7TV's `style.activeEmoteSetId` for the
 *  foreign one), which is exactly why the frontend does not need to know which route produced it. */
export interface EmoteSetListResponse {
  activeEmoteSetId: string;
  sets: EmoteSetSummary[];
}

/** Same fields as {@link EmoteSetSummary} minus `observations` — 6.2's account-weight endpoint never
 *  carries per-set history (spec 6.2). */
export interface EmoteSetTargetSummary {
  id: string;
  name: string;
  capacity: number | null;
  kind: string;
  isActive: boolean;
  isPersonal: boolean;
  ownerDisplayName: string | null;
  /** `owner.id` (spec 5.8, E19) — `null` when 7TV named no owner at all, the one case F16 makes
   *  `editable` unconditionally `false` for: the backend cannot ask 7TV whose set this is without
   *  spending an unbudgeted request the target list must not cost. Never compared against anything
   *  here — {@link editable} is already the answer to "can this account write to it", computed
   *  once by the backend from the same pure rule the ownership check itself runs (spec 5.8). The id
   *  is on the wire only so a test can recompute {@link editable} independently, not so the
   *  frontend derives it a second time. */
  ownerSevenTvUserId: string | null;
  /** Spec 5.8/E19 — `true` exactly when {@link ownerSevenTvUserId} equals the `sevenTvUserId` of
   *  some account in the *same* response whose list was readable. The frontend reads this field, it
   *  never recomputes it (spec 5.8: "das Frontend liest `editable`, es berechnet es nicht") — the
   *  picker ({@link import-target-choices.ts}'s `notEditable`) and the shared pre-check
   *  (`resolveEditableSet`) both key off it directly. */
  editable: boolean;
}

/**
 * One 7TV account the target picker may offer — the caller's own account, or one they hold a 7TV
 * editor grant on (spec 6.2).
 *
 * `trackedChannelName` is the *only* field the picker's tracked/untracked grouping reads (spec 6.2,
 * 8.6) — never `isOwnAccount`, which answers a different question ("is this the caller's own Twitch
 * identity") and can disagree with it in either direction (a moderator's own channel need not be
 * EmotePurge-tracked; an editor grant can point at a tracked channel).
 */
export interface EmoteSetTargetAccount {
  twitchChannelId: string;
  twitchLogin: string;
  isOwnAccount: boolean;
  trackedChannelName: string | null;
  activeEmoteSetId: string | null;
  sets: EmoteSetTargetSummary[];
  /** This one account's set list could not be read — the account still renders, with a visible
   *  reason (spec Falle: never a silently empty flyout, spec 8.6). */
  setsUnavailable: boolean;
  /** `userByConnection.id` (spec 5.8, E19) — `null` means either this account has no 7TV
   *  connection or its list could not be read ({@link setsUnavailable}); the value another
   *  account's set can point at through {@link EmoteSetTargetSummary.ownerSevenTvUserId} to mark
   *  itself `editable` by *this* account. */
  sevenTvUserId: string | null;
}

/** Response of 6.2 (`GET /api/seventv/me/emote-set-targets`). */
export interface EmoteSetTargetsResponse {
  accounts: EmoteSetTargetAccount[];
  /** True when the grants lookup or at least one account's set list could not be read — a
   *  dialog-level degradation notice, not a silent gap (spec 6.2, 8.6). */
  sevenTvUnavailable: boolean;
}

/** One channel the client expected the report to touch, but the service could not resolve to a
 *  changed row (spec 5.2 step 3, 5.3, E18/H2) — a missing, inactive or excluded channel
 *  (`'notTracked'`, the block kept deliberately vague) or one that is active under a *different*
 *  set right now (`'activeSetDiffers'`, a stale `Channel.ActiveEmoteSetId`, F13). Either reason
 *  keeps the report at `partial` (`sync-report-outcome.ts`'s `channelMismatch`), never `succeeded`,
 *  even when every resolved channel was complete. */
export interface UnresolvedChannel {
  channelName: string;
  reason: 'notTracked' | 'activeSetDiffers';
}

/** Shared body of the two set-centric bookkeeping routes (spec 5.1) — no `emoteSetId` here, the
 *  route already names it. `expectedChannelName` is the channel the client expects this report to
 *  touch (E18): the target's tracked channel when the target is that channel's *active* set,
 *  `null` otherwise (untracked target, or a tracked-but-not-active one). */
export interface SyncInSetBody {
  sevenTvEmoteIds: string[];
  expectedChannelName: string | null;
}

/** One tracked channel the deletion report actually touched (spec 5.3). */
export interface SyncDeletedInSetChannelResult {
  channelName: string;
  archivedCount: number;
  notFoundIds: string[];
}

/** `POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted` (spec 5.1, 5.3) — the set-centric
 *  closing report for a delete/replace run. `channels: []` together with `unresolvedChannel: null`
 *  means "no tracked channel had this set active" (an untracked or non-active target, spec 5.2) —
 *  not a failure (F8, `sync-report-outcome.ts`'s `succeeded`). `resyncTriggered` names exactly the
 *  channels stage 7 (5.1) actually started a resync for — the client must not start a second one
 *  for any channel in this list (E12, F15). */
export interface SyncDeletedInSetResponse {
  reportedCount: number;
  channels: SyncDeletedInSetChannelResult[];
  unresolvedChannel: UnresolvedChannel | null;
  resyncTriggered: string[];
}

/** One tracked channel the restore report actually touched (spec 5.3). */
export interface SyncRestoredInSetChannelResult {
  channelName: string;
  restoredCount: number;
  notFoundIds: string[];
}

/** `POST /api/seventv/emote-sets/{emoteSetId}/sync-restored` (spec 5.1, 5.3) — the mirror of
 *  {@link SyncDeletedInSetResponse}, see its doc for `channels`/`resyncTriggered`. */
export interface SyncRestoredInSetResponse {
  reportedCount: number;
  channels: SyncRestoredInSetChannelResult[];
  unresolvedChannel: UnresolvedChannel | null;
  resyncTriggered: string[];
}

/** The account-/set-scoped fields of a resolved restore target (spec 6.1's `ResolvedRestoreTarget`)
 *  *without* the `host*` fields that only make sense once a page/flow has attached itself to them —
 *  what `resolveEditableSet` (spec 6.2) alone can answer from the target list, before any caller
 *  knows which page or run it will end up feeding. `ownerDisplayName`/`setName` already carry their
 *  display fallback (never blank, matching `import-target-choices.ts`'s `resolveOwnerLabel`) —
 *  every later reader shows these directly instead of inventing its own `?? ''`. */
export interface EditableSetTarget {
  emoteSetId: string;
  setName: string;
  ownerDisplayName: string;
  twitchLogin: string;
  trackedChannelName: string | null;
  isActiveSet: boolean;
}

/**
 * The four outcomes of `SevenTvEmoteSetService.resolveEditableSet` (spec 6.2, 4.2, E19) — the one
 * pre-check every first mutation (restore, delete, replace) runs before touching 7TV. A discriminated
 * union on `status` rather than a `target: EditableSetTarget | null` plus a separate reason: a caller
 * that only handles the success case is guided by the compiler to a `status` check, not an
 * accidental null-check on a value it never needed to read in the first place.
 */
export type EditableSetResolution =
  | { status: 'editable'; target: EditableSetTarget }
  | { status: 'notSelectable' }
  | { status: 'notEditable' }
  | { status: 'unavailable' };
