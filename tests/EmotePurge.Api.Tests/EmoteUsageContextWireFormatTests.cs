using System.Text.Json;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The wire shape of one <c>/usage-stats/totals</c> row (<see cref="EmoteUsageContextDto"/>), pinned for the
/// emote-mode row — the only kind production serves while <c>SevenTv:SlotReconcile:Enabled</c> is off
/// (plan #376 R1). The fields the frontend reads before slots keep their names, values and relative order;
/// the fields of the slot read API are added with their off values: <c>slotId</c> present and
/// <c>null</c>, <c>firstSeenAtFromRest</c> <c>false</c>, <c>siblingNames</c> an empty array, and last
/// <c>firstSeenAtShared</c> <c>false</c> (plan #377 2.12).
/// </summary>
public class EmoteUsageContextWireFormatTests
{
    // Mirrors the API's own configuration; Minimal API results serialize with web defaults.
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Web;

    [Fact]
    public void AnEmoteModeRow_KeepsThePreSlotFields_AndAddsTheSlotFieldsWithTheirOffValues()
    {
        // protects=the pre-slot field names and their relative order, and the presence and off values of
        // slotId, firstSeenAtFromRest and siblingNames;
        // fails_when=a field is renamed or dropped, slotId is omitted instead of null, or siblingNames
        // serializes as null;
        // no_claim=nothing about which rows are emote-mode rows (the service tests decide that).
        var dto = new EmoteUsageContextDto(
            "emote-1",
            null,
            "PogChamp",
            "01JB8QK3X0000000000000000",
            "https://cdn.7tv.app/emote/example/2x.webp",
            12,
            new DateOnly(2026, 7, 2),
            3,
            new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            false,
            false,
            ["01PREVIOUSSET00000000000000"],
            [],
            false);

        var json = JsonSerializer.Serialize(dto, Options);

        Assert.Equal(
            """{"emoteId":"emote-1","slotId":null,"emoteName":"PogChamp","sevenTvEmoteId":"01JB8QK3X0000000000000000","imageUrl":"https://cdn.7tv.app/emote/example/2x.webp","totalUseCount":12,"lastUsedDate":"2026-07-02","previousWindowUseCount":3,"firstSeenAt":"2026-06-01T08:00:00Z","firstSeenAtFromRest":false,"isArchived":false,"nameTwinEmoteSetIds":["01PREVIOUSSET00000000000000"],"siblingNames":[],"firstSeenAtShared":false}""",
            json);
    }
}
