using Microsoft.Extensions.Logging;

namespace EmotePurge.Worker.SevenTv;

/// <summary>
/// Isolation rule for a dispatch that fans out to every channel sharing one emote set: the channels
/// are independent rows, so one failing must not skip the ones after it. The periodic resync
/// reconciles whatever was skipped (issue #59). Kept free of the socket so the rule is testable
/// without <see cref="SevenTvEventClient"/>.
/// </summary>
public static class PerChannelFanOut
{
    /// <summary>
    /// Runs <paramref name="apply"/> sequentially for each channel. A failure is logged and
    /// swallowed; cancellation of <paramref name="ct"/> still propagates.
    /// </summary>
    public static async Task ApplyAsync(
        IEnumerable<string> channels, Func<string, Task> apply, ILogger logger, CancellationToken ct)
    {
        foreach (var channelName in channels)
        {
            try
            {
                await apply(channelName);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "7TV delta for {Channel} of a shared set failed, continuing with the other channels.", channelName);
            }
        }
    }
}
