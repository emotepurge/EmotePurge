using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// "An open attempt blocks decisions" (#487 plan 2.5a (b), T6): an add intent only in approved, a removal attempt in
// any state; a repair phase alone blocks nothing.
public class WishBotAttemptGuardTests
{
    private static readonly DateTime At = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ACommittedAddIntent_BlocksOnlyInApproved()
    {
        foreach (var status in WishStatus.All)
        {
            Assert.Equal(status == WishStatus.Approved, WishBotAttemptGuard.Blocks(Wish(status, write: At)));
        }
    }

    [Fact]
    public void ARemovalAttempt_BlocksInEveryState()
    {
        foreach (var status in WishStatus.All)
        {
            Assert.True(WishBotAttemptGuard.Blocks(Wish(status, remove: At)));
        }
    }

    [Fact]
    public void NothingOpen_OrOnlyARepairPhase_BlocksNothing()
    {
        foreach (var status in WishStatus.All)
        {
            Assert.False(WishBotAttemptGuard.Blocks(Wish(status)));
            var repairing = Wish(status);
            repairing.BotRepairAtUtc = At;
            repairing.BotRemoveEvidence = "{}";
            Assert.False(WishBotAttemptGuard.Blocks(repairing));
        }
    }

    [Fact]
    public void TheColumnForm_AnswersLikeTheEntityForm()
    {
        Assert.True(WishBotAttemptGuard.Blocks(WishStatus.Approved, At, null));
        Assert.False(WishBotAttemptGuard.Blocks(WishStatus.Unconfirmed, At, null));
        Assert.True(WishBotAttemptGuard.Blocks(WishStatus.Active, null, At));
        Assert.Throws<ArgumentNullException>(() => WishBotAttemptGuard.Blocks(null!));
    }

    private static Wish Wish(string status, DateTime? write = null, DateTime? remove = null) =>
        new() { Status = status, BotWriteAtUtc = write, BotRemoveAtUtc = remove };
}
