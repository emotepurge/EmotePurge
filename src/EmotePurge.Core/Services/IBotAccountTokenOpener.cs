namespace EmotePurge.Core.Services;

/// <summary>
/// Opens a seal written by <see cref="IBotAccountTokenSealer"/> (spec 7.1, E7). Registered in the worker only, and only
/// while <c>BotAccount:Enabled</c> is on: it holds the private half of the token key pair.
/// </summary>
public interface IBotAccountTokenOpener
{
    /// <summary>
    /// Returns the token, or <c>null</c> for anything that is not an intact seal for this key: an unknown prefix, a
    /// wrong length, a flipped byte, another key. Never throws, so no exception can carry seal or token content (AK72).
    /// </summary>
    string? TryOpen(string? sealedToken);
}
