using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The retention pass against real Postgres, with the real account deletion and channel purge behind it.
//
// Its own database, not the collection's shared one: the pass works on *every* row (all users, all
// sessions, the whole audit log), so on the shared database it would count and delete what other suites
// seeded, and no count here could be asserted exactly. The database is created and migrated once, and
// emptied before every test (IAsyncLifetime). The clock is fixed at "now, whole seconds": close enough to
// the wall clock that the entries the services stamp with DateTime.UtcNow (user.delete, channel.purge)
// are never older than a cutoff, and whole seconds so a row stamped exactly at a cutoff reads back
// exactly at it (Postgres keeps microseconds, .NET ticks).
[Collection("Postgres")]
public class DataRetentionServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "retention_service_tests";

    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;

    private readonly DateTime _now = WholeSecondsNow();
    private readonly LogSink _logs = new();

    private DateTime TokenCutoff => _now - RetentionPolicy.TwitchTokens;
    private DateTime AccountCutoff => _now - RetentionPolicy.InactiveAccount;
    private DateTime SessionCutoff => _now - RetentionPolicy.EndedVoteSession;
    private DateTime AuditCutoff => _now - RetentionPolicy.AuditLogEntry;
    private DateTime ChannelCutoff => _now - RetentionPolicy.DeactivatedChannel;

    public async Task InitializeAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (!_databaseReady)
            {
                await using (var admin = fixture.CreateDbContext())
                {
                    // A constant name, not user input; CREATE DATABASE cannot be parameterised.
#pragma warning disable EF1002
                    await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {DatabaseName}");
#pragma warning restore EF1002
                }

                await using var db = CreateDbContext();
                await db.Database.MigrateAsync();
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE FUNCTION retention_test_fail() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        -- Quotes the row's identity on purpose: the pass must not log exception messages.
                        RAISE EXCEPTION 'injected retention test failure for %', OLD."Id";
                    END $$;
                    """);
                _databaseReady = true;
            }
        }
        finally
        {
            DatabaseGate.Release();
        }

        await using var reset = CreateDbContext();
        await reset.Database.ExecuteSqlRawAsync(
            """
            DROP TRIGGER IF EXISTS retention_test_fail_user ON "Users";
            DROP TRIGGER IF EXISTS retention_test_fail_channel ON "Channels";
            TRUNCATE "Votes", "VoteSessionEmotes", "VoteSessions", "UsageStats", "ChannelLiveDays", "Emotes",
                "Channels", "Users", "AuditLogEntries" RESTART IDENTITY CASCADE;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------------------------------------
    // Category 1: tokens
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Tokens_AreClearedOnlyOnceTheLastActivityIsMoreThanThirtyDaysAgo()
    {
        await SeedUserAsync("tok-due-login", lastLogin: TokenCutoff.AddSeconds(-1), withTokens: true);
        await SeedUserAsync("tok-at-cutoff", lastLogin: TokenCutoff, withTokens: true);
        // max(LastLogin, LastSeenAtUtc): a recent sighting keeps the token, an old one does not.
        await SeedUserAsync("tok-seen-at-cutoff", lastLogin: TokenCutoff.AddDays(-5), lastSeen: TokenCutoff, withTokens: true);
        await SeedUserAsync("tok-due-seen", lastLogin: TokenCutoff.AddDays(-5), lastSeen: TokenCutoff.AddSeconds(-1), withTokens: true);
        await SeedUserAsync("tok-none", lastLogin: TokenCutoff.AddDays(-5));

        var (dry, enforced) = await RunDryThenEnforcedAsync();

        Assert.Equal(2, dry.TokensCleared);
        Assert.Equal(2, enforced.TokensCleared);
        Assert.Equal(["tok-at-cutoff", "tok-seen-at-cutoff"], await UsersWithTokensAsync());
        await using var db = CreateDbContext();
        var cleared = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "tok-due-login");
        Assert.Null(cleared.TwitchRefreshToken);
        Assert.Null(cleared.TwitchAccessToken);
        Assert.Null(cleared.TwitchAccessTokenExpiresAtUtc);
        Assert.Null(cleared.TwitchTokenScopes);
        // Tokens only — the account itself is far from its own period.
        Assert.Equal(5, await db.Users.CountAsync());
        Assert.Equal(0, enforced.Accounts.Deleted);
    }

    // ---------------------------------------------------------------------------------------------
    // Category 2: accounts
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Accounts_AreDeletedOnlyOnceTheLastActivityIsMoreThanTwelveMonthsAgo()
    {
        await SeedUserAsync("acc-due", lastLogin: AccountCutoff.AddSeconds(-1), lastSeen: AccountCutoff.AddSeconds(-1));
        await SeedUserAsync("acc-at-cutoff", lastLogin: AccountCutoff.AddDays(-30), lastSeen: AccountCutoff);
        // The case LastSeenAtUtc exists for: logged in long ago, around ever since.
        await SeedUserAsync("acc-seen-recently", lastLogin: AccountCutoff.AddDays(-300), lastSeen: _now.AddDays(-2));

        var (dry, enforced) = await RunDryThenEnforcedAsync();

        Assert.Equal(1, dry.Accounts.Deleted);
        Assert.Equal(1, enforced.Accounts.Deleted);
        Assert.False(enforced.Accounts.CapReached);
        await using var db = CreateDbContext();
        Assert.Equal(["acc-at-cutoff", "acc-seen-recently"], await UserIdsAsync());
        // Through the account deletion path: its entry, with the job as actor and no identity.
        var entry = await db.AuditLogEntries.AsNoTracking().SingleAsync(e => e.Action == AuditActions.UserDelete);
        Assert.Equal(AuditActor.System.TwitchUserId, entry.ActorTwitchUserId);
        Assert.Equal(AuditActor.DeletedUser.TwitchUserId, entry.TargetId);
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        Assert.Equal("inactivity", details.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Accounts_AreCappedPerPass_LongestAbsentFirst_AndTheRestFollowsNextPass()
    {
        await SeedUserAsync("cap-oldest", lastLogin: AccountCutoff.AddDays(-300));
        await SeedUserAsync("cap-middle", lastLogin: AccountCutoff.AddDays(-200));
        await SeedUserAsync("cap-newest", lastLogin: AccountCutoff.AddDays(-100));

        var (dry, enforced) = await RunDryThenEnforcedAsync(maxAccountsPerRun: 2);

        Assert.Equal(2, dry.Accounts.Deleted);
        Assert.True(dry.Accounts.CapReached);
        Assert.Equal(2, enforced.Accounts.Deleted);
        Assert.True(enforced.Accounts.CapReached);
        Assert.Equal(["cap-newest"], await UserIdsAsync());

        var next = await RunAsync(enforce: true, maxAccountsPerRun: 2);

        Assert.Equal(1, next.Accounts.Deleted);
        Assert.False(next.Accounts.CapReached);
        Assert.Empty(await UserIdsAsync());
    }

    [Fact]
    public async Task Accounts_StillActiveNotFoundAndAFailure_AreCounted_AndDoNotStopTheOthers()
    {
        // Ordered by LastLogin, so the failing account is processed first: if its rolled-back removal
        // stayed pending in the shared context, every later deletion would replay it and fail too.
        var failing = await SeedUserAsync("acct-fails", lastLogin: AccountCutoff.AddDays(-40));
        var (_, openSession, _, emote) = await SeedChannelWithSessionsAsync("acctfailchan");
        await SeedVoteAsync(openSession, emote, failing.Id);
        await SeedUserAsync("acct-returns", lastLogin: AccountCutoff.AddDays(-30));
        await SeedUserAsync("acct-vanishes", lastLogin: AccountCutoff.AddDays(-20));
        await SeedUserAsync("acct-plain", lastLogin: AccountCutoff.AddDays(-10));
        await InstallFailureTriggerAsync("Users", "retention_test_fail_user", "acct-fails");

        var summary = await RunAsync(
            enforce: true,
            beforeAccountDeletion: async id =>
            {
                await using var other = CreateDbContext();
                if (id == "acct-returns")
                {
                    // A request stamps the user between the selection and the deletion's row lock.
                    await other.Users.Where(u => u.Id == id)
                        .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAtUtc, DateTime.UtcNow));
                }
                else if (id == "acct-vanishes")
                {
                    // An admin deleted it in the meantime.
                    await other.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
                }
            });

        Assert.Equal(1, summary.Accounts.Deleted);
        Assert.Equal(1, summary.Accounts.StillActive);
        Assert.Equal(1, summary.Accounts.NotFound);
        Assert.Equal(1, summary.Accounts.Failed);
        Assert.Equal(["acct-fails", "acct-returns"], await UserIdsAsync());
        await using var db = CreateDbContext();
        // Rolled back as a whole: the failed account kept its vote.
        Assert.Equal(1, await db.Votes.CountAsync(v => v.UserId == failing.Id));
        Assert.Equal(1, await db.AuditLogEntries.CountAsync(e => e.Action == AuditActions.UserDelete));
        var warning = Assert.Single(_logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("SQLSTATE P0001", warning.Message);
        _logs.AssertNamesNone("acct-fails", "acctfails", "acct-returns", "acct-vanishes", "acct-plain", "acctfailchan");
    }

    // ---------------------------------------------------------------------------------------------
    // Category 3: ended vote sessions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task VoteSessions_EndedMoreThanTwelveMonthsAgo_GoWithVotesAndBallot_OpenOnesNever()
    {
        var voter = await SeedUserAsync("ses-voter", lastLogin: _now);
        var channel = await SeedChannelAsync("seschannel", isBotActive: true, deactivatedAtUtc: null);
        var emote = await SeedEmoteAsync(channel.Id);
        var due = await SeedSessionAsync(channel.Id, isActive: false, endedAt: SessionCutoff.AddSeconds(-1));
        var atCutoff = await SeedSessionAsync(channel.Id, isActive: false, endedAt: SessionCutoff);
        // Open, however old: never.
        var open = await SeedSessionAsync(channel.Id, isActive: true, endedAt: null, startedAt: SessionCutoff.AddDays(-400));
        // The theoretical session that ended without an EndedAt: its start stands in.
        var noEnd = await SeedSessionAsync(channel.Id, isActive: false, endedAt: null, startedAt: SessionCutoff.AddSeconds(-1));
        foreach (var session in new[] { due, atCutoff, open, noEnd })
        {
            await SeedVoteAsync(session, emote, voter.Id);
            await SeedBallotAsync(session, emote);
        }

        var (dry, enforced) = await RunDryThenEnforcedAsync();

        Assert.Equal(new VoteSessionRetentionCounts(2, 2, 2), dry.VoteSessions);
        Assert.Equal(new VoteSessionRetentionCounts(2, 2, 2), enforced.VoteSessions);
        await using var db = CreateDbContext();
        Assert.Equal([atCutoff, open], await db.VoteSessions.OrderBy(s => s.Id).Select(s => s.Id).ToListAsync());
        Assert.Equal([atCutoff, open], await db.Votes.OrderBy(v => v.VoteSessionId).Select(v => v.VoteSessionId).ToListAsync());
        Assert.Equal(2, await db.VoteSessionEmotes.CountAsync());
        // No audit entry per deleted session (plan, decision 3).
        Assert.Equal(0, await db.AuditLogEntries.CountAsync());
    }

    [Fact]
    public async Task VoteSessions_AreWalkedInBatches_UntilNoneIsDue()
    {
        var channel = await SeedChannelAsync("sesbatch", isBotActive: true, deactivatedAtUtc: null);
        await using (var seed = CreateDbContext())
        {
            // More than one batch of 500, plus one that is not due.
            await seed.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "VoteSessions" ("ChannelId", "Title", "AllowedVoterRoles", "IsActive", "HideResultsUntilEnd", "StartedAt", "EndedAt")
                SELECT {channel.Id}, 'batch', 1, false, false, {SessionCutoff.AddDays(-30)}, {SessionCutoff.AddDays(-1)}
                FROM generate_series(1, 1203)
                """);
        }

        await SeedSessionAsync(channel.Id, isActive: false, endedAt: _now.AddDays(-1));

        var (dry, enforced) = await RunDryThenEnforcedAsync();

        Assert.Equal(1203, dry.VoteSessions.Deleted);
        Assert.Equal(1203, enforced.VoteSessions.Deleted);
        await using var db = CreateDbContext();
        Assert.Equal(1, await db.VoteSessions.CountAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // Category 4: audit log
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AuditEntries_OlderThanTwelveMonths_AreDeleted_InBatches()
    {
        await using (var seed = CreateDbContext())
        {
            // More than one batch of 5,000.
            await seed.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "AuditLogEntries" ("OccurredAtUtc", "ActorTwitchUserId", "ActorLogin", "Action")
                SELECT {AuditCutoff.AddDays(-3)}, 'system', 'system', 'channel.resync'
                FROM generate_series(1, 5002)
                """);
        }

        await SeedAuditAsync(AuditCutoff.AddSeconds(-1), AuditActor.System.TwitchUserId, AuditActor.System.Login);
        var atCutoff = await SeedAuditAsync(AuditCutoff, AuditActor.System.TwitchUserId, AuditActor.System.Login);
        var recent = await SeedAuditAsync(_now.AddDays(-1), AuditActor.System.TwitchUserId, AuditActor.System.Login);

        var (dry, enforced) = await RunDryThenEnforcedAsync();

        Assert.Equal(5003, dry.AuditEntriesDeleted);
        Assert.Equal(5003, enforced.AuditEntriesDeleted);
        await using var db = CreateDbContext();
        Assert.Equal([atCutoff, recent], await db.AuditLogEntries.OrderBy(e => e.Id).Select(e => e.Id).ToListAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // Category 5: channels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Channels_DeactivatedMoreThan180DaysAgo_ArePurgedWithTheirHistory_AndAudited()
    {
        var due = await SeedChannelWithHistoryAsync("chandue", ChannelCutoff.AddSeconds(-1));
        var atCutoff = await SeedChannelWithHistoryAsync("chanatcutoff", ChannelCutoff);
        // Active with a leftover stamp (an inconsistent row): active always wins.
        var active = await SeedChannelAsync("chanactive", isBotActive: true, deactivatedAtUtc: ChannelCutoff.AddDays(-10));

        var (dry, enforced) = await RunDryThenEnforcedAsync();

        var expected = new ChannelRetentionCounts(
            Restamped: 0, Purged: 1, StillActive: 0, NotFound: 0, Failed: 0,
            EmotesDeleted: 1, UsageRowsDeleted: 2, LiveDaysDeleted: 1, VoteSessionsDeleted: 1, VotesDeleted: 1,
            ObservationsDeleted: 1);
        Assert.Equal(expected, dry.Channels);
        Assert.Equal(expected, enforced.Channels);
        await using var db = CreateDbContext();
        Assert.Equal(
            new HashSet<string> { active.Id, atCutoff.Id },
            (await db.Channels.Select(c => c.Id).ToListAsync()).ToHashSet());
        Assert.False(await db.Emotes.AnyAsync(e => e.ChannelId == due.Id));
        var purge = await db.AuditLogEntries.AsNoTracking().SingleAsync(e => e.Action == AuditActions.ChannelPurge);
        Assert.Equal("chandue", purge.ChannelName);
        Assert.Equal(AuditActor.System.TwitchUserId, purge.ActorTwitchUserId);
    }

    [Fact]
    public async Task Channels_WithoutAStamp_AreStampedInTheDryRunToo_AndNotDueInTheSamePass()
    {
        var unstamped = await SeedChannelWithHistoryAsync("chanunstamped", deactivatedAtUtc: null);
        var activeUnstamped = await SeedChannelAsync("chanactivenostamp", isBotActive: true, deactivatedAtUtc: null);

        var dry = await RunAsync(enforce: false);

        Assert.Equal(1, dry.Channels.Restamped);
        Assert.Equal(0, dry.Channels.Purged);
        await using (var db = CreateDbContext())
        {
            Assert.Equal(_now, (await db.Channels.AsNoTracking().SingleAsync(c => c.Id == unstamped.Id)).DeactivatedAtUtc);
            Assert.Null((await db.Channels.AsNoTracking().SingleAsync(c => c.Id == activeUnstamped.Id)).DeactivatedAtUtc);
        }

        // Stamped once: the next pass leaves the stamp alone, and the channel is still far from due.
        var enforced = await RunAsync(enforce: true);

        Assert.Equal(0, enforced.Channels.Restamped);
        Assert.Equal(0, enforced.Channels.Purged);
        await using var verify = CreateDbContext();
        Assert.Equal(_now, (await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == unstamped.Id)).DeactivatedAtUtc);
        Assert.True(await verify.Emotes.AnyAsync(e => e.ChannelId == unstamped.Id));
    }

    [Fact]
    public async Task Channels_StillActiveNotFoundAndAFailure_AreCounted_AndDoNotStopTheOthers()
    {
        // Ids fix the processing order (candidates are walked by id): the failing channel goes first.
        await SeedChannelWithHistoryAsync("chanfails", ChannelCutoff.AddDays(-1), id: "retention-channel-1");
        await SeedChannelWithHistoryAsync("chanrejoined", ChannelCutoff.AddDays(-1), id: "retention-channel-2");
        await SeedChannelWithHistoryAsync("chanvanished", ChannelCutoff.AddDays(-1), id: "retention-channel-3");
        await SeedChannelWithHistoryAsync("chanplain", ChannelCutoff.AddDays(-1), id: "retention-channel-4");
        await InstallFailureTriggerAsync("Channels", "retention_test_fail_channel", "retention-channel-1");

        var summary = await RunAsync(
            enforce: true,
            beforeChannelPurge: async name =>
            {
                await using var other = CreateDbContext();
                if (name == "chanrejoined")
                {
                    await other.Channels.Where(c => c.ChannelName == name)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(c => c.IsBotActive, true)
                            .SetProperty(c => c.DeactivatedAtUtc, (DateTime?)null));
                }
                else if (name == "chanvanished")
                {
                    await other.Channels.Where(c => c.ChannelName == name).ExecuteDeleteAsync();
                }
            });

        Assert.Equal(1, summary.Channels.Purged);
        Assert.Equal(1, summary.Channels.StillActive);
        Assert.Equal(1, summary.Channels.NotFound);
        Assert.Equal(1, summary.Channels.Failed);
        // Only the purged channel's cascade is reported.
        Assert.Equal(1, summary.Channels.EmotesDeleted);
        await using var db = CreateDbContext();
        Assert.Equal(
            ["chanfails", "chanrejoined"],
            await db.Channels.OrderBy(c => c.ChannelName).Select(c => c.ChannelName).ToListAsync());
        Assert.True(await db.Emotes.AnyAsync(e => e.ChannelId == "retention-channel-1"));
        Assert.Equal(["chanplain"], await db.AuditLogEntries.Where(e => e.Action == AuditActions.ChannelPurge).Select(e => e.ChannelName).ToListAsync());
        var warning = Assert.Single(_logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("SQLSTATE P0001", warning.Message);
        _logs.AssertNamesNone(
            "chanfails", "chanrejoined", "chanvanished", "chanplain",
            "retention-channel-1", "retention-channel-2", "retention-channel-3", "retention-channel-4");
    }

    // ---------------------------------------------------------------------------------------------
    // All categories at once: overlap, dry-run parity, and the counts matching the rows that went
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFullPass_DryRunCountsEqualTheEnforcedCounts_AndTheEnforcedCountsEqualTheRowsThatWent()
    {
        var seeded = await SeedOverlappingWorldAsync();
        var before = await CountRowsAsync();

        var (dry, enforced) = await RunDryThenEnforcedAsync();

        // The numbers, worked out by hand in SeedOverlappingWorldAsync's comments.
        Assert.Equal(2, enforced.TokensCleared);
        Assert.Equal(new AccountRetentionCounts(2, 0, 0, 0, false, 6, 2, 5), enforced.Accounts);
        Assert.Equal(new VoteSessionRetentionCounts(2, 2, 2), enforced.VoteSessions);
        Assert.Equal(2, enforced.AuditEntriesDeleted);
        Assert.Equal(new ChannelRetentionCounts(0, 1, 0, 0, 0, 2, 3, 2, 2, 3, 0), enforced.Channels);
        Assert.Equal(1, dry.Channels.Restamped);

        // What was reported is exactly what went: every cascade edge, category overlaps included.
        var after = await CountRowsAsync();
        Assert.Equal(before.Users - enforced.Accounts.Deleted, after.Users);
        Assert.Equal(
            before.Votes - enforced.Accounts.VotesDeleted - enforced.VoteSessions.VotesDeleted - enforced.Channels.VotesDeleted,
            after.Votes);
        Assert.Equal(before.Sessions - enforced.VoteSessions.Deleted - enforced.Channels.VoteSessionsDeleted, after.Sessions);
        Assert.Equal(before.Emotes - enforced.Channels.EmotesDeleted, after.Emotes);
        Assert.Equal(before.UsageRows - enforced.Channels.UsageRowsDeleted, after.UsageRows);
        Assert.Equal(before.LiveDays - enforced.Channels.LiveDaysDeleted, after.LiveDays);
        Assert.Equal(before.Channels - enforced.Channels.Purged, after.Channels);
        // Two old entries went; one user.delete per account and one channel.purge came.
        Assert.Equal(before.AuditEntries - 2 + 2 + 1, after.AuditEntries);

        await using var db = CreateDbContext();
        Assert.Equal(["u-seen", "u-stay", "u-tokens"], await UserIdsAsync());
        Assert.Equal(["u-seen", "u-stay"], await UsersWithTokensAsync());
        Assert.False(await db.AuditLogEntries.AnyAsync(e => e.ActorTwitchUserId == "u-gone1" || e.TargetId == "u-gone1" || e.TargetId == "u-gone2"));
        Assert.True(await db.Channels.AnyAsync(c => c.Id == seeded.NotDueChannelId));

        _logs.AssertNamesNone("u-gone1", "u-gone2", "ugone1", "ugone2", "u-stay", "chdue", "chactive");
    }

    [Fact]
    public async Task ADryRun_WritesNothingButTheMissingChannelStamps()
    {
        await SeedOverlappingWorldAsync();
        var before = await SnapshotAsync();

        await RunAsync(enforce: false);

        Assert.Equal(before, await SnapshotAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private static DateTime WholeSecondsNow()
    {
        var now = DateTime.UtcNow;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
    }

    private AppDbContext CreateDbContext() => fixture.CreateDbContext(DatabaseName);

    /// <summary>
    /// The pass as the worker's scope builds it: one context shared by the retention service, the real
    /// account deletion and the real channel purge. Redis and the identity lookup are not reached on
    /// these paths and are substituted. The two hooks run just before an account deletion or a channel
    /// purge, to stage what a concurrent request would do in between.
    /// </summary>
    private async Task<RetentionRunSummary> RunAsync(
        bool enforce,
        int maxAccountsPerRun = 100,
        Func<string, Task>? beforeAccountDeletion = null,
        Func<string, Task>? beforeChannelPurge = null)
    {
        await using var db = CreateDbContext();

        IAccountDeletionService accounts = new AccountDeletionService(
            db, Substitute.For<IModRoleCache>(), new RecordingRateLimitTelemetry(), _logs.For<AccountDeletionService>());
        if (beforeAccountDeletion is not null)
        {
            accounts = new InterceptedAccountDeletion(accounts, beforeAccountDeletion);
        }

        IChannelService channels = new ChannelService(
            db,
            Substitute.For<IRedisPublisher>(),
            Substitute.For<IChannelIdentityService>(),
            new ChannelEmoteSetObservationService(db),
            new ChannelCapacityOptions(),
            Substitute.For<IExcludedChannelFilter>(),
            _logs.For<ChannelService>());
        if (beforeChannelPurge is not null)
        {
            var real = channels;
            channels = Substitute.For<IChannelService>();
            channels.PurgeIfInactiveSinceAsync(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    await beforeChannelPurge(call.ArgAt<string>(0));
                    return await real.PurgeIfInactiveSinceAsync(
                        call.ArgAt<string>(0), call.ArgAt<DateTime>(1), call.ArgAt<AuditActor>(2), call.ArgAt<CancellationToken>(3));
                });
        }

        var service = new DataRetentionService(
            db,
            accounts,
            channels,
            new RetentionOptions { MaxAccountsPerRun = maxAccountsPerRun },
            new HandWoundTimeProvider(new DateTimeOffset(_now)),
            _logs.For<DataRetentionService>());

        return await service.RunAsync(enforce);
    }

    /// <summary>
    /// Dry run, then an enforced run over the same rows, and the parity every test gets for free: the two
    /// summaries agree except for the mode flag and the channel stamps (the dry run wrote them, so the
    /// enforced run finds none left to write).
    /// </summary>
    private async Task<(RetentionRunSummary Dry, RetentionRunSummary Enforced)> RunDryThenEnforcedAsync(int maxAccountsPerRun = 100)
    {
        var dry = await RunAsync(enforce: false, maxAccountsPerRun);
        var enforced = await RunAsync(enforce: true, maxAccountsPerRun);

        Assert.False(dry.Enforced);
        Assert.True(enforced.Enforced);
        Assert.Equal(0, enforced.Channels.Restamped);
        Assert.Equal(dry with { Enforced = true, Channels = dry.Channels with { Restamped = 0 } }, enforced);
        return (dry, enforced);
    }

    private async Task InstallFailureTriggerAsync(string table, string triggerName, string id)
    {
        await using var db = CreateDbContext();
        // Identifiers from the test's own constants; the id goes in as a literal because trigger
        // conditions cannot take parameters.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync(
            $"""
            CREATE TRIGGER {triggerName} BEFORE DELETE ON "{table}"
            FOR EACH ROW WHEN (OLD."Id" = '{id}') EXECUTE FUNCTION retention_test_fail();
            """);
#pragma warning restore EF1002
    }

    private async Task<User> SeedUserAsync(string id, DateTime lastLogin, DateTime? lastSeen = null, bool withTokens = false)
    {
        await using var db = CreateDbContext();
        var user = new User
        {
            Id = id,
            TwitchUsername = id.Replace("-", string.Empty),
            DisplayName = id,
            LastLogin = lastLogin,
            LastSeenAtUtc = lastSeen
        };
        if (withTokens)
        {
            // Opaque placeholders: nothing here decrypts them.
            user.TwitchRefreshToken = "encrypted-refresh";
            user.TwitchAccessToken = "encrypted-access";
            user.TwitchAccessTokenExpiresAtUtc = _now.AddHours(1);
            user.TwitchTokenScopes = "user:read:moderated_channels";
        }

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Channel> SeedChannelAsync(string name, bool isBotActive, DateTime? deactivatedAtUtc, string? id = null)
    {
        await using var db = CreateDbContext();
        var channel = new Channel { ChannelName = name, IsBotActive = isBotActive, DeactivatedAtUtc = deactivatedAtUtc };
        if (id is not null)
        {
            channel.Id = id;
        }

        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private async Task<string> SeedEmoteAsync(string channelId, int usageDays = 0)
    {
        await using var db = CreateDbContext();
        var emote = new Emote
        {
            ChannelId = channelId,
            Name = "RetentionEmote",
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        for (var day = 1; day <= usageDays; day++)
        {
            db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2025, 1, day), UseCount = day });
        }

        await db.SaveChangesAsync();
        return emote.Id;
    }

    private async Task SeedLiveDaysAsync(string channelId, int days)
    {
        await using var db = CreateDbContext();
        for (var day = 1; day <= days; day++)
        {
            db.ChannelLiveDays.Add(new ChannelLiveDay { ChannelId = channelId, Date = new DateOnly(2025, 1, day), LiveMinutes = 60 });
        }

        await db.SaveChangesAsync();
    }

    private async Task<long> SeedSessionAsync(string channelId, bool isActive, DateTime? endedAt, DateTime? startedAt = null)
    {
        await using var db = CreateDbContext();
        var session = new VoteSession
        {
            ChannelId = channelId,
            Title = "Retention",
            IsActive = isActive,
            StartedAt = startedAt ?? (endedAt ?? _now).AddDays(-7),
            EndedAt = endedAt
        };
        db.VoteSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task SeedVoteAsync(long sessionId, string emoteId, string userId)
    {
        await using var db = CreateDbContext();
        db.Votes.Add(new Vote { VoteSessionId = sessionId, EmoteId = emoteId, UserId = userId, Type = VoteType.Keep });
        await db.SaveChangesAsync();
    }

    private async Task SeedBallotAsync(long sessionId, string emoteId)
    {
        await using var db = CreateDbContext();
        db.VoteSessionEmotes.Add(new VoteSessionEmote { VoteSessionId = sessionId, EmoteId = emoteId });
        await db.SaveChangesAsync();
    }

    private async Task<long> SeedAuditAsync(
        DateTime occurredAtUtc, string actorId, string actorLogin, string? targetUserId = null, string? channelName = null)
    {
        await using var db = CreateDbContext();
        var entry = new AuditLogEntry
        {
            OccurredAtUtc = occurredAtUtc,
            ActorTwitchUserId = actorId,
            ActorLogin = actorLogin,
            Action = targetUserId is null ? AuditActions.ChannelLeave : AuditActions.UserRevokeSessions,
            ChannelName = channelName,
            TargetType = targetUserId is null ? null : "user",
            TargetId = targetUserId,
            DetailsJson = targetUserId is null ? null : JsonSerializer.Serialize(new { login = targetUserId.Replace("-", string.Empty) })
        };
        db.AuditLogEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private async Task<(Channel Channel, long Open, long Ended, string Emote)> SeedChannelWithSessionsAsync(string name)
    {
        var channel = await SeedChannelAsync(name, isBotActive: true, deactivatedAtUtc: null);
        var emote = await SeedEmoteAsync(channel.Id);
        var open = await SeedSessionAsync(channel.Id, isActive: true, endedAt: null);
        var ended = await SeedSessionAsync(channel.Id, isActive: false, endedAt: _now.AddDays(-3));
        return (channel, open, ended, emote);
    }

    /// <summary>
    /// An inactive channel with one row on every cascade edge: an emote with two usage rows, a live day,
    /// an open vote session with a ballot row and a vote (open, so the session category never takes it
    /// first and the channel purge's own count covers it), and one closed emote-set observation interval.
    /// </summary>
    private async Task<Channel> SeedChannelWithHistoryAsync(string name, DateTime? deactivatedAtUtc, string? id = null)
    {
        var channel = await SeedChannelAsync(name, isBotActive: false, deactivatedAtUtc, id);
        var emote = await SeedEmoteAsync(channel.Id, usageDays: 2);
        await SeedLiveDaysAsync(channel.Id, 1);
        var voter = await SeedUserAsync($"{name}-voter", lastLogin: _now);
        var session = await SeedSessionAsync(channel.Id, isActive: true, endedAt: null);
        await SeedBallotAsync(session, emote);
        await SeedVoteAsync(session, emote, voter.Id);
        await SeedObservationAsync(channel.Id);
        return channel;
    }

    private async Task SeedObservationAsync(string channelId)
    {
        await using var db = CreateDbContext();
        db.ChannelEmoteSetObservations.Add(new ChannelEmoteSetObservation
        {
            ChannelId = channelId,
            SevenTvEmoteSetId = "retention-set",
            ObservedFromUtc = _now.AddDays(-30),
            ObservedToUtc = _now.AddDays(-1),
            ClosedBy = ChannelEmoteSetObservationClosedBy.SetSwitch
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Every category due at once, overlapping wherever they can. Expected counts (T = cutoff):
    /// <list type="bullet">
    /// <item>Tokens 2: u-gone1 (inactive 400 d) and u-tokens (40 d). u-seen was seen yesterday, u-stay
    /// logged in 10 days ago, u-gone2 holds none.</item>
    /// <item>Accounts 2 (u-gone1, u-gone2). Votes 6: u-gone1 in s-open, s-old, sd-old, sd-recent; u-gone2
    /// in s-recent, sd-open — 2 of them in open sessions. Audit 5: u-gone1 is actor of a1, a3, a4 and
    /// target of a2, a3 (4 distinct), u-gone2 target of a4 (1).</item>
    /// <item>Sessions 2 (s-old, sd-old), with the 2 votes the account deletions leave (u-seen, u-stay)
    /// and 2 ballot rows.</item>
    /// <item>Audit 2 (a1 at 400 d, a5 at 366 d); a6 sits exactly at the cutoff.</item>
    /// <item>Channels: ch-unstamped stamped, ch-due purged with 2 emotes, 3 usage rows, 2 live days, and
    /// the 2 sessions category 3 leaves (sd-open, sd-recent) with their 3 votes not cast by a deleted
    /// account. ch-notdue left 10 days ago stays.</item>
    /// </list>
    /// </summary>
    private async Task<OverlappingWorld> SeedOverlappingWorldAsync()
    {
        await SeedUserAsync("u-gone1", lastLogin: _now.AddDays(-400), lastSeen: _now.AddDays(-400), withTokens: true);
        await SeedUserAsync("u-gone2", lastLogin: _now.AddDays(-500));
        await SeedUserAsync("u-stay", lastLogin: _now.AddDays(-10), withTokens: true);
        await SeedUserAsync("u-tokens", lastLogin: _now.AddDays(-40), withTokens: true);
        await SeedUserAsync("u-seen", lastLogin: _now.AddDays(-600), lastSeen: _now.AddDays(-1), withTokens: true);

        var active = await SeedChannelAsync("chactive", isBotActive: true, deactivatedAtUtc: null);
        var ea = await SeedEmoteAsync(active.Id);
        var sOpen = await SeedSessionAsync(active.Id, isActive: true, endedAt: null, startedAt: _now.AddDays(-500));
        var sOld = await SeedSessionAsync(active.Id, isActive: false, endedAt: _now.AddDays(-400));
        var sRecent = await SeedSessionAsync(active.Id, isActive: false, endedAt: _now.AddDays(-100));
        await SeedBallotAsync(sOld, ea);
        await SeedBallotAsync(sRecent, ea);

        var due = await SeedChannelAsync("chdue", isBotActive: false, deactivatedAtUtc: _now.AddDays(-200));
        var ed1 = await SeedEmoteAsync(due.Id, usageDays: 2);
        var ed2 = await SeedEmoteAsync(due.Id, usageDays: 1);
        await SeedLiveDaysAsync(due.Id, 2);
        var sdOld = await SeedSessionAsync(due.Id, isActive: false, endedAt: _now.AddDays(-400));
        var sdOpen = await SeedSessionAsync(due.Id, isActive: true, endedAt: null);
        var sdRecent = await SeedSessionAsync(due.Id, isActive: false, endedAt: _now.AddDays(-50));
        await SeedBallotAsync(sdOld, ed1);

        var notDue = await SeedChannelAsync("chnotdue", isBotActive: false, deactivatedAtUtc: _now.AddDays(-10));
        await SeedEmoteAsync(notDue.Id);
        await SeedChannelAsync("chunstamped", isBotActive: false, deactivatedAtUtc: null);

        await SeedVoteAsync(sOpen, ea, "u-gone1");
        await SeedVoteAsync(sOpen, ea, "u-stay");
        await SeedVoteAsync(sOld, ea, "u-gone1");
        await SeedVoteAsync(sOld, ea, "u-seen");
        await SeedVoteAsync(sRecent, ea, "u-gone2");
        await SeedVoteAsync(sRecent, ea, "u-stay");
        await SeedVoteAsync(sdOld, ed1, "u-gone1");
        await SeedVoteAsync(sdOld, ed1, "u-stay");
        await SeedVoteAsync(sdOpen, ed1, "u-gone2");
        await SeedVoteAsync(sdOpen, ed1, "u-stay");
        await SeedVoteAsync(sdOpen, ed2, "u-seen");
        await SeedVoteAsync(sdRecent, ed2, "u-gone1");
        await SeedVoteAsync(sdRecent, ed2, "u-stay");

        await SeedAuditAsync(_now.AddDays(-400), "u-gone1", "ugone1", channelName: "chactive"); // a1
        await SeedAuditAsync(_now.AddDays(-10), "u-stay", "ustay", targetUserId: "u-gone1"); // a2
        await SeedAuditAsync(_now.AddDays(-10), "u-gone1", "ugone1", targetUserId: "u-gone1"); // a3
        await SeedAuditAsync(_now.AddDays(-10), "u-gone1", "ugone1", targetUserId: "u-gone2"); // a4
        await SeedAuditAsync(_now.AddDays(-366), AuditActor.System.TwitchUserId, AuditActor.System.Login, channelName: "chdue"); // a5
        await SeedAuditAsync(AuditCutoff, AuditActor.System.TwitchUserId, AuditActor.System.Login); // a6

        return new OverlappingWorld(notDue.Id);
    }

    private async Task<List<string>> UserIdsAsync()
    {
        await using var db = CreateDbContext();
        // Sorted in memory, ordinally: Postgres' collation would weigh the hyphens differently.
        return (await db.Users.Select(u => u.Id).ToListAsync()).Order(StringComparer.Ordinal).ToList();
    }

    private async Task<List<string>> UsersWithTokensAsync()
    {
        await using var db = CreateDbContext();
        return (await db.Users.Where(u => u.TwitchRefreshToken != null).Select(u => u.Id).ToListAsync())
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private async Task<RowCounts> CountRowsAsync()
    {
        await using var db = CreateDbContext();
        return new RowCounts(
            await db.Users.CountAsync(),
            await db.Votes.CountAsync(),
            await db.VoteSessions.CountAsync(),
            await db.Emotes.CountAsync(),
            await db.UsageStats.CountAsync(),
            await db.ChannelLiveDays.CountAsync(),
            await db.Channels.CountAsync(),
            await db.AuditLogEntries.CountAsync());
    }

    /// <summary>
    /// Every table as text, for "nothing changed" comparisons. An inactive channel without a stamp is
    /// rendered with the stamp the pass will give it — the one write a dry run is allowed.
    /// </summary>
    private async Task<string> SnapshotAsync()
    {
        await using var db = CreateDbContext();
        var snapshot = new
        {
            Users = await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(),
            Channels = (await db.Channels.AsNoTracking().OrderBy(c => c.Id).ToListAsync())
                .Select(c => new
                {
                    c.Id,
                    c.ChannelName,
                    c.IsBotActive,
                    DeactivatedAtUtc = !c.IsBotActive && c.DeactivatedAtUtc is null ? _now : c.DeactivatedAtUtc
                }),
            Emotes = await db.Emotes.AsNoTracking().OrderBy(e => e.Id).Select(e => new { e.Id, e.ChannelId }).ToListAsync(),
            Usage = await db.UsageStats.AsNoTracking().OrderBy(u => u.Id).Select(u => new { u.Id, u.EmoteId, u.UseCount }).ToListAsync(),
            LiveDays = await db.ChannelLiveDays.AsNoTracking().OrderBy(d => d.Id).Select(d => new { d.Id, d.ChannelId }).ToListAsync(),
            Sessions = await db.VoteSessions.AsNoTracking().OrderBy(s => s.Id).Select(s => new { s.Id, s.IsActive, s.EndedAt }).ToListAsync(),
            Ballots = await db.VoteSessionEmotes.AsNoTracking().OrderBy(b => b.VoteSessionId).ThenBy(b => b.EmoteId).Select(b => new { b.VoteSessionId, b.EmoteId }).ToListAsync(),
            Votes = await db.Votes.AsNoTracking().OrderBy(v => v.Id).Select(v => new { v.Id, v.UserId, v.VoteSessionId }).ToListAsync(),
            Audit = await db.AuditLogEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync()
        };
        return JsonSerializer.Serialize(snapshot);
    }

    private sealed record OverlappingWorld(string NotDueChannelId);

    private sealed record RowCounts(
        int Users, int Votes, int Sessions, int Emotes, int UsageRows, int LiveDays, int Channels, int AuditEntries);

    private sealed class InterceptedAccountDeletion(IAccountDeletionService inner, Func<string, Task> before) : IAccountDeletionService
    {
        public async Task<AccountDeletionResult> DeleteAsync(
            string twitchUserId,
            AuditActor actor,
            AccountDeletionReason reason,
            DateTime? onlyIfInactiveBeforeUtc,
            CancellationToken cancellationToken = default)
        {
            await before(twitchUserId);
            return await inner.DeleteAsync(twitchUserId, actor, reason, onlyIfInactiveBeforeUtc, cancellationToken);
        }
    }

    /// <summary>
    /// One log for every logger of the pass (the retention service, the account deletion, the channel
    /// purge), with exceptions rendered in full — an identity in an exception message counts as logged.
    /// </summary>
    private sealed class LogSink
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger<T> For<T>() => new SinkLogger<T>(this);

        public void AssertNamesNone(params string[] identities)
        {
            foreach (var (_, message) in Entries)
            {
                foreach (var identity in identities)
                {
                    Assert.DoesNotContain(identity, message, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private void Add(LogLevel level, string message)
        {
            lock (_entries)
            {
                _entries.Add((level, message));
            }
        }

        private sealed class SinkLogger<T>(LogSink sink) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Add(logLevel, exception is null ? formatter(state, exception) : $"{formatter(state, exception)}\n{exception}");
        }
    }
}
