using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The count of stuck removal orders (#487 plan 2.7 "Festsitzender Auftrag", K6e-5, K6f-2, K6f-3).
public class WishBotStuckRemovalsTests
{
    private const long Epoch = 7;
    private static readonly DateTime T0 = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly WishBotRemovalGeneration Generation = new(1, 4, T0.AddHours(-1));

    [Fact]
    public void ThreeAborts_AreDueOnlyOnceTheFirstIs15MinutesOld()
    {
        var stuck = new WishBotStuckRemovals();

        Assert.False(stuck.Abort(Epoch, Generation, T0));
        Assert.False(stuck.Abort(Epoch, Generation, T0.AddMinutes(1)));
        Assert.False(stuck.Abort(Epoch, Generation, T0.AddMinutes(2)));
        Assert.True(stuck.Abort(Epoch, Generation, T0.AddMinutes(15)));
        Assert.Equal(4, stuck.AbortsOf(Generation));
    }

    // K6f-2: another generation of the same wish starts at its own first abort.
    [Fact]
    public void AnotherGeneration_StartsAgain()
    {
        var stuck = new WishBotStuckRemovals();
        stuck.Abort(Epoch, Generation, T0);
        stuck.Abort(Epoch, Generation, T0.AddMinutes(10));

        var requeued = Generation with { QueuedAtUtc = T0.AddMinutes(11) };

        Assert.False(stuck.Abort(Epoch, requeued, T0.AddMinutes(16)));
        Assert.Equal(1, stuck.AbortsOf(requeued));
        Assert.Equal(0, stuck.AbortsOf(Generation));
    }

    // K6f-3: another runner epoch drops every count; so does a reset, and a wish no longer queued.
    [Fact]
    public void AnotherEpoch_AResetAndAWishNoLongerQueued_DropTheCount()
    {
        var stuck = new WishBotStuckRemovals();
        var other = new WishBotRemovalGeneration(2, 1, T0);
        stuck.Abort(Epoch, Generation, T0);
        stuck.Abort(Epoch, other, T0);

        stuck.Retain(Epoch, [Generation.WishId]);
        Assert.Equal((1, 0), (stuck.AbortsOf(Generation), stuck.AbortsOf(other)));

        stuck.Reset(Generation.WishId);
        Assert.Equal(0, stuck.AbortsOf(Generation));

        stuck.Abort(Epoch, Generation, T0);
        stuck.Abort(Epoch, Generation, T0.AddMinutes(1));
        stuck.Retain(Epoch + 1, [Generation.WishId]);
        Assert.Equal(0, stuck.AbortsOf(Generation));

        stuck.Abort(Epoch + 1, Generation, T0.AddMinutes(20));
        Assert.False(stuck.Abort(Epoch + 2, Generation, T0.AddMinutes(40)));
        Assert.Equal(1, stuck.AbortsOf(Generation));
    }
}
