using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Worker.Tests;

public class BootRecoveryOrderPolicyTests
{
    private static TwitchLiveStatusSnapshot Snapshot(params string[] live) => new(DateTime.UtcNow, live);

    [Fact]
    public void LiveFirst_PutsLiveChannelsBeforeTheOthers()
    {
        var result = BootRecoveryOrderPolicy.LiveFirst(["alpha", "bravo", "charlie", "delta"], Snapshot("charlie", "bravo"));

        Assert.Equal(["bravo", "charlie", "alpha", "delta"], result);
    }

    [Fact]
    public void LiveFirst_KeepsTheRelativeOrderWithinEachGroup_RegardlessOfSnapshotOrder()
    {
        var result = BootRecoveryOrderPolicy.LiveFirst(["a", "b", "c", "d", "e"], Snapshot("e", "c"));

        Assert.Equal(["c", "e", "a", "b", "d"], result);
    }

    [Fact]
    public void LiveFirst_WithoutSnapshot_ReturnsTheOrderUnchanged()
    {
        var result = BootRecoveryOrderPolicy.LiveFirst(["a", "b", "c"], null);

        Assert.Equal(["a", "b", "c"], result);
    }

    [Fact]
    public void LiveFirst_WithNobodyLive_ReturnsTheOrderUnchanged()
    {
        var result = BootRecoveryOrderPolicy.LiveFirst(["a", "b", "c"], Snapshot());

        Assert.Equal(["a", "b", "c"], result);
    }

    [Fact]
    public void LiveFirst_IgnoresLiveNamesThatAreNotOnTheActiveRoster()
    {
        var result = BootRecoveryOrderPolicy.LiveFirst(["a", "b"], Snapshot("zzz", "b"));

        Assert.Equal(["b", "a"], result);
    }

    [Fact]
    public void LiveFirst_NormalizesCaseAndWhitespaceOfLiveLogins()
    {
        var result = BootRecoveryOrderPolicy.LiveFirst(["handofblood", "other"], Snapshot("  HandOfBlood "));

        Assert.Equal(["handofblood", "other"], result);

        var reordered = BootRecoveryOrderPolicy.LiveFirst(["other", "handofblood"], Snapshot("  HandOfBlood "));

        Assert.Equal(["handofblood", "other"], reordered);
    }

    [Fact]
    public void LiveFirst_WithEmptyRoster_ReturnsEmpty()
    {
        Assert.Empty(BootRecoveryOrderPolicy.LiveFirst([], Snapshot("a")));
        Assert.Empty(BootRecoveryOrderPolicy.LiveFirst([], null));
    }

    [Fact]
    public void LiveFirst_WithEveryChannelLive_ReturnsTheOrderUnchanged()
    {
        var result = BootRecoveryOrderPolicy.LiveFirst(["a", "b"], Snapshot("b", "a"));

        Assert.Equal(["a", "b"], result);
    }

    [Fact]
    public void LiveFirst_SkipsNullAndBlankSnapshotEntries()
    {
        var result = BootRecoveryOrderPolicy.LiveFirst(["a", "b"], Snapshot(null!, "", "   ", "b"));

        Assert.Equal(["b", "a"], result);
    }

    [Fact]
    public void ColdFirst_PutsColdChannelsFirst_KeepingTheOrderWithinEachGroup()
    {
        var result = BootRecoveryOrderPolicy.ColdFirst(["a", "b", "c", "d"], new HashSet<string> { "d", "b" });

        Assert.Equal(["b", "d", "a", "c"], result);
    }

    [Fact]
    public void ColdFirst_WithNoColdChannelsOrNoChannels_ReturnsTheOrderUnchanged()
    {
        Assert.Equal(["a", "b"], BootRecoveryOrderPolicy.ColdFirst(["a", "b"], new HashSet<string>()));
        Assert.Empty(BootRecoveryOrderPolicy.ColdFirst([], new HashSet<string> { "a" }));
    }

    [Fact]
    public void ColdFirst_IgnoresColdNamesThatAreNotInTheList()
    {
        Assert.Equal(["b", "a"], BootRecoveryOrderPolicy.ColdFirst(["a", "b"], new HashSet<string> { "zzz", "b" }));
    }
}
