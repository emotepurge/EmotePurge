namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Bound from configuration section <c>Channels:*</c>. A plain POCO registered as a singleton, the
/// same shape as <c>ChatLogArchiveOptions</c> and <c>RateLimitingOptions</c> next to it: read once at
/// startup and never again, so a changed cap takes effect on the next restart, not live.
/// </summary>
public sealed class ChannelCapacityOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Channels";

    /// <summary>
    /// Hard ceiling on the number of channels with <c>Channel.IsBotActive == true</c> at once,
    /// enforced by <c>ChannelService.JoinAsync</c> against every transition into that state — a
    /// brand-new channel and reactivating a previously left one alike, but never against a join on a
    /// channel that is already active (that stays idempotent regardless of the cap). Default 80,
    /// comfortably below Twitch's own 100-simultaneously-joined-chatrooms-per-account limit (in force
    /// since 2024-05-15, CLAUDE.md "Bekannte offene Grenzen") so the rejection happens here, with a
    /// reason, instead of as a silent TwitchLib rejoin failure. Global admins are exempt from the
    /// cap — see the join path — but still count toward it.
    /// </summary>
    public int MaxActiveChannels { get; set; } = 80;

    /// <summary>
    /// Throws unless the cap is usable. Called during startup by <c>AddEmotePurgeInfrastructure</c>
    /// (both the Api and the Worker call it), so a typo in an environment variable stops the
    /// container with a readable message instead of silently handing every join a capacity of zero —
    /// which would not be a strict cap but a total outage of the join path.
    /// </summary>
    public void Validate()
    {
        if (MaxActiveChannels <= 0)
        {
            throw new InvalidOperationException(
                $"Invalid channel capacity configuration: '{SectionName}:{nameof(MaxActiveChannels)}' must be greater than 0, but is {MaxActiveChannels}.");
        }
    }
}
