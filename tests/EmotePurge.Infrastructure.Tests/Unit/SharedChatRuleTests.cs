using EmotePurge.Core.Chat;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Pure, dependency-free — no container needed. Exercises the exact rule shared by
// TwitchChatManager.OnMessageReceived (via FromTags, Task 3) and the chat-log backfill's
// ChatLogBackfillBlockCounter.Count (via Classify), extracted rather than rewritten (DECISIONS.md, "Shared Chat", 2026-09-06). TwitchLib-bound coverage of FromTags
// against a real, library-parsed ChatMessage lives in SharedChatRuleTwitchLibTests
// (EmotePurge.Worker.Tests) instead — this class never touches TwitchLib.
public class SharedChatRuleTests
{
    private const string RoomId = "111111111";
    private const string OtherRoomId = "222222222";

    // ---- Classify ----

    [Fact]
    public void Classify_NoSourceRoomIdAndNoOtherMarkers_ReturnsOwn()
    {
        var result = SharedChatRule.Classify(RoomId, sourceRoomId: null, hasOtherSourceMarkers: false);

        Assert.Equal(MessageOrigin.Own, result);
    }

    [Fact]
    public void Classify_SourceRoomIdEqualsRoomId_ReturnsOwn()
    {
        var result = SharedChatRule.Classify(RoomId, sourceRoomId: RoomId, hasOtherSourceMarkers: true);

        Assert.Equal(MessageOrigin.Own, result);
    }

    [Fact]
    public void Classify_SourceRoomIdDiffersFromRoomId_ReturnsForeign()
    {
        var result = SharedChatRule.Classify(RoomId, sourceRoomId: OtherRoomId, hasOtherSourceMarkers: true);

        Assert.Equal(MessageOrigin.Foreign, result);
    }

    [Fact]
    public void Classify_EmptySourceRoomIdWithoutOtherMarkers_ReturnsOwn()
    {
        var result = SharedChatRule.Classify(RoomId, sourceRoomId: string.Empty, hasOtherSourceMarkers: false);

        Assert.Equal(MessageOrigin.Own, result);
    }

    [Fact]
    public void Classify_EmptySourceRoomIdWithOtherMarkers_ReturnsIndeterminate()
    {
        var result = SharedChatRule.Classify(RoomId, sourceRoomId: string.Empty, hasOtherSourceMarkers: true);

        Assert.Equal(MessageOrigin.Indeterminate, result);
    }

    [Fact]
    public void Classify_RoomIdMissingWithSourceRoomIdSet_ReturnsForeign()
    {
        // Contract, not an artifact of string.Equals' null handling: a comparison against nothing
        // is never "equal", so a session mirrored into a room this side never saw its own id for
        // still counts as foreign rather than as some third, undecided thing.
        var result = SharedChatRule.Classify(roomId: null, sourceRoomId: OtherRoomId, hasOtherSourceMarkers: true);

        Assert.Equal(MessageOrigin.Foreign, result);
    }

    // ---- HasOtherSourceMarkers ----

    [Theory]
    [InlineData(SharedChatRule.SourceIdTag)]
    [InlineData(SharedChatRule.SourceBadgesTag)]
    [InlineData(SharedChatRule.SourceBadgeInfoTag)]
    public void HasOtherSourceMarkers_EachMarkerAlonePresent_ReturnsTrue(string tagKey)
    {
        // Presence only — an empty value still counts as present.
        var tags = new Dictionary<string, string> { [tagKey] = string.Empty };

        var result = SharedChatRule.HasOtherSourceMarkers(tags);

        Assert.True(result);
    }

    [Fact]
    public void HasOtherSourceMarkers_NullDictionary_ReturnsFalse()
    {
        var result = SharedChatRule.HasOtherSourceMarkers(null);

        Assert.False(result);
    }

    // ---- FromTags ----

    [Fact]
    public void FromTags_NullDictionary_ReturnsOwn_NoException()
    {
        var result = SharedChatRule.FromTags(RoomId, tags: null);

        Assert.Equal(MessageOrigin.Own, result);
    }

    [Fact]
    public void FromTags_DictionaryWithoutSourceRoomIdOrMarkers_ReturnsOwn()
    {
        var tags = new Dictionary<string, string> { ["client-nonce"] = "abc" };

        var result = SharedChatRule.FromTags(RoomId, tags);

        Assert.Equal(MessageOrigin.Own, result);
    }

    [Fact]
    public void FromTags_DictionaryWithDifferentSourceRoomId_ReturnsForeign()
    {
        var tags = new Dictionary<string, string> { [SharedChatRule.SourceRoomIdTag] = OtherRoomId };

        var result = SharedChatRule.FromTags(RoomId, tags);

        Assert.Equal(MessageOrigin.Foreign, result);
    }

    [Fact]
    public void FromTags_DictionaryWithOnlyAnOtherMarker_ReturnsIndeterminate()
    {
        var tags = new Dictionary<string, string> { [SharedChatRule.SourceIdTag] = "some-id" };

        var result = SharedChatRule.FromTags(RoomId, tags);

        Assert.Equal(MessageOrigin.Indeterminate, result);
    }
}
