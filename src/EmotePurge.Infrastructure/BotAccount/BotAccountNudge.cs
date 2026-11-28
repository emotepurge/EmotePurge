using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// <see cref="IBotAccountNudge"/>: publishes <c>BOTACCOUNT:</c> on the bot command channel. Called after the commit; a
/// failed publish is logged and swallowed (the worker's poll converges), a caller's cancellation still propagates.
/// </summary>
public sealed class BotAccountNudge(IRedisPublisher redisPublisher, ILogger<BotAccountNudge> logger) : IBotAccountNudge
{
    public async Task NudgeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await redisPublisher.PublishAsync(BotCommands.Channel, BotCommands.BotAccountPrefix, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "Bot account: publishing the worker nudge failed ({ExceptionType}); the row is committed and the worker's poll converges.",
                ex.GetType().Name);
        }
    }
}
