using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The Name tie of the D2 order (plan #373 E5, 6.4). The Testcontainers image (alpine, musl) compares
// bytewise by default, which would let an ORDER BY "Name" without COLLATE "C" pass unnoticed; so
// EmoteSlots.Name lives on an ICU collation in a database of its own, where 'alpha' sorts before
// 'Bravo'. The rule decides ordinally ('Bravo' first) whatever the column says.
[Collection("Postgres")]
public class BaseSlotRuleOrdinalOrderTests(PostgresFixture fixture)
{
    private static readonly DateTime Now = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RulesAAndB_BreakANameTieOrdinally_NotInTheColumnCollation()
    {
        var databaseName = $"baseslot_ordinal_{Guid.NewGuid():N}";
        await using (var admin = fixture.CreateDbContext())
        {
#pragma warning disable EF1002 // locally generated Guid, CREATE DATABASE cannot be parameterized
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {databaseName}");
#pragma warning restore EF1002
        }

        await using var db = fixture.CreateDbContext(databaseName);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "EmoteSlots" ALTER COLUMN "Name" TYPE text COLLATE "und-x-icu";""");

        var channel = new Channel { ChannelName = "baseslot_ordinal", IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        var emote = new Emote { ChannelId = channel.Id, SevenTvEmoteId = "7tv", Name = "Emote", ImageUrl = "i" };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        // Rule (a): two undated slots carrying rows of SETA (the state after an on-phase).
        var lowerA = new EmoteSlot { EmoteId = emote.Id, Name = "alpha" };
        var upperA = new EmoteSlot { EmoteId = emote.Id, Name = "Bravo" };
        // Rule (b): two row-less unsettled seeds a run of SETB points at.
        var lowerB = new EmoteSlot { EmoteId = emote.Id, Name = "alpha", SeedUnsettled = true };
        var upperB = new EmoteSlot { EmoteId = emote.Id, Name = "Bravo", SeedUnsettled = true };
        db.EmoteSlots.AddRange(lowerA, upperA, lowerB, upperB);
        await db.SaveChangesAsync();
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, EmoteSetId = "SETA", SlotId = lowerA.Id, Date = new DateOnly(2026, 9, 1), UseCount = 1 },
            new UsageStat { EmoteId = emote.Id, EmoteSetId = "SETA", SlotId = upperA.Id, Date = new DateOnly(2026, 9, 2), UseCount = 1 });
        var runs = new[] { lowerB, upperB }.Select(_ => new ChatLogBackfillRun
        {
            ChannelId = channel.Id,
            Status = ChatLogBackfillRunStatus.Completed,
            RequestedMonths = 1,
            WindowFrom = new DateOnly(2026, 8, 1),
            WindowTo = new DateOnly(2026, 9, 1),
            WeeksTotal = 5,
            EmoteSetId = "SETB",
            ArchiveBaseUrl = "https://logs.cyex.app/",
            RequestedByTwitchUserId = "42",
            RequestedByLogin = "ordinal",
            RequestedAtUtc = Now
        }).ToArray();
        db.ChatLogBackfillRuns.AddRange(runs);
        await db.SaveChangesAsync();
        db.ChatLogBackfillRunEmotes.AddRange(
            new ChatLogBackfillRunEmote { RunId = runs[0].Id, EmoteId = emote.Id, SevenTvEmoteId = "7tv", Name = "alpha", SlotId = lowerB.Id },
            new ChatLogBackfillRunEmote { RunId = runs[1].Id, EmoteId = emote.Id, SevenTvEmoteId = "7tv", Name = "Bravo", SlotId = upperB.Id });
        await db.SaveChangesAsync();

        // Sanity: the trap is armed. The column collation orders 'alpha' first; only COLLATE "C" gives
        // the D2 answer — so a rule that ordered in SQL without the clause would pick 'alpha' here.
        var ids = new[] { lowerA.Id, upperA.Id };
        Assert.Equal(lowerA.Id, await db.Database
            .SqlQuery<string>($"""SELECT "Id" AS "Value" FROM "EmoteSlots" WHERE "Id" = ANY({ids}) ORDER BY "Name" LIMIT 1""").SingleAsync());
        Assert.Equal(upperA.Id, await db.Database
            .SqlQuery<string>($"""SELECT "Id" AS "Value" FROM "EmoteSlots" WHERE "Id" = ANY({ids}) ORDER BY "Name" COLLATE "C" LIMIT 1""").SingleAsync());

        var found = await BaseSlotRule.FindAsync(db, [new(emote.Id, "SETA")], CancellationToken.None);
        Assert.Equal(upperA.Id, found[new(emote.Id, "SETA")]);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var result = await BaseSlotRule.LockAndGetOrCreateAsync(db, [new(emote.Id, "SETA"), new(emote.Id, "SETB")], Now, CancellationToken.None);
        Assert.Equal(upperA.Id, result.SlotIds[new(emote.Id, "SETA")]);
        Assert.Equal(upperB.Id, result.SlotIds[new(emote.Id, "SETB")]);
        Assert.Empty(result.Created);
    }
}
