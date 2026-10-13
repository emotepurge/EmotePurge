using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Slot plan #375 §2 (T5): the enqueue with SevenTv:SlotReconcile:Enabled on binds every preview entry to
// its own slot (D28) — the active preview through REST's steps 3–5, any other set by exact date with its
// own seed settle — or answers 409 backfill_slots_unsettled and writes nothing (2.4). AK 22, 26, 27, 53,
// 56 and the Sol-P2-2 regression; the flag-off path stays child 3's (Oracle.cs, Slots.cs).
public partial class ChatLogBackfillServiceTests
{
    private static readonly DateTime EntryT1 = new(2026, 4, 20, 9, 0, 0, 123, DateTimeKind.Utc);
    private static readonly DateTime EntryT2 = new(2026, 5, 2, 18, 30, 0, 456, DateTimeKind.Utc);
    private static readonly DateTime EntryT3 = new(2026, 6, 10, 12, 0, 0, 789, DateTimeKind.Utc);
    private static readonly DateTime EarlierArchive = new(2026, 8, 1, 7, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- AK 22, AK 56, 2.5

    // AK 22/56 with the flag on: an emote listed twice in the active set and unknown to the slots gets two
    // authoritative slots, archived and never in the set, one snapshot row each with its own entry day;
    // an unknown emote gets its placeholder, and CreatedRow is the same on every row of an emote (2.5).
    // A seed of another set under the same emote, and its rows, stay as they are. The block then writes
    // both slots of the emote on the same day (AK 24).
    [Fact]
    public async Task On_ActivePreviewWithoutHits_CreatesAnArchivedAuthoritativeSlotPerEntry_AndTheBlockWritesEach()
    {
        var channel = await SeedChannelAsync("bfmulti22");
        await SeedUserAsync();
        var known = await SeedEmoteAsync(channel, "X", "XNow");
        var seedOfB = await AddSlotAsync(known.Id, "XNow", addedAt: null, fromRest: false, seed: true);
        await AddUsageAsync(known.Id, seedOfB.Id, SetB, new DateOnly(2026, 9, 1), 4);
        var emoteRowBefore = await EmoteRowsJsonAsync(channel, "X");
        PreviewReturns(ActiveSetId, "Active", Member("X", "ome44", EntryT1), Member("X", "hob44", EntryT2), Member("Z", "zed", EntryT3));

        var result = await EnqueueOnAsync(channel, ActiveSetId);

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, result.Status);
        Assert.Equal(3, result.Run!.EmoteCount);
        var entries = await NewestSnapshotAsync();
        Assert.Equal(["hob44", "ome44", "zed"], entries.Select(e => e.Name));
        Assert.All(entries, e => Assert.True(e.CreatedSlot));
        Assert.Equal(
            [(known.Id, false, D(2026, 5, 2)), (known.Id, false, D(2026, 4, 20)), (entries[2].EmoteId, true, D(2026, 6, 10))],
            entries.Select(e => (e.EmoteId, e.CreatedRow, e.AddedToSetDay)));

        await using (var verify = CreateDbContext())
        {
            foreach (var (entry, addedAt) in entries.Zip(new[] { EntryT2, EntryT1, EntryT3 }))
            {
                var slot = await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == entry.SlotId);
                Assert.Equal(
                    (entry.EmoteId, entry.Name, (DateTime?)addedAt, true, true, (DateTime?)null, false),
                    (slot.EmoteId, slot.Name, slot.AddedAt, slot.AddedAtFromRest, slot.IsArchived, slot.ArchivedAt, slot.SeedUnsettled));
            }

            Assert.Equal(Columns(seedOfB), Columns(await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == seedOfB.Id)));
            Assert.Equal(1, await verify.UsageStats.CountAsync(u => u.SlotId == seedOfB.Id));
            Assert.Equal(3, await verify.EmoteSlots.CountAsync(s => s.EmoteId == known.Id));
            var placeholder = await verify.Emotes.AsNoTracking().SingleAsync(e => e.SevenTvEmoteId == "Z");
            Assert.Equal((true, true, "zed"), (placeholder.IsPlaceholder, placeholder.IsArchived, placeholder.Name));
        }

        Assert.Equal(emoteRowBefore, await EmoteRowsJsonAsync(channel, "X"));

        var claim = await ClaimAsync();
        await using (var db = CreateDbContext())
        {
            var day = claim.WindowFrom.AddDays(1);
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await CreateService(db).ReplaceBlockAsync(
                claim.RunId, claim.WindowFrom, claim.WindowFrom.AddDays(7),
                [new ChatLogBackfillAggregate(entries[0].SlotId, known.Id, day, 2, 0, 0), new ChatLogBackfillAggregate(entries[1].SlotId, known.Id, day, 5, 0, 0)],
                10, 1, isLastBlock: false));
        }

        await using var rows = CreateDbContext();
        var written = await rows.UsageStats.AsNoTracking()
            .Where(u => u.EmoteId == known.Id && u.EmoteSetId == ActiveSetId)
            .ToDictionaryAsync(u => u.SlotId, u => u.UseCount);
        Assert.Equal(new Dictionary<string, int> { [entries[0].SlotId] = 2, [entries[1].SlotId] = 5 }, written);
    }

    // AK 22/27, existing slots: dated entries bind their authoritative slots by exact date, active or
    // archived; a single slot of a date takes the entry's name (rename, AK 27). The binding changes the
    // identity only (E2): the archived slot stays archived, and nothing new is created.
    [Fact]
    public async Task On_ActivePreview_BindsExistingAuthoritativeSlotsByDate_RenamesButKeepsTheirArchiveState()
    {
        var channel = await SeedChannelAsync("bfmultidate");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "A");
        var renamed = await AddSlotAsync(emote.Id, "A", EntryT1, fromRest: true);
        var archived = await AddSlotAsync(emote.Id, "hob44", EntryT2, fromRest: true, archived: true, archivedAt: EarlierArchive);
        PreviewReturns(ActiveSetId, "Active", Member("X", "ome44", EntryT1), Member("X", "hob44", EntryT2));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, ActiveSetId)).Status);

        var entries = await NewestSnapshotAsync();
        Assert.Equal(
            [("hob44", archived.Id, false), ("ome44", renamed.Id, false)],
            entries.Select(e => (e.Name, e.SlotId, e.CreatedSlot)));
        await using var verify = CreateDbContext();
        var slots = await verify.EmoteSlots.AsNoTracking().Where(s => s.EmoteId == emote.Id).ToDictionaryAsync(s => s.Id);
        Assert.Equal(2, slots.Count);
        Assert.Equal(("ome44", (DateTime?)EntryT1, true, false, NowUtc), (slots[renamed.Id].Name, slots[renamed.Id].AddedAt, slots[renamed.Id].AddedAtFromRest, slots[renamed.Id].IsArchived, slots[renamed.Id].LastSyncedAt));
        Assert.Equal(Columns(archived), Columns(slots[archived.Id]));
    }

    // The bind renamed a slot that is visible in the set: no later REST tick sees an inventory change, so the
    // enqueue itself announces channel.synced and an open usage view refetches the new alias.
    [Fact]
    public async Task On_ActivePreviewRenamingAVisibleSlot_PublishesChannelSynced()
    {
        var channel = await SeedChannelAsync("bfmultisynced");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "A");
        await AddSlotAsync(emote.Id, "A", EntryT1, fromRest: true);
        PreviewReturns(ActiveSetId, "Active", Member("X", "C", EntryT1));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, ActiveSetId)).Status);

        await _publisher.Received(1).PublishAsync(
            LiveEvents.Channel,
            Arg.Is<string>(m => m.Contains(LiveEvents.ChannelSynced, StringComparison.Ordinal) && m.Contains(channel.ChannelName, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    // AK 27 with an archived slot: it is not visible in the set, so renaming it changes nothing a usage view
    // shows by itself, and nothing is announced.
    [Fact]
    public async Task On_ActivePreviewRenamingAnArchivedSlot_PublishesNoChannelSynced()
    {
        var channel = await SeedChannelAsync("bfmultisilent");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "XNow");
        var archived = await AddSlotAsync(emote.Id, "old", EntryT2, fromRest: true, archived: true, archivedAt: EarlierArchive);
        PreviewReturns(ActiveSetId, "Active", Member("X", "new", EntryT2));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, ActiveSetId)).Status);

        await using var verify = CreateDbContext();
        Assert.Equal("new", (await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == archived.Id)).Name);
        await _publisher.DidNotReceive().PublishAsync(
            LiveEvents.Channel, Arg.Is<string>(m => m.Contains(LiveEvents.ChannelSynced, StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    // Flag off is child 3's path: no slot is renamed and nothing but the backfill messages goes out.
    [Fact]
    public async Task Off_Enqueue_PublishesNoChannelSynced()
    {
        PinFlagOff();
        var channel = await SeedChannelAsync("bfmultioffsync");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "A");
        await AddSlotAsync(emote.Id, "A", EntryT1, fromRest: true);
        PreviewReturns(ActiveSetId, "Active", Member("X", "C", EntryT1));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, ActiveSetId, months: 6)).Status);

        await _publisher.DidNotReceive().PublishAsync(
            LiveEvents.Channel, Arg.Is<string>(m => m.Contains(LiveEvents.ChannelSynced, StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    // AK 22, another set: new slots are archived like in the active set; a seed of the active set does not
    // block the preview of another set and stays as it is.
    [Fact]
    public async Task On_InactivePreview_CreatesArchivedSlots_AndLeavesTheActiveSetsSeedAlone()
    {
        var channel = await SeedChannelAsync("bfmultiinactive");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "XNow");
        var activeSeed = await AddSlotAsync(emote.Id, "XNow", addedAt: null, fromRest: false, seed: true);
        await AddUsageAsync(emote.Id, activeSeed.Id, ActiveSetId, new DateOnly(2026, 9, 1), 4);
        PreviewReturns(SetB, "Set B", Member("X", "ome44", EntryT1), Member("X", "hob44", EntryT2));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, SetB)).Status);

        var entries = await NewestSnapshotAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.CreatedSlot));
        await using var verify = CreateDbContext();
        foreach (var entry in entries)
        {
            var slot = await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == entry.SlotId);
            Assert.Equal((true, (DateTime?)null, true, false), (slot.IsArchived, slot.ArchivedAt, slot.AddedAtFromRest, slot.SeedUnsettled));
        }

        Assert.Equal(Columns(activeSeed), Columns(await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == activeSeed.Id)));
    }

    // ---------------------------------------------------------------- AK 26: the seed settle of another set

    // AK 26, in place: the seed of set B with B's rows, preview [ome44 (older), hob44] fully dated → the
    // seed becomes ome44 itself (authoritative, marker off), archived with the enqueue's time as its
    // archive date (R2: it was never archived), B's rows stay on it; hob44 is a new archived slot.
    [Fact]
    public async Task On_InactivePreview_SettlesTheSetsSeedInPlace_OntoTheOldestEntry_Archived()
    {
        var channel = await SeedChannelAsync("bfmulti26a");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "XNow");
        var seed = await AddSlotAsync(emote.Id, "XNow", addedAt: null, fromRest: false, seed: true);
        await AddUsageAsync(emote.Id, seed.Id, SetB, new DateOnly(2026, 9, 1), 4);
        PreviewReturns(SetB, "Set B", Member("X", "hob44", EntryT2), Member("X", "ome44", EntryT1));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, SetB)).Status);

        var entries = (await NewestSnapshotAsync()).ToDictionary(e => e.Name);
        Assert.Equal((seed.Id, false), (entries["ome44"].SlotId, entries["ome44"].CreatedSlot));
        Assert.True(entries["hob44"].CreatedSlot);
        await using var verify = CreateDbContext();
        var settled = await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == seed.Id);
        Assert.Equal(
            ("ome44", (DateTime?)EntryT1, true, false, true, (DateTime?)NowUtc),
            (settled.Name, settled.AddedAt, settled.AddedAtFromRest, settled.SeedUnsettled, settled.IsArchived, settled.ArchivedAt));
        Assert.Equal(1, await verify.UsageStats.CountAsync(u => u.SlotId == seed.Id && u.EmoteSetId == SetB));
        var hob = await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == entries["hob44"].SlotId);
        Assert.Equal(("hob44", (DateTime?)EntryT2, true), (hob.Name, hob.AddedAt, hob.IsArchived));
    }

    // AK 26, target exists: an authoritative slot with ome44's exact date already exists → B's rows move
    // onto it (the colliding cell merged), the seed ends empty, settled, archived without date, and the
    // snapshot row of an earlier run on the seed follows onto the target with CreatedSlot false.
    [Fact]
    public async Task On_InactivePreview_SettlesTheSetsSeedOntoAnExistingTarget_MovingRowsAndOlderSnapshots()
    {
        var channel = await SeedChannelAsync("bfmulti26b");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "XNow");
        var seed = await AddSlotAsync(emote.Id, "XNow", addedAt: null, fromRest: false, seed: true);
        var target = await AddSlotAsync(emote.Id, "ome44", EntryT1, fromRest: true, archived: true);
        var day1 = new DateOnly(2026, 9, 1);
        var day2 = new DateOnly(2026, 9, 2);
        await AddUsageAsync(emote.Id, seed.Id, SetB, day1, 3, UsageStatSource.ChatLogArchive);
        await AddUsageAsync(emote.Id, seed.Id, SetB, day2, 2, UsageStatSource.ChatLogArchive);
        await AddUsageAsync(emote.Id, target.Id, SetB, day1, 10);
        var olderRun = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Completed, finishedAtUtc: NowUtc.AddDays(-2));
        await using (var db = CreateDbContext())
        {
            db.ChatLogBackfillRunEmotes.Add(new ChatLogBackfillRunEmote
            {
                RunId = olderRun.Id,
                EmoteId = emote.Id,
                SevenTvEmoteId = "X",
                Name = "ome44",
                SlotId = seed.Id,
                CreatedSlot = true
            });
            await db.SaveChangesAsync();
        }

        PreviewReturns(SetB, "Set B", Member("X", "ome44", EntryT1), Member("X", "hob44", EntryT2));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, SetB)).Status);

        var entries = (await NewestSnapshotAsync()).ToDictionary(e => e.Name);
        Assert.Equal((target.Id, false), (entries["ome44"].SlotId, entries["ome44"].CreatedSlot));
        Assert.True(entries["hob44"].CreatedSlot);
        await using var verify = CreateDbContext();
        var emptied = await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == seed.Id);
        Assert.Equal((false, true, (DateTime?)null, "XNow"), (emptied.SeedUnsettled, emptied.IsArchived, emptied.ArchivedAt, emptied.Name));
        Assert.False(await verify.UsageStats.AnyAsync(u => u.SlotId == seed.Id));
        var onTarget = await verify.UsageStats.AsNoTracking().Where(u => u.SlotId == target.Id).ToDictionaryAsync(u => u.Date);
        Assert.Equal((13, UsageStatSource.Live), (onTarget[day1].UseCount, onTarget[day1].Source));
        Assert.Equal(2, onTarget[day2].UseCount);
        var older = await verify.ChatLogBackfillRunEmotes.AsNoTracking().SingleAsync(e => e.RunId == olderRun.Id);
        Assert.Equal((target.Id, false, "ome44"), (older.SlotId, older.CreatedSlot, older.Name));
    }

    // ---------------------------------------------------------------- 2.4: 409 backfill_slots_unsettled

    // AK 26: a seed of the previewed set and an undated entry → 409, nothing written — no placeholder for
    // the unknown member, no slot change, the seed keeps its marker, no run, no snapshot, no audit — and
    // the worker is not nudged.
    [Fact]
    public async Task On_InactivePreviewWithAnUndatedEntry_Is409_AndWritesNothing()
    {
        var channel = await SeedChannelAsync("bfmulti409b");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "XNow");
        var seed = await AddSlotAsync(emote.Id, "XNow", addedAt: null, fromRest: false, seed: true);
        await AddUsageAsync(emote.Id, seed.Id, SetB, new DateOnly(2026, 9, 1), 4);
        PreviewReturns(SetB, "Set B", Member("X", "ome44", EntryT1), Member("X", "hob44", null), Member("Z", "zed", EntryT3));
        var before = await EverythingAsync();

        var result = await EnqueueOnAsync(channel, SetB);

        Assert.Equal(ChatLogBackfillEnqueueStatus.SlotsUnsettled, result.Status);
        Assert.Null(result.Run);
        Assert.Equal(before, await EverythingAsync());
        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    // AK 26 / 2.4 (a): an unsettled seed of the active set rejects the active preview — settling it is
    // REST's. The information line names the channel, the reason and the count, never an alias.
    [Fact]
    public async Task On_ActivePreviewWithAnUnsettledSeedOfTheActiveSet_Is409_AndLogsWithoutNames()
    {
        var channel = await SeedChannelAsync("bfmulti409a");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "XNow");
        var seed = await AddSlotAsync(emote.Id, "XNow", addedAt: null, fromRest: false, seed: true);
        await AddUsageAsync(emote.Id, seed.Id, ActiveSetId, new DateOnly(2026, 9, 1), 4);
        PreviewReturns(ActiveSetId, "Active", Member("X", "Zyxwv", EntryT1));
        var before = await EverythingAsync();
        var logger = new RecordingLogger<ChatLogBackfillService>();

        ChatLogBackfillEnqueueResult result;
        await using (var db = CreateDbContext())
        {
            result = await CreateService(db, logger: logger, slotsEnabled: true).EnqueueAsync(channel.ChannelName, ActiveSetId, 6, Today, Actor);
        }

        Assert.Equal(ChatLogBackfillEnqueueStatus.SlotsUnsettled, result.Status);
        Assert.Equal(before, await EverythingAsync());
        var line = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("not settled", StringComparison.Ordinal));
        Assert.Contains("bfmulti409a", line.Message, StringComparison.Ordinal);
        Assert.Contains("1 emotes", line.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(BackfillSlotBindingRejection.ActiveSetSeedUnsettled), line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Zyxwv", line.Message, StringComparison.Ordinal);
        Assert.Contains(emote.SevenTvEmoteId, line.Message, StringComparison.Ordinal);
    }

    // 2.6 / 7.3 "flag off": the same state with the flag off is child 3's path — no 409, the snapshot binds
    // the pair's seed through the base slot rule and creates nothing.
    [Fact]
    public async Task Off_ActivePreviewWithAnUnsettledSeedOfTheActiveSet_IsNo409_AndBindsTheSeed()
    {
        PinFlagOff();
        var channel = await SeedChannelAsync("bfmultioff");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "XNow");
        var seed = await AddSlotAsync(emote.Id, "XNow", addedAt: null, fromRest: false, seed: true);
        await AddUsageAsync(emote.Id, seed.Id, ActiveSetId, new DateOnly(2026, 9, 1), 4);
        PreviewReturns(ActiveSetId, "Active", Member("X", "A", EntryT1));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, ActiveSetId, months: 6)).Status);

        var entry = Assert.Single(await NewestSnapshotAsync());
        Assert.Equal((seed.Id, false), (entry.SlotId, entry.CreatedSlot));
        await using var verify = CreateDbContext();
        Assert.Equal(Columns(seed), Columns(await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == seed.Id)));
        Assert.Equal(1, await verify.EmoteSlots.CountAsync());
    }

    // 2.4 (c) and (b'), R4/R7 (a): an undated entry without a slot of its name in the active preview, and
    // any undated entry in another set's preview, reject the enqueue; nothing is written.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task On_UndatedEntryWithoutBinding_Is409_AndWritesNothing(bool activePreview)
    {
        var channel = await SeedChannelAsync(activePreview ? "bfmulti409c" : "bfmulti409bb");
        await SeedUserAsync();
        var set = activePreview ? ActiveSetId : SetB;
        PreviewReturns(set, "Set", Member("Y", "dated", EntryT1), Member("Z", "undated", null));
        var before = await EverythingAsync();

        Assert.Equal(ChatLogBackfillEnqueueStatus.SlotsUnsettled, (await EnqueueOnAsync(channel, set)).Status);

        Assert.Equal(before, await EverythingAsync());
    }

    // 2.2 step 5: an undated entry of the active preview binds an active slot of its name, authoritative
    // or not, and never touches its date.
    [Fact]
    public async Task On_UndatedActiveEntry_BindsItsActiveNamesake_WithoutTouchingTheDate()
    {
        var channel = await SeedChannelAsync("bfmultiundated");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "A");
        var slot = await AddSlotAsync(emote.Id, "A", EntryT1, fromRest: true);
        PreviewReturns(ActiveSetId, "Active", Member("X", "A", null));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, ActiveSetId)).Status);

        var entry = Assert.Single(await NewestSnapshotAsync());
        Assert.Equal((slot.Id, false, (DateOnly?)null), (entry.SlotId, entry.CreatedSlot, entry.AddedToSetDay));
        await using var verify = CreateDbContext();
        Assert.Equal(Columns(slot), Columns(await verify.EmoteSlots.AsNoTracking().SingleAsync()));
    }

    // ---------------------------------------------------------------- Name binding: push slot (#374), Sol-P2-2

    // Hand-off #374: the provisional slot an EventAPI push created is bound by name in the active preview,
    // and the dated entry authorizes its date — no second slot next to it.
    [Fact]
    public async Task On_ActivePreview_BindsThePushSlotByName_AndAuthorizesItsDate()
    {
        var channel = await SeedChannelAsync("bfmultipush");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "A");
        var push = await AddSlotAsync(emote.Id, "A", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), fromRest: false);
        PreviewReturns(ActiveSetId, "Active", Member("X", "A", EntryT3));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, ActiveSetId)).Status);

        var entry = Assert.Single(await NewestSnapshotAsync());
        Assert.Equal((push.Id, false), (entry.SlotId, entry.CreatedSlot));
        await using var verify = CreateDbContext();
        var bound = await verify.EmoteSlots.AsNoTracking().SingleAsync();
        Assert.Equal(("A", (DateTime?)EntryT3, true, false), (bound.Name, bound.AddedAt, bound.AddedAtFromRest, bound.IsArchived));
    }

    // Sol-P2-2: the preview of set X settles X's seed by exact date only. A same-named provisional push slot
    // of the active set Y is no target: the seed is re-keyed in place and archived, Y's slot keeps its name,
    // date, provisional state, activity and rows — and the next flag-on REST tick of Y binds it by name
    // and creates no slot.
    [Fact]
    public async Task On_InactivePreview_NeverSettlesOntoAProvisionalSlotOfTheActiveSet()
    {
        var channel = await SeedChannelAsync("bfmultisolp22");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "A");
        var push = await AddSlotAsync(emote.Id, "A", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), fromRest: false);
        await AddUsageAsync(emote.Id, push.Id, ActiveSetId, new DateOnly(2026, 10, 2), 6);
        var seed = await AddSlotAsync(emote.Id, "Old", addedAt: null, fromRest: false, seed: true);
        await AddUsageAsync(emote.Id, seed.Id, SetB, new DateOnly(2026, 9, 1), 4);
        PreviewReturns(SetB, "Set B", Member("X", "A", EntryT1));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, SetB)).Status);

        var entry = Assert.Single(await NewestSnapshotAsync());
        Assert.Equal((seed.Id, false), (entry.SlotId, entry.CreatedSlot));
        await using (var verify = CreateDbContext())
        {
            var settled = await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == seed.Id);
            Assert.Equal(("A", (DateTime?)EntryT1, true, false, true), (settled.Name, settled.AddedAt, settled.AddedAtFromRest, settled.SeedUnsettled, settled.IsArchived));
            Assert.Equal(Columns(push), Columns(await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == push.Id)));
            Assert.Equal(1, await verify.UsageStats.CountAsync(u => u.SlotId == push.Id));
            Assert.Equal(1, await verify.UsageStats.CountAsync(u => u.SlotId == seed.Id && u.EmoteSetId == SetB));
        }

        await RestOnAsync(channel, ActiveSetId, Live("X", "A", EntryT3));

        await using var after = CreateDbContext();
        var slots = await after.EmoteSlots.AsNoTracking().Where(s => s.EmoteId == emote.Id).ToDictionaryAsync(s => s.Id);
        Assert.Equal(2, slots.Count);
        Assert.Equal(("A", (DateTime?)EntryT3, true, false), (slots[push.Id].Name, slots[push.Id].AddedAt, slots[push.Id].AddedAtFromRest, slots[push.Id].IsArchived));
        Assert.Equal(("A", (DateTime?)EntryT1, true), (slots[seed.Id].Name, slots[seed.Id].AddedAt, slots[seed.Id].IsArchived));
    }

    // ---------------------------------------------------------------- AK 27: enqueue against REST

    // AK 27, enqueue first: the enqueue creates the archived slot A(t) for a new entry and holds the channel
    // row; the REST tick for the same entry, renamed to C meanwhile, waits at that row, then binds the slot
    // by its date and renames it. One slot, the snapshot and REST on it.
    [Fact]
    public async Task On_EnqueueFirst_ThenRestWithTheRenamedEntry_EndWithOneRenamedSlot()
    {
        var channel = await SeedChannelAsync("bfmulti27a");
        await SeedUserAsync();
        PreviewReturns(ActiveSetId, "Active", Member("X", "A", EntryT1));

        const string tag = "bfmulti27a-rest";
        await using var restDb = fixture.CreateTaggedDbContext(tag, DatabaseName);
        Task<SevenTvSyncResult?>? rest = null;
        var hook = new CommandHook(
            sql => sql.Contains("INSERT INTO \"ChatLogBackfillRunEmotes\"", StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence != 1)
                {
                    return;
                }

                rest = Task.Run(() => SlotSyncKit.CreateService(restDb, SlotSyncKit.ApiReturning(channel, ActiveSetId, Live("X", "C", EntryT1)))
                    .SyncChannelAsync(channel.ChannelName));
                await fixture.WaitUntilBlockedOnLockAsync(tag, rest);
            });
        await using (var enqueueDb = fixture.CreateTaggedDbContext("bfmulti27a-enq", [hook], databaseName: DatabaseName))
        {
            Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued,
                (await CreateService(enqueueDb, slotsEnabled: true).EnqueueAsync(channel.ChannelName, ActiveSetId, 6, Today, Actor)).Status);
        }

        Assert.NotNull(rest);
        Assert.NotNull(await rest);
        var entry = Assert.Single(await NewestSnapshotAsync());
        await using var verify = CreateDbContext();
        var slot = await verify.EmoteSlots.AsNoTracking().SingleAsync();
        Assert.Equal((entry.SlotId, "C", (DateTime?)EntryT1, false), (slot.Id, slot.Name, slot.AddedAt, slot.IsArchived));
        Assert.True(entry.CreatedSlot);
    }

    // AK 27, REST first: REST creates the active slot A(t) and holds the channel row; the enqueue of the
    // renamed entry C(t) waits, then binds that slot by date and renames it, keeping it active (E2).
    [Fact]
    public async Task On_RestFirst_ThenTheEnqueueWithTheRenamedEntry_EndWithOneRenamedSlot()
    {
        var channel = await SeedChannelAsync("bfmulti27b");
        await SeedUserAsync();
        PreviewReturns(ActiveSetId, "Active", Member("X", "C", EntryT1));

        const string tag = "bfmulti27b-enq";
        await using var enqueueDb = fixture.CreateTaggedDbContext(tag, DatabaseName);
        Task<ChatLogBackfillEnqueueResult>? enqueue = null;
        var hook = new CommandHook(
            sql => sql.Contains("INSERT INTO \"EmoteSlots\"", StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence != 1)
                {
                    return;
                }

                enqueue = Task.Run(() => CreateService(enqueueDb, slotsEnabled: true).EnqueueAsync(channel.ChannelName, ActiveSetId, 6, Today, Actor));
                await fixture.WaitUntilBlockedOnLockAsync(tag, enqueue);
            });
        await using (var restDb = fixture.CreateTaggedDbContext("bfmulti27b-rest", [hook], databaseName: DatabaseName))
        {
            Assert.NotNull(await SlotSyncKit.CreateService(restDb, SlotSyncKit.ApiReturning(channel, ActiveSetId, Live("X", "A", EntryT1)))
                .SyncChannelAsync(channel.ChannelName));
        }

        Assert.NotNull(enqueue);
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await enqueue).Status);
        var entry = Assert.Single(await NewestSnapshotAsync());
        await using var verify = CreateDbContext();
        var slot = await verify.EmoteSlots.AsNoTracking().SingleAsync();
        Assert.Equal((entry.SlotId, false, "C", (DateTime?)EntryT1, false), (slot.Id, entry.CreatedSlot, slot.Name, slot.AddedAt, slot.IsArchived));
    }

    // AK 27, a real group: two entries of one emote with their own dates stay two slots.
    [Fact]
    public async Task On_ARealGroupOfTwoDatedEntries_StaysTwoSlots_AlsoAfterRest()
    {
        var channel = await SeedChannelAsync("bfmulti27c");
        await SeedUserAsync();
        PreviewReturns(ActiveSetId, "Active", Member("X", "A", EntryT1), Member("X", "C", EntryT2));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, ActiveSetId)).Status);
        await RestOnAsync(channel, ActiveSetId, Live("X", "A", EntryT1), Live("X", "C", EntryT2));

        var entries = await NewestSnapshotAsync();
        await using var verify = CreateDbContext();
        var slots = await verify.EmoteSlots.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
        Assert.Equal([("A", false), ("C", false)], slots.Select(s => (s.Name, s.IsArchived)));
        Assert.Equal(slots.Select(s => s.Id).Order(StringComparer.Ordinal), entries.Select(e => e.SlotId).Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- AK 53: the returning slot S1

    // AK 53 (a): S1(A, t1) archived with history, no active A; the enqueue of A(t3) re-keys S1 onto t3 (D7)
    // and leaves it archived; the next REST tick binds S1 by its new date and un-archives it — no S2.
    [Fact]
    public async Task On_Ak53a_TheEnqueueReKeysTheArchivedSlot_AndRestThenResumesIt()
    {
        var (channel, emote, s1) = await SeedReturningSlotAsync("bfmulti53a");
        PreviewReturns(ActiveSetId, "Active", Member("X", "A", EntryT3));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, ActiveSetId)).Status);

        var entry = Assert.Single(await NewestSnapshotAsync());
        Assert.Equal((s1.Id, false), (entry.SlotId, entry.CreatedSlot));
        await using (var verify = CreateDbContext())
        {
            var rekeyed = await verify.EmoteSlots.AsNoTracking().SingleAsync();
            Assert.Equal(((DateTime?)EntryT3, true, true, (DateTime?)EarlierArchive), (rekeyed.AddedAt, rekeyed.AddedAtFromRest, rekeyed.IsArchived, rekeyed.ArchivedAt));
        }

        await RestOnAsync(channel, ActiveSetId, Live("X", "A", EntryT3));

        await using var after = CreateDbContext();
        var resumed = await after.EmoteSlots.AsNoTracking().SingleAsync();
        Assert.Equal((s1.Id, false, (DateTime?)EntryT3), (resumed.Id, resumed.IsArchived, resumed.AddedAt));
        Assert.Equal(2, await after.UsageStats.CountAsync(u => u.SlotId == s1.Id));
        Assert.Equal(emote.Id, resumed.EmoteId);
    }

    // AK 53 (b): after the enqueue's re-key, a restore report naming S1 with its old generation t1 is stale
    // and a no-op (#376); S1 stays archived on t3.
    [Fact]
    public async Task On_Ak53b_ARestoreReportOfTheOldGenerationAfterTheEnqueue_IsANoOp()
    {
        var (channel, _, s1) = await SeedReturningSlotAsync("bfmulti53b");
        PreviewReturns(ActiveSetId, "Active", Member("X", "A", EntryT3));
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, ActiveSetId)).Status);
        EmoteSlot rekeyed;
        await using (var read = CreateDbContext())
        {
            rekeyed = await read.EmoteSlots.AsNoTracking().SingleAsync();
        }

        await RestoreReportAsync(channel, s1.Id, EntryT1);

        await using var verify = CreateDbContext();
        Assert.Equal(Columns(rekeyed), Columns(await verify.EmoteSlots.AsNoTracking().SingleAsync()));
    }

    // AK 53 (c): a restore report first reactivates S1 with a provisional date; the enqueue of A(t3) then
    // binds S1 by name and authorizes t3. Still one slot, the history on S1.
    [Fact]
    public async Task On_Ak53c_ARestoreReportFirst_ThenTheEnqueueBindsS1ByName_AndAuthorizesTheDate()
    {
        var (channel, _, s1) = await SeedReturningSlotAsync("bfmulti53c");
        await RestoreReportAsync(channel, s1.Id, EntryT1);
        await using (var read = CreateDbContext())
        {
            var restored = await read.EmoteSlots.AsNoTracking().SingleAsync();
            Assert.Equal((false, false), (restored.IsArchived, restored.AddedAtFromRest));
        }

        PreviewReturns(ActiveSetId, "Active", Member("X", "A", EntryT3));
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueOnAsync(channel, ActiveSetId)).Status);

        var entry = Assert.Single(await NewestSnapshotAsync());
        Assert.Equal((s1.Id, false), (entry.SlotId, entry.CreatedSlot));
        await using var verify = CreateDbContext();
        var bound = await verify.EmoteSlots.AsNoTracking().SingleAsync();
        Assert.Equal(((DateTime?)EntryT3, true, false), (bound.AddedAt, bound.AddedAtFromRest, bound.IsArchived));
        Assert.Equal(2, await verify.UsageStats.CountAsync(u => u.SlotId == s1.Id));
    }

    // ---------------------------------------------------------------- Retry (2.1)

    // A slot key conflict in the first attempt (a writer without the emote lock inserts the authoritative
    // slot the enqueue's rename would produce) rolls the attempt back; the second attempt reads the new
    // slot behind the locks and binds it by date and name.
    [Fact]
    public async Task On_ASlotKeyConflictInTheFirstAttempt_IsRetried_AndTheSecondAttemptSucceeds()
    {
        var channel = await SeedChannelAsync("bfmultiretry");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "A");
        var existing = await AddSlotAsync(emote.Id, "A", EntryT1, fromRest: true);
        PreviewReturns(ActiveSetId, "Active", Member("X", "C", EntryT1));
        string? inserted = null;
        var hook = new SaveHook(async (_, call) =>
        {
            if (call != 1)
            {
                return;
            }

            await using var other = CreateDbContext();
            var slot = new EmoteSlot { EmoteId = emote.Id, Name = "C", AddedAt = EntryT1, AddedAtFromRest = true, IsArchived = true, LastSyncedAt = NowUtc };
            other.EmoteSlots.Add(slot);
            await other.SaveChangesAsync();
            inserted = slot.Id;
        });

        await using (var db = CreateDbContext(hook))
        {
            Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued,
                (await CreateService(db, slotsEnabled: true).EnqueueAsync(channel.ChannelName, ActiveSetId, 6, Today, Actor)).Status);
        }

        Assert.Equal(2, hook.Calls);
        var entry = Assert.Single(await NewestSnapshotAsync());
        Assert.Equal(inserted, entry.SlotId);
        await using var verify = CreateDbContext();
        Assert.Equal(Columns(existing), Columns(await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == existing.Id)));
        Assert.Equal(1, await verify.ChatLogBackfillRuns.CountAsync());
    }

    // ---------------------------------------------------------------- Helpers

    private async Task<ChatLogBackfillEnqueueResult> EnqueueOnAsync(Channel channel, string emoteSetId, int months = 6)
    {
        await using var db = CreateDbContext();
        return await CreateService(db, slotsEnabled: true).EnqueueAsync(channel.ChannelName, emoteSetId, months, Today, Actor);
    }

    private async Task<EmoteSlot> AddSlotAsync(
        string emoteId, string name, DateTime? addedAt, bool fromRest, bool archived = false, DateTime? archivedAt = null, bool seed = false)
    {
        await using var db = CreateDbContext();
        var slot = new EmoteSlot
        {
            EmoteId = emoteId,
            Name = name,
            AddedAt = addedAt,
            AddedAtFromRest = fromRest,
            IsArchived = archived,
            ArchivedAt = archivedAt,
            SeedUnsettled = seed,
            LastSyncedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot;
    }

    private async Task AddUsageAsync(
        string emoteId, string slotId, string emoteSetId, DateOnly date, int uses, UsageStatSource source = UsageStatSource.Live)
    {
        await using var db = CreateDbContext();
        db.UsageStats.Add(new UsageStat { EmoteId = emoteId, SlotId = slotId, EmoteSetId = emoteSetId, Date = date, UseCount = uses, Source = source });
        await db.SaveChangesAsync();
    }

    // The snapshot rows of the newest run, by alias.
    private async Task<List<ChatLogBackfillRunEmote>> NewestSnapshotAsync()
    {
        await using var db = CreateDbContext();
        var runId = await db.ChatLogBackfillRuns.MaxAsync(r => r.Id);
        return (await db.ChatLogBackfillRunEmotes.AsNoTracking().Where(e => e.RunId == runId).ToListAsync())
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    // StateFingerprintAsync plus every slot and usage row: everything a flag-on enqueue may write.
    private async Task<string> EverythingAsync()
    {
        await using var db = CreateDbContext();
        var slots = await db.EmoteSlots.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
        var usage = await db.UsageStats.AsNoTracking().OrderBy(u => u.Id).ToListAsync();
        return await StateFingerprintAsync() + JsonSerializer.Serialize(new
        {
            Slots = slots.Select(Columns),
            Usage = usage.Select(u => new { u.Id, u.SlotId, u.EmoteId, u.EmoteSetId, u.Date, u.UseCount, u.Source }),
        });
    }

    private static string Columns(EmoteSlot s) =>
        $"{s.Id}|{s.EmoteId}|{s.Name}|{s.AddedAt:O}|{s.AddedAtFromRest}|{s.IsArchived}|{s.ArchivedAt:O}|{s.SeedUnsettled}|{s.LastSyncedAt:O}";

    // The flag-on REST reconcile of the channel against the given live set.
    private async Task RestOnAsync(Channel channel, string emoteSetId, params SevenTvEmote[] live)
    {
        await using var db = CreateDbContext();
        Assert.NotNull(await SlotSyncKit.CreateService(db, SlotSyncKit.ApiReturning(channel, emoteSetId, live))
            .SyncChannelAsync(channel.ChannelName));
    }

    // AK 53: an archived emote X whose only slot S1(A, t1) was archived at EarlierArchive and carries two
    // live rows of the active set; no active slot named A.
    private async Task<(Channel Channel, Emote Emote, EmoteSlot S1)> SeedReturningSlotAsync(string name)
    {
        var channel = await SeedChannelAsync(name);
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "A", archived: true, archivedAt: EarlierArchive);
        var s1 = await AddSlotAsync(emote.Id, "A", EntryT1, fromRest: true, archived: true, archivedAt: EarlierArchive);
        await AddUsageAsync(emote.Id, s1.Id, ActiveSetId, new DateOnly(2026, 7, 1), 3);
        await AddUsageAsync(emote.Id, s1.Id, ActiveSetId, new DateOnly(2026, 7, 2), 1);
        return (channel, emote, s1);
    }

    // The flag-on set-centric restore report (#376) for S1 with the generation it names.
    private async Task RestoreReportAsync(Channel channel, string slotId, DateTime generation)
    {
        await using var db = CreateDbContext();
        var service = new EmoteService(
            db, NullLogger<EmoteService>.Instance, Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db),
            new SlotReconcileOptions { Enabled = true });
        await service.MarkRestoredInSetAsync(
            ActiveSetId, "owner-7tv", "owner", "4499", ["X"], channel.ChannelName, Actor,
            [new SyncInSetEntryDto("X", "A", slotId, generation)]);
    }
}
