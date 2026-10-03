using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// When a 7TV search answer blocks the shared bucket, and for how long (design note
/// <c>docs/Konzept-7TV-Such-Budget-2026-10-03.md</c>, 2.2).
/// </summary>
public class SevenTvSearchBlockPolicyTests
{
    private static readonly SevenTvSearchBudgetOptions Options = new();

    [Fact]
    public void AHealthyAnswer_BlocksNothing()
    {
        Assert.Null(SevenTvSearchBlockPolicy.BlockFor(new SevenTvSearchObservation(97, 55, RateLimited: false), Options));
    }

    [Fact]
    public void AnAnswerWithoutHeaders_BlocksNothing()
    {
        Assert.Null(SevenTvSearchBlockPolicy.BlockFor(new SevenTvSearchObservation(null, null, RateLimited: false), Options));
    }

    [Fact]
    public void ARateLimit_BlocksForTheClientsHint_First()
    {
        var block = SevenTvSearchBlockPolicy.BlockFor(
            new SevenTvSearchObservation(0, 100, RateLimited: true, TimeSpan.FromSeconds(3583)), Options);

        Assert.Equal(new SevenTvSearchBlock(TimeSpan.FromSeconds(3583), SevenTvSearchBlockCause.RateLimited), block);
    }

    [Fact]
    public void ARateLimit_WithoutAHint_FallsBackToTheResetHeader()
    {
        var block = SevenTvSearchBlockPolicy.BlockFor(new SevenTvSearchObservation(0, 1800, RateLimited: true), Options);

        Assert.Equal(TimeSpan.FromSeconds(1800), block?.Duration);
    }

    [Fact]
    public void ARateLimit_WithNoResetAtAll_BlocksForTheDefaultLockout()
    {
        var block = SevenTvSearchBlockPolicy.BlockFor(new SevenTvSearchObservation(null, null, RateLimited: true), Options);

        Assert.Equal(TimeSpan.FromSeconds(Options.DefaultLockoutSeconds), block?.Duration);
    }

    [Fact]
    public void ARateLimit_IsNeverShorterThanAMinute()
    {
        var block = SevenTvSearchBlockPolicy.BlockFor(
            new SevenTvSearchObservation(0, 5, RateLimited: true, TimeSpan.FromSeconds(5)), Options);

        Assert.Equal(TimeSpan.FromSeconds(60), block?.Duration);
    }

    [Fact]
    public void ABlock_IsCappedAtSixHours()
    {
        var block = SevenTvSearchBlockPolicy.BlockFor(
            new SevenTvSearchObservation(0, null, RateLimited: true, TimeSpan.FromDays(3)), Options);

        Assert.Equal(TimeSpan.FromSeconds(SevenTvSearchBudgetOptions.MaxBlockSeconds), block?.Duration);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(0)]
    public void ANearlyEmptyBucket_BlocksUntilTheReset(int remaining)
    {
        var block = SevenTvSearchBlockPolicy.BlockFor(new SevenTvSearchObservation(remaining, 42, RateLimited: false), Options);

        Assert.Equal(new SevenTvSearchBlock(TimeSpan.FromSeconds(42), SevenTvSearchBlockCause.LowWatermark), block);
    }

    [Fact]
    public void ABucketJustAboveTheWatermark_BlocksNothing()
    {
        Assert.Null(SevenTvSearchBlockPolicy.BlockFor(new SevenTvSearchObservation(11, 42, RateLimited: false), Options));
    }

    [Fact]
    public void ANearlyEmptyBucket_WithoutAReset_BlocksNothing()
    {
        // Nothing to block until; the next answer will carry the header again.
        Assert.Null(SevenTvSearchBlockPolicy.BlockFor(new SevenTvSearchObservation(3, null, RateLimited: false), Options));
    }

    [Fact]
    public void AWatermarkOfZero_OnlyBlocksAnEmptyBucket()
    {
        var options = new SevenTvSearchBudgetOptions { LowWatermark = 0 };

        Assert.Null(SevenTvSearchBlockPolicy.BlockFor(new SevenTvSearchObservation(1, 42, RateLimited: false), options));
        Assert.NotNull(SevenTvSearchBlockPolicy.BlockFor(new SevenTvSearchObservation(0, 42, RateLimited: false), options));
    }
}
