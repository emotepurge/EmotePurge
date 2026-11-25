namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Bound from <c>Channels:JoinHealth</c>, read once at startup and validated eagerly like
/// <see cref="ChannelCapacityOptions"/>.
/// </summary>
public sealed class ChannelJoinHealthOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Channels:JoinHealth";

    /// <summary>Lower bound of <see cref="GraceSeconds"/>.</summary>
    public const int MinGraceSeconds = 60;

    /// <summary>Upper bound of <see cref="GraceSeconds"/>.</summary>
    public const int MaxGraceSeconds = 3600;

    /// <summary>
    /// Connected seconds a wished JOIN may stay unconfirmed before it counts as overdue. 300 covers
    /// two rejoin rounds at 120 channels (about 2 x 112 s); see DECISIONS "Join-failure detection".
    /// </summary>
    public int GraceSeconds { get; set; } = 300;

    /// <summary>Throws unless <see cref="GraceSeconds"/> is within 60 to 3600.</summary>
    public void Validate()
    {
        if (GraceSeconds is < MinGraceSeconds or > MaxGraceSeconds)
        {
            throw new InvalidOperationException(
                $"Invalid join health configuration: '{SectionName}:{nameof(GraceSeconds)}' must be between {MinGraceSeconds} and {MaxGraceSeconds}, but is {GraceSeconds}.");
        }
    }
}
