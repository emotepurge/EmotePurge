using System.Text.Json;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free: pure JSON-to-delta mapping. The fixture files under Unit/TestData/ are real
// EventAPI frames captured live on 2026-07-30 against channel "sensitron" (add/remove/rename of
// catJAM/Aware/rain and an active-set switch) — see the csproj comment on why hand-written
// fixtures are banned here. The "two-slots" pulled fixture is derived from the real pulled frame by
// duplicating its item under a second alias (index and name changed, nothing else), the shape 7TV
// sends when a multi-alias emote leaves; it is replaced by a captured frame once a live run has one.
public class SevenTvDispatchParserTests
{
    private static JsonElement LoadBody(string fixtureName)
    {
        var json = File.ReadAllText(Path.Combine("Unit", "TestData", fixtureName));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("d").GetProperty("body").Clone();
    }

    private static JsonElement ParseBody(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ParseEmoteSetUpdate_RealPushedFrame_MapsEmoteWithImageUrl()
    {
        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(
            LoadBody("dispatch-emote-set-update-pushed.json"), NullLogger.Instance);

        var emote = Assert.Single(delta.Pushed);
        Assert.Equal("01FFWH9WV80000JT8GHDKHJNZC", emote.Id);
        Assert.Equal("Aware", emote.Name);
        // 4x and the file's own static_name, both: the atlas draws a 64 px cell, so 4x (128 px) is
        // what stays sharp at a device pixel ratio of 2 — and "Aware" is animated, where the plain
        // 4x.webp carries every frame. Measured 2026-08-28 over HandOfBlood's 931 emotes: the
        // animated ones average 133 KB at 2x and reach 1.2 MB, which is 73 MB for one set. The
        // static variant brings the same set to 15 MB. The animation is not lost, it moves to the
        // sidecar, which shows one emote at a time (see the frontend's animatedEmoteUrl).
        Assert.Equal(
            "https://cdn.7tv.app/emote/01FFWH9WV80000JT8GHDKHJNZC/4x_static.webp", emote.ImageUrl);
        Assert.Empty(delta.Updated);
        Assert.Empty(delta.PulledIds);
    }

    [Fact]
    public void ParseEmoteSetUpdate_RealPulledFrame_ExtractsIdFromOldValue()
    {
        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(
            LoadBody("dispatch-emote-set-update-pulled.json"), NullLogger.Instance);

        Assert.Equal(["01F6MQ33FG000FFJ97ZB8MWV52"], delta.PulledIds);
        Assert.Equal(new SevenTvPulledEntry("01F6MQ33FG000FFJ97ZB8MWV52", "catJAM"), Assert.Single(delta.Pulled));
        Assert.Empty(delta.Pushed);
        Assert.Empty(delta.Updated);
    }

    [Fact]
    public void ParseEmoteSetUpdate_RealRenameFrame_MapsNewName()
    {
        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(
            LoadBody("dispatch-emote-set-update-renamed.json"), NullLogger.Instance);

        var rename = Assert.Single(delta.Updated);
        Assert.Equal("01EZPGBXMR0001DCZS00AD662R", rename.Id);
        Assert.Equal("FeelsRainMan", rename.OldName);
        Assert.Equal("rain", rename.NewName);
        Assert.Equal(
            "https://cdn.7tv.app/emote/01EZPGBXMR0001DCZS00AD662R/4x_static.webp", rename.ImageUrl);
    }

    [Fact]
    public void ParseEmoteSetUpdate_AddedRemovedFieldNames_YieldEmptyDelta()
    {
        // Regression guard for the bug that sank the 2026-07 implementation: its parser read
        // added/removed — field names that do not exist in real dispatches, which use
        // pushed/pulled. A frame in that imaginary shape must parse to an empty delta, and the
        // real frames above prove the actual names are covered.
        var body = ParseBody("""
            {"id":"01SET0","kind":3,
             "added":[{"key":"emotes","index":0,"type":"object","value":{"id":"01E1","name":"ghost"}}],
             "removed":[{"key":"emotes","index":1,"type":"object","old_value":{"id":"01E2","name":"gone"}}]}
            """);

        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(body, NullLogger.Instance);

        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void ParseEmoteSetUpdate_SetLevelMetadataChange_IsIgnored()
    {
        var body = ParseBody("""
            {"id":"01SET0","kind":3,
             "updated":[{"key":"name","index":null,"type":"string","old_value":"Old Set Name","value":"New Set Name"}]}
            """);

        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(body, NullLogger.Instance);

        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void ParseEmoteSetUpdate_MultipleChangesInOneFrame_FillsAllThreeLists()
    {
        var body = ParseBody("""
            {"id":"01SET0","kind":3,
             "pushed":[{"key":"emotes","index":0,"type":"object","value":{"id":"01E1","name":"new1"}},
                       {"key":"emotes","index":1,"type":"object","value":{"id":"01E2","name":"new2"}}],
             "updated":[{"key":"emotes","index":2,"type":"object","old_value":{"id":"01E3","name":"old"},"value":{"id":"01E3","name":"renamed"}}],
             "pulled":[{"key":"emotes","index":3,"type":"object","old_value":{"id":"01E4","name":"gone"}}]}
            """);

        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(body, NullLogger.Instance);

        Assert.Equal(2, delta.Pushed.Count);
        var rename = Assert.Single(delta.Updated);
        Assert.Equal(("01E3", "old", "renamed"), (rename.Id, rename.OldName, rename.NewName));
        Assert.Equal(["01E4"], delta.PulledIds);
        Assert.Equal(new SevenTvPulledEntry("01E4", "gone"), Assert.Single(delta.Pulled));
    }

    [Theory]
    [InlineData("""{"id":"01SET0"}""")]
    [InlineData("""{"id":"01SET0","pushed":[]}""")]
    [InlineData("""{"id":"01SET0","pushed":[{"key":"emotes","value":null}]}""")]
    [InlineData("""{"id":"01SET0","pushed":[{"key":"emotes","value":"not-an-object"}]}""")]
    [InlineData("""{"id":"01SET0","pushed":[{"index":0,"value":{"id":"01E1","name":"x"}}]}""")]
    [InlineData("""{"id":"01SET0","pulled":[{"key":"emotes","old_value":{}}]}""")]
    [InlineData("""{"id":"01SET0","pulled":"not-an-array"}""")]
    public void ParseEmoteSetUpdate_MalformedShapes_YieldEmptyDeltaWithoutThrowing(string bodyJson)
    {
        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(ParseBody(bodyJson), NullLogger.Instance);

        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void ParseEmoteSetUpdate_PushedWithoutHostBlock_MapsEmptyImageUrl()
    {
        // Documents the precondition for the ImageUrl-preserve guard in SevenTvSyncService:
        // a value without data.host maps to an empty ImageUrl, which must then never overwrite
        // a known URL. (Real captured frames do carry data.host — this covers the unproven case.)
        var body = ParseBody("""
            {"id":"01SET0","pushed":[{"key":"emotes","index":0,"type":"object","value":{"id":"01E1","name":"nohost"}}]}
            """);

        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(body, NullLogger.Instance);

        Assert.Equal(string.Empty, Assert.Single(delta.Pushed).ImageUrl);
    }

    [Fact]
    public void ParseEmoteSetUpdate_PulledFrameWithTwoItemsOfOneId_KeepsBothEntriesAndOneDistinctId()
    {
        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(
            LoadBody("dispatch-emote-set-update-pulled-two-slots.json"), NullLogger.Instance);

        Assert.Equal(
            [
                new SevenTvPulledEntry("01F6MQ33FG000FFJ97ZB8MWV52", "catJAM"),
                new SevenTvPulledEntry("01F6MQ33FG000FFJ97ZB8MWV52", "catJAM2"),
            ],
            delta.Pulled);
        Assert.Equal(["01F6MQ33FG000FFJ97ZB8MWV52"], delta.PulledIds);
    }

    [Fact]
    public void ParseEmoteSetUpdate_PulledIds_AreOrdinalDistinctInOrderOfFirstAppearance()
    {
        var body = ParseBody("""
            {"id":"01SET0","kind":3,
             "pulled":[{"key":"emotes","old_value":{"id":"01b","name":"x"}},
                       {"key":"emotes","old_value":{"id":"01A","name":"y"}},
                       {"key":"emotes","old_value":{"id":"01b","name":"z"}},
                       {"key":"emotes","old_value":{"id":"01a","name":"w"}}]}
            """);

        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(body, NullLogger.Instance);

        Assert.Equal(["01b", "01A", "01a"], delta.PulledIds);
        Assert.Equal(4, delta.Pulled.Count);
    }

    [Fact]
    public void ParseEmoteSetUpdate_MissingNames_AreNullAndNeverThrow()
    {
        var body = ParseBody("""
            {"id":"01SET0","kind":3,
             "updated":[{"key":"emotes","old_value":{"id":"01E1"},"value":{"id":"01E1","name":"new"}},
                        {"key":"emotes","value":{"id":"01E2","name":"new2"}},
                        {"key":"emotes","old_value":{"id":"01E3","name":"old3"},"value":{"id":"01E3"}},
                        {"key":"emotes","old_value":{"id":"01E4","name":""},"value":{"id":"01E4","name":""}}],
             "pulled":[{"key":"emotes","old_value":{"id":"01E5"}},
                       {"key":"emotes","old_value":{"id":"01E6","name":""}}]}
            """);

        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(body, NullLogger.Instance);

        Assert.Equal(
            [
                new SevenTvEmoteRename("01E1", null, "new", string.Empty),
                new SevenTvEmoteRename("01E2", null, "new2", string.Empty),
                new SevenTvEmoteRename("01E3", "old3", null, string.Empty),
                new SevenTvEmoteRename("01E4", null, null, string.Empty),
            ],
            delta.Updated);
        Assert.Equal([new SevenTvPulledEntry("01E5"), new SevenTvPulledEntry("01E6")], delta.Pulled);
    }

    [Fact]
    public void ParseEmoteSetUpdate_UpdatedWithoutAnId_IsSkippedAndTheRestKept()
    {
        var body = ParseBody("""
            {"id":"01SET0","kind":3,
             "updated":[{"key":"emotes","old_value":{"id":"01E1","name":"a"},"value":{"name":"b"}},
                        {"key":"emotes","old_value":{"id":"01E2","name":"a"},"value":{"id":"01E2","name":"b"}}]}
            """);

        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(body, NullLogger.Instance);

        Assert.Equal("01E2", Assert.Single(delta.Updated).Id);
    }

    [Fact]
    public void ParseEmoteSetUpdate_QuirkRename_ReportsTheNameThatArrivedNotTheSlotThatChanged()
    {
        // 7TV answers a rename of the second alias of an emote with the first alias as the new name
        // (plan #374 2.3); the parser reports what is on the wire and leaves the interpretation to
        // the consumer.
        var body = ParseBody("""
            {"id":"01SET0","kind":3,
             "updated":[{"key":"emotes","index":0,"old_value":{"id":"01E1","name":"probeB"},"value":{"id":"01E1","name":"probeA"}}]}
            """);

        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(body, NullLogger.Instance);

        var rename = Assert.Single(delta.Updated);
        Assert.Equal(("probeB", "probeA"), (rename.OldName, rename.NewName));
    }

    [Fact]
    public void ParseEmoteSetUpdate_DoesNotReadTheValueTimestamp()
    {
        var body = ParseBody("""
            {"id":"01SET0","kind":3,
             "pushed":[{"key":"emotes","value":{"id":"01E1","name":"x","timestamp":1614589327000}}]}
            """);

        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(body, NullLogger.Instance);

        Assert.Null(Assert.Single(delta.Pushed).AddedToSetAt);
    }

    [Fact]
    public void ParseUserSetChange_RealSetSwitchFrame_ExtractsOldAndNewSetId()
    {
        var change = SevenTvDispatchParser.ParseUserSetChange(
            LoadBody("dispatch-user-update-set-switch.json"), NullLogger.Instance);

        Assert.NotNull(change);
        Assert.Equal("01KFX4RSNM0TP5N32CXBBPH4SH", change.SevenTvUserId);
        Assert.Equal("01KH8GBXCN401GKGN7T1XQKZCP", change.OldEmoteSetId);
        Assert.Equal("01KYACMRPPQTH726E1VFJXCCBV", change.NewEmoteSetId);
    }

    [Fact]
    public void ParseUserSetChange_EmoteSetObjectFallback_ExtractsIds()
    {
        // The wire carries both an emote_set_id string pair and an emote_set object pair; if a
        // future payload only carries the objects, the parser falls back to their ids.
        var body = ParseBody("""
            {"id":"01USER0","kind":1,
             "updated":[{"key":"connections","index":0,"nested":true,
               "value":[{"key":"emote_set","index":null,"type":"object",
                         "old_value":{"id":"01OLDSET","name":"Old"},"value":{"id":"01NEWSET","name":"New"}}]}]}
            """);

        var change = SevenTvDispatchParser.ParseUserSetChange(body, NullLogger.Instance);

        Assert.NotNull(change);
        Assert.Equal("01OLDSET", change.OldEmoteSetId);
        Assert.Equal("01NEWSET", change.NewEmoteSetId);
    }

    [Theory]
    [InlineData("""{"id":"01USER0"}""")]
    [InlineData("""{"id":"01USER0","updated":[]}""")]
    [InlineData("""{"id":"01USER0","updated":[{"key":"style","value":{}}]}""")]
    [InlineData("""{"id":"01USER0","updated":[{"key":"connections","value":"not-an-array"}]}""")]
    [InlineData("""{"id":"01USER0","updated":[{"key":"connections","value":[{"key":"platform","value":"TWITCH"}]}]}""")]
    [InlineData("""{"updated":[{"key":"connections","value":[{"key":"emote_set_id","value":"01X"}]}]}""")]
    public void ParseUserSetChange_WithoutSetSwitch_ReturnsNullWithoutThrowing(string bodyJson)
    {
        Assert.Null(SevenTvDispatchParser.ParseUserSetChange(ParseBody(bodyJson), NullLogger.Instance));
    }
}
