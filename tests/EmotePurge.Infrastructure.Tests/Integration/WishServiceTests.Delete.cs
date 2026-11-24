using System.Globalization;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishService part 6 (K7a, plan #500 T1): deleting an ended wish. States and episode (E34, E44, AK109, including the
// Postgres half of the microsecond round trip), the effect on slot, exclusions and merged children (E35, AK110), the
// reconcile it starts, audit and events (E36, AK115), the access check behind the channel lock (E41, AK114 without a
// race) and the wish lock that never leaves the caller's channel (E32, AK134). The races with a second delete, the
// retention and an identity change, and the NOWAIT lock of the merged children, are T2 (WishServiceTests.DeleteRaces).
public partial class WishServiceTests
{
    private static readonly string[] DeleteDetailKeys = ["durationDays", "fromStatus", "sevenTvEmoteId", "wishId"];

    public static TheoryData<string> EndStates => [.. WishStatus.All.Where(WishStatus.IsEnded)];

    public static TheoryData<string> NotEndedStates => [.. WishStatus.All.Where(s => !WishStatus.IsEnded(s))];

    // ------------------------------------------------------------------------------------ states (E34, AK109)

    [Theory]
    [MemberData(nameof(EndStates))]
    public async Task Delete_InEveryEndState_WithTheShownEnd_RemovesTheRow_AndAuditsOnceWithFromStatus(string state)
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, state);
        var wish = await LoadWishAsync(wishId);

        var result = await DeleteAsync(channel, wishId, wish.EndedAtUtc!.Value);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Null(result.Wish);
        Assert.Null(result.Settings);
        await AssertGoneAsync(wishId);
        await using var db = fixture.CreateDbContext();
        var entry = await db.AuditLogEntries.AsNoTracking().SingleAsync(a => a.ChannelName == channel.Name);
        Assert.Equal((AuditActions.WishDelete, "wish", wishId.ToString(CultureInfo.InvariantCulture)), (entry.Action, entry.TargetType, entry.TargetId));
        Assert.Equal((Actor.TwitchUserId, Actor.Login), (entry.ActorTwitchUserId, entry.ActorLogin));
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        Assert.Equal(DeleteDetailKeys, details.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(wishId, details.RootElement.GetProperty("wishId").GetInt64());
        Assert.Equal(wish.SevenTvEmoteId, details.RootElement.GetProperty("sevenTvEmoteId").GetString());
        Assert.Equal(wish.DurationDays, details.RootElement.GetProperty("durationDays").GetInt32());
        Assert.Equal(state, details.RootElement.GetProperty("fromStatus").GetString());
        AssertPublishedOnce(channel);
    }

    [Theory]
    [MemberData(nameof(NotEndedStates))]
    public async Task Delete_InEveryOtherState_IsAStateConflict_AndChangesNothing(string state)
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, state, w => w.EndedAtUtc = null);
        var before = await SnapshotAsync(wishId);

        var result = await DeleteAsync(channel, wishId, Now.AddHours(-1));

        Assert.Equal(WishOutcome.StateConflict, result.Outcome);
        await AssertUntouchedAsync(channel, wishId, before);
    }

    [Fact]
    public async Task Delete_WithAnotherEnd_IsAStateConflict_AndChangesNothing()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Kept);
        var shown = (await LoadWishAsync(wishId)).EndedAtUtc!.Value;
        var before = await SnapshotAsync(wishId);

        Assert.Equal(WishOutcome.StateConflict, (await DeleteAsync(channel, wishId, shown.AddMilliseconds(-1))).Outcome);
        Assert.Equal(WishOutcome.StateConflict, (await DeleteAsync(channel, wishId, shown.AddSeconds(1))).Outcome);

        await AssertUntouchedAsync(channel, wishId, before);
    }

    // AK109, the Postgres half of the round trip (the HTTP half is the Api's, T3): an end with microseconds, made by the
    // real command and read back from Postgres, formatted as the ISO string a client sends and parsed the way the
    // handler does, matches; the same instant plus one microsecond does not.
    [Fact]
    public async Task Delete_AnEndWithMicrosecondsReadFromPostgres_RoundTripsAsAnIsoString_AndOneMicrosecondMoreConflicts()
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        var access = await AccessAsync(channel);
        _clock.Advance(TimeSpan.FromTicks(1_234_567));
        Assert.Equal(WishOutcome.Success, (await Sut().KeepAsync(channel.Name, bound.WishId, Actor, access)).Outcome);

        var stored = (await Sut().GetAsync(channel.Name, bound.WishId, access))!.EndedAtUtc!.Value;
        Assert.NotEqual(0, stored.Ticks % TimeSpan.TicksPerMillisecond / 10);
        var sent = stored.ToString("O", CultureInfo.InvariantCulture);
        var parsed = DateTime.Parse(sent, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        Assert.Equal((stored.Ticks, DateTimeKind.Utc), (parsed.Ticks, parsed.Kind));

        Assert.Equal(WishOutcome.StateConflict, (await DeleteAsync(channel, bound.WishId, parsed.AddTicks(10))).Outcome);
        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, bound.WishId, parsed)).Outcome);
        await AssertGoneAsync(bound.WishId);
    }

    // ------------------------------------------------------------------------------------ episodes (E44, AK109)

    // removed_externally (T1) -> return -> removed_externally again (T2 > T1): a confirmation of the first end cannot
    // delete the second episode.
    [Fact]
    public async Task Delete_AfterAReturnAndASecondExternalRemoval_TheFirstEndConflicts_TheSecondDeletes()
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        var firstEnd = await RemoveExternallyAsync(channel, bound, archivedAt: Now);
        await ReturnAsync(channel, bound);

        _clock.Advance(TimeSpan.FromMinutes(10));
        var secondEnd = await RemoveExternallyAsync(channel, bound, archivedAt: Now);
        Assert.True(secondEnd > firstEnd);

        Assert.Equal(WishOutcome.StateConflict, (await DeleteAsync(channel, bound.WishId, firstEnd)).Outcome);
        Assert.Equal(WishStatus.RemovedExternally, (await LoadWishAsync(bound.WishId)).Status);
        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, bound.WishId, secondEnd)).Outcome);
        await AssertGoneAsync(bound.WishId);
    }

    // removed -> re-add -> linked again -> removed again: the first confirmation conflicts.
    [Fact]
    public async Task Delete_AfterAReAddAndASecondRemoval_TheFirstEndConflicts()
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        Assert.Equal(WishOutcome.Success, (await ReportRemovalAsync(channel, bound.WishId)).Outcome);
        var firstEnd = (await LoadWishAsync(bound.WishId)).EndedAtUtc!.Value;
        await UpdateSlotAsync(bound.SlotId, s => (s.IsArchived, s.ArchivedAt) = (true, Now));

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(WishOutcome.Success, (await Sut().ReAddAsync(channel.Name, bound.WishId, new ReAddWishRequest("timed", null), Actor)).Outcome);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var newSlot = await SeedSlotAsync(bound.Emote, "Kappa", Now);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await ReconcileSut(fixture.CreateDbContext).ReconcileChannelAsync(channel.Id);
        Assert.Equal((WishStatus.Active, newSlot), ((await LoadWishAsync(bound.WishId)).Status, (await LoadWishAsync(bound.WishId)).SlotId));

        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(WishOutcome.Success, (await ReportRemovalAsync(channel, bound.WishId)).Outcome);
        var secondEnd = (await LoadWishAsync(bound.WishId)).EndedAtUtc!.Value;
        Assert.True(secondEnd > firstEnd);

        Assert.Equal(WishOutcome.StateConflict, (await DeleteAsync(channel, bound.WishId, firstEnd)).Outcome);
        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, bound.WishId, secondEnd)).Outcome);
    }

    // removed_externally -> removed through the removal report is the same episode: the end the row showed deletes.
    [Fact]
    public async Task Delete_AfterAnExternalRemovalWasBookedAsOurs_TheShownEndDeletes()
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        var shown = await RemoveExternallyAsync(channel, bound, archivedAt: Now);

        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(WishOutcome.Success, (await ReportRemovalAsync(channel, bound.WishId)).Outcome);
        Assert.Equal((WishStatus.Removed, (DateTime?)shown), ((await LoadWishAsync(bound.WishId)).Status, (await LoadWishAsync(bound.WishId)).EndedAtUtc));

        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, bound.WishId, shown)).Outcome);
        await AssertGoneAsync(bound.WishId);
    }

    // PR1-3: an archive stamp older than the return (left on the row) is never reused as the next end, so the old
    // confirmation conflicts.
    [Fact]
    public async Task Delete_AStaleArchiveStampAfterAReturn_IsNotTheNewEnd_AndTheOldConfirmationConflicts()
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        var stamp = Now;
        var firstEnd = await RemoveExternallyAsync(channel, bound, archivedAt: stamp);
        Assert.Equal(stamp, firstEnd);
        await ReturnAsync(channel, bound);

        _clock.Advance(TimeSpan.FromMinutes(10));
        var secondEnd = await RemoveExternallyAsync(channel, bound, archivedAt: stamp);

        Assert.NotEqual(stamp, secondEnd);
        Assert.Equal(Now, secondEnd);
        Assert.Equal(WishOutcome.StateConflict, (await DeleteAsync(channel, bound.WishId, stamp)).Outcome);
        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, bound.WishId, secondEnd)).Outcome);
    }

    // ------------------------------------------------------------------------------------ not found (E38, AK111)

    [Fact]
    public async Task Delete_ASecondTime_IsWishNotFound_AndAuditsOnce()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Closed);
        var ended = (await LoadWishAsync(wishId)).EndedAtUtc!.Value;

        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, wishId, ended)).Outcome);
        Assert.Equal(WishOutcome.WishNotFound, (await DeleteAsync(channel, wishId, ended)).Outcome);

        Assert.Equal(1, await CountAuditsAsync(channel, AuditActions.WishDelete));
        AssertPublishedOnce(channel);
    }

    [Fact]
    public async Task Delete_AWishOfAnotherChannel_IsWishNotFound_AndLeavesItAlone()
    {
        var channel = await SeedChannelAsync();
        var other = await SeedChannelAsync();
        var foreign = await SeedRowAsync(other, WishStatus.Kept);
        var before = await SnapshotAsync(foreign);

        var result = await DeleteAsync(channel, foreign, (await LoadWishAsync(foreign)).EndedAtUtc!.Value);

        Assert.Equal(WishOutcome.WishNotFound, result.Outcome);
        Assert.Equal(before, await SnapshotAsync(foreign));
        await AssertNoTraceAsync(channel);
        await AssertNoTraceAsync(other);
    }

    // AK111, second sentence: the removal report of a removed_externally wish that was deleted meanwhile.
    [Fact]
    public async Task CompleteRemoval_OfAnExternallyRemovedWishDeletedMeanwhile_IsWishNotFound()
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        var ended = await RemoveExternallyAsync(channel, bound, archivedAt: Now);
        var report = await ReportOfAsync(bound.WishId);

        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, bound.WishId, ended)).Outcome);
        var result = await Sut().CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor);

        Assert.Equal(WishOutcome.WishNotFound, result.Outcome);
        Assert.Equal(0, await CountRemovalAuditsAsync(channel));
    }

    // ------------------------------------------------------------------------------------ the lock stays in the channel (E32, AK134)

    [Fact]
    public async Task Delete_ByTheIdOfAForeignWishAnotherConnectionHolds_AnswersWishNotFound_WithoutWaiting_AndNeverLocksIt()
    {
        var channel = await SeedChannelAsync();
        var other = await SeedChannelAsync();
        var foreign = await SeedRowAsync(other, WishStatus.Kept);
        var ended = (await LoadWishAsync(foreign)).EndedAtUtc!.Value;
        await using var holder = fixture.CreateTaggedDbContext("wish-delete-foreign-holder");
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Wishes" WHERE "Id" = {foreign} FOR UPDATE""");

        var result = await DeleteAsync(channel, foreign, ended).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WishOutcome.WishNotFound, result.Outcome);
        // The holder writes on undisturbed: nothing of the delete is left on the row.
        await holder.Database.ExecuteSqlAsync($"""UPDATE "Wishes" SET "DurationDays" = 12 WHERE "Id" = {foreign}""");
        await holderTransaction.CommitAsync();
        Assert.Equal(12, (await LoadWishAsync(foreign)).DurationDays);
        await AssertNoTraceAsync(channel);
    }

    // ------------------------------------------------------------------------------------ effect (E35, AK110)

    [Fact]
    public async Task Delete_DropsTheSlotExclusions_AndClearsOnlyTheReferenceOfAWishMergedIntoIt()
    {
        var channel = await SeedChannelAsync();
        var target = await SeedRowAsync(channel, WishStatus.Kept);
        var child = await SeedRowAsync(channel, WishStatus.Merged, w => w.MergedIntoWishId = target);
        await using (var db = fixture.CreateDbContext())
        {
            db.WishSlotExclusions.Add(new WishSlotExclusion { WishId = target, SlotId = "any-slot", CreatedAtUtc = Now.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        var childBefore = await SnapshotAsync(child);

        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, target, (await LoadWishAsync(target)).EndedAtUtc!.Value)).Outcome);

        await using var after = fixture.CreateDbContext();
        Assert.False(await after.WishSlotExclusions.AnyAsync(x => x.WishId == target));
        var childAfter = await after.Wishes.AsNoTracking().SingleAsync(w => w.Id == child);
        Assert.Equal((WishStatus.Merged, (long?)null), (childAfter.Status, childAfter.MergedIntoWishId));
        childAfter.MergedIntoWishId = target;
        Assert.Equal(childBefore, JsonSerializer.Serialize(childAfter));
    }

    [Fact]
    public async Task Delete_OfAMergedWish_LeavesItsTargetUnchanged()
    {
        var channel = await SeedChannelAsync();
        var target = await SeedRowAsync(channel, WishStatus.Active);
        var merged = await SeedRowAsync(channel, WishStatus.Merged, w => w.MergedIntoWishId = target);
        var targetBefore = await SnapshotAsync(target);

        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, merged, (await LoadWishAsync(merged)).EndedAtUtc!.Value)).Outcome);

        Assert.Equal(targetBefore, await SnapshotAsync(target));
    }

    [Fact]
    public async Task Delete_OfAKeptWishWithItsSlot_FreesTheEntry_ForTheListAndForAnAdoption()
    {
        var channel = await SeedChannelAsync();
        var kept = await SeedBoundRowAsync(channel, WishStatus.Kept);
        var held = Assert.Single((await Sut().ListAdoptableSlotsAsync(channel.Name, kept.Emote.SevenTvEmoteId))!);
        Assert.Equal(kept.WishId, held.HeldByWishId);

        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, kept.WishId, (await LoadWishAsync(kept.WishId)).EndedAtUtc!.Value)).Outcome);

        var free = Assert.Single((await Sut().ListAdoptableSlotsAsync(channel.Name, kept.Emote.SevenTvEmoteId))!);
        Assert.Equal((kept.SlotId, (long?)null), (free.SlotId, free.HeldByWishId));
        var adopted = await AdoptAsync(channel, kept.SlotId, durationDays: 30);
        Assert.Equal(WishOutcome.Success, adopted.Outcome);
        Assert.NotEqual(kept.WishId, adopted.Wish!.Id);
        Assert.Equal((kept.SlotId, 30), (adopted.Wish.SlotId, adopted.Wish.DurationDays));
    }

    [Fact]
    public async Task Delete_OfAKeptWishWithItsSlot_WhileASecondAliasOfTheEmoteRuns_TheAdoptionIsAlreadyRunning()
    {
        var channel = await SeedChannelAsync();
        var kept = await SeedBoundRowAsync(channel, WishStatus.Kept);
        var secondSlot = await SeedSlotAsync(kept.Emote, "Kappa2", Now.AddDays(-5));
        var running = await SeedRowAsync(channel, WishStatus.Active, w =>
        {
            w.SevenTvEmoteId = kept.Emote.SevenTvEmoteId;
            w.Alias = "Kappa2";
            w.SlotId = secondSlot;
        });

        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, kept.WishId, (await LoadWishAsync(kept.WishId)).EndedAtUtc!.Value)).Outcome);
        var adopted = await AdoptAsync(channel, kept.SlotId);

        Assert.Equal((WishOutcome.AlreadyRunning, (long?)running), (adopted.Outcome, adopted.ConflictingWishId));
    }

    // The reconcile runs after the commit, exactly once and for the channel, only when a kept wish held a slot.
    [Theory]
    [InlineData("kept-with-slot", true)]
    [InlineData("kept-without-slot", false)]
    [InlineData("removed-with-slot", false)]
    [InlineData("closed", false)]
    public async Task Delete_StartsTheReconcile_OnlyForAKeptWishThatHeldASlot(string variant, bool triggers)
    {
        var channel = await SeedChannelAsync();
        var wishId = variant switch
        {
            "kept-with-slot" => (await SeedBoundRowAsync(channel, WishStatus.Kept)).WishId,
            "kept-without-slot" => await SeedRowAsync(channel, WishStatus.Kept),
            "removed-with-slot" => (await SeedBoundRowAsync(channel, WishStatus.Removed)).WishId,
            _ => await SeedRowAsync(channel, WishStatus.Closed),
        };
        var trigger = Substitute.For<IWishReconcileTrigger>();
        var ended = (await LoadWishAsync(wishId)).EndedAtUtc!.Value;

        var result = await SutWith(trigger).DeleteAsync(channel.Name, wishId, ended, Actor, await AccessAsync(channel));

        Assert.Equal(WishOutcome.Success, result.Outcome);
        await trigger.Received(triggers ? 1 : 0).TriggerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        if (triggers)
        {
            await trigger.Received(1).TriggerAsync(channel.Id, Arg.Any<CancellationToken>());
        }
    }

    // The freed entry is the reconcile's to hand on: an approved wish of the emote whose window covers its AddedAt.
    [Fact]
    public async Task Delete_OfAKeptWishWithItsSlot_ThenTheReconcile_HandsTheEntryToAnApprovedWishOfTheEmote()
    {
        var channel = await SeedChannelAsync();
        var kept = await SeedBoundRowAsync(channel, WishStatus.Kept);
        var approved = await SeedRowAsync(channel, WishStatus.Approved, w =>
        {
            w.SevenTvEmoteId = kept.Emote.SevenTvEmoteId;
            w.Alias = "Kappa";
            w.ApprovedAtUtc = kept.Generation.AddDays(-1);
        });
        var reconcile = ReconcileSut(fixture.CreateDbContext);
        await reconcile.ReconcileChannelAsync(channel.Id);
        Assert.Equal((WishStatus.Approved, (string?)null), ((await LoadWishAsync(approved)).Status, (await LoadWishAsync(approved)).SlotId));

        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, kept.WishId, (await LoadWishAsync(kept.WishId)).EndedAtUtc!.Value)).Outcome);
        await reconcile.ReconcileChannelAsync(channel.Id);

        var linked = await LoadWishAsync(approved);
        Assert.Equal((WishStatus.Active, kept.SlotId), (linked.Status, linked.SlotId));
    }

    // ------------------------------------------------------------------------------------ events (E36)

    [Fact]
    public async Task Delete_PublishesNothingOnARefusal_AndOneWishChangedOnSuccess()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Rejected);
        var ended = (await LoadWishAsync(wishId)).EndedAtUtc!.Value;

        Assert.Equal(WishOutcome.StateConflict, (await DeleteAsync(channel, wishId, ended.AddMinutes(1))).Outcome);
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);

        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, wishId, ended)).Outcome);
        AssertPublishedOnce(channel);
    }

    [Fact]
    public async Task Delete_WithAThrowingPublisher_StaysCommitted()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Expired);
        var ended = (await LoadWishAsync(wishId)).EndedAtUtc!.Value;
        _publisher.PublishAsync(default!, default!, default).ReturnsForAnyArgs<Task>(_ => throw new InvalidOperationException("redis down"));

        var result = await DeleteAsync(channel, wishId, ended);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        await AssertGoneAsync(wishId);
        Assert.Equal(1, await CountAuditsAsync(channel, AuditActions.WishDelete));
    }

    // ------------------------------------------------------------------------------------ access (E41, AK114 without a race)

    [Theory]
    [InlineData("other-twitch-id")]
    [InlineData("unconfirmed")]
    [InlineData("unknown-wish-other-twitch-id")]
    public async Task Delete_WithAnAccessNotConfirmedOnTheRowsTwitchId_IsAccessDenied_AndWritesNothing(string variant)
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Kept);
        var ended = (await LoadWishAsync(wishId)).EndedAtUtc!.Value;
        var before = await SnapshotAsync(wishId);
        var confirmed = await AccessAsync(channel);
        var (access, target) = variant switch
        {
            "other-twitch-id" => (confirmed with { TwitchChannelId = "someone-else" }, wishId),
            "unconfirmed" => (confirmed with { IdConfirmed = false }, wishId),
            _ => (confirmed with { TwitchChannelId = "someone-else" }, 987654321L),
        };

        var result = await Sut().DeleteAsync(channel.Name, target, ended, Actor, access);

        Assert.Equal(WishOutcome.AccessDenied, result.Outcome);
        await AssertUntouchedAsync(channel, wishId, before);
    }

    [Fact]
    public async Task Delete_ForAChannelRowThatIsGone_IsChannelNotFound()
    {
        var channel = await SeedChannelAsync();
        var access = new WishCaptureAccess(Guid.NewGuid().ToString(), "123", IdConfirmed: true);

        var result = await Sut().DeleteAsync(channel.Name, 1, Now, Actor, access);

        Assert.Equal(WishOutcome.ChannelNotFound, result.Outcome);
        await AssertNoTraceAsync(channel);
    }

    // The channel row is deleted while the command waits for its lock: it finds no row behind the lock.
    [Fact]
    public async Task Delete_WhileTheChannelRowIsDeletedUnderIt_IsChannelNotFound()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Kept);
        var ended = (await LoadWishAsync(wishId)).EndedAtUtc!.Value;
        var access = await AccessAsync(channel);
        const string tag = "wish-delete-channel-gone";
        await using var purger = fixture.CreateDbContext();
        await using var purge = await purger.Database.BeginTransactionAsync();
        await purger.Database.ExecuteSqlAsync($"""DELETE FROM "Channels" WHERE "Id" = {channel.Id}""");

        var delete = Sut(fixture.CreateTaggedDbContext(tag)).DeleteAsync(channel.Name, wishId, ended, Actor, access);
        await fixture.WaitUntilBlockedOnLockAsync(tag, delete);
        await purge.CommitAsync();

        Assert.Equal(WishOutcome.ChannelNotFound, (await delete).Outcome);
        Assert.Equal(0, await CountAuditsAsync(channel, AuditActions.WishDelete));
    }

    // After an identity merge the wish lives on the survivor; an access decided on the loser never deletes there.
    [Theory]
    [InlineData(false, WishOutcome.WishNotFound)]
    [InlineData(true, WishOutcome.ChannelNotFound)]
    public async Task Delete_WithAnAccessOnTheLoserOfAMerge_NeverDeletesTheSurvivorsWish(bool loserGone, WishOutcome expected)
    {
        var loser = await SeedChannelAsync();
        var survivor = await SeedChannelAsync();
        var wishId = await SeedRowAsync(survivor, WishStatus.Kept);
        var ended = (await LoadWishAsync(wishId)).EndedAtUtc!.Value;
        var access = await AccessAsync(loser);
        var before = await SnapshotAsync(wishId);
        if (loserGone)
        {
            await using var db = fixture.CreateDbContext();
            await db.Channels.Where(c => c.Id == loser.Id).ExecuteDeleteAsync();
        }

        var result = await Sut().DeleteAsync(survivor.Name, wishId, ended, Actor, access);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(before, await SnapshotAsync(wishId));
        await AssertNoTraceAsync(survivor);
    }

    // ------------------------------------------------------------------------------------ audit without personal data (AK115)

    [Fact]
    public async Task Delete_TheAuditEntryCarriesNoAliasNameLoginOrText()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Rejected, w =>
        {
            w.Source = WishSource.Irc;
            w.MatchKind = WishMatchKind.Name;
            w.Alias = "SecretAlias";
            w.EmoteName = "SecretName";
            w.RequesterLogin = "secretlogin";
            w.RequesterTwitchUserId = "secret-id";
            w.RequestText = "Secret text";
            w.RequestedName = "SecretRequested";
        });

        Assert.Equal(WishOutcome.Success, (await DeleteAsync(channel, wishId, (await LoadWishAsync(wishId)).EndedAtUtc!.Value)).Outcome);

        var details = await SingleAuditDetailsAsync(channel, AuditActions.WishDelete);
        Assert.DoesNotContain("secret", details.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------ helpers (delete)

    private async Task<WishCommandResult> DeleteAsync(Seeded channel, long wishId, DateTime expectedEndedAtUtc) =>
        await Sut().DeleteAsync(channel.Name, wishId, expectedEndedAtUtc, Actor, await AccessAsync(channel));

    // The access the filter would decide for a manager confirmed on the channel row's Twitch id.
    private async Task<WishCaptureAccess> AccessAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        var twitchId = await db.Channels.AsNoTracking().Where(c => c.Id == channel.Id).Select(c => c.TwitchChannelId).SingleAsync();
        return new WishCaptureAccess(channel.Id, twitchId, IdConfirmed: true);
    }

    private async Task AssertGoneAsync(long wishId)
    {
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.Wishes.AnyAsync(w => w.Id == wishId));
    }

    // The slot leaves the set at archivedAt and the reconcile books it ten minutes later; returns the end it stored.
    private async Task<DateTime> RemoveExternallyAsync(Seeded channel, BoundRow bound, DateTime archivedAt)
    {
        await UpdateSlotAsync(bound.SlotId, s => (s.IsArchived, s.ArchivedAt) = (true, archivedAt));
        _clock.Advance(TimeSpan.FromMinutes(10));
        await ReconcileSut(fixture.CreateDbContext).ReconcileChannelAsync(channel.Id);
        var wish = await LoadWishAsync(bound.WishId);
        Assert.Equal(WishStatus.RemovedExternally, wish.Status);
        return wish.EndedAtUtc!.Value;
    }

    // The same slot row comes back with a new date inside the return window, and the reconcile returns the wish.
    private async Task ReturnAsync(Seeded channel, BoundRow bound)
    {
        var back = Now.AddMinutes(10);
        await UpdateSlotAsync(bound.SlotId, s => (s.IsArchived, s.ArchivedAt, s.AddedAt, s.AddedAtFromRest) = (false, null, back, true));
        _clock.Advance(TimeSpan.FromMinutes(20));
        await ReconcileSut(fixture.CreateDbContext).ReconcileChannelAsync(channel.Id);
        var wish = await LoadWishAsync(bound.WishId);
        Assert.Equal((WishStatus.Active, (DateTime?)null), (wish.Status, wish.EndedAtUtc));
    }
}
