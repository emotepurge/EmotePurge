using EmotePurge.Core.Messaging;
using EmotePurge.Infrastructure.BotAccount;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The nudge of the bot account loop after a committed channel write (#487 plan 2.9): the bare prefix on the bot command
// channel; a Redis failure never fails the caller, its own cancellation does.
public class BotAccountNudgeTests
{
    private readonly IRedisPublisher _publisher = Substitute.For<IRedisPublisher>();

    [Fact]
    public async Task PublishesTheBarePrefix_OnTheBotCommandChannel()
    {
        await Sut().NudgeAsync();

        await _publisher.Received(1).PublishAsync(BotCommands.Channel, BotCommands.BotAccountPrefix, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARedisFailure_IsSwallowed()
    {
        _publisher.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        await Sut().NudgeAsync();
    }

    [Fact]
    public async Task TheCallersCancellation_Propagates()
    {
        _publisher.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Sut().NudgeAsync());
    }

    private BotAccountNudge Sut() => new(_publisher, NullLogger<BotAccountNudge>.Instance);
}
