namespace EmotePurge.Core.Services;

public interface ILiveCoverageService
{
    /// <summary>
    /// Credits <paramref name="minutes"/> of live coverage to <paramref name="dateUtc"/> for every
    /// live channel — one call per poll tick of the worker's Helix streams poll, each stream as its
    /// login and its Helix user id. Logins are normalized and matched to rows by name; logins without
    /// a channel row are skipped silently (the channel may have been purged between listing and poll).
    /// A stream whose user id is on the excluded-channel list or locked by its broadcaster (#245) is
    /// skipped too — decided on Helix's id, not on the row, so a row that does not carry its id yet
    /// counts nothing either. A day is clamped to 1440 minutes, so an occasional extra poll (worker
    /// restart) cannot push a day past its own length. Returns the number of channels actually credited.
    /// </summary>
    Task<int> AddLiveMinutesAsync(
        IReadOnlyCollection<(string Login, string UserId)> liveChannels, DateOnly dateUtc, int minutes, CancellationToken cancellationToken = default);
}
