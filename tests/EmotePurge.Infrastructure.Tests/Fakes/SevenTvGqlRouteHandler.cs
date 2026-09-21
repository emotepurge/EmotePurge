using System.Net;
using System.Text;

namespace EmotePurge.Infrastructure.Tests.Fakes;

/// <summary>
/// Answers 7TV GraphQL requests by what they ask for, and counts them per kind — so a test can say
/// "two grant requests and no owner request" instead of only "two requests". The kind is read from
/// the query text: <c>user_by_connection</c> (identity), <c>editor_of</c> (grants), <c>emote_set</c>
/// (the v3 owner query). Anything a test did not configure answers HTTP 503.
/// </summary>
public sealed class SevenTvGqlRouteHandler : HttpMessageHandler
{
    public const string Identity = "user_by_connection";
    public const string EditorOf = "editor_of";
    public const string Owner = "emote_set: emoteSet";

    private readonly Dictionary<string, Func<HttpResponseMessage>> _answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _requests;

    /// <summary>Optional gate every request waits on before it is answered.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int Requests => Volatile.Read(ref _requests);

    public int CountOf(string kind)
    {
        lock (_gate)
        {
            return _counts.GetValueOrDefault(kind);
        }
    }

    public SevenTvGqlRouteHandler Answer(string kind, HttpStatusCode statusCode, string body = "")
    {
        _answers[kind] = () => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return this;
    }

    public SevenTvGqlRouteHandler Answer(string kind, Func<HttpResponseMessage> answer)
    {
        _answers[kind] = answer;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var kind = new[] { Identity, EditorOf, Owner }.FirstOrDefault(body.Contains) ?? "other";
        lock (_gate)
        {
            _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return _answers.TryGetValue(kind, out var answer)
            ? answer()
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(string.Empty) };
    }
}
