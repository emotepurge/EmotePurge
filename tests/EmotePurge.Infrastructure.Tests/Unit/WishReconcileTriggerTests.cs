using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The wrapper the slot writers start the wish reconcile through (plan 483 T8): switch, swallow, cancellation.
public class WishReconcileTriggerTests
{
    [Fact]
    public async Task SwitchOff_NeverCallsTheReconcile()
    {
        var reconcile = Substitute.For<IWishReconcileService>();
        var trigger = new WishReconcileTrigger(reconcile, new WishOptions { Enabled = false }, NullLogger<WishReconcileTrigger>.Instance);

        await trigger.TriggerAsync("channel-id");

        Assert.Empty(reconcile.ReceivedCalls());
    }

    [Fact]
    public async Task AFailure_IsSwallowedAndLoggedOnce_WithoutAnythingButTheChannelId()
    {
        var reconcile = Substitute.For<IWishReconcileService>();
        reconcile.ReconcileChannelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        var logger = new RecordingLogger<WishReconcileTrigger>();

        await new WishReconcileTrigger(reconcile, new WishOptions { Enabled = true }, logger).TriggerAsync("channel-id");

        var line = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("channel-id", line.Message, StringComparison.Ordinal);
    }

    // An OperationCanceledException that is not the caller's cancellation (an inner timeout, a cancelled
    // linked token) is an ordinary failure of the reconcile: logged and swallowed.
    [Fact]
    public async Task ACancellationExceptionWhileTheCallersTokenIsNotCancelled_IsLoggedAndSwallowed()
    {
        var reconcile = Substitute.For<IWishReconcileService>();
        reconcile.ReconcileChannelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());
        var logger = new RecordingLogger<WishReconcileTrigger>();

        await new WishReconcileTrigger(reconcile, new WishOptions { Enabled = true }, logger).TriggerAsync("channel-id", CancellationToken.None);

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task ACancellationOfTheCallersToken_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var reconcile = Substitute.For<IWishReconcileService>();
        reconcile.ReconcileChannelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException(cts.Token));
        var trigger = new WishReconcileTrigger(reconcile, new WishOptions { Enabled = true }, NullLogger<WishReconcileTrigger>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trigger.TriggerAsync("channel-id", cts.Token));
    }
}
