namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Bound from configuration section <c>SevenTv:*</c> (only the two keys below; the section is shared
/// with the resync and EventAPI settings). Read once at startup.
/// </summary>
public sealed class EmptySetConfirmationOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "SevenTv";

    /// <summary>
    /// How many consecutive, spaced syncs must each report zero emotes for the same set before the
    /// zero is believed. Default 3. 1 switches the guard off.
    /// </summary>
    public int EmptySetConfirmations { get; set; } = 3;

    /// <summary>
    /// Minimum time between two zeros that both count toward the streak. Default 45 s, just below the
    /// 60 s periodic resync, so each periodic tick counts once while a burst of event-driven syncs
    /// within a few seconds counts once in total.
    /// </summary>
    public int EmptySetConfirmationSpacingSeconds { get; set; } = 45;

    /// <summary>Throws unless both values are usable; called at startup like the other options.</summary>
    public void Validate()
    {
        if (EmptySetConfirmations < 1)
        {
            throw new InvalidOperationException(
                $"Invalid 7TV configuration: '{SectionName}:{nameof(EmptySetConfirmations)}' must be at least 1, but is {EmptySetConfirmations}.");
        }

        if (EmptySetConfirmationSpacingSeconds < 0)
        {
            throw new InvalidOperationException(
                $"Invalid 7TV configuration: '{SectionName}:{nameof(EmptySetConfirmationSpacingSeconds)}' must not be negative, but is {EmptySetConfirmationSpacingSeconds}.");
        }
    }
}
