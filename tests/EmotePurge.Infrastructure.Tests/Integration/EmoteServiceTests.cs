using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class EmoteServiceTests(PostgresFixture fixture)
{
    private const string OwnerSevenTvUserId = "owner-seven-tv-id";
    private const string OwnerTwitchLogin = "setowner";

    // The owner's Twitch id for every case that does not seed an owner channel: no channel row in
    // the shared database carries it, so the paper entry has no owner channel (N3) unless a case
    // passes a seeded channel's id instead.
    private const string OwnerTwitchUserId = "4499";

    // The Twitch ids on the block list of every EmoteService built here (F10/E8): a real
    // ExcludedChannelFilter read from configuration, the way ExcludedChannelFilterTests builds one.
    private const string ExcludedTwitchChannelId = "4490";
    private const string ExcludedExpectedTwitchChannelId = "4491";
    private const string ExcludedRestoreTwitchChannelId = "4492";
    private const string ExcludedOwnerTwitchChannelId = "4493";

    private static readonly AuditActor Actor = new("100", "synctester");

    [Fact]
    public async Task MarkDeletedAsync_CountsGuidMatches_ButLeavesIsArchivedUnchanged()
    {
        // H4/spec 5.6: this form no longer archives anything — a tab open across a set switch could
        // otherwise archive a row of the *new* active set on the strength of a body that only ever
        // meant the old one. It only counts and audits; the endpoint's own guarded resync (stage 7)
        // is what actually reconciles the row against 7TV afterwards.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_a", TwitchChannelId = "4001", ActiveEmoteSetId = "set-a" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "PogU", SevenTvEmoteId = "7tv-a1", ImageUrl = "https://cdn/a1" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.MarkDeletedAsync("syncdeletetest_a", [emote.Id], Actor);

        Assert.Equal(1, result.ArchivedCount);
        Assert.Empty(result.NotFoundIds);
        Assert.False(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());

        // AK 23: the audit entry carries legacyBodyForm: true, so the admin view can tell this report
        // apart from a set-checked one that actually changed a row.
        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncdeletetest_a" && a.Action == AuditActions.EmotesSyncDeleted);
        Assert.Contains("\"emoteCount\":1", audit.DetailsJson);
        Assert.Contains("\"legacyBodyForm\":true", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedAsync_CountsAnAlreadyArchivedEmoteToo()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_b", TwitchChannelId = "4002", ActiveEmoteSetId = "set-b" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "KEKW", SevenTvEmoteId = "7tv-b1", ImageUrl = "https://cdn/b1", IsArchived = true };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.MarkDeletedAsync("syncdeletetest_b", [emote.Id], Actor);

        Assert.Equal(1, result.ArchivedCount);
        Assert.Empty(result.NotFoundIds);
        // The audit row IS written regardless of the row's prior state — the point of this call is
        // the paper trail (plus the resync it triggers), not a state change it no longer makes.
        Assert.Equal(1, await db.AuditLogEntries.CountAsync(a =>
            a.ChannelName == "syncdeletetest_b" && a.Action == AuditActions.EmotesSyncDeleted));
    }

    [Fact]
    public async Task MarkDeletedAsync_LeavesTheArchiveDateOfAnAlreadyArchivedEmoteAlone()
    {
        await using var db = fixture.CreateDbContext();
        var earlier = DateTime.UtcNow.AddMinutes(-10);
        var channel = new Channel { ChannelName = "syncdeletetest_e", TwitchChannelId = "4006", ActiveEmoteSetId = "set-e" };
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Channel = channel,
            Name = "Kept",
            SevenTvEmoteId = "7tv-e1",
            ImageUrl = "https://cdn/e1",
            IsArchived = true,
            ArchivedAt = earlier
        };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MarkDeletedAsync("syncdeletetest_e", [emote.Id], Actor);

        var archivedAt = await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.ArchivedAt).SingleAsync();
        Assert.NotNull(archivedAt);
        Assert.Equal(earlier, archivedAt.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task MarkDeletedAsync_ReportsUnknownAndForeignIdsAsNotFound()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_c", TwitchChannelId = "4003", ActiveEmoteSetId = "set-c" };
        var foreignChannel = new Channel { ChannelName = "syncdeletetest_c2", TwitchChannelId = "4004", ActiveEmoteSetId = "set-c2" };
        var foreignEmote = new Emote { ChannelId = foreignChannel.Id, Channel = foreignChannel, Name = "Foreign", SevenTvEmoteId = "7tv-c1", ImageUrl = "https://cdn/c1" };
        db.Channels.AddRange(channel, foreignChannel);
        db.Emotes.Add(foreignEmote);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.MarkDeletedAsync("syncdeletetest_c", [foreignEmote.Id, "does-not-exist"], Actor);

        Assert.Equal(0, result.ArchivedCount);
        Assert.Equal(2, result.NotFoundIds.Count);
        // The foreign channel's emote stays untouched.
        Assert.False(await db.Emotes.Where(e => e.Id == foreignEmote.Id).Select(e => e.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task MarkDeletedAsync_MissingChannel_ReturnsAllIdsAsNotFound_WithoutAnAuditRow()
    {
        // Grenzfall (spec 5.6, point 1): the old response shape, unconditionally — the endpoint's own
        // stage 7 still tries a resync (and releases the cooldown on NotFound), but that is the
        // endpoint's job, not the service's.
        await using var db = fixture.CreateDbContext();

        var service = CreateService(db);
        var result = await service.MarkDeletedAsync("syncdeletetest_missing", ["does-not-exist"], Actor);

        Assert.Equal(0, result.ArchivedCount);
        Assert.Equal(["does-not-exist"], result.NotFoundIds);
        Assert.Equal(0, await db.AuditLogEntries.CountAsync(a => a.ChannelName == "syncdeletetest_missing"));
    }

    [Fact]
    public async Task MarkRestoredAsync_CountsGuidMatches_ButLeavesIsArchivedUnchanged()
    {
        // Mirror of MarkDeletedAsync's inversion above, in the restore direction.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncrestoretest_a", TwitchChannelId = "4101", ActiveEmoteSetId = "set-ra" };
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Channel = channel,
            Name = "Back",
            SevenTvEmoteId = "7tv-ra1",
            ImageUrl = "https://cdn/ra1",
            IsArchived = true,
            ArchivedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.MarkRestoredAsync("syncrestoretest_a", [emote.Id], Actor);

        Assert.Equal(1, result.RestoredCount);
        Assert.Empty(result.NotFoundIds);

        var row = await db.Emotes.Where(e => e.Id == emote.Id).Select(e => new { e.IsArchived, e.ArchivedAt }).SingleAsync();
        Assert.True(row.IsArchived);
        Assert.NotNull(row.ArchivedAt);

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncrestoretest_a" && a.Action == AuditActions.EmotesSyncRestored);
        Assert.Contains("\"emoteCount\":1", audit.DetailsJson);
        Assert.Contains("\"legacyBodyForm\":true", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkRestoredAsync_CountsAnAlreadyActiveEmoteToo()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncrestoretest_b", TwitchChannelId = "4102", ActiveEmoteSetId = "set-rb" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "Alive", SevenTvEmoteId = "7tv-rb1", ImageUrl = "https://cdn/rb1" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.MarkRestoredAsync("syncrestoretest_b", [emote.Id], Actor);

        Assert.Equal(1, result.RestoredCount);
        Assert.Empty(result.NotFoundIds);
        Assert.Equal(1, await db.AuditLogEntries.CountAsync(a =>
            a.ChannelName == "syncrestoretest_b" && a.Action == AuditActions.EmotesSyncRestored));
    }

    [Fact]
    public async Task MarkRestoredAsync_ReportsUnknownAndForeignIdsAsNotFound_WithoutAnAuditRow()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncrestoretest_c", TwitchChannelId = "4103", ActiveEmoteSetId = "set-rc" };
        var foreignChannel = new Channel { ChannelName = "syncrestoretest_c2", TwitchChannelId = "4104", ActiveEmoteSetId = "set-rc2" };
        var foreignEmote = new Emote { ChannelId = foreignChannel.Id, Channel = foreignChannel, Name = "Foreign", SevenTvEmoteId = "7tv-rc1", ImageUrl = "https://cdn/rc1", IsArchived = true };
        db.Channels.AddRange(channel, foreignChannel);
        db.Emotes.Add(foreignEmote);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.MarkRestoredAsync("syncrestoretest_c", [foreignEmote.Id, "does-not-exist"], Actor);

        Assert.Equal(0, result.RestoredCount);
        Assert.Equal(2, result.NotFoundIds.Count);
        // The foreign channel's emote stays archived, and a call that matched nothing writes no entry.
        Assert.True(await db.Emotes.Where(e => e.Id == foreignEmote.Id).Select(e => e.IsArchived).SingleAsync());
        Assert.Equal(0, await db.AuditLogEntries.CountAsync(a => a.ChannelName == "syncrestoretest_c"));
    }

    [Fact]
    public async Task MarkImportedAsync_WritesOneAuditEntry_WithCountAndSourceInTheDetails()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncimporttest_a", TwitchChannelId = "4201", ActiveEmoteSetId = "set-ia" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var written = await service.MarkImportedAsync(
            "syncimporttest_a", ["7tv-ia1", "7tv-ia2"], "SourceChannel", "channel", null, Actor);

        Assert.True(written);
        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncimporttest_a" && a.Action == AuditActions.EmotesSyncImported);
        Assert.Contains("\"emoteCount\":2", audit.DetailsJson);
        // Stored normalized (Regel 9), not as the caller typed it.
        Assert.Contains("\"sourceChannelName\":\"sourcechannel\"", audit.DetailsJson);
        Assert.Contains("\"sourceKind\":\"channel\"", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkImportedAsync_TouchesNoEmoteRow()
    {
        // The whole point of R10: this call is audit-only, the target channel's rows are unchanged
        // until its own resync runs.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncimporttest_b", TwitchChannelId = "4202", ActiveEmoteSetId = "set-ib" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "Untouched", SevenTvEmoteId = "7tv-ib1", ImageUrl = "https://cdn/ib1" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MarkImportedAsync("syncimporttest_b", ["7tv-ib1"], null, "file", null, Actor);

        var row = await db.Emotes.Where(e => e.Id == emote.Id)
            .Select(e => new { e.IsArchived, e.ArchivedAt })
            .SingleAsync();
        Assert.False(row.IsArchived);
        Assert.Null(row.ArchivedAt);
        Assert.Equal(1, await db.Emotes.CountAsync(e => e.ChannelId == channel.Id));
    }

    [Fact]
    public async Task MarkImportedAsync_DeduplicatesTheReportedIds_BeforeCounting()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncimporttest_c", TwitchChannelId = "4203", ActiveEmoteSetId = "set-ic" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MarkImportedAsync("syncimporttest_c", ["7tv-ic1", "7tv-ic1"], null, "file", null, Actor);

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncimporttest_c" && a.Action == AuditActions.EmotesSyncImported);
        Assert.Contains("\"emoteCount\":1", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkImportedAsync_ReportsUnknownChannel_AndWritesNoAuditRow()
    {
        await using var db = fixture.CreateDbContext();

        var service = CreateService(db);
        var written = await service.MarkImportedAsync("syncimporttest_unknown", ["7tv-id1"], null, "channel", null, Actor);

        Assert.False(written);
        Assert.Equal(0, await db.AuditLogEntries.CountAsync(a => a.ChannelName == "syncimporttest_unknown"));
    }

    [Fact]
    public async Task MarkImportedAsync_WithTargetEmoteSetId_WritesTargetIsActiveSetOfChannelTrue_WhenItMatchesTheChannelsActiveSet()
    {
        // Spec 6.7/E5: targetIsActiveSetOfChannel is a comparison against Channel.ActiveEmoteSetId at
        // write time, taken from the channel row this call already loaded — not a second 7TV read.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncimporttest_d", TwitchChannelId = "4204", ActiveEmoteSetId = "set-id" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MarkImportedAsync("syncimporttest_d", ["7tv-id1"], null, "file", null, Actor, "set-id");

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncimporttest_d" && a.Action == AuditActions.EmotesSyncImported);
        Assert.Equal("emoteSet", audit.TargetType);
        Assert.Equal("set-id", audit.TargetId);
        Assert.Contains("\"targetEmoteSetId\":\"set-id\"", audit.DetailsJson);
        Assert.Contains("\"targetIsActiveSetOfChannel\":true", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkImportedAsync_WithTargetEmoteSetId_WritesTargetIsActiveSetOfChannelFalse_WhenItDoesNotMatch()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncimporttest_e", TwitchChannelId = "4205", ActiveEmoteSetId = "set-active" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MarkImportedAsync("syncimporttest_e", ["7tv-ie1"], null, "file", null, Actor, "set-not-active");

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncimporttest_e" && a.Action == AuditActions.EmotesSyncImported);
        Assert.Equal("emoteSet", audit.TargetType);
        Assert.Equal("set-not-active", audit.TargetId);
        Assert.Contains("\"targetIsActiveSetOfChannel\":false", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkImportedAsync_WithoutTargetEmoteSetId_WritesNoTargetTypeAndNullTargetIsActiveSetOfChannel()
    {
        // E5: an old client that never learned the field is still a valid, complete call — the row
        // honestly records "no set known" rather than failing after the 7TV mutation already happened.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncimporttest_f", TwitchChannelId = "4206", ActiveEmoteSetId = "set-if" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MarkImportedAsync("syncimporttest_f", ["7tv-if1"], null, "file", null, Actor);

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncimporttest_f" && a.Action == AuditActions.EmotesSyncImported);
        Assert.Null(audit.TargetType);
        Assert.Null(audit.TargetId);
        Assert.Contains("\"targetEmoteSetId\":null", audit.DetailsJson);
        Assert.Contains("\"targetIsActiveSetOfChannel\":null", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkImportedToSetAsync_WritesAnAuditEntry_WithChannelNameNull_AndTheResolvedOwnerIdentity()
    {
        // The set-centric endpoint's own service call (spec 6.7): no Channel row is loaded or
        // required at all, and ChannelName is deliberately null — the row surfaces in the global
        // admin log, not a channel's own activity feed, because the target may not be a tracked
        // channel in the first place (a bewusster Rest, not a bug).
        await using var db = fixture.CreateDbContext();

        var service = CreateService(db);
        await service.MarkImportedToSetAsync(
            "set-foreign", "owner-seven-tv-id", "handofblood", ["7tv-x1", "7tv-x2"],
            "sourcechannel", "channel", null, Actor);

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.TargetType == "emoteSet" && a.TargetId == "set-foreign" && a.Action == AuditActions.EmotesSyncImported);
        Assert.Null(audit.ChannelName);
        Assert.Contains("\"emoteCount\":2", audit.DetailsJson);
        Assert.Contains("\"targetEmoteSetId\":\"set-foreign\"", audit.DetailsJson);
        Assert.Contains("\"targetOwnerSevenTvUserId\":\"owner-seven-tv-id\"", audit.DetailsJson);
        Assert.Contains("\"targetOwnerTwitchLogin\":\"handofblood\"", audit.DetailsJson);
        // The set-centric endpoint never compares against a Channel row — no such property is written.
        Assert.DoesNotContain("targetIsActiveSetOfChannel", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedAsync_LegacyForm_LogsLegacyBodyFormUsage()
    {
        // E4: the log line that lets Folge-Issue 1 measure, rather than guess, when the legacy body
        // form is safe to retire.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_legacylog", TwitchChannelId = "4304", ActiveEmoteSetId = "set-legacylog" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "Legacy", SevenTvEmoteId = "7tv-legacylog1", ImageUrl = "https://cdn/legacylog1" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var logger = new RecordingLogger<EmoteService>();
        var service = CreateService(db, logger);
        await service.MarkDeletedAsync("syncdeletetest_legacylog", [emote.Id], Actor);

        Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information
                && e.Message.Contains("sync-deleted", StringComparison.Ordinal)
                && e.Message.Contains("legacy body form", StringComparison.Ordinal));
    }

    // The set-centric report (restore-per-set spec 5.2/5.5, AK 10–13, 28). Every case uses its own
    // set id, so the "which channels have this set active" query never sees another test's rows in
    // the shared database.

    [Fact]
    public async Task MarkDeletedInSetAsync_ActiveSetOfATrackedChannel_ArchivesItsRows_AndAuditsWithTheChannel()
    {
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetdel_active", "4401", "set-insetdel-active");
        var emote = SeedEmote(db, channel, "7tv-ida1");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-active", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-ida1", "7tv-ida-missing"], null, Actor);

        Assert.Equal(2, result.ReportedCount);
        Assert.Null(result.UnresolvedChannel);
        var hit = Assert.Single(result.Channels);
        Assert.Equal("insetdel_active", hit.ChannelName);
        Assert.Equal(1, hit.Count);
        Assert.Equal(1, hit.NewlyChangedCount);
        Assert.Equal(["7tv-ida-missing"], hit.NotFoundIds);

        var row = await db.Emotes.AsNoTracking().SingleAsync(e => e.Id == emote.Id);
        Assert.True(row.IsArchived);
        Assert.NotNull(row.ArchivedAt);

        // Byte-identical with the set-scoped active branch it replaces (spec 5.5), so audit-row renders
        // it as before — and no paper entry next to it: the channel entry is the trail.
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-active"));
        Assert.Equal(AuditActions.EmotesSyncDeleted, audit.Action);
        Assert.Equal("insetdel_active", audit.ChannelName);
        Assert.Equal("emoteSet", audit.TargetType);
        Assert.Equal("""{"emoteCount":1,"emoteSetId":"set-insetdel-active","targetIsActiveSetOfChannel":true}""", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_SharedSet_ArchivesInBothChannels_OneEntryEach_OrderedByName()
    {
        await using var db = fixture.CreateDbContext();
        // Seeded in reverse name order, so the ordinal ordering of the answer is actually tested.
        var second = SeedChannel(db, "insetdel_shared_b", "4403", "set-insetdel-shared");
        var first = SeedChannel(db, "insetdel_shared_a", "4402", "set-insetdel-shared");
        var secondEmote = SeedEmote(db, second, "7tv-ids1");
        var firstEmote = SeedEmote(db, first, "7tv-ids1");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-shared", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-ids1"], null, Actor);

        Assert.Equal(["insetdel_shared_a", "insetdel_shared_b"], result.Channels.Select(c => c.ChannelName));
        Assert.All(result.Channels, c => Assert.Equal(1, c.Count));
        Assert.True(await db.Emotes.Where(e => e.Id == firstEmote.Id).Select(e => e.IsArchived).SingleAsync());
        Assert.True(await db.Emotes.Where(e => e.Id == secondEmote.Id).Select(e => e.IsArchived).SingleAsync());

        var audits = await AuditEntriesForSetAsync(db, "set-insetdel-shared");
        Assert.Equal(["insetdel_shared_a", "insetdel_shared_b"], audits.Select(a => a.ChannelName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_BlockedChannelWithTheSetActive_IsNeitherTouchedNorNamed()
    {
        // AK 12: a channel on Channels:ExcludedChannelIds is written nowhere any more (#252), even
        // with the reported set active — the report falls back to paper, as if nobody tracked it.
        await using var db = fixture.CreateDbContext();
        var blocked = SeedChannel(db, "insetdel_blocked", ExcludedTwitchChannelId, "set-insetdel-blocked");
        var emote = SeedEmote(db, blocked, "7tv-idb1");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-blocked", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-idb1"], null, Actor);

        Assert.Empty(result.Channels);
        Assert.Null(result.UnresolvedChannel);
        Assert.False(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-blocked"));
        Assert.Null(audit.ChannelName);
        Assert.DoesNotContain("insetdel_blocked", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_BlockedChannelAsTheExpectedOne_IsNotTracked_NeverABlockHint()
    {
        // AK 12/28: the same reason as a channel nobody ever joined — the block is not revealed.
        await using var db = fixture.CreateDbContext();
        var blocked = SeedChannel(db, "insetdel_blockedexp", ExcludedExpectedTwitchChannelId, "set-insetdel-blockedexp");
        var emote = SeedEmote(db, blocked, "7tv-idbe1");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-blockedexp", OwnerSevenTvUserId, OwnerTwitchLogin, ExcludedExpectedTwitchChannelId, ["7tv-idbe1"], "insetdel_blockedexp", Actor);

        Assert.Empty(result.Channels);
        Assert.Equal(new UnresolvedChannelDto("insetdel_blockedexp", UnresolvedChannelReasons.NotTracked), result.UnresolvedChannel);
        Assert.False(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());

        // AK 39 (N3): the blocked channel is the owner's, but step 3a does not resolve it either — the
        // entry has the same form as the one for a left channel (…_ExpectedChannelThatWasLeft_…).
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-blockedexp"));
        Assert.Null(audit.ChannelName);
        Assert.Equal(
            """{"emoteCount":1,"emoteSetId":"set-insetdel-blockedexp","targetOwnerSevenTvUserId":"owner-seven-tv-id","targetOwnerTwitchLogin":"setowner","unresolvedChannelName":"insetdel_blockedexp","unresolvedReason":"notTracked","unresolvedSevenTvEmoteIds":["7tv-idbe1"]}""",
            audit.DetailsJson);
        Assert.DoesNotContain("exclu", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_UntrackedSet_WritesOnlyThePaperEntry_WithTheOwner()
    {
        // AK 13: no channel has the set active — no row is touched, one entry without a channel that
        // audit-row renders "for <ownerLogin>" (no targetIsActiveSetOfChannel in it).
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-untracked", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-idu1", "7tv-idu2"], null, Actor);

        Assert.Equal(2, result.ReportedCount);
        Assert.Empty(result.Channels);
        Assert.Null(result.UnresolvedChannel);

        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-untracked"));
        Assert.Equal(AuditActions.EmotesSyncDeleted, audit.Action);
        Assert.Null(audit.ChannelName);
        Assert.Equal("emoteSet", audit.TargetType);
        Assert.Equal(
            """{"emoteCount":2,"emoteSetId":"set-insetdel-untracked","targetOwnerSevenTvUserId":"owner-seven-tv-id","targetOwnerTwitchLogin":"setowner"}""",
            audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_NonActiveSetOfTheOwnersTrackedChannel_IsPaperOnly_WithThatChannel_AndTouchesNoRow()
    {
        // AK 13/38 (N3): the set belongs to a tracked channel's account, but is not its active set.
        // No row is touched; the paper entry carries the owner's channel (resolved by Twitch id) with
        // targetIsActiveSetOfChannel: false and no targetOwner* fields — the pre-#253 form, so it
        // shows up in that channel's audit view again.
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetdel_nonactive", "4404", "set-insetdel-nonactive-active");
        var emote = SeedEmote(db, channel, "7tv-idn1");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-nonactive-other", OwnerSevenTvUserId, OwnerTwitchLogin, "4404", ["7tv-idn1"], null, Actor);

        Assert.Empty(result.Channels);
        Assert.Null(result.UnresolvedChannel);
        Assert.False(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-nonactive-other"));
        Assert.Equal("insetdel_nonactive", audit.ChannelName);
        Assert.Equal(
            """{"emoteCount":1,"emoteSetId":"set-insetdel-nonactive-other","targetIsActiveSetOfChannel":false}""",
            audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_NonActiveSetOfABlockedOwnerChannel_WritesThePaperEntryWithoutAChannel()
    {
        // AK 38 (N3, 5.2 step 3a): the same rule as GetActiveByTwitchChannelIdAsync — a blocked
        // owner channel is no owner channel, and the entry looks like the one of an untracked set.
        await using var db = fixture.CreateDbContext();
        SeedChannel(db, "insetdel_blockedowner", ExcludedOwnerTwitchChannelId, "set-insetdel-blockedowner-active");
        await db.SaveChangesAsync();

        await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-blockedowner-other", OwnerSevenTvUserId, OwnerTwitchLogin, ExcludedOwnerTwitchChannelId, ["7tv-idbo1"], null, Actor);

        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-blockedowner-other"));
        Assert.Null(audit.ChannelName);
        Assert.Equal(
            """{"emoteCount":1,"emoteSetId":"set-insetdel-blockedowner-other","targetOwnerSevenTvUserId":"owner-seven-tv-id","targetOwnerTwitchLogin":"setowner"}""",
            audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_NonActiveSetOfAnInactiveOwnerChannel_WritesThePaperEntryWithoutAChannel()
    {
        // AK 38: a channel that was left (IsBotActive = false) is no owner channel either.
        await using var db = fixture.CreateDbContext();
        SeedChannel(db, "insetdel_inactiveowner", "4414", "set-insetdel-inactiveowner-active", isBotActive: false);
        await db.SaveChangesAsync();

        await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-inactiveowner-other", OwnerSevenTvUserId, OwnerTwitchLogin, "4414", ["7tv-idio1"], null, Actor);

        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-inactiveowner-other"));
        Assert.Null(audit.ChannelName);
        Assert.Equal(
            """{"emoteCount":1,"emoteSetId":"set-insetdel-inactiveowner-other","targetOwnerSevenTvUserId":"owner-seven-tv-id","targetOwnerTwitchLogin":"setowner"}""",
            audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_RenamedOwnerChannel_ExpectedUnderTheOldName_NamesTheNewChannel()
    {
        // N3 edge case: step 3 misses the old name (notTracked), step 3a finds the renamed row by its
        // Twitch id — the entry carries the new name, unresolvedChannelName the old one.
        await using var db = fixture.CreateDbContext();
        SeedChannel(db, "insetdel_renamed_new", "4415", "set-insetdel-renamed-stale");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-renamed", OwnerSevenTvUserId, OwnerTwitchLogin, "4415", ["7tv-idrn1"], "insetdel_renamed_old", Actor);

        Assert.Equal(new UnresolvedChannelDto("insetdel_renamed_old", UnresolvedChannelReasons.NotTracked), result.UnresolvedChannel);
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-renamed"));
        Assert.Equal("insetdel_renamed_new", audit.ChannelName);
        Assert.Equal(
            """{"emoteCount":1,"emoteSetId":"set-insetdel-renamed","targetIsActiveSetOfChannel":false,"unresolvedChannelName":"insetdel_renamed_old","unresolvedReason":"notTracked","unresolvedSevenTvEmoteIds":["7tv-idrn1"]}""",
            audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_InactiveChannelWithTheSetActive_IsNoHit()
    {
        // IsBotActive = false: a channel that was left keeps its last ActiveEmoteSetId, but nobody
        // tracks it any more, so it is not written to.
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetdel_inactive", "4405", "set-insetdel-inactive", isBotActive: false);
        var emote = SeedEmote(db, channel, "7tv-idi1");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-inactive", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-idi1"], null, Actor);

        Assert.Empty(result.Channels);
        Assert.False(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());
        Assert.Null(Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-inactive")).ChannelName);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_DeduplicatesTheReportedIds_BeforeCounting()
    {
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetdel_dedup", "4406", "set-insetdel-dedup");
        SeedEmote(db, channel, "7tv-idd1");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-dedup", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-idd1", "7tv-idd1", "7tv-idd2", "7tv-idd2"], null, Actor);

        Assert.Equal(2, result.ReportedCount);
        var hit = Assert.Single(result.Channels);
        Assert.Equal(1, hit.Count);
        Assert.Equal(["7tv-idd2"], hit.NotFoundIds);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_HitChannelWithoutAnyMatchingRow_CountsZero_AndStillWritesThePaperEntry()
    {
        // Spec 5.5: a hit whose count is 0 writes no channel entry, so the paper entry takes over —
        // there is never a successful report without a trail (#224).
        await using var db = fixture.CreateDbContext();
        SeedChannel(db, "insetdel_norow", "4407", "set-insetdel-norow");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-norow", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-idnr1"], null, Actor);

        var hit = Assert.Single(result.Channels);
        Assert.Equal("insetdel_norow", hit.ChannelName);
        Assert.Equal(0, hit.Count);
        Assert.Equal(0, hit.NewlyChangedCount);
        Assert.Equal(["7tv-idnr1"], hit.NotFoundIds);

        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-norow"));
        Assert.Null(audit.ChannelName);
        Assert.Contains("\"emoteCount\":1", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_AlreadyArchivedRow_CountsAsArchived_AndKeepsItsDate()
    {
        // Today's semantics, per channel: the live sync usually archives first; that earlier, more
        // accurate date survives, and the row is not "newly" changed — no live event for it.
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetdel_already", "4408", "set-insetdel-already");
        var archivedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var emote = SeedEmote(db, channel, "7tv-idal1", isArchived: true, archivedAt: archivedAt);
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-already", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-idal1"], null, Actor);

        var hit = Assert.Single(result.Channels);
        Assert.Equal(1, hit.Count);
        Assert.Equal(0, hit.NewlyChangedCount);
        Assert.Equal(archivedAt, await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.ArchivedAt).SingleAsync());
        Assert.Equal("insetdel_already", Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-already")).ChannelName);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_ExpectedChannelWithAnotherActiveSet_IsActiveSetDiffers_WithoutTouchingItsRows()
    {
        // AK 28 / F13: the stored ActiveEmoteSetId lags a set switch on 7TV — the expected channel is
        // named back with its reason, its rows stay as they are, and the paper entry carries the
        // channel and the reported ids.
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetdel_lagging", "4409", "set-insetdel-lagging-stale");
        var emote = SeedEmote(db, channel, "7tv-idl1");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-lagging-new", OwnerSevenTvUserId, OwnerTwitchLogin, "4409", ["7tv-idl1", "7tv-idl1"], "insetdel_lagging", Actor);

        Assert.Empty(result.Channels);
        Assert.Equal(new UnresolvedChannelDto("insetdel_lagging", UnresolvedChannelReasons.ActiveSetDiffers), result.UnresolvedChannel);
        Assert.False(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());

        // AK 39 (N3): the lagging channel is the owner's channel (active, unblocked), so the paper
        // entry names it — with targetIsActiveSetOfChannel: false, which is what our database says
        // right now — plus the three unresolved* fields, and no targetOwner* fields.
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-lagging-new"));
        Assert.Equal("insetdel_lagging", audit.ChannelName);
        Assert.Equal(
            """{"emoteCount":1,"emoteSetId":"set-insetdel-lagging-new","targetIsActiveSetOfChannel":false,"unresolvedChannelName":"insetdel_lagging","unresolvedReason":"activeSetDiffers","unresolvedSevenTvEmoteIds":["7tv-idl1"]}""",
            audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_ExpectedChannelThatDoesNotExist_IsNotTracked()
    {
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-missing", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-idm1"], "insetdel_nosuchchannel", Actor);

        Assert.Empty(result.Channels);
        Assert.Equal(new UnresolvedChannelDto("insetdel_nosuchchannel", UnresolvedChannelReasons.NotTracked), result.UnresolvedChannel);
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-missing"));
        Assert.Contains("\"unresolvedChannelName\":\"insetdel_nosuchchannel\"", audit.DetailsJson);
        Assert.Contains("\"unresolvedReason\":\"notTracked\"", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_ExpectedChannelThatWasLeft_IsNotTracked_EvenWithTheSetStillStoredAsActive()
    {
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetdel_left", "4410", "set-insetdel-left", isBotActive: false);
        var emote = SeedEmote(db, channel, "7tv-idle1");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-left", OwnerSevenTvUserId, OwnerTwitchLogin, "4410", ["7tv-idle1"], "insetdel_left", Actor);

        Assert.Empty(result.Channels);
        Assert.Equal(new UnresolvedChannelDto("insetdel_left", UnresolvedChannelReasons.NotTracked), result.UnresolvedChannel);
        Assert.False(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());

        // AK 39 (N3): a left owner channel is no owner channel — the entry has no channel, the
        // targetOwner* fields and the three unresolved* fields.
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-left"));
        Assert.Null(audit.ChannelName);
        Assert.Equal(
            """{"emoteCount":1,"emoteSetId":"set-insetdel-left","targetOwnerSevenTvUserId":"owner-seven-tv-id","targetOwnerTwitchLogin":"setowner","unresolvedChannelName":"insetdel_left","unresolvedReason":"notTracked","unresolvedSevenTvEmoteIds":["7tv-idle1"]}""",
            audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_ExpectedChannelThatIsHit_ResolvesIt_AndNormalizesTheName()
    {
        // Regel 9: a client may send the login as typed ("HandOfBlood"); the hit is still recognized.
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetdel_expected", "4411", "set-insetdel-expected");
        SeedEmote(db, channel, "7tv-idx1");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-expected", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-idx1"], " InSetDel_Expected ", Actor);

        Assert.Null(result.UnresolvedChannel);
        Assert.Equal("insetdel_expected", Assert.Single(result.Channels).ChannelName);
        Assert.Equal("insetdel_expected", Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-expected")).ChannelName);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_HitAndUnresolvedExpectedChannel_WritesTheChannelEntry_AndThePaperEntry()
    {
        // A shared set whose other channel lags (spec 7): the hit is written as usual, and the missed
        // expected channel still leaves its own trace.
        await using var db = fixture.CreateDbContext();
        var hitChannel = SeedChannel(db, "insetdel_mixhit", "4412", "set-insetdel-mix");
        var laggingChannel = SeedChannel(db, "insetdel_mixlag", "4413", "set-insetdel-mix-stale");
        SeedEmote(db, hitChannel, "7tv-idmx1");
        var laggingEmote = SeedEmote(db, laggingChannel, "7tv-idmx1");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-mix", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-idmx1"], "insetdel_mixlag", Actor);

        Assert.Equal("insetdel_mixhit", Assert.Single(result.Channels).ChannelName);
        Assert.Equal(UnresolvedChannelReasons.ActiveSetDiffers, result.UnresolvedChannel?.Reason);
        Assert.False(await db.Emotes.Where(e => e.Id == laggingEmote.Id).Select(e => e.IsArchived).SingleAsync());

        var audits = await AuditEntriesForSetAsync(db, "set-insetdel-mix");
        Assert.Equal(2, audits.Count);
        Assert.Contains(audits, a => a.ChannelName == "insetdel_mixhit");
        Assert.Contains(audits, a => a.ChannelName is null && a.DetailsJson!.Contains("\"unresolvedChannelName\":\"insetdel_mixlag\""));
    }

    [Fact]
    public async Task MarkRestoredInSetAsync_ActiveSetOfATrackedChannel_UnarchivesItsRows_AndAuditsWithTheChannel()
    {
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetres_active", "4421", "set-insetres-active");
        var archived = SeedEmote(db, channel, "7tv-ira1", isArchived: true, archivedAt: DateTime.UtcNow.AddMinutes(-5));
        // Already back (the live sync won the race): counts as restored, but not as newly changed.
        SeedEmote(db, channel, "7tv-ira2");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkRestoredInSetAsync(
            "set-insetres-active", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-ira1", "7tv-ira2", "7tv-ira3"], null, Actor);

        Assert.Equal(3, result.ReportedCount);
        Assert.Null(result.UnresolvedChannel);
        var hit = Assert.Single(result.Channels);
        Assert.Equal(("insetres_active", 2, 1), (hit.ChannelName, hit.Count, hit.NewlyChangedCount));
        Assert.Equal(["7tv-ira3"], hit.NotFoundIds);

        var row = await db.Emotes.AsNoTracking().SingleAsync(e => e.Id == archived.Id);
        Assert.False(row.IsArchived);
        Assert.Null(row.ArchivedAt);

        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetres-active"));
        Assert.Equal(AuditActions.EmotesSyncRestored, audit.Action);
        Assert.Equal("insetres_active", audit.ChannelName);
        Assert.Equal("""{"emoteCount":2,"emoteSetId":"set-insetres-active","targetIsActiveSetOfChannel":true}""", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkRestoredInSetAsync_SharedSet_UnarchivesInBothChannels_OneEntryEach()
    {
        await using var db = fixture.CreateDbContext();
        var first = SeedChannel(db, "insetres_shared_a", "4422", "set-insetres-shared");
        var second = SeedChannel(db, "insetres_shared_b", "4423", "set-insetres-shared");
        var firstEmote = SeedEmote(db, first, "7tv-irs1", isArchived: true, archivedAt: DateTime.UtcNow.AddMinutes(-5));
        var secondEmote = SeedEmote(db, second, "7tv-irs1", isArchived: true, archivedAt: DateTime.UtcNow.AddMinutes(-5));
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkRestoredInSetAsync(
            "set-insetres-shared", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-irs1"], null, Actor);

        Assert.Equal(["insetres_shared_a", "insetres_shared_b"], result.Channels.Select(c => c.ChannelName));
        Assert.False(await db.Emotes.Where(e => e.Id == firstEmote.Id).Select(e => e.IsArchived).SingleAsync());
        Assert.False(await db.Emotes.Where(e => e.Id == secondEmote.Id).Select(e => e.IsArchived).SingleAsync());
        var audits = await AuditEntriesForSetAsync(db, "set-insetres-shared");
        Assert.Equal(["insetres_shared_a", "insetres_shared_b"], audits.Select(a => a.ChannelName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MarkRestoredInSetAsync_UntrackedSet_WritesOnlyThePaperEntry_WithTheOwner()
    {
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db).MarkRestoredInSetAsync(
            "set-insetres-untracked", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-iru1"], null, Actor);

        Assert.Empty(result.Channels);
        Assert.Null(result.UnresolvedChannel);
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetres-untracked"));
        Assert.Equal(AuditActions.EmotesSyncRestored, audit.Action);
        Assert.Null(audit.ChannelName);
        Assert.Equal(
            """{"emoteCount":1,"emoteSetId":"set-insetres-untracked","targetOwnerSevenTvUserId":"owner-seven-tv-id","targetOwnerTwitchLogin":"setowner"}""",
            audit.DetailsJson);
    }

    [Fact]
    public async Task MarkRestoredInSetAsync_NonActiveSetOfTheOwnersTrackedChannel_IsListedInThatChannelsAuditLog()
    {
        // AK 38 (N3, finding B2): the paper entry of a restore into a non-active set of the owner's
        // own tracked channel is found by the channel-scoped audit query, which filters exactly on
        // ChannelName — the first version of the report wrote it without a channel, and it vanished
        // from GET /api/channels/{c}/audit-log.
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetres_nonactive", "4426", "set-insetres-nonactive-active");
        var emote = SeedEmote(db, channel, "7tv-irn2", isArchived: true, archivedAt: DateTime.UtcNow.AddMinutes(-5));
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkRestoredInSetAsync(
            "set-insetres-nonactive-other", OwnerSevenTvUserId, OwnerTwitchLogin, "4426", ["7tv-irn2", "7tv-irn3"], null, Actor);

        Assert.Empty(result.Channels);
        Assert.True(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, "insetres_nonactive", null));
        var dto = Assert.Single(page.Items);
        Assert.Equal(AuditActions.EmotesSyncRestored, dto.Action);
        Assert.Equal("insetres_nonactive", dto.ChannelName);
        Assert.Equal(2, dto.Detail!.Count);
        Assert.Equal(new AuditLogTargetEmoteSet("set-insetres-nonactive-other", false, null), dto.Detail.TargetEmoteSet);
    }

    [Fact]
    public async Task MarkRestoredInSetAsync_ExpectedChannelWithAnotherActiveSet_IsActiveSetDiffers_WithoutTouchingItsRows()
    {
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetres_lagging", "4424", "set-insetres-lagging-stale");
        var emote = SeedEmote(db, channel, "7tv-irl1", isArchived: true, archivedAt: DateTime.UtcNow.AddMinutes(-5));
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkRestoredInSetAsync(
            "set-insetres-lagging-new", OwnerSevenTvUserId, OwnerTwitchLogin, "4424", ["7tv-irl1"], "insetres_lagging", Actor);

        Assert.Empty(result.Channels);
        Assert.Equal(new UnresolvedChannelDto("insetres_lagging", UnresolvedChannelReasons.ActiveSetDiffers), result.UnresolvedChannel);
        Assert.True(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetres-lagging-new"));
        Assert.Equal("insetres_lagging", audit.ChannelName);
        Assert.Contains("\"targetIsActiveSetOfChannel\":false", audit.DetailsJson);
        Assert.DoesNotContain("targetOwner", audit.DetailsJson);
        Assert.Contains("\"unresolvedReason\":\"activeSetDiffers\"", audit.DetailsJson);
        Assert.Contains("\"unresolvedSevenTvEmoteIds\":[\"7tv-irl1\"]", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkRestoredInSetAsync_BlockedExpectedChannel_IsNotTracked_WithoutTouchingItsRows()
    {
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetres_blocked", ExcludedRestoreTwitchChannelId, "set-insetres-blocked");
        var emote = SeedEmote(db, channel, "7tv-irb1", isArchived: true, archivedAt: DateTime.UtcNow.AddMinutes(-5));
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkRestoredInSetAsync(
            "set-insetres-blocked", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-irb1"], "insetres_blocked", Actor);

        Assert.Empty(result.Channels);
        Assert.Equal(new UnresolvedChannelDto("insetres_blocked", UnresolvedChannelReasons.NotTracked), result.UnresolvedChannel);
        Assert.True(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task MarkRestoredInSetAsync_DeduplicatesTheReportedIds_BeforeCounting()
    {
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db).MarkRestoredInSetAsync(
            "set-insetres-dedup", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-ird1", "7tv-ird1"], null, Actor);

        Assert.Equal(1, result.ReportedCount);
        Assert.Contains("\"emoteCount\":1", Assert.Single(await AuditEntriesForSetAsync(db, "set-insetres-dedup")).DetailsJson);
    }

    [Fact]
    public async Task MarkRestoredInSetAsync_HitChannelWithoutAnyMatchingRow_CountsZero_AndStillWritesThePaperEntry()
    {
        await using var db = fixture.CreateDbContext();
        SeedChannel(db, "insetres_norow", "4425", "set-insetres-norow");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkRestoredInSetAsync(
            "set-insetres-norow", OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ["7tv-irn1"], null, Actor);

        var hit = Assert.Single(result.Channels);
        Assert.Equal((0, 0), (hit.Count, hit.NewlyChangedCount));
        Assert.Equal(["7tv-irn1"], hit.NotFoundIds);
        Assert.Null(Assert.Single(await AuditEntriesForSetAsync(db, "set-insetres-norow")).ChannelName);
    }

    private static EmoteService CreateService(AppDbContext db, ILogger<EmoteService>? logger = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Channels:ExcludedChannelIds"] =
                    $"{ExcludedTwitchChannelId},{ExcludedExpectedTwitchChannelId},{ExcludedRestoreTwitchChannelId},{ExcludedOwnerTwitchChannelId}",
            })
            .Build();
        var excludedChannelFilter = new ExcludedChannelFilter(configuration, NullLogger<ExcludedChannelFilter>.Instance);

        return new EmoteService(db, logger ?? NullLogger<EmoteService>.Instance, excludedChannelFilter);
    }

    private static Channel SeedChannel(AppDbContext db, string channelName, string twitchChannelId, string activeEmoteSetId, bool isBotActive = true)
    {
        var channel = new Channel
        {
            ChannelName = channelName,
            TwitchChannelId = twitchChannelId,
            ActiveEmoteSetId = activeEmoteSetId,
            IsBotActive = isBotActive,
        };
        db.Channels.Add(channel);
        return channel;
    }

    private static Emote SeedEmote(AppDbContext db, Channel channel, string sevenTvEmoteId, bool isArchived = false, DateTime? archivedAt = null)
    {
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Channel = channel,
            Name = sevenTvEmoteId,
            SevenTvEmoteId = sevenTvEmoteId,
            ImageUrl = $"https://cdn/{sevenTvEmoteId}",
            IsArchived = isArchived,
            ArchivedAt = archivedAt,
        };
        db.Emotes.Add(emote);
        return emote;
    }

    // Tracked on purpose, like every other audit query in this class: the entries were added through
    // this same context, so the identity map hands back the DetailsJson exactly as the service
    // serialized it — a no-tracking read would return Postgres' jsonb rendering (spacing, key order).
    private static Task<List<AuditLogEntry>> AuditEntriesForSetAsync(AppDbContext db, string emoteSetId) =>
        db.AuditLogEntries
            .Where(a => a.TargetType == "emoteSet" && a.TargetId == emoteSetId)
            .ToListAsync();
}
