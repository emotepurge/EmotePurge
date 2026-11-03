using EmotePurge.Core.Matching;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Pure, dependency-free — no container needed. Exercises the exact rule shared by
// TwitchChatManager.OnMessageReceived, SevenTvSyncService.RefreshMatchCacheAsync and the
// chat-log backfill. Fixtures here are synthetic (invented names/ids/text), never a recorded IRC
// line — recorded chat does not belong in the repo (see DECISIONS.md for this file).
public class EmoteNameMatchingTests
{
    private static readonly Dictionary<string, string> OneEmote = new() { ["PogChamp"] = "emote-1" };

    // ---- MatchEmoteIds ----

    [Fact]
    public void MatchEmoteIds_EmptyText_ReturnsEmpty()
    {
        var result = EmoteNameMatching.MatchEmoteIds(string.Empty, OneEmote);

        Assert.Empty(result);
    }

    [Fact]
    public void MatchEmoteIds_OnlyWhitespace_ReturnsEmpty()
    {
        var result = EmoteNameMatching.MatchEmoteIds("   ", OneEmote);

        Assert.Empty(result);
    }

    [Fact]
    public void MatchEmoteIds_EmoteAtStart_Matches()
    {
        var result = EmoteNameMatching.MatchEmoteIds("PogChamp hello there", OneEmote);

        Assert.Equal(["emote-1"], result);
    }

    [Fact]
    public void MatchEmoteIds_EmoteInMiddle_Matches()
    {
        var result = EmoteNameMatching.MatchEmoteIds("hello PogChamp there", OneEmote);

        Assert.Equal(["emote-1"], result);
    }

    [Fact]
    public void MatchEmoteIds_EmoteAtEnd_Matches()
    {
        var result = EmoteNameMatching.MatchEmoteIds("hello there PogChamp", OneEmote);

        Assert.Equal(["emote-1"], result);
    }

    [Fact]
    public void MatchEmoteIds_SameEmoteThreeTimes_ReturnsExactlyOneId()
    {
        var result = EmoteNameMatching.MatchEmoteIds("PogChamp PogChamp PogChamp", OneEmote);

        Assert.Equal(["emote-1"], result);
    }

    [Fact]
    public void MatchEmoteIds_TwoNamesOnSameId_ReturnsOneId()
    {
        var nameToId = new Dictionary<string, string> { ["PogChamp"] = "emote-1", ["PogChampAlias"] = "emote-1" };

        var result = EmoteNameMatching.MatchEmoteIds("PogChamp PogChampAlias", nameToId);

        Assert.Equal(["emote-1"], result);
    }

    [Fact]
    public void MatchEmoteIds_DoubleSpaceBetweenTokens_SurroundingTokenStillMatches()
    {
        var result = EmoteNameMatching.MatchEmoteIds("hello  PogChamp", OneEmote);

        Assert.Equal(["emote-1"], result);
    }

    [Fact]
    public void MatchEmoteIds_DoubleSpaceProducesEmptyToken_MatchesIfMapHasAnEmptyKey()
    {
        // Honest consequence of "no RemoveEmptyEntries": a double space produces an empty token
        // like any other, and it matches if the map happens to have an entry for it. Nothing in
        // this codebase creates an empty-named emote today, but the rule itself does not special-
        // case it — this documents that instead of asserting a guarantee that does not exist.
        var nameToId = new Dictionary<string, string> { [string.Empty] = "emote-empty" };

        var result = EmoteNameMatching.MatchEmoteIds("hello  world", nameToId);

        Assert.Equal(["emote-empty"], result);
    }

    [Fact]
    public void MatchEmoteIds_Tab_DoesNotSplitToken()
    {
        var nameToId = new Dictionary<string, string> { ["PogChamp\tKappa"] = "emote-glued" };

        var result = EmoteNameMatching.MatchEmoteIds("PogChamp\tKappa", nameToId);

        Assert.Equal(["emote-glued"], result);
    }

    [Fact]
    public void MatchEmoteIds_TabAlone_DoesNotMatchSeparateNames()
    {
        var nameToId = new Dictionary<string, string> { ["PogChamp"] = "emote-1", ["Kappa"] = "emote-2" };

        var result = EmoteNameMatching.MatchEmoteIds("PogChamp\tKappa", nameToId);

        Assert.Empty(result);
    }

    [Fact]
    public void MatchEmoteIds_NonBreakingSpace_DoesNotSplitToken()
    {
        var nameToId = new Dictionary<string, string> { ["PogChamp Kappa"] = "emote-glued" };

        var result = EmoteNameMatching.MatchEmoteIds("PogChamp Kappa", nameToId);

        Assert.Equal(["emote-glued"], result);
    }

    [Fact]
    public void MatchEmoteIds_IsCaseSensitive_Ordinal()
    {
        var result = EmoteNameMatching.MatchEmoteIds("pogchamp", OneEmote);

        Assert.Empty(result);
    }

