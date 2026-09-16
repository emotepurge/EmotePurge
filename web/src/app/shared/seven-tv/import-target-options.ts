import { normalizeChannelName } from '../../core/channels/channel-name';
import { MyChannelsResult } from '../../core/channels/channel.model';

/** One selectable row of the target picker's channel radiogroup. */
export interface ImportTargetOption {
  channelName: string;
  /** True when EmotePurge does not track this channel yet — the row still renders (so the user
   *  understands why it cannot be chosen) but its radio is disabled. */
  disabled: boolean;
}

/**
 * The channels a copy can land in: broadcaster or 7TV-editor role (moderator alone is not enough —
 * ADD is a 7TV write, not a chat privilege), excluding the channel the picker was opened *from*
 * (compared normalized — Regel 9, Twitch logins are commonly typed with capitals like
 * "HandOfBlood"), sorted ordinally by name (plain code-unit comparison — these are login names, not
 * display text with a locale-specific order). `MyChannelsResult.channels` carries every channel the
 * account has *some* relationship with; a "current channel" that would otherwise show up (e.g. the
 * user is also a 7TV editor of it under a different case) is still excluded correctly because the
 * comparison normalizes both sides.
 */
export function importTargetOptions(
  result: MyChannelsResult,
  currentChannelName: string,
): ImportTargetOption[] {
  const current = normalizeChannelName(currentChannelName);

  return result.channels
    .filter((channel) => channel.isBroadcaster || channel.isSevenTvEditor)
    .filter((channel) => normalizeChannelName(channel.channelName) !== current)
    .map((channel) => ({ channelName: channel.channelName, disabled: !channel.isTracked }))
    .sort(compareChannelNames);
}

/** Ordinal (code-unit) comparison — these are login names, not locale-sensitive display text. */
function compareChannelNames(a: ImportTargetOption, b: ImportTargetOption): number {
  if (a.channelName < b.channelName) {
    return -1;
  }
  if (a.channelName > b.channelName) {
    return 1;
  }
  return 0;
}
