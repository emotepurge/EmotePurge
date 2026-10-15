using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Infrastructure.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class LogsAppendedNotifierTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ExpectedPayload = new LiveEvent(LiveEvents.LogsAppended).Serialize();

    private readonly FakeTimeProvider _time = new(Noon);
    private readonly IRedisPublisher _publisher = Substitute.For<IRedisPublisher>();
    private readonly IServiceProvider _services = Substitute.For<IServiceProvider>();

    public LogsAppendedNotifierTests()
    {
        _services.GetService(typeof(IRedisPublisher)).Returns(_publisher);
    }

    [Fact]
    public async Task Notifier_PublishesThinAdminEventOnLiveChannel()
    {
        var notifier = new LogsAppendedNotifier(_services, _time);

        notifier.OnBatchCommitted([Entry("EmotePurge.Worker.Worker")]);
        await WaitForPublishAsync(notifier);

        await _publisher.Received(1).PublishAsync(LiveEvents.Channel, ExpectedPayload, Arg.Any<CancellationToken>());
        Assert.Equal("logs.appended", LiveEvents.LogsAppended);
        Assert.Equal("""{"type":"logs.appended"}""", ExpectedPayload);
    }

    [Fact]
    public async Task Notifier_ThrottlesToOnePer2Seconds()
    {
        var notifier = new LogsAppendedNotifier(_services, _time);

        notifier.OnBatchCommitted([Entry("A")]);
        await WaitForPublishAsync(notifier);
        _time.Advance(TimeSpan.FromMilliseconds(1999));
        notifier.OnBatchCommitted([Entry("A")]);
        notifier.OnBatchCommitted([Entry("A")]);
        await WaitForPublishAsync(notifier);

        await _publisher.Received(1).PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromMilliseconds(1));
        notifier.OnBatchCommitted([Entry("A")]);
        await WaitForPublishAsync(notifier);

        await _publisher.Received(2).PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Notifier_ThrottledHint_IsNotAnnouncedLater()
    {
        // No dirty flag and no trailing announcement (spec 5.6): a throttled hint is simply gone.
        var notifier = new LogsAppendedNotifier(_services, _time);

        notifier.OnBatchCommitted([Entry("A")]);
        notifier.OnBatchCommitted([Entry("A")]);
        await WaitForPublishAsync(notifier);
        _time.Advance(TimeSpan.FromSeconds(10));
        await WaitForPublishAsync(notifier);

        await _publisher.Received(1).PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Notifier_SkipsSyntheticOnlyBatch()
    {
        var notifier = new LogsAppendedNotifier(_services, _time);

        notifier.OnBatchCommitted([Entry("EmotePurge.LogCapture", synthetic: true), Entry("EmotePurge.LogCapture", synthetic: true)]);
        await WaitForPublishAsync(notifier);

        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);

        notifier.OnBatchCommitted([Entry("EmotePurge.LogCapture", synthetic: true), Entry("EmotePurge.Worker.Worker")]);
        await WaitForPublishAsync(notifier);

        await _publisher.Received(1).PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Notifier_SkipsDeliveryCategoryOnlyBatch()
    {
        var notifier = new LogsAppendedNotifier(_services, _time);

        notifier.OnBatchCommitted(
        [
            Entry("EmotePurge.Infrastructure.Redis.RedisLiveEventStream"),
            Entry("EmotePurge.Api.Endpoints.LiveEndpoints"),
            Entry("EmotePurge.LogCapture", synthetic: true),
        ]);
        notifier.OnBatchCommitted([]);
        await WaitForPublishAsync(notifier);

        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);

        notifier.OnBatchCommitted([Entry("EmotePurge.Infrastructure.Redis.RedisLiveEventStream"), Entry("EmotePurge.Infrastructure.Redis.RedisLiveEventStreamX")]);
        await WaitForPublishAsync(notifier);

        await _publisher.Received(1).PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Notifier_RedisThrows_DoesNotPropagate()
    {
        _publisher.PublishAsync(default!, default!, default).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("redis down")));
        var notifier = new LogsAppendedNotifier(_services, _time);

        notifier.OnBatchCommitted([Entry("A")]);
        await WaitForPublishAsync(notifier);

        _publisher.PublishAsync(default!, default!, default).ThrowsForAnyArgs(new InvalidOperationException("redis down, synchronously"));
        _time.Advance(TimeSpan.FromSeconds(2));
        notifier.OnBatchCommitted([Entry("A")]);
        await WaitForPublishAsync(notifier);

        await _publisher.Received(2).PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Notifier_PublisherResolutionThrows_DoesNotPropagate()
    {
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IRedisPublisher)).Throws(new InvalidOperationException("no multiplexer"));
        var notifier = new LogsAppendedNotifier(services, _time);

        notifier.OnBatchCommitted([Entry("A")]);
        await WaitForPublishAsync(notifier);
    }

    [Fact]
    public async Task Notifier_NoPublisherRegistered_DoesNothing()
    {
        var notifier = new LogsAppendedNotifier(Substitute.For<IServiceProvider>(), _time);

        notifier.OnBatchCommitted([Entry("A")]);
        await WaitForPublishAsync(notifier);
    }

    [Fact]
    public void Notifier_DoesNotWaitForRedis()
    {
        var never = new TaskCompletionSource();
        _publisher.PublishAsync(default!, default!, default).ReturnsForAnyArgs(never.Task);
        var notifier = new LogsAppendedNotifier(_services, _time);

        notifier.OnBatchCommitted([Entry("A")]);

        // Returned without the publish having completed; the hint is fire and forget.
        Assert.False(never.Task.IsCompleted);
        never.SetResult();
    }

    private static async Task WaitForPublishAsync(LogsAppendedNotifier notifier)
    {
        if (notifier.LastPublish is { } publish)
        {
            await publish.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static CapturedLogEntry Entry(string category, bool synthetic = false) => new(
        Guid.CreateVersion7(),
        Noon.UtcDateTime,
        LogEntryLevel.Warning,
        category,
        null,
        null,
        "message",
        null,
        null,
        null,
        false,
        synthetic);
}
