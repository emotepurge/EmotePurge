using System.Diagnostics;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;

namespace EmotePurge.Infrastructure.Services;

public class TrackedEmoteSetMembershipService(
    AppDbContext db,
    ISevenTvEmoteSetListService emoteSetListService) : ITrackedEmoteSetMembershipService
{
    public async Task<TrackedEmoteSetMembership> CheckAsync(
        string channelName, string emoteSetId, CancellationToken cancellationToken = default)
    {
        // Deliberately neither IsBotActive nor the exclusion list: a deactivated channel is still
        // served by the sibling dropdown route and by vote-session creation until retention deletes it.
        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return TrackedEmoteSetMembership.ChannelNotFound;
        }

        // The active set needs no 7TV round trip: it is our own observed state. An empty id (never
        // synced) is never a match, so the list alone decides then.
        if (!string.IsNullOrEmpty(channel.ActiveEmoteSetId)
            && string.Equals(channel.ActiveEmoteSetId, emoteSetId, StringComparison.Ordinal))
        {
            return TrackedEmoteSetMembership.Member;
        }

        if (channel.TwitchChannelId is null)
        {
            // No sync has resolved a Twitch identity, so there is nothing to ask 7TV about.
            return TrackedEmoteSetMembership.NotMember;
        }

        var setListResult = await emoteSetListService.ListByTwitchIdAsync(channel.TwitchChannelId, cancellationToken);
        switch (setListResult.Status)
        {
            case EmoteSetListStatus.Ok:
                return EmoteSetMembershipRule.BelongsToChannel(setListResult.List!, channel.ActiveEmoteSetId, emoteSetId)
                    ? TrackedEmoteSetMembership.Member
                    : TrackedEmoteSetMembership.NotMember;
            case EmoteSetListStatus.NoSevenTvAccount:
                // An answer, not a failure — but one that leaves no set for the id to belong to.
                return TrackedEmoteSetMembership.NotMember;
            case EmoteSetListStatus.RateLimited:
            case EmoteSetListStatus.Unavailable:
            case EmoteSetListStatus.BudgetExhausted:
                return TrackedEmoteSetMembership.SevenTvUnavailable;
            default:
                throw new UnreachableException(
                    $"Unexpected {nameof(EmoteSetListStatus)} value: {setListResult.Status}.");
        }
    }
}
