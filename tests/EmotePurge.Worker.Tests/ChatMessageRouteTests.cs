using EmotePurge.Worker.WishCapture;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The order "exclusion, then capture, then the cache check" of the chat handler (plan #486 2.1, AK49, AK65, AK67).
// TwitchChatManager follows these flags; the manager itself is verified live (rule 16, live step L0).
public class ChatMessageRouteTests
{
    // Every combination of the five inputs; "only" pins one input to a value (0 = isExcluded, 1 = captureEnabled).
    public static TheoryData<bool, bool, bool, bool, bool> Inputs(int only = -1, bool value = false)
    {
        var data = new TheoryData<bool, bool, bool, bool, bool>();
        foreach (var isExcluded in only == 0 ? [value] : new[] { false, true })
        {
            foreach (var captureEnabled in only == 1 ? [value] : new[] { false, true })
            {
                foreach (var hasRewardTag in new[] { false, true })
                {
                    foreach (var isSpliced in new[] { false, true })
                    {
                        foreach (var snapshotIsEmpty in new[] { false, true })
                        {
                            data.Add(isExcluded, captureEnabled, hasRewardTag, isSpliced, snapshotIsEmpty);
                        }
                    }
                }
            }
        }

        return data;
    }

    // AK67 (second half): an excluded chatter's line does nothing at all, with or without the tag.
    [Theory]
    [MemberData(nameof(Inputs), 0, true)]
    public void ExcludedChatter_GetsNoCountNoObservationNoMatching(
        bool isExcluded, bool captureEnabled, bool hasRewardTag, bool isSpliced, bool snapshotIsEmpty)
    {
        Assert.Equal(default, ChatMessageRoute.Decide(isExcluded, captureEnabled, hasRewardTag, isSpliced, snapshotIsEmpty));
    }

    // AK49: whether the emotes are matched never depends on the reward tag or the capture switch.
    [Theory]
    [MemberData(nameof(Inputs), -1, false)]
    public void MatchEmotes_IsTheSameWithAndWithoutTheTag_AndWithTheSwitchOnOrOff(
        bool isExcluded, bool captureEnabled, bool hasRewardTag, bool isSpliced, bool snapshotIsEmpty)
    {
        var route = ChatMessageRoute.Decide(isExcluded, captureEnabled, hasRewardTag, isSpliced, snapshotIsEmpty);
        var withoutK6 = ChatMessageRoute.Decide(isExcluded, captureEnabled: false, hasRewardTag: false, isSpliced, snapshotIsEmpty);

        Assert.Equal(withoutK6.MatchEmotes, route.MatchEmotes);
        Assert.Equal(withoutK6.CountLine, route.CountLine);
        Assert.Equal(!isExcluded && !snapshotIsEmpty, route.MatchEmotes);
    }

    // AK65: with the switch off the capture branch is never entered, not even for a line with the tag, and a
    // spliced reward line is not counted either.
    [Theory]
    [MemberData(nameof(Inputs), 1, false)]
    public void SwitchOff_NeverObserves(
        bool isExcluded, bool captureEnabled, bool hasRewardTag, bool isSpliced, bool snapshotIsEmpty)
    {
        var route = ChatMessageRoute.Decide(isExcluded, captureEnabled, hasRewardTag, isSpliced, snapshotIsEmpty);

        Assert.False(route.Observe);
        Assert.False(route.CountSplicedRedemption);
    }

    // Observe exactly for an accepted, unspliced line with the tag while capturing — independent of the snapshot.
    [Theory]
    [MemberData(nameof(Inputs), -1, false)]
    public void Observe_FollowsExclusionSwitchTagAndSplice_ButNotTheSnapshot(
        bool isExcluded, bool captureEnabled, bool hasRewardTag, bool isSpliced, bool snapshotIsEmpty)
    {
        var route = ChatMessageRoute.Decide(isExcluded, captureEnabled, hasRewardTag, isSpliced, snapshotIsEmpty);

        Assert.Equal(!isExcluded && captureEnabled && hasRewardTag && !isSpliced, route.Observe);
        Assert.Equal(!isExcluded && captureEnabled && hasRewardTag && isSpliced, route.CountSplicedRedemption);
        Assert.False(route.Observe && route.CountSplicedRedemption);
    }

    // AK67 (first half): a line with the tag in a channel with an empty snapshot is observed, and the matching stops.
    [Fact]
    public void EmptySnapshotWithTheTag_IsObserved_AndNotMatched()
    {
        var route = ChatMessageRoute.Decide(isExcluded: false, captureEnabled: true, hasRewardTag: true, isSpliced: false, snapshotIsEmpty: true);

        Assert.Equal(new ChatMessageRoute(CountLine: true, Observe: true, CountSplicedRedemption: false, MatchEmotes: false), route);
    }

    // W23: a spliced line with the tag is counted, not observed, and still matched like any other line.
    [Fact]
    public void SplicedLineWithTheTag_IsCountedNotObserved_AndStillMatched()
    {
        var route = ChatMessageRoute.Decide(isExcluded: false, captureEnabled: true, hasRewardTag: true, isSpliced: true, snapshotIsEmpty: false);

        Assert.Equal(new ChatMessageRoute(CountLine: true, Observe: false, CountSplicedRedemption: true, MatchEmotes: true), route);
    }

    [Fact]
    public void OrdinaryLine_IsCountedAndMatchedOnly()
    {
        var route = ChatMessageRoute.Decide(isExcluded: false, captureEnabled: true, hasRewardTag: false, isSpliced: false, snapshotIsEmpty: false);

        Assert.Equal(new ChatMessageRoute(CountLine: true, Observe: false, CountSplicedRedemption: false, MatchEmotes: true), route);
    }
}
