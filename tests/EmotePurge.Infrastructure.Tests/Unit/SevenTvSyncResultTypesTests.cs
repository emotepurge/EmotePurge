using System.Reflection;
using EmotePurge.Core.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Issue #60 gave both sync result types a payload with a rule attached — the login the row actually
// carried — so both are closed per the decision-log entry of 2026-09-04. These cases pin the doors
// and the guards; the behaviour itself (that the login really is the post-rename one) is proven
// against real Postgres in SevenTvSyncServiceRenameHandoverTests.
public class SevenTvSyncResultTypesTests
{
    [Fact]
    public void NoPublicConstructor_AndNoWithExpression()
    {
        foreach (var type in new[] { typeof(SevenTvSyncResult), typeof(SevenTvDeltaResult) })
        {
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

            // `with` compiles to a call to the compiler-generated clone method every record carries.
            Assert.Null(type.GetMethod(
                "<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        }
    }

    [Fact]
    public void SyncResult_WithoutAChannelName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SevenTvSyncResult.Create(null!, "set", "7tv", hasChanges: true));
        Assert.Throws<ArgumentException>(() => SevenTvSyncResult.Create("  ", "set", "7tv", hasChanges: true));
    }

    [Fact]
    public void SyncResult_WithABlankSetId_Throws()
    {
        // Safe only because SevenTvSyncService rejects an unusable 7TV answer before it writes
        // anything (SyncChannel_OkWithABlankSetId_IsRejectedBeforeAnythingIsWritten). This factory
        // runs after SaveChangesAsync, so the guard is allowed to state an impossibility and must
        // never be the place a real case first shows up — the Codex review of this branch caught it
        // being exactly that, and the fix was the early rejection, not a weaker guard here.
        Assert.Throws<ArgumentNullException>(() => SevenTvSyncResult.Create("chan", null!, null, hasChanges: false));
        Assert.Throws<ArgumentException>(() => SevenTvSyncResult.Create("chan", "  ", null, hasChanges: false));
    }

    [Fact]
    public void SyncResult_CarriesEveryValueItWasGiven()
    {
        var result = SevenTvSyncResult.Create("handover_new", "set-1", "7tv-user", hasChanges: true);

        Assert.Equal("handover_new", result.ChannelName);
        Assert.Equal("set-1", result.EmoteSetId);
        Assert.Equal("7tv-user", result.SevenTvUserId);
        Assert.True(result.HasChanges);

        // A channel with no 7TV account id is a legitimate result — only the login and the set id
        // are required.
        Assert.Null(SevenTvSyncResult.Create("chan", "set-1", null, hasChanges: false).SevenTvUserId);
    }

    [Theory]
    [InlineData(SevenTvDeltaOutcome.Applied)]
    [InlineData(SevenTvDeltaOutcome.NoChange)]
    [InlineData(SevenTvDeltaOutcome.SetNotActive)]
    [InlineData(SevenTvDeltaOutcome.ImplausibleSkipped)]
    [InlineData(SevenTvDeltaOutcome.AmbiguousDelta)]
    public void DeltaResult_ForChannel_CarriesTheLogin(SevenTvDeltaOutcome outcome)
    {
        var result = SevenTvDeltaResult.ForChannel(outcome, "handover_new");

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal("handover_new", result.ChannelName);
    }

    [Fact]
    public void DeltaResult_ForChannel_RejectsChannelUnknown()
    {
        // "No row was found" and "here is the row's login" cannot both be true.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.ChannelUnknown, "somechannel"));

        Assert.Equal("outcome", ex.ParamName);
    }

    [Theory]
    [InlineData(SevenTvDeltaOutcome.Applied)]
    [InlineData(SevenTvDeltaOutcome.SetNotActive)]
    [InlineData(SevenTvDeltaOutcome.ImplausibleSkipped)]
    [InlineData(SevenTvDeltaOutcome.AmbiguousDelta)]
    public void DeltaResult_WithoutChannel_RejectsTheOutcomesThatProvablyReadTheRow(SevenTvDeltaOutcome outcome)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => SevenTvDeltaResult.WithoutChannel(outcome));

        Assert.Equal("outcome", ex.ParamName);
    }

    [Fact]
    public void DeltaResult_WithoutChannel_AllowsTheTwoOutcomesThatNeverSeeARow()
    {
        // NoChange straddles both factories on purpose: an empty delta short-circuits before any
        // database access, while a no-op against stored rows has been through the gate.
        Assert.Null(SevenTvDeltaResult.WithoutChannel(SevenTvDeltaOutcome.NoChange).ChannelName);
        Assert.Null(SevenTvDeltaResult.WithoutChannel(SevenTvDeltaOutcome.ChannelUnknown).ChannelName);
    }

    [Fact]
    public void DeltaResult_RejectsAnUndefinedOutcome()
    {
        const SevenTvDeltaOutcome Undefined = (SevenTvDeltaOutcome)9;

        Assert.Throws<ArgumentOutOfRangeException>(() => SevenTvDeltaResult.ForChannel(Undefined, "chan"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SevenTvDeltaResult.WithoutChannel(Undefined));
    }

    [Fact]
    public void DeltaResult_ForChannel_WithoutALogin_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.Applied, null!));
        Assert.Throws<ArgumentException>(
            () => SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.Applied, "  "));
    }
}
