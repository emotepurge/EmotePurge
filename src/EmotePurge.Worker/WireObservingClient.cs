using TwitchLib.Communication.Events;
using TwitchLib.Communication.Interfaces;

namespace EmotePurge.Worker;

/// <summary>
/// Pass-through <see cref="IClient"/> that reports every outgoing <c>JOIN #channel</c> and
/// <c>PART #channel</c> to a hook before forwarding the line unchanged (plan #516, E2 "attempt on the
/// wire").
/// <para>
/// The seam sits at the transport on purpose. TwitchLib sends its JOINs straight through
/// <c>IClient.SendAsync</c> (<c>OnSendReceiveData</c> never sees them), and it silently drops a second
/// JOIN for a channel it already lists as joined — so only the line that actually leaves the process
/// is a real join attempt, which is why <c>TwitchChatManager.TryJoinAsync</c> reports nothing itself.
/// </para>
/// <para>
/// The hook runs inline in TwitchLib's send path (our own join loop, or TwitchLib's read loop when a
/// suspended notice advances its queue). It must therefore never block on anything a caller of
/// TwitchLib could be holding, and it must never throw: a hook exception is swallowed here so the
/// line is always forwarded.
/// </para>
/// </summary>
internal sealed class WireObservingClient(IClient inner) : IClient
{
    private volatile Action<OutgoingChannelCommand, string>? _hook;

    public bool IsConnected => inner.IsConnected;

    public IClientOptions Options => inner.Options;

    public event AsyncEventHandler<OnConnectedEventArgs>? OnConnected
    {
        add => inner.OnConnected += value;
        remove => inner.OnConnected -= value;
    }

    public event AsyncEventHandler<OnDisconnectedEventArgs>? OnDisconnected
    {
        add => inner.OnDisconnected += value;
        remove => inner.OnDisconnected -= value;
    }

    public event AsyncEventHandler<OnErrorEventArgs>? OnError
    {
        add => inner.OnError += value;
        remove => inner.OnError -= value;
    }

    public event AsyncEventHandler<OnFatalErrorEventArgs>? OnFatality
    {
        add => inner.OnFatality += value;
        remove => inner.OnFatality -= value;
    }

    public event AsyncEventHandler<OnMessageEventArgs>? OnMessage
    {
        add => inner.OnMessage += value;
        remove => inner.OnMessage -= value;
    }

    public event AsyncEventHandler<OnSendFailedEventArgs>? OnSendFailed
    {
        add => inner.OnSendFailed += value;
        remove => inner.OnSendFailed -= value;
    }

    public event AsyncEventHandler<OnConnectedEventArgs>? OnReconnected
    {
        add => inner.OnReconnected += value;
        remove => inner.OnReconnected -= value;
    }

    /// <summary>
    /// Receives (command, lower-case channel without <c>#</c>) for each outgoing JOIN/PART line;
    /// <c>null</c> detaches. Set and cleared by the manager under its client-state gate.
    /// </summary>
    public Action<OutgoingChannelCommand, string>? Hook
    {
        get => _hook;
        set => _hook = value;
    }

    public Task<bool> OpenAsync() => inner.OpenAsync();

    public Task<bool> ReconnectAsync() => inner.ReconnectAsync();

    public Task CloseAsync() => inner.CloseAsync();

    public Task<bool> SendAsync(string message)
    {
        if (TryParseChannelCommand(message, out var command, out var channel))
        {
            try
            {
                _hook?.Invoke(command, channel);
            }
            catch
            {
                // The line must go out whatever the observer does.
            }
        }

        return inner.SendAsync(message);
    }

    public void Dispose() => inner.Dispose();

    /// <summary>
    /// Recognises <c>JOIN #channel</c> and <c>PART #channel</c> at the very start of the line (the only
    /// shapes TwitchLib emits). Only the first channel of a comma list is reported; the manager never
    /// sends lists. Never throws.
    /// </summary>
    internal static bool TryParseChannelCommand(string? line, out OutgoingChannelCommand command, out string channel)
    {
        command = default;
        channel = string.Empty;
        if (line is null)
        {
            return false;
        }

        OutgoingChannelCommand parsed;
        ReadOnlySpan<char> rest;
        if (line.StartsWith("JOIN #", StringComparison.Ordinal))
        {
            parsed = OutgoingChannelCommand.Join;
            rest = line.AsSpan("JOIN #".Length);
        }
        else if (line.StartsWith("PART #", StringComparison.Ordinal))
        {
            parsed = OutgoingChannelCommand.Part;
            rest = line.AsSpan("PART #".Length);
        }
        else
        {
            return false;
        }

        var end = rest.IndexOfAny(" ,\r\n");
        if (end >= 0)
        {
            rest = rest[..end];
        }

        if (rest.IsEmpty)
        {
            return false;
        }

        command = parsed;
        channel = rest.ToString().ToLowerInvariant();
        return true;
    }
}

/// <summary>The two outgoing commands <see cref="WireObservingClient"/> reports.</summary>
internal enum OutgoingChannelCommand
{
    Join,
    Part
}
