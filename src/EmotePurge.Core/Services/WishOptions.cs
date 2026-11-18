namespace EmotePurge.Core.Services;

/// <summary>
/// Bound from configuration section <c>Wishes:*</c>: a plain POCO, bound and validated once at startup
/// and registered as a singleton. It lives in Core because K2 reads it from there. Later children add
/// keys (expiry interval, capture switch, pilot list); today the only key is the feature switch.
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

    /// <summary>
    /// Throws unless every value is usable. Called during startup, so a typo in an environment variable
    /// stops the container with a readable message. Nothing can be invalid yet: the only key is a bool.
    /// </summary>
    public void Validate()
    {
    }
}
