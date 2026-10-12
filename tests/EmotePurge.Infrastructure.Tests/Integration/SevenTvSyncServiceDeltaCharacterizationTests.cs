using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Tests.Fakes;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The oracle of the emote-wide EventAPI delta path (plan #374, 7.4): what the dispatch path does with
// SevenTv:SlotReconcile:Enabled = false, pinned on the code as it stood before the delta model learned
// old and new names. A table of dispatch frames goes through the real parser and the real service, and
// the observed emote rows, leave observations, slot rows and outcomes are compared with what main did.
// The file deliberately touches only the two stable entry points (ParseEmoteSetUpdate and
// ApplyEmoteSetUpdateAsync) and never builds a delta by hand, so the refactor of the delta type cannot
// force an edit here: any difference after that refactor is a regression, not a test to adjust.
[Collection("Postgres")]
public class SevenTvSyncServiceDeltaCharacterizationTests(PostgresFixture fixture)
{
    private const string SetId = "01J94NYQR0000D15QN0BDGN85E";
    private static readonly DateTime SeedEntered = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string FixtureAwareId = "01FFWH9WV80000JT8GHDKHJNZC";
    private const string FixtureCatJamId = "01F6MQ33FG000FFJ97ZB8MWV52";
    private const string FixtureRainId = "01EZPGBXMR0001DCZS00AD662R";

    private sealed record Seed(string Id, string Name, bool Archived = false, bool Placeholder = false, bool ArchivedAtSet = false);

    private sealed record Row(
        string Id, string Name, string Image, bool Archived, bool Placeholder, bool ArchivedAtSet, bool EnteredAfterSeed, bool FirstSeenSet);

    private sealed record Case(
        Seed[] Seeds,
        Func<JsonElement> Frame,
        SevenTvDeltaOutcome Outcome,
        bool OutcomeCarriesLogin,
        Row[] Rows,
        string[] Observations);

    private static string SeedImage(string id) => $"https://seed.example/{id}.webp";

    private static string WireImage(string id) => $"https://cdn.7tv.app/emote/{id}/4x_static.webp";

    // An unchanged seed row as it must look after a dispatch that did not touch it.
    private static Row Same(string id, string name, bool archived = false, bool placeholder = false, bool archivedAtSet = false) =>
        new(id, name, SeedImage(id), archived, placeholder, archivedAtSet, EnteredAfterSeed: false, FirstSeenSet: false);

    private static JsonElement Body(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement FixtureBody(string fixtureName)
    {
        var json = File.ReadAllText(Path.Combine("Unit", "TestData", fixtureName));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("d").GetProperty("body").Clone();
    }

    // A wire emote object with the image-host block the parser turns into WireImage(id).
    private static string WireEmote(string id, string? name) =>
        "{\"id\":\"" + id + "\"" + (name is null ? "" : ",\"name\":\"" + name + "\"") +
        ",\"data\":{\"host\":{\"url\":\"//cdn.7tv.app/emote/" + id +
        "\",\"files\":[{\"name\":\"4x.webp\",\"static_name\":\"4x_static.webp\"}]}}}";

    private static string WireEmoteWithoutHost(string id, string name) => $$"""{"id":"{{id}}","name":"{{name}}"}""";

    private static string Pushed(string value, int index = 0) => $$"""{"key":"emotes","index":{{index}},"type":"object","value":{{value}}}""";

    private static string Updated(string oldValue, string value, int index = 0) =>
        $$"""{"key":"emotes","index":{{index}},"type":"object","old_value":{{oldValue}},"value":{{value}}}""";

    private static string Pulled(string oldValue, int index = 0) => $$"""{"key":"emotes","index":{{index}},"type":"object","old_value":{{oldValue}}}""";

    private static string Frame(string[]? pushed = null, string[]? updated = null, string[]? pulled = null)
    {
        var parts = new List<string> { "\"id\":\"01SET0\",\"kind\":3" };
        if (pushed is not null)
        {
            parts.Add($"\"pushed\":[{string.Join(",", pushed)}]");
        }

        if (updated is not null)
        {
            parts.Add($"\"updated\":[{string.Join(",", updated)}]");
        }

        if (pulled is not null)
        {
            parts.Add($"\"pulled\":[{string.Join(",", pulled)}]");
        }

        return "{" + string.Join(",", parts) + "}";
    }

    private static readonly Dictionary<string, Case> Cases = new()
    {
        ["real pushed frame creates the row with a push date"] = new(
            [new("keep1", "keep1")],
            () => FixtureBody("dispatch-emote-set-update-pushed.json"),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), new(FixtureAwareId, "Aware", WireImage(FixtureAwareId), false, false, false, true, true)],
            []),

        ["real pulled frame archives the row and records the leave"] = new(
            [new("keep1", "keep1"), new(FixtureCatJamId, "catJAM")],
            () => FixtureBody("dispatch-emote-set-update-pulled.json"),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), Same(FixtureCatJamId, "catJAM", archived: true, archivedAtSet: true)],
            [FixtureCatJamId]),

        ["real rename frame renames the row and takes the new image"] = new(
            [new("keep1", "keep1"), new(FixtureRainId, "FeelsRainMan")],
            () => FixtureBody("dispatch-emote-set-update-renamed.json"),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), new(FixtureRainId, "rain", WireImage(FixtureRainId), false, false, false, false, false)],
            []),

        ["quirk rename: the value carries the first alias, the row takes it emote-wide"] = new(
            [new("keep1", "keep1"), new("multi1", "probeB")],
            () => Body(Frame(updated: [Updated(WireEmote("multi1", "probeB"), WireEmote("multi1", "probeA"))])),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), new("multi1", "probeA", WireImage("multi1"), false, false, false, false, false)],
            []),

        ["update that only swaps the image url is applied"] = new(
            [new("keep1", "keep1")],
            () => Body(Frame(updated: [Updated(WireEmote("keep1", "keep1"), WireEmote("keep1", "keep1"))])),
            SevenTvDeltaOutcome.Applied, true,
            [new("keep1", "keep1", WireImage("keep1"), false, false, false, false, false)],
            []),

        ["two pulled items of one id archive once and record one observation"] = new(
            [new("keep1", "keep1"), new("multi1", "probeA")],
            () => Body(Frame(pulled: [Pulled(WireEmote("multi1", "probeA"), 3), Pulled(WireEmote("multi1", "probeB"), 4)])),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), Same("multi1", "probeA", archived: true, archivedAtSet: true)],
            ["multi1"]),

        ["push without a name creates the row with an empty name"] = new(
            [new("keep1", "keep1")],
            () => Body(Frame(pushed: [Pushed(WireEmote("nameless1", null))])),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), new("nameless1", "", WireImage("nameless1"), false, false, false, true, true)],
            []),

        ["update without a name blanks the row name and keeps the image"] = new(
            [new("keep1", "keep1"), new("old1", "oldname")],
            () => Body(Frame(updated: [Updated(WireEmote("old1", "oldname"), """{"id":"old1"}""")])),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), new("old1", "", SeedImage("old1"), false, false, false, false, false)],
            []),

        ["update with an empty image url keeps the known image and takes the name"] = new(
            [new("keep1", "keep1"), new("img1", "before")],
            () => Body(Frame(updated: [Updated(WireEmoteWithoutHost("img1", "before"), WireEmoteWithoutHost("img1", "after"))])),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), new("img1", "after", SeedImage("img1"), false, false, false, false, false)],
            []),

        ["push of an unknown emote without a host block creates a row with an empty image"] = new(
            [new("keep1", "keep1")],
            () => Body(Frame(pushed: [Pushed(WireEmoteWithoutHost("bare1", "bare"))])),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), new("bare1", "bare", "", false, false, false, true, true)],
            []),

        ["update of an unknown emote creates the row without a push date"] = new(
            [new("keep1", "keep1")],
            () => Body(Frame(updated: [Updated(WireEmote("fresh1", "was"), WireEmote("fresh1", "now"))])),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), new("fresh1", "now", WireImage("fresh1"), false, false, false, true, false)],
            []),

        ["push of an archived row un-archives it and clears the placeholder marker"] = new(
            [new("keep1", "keep1"), new("back1", "gone", Archived: true, Placeholder: true)],
            () => Body(Frame(pushed: [Pushed(WireEmote("back1", "back"))])),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), new("back1", "back", WireImage("back1"), false, false, false, true, false)],
            []),

        ["push of an active row is a rename and keeps the entry stamp"] = new(
            [new("keep1", "keep1"), new("act1", "before")],
            () => Body(Frame(pushed: [Pushed(WireEmote("act1", "after"))])),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), new("act1", "after", WireImage("act1"), false, false, false, false, false)],
            []),

        ["pull of an active row with a stale placeholder marker clears the marker"] = new(
            [new("keep1", "keep1"), new("stale1", "stale", Placeholder: true)],
            () => Body(Frame(pulled: [Pulled(WireEmote("stale1", "stale"))])),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), Same("stale1", "stale", archived: true, archivedAtSet: true)],
            ["stale1"]),

        ["pull of an archived row and of an unknown id records both and changes nothing"] = new(
            [new("keep1", "keep1"), new("arch1", "arch", Archived: true, ArchivedAtSet: true)],
            () => Body(Frame(pulled: [Pulled(WireEmote("arch1", "arch")), Pulled(WireEmote("unknown1", "u"), 1)])),
            SevenTvDeltaOutcome.NoChange, true,
            [Same("keep1", "keep1"), Same("arch1", "arch", archived: true, archivedAtSet: true)],
            ["arch1", "unknown1"]),

        ["pull of the whole active set is skipped as implausible"] = new(
            [new("a1", "a"), new("b1", "b"), new("arch1", "arch", Archived: true, ArchivedAtSet: true)],
            () => Body(Frame(pulled: [Pulled(WireEmote("a1", "a")), Pulled(WireEmote("b1", "b"), 1)])),
            SevenTvDeltaOutcome.ImplausibleSkipped, true,
            [Same("a1", "a"), Same("arch1", "arch", archived: true, archivedAtSet: true), Same("b1", "b")],
            []),

        ["push and pull of the same id in one frame end archived"] = new(
            [new("keep1", "keep1"), new("both1", "both")],
            () => Body(Frame(pushed: [Pushed(WireEmote("both1", "both2"))], pulled: [Pulled(WireEmote("both1", "both"), 1)])),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), new("both1", "both2", WireImage("both1"), true, false, true, false, false)],
            ["both1"]),

        ["push, rename and pull of different emotes in one frame"] = new(
            [new("keep1", "keep1"), new("ren1", "r0"), new("pull1", "p0")],
            () => Body(Frame(
                pushed: [Pushed(WireEmote("new1", "n1"))],
                updated: [Updated(WireEmote("ren1", "r0"), WireEmote("ren1", "r1"), 1)],
                pulled: [Pulled(WireEmote("pull1", "p0"), 2)])),
            SevenTvDeltaOutcome.Applied, true,
            [
                Same("keep1", "keep1"),
                new("new1", "n1", WireImage("new1"), false, false, false, true, true),
                Same("pull1", "p0", archived: true, archivedAtSet: true),
                new("ren1", "r1", WireImage("ren1"), false, false, false, false, false),
            ],
            ["pull1"]),

        ["set-level metadata only is an empty delta without a login"] = new(
            [new("keep1", "keep1")],
            () => Body("""{"id":"01SET0","kind":3,"updated":[{"key":"name","index":null,"type":"string","old_value":"A","value":"B"}]}"""),
            SevenTvDeltaOutcome.NoChange, false,
            [Same("keep1", "keep1")],
            []),

        ["items without an id are skipped, the rest still applies"] = new(
            [new("keep1", "keep1"), new("pull1", "p0")],
            () => Body(Frame(
                pushed: [Pushed("""{"name":"noid"}"""), Pushed(WireEmote("ok1", "ok"), 1)],
                pulled: [Pulled("{}"), Pulled(WireEmote("pull1", "p0"), 1)])),
            SevenTvDeltaOutcome.Applied, true,
            [Same("keep1", "keep1"), new("ok1", "ok", WireImage("ok1"), false, false, false, true, true), Same("pull1", "p0", archived: true, archivedAtSet: true)],
            ["pull1"]),
    };

    public static TheoryData<string> CaseNames => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task FlagOff_DispatchFrame_ProducesTheEmoteWideResultOfMain(string caseName)
    {
        var testCase = Cases[caseName];
        var channel = await SeedChannelAsync($"oracle_{Guid.NewGuid():N}", testCase.Seeds);
        var slotsBefore = await LoadSlotsAsync(channel);
        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(testCase.Frame(), NullLogger.Instance);

        SevenTvDeltaResult result;
        await using (var db = fixture.CreateDbContext())
        {
            result = await CreateService(db).ApplyEmoteSetUpdateAsync(channel.ChannelName, SetId, delta);
        }

        Assert.Equal(testCase.Outcome, result.Outcome);
        Assert.Equal(testCase.OutcomeCarriesLogin ? channel.ChannelName : null, result.ChannelName);

        await using var verify = fixture.CreateDbContext();
        var rows = (await verify.Emotes.AsNoTracking().Where(e => e.ChannelId == channel.Id).ToListAsync())
            .Select(e => new Row(
                e.SevenTvEmoteId, e.Name, e.ImageUrl, e.IsArchived, e.IsPlaceholder, e.ArchivedAt is not null,
                e.LastEnteredSetAtUtc > SeedEntered, e.FirstSeenAt is not null))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(testCase.Rows.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(), rows);

        var observations = await verify.EmoteSetLeaveObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id)
            .Select(o => new { o.SevenTvEmoteId, o.SevenTvEmoteSetId })
            .ToListAsync();
        Assert.All(observations, o => Assert.Equal(SetId, o.SevenTvEmoteSetId));
        Assert.Equal(
            testCase.Observations.OrderBy(i => i, StringComparer.Ordinal),
            observations.Select(o => o.SevenTvEmoteId).OrderBy(i => i, StringComparer.Ordinal));

        // Flag off writes no slot, whatever the frame says.
        Assert.Equal(slotsBefore, await LoadSlotsAsync(channel));
    }

    private static SevenTvSyncService CreateService(AppDbContext db) =>
        new(db, Substitute.For<ISevenTvApiClient>(), new EmoteMatchCache(), new DuplicateEmoteNameTracker(),
            new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(),
            new BroadcasterChannelLockService(db), new RecordingSevenTvSearchBudget(),
            new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System),
            new EmptySetConfirmationTracker(new EmptySetConfirmationOptions(), TimeProvider.System),
            NullLogger<SevenTvSyncService>.Instance,
            new SlotReconcileOptions { Enabled = false });

    // Every seed row also gets two slots (an active alias and an archived one), so a write to
    // EmoteSlots by the flag-off path would show up as a difference in any column.
    private async Task<Channel> SeedChannelAsync(string name, Seed[] seeds)
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", ActiveEmoteSetId = SetId, IsBotActive = true };
        db.Channels.Add(channel);
        foreach (var seed in seeds)
        {
            var emote = new Emote
            {
                ChannelId = channel.Id,
                SevenTvEmoteId = seed.Id,
                Name = seed.Name,
                ImageUrl = SeedImage(seed.Id),
                IsArchived = seed.Archived,
                IsPlaceholder = seed.Placeholder,
                ArchivedAt = seed.ArchivedAtSet ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
                LastEnteredSetAtUtc = SeedEntered,
            };
            db.Emotes.Add(emote);
            db.EmoteSlots.Add(new EmoteSlot
            {
                EmoteId = emote.Id,
                Name = seed.Name,
                AddedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                AddedAtFromRest = true,
                IsArchived = seed.Archived,
                LastSyncedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            });
            db.EmoteSlots.Add(new EmoteSlot
            {
                EmoteId = emote.Id,
                Name = seed.Name + "_alias",
                AddedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                AddedAtFromRest = true,
                IsArchived = true,
                ArchivedAt = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc),
                LastSyncedAt = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc),
            });
        }

        await db.SaveChangesAsync();
        return channel;
    }

    private async Task<List<string>> LoadSlotsAsync(Channel channel)
    {
        await using var db = fixture.CreateDbContext();
        return (await db.EmoteSlots.AsNoTracking()
                .Where(s => s.Emote.ChannelId == channel.Id)
                .ToListAsync())
            .Select(s => $"{s.Id}|{s.EmoteId}|{s.Name}|{s.AddedAt:O}|{s.AddedAtFromRest}|{s.IsArchived}|{s.ArchivedAt:O}|{s.LastSyncedAt:O}|{s.SeedUnsettled}")
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
