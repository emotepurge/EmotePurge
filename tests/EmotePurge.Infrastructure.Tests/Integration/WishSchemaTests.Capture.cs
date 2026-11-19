using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The schema contract of AddWishCapture (wish emotes, child 6, T1) against the migrated head schema: the
// emote placeholder CHECK of plan #486 2.8a with its transitions, the new CHECKs, keys and indexes of the
// five capture tables, the cascades and SET NULL, and the column shapes an image older than K6 relies on.
public partial class WishSchemaTests
{
    private const string PlaceholderCheck = "CK_Wishes_EmotePlaceholder";

    private static readonly string[] PlaceholderColumns = ["SevenTvEmoteId", "EmoteName", "Alias"];

    // Every state times every placeholder column: the empty string is allowed in pending, rejected and expired
    // only, for each of the three columns on its own.
    public static TheoryData<string, string> EveryStateAndPlaceholderColumn()
    {
        var data = new TheoryData<string, string>();
        foreach (var status in WishStatus.All)
        {
            foreach (var column in PlaceholderColumns)
            {
                data.Add(status, column);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryStateAndPlaceholderColumn))]
    public async Task EmptyEmoteField_IsAllowedOnlyInPendingRejectedAndExpired(string status, string column)
    {
        var seeded = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var wish = NewWish(seeded.ChannelId, EmoteId(), status);
        wish.EndedAtUtc = WishStatus.IsEnded(status) ? DateTime.UtcNow : null;
        Blank(wish, column);
        db.Wishes.Add(wish);

        if (status is WishStatus.Pending or WishStatus.Rejected or WishStatus.Expired)
        {
            await db.SaveChangesAsync();
            return;
        }

        await AssertViolatesAsync(() => db.SaveChangesAsync(), "23514", PlaceholderCheck);
    }

    [Fact]
    public async Task PlaceholderStates_AreExactlyTheThreeOfTheSpec()
    {
        Assert.Equal(
            [WishStatus.Pending, WishStatus.Rejected, WishStatus.Expired],
            WishStatus.All.Where(WishStatus.EmotePlaceholderAllowed.Contains));

        await using var db = fixture.CreateDbContext();
        var definition = await db.Database.SqlQueryRaw<string>(
            """
            SELECT pg_get_constraintdef(oid) AS "Value" FROM pg_constraint WHERE conname = {0}
            """, PlaceholderCheck).SingleAsync();
        Assert.Contains("'pending'", definition);
        Assert.Contains("'rejected'", definition);
        Assert.Contains("'expired'", definition);
    }

    // Two open wishes without an emote in one channel: the running index does not see them (pending is none of
    // its states), and a link wish may carry its id before the name and alias are known.
    [Fact]
    public async Task OpenWishesWithoutAnEmote_CoexistInOneChannel_BesideARunningWish()
    {
        var seeded = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        db.Wishes.Add(Placeholder(seeded.ChannelId));
        db.Wishes.Add(Placeholder(seeded.ChannelId));
        var link = Placeholder(seeded.ChannelId);
        link.SevenTvEmoteId = "01JLINKEDEMOTE0000000000AA";
        db.Wishes.Add(link);
        db.Wishes.Add(NewWish(seeded.ChannelId, "01JLINKEDEMOTE0000000000AA", WishStatus.Active));
        await db.SaveChangesAsync();

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(4, await verify.Wishes.CountAsync(w => w.ChannelId == seeded.ChannelId));
        Assert.Equal(2, await verify.Wishes.CountAsync(w => w.ChannelId == seeded.ChannelId && w.SevenTvEmoteId == ""));
    }

