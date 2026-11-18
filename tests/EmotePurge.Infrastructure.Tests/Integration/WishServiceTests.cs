using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishService part 1 (wish emotes, T4) against real Postgres: manual entry, adoption, settings, the reads,
// the channel-lock ordering and the one-event-per-commit rule. Every test seeds its own channel in the
// shared database and only ever looks at that channel's rows.
[Collection("Postgres")]
public partial class WishServiceTests(PostgresFixture fixture)
{
    private static readonly AuditActor Actor = new("4242", "wishmod");

    private readonly IRedisPublisher _publisher = Substitute.For<IRedisPublisher>();
    private readonly HandWoundTimeProvider _clock = new();

    private DateTime Now => _clock.Now.UtcDateTime;

    // ------------------------------------------------------------------------------------ manual entry

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(365, true)]
    [InlineData(366, false)]
    [InlineData(-5, false)]
    public async Task Create_DurationBoundaries_AreOneToThreeHundredSixtyFive(int days, bool accepted)
    {
        var channel = await SeedChannelAsync();

        var result = await CreateAsync(channel, EmoteId(), "Alias", durationDays: days);

        Assert.Equal(accepted ? WishOutcome.Success : WishOutcome.DurationOutOfRange, result.Outcome);
        if (accepted)
        {
            Assert.Equal(days, result.Wish!.DurationDays);
        }
        else
        {
            await AssertNothingWrittenAsync(channel);
        }
    }

    [Fact]
    public async Task Create_WithoutADuration_TakesTheChannelDefault_AndTheShippedDefaultWithoutASettingsRow()
    {
        var channel = await SeedChannelAsync();

        var first = await CreateAsync(channel, EmoteId(), "First");
        Assert.Equal(30, first.Wish!.DurationDays);

        var update = await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(14, 7, null), Actor);
        Assert.True(update.IsSuccess);
        var second = await CreateAsync(channel, EmoteId(), "Second");
        Assert.Equal(14, second.Wish!.DurationDays);
    }

    [Fact]
    public async Task Create_WritesAnApprovedManualWish_WithoutRequesterIdentity()
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();

        var result = await Sut().CreateAsync(
            channel.Name.ToUpperInvariant(),
            new CreateWishRequest(emoteId, "EmoteName", "Alias", 21, " SomeViewer "),
            Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var dto = result.Wish!;
        Assert.Equal((WishStatus.Approved, WishSource.Manual, "manual"), (dto.Status, dto.Source, dto.Mode));
        Assert.Equal(("Alias", "EmoteName", "someviewer"), (dto.Alias, dto.EmoteName, dto.RequesterLogin));
        Assert.Equal(Now, dto.ApprovedAtUtc);
        Assert.Null(dto.SlotId);
        Assert.Null(dto.StartsAtUtc);
        Assert.Null(dto.ProbationStartsAtUtc);
        Assert.False(dto.BotOwned);
        Assert.False(dto.BotWritePending);

        await using var db = fixture.CreateDbContext();
        var row = await db.Wishes.SingleAsync(w => w.ChannelId == channel.Id);
        Assert.Null(row.RequesterTwitchUserId);
        Assert.Equal(0, row.DecisionVersion);
    }

    [Theory]
    [InlineData("", "name", WishOutcome.AliasInvalid)]
    [InlineData("has space", "name", WishOutcome.AliasInvalid)]
    [InlineData("tab\tchar", "name", WishOutcome.AliasInvalid)]
    public async Task Create_RejectsAnInvalidAlias(string alias, string name, WishOutcome expected)
    {
        var channel = await SeedChannelAsync();

        var result = await Sut().CreateAsync(channel.Name, new CreateWishRequest(EmoteId(), alias.Length == 0 ? "" : name, alias, null, null), Actor);

        Assert.Equal(expected, result.Outcome);
        await AssertNothingWrittenAsync(channel);
    }

    [Fact]
    public async Task Create_RejectsATooLongAlias_ButAcceptsTheLimit()
    {
        var channel = await SeedChannelAsync();

        Assert.Equal(WishOutcome.AliasInvalid, (await CreateAsync(channel, EmoteId(), new string('a', 101))).Outcome);
        Assert.Equal(WishOutcome.Success, (await CreateAsync(channel, EmoteId(), new string('a', 100))).Outcome);
    }

    // Length counts code points: 60 emoji are 120 UTF-16 units but one valid alias; 101 are not.
    [Fact]
    public async Task Create_CountsAliasLengthInCodePoints()
    {
        var channel = await SeedChannelAsync();

        var accepted = await CreateAsync(channel, EmoteId(), string.Concat(Enumerable.Repeat("\U0001F600", 60)));
        Assert.Equal(WishOutcome.Success, accepted.Outcome);
        Assert.Equal(WishOutcome.Success, (await CreateAsync(channel, EmoteId(), string.Concat(Enumerable.Repeat("\U0001F600", 100)))).Outcome);
        Assert.Equal(WishOutcome.AliasInvalid, (await CreateAsync(channel, EmoteId(), string.Concat(Enumerable.Repeat("\U0001F600", 101)))).Outcome);
    }

    // The belt: a competing row committed without the channel lock. The service's insert waits on the
    // unique key, then fails on it, and the failure is a conflict outcome carrying the winner's id.
    [Fact]
    public async Task Create_LosingARaceAtTheRunningIndex_IsAlreadyRunning_WithTheWinnersId()
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        await using var holder = fixture.CreateTaggedDbContext("wish-index-holder");
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        var winner = NewWish(channel.Id, emoteId, WishStatus.Approved, slotId: null);
        holder.Wishes.Add(winner);
        await holder.SaveChangesAsync();

        await using var contender = fixture.CreateTaggedDbContext("wish-index-contender");
        var waiting = Sut(contender).CreateAsync(channel.Name, new CreateWishRequest(emoteId, "N", "A", null, null), Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-index-contender", waiting);

        await holderTransaction.CommitAsync();
        var result = await waiting;

        Assert.Equal(WishOutcome.AlreadyRunning, result.Outcome);
        Assert.Equal(winner.Id, result.ConflictingWishId);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.Wishes.CountAsync(w => w.ChannelId == channel.Id));
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    [Fact]
    public async Task Adopt_LosingARaceAtTheLiveSlotIndex_IsSlotAlreadyLinked()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-2));
        await using var holder = fixture.CreateTaggedDbContext("wish-slotindex-holder");
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        // Another emote id keeps the running index out of the way; only the slot key is contended.
        holder.Wishes.Add(NewWish(channel.Id, EmoteId(), WishStatus.Active, slot));
        await holder.SaveChangesAsync();

        await using var contender = fixture.CreateTaggedDbContext("wish-slotindex-contender");
        var waiting = Sut(contender).AdoptAsync(channel.Name, new AdoptWishRequest(slot, null, null, null), Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-slotindex-contender", waiting);

        await holderTransaction.CommitAsync();
        var result = await waiting;

        Assert.Equal(WishOutcome.SlotAlreadyLinked, result.Outcome);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.Wishes.CountAsync(w => w.ChannelId == channel.Id));
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("waytoolongloginnamethatexceeds25")]
    public async Task Create_RejectsAnInvalidRequesterLogin(string login)
    {
        var channel = await SeedChannelAsync();

        var result = await Sut().CreateAsync(channel.Name, new CreateWishRequest(EmoteId(), "Name", "Alias", null, login), Actor);

        Assert.Equal(WishOutcome.RequesterInvalid, result.Outcome);
        await AssertNothingWrittenAsync(channel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-alphanumeric")]
    [InlineData("0123456789012345678901234567890123")]
    public async Task Create_RejectsAnInvalidEmoteId(string emoteId)
    {
        var channel = await SeedChannelAsync();

        var result = await Sut().CreateAsync(channel.Name, new CreateWishRequest(emoteId, "Name", "Alias", null, null), Actor);

        Assert.Equal(WishOutcome.EmoteIdInvalid, result.Outcome);
        await AssertNothingWrittenAsync(channel);
    }

    [Fact]
    public async Task Create_ForAnUnknownChannel_IsChannelNotFound()
    {
        var result = await Sut().CreateAsync("no_such_channel_x", new CreateWishRequest(EmoteId(), "Name", "Alias", null, null), Actor);

        Assert.Equal(WishOutcome.ChannelNotFound, result.Outcome);
    }

    // AK5: manual entry → conflict with the slot ids → adopt.
    [Fact]
    public async Task Create_ForAnEmoteActiveInTheSet_ConflictsWithTheSlotIds_ThenAdoptLinksIt()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var added = Now.AddDays(-3);
        var slot = await SeedSlotAsync(emote, "Kappa", added);

        var conflict = await CreateAsync(channel, emote.SevenTvEmoteId, "Kappa");
        Assert.Equal(WishOutcome.EmoteAlreadyInSet, conflict.Outcome);
        Assert.Equal([slot], conflict.SlotIds);
        await AssertNothingWrittenAsync(channel);
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);

        var adopted = await Sut().AdoptAsync(channel.Name, new AdoptWishRequest(slot, null, null, null), Actor);

        Assert.Equal(WishOutcome.Success, adopted.Outcome);
        var dto = adopted.Wish!;
        Assert.Equal((WishStatus.Active, WishSource.Adopted, WishLinkOrigin.Adopted), (dto.Status, dto.Source, dto.LinkOrigin));
        Assert.False(dto.BotOwned);
        Assert.Equal(added, dto.StartsAtUtc);
        Assert.True(dto.StartsAtAuthoritative);
        Assert.Equal(added.AddDays(30), dto.ExpiresAtUtc);
        Assert.Equal(added.AddDays(23), dto.ProbationStartsAtUtc);
        Assert.Equal(slot, dto.SlotId);
        Assert.Equal("Kappa", dto.Alias);

        await using var db = fixture.CreateDbContext();
        var row = await db.Wishes.SingleAsync(w => w.ChannelId == channel.Id);
        Assert.Equal(added, row.SlotAddedAtUtc);
        Assert.Equal(Now, row.SlotConfirmedAtUtc);
        Assert.Equal(Now, row.FirstActivatedAtUtc);
    }

    [Fact]
    public async Task Create_AnEmoteInTheSetAndRunning_ReportsTheSetFirst()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        await SeedSlotAsync(emote, "Kappa", Now.AddDays(-1));
        await SeedWishAsync(channel, emote.SevenTvEmoteId, WishStatus.Approved);

        var result = await CreateAsync(channel, emote.SevenTvEmoteId, "Kappa");

        Assert.Equal(WishOutcome.EmoteAlreadyInSet, result.Outcome);
    }

    [Fact]
    public async Task Create_IgnoresArchivedAndUnsettledSlots()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        await SeedSlotAsync(emote, "Old", Now.AddDays(-9), archived: true);
        await SeedSlotAsync(emote, "Seed", null, seed: true);

        var result = await CreateAsync(channel, emote.SevenTvEmoteId, "New");

        Assert.Equal(WishOutcome.Success, result.Outcome);
    }

    // AK6, 409 part.
    [Fact]
    public async Task Create_ForARunningEmote_ConflictsWithThatWishId_AnEndedOneDoesNotBlock()
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        var first = await CreateAsync(channel, emoteId, "One");
        _publisher.ClearReceivedCalls();

        var second = await CreateAsync(channel, emoteId, "Two");

        Assert.Equal(WishOutcome.AlreadyRunning, second.Outcome);
        Assert.Equal(first.Wish!.Id, second.ConflictingWishId);
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);

        await using (var db = fixture.CreateDbContext())
        {
            await db.Wishes.Where(w => w.Id == first.Wish.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, WishStatus.Closed));
        }

        Assert.Equal(WishOutcome.Success, (await CreateAsync(channel, emoteId, "Three")).Outcome);
    }

    [Fact]
    public async Task Create_TwoAtOnceForTheSameEmote_OneWins_TheOtherIsAlreadyRunning()
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        await using var first = fixture.CreateDbContext();
        await using var second = fixture.CreateDbContext();

        var results = await Task.WhenAll(
            Sut(first).CreateAsync(channel.Name, new CreateWishRequest(emoteId, "N", "A", null, null), Actor),
            Sut(second).CreateAsync(channel.Name, new CreateWishRequest(emoteId, "N", "B", null, null), Actor));

        Assert.Single(results, r => r.Outcome == WishOutcome.Success);
        var loser = Assert.Single(results, r => r.Outcome == WishOutcome.AlreadyRunning);
        Assert.NotNull(loser.ConflictingWishId);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.Wishes.CountAsync(w => w.ChannelId == channel.Id));
        Assert.Equal(1, await db.AuditLogEntries.CountAsync(a => a.ChannelName == channel.Name && a.Action == AuditActions.WishCreate));
    }

    [Fact]
    public async Task Create_SetFullIsAHintFromTheCapacity_NeverABlock()
    {
        var channel = await SeedChannelAsync(capacity: 2);
        var emote = await SeedEmoteAsync(channel);
        await SeedSlotAsync(emote, "A", Now.AddDays(-1));
        await SeedSlotAsync(emote, "B", Now.AddDays(-1));

        var result = await CreateAsync(channel, EmoteId(), "New");

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.True(result.SetFull);

        var roomy = await SeedChannelAsync(capacity: 5);
        Assert.False((await CreateAsync(roomy, EmoteId(), "New")).SetFull);
        var unknown = await SeedChannelAsync();
        Assert.False((await CreateAsync(unknown, EmoteId(), "New")).SetFull);
    }

    // ------------------------------------------------------------------------------------ adoption

    [Fact]
    public async Task Adopt_WithAFutureStart_OrOneBeforeTheSlot_IsStartsAtInvalid_AValidOneIsKept()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var added = Now.AddDays(-10);
        var slot = await SeedSlotAsync(emote, "Kappa", added);

        Assert.Equal(WishOutcome.StartsAtInvalid, (await AdoptAsync(channel, slot, startsAt: Now.AddMinutes(1))).Outcome);
        Assert.Equal(WishOutcome.StartsAtInvalid, (await AdoptAsync(channel, slot, startsAt: added.AddSeconds(-1))).Outcome);
        await AssertNothingWrittenAsync(channel);

        var chosen = added.AddDays(2);
        var ok = await AdoptAsync(channel, slot, startsAt: chosen);

        Assert.Equal(WishOutcome.Success, ok.Outcome);
        Assert.Equal(chosen, ok.Wish!.StartsAtUtc);
        Assert.Equal(chosen.AddDays(30), ok.Wish.ExpiresAtUtc);
    }

    [Fact]
    public async Task Adopt_AnUndatedSlot_StartsNowProvisionally_AProvisionalDateIsNotAuthoritative()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var undated = await SeedSlotAsync(emote, "Undated", null);
        var provisional = await SeedSlotAsync(await SeedEmoteAsync(channel), "Provisional", Now.AddDays(-1), fromRest: false);

        var first = await AdoptAsync(channel, undated);
        Assert.Equal(WishOutcome.Success, first.Outcome);
        Assert.Equal(Now, first.Wish!.StartsAtUtc);
        Assert.False(first.Wish.StartsAtAuthoritative);

        var second = await AdoptAsync(channel, provisional, durationDays: 10);
        Assert.Equal(WishOutcome.Success, second.Outcome);
        Assert.Equal(Now.AddDays(-1), second.Wish!.StartsAtUtc);
        Assert.False(second.Wish.StartsAtAuthoritative);
        Assert.Equal(Now.AddDays(9), second.Wish.ExpiresAtUtc);
    }

    [Fact]
    public async Task Adopt_RefusesASlotThatIsNotAdoptable()
    {
        var channel = await SeedChannelAsync();
        var other = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var archived = await SeedSlotAsync(emote, "Gone", Now.AddDays(-2), archived: true);
        var seed = await SeedSlotAsync(emote, "Seed", null, seed: true);
        var foreignEmote = await SeedEmoteAsync(other);
        var foreign = await SeedSlotAsync(foreignEmote, "Foreign", Now.AddDays(-2));

        foreach (var slotId in new[] { archived, seed, foreign, "no-such-slot" })
        {
            Assert.Equal(WishOutcome.SlotNotFound, (await AdoptAsync(channel, slotId)).Outcome);
        }

        await AssertNothingWrittenAsync(channel);
    }

    [Fact]
    public async Task Adopt_ASlotWithALiveWish_IsSlotAlreadyLinked_AlsoThroughTheIndex()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-2));
        Assert.Equal(WishOutcome.Success, (await AdoptAsync(channel, slot)).Outcome);

        var again = await AdoptAsync(channel, slot);
        Assert.Equal(WishOutcome.SlotAlreadyLinked, again.Outcome);

        // The index itself: a second live wish on the slot cannot be inserted behind the service's back.
        await using var db = fixture.CreateDbContext();
        db.Wishes.Add(NewWish(channel.Id, EmoteId(), WishStatus.Presented, slot));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("IX_Wishes_SlotId_Live", Assert.IsType<Npgsql.PostgresException>(ex.InnerException).ConstraintName);
    }

    [Fact]
    public async Task Adopt_NewWhileAnApprovedWishRuns_IsAlreadyRunning_WithTheWishId()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-2));
        var running = await SeedWishAsync(channel, emote.SevenTvEmoteId, WishStatus.Approved);

        var result = await AdoptAsync(channel, slot);

        Assert.Equal(WishOutcome.AlreadyRunning, result.Outcome);
        Assert.Equal(running, result.ConflictingWishId);
    }

    [Fact]
    public async Task Adopt_WithAWishId_LinksTheExistingWish_CountsTheDecision_AndTakesTheSlotName()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "RealName", Now.AddDays(-4));
        var wishId = await SeedWishAsync(channel, emote.SevenTvEmoteId, WishStatus.Approved, alias: "Wanted", queuedRemoval: true);

        var result = await AdoptAsync(channel, slot, wishId: wishId, durationDays: 20);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var dto = result.Wish!;
        Assert.Equal((wishId, WishStatus.Active, "RealName", slot), (dto.Id, dto.Status, dto.Alias, dto.SlotId));
        Assert.Equal(WishLinkOrigin.Adopted, dto.LinkOrigin);
        Assert.Equal(20, dto.DurationDays);
        Assert.Equal(Now.AddDays(-4).AddDays(20), dto.ExpiresAtUtc);

        await using var db = fixture.CreateDbContext();
        var row = await db.Wishes.SingleAsync(w => w.Id == wishId);
        Assert.Equal(1, row.DecisionVersion);
        Assert.Null(row.RemovalQueuedAtUtc);
        Assert.Null(row.RemovalQueuedVersion);
        Assert.Equal(Now, row.FirstActivatedAtUtc);
        Assert.Equal(1, await db.Wishes.CountAsync(w => w.ChannelId == channel.Id));
    }

    [Fact]
    public async Task Adopt_WithAWishId_OfAPermanentWish_EndsInKept()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-4));
        var wishId = await SeedWishAsync(channel, emote.SevenTvEmoteId, WishStatus.Approved, permanent: true);

        var result = await AdoptAsync(channel, slot, wishId: wishId);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal(WishStatus.Kept, result.Wish!.Status);
        Assert.Equal(Now, result.Wish.EndedAtUtc);
        await using var db = fixture.CreateDbContext();
        Assert.Null((await db.Wishes.SingleAsync(w => w.Id == wishId)).FirstActivatedAtUtc);
    }

    [Fact]
    public async Task Adopt_WithAWishId_RefusesAWishOfAnotherChannel_AStateThatCannotAdopt_AndAnotherEmote()
    {
        var channel = await SeedChannelAsync();
        var other = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-4));
        var foreignWish = await SeedWishAsync(other, emote.SevenTvEmoteId, WishStatus.Approved);
        var closed = await SeedWishAsync(channel, emote.SevenTvEmoteId, WishStatus.Closed);
        var otherEmote = await SeedWishAsync(channel, EmoteId(), WishStatus.Approved);

        Assert.Equal(WishOutcome.WishNotFound, (await AdoptAsync(channel, slot, wishId: foreignWish)).Outcome);
        Assert.Equal(WishOutcome.WishNotFound, (await AdoptAsync(channel, slot, wishId: 987654321)).Outcome);
        Assert.Equal(WishOutcome.SlotNotFound, (await AdoptAsync(channel, slot, wishId: otherEmote)).Outcome);
        Assert.Equal(WishOutcome.StateConflict, (await AdoptAsync(channel, slot, wishId: closed)).Outcome);
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    // ------------------------------------------------------------------------------------ events and audit

    [Fact]
    public async Task EveryCommit_PublishesExactlyOneWishChangedForItsChannel()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-2));

        await CreateAsync(channel, EmoteId(), "One");
        AssertPublishedOnce(channel);

        _publisher.ClearReceivedCalls();
        await AdoptAsync(channel, slot);
        AssertPublishedOnce(channel);

        _publisher.ClearReceivedCalls();
        await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(10, 3, null), Actor);
        AssertPublishedOnce(channel);
    }

    [Fact]
    public async Task AFailingPublisher_DoesNotUndoTheCommit_NorThrow()
    {
        var channel = await SeedChannelAsync();
        _publisher.PublishAsync(default!, default!, default).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("redis down")));

        var result = await CreateAsync(channel, EmoteId(), "One");

        Assert.Equal(WishOutcome.Success, result.Outcome);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.Wishes.CountAsync(w => w.ChannelId == channel.Id));
    }

    // AK19: no alias, emote name or login in the raw JSON.
    [Fact]
    public async Task AuditDetails_CarryOnlyIdsAndNumbers_NeverAliasNameOrLogin()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel, name: "SecretEmoteName");
        var slot = await SeedSlotAsync(emote, "SecretSlotAlias", Now.AddDays(-2));

        await Sut().CreateAsync(
            channel.Name, new CreateWishRequest(EmoteId(), "SecretCreateName", "SecretCreateAlias", 12, "secretviewer"), Actor);
        await AdoptAsync(channel, slot);
        await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(10, 3, 2.5m), Actor);

        await using var db = fixture.CreateDbContext();
        var entries = await db.AuditLogEntries.Where(a => a.ChannelName == channel.Name).ToListAsync();
        Assert.Equal(
            [AuditActions.WishAdopt, AuditActions.WishCreate, AuditActions.WishSettings],
            entries.Select(e => e.Action).Order().ToList());
        Assert.All(entries, e =>
        {
            Assert.NotNull(e.DetailsJson);
            Assert.DoesNotContain("Secret", e.DetailsJson!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("viewer", e.DetailsJson!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal((Actor.TwitchUserId, Actor.Login), (e.ActorTwitchUserId, e.ActorLogin));
        });

        var create = entries.Single(e => e.Action == AuditActions.WishCreate);
        Assert.Equal("wish", create.TargetType);
        using var json = System.Text.Json.JsonDocument.Parse(create.DetailsJson!);
        Assert.Equal(["durationDays", "sevenTvEmoteId", "wishId"], json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(12, json.RootElement.GetProperty("durationDays").GetInt32());
        Assert.Equal(long.Parse(create.TargetId!), json.RootElement.GetProperty("wishId").GetInt64());
    }

    // ------------------------------------------------------------------------------------ settings

    [Fact]
    public async Task Settings_WithoutARow_AreTheDefaults_ThenUpsertAndOverwrite()
    {
        var channel = await SeedChannelAsync();

        var defaults = await Sut().GetSettingsAsync(channel.Name);
        Assert.Equal(new WishSettingsDto(30, 7, null, false, null), defaults);
        Assert.Null(await Sut().GetSettingsAsync("no_such_channel_x"));

        var first = await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(14, 3, 1.5m), Actor);
        Assert.Equal(new WishSettingsDto(14, 3, 1.5m, false, null), first.Settings);

        var second = await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(60, 30, null), Actor);
        Assert.Equal(new WishSettingsDto(60, 30, null, false, null), second.Settings);
        Assert.Equal(second.Settings, await Sut().GetSettingsAsync(channel.Name));

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.ChannelWishSettings.CountAsync(s => s.ChannelId == channel.Id));
        Assert.Equal(2, await db.AuditLogEntries.CountAsync(a => a.ChannelName == channel.Name && a.Action == AuditActions.WishSettings));
    }

    [Theory]
    [InlineData(0, 7, null, false)]
    [InlineData(1, 7, null, true)]
    [InlineData(365, 7, null, true)]
    [InlineData(366, 7, null, false)]
    [InlineData(30, 0, null, false)]
    [InlineData(30, 1, null, true)]
    [InlineData(30, 30, null, true)]
    [InlineData(30, 31, null, false)]
    [InlineData(30, 7, "0", false)]
    [InlineData(30, 7, "-1", false)]
    [InlineData(30, 7, "0.004", false)]
    [InlineData(30, 7, "0.01", true)]
    [InlineData(30, 7, "1000000", true)]
    [InlineData(30, 7, "1000001", false)]
    public async Task Settings_BoundariesAreChecked_AndARefusalWritesNothing(int duration, int lead, string? threshold, bool accepted)
    {
        var channel = await SeedChannelAsync();
        decimal? value = threshold is null ? null : decimal.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture);

        var result = await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(duration, lead, value), Actor);

        Assert.Equal(accepted ? WishOutcome.Success : WishOutcome.SettingsOutOfRange, result.Outcome);
        if (!accepted)
        {
            await AssertNothingWrittenAsync(channel);
            _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
        }
    }

    [Fact]
    public async Task Settings_ForAnUnknownChannel_AreChannelNotFound()
    {
        var result = await Sut().UpdateSettingsAsync("no_such_channel_x", new UpdateWishSettingsRequest(30, 7, null), Actor);

        Assert.Equal(WishOutcome.ChannelNotFound, result.Outcome);
    }

    // The decision behind the settings filter (AK14 service half).
    [Fact]
    public async Task CanChangeSettings_ManualMode_AnyManager_BotMode_OnlyBroadcasterActivatorAndAdmin()
    {
        var broadcasterId = $"bc-{Guid.NewGuid():N}";
        var channel = await SeedChannelAsync(twitchId: broadcasterId);

        Assert.True(await Sut().CanChangeSettingsAsync(channel.Name, "1234", false));
        Assert.True(await Sut().CanChangeSettingsAsync("no_such_channel_x", "1234", false));

        await using (var db = fixture.CreateDbContext())
        {
            db.ChannelWishSettings.Add(new ChannelWishSettings
            {
                ChannelId = channel.Id,
                BotModeEnabled = true,
                BotModeActivatedByTwitchUserId = "7777"
            });
            await db.SaveChangesAsync();
        }

        Assert.False(await Sut().CanChangeSettingsAsync(channel.Name, "1234", false));
        Assert.True(await Sut().CanChangeSettingsAsync(channel.Name, broadcasterId, false));
        Assert.True(await Sut().CanChangeSettingsAsync(channel.Name, "7777", false));
        Assert.True(await Sut().CanChangeSettingsAsync(channel.Name, "1234", true));

        await using (var db = fixture.CreateDbContext())
        {
            await db.ChannelWishSettings.Where(s => s.ChannelId == channel.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.BotSuspendedReason, "not_editor"));
        }

        // A suspended bot means the channel runs the manual flow again.
        Assert.True(await Sut().CanChangeSettingsAsync(channel.Name, "1234", false));
    }

    // ------------------------------------------------------------------------------------ reads

    [Fact]
    public async Task List_SortsIntoTheThreeViews_AndPages()
    {
        var channel = await SeedChannelAsync();
        var pending = await SeedWishAsync(channel, EmoteId(), WishStatus.Pending);
        var failed = await SeedWishAsync(channel, EmoteId(), WishStatus.Failed);
        var presented = await SeedWishAsync(channel, EmoteId(), WishStatus.Presented);
        var approved = await SeedWishAsync(channel, EmoteId(), WishStatus.Approved);
        var active = await SeedWishAsync(channel, EmoteId(), WishStatus.Active);
        var unconfirmed = await SeedWishAsync(channel, EmoteId(), WishStatus.Unconfirmed);
        var kept = await SeedWishAsync(channel, EmoteId(), WishStatus.Kept, endedAt: Now.AddDays(-2));
        var closed = await SeedWishAsync(channel, EmoteId(), WishStatus.Closed, endedAt: Now.AddDays(-1));

        Assert.Equal(
            new[] { pending, failed, presented, approved }.Order(),
            (await ListAsync(channel, WishListView.Open)).Items.Select(w => w.Id).Order());
        Assert.Equal(
            new[] { active, unconfirmed }.Order(),
            (await ListAsync(channel, WishListView.Running)).Items.Select(w => w.Id).Order());
        Assert.Equal([closed, kept], (await ListAsync(channel, WishListView.Ended)).Items.Select(w => w.Id));

        var page = await ListAsync(channel, WishListView.Ended, pageNumber: 2, pageSize: 1);
        Assert.Equal([kept], page.Items.Select(w => w.Id));
        Assert.Equal((2, 1, 2), (page.Page, page.PageSize, page.TotalCount));
        Assert.Null(await Sut().ListAsync("no_such_channel_x", WishListView.Open, 1, 10));
    }

    [Fact]
    public async Task List_InBotMode_AnApprovedWishCountsAsRunning_AndTheModeIsBot()
    {
        var channel = await SeedChannelAsync();
        var approved = await SeedWishAsync(channel, EmoteId(), WishStatus.Approved);
        await using (var db = fixture.CreateDbContext())
        {
            db.ChannelWishSettings.Add(new ChannelWishSettings { ChannelId = channel.Id, BotModeEnabled = true });
            await db.SaveChangesAsync();
        }

        Assert.Empty((await ListAsync(channel, WishListView.Open)).Items);
        var running = (await ListAsync(channel, WishListView.Running)).Items;
        Assert.Equal(approved, Assert.Single(running).Id);
        Assert.Equal("bot", running[0].Mode);
    }

    [Fact]
    public async Task List_OrdersByExpiryThenNewest_AndGetIsScopedToTheChannel()
    {
        var channel = await SeedChannelAsync();
        var other = await SeedChannelAsync();
        var late = await SeedWishAsync(channel, EmoteId(), WishStatus.Active, expiresAt: Now.AddDays(9));
        var soon = await SeedWishAsync(channel, EmoteId(), WishStatus.Active, expiresAt: Now.AddDays(2));
        var none = await SeedWishAsync(channel, EmoteId(), WishStatus.Unconfirmed);
        var foreign = await SeedWishAsync(other, EmoteId(), WishStatus.Active);

        Assert.Equal([soon, late, none], (await ListAsync(channel, WishListView.Running)).Items.Select(w => w.Id));
        Assert.Equal(soon, (await Sut().GetAsync(channel.Name, soon))!.Id);
        Assert.Null(await Sut().GetAsync(channel.Name, foreign));
        Assert.Null(await Sut().GetAsync("no_such_channel_x", soon));
    }

    // ------------------------------------------------------------------------------------ locks

    [Fact]
    public async Task Create_WaitsOnAForeignHeldChannelLock_AndWritesNothingBefore()
    {
        var channel = await SeedChannelAsync();
        await using var holder = fixture.CreateTaggedDbContext("wish-create-holder");
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);

        await using var contender = fixture.CreateTaggedDbContext("wish-create-contender");
        var waiting = Sut(contender).CreateAsync(channel.Name, new CreateWishRequest(EmoteId(), "N", "A", null, null), Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-create-contender", waiting);

        Assert.Contains("\"Channels\"", await fixture.WaitingQueryAsync("wish-create-contender"));
        await AssertNothingWrittenAsync(channel);

        await holderTransaction.CommitAsync();
        Assert.Equal(WishOutcome.Success, (await waiting).Outcome);
    }

    [Fact]
    public async Task SettingsUpsert_WaitsOnAForeignHeldChannelLock_AndWritesNothingBefore()
    {
        var channel = await SeedChannelAsync();
        await using var holder = fixture.CreateTaggedDbContext("wish-settings-holder");
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);

        await using var contender = fixture.CreateTaggedDbContext("wish-settings-contender");
        var waiting = Sut(contender).UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(10, 2, null), Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-settings-contender", waiting);

        Assert.Contains("\"Channels\"", await fixture.WaitingQueryAsync("wish-settings-contender"));
        await AssertNothingWrittenAsync(channel);

        await holderTransaction.CommitAsync();
        Assert.Equal(WishOutcome.Success, (await waiting).Outcome);
    }

    [Fact]
    public async Task Adopt_TakesTheChannelLockFirst_ThenWaitsOnAForeignHeldWishRow()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-2));
        var wishId = await SeedWishAsync(channel, emote.SevenTvEmoteId, WishStatus.Approved);

        await using var holder = fixture.CreateTaggedDbContext("wish-adopt-holder");
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockWishesByIdsAsync([wishId], CancellationToken.None);

        await using var contender = fixture.CreateTaggedDbContext("wish-adopt-contender");
        var waiting = Sut(contender).AdoptAsync(channel.Name, new AdoptWishRequest(slot, wishId, null, null), Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-adopt-contender", waiting);

        Assert.Contains("\"Wishes\"", await fixture.WaitingQueryAsync("wish-adopt-contender"));
        await using (var probe = fixture.CreateDbContext())
        {
            Assert.Equal(WishStatus.Approved, (await probe.Wishes.SingleAsync(w => w.Id == wishId)).Status);
        }

        await holderTransaction.CommitAsync();
        Assert.Equal(WishOutcome.Success, (await waiting).Outcome);
    }

    [Fact]
    public async Task Create_WhenTheChannelIsDeletedWhileItWaits_IsChannelNotFound_AndWritesNothing()
    {
        var channel = await SeedChannelAsync();
        await using var holder = fixture.CreateTaggedDbContext("wish-gone-holder");
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.Channels.Where(c => c.Id == channel.Id).ExecuteDeleteAsync();

        await using var contender = fixture.CreateTaggedDbContext("wish-gone-contender");
        var waiting = Sut(contender).CreateAsync(channel.Name, new CreateWishRequest(EmoteId(), "N", "A", null, null), Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-gone-contender", waiting);

        await holderTransaction.CommitAsync();

        Assert.Equal(WishOutcome.ChannelNotFound, (await waiting).Outcome);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.Wishes.AnyAsync(w => w.ChannelId == channel.Id));
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    [Fact]
    public async Task LockWishesByIds_ReturnsExistingIdsAscending_AndNeedsATransaction()
    {
        var channel = await SeedChannelAsync();
        var a = await SeedWishAsync(channel, EmoteId(), WishStatus.Approved);
        var b = await SeedWishAsync(channel, EmoteId(), WishStatus.Approved);

        await using var db = fixture.CreateDbContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.LockWishesByIdsAsync([a], CancellationToken.None));

        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.Equal([a, b], await db.LockWishesByIdsAsync([b, 987654321, a, b], CancellationToken.None));
        Assert.Empty(await db.LockWishesByIdsAsync([], CancellationToken.None));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    // ------------------------------------------------------------------------------------ helpers

    private WishService Sut(AppDbContext? db = null) =>
        new(db ?? fixture.CreateDbContext(), new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance), _clock);

    private Task<WishCommandResult> CreateAsync(Seeded channel, string emoteId, string alias, int? durationDays = null) =>
        Sut().CreateAsync(channel.Name, new CreateWishRequest(emoteId, "Emote", alias, durationDays, null), Actor);

    private Task<WishCommandResult> AdoptAsync(
        Seeded channel, string slotId, long? wishId = null, int? durationDays = null, DateTime? startsAt = null) =>
        Sut().AdoptAsync(channel.Name, new AdoptWishRequest(slotId, wishId, durationDays, startsAt), Actor);

    private async Task<PagedResult<WishDto>> ListAsync(Seeded channel, WishListView view, int pageNumber = 1, int pageSize = 50) =>
        (await Sut().ListAsync(channel.Name, view, pageNumber, pageSize))!;

    private void AssertPublishedOnce(Seeded channel)
    {
        var calls = _publisher.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IRedisPublisher.PublishAsync)).ToList();
        var call = Assert.Single(calls);
        Assert.Equal(LiveEvents.Channel, call.GetArguments()[0]);
        var liveEvent = LiveEvent.TryParse((string?)call.GetArguments()[1]);
        Assert.Equal(new LiveEvent(LiveEvents.WishChanged, channel.Name), liveEvent);
    }

    private async Task AssertNothingWrittenAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.Wishes.AnyAsync(w => w.ChannelId == channel.Id));
        Assert.False(await db.ChannelWishSettings.AnyAsync(s => s.ChannelId == channel.Id));
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
    }

    private static string EmoteId() => Guid.NewGuid().ToString("N")[..24];

    private async Task<Seeded> SeedChannelAsync(string? twitchId = null, int? capacity = null)
    {
        var name = $"w{Guid.NewGuid():N}"[..18];
        await using var db = fixture.CreateDbContext();
        var channel = new Channel
        {
            ChannelName = name,
            TwitchChannelId = twitchId ?? Guid.NewGuid().ToString("N")[..12],
            ActiveEmoteSetCapacity = capacity
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return new Seeded(channel.Id, name);
    }

    private async Task<Emote> SeedEmoteAsync(Seeded channel, string name = "Emote")
    {
        await using var db = fixture.CreateDbContext();
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Name = name,
            SevenTvEmoteId = EmoteId(),
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private async Task<string> SeedSlotAsync(
        Emote emote, string alias, DateTime? addedAt, bool fromRest = true, bool archived = false, bool seed = false)
    {
        await using var db = fixture.CreateDbContext();
        var slot = new EmoteSlot
        {
            EmoteId = emote.Id,
            Name = alias,
            AddedAt = addedAt,
            AddedAtFromRest = fromRest && addedAt is not null,
            IsArchived = archived,
            ArchivedAt = archived ? Now : null,
            SeedUnsettled = seed
        };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot.Id;
    }

    private async Task<long> SeedWishAsync(
        Seeded channel,
        string emoteId,
        string status,
        string alias = "Alias",
        bool permanent = false,
        bool queuedRemoval = false,
        DateTime? endedAt = null,
        DateTime? expiresAt = null)
    {
        await using var db = fixture.CreateDbContext();
        var wish = NewWish(channel.Id, emoteId, status, slotId: null);
        wish.Alias = alias;
        wish.Permanent = permanent;
        wish.ExpiresAtUtc = expiresAt;
        wish.EndedAtUtc = endedAt ?? (WishStatus.IsEnded(status) ? Now : null);
        if (queuedRemoval)
        {
            wish.RemovalQueuedAtUtc = Now;
            wish.RemovalQueuedVersion = 0;
        }

        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }

    private Wish NewWish(string channelId, string emoteId, string status, string? slotId) => new()
    {
        ChannelId = channelId,
        SevenTvEmoteId = emoteId,
        EmoteName = "Emote",
        Alias = "Alias",
        Source = WishSource.Manual,
        Status = status,
        StatusChangedAtUtc = Now,
        CreatedAtUtc = Now,
        SlotId = slotId
    };

    private sealed record Seeded(string Id, string Name);
}
