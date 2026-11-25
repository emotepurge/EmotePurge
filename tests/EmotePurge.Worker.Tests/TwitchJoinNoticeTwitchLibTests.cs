using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Deliberately TwitchLib-bound, like IrcLineSpliceRuleTwitchLibTests: the classification of join
// failures (plan #516, E2) rests on three facts about the installed TwitchLib.Client 4.0.1 that no pure
// test can see, so real lines run through the library's own receive path over a scripted transport.
// What turns red here after a TwitchLib update is the premise of TwitchChatManager's
// OnFailureToReceiveJoinConfirmation classification (Details null = timer, not null = suspended, which
// it ignores) and of the NOTICE branch in OnSendReceiveData (the limit notice never reaches a callback;
// a suspended notice reaches OnSendReceiveData before TwitchLib sends its next queued JOIN, and the
// callback only after that JOIN).
public class TwitchJoinNoticeTwitchLibTests
{
    private const string Channel = "probechan";

    [Fact]
    public async Task SuspendedNotice_RaisesTheFailureCallbackWithDetails()
    {
        var (_, transport, failures) = await ConnectedClientJoiningAsync();

        await transport.ReceiveAsync(
            $"@msg-id=msg_channel_suspended :tmi.twitch.tv NOTICE #{Channel} :This channel has been suspended.");

        var failure = Assert.Single(failures);
        Assert.Equal(Channel, failure.Exception.Channel);
        Assert.NotNull(failure.Exception.Details);
    }

    [Fact]
    public async Task SuspendedNotice_ReachesTheRawLineBeforeTheQueuedRetry_AndTheCallbackAfterIt()
    {
        var transport = new FakeIrcTransport();
        var client = new TwitchClient(client: transport);
        client.Initialize(new ConnectionCredentials());
        var joinsAtRawNotice = -1;
        var joinsAtCallback = -1;
        client.OnSendReceiveData += (_, e) =>
        {
            if (e.Data.Contains("msg_channel_suspended", StringComparison.Ordinal))
            {
                joinsAtRawNotice = transport.JoinsSentFor(Channel);
            }

            return Task.CompletedTask;
        };
        client.OnFailureToReceiveJoinConfirmation += (_, _) =>
        {
            joinsAtCallback = transport.JoinsSentFor(Channel);
            return Task.CompletedTask;
        };
        Assert.True(await client.ConnectAsync());
        await client.JoinChannelAsync("holder");
        await client.JoinChannelAsync(Channel);
        await client.JoinChannelAsync(Channel); // queued twice: only joined channels are checked, not the queue
        await transport.ReceiveAsync($":tmi.twitch.tv 366 {client.TwitchUsername} #holder :End of /NAMES list");
        Assert.Equal(1, transport.JoinsSentFor(Channel));

        await transport.ReceiveAsync(
            $"@msg-id=msg_channel_suspended :tmi.twitch.tv NOTICE #{Channel} :This channel has been suspended.");

        Assert.Equal(1, joinsAtRawNotice);
        Assert.Equal(2, joinsAtCallback);
    }

    [Fact]
    public async Task LimitNotice_RaisesNoFailureCallback()
    {
        var (_, transport, failures) = await ConnectedClientJoiningAsync();

        await transport.ReceiveAsync(
            $"@msg-id=msg_concurrent_channel_limit_reached :tmi.twitch.tv NOTICE #{Channel} :You are connected to too many chat channels.");

        Assert.Empty(failures);
    }

    [Fact]
    public async Task JoinTimerExpiry_RaisesTheFailureCallbackWithoutDetails()
    {
        var (client, _, failures) = await ConnectedClientJoiningAsync();

        // TwitchLib's own timer: a 1s tick drops an entry older than 5s, so the callback comes at
        // 5-6s; the wait below is that real timer, there is no seam to shorten it.
        await WaitUntilAsync(() => failures.Count > 0, TimeSpan.FromSeconds(15));

        var failure = Assert.Single(failures);
        Assert.Equal(Channel, failure.Exception.Channel);
        Assert.Null(failure.Exception.Details);
    }

    private static async Task<(TwitchClient Client, FakeIrcTransport Transport, List<OnFailureToReceiveJoinConfirmationArgs> Failures)>
        ConnectedClientJoiningAsync()
    {
        var transport = new FakeIrcTransport();
        var client = new TwitchClient(client: transport);
        client.Initialize(new ConnectionCredentials());
        var failures = new List<OnFailureToReceiveJoinConfirmationArgs>();
        client.OnFailureToReceiveJoinConfirmation += (_, e) =>
        {
            lock (failures)
            {
                failures.Add(e);
            }

            return Task.CompletedTask;
        };

        Assert.True(await client.ConnectAsync());
        await client.JoinChannelAsync(Channel);
        Assert.Equal(1, transport.JoinsSentFor(Channel));
        return (client, transport, failures);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(100);
        }
    }
}