    // Plan 2.8a, "transitions out of a placeholder state": the change of state and the filling of the three
    // fields are one update. Leaving a field empty in a state outside the three fails; going back to pending
    // (unlink, date correction) may empty them again.
    [Theory]
    [InlineData(WishStatus.Pending, WishStatus.Active, true, true)]
    [InlineData(WishStatus.Pending, WishStatus.Kept, true, true)]
    [InlineData(WishStatus.Pending, WishStatus.Approved, true, true)]
    [InlineData(WishStatus.Pending, WishStatus.Merged, true, true)]
    [InlineData(WishStatus.Pending, WishStatus.Active, false, false)]
    [InlineData(WishStatus.Pending, WishStatus.Approved, false, false)]
    [InlineData(WishStatus.Pending, WishStatus.Merged, false, false)]
    [InlineData(WishStatus.Pending, WishStatus.Kept, false, false)]
    [InlineData(WishStatus.Pending, WishStatus.Rejected, false, true)]
    [InlineData(WishStatus.Pending, WishStatus.Expired, false, true)]
    [InlineData(WishStatus.Active, WishStatus.Pending, false, true)]
    public async Task ATransition_FillsTheEmoteFieldsInTheSameUpdate(string from, string to, bool fill, bool accepted)
    {
        var seeded = await SeedAsync();
        long wishId;
        await using (var db = fixture.CreateDbContext())
        {
            var wish = from == WishStatus.Pending ? Placeholder(seeded.ChannelId) : NewWish(seeded.ChannelId, EmoteId(), from);
            db.Wishes.Add(wish);
            await db.SaveChangesAsync();
            wishId = wish.Id;
        }

        var emoteId = fill ? EmoteId() : string.Empty;
        var name = fill ? "Emote" : string.Empty;
        await using var update = fixture.CreateDbContext();
        Task<int> Run() => update.Wishes
            .Where(w => w.Id == wishId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, to)
                .SetProperty(w => w.SevenTvEmoteId, emoteId)
                .SetProperty(w => w.EmoteName, name)
                .SetProperty(w => w.Alias, name));

        if (accepted)
        {
            Assert.Equal(1, await Run());
            return;
        }

