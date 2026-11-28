using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using EmotePurge.Infrastructure.Tests.Fakes;

namespace EmotePurge.Infrastructure.Tests.Integration;

// K6 (plan #486 T6, Sol-P2-9): an approval that becomes the extension of a running wish, against the expiry run that
// presents that wish at the same moment. Both take the running wish's row lock; in either order the end state is
// consistent: active with a new expiry counted from now, and the approval's wish.extend records the expiry it replaced.
public partial class WishExpiryServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApproveAsExtension_AgainstTheRunPresentingTheRunningWish_EndsActive_WithThePreviousExpiry(bool runQueuesFirst)
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        var overdue = Now.AddMinutes(-1);
        var target = await SeedWishAsync(channel, expiresAt: overdue, emoteId: emoteId);
        var open = await SeedOpenCaptureAsync(channel, emoteId);
        await using var holder = CreateDb();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        Assert.Single(await holder.LockWishesByIdsAsync([target], CancellationToken.None));

        const string RunTag = "wish-expiry-vs-approve-run";
        const string ApproveTag = "wish-expiry-vs-approve-cmd";
        var approver = new WishService(
            fixture.CreateTaggedDbContext(ApproveTag, DatabaseName),
            new WishChangedPublisher(_otherPublisher, NullLogger<WishChangedPublisher>.Instance),
            _clock,
            NullLogger<WishService>.Instance, BotChannelStates.Unavailable());
        Task<WishExpiryRunResult> Run() => Sut(fixture.CreateTaggedDbContext(RunTag, DatabaseName)).RunAsync();
        Task<WishCommandResult> Approve() => approver.ApproveAsync(channel.Name, open, new ApproveWishRequest(null, null, null, null), Actor);
        Task<WishExpiryRunResult> run;
        Task<WishCommandResult> approve;
        if (runQueuesFirst)
        {
            run = Run();
            await fixture.WaitUntilBlockedOnLockAsync(RunTag, run);
            approve = Approve();
            await fixture.WaitUntilBlockedOnLockAsync(ApproveTag, approve);
        }
        else
        {
            approve = Approve();
            await fixture.WaitUntilBlockedOnLockAsync(ApproveTag, approve);
            run = Run();
            await fixture.WaitUntilBlockedOnLockAsync(RunTag, run);
        }

        await holderTransaction.CommitAsync();
        await run;
        var result = await approve;

        Assert.Equal((WishOutcome.Success, WishStatus.Merged), (result.Outcome, result.Wish!.Status));
        var running = await LoadAsync(target);
        Assert.Equal((WishStatus.Active, (string?)null, (DateTime?)Now.AddDays(30), 1), (running.Status, running.StatusReason, running.ExpiresAtUtc, running.ExtensionCount));
        await using (var db = CreateDb())
        {
            var extend = await db.AuditLogEntries.AsNoTracking().SingleAsync(a => a.ChannelName == channel.Name && a.Action == AuditActions.WishExtend);
            Assert.Equal(overdue, JsonDocument.Parse(extend.DetailsJson!).RootElement.GetProperty("previousExpiresAtUtc").GetDateTime());
            // The run presented it first (one wish.due) or found it extended (none); never twice.
            Assert.Equal(runQueuesFirst ? 1 : 0, (await DueEntriesAsync(target)).Count);

            // This database is shared by the class and later tests wind the clock forward: end both rows.
            await db.Wishes.Where(w => w.ChannelId == channel.Id).ExecuteUpdateAsync(u => u
                .SetProperty(w => w.Status, WishStatus.Closed)
                .SetProperty(w => w.EndedAtUtc, Now));
        }
    }

    // A wish the capture created for the emote: pending, source irc, the placeholder in name and alias.
    private async Task<long> SeedOpenCaptureAsync(Seeded channel, string emoteId)
    {
        await using var db = CreateDb();
        var wish = new Wish
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = emoteId,
            Source = WishSource.Irc,
            MatchKind = WishMatchKind.Link,
            Status = WishStatus.Pending,
            StatusChangedAtUtc = Now.AddMinutes(-10),
            CreatedAtUtc = Now.AddMinutes(-10),
            RequesterTwitchUserId = "777",
            RequesterLogin = "viewer",
            RequestText = "please"
        };
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }
}
