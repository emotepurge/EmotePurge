using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The cross-channel overview and counter (wish emotes, T6) against real Postgres in a database of their own
// (the overview reads every channel's wishes, so it must not see the rows of other classes) and, for the cost
// claim of AK15, a real ChannelAccessService on a real ModeratedChannelsProvider on real Redis with a counting
// Helix double. The clock shows 2026-09-14 12:00 UTC.
[Collection("Postgres")]
public class WishOverviewServiceTests(PostgresFixture fixture, RedisFixture redisFixture) : IAsyncLifetime, IClassFixture<RedisFixture>
{
    private const string DatabaseName = "wish_overview_tests";
    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;

    private readonly HandWoundTimeProvider _clock = new();

    private DateTime Now => _clock.Now.UtcDateTime;

    public async Task InitializeAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (!_databaseReady)
            {
                await using (var admin = fixture.CreateDbContext())
                {
#pragma warning disable EF1002
                    await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {DatabaseName}");
#pragma warning restore EF1002
                }

                await using var migrate = fixture.CreateDbContext(DatabaseName);
                await migrate.Database.MigrateAsync();
                _databaseReady = true;
            }
        }
        finally
        {
            DatabaseGate.Release();
        }

        await using var reset = CreateDb();
        await reset.Database.ExecuteSqlRawAsync("TRUNCATE \"Wishes\", \"ChannelWishSettings\", \"Channels\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------------------------ the list

    [Fact]
    public async Task Mine_ListsOnlyChannelsTheCallerManages_AndNamesTheChannelPerRow()
    {
        var mine = await SeedChannelAsync("mine_channel");
        var foreign = await SeedChannelAsync("foreign_channel");
        var wishInMine = await SeedWishAsync(mine, WishStatus.Approved);
        await SeedWishAsync(foreign, WishStatus.Approved);
        var access = AccessAllowing("mine_channel");

        var rows = await Sut(access).ListMineAsync(Principal("u-mine"));

        var row = Assert.Single(rows);
        Assert.Equal("mine_channel", row.ChannelName);
        Assert.Equal(wishInMine, row.Wish.Id);
        await access.Received(1).CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), "foreign_channel", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Mine_HoldsActionAndProbation_NotTheRest_SortedByExpiryThenCreation_NoExpiryLast()
    {
        var channel = await SeedChannelAsync("sorted_channel");
        var approved = await SeedWishAsync(channel, WishStatus.Approved, createdAt: Now.AddDays(-5));
        var failed = await SeedWishAsync(channel, WishStatus.Failed, createdAt: Now.AddDays(-4));
        var presented = await SeedWishAsync(channel, WishStatus.Presented, expiresAt: Now.AddDays(-1), createdAt: Now.AddDays(-9));
        var soon = await SeedWishAsync(channel, WishStatus.Active, startsAt: Now.AddDays(-20), expiresAt: Now.AddDays(3));
        var tieA = await SeedWishAsync(channel, WishStatus.Active, startsAt: Now.AddDays(-20), expiresAt: Now.AddDays(5), createdAt: Now.AddDays(-2));
        var tieB = await SeedWishAsync(channel, WishStatus.Active, startsAt: Now.AddDays(-20), expiresAt: Now.AddDays(5), createdAt: Now.AddDays(-3));
        await SeedWishAsync(channel, WishStatus.Active, startsAt: Now.AddDays(-5), expiresAt: Now.AddDays(25)); // not yet in probation
        await SeedWishAsync(channel, WishStatus.Active, startsAt: Now.AddDays(1), expiresAt: Now.AddDays(30)); // not started
        await SeedWishAsync(channel, WishStatus.Unconfirmed);
        await SeedWishAsync(channel, WishStatus.Kept, endedAt: Now.AddDays(-1));
        await SeedWishAsync(channel, WishStatus.Removed, endedAt: Now.AddDays(-1));

        var rows = await Sut(AccessAllowing("sorted_channel")).ListMineAsync(Principal("u-sorted"));

        Assert.Equal([presented, soon, tieB, tieA, approved, failed], rows.Select(r => r.Wish.Id));
    }

    [Fact]
    public async Task Mine_UsesTheChannelsProbationLead_ForTheProbationRule()
    {
        var channel = await SeedChannelAsync("lead_channel");
        await SeedSettingsAsync(channel, probationLeadDays: 30);
        var wish = await SeedWishAsync(channel, WishStatus.Active, startsAt: Now.AddDays(-5), expiresAt: Now.AddDays(25));

        var rows = await Sut(AccessAllowing("lead_channel")).ListMineAsync(Principal("u-lead"));

        Assert.Equal(wish, Assert.Single(rows).Wish.Id);
    }

    [Fact]
    public async Task Mine_TreatsApprovedAsActionOnlyWhileTheChannelRunsManually()
    {
        var manual = await SeedChannelAsync("manual_channel");
        var bot = await SeedChannelAsync("bot_channel");
        await SeedSettingsAsync(bot, botMode: true);
        var manualWish = await SeedWishAsync(manual, WishStatus.Approved);
        await SeedWishAsync(bot, WishStatus.Approved);
        var botFailed = await SeedWishAsync(bot, WishStatus.Failed);

        var rows = await Sut(AccessAllowing("manual_channel", "bot_channel")).ListMineAsync(Principal("u-mode"));

        Assert.Equal([manualWish, botFailed], rows.Select(r => r.Wish.Id).Order());
        Assert.Equal(2, await Sut(AccessAllowing("manual_channel", "bot_channel")).CountActionAsync(Principal("u-mode")));
    }

    // ------------------------------------------------------------------------------------ the counter

    [Fact]
    public async Task Count_CountsActionWishesInManagedChannels_NotProbationAndNotForeignChannels()
    {
        var mine = await SeedChannelAsync("count_mine");
        var foreign = await SeedChannelAsync("count_foreign");
        await SeedWishAsync(mine, WishStatus.Approved);
        await SeedWishAsync(mine, WishStatus.Presented);
        await SeedWishAsync(mine, WishStatus.Active, startsAt: Now.AddDays(-20), expiresAt: Now.AddDays(2)); // probation only
        await SeedWishAsync(foreign, WishStatus.Failed);

        Assert.Equal(2, await Sut(AccessAllowing("count_mine")).CountActionAsync(Principal("u-count")));
    }

    [Fact]
    public async Task Count_WithoutCandidates_IsZeroWithoutAnyRoleCheck()
    {
        var channel = await SeedChannelAsync("quiet_channel");
        await SeedWishAsync(channel, WishStatus.Active, startsAt: Now.AddDays(-5), expiresAt: Now.AddDays(25)); // no probation yet
        await SeedWishAsync(channel, WishStatus.Kept, endedAt: Now.AddDays(-1));
        var access = AccessAllowing("quiet_channel");

        Assert.Equal(0, await Sut(access).CountActionAsync(Principal("u-quiet")));
        Assert.Empty(await Sut(access).ListMineAsync(Principal("u-quiet")));
        Assert.Empty(access.ReceivedCalls());
    }

    [Fact]
    public async Task Count_OfAChannelWithOnlyProbationWishes_AsksNoOne()
    {
        var channel = await SeedChannelAsync("probation_only");
        await SeedWishAsync(channel, WishStatus.Active, startsAt: Now.AddDays(-20), expiresAt: Now.AddDays(2));
        var access = AccessAllowing("probation_only");

        Assert.Equal(0, await Sut(access).CountActionAsync(Principal("u-prob")));
        Assert.Empty(access.ReceivedCalls());
    }

    // A channel merge (ChannelIdentityService.MoveWishesAsync) commits while the role checks run: the selected
    // wish now belongs to the survivor, which the caller was never checked for. The reload stays inside the
    // authorized channels, so the moved wish drops out instead of failing the whole list.
    [Fact]
    public async Task Mine_AWishMovedToAnUncheckedChannelDuringTheRoleChecks_DropsOut()
    {
        var loser = await SeedChannelAsync("merge_loser");
        var survivor = await SeedChannelAsync("merge_survivor");
        var moved = await SeedWishAsync(loser, WishStatus.Approved);
        var stays = await SeedWishAsync(loser, WishStatus.Failed);
        var access = Substitute.For<IChannelAccessService>();
        access.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), "merge_loser", Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await using var db = CreateDb();
                await db.Wishes.Where(w => w.Id == moved).ExecuteUpdateAsync(s => s.SetProperty(w => w.ChannelId, survivor));
                return true;
            });

        var rows = await Sut(access).ListMineAsync(Principal("u-merge"));

        var row = Assert.Single(rows);
        Assert.Equal(("merge_loser", stays), (row.ChannelName, row.Wish.Id));
        await access.DidNotReceive().CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), "merge_survivor", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void NeitherReadRoutesThroughTheMyChannelsService()
    {
        // The overview and the counter must not pay for the channel overview (spec E18, AK15): neither service,
        // nor the access check they call, can reach IMyChannelsService, because none takes it.
        foreach (var type in new[] { typeof(WishOverviewService), typeof(ChannelAccessService) })
        {
            var parameters = type.GetConstructors().Single().GetParameters();
            Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(IMyChannelsService));
        }
    }

    // ------------------------------------------------------------------------------------ the cost (AK15)

    [Fact]
    public async Task RealRoleCheck_PaginatesHelixOnce_AndAnsweringTheSecondCallFromTheCache()
    {
        const string userId = "wish-overview-moderator";
        var moderated = await SeedChannelAsync("moderated_channel");
        var other = await SeedChannelAsync("other_channel");
        var editorOnly = await SeedChannelAsync("editor_only_channel");
        var inModerated = await SeedWishAsync(moderated, WishStatus.Approved);
        await SeedWishAsync(other, WishStatus.Approved);
        await SeedWishAsync(editorOnly, WishStatus.Failed);

        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetModeratedChannelsAsync(Arg.Any<string>(), userId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<TwitchModeratedChannelInfo>?>(_ => [new TwitchModeratedChannelInfo("moderated_channel", "9001")]);
        var tokens = Substitute.For<ITwitchUserTokenService>();
        tokens.GetValidAccessTokenAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>())
            .Returns(new TwitchUserTokenResult("token", false));
        var editors = Substitute.For<ISevenTvEditorService>();
        var access = RealAccessService(helix, tokens, editors);
        var sut = Sut(access);
        var principal = new TwitchPrincipalInfo(userId, "somemod", "cookie-token");

        var first = await sut.CountActionAsync(principal);
        var second = await sut.ListMineAsync(principal);
        var third = await sut.CountActionAsync(principal);

        Assert.Equal(1, first);
        Assert.Equal(inModerated, Assert.Single(second).Wish.Id);
        Assert.Equal(1, third);
        // Cold cache: one token lookup and one Helix pagination for the whole sequence; no 7TV call at all,
        // and an editor-only channel is not visible.
        await helix.Received(1).GetModeratedChannelsAsync(Arg.Any<string>(), userId, Arg.Any<CancellationToken>());
        await tokens.Received(1).GetValidAccessTokenAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>());
        Assert.Empty(editors.ReceivedCalls());
    }

    // Plan #485 2.1a: each row carries its own channel's stored image of the emote, null without one.
    [Fact]
    public async Task Mine_CarriesEachChannelsOwnImage_OfTheEmote()
    {
        var first = await SeedChannelAsync("image_first");
        var second = await SeedChannelAsync("image_second");
        var firstWish = await SeedWishAsync(first, WishStatus.Approved);
        var secondWish = await SeedWishAsync(second, WishStatus.Approved);
        var bare = await SeedWishAsync(second, WishStatus.Approved);
        string emoteId;
        await using (var db = CreateDb())
        {
            var wishes = await db.Wishes.Where(w => w.Id == firstWish || w.Id == secondWish).ToListAsync();
            emoteId = wishes[0].SevenTvEmoteId;
            foreach (var wish in wishes)
            {
                wish.SevenTvEmoteId = emoteId;
            }

            db.Emotes.Add(new Emote { ChannelId = first, SevenTvEmoteId = emoteId, Name = "E", ImageUrl = "https://cdn.7tv.app/emote/first/2x.webp" });
            db.Emotes.Add(new Emote { ChannelId = second, SevenTvEmoteId = emoteId, Name = "E", ImageUrl = "https://cdn.7tv.app/emote/second/2x.webp" });
            await db.SaveChangesAsync();
        }

        var rows = (await Sut(AccessAllowing("image_first", "image_second")).ListMineAsync(Principal("u-images")))
            .ToDictionary(r => r.Wish.Id, r => r.Wish.ImageUrl);

        Assert.Equal("https://cdn.7tv.app/emote/first/2x.webp", rows[firstWish]);
        Assert.Equal("https://cdn.7tv.app/emote/second/2x.webp", rows[secondWish]);
        Assert.Null(rows[bare]);
    }

    // K6 (plan #486 2.4a): /mine gives out the redeemer data of a captured wish only for a channel where the caller is
    // confirmed on the row's Twitch id, asked next to the manager check; an answer about another row (renamed in
    // between) or an unconfirmed one leaves the fields empty. A manual wish keeps its requester login.
    [Fact]
    public async Task Mine_GivesOutRedeemerDataOnlyForChannelsConfirmedOnTheirId()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (confirmed, unconfirmed, renamed) = ($"conf_{suffix}", $"unconf_{suffix}", $"renamed_{suffix}");
        var confirmedId = await SeedChannelAsync(confirmed);
        var unconfirmedId = await SeedChannelAsync(unconfirmed);
        var renamedId = await SeedChannelAsync(renamed);
        var inConfirmed = await SeedCapturedWishAsync(confirmedId);
        var inUnconfirmed = await SeedCapturedWishAsync(unconfirmedId);
        var inRenamed = await SeedCapturedWishAsync(renamedId);
        var manual = await SeedWishAsync(unconfirmedId, WishStatus.Approved);
        await using (var db = CreateDb())
        {
            await db.Wishes.Where(w => w.Id == manual).ExecuteUpdateAsync(u => u.SetProperty(w => w.RequesterLogin, "handviewer"));
        }

        var access = AccessAllowing(confirmed, unconfirmed, renamed);
        var answers = new Dictionary<string, WishCaptureAccess?>(StringComparer.Ordinal);
        await using (var db = CreateDb())
        {
            foreach (var row in await db.Channels.AsNoTracking().Where(c => c.Id == confirmedId || c.Id == unconfirmedId || c.Id == renamedId).ToListAsync())
            {
                answers[row.ChannelName] = row.Id == unconfirmedId
                    ? new WishCaptureAccess(row.Id, row.TwitchChannelId, IdConfirmed: false)
                    : row.Id == renamedId
                        ? new WishCaptureAccess("another-row", row.TwitchChannelId, IdConfirmed: true)
                        : new WishCaptureAccess(row.Id, row.TwitchChannelId, IdConfirmed: true);
            }
        }

        access.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<WishCaptureAccess?>>(call => Task.FromResult(answers.GetValueOrDefault((string)call[1])));

        var rows = (await Sut(access).ListMineAsync(Principal($"u-{suffix}"))).ToDictionary(r => r.Wish.Id);

        Assert.Equal(("please", "peepoHey", "viewer"), (rows[inConfirmed].Wish.RequestText, rows[inConfirmed].Wish.RequestedName, rows[inConfirmed].Wish.RequesterLogin));
        foreach (var hidden in new[] { inUnconfirmed, inRenamed })
        {
            Assert.Equal(((string?)null, (string?)null, (string?)null), (rows[hidden].Wish.RequestText, rows[hidden].Wish.RequestedName, rows[hidden].Wish.RequesterLogin));
        }

        Assert.Equal("handviewer", rows[manual].Wish.RequesterLogin);
        // The counter carries numbers only and asks nothing about the id.
        Assert.Equal(4, await Sut(access).CountActionAsync(Principal($"u-{suffix}")));
    }

    // ------------------------------------------------------------------------------------ helpers

    private AppDbContext CreateDb() => fixture.CreateDbContext(DatabaseName);

    private WishOverviewService Sut(IChannelAccessService access) => new(CreateDb(), access, _clock);

    private static TwitchPrincipalInfo Principal(string userId) => new(userId, "caller", "cookie-token");

    private static IChannelAccessService AccessAllowing(params string[] channelNames)
    {
        var access = Substitute.For<IChannelAccessService>();
        access.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => channelNames.Contains((string)call[1]));
        return access;
    }

    private ChannelAccessService RealAccessService(ITwitchHelixClient helix, ITwitchUserTokenService tokens, ISevenTvEditorService editors)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:ModCheckCacheTtlMinutes"] = "10" })
            .Build();
        var provider = new ModeratedChannelsProvider(
            tokens, helix, redisFixture.Connection, configuration, Substitute.For<IRateLimitTelemetry>(),
            NullLogger<ModeratedChannelsProvider>.Instance);
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new Channel { ChannelName = (string)call[0], TwitchChannelId = "owner-" + call[0] });
        return new ChannelAccessService(
            new ModeratorCheckService(provider, NullLogger<ModeratorCheckService>.Instance),
            provider,
            editors,
            channels,
            new GlobalAdminAllowlist(configuration, NullLogger<GlobalAdminAllowlist>.Instance),
            NullLogger<ChannelAccessService>.Instance);
    }

    private async Task<string> SeedChannelAsync(string name)
    {
        await using var db = CreateDb();
        var channel = new Channel { ChannelName = name, TwitchChannelId = Guid.NewGuid().ToString("N")[..12], IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel.Id;
    }

    private async Task SeedSettingsAsync(string channelId, int probationLeadDays = 7, bool botMode = false)
    {
        await using var db = CreateDb();
        db.ChannelWishSettings.Add(new ChannelWishSettings
        {
            ChannelId = channelId,
            ProbationLeadDays = probationLeadDays,
            BotModeEnabled = botMode
        });
        await db.SaveChangesAsync();
    }

    private async Task<long> SeedWishAsync(
        string channelId,
        string status,
        DateTime? startsAt = null,
        DateTime? expiresAt = null,
        DateTime? endedAt = null,
        DateTime? createdAt = null)
    {
        await using var db = CreateDb();
        var wish = new Wish
        {
            ChannelId = channelId,
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            EmoteName = "Emote",
            Alias = "Alias",
            Source = WishSource.Manual,
            Status = status,
            StatusChangedAtUtc = Now,
            CreatedAtUtc = createdAt ?? Now,
            StartsAtUtc = startsAt,
            ExpiresAtUtc = expiresAt,
            EndedAtUtc = endedAt
        };
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }

    private async Task<long> SeedCapturedWishAsync(string channelId)
    {
        await using var db = CreateDb();
        var wish = new Wish
        {
            ChannelId = channelId,
            SevenTvEmoteId = string.Empty,
            Source = WishSource.Irc,
            Status = WishStatus.Pending,
            MatchKind = WishMatchKind.Name,
            RequestedName = "peepoHey",
            RequestText = "please",
            RequesterTwitchUserId = "777",
            RequesterLogin = "viewer",
            StatusChangedAtUtc = Now,
            CreatedAtUtc = Now
        };
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }
}
