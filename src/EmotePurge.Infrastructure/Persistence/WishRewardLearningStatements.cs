using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// The conditional statements on <c>WishRewardLearnings</c> that more than one writer shares (K6, plan #486 2.4,
/// Sol-R2-P2-5). No caller reads the learning row, decides in memory and writes it by key: each statement names the
/// row it expects in its condition, and the number of rows it hit is the answer. A row past its
/// <c>ExpiresAtUtc</c> counts as absent everywhere; deleting it is the only change an expired row ever gets.
/// </summary>
internal static class WishRewardLearningStatements
{
    /// <summary>
    /// Deletes the channel's learning row only if it is still the session <paramref name="sessionId"/> a caller read
    /// and has expired at <paramref name="nowUtc"/>. A session started after that read survives (it has another id and
    /// an expiry in the future). Returns the rows deleted (0 or 1).
    /// </summary>
    public static Task<int> DeleteExpiredSessionAsync(
        this AppDbContext db, string channelId, Guid sessionId, DateTime nowUtc, CancellationToken cancellationToken) =>
        db.WishRewardLearnings
            .Where(l => l.ChannelId == channelId && l.SessionId == sessionId && l.ExpiresAtUtc <= nowUtc)
            .ExecuteDeleteAsync(cancellationToken);

    /// <summary>
    /// Deletes every learning row that has expired at <paramref name="nowUtc"/>, in every channel or, with
    /// <paramref name="channelId"/>, in that one: the condition is the expiry alone, which a session started at
    /// <paramref name="nowUtc"/> or later never meets. Used by a start behind the channel lock and by the capture's
    /// housekeeping, which holds no lock. Returns the rows deleted.
    /// </summary>
    public static Task<int> DeleteExpiredLearningsAsync(
        this AppDbContext db, DateTime nowUtc, CancellationToken cancellationToken, string? channelId = null)
    {
        var expired = db.WishRewardLearnings.Where(l => l.ExpiresAtUtc <= nowUtc);
        if (channelId is not null)
        {
            expired = expired.Where(l => l.ChannelId == channelId);
        }

        return expired.ExecuteDeleteAsync(cancellationToken);
    }
}
