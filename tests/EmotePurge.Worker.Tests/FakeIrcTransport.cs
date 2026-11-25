using System.Collections.Concurrent;
using TwitchLib.Communication.Events;
using TwitchLib.Communication.Interfaces;
using TwitchLib.Communication.Models;

namespace EmotePurge.Worker.Tests;

// A scripted IRC transport: TwitchLib's real client, parser and join queue run on top of it, so a test
// feeds a line through exactly the receive path production uses (OnMessage -> OnSendReceiveData ->
// parse -> handler). Opening it "connects" and, like Twitch, answers with the 004 welcome line; nothing
// else is answered unless the test says so.
internal sealed class FakeIrcTransport : IClient
{
    private readonly ConcurrentQueue<string> _sent = new();

    public bool IsConnected { get; private set; }

    public IClientOptions Options { get; } = new ClientOptions();

    // Welcome sent on open; set false to hold the handshake back.
    public bool AnswerOpenWithWelcome { get; set; } = true;

    public IReadOnlyCollection<string> Sent => _sent;

    public event AsyncEventHandler<OnConnectedEventArgs>? OnConnected;

    public event AsyncEventHandler<OnDisconnectedEventArgs>? OnDisconnected;

    public event AsyncEventHandler<OnErrorEventArgs>? OnError;

    public event AsyncEventHandler<OnFatalErrorEventArgs>? OnFatality;

    public event AsyncEventHandler<OnMessageEventArgs>? OnMessage;

    public event AsyncEventHandler<OnSendFailedEventArgs>? OnSendFailed;

    public event AsyncEventHandler<OnConnectedEventArgs>? OnReconnected;

    public int JoinsSentFor(string channel) =>
        _sent.Count(line => string.Equals(line, $"JOIN #{channel}", StringComparison.OrdinalIgnoreCase));

    public async Task<bool> OpenAsync()
    {
        IsConnected = true;
        if (OnConnected is { } connected)
        {
            await connected(this, new OnConnectedEventArgs());
        }

        if (AnswerOpenWithWelcome)
        {
            await ReceiveAsync(":tmi.twitch.tv 004 justinfan1 :-");
        }

        return true;
    }

    public Task<bool> ReconnectAsync() => OpenAsync();

    public Task CloseAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<bool> SendAsync(string message)
    {
        _sent.Enqueue(message);
        return Task.FromResult(true);
    }

    // One line (or several, CRLF separated) arriving from "Twitch".
    public Task ReceiveAsync(string line) =>
        OnMessage is { } handler ? handler(this, new OnMessageEventArgs(line)) : Task.CompletedTask;

    // The socket dropping: what TwitchLib turns into OnDisconnected.
    public async Task DropAsync()
    {
        IsConnected = false;
        if (OnDisconnected is { } handler)
        {
            await handler(this, new OnDisconnectedEventArgs());
        }
    }

    public void Dispose()
    {
        _ = OnError;
        _ = OnFatality;
        _ = OnSendFailed;
        _ = OnReconnected;
    }
}
