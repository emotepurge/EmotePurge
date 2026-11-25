using System.Net;
using System.Text;

namespace EmotePurge.Infrastructure.Tests.Fakes;

/// <summary>
/// Answers requests from a script, one step per request in order, and records what was sent: method, URI, the
/// <c>Authorization</c> header and the body. Once the script is used up every request answers HTTP 503, so a client that
/// sends more than a test expected sees a failure rather than a reused answer.
/// </summary>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _steps = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Answers with <paramref name="statusCode"/> and a JSON body; <paramref name="configure"/> may add headers.</summary>
    public ScriptedHttpHandler Then(HttpStatusCode statusCode, string body, Action<HttpResponseMessage>? configure = null)
    {
        _steps.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            configure?.Invoke(response);
            return Task.FromResult(response);
        });
        return this;
    }

    /// <summary>HTTP 200 with <paramref name="body"/>.</summary>
    public ScriptedHttpHandler ThenOk(string body) => Then(HttpStatusCode.OK, body);

    public ScriptedHttpHandler ThenThrow(Exception exception)
    {
        _steps.Enqueue(_ => Task.FromException<HttpResponseMessage>(exception));
        return this;
    }

    /// <summary>HTTP 200 with <paramref name="body"/> after <paramref name="delay"/>, unless the request is cancelled first.</summary>
    public ScriptedHttpHandler ThenOkAfter(TimeSpan delay, string body)
    {
        _steps.Enqueue(async cancellationToken =>
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        return this;
    }

    /// <summary>Never answers; the request ends only when it is cancelled (by the client's time limit or the caller).</summary>
    public ScriptedHttpHandler ThenHang()
    {
        _steps.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Func<CancellationToken, Task<HttpResponseMessage>>? step;
        lock (_gate)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), body));
            _steps.TryDequeue(out step);
        }

        return step is null
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(string.Empty) }
            : await step(cancellationToken);
    }

    public sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, string? Authorization, string Body);
}