    [Fact]
    public void MatchEmoteIds_UnicodeEmojiName_Matches()
    {
        var nameToId = new Dictionary<string, string> { ["🐸Kappa"] = "emote-emoji" };

        var result = EmoteNameMatching.MatchEmoteIds("hello 🐸Kappa there", nameToId);

        Assert.Equal(["emote-emoji"], result);
    }

    [Fact]
    public void MatchEmoteIds_ActionInnerText_Matches()
    {
        // The caller (TwitchLib) unwraps /me ACTION framing before this function ever sees the
        // text — it only ever sees the inner message.
        var result = EmoteNameMatching.MatchEmoteIds("waves at PogChamp", OneEmote);

        Assert.Equal(["emote-1"], result);
    }

    [Fact]
    public void MatchEmoteIds_EmptyMap_ReturnsSharedEmptyInstanceWithoutAllocating()
    {
        var empty = new Dictionary<string, string>();

        var first = EmoteNameMatching.MatchEmoteIds("PogChamp", empty);
        var second = EmoteNameMatching.MatchEmoteIds("something else entirely", empty);

        Assert.Same(first, second);
    }

    [Fact]
    public void MatchEmoteIds_EmptyText_ReturnsSharedEmptyInstanceAsEmptyMap()
    {
        var first = EmoteNameMatching.MatchEmoteIds(string.Empty, OneEmote);
        var second = EmoteNameMatching.MatchEmoteIds(string.Empty, new Dictionary<string, string>());

        Assert.Same(first, second);
    }

    // ---- MatchEmoteIds: the buffer-taking overload agrees with the allocating one ----

    [Fact]
    public void MatchEmoteIds_BufferOverload_AgreesWithAllocatingOverload_WhenThereAreMatches()
    {
        var nameToId = new Dictionary<string, string> { ["PogChamp"] = "emote-1", ["Kappa"] = "emote-2" };
        const string message = "PogChamp said Kappa PogChamp";

        var expected = EmoteNameMatching.MatchEmoteIds(message, nameToId);
        var actual = new HashSet<string>();
        EmoteNameMatching.MatchEmoteIds(message, nameToId, actual);

        // Set comparison, not sequence comparison: HashSet<T> enumeration order is unspecified,
        // and both overloads are only contracted to agree on membership, not on order.
        Assert.True(actual.SetEquals(expected));
    }

    [Fact]
    public void MatchEmoteIds_BufferOverload_AgreesWithAllocatingOverload_WhenThereAreNoMatches()
    {
        const string message = "no emotes in this message at all";

        var expected = EmoteNameMatching.MatchEmoteIds(message, OneEmote);
        var actual = new HashSet<string>();
        EmoteNameMatching.MatchEmoteIds(message, OneEmote, actual);

        Assert.True(actual.SetEquals(expected));
    }

    [Fact]
    public void MatchEmoteIds_BufferOverload_OnlyAdds_DoesNotClearExistingEntries()
    {
        var actual = new HashSet<string> { "emote-preexisting" };

        EmoteNameMatching.MatchEmoteIds("PogChamp", OneEmote, actual);

        Assert.Equal(2, actual.Count);
        Assert.Contains("emote-preexisting", actual);
        Assert.Contains("emote-1", actual);
    }

    // ---- Coalesce ----

    [Fact]
    public void Coalesce_UniqueNames_ReportsNoAmbiguity()
    {
        var result = EmoteNameMatching.Coalesce(
        [
            new("PogChamp", "emote-1"),
            new("Kappa", "emote-2"),
        ]);

        Assert.Equal(2, result.NameToId.Count);
        Assert.Empty(result.AmbiguousNames);
    }

    [Fact]
    public void Coalesce_DuplicateName_FirstWins_AndNameIsAmbiguous()
    {
        var result = EmoteNameMatching.Coalesce(
        [
            new("PogChamp", "emote-first"),
            new("PogChamp", "emote-second"),
        ]);

        Assert.Equal("emote-first", result.NameToId["PogChamp"]);
        Assert.Equal(["PogChamp"], result.AmbiguousNames);
    }

    [Fact]
    public void Coalesce_NameRepeatedThreeTimes_AppearsOnceInAmbiguousNames()
    {
        var result = EmoteNameMatching.Coalesce(
        [
            new("PogChamp", "emote-first"),
            new("PogChamp", "emote-second"),
            new("PogChamp", "emote-third"),
        ]);

        Assert.Equal(["PogChamp"], result.AmbiguousNames);
    }

    [Fact]
    public void Coalesce_EmptyInput_ReturnsEmptyMap()
    {
        var result = EmoteNameMatching.Coalesce([]);

        Assert.Empty(result.NameToId);
        Assert.Empty(result.AmbiguousNames);
    }
}
