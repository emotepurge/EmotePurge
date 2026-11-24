using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// Bound from configuration section <c>Wishes:*</c>: a plain POCO, bound and validated once at startup
/// and registered as a singleton. It lives in Core because K2 reads it from there. Each process reads its
/// own environment; for the chat capture the Worker's value is the one that counts (plan #486 2.3).
/// </summary>
public sealed class WishOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Wishes";

    /// <summary>
    /// The feature switch. <c>false</c> by default: the wish routes answer 404 <c>wishes_disabled</c>
    /// and <c>wishesEnabled</c> in <c>/permissions</c> is false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Minutes between two runs of the expiry worker (1 to 1440).</summary>
    public int ExpiryIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// The pilot list (spec E8): numeric Twitch channel ids, comma-separated, or <c>*</c> for every channel; empty
    /// means no channel. It releases the bot mode for wishes only. Evaluate it through <see cref="PilotList"/>.
    /// </summary>
    public string? PilotChannelIds { get; set; }

    /// <summary>The parsed <see cref="PilotChannelIds"/>; throws on an invalid entry.</summary>
    public WishPilotList PilotList => WishPilotList.Parse(PilotChannelIds);

    /// <summary>The chat capture, <c>Wishes:Capture:*</c> (K6).</summary>
    public WishCaptureOptions Capture { get; set; } = new();

    /// <summary>
    /// Whether this process captures (Worker) respectively offers the reward routes (Api): only together
    /// with the feature switch.
    /// </summary>
    public bool CaptureActive => Enabled && Capture.Enabled;

    /// <summary>
    /// Throws unless every value is usable. Called during startup, so a typo in an environment variable
    /// stops the container with a readable message.
    /// </summary>
    public void Validate()
    {
        _ = PilotList;

        if (ExpiryIntervalMinutes is < 1 or > 1440)
        {
            throw new InvalidOperationException(
                $"Wishes:ExpiryIntervalMinutes must be between 1 and 1440, but is {ExpiryIntervalMinutes}.");
        }

        if (Capture.MaxPendingPerRequester is < WishLimits.MaxPendingPerRequesterMin or > WishLimits.MaxPendingPerRequesterMax)
        {
            throw new InvalidOperationException(
                $"Wishes:Capture:MaxPendingPerRequester must be between {WishLimits.MaxPendingPerRequesterMin} and "
                + $"{WishLimits.MaxPendingPerRequesterMax}, but is {Capture.MaxPendingPerRequester}.");
        }
    }

    /// <summary>Bound from <c>Wishes:Capture:*</c>.</summary>
    public sealed class WishCaptureOptions
    {
        /// <summary>The capture switch, <c>false</c> by default; effective only with <see cref="WishOptions.Enabled"/>.</summary>
        public bool Enabled { get; set; }

        /// <summary>Open wishes per viewer and channel (1 to 50, default 3).</summary>
        public int MaxPendingPerRequester { get; set; } = WishLimits.DefaultMaxPendingPerRequester;
    }
}
