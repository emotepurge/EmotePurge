using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishService part 5 (plan #485 T0b, 2.1a): complete-removal bound to slot, generation and set, checked against the
// slot inventory behind the channel lock, and idempotent. One case per row of the ten-row table, the lock order
// against an open slot transaction and two reports at once. The cases with the real sync writers and a paused wish
// reconcile are in WishReconcileServiceTests.RemovalReport.
public partial class WishServiceTests
{
    private const string ActiveSet = "01WISHACTIVESET000000000AA";
    private const string OtherSet = "01WISHOTHERSET0000000000BB";

    // ------------------------------------------------------------------------------------ row 1

    [Theory]
    [InlineData("no-slot")]
    [InlineData("empty-slot")]
    [InlineData("no-generation")]
    [InlineData("no-set")]
    [InlineData("empty-set")]
    [InlineData("evidence-case")]
    [InlineData("evidence-unknown")]
    public async Task Report_WithoutSlotGenerationOrSet_OrWithAnUnknownEvidence_IsRequestInvalid_BeforeTheWishIsLookedUp(string variant)
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        var full = await ReportOfAsync(bound.WishId);
        var request = variant switch
        {
            "no-slot" => full with { SlotId = null },
            "empty-slot" => full with { SlotId = "" },
            "no-generation" => full with { SlotAddedAtUtc = null },
            "no-set" => full with { EmoteSetId = null },
            "empty-set" => full with { EmoteSetId = "" },
            "evidence-case" => full with { Evidence = "Acknowledged" },
            _ => full with { Evidence = "deleted" },
        };
        var before = await SnapshotAsync(bound.WishId);

        Assert.Equal(WishOutcome.RequestInvalid, (await Sut().CompleteRemovalAsync(channel.Name, bound.WishId, request, Actor)).Outcome);
        Assert.Equal(WishOutcome.RequestInvalid, (await Sut().CompleteRemovalAsync(channel.Name, 987654321, request, Actor)).Outcome);

        Assert.Equal(before, await SnapshotAsync(bound.WishId));
        await AssertNoTraceAsync(channel);
    }

    // ------------------------------------------------------------------------------------ row 2

    [Fact]
    public async Task Report_ForAWishOfAnotherChannel_IsWishNotFound()
    {
        var channel = await SeedChannelAsync();
        var other = await SeedChannelAsync();
        var foreign = await SeedBoundRowAsync(other, WishStatus.Active);
        var before = await SnapshotAsync(foreign.WishId);

        var result = await Sut().CompleteRemovalAsync(channel.Name, foreign.WishId, await ReportOfAsync(foreign.WishId), Actor);

        Assert.Equal(WishOutcome.WishNotFound, result.Outcome);
        Assert.Equal(before, await SnapshotAsync(foreign.WishId));
        await AssertNoTraceAsync(channel);
    }

    // ------------------------------------------------------------------------------------ row 3

    [Theory]
    [InlineData(WishStatus.Active)]
    [InlineData(WishStatus.Presented)]
    [InlineData(WishStatus.RemovedExternally)]
    public async Task Report_ForAnotherSet_IsAConflict_AndChangesNothing(string status)
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, status);
        var before = await SnapshotAsync(bound.WishId);

        var result = await ReportRemovalAsync(channel, bound.WishId, set: OtherSet);

        Assert.Equal(WishOutcome.StateConflict, result.Outcome);
        Assert.Equal(before, await SnapshotAsync(bound.WishId));
        await AssertNoTraceAsync(channel);
    }

    [Fact]
    public async Task Report_InAChannelWithoutAnActiveSet_IsAConflict()
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        await using (var db = fixture.CreateDbContext())
        {
            // "No set" is the empty id (a channel no sync has seen with one).
            await db.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ActiveEmoteSetId, string.Empty));
        }

        var before = await SnapshotAsync(bound.WishId);

        Assert.Equal(WishOutcome.StateConflict, (await ReportRemovalAsync(channel, bound.WishId)).Outcome);
        Assert.Equal(before, await SnapshotAsync(bound.WishId));
        await AssertNoTraceAsync(channel);
    }

    // Row 3 stands before row 5: once the set has switched, even the repeat of a booked report has no proof.
    [Fact]
    public async Task Report_RepeatedAfterTheSetSwitched_IsAConflict_NotTheRepeatSuccess()
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        Assert.Equal(WishOutcome.Success, (await ReportRemovalAsync(channel, bound.WishId)).Outcome);
        await using (var db = fixture.CreateDbContext())
        {
            await db.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ActiveEmoteSetId, OtherSet));
        }

        var booked = await SnapshotAsync(bound.WishId);

        Assert.Equal(WishOutcome.StateConflict, (await ReportRemovalAsync(channel, bound.WishId)).Outcome);
        Assert.Equal(booked, await SnapshotAsync(bound.WishId));
        Assert.Equal(1, await CountRemovalAuditsAsync(channel));
    }

    // ------------------------------------------------------------------------------------ row 4

    [Theory]
    [InlineData("other-slot")]
    [InlineData("later-generation")]
    [InlineData("millisecond-generation")]
    [InlineData("wish-without-generation")]
    public async Task Report_OfAnotherSlotOrGeneration_IsAConflict(string variant)
    {
        var channel = await SeedChannelAsync();
        // A generation with microseconds below the millisecond, as 7TV dates carry them.
        var generation = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active, generation: generation, configure: w =>
        {
            if (variant == "wish-without-generation")
            {
                w.SlotAddedAtUtc = null;
            }
        });
        var report = new CompleteWishRemovalRequest(bound.SlotId, generation, ActiveSet);
        report = variant switch
        {
            "other-slot" => report with { SlotId = await SeedSlotAsync(bound.Emote, "Kappa2", generation) },
            "later-generation" => report with { SlotAddedAtUtc = generation.AddTicks(10) },
            "millisecond-generation" => report with { SlotAddedAtUtc = generation.AddTicks(-(generation.Ticks % TimeSpan.TicksPerMillisecond)) },
            _ => report,
        };
        var before = await SnapshotAsync(bound.WishId);

        var result = await Sut().CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor);

        Assert.Equal(WishOutcome.StateConflict, result.Outcome);
        Assert.Equal(before, await SnapshotAsync(bound.WishId));
        await AssertNoTraceAsync(channel);
    }

    // The generation is compared as the database stores it: the DTO's value verbatim books, so does a value that
    // only differs below the microsecond (the precision a write keeps).
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Report_WithTheGenerationAsTheDtoCarriesIt_Books_AlsoBelowTheMillisecond(int extraTicks)
    {
        var channel = await SeedChannelAsync();
        var generation = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active, generation: generation);
        var dto = (await Sut().GetAsync(channel.Name, bound.WishId))!;
        Assert.Equal(generation, dto.SlotAddedAtUtc);

        var report = new CompleteWishRemovalRequest(dto.SlotId, dto.SlotAddedAtUtc!.Value.AddTicks(extraTicks), ActiveSet);
        var result = await Sut().CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor);

        Assert.Equal((WishOutcome.Success, WishStatus.Removed), (result.Outcome, result.Wish!.Status));
    }

    // A re-add clears the generation; the old run's report (first, or repeated after a booking) books nothing.
    [Fact]
    public async Task Report_AfterAReAdd_IsAConflict()
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        var report = await ReportOfAsync(bound.WishId);
        Assert.Equal(WishOutcome.Success, (await Sut().CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor)).Outcome);
        Assert.True((await Sut().ReAddAsync(channel.Name, bound.WishId, new ReAddWishRequest(WishReAddMode.Timed, null), Actor)).IsSuccess);
        var readded = await SnapshotAsync(bound.WishId);

        var result = await Sut().CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor);

        Assert.Equal(WishOutcome.StateConflict, result.Outcome);
        Assert.Equal(readded, await SnapshotAsync(bound.WishId));
        Assert.Equal(1, await CountRemovalAuditsAsync(channel));
    }

    // Retry after the reconcile re-bound the wish (here written as the reconcile writes it): the old report is a
    // conflict and the wish stays active at its new entry.
    [Fact]
    public async Task Report_RetriedAfterTheWishWasReBound_IsAConflict_AndTheWishStaysActive()
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        var report = await ReportOfAsync(bound.WishId);
        var newGeneration = bound.Generation.AddHours(3);
        var replacement = await SeedSlotAsync(bound.Emote, "Kappa", newGeneration);
        await using (var db = fixture.CreateDbContext())
        {
            await db.EmoteSlots.Where(s => s.Id == bound.SlotId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsArchived, true).SetProperty(x => x.ArchivedAt, Now));
            await db.Wishes.Where(w => w.Id == bound.WishId).ExecuteUpdateAsync(s => s
                .SetProperty(w => w.SlotId, replacement)
                .SetProperty(w => w.SlotAddedAtUtc, newGeneration));
        }

        var before = await SnapshotAsync(bound.WishId);

        Assert.Equal(WishOutcome.StateConflict, (await Sut().CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor)).Outcome);
        Assert.Equal(WishOutcome.StateConflict, (await Sut().CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor)).Outcome);

        Assert.Equal(before, await SnapshotAsync(bound.WishId));
        await AssertNoTraceAsync(channel);
    }

    // ------------------------------------------------------------------------------------ row 5

    // A lost 200: the client reports again with the same triple. Same answer, nothing written again.
    [Fact]
    public async Task Report_Repeated_AnswersTheSameWish_WithOneAuditEntryAndOneEvent()
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        var report = await ReportOfAsync(bound.WishId);

        var first = await Sut().CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor);
        var booked = await SnapshotAsync(bound.WishId);
        _clock.Advance(TimeSpan.FromMinutes(3));
        var second = await Sut().CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor);

        Assert.Equal((WishOutcome.Success, WishOutcome.Success), (first.Outcome, second.Outcome));
        Assert.Equal(first.Wish, second.Wish);
        Assert.Equal(booked, await SnapshotAsync(bound.WishId));
        await using var db = fixture.CreateDbContext();
        var row = await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == bound.WishId);
        Assert.Equal((WishStatus.Removed, SeededDecisionVersion + 1, (DateTime?)Now.AddMinutes(-3)), (row.Status, row.DecisionVersion, row.EndedAtUtc));
        Assert.Equal(1, await CountRemovalAuditsAsync(channel));
        AssertPublishedOnce(channel);
    }

    // ------------------------------------------------------------------------------------ row 6

    // The inventory already shows an entry the pending wish reconcile would bind the wish to: (a) its own slot row
    // revived with a new date, (b) another row under its alias that no wish holds, newer, older or undated.
    [Theory]
    [InlineData(WishStatus.Active, "revived")]
    [InlineData(WishStatus.Active, "newer")]
    [InlineData(WishStatus.Active, "older")]
    [InlineData(WishStatus.Active, "undated")]
    [InlineData(WishStatus.Presented, "newer")]
    [InlineData(WishStatus.RemovedExternally, "revived")]
    [InlineData(WishStatus.RemovedExternally, "older")]
    public async Task Report_WhileTheInventoryShowsAReplacement_IsAConflict(string status, string replacement)
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, status);
        var report = await ReportOfAsync(bound.WishId);
        if (replacement == "revived")
        {
            await UpdateSlotAsync(bound.SlotId, s => s.AddedAt = bound.Generation.AddHours(2));
        }
        else
        {
            await UpdateSlotAsync(bound.SlotId, s => (s.IsArchived, s.ArchivedAt) = (true, Now.AddMinutes(-10)));
            DateTime? addedAt = replacement switch
            {
                "newer" => bound.Generation.AddDays(1),
                "older" => bound.Generation.AddDays(-5),
                _ => null,
            };
            await SeedSlotAsync(bound.Emote, "Kappa", addedAt, fromRest: addedAt is not null);
        }

        var before = await SnapshotAsync(bound.WishId);

        var result = await Sut().CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor);

        Assert.Equal(WishOutcome.StateConflict, result.Outcome);
        Assert.Equal(before, await SnapshotAsync(bound.WishId));
        await AssertNoTraceAsync(channel);
    }

    // No replacement: an entry under another alias (E13), one another wish holds, an archived one, an unsettled
    // seed. The report books.
    [Theory]
    [InlineData("other-alias")]
    [InlineData("held")]
    [InlineData("archived")]
    [InlineData("seed")]
    public async Task Report_NextToAnEntryThatIsNoReplacement_Books(string entry)
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        var alias = entry == "other-alias" ? "KappaToo" : "Kappa";
        var other = await SeedSlotAsync(bound.Emote, alias, bound.Generation.AddDays(1), archived: entry == "archived", seed: entry == "seed");
        if (entry == "held")
        {
            await UpdateSlotAsync(bound.SlotId, s => (s.IsArchived, s.ArchivedAt) = (true, Now.AddMinutes(-10)));
            await SeedRowAsync(channel, WishStatus.Kept, w =>
            {
                w.SevenTvEmoteId = bound.Emote.SevenTvEmoteId;
                w.SlotId = other;
                w.SlotAddedAtUtc = bound.Generation.AddDays(1);
            });
        }

        var result = await ReportRemovalAsync(channel, bound.WishId);

        Assert.Equal((WishOutcome.Success, WishStatus.Removed), (result.Outcome, result.Wish!.Status));
    }

    // ------------------------------------------------------------------------------------ rows 7 and 8

    // AK11 with the end time kept: an external removal the reconcile saw first becomes ours within six hours
    // (the boundary included), and the wish keeps the time the entry left the set.
    [Theory]
    [InlineData(5 * 3600, true)]
    [InlineData(6 * 3600, true)]
    [InlineData((6 * 3600) + 1, false)]
    [InlineData(7 * 3600, false)]
    public async Task Report_AfterAnExternalRemoval_BooksWithinSixHours_AndKeepsTheEndTime(int secondsAgo, bool accepted)
    {
        var channel = await SeedChannelAsync();
        var endedAt = Now.AddSeconds(-secondsAgo);
        var bound = await SeedBoundRowAsync(channel, WishStatus.RemovedExternally, configure: w => w.EndedAtUtc = endedAt);
        await UpdateSlotAsync(bound.SlotId, s => (s.IsArchived, s.ArchivedAt) = (true, endedAt));
        var before = await SnapshotAsync(bound.WishId);

        var result = await ReportRemovalAsync(channel, bound.WishId);

        if (accepted)
        {
            Assert.Equal(WishOutcome.Success, result.Outcome);
            await using var db = fixture.CreateDbContext();
            var row = await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == bound.WishId);
            Assert.Equal((WishStatus.Removed, (DateTime?)endedAt, Now), (row.Status, row.EndedAtUtc, row.StatusChangedAtUtc));
            Assert.Equal(endedAt, result.Wish!.EndedAtUtc);
            Assert.Equal(1, await CountRemovalAuditsAsync(channel));
            AssertPublishedOnce(channel);
        }
        else
        {
            Assert.Equal(WishOutcome.StateConflict, result.Outcome);
            Assert.Equal(before, await SnapshotAsync(bound.WishId));
            await AssertNoTraceAsync(channel);
        }
    }

    // Plan finding 9 / T12-Auff. 4: a report after midnight keeps the day the entry left the set.
    [Fact]
    public async Task Report_AfterMidnight_KeepsThePreviousDayAsTheEnd()
    {
        var channel = await SeedChannelAsync();
        _clock.SetUtcNow(new DateTimeOffset(2026, 9, 15, 1, 15, 0, TimeSpan.Zero));
        var endedAt = new DateTime(2026, 9, 14, 23, 40, 0, DateTimeKind.Utc);
        var bound = await SeedBoundRowAsync(channel, WishStatus.RemovedExternally, configure: w => w.EndedAtUtc = endedAt);

        var result = await ReportRemovalAsync(channel, bound.WishId);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal(new DateOnly(2026, 9, 14), DateOnly.FromDateTime(result.Wish!.EndedAtUtc!.Value));
    }

    // ------------------------------------------------------------------------------------ row 9

    [Theory]
    [InlineData(WishStatus.Active, WishRemovalEvidence.Acknowledged)]
    [InlineData(WishStatus.Presented, WishRemovalEvidence.Absent)]
    [InlineData(WishStatus.Active, null)]
    public async Task Report_OfALiveWish_RemovesItNow_AndTheAuditNamesTheReportedEntry(string status, string? evidence)
    {
        var channel = await SeedChannelAsync();
        var generation = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);
        var bound = await SeedBoundRowAsync(channel, status, generation: generation);

        var result = await ReportRemovalAsync(channel, bound.WishId, evidence: evidence);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        await using var db = fixture.CreateDbContext();
        var row = await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == bound.WishId);
        Assert.Equal((WishStatus.Removed, (DateTime?)Now, SeededDecisionVersion + 1), (row.Status, row.EndedAtUtc, row.DecisionVersion));
        Assert.Equal(((DateTime?)null, (int?)null), (row.RemovalQueuedAtUtc, row.RemovalQueuedVersion));
        // A removed wish keeps its slot and generation (only a re-add clears the generation).
        Assert.Equal((bound.SlotId, (DateTime?)generation), (row.SlotId, row.SlotAddedAtUtc));
        var entry = await db.AuditLogEntries.SingleAsync(a => a.ChannelName == channel.Name);
        Assert.Equal(AuditActions.WishRemove, entry.Action);
        using var json = JsonDocument.Parse(entry.DetailsJson!);
        var details = json.RootElement;
        Assert.Equal(bound.SlotId, details.GetProperty("slotId").GetString());
        Assert.Equal(generation, details.GetProperty("slotAddedAtUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(ActiveSet, details.GetProperty("emoteSetId").GetString());
        Assert.Equal(evidence, details.TryGetProperty("evidence", out var e) ? e.GetString() : null);
        // The expiry lies ahead: no late decision.
        Assert.False(details.TryGetProperty("previousExpiresAtUtc", out _));
        AssertPublishedOnce(channel);
    }

    // ------------------------------------------------------------------------------------ concurrency

    // Two reports of the same run at once: the channel lock puts them in line, one books, the other sees removed.
    [Fact]
    public async Task Report_TwiceAtOnce_BooksOnce_AndBothAnswerSuccess()
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        var report = await ReportOfAsync(bound.WishId);
        await using var first = fixture.CreateDbContext();
        await using var second = fixture.CreateDbContext();

        var results = await Task.WhenAll(
            Sut(first).CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor),
            Sut(second).CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor));

        Assert.All(results, r => Assert.Equal((WishOutcome.Success, WishStatus.Removed), (r.Outcome, r.Wish!.Status)));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(SeededDecisionVersion + 1, (await db.Wishes.SingleAsync(w => w.Id == bound.WishId)).DecisionVersion);
        Assert.Equal(1, await CountRemovalAuditsAsync(channel));
        AssertPublishedOnce(channel);
    }

    // A sync holds the channel row while it writes the inventory: the report waits there, before it has locked
    // the wish, and decides on the inventory the sync committed (a replacement under the alias, or another alias).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Report_WaitsForAnOpenSlotTransaction_ThenSeesItsInventory(bool replacement)
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedBoundRowAsync(channel, WishStatus.Active);
        var report = await ReportOfAsync(bound.WishId);
        var before = await SnapshotAsync(bound.WishId);
        await using var holder = fixture.CreateTaggedDbContext("wish-report-slot-writer");
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);
        await holder.EmoteSlots.Where(s => s.Id == bound.SlotId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsArchived, true).SetProperty(x => x.ArchivedAt, Now));
        holder.EmoteSlots.Add(new EmoteSlot
        {
            EmoteId = bound.Emote.Id,
            Name = replacement ? "Kappa" : "KappaToo",
            AddedAt = Now.AddMinutes(-1),
            AddedAtFromRest = true
        });
        await holder.SaveChangesAsync();

        await using var contender = fixture.CreateTaggedDbContext("wish-report-contender");
        var waiting = Sut(contender).CompleteRemovalAsync(channel.Name, bound.WishId, report, Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-report-contender", waiting);

        Assert.Contains("\"Channels\"", await fixture.WaitingQueryAsync("wish-report-contender"));
        Assert.False(await IsWishRowLockedAsync(bound.WishId));

        await holderTransaction.CommitAsync();
        var result = await waiting;

        if (replacement)
        {
            Assert.Equal(WishOutcome.StateConflict, result.Outcome);
            Assert.Equal(before, await SnapshotAsync(bound.WishId));
            await AssertNoTraceAsync(channel);
        }
        else
        {
            Assert.Equal((WishOutcome.Success, WishStatus.Removed), (result.Outcome, result.Wish!.Status));
        }
    }

    // ------------------------------------------------------------------------------------ helpers (removal)

    // A wish in the state, bound to a fresh slot of a fresh emote under alias "Kappa" in the given generation (the
    // slot is in the set; a test that needs it archived says so), as the reconcile leaves a link.
    private async Task<BoundRow> SeedBoundRowAsync(
        Seeded channel, string status, Action<Wish>? configure = null, DateTime? generation = null)
    {
        var emote = await SeedEmoteAsync(channel);
        var addedAt = generation ?? Now.AddDays(-10);
        var slot = await SeedSlotAsync(emote, "Kappa", addedAt);
        var wishId = await SeedRowAsync(channel, status, w =>
        {
            w.SevenTvEmoteId = emote.SevenTvEmoteId;
            w.Alias = "Kappa";
            w.SlotId = slot;
            w.SlotAddedAtUtc = addedAt;
            w.SlotConfirmedAtUtc = Now.AddHours(-2);
            w.LinkOrigin = WishLinkOrigin.Auto;
            w.StartsAtUtc = addedAt;
            configure?.Invoke(w);
        });
        return new BoundRow(wishId, emote, slot, addedAt);
    }

    // The report a client sends for the wish as it is stored: its slot and generation verbatim and the set; a wish
    // without either still gets a complete body, so the table decides, not the input check.
    private async Task<CompleteWishRemovalRequest> ReportOfAsync(long wishId, string set = ActiveSet, string? evidence = null)
    {
        await using var db = fixture.CreateDbContext();
        var wish = await db.Wishes.AsNoTracking().Where(w => w.Id == wishId).Select(w => new { w.SlotId, w.SlotAddedAtUtc }).FirstOrDefaultAsync();
        return new CompleteWishRemovalRequest(wish?.SlotId ?? "no-such-slot", wish?.SlotAddedAtUtc ?? Now, set, evidence);
    }

    private async Task<WishCommandResult> ReportRemovalAsync(
        Seeded channel, long wishId, string set = ActiveSet, string? evidence = null, AppDbContext? db = null) =>
        await Sut(db).CompleteRemovalAsync(channel.Name, wishId, await ReportOfAsync(wishId, set, evidence), Actor);

    private async Task UpdateSlotAsync(string slotId, Action<EmoteSlot> change)
    {
        await using var db = fixture.CreateDbContext();
        change(await db.EmoteSlots.SingleAsync(s => s.Id == slotId));
        await db.SaveChangesAsync();
    }

    private async Task<int> CountRemovalAuditsAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        return await db.AuditLogEntries.CountAsync(a => a.ChannelName == channel.Name && a.Action == AuditActions.WishRemove);
    }

    private async Task AssertNoTraceAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    // Whether another transaction holds the wish row: NOWAIT fails at once with 55P03 instead of waiting.
    private async Task<bool> IsWishRowLockedAsync(long wishId)
    {
        await using var probe = fixture.CreateDbContext();
        await using var transaction = await probe.Database.BeginTransactionAsync();
        try
        {
            await probe.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Wishes" WHERE "Id" = {wishId} FOR UPDATE NOWAIT""");
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return true;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private sealed record BoundRow(long WishId, Emote Emote, string SlotId, DateTime Generation);
}
