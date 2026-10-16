using System.Text.Json;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The wire shape of <see cref="ChannelUsageSeriesDto"/>, pinned.
///
/// This response is the one place in the API where the payload is encoded rather than merely
/// serialized: day offsets instead of ISO dates, pairs instead of objects, emotes without usage
/// omitted entirely. All three exist to keep a whole-set response small, and all three are read on
/// the other side by hand-written TypeScript (<c>ChannelUsageSeries</c> in
/// <c>web/src/app/core/usage-stats/usage-stat.model.ts</c>). A well-meant "cleanup" that turned the
/// pairs into a record would keep C# compiling, keep every other test green, and leave the atlas's
/// sidecar drawing flat lines — so the encoding is asserted here rather than assumed.
/// </summary>
public class ChannelUsageSeriesWireFormatTests
{
    // Mirrors the API's own configuration; Minimal API results serialize with web defaults.
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Web;

    [Fact]
    public void Days_SerializeAsPairsOfNumbers_NotObjects()
    {
        // protects=day pairs stay number pairs, both identity fields stay on the entry, and the
        // three bound properties are present (null) on the wire;
        // fails_when=pairs become objects, an identity field disappears, or a bound is dropped or renamed;
        // no_claim=nothing about the values of the bounds (the next cases and the service tests cover those).
        // Both identity fields are pinned here, not just the encoding. From #200 on an entry is
        // named by its 7TV id — the only key our own rows and 7TV's live membership list share —
        // and carries the Emote.Id guid alongside it for the length of one transition (spec 6.5,
        // step 1). Dropping either one early breaks a client that is still reading it, so this
        // case fails if either goes missing. The entry is an emote-mode one: slotId present and
        // null, never omitted (plan #376 2.4).
        var dto = new ChannelUsageSeriesDto(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 7),
            [0, 6],
            [new EmoteSeriesEntryDto("01JB8QK3X0000000000000000", "emote-1", null, "PogChamp", [[0, 3], [4, 8]])]);

        var json = JsonSerializer.Serialize(dto, Options);

        Assert.Equal(
            """{"from":"2026-07-01","to":"2026-07-07","liveDays":[0,6],"emotes":[{"sevenTvEmoteId":"01JB8QK3X0000000000000000","emoteId":"emote-1","slotId":null,"name":"PogChamp","days":[[0,3],[4,8]]}],"firstCountedDay":null,"lastCountedDay":null,"channelFirstCountedDay":null}""",
            json);
    }

    [Fact]
    public void ASlotEntry_CarriesSlotIdAndName_BeforeTheUnchangedDayPairs()
    {
        // protects=the slot entry's field names and their order sevenTvEmoteId, emoteId, slotId, name,
        // days (spec 5.3, plan #376 2.4), with the day pairs still pairs;
        // fails_when=slotId or name is renamed, dropped or moved, or the pairs change shape;
        // no_claim=nothing about which rows are slot-mode rows (the service tests decide that).
        var dto = new ChannelUsageSeriesDto(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 7),
            [],
            [new EmoteSeriesEntryDto("01JB8QK3X0000000000000000", "emote-1", "slot-2", "hob44", [[2, 5]])]);

        var json = JsonSerializer.Serialize(dto, Options);

        Assert.Equal(
            """{"from":"2026-07-01","to":"2026-07-07","liveDays":[],"emotes":[{"sevenTvEmoteId":"01JB8QK3X0000000000000000","emoteId":"emote-1","slotId":"slot-2","name":"hob44","days":[[2,5]]}],"firstCountedDay":null,"lastCountedDay":null,"channelFirstCountedDay":null}""",
            json);
    }

    [Fact]
    public void AnEmptyResponse_SerializesAsEmptyArrays_NotNull()
    {
        // protects=an empty response carries empty arrays and null bounds, never null arrays;
        // fails_when=an array turns null/omitted or a bound is dropped from the empty shape;
        // no_claim=nothing about populated responses.
        // The unknown-channel answer. `null` here would reach the client as a missing property and
        // turn a legitimate "nothing to show" into a TypeError on `.map`.
        var dto = new ChannelUsageSeriesDto(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7), [], []);

        var json = JsonSerializer.Serialize(dto, Options);

        Assert.Equal("""{"from":"2026-07-01","to":"2026-07-07","liveDays":[],"emotes":[],"firstCountedDay":null,"lastCountedDay":null,"channelFirstCountedDay":null}""", json);
    }

    [Fact]
    public void CountedDayBounds_SerializeAsIsoDates()
    {
        // protects=the camelCase names and ISO form of the three bounds the frontend reads;
        // fails_when=a property is renamed or the date format changes;
        // no_claim=none beyond protects.
        var dto = new ChannelUsageSeriesDto(
            new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7), [], [], new DateOnly(2026, 7, 2), new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 1));

        var json = JsonSerializer.Serialize(dto, Options);

        Assert.Contains(""""firstCountedDay":"2026-07-02","lastCountedDay":"2026-07-05","channelFirstCountedDay":"2026-07-01"""", json);
    }
}