        await AssertViolatesAsync(Run, "23514", PlaceholderCheck);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(WishMatchKind.Link, true)]
    [InlineData(WishMatchKind.Name, true)]
    [InlineData(WishMatchKind.None, true)]
    [InlineData("guess", false)]
    public async Task MatchKind_IsNullOrOneOfThree(string? matchKind, bool accepted)
    {
        var seeded = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var wish = Placeholder(seeded.ChannelId);
        wish.MatchKind = matchKind;
        db.Wishes.Add(wish);

        if (accepted)
        {
            await db.SaveChangesAsync();
            return;
        }

        await AssertViolatesAsync(() => db.SaveChangesAsync(), "23514", "CK_Wishes_MatchKind");
    }

    [Fact]
    public async Task IrcMessageId_IsUniquePerChannelWhileSet()
    {
        var seeded = await SeedAsync();
        var other = await SeedAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.Wishes.Add(Captured(seeded.ChannelId, "msg-1"));
            db.Wishes.Add(Captured(other.ChannelId, "msg-1"));
            // Manual and adopted wishes carry none; any number of them is fine.
            db.Wishes.Add(NewWish(seeded.ChannelId, EmoteId(), WishStatus.Approved));
            db.Wishes.Add(NewWish(seeded.ChannelId, EmoteId(), WishStatus.Approved));
            await db.SaveChangesAsync();
        }

        await using var attempt = fixture.CreateDbContext();
        attempt.Wishes.Add(Captured(seeded.ChannelId, "msg-1"));
        await AssertViolatesAsync(() => attempt.SaveChangesAsync(), "23505", "IX_Wishes_ChannelId_IrcMessageId");
    }

    [Fact]
    public async Task ForgettingAReward_NullsRewardId_AndKeepsTheWish()
    {
        var seeded = await SeedAsync();
        long wishId;
        long rewardRowId;
        await using (var db = fixture.CreateDbContext())
        {
            var reward = NewReward(seeded.ChannelId, "reward-a");
            db.ChannelWishRewards.Add(reward);
            await db.SaveChangesAsync();
            var wish = Captured(seeded.ChannelId, "msg-r");
            wish.RewardId = reward.Id;
            db.Wishes.Add(wish);
            await db.SaveChangesAsync();
            (wishId, rewardRowId) = (wish.Id, reward.Id);
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.ChannelWishRewards.Where(r => r.Id == rewardRowId).ExecuteDeleteAsync());
        }

        await using var verify = fixture.CreateDbContext();
        var kept = await verify.Wishes.SingleAsync(w => w.Id == wishId);
        Assert.Null(kept.RewardId);
        Assert.Equal(WishStatus.Pending, kept.Status);
    }

    [Fact]
    public async Task RewardId_IsUniquePerChannel_NotAcrossChannels()
    {
        var seeded = await SeedAsync();
        var other = await SeedAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.ChannelWishRewards.Add(NewReward(seeded.ChannelId, "same-reward"));
            db.ChannelWishRewards.Add(NewReward(other.ChannelId, "same-reward"));
            await db.SaveChangesAsync();
        }

        await using var attempt = fixture.CreateDbContext();
        attempt.ChannelWishRewards.Add(NewReward(seeded.ChannelId, "same-reward"));
        await AssertViolatesAsync(() => attempt.SaveChangesAsync(), "23505", "IX_ChannelWishRewards_ChannelId_RewardId");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(365, true)]
    [InlineData(0, false)]
    [InlineData(366, false)]
    public async Task RewardDefaultDuration_IsOneToThreeHundredSixtyFive(int days, bool accepted)
    {
        var seeded = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var reward = NewReward(seeded.ChannelId, "reward-d");
        reward.DefaultDurationDays = days;
        db.ChannelWishRewards.Add(reward);

        if (accepted)
        {
            await db.SaveChangesAsync();
            return;
        }

        await AssertViolatesAsync(() => db.SaveChangesAsync(), "23514", "CK_ChannelWishRewards_DefaultDurationDays");
    }

    // Written as raw SQL: EF leaves a value equal to the CLR default out of the INSERT once the column has a
    // database default, so 0 would never reach the CHECK through the model.
    [Theory]
    [InlineData(1, true)]
    [InlineData(90, true)]
    [InlineData(0, false)]
    [InlineData(91, false)]
    public async Task PendingExpiryDays_IsOneToNinety(int days, bool accepted)
    {
        var seeded = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        Task<int> Insert() => db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ChannelWishSettings" ("ChannelId", "DefaultDurationDays", "ProbationLeadDays", "BotModeEnabled", "PendingExpiryDays")
            VALUES ({0}, 30, 7, false, {1});
            """, seeded.ChannelId, days);

        if (accepted)
        {
            Assert.Equal(1, await Insert());
            return;
        }

        await AssertViolatesAsync(Insert, "23514", "CK_ChannelWishSettings_PendingExpiryDays");
    }

    // The insert an image older than K6 makes: the column is not named, so only the database default fills it.
    [Fact]
    public async Task ASettingsRowWithoutPendingExpiryDays_GetsFourteen()
    {
        var seeded = await SeedAsync();
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "ChannelWishSettings" ("ChannelId", "DefaultDurationDays", "ProbationLeadDays", "BotModeEnabled")
                VALUES ({0}, 30, 7, false);
                """, seeded.ChannelId);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(14, (await verify.ChannelWishSettings.SingleAsync(s => s.ChannelId == seeded.ChannelId)).PendingExpiryDays);
        Assert.Equal(14, new ChannelWishSettings().PendingExpiryDays);
    }

    [Fact]
    public async Task Learning_IsOneRowPerChannel()
    {
        var seeded = await SeedAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.WishRewardLearnings.Add(NewLearning(seeded.ChannelId));
            await db.SaveChangesAsync();
        }

        await using var attempt = fixture.CreateDbContext();
        attempt.WishRewardLearnings.Add(NewLearning(seeded.ChannelId));
        await AssertViolatesAsync(() => attempt.SaveChangesAsync(), "23505", "PK_WishRewardLearnings");
    }

    // A candidate is all five fields or none; an empty text is still a text.
    [Theory]
    [InlineData(null, true)]
    [InlineData("all", true)]
    [InlineData("emptyText", true)]
    [InlineData("CandidateId", false)]
    [InlineData("CandidateRewardId", false)]
    [InlineData("CandidateText", false)]
    [InlineData("CandidateLogin", false)]
    [InlineData("CandidateSeenAtUtc", false)]
    public async Task LearningCandidate_IsAllFieldsOrNone(string? shape, bool accepted)
    {
        var seeded = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var learning = NewLearning(seeded.ChannelId);
        if (shape is not null)
        {
            learning.CandidateId = Guid.NewGuid();
            learning.CandidateRewardId = "reward-c";
            learning.CandidateText = shape == "emptyText" ? string.Empty : "catJAM please";
            learning.CandidateLogin = "viewer";
            learning.CandidateSeenAtUtc = DateTime.UtcNow;
            switch (shape)
            {
                case "CandidateId": learning.CandidateId = null; break;
                case "CandidateRewardId": learning.CandidateRewardId = null; break;
                case "CandidateText": learning.CandidateText = null; break;
                case "CandidateLogin": learning.CandidateLogin = null; break;
                case "CandidateSeenAtUtc": learning.CandidateSeenAtUtc = null; break;
            }
        }

        db.WishRewardLearnings.Add(learning);
        if (accepted)
        {
            await db.SaveChangesAsync();
            return;
        }

        await AssertViolatesAsync(() => db.SaveChangesAsync(), "23514", "CK_WishRewardLearnings_Candidate");
    }

    [Theory]
    [InlineData(WishCaptureGapReason.Disconnect, true)]
    [InlineData(WishCaptureGapReason.WorkerRestart, true)]
    [InlineData(WishCaptureGapReason.NotJoined, true)]
    [InlineData("outage", false)]
    public async Task GapReason_IsOneOfThree(string reason, bool accepted)
    {
        var seeded = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        db.WishCaptureGaps.Add(NewGap(seeded.ChannelId, reason));

        if (accepted)
        {
            await db.SaveChangesAsync();
            return;
        }

        await AssertViolatesAsync(() => db.SaveChangesAsync(), "23514", "CK_WishCaptureGaps_Reason");
    }

    // The state row is global; the test writes inside a transaction it rolls back, so no other test sees a stamp.
    [Fact]
    public async Task CaptureState_AcceptsOnlyTheSingletonKey()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.WishCaptureState.Add(new WishCaptureState { LastAliveAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.WishCaptureState.Select(s => s.Id).SingleAsync());

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            """INSERT INTO "WishCaptureState" ("Id", "LastAliveAtUtc") VALUES (2, now());"""));
        Assert.Equal(("23514", "CK_WishCaptureState_Singleton"), (ex.SqlState, ex.ConstraintName));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Receipt_IsUniquePerChannelAndMessage()
    {
        var seeded = await SeedAsync();
        var other = await SeedAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.WishCaptureReceipts.Add(NewReceipt(seeded.ChannelId, "msg-x"));
            db.WishCaptureReceipts.Add(NewReceipt(other.ChannelId, "msg-x"));
            await db.SaveChangesAsync();
        }

        await using var attempt = fixture.CreateDbContext();
        attempt.WishCaptureReceipts.Add(NewReceipt(seeded.ChannelId, "msg-x"));
        await AssertViolatesAsync(() => attempt.SaveChangesAsync(), "23505", "PK_WishCaptureReceipts");
    }

    // No foreign key to the slot: the exclusion names a slot that is archived, deleted or never existed, and goes
    // only with its wish.
    [Fact]
    public async Task Exclusion_OutlivesItsSlot_AndGoesWithItsWish()
    {
        var seeded = await SeedAsync();
        long wishId;
        await using (var db = fixture.CreateDbContext())
        {
            var wish = Captured(seeded.ChannelId, "msg-e");
            db.Wishes.Add(wish);
            await db.SaveChangesAsync();
            wishId = wish.Id;
            db.WishSlotExclusions.Add(new WishSlotExclusion { WishId = wishId, SlotId = seeded.SlotId, CreatedAtUtc = DateTime.UtcNow });
            db.WishSlotExclusions.Add(new WishSlotExclusion { WishId = wishId, SlotId = "no-such-slot", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await using (var duplicate = fixture.CreateDbContext())
        {
            duplicate.WishSlotExclusions.Add(new WishSlotExclusion { WishId = wishId, SlotId = seeded.SlotId, CreatedAtUtc = DateTime.UtcNow });
            await AssertViolatesAsync(() => duplicate.SaveChangesAsync(), "23505", "PK_WishSlotExclusions");
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.EmoteSlots.Where(s => s.Id == seeded.SlotId).ExecuteDeleteAsync());
            Assert.Equal(2, await db.WishSlotExclusions.CountAsync(x => x.WishId == wishId));
            Assert.Equal(1, await db.Wishes.Where(w => w.Id == wishId).ExecuteDeleteAsync());
        }

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.WishSlotExclusions.AnyAsync(x => x.WishId == wishId));
    }

    [Fact]
    public async Task DeletingTheChannel_TakesEveryCaptureTableAlong()
    {
        var seeded = await SeedAsync();
        long wishId;
        await using (var db = fixture.CreateDbContext())
        {
            var reward = NewReward(seeded.ChannelId, "reward-p");
            db.ChannelWishRewards.Add(reward);
            await db.SaveChangesAsync();
            var wish = Captured(seeded.ChannelId, "msg-p");
            wish.RewardId = reward.Id;
            db.Wishes.Add(wish);
            db.WishRewardLearnings.Add(NewLearning(seeded.ChannelId));
            db.WishCaptureGaps.Add(NewGap(seeded.ChannelId, WishCaptureGapReason.Disconnect));
            db.WishCaptureReceipts.Add(NewReceipt(seeded.ChannelId, "msg-p"));
            await db.SaveChangesAsync();
            wishId = wish.Id;
            db.WishSlotExclusions.Add(new WishSlotExclusion { WishId = wishId, SlotId = seeded.SlotId, CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Channels" WHERE "Id" = {0};""", seeded.ChannelId));
        }

        await using var verify = fixture.CreateDbContext();
        var id = seeded.ChannelId;
        Assert.False(await verify.Wishes.AnyAsync(w => w.ChannelId == id));
        Assert.False(await verify.ChannelWishRewards.AnyAsync(r => r.ChannelId == id));
        Assert.False(await verify.WishRewardLearnings.AnyAsync(l => l.ChannelId == id));
        Assert.False(await verify.WishCaptureGaps.AnyAsync(g => g.ChannelId == id));
        Assert.False(await verify.WishCaptureReceipts.AnyAsync(r => r.ChannelId == id));
        Assert.False(await verify.WishSlotExclusions.AnyAsync(x => x.WishId == wishId));
    }

    // Every column K6 adds to Wishes is nullable without a default, so an older image inserting without them is
    // unaffected; the placeholder columns keep NOT NULL.
    [Theory]
    [InlineData("RewardId", "YES|")]
    [InlineData("IrcMessageId", "YES|")]
    [InlineData("RequestText", "YES|")]
    [InlineData("RequestedName", "YES|")]
    [InlineData("MatchKind", "YES|")]
    [InlineData("SevenTvEmoteId", "NO|")]
    [InlineData("EmoteName", "NO|")]
    [InlineData("Alias", "NO|")]
    public async Task WishColumns_KeepTheShapeAnOlderImageReads(string column, string expected)
    {
        await using var db = fixture.CreateDbContext();
        var rows = await db.Database.SqlQueryRaw<string>(
            """
            SELECT is_nullable || '|' || coalesce(column_default, '') AS "Value"
            FROM information_schema.columns
            WHERE table_name = 'Wishes' AND column_name = {0}
            """, column).ToListAsync();

        Assert.Equal([expected], rows);
    }

    [Fact]
    public async Task CaptureIndexes_AreThePlannedOnes()
    {
        await using var db = fixture.CreateDbContext();
        var indexes = await db.Database.SqlQueryRaw<string>(
            """
            SELECT indexdef AS "Value" FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename IN ('Wishes', 'ChannelWishRewards', 'WishCaptureGaps', 'WishCaptureReceipts')
            ORDER BY indexname
            """).ToListAsync();

        Assert.Contains(indexes, d => d.Contains("UNIQUE") && d.Contains("(\"ChannelId\", \"IrcMessageId\") WHERE (\"IrcMessageId\" IS NOT NULL)"));
        Assert.Contains(indexes, d => d.Contains("IX_Wishes_RewardId") && d.Contains("(\"RewardId\")"));
        Assert.Contains(indexes, d => d.Contains("UNIQUE") && d.Contains("\"ChannelWishRewards\"") && d.Contains("(\"ChannelId\", \"RewardId\")"));
        Assert.Contains(indexes, d => d.Contains("\"WishCaptureGaps\"") && d.Contains("(\"ChannelId\", \"FromUtc\")"));
        Assert.Contains(indexes, d => d.Contains("\"WishCaptureReceipts\"") && d.Contains("(\"SeenAtUtc\")"));
        Assert.Contains(indexes, d => d.Contains("PK_WishCaptureReceipts") && d.Contains("(\"ChannelId\", \"IrcMessageId\")"));
    }

    [Fact]
    public void HasEmote_IsFalseExactlyForThePlaceholder()
    {
        Assert.False(new Wish().HasEmote);
        Assert.False(new Wish { SevenTvEmoteId = string.Empty, EmoteName = "x", Alias = "x" }.HasEmote);
        Assert.True(new Wish { SevenTvEmoteId = "01JLINKEDEMOTE0000000000AA" }.HasEmote);
    }

    private static async Task AssertViolatesAsync(Func<Task> action, string sqlState, string constraint)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(action);
        var postgres = ex as PostgresException ?? Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal((sqlState, constraint), (postgres.SqlState, postgres.ConstraintName));
    }

    private static void Blank(Wish wish, string column)
    {
        switch (column)
        {
            case "SevenTvEmoteId": wish.SevenTvEmoteId = string.Empty; break;
            case "EmoteName": wish.EmoteName = string.Empty; break;
            case "Alias": wish.Alias = string.Empty; break;
            default: throw new ArgumentOutOfRangeException(nameof(column), column, null);
        }
    }

    private static string EmoteId() => Guid.NewGuid().ToString("N")[..24];

    // An open wish captured from chat whose text named no emote: all three fields are the placeholder.
    private static Wish Placeholder(string channelId)
    {
        var wish = NewWish(channelId, string.Empty, WishStatus.Pending);
        wish.EmoteName = string.Empty;
        wish.Alias = string.Empty;
        wish.Source = WishSource.Irc;
        wish.MatchKind = WishMatchKind.None;
        wish.StatusReason = WishStatusReason.Unparsed;
        return wish;
    }

    private static Wish Captured(string channelId, string ircMessageId)
    {
        var wish = Placeholder(channelId);
        wish.IrcMessageId = ircMessageId;
        wish.RequestText = "please add something";
        wish.RequesterTwitchUserId = "123456";
        wish.RequesterLogin = "viewer";
        return wish;
    }

    private static ChannelWishReward NewReward(string channelId, string rewardId) => new()
    {
        ChannelId = channelId,
        RewardId = rewardId,
        Label = "Emote wish",
        CreatedAtUtc = DateTime.UtcNow
    };

    private static WishRewardLearning NewLearning(string channelId) => new()
    {
        ChannelId = channelId,
        SessionId = Guid.NewGuid(),
        StartedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow + WishLimits.LearningWindow
    };

    private static WishCaptureGap NewGap(string channelId, string reason) => new()
    {
        ChannelId = channelId,
        FromUtc = DateTime.UtcNow.AddMinutes(-5),
        ToUtc = DateTime.UtcNow,
        Reason = reason
    };

    private static WishCaptureReceipt NewReceipt(string channelId, string ircMessageId) => new()
    {
        ChannelId = channelId,
        IrcMessageId = ircMessageId,
        SeenAtUtc = DateTime.UtcNow
    };
}
