using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Wish emotes (plan T9, finding 15, Sol-R2-1): the identity merge moves the loser's wishes and wish settings
// to the survivor before it deletes the loser, and serialises against the wish writers that insert under the
// channel lock. The barrier tests park one side right before its commit (the audit log held in SHARE mode)
// with its locks in hand, start the other side, wait until Postgres reports it waiting, then release: no
// deadlock (40P01 would surface from either await), both ends defined.
public partial class ChannelIdentityServiceTests
{
    private static readonly AuditActor WishActor = new("4243", "wishmergemod");

    [Fact]
    public async Task Merge_MovesTheLosersWishesAndSettings_WithEveryColumnUnchanged_WhenTheSurvivorHasNone()
    {
        var pair = await SeedMergePairAsync();
        var generation = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        long approved, removed;
        await using (var seed = fixture.CreateDbContext())
        {
            var a = MergeWish(pair.LoserId, WishStatus.Approved, new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc));
            var r = MergeWish(pair.LoserId, WishStatus.Removed, new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc));
            r.FirstActivatedAtUtc = generation;
            r.SlotAddedAtUtc = generation;
            r.SlotConfirmedAtUtc = generation.AddHours(3);
            r.EndedAtUtc = generation.AddDays(5);
            r.RequesterTwitchUserId = "wish-requester";
            r.RequesterLogin = "wishrequester";
            seed.Wishes.AddRange(a, r);
            seed.ChannelWishSettings.Add(new ChannelWishSettings { ChannelId = pair.LoserId, DefaultDurationDays = 10, ProbationLeadDays = 3 });
            await seed.SaveChangesAsync();
            (approved, removed) = (a.Id, r.Id);
        }

        var before = await WishRowsAsync(approved, removed);

        await using var db = fixture.CreateDbContext();
        var summary = await CreateHarness(db, [pair.Identity]).Service.ReconcileActiveChannelsAsync();

        Assert.Equal(1, summary?.Merged);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Channels.AnyAsync(c => c.Id == pair.LoserId));
        var after = await WishRowsAsync(approved, removed);
        Assert.All(after, w => Assert.Equal(pair.SurvivorId, w.ChannelId));
        // Only the channel moved; the slot generation, FirstActivatedAtUtc and the requester travel as they are.
        Assert.Equal(
            before.Select(w => Serialize(w, channelId: null)),
            after.Select(w => Serialize(w, channelId: null)));
        var settings = await verify.ChannelWishSettings.AsNoTracking().SingleAsync(s => s.ChannelId == pair.SurvivorId);
        Assert.Equal((10, 3), (settings.DefaultDurationDays, settings.ProbationLeadDays));
        Assert.False(await verify.ChannelWishSettings.AnyAsync(s => s.ChannelId == pair.LoserId));

        var entry = await verify.AuditLogEntries.AsNoTracking()
            .SingleAsync(e => e.Action == AuditActions.ChannelMerge && e.ChannelName == pair.LoserName);
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        Assert.Equal(2, details.RootElement.GetProperty("movedWishes").GetInt32());
        Assert.Equal(0, details.RootElement.GetProperty("mergedWishes").GetInt32());
        Assert.Equal(1, details.RootElement.GetProperty("movedWishSettings").GetInt32());
    }

    // Finding 15: the younger of two running wishes for the same emote (greater CreatedAtUtc, on a tie the
    // greater id; the loser's is seeded second, so it has the greater id) ends as merged into the older one,
    // whichever channel it came from; the survivor's settings win.
    [Theory]
    [InlineData("loserOlder")]
    [InlineData("survivorOlder")]
    [InlineData("tie")]
    public async Task Merge_OnARunningCollision_EndsTheYoungerAsMerged_AndTheSurvivorsSettingsWin(string variant)
    {
        var pair = await SeedMergePairAsync();
        var emoteId = Guid.NewGuid().ToString("N")[..24];
        var early = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var late = early.AddDays(3);
        var (survivorCreated, loserCreated) = variant switch
        {
            "loserOlder" => (late, early),
            "survivorOlder" => (early, late),
            _ => (early, early)
        };

        long survivorWish, loserWish, unrelated;
        await using (var seed = fixture.CreateDbContext())
        {
            var s = MergeWish(pair.SurvivorId, WishStatus.Approved, survivorCreated, emoteId);
            seed.Wishes.Add(s);
            await seed.SaveChangesAsync();
            var l = MergeWish(pair.LoserId, WishStatus.Approved, loserCreated, emoteId);
            l.RemovalQueuedAtUtc = early;
            l.RemovalQueuedVersion = 0;
            s.RemovalQueuedAtUtc = early;
            s.RemovalQueuedVersion = 0;
            var u = MergeWish(pair.LoserId, WishStatus.Approved, early);
            seed.Wishes.AddRange(l, u);
            seed.ChannelWishSettings.AddRange(
                new ChannelWishSettings { ChannelId = pair.SurvivorId, DefaultDurationDays = 20 },
                new ChannelWishSettings { ChannelId = pair.LoserId, DefaultDurationDays = 10 });
            await seed.SaveChangesAsync();
            (survivorWish, loserWish, unrelated) = (s.Id, l.Id, u.Id);
        }

        await using var db = fixture.CreateDbContext();
        var summary = await CreateHarness(db, [pair.Identity]).Service.ReconcileActiveChannelsAsync();

        Assert.Equal(1, summary?.Merged);
        var (olderId, youngerId) = variant == "survivorOlder" ? (survivorWish, loserWish) : (loserWish, survivorWish);
        if (variant == "tie")
        {
            (olderId, youngerId) = (survivorWish, loserWish);
        }

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.Wishes.AsNoTracking()
            .Where(w => w.Id == survivorWish || w.Id == loserWish || w.Id == unrelated)
            .ToDictionaryAsync(w => w.Id);
        Assert.All(rows.Values, w => Assert.Equal(pair.SurvivorId, w.ChannelId));
        var younger = rows[youngerId];
        Assert.Equal(WishStatus.Merged, younger.Status);
        Assert.Equal(olderId, younger.MergedIntoWishId);
        Assert.NotNull(younger.EndedAtUtc);
        Assert.Equal(younger.EndedAtUtc, younger.StatusChangedAtUtc);
        Assert.Equal(1, younger.DecisionVersion);
        Assert.Null(younger.RemovalQueuedAtUtc);
        Assert.Null(younger.RemovalQueuedVersion);
        var older = rows[olderId];
        Assert.Equal(WishStatus.Approved, older.Status);
        Assert.Null(older.EndedAtUtc);
        Assert.Equal(0, older.DecisionVersion);
        Assert.NotNull(older.RemovalQueuedAtUtc);
        Assert.Equal(WishStatus.Approved, rows[unrelated].Status);

        var settings = await verify.ChannelWishSettings.AsNoTracking()
            .Where(s => s.ChannelId == pair.SurvivorId || s.ChannelId == pair.LoserId)
            .ToListAsync();
        var only = Assert.Single(settings);
        Assert.Equal((pair.SurvivorId, 20), (only.ChannelId, only.DefaultDurationDays));
    }

    // Operator ruling 2026-11-18: a trace in 7TV beats age. The survivor's linked active wish is the younger
    // one. Against an older approved wish without a trace it wins, also when that approved wish still carries
    // an old SlotId (history, not a link). Against an older wish with a trace of its own (BotWriteAtUtc,
    // prepared for K4) both stand alike and the age rule decides.
    [Theory]
    [InlineData("olderUntraced", true)]
    [InlineData("olderWithHistorySlot", true)]
    [InlineData("olderBotWritten", false)]
    public async Task Merge_OnARunningCollision_TheWishWithA7TvTraceWins_ElseTheOlder(string variant, bool linkedWins)
    {
        var pair = await SeedMergePairAsync();
        var emoteId = Guid.NewGuid().ToString("N")[..24];
        var early = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var linkedSlot = await SeedSurvivorSlotAsync(pair.SurvivorId, emoteId, "Alias");
        var historySlot = await SeedSurvivorSlotAsync(pair.SurvivorId, Guid.NewGuid().ToString("N")[..24], "Old");

        long linkedWish, olderWish;
        await using (var seed = fixture.CreateDbContext())
        {
            var linked = MergeWish(pair.SurvivorId, WishStatus.Active, early.AddDays(3), emoteId);
            linked.SlotId = linkedSlot;
            linked.LinkOrigin = WishLinkOrigin.Adopted;
            linked.StartsAtUtc = early.AddDays(3);
            linked.ExpiresAtUtc = early.AddDays(33);
            var older = MergeWish(pair.LoserId, WishStatus.Approved, early, emoteId);
            if (variant == "olderWithHistorySlot")
            {
                older.SlotId = historySlot;
            }

            if (variant == "olderBotWritten")
            {
                older.BotWriteAtUtc = early.AddHours(1);
            }

            seed.Wishes.AddRange(linked, older);
            await seed.SaveChangesAsync();
            (linkedWish, olderWish) = (linked.Id, older.Id);
        }

        await using var db = fixture.CreateDbContext();
        var summary = await CreateHarness(db, [pair.Identity]).Service.ReconcileActiveChannelsAsync();

        Assert.Equal(1, summary?.Merged);
        var (winnerId, endedId) = linkedWins ? (linkedWish, olderWish) : (olderWish, linkedWish);
        await using var verify = fixture.CreateDbContext();
        var rows = await verify.Wishes.AsNoTracking()
            .Where(w => w.Id == linkedWish || w.Id == olderWish)
            .ToDictionaryAsync(w => w.Id);
        Assert.All(rows.Values, w => Assert.Equal(pair.SurvivorId, w.ChannelId));
        var ended = rows[endedId];
        Assert.Equal(WishStatus.Merged, ended.Status);
        Assert.Equal(winnerId, ended.MergedIntoWishId);
        Assert.Equal(1, ended.DecisionVersion);
        var winner = rows[winnerId];
        Assert.Null(winner.EndedAtUtc);
        Assert.Equal(0, winner.DecisionVersion);
        Assert.Equal(linkedWins ? WishStatus.Active : WishStatus.Approved, winner.Status);
        if (linkedWins)
        {
            Assert.Equal(linkedSlot, winner.SlotId);
        }
    }

    // Sol-R2-1, manual entry in the survivor for an emote a running wish of the loser carries. Entry first:
    // the merge waits for the channel row, then ends the (younger) new wish as merged. Merge first: the entry
    // waits for the channel row, then finds the moved wish and answers wish_already_running.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Barrier_ManualEntryInTheSurvivor_AgainstTheMerge_EndsDefinedInBothOrders(bool entryFirst)
    {
        var pair = await SeedMergePairAsync();
        var emoteId = Guid.NewGuid().ToString("N")[..24];
        long loserWish;
        await using (var seed = fixture.CreateDbContext())
        {
            // Older than anything the hand-wound clock of the entry stamps (2026-09-14).
            var l = MergeWish(pair.LoserId, WishStatus.Approved, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), emoteId);
            seed.Wishes.Add(l);
            await seed.SaveChangesAsync();
            loserWish = l.Id;
        }

        var tag = $"wm-entry-{entryFirst}";
        var (entry, summary) = await RaceAsync(
            pair,
            tag,
            entryFirst,
            service => service.CreateAsync(pair.SurvivorName, new CreateWishRequest(emoteId, "Emote", "Alias", null, null), WishActor));

        Assert.Equal(1, summary?.Merged);
        await using var verify = fixture.CreateDbContext();
        var rows = await verify.Wishes.AsNoTracking()
            .Where(w => w.ChannelId == pair.SurvivorId || w.ChannelId == pair.LoserId)
            .ToListAsync();
        Assert.All(rows, w => Assert.Equal(pair.SurvivorId, w.ChannelId));
        var moved = Assert.Single(rows, w => w.Id == loserWish);
        Assert.Equal(WishStatus.Approved, moved.Status);
        if (entryFirst)
        {
            Assert.Equal(WishOutcome.Success, entry.Outcome);
            var created = Assert.Single(rows, w => w.Id == entry.Wish!.Id);
            Assert.Equal(WishStatus.Merged, created.Status);
            Assert.Equal(loserWish, created.MergedIntoWishId);
            Assert.Equal(2, rows.Count);
        }
        else
        {
            Assert.Equal(WishOutcome.AlreadyRunning, entry.Outcome);
            Assert.Equal(loserWish, entry.ConflictingWishId);
            Assert.Single(rows);
        }
    }

    // Sol-R2-1, the settings upsert in the survivor against a merge whose loser has settings: either way
    // exactly one row is left, the survivor's, with the upsert's values.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Barrier_SettingsUpsertInTheSurvivor_AgainstAMergeWithLoserSettings_LeavesExactlyTheSurvivorsRow(bool upsertFirst)
    {
        var pair = await SeedMergePairAsync();
        await using (var seed = fixture.CreateDbContext())
        {
            seed.ChannelWishSettings.Add(new ChannelWishSettings { ChannelId = pair.LoserId, DefaultDurationDays = 10, ProbationLeadDays = 3 });
            await seed.SaveChangesAsync();
        }

        var tag = $"wm-settings-{upsertFirst}";
        var (upsert, summary) = await RaceAsync(
            pair,
            tag,
            upsertFirst,
            service => service.UpdateSettingsAsync(pair.SurvivorName, new UpdateWishSettingsRequest(20, 5, null), WishActor));

        Assert.Equal(1, summary?.Merged);
        Assert.Equal(WishOutcome.Success, upsert.Outcome);
        await using var verify = fixture.CreateDbContext();
        var rows = await verify.ChannelWishSettings.AsNoTracking()
            .Where(s => s.ChannelId == pair.SurvivorId || s.ChannelId == pair.LoserId)
            .ToListAsync();
        var only = Assert.Single(rows);
        Assert.Equal((pair.SurvivorId, 20, 5), (only.ChannelId, only.DefaultDurationDays, only.ProbationLeadDays));
    }

    // Sol-R2-1, a manual entry in the loser while it is merged away: either the entry commits first and its
    // wish moves with the others, or it waits and finds the loser gone (channel_not_found). Never lost.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Barrier_ManualEntryInTheLoser_DuringTheMerge_MovesOrIsChannelNotFound(bool entryFirst)
    {
        var pair = await SeedMergePairAsync();
        var emoteId = Guid.NewGuid().ToString("N")[..24];

        var tag = $"wm-loser-{entryFirst}";
        var (entry, summary) = await RaceAsync(
            pair,
            tag,
            entryFirst,
            service => service.CreateAsync(pair.LoserName, new CreateWishRequest(emoteId, "Emote", "Alias", null, null), WishActor));

        Assert.Equal(1, summary?.Merged);
        await using var verify = fixture.CreateDbContext();
        var rows = await verify.Wishes.AsNoTracking().Where(w => w.SevenTvEmoteId == emoteId).ToListAsync();
        if (entryFirst)
        {
            Assert.Equal(WishOutcome.Success, entry.Outcome);
            var wish = Assert.Single(rows);
            Assert.Equal((entry.Wish!.Id, pair.SurvivorId, WishStatus.Approved), (wish.Id, wish.ChannelId, wish.Status));
        }
        else
        {
            Assert.Equal(WishOutcome.ChannelNotFound, entry.Outcome);
            Assert.Empty(rows);
        }
    }

    /// <summary>
    /// Runs the wish write and the identity reconcile (which merges the pair) against each other: the first
    /// one parks before its commit on the held audit log, the second one is started and must wait on a row
    /// lock, then the hold is released and both are awaited.
    /// </summary>
    private async Task<(WishCommandResult Write, ChannelIdentityReconcileSummary? Summary)> RaceAsync(
        MergePair pair, string tag, bool writeFirst, Func<WishService, Task<WishCommandResult>> write)
    {
        await using var auditHold = await HoldTheAuditLogAsync();
        await using var writeDb = fixture.CreateTaggedDbContext($"{tag}-write");
        await using var mergeDb = fixture.CreateTaggedDbContext($"{tag}-merge");
        var service = new WishService(
            writeDb,
            new WishChangedPublisher(Substitute.For<IRedisPublisher>(), NullLogger<WishChangedPublisher>.Instance),
            new HandWoundTimeProvider(),
            NullLogger<WishService>.Instance, BotChannelStates.Unavailable());
        var harness = CreateHarness(mergeDb, [pair.Identity]);

        Task<WishCommandResult> writeTask;
        Task<ChannelIdentityReconcileSummary?> mergeTask;
        if (writeFirst)
        {
            writeTask = write(service);
            await fixture.WaitUntilBlockedOnLockAsync($"{tag}-write", writeTask);
            mergeTask = harness.Service.ReconcileActiveChannelsAsync();
            await fixture.WaitUntilBlockedOnLockAsync($"{tag}-merge", mergeTask);
            Assert.Contains("\"Channels\"", await fixture.WaitingQueryAsync($"{tag}-merge"));
        }
        else
        {
            mergeTask = harness.Service.ReconcileActiveChannelsAsync();
            await fixture.WaitUntilBlockedOnLockAsync($"{tag}-merge", mergeTask);
            writeTask = write(service);
            await fixture.WaitUntilBlockedOnLockAsync($"{tag}-write", writeTask);
            Assert.Contains("\"Channels\"", await fixture.WaitingQueryAsync($"{tag}-write"));
        }

        await auditHold.ReleaseAsync();
        return (await writeTask, await mergeTask);
    }

    // A survivor holding a Twitch id under its old name and an id-less loser under the name Helix now reports
    // for that id: the reconcile merges the loser into the survivor.
    private async Task<MergePair> SeedMergePairAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var twitchId = $"wm{suffix}";
        await using var db = fixture.CreateDbContext();
        var survivor = await SeedChannelAsync(db, $"wmold{suffix}", twitchId);
        var loser = await SeedChannelAsync(db, $"wmnew{suffix}", twitchChannelId: null);
        return new MergePair(survivor.Id, survivor.ChannelName, loser.Id, loser.ChannelName, new TwitchUserIdentity(twitchId, loser.ChannelName));
    }

    // A dated, active slot of the survivor (the loser may hold no emotes, or the merge is refused).
    private async Task<string> SeedSurvivorSlotAsync(string channelId, string sevenTvEmoteId, string alias)
    {
        await using var db = fixture.CreateDbContext();
        var emote = new Emote
        {
            ChannelId = channelId,
            Name = alias,
            SevenTvEmoteId = sevenTvEmoteId,
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        var slot = new EmoteSlot
        {
            EmoteId = emote.Id,
            Name = alias,
            AddedAt = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc),
            AddedAtFromRest = true
        };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot.Id;
    }

    private static Wish MergeWish(string channelId, string status, DateTime createdAtUtc, string? emoteId = null) => new()
    {
        ChannelId = channelId,
        SevenTvEmoteId = emoteId ?? Guid.NewGuid().ToString("N")[..24],
        EmoteName = "Emote",
        Alias = "Alias",
        Source = WishSource.Manual,
        Status = status,
        StatusChangedAtUtc = createdAtUtc,
        CreatedAtUtc = createdAtUtc,
        ApprovedAtUtc = createdAtUtc,
        EndedAtUtc = WishStatus.IsEnded(status) ? createdAtUtc.AddDays(1) : null
    };

    private async Task<List<Wish>> WishRowsAsync(params long[] ids)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Wishes.AsNoTracking().Where(w => ids.Contains(w.Id)).OrderBy(w => w.Id).ToListAsync();
    }

    private static string Serialize(Wish wish, string? channelId)
    {
        wish.ChannelId = channelId ?? string.Empty;
        return JsonSerializer.Serialize(wish);
    }

    private sealed record MergePair(string SurvivorId, string SurvivorName, string LoserId, string LoserName, TwitchUserIdentity Identity);
}
