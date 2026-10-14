using System.Text;

namespace EmotePurge.Core.Logging;

/// <summary>One link of the withheld chain: the type name and stack trace of an original exception.</summary>
public sealed record WithheldExceptionLink(string TypeName, string? StackTrace);

/// <summary>
/// Sanitized stand-in for an exception whose message (or <c>Data</c>, or <c>ToString()</c>) may carry
/// raw chat content or identifiers, e.g. the exceptions TwitchLib hands to its parse-failure log call.
/// It keeps what is needed to diagnose a defect (type names and stack traces of the whole chain,
/// outer first) and never reads <c>Message</c>, <c>Data</c> or <c>ToString()</c> of any original.
/// <see cref="Exception.InnerException"/> is always <c>null</c>, so a sink walking the inner chain
/// cannot reach an original. Lives in Core so a log sink can recognise it and read the original type.
/// <para>
/// Chain walk: <see cref="Exception.InnerException"/>, or every <see cref="AggregateException.InnerExceptions"/>
/// entry for an aggregate, depth first in pre-order. The outer exception is level 1; nothing deeper
/// than <see cref="MaxDepth"/> levels is listed, and the limit applies per branch, so every branch of
/// an aggregate may reach level 8.
/// </para>
/// </summary>
public sealed class WithheldMessageException : Exception
{
    public const int MaxDepth = 8;

    private WithheldMessageException(string originalTypeName, IReadOnlyList<WithheldExceptionLink> chain)
        : base($"{originalTypeName}: message withheld (#246)")
    {
        OriginalTypeName = originalTypeName;
        Chain = chain;
    }

    public string OriginalTypeName { get; }

    public IReadOnlyList<WithheldExceptionLink> Chain { get; }

    public static WithheldMessageException From(Exception original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var links = new List<WithheldExceptionLink>();
        Collect(original, 1, links);
        return new WithheldMessageException(TypeNameOf(original), links);
    }

    public override string ToString()
    {
        var builder = new StringBuilder(Message);
        foreach (var link in Chain)
        {
            builder.AppendLine().Append("--- ").Append(link.TypeName);
            if (link.StackTrace is not null)
            {
                builder.AppendLine().Append(link.StackTrace);
            }
        }

        return builder.ToString();
    }

    private static void Collect(Exception exception, int depth, List<WithheldExceptionLink> links)
    {
        links.Add(new WithheldExceptionLink(TypeNameOf(exception), exception.StackTrace));
        if (depth >= MaxDepth)
        {
            return;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                Collect(inner, depth + 1, links);
            }
        }
        else if (exception.InnerException is { } next)
        {
            Collect(next, depth + 1, links);
        }
    }

    private static string TypeNameOf(Exception exception) => exception.GetType().FullName ?? exception.GetType().Name;
}
