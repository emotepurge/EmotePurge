using EmotePurge.Worker;
using Microsoft.Extensions.Logging;
using TwitchLib.Client.Models;
using TwitchLib.Client.Parsing;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

/// <summary>
/// The prepared lines of acceptance criterion 4 (privacy of captured logs), each logged the way the
/// Worker logs it: TwitchLib's own <c>LoggerMessage</c> calls (event ids and templates pinned from the
/// decompiled TwitchLib.Client 4.0.1, as in <c>RedactingTwitchClientLoggerFactoryTests</c>) and the splice
/// sentinel's warning. Every identity, id and text in them is invented. None of <see cref="Forbidden"/>
/// may reach the log table or a console line.
/// </summary>
public static class RedactionChainFixtures
{
    public const string TwitchClientCategory = "TwitchLib.Client.TwitchClient";
    public const string ChatManagerCategory = "EmotePurge.Worker.TwitchChatManager";
    public const string ChatText = "this is private chat text";

    /// <summary>
    /// Raw TwitchLib parse failure: identifying tags and chat text.
    /// </summary>
    public const string IdentifyingLine =
        "@badge-info=;color=#FF0000;display-name=EvilName;user-id=444444444 "
        + ":someone!someone@someone.tmi.twitch.tv PRIVMSG #targetchannel_test :" + ChatText;

    /// <summary>A bare fragment after a spliced free-text tag: it sits where a tag key belongs.</summary>
    public const string FragmentLine =
        "@reply-parent-msg-body=hello;GEHEIMTEXT PRIVMSG #targetchannel_test :" + ChatText;

    /// <summary>
    /// A spliced line (the shape of <c>IrcLineSpliceRuleTwitchLibTests</c>, with the typed tags TwitchLib's
    /// <c>ChatMessage</c> needs): a second tag block embedded in the
    /// room id, a recipient display name, a system message naming a user, and the bare fragment on the top
    /// level of the tag block. It stands before any free-text value on purpose: an embedded free-text value
    /// is replaced whole and nothing after it is scanned, so a fragment there would never be withheld
    /// separately.
    /// </summary>
    public const string SpliceLine =
        "@badge-info=;badges=;color=#FF0000;display-name=TestUser1;id=cccc3333-3333-3333-3333-333333333333;mod=0;"
        + "room-id=111111111@display-name=EvilName@user-id=444444444;msg-param-recipient-display-name=SomeDisplayName;"
        + @"system-msg=Gift\sfrom\sSomeUser;GEHEIMTEXT;subscriber=0;tmi-sent-ts=1694000000000;turbo=0;user-id=333333333;user-type= "
        + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :" + ChatText;

    /// <summary>The brief's list, plus the splice line's own chatter (display name and user id).</summary>
    public static readonly IReadOnlyList<string> Forbidden =
        ["EvilName", "444444444", "SomeDisplayName", "SomeUser", "GEHEIMTEXT", ChatText, "TestUser1", "333333333"];

    // TwitchLib.Client 4.0.1, LogExtensions — not invented here.
    private static readonly EventId ParsingErrorEventId = new(932391387, "LogParsingError");
    private static readonly EventId UnaccountedForEventId = new(1006327295, "LogUnaccountedFor");

    private static readonly Action<ILogger, string, Exception?> LogParsingError =
        LoggerMessage.Define<string>(LogLevel.Error, ParsingErrorEventId, "Unexpected error during message parsing, message: {message}");

    private static readonly Action<ILogger, string, Exception?> LogUnaccountedFor =
        LoggerMessage.Define<string>(LogLevel.Warning, UnaccountedForEventId, "Unaccounted for: {ircString} (please create a TwitchLib GitHub issue :P)");

