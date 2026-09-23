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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class ChannelServiceTests(PostgresFixture fixture)
{
    private static readonly AuditActor Actor = new("4711", "sensitron");

    [Fact]
    public async Task JoinAsync_CreatesChannel_AndPublishesJoinCommand()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher);

        var channel = await JoinChannelAsync(service, "ChannelServiceTest1");

        Assert.Equal("channelservicetest1", channel.ChannelName);
        Assert.True(channel.IsBotActive);
        await redisPublisher.Received(1).PublishAsync("channel:bot:commands", "JOIN:channelservicetest1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAsync_CalledTwice_WithoutLeaving_DoesNotCreateADuplicateRow()
    {
        // JoinAsync's null-check/else branch (reuse existing row, just flip IsBotActive) only
        // matters because the unique index on ChannelName would otherwise reject a second insert.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher);

        var first = await JoinChannelAsync(service, "channelservicetest2");
        var second = await JoinChannelAsync(service, "channelservicetest2");

        Assert.Equal(first.Id, second.Id);
        var rows = await db.Channels.AsNoTracking().Where(c => c.ChannelName == "channelservicetest2").ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task LeaveAsync_DeactivatesChannel_ButKeepsTheRow()
    {
        // The row must survive: it hangs on four cascade edges (emotes, usage stats, vote sessions,
        // votes), and none of that history is reconstructible. Deleting on leave used to make a
        // moderator's single click destroy the channel's entire recorded history.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher);
        await JoinChannelAsync(service, "channelservicetest3");

        var deactivated = await service.LeaveAsync("ChannelServiceTest3", Actor);

        Assert.True(deactivated);
        var channel = await service.GetByNameAsync("channelservicetest3");
        Assert.NotNull(channel);
        Assert.False(channel.IsBotActive);
        await redisPublisher.Received(1).PublishAsync("channel:bot:commands", "LEAVE:channelservicetest3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAsync_AfterLeave_ReactivatesTheSameRow()
    {
        // The whole point of the soft deactivate: rejoining must bring the channel and its history
        // back rather than start a second, empty row.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher);
        var joined = await JoinChannelAsync(service, "channelservicetest5");
        await service.LeaveAsync("channelservicetest5", Actor);

        var rejoined = await JoinChannelAsync(service, "channelservicetest5");

        Assert.Equal(joined.Id, rejoined.Id);
        Assert.True(rejoined.IsBotActive);
    }

    [Fact]
    public async Task JoinAsync_AfterLeave_RestartsTheTrackingClock()
    {
        // The gap between leave and rejoin is time we did not count. Reporting CreatedAt as "we
        // track since" would claim coverage over exactly that hole.
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);
        var joined = await JoinChannelAsync(service, "channelservicetracking1");
        Assert.Null(joined.TrackingResumedAt);
        await service.LeaveAsync("channelservicetracking1", Actor);

        var rejoined = await JoinChannelAsync(service, "channelservicetracking1");

        Assert.NotNull(rejoined.TrackingResumedAt);
        Assert.True(rejoined.TrackingResumedAt >= rejoined.CreatedAt);
    }

    [Fact]
    public async Task JoinAsync_OnAnAlreadyActiveChannel_LeavesTheTrackingClockAlone()
    {
        // Nothing was missed, so the history we claim must not shrink. A join on an active channel
        // still publishes a command and is still audited — it just isn't a coverage event.
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);
        await JoinChannelAsync(service, "channelservicetracking2");
        await service.LeaveAsync("channelservicetracking2", Actor);
        var rejoined = await JoinChannelAsync(service, "channelservicetracking2");
        var resumedAt = rejoined.TrackingResumedAt;

        var joinedAgain = await JoinChannelAsync(service, "channelservicetracking2");

        Assert.Equal(resumedAt, joinedAgain.TrackingResumedAt);
    }

    [Fact]
    public async Task JoinAsync_WhenTwitchKnowsTheLogin_StampsTheTwitchIdOnTheNewRow()
    {
        // The whole point of resolving the identity at join time: a row created here already carries
        // the immutable id, so the very first rename of that channel is followable — instead of the
        // hourly reconciliation having to backfill an id it can only guess from a login that may
        // already be gone.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var identityService = IdentityFound("770001", "ChannelServiceIdentity1");
        var service = CreateService(db, redisPublisher, identityService);

        var result = await service.JoinAsync("  ChannelServiceIdentity1  ", Actor);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.NotNull(result.Channel);
        Assert.Equal("770001", result.Channel.TwitchChannelId);
        // Stored normalized, whichever side it is derived from: Helix's login and the caller's input
        // agree by construction here (LookupByLoginAsync matches on the normalized form), so this
        // asserts the normalization itself — padding and casing gone (Regel 9).
        Assert.Equal("channelserviceidentity1", result.Channel.ChannelName);
        await identityService.Received(1).LookupByLoginAsync("channelserviceidentity1", Arg.Any<CancellationToken>());
        await redisPublisher.Received(1).PublishAsync(
            BotCommands.Channel, "JOIN:channelserviceidentity1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAsync_WhenTwitchReportsANewLogin_RenamesTheExistingRow_AndPublishesLeaveThenJoin()
    {
        // A join under the channel's *new* Twitch login must land on the row that holds its history,
        // not start an empty second one — the id is what makes the two the same channel.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var seeded = await SeedChannelAsync(db, "channelserviceidold2", "770002");
        var service = CreateService(db, redisPublisher, IdentityFound("770002", "ChannelServiceIdNew2"));
        var before = DateTime.UtcNow;

        var result = await service.JoinAsync("ChannelServiceIdNew2", Actor);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.NotNull(result.Channel);
        Assert.Equal(seeded.Id, result.Channel.Id);

        await using var verify = fixture.CreateDbContext();
        Assert.Empty(await verify.Channels.AsNoTracking().Where(c => c.ChannelName == "channelserviceidold2").ToListAsync());
        var stored = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        Assert.Equal("channelserviceidnew2", stored.ChannelName);
        Assert.True(stored.IsBotActive);
        // Between the rename on Twitch and this join the IRC connection pointed at a name that no
        // longer answered, so nothing was counted — the same gap the reconciliation stamps.
        Assert.NotNull(stored.TrackingResumedAt);
        Assert.InRange(stored.TrackingResumedAt.Value, before.AddMilliseconds(-1), DateTime.UtcNow.AddMilliseconds(1));

        var entries = await LoadAuditEntriesAsync(verify, "channelserviceidnew2");
        Assert.Equal([AuditActions.ChannelRename, AuditActions.ChannelJoin], entries.Select(e => e.Action));
        // The real actor, unlike the worker's system-attributed rename: a person asked for this.
        Assert.Equal(Actor.TwitchUserId, entries[0].ActorTwitchUserId);
        Assert.Equal(Actor.Login, entries[0].ActorLogin);
        Assert.NotNull(entries[0].DetailsJson);
        // Parsed, not substring-matched: the column is jsonb, so Postgres hands the payload back
        // reordered and reformatted.
        using var details = JsonDocument.Parse(entries[0].DetailsJson!);
        Assert.Equal("channelserviceidold2", details.RootElement.GetProperty("oldLogin").GetString());
        Assert.Equal("channelserviceidnew2", details.RootElement.GetProperty("newLogin").GetString());
        Assert.Equal("770002", details.RootElement.GetProperty("twitchChannelId").GetString());

        // Order is the contract: the worker resolves the row by name when it handles the JOIN, and
        // the LEAVE is what drops the old name's match cache and EventAPI subscription.
        Received.InOrder(() =>
        {
            redisPublisher.PublishAsync(BotCommands.Channel, "LEAVE:channelserviceidold2", Arg.Any<CancellationToken>());
            redisPublisher.PublishAsync(BotCommands.Channel, "JOIN:channelserviceidnew2", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task JoinAsync_WhenTwitchReportsANewLoginForAnInactiveRow_RenamesAndReactivatesIt()
    {
        // The case the hourly reconciliation deliberately never reaches: it only scans active
        // channels, so a left channel that was renamed on Twitch stays under its dead name until
        // somebody joins it again. That join is the moment to put it right.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var seeded = await SeedChannelAsync(db, "channelserviceidold3", "770003", isBotActive: false);
        var service = CreateService(db, redisPublisher, IdentityFound("770003", "ChannelServiceIdNew3"));

        var result = await service.JoinAsync("channelserviceidnew3", Actor);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.NotNull(result.Channel);
        Assert.Equal(seeded.Id, result.Channel.Id);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.Channels.AsNoTracking()
            .Where(c => c.ChannelName == "channelserviceidold3" || c.ChannelName == "channelserviceidnew3")
            .ToListAsync();
        var stored = Assert.Single(rows);
        Assert.Equal(seeded.Id, stored.Id);
        Assert.Equal("channelserviceidnew3", stored.ChannelName);
        Assert.True(stored.IsBotActive);
        Assert.NotNull(stored.TrackingResumedAt);

        var entries = await LoadAuditEntriesAsync(verify, "channelserviceidnew3");
        Assert.Equal([AuditActions.ChannelRename, AuditActions.ChannelJoin], entries.Select(e => e.Action));
        Received.InOrder(() =>
        {
            redisPublisher.PublishAsync(BotCommands.Channel, "LEAVE:channelserviceidold3", Arg.Any<CancellationToken>());
            redisPublisher.PublishAsync(BotCommands.Channel, "JOIN:channelserviceidnew3", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task JoinAsync_WhenTwitchDoesNotKnowTheLogin_RejectsTheJoin_AndWritesNothing()
    {
        // A definite "no such account" — a typo, or a channel that was renamed away. Accepting it
        // used to create a row that could never sync and never be counted, and that nobody would
        // notice until they went looking for their statistics.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(
            db, redisPublisher, IdentityLookup(TwitchUserLookup.Failed(TwitchUserLookupStatus.NotFound)));

        var result = await service.JoinAsync("channelservicenosuchlogin", Actor);

        Assert.Equal(ChannelJoinStatus.ChannelNotOnTwitch, result.Status);
        Assert.Null(result.Channel);
        Assert.Null(await service.GetByNameAsync("channelservicenosuchlogin"));
        Assert.Empty(await LoadAuditEntriesAsync(db, "channelservicenosuchlogin"));
        await redisPublisher.DidNotReceive().PublishAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAsync_WhenTwitchDoesNotKnowTheLogin_ButWeAlreadyTrackIt_JoinsAnyway()
    {
        // Helix answers the same way for a deleted account and for a banned one, so a refusal here
        // would let a state that can be lifted block a channel whose history we already hold. Only a
        // join that would *create* a row is refused.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var seeded = await SeedChannelAsync(db, "channelservicebanned8", "770008");
        var service = CreateService(
            db, redisPublisher, IdentityLookup(TwitchUserLookup.Failed(TwitchUserLookupStatus.NotFound)));

        var result = await service.JoinAsync("ChannelServiceBanned8", Actor);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.NotNull(result.Channel);
        Assert.Equal(seeded.Id, result.Channel.Id);

        await using var verify = fixture.CreateDbContext();
        var stored = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        // Untouched: it is still the best information we have about this channel, and clearing it
        // would throw away the one field that survives a login change.
        Assert.Equal("770008", stored.TwitchChannelId);
        Assert.Equal("channelservicebanned8", stored.ChannelName);
        Assert.Equal(
            [AuditActions.ChannelJoin],
            (await LoadAuditEntriesAsync(verify, "channelservicebanned8")).Select(e => e.Action));
        await redisPublisher.Received(1).PublishAsync(
            BotCommands.Channel, "JOIN:channelservicebanned8", Arg.Any<CancellationToken>());
        await redisPublisher.Received(1).PublishAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAsync_WhenTwitchDoesNotKnowTheLogin_ButAnInactiveRowExists_ReactivatesIt()
    {
        // The actual ban case: somebody left, the channel was then suspended on Twitch, and a
        // moderator wants the bot back. Nothing about that is a typo, which is the only thing the
        // rejection was ever meant to catch.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var seeded = await SeedChannelAsync(db, "channelservicebanned9", "770009", isBotActive: false);
        var service = CreateService(
            db, redisPublisher, IdentityLookup(TwitchUserLookup.Failed(TwitchUserLookupStatus.NotFound)));

        var result = await service.JoinAsync("channelservicebanned9", Actor);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.NotNull(result.Channel);
        Assert.Equal(seeded.Id, result.Channel.Id);

        await using var verify = fixture.CreateDbContext();
        var stored = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        Assert.True(stored.IsBotActive);
        // The gap between leave and rejoin is time nobody counted — the same reason every other
        // reactivating join stamps this.
        Assert.NotNull(stored.TrackingResumedAt);
        Assert.Equal("770009", stored.TwitchChannelId);
        await redisPublisher.Received(1).PublishAsync(
            BotCommands.Channel, "JOIN:channelservicebanned9", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAsync_WhenTwitchCannotBeAsked_JoinsExactlyAsBefore()
    {
        // The regression case, and the reason the lookup has three states instead of a nullable
        // identity: an outage on our side is not evidence about the channel. Availability wins, the
        // row gets no id, and the reconciliation backfills it later.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(
            db, redisPublisher, IdentityLookup(TwitchUserLookup.Failed(TwitchUserLookupStatus.Unavailable)));

        var result = await service.JoinAsync("ChannelSvcIdUnavail4", Actor);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.NotNull(result.Channel);
        Assert.Null(result.Channel.TwitchChannelId);
        Assert.Equal("channelsvcidunavail4", result.Channel.ChannelName);
        Assert.True(result.Channel.IsBotActive);
        var entries = await LoadAuditEntriesAsync(db, "channelsvcidunavail4");
        Assert.Equal([AuditActions.ChannelJoin], entries.Select(e => e.Action));
        await redisPublisher.Received(1).PublishAsync(
            BotCommands.Channel, "JOIN:channelsvcidunavail4", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAsync_OnAnExistingRowWithoutAnId_BackfillsTheId_WithoutAuditingARename()
    {
        // Nothing about the channel changed — we merely wrote down what it always was, which is why
        // this is not a rename and not audited (same reasoning as the reconciliation's backfill).
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "channelserviceidbackfill5", twitchChannelId: null);
        var service = CreateService(db, identityService: IdentityFound("770005", "channelserviceidbackfill5"));

        var result = await service.JoinAsync("channelserviceidbackfill5", Actor);

        Assert.NotNull(result.Channel);
        Assert.Equal(seeded.Id, result.Channel.Id);

        await using var verify = fixture.CreateDbContext();
        var stored = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        Assert.Equal("770005", stored.TwitchChannelId);
        // The other half of "nothing changed": the tracking clock must not move either, or the
        // channel would claim a coverage gap it never had.
        Assert.Null(stored.TrackingResumedAt);
        var entries = await LoadAuditEntriesAsync(verify, "channelserviceidbackfill5");
        Assert.Equal([AuditActions.ChannelJoin], entries.Select(e => e.Action));
    }

    [Fact]
    public async Task JoinAsync_WhenTheRowUnderThatNameClaimsADifferentId_JoinsItUnchanged_AndSaysSo()
    {
        // The mirror image of the occupant case: no row holds the id Helix reports, but the row
        // sitting under the login carries an id of its own that contradicts it. Overwriting that id
        // would fuse two different channels, so the join proceeds untouched — and logs, because
        // otherwise the state is invisible until the next reconcile tick resolves it.
        await using var db = fixture.CreateDbContext();
        var logger = new RecordingLogger<ChannelService>();
        var seeded = await SeedChannelAsync(db, "channelservicemismatch7", "770007");
        var service = CreateService(
            db, identityService: IdentityFound("770777", "channelservicemismatch7"), logger: logger);

        var result = await service.JoinAsync("channelservicemismatch7", Actor);

        Assert.NotNull(result.Channel);
        Assert.Equal(seeded.Id, result.Channel.Id);

        await using var verify = fixture.CreateDbContext();
        var stored = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        Assert.Equal("770007", stored.TwitchChannelId);
        Assert.Equal(
            [AuditActions.ChannelJoin],
            (await LoadAuditEntriesAsync(verify, "channelservicemismatch7")).Select(e => e.Action));
        Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("770007") && e.Message.Contains("770777"));
    }

    [Fact]
    public async Task JoinAsync_WhenTheNewLoginIsHeldByAnotherRow_JoinsThatRow_AndLeavesTheMergeToReconciliation()
    {
        // The duplicate a rename leaves behind: the id row still answers to the old name while a
        // second row already sits on the new one. Renaming here would hit IX_Channels_ChannelName and
        // turn a join into a 500; folding the two together is the reconciliation's job, and it is the
        // one path that must refuse rather than guess (emote histories cannot be fused). So the join
        // does what it did before this task existed — and says so in the log.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var logger = new RecordingLogger<ChannelService>();
        var idRow = await SeedChannelAsync(db, "channelserviceidold6", "770006");
        var occupant = await SeedChannelAsync(db, "channelserviceidnew6", twitchChannelId: null);
        var service = CreateService(db, redisPublisher, IdentityFound("770006", "channelserviceidnew6"), logger);

        var result = await service.JoinAsync("channelserviceidnew6", Actor);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.NotNull(result.Channel);
        Assert.Equal(occupant.Id, result.Channel.Id);

        await using var verify = fixture.CreateDbContext();
        // Untouched on both sides: no rename, and no id written onto the occupant — the unique index
        // on TwitchChannelId would reject the second one anyway.
        Assert.Equal("channelserviceidold6", (await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == idRow.Id)).ChannelName);
        Assert.Null((await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == occupant.Id)).TwitchChannelId);
        Assert.Equal(
            [AuditActions.ChannelJoin],
            (await LoadAuditEntriesAsync(verify, "channelserviceidnew6")).Select(e => e.Action));
        await redisPublisher.Received(1).PublishAsync(
            BotCommands.Channel, "JOIN:channelserviceidnew6", Arg.Any<CancellationToken>());
        await redisPublisher.DidNotReceive().PublishAsync(
            BotCommands.Channel, "LEAVE:channelserviceidold6", Arg.Any<CancellationToken>());
        Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("channelserviceidold6") && e.Message.Contains("channelserviceidnew6"));
    }

    [Fact]
    public async Task PurgeAsync_RemovesChannel_AndPublishesLeaveCommand()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher);
        await JoinChannelAsync(service, "channelservicetest6");

        var purged = await service.PurgeAsync("ChannelServiceTest6", Actor);

        Assert.True(purged);
        Assert.Null(await service.GetByNameAsync("channelservicetest6"));
        await redisPublisher.Received(1).PublishAsync("channel:bot:commands", "LEAVE:channelservicetest6", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeAsync_ForUnknownChannel_ReturnsFalse_AndDoesNotPublish()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher);

        var purged = await service.PurgeAsync("neverjoinedchannel", Actor);

        Assert.False(purged);
        await redisPublisher.DidNotReceive().PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaveAsync_ForUnknownChannel_ReturnsFalse_AndDoesNotPublish()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher);

        var deactivated = await service.LeaveAsync("neverjoinedchannel", Actor);

        Assert.False(deactivated);
        await redisPublisher.DidNotReceive().PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByNameAsync_Normalizes_ChannelNameLookup()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher);
        await JoinChannelAsync(service, "ChannelServiceTest4");

        var found = await service.GetByNameAsync("  channelservicetest4  ");

        Assert.NotNull(found);
    }

    [Fact]
    public async Task JoinAsync_WritesAuditEntry_WithNormalizedChannelName_AndActor()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);

        await JoinChannelAsync(service, "ChannelServiceAudit1");

        var entry = Assert.Single(await LoadAuditEntriesAsync(db, "channelserviceaudit1"));
        Assert.Equal(AuditActions.ChannelJoin, entry.Action);
        // The name is stored normalized, not as typed — the audit log filters on the same form every
        // other channel lookup does.
        Assert.Equal("channelserviceaudit1", entry.ChannelName);
        Assert.Equal(Actor.TwitchUserId, entry.ActorTwitchUserId);
        Assert.Equal(Actor.Login, entry.ActorLogin);
        Assert.Null(entry.DetailsJson);
    }

    [Fact]
    public async Task LeaveAsync_WritesAuditEntry_AfterTheJoinEntry()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);
        await JoinChannelAsync(service, "channelserviceaudit2");

        await service.LeaveAsync("ChannelServiceAudit2", Actor);

        var entries = await LoadAuditEntriesAsync(db, "channelserviceaudit2");
        Assert.Equal([AuditActions.ChannelJoin, AuditActions.ChannelLeave], entries.Select(e => e.Action));
    }

    [Fact]
    public async Task PurgeAsync_AuditEntry_SurvivesTheChannelDeletion()
    {
        // The reason ChannelName is a snapshot string and not a foreign key: the entry is written in
        // the same transaction that deletes the channel, and an FK would have cascaded away the only
        // record that the purge ever happened.
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);
        await JoinChannelAsync(service, "channelserviceaudit3");

        await service.PurgeAsync("channelserviceaudit3", Actor);

        Assert.Null(await service.GetByNameAsync("channelserviceaudit3"));
        var entries = await LoadAuditEntriesAsync(db, "channelserviceaudit3");
        Assert.Equal([AuditActions.ChannelJoin, AuditActions.ChannelPurge], entries.Select(e => e.Action));
    }

    [Fact]
    public async Task LeaveAndPurge_ForUnknownChannel_WriteNoAuditEntry()
    {
        // No-ops are not events: both calls return false without touching anything, so the log must
        // stay empty rather than record an action that did not happen.
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);

        await service.LeaveAsync("channelserviceaudit4", Actor);
        await service.PurgeAsync("channelserviceaudit4", Actor);

        Assert.Empty(await LoadAuditEntriesAsync(db, "channelserviceaudit4"));
    }

    [Fact]
    public async Task TriggerResyncAsync_ForActiveChannel_PublishesResyncCommand_AndWritesAuditEntry()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher);
        await JoinChannelAsync(service, "channelserviceresync1");

        var result = await service.TriggerResyncAsync("channelserviceresync1", Actor);

        Assert.Equal(ChannelResyncResult.Triggered, result);
        await redisPublisher.Received(1).PublishAsync(
            BotCommands.Channel, "RESYNC:channelserviceresync1", Arg.Any<CancellationToken>());

        var entries = await LoadAuditEntriesAsync(db, "channelserviceresync1");
        Assert.Equal([AuditActions.ChannelJoin, AuditActions.ChannelResync], entries.Select(e => e.Action));
        var resync = entries[^1];
        Assert.Equal("channelserviceresync1", resync.ChannelName);
        Assert.Equal(Actor.TwitchUserId, resync.ActorTwitchUserId);
        Assert.Equal(Actor.Login, resync.ActorLogin);
    }

    [Fact]
    public async Task TriggerResyncAsync_NormalizesTheChannelName_ForLookupPublishAndAudit()
    {
        // The command payload the worker parses and the audit row must both carry the stored form,
        // not whatever casing/padding the caller typed.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher);
        await JoinChannelAsync(service, "channelserviceresync2");

        var result = await service.TriggerResyncAsync("  ChannelServiceResync2  ", Actor);

        Assert.Equal(ChannelResyncResult.Triggered, result);
        await redisPublisher.Received(1).PublishAsync(
            BotCommands.Channel, "RESYNC:channelserviceresync2", Arg.Any<CancellationToken>());
        var entries = await LoadAuditEntriesAsync(db, "channelserviceresync2");
        Assert.Equal([AuditActions.ChannelJoin, AuditActions.ChannelResync], entries.Select(e => e.Action));
    }

    [Fact]
    public async Task TriggerResyncAsync_ForUnknownChannel_ReturnsNotFound_AndDoesNothing()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher);

        var result = await service.TriggerResyncAsync("channelserviceresync3", Actor);

        Assert.Equal(ChannelResyncResult.NotFound, result);
        await redisPublisher.DidNotReceive().PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Empty(await LoadAuditEntriesAsync(db, "channelserviceresync3"));
    }

    [Fact]
    public async Task TriggerResyncAsync_ForInactiveChannel_ReturnsNotActive_AndDoesNothing()
    {
        // A left channel keeps its row and its history, but resyncing it would subscribe its emote
        // set on the EventAPI while nothing consumes the events — a no-op, so also unaudited.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher);
        await JoinChannelAsync(service, "channelserviceresync4");
        await service.LeaveAsync("channelserviceresync4", Actor);
        redisPublisher.ClearReceivedCalls();

        var result = await service.TriggerResyncAsync("channelserviceresync4", Actor);

        Assert.Equal(ChannelResyncResult.NotActive, result);
        await redisPublisher.DidNotReceive().PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        var entries = await LoadAuditEntriesAsync(db, "channelserviceresync4");
        Assert.Equal([AuditActions.ChannelJoin, AuditActions.ChannelLeave], entries.Select(e => e.Action));
    }

    [Fact]
    public async Task ListActiveChannelNamesAsync_ReturnsOnlyActiveChannels_Sorted()
    {
        // The worker's boot recovery and its once-a-minute 7TV resync both start from this list, so
        // a left channel leaking into it would mean rejoining a chat the broadcaster removed us from.
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);
        await JoinChannelAsync(service, "channelserviceactive2");
        await JoinChannelAsync(service, "channelserviceactive1");
        await JoinChannelAsync(service, "channelserviceactive3");
        await service.LeaveAsync("channelserviceactive3", Actor);

        var names = await service.ListActiveChannelNamesAsync();

        Assert.Contains("channelserviceactive1", names);
        Assert.Contains("channelserviceactive2", names);
        Assert.DoesNotContain("channelserviceactive3", names);
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    /// <summary>
    /// Builds the service under test. The identity lookup defaults to
    /// <see cref="TwitchUserLookupStatus.Unavailable"/> on purpose: that status is defined as "carry
    /// on exactly as before", so every test written before the join path asked Twitch anything keeps
    /// exercising the behaviour it was written for.
    /// </summary>
    private static ChannelService CreateService(
        AppDbContext db,
        IRedisPublisher? redisPublisher = null,
        IChannelIdentityService? identityService = null,
        ILogger<ChannelService>? logger = null,
        ChannelCapacityOptions? capacityOptions = null)
    {
        return new ChannelService(
            db,
            redisPublisher ?? Substitute.For<IRedisPublisher>(),
            identityService ?? IdentityLookup(TwitchUserLookup.Failed(TwitchUserLookupStatus.Unavailable)),
            // Effectively uncapped: this class is not about the cap (that is
            // ChannelServiceCapacityTests, on its own isolated database), and the "Postgres"
            // collection's single shared database accumulates active channels across every test
            // that runs before these — the production default of 80 would make these tests fail
            // depending on run order, not on anything this class actually does.
            capacityOptions ?? new ChannelCapacityOptions { MaxActiveChannels = int.MaxValue },
            logger ?? NullLogger<ChannelService>.Instance);
    }

    private static IChannelIdentityService IdentityLookup(TwitchUserLookup lookup)
    {
        var identityService = Substitute.For<IChannelIdentityService>();
        identityService.LookupByLoginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(lookup);
        return identityService;
    }

    private static IChannelIdentityService IdentityFound(string twitchChannelId, string login) =>
        IdentityLookup(TwitchUserLookup.Found(new TwitchUserIdentity(twitchChannelId, login)));

    /// <summary>
    /// Joins and unwraps, so the assertions of every pre-existing test keep reading as before while
    /// also proving the new status is <see cref="ChannelJoinStatus.Joined"/> on each of those paths.
    /// </summary>
    private static async Task<Channel> JoinChannelAsync(ChannelService service, string channelName)
    {
        var result = await service.JoinAsync(channelName, Actor);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.NotNull(result.Channel);
        return result.Channel;
    }

    private static async Task<Channel> SeedChannelAsync(
        AppDbContext db, string channelName, string? twitchChannelId, bool isBotActive = true)
    {
        var channel = new Channel
        {
            ChannelName = ChannelName.Normalize(channelName),
            TwitchChannelId = twitchChannelId,
            IsBotActive = isBotActive
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private static async Task<IReadOnlyList<AuditLogEntry>> LoadAuditEntriesAsync(AppDbContext db, string channelName)
    {
        return await db.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.ChannelName == channelName)
            .OrderBy(e => e.Id)
            .ToListAsync();
    }
}
