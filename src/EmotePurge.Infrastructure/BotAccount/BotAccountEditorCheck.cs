using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// <see cref="IBotAccountEditorCheck"/> over 7TV's public v4 read (<see cref="ISevenTvApiClient.GetEditorForAsync"/>, no
/// token, telemetry under <c>seventv-bot</c>). Registered scoped by the worker's
/// <see cref="BotAccountRegistration.AddBotAccountWriter"/> and resolved from the loop-long scope, so the one-minute reuse
/// lives as long as the loop. Not thread-safe, like the loop.
/// <para>
/// A read is cached per bot id and <c>IdentityVersion</c>: a hand <c>PUT</c> (new identity) never gets the read of the
/// previous one, a renewal swap (same identity, same bot id) keeps it. Only an available read is cached; after a failed
/// one the next caller asks 7TV again.
/// </para>
/// </summary>
public sealed class BotAccountEditorCheck(ISevenTvApiClient client, TimeProvider timeProvider) : IBotAccountEditorCheck
{
    /// <summary>How long one read answers for every channel (plan 2.5: one minute).</summary>
    public static readonly TimeSpan ReadLifetime = TimeSpan.FromMinutes(1);

    private readonly Dictionary<(string BotSevenTvUserId, int IdentityVersion), BotAccountEditorRead> _cache = [];

    public async Task<BotAccountEditorRead> ReadAsync(
        string botSevenTvUserId,
        int identityVersion,
        DateTime? startedAfterUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(botSevenTvUserId);

        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        var key = (botSevenTvUserId, identityVersion);
        if (_cache.TryGetValue(key, out var cached)
            && startedAt - cached.StartedAtUtc < ReadLifetime
            && (startedAfterUtc is not { } after || cached.StartedAtUtc > after))
        {
            return cached;
        }

        var result = await client.GetEditorForAsync(botSevenTvUserId, cancellationToken);
        if (result.Status != SevenTvLookupStatus.Ok)
        {
            return BotAccountEditorRead.Unavailable(botSevenTvUserId, identityVersion, startedAt);
        }

        // One Twitch id, one verdict: should 7TV ever list two owners with the same connection, the first entry counts.
        var byTwitchUserId = new Dictionary<string, BotAccountEditorEntry>(StringComparer.Ordinal);
        foreach (var entry in result.Entries!)
        {
            byTwitchUserId.TryAdd(
                entry.OwnerTwitchUserId,
                BotAccountEditorEntry.Of(entry.State, entry.ManagesEmoteSets, entry.OwnerSevenTvUserId));
        }

        var read = BotAccountEditorRead.Available(botSevenTvUserId, identityVersion, startedAt, byTwitchUserId);
        _cache[key] = read;
        return read;
    }
}
