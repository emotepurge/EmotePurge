using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The races of the reward service with two real Postgres connections (plan #486 T4, Sol-R2-P2-5, Sol-P2-4,
// Sol-P1-1): every change of the learning row is one conditional statement, so a reader or writer that saw an older
// session never touches a newer one; and the binding is decided behind the channel lock.
public partial class WishRewardServiceTests
{
    // The GET reads the expired session A; before its cleanup runs, a start (another connection) replaces A by B.
    // The cleanup names A and the expiry, so B lives.
    [Fact]
    public async Task Race_ReadSawExpiredSessionA_StartReplacesItByB_TheReadsCleanupLeavesB()
    {
        var channel = await SeedChannelAsync();
        var sessionA = await SeedLearningAsync(channel, withCandidate: true);
        _clock.Advance(TimeSpan.FromMinutes(11));
        Guid? sessionB = null;
        var hook = new CommandHook(
            IsLearningSelect,
            async occurrence =>
            {
                if (occurrence == 1)
                {
                    var started = await Sut(fixture.CreateDbContext()).StartLearningAsync(channel.Access, Actor);
                    Assert.Equal(WishRewardOutcome.Success, started.Outcome);
                    sessionB = started.Learning!.SessionId;
                }
            });

        var read = await Sut(fixture.CreateDbContext([hook])).GetAsync(channel.Access, "99", false);

        Assert.Equal(WishRewardOutcome.Success, read.Outcome);
        Assert.Null(read.Rewards!.Learning);
        Assert.NotNull(sessionB);
        Assert.NotEqual(sessionA.SessionId, sessionB);
        var row = (await LearningRowAsync(channel))!;
        Assert.Equal(sessionB, row.SessionId);
        Assert.Equal(1, hook.Count(c => c.Contains("DELETE FROM \"WishRewardLearnings\"", StringComparison.Ordinal)));
    }

    // Confirm reads the candidate of A; before its delete runs, another connection replaces the row by session B with
    // another candidate, without the channel lock. The delete names A's session and candidate: nothing is hit, the
    // answer is stale, no reward is written and B stays.
    [Fact]
    public async Task Race_ConfirmSawCandidateOfA_RowIsReplacedByB_ConfirmIsStale_AndBStays()
    {
        var channel = await SeedChannelAsync();
        var seen = await SeedLearningAsync(channel, withCandidate: true, rewardTag: "tag-a");
        var sessionB = Guid.NewGuid();
        var candidateB = Guid.NewGuid();
        var hook = new CommandHook(
            c => IsLearningSelect(c) && c.Contains("\"CandidateRewardId\"", StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence == 1)
                {
                    await using var other = fixture.CreateDbContext();
                    var replaced = await other.WishRewardLearnings
                        .Where(l => l.ChannelId == channel.Id)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(l => l.SessionId, sessionB)
                            .SetProperty(l => l.CandidateId, candidateB)
                            .SetProperty(l => l.CandidateRewardId, "tag-b"));
                    Assert.Equal(1, replaced);
                }
            });

        var result = await Sut(fixture.CreateDbContext([hook])).ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(seen.SessionId, seen.CandidateId, "Label", 7), Actor);

        Assert.Equal(WishRewardOutcome.LearningStale, result.Outcome);
        Assert.Empty(await RewardsAsync(channel));
        var row = (await LearningRowAsync(channel))!;
        Assert.Equal((sessionB, (Guid?)candidateB, "tag-b"), (row.SessionId, row.CandidateId, row.CandidateRewardId));
    }

    // The cancel of a dialog behind a restart, from its own connection: session B was started on another one.
    [Fact]
    public async Task Race_CancelWithSessionA_AfterAnotherConnectionStartedB_IsStale_AndBStays()
    {
        var channel = await SeedChannelAsync();
        var sessionA = (await Sut(fixture.CreateDbContext()).StartLearningAsync(channel.Access, Actor)).Learning!.SessionId;
        _clock.Advance(TimeSpan.FromMinutes(10));
        var sessionB = (await Sut(fixture.CreateDbContext()).StartLearningAsync(channel.Access, Actor)).Learning!.SessionId;

        var result = await Sut(fixture.CreateDbContext()).CancelLearningAsync(channel.Access, sessionA, Actor);

        Assert.Equal(WishRewardOutcome.LearningStale, result.Outcome);
        Assert.Equal(sessionB, (await LearningRowAsync(channel))!.SessionId);
    }

    [Fact]
    public async Task Race_TwoStartsAtOnce_OneOpensTheSession_TheOtherIsLearningActive()
    {
        var channel = await SeedChannelAsync();

        var results = await Task.WhenAll(
            Sut(fixture.CreateDbContext()).StartLearningAsync(channel.Access, Actor),
            Sut(fixture.CreateDbContext()).StartLearningAsync(channel.Access, Actor));

        Assert.Equal(
            [WishRewardOutcome.Success, WishRewardOutcome.LearningActive],
            results.Select(r => r.Outcome).Order());
        var winner = results.Single(r => r.IsSuccess).Learning!.SessionId;
        Assert.Equal(winner, (await LearningRowAsync(channel))!.SessionId);
    }

    [Fact]
    public async Task Race_TwoConfirmsAtOnce_OneRewardIsWritten_TheOtherFindsNoCandidate()
    {
        var channel = await SeedChannelAsync();
        var learning = await SeedLearningAsync(channel, withCandidate: true);
        var request = new ConfirmWishRewardLearningRequest(learning.SessionId, learning.CandidateId, "Label", 7);

        var results = await Task.WhenAll(
            Sut(fixture.CreateDbContext()).ConfirmLearningAsync(channel.Access, request, Actor),
            Sut(fixture.CreateDbContext()).ConfirmLearningAsync(channel.Access, request, Actor));

        Assert.Equal(
            [WishRewardOutcome.Success, WishRewardOutcome.NoCandidate],
            results.Select(r => r.Outcome).Order());
        Assert.Single(await RewardsAsync(channel));
        Assert.Null(await LearningRowAsync(channel));
    }

    // The housekeeping's statement (expiry alone, no lock) against a start that replaces the expired row: the
    // housekeeping waits at the expired row the start deleted, finds it gone after the commit, and leaves B alone.
    [Fact]
    public async Task Race_HousekeepingAgainstAStartReplacingTheExpiredRow_LeavesTheNewSession()
    {
        var channel = await SeedChannelAsync();
        await SeedLearningAsync(channel, withCandidate: true);
        _clock.Advance(TimeSpan.FromMinutes(12));
        var now = Now;
        Task<int>? housekeeping = null;
        AppDbContext? housekeepingDb = null;
        var hook = new CommandHook(
            c => c.Contains("INSERT INTO \"WishRewardLearnings\"", StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence == 1)
                {
                    housekeepingDb = fixture.CreateTaggedDbContext("wish-learning-housekeeping");
                    housekeeping = housekeepingDb.DeleteExpiredLearningsAsync(now, CancellationToken.None);
                    await fixture.WaitUntilBlockedOnLockAsync("wish-learning-housekeeping", housekeeping);
                }
            });

        try
        {
            var started = await Sut(fixture.CreateDbContext([hook])).StartLearningAsync(channel.Access, Actor);

            Assert.Equal(WishRewardOutcome.Success, started.Outcome);
            await housekeeping!;
            Assert.Equal(started.Learning!.SessionId, (await LearningRowAsync(channel))!.SessionId);
        }
        finally
        {
            if (housekeepingDb is not null)
            {
                await housekeepingDb.DisposeAsync();
            }
        }
    }

    // AK50 at the locked decision point (Sol review round 2): the confirm arrives a minute before the deadline but
    // waits on the channel lock until after it. The expiry is judged on the clock behind the lock, so the candidate
    // is gone (no candidate), no reward is written, and the expired row is cleared.
    [Fact]
    public async Task Race_ConfirmWaitsOnTheChannelLockPastTheDeadline_IsNoCandidate_AndWritesNoReward()
    {
        var channel = await SeedChannelAsync();
        var learning = await SeedLearningAsync(channel, withCandidate: true);
        _clock.Advance(WishLimits.LearningWindow - TimeSpan.FromMinutes(1));
        await using var holder = fixture.CreateDbContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);

        await using var contender = fixture.CreateTaggedDbContext("wish-reward-confirm-deadline");
        var waiting = Sut(contender).ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(learning.SessionId, learning.CandidateId, "Label", 7), Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-reward-confirm-deadline", waiting);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await holderTransaction.CommitAsync();

        Assert.Equal(WishRewardOutcome.NoCandidate, (await waiting).Outcome);
        Assert.Empty(await RewardsAsync(channel));
        Assert.Null(await LearningRowAsync(channel));
    }

    // ------------------------------------------------------------------------------------ binding under the lock

    // Plan 2.4: the binding is read again behind the channel lock. A second connection holds the lock and lets the
    // identity reconcile's kind of change happen; the waiting start decides on the state after it.
    [Theory]
    [InlineData("unchanged", WishRewardOutcome.Success)]
    [InlineData("changed", WishRewardOutcome.AccessDenied)]
    [InlineData("cleared", WishRewardOutcome.ChannelUnbound)]
    [InlineData("deleted", WishRewardOutcome.ChannelNotFound)]
    public async Task Binding_ChangesWhileTheStartWaitsOnTheChannelLock_TheStartDecidesOnTheNewState(string change, WishRewardOutcome expected)
    {
        var channel = await SeedChannelAsync();
        var tag = $"wish-reward-binding-{change}";
        await using var holder = fixture.CreateDbContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);

        await using var contender = fixture.CreateTaggedDbContext(tag);
        var waiting = Sut(contender).StartLearningAsync(channel.Access, Actor);
        await fixture.WaitUntilBlockedOnLockAsync(tag, waiting);

        var rows = holder.Channels.Where(c => c.Id == channel.Id);
        _ = change switch
        {
            "changed" => await rows.ExecuteUpdateAsync(set => set.SetProperty(c => c.TwitchChannelId, Guid.NewGuid().ToString("N")[..12])),
            "cleared" => await rows.ExecuteUpdateAsync(set => set.SetProperty(c => c.TwitchChannelId, (string?)null)),
            "deleted" => await rows.ExecuteDeleteAsync(),
            _ => await rows.ExecuteUpdateAsync(set => set.SetProperty(c => c.IsBotActive, true))
        };
        await holderTransaction.CommitAsync();

        Assert.Equal(expected, (await waiting).Outcome);
        Assert.Equal(expected == WishRewardOutcome.Success, await LearningRowAsync(channel) is not null);
    }

    // The same for a row that had no id when the caller was checked: the id the reconcile sets meanwhile is not the
    // one the caller was confirmed for (he was confirmed for none), so the start is denied rather than let through.
    [Fact]
    public async Task Binding_AnIdSetWhileTheStartWaits_DoesNotConfirmTheEarlierUnboundAccess()
    {
        var channel = await SeedChannelAsync(bound: false);
        await using var holder = fixture.CreateDbContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);

        await using var contender = fixture.CreateTaggedDbContext("wish-reward-binding-set");
        var waiting = Sut(contender).StartLearningAsync(channel.Access, Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-reward-binding-set", waiting);
        await holder.Channels.Where(c => c.Id == channel.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.TwitchChannelId, Guid.NewGuid().ToString("N")[..12]));
        await holderTransaction.CommitAsync();

        Assert.Equal(WishRewardOutcome.AccessDenied, (await waiting).Outcome);
        Assert.Null(await LearningRowAsync(channel));
    }

    // ------------------------------------------------------------------------------------ reassigned login (2.4a)

    // The row still carries the login Twitch has given to another account. The new owner (other id, same login) and a
    // moderator of the new owner (list entry with that login, other BroadcasterId) are let in by the login-based
    // manager check, but neither is confirmed on the id, so neither reads the old row's candidate or starts learning.
    // The real broadcaster under a new login and a moderator listed with the row's BroadcasterId are confirmed.
    [Fact]
    public async Task ReassignedLogin_NeitherTheNewOwnerNorTheirModeratorReachesTheOldRowsK6Data()
    {
        var channel = await SeedChannelAsync();
        var oldId = channel.Access.TwitchChannelId!;
        await SeedLearningAsync(channel, withCandidate: true, text: "old channel's text", login: "oldviewer");
        var newOwner = new TwitchPrincipalInfo("new-owner-id", channel.Name, null);
        var newOwnersMod = new TwitchPrincipalInfo("mod-of-new", "modofnew", null);
        var realBroadcaster = new TwitchPrincipalInfo(oldId, "renamed_broadcaster", null);
        var oldMod = new TwitchPrincipalInfo("mod-of-old", "modofold", null);
        var lists = Substitute.For<IModeratedChannelsProvider>();
        lists.GetModeratedChannelsAsync(newOwnersMod, Arg.Any<CancellationToken>())
            .Returns(new ModeratedChannelsLookup([new TwitchModeratedChannelInfo(channel.Name, "new-owner-id")], false));
        lists.GetModeratedChannelsAsync(oldMod, Arg.Any<CancellationToken>())
            .Returns(new ModeratedChannelsLookup([new TwitchModeratedChannelInfo("renamed_broadcaster", oldId)], false));
        lists.GetModeratedChannelsAsync(newOwner, Arg.Any<CancellationToken>())
            .Returns(new ModeratedChannelsLookup([], false));
        var access = AccessService(lists);

        // The contrast: the existing login-based check lets the new owner's moderator manage the old row.
        Assert.True(await access.CanManageChannelAsync(newOwnersMod, channel.Name));

        foreach (var outsider in new[] { newOwner, newOwnersMod })
        {
            var resolved = (await access.ResolveWishCaptureAccessAsync(outsider, channel.Name))!;
            Assert.Equal((channel.Id, oldId, false), (resolved.ChannelId, resolved.TwitchChannelId, resolved.IdConfirmed));
            Assert.Equal(WishRewardOutcome.AccessDenied, (await Sut().GetAsync(resolved, outsider.TwitchUserId, false)).Outcome);
            Assert.Equal(WishRewardOutcome.AccessDenied, (await Sut().StartLearningAsync(resolved, Actor)).Outcome);
        }

        foreach (var insider in new[] { realBroadcaster, oldMod })
        {
            var resolved = (await access.ResolveWishCaptureAccessAsync(insider, channel.Name.ToUpperInvariant()))!;
            Assert.True(resolved.IdConfirmed);
            var read = await Sut().GetAsync(resolved, insider.TwitchUserId, false);
            Assert.Equal("old channel's text", read.Rewards!.Learning!.Candidate!.Text);
        }
    }

    private ChannelAccessService AccessService(IModeratedChannelsProvider lists)
    {
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await using var db = fixture.CreateDbContext();
                var name = (string)call[0];
                return await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.ChannelName == name);
            });
        var configuration = new ConfigurationBuilder().Build();
        return new ChannelAccessService(
            new ModeratorCheckService(lists, NullLogger<ModeratorCheckService>.Instance),
            lists,
            Substitute.For<ISevenTvEditorService>(),
            channels,
            new GlobalAdminAllowlist(configuration, NullLogger<GlobalAdminAllowlist>.Instance),
            NullLogger<ChannelAccessService>.Instance);
    }

    private static bool IsLearningSelect(string commandText) =>
        commandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal)
        && commandText.Contains("FROM \"WishRewardLearnings\"", StringComparison.Ordinal);
}
