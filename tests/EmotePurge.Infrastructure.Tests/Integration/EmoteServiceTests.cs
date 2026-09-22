using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class EmoteServiceTests(PostgresFixture fixture)
{
    private static readonly AuditActor Actor = new("100", "synctester");

    [Fact]
    public async Task MarkDeletedAsync_ArchivesActiveEmotes_AndReportsThemAsArchived()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_a", TwitchChannelId = "4001", ActiveEmoteSetId = "set-a" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "PogU", SevenTvEmoteId = "7tv-a1", ImageUrl = "https://cdn/a1" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        var result = await service.MarkDeletedAsync("syncdeletetest_a", [emote.Id], Actor);

        Assert.Equal(1, result.ArchivedCount);
        // Drives the channel.synced live event in the endpoint: this call really changed state.
        Assert.Equal(1, result.NewlyArchivedCount);
        Assert.Empty(result.NotFoundIds);
        Assert.True(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncdeletetest_a" && a.Action == AuditActions.EmotesSyncDeleted);
        Assert.Contains("\"emoteCount\":1", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedAsync_CountsAlreadyArchivedEmoteAsArchived()
    {
        // The realistic race since the EventAPI live sync: the worker archives the emote off the
        // 7TV dispatch seconds before the frontend's bookkeeping call arrives. That call must see
        // "goal state reached", not "not found" — the old behavior made every successful delete
        // look like a failed sync in the UI.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_b", TwitchChannelId = "4002", ActiveEmoteSetId = "set-b" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "KEKW", SevenTvEmoteId = "7tv-b1", ImageUrl = "https://cdn/b1", IsArchived = true };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        var result = await service.MarkDeletedAsync("syncdeletetest_b", [emote.Id], Actor);

        Assert.Equal(1, result.ArchivedCount);
        Assert.Empty(result.NotFoundIds);
        // Goal state was already reached, so no rows changed — no live event. The audit row IS
        // written regardless: the user's delete on 7TV happened, and with the live sync usually
        // archiving first, gating the paper trail on this race made real deletes invisible.
        Assert.Equal(0, result.NewlyArchivedCount);
        Assert.Equal(1, await db.AuditLogEntries.CountAsync(a =>
            a.ChannelName == "syncdeletetest_b" && a.Action == AuditActions.EmotesSyncDeleted));
    }

    [Fact]
    public async Task MarkDeletedAsync_StampsArchivedAt_ForNewlyArchivedEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_d", TwitchChannelId = "4005", ActiveEmoteSetId = "set-d" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "Stamp", SevenTvEmoteId = "7tv-d1", ImageUrl = "https://cdn/d1" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        await service.MarkDeletedAsync("syncdeletetest_d", [emote.Id], Actor);

        var archivedAt = await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.ArchivedAt).SingleAsync();
        Assert.NotNull(archivedAt);
        Assert.True(archivedAt >= before);
    }

    [Fact]
    public async Task MarkDeletedAsync_LeavesTheArchiveDateOfAnAlreadyArchivedEmoteAlone()
    {
        // The live sync usually archives first (with the accurate timestamp); this later
        // bookkeeping call counts the row as archived but must not overwrite the earlier date.
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

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
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

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        var result = await service.MarkDeletedAsync("syncdeletetest_c", [foreignEmote.Id, "does-not-exist"], Actor);

        Assert.Equal(0, result.ArchivedCount);
        Assert.Equal(0, result.NewlyArchivedCount);
        Assert.Equal(2, result.NotFoundIds.Count);
        // The foreign channel's emote stays untouched.
        Assert.False(await db.Emotes.Where(e => e.Id == foreignEmote.Id).Select(e => e.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task MarkRestoredAsync_UnarchivesEmotes_ClearsTheArchiveDate_AndWritesAnAuditRow()
    {
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

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        var result = await service.MarkRestoredAsync("syncrestoretest_a", [emote.Id], Actor);

        Assert.Equal(1, result.RestoredCount);
        // Drives the channel.synced live event in the endpoint: this call really changed state.
        Assert.Equal(1, result.NewlyRestoredCount);
        Assert.Empty(result.NotFoundIds);

        var row = await db.Emotes.Where(e => e.Id == emote.Id).Select(e => new { e.IsArchived, e.ArchivedAt }).SingleAsync();
        Assert.False(row.IsArchived);
        // Active again means the archive date is meaningless — same clearing UpsertEmote does.
        Assert.Null(row.ArchivedAt);

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncrestoretest_a" && a.Action == AuditActions.EmotesSyncRestored);
        Assert.Contains("\"emoteCount\":1", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkRestoredAsync_CountsAnAlreadyActiveEmoteAsRestored_AndStillAudits()
    {
        // The realistic race, mirrored from the delete: the EventAPI live sync un-archives the
        // emote off the 7TV ADD dispatch before this bookkeeping call arrives. Goal state reached
        // → counted, no live event — but the restore happened, so the paper trail is written.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncrestoretest_b", TwitchChannelId = "4102", ActiveEmoteSetId = "set-rb" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "Alive", SevenTvEmoteId = "7tv-rb1", ImageUrl = "https://cdn/rb1" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        var result = await service.MarkRestoredAsync("syncrestoretest_b", [emote.Id], Actor);

        Assert.Equal(1, result.RestoredCount);
        Assert.Equal(0, result.NewlyRestoredCount);
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

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        var result = await service.MarkRestoredAsync("syncrestoretest_c", [foreignEmote.Id, "does-not-exist"], Actor);

        Assert.Equal(0, result.RestoredCount);
        Assert.Equal(0, result.NewlyRestoredCount);
        Assert.Equal(2, result.NotFoundIds.Count);
        // The foreign channel's emote stays archived, and a call that matched nothing is no event.
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

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
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

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
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

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        await service.MarkImportedAsync("syncimporttest_c", ["7tv-ic1", "7tv-ic1"], null, "file", null, Actor);

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncimporttest_c" && a.Action == AuditActions.EmotesSyncImported);
        Assert.Contains("\"emoteCount\":1", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkImportedAsync_ReportsUnknownChannel_AndWritesNoAuditRow()
    {
        await using var db = fixture.CreateDbContext();

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
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

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
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

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
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

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
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

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
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

    // The set-scoped overloads below (spec 6.6, T5.2): the new sync-deleted/sync-restored body form.

    [Fact]
    public async Task MarkDeletedAsync_SetScoped_ActiveSet_ArchivesBySevenTvId_AndAuditsSetDetails()
    {
        // The precision claim of E1 in the spec's service section: matching by (ChannelId,
        // SevenTvEmoteId) instead of Emote.Id finds the same row, and this is the only identity a
        // live-only member (one the grid never saw a UsageStat for) even has.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_seta", TwitchChannelId = "4301", ActiveEmoteSetId = "set-active-a" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "SetScoped", SevenTvEmoteId = "7tv-seta1", ImageUrl = "https://cdn/seta1" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        var result = await service.MarkDeletedAsync("syncdeletetest_seta", "set-active-a", ["7tv-seta1"], Actor);

        Assert.Equal(1, result.ArchivedCount);
        Assert.Equal(1, result.NewlyArchivedCount);
        Assert.Empty(result.NotFoundIds);
        Assert.True(result.TargetIsActiveSetOfChannel);
        Assert.True(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncdeletetest_seta" && a.Action == AuditActions.EmotesSyncDeleted);
        Assert.Equal("emoteSet", audit.TargetType);
        Assert.Equal("set-active-a", audit.TargetId);
        Assert.Contains("\"emoteCount\":1", audit.DetailsJson);
        Assert.Contains("\"emoteSetId\":\"set-active-a\"", audit.DetailsJson);
        Assert.Contains("\"targetIsActiveSetOfChannel\":true", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedAsync_SetScoped_NonActiveSet_WritesPaperOnly_NoRowChanged()
    {
        // Spec 6.6's paper case: a non-active set has no Emote row to match against, so nothing is
        // archived — only the audit trail records that the report happened.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_setb", TwitchChannelId = "4302", ActiveEmoteSetId = "set-active-b" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "Untouched", SevenTvEmoteId = "7tv-setb1", ImageUrl = "https://cdn/setb1" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        var result = await service.MarkDeletedAsync("syncdeletetest_setb", "set-other-b", ["7tv-setb1"], Actor);

        Assert.Equal(0, result.ArchivedCount);
        Assert.Equal(0, result.NewlyArchivedCount);
        Assert.Empty(result.NotFoundIds);
        Assert.False(result.TargetIsActiveSetOfChannel);
        Assert.False(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncdeletetest_setb" && a.Action == AuditActions.EmotesSyncDeleted);
        Assert.Equal("emoteSet", audit.TargetType);
        Assert.Equal("set-other-b", audit.TargetId);
        Assert.Contains("\"emoteCount\":1", audit.DetailsJson);
        Assert.Contains("\"targetIsActiveSetOfChannel\":false", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedAsync_SetScoped_NonActiveSet_DeduplicatesSevenTvEmoteIds_BeforeCountingEmoteCount()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_dedup", TwitchChannelId = "4303", ActiveEmoteSetId = "set-active-dedup" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        await service.MarkDeletedAsync("syncdeletetest_dedup", "set-other-dedup", ["7tv-dup1", "7tv-dup1"], Actor);

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncdeletetest_dedup" && a.Action == AuditActions.EmotesSyncDeleted);
        Assert.Contains("\"emoteCount\":1", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedAsync_LegacyForm_LogsLegacyBodyFormUsage()
    {
        // E3: the log line that lets Folge-Issue 1 measure, rather than guess, when the legacy body
        // form is safe to retire.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_legacylog", TwitchChannelId = "4304", ActiveEmoteSetId = "set-legacylog" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "Legacy", SevenTvEmoteId = "7tv-legacylog1", ImageUrl = "https://cdn/legacylog1" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var logger = new RecordingLogger<EmoteService>();
        var service = new EmoteService(db, logger);
        await service.MarkDeletedAsync("syncdeletetest_legacylog", [emote.Id], Actor);

        Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information
                && e.Message.Contains("sync-deleted", StringComparison.Ordinal)
                && e.Message.Contains("legacy body form", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarkRestoredAsync_SetScoped_ActiveSet_UnarchivesBySevenTvId_AndAuditsSetDetails()
    {
        // Mirror of the set-scoped MarkDeletedAsync case above, in the restore direction.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncrestoretest_seta", TwitchChannelId = "4305", ActiveEmoteSetId = "set-ractive-a" };
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Channel = channel,
            Name = "SetScopedBack",
            SevenTvEmoteId = "7tv-rseta1",
            ImageUrl = "https://cdn/rseta1",
            IsArchived = true,
            ArchivedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        var result = await service.MarkRestoredAsync("syncrestoretest_seta", "set-ractive-a", ["7tv-rseta1"], Actor);

        Assert.Equal(1, result.RestoredCount);
        Assert.Equal(1, result.NewlyRestoredCount);
        Assert.Empty(result.NotFoundIds);
        Assert.True(result.TargetIsActiveSetOfChannel);

        var row = await db.Emotes.Where(e => e.Id == emote.Id).Select(e => new { e.IsArchived, e.ArchivedAt }).SingleAsync();
        Assert.False(row.IsArchived);
        Assert.Null(row.ArchivedAt);

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncrestoretest_seta" && a.Action == AuditActions.EmotesSyncRestored);
        Assert.Equal("emoteSet", audit.TargetType);
        Assert.Equal("set-ractive-a", audit.TargetId);
        Assert.Contains("\"targetIsActiveSetOfChannel\":true", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkRestoredAsync_SetScoped_NonActiveSet_WritesPaperOnly_NoRowChanged()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncrestoretest_setb", TwitchChannelId = "4306", ActiveEmoteSetId = "set-ractive-b" };
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Channel = channel,
            Name = "StaysArchived",
            SevenTvEmoteId = "7tv-rsetb1",
            ImageUrl = "https://cdn/rsetb1",
            IsArchived = true,
            ArchivedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = new EmoteService(db, NullLogger<EmoteService>.Instance);
        var result = await service.MarkRestoredAsync("syncrestoretest_setb", "set-other-b", ["7tv-rsetb1"], Actor);

        Assert.Equal(0, result.RestoredCount);
        Assert.Equal(0, result.NewlyRestoredCount);
        Assert.Empty(result.NotFoundIds);
        Assert.False(result.TargetIsActiveSetOfChannel);
        Assert.True(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncrestoretest_setb" && a.Action == AuditActions.EmotesSyncRestored);
        Assert.Equal("emoteSet", audit.TargetType);
        Assert.Equal("set-other-b", audit.TargetId);
        Assert.Contains("\"targetIsActiveSetOfChannel\":false", audit.DetailsJson);
    }
}
