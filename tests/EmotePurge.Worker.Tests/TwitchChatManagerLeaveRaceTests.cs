using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The manager's desired-channel bookkeeping only, on a client that is never connected: no socket is
// opened, no TwitchLib behaviour is faked, and every JOIN stops at "not connected" after the intent
// has (or has not) been recorded. The roster is that intent, so it is what these tests read.
public class TwitchChatManagerLeaveRaceTests
{
    // #245 live run, 2026-10-08: the periodic resync read the active roster, the identity reconcile
    // then deactivated a locked channel and its LEAVE was processed, and the resync's walk reached the
    // channel and joined it straight back from its stale list (Quelle ConvergenceNet).
    [Fact]
    public async Task EnsureJoined_FromARosterReadBeforeTheLeave_DoesNotRecreateTheIntent()
    {
        var manager = CreateManager();
        await manager.JoinChannelAsync("sensitron");

        var stamp = manager.CaptureLeaveStamp();
        await manager.LeaveChannelAsync("sensitron");
        await manager.EnsureJoinedAsync("sensitron", stamp);

        Assert.DoesNotContain(manager.GetRoster(), e => e.ChannelName == "sensitron");
    }

    // The convergence net itself must keep working: a roster read after the LEAVE that still lists the
    // channel means it is active again (its JOIN command got lost), and it is joined.
    [Fact]
    public async Task EnsureJoined_FromARosterReadAfterTheLeave_RecreatesTheIntent()
    {
        var manager = CreateManager();
        await manager.LeaveChannelAsync("sensitron");

        var stamp = manager.CaptureLeaveStamp();
        await manager.EnsureJoinedAsync("sensitron", stamp);

        Assert.Contains(manager.GetRoster(), e => e.ChannelName == "sensitron");
    }

    [Fact]
    public async Task EnsureJoined_ALeaveOfAnotherChannel_DoesNotBlockThisOne()
    {
        var manager = CreateManager();
        var stamp = manager.CaptureLeaveStamp();

        await manager.LeaveChannelAsync("t8xq_gibtsnicht_alt");
        await manager.EnsureJoinedAsync("olaf_olaf_son", stamp);

        Assert.Contains(manager.GetRoster(), e => e.ChannelName == "olaf_olaf_son");
    }

    // A roster-checked JOIN after the LEAVE is newer than the stale read: the channel stays desired.
    [Fact]
    public async Task Join_AfterTheLeave_IsNotUndoneByTheStaleEnsureJoined()
    {
        var manager = CreateManager();
        var stamp = manager.CaptureLeaveStamp();
        await manager.LeaveChannelAsync("sensitron");
        await manager.JoinChannelAsync("sensitron");

        await manager.EnsureJoinedAsync("sensitron", stamp);

        Assert.Contains(manager.GetRoster(), e => e.ChannelName == "sensitron");
    }

    private static TwitchChatManager CreateManager() => new(
        NullLogger<TwitchChatManager>.Instance,
        NullLoggerFactory.Instance,
        Substitute.For<IEmoteMatchCache>(),
        Substitute.For<IEmoteUsageCounter>(),
        Substitute.For<IBotChatterDetector>(),
        Substitute.For<IExcludedChatterFilter>(),
        new WorkerStats());
}
