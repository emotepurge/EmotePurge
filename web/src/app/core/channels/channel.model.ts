export interface ChannelStatus {
  channelId: string;
  channelName: string;
  isBotActive: boolean;
  activeEmoteSetId: string;
}

/**
 * One answer to "what may I do with this channel", from GET /api/channels/{c}/permissions.
 * Replaces the four probe calls that used to read a real endpoint's success/failure as a permission
 * bit. `canViewUsageStats` is a superset of `canManage` — it also admits the channel's 7TV editors,
 * who may not manage the channel at all — so gating the usage tab on `canManage` would be wrong.
 */
export interface ChannelPermissions {
  canManage: boolean;
  canViewUsageStats: boolean;
  isGlobalAdmin: boolean;
  isTracked: boolean;
  isBotActive: boolean;
  /** Server flag `Tags:RunsEnabled`: the tag play-in and removal runs are released (T-C). */
  tagRunsEnabled: boolean;
  /** True only for the channel's own broadcaster (matched by Twitch id) on an existing row. */
  canPurgeAsBroadcaster: boolean;
}

/**
 * What a broadcaster self-purge would remove, from GET /api/channels/{c}/data-summary. Shown in the
 * confirmation dialog so the number is the actual one, not a guess.
 */
export interface ChannelDataSummary {
  emoteCount: number;
  voteSessionCount: number;
  liveDayCount: number;
  /** Tags the mod team created (#201); the purge removes them with their placements. */
  tagCount: number;
}

/**
 * Per-channel live answer derived from the worker's last Helix poll (up to ~5 min old).
 * `unknown` is "no statement" — the snapshot is missing (worker down, poll disabled) or the
 * channel was never polled (bot inactive) — and must render as no badge, never as offline.
 */
export type ChannelLiveState = 'live' | 'offline' | 'unknown';

// Independent flags, not a single role — a channel can be broadcaster-self, Twitch-moderator,
// 7TV-editor, any combination, or (7TV-editor-only) none of the Twitch roles at all.
export interface MyChannelDto {
  channelName: string;
  isBroadcaster: boolean;
  isModerator: boolean;
  isSevenTvEditor: boolean;
  isTracked: boolean;
  isBotActive: boolean;
  liveState: ChannelLiveState;
}

export interface MyChannelsResult {
  helixUnavailable: boolean;
  /**
   * Sharper than `helixUnavailable`: the backend's token store says only a fresh Twitch login can
   * restore Helix access (refresh token revoked/absent) — show a re-login prompt, not a generic
   * "Helix is down" note. Transient Helix failures keep this false.
   */
  reauthRequired: boolean;
  sevenTvUnavailable: boolean;
  channels: MyChannelDto[];
  /** When the live poll behind the `liveState` values ran; null when no snapshot exists. */
  livePolledAtUtc: string | null;
}
