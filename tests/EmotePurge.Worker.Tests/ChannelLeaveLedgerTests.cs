using Xunit;

namespace EmotePurge.Worker.Tests;

public class ChannelLeaveLedgerTests
{
    [Fact]
    public void LeftSince_IsFalseForAChannelNeverLeft()
    {
        var ledger = new ChannelLeaveLedger();

        Assert.False(ledger.LeftSince("sensitron", ledger.Stamp));
    }

    // The #245 live race: the resync's stamp (and roster read) came first, the reconcile's LEAVE after.
    [Fact]
    public void LeftSince_IsTrueForALeaveRecordedAfterTheStamp_CaseInsensitively()
    {
        var ledger = new ChannelLeaveLedger();
        var stamp = ledger.Stamp;

        ledger.RecordLeave("Sensitron");

        Assert.True(ledger.LeftSince("sensitron", stamp));
    }

    // A roster read that starts after the LEAVE already sees the commit behind it: a channel still on
    // that roster is active again and must stay joinable.
    [Fact]
    public void LeftSince_IsFalseForALeaveRecordedBeforeTheStamp()
    {
        var ledger = new ChannelLeaveLedger();
        ledger.RecordLeave("sensitron");

        var stamp = ledger.Stamp;

        Assert.False(ledger.LeftSince("sensitron", stamp));
    }

    [Fact]
    public void LeftSince_OnlyConcernsTheChannelThatWasLeft()
    {
        var ledger = new ChannelLeaveLedger();
        var stamp = ledger.Stamp;

        ledger.RecordLeave("sensitron");

        Assert.False(ledger.LeftSince("olaf_olaf_son", stamp));
    }

    // Leave, join, leave again within one stale read: the newest LEAVE counts.
    [Fact]
    public void LeftSince_FollowsTheLatestLeaveOfAChannel()
    {
        var ledger = new ChannelLeaveLedger();
        ledger.RecordLeave("sensitron");
        var stamp = ledger.Stamp;
        ledger.Forget("sensitron");

        ledger.RecordLeave("sensitron");

        Assert.True(ledger.LeftSince("sensitron", stamp));
    }

    [Fact]
    public void Forget_ClearsTheEntry_SoAStaleStampNoLongerBlocks()
    {
        var ledger = new ChannelLeaveLedger();
        var stamp = ledger.Stamp;
        ledger.RecordLeave("sensitron");

        ledger.Forget("SENSITRON");

        Assert.False(ledger.LeftSince("sensitron", stamp));
    }
}
