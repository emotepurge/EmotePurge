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
}

/** Response of 6.2 (`GET /api/seventv/me/emote-set-targets`). */
export interface EmoteSetTargetsResponse {
  accounts: EmoteSetTargetAccount[];
  /** True when the grants lookup or at least one account's set list could not be read — a
   *  dialog-level degradation notice, not a silent gap (spec 6.2, 8.6). */
  sevenTvUnavailable: boolean;
}
