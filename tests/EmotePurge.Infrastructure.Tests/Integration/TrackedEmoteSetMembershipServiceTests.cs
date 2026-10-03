using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class TrackedEmoteSetMembershipServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task UnknownChannel_IsChannelNotFound_AndTheListIsNotRead()
    {
        await using var db = fixture.CreateDbContext();
        var lists = Substitute.For<ISevenTvEmoteSetListService>();

        var verdict = await new TrackedEmoteSetMembershipService(db, lists).CheckAsync("tsm-unknown", "set-a");

        Assert.Equal(TrackedEmoteSetMembership.ChannelNotFound, verdict);
        await lists.DidNotReceiveWithAnyArgs().ListByTwitchIdAsync(default!, default);
    }

    [Fact]
    public async Task ActiveSet_IsMember_WithoutReadingTheList()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "tsm-active", activeSetId: "set-active");
        var lists = Substitute.For<ISevenTvEmoteSetListService>();

        var verdict = await new TrackedEmoteSetMembershipService(db, lists).CheckAsync("tsm-active", "set-active");

        Assert.Equal(TrackedEmoteSetMembership.Member, verdict);
        await lists.DidNotReceiveWithAnyArgs().ListByTwitchIdAsync(default!, default);
    }

    [Fact]
    public async Task DeactivatedChannel_ActiveSet_IsMember_EvenWithAStaleActiveId_WithoutReadingTheList()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "tsm-inactive-active", activeSetId: "set-stale", isBotActive: false);
        var lists = Substitute.For<ISevenTvEmoteSetListService>();

        var verdict = await new TrackedEmoteSetMembershipService(db, lists).CheckAsync("tsm-inactive-active", "set-stale");

        Assert.Equal(TrackedEmoteSetMembership.Member, verdict);
        await lists.DidNotReceiveWithAnyArgs().ListByTwitchIdAsync(default!, default);
    }

    [Fact]
    public async Task DeactivatedChannel_ListedNormalSet_IsMember()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "tsm-inactive-listed", isBotActive: false);
        var lists = ListReturning(EmoteSetListResult.Ok(ListOf(Set("set-a", "NORMAL"))));

        var verdict = await new TrackedEmoteSetMembershipService(db, lists).CheckAsync("tsm-inactive-listed", "set-a");

        Assert.Equal(TrackedEmoteSetMembership.Member, verdict);
    }

    [Fact]
    public async Task ChannelWithoutTwitchId_IsNotMember_AndTheListIsNotRead()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "tsm-no-twitch-id", withTwitchId: false);
        var lists = Substitute.For<ISevenTvEmoteSetListService>();

        var verdict = await new TrackedEmoteSetMembershipService(db, lists).CheckAsync("tsm-no-twitch-id", "set-a");

        Assert.Equal(TrackedEmoteSetMembership.NotMember, verdict);
        await lists.DidNotReceiveWithAnyArgs().ListByTwitchIdAsync(default!, default);
    }

    [Fact]
    public async Task ListedNormalSet_IsMember()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "tsm-listed", activeSetId: "set-active");
        var lists = ListReturning(EmoteSetListResult.Ok(ListOf(Set("set-a", "NORMAL"))));

        var verdict = await new TrackedEmoteSetMembershipService(db, lists).CheckAsync("tsm-listed", "set-a");

        Assert.Equal(TrackedEmoteSetMembership.Member, verdict);
        await lists.Received(1).ListByTwitchIdAsync("tsm-listed", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListedSetOfAnotherKind_IsNotMember()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "tsm-personal", activeSetId: "set-active");
        var lists = ListReturning(EmoteSetListResult.Ok(ListOf(Set("set-a", "PERSONAL"))));

        var verdict = await new TrackedEmoteSetMembershipService(db, lists).CheckAsync("tsm-personal", "set-a");

        Assert.Equal(TrackedEmoteSetMembership.NotMember, verdict);
    }

    [Fact]
    public async Task UnlistedSet_IsNotMember()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "tsm-unlisted", activeSetId: "set-active");
        var lists = ListReturning(EmoteSetListResult.Ok(ListOf(Set("set-a", "NORMAL"))));

        var verdict = await new TrackedEmoteSetMembershipService(db, lists).CheckAsync("tsm-unlisted", "set-other");

        Assert.Equal(TrackedEmoteSetMembership.NotMember, verdict);
    }

    [Fact]
    public async Task EmptyActiveSetId_NeverMatches_SoTheListDecides()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "tsm-never-synced", activeSetId: "");
        var lists = ListReturning(EmoteSetListResult.Ok(ListOf(Set("set-a", "NORMAL"))));

        var service = new TrackedEmoteSetMembershipService(db, lists);

        Assert.Equal(TrackedEmoteSetMembership.NotMember, await service.CheckAsync("tsm-never-synced", "set-other"));
        Assert.Equal(TrackedEmoteSetMembership.Member, await service.CheckAsync("tsm-never-synced", "set-a"));
    }

    [Fact]
    public async Task NoSevenTvAccount_IsNotMember()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "tsm-no-7tv", activeSetId: "set-active");
        var lists = ListReturning(EmoteSetListResult.Failed(EmoteSetListStatus.NoSevenTvAccount));

        var verdict = await new TrackedEmoteSetMembershipService(db, lists).CheckAsync("tsm-no-7tv", "set-a");

        Assert.Equal(TrackedEmoteSetMembership.NotMember, verdict);
    }

    [Theory]
    [InlineData(EmoteSetListStatus.RateLimited)]
    [InlineData(EmoteSetListStatus.Unavailable)]
    [InlineData(EmoteSetListStatus.BudgetExhausted)]
    public async Task UnreadableList_IsSevenTvUnavailable(EmoteSetListStatus status)
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, $"tsm-fail-{status.ToString().ToLowerInvariant()}", activeSetId: "set-active");
        var lists = ListReturning(EmoteSetListResult.Failed(status));

        var verdict = await new TrackedEmoteSetMembershipService(db, lists)
            .CheckAsync($"tsm-fail-{status.ToString().ToLowerInvariant()}", "set-a");

        Assert.Equal(TrackedEmoteSetMembership.SevenTvUnavailable, verdict);
    }

    [Fact]
    public async Task ChannelName_IsNormalised()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "tsm-handofblood", activeSetId: "set-active");
        var lists = ListReturning(EmoteSetListResult.Ok(ListOf(Set("set-a", "NORMAL"))));

        var verdict = await new TrackedEmoteSetMembershipService(db, lists).CheckAsync("  TSM-HandOfBlood ", "set-a");

        Assert.Equal(TrackedEmoteSetMembership.Member, verdict);
    }

    private static ISevenTvEmoteSetListService ListReturning(EmoteSetListResult result)
    {
        var lists = Substitute.For<ISevenTvEmoteSetListService>();
        lists.ListByTwitchIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(result);
        return lists;
    }

    private static EmoteSetList ListOf(params EmoteSetSummary[] sets) => new(null, sets);

    private static EmoteSetSummary Set(string id, string kind) =>
        new(id, "Name", null, kind, kind == "PERSONAL", null);

    private static async Task SeedChannelAsync(
        AppDbContext db, string channelName, bool withTwitchId = true, string activeSetId = "", bool isBotActive = true)
    {
        // TwitchChannelId is unique, so the channel name doubles as its Twitch id.
        db.Channels.Add(new Channel
        {
            ChannelName = channelName,
            TwitchChannelId = withTwitchId ? channelName : null,
            ActiveEmoteSetId = activeSetId,
            IsBotActive = isBotActive
        });
        await db.SaveChangesAsync();
    }
}
