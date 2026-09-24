namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Decides whether a channel whose broadcaster objected to being processed (GDPR Art. 21, issue
/// #252) must be refused wherever a <see cref="EmotePurge.Core.Entities.Channel"/> row could be
/// newly created or reactivated for chat observation — the channel-scoped counterpart of the
/// per-chatter <c>IExcludedChatterFilter</c> in <c>EmotePurge.Worker</c>. Lives in Infrastructure,
/// not Worker, because both the Api (the join endpoint) and the Worker (identity reconcile/merge)
/// need it, and both already depend on this assembly through
/// <c>ServiceCollectionExtensions.AddEmotePurgeInfrastructure</c>.
/// </summary>
public interface IExcludedChannelFilter
{
    /// <summary>
    /// Never throws. Matches only the immutable numeric Twitch broadcaster id, never a login — a
    /// login can change, and <c>PurgeAsync</c> deletes the row a blocked id would otherwise be
    /// looked up under, so the id is the only thing that can still be checked against a rejoin
    /// attempt. A missing/empty <paramref name="twitchChannelId"/> just returns <c>false</c>.
    /// </summary>
    bool IsExcluded(string? twitchChannelId);
}
