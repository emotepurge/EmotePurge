using EmotePurge.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

/// <summary>
/// A broadcaster re-add lock (#245) for the length of one test, removed again on dispose.
/// <para>
/// Not optional hygiene: the Postgres collection shares one database, and the identity reconcile
/// scans every active channel in it. An active row whose id stays locked after its test would be
/// deactivated by whichever reconcile test runs next, adding a LEAVE that test never expected.
/// </para>
/// </summary>
public sealed class BroadcasterLockScope : IAsyncDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string _twitchChannelId;

    private BroadcasterLockScope(PostgresFixture fixture, string twitchChannelId, DateTime lockedAtUtc)
    {
        _fixture = fixture;
        _twitchChannelId = twitchChannelId;
        LockedAtUtc = lockedAtUtc;
    }

    public DateTime LockedAtUtc { get; }

    public static async Task<BroadcasterLockScope> CreateAsync(PostgresFixture fixture, string twitchChannelId, DateTime? lockedAtUtc = null)
    {
        // Whole microseconds: Postgres stores timestamptz at that precision, so a date read back
        // compares equal to the one written here.
        var at = lockedAtUtc ?? new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        await using var db = fixture.CreateDbContext();
        db.BroadcasterChannelLocks.Add(new BroadcasterChannelLock { TwitchChannelId = twitchChannelId, LockedAtUtc = at });
        await db.SaveChangesAsync();
        return new BroadcasterLockScope(fixture, twitchChannelId, at);
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = _fixture.CreateDbContext();
        await db.BroadcasterChannelLocks.Where(l => l.TwitchChannelId == _twitchChannelId).ExecuteDeleteAsync();
    }
}
