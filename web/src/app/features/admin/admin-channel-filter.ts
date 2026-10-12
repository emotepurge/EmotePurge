import { AdminChannel } from '../../core/admin/admin.model';

export const CHANNEL_STATES = ['all', 'live', 'offline', 'inactive'] as const;

export type ChannelStateFilter = (typeof CHANNEL_STATES)[number];

/** A URL can carry anything; whatever is not a known state means "all". */
export function toChannelStateFilter(raw: string | null | undefined): ChannelStateFilter {
  return (CHANNEL_STATES as readonly string[]).includes(raw ?? '')
    ? (raw as ChannelStateFilter)
    : 'all';
}

type ChannelFacts = Pick<AdminChannel, 'isBotActive' | 'liveState'>;

/**
 * Live and offline only describe channels the bot is in; an inactive channel is just inactive,
 * whatever the last poll said. An active channel whose live state is still 'unknown' belongs to
 * "all" only — claiming it is offline would be the very guess the row badges avoid.
 */
export function matchesChannelState(channel: ChannelFacts, state: ChannelStateFilter): boolean {
  switch (state) {
    case 'all':
      return true;
    case 'inactive':
      return !channel.isBotActive;
    case 'live':
      return channel.isBotActive && channel.liveState === 'live';
    case 'offline':
      return channel.isBotActive && channel.liveState === 'offline';
  }
}

/** Trimmed, case-insensitive substring match on the channel name. An empty query matches all. */
export function matchesChannelSearch(channelName: string, query: string): boolean {
  const needle = query.trim().toLowerCase();
  return needle === '' || channelName.toLowerCase().includes(needle);
}

export function filterChannels<T extends ChannelFacts & { channelName: string }>(
  channels: readonly T[],
  state: ChannelStateFilter,
  query: string,
): T[] {
  return channels.filter(
    (channel) =>
      matchesChannelState(channel, state) && matchesChannelSearch(channel.channelName, query),
  );
}

/** A count of `null` means "not known", which the UI must show as such rather than as 0. */
export type ChannelStateCounts = Record<ChannelStateFilter, number | null>;

/**
 * Counts per state over *all* channels — deliberately independent of the search text, so the
 * segments describe the fleet and do not shrink while typing.
 *
 * Unknown rule: if at least one channel is active but none of the active ones has a known live
 * state (no poll snapshot yet), Live and Offline are `null`, not 0 — "0 live" would be a claim the
 * data cannot back. With no active channel at all, 0 is true and stays 0.
 */
export function countChannelStates(channels: readonly ChannelFacts[]): ChannelStateCounts {
  const active = channels.filter((channel) => channel.isBotActive);
  const liveKnown = active.some((channel) => channel.liveState !== 'unknown');
  const liveStateUnknown = active.length > 0 && !liveKnown;
  return {
    all: channels.length,
    live: liveStateUnknown ? null : channels.filter((c) => matchesChannelState(c, 'live')).length,
    offline: liveStateUnknown
      ? null
      : channels.filter((c) => matchesChannelState(c, 'offline')).length,
    inactive: channels.filter((c) => matchesChannelState(c, 'inactive')).length,
  };
}
