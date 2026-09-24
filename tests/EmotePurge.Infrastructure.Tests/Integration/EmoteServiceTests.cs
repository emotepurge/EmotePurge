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

    // The Twitch ids on the block list of every EmoteService built here (F10/E8): a real
    // ExcludedChannelFilter read from configuration, the way ExcludedChannelFilterTests builds one.
    private const string ExcludedTwitchChannelId = "4490";
    private const string ExcludedExpectedTwitchChannelId = "4491";
    private const string ExcludedRestoreTwitchChannelId = "4492";

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

        var service = CreateService(db);
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

        var service = CreateService(db);
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
        var service = CreateService(db);
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

        var service = CreateService(db);
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

        var service = CreateService(db);
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

        var service = CreateService(db);
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

        var service = CreateService(db);
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

        var service = CreateService(db);
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

        var service = CreateService(db);
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
        var service = CreateService(db, logger);
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

        var service = CreateService(db);
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

        var service = CreateService(db);
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
            "set-insetdel-active", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-ida1", "7tv-ida-missing"], null, Actor);

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
            "set-insetdel-shared", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-ids1"], null, Actor);

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
            "set-insetdel-blocked", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idb1"], null, Actor);

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
            "set-insetdel-blockedexp", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idbe1"], "insetdel_blockedexp", Actor);

        Assert.Empty(result.Channels);
        Assert.Equal(new UnresolvedChannelDto("insetdel_blockedexp", UnresolvedChannelReasons.NotTracked), result.UnresolvedChannel);
        Assert.False(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-blockedexp"));
        Assert.Contains("\"unresolvedReason\":\"notTracked\"", audit.DetailsJson);
        Assert.DoesNotContain("exclu", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_UntrackedSet_WritesOnlyThePaperEntry_WithTheOwner()
    {
        // AK 13: no channel has the set active — no row is touched, one entry without a channel that
        // audit-row renders "for <ownerLogin>" (no targetIsActiveSetOfChannel in it).
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-untracked", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idu1", "7tv-idu2"], null, Actor);

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
    public async Task MarkDeletedInSetAsync_NonActiveSetOfATrackedChannel_IsPaperOnly_AndTouchesNoRow()
    {
        // AK 13: the set belongs to a tracked channel's account, but is not its active set.
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetdel_nonactive", "4404", "set-insetdel-nonactive-active");
        var emote = SeedEmote(db, channel, "7tv-idn1");
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-nonactive-other", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idn1"], null, Actor);

        Assert.Empty(result.Channels);
        Assert.False(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-nonactive-other"));
        Assert.Null(audit.ChannelName);
        Assert.Contains("\"targetOwnerTwitchLogin\":\"setowner\"", audit.DetailsJson);
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
            "set-insetdel-inactive", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idi1"], null, Actor);

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
            "set-insetdel-dedup", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idd1", "7tv-idd1", "7tv-idd2", "7tv-idd2"], null, Actor);

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
            "set-insetdel-norow", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idnr1"], null, Actor);

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
            "set-insetdel-already", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idal1"], null, Actor);

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
            "set-insetdel-lagging-new", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idl1", "7tv-idl1"], "insetdel_lagging", Actor);

        Assert.Empty(result.Channels);
        Assert.Equal(new UnresolvedChannelDto("insetdel_lagging", UnresolvedChannelReasons.ActiveSetDiffers), result.UnresolvedChannel);
        Assert.False(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());

        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetdel-lagging-new"));
        Assert.Null(audit.ChannelName);
        Assert.Equal(
            """{"emoteCount":1,"emoteSetId":"set-insetdel-lagging-new","targetOwnerSevenTvUserId":"owner-seven-tv-id","targetOwnerTwitchLogin":"setowner","unresolvedChannelName":"insetdel_lagging","unresolvedReason":"activeSetDiffers","unresolvedSevenTvEmoteIds":["7tv-idl1"]}""",
            audit.DetailsJson);
    }

    [Fact]
    public async Task MarkDeletedInSetAsync_ExpectedChannelThatDoesNotExist_IsNotTracked()
    {
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db).MarkDeletedInSetAsync(
            "set-insetdel-missing", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idm1"], "insetdel_nosuchchannel", Actor);

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
            "set-insetdel-left", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idle1"], "insetdel_left", Actor);

        Assert.Empty(result.Channels);
        Assert.Equal(new UnresolvedChannelDto("insetdel_left", UnresolvedChannelReasons.NotTracked), result.UnresolvedChannel);
        Assert.False(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());
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
            "set-insetdel-expected", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idx1"], " InSetDel_Expected ", Actor);

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
            "set-insetdel-mix", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-idmx1"], "insetdel_mixlag", Actor);

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
            "set-insetres-active", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-ira1", "7tv-ira2", "7tv-ira3"], null, Actor);

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
            "set-insetres-shared", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-irs1"], null, Actor);

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
            "set-insetres-untracked", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-iru1"], null, Actor);

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
    public async Task MarkRestoredInSetAsync_ExpectedChannelWithAnotherActiveSet_IsActiveSetDiffers_WithoutTouchingItsRows()
    {
        await using var db = fixture.CreateDbContext();
        var channel = SeedChannel(db, "insetres_lagging", "4424", "set-insetres-lagging-stale");
        var emote = SeedEmote(db, channel, "7tv-irl1", isArchived: true, archivedAt: DateTime.UtcNow.AddMinutes(-5));
        await db.SaveChangesAsync();

        var result = await CreateService(db).MarkRestoredInSetAsync(
            "set-insetres-lagging-new", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-irl1"], "insetres_lagging", Actor);

        Assert.Empty(result.Channels);
        Assert.Equal(new UnresolvedChannelDto("insetres_lagging", UnresolvedChannelReasons.ActiveSetDiffers), result.UnresolvedChannel);
        Assert.True(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());
        var audit = Assert.Single(await AuditEntriesForSetAsync(db, "set-insetres-lagging-new"));
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
            "set-insetres-blocked", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-irb1"], "insetres_blocked", Actor);

        Assert.Empty(result.Channels);
        Assert.Equal(new UnresolvedChannelDto("insetres_blocked", UnresolvedChannelReasons.NotTracked), result.UnresolvedChannel);
        Assert.True(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task MarkRestoredInSetAsync_DeduplicatesTheReportedIds_BeforeCounting()
    {
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db).MarkRestoredInSetAsync(
            "set-insetres-dedup", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-ird1", "7tv-ird1"], null, Actor);

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
            "set-insetres-norow", OwnerSevenTvUserId, OwnerTwitchLogin, ["7tv-irn1"], null, Actor);

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
                    $"{ExcludedTwitchChannelId},{ExcludedExpectedTwitchChannelId},{ExcludedRestoreTwitchChannelId}",
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
