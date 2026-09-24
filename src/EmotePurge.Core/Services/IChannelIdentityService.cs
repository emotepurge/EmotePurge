using EmotePurge.Core.Twitch;

namespace EmotePurge.Core.Services;

/// <summary>
/// What Twitch had to say about one login or id. Three states rather than a nullable identity,
/// because "Twitch has no such account" and "we could not ask Twitch" must never collapse into one
/// answer: the first is a fact a caller may act on, the second is the absence of a fact. Helix
/// signals the difference itself — an empty <c>data</c> array in a successful response versus a
/// failed request (see <see cref="ITwitchHelixClient.GetUsersAsync"/>).
/// </summary>
public enum TwitchUserLookupStatus
{
    Found,
    NotFound,
    Unavailable
}

/// <summary>
/// <see cref="User"/> is non-null if and only if <see cref="Status"/> is
/// <see cref="TwitchUserLookupStatus.Found"/>, and the two factories below are the only way to build
/// one at all — so that invariant cannot be broken at a call site, not even by accident.
/// <para>
/// A sealed class with a private constructor rather than a record, for the same reason as
/// <see cref="ChannelJoinResult"/>: a record's public positional constructor and <c>with</c> would
/// leave <c>Failed(TwitchUserLookupStatus.Found)</c> and <c>lookup with { User = null }</c> open, and
/// a "found" lookup without an identity is exactly the value every caller of this type assumes
/// cannot exist.
/// </para>
/// </summary>
public sealed class TwitchUserLookup
{
    private TwitchUserLookup(TwitchUserLookupStatus status, TwitchUserIdentity? user)
    {
        Status = status;
        User = user;
    }

    public TwitchUserLookupStatus Status { get; }

    /// <summary>Non-null if and only if <see cref="Status"/> is <see cref="TwitchUserLookupStatus.Found"/>.</summary>
    public TwitchUserIdentity? User { get; }

    public static TwitchUserLookup Found(TwitchUserIdentity user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new TwitchUserLookup(TwitchUserLookupStatus.Found, user);
    }

    /// <summary>
    /// Builds a lookup that produced no identity. Rejects a success status outright — see
    /// <see cref="ChannelJoinResult.Failed"/> for the reasoning; a future success status has to be
    /// added to the guard below in the same commit that adds it to the enum.
    /// </summary>
    public static TwitchUserLookup Failed(TwitchUserLookupStatus status)
    {
        if (status == TwitchUserLookupStatus.Found)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "TwitchUserLookup.Failed() kann keinen Erfolgsstatus tragen — für Found ist TwitchUserLookup.Found(user) zuständig.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), status, "Unbekannter TwitchUserLookupStatus.");
        }

        return new TwitchUserLookup(status, null);
    }
}

/// <summary>
/// What one reconcile pass did, for the worker's log line. Every counter is a write except
/// <see cref="Checked"/> (rows examined) and <see cref="LoginsMissing"/> (rows Twitch no longer
/// knows, under either their login or their id — deliberately counted but never acted on).
/// <para>
/// <see cref="LoginsMissing"/> deliberately stays *one* number over both of those shapes — an
/// id-less row whose login Helix does not know, and a row whose stored id resolves to nothing. They
/// are the same fact for the reader ("Twitch does not know this channel any more") and neither is
/// acted on, so splitting the field would only invite a caller to treat them differently. What must
/// not happen instead is a log line that names only one of the two; the log line spells out both.
/// </para>
/// <para>
/// <see cref="Deactivated"/> (issue #260, this revision) counts an active row the pass stopped
/// observing because its Twitch id — known already, or just resolved by the row's login — is on
/// <c>Channels:ExcludedChannelIds</c>: the objection gate that would otherwise let a rename or a
/// merge bring it back into observation. It is a write, not a no-op case like
/// <see cref="LoginsMissing"/>, and it never carries an id or a login into the log for the same
/// reason the gate itself does not.
/// </para>
/// </summary>
public record ChannelIdentityReconcileSummary(
    int Checked,
    int IdsBackfilled,
    int Renamed,
    int Merged,
    int MergesRefused,
    int LoginsMissing,
    int Deactivated);

/// <summary>
/// Keeps the stored channel rows in step with Twitch's own view of who they are. The immutable
/// Twitch id is the channel's identity; the login is a display name that its owner may change at
/// any time, and Twitch does not tell anyone when they do — so the only way to notice is to ask.
/// </summary>
public interface IChannelIdentityService
{
    /// <summary>
    /// Asks Helix about every active channel in one request and brings the rows in line: backfills
    /// missing ids, follows renames, and merges the duplicate row a rename can leave behind.
    /// <para>
    /// <c>null</c> means the tick was skipped without writing anything — no app token, or Helix
    /// unreachable. That is deliberately not an empty summary: "nothing needed doing" and "we never
    /// found out" are different states, and only the second one is worth retrying immediately.
    /// </para>
    /// </summary>
    Task<ChannelIdentityReconcileSummary?> ReconcileActiveChannelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolves one login to its Twitch identity — the app-token handling and the Helix call the
    /// join path needs, in one place. Callers must treat
    /// <see cref="TwitchUserLookupStatus.Unavailable"/> as "carry on as before" rather than as a
    /// rejection: an outage on our side is not evidence about the channel.
    /// </summary>
    Task<TwitchUserLookup> LookupByLoginAsync(string login, CancellationToken ct = default);
}