    public static readonly IReadOnlyDictionary<string, ChainFixture> All = new Dictionary<string, ChainFixture>(StringComparer.Ordinal)
    {
        ["a parsing error with chat text and display name"] =
            Redacted("LogParsingError", LogLevel.Error, logger => LogParsingError(logger, IdentifyingLine, null), withheldTagKeys: false, withException: false),
        ["b unaccounted line with a bare fragment"] =
            Redacted("LogUnaccountedFor", LogLevel.Warning, logger => LogUnaccountedFor(logger, FragmentLine, null), withheldTagKeys: true, withException: false),
        ["c parsing error with a bare fragment"] =
            Redacted("LogParsingError", LogLevel.Error, logger => LogParsingError(logger, FragmentLine, null), withheldTagKeys: true, withException: false),
        ["d-a parsing error with chat text and an exception carrying it"] =
            Redacted("LogParsingError", LogLevel.Error, logger => LogParsingError(logger, IdentifyingLine, RawTextException(IdentifyingLine)), withheldTagKeys: false, withException: true),
        ["d-b unaccounted line with a fragment and an exception carrying it"] =
            Redacted("LogUnaccountedFor", LogLevel.Warning, logger => LogUnaccountedFor(logger, FragmentLine, RawTextException(FragmentLine)), withheldTagKeys: true, withException: true),
        ["d-c parsing error with a fragment and an exception carrying it"] =
            Redacted("LogParsingError", LogLevel.Error, logger => LogParsingError(logger, FragmentLine, RawTextException(FragmentLine)), withheldTagKeys: true, withException: true),
        ["e bypassed wrapper, harmless message, exception carrying chat text"] = new(
            TwitchClientCategory,
            ThroughWrapper: false,
            LogLevel.Error,
            logger => LogParsingError(logger, "harmless", RawTextException(IdentifyingLine)),
            Shape.TypesAndStacksOnly,
            EventName: "LogParsingError",
            WithheldTagKeys: false,
            WithException: true,
            ConsoleChecked: false),
        ["f splice warning replayed through TwitchLib's parser"] = new(
            ChatManagerCategory,
            ThroughWrapper: true,
            LogLevel.Warning,
            LogSpliceWarning,
            Shape.SpliceWarning,
            EventName: null,
            WithheldTagKeys: false,
            WithException: false,
            ConsoleChecked: true),
    };

    public static IEnumerable<object[]> Names => All.Keys.Select(name => new object[] { name });

    /// <summary>An exception whose message and inner message carry the raw line, both thrown so they have stack traces.</summary>
    public static Exception RawTextException(string rawLine)
    {
        try
        {
            try
            {
                throw new FormatException("inner: " + rawLine);
            }
            catch (FormatException inner)
            {
                throw new InvalidOperationException("outer: " + rawLine, inner);
            }
        }
        catch (InvalidOperationException outer)
        {
            return outer;
        }
    }

    private static ChainFixture Redacted(string eventName, LogLevel level, Action<ILogger> log, bool withheldTagKeys, bool withException) =>
        new(TwitchClientCategory, ThroughWrapper: true, level, log, Shape.RedactedLine, eventName, withheldTagKeys, withException, ConsoleChecked: true);

    private static void LogSpliceWarning(ILogger logger)
    {
        var message = new ChatMessage("purgebot_test", IrcParser.ParseMessage(SpliceLine), new MessageEmoteCollection(), false, "prefix", "suffix");
        if (!IrcLineSpliceRule.IsSpliced(message.RawIrcMessage))
        {
            throw new InvalidOperationException("The splice fixture no longer looks spliced to the sentinel.");
        }

        var fields = IrcLineSpliceRule.ForSpliceWarning(message.Channel, message.RoomId, message.RawIrcMessage);
#pragma warning disable CA2254 // the template is the production constant, replayed verbatim
        logger.LogWarning(IrcLineSpliceRule.SpliceWarningTemplate, fields.Channel, fields.RoomId, fields.TagBlock);
#pragma warning restore CA2254
    }

    public enum Shape
    {
        /// <summary><c>TwitchLib.&lt;Event&gt; (redacted, #246): command=…, tagKeys=[…]</c>, no properties.</summary>
        RedactedLine,

        /// <summary>The wrapper was bypassed: only the stored exception is checked (types and stacks only).</summary>
        TypesAndStacksOnly,

        /// <summary>The splice sentinel's warning: keys only, the bare fragment as <c>&lt;withheld&gt;</c>.</summary>
        SpliceWarning,
    }
}

/// <summary>One prepared log call of <see cref="RedactionChainFixtures"/> and what its stored entry must look like.</summary>
public sealed record ChainFixture(
    string Category,
    bool ThroughWrapper,
    LogLevel Level,
    Action<ILogger> Log,
    RedactionChainFixtures.Shape Shape,
    string? EventName,
    bool WithheldTagKeys,
    bool WithException,
    bool ConsoleChecked);
