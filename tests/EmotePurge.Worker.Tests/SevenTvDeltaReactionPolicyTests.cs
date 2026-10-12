using EmotePurge.Core.SevenTv;
using EmotePurge.Worker.SevenTv;
using Xunit;

namespace EmotePurge.Worker.Tests;

public class SevenTvDeltaReactionPolicyTests
{
    private static readonly Dictionary<SevenTvDeltaOutcome, SevenTvDeltaReaction> Expected = new()
    {
        [SevenTvDeltaOutcome.Applied] = SevenTvDeltaReaction.PublishSynced,
        [SevenTvDeltaOutcome.NoChange] = SevenTvDeltaReaction.None,
        [SevenTvDeltaOutcome.ChannelUnknown] = SevenTvDeltaReaction.RemoveSubscription,
        [SevenTvDeltaOutcome.SetNotActive] = SevenTvDeltaReaction.ResyncAdoptResult,
        [SevenTvDeltaOutcome.ImplausibleSkipped] = SevenTvDeltaReaction.ResyncAdoptResult,
        [SevenTvDeltaOutcome.AmbiguousDelta] = SevenTvDeltaReaction.ResyncAdoptResult
    };

    [Theory]
    [InlineData(SevenTvDeltaOutcome.Applied, SevenTvDeltaReaction.PublishSynced)]
    [InlineData(SevenTvDeltaOutcome.NoChange, SevenTvDeltaReaction.None)]
    [InlineData(SevenTvDeltaOutcome.ChannelUnknown, SevenTvDeltaReaction.RemoveSubscription)]
    [InlineData(SevenTvDeltaOutcome.SetNotActive, SevenTvDeltaReaction.ResyncAdoptResult)]
    [InlineData(SevenTvDeltaOutcome.ImplausibleSkipped, SevenTvDeltaReaction.ResyncAdoptResult)]
    public void Decide_KeepsTheExistingReactions(SevenTvDeltaOutcome outcome, SevenTvDeltaReaction reaction) =>
        Assert.Equal(reaction, SevenTvDeltaReactionPolicy.Decide(outcome));

    [Fact]
    public void Decide_AmbiguousDelta_ResyncsAndNeverPublishes()
    {
        var reaction = SevenTvDeltaReactionPolicy.Decide(SevenTvDeltaOutcome.AmbiguousDelta);

        Assert.Equal(SevenTvDeltaReaction.ResyncAdoptResult, reaction);
        Assert.NotEqual(SevenTvDeltaReaction.PublishSynced, reaction);
    }

    [Fact]
    public void Decide_CoversEveryOutcomeExplicitly()
    {
        foreach (var outcome in Enum.GetValues<SevenTvDeltaOutcome>())
        {
            Assert.True(Expected.TryGetValue(outcome, out var reaction), $"{outcome} has no expected reaction");
            Assert.Equal(reaction, SevenTvDeltaReactionPolicy.Decide(outcome));
        }
    }

    [Fact]
    public void Decide_UnknownOutcome_FallsBackToResync() =>
        Assert.Equal(SevenTvDeltaReaction.ResyncAdoptResult, SevenTvDeltaReactionPolicy.Decide((SevenTvDeltaOutcome)999));
}
