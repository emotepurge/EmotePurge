namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Bound from configuration section <c>Tags:*</c>; a plain POCO registered as a singleton, the same
/// shape as <see cref="ChannelCapacityOptions"/>: read once at startup, so a changed value takes
/// effect on the next restart.
/// </summary>
public sealed class EmoteTagOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Tags";

    /// <summary>
    /// Whether the browser may offer the tag play-in and removal runs (#201 T-C, spec 12.1). Default
    /// <c>false</c>: the routes behind the runs exist either way, the flag only lets the frontend
    /// show its buttons, and it is delivered with the channel permissions. Switched on by the
    /// operator once the worker that writes leave observations has run a full sync.
    /// </summary>
    public bool RunsEnabled { get; set; }
}
