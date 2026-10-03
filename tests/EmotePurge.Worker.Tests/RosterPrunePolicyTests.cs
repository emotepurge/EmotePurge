using Xunit;

namespace EmotePurge.Worker.Tests;

// Pure decision logic, no container and no TwitchChatManager needed — the roster and the active-set
// are both passed in. Every rule under test traces back to issue #41: a lost Redis LEAVE command
// never pruned _desiredChannels, so the worker kept matching chat, holding the 7TV EventAPI
// subscription and reporting the channel forever.
public class RosterPrunePolicyTests
{
    [Fact]
    public void DetermineChannelsToPrune_ConfirmedChannelStaleForTwoConsecutiveTicks_IsPruned()
    {
        var roster = new[] { new TwitchRosterEntry("handofblood", JoinConfirmed: true, LastMessageUtc: null) };

        var firstTick = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, previouslyStaleChannels: []);
        Assert.Empty(firstTick.ChannelsToPrune);

        var secondTick = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, firstTick.StaleChannels);

        Assert.Equal(["handofblood"], secondTick.ChannelsToPrune);
    }

    [Fact]
    public void DetermineChannelsToPrune_ChannelStillActive_IsNotPruned()
    {
        var roster = new[] { new TwitchRosterEntry("handofblood", JoinConfirmed: true, LastMessageUtc: null) };

        var toPrune = RosterPrunePolicy.DetermineChannelsToPrune(["handofblood"], roster, previouslyStaleChannels: []);

        Assert.Empty(toPrune.ChannelsToPrune);
    }

    [Fact]
    public void DetermineChannelsToPrune_ActiveComparisonIsCaseInsensitive()
    {
        // ChannelService normalizes to lowercase (Regel 9), but nothing here should rely on that —
        // a mismatch in casing between the DB read and the roster must not look like "not active".
        var roster = new[] { new TwitchRosterEntry("HandOfBlood", JoinConfirmed: true, LastMessageUtc: null) };

        var toPrune = RosterPrunePolicy.DetermineChannelsToPrune(["handofblood"], roster, previouslyStaleChannels: []);

        Assert.Empty(toPrune.ChannelsToPrune);
    }

    [Fact]
    public void DetermineChannelsToPrune_UnconfirmedChannelStaleForOnlyOneTick_IsNotPruned()
    {
        // The guard against the race where a channel just joined (unconfirmed) can appear in the
        // roster moments after this tick's DB snapshot was taken but before it reflects the new row
        // — see the class comment on RosterPrunePolicy. A single stale tick must not prune it: that
        // would drop a channel that was never actually inactive.
        var roster = new[] { new TwitchRosterEntry("handofblood", JoinConfirmed: false, LastMessageUtc: null) };

        var toPrune = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, previouslyStaleChannels: []);

        Assert.Empty(toPrune.ChannelsToPrune);
        Assert.Equal(["handofblood"], toPrune.StaleChannels);
    }

    [Fact]
    public void DetermineChannelsToPrune_UnconfirmedChannelStaleForTwoConsecutiveTicks_IsPrunedAnyway()
    {
        // The fix for the hole the pure JoinConfirmed guard left open: an entry that never confirms
        // (banned bot account, deleted channel, a hanging JOIN) must still be pruned eventually, not
        // held forever just because nothing ever flips JoinConfirmed to true.
        var roster = new[] { new TwitchRosterEntry("handofblood", JoinConfirmed: false, LastMessageUtc: null) };

        var firstTick = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, previouslyStaleChannels: []);
        Assert.Empty(firstTick.ChannelsToPrune);

        var secondTick = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, firstTick.StaleChannels);

        Assert.Equal(["handofblood"], secondTick.ChannelsToPrune);
    }

    [Fact]
    public void DetermineChannelsToPrune_ChannelBecomesActiveAgainBetweenStaleTicks_ClearsItsStaleState()
    {
        // A channel stale on tick 1 that is active again on tick 2 must not be pruned on tick 2, and
        // must not carry a leftover stale marker into tick 3 that would prune it despite being active
        // in between.
        var roster = new[] { new TwitchRosterEntry("handofblood", JoinConfirmed: true, LastMessageUtc: null) };

        var firstTick = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, previouslyStaleChannels: []);
        Assert.Equal(["handofblood"], firstTick.StaleChannels);

        var secondTick = RosterPrunePolicy.DetermineChannelsToPrune(["handofblood"], roster, firstTick.StaleChannels);
        Assert.Empty(secondTick.ChannelsToPrune);
        Assert.Empty(secondTick.StaleChannels);

        var thirdTick = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, secondTick.StaleChannels);
        Assert.Empty(thirdTick.ChannelsToPrune);
    }

    [Fact]
    public void DetermineChannelsToPrune_MixedRoster_OnlyPrunesTheEntriesStaleForTwoConsecutiveTicks()
    {
        var roster = new[]
        {
            new TwitchRosterEntry("stillactive", JoinConfirmed: true, LastMessageUtc: null),
            new TwitchRosterEntry("leftbehind", JoinConfirmed: true, LastMessageUtc: null),
            new TwitchRosterEntry("justjoining", JoinConfirmed: false, LastMessageUtc: null),
        };

        var firstTick = RosterPrunePolicy.DetermineChannelsToPrune(["stillactive"], roster, previouslyStaleChannels: []);
        Assert.Empty(firstTick.ChannelsToPrune);
        Assert.Equal(new HashSet<string>(["leftbehind", "justjoining"]), new HashSet<string>(firstTick.StaleChannels));

        var secondTick = RosterPrunePolicy.DetermineChannelsToPrune(["stillactive"], roster, firstTick.StaleChannels);

        Assert.Equal(
            new HashSet<string>(["leftbehind", "justjoining"]),
            new HashSet<string>(secondTick.ChannelsToPrune));
    }

    [Fact]
    public void DetermineChannelsToPrune_EmptyRoster_PrunesNothing()
    {
        var toPrune = RosterPrunePolicy.DetermineChannelsToPrune(["handofblood"], roster: [], previouslyStaleChannels: []);

        Assert.Empty(toPrune.ChannelsToPrune);
        Assert.Empty(toPrune.StaleChannels);
    }

    // Issue #59: a sync in flight across a rename or deactivation re-creates a match-cache entry or
    // a registry subscription under the retired login after its LEAVE already ran — the name is on
    // no roster, so a roster-only walk never sees it.
    [Fact]
    public void DetermineChannelsToPrune_GhostOnlyInCache_IsPrunedAfterTwoTicks()
    {
        var firstTick = RosterPrunePolicy.DetermineChannelsToPrune(["current"], roster: [], previouslyStaleChannels: [], ghostCandidates: ["oldlogin"]);
        Assert.Empty(firstTick.ChannelsToPrune);

        var secondTick = RosterPrunePolicy.DetermineChannelsToPrune(["current"], roster: [], firstTick.StaleChannels, ghostCandidates: ["oldlogin"]);

        Assert.Equal(["oldlogin"], secondTick.ChannelsToPrune);
    }

    [Fact]
    public void DetermineChannelsToPrune_GhostOnlyInRegistry_IsPrunedAfterTwoTicks()
    {
        // The registry normalizes on SetDesired, but this policy must not rely on that: a differently
        // cased candidate has to match its lowercase counterpart.
        var firstTick = RosterPrunePolicy.DetermineChannelsToPrune([], roster: [], previouslyStaleChannels: [], ghostCandidates: ["GhostLogin"]);
        var secondTick = RosterPrunePolicy.DetermineChannelsToPrune([], roster: [], firstTick.StaleChannels, ghostCandidates: ["ghostlogin"]);

        Assert.Equal(["ghostlogin"], secondTick.ChannelsToPrune);
    }

    [Fact]
    public void DetermineChannelsToPrune_ActiveChannelHeldInMemory_IsNeverPruned()
    {
        var firstTick = RosterPrunePolicy.DetermineChannelsToPrune(["handofblood"], roster: [], previouslyStaleChannels: [], ghostCandidates: ["handofblood"]);
        var secondTick = RosterPrunePolicy.DetermineChannelsToPrune(["handofblood"], roster: [], firstTick.StaleChannels, ghostCandidates: ["HandOfBlood"]);

        Assert.Empty(secondTick.ChannelsToPrune);
        Assert.Empty(secondTick.StaleChannels);
    }

    [Fact]
    public void DetermineChannelsToPrune_FreshlyJoinedChannelHeldInMemory_StaysWithinTheGrace()
    {
        // Join committed after this tick's DB snapshot: the cache already holds it, the snapshot
        // does not. One stale tick must not prune it.
        var result = RosterPrunePolicy.DetermineChannelsToPrune([], roster: [], previouslyStaleChannels: [], ghostCandidates: ["justjoined"]);

        Assert.Empty(result.ChannelsToPrune);
        Assert.Equal(["justjoined"], result.StaleChannels);
    }

    [Fact]
    public void DetermineChannelsToPrune_NameOnRosterAndInMemory_IsReportedOnce()
    {
        var roster = new[] { new TwitchRosterEntry("leftbehind", JoinConfirmed: true, LastMessageUtc: null) };
        var firstTick = RosterPrunePolicy.DetermineChannelsToPrune([], roster, previouslyStaleChannels: [], ghostCandidates: ["leftbehind", "LeftBehind"]);

        var secondTick = RosterPrunePolicy.DetermineChannelsToPrune([], roster, firstTick.StaleChannels, ghostCandidates: ["leftbehind", "LeftBehind"]);

        Assert.Equal(["leftbehind"], secondTick.ChannelsToPrune);
    }
}
