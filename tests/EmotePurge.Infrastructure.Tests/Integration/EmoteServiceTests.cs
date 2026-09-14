using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
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

        var service = new EmoteService(db);
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

        var service = new EmoteService(db);
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
        var service = new EmoteService(db);
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

        var service = new EmoteService(db);
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

        var service = new EmoteService(db);
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

        var service = new EmoteService(db);
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

        var service = new EmoteService(db);
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

        var service = new EmoteService(db);
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

        var service = new EmoteService(db);
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

        var service = new EmoteService(db);
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

        var service = new EmoteService(db);
        await service.MarkImportedAsync("syncimporttest_c", ["7tv-ic1", "7tv-ic1"], null, "file", null, Actor);

        var audit = await db.AuditLogEntries.SingleAsync(a =>
            a.ChannelName == "syncimporttest_c" && a.Action == AuditActions.EmotesSyncImported);
        Assert.Contains("\"emoteCount\":1", audit.DetailsJson);
    }

    [Fact]
    public async Task MarkImportedAsync_ReportsUnknownChannel_AndWritesNoAuditRow()
    {
        await using var db = fixture.CreateDbContext();

        var service = new EmoteService(db);
        var written = await service.MarkImportedAsync("syncimporttest_unknown", ["7tv-id1"], null, "channel", null, Actor);

        Assert.False(written);
        Assert.Equal(0, await db.AuditLogEntries.CountAsync(a => a.ChannelName == "syncimporttest_unknown"));
    }
}
