using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The shared placeholder-row insert (chat-log backfill spec, D27/D37) against real Postgres: Npgsql
// refuses a non-UTC DateTime for a timestamptz parameter, so the helper normalizes what callers hand it.
[Collection("Postgres")]
public class ArchivedEmoteRowUpsertTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EnsureRowsAsync_StoresEveryDateKindAsUtc_AndLeavesAnExistingRowAlone()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "archivedrowupsert_utc", IsBotActive = true };
        db.Channels.Add(channel);
        db.Emotes.Add(new Emote { ChannelId = channel.Id, SevenTvEmoteId = "existing1", Name = "Existing", ImageUrl = "kept" });
        await db.SaveChangesAsync();

        var utc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var local = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Unspecified);
        var now = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Unspecified);

        var created = await ArchivedEmoteRowUpsert.EnsureRowsAsync(
            db,
            channel.Id,
            [
                new ArchivedEmoteRow("utc1", "Utc", "u", utc),
                new ArchivedEmoteRow("local1", "Local", "l", local),
                new ArchivedEmoteRow("unspec1", "Unspecified", "s", unspecified),
                new ArchivedEmoteRow("null1", "Null", "n", null),
                new ArchivedEmoteRow("existing1", "Renamed", "changed", utc),
            ],
            now,
            CancellationToken.None);

        Assert.Equal(4, created);
        var rows = await db.Emotes.AsNoTracking().Where(e => e.ChannelId == channel.Id).ToDictionaryAsync(e => e.SevenTvEmoteId);
        Assert.Equal(utc, rows["utc1"].FirstSeenAt);
        Assert.Equal(local.ToUniversalTime(), rows["local1"].FirstSeenAt);
        Assert.Equal(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), rows["unspec1"].FirstSeenAt);
        Assert.Null(rows["null1"].FirstSeenAt);
        Assert.All(new[] { "utc1", "local1", "unspec1", "null1" }, id =>
        {
            var row = rows[id];
            Assert.Equal((true, true, (DateTime?)null), (row.IsArchived, row.IsPlaceholder, row.ArchivedAt));
            Assert.Equal(DateTime.SpecifyKind(now, DateTimeKind.Utc), row.LastEnteredSetAtUtc);
        });
        Assert.Equal(("Existing", "kept", false, false),
            (rows["existing1"].Name, rows["existing1"].ImageUrl, rows["existing1"].IsArchived, rows["existing1"].IsPlaceholder));
    }
}
