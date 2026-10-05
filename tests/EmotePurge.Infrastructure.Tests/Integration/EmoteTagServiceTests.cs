using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// EmoteTagService (#201 T-B) against real Postgres: the unique index, the FK cascades and the channel
// row lock are the database's, and the races below are only meaningful against real row locks.
[Collection("Postgres")]
public class EmoteTagServiceTests(PostgresFixture fixture)
{
    private const string ActiveSetId = "01JACTIVESET0000000000000A";
    private const string OtherSetId = "01JOTHERSET00000000000000B";

    private static readonly AuditActor Actor = new("201", "tagmod");

    // Whole seconds, so values that round-trip through Postgres (microseconds) compare exactly.
    private static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    // ---- Create ----------------------------------------------------------------------------------

    [Fact]
    public async Task Create_StoresTheTrimmedName_AndItsNormalizedForm()
    {
        var channel = await SeedChannelAsync("tagcreate");

        var result = await CreateAsync("TagCreate", "  Funny Ones ");

        Assert.Equal(EmoteTagMutationStatus.Ok, result.Status);
        Assert.NotNull(result.Tag);
        Assert.Equal("Funny Ones", result.Tag.Name);

        await using var verify = fixture.CreateDbContext();
        var stored = await verify.EmoteTags.SingleAsync(t => t.ChannelId == channel.Id);
        Assert.Equal(result.Tag.Id, stored.Id);
        Assert.Equal("Funny Ones", stored.Name);
        Assert.Equal("funny ones", stored.NormalizedName);

        var audit = Assert.Single(await LoadAuditAsync("tagcreate"));
        Assert.Equal(AuditActions.TagCreate, audit.Action);
        Assert.Equal("emoteTag", audit.TargetType);
        Assert.Equal(stored.Id.ToString(CultureInfo.InvariantCulture), audit.TargetId);
        Assert.Equal(Details(("tagId", stored.Id)), ParseDetails(audit.DetailsJson));
        Assert.Equal(Actor.Login, audit.ActorLogin);
    }

    [Theory]
    [InlineData("tagduplower", "funny")]
    [InlineData("tagdupupper", "FUNNY")]
    [InlineData("tagduppadded", "  Funny  ")]
    public async Task Create_ANameTheChannelAlreadyHasInAnyCasingOrPadding_IsNameTaken(string channelName, string duplicate)
    {
        var channel = await SeedChannelAsync(channelName);
        await SeedTagAsync(channel.Id, "Funny");

        var result = await CreateAsync(channel.ChannelName, duplicate);

        Assert.Equal(EmoteTagMutationStatus.NameTaken, result.Status);
        Assert.Null(result.Tag);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.EmoteTags.CountAsync(t => t.ChannelId == channel.Id));
        Assert.Empty(await LoadAuditAsync(channel.ChannelName));
    }

    [Fact]
    public async Task Create_TheSameNameInAnotherChannel_IsFine()
    {
        var first = await SeedChannelAsync("tagothera");
        await SeedTagAsync(first.Id, "Funny");
        await SeedChannelAsync("tagotherb");

        var result = await CreateAsync("tagotherb", "Funny");

        Assert.Equal(EmoteTagMutationStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Create_FortyCharactersAreFine_FortyOneAreNameInvalid()
    {
        await SeedChannelAsync("taglength");

        Assert.Equal(EmoteTagMutationStatus.Ok, (await CreateAsync("taglength", new string('a', 40))).Status);
        Assert.Equal(EmoteTagMutationStatus.NameInvalid, (await CreateAsync("taglength", new string('b', 41))).Status);
    }

    [Theory]
    [InlineData("tagunfitnull", null)]
    [InlineData("tagunfitempty", "")]
    [InlineData("tagunfitblank", "   ")]
    [InlineData("tagunfittab", "tab\there")]
    [InlineData("tagunfitnewline", "line\nbreak")]
    [InlineData("tagunfitnul", "nul\0char")]
    public async Task Create_AnUnfitName_IsNameInvalid_AndWritesNothing(string channelName, string? name)
    {
        var channel = await SeedChannelAsync(channelName);

        var result = await CreateAsync(channel.ChannelName, name);

        Assert.Equal(EmoteTagMutationStatus.NameInvalid, result.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTags.AnyAsync(t => t.ChannelId == channel.Id));
        Assert.Empty(await LoadAuditAsync(channel.ChannelName));
    }

    [Fact]
    public async Task Create_TheFiftiethTagIsFine_TheFiftyFirstIsLimitReached()
    {
        var channel = await SeedChannelAsync("taglimit");
        await SeedTagsAsync(channel.Id, EmoteTagLimits.MaxTagsPerChannel - 1);

        Assert.Equal(EmoteTagMutationStatus.Ok, (await CreateAsync("taglimit", "fiftieth")).Status);
        var over = await CreateAsync("taglimit", "fifty-first");

        Assert.Equal(EmoteTagMutationStatus.LimitReached, over.Status);
        Assert.Null(over.Tag);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(EmoteTagLimits.MaxTagsPerChannel, await verify.EmoteTags.CountAsync(t => t.ChannelId == channel.Id));
    }

    [Fact]
    public async Task Create_InAnUntrackedChannel_IsChannelNotFound()
    {
        var result = await CreateAsync("tagnosuchchannel", "Funny");

        Assert.Equal(EmoteTagMutationStatus.ChannelNotFound, result.Status);
        Assert.Empty(await LoadAuditAsync("tagnosuchchannel"));
    }

    // ---- Rename ----------------------------------------------------------------------------------

    [Fact]
    public async Task Rename_ToItsOwnNameInAnotherCasing_IsAllowed_AndChangesTheDisplayName()
    {
        var channel = await SeedChannelAsync("tagrecase");
        var tag = await SeedTagAsync(channel.Id, "funny");

        var result = await RenameAsync("tagrecase", tag.Id, " FUNNY ");

        Assert.Equal(EmoteTagMutationStatus.Ok, result.Status);
        Assert.Equal(new EmoteTagDto(tag.Id, "FUNNY"), result.Tag);
        await using var verify = fixture.CreateDbContext();
        var stored = await verify.EmoteTags.SingleAsync(t => t.Id == tag.Id);
        Assert.Equal("FUNNY", stored.Name);
        Assert.Equal("funny", stored.NormalizedName);

        var audit = Assert.Single(await LoadAuditAsync("tagrecase"));
        Assert.Equal(AuditActions.TagRename, audit.Action);
        Assert.Equal(tag.Id.ToString(CultureInfo.InvariantCulture), audit.TargetId);
        Assert.Equal(Details(("tagId", tag.Id)), ParseDetails(audit.DetailsJson));
    }

    [Fact]
    public async Task Rename_ToANewName_StoresBothForms()
    {
        var channel = await SeedChannelAsync("tagrename");
        var tag = await SeedTagAsync(channel.Id, "Funny");

        var result = await RenameAsync("tagrename", tag.Id, "Sad");

        Assert.Equal(EmoteTagMutationStatus.Ok, result.Status);
        await using var verify = fixture.CreateDbContext();
        var stored = await verify.EmoteTags.SingleAsync(t => t.Id == tag.Id);
        Assert.Equal(("Sad", "sad"), (stored.Name, stored.NormalizedName));
    }

    [Fact]
    public async Task Rename_ToTheExactSameName_IsOk_WithoutAnAuditEntry()
    {
        var channel = await SeedChannelAsync("tagrenamesame");
        var tag = await SeedTagAsync(channel.Id, "Funny");

        var result = await RenameAsync("tagrenamesame", tag.Id, " Funny ");

        Assert.Equal(EmoteTagMutationStatus.Ok, result.Status);
        Assert.Equal(new EmoteTagDto(tag.Id, "Funny"), result.Tag);
        Assert.Empty(await LoadAuditAsync("tagrenamesame"));
    }

    [Fact]
    public async Task Rename_ToAnotherTagsName_IsNameTaken()
    {
        var channel = await SeedChannelAsync("tagrenametaken");
        var tag = await SeedTagAsync(channel.Id, "Funny");
        await SeedTagAsync(channel.Id, "Sad");

        var result = await RenameAsync("tagrenametaken", tag.Id, "SAD");

        Assert.Equal(EmoteTagMutationStatus.NameTaken, result.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("Funny", (await verify.EmoteTags.SingleAsync(t => t.Id == tag.Id)).Name);
        Assert.Empty(await LoadAuditAsync("tagrenametaken"));
    }

    [Fact]
    public async Task Rename_ToAnUnfitName_IsNameInvalid()
    {
        var channel = await SeedChannelAsync("tagrenameunfit");
        var tag = await SeedTagAsync(channel.Id, "Funny");

        Assert.Equal(EmoteTagMutationStatus.NameInvalid, (await RenameAsync("tagrenameunfit", tag.Id, new string('x', 41))).Status);
        Assert.Equal(EmoteTagMutationStatus.NameInvalid, (await RenameAsync("tagrenameunfit", tag.Id, null)).Status);
    }

    [Fact]
    public async Task Rename_AnUnknownTag_OrATagOfAnotherChannel_IsTagNotFound()
    {
        await SeedChannelAsync("tagrenamemine");
        var other = await SeedChannelAsync("tagrenameforeign");
        var foreignTag = await SeedTagAsync(other.Id, "Theirs");

        Assert.Equal(EmoteTagMutationStatus.TagNotFound, (await RenameAsync("tagrenamemine", foreignTag.Id, "Mine")).Status);
        Assert.Equal(EmoteTagMutationStatus.TagNotFound, (await RenameAsync("tagrenamemine", long.MaxValue, "Mine")).Status);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal("Theirs", (await verify.EmoteTags.SingleAsync(t => t.Id == foreignTag.Id)).Name);
    }

    [Fact]
    public async Task Rename_InAnUntrackedChannel_IsChannelNotFound() =>
        Assert.Equal(EmoteTagMutationStatus.ChannelNotFound, (await RenameAsync("tagrenamenochannel", 1, "Funny")).Status);

    // ---- Delete ----------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheTagItsEntriesAndPlacements_LeavesASiblingTag_AndAuditsIdAndCounts()
    {
        var channel = await SeedChannelAsync("tagdelete");
        var tag = await SeedTagAsync(channel.Id, "Funny");
        var sibling = await SeedTagAsync(channel.Id, "Sad");
        await SeedEntriesAsync(tag.Id, 3);
        await SeedEntriesAsync(sibling.Id, 2);
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        await SeedPlayInAsync(tag.Id, ActiveSetId, T0, x);

        var status = await DeleteAsync("tagdelete", tag.Id);

        Assert.Equal(EmoteTagMutationStatus.Ok, status);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTags.AnyAsync(t => t.Id == tag.Id));
        Assert.False(await verify.EmoteTagEntries.AnyAsync(e => e.TagId == tag.Id));
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == tag.Id));
        Assert.True(await verify.EmoteTags.AnyAsync(t => t.Id == sibling.Id));
        Assert.Equal(2, await verify.EmoteTagEntries.CountAsync(e => e.TagId == sibling.Id));

        var audit = Assert.Single(await LoadAuditAsync("tagdelete"));
        Assert.Equal(AuditActions.TagDelete, audit.Action);
        Assert.Equal("emoteTag", audit.TargetType);
        Assert.Equal(tag.Id.ToString(CultureInfo.InvariantCulture), audit.TargetId);
        Assert.Equal(Details(("tagId", tag.Id), ("entryCount", 4), ("placementCount", 1)), ParseDetails(audit.DetailsJson));
    }

    [Fact]
    public async Task Delete_AnUnknownTag_OrATagOfAnotherChannel_IsTagNotFound_AndLeavesItAlone()
    {
        await SeedChannelAsync("tagdeletemine");
        var other = await SeedChannelAsync("tagdeleteforeign");
        var foreignTag = await SeedTagAsync(other.Id, "Theirs");

        Assert.Equal(EmoteTagMutationStatus.TagNotFound, await DeleteAsync("tagdeletemine", foreignTag.Id));
        Assert.Equal(EmoteTagMutationStatus.TagNotFound, await DeleteAsync("tagdeletemine", long.MaxValue));
        Assert.Equal(EmoteTagMutationStatus.ChannelNotFound, await DeleteAsync("tagdeletenochannel", foreignTag.Id));

        await using var verify = fixture.CreateDbContext();
        Assert.True(await verify.EmoteTags.AnyAsync(t => t.Id == foreignTag.Id));
        Assert.Empty(await LoadAuditAsync("tagdeletemine"));
    }

    [Fact]
    public async Task EveryMutation_AuditsWithoutTheTagName()
    {
        // E30: a tag name is free text that can name a person; the actor-bound audit row must not keep
        // a copy of it. One assertion over every audited mutation, so a new detail field cannot slip a
        // name in through one of them.
        await SeedChannelAsync("tagauditnames");
        var created = await CreateAsync("tagauditnames", "SecretAlpha");
        Assert.NotNull(created.Tag);
        await RenameAsync("tagauditnames", created.Tag.Id, "SecretBravo");
        await DeleteAsync("tagauditnames", created.Tag.Id);

        var entries = await LoadAuditAsync("tagauditnames");
        Assert.Equal([AuditActions.TagCreate, AuditActions.TagRename, AuditActions.TagDelete], entries.Select(e => e.Action));
        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain("secret", entry.DetailsJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("name", entry.DetailsJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ---- AddEntries ------------------------------------------------------------------------------

    [Fact]
    public async Task AddEntries_SnapshotsAliasAndImageFromTheRow_AndSkipsIdsWithoutAnUnarchivedRow()
    {
        var channel = await SeedChannelAsync("tagadd");
        var tag = await SeedTagAsync(channel.Id, "Funny");
        var kekw = await SeedEmoteAsync(channel.Id, "KEKW");
        var archived = await SeedEmoteAsync(channel.Id, "Gone", isArchived: true);
        var unknown = NewSevenTvId();
        var pog = await SeedEmoteAsync(channel.Id, "PogU");

        var result = await AddAsync("tagadd", tag.Id, [unknown, kekw.SevenTvEmoteId, archived.SevenTvEmoteId, pog.SevenTvEmoteId]);

        Assert.Equal(EmoteTagAddEntriesStatus.Ok, result.Status);
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(0, result.AlreadyTaggedCount);
        // Request order, not storage order.
        Assert.Equal([unknown, archived.SevenTvEmoteId], result.SkippedNotInSetIds);

        await using var verify = fixture.CreateDbContext();
        var entries = await verify.EmoteTagEntries.Where(e => e.TagId == tag.Id).OrderBy(e => e.Alias).ToListAsync();
        Assert.Equal(
            [(kekw.SevenTvEmoteId, "KEKW", kekw.ImageUrl), (pog.SevenTvEmoteId, "PogU", pog.ImageUrl)],
            entries.Select(e => (e.SevenTvEmoteId, e.Alias, e.ImageUrl)));
        Assert.All(entries, e => Assert.True(e.AddedAtUtc > DateTime.UtcNow.AddMinutes(-5)));

        // Assigning is not audited (spec 6.3).
        Assert.Empty(await LoadAuditAsync("tagadd"));
    }

    [Fact]
    public async Task AddEntries_CountsAlreadyTaggedEmotes_AndIgnoresDuplicatesInTheRequest()
    {
        var channel = await SeedChannelAsync("tagaddagain");
        var tag = await SeedTagAsync(channel.Id, "Funny");
        var kekw = await SeedEmoteAsync(channel.Id, "KEKW");
        var pog = await SeedEmoteAsync(channel.Id, "PogU");
        Assert.Equal(1, (await AddAsync("tagaddagain", tag.Id, [kekw.SevenTvEmoteId])).AddedCount);

        var result = await AddAsync("tagaddagain", tag.Id, [kekw.SevenTvEmoteId, pog.SevenTvEmoteId, pog.SevenTvEmoteId, kekw.SevenTvEmoteId]);

        Assert.Equal(EmoteTagAddEntriesStatus.Ok, result.Status);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.AlreadyTaggedCount);
        Assert.Empty(result.SkippedNotInSetIds);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(2, await verify.EmoteTagEntries.CountAsync(e => e.TagId == tag.Id));
    }

    [Fact]
    public async Task AddEntries_TheThousandthEntryIsFine_TheThousandAndFirstIsEntryLimitReached_WithNothingWritten()
    {
        var channel = await SeedChannelAsync("tagentrylimit");
        var tag = await SeedTagAsync(channel.Id, "Big");
        await SeedEntriesAsync(tag.Id, EmoteTagLimits.MaxEntriesPerTag - 1);
        var thousandth = await SeedEmoteAsync(channel.Id, "Thousandth");
        var over = await SeedEmoteAsync(channel.Id, "Over");
        var overToo = await SeedEmoteAsync(channel.Id, "OverToo");

        var ok = await AddAsync("tagentrylimit", tag.Id, [thousandth.SevenTvEmoteId]);
        Assert.Equal(EmoteTagAddEntriesStatus.Ok, ok.Status);
        Assert.Equal(1, ok.AddedCount);

        // An id already tagged does not count towards the limit; the two new ones do, and either alone
        // would exceed it — so nothing at all is written.
        var result = await AddAsync("tagentrylimit", tag.Id, [thousandth.SevenTvEmoteId, over.SevenTvEmoteId, overToo.SevenTvEmoteId]);

        Assert.Equal(EmoteTagAddEntriesStatus.EntryLimitReached, result.Status);
        Assert.Equal(0, result.AddedCount);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(EmoteTagLimits.MaxEntriesPerTag, await verify.EmoteTagEntries.CountAsync(e => e.TagId == tag.Id));
        Assert.False(await verify.EmoteTagEntries.AnyAsync(e => e.SevenTvEmoteId == over.SevenTvEmoteId || e.SevenTvEmoteId == overToo.SevenTvEmoteId));
    }

    [Fact]
    public async Task AddEntries_AtTheLimit_AnAlreadyTaggedOrSkippedIdIsStillOk()
    {
        // The limit counts what would be written, not what was asked for.
        var channel = await SeedChannelAsync("tagentrylimitnoop");
        var tag = await SeedTagAsync(channel.Id, "Full");
        await SeedEntriesAsync(tag.Id, EmoteTagLimits.MaxEntriesPerTag - 1);
        var last = await SeedEmoteAsync(channel.Id, "Last");
        await AddAsync("tagentrylimitnoop", tag.Id, [last.SevenTvEmoteId]);

        var result = await AddAsync("tagentrylimitnoop", tag.Id, [last.SevenTvEmoteId, NewSevenTvId()]);

        Assert.Equal(EmoteTagAddEntriesStatus.Ok, result.Status);
        Assert.Equal((0, 1, 1), (result.AddedCount, result.AlreadyTaggedCount, result.SkippedNotInSetIds.Count));
    }

    [Fact]
    public async Task AddEntries_WithoutIds_IsEmoteIdsEmpty()
    {
        var channel = await SeedChannelAsync("tagaddempty");
        var tag = await SeedTagAsync(channel.Id, "Funny");

        Assert.Equal(EmoteTagAddEntriesStatus.EmoteIdsEmpty, (await AddAsync("tagaddempty", tag.Id, [])).Status);
        Assert.Equal(EmoteTagAddEntriesStatus.EmoteIdsEmpty, (await AddAsync("tagaddempty", tag.Id, null)).Status);
    }

    [Theory]
    [InlineData("tagaddunfitempty", "")]
    [InlineData("tagaddunfitspace", "has space")]
    [InlineData("tagaddunfitpath", "../etc")]
    [InlineData("tagaddunfitquote", "quote\"")]
    [InlineData("tagaddunfitnewline", "trailingnewline\n")]
    [InlineData("tagaddunfitlong", "A23456789012345678901234567890123")] // 33 characters
    [InlineData("tagaddunfitnull", null)]
    public async Task AddEntries_WithAnUnfitId_IsEmoteIdsInvalid_AndWritesNothing(string channelName, string? unfit)
    {
        var channel = await SeedChannelAsync(channelName);
        var tag = await SeedTagAsync(channel.Id, "Funny");
        var kekw = await SeedEmoteAsync(channel.Id, "KEKW");

        var result = await AddAsync(channel.ChannelName, tag.Id, [kekw.SevenTvEmoteId, unfit!]);

        Assert.Equal(EmoteTagAddEntriesStatus.EmoteIdsInvalid, result.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagEntries.AnyAsync(e => e.TagId == tag.Id));
    }

    [Fact]
    public async Task EntryRequests_AreCappedAtTwiceTheEntryLimitInRawIds()
    {
        var channel = await SeedChannelAsync("tagrequestcap");
        var tag = await SeedTagAsync(channel.Id, "Funny");
        var id = NewSevenTvId();
        var atCap = Enumerable.Repeat(id, EmoteTagLimits.MaxIdsPerRequest).ToList();
        var overCap = Enumerable.Repeat(id, EmoteTagLimits.MaxIdsPerRequest + 1).ToList();

        // Exactly the cap passes (duplicates collapse, the id has no row, so it is merely skipped).
        var add = await AddAsync("tagrequestcap", tag.Id, atCap);
        Assert.Equal(EmoteTagAddEntriesStatus.Ok, add.Status);
        Assert.Equal([id], add.SkippedNotInSetIds);
        Assert.Equal(EmoteTagRemoveEntriesStatus.Ok, (await RemoveAsync("tagrequestcap", tag.Id, atCap)).Status);

        Assert.Equal(EmoteTagAddEntriesStatus.EmoteIdsInvalid, (await AddAsync("tagrequestcap", tag.Id, overCap)).Status);
        Assert.Equal(EmoteTagRemoveEntriesStatus.EmoteIdsInvalid, (await RemoveAsync("tagrequestcap", tag.Id, overCap)).Status);
    }

    [Fact]
    public async Task AddEntries_ToAnUnknownTag_OrATagOfAnotherChannel_IsTagNotFound()
    {
        var mine = await SeedChannelAsync("tagaddmine");
        var kekw = await SeedEmoteAsync(mine.Id, "KEKW");
        var other = await SeedChannelAsync("tagaddforeign");
        var foreignTag = await SeedTagAsync(other.Id, "Theirs");

        Assert.Equal(EmoteTagAddEntriesStatus.TagNotFound, (await AddAsync("tagaddmine", foreignTag.Id, [kekw.SevenTvEmoteId])).Status);
        Assert.Equal(EmoteTagAddEntriesStatus.TagNotFound, (await AddAsync("tagaddmine", long.MaxValue, [kekw.SevenTvEmoteId])).Status);
        Assert.Equal(EmoteTagAddEntriesStatus.ChannelNotFound, (await AddAsync("tagaddnochannel", foreignTag.Id, [kekw.SevenTvEmoteId])).Status);

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagEntries.AnyAsync(e => e.TagId == foreignTag.Id));
    }

    // ---- RemoveEntries ---------------------------------------------------------------------------

    [Fact]
    public async Task RemoveEntries_CountsOnlyWhatWasDeleted_AndToleratesUnknownIds()
    {
        var channel = await SeedChannelAsync("tagremove");
        var tag = await SeedTagAsync(channel.Id, "Funny");
        var sibling = await SeedTagAsync(channel.Id, "Sad");
        var kekw = await SeedEmoteAsync(channel.Id, "KEKW");
        var pog = await SeedEmoteAsync(channel.Id, "PogU");
        await AddAsync("tagremove", tag.Id, [kekw.SevenTvEmoteId, pog.SevenTvEmoteId]);
        await AddAsync("tagremove", sibling.Id, [kekw.SevenTvEmoteId]);

        var result = await RemoveAsync("tagremove", tag.Id, [kekw.SevenTvEmoteId, NewSevenTvId(), kekw.SevenTvEmoteId]);

        Assert.Equal(new EmoteTagRemoveEntriesResult(EmoteTagRemoveEntriesStatus.Ok, 1), result);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal([pog.SevenTvEmoteId], await verify.EmoteTagEntries.Where(e => e.TagId == tag.Id).Select(e => e.SevenTvEmoteId).ToListAsync());
        // The same emote in another tag is untouched.
        Assert.True(await verify.EmoteTagEntries.AnyAsync(e => e.TagId == sibling.Id && e.SevenTvEmoteId == kekw.SevenTvEmoteId));
        Assert.Empty(await LoadAuditAsync("tagremove"));
    }

    [Fact]
    public async Task RemoveEntries_RejectsMissingOrUnfitIds_AndUnknownTagsOrChannels()
    {
        var channel = await SeedChannelAsync("tagremovestatus");
        var tag = await SeedTagAsync(channel.Id, "Funny");
        var other = await SeedChannelAsync("tagremoveforeign");
        var foreignTag = await SeedTagAsync(other.Id, "Theirs");
        var id = NewSevenTvId();

        Assert.Equal(EmoteTagRemoveEntriesStatus.EmoteIdsEmpty, (await RemoveAsync("tagremovestatus", tag.Id, [])).Status);
        Assert.Equal(EmoteTagRemoveEntriesStatus.EmoteIdsEmpty, (await RemoveAsync("tagremovestatus", tag.Id, null)).Status);
        Assert.Equal(EmoteTagRemoveEntriesStatus.EmoteIdsInvalid, (await RemoveAsync("tagremovestatus", tag.Id, [id, "no/slash"])).Status);
        Assert.Equal(EmoteTagRemoveEntriesStatus.TagNotFound, (await RemoveAsync("tagremovestatus", foreignTag.Id, [id])).Status);
        Assert.Equal(EmoteTagRemoveEntriesStatus.TagNotFound, (await RemoveAsync("tagremovestatus", long.MaxValue, [id])).Status);
        Assert.Equal(EmoteTagRemoveEntriesStatus.ChannelNotFound, (await RemoveAsync("tagremovenochannel", tag.Id, [id])).Status);
    }

    // ---- Reads -----------------------------------------------------------------------------------

    [Fact]
    public async Task List_CountsEntriesAndThoseInTheActiveSet_InCreationOrder()
    {
        var channel = await SeedChannelAsync("taglist");
        var older = await SeedTagAsync(channel.Id, "Older", DateTime.UtcNow.AddHours(-2));
        var newer = await SeedTagAsync(channel.Id, "Newer", DateTime.UtcNow.AddHours(-1));
        var empty = await SeedTagAsync(channel.Id, "Empty", DateTime.UtcNow);
        var kekw = await SeedEmoteAsync(channel.Id, "KEKW");
        var pog = await SeedEmoteAsync(channel.Id, "PogU");
        await AddAsync("taglist", older.Id, [kekw.SevenTvEmoteId, pog.SevenTvEmoteId]);
        await AddAsync("taglist", newer.Id, [kekw.SevenTvEmoteId]);
        // pog leaves the set; one more entry whose emote was never in the channel.
        await ArchiveAsync(pog.Id);
        await SeedEntriesAsync(newer.Id, 1);

        var result = await CreateService(fixture.CreateDbContext()).ListAsync("TagList", null);

        Assert.Equal(EmoteTagListStatus.Ok, result.Status);
        Assert.Equal(ActiveSetId, result.EmoteSetId);
        Assert.True(result.IsActiveSet);
        Assert.Equal(
            [
                new EmoteTagSummaryDto(older.Id, "Older", 2, 1, 0, false, null),
                new EmoteTagSummaryDto(newer.Id, "Newer", 2, 1, 0, false, null),
                new EmoteTagSummaryDto(empty.Id, "Empty", 0, 0, 0, false, null)
            ],
            result.Tags);
    }

    [Fact]
    public async Task List_NamingTheActiveSet_IsTheActiveSet_AnotherSetHasNoInSetCount()
    {
        var channel = await SeedChannelAsync("taglistset");
        var tag = await SeedTagAsync(channel.Id, "Funny");
        var kekw = await SeedEmoteAsync(channel.Id, "KEKW");
        await AddAsync("taglistset", tag.Id, [kekw.SevenTvEmoteId]);

        var service = CreateService(fixture.CreateDbContext());
        var active = await service.ListAsync("taglistset", ActiveSetId);
        var other = await service.ListAsync("taglistset", OtherSetId);

        Assert.Equal((ActiveSetId, true), (active.EmoteSetId, active.IsActiveSet));
        Assert.Equal(new EmoteTagSummaryDto(tag.Id, "Funny", 1, 1, 0, false, null), Assert.Single(active.Tags));
        Assert.Equal((OtherSetId, false), (other.EmoteSetId, other.IsActiveSet));
        Assert.Equal(new EmoteTagSummaryDto(tag.Id, "Funny", 1, null, 0, false, null), Assert.Single(other.Tags));
    }

    [Fact]
    public async Task List_InAChannelWithoutAnActiveSet_HasNoSet()
    {
        var channel = await SeedChannelAsync("taglistnoset", activeEmoteSetId: string.Empty);
        var tag = await SeedTagAsync(channel.Id, "Funny");
        await SeedEntriesAsync(tag.Id, 2);

        var result = await CreateService(fixture.CreateDbContext()).ListAsync("taglistnoset", null);

        Assert.Equal(EmoteTagListStatus.Ok, result.Status);
        Assert.Null(result.EmoteSetId);
        Assert.False(result.IsActiveSet);
        Assert.Equal(new EmoteTagSummaryDto(tag.Id, "Funny", 2, null, 0, false, null), Assert.Single(result.Tags));
    }

    [Fact]
    public async Task List_AnUntrackedChannel_IsChannelNotFound()
    {
        var result = await CreateService(fixture.CreateDbContext()).ListAsync("taglistnochannel", null);

        Assert.Equal(EmoteTagListStatus.ChannelNotFound, result.Status);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public async Task ListEntries_InAddedOrder_WithTheCurrentNameOnlyForEmotesInTheSet()
    {
        var channel = await SeedChannelAsync("tagentries");
        var tag = await SeedTagAsync(channel.Id, "Funny");
        var kekw = await SeedEmoteAsync(channel.Id, "KEKW");
        var pog = await SeedEmoteAsync(channel.Id, "PogU");
        var first = await SeedEntryAsync(tag.Id, kekw.SevenTvEmoteId, "KEKW", DateTime.UtcNow.AddMinutes(-3));
        // Same timestamp twice: the 7TV id breaks the tie, so the order is deterministic.
        var tieAt = DateTime.UtcNow.AddMinutes(-2);
        var tieB = await SeedEntryAsync(tag.Id, "01JZZZZZZZZZZZZZZZZZZZZZZZ", "NeverHere", tieAt);
        var tieA = await SeedEntryAsync(tag.Id, pog.SevenTvEmoteId, "PogUOld", tieAt);
        // kekw was renamed in 7TV since it was tagged; pog left the set.
        await RenameEmoteAsync(kekw.Id, "KEKWait");
        await ArchiveAsync(pog.Id);

        var result = await CreateService(fixture.CreateDbContext()).ListEntriesAsync("tagentries", tag.Id, null);

        Assert.Equal(EmoteTagEntriesStatus.Ok, result.Status);
        Assert.Equal((ActiveSetId, true), (result.EmoteSetId, result.IsActiveSet));
        var tieOrder = string.CompareOrdinal(tieA.SevenTvEmoteId, tieB.SevenTvEmoteId) < 0
            ? new[] { tieA, tieB }
            : [tieB, tieA];
        Assert.Equal(
            new[] { first, tieOrder[0], tieOrder[1] }.Select(e => e.SevenTvEmoteId),
            result.Entries.Select(e => e.SevenTvEmoteId));
        var head = result.Entries[0];
        Assert.Equal(
            (kekw.SevenTvEmoteId, "KEKW", first.ImageUrl, (bool?)true, (string?)"KEKWait"),
            (head.SevenTvEmoteId, head.Alias, head.ImageUrl, head.InSet, head.CurrentName));
        Assert.All(result.Entries.Skip(1), e => Assert.Equal((false, (string?)null), (e.InSet, e.CurrentName)));
    }

    [Fact]
    public async Task ListEntries_ForAnotherSet_HasNoInSetStatus()
    {
        var channel = await SeedChannelAsync("tagentriesset");
        var tag = await SeedTagAsync(channel.Id, "Funny");
        var kekw = await SeedEmoteAsync(channel.Id, "KEKW");
        await AddAsync("tagentriesset", tag.Id, [kekw.SevenTvEmoteId]);

        var result = await CreateService(fixture.CreateDbContext()).ListEntriesAsync("tagentriesset", tag.Id, OtherSetId);

        Assert.Equal((OtherSetId, false), (result.EmoteSetId, result.IsActiveSet));
        var entry = Assert.Single(result.Entries);
        Assert.Null(entry.InSet);
        Assert.Null(entry.CurrentName);
        Assert.Equal("KEKW", entry.Alias);
    }

    [Fact]
    public async Task ListEntries_AnUnknownTag_OrATagOfAnotherChannel_IsTagNotFound()
    {
        await SeedChannelAsync("tagentriesmine");
        var other = await SeedChannelAsync("tagentriesforeign");
        var foreignTag = await SeedTagAsync(other.Id, "Theirs");
        await SeedEntriesAsync(foreignTag.Id, 1);
        var service = CreateService(fixture.CreateDbContext());

        var foreign = await service.ListEntriesAsync("tagentriesmine", foreignTag.Id, null);
        Assert.Equal(EmoteTagEntriesStatus.TagNotFound, foreign.Status);
        Assert.Empty(foreign.Entries);
        Assert.Equal(EmoteTagEntriesStatus.TagNotFound, (await service.ListEntriesAsync("tagentriesmine", long.MaxValue, null)).Status);
        Assert.Equal(EmoteTagEntriesStatus.ChannelNotFound, (await service.ListEntriesAsync("tagentriesnochannel", foreignTag.Id, null)).Status);
    }

    // ---- Placements, activations and the read-time rule (T-C) ------------------------------------
    //
    // Spec 5.5 rule 5 / E33 rev. 4: a placement holds unless the channel has a leave observation for
    // the same emote and set that is later than the placement's own RegisteredAtUtc (the registration
    // of the operation that last wrote it — F30). Fixed timestamps (whole seconds) so stored and
    // expected values compare exactly.

    [Theory]
    [InlineData("tagrtrlater", 60, false)]
    [InlineData("tagrtrsame", 0, true)]
    [InlineData("tagrtrearlier", -60, true)]
    public async Task ReadTimeRule_AnObservationLaterThanTheRegistration_HidesThePlacement_AnEarlierOneChangesNothing(
        string channelName, int observedSecondsAfterRegistration, bool holds)
    {
        var channel = await SeedChannelAsync(channelName);
        var mine = await SeedTagAsync(channel.Id, "Mine", T0.AddHours(-2));
        var other = await SeedTagAsync(channel.Id, "Other", T0.AddHours(-1));
        var x = NewSevenTvId();
        var untouched = NewSevenTvId();
        await SeedEntryAsync(mine.Id, x, "X", T0);
        await SeedEntryAsync(mine.Id, untouched, "Untouched", T0);
        await SeedEntryAsync(other.Id, x, "X", T0);
        var operation = await SeedPlayInAsync(mine.Id, ActiveSetId, T0, x, untouched);
        await SeedPlayInAsync(other.Id, ActiveSetId, T0, x);
        await SeedObservationAsync(channel.Id, x, ActiveSetId, T0.AddSeconds(observedSecondsAfterRegistration));

        var service = CreateService(fixture.CreateDbContext());
        var list = await service.ListAsync(channelName, null);
        var mineEntries = await service.ListEntriesAsync(channelName, mine.Id, null);
        var otherEntries = await service.ListEntriesAsync(channelName, other.Id, null);

        Assert.Equal([holds ? 2 : 1, holds ? 1 : 0], list.Tags.Select(t => t.PlacedCount));
        var mineX = mineEntries.Entries.Single(e => e.SevenTvEmoteId == x);
        Assert.Equal(holds, mineX.PlacedByThisTag);
        Assert.Equal(holds ? T0 : (DateTime?)null, mineX.PlacedAtUtc);
        Assert.Equal(holds ? operation : (Guid?)null, mineX.PlacementOperationId);
        Assert.Equal(holds ? new[] { new EmoteTagRefDto(other.Id, "Other") } : [], mineX.PlacedByOtherTags);
        Assert.Equal(holds ? new[] { new EmoteTagRefDto(mine.Id, "Mine") } : [],
            Assert.Single(otherEntries.Entries).PlacedByOtherTags);
        // The observation names X only: the other emote of the same run is untouched either way.
        var mineUntouched = mineEntries.Entries.Single(e => e.SevenTvEmoteId == untouched);
        Assert.Equal((true, (DateTime?)T0, (Guid?)operation),
            (mineUntouched.PlacedByThisTag, mineUntouched.PlacedAtUtc, mineUntouched.PlacementOperationId));
        // Observations never touch the activation.
        Assert.All(list.Tags, t => Assert.True(t.Active));
    }

    [Fact]
    public async Task ReadTimeRule_IsPerSet_AnObservationInAnotherSetChangesNothing()
    {
        var channel = await SeedChannelAsync("tagrtrperset");
        var tag = await SeedTagAsync(channel.Id, "Mine");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var operation = await SeedPlayInAsync(tag.Id, ActiveSetId, T0, x);
        await SeedObservationAsync(channel.Id, x, OtherSetId, T0.AddHours(1));

        var service = CreateService(fixture.CreateDbContext());
        var entry = Assert.Single((await service.ListEntriesAsync("tagrtrperset", tag.Id, null)).Entries);

        Assert.Equal((true, (Guid?)operation), (entry.PlacedByThisTag, entry.PlacementOperationId));
        Assert.Equal(1, Assert.Single((await service.ListAsync("tagrtrperset", null)).Tags).PlacedCount);
    }

    [Fact]
    public async Task ReadTimeRule_IsPerChannel_AnObservationInAnotherChannelChangesNothing()
    {
        var channel = await SeedChannelAsync("tagrtrperchannel");
        var neighbour = await SeedChannelAsync("tagrtrperchannel2");
        var tag = await SeedTagAsync(channel.Id, "Mine");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var operation = await SeedPlayInAsync(tag.Id, ActiveSetId, T0, x);
        // Same emote, same set, later — but recorded for another channel.
        await SeedObservationAsync(neighbour.Id, x, ActiveSetId, T0.AddHours(1));

        var service = CreateService(fixture.CreateDbContext());
        var entry = Assert.Single((await service.ListEntriesAsync("tagrtrperchannel", tag.Id, null)).Entries);

        Assert.Equal((true, (Guid?)operation), (entry.PlacedByThisTag, entry.PlacementOperationId));
        Assert.Equal(1, Assert.Single((await service.ListAsync("tagrtrperchannel", null)).Tags).PlacedCount);
    }

    // A transfer points another tag's placement at the removing tag's operation, and operations
    // cascade with their tag — so the operation row can be gone while the placement is still valid.
    [Fact]
    public async Task ReadTimeRule_APlacementWhoseOperationIsGone_StillHolds_WithoutALaterObservation()
    {
        var channel = await SeedChannelAsync("tagrtrnoop");
        var tag = await SeedTagAsync(channel.Id, "Mine");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var missingOperation = Guid.NewGuid();
        await SeedPlacementAsync(tag.Id, x, ActiveSetId, missingOperation, T0, T0);
        await SeedObservationAsync(channel.Id, x, ActiveSetId, T0.AddMinutes(-1));

        var service = CreateService(fixture.CreateDbContext());
        var entry = Assert.Single((await service.ListEntriesAsync("tagrtrnoop", tag.Id, null)).Entries);

        Assert.Equal((true, (DateTime?)T0, (Guid?)missingOperation), (entry.PlacedByThisTag, entry.PlacedAtUtc, entry.PlacementOperationId));
        Assert.Equal(1, Assert.Single((await service.ListAsync("tagrtrnoop", null)).Tags).PlacedCount);
    }

    // The anchor is the placement's own column, not its operation's: with the two disagreeing, an
    // observation between them follows the placement.
    [Theory]
    [InlineData("tagrtranchorlater", 60, true)]
    [InlineData("tagrtranchorearlier", -60, false)]
    public async Task ReadTimeRule_ComparesAgainstThePlacementsOwnRegistration_NotItsOperations(
        string channelName, int placementAnchorMinutesFromOperation, bool holds)
    {
        var channel = await SeedChannelAsync(channelName);
        var tag = await SeedTagAsync(channel.Id, "Mine");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var operation = await SeedOperationAsync(tag.Id, ActiveSetId, T0);
        await SeedPlacementAsync(tag.Id, x, ActiveSetId, operation, T0, T0.AddMinutes(placementAnchorMinutesFromOperation));
        // Halfway between the operation's registration and the placement's anchor.
        await SeedObservationAsync(channel.Id, x, ActiveSetId, T0.AddMinutes(placementAnchorMinutesFromOperation / 2.0));

        var service = CreateService(fixture.CreateDbContext());
        var entry = Assert.Single((await service.ListEntriesAsync(channelName, tag.Id, null)).Entries);

        Assert.Equal(holds, entry.PlacedByThisTag);
        Assert.Equal(holds ? 1 : 0, Assert.Single((await service.ListAsync(channelName, null)).Tags).PlacedCount);
    }

    [Fact]
    public async Task HeldByActiveTags_NeedsAnActivationInTheSetAndAnEntry_NotAPlacement_OldestTagFirst()
    {
        var channel = await SeedChannelAsync("tagheld");
        var mine = await SeedTagAsync(channel.Id, "Mine", T0.AddHours(-5));
        // Seeded in id order A < E < F < B, created B < E = F < A: the order is by creation, ties by id.
        var a = await SeedTagAsync(channel.Id, "A", T0.AddHours(-1));
        var e = await SeedTagAsync(channel.Id, "E", T0.AddHours(-2));
        var f = await SeedTagAsync(channel.Id, "F", T0.AddHours(-2));
        var b = await SeedTagAsync(channel.Id, "B", T0.AddHours(-3));
        var activeWithoutEntry = await SeedTagAsync(channel.Id, "NoEntry", T0.AddHours(-4));
        var activeElsewhere = await SeedTagAsync(channel.Id, "Elsewhere", T0.AddHours(-4));
        var x = NewSevenTvId();
        foreach (var tag in new[] { mine, a, e, f, b, activeElsewhere })
        {
            await SeedEntryAsync(tag.Id, x, "X", T0);
        }

        foreach (var tag in new[] { mine, a, e, f, b, activeWithoutEntry })
        {
            await SeedActivationAsync(tag.Id, ActiveSetId, await SeedOperationAsync(tag.Id, ActiveSetId, T0), T0);
        }

        await SeedActivationAsync(activeElsewhere.Id, OtherSetId, await SeedOperationAsync(activeElsewhere.Id, OtherSetId, T0), T0);

        var entry = Assert.Single((await CreateService(fixture.CreateDbContext()).ListEntriesAsync("tagheld", mine.Id, null)).Entries);

        Assert.Equal([b.Id, e.Id, f.Id, a.Id], entry.HeldByActiveTags.Select(t => t.Id));
        Assert.Equal(["B", "E", "F", "A"], entry.HeldByActiveTags.Select(t => t.Name));
        // Nobody placed anything: holding is about activation and entry only.
        Assert.False(entry.PlacedByThisTag);
        Assert.Empty(entry.PlacedByOtherTags);
    }

    [Fact]
    public async Task PlacedByOtherTags_ListsOnlyValidPlacements_OldestTagFirst()
    {
        var channel = await SeedChannelAsync("tagplacedothers");
        var mine = await SeedTagAsync(channel.Id, "Mine", T0.AddHours(-4));
        var expired = await SeedTagAsync(channel.Id, "Expired", T0.AddHours(-3));
        var newer = await SeedTagAsync(channel.Id, "Newer", T0.AddHours(-1));
        var older = await SeedTagAsync(channel.Id, "Older", T0.AddHours(-2));
        var x = NewSevenTvId();
        foreach (var tag in new[] { mine, expired, newer, older })
        {
            await SeedEntryAsync(tag.Id, x, "X", T0);
        }

        // One observation at T0: it expires the run registered before it, not those registered after.
        await SeedPlayInAsync(expired.Id, ActiveSetId, T0.AddMinutes(-10), x);
        await SeedPlayInAsync(newer.Id, ActiveSetId, T0.AddMinutes(10), x);
        await SeedPlayInAsync(older.Id, ActiveSetId, T0.AddMinutes(5), x);
        await SeedObservationAsync(channel.Id, x, ActiveSetId, T0);

        var service = CreateService(fixture.CreateDbContext());
        var entry = Assert.Single((await service.ListEntriesAsync("tagplacedothers", mine.Id, null)).Entries);

        Assert.Equal([new EmoteTagRefDto(older.Id, "Older"), new EmoteTagRefDto(newer.Id, "Newer")], entry.PlacedByOtherTags);
        // Every one of them is active, the expired holder included: holding does not look at placements.
        Assert.Equal([expired.Id, older.Id, newer.Id], entry.HeldByActiveTags.Select(t => t.Id));
        Assert.Equal([0, 0, 1, 1], (await service.ListAsync("tagplacedothers", null)).Tags.Select(t => t.PlacedCount));
    }

    [Fact]
    public async Task Activation_IsReportedPerSet_WithItsOperation_OrNullAndInactive()
    {
        var channel = await SeedChannelAsync("tagactivation");
        var tag = await SeedTagAsync(channel.Id, "Mine");
        await SeedEntriesAsync(tag.Id, 1);
        var operation = await SeedOperationAsync(tag.Id, ActiveSetId, T0);
        await SeedActivationAsync(tag.Id, ActiveSetId, operation, T0.AddMinutes(3));

        var service = CreateService(fixture.CreateDbContext());
        var activeEntries = await service.ListEntriesAsync("tagactivation", tag.Id, null);
        var otherEntries = await service.ListEntriesAsync("tagactivation", tag.Id, OtherSetId);
        var activeList = Assert.Single((await service.ListAsync("tagactivation", null)).Tags);
        var otherList = Assert.Single((await service.ListAsync("tagactivation", OtherSetId)).Tags);

        Assert.Equal(operation, activeEntries.ActivationOperationId);
        Assert.Null(otherEntries.ActivationOperationId);
        Assert.Equal((true, (DateTime?)T0.AddMinutes(3)), (activeList.Active, activeList.ActivatedAtUtc));
        Assert.Equal((false, (DateTime?)null), (otherList.Active, otherList.ActivatedAtUtc));
    }

    [Fact]
    public async Task ForeignSet_HasNoInSetStatus_ButItsPlacementsAndActivationAreComputedForThatSet()
    {
        var channel = await SeedChannelAsync("tagforeignset");
        var tag = await SeedTagAsync(channel.Id, "Mine");
        var kekw = await SeedEmoteAsync(channel.Id, "KEKW");
        await AddAsync("tagforeignset", tag.Id, [kekw.SevenTvEmoteId]);
        var operation = await SeedPlayInAsync(tag.Id, OtherSetId, T0, kekw.SevenTvEmoteId);

        var service = CreateService(fixture.CreateDbContext());
        var foreign = await service.ListEntriesAsync("tagforeignset", tag.Id, OtherSetId);
        var foreignTag = Assert.Single((await service.ListAsync("tagforeignset", OtherSetId)).Tags);
        var activeEntry = Assert.Single((await service.ListEntriesAsync("tagforeignset", tag.Id, null)).Entries);

        var entry = Assert.Single(foreign.Entries);
        Assert.Equal(((bool?)null, (string?)null), (entry.InSet, entry.CurrentName));
        Assert.Equal((true, (DateTime?)T0, (Guid?)operation), (entry.PlacedByThisTag, entry.PlacedAtUtc, entry.PlacementOperationId));
        Assert.Equal(operation, foreign.ActivationOperationId);
        Assert.Equal(((int?)null, 1, true), (foreignTag.InSetCount, foreignTag.PlacedCount, foreignTag.Active));
        // The placement belongs to the other set: the active set's view does not see it.
        Assert.Equal((true, false), (activeEntry.InSet, activeEntry.PlacedByThisTag));
    }

    [Fact]
    public async Task WithoutAnActiveSetAndWithoutASetParameter_EverySetFieldIsEmpty()
    {
        var channel = await SeedChannelAsync("tagnosetfields", activeEmoteSetId: string.Empty);
        var mine = await SeedTagAsync(channel.Id, "Mine", T0.AddHours(-2));
        var other = await SeedTagAsync(channel.Id, "Other", T0.AddHours(-1));
        var x = NewSevenTvId();
        await SeedEntryAsync(mine.Id, x, "X", T0);
        await SeedEntryAsync(other.Id, x, "X", T0);
        await SeedPlayInAsync(mine.Id, OtherSetId, T0, x);
        await SeedPlayInAsync(other.Id, OtherSetId, T0, x);

        var service = CreateService(fixture.CreateDbContext());
        var list = await service.ListAsync("tagnosetfields", null);
        var entries = await service.ListEntriesAsync("tagnosetfields", mine.Id, null);

        Assert.Null(list.EmoteSetId);
        Assert.All(list.Tags, t => Assert.Equal((0, false, (DateTime?)null), (t.PlacedCount, t.Active, t.ActivatedAtUtc)));
        Assert.Null(entries.EmoteSetId);
        Assert.Null(entries.ActivationOperationId);
        var entry = Assert.Single(entries.Entries);
        Assert.Equal((false, (DateTime?)null, (Guid?)null), (entry.PlacedByThisTag, entry.PlacedAtUtc, entry.PlacementOperationId));
        Assert.Empty(entry.HeldByActiveTags);
        Assert.Empty(entry.PlacedByOtherTags);
    }

    // ---- Races under the channel row lock --------------------------------------------------------
    //
    // The first contender takes the channel lock and parks on a table the test holds in SHARE mode
    // (its write needs ROW EXCLUSIVE); the second then queues on the channel lock. Releasing the hold
    // lets the first commit, and only then does the second read — so it must see the first's write.

    [Fact]
    public async Task Race_TwoCreatesOfTheSameName_OneOk_OneNameTaken()
    {
        var channel = await SeedChannelAsync("tagracename");

        var (first, second) = await RaceAsync(
            "tagracename",
            "AuditLogEntries",
            service => service.CreateAsync("tagracename", "Funny", Actor),
            service => service.CreateAsync("tagracename", "FUNNY", Actor));

        Assert.Equal(EmoteTagMutationStatus.Ok, first.Status);
        Assert.Equal(EmoteTagMutationStatus.NameTaken, second.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.EmoteTags.CountAsync(t => t.ChannelId == channel.Id));
    }

    [Fact]
    public async Task Race_TwoCreatesAtFortyNineTags_OneOk_OneLimitReached()
    {
        var channel = await SeedChannelAsync("tagracelimit");
        await SeedTagsAsync(channel.Id, EmoteTagLimits.MaxTagsPerChannel - 1);

        var (first, second) = await RaceAsync(
            "tagracelimit",
            "AuditLogEntries",
            service => service.CreateAsync("tagracelimit", "Alpha", Actor),
            service => service.CreateAsync("tagracelimit", "Bravo", Actor));

        Assert.Equal(EmoteTagMutationStatus.Ok, first.Status);
        Assert.Equal(EmoteTagMutationStatus.LimitReached, second.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(EmoteTagLimits.MaxTagsPerChannel, await verify.EmoteTags.CountAsync(t => t.ChannelId == channel.Id));
    }

    [Theory]
    [InlineData(EmoteTagLimits.MaxEntriesPerTag - 1, EmoteTagAddEntriesStatus.EntryLimitReached)]
    [InlineData(EmoteTagLimits.MaxEntriesPerTag - 2, EmoteTagAddEntriesStatus.Ok)]
    public async Task Race_TwoDisjointAddsNearTheEntryLimit_NeverExceedIt(int seeded, EmoteTagAddEntriesStatus expectedSecond)
    {
        // Without the lock both would count the same 999 and both commit: 1001 (Codex finding 6).
        var name = $"tagraceentries{seeded}";
        var channel = await SeedChannelAsync(name);
        var tag = await SeedTagAsync(channel.Id, "Big");
        await SeedEntriesAsync(tag.Id, seeded);
        var alpha = await SeedEmoteAsync(channel.Id, "Alpha");
        var bravo = await SeedEmoteAsync(channel.Id, "Bravo");

        var (first, second) = await RaceAsync(
            name,
            "EmoteTagEntries",
            service => service.AddEntriesAsync(name, tag.Id, [alpha.SevenTvEmoteId]),
            service => service.AddEntriesAsync(name, tag.Id, [bravo.SevenTvEmoteId]));

        Assert.Equal(EmoteTagAddEntriesStatus.Ok, first.Status);
        Assert.Equal(expectedSecond, second.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(EmoteTagLimits.MaxEntriesPerTag, await verify.EmoteTagEntries.CountAsync(e => e.TagId == tag.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Race_RemoveAndAddOfTheSameEmote_EndInCommitOrder(bool addFirst)
    {
        var name = addFirst ? "tagraceaddfirst" : "tagraceremovefirst";
        var channel = await SeedChannelAsync(name);
        var tag = await SeedTagAsync(channel.Id, "Funny");
        var kekw = await SeedEmoteAsync(channel.Id, "KEKW");
        IReadOnlyList<string> ids = [kekw.SevenTvEmoteId];

        int removed;
        if (addFirst)
        {
            var (added, removal) = await RaceAsync(
                name, "EmoteTagEntries",
                service => service.AddEntriesAsync(name, tag.Id, ids),
                service => service.RemoveEntriesAsync(name, tag.Id, ids));
            Assert.Equal((EmoteTagAddEntriesStatus.Ok, 1), (added.Status, added.AddedCount));
            Assert.Equal(EmoteTagRemoveEntriesStatus.Ok, removal.Status);
            removed = removal.RemovedCount;
        }
        else
        {
            var (removal, added) = await RaceAsync(
                name, "EmoteTagEntries",
                service => service.RemoveEntriesAsync(name, tag.Id, ids),
                service => service.AddEntriesAsync(name, tag.Id, ids));
            Assert.Equal((EmoteTagAddEntriesStatus.Ok, 1), (added.Status, added.AddedCount));
            Assert.Equal(EmoteTagRemoveEntriesStatus.Ok, removal.Status);
            removed = removal.RemovedCount;
        }

        // Add then remove: the remove saw the committed entry. Remove then add: it found nothing and
        // the add wrote the entry afterwards. Either way exactly the commit order, never a duplicate.
        Assert.Equal(addFirst ? 1 : 0, removed);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(addFirst ? 0 : 1, await verify.EmoteTagEntries.CountAsync(e => e.TagId == tag.Id));
    }

    // ---- Registration and play-in report (T-C Task 4) --------------------------------------------
    //
    // Spec 6.4 and 5.5 rules 2, 6, 10; E10, E27, E33. Registration stamps the server clock; every
    // report runs under the channel lock and is applied once per operation. Each counterexample of
    // spec 5.5 ends with the invariant "inactive ⇒ no placement".

    [Fact]
    public async Task Register_IsIdempotent_TheRepeatIsReplayedWithTheSameInstant()
    {
        var channel = await SeedChannelAsync("tagregreplay");
        var tag = await SeedTagAsync(channel.Id, "Stronghold");
        var operationId = Guid.NewGuid();

        var first = await RegisterAsync("tagregreplay", tag.Id, operationId);
        var second = await RegisterAsync("tagregreplay", tag.Id, operationId);

        Assert.Equal(TagOperationRegistrationStatus.Ok, first.Status);
        Assert.Equal(TagOperationRegistrationStatus.Replayed, second.Status);
        Assert.NotNull(first.RegisteredAtUtc);
        Assert.Equal(first.RegisteredAtUtc, second.RegisteredAtUtc);
        await using var verify = fixture.CreateDbContext();
        var stored = Assert.Single(await verify.EmoteTagOperations.AsNoTracking().Where(o => o.TagId == tag.Id).ToListAsync());
        Assert.Equal((operationId, EmoteTagOperationKind.PlayIn, ActiveSetId, first.RegisteredAtUtc.Value, (DateTime?)null),
            (stored.OperationId, stored.Kind, stored.SevenTvEmoteSetId, stored.RegisteredAtUtc, stored.AppliedAtUtc));
    }

    [Fact]
    public async Task Register_StampsTheServerClock_BetweenTwoReadingsOfTheTest()
    {
        var channel = await SeedChannelAsync("tagregclock");
        var tag = await SeedTagAsync(channel.Id, "Stronghold");

        var before = DateTime.UtcNow;
        var result = await RegisterAsync("tagregclock", tag.Id, Guid.NewGuid(), kind: EmoteTagOperationKind.Removal);
        var after = DateTime.UtcNow;

        Assert.Equal(TagOperationRegistrationStatus.Ok, result.Status);
        // The stamp is truncated to the microseconds Postgres keeps, so the lower bound is too.
        Assert.InRange(result.RegisteredAtUtc!.Value, before.AddTicks(-(before.Ticks % TimeSpan.TicksPerMicrosecond)), after);
        Assert.Equal(DateTimeKind.Utc, result.RegisteredAtUtc.Value.Kind);
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("set")]
    [InlineData("kind")]
    public async Task Register_TheSameIdForAnotherTagSetOrKind_IsConflict_AndKeepsTheFirst(string differs)
    {
        var name = $"tagregconflict{differs}";
        var channel = await SeedChannelAsync(name);
        var tag = await SeedTagAsync(channel.Id, "Stronghold");
        var otherTag = await SeedTagAsync(channel.Id, "Halloween");
        var operationId = Guid.NewGuid();
        var first = await RegisterAsync(name, tag.Id, operationId);

        var second = await RegisterAsync(
            name,
            differs == "tag" ? otherTag.Id : tag.Id,
            operationId,
            differs == "set" ? OtherSetId : ActiveSetId,
            differs == "kind" ? EmoteTagOperationKind.Removal : EmoteTagOperationKind.PlayIn);

        Assert.Equal(new TagOperationRegistrationResult(TagOperationRegistrationStatus.Conflict, null), second);
        await using var verify = fixture.CreateDbContext();
        var stored = await verify.EmoteTagOperations.AsNoTracking().SingleAsync(o => o.OperationId == operationId);
        Assert.Equal((tag.Id, EmoteTagOperationKind.PlayIn, ActiveSetId, first.RegisteredAtUtc!.Value),
            (stored.TagId, stored.Kind, stored.SevenTvEmoteSetId, stored.RegisteredAtUtc));
    }

    [Fact]
    public async Task Register_AnUnknownChannelOrTag_OrATagOfAnotherChannel_IsRejected_AndAnUnknownKindThrows()
    {
        var channel = await SeedChannelAsync("tagregmissing");
        var neighbour = await SeedChannelAsync("tagregmissing2");
        var tag = await SeedTagAsync(channel.Id, "Stronghold");
        var foreignTag = await SeedTagAsync(neighbour.Id, "Foreign");

        Assert.Equal(TagOperationRegistrationStatus.ChannelNotFound, (await RegisterAsync("tagregnochannel", tag.Id, Guid.NewGuid())).Status);
        Assert.Equal(TagOperationRegistrationStatus.TagNotFound, (await RegisterAsync("tagregmissing", foreignTag.Id, Guid.NewGuid())).Status);
        Assert.Equal(TagOperationRegistrationStatus.TagNotFound, (await RegisterAsync("tagregmissing", long.MaxValue, Guid.NewGuid())).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => RegisterAsync("tagregmissing", tag.Id, Guid.NewGuid(), kind: "PlayIn"));

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagOperations.AnyAsync(o => o.TagId == tag.Id || o.TagId == foreignTag.Id));
    }

    // Two channels do not share a lock, so the same operation id registered in both at once meets at
    // the primary key: one registration wins, the other is a conflict rather than a 500.
    [Fact]
    public async Task Race_TheSameIdRegisteredInTwoChannelsAtOnce_OneOk_OneConflict()
    {
        var alpha = await SeedChannelAsync("tagregracea");
        var bravo = await SeedChannelAsync("tagregraceb");
        var alphaTag = await SeedTagAsync(alpha.Id, "Stronghold");
        var bravoTag = await SeedTagAsync(bravo.Id, "Stronghold");
        var operationId = Guid.NewGuid();
        var request = new RegisterTagOperationRequest(operationId, EmoteTagOperationKind.PlayIn, ActiveSetId);

        var (first, second) = await RaceAsync(
            "tagregrace",
            "EmoteTagOperations",
            service => service.RegisterOperationAsync("tagregracea", alphaTag.Id, request),
            service => service.RegisterOperationAsync("tagregraceb", bravoTag.Id, request));

        Assert.Equal(
            [TagOperationRegistrationStatus.Ok, TagOperationRegistrationStatus.Conflict],
            new[] { first.Status, second.Status }.Order());
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.EmoteTagOperations.CountAsync(o => o.OperationId == operationId));
    }

    [Fact]
    public async Task Report_AnUnknownChannelOrTag_OrAnUnregisteredOperation_IsRejected_AndWritesNothing()
    {
        var channel = await SeedChannelAsync("tagrepmissing");
        var tag = await SeedTagAsync(channel.Id, "Stronghold");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var operationId = Guid.NewGuid();
        await RegisterAsync("tagrepmissing", tag.Id, operationId);

        Assert.Equal(TagReportStatus.ChannelNotFound, (await ReportAsync("tagrepnochannel", tag.Id, operationId, ActiveSetId, x)).Status);
        Assert.Equal(TagReportStatus.TagNotFound, (await ReportAsync("tagrepmissing", long.MaxValue, operationId, ActiveSetId, x)).Status);
        var unknown = await ReportAsync("tagrepmissing", tag.Id, Guid.NewGuid(), ActiveSetId, x);

        Assert.Equal((TagReportStatus.OperationUnknown, false, 0, 0),
            (unknown.Status, unknown.Replayed, unknown.RecordedCount, unknown.AlreadyRecordedCount));
        Assert.Empty(unknown.NotTaggedIds);
        Assert.Empty(unknown.DiscardedStaleIds);
        await AssertNoReportWrittenAsync(channel, tag.Id);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("kind")]
    [InlineData("tag")]
    public async Task Report_ToAnotherSet_OfARemovalOperation_OrOfAnotherTag_IsOperationConflict_AndWritesNothing(string differs)
    {
        var name = $"tagrepconflict{differs}";
        var channel = await SeedChannelAsync(name);
        var tag = await SeedTagAsync(channel.Id, "Stronghold");
        var otherTag = await SeedTagAsync(channel.Id, "Halloween");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var operationId = Guid.NewGuid();
        await RegisterAsync(
            name,
            differs == "tag" ? otherTag.Id : tag.Id,
            operationId,
            kind: differs == "kind" ? EmoteTagOperationKind.Removal : EmoteTagOperationKind.PlayIn);

        var result = await ReportAsync(name, tag.Id, operationId, differs == "set" ? OtherSetId : ActiveSetId, x);

        Assert.Equal((TagReportStatus.OperationConflict, false, 0, 0),
            (result.Status, result.Replayed, result.RecordedCount, result.AlreadyRecordedCount));
        await AssertNoReportWrittenAsync(channel, tag.Id);
        await using var verify = fixture.CreateDbContext();
        Assert.Null((await verify.EmoteTagOperations.AsNoTracking().SingleAsync(o => o.OperationId == operationId)).AppliedAtUtc);
    }

    // The conflict check comes before the replay check: an applied operation reported for another
    // set is still a conflict — a "replayed: true" would tell the browser its report had landed.
    [Fact]
    public async Task Report_OfAnAppliedOperationToAnotherSet_IsStillOperationConflict()
    {
        var channel = await SeedChannelAsync("tagrepappliedconflict");
        var tag = await SeedTagAsync(channel.Id, "Stronghold");
        var operationId = Guid.NewGuid();
        await RegisterPlayInAsync("tagrepappliedconflict", tag.Id, operationId);
        Assert.Equal(TagReportStatus.Ok, (await ReportAsync("tagrepappliedconflict", tag.Id, operationId, ActiveSetId)).Status);

        var result = await ReportAsync("tagrepappliedconflict", tag.Id, operationId, OtherSetId);

        Assert.Equal((TagReportStatus.OperationConflict, false), (result.Status, result.Replayed));
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    [Fact]
    public async Task Report_UpsertsNewAndExistingPlacements_OverwritingOperationPlacedAtAndAnchorOfTheExistingOne()
    {
        var channel = await SeedChannelAsync("tagrepupsert");
        var tag = await SeedTagAsync(channel.Id, "Stronghold");
        var x = NewSevenTvId();
        var y = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        await SeedEntryAsync(tag.Id, y, "Y", T0);
        // Last week's play-in left an expired placement of X behind (a leave after its anchor).
        var oldOperation = await SeedPlayInAsync(tag.Id, ActiveSetId, T0.AddDays(-7), x);
        await SeedObservationAsync(channel.Id, x, ActiveSetId, T0.AddDays(-6));
        var operationId = Guid.NewGuid();
        var registeredAtUtc = await RegisterPlayInAsync("tagrepupsert", tag.Id, operationId);

        var before = DateTime.UtcNow;
        var result = await ReportAsync("tagrepupsert", tag.Id, operationId, ActiveSetId, x, y);
        var after = DateTime.UtcNow;

        Assert.Equal((TagReportStatus.Ok, false, 1, 1), (result.Status, result.Replayed, result.RecordedCount, result.AlreadyRecordedCount));
        Assert.Empty(result.NotTaggedIds);
        Assert.Empty(result.DiscardedStaleIds);
        await using var verify = fixture.CreateDbContext();
        var placements = await verify.EmoteTagPlacements.AsNoTracking().Where(p => p.TagId == tag.Id).ToListAsync();
        Assert.Equal(new[] { x, y }.Order(StringComparer.Ordinal), placements.Select(p => p.SevenTvEmoteId).Order(StringComparer.Ordinal));
        Assert.All(placements, p =>
        {
            Assert.Equal((operationId, registeredAtUtc, ActiveSetId), (p.OperationId, p.RegisteredAtUtc, p.SevenTvEmoteSetId));
            Assert.InRange(p.PlacedAtUtc, before.AddMilliseconds(-1), after);
        });
        Assert.DoesNotContain(placements, p => p.OperationId == oldOperation);
        // Rule 10 pays off at the read: the overwritten anchor is later than the old leave, so X holds again.
        var entries = await CreateService(verify).ListEntriesAsync("tagrepupsert", tag.Id, null);
        Assert.All(entries.Entries, e => Assert.Equal((true, (Guid?)operationId), (e.PlacedByThisTag, e.PlacementOperationId)));
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    // E6: the report names the set the run wrote to, which need not be the active one; and E26: a
    // play-in that added nothing still makes the tag active there.
    [Fact]
    public async Task Report_WithAnEmptyList_ActivatesTheTag_InANonActiveSetToo_AndMarksTheOperationApplied()
    {
        var channel = await SeedChannelAsync("tagrepempty");
        var tag = await SeedTagAsync(channel.Id, "Stronghold");
        var operationId = Guid.NewGuid();
        await RegisterPlayInAsync("tagrepempty", tag.Id, operationId, OtherSetId);

        var before = DateTime.UtcNow;
        var result = await ReportAsync("tagrepempty", tag.Id, operationId, OtherSetId);
        var after = DateTime.UtcNow;

        Assert.Equal((TagReportStatus.Ok, false, 0, 0), (result.Status, result.Replayed, result.RecordedCount, result.AlreadyRecordedCount));
        await using var verify = fixture.CreateDbContext();
        var activation = await verify.EmoteTagActivations.AsNoTracking().SingleAsync(a => a.TagId == tag.Id);
        Assert.Equal((OtherSetId, operationId), (activation.SevenTvEmoteSetId, activation.OperationId));
        Assert.InRange(activation.ActivatedAtUtc, before.AddMilliseconds(-1), after);
        var operation = await verify.EmoteTagOperations.AsNoTracking().SingleAsync(o => o.OperationId == operationId);
        Assert.Equal(activation.ActivatedAtUtc, operation.AppliedAtUtc);
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == tag.Id));
        var list = await CreateService(verify).ListAsync("tagrepempty", OtherSetId);
        Assert.Equal((true, (DateTime?)activation.ActivatedAtUtc, 0), (list.Tags[0].Active, list.Tags[0].ActivatedAtUtc, list.Tags[0].PlacedCount));
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    [Fact]
    public async Task Report_IdsWithoutAnEntry_AreNotTaggedIds_InRequestOrder_AndAreNotPlaced()
    {
        var channel = await SeedChannelAsync("tagrepnottagged");
        var tag = await SeedTagAsync(channel.Id, "Stronghold");
        var otherTag = await SeedTagAsync(channel.Id, "Halloween");
        var x = NewSevenTvId();
        var foreign = NewSevenTvId();
        var stranger = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        // An entry of another tag of the channel is no entry of this one.
        await SeedEntryAsync(otherTag.Id, foreign, "Foreign", T0);
        var operationId = Guid.NewGuid();
        await RegisterPlayInAsync("tagrepnottagged", tag.Id, operationId);

        var result = await ReportAsync("tagrepnottagged", tag.Id, operationId, ActiveSetId, stranger, x, foreign, stranger);

        Assert.Equal((1, 0), (result.RecordedCount, result.AlreadyRecordedCount));
        Assert.Equal([stranger, foreign], result.NotTaggedIds);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal([x], await verify.EmoteTagPlacements.Where(p => p.TagId == tag.Id || p.TagId == otherTag.Id)
            .Select(p => p.SevenTvEmoteId).ToListAsync());
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    // Causal: only a leave observed after the registration discards. The observation is seeded
    // relative to the stamp the registration returned, so the three cases sit exactly on, before and
    // after it.
    [Theory]
    [InlineData("tagrepcausalafter", 1, true)]
    [InlineData("tagrepcausalsame", 0, false)]
    [InlineData("tagrepcausalbefore", -1, false)]
    public async Task Report_DiscardsAnIdOnlyForALeaveObservedAfterTheRegistration(
        string channelName, int observedSecondsAfterRegistration, bool discarded)
    {
        var channel = await SeedChannelAsync(channelName);
        var tag = await SeedTagAsync(channel.Id, "Stronghold");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var operationId = Guid.NewGuid();
        var registeredAtUtc = await RegisterPlayInAsync(channelName, tag.Id, operationId);
        await SeedObservationAsync(channel.Id, x, ActiveSetId, registeredAtUtc.AddSeconds(observedSecondsAfterRegistration));

        var result = await ReportAsync(channelName, tag.Id, operationId, ActiveSetId, x);

        Assert.Equal(discarded ? new[] { x } : [], result.DiscardedStaleIds);
        Assert.Equal(discarded ? 0 : 1, result.RecordedCount);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(!discarded, await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == tag.Id));
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    [Fact]
    public async Task Report_EverythingDiscarded_StillActivatesTheTag_AndLeavesAnExpiredPlacementAlone()
    {
        var channel = await SeedChannelAsync("tagrepalldiscarded");
        var tag = await SeedTagAsync(channel.Id, "Stronghold");
        var x = NewSevenTvId();
        var y = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        await SeedEntryAsync(tag.Id, y, "Y", T0);
        // An earlier play-in placed X; the leave below expires it as well.
        var oldOperation = await SeedPlayInAsync(tag.Id, ActiveSetId, T0.AddDays(-7), x);
        var operationId = Guid.NewGuid();
        var registeredAtUtc = await RegisterPlayInAsync("tagrepalldiscarded", tag.Id, operationId);
        await SeedObservationAsync(channel.Id, x, ActiveSetId, registeredAtUtc.AddSeconds(1));
        await SeedObservationAsync(channel.Id, y, ActiveSetId, registeredAtUtc.AddSeconds(2));

        var result = await ReportAsync("tagrepalldiscarded", tag.Id, operationId, ActiveSetId, y, x);

        Assert.Equal((TagReportStatus.Ok, 0, 0), (result.Status, result.RecordedCount, result.AlreadyRecordedCount));
        Assert.Equal([y, x], result.DiscardedStaleIds);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(operationId, (await verify.EmoteTagActivations.AsNoTracking().SingleAsync(a => a.TagId == tag.Id)).OperationId);
        // Discarded means untouched: the old row keeps its old revision and stays expired.
        Assert.Equal(oldOperation, (await verify.EmoteTagPlacements.AsNoTracking().SingleAsync(p => p.TagId == tag.Id)).OperationId);
        Assert.Equal(0, Assert.Single((await CreateService(verify).ListAsync("tagrepalldiscarded", null)).Tags).PlacedCount);
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    [Fact]
    public async Task Report_Replayed_WritesNothing_EvenWithAnotherIdList()
    {
        var channel = await SeedChannelAsync("tagrepreplay");
        var tag = await SeedTagAsync(channel.Id, "Stronghold");
        var x = NewSevenTvId();
        var y = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        await SeedEntryAsync(tag.Id, y, "Y", T0);
        var operationId = Guid.NewGuid();
        await RegisterPlayInAsync("tagrepreplay", tag.Id, operationId);
        await ReportAsync("tagrepreplay", tag.Id, operationId, ActiveSetId, x);
        var before = await TagTablesAsync(channel, tag.Id);

        var replay = await ReportAsync("tagrepreplay", tag.Id, operationId, ActiveSetId, x, y, NewSevenTvId());

        Assert.Equal((TagReportStatus.Ok, true, 0, 0), (replay.Status, replay.Replayed, replay.RecordedCount, replay.AlreadyRecordedCount));
        Assert.Empty(replay.NotTaggedIds);
        Assert.Empty(replay.DiscardedStaleIds);
        Assert.Equal(before, await TagTablesAsync(channel, tag.Id));
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    [Fact]
    public async Task Report_IsAuditedAsPlayedIn_WithIdsAndCount_WithoutTheTagName()
    {
        var channel = await SeedChannelAsync("tagrepaudit");
        var tag = await SeedTagAsync(channel.Id, "SecretStronghold");
        var x = NewSevenTvId();
        var y = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        await SeedEntryAsync(tag.Id, y, "Y", T0);
        await SeedPlayInAsync(tag.Id, ActiveSetId, T0.AddDays(-7), x);
        var operationId = Guid.NewGuid();
        await RegisterPlayInAsync("tagrepaudit", tag.Id, operationId);

        await ReportAsync("tagrepaudit", tag.Id, operationId, ActiveSetId, x, y, NewSevenTvId());

        var entry = Assert.Single(await LoadAuditAsync("tagrepaudit"));
        Assert.Equal((AuditActions.TagPlayedIn, "emoteTag", tag.Id.ToString(CultureInfo.InvariantCulture), Actor.TwitchUserId),
            (entry.Action, entry.TargetType, entry.TargetId, entry.ActorTwitchUserId));
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        var root = details.RootElement;
        Assert.Equal(["emoteCount", "emoteSetId", "operationId", "tagId"],
            root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal((tag.Id, ActiveSetId, operationId, 2),
            (root.GetProperty("tagId").GetInt64(), root.GetProperty("emoteSetId").GetString(),
                root.GetProperty("operationId").GetGuid(), root.GetProperty("emoteCount").GetInt32()));
        Assert.DoesNotContain("secret", entry.DetailsJson, StringComparison.OrdinalIgnoreCase);
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    // Counterexample 4: a repeated play-in report must not lay placements down again — the repeat is
    // a replay, and the first report's rows keep their placement time and revision.
    [Fact]
    public async Task CounterExample4_ARepeatedPlayInReport_IsIgnored()
    {
        var channel = await SeedChannelAsync("tagce4");
        var tag = await SeedTagAsync(channel.Id, "A");
        var x = NewSevenTvId();
        var y = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        await SeedEntryAsync(tag.Id, y, "Y", T0);
        var operationId = Guid.NewGuid();
        await RegisterPlayInAsync("tagce4", tag.Id, operationId);
        var first = await ReportAsync("tagce4", tag.Id, operationId, ActiveSetId, x, y);
        var afterFirst = await TagTablesAsync(channel, tag.Id);

        var repeat = await ReportAsync("tagce4", tag.Id, operationId, ActiveSetId, x, y);

        Assert.Equal((false, 2, true, 0), (first.Replayed, first.RecordedCount, repeat.Replayed, repeat.RecordedCount + repeat.AlreadyRecordedCount));
        Assert.Equal(afterFirst, await TagTablesAsync(channel, tag.Id));
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    // Counterexample 6: play-in P registered at t₀ adds X and Y; its report hangs; X is removed by
    // hand and the sync observes it at t₁ > t₀; X is added back by hand. The late report discards X
    // for good, places Y and activates the tag.
    [Fact]
    public async Task CounterExample6_ALatePlayInReportAfterAnObservedLeave_DiscardsThatEmote_PlacesTheRest()
    {
        var channel = await SeedChannelAsync("tagce6");
        var tag = await SeedTagAsync(channel.Id, "A");
        var x = await SeedEmoteAsync(channel.Id, "X");
        var y = await SeedEmoteAsync(channel.Id, "Y");
        await SeedEntryAsync(tag.Id, x.SevenTvEmoteId, "X", T0);
        await SeedEntryAsync(tag.Id, y.SevenTvEmoteId, "Y", T0);
        var operationId = Guid.NewGuid();
        await RegisterPlayInAsync("tagce6", tag.Id, operationId);
        await SeedObservationAsync(channel.Id, x.SevenTvEmoteId, ActiveSetId, DateTime.UtcNow);

        var result = await ReportAsync("tagce6", tag.Id, operationId, ActiveSetId, x.SevenTvEmoteId, y.SevenTvEmoteId);

        Assert.Equal((1, 0), (result.RecordedCount, result.AlreadyRecordedCount));
        Assert.Equal([x.SevenTvEmoteId], result.DiscardedStaleIds);
        await using var verify = fixture.CreateDbContext();
        var service = CreateService(verify);
        var entries = (await service.ListEntriesAsync("tagce6", tag.Id, null)).Entries.ToDictionary(e => e.SevenTvEmoteId);
        // X is back in the set by hand, but without a placement and without a fresh date.
        Assert.Equal((true, false, (DateTime?)null), (entries[x.SevenTvEmoteId].InSet, entries[x.SevenTvEmoteId].PlacedByThisTag, entries[x.SevenTvEmoteId].PlacedAtUtc));
        Assert.True(entries[y.SevenTvEmoteId].PlacedByThisTag);
        Assert.True(Assert.Single((await service.ListAsync("tagce6", null)).Tags).Active);
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    // Counterexample 9, report first: the report holds the channel lock and has inserted (A, X, S),
    // not committed; meanwhile the delta path observes X leaving at t₁ > t₀. With an observation row
    // already there, the observer's upsert is a plain update and commits before the report does; with
    // a first observation, the insert's foreign-key check waits for the report's channel lock and
    // commits after it. Either way the report did not see t₁ and commits the placement — and every
    // read after both commits applies the rule: the placement does not hold.
    [Theory]
    [InlineData("tagce9update", true)]
    [InlineData("tagce9insert", false)]
    public async Task CounterExample9_AnObservationCommittingWhileTheReportHoldsTheLock_ExpiresThePlacementAtRead(
        string channelName, bool observationRowExists)
    {
        var channel = await SeedChannelAsync(channelName);
        var tag = await SeedTagAsync(channel.Id, "A");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var operationId = Guid.NewGuid();
        var registeredAtUtc = await RegisterPlayInAsync(channelName, tag.Id, operationId);
        if (observationRowExists)
        {
            await SeedObservationAsync(channel.Id, x, ActiveSetId, registeredAtUtc.AddDays(-7));
        }

        var pause = new PauseBeforeCommit();
        await using var reportDb = fixture.CreateDbContext([pause]);
        var report = CreateService(reportDb).ReportPlacementsAsync(
            channelName, tag.Id, new TagPlacementReport(operationId, ActiveSetId, [x]), Actor);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var observerName = $"{channelName}-observer";
        await using var observerDb = fixture.CreateTaggedDbContext(observerName);
        var observer = Task.Run(async () =>
        {
            await using var transaction = await observerDb.Database.BeginTransactionAsync();
            await EmoteSetLeaveObservations.RecordAsync(observerDb, channel.Id, ActiveSetId, [x], DateTime.UtcNow, CancellationToken.None);
            await transaction.CommitAsync();
        });
        if (observationRowExists)
        {
            await observer.WaitAsync(TimeSpan.FromSeconds(30));
        }
        else
        {
            await fixture.WaitUntilBlockedOnLockAsync(observerName, observer);
        }

        pause.Release.SetResult();
        var result = await report.WaitAsync(TimeSpan.FromSeconds(30));
        await observer.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal((1, 0), (result.RecordedCount, result.DiscardedStaleIds.Count));
        await using var verify = fixture.CreateDbContext();
        var placement = await verify.EmoteTagPlacements.AsNoTracking().SingleAsync(p => p.TagId == tag.Id);
        Assert.Equal((operationId, registeredAtUtc), (placement.OperationId, placement.RegisteredAtUtc));
        var observed = await EmoteSetLeaveObservations.LoadLatestAsync(verify, channel.Id, ActiveSetId, [x], CancellationToken.None);
        Assert.True(observed[x] > registeredAtUtc);
        var service = CreateService(verify);
        var entry = Assert.Single((await service.ListEntriesAsync(channelName, tag.Id, null)).Entries);
        Assert.Equal((false, (Guid?)null), (entry.PlacedByThisTag, entry.PlacementOperationId));
        Assert.Equal(0, Assert.Single((await service.ListAsync(channelName, null)).Tags).PlacedCount);
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    // Counterexample 9, observation first: it commits t₁ > t₀ before the report takes the lock, so
    // the report sees it and discards X. Both orders end the same — no valid placement of X.
    [Fact]
    public async Task CounterExample9_AnObservationCommittedBeforeTheReport_DiscardsTheEmote()
    {
        var channel = await SeedChannelAsync("tagce9reverse");
        var tag = await SeedTagAsync(channel.Id, "A");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var operationId = Guid.NewGuid();
        await RegisterPlayInAsync("tagce9reverse", tag.Id, operationId);
        await using (var observerDb = fixture.CreateDbContext())
        {
            await using var transaction = await observerDb.Database.BeginTransactionAsync();
            await EmoteSetLeaveObservations.RecordAsync(observerDb, channel.Id, ActiveSetId, [x], DateTime.UtcNow, CancellationToken.None);
            await transaction.CommitAsync();
        }

        var result = await ReportAsync("tagce9reverse", tag.Id, operationId, ActiveSetId, x);

        Assert.Equal(0, result.RecordedCount);
        Assert.Equal([x], result.DiscardedStaleIds);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == tag.Id));
        Assert.False(Assert.Single((await CreateService(verify).ListEntriesAsync("tagce9reverse", tag.Id, null)).Entries).PlacedByThisTag);
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    // ---- Removal report: hits, transfer, sweep, deactivation (T-C Task 5) ------------------------
    //
    // Spec 6.4 "Ausräumen" in the order 4 → 1 → 2 → 3 → 5 → 6, spec 5.5 rules 3, 4, 6 and 10, E7 rev. 2,
    // E26 rev. 3, E27. Every scenario ends with both placement invariants: inactive ⇒ no placement,
    // and no placement without an entry.

    [Fact]
    public async Task Removal_AnUnknownChannelOrTag_AnUnregisteredOperation_OrAnotherTagSetOrKind_IsRejected_AndWritesNothing()
    {
        var channel = await SeedChannelAsync("tagrmreject");
        var tag = await SeedTagAsync(channel.Id, "A");
        var otherTag = await SeedTagAsync(channel.Id, "B");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var playIn = await SeedPlayInAsync(tag.Id, ActiveSetId, T0.AddDays(-7), x);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync("tagrmreject", tag.Id, removal);
        var playInRegistered = Guid.NewGuid();
        await RegisterPlayInAsync("tagrmreject", tag.Id, playInRegistered);
        var foreignRemoval = Guid.NewGuid();
        await RegisterRemovalAsync("tagrmreject", otherTag.Id, foreignRemoval);
        var before = await TagTablesAsync(channel, tag.Id);
        var body = Removal(removal, playIn, Snapshot((x, playIn)), removed: [x], kept: []);

        Assert.Equal(TagReportStatus.ChannelNotFound, (await ReportRemovalAsync("tagrmnochannel", tag.Id, body)).Status);
        Assert.Equal(TagReportStatus.TagNotFound, (await ReportRemovalAsync("tagrmreject", long.MaxValue, body)).Status);
        Assert.Equal(TagReportStatus.OperationUnknown, (await ReportRemovalAsync("tagrmreject", tag.Id, body with { OperationId = Guid.NewGuid() })).Status);
        Assert.Equal(TagReportStatus.OperationConflict, (await ReportRemovalAsync("tagrmreject", tag.Id, body with { OperationId = playInRegistered })).Status);
        Assert.Equal(TagReportStatus.OperationConflict, (await ReportRemovalAsync("tagrmreject", tag.Id, body with { EmoteSetId = OtherSetId })).Status);
        var foreign = await ReportRemovalAsync("tagrmreject", tag.Id, body with { OperationId = foreignRemoval });

        Assert.Equal(new TagRemovalReportResult(TagReportStatus.OperationConflict, false, 0, 0, 0, 0, false), foreign);
        Assert.Equal(before, await TagTablesAsync(channel, tag.Id));
        Assert.Empty(await LoadAuditAsync("tagrmreject"));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // A hit needs the id and the revision: Y's snapshot entry names another operation, so Y is not
    // touched although it is in removedIds — and with the activation not matching either, no sweep
    // takes it.
    [Fact]
    public async Task Removal_AHitNeedsIdAndRevision_AnotherRevisionIsNoHit_AndTheRowStays()
    {
        var channel = await SeedChannelAsync("tagrmrevision");
        var tag = await SeedTagAsync(channel.Id, "A");
        var x = NewSevenTvId();
        var y = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        await SeedEntryAsync(tag.Id, y, "Y", T0);
        var playIn = await SeedPlayInAsync(tag.Id, ActiveSetId, T0.AddDays(-7), x, y);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync("tagrmrevision", tag.Id, removal);

        var result = await ReportRemovalAsync("tagrmrevision", tag.Id,
            Removal(removal, Guid.NewGuid(), Snapshot((x, playIn), (y, Guid.NewGuid())), removed: [x, y], kept: []));

        AssertCounts(result, deleted: 1, transferred: 0, dropped: 0, swept: 0, deactivated: false);
        var remaining = Assert.Single(await LoadPlacementsAsync(tag.Id));
        Assert.Equal((y, playIn), (remaining.SevenTvEmoteId, remaining.OperationId));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    [Theory]
    [InlineData("tagrmremoveddeact", true)]
    [InlineData("tagrmremovedactive", false)]
    public async Task Removal_RemovedHits_AreDeleted_WhetherOrNotTheTagIsDeactivated(string channelName, bool deactivated)
    {
        var channel = await SeedChannelAsync(channelName);
        var tag = await SeedTagAsync(channel.Id, "A");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var playIn = await SeedPlayInAsync(tag.Id, ActiveSetId, T0.AddDays(-7), x);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync(channelName, tag.Id, removal);

        var result = await ReportRemovalAsync(channelName, tag.Id,
            Removal(removal, deactivated ? playIn : Guid.NewGuid(), Snapshot((x, playIn)), removed: [x], kept: []));

        AssertCounts(result, deleted: 1, transferred: 0, dropped: 0, swept: 0, deactivated);
        Assert.Empty(await LoadPlacementsAsync(tag.Id));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(!deactivated, await verify.EmoteTagActivations.AnyAsync(a => a.TagId == tag.Id));
        Assert.NotNull((await verify.EmoteTagOperations.AsNoTracking().SingleAsync(o => o.OperationId == removal)).AppliedAtUtc);
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // The transfer target (spec 5.5 rule 4): the oldest other tag that is active in the set AND has an
    // entry for the emote. D is older and active but has no entry; E is older and has the entry but
    // is not active; B, C and F all qualify. "Oldest" is the tag's age (rule 3: CreatedAtUtc, then Id),
    // the order heldByActiveTags uses — not the activation: B is the oldest tag, yet C was activated
    // first and F last, and B was inserted after C, so its Id is the higher one.
    [Fact]
    public async Task Removal_KeptHits_GoToTheOldestActiveTagWithAnEntry_WithTheRemovalAsRevisionAndAnchor_AndTheOldPlacedAt()
    {
        var channel = await SeedChannelAsync("tagrmtransfer");
        var d = await SeedTagAsync(channel.Id, "D", T0.AddDays(-5));
        var e = await SeedTagAsync(channel.Id, "E", T0.AddDays(-4));
        var c = await SeedTagAsync(channel.Id, "C", T0.AddDays(-2));
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-3));
        var f = await SeedTagAsync(channel.Id, "F", T0.AddDays(-1.5));
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-1));
        Assert.True(b.Id > c.Id);
        var x = NewSevenTvId();
        foreach (var tag in new[] { e, b, c, f, a })
        {
            await SeedEntryAsync(tag.Id, x, "X", T0);
        }

        await SeedPlayInAsync(d.Id, ActiveSetId, T0.AddDays(-5));
        await SeedPlayInAsync(c.Id, ActiveSetId, T0.AddDays(-3));
        await SeedPlayInAsync(b.Id, ActiveSetId, T0.AddDays(-2));
        await SeedPlayInAsync(f.Id, ActiveSetId, T0.AddHours(-30));
        var placedAtUtc = T0.AddDays(-1);
        var playIn = await SeedPlayInAsync(a.Id, ActiveSetId, placedAtUtc, x);
        var removal = Guid.NewGuid();
        var registeredAtUtc = await RegisterRemovalAsync("tagrmtransfer", a.Id, removal);

        var result = await ReportRemovalAsync("tagrmtransfer", a.Id,
            Removal(removal, playIn, Snapshot((x, playIn)), removed: [], kept: [x]));

        AssertCounts(result, deleted: 0, transferred: 1, dropped: 0, swept: 0, deactivated: true);
        await using var verify = fixture.CreateDbContext();
        var row = Assert.Single(await verify.EmoteTagPlacements.AsNoTracking().Where(p => p.SevenTvEmoteId == x).ToListAsync());
        Assert.Equal((b.Id, ActiveSetId, removal, registeredAtUtc, placedAtUtc),
            (row.TagId, row.SevenTvEmoteSetId, row.OperationId, row.RegisteredAtUtc, row.PlacedAtUtc));
        var service = CreateService(verify);
        var entry = Assert.Single((await service.ListEntriesAsync("tagrmtransfer", b.Id, null)).Entries);
        Assert.Equal((true, (DateTime?)placedAtUtc, (Guid?)removal), (entry.PlacedByThisTag, entry.PlacedAtUtc, entry.PlacementOperationId));
        var tags = (await service.ListAsync("tagrmtransfer", null)).Tags.ToDictionary(t => t.Id);
        Assert.Equal((false, 0), (tags[a.Id].Active, tags[a.Id].PlacedCount));
        Assert.Equal((true, 1), (tags[b.Id].Active, tags[b.Id].PlacedCount));
        Assert.Equal(0, tags[c.Id].PlacedCount + tags[d.Id].PlacedCount + tags[e.Id].PlacedCount + tags[f.Id].PlacedCount);
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    [Fact]
    public async Task Removal_ATargetThatAlreadyHoldsTheEmote_KeepsItsRow_TheOwnRowGoes_CountedAsTransferred()
    {
        var channel = await SeedChannelAsync("tagrmalreadyheld");
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-2));
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-1));
        var x = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        await SeedEntryAsync(b.Id, x, "X", T0);
        var bPlayIn = await SeedPlayInAsync(b.Id, ActiveSetId, T0.AddDays(-2), x);
        var aPlayIn = await SeedPlayInAsync(a.Id, ActiveSetId, T0.AddDays(-1), x);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync("tagrmalreadyheld", a.Id, removal);

        var result = await ReportRemovalAsync("tagrmalreadyheld", a.Id,
            Removal(removal, aPlayIn, Snapshot((x, aPlayIn)), removed: [], kept: [x]));

        AssertCounts(result, deleted: 0, transferred: 1, dropped: 0, swept: 0, deactivated: true);
        Assert.Empty(await LoadPlacementsAsync(a.Id));
        var row = Assert.Single(await LoadPlacementsAsync(b.Id));
        // B's own row is untouched: its revision, anchor and placement time are still B's play-in.
        Assert.Equal((x, bPlayIn, T0.AddDays(-2), T0.AddDays(-2)),
            (row.SevenTvEmoteId, row.OperationId, row.RegisteredAtUtc, row.PlacedAtUtc));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // The target already has a row for X, but it expired (a leave observed after B's play-in, before
    // A's): it counts as absent. Keeping it would delete X's only valid placement; instead it is
    // rewritten like a fresh transfer — the removal as revision and anchor, A's placement time — and
    // holds by the read-time rule. Both ways in: a kept hit and an unhit row the sweep takes.
    [Theory]
    [InlineData("tagrmexpiredtgtkept", false)]
    [InlineData("tagrmexpiredtgtswept", true)]
    public async Task Removal_ATargetWhoseOwnRowExpired_GetsThatRowRewritten_AndItHolds(string channelName, bool swept)
    {
        var channel = await SeedChannelAsync(channelName);
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-2));
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-1));
        var x = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        await SeedEntryAsync(b.Id, x, "X", T0);
        await SeedPlayInAsync(b.Id, ActiveSetId, T0.AddDays(-7), x);
        await SeedObservationAsync(channel.Id, x, ActiveSetId, T0.AddDays(-6));
        var placedAtUtc = T0.AddDays(-1);
        var aPlayIn = await SeedPlayInAsync(a.Id, ActiveSetId, placedAtUtc, x);
        var before = await ListEntriesAsync(channelName, b.Id);
        Assert.False(Assert.Single(before.Entries).PlacedByThisTag);
        var removal = Guid.NewGuid();
        var registeredAtUtc = await RegisterRemovalAsync(channelName, a.Id, removal);

        var result = await ReportRemovalAsync(channelName, a.Id, swept
            ? Removal(removal, aPlayIn, Snapshot(), removed: [], kept: [])
            : Removal(removal, aPlayIn, Snapshot((x, aPlayIn)), removed: [], kept: [x]));

        AssertCounts(result, deleted: 0, transferred: swept ? 0 : 1, dropped: 0, swept: swept ? 1 : 0, deactivated: true);
        Assert.Empty(await LoadPlacementsAsync(a.Id));
        var row = Assert.Single(await LoadPlacementsAsync(b.Id));
        Assert.Equal((x, removal, registeredAtUtc, placedAtUtc),
            (row.SevenTvEmoteId, row.OperationId, row.RegisteredAtUtc, row.PlacedAtUtc));
        var entry = Assert.Single((await ListEntriesAsync(channelName, b.Id)).Entries);
        Assert.Equal((true, (DateTime?)placedAtUtc, (Guid?)removal), (entry.PlacedByThisTag, entry.PlacedAtUtc, entry.PlacementOperationId));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // No target: an inactive tag with the entry does not count, nor does an active tag without it.
    [Fact]
    public async Task Removal_AKeptHitWithoutAnActiveTagWithAnEntry_IsDropped()
    {
        var channel = await SeedChannelAsync("tagrmdropped");
        var inactiveWithEntry = await SeedTagAsync(channel.Id, "E", T0.AddDays(-3));
        var activeWithoutEntry = await SeedTagAsync(channel.Id, "D", T0.AddDays(-2));
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-1));
        var x = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        await SeedEntryAsync(inactiveWithEntry.Id, x, "X", T0);
        await SeedPlayInAsync(activeWithoutEntry.Id, ActiveSetId, T0.AddDays(-2));
        var playIn = await SeedPlayInAsync(a.Id, ActiveSetId, T0.AddDays(-1), x);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync("tagrmdropped", a.Id, removal);

        var result = await ReportRemovalAsync("tagrmdropped", a.Id,
            Removal(removal, playIn, Snapshot((x, playIn)), removed: [], kept: [x]));

        AssertCounts(result, deleted: 0, transferred: 0, dropped: 1, swept: 0, deactivated: true);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.SevenTvEmoteId == x));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // Kept by the person, but a leave was observed since the placement's anchor: the server has seen
    // the emote go and hands nothing over (spec 0a) — dropped, although B would qualify as a target.
    [Fact]
    public async Task Removal_AKeptHitThatExpiredSinceThePreview_IsDropped_NotTransferred()
    {
        var channel = await SeedChannelAsync("tagrmkeptexpired");
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-2));
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-1));
        var x = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        await SeedEntryAsync(b.Id, x, "X", T0);
        await SeedPlayInAsync(b.Id, ActiveSetId, T0.AddDays(-2));
        var playIn = await SeedPlayInAsync(a.Id, ActiveSetId, T0.AddDays(-7), x);
        await SeedObservationAsync(channel.Id, x, ActiveSetId, T0.AddDays(-6));
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync("tagrmkeptexpired", a.Id, removal);

        var result = await ReportRemovalAsync("tagrmkeptexpired", a.Id,
            Removal(removal, playIn, Snapshot((x, playIn)), removed: [], kept: [x]));

        AssertCounts(result, deleted: 0, transferred: 0, dropped: 1, swept: 0, deactivated: true);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.SevenTvEmoteId == x));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    [Theory]
    [InlineData("tagrmneitherdeact", true)]
    [InlineData("tagrmneitheractive", false)]
    public async Task Removal_AHitNeitherRemovedNorKept_IsDropped_WhetherOrNotTheTagIsDeactivated(string channelName, bool deactivated)
    {
        var channel = await SeedChannelAsync(channelName);
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-2));
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-1));
        var x = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        await SeedEntryAsync(b.Id, x, "X", T0);
        // B would be a transfer target — but a row the preview saw gone is dropped, never handed over.
        await SeedPlayInAsync(b.Id, ActiveSetId, T0.AddDays(-2));
        var playIn = await SeedPlayInAsync(a.Id, ActiveSetId, T0.AddDays(-1), x);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync(channelName, a.Id, removal);

        var result = await ReportRemovalAsync(channelName, a.Id,
            Removal(removal, deactivated ? playIn : Guid.NewGuid(), Snapshot((x, playIn)), removed: [], kept: []));

        AssertCounts(result, deleted: 0, transferred: 0, dropped: 1, swept: 0, deactivated);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.SevenTvEmoteId == x));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // The sweep: X is the only snapshot hit (kept → transferred). Y, Z and W were not in the
    // snapshot. Y is valid and B has an entry → transferred; Z is expired (a leave after its anchor)
    // → deleted although B has an entry; W is valid but no other tag has an entry → deleted. All
    // three count as swept, not as transferred or dropped.
    [Fact]
    public async Task Removal_TheSweep_TransfersValidUnhitRows_AndOnlyDeletesExpiredOrUnheldOnes()
    {
        var channel = await SeedChannelAsync("tagrmsweep");
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-2));
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-1));
        var x = NewSevenTvId();
        var y = NewSevenTvId();
        var z = NewSevenTvId();
        var w = NewSevenTvId();
        foreach (var id in new[] { x, y, z, w })
        {
            await SeedEntryAsync(a.Id, id, id, T0);
        }

        foreach (var id in new[] { x, y, z })
        {
            await SeedEntryAsync(b.Id, id, id, T0);
        }

        await SeedPlayInAsync(b.Id, ActiveSetId, T0.AddDays(-2));
        var playIn = await SeedPlayInAsync(a.Id, ActiveSetId, T0.AddDays(-7), x, y, z, w);
        await SeedObservationAsync(channel.Id, z, ActiveSetId, T0.AddDays(-6));
        var removal = Guid.NewGuid();
        var registeredAtUtc = await RegisterRemovalAsync("tagrmsweep", a.Id, removal);

        var result = await ReportRemovalAsync("tagrmsweep", a.Id,
            Removal(removal, playIn, Snapshot((x, playIn)), removed: [], kept: [x]));

        AssertCounts(result, deleted: 0, transferred: 1, dropped: 0, swept: 3, deactivated: true);
        Assert.Empty(await LoadPlacementsAsync(a.Id));
        var rows = await LoadPlacementsAsync(b.Id);
        Assert.Equal(new[] { x, y }.Order(StringComparer.Ordinal), rows.Select(r => r.SevenTvEmoteId));
        Assert.All(rows, r => Assert.Equal((removal, registeredAtUtc, T0.AddDays(-7)), (r.OperationId, r.RegisteredAtUtc, r.PlacedAtUtc)));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // The sweep candidate's only active other tag has no entry for X: a transfer would violate the
    // composite FK, so the row is deleted — and the report succeeds instead of failing with a 23503.
    [Fact]
    public async Task Removal_ASweptRowWhoseOnlyActiveOtherTagHasNoEntry_IsDeleted_NotTransferred()
    {
        var channel = await SeedChannelAsync("tagrmsweepnoentry");
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-2));
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-1));
        var x = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        await SeedPlayInAsync(b.Id, ActiveSetId, T0.AddDays(-2));
        var playIn = await SeedPlayInAsync(a.Id, ActiveSetId, T0.AddDays(-1), x);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync("tagrmsweepnoentry", a.Id, removal);

        var result = await ReportRemovalAsync("tagrmsweepnoentry", a.Id,
            Removal(removal, playIn, Snapshot(), removed: [], kept: []));

        AssertCounts(result, deleted: 0, transferred: 0, dropped: 0, swept: 1, deactivated: true);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.SevenTvEmoteId == x));
        Assert.True(await verify.EmoteTagActivations.AnyAsync(a => a.TagId == b.Id));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // Not deactivated (a newer play-in holds the activation): kept hits stay with the tag, unhit rows
    // are not swept, and nothing is transferred to B although it qualifies.
    [Fact]
    public async Task Removal_WithoutDeactivation_LeavesKeptHitsAndUnhitRowsAlone()
    {
        var channel = await SeedChannelAsync("tagrmstillactive");
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-2));
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-1));
        var x = NewSevenTvId();
        var z = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        await SeedEntryAsync(a.Id, z, "Z", T0);
        await SeedEntryAsync(b.Id, x, "X", T0);
        await SeedEntryAsync(b.Id, z, "Z", T0);
        await SeedPlayInAsync(b.Id, ActiveSetId, T0.AddDays(-2));
        var firstPlayIn = await SeedPlayInAsync(a.Id, ActiveSetId, T0.AddDays(-7), x);
        var newerPlayIn = await SeedOperationAsync(a.Id, ActiveSetId, T0.AddDays(-1));
        await SeedPlacementAsync(a.Id, z, ActiveSetId, newerPlayIn, T0.AddDays(-1), T0.AddDays(-1));
        await using (var db = fixture.CreateDbContext())
        {
            await db.EmoteTagActivations.Where(act => act.TagId == a.Id).ExecuteUpdateAsync(s => s.SetProperty(act => act.OperationId, newerPlayIn));
        }

        var removal = Guid.NewGuid();
        await RegisterRemovalAsync("tagrmstillactive", a.Id, removal);
        var before = await LoadPlacementsAsync(a.Id);

        var result = await ReportRemovalAsync("tagrmstillactive", a.Id,
            Removal(removal, firstPlayIn, Snapshot((x, firstPlayIn)), removed: [], kept: [x]));

        AssertCounts(result, deleted: 0, transferred: 0, dropped: 0, swept: 0, deactivated: false);
        Assert.Equal(before.Select(p => (p.SevenTvEmoteId, p.OperationId)), (await LoadPlacementsAsync(a.Id)).Select(p => (p.SevenTvEmoteId, p.OperationId)));
        Assert.Empty(await LoadPlacementsAsync(b.Id));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(newerPlayIn, (await verify.EmoteTagActivations.AsNoTracking().SingleAsync(act => act.TagId == a.Id)).OperationId);
        Assert.NotNull((await verify.EmoteTagOperations.AsNoTracking().SingleAsync(o => o.OperationId == removal)).AppliedAtUtc);
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    [Theory]
    [InlineData("tagrmnullread", true, false)]
    [InlineData("tagrmnoactivation", false, true)]
    [InlineData("tagrmneither", false, false)]
    public async Task Removal_IsNotDeactivated_WithANullActivationOperationId_OrWithoutAnActivation(
        string channelName, bool activationExists, bool reportNamesOne)
    {
        var channel = await SeedChannelAsync(channelName);
        var tag = await SeedTagAsync(channel.Id, "A");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var playIn = await SeedPlayInAsync(tag.Id, ActiveSetId, T0.AddDays(-7), x);
        if (!activationExists)
        {
            await using var db = fixture.CreateDbContext();
            // Not a reachable state by the service's own writes; the report must still not deactivate
            // what is not there, and must not fail.
            await db.EmoteTagActivations.Where(a => a.TagId == tag.Id).ExecuteDeleteAsync();
        }

        var removal = Guid.NewGuid();
        await RegisterRemovalAsync(channelName, tag.Id, removal);

        var result = await ReportRemovalAsync(channelName, tag.Id,
            Removal(removal, reportNamesOne ? playIn : null, Snapshot((x, playIn)), removed: [x], kept: []));

        AssertCounts(result, deleted: 1, transferred: 0, dropped: 0, swept: 0, deactivated: false);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(activationExists, await verify.EmoteTagActivations.AnyAsync(a => a.TagId == tag.Id));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    [Fact]
    public async Task Removal_Replayed_WritesNothing_EvenWithAnotherBody()
    {
        var channel = await SeedChannelAsync("tagrmreplay");
        var tag = await SeedTagAsync(channel.Id, "A");
        var x = NewSevenTvId();
        var y = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        await SeedEntryAsync(tag.Id, y, "Y", T0);
        var playIn = await SeedPlayInAsync(tag.Id, ActiveSetId, T0.AddDays(-7), x, y);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync("tagrmreplay", tag.Id, removal);
        var first = await ReportRemovalAsync("tagrmreplay", tag.Id,
            Removal(removal, Guid.NewGuid(), Snapshot((x, playIn)), removed: [x], kept: []));
        var before = await TagTablesAsync(channel, tag.Id);

        var replay = await ReportRemovalAsync("tagrmreplay", tag.Id,
            Removal(removal, playIn, Snapshot((y, playIn)), removed: [y], kept: []));

        AssertCounts(first, deleted: 1, transferred: 0, dropped: 0, swept: 0, deactivated: false);
        Assert.Equal(new TagRemovalReportResult(TagReportStatus.Ok, true, 0, 0, 0, 0, false), replay);
        Assert.Equal(before, await TagTablesAsync(channel, tag.Id));
        Assert.Single(await LoadPlacementsAsync(tag.Id));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    [Fact]
    public async Task Removal_IsAuditedAsRemoved_WithTheDeletedCount_WithoutTheTagName()
    {
        var channel = await SeedChannelAsync("tagrmaudit");
        var tag = await SeedTagAsync(channel.Id, "SecretStronghold");
        var x = NewSevenTvId();
        var y = NewSevenTvId();
        var z = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        await SeedEntryAsync(tag.Id, y, "Y", T0);
        await SeedEntryAsync(tag.Id, z, "Z", T0);
        var playIn = await SeedPlayInAsync(tag.Id, ActiveSetId, T0.AddDays(-7), x, y, z);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync("tagrmaudit", tag.Id, removal);

        var result = await ReportRemovalAsync("tagrmaudit", tag.Id,
            Removal(removal, playIn, Snapshot((x, playIn), (y, playIn), (z, playIn)), removed: [x, y], kept: []));

        AssertCounts(result, deleted: 2, transferred: 0, dropped: 1, swept: 0, deactivated: true);
        var entry = Assert.Single(await LoadAuditAsync("tagrmaudit"));
        Assert.Equal((AuditActions.TagRemoved, "emoteTag", tag.Id.ToString(CultureInfo.InvariantCulture), Actor.TwitchUserId),
            (entry.Action, entry.TargetType, entry.TargetId, entry.ActorTwitchUserId));
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        var root = details.RootElement;
        Assert.Equal(["emoteCount", "emoteSetId", "operationId", "tagId"],
            root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal((tag.Id, ActiveSetId, removal, 2),
            (root.GetProperty("tagId").GetInt64(), root.GetProperty("emoteSetId").GetString(),
                root.GetProperty("operationId").GetGuid(), root.GetProperty("emoteCount").GetInt32()));
        Assert.DoesNotContain("secret", entry.DetailsJson, StringComparison.OrdinalIgnoreCase);
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // F30: the transferred row points at A's removal operation, and operations cascade with their
    // tag. Deleting A afterwards must not make B's placement stop holding — its anchor is its own.
    [Fact]
    public async Task Removal_ATransferredPlacement_SurvivesTheDeletionOfTheTagItCameFrom()
    {
        var channel = await SeedChannelAsync("tagrmgiverdeleted");
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-2));
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-1));
        var x = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        await SeedEntryAsync(b.Id, x, "X", T0);
        await SeedPlayInAsync(b.Id, ActiveSetId, T0.AddDays(-2));
        var playIn = await SeedPlayInAsync(a.Id, ActiveSetId, T0.AddDays(-1), x);
        var removal = Guid.NewGuid();
        var registeredAtUtc = await RegisterRemovalAsync("tagrmgiverdeleted", a.Id, removal);
        var transfer = await ReportRemovalAsync("tagrmgiverdeleted", a.Id,
            Removal(removal, playIn, Snapshot((x, playIn)), removed: [], kept: [x]));
        AssertCounts(transfer, deleted: 0, transferred: 1, dropped: 0, swept: 0, deactivated: true);

        Assert.Equal(EmoteTagMutationStatus.Ok, await DeleteAsync("tagrmgiverdeleted", a.Id));

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagOperations.AnyAsync(o => o.OperationId == removal));
        var service = CreateService(verify);
        var entry = Assert.Single((await service.ListEntriesAsync("tagrmgiverdeleted", b.Id, null)).Entries);
        Assert.Equal((true, (Guid?)removal), (entry.PlacedByThisTag, entry.PlacementOperationId));
        var summary = Assert.Single((await service.ListAsync("tagrmgiverdeleted", null)).Tags);
        Assert.Equal((b.Id, true, 1), (summary.Id, summary.Active, summary.PlacedCount));
        var row = Assert.Single(await LoadPlacementsAsync(b.Id));
        Assert.Equal(registeredAtUtc, row.RegisteredAtUtc);
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // Counterexample 1: B only plays in emotes A already added. A = {X, Y} played in; B = {X} played
    // in with nothing to add (activation only). Clearing A: Y removed, X kept → wanders to B, A
    // inactive. Clearing B: X removed, B inactive. Both play-ins and both reports are the real ones.
    [Fact]
    public async Task CounterExample1_BLeavesNoTraceOfItsOwn_YetTakesOverXWhenAIsCleared()
    {
        var channel = await SeedChannelAsync("tagce1");
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-2));
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-1));
        var x = NewSevenTvId();
        var y = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        await SeedEntryAsync(a.Id, y, "Y", T0);
        await SeedEntryAsync(b.Id, x, "X", T0);
        var aPlayIn = Guid.NewGuid();
        await RegisterPlayInAsync("tagce1", a.Id, aPlayIn);
        Assert.Equal(2, (await ReportAsync("tagce1", a.Id, aPlayIn, ActiveSetId, x, y)).RecordedCount);
        var bPlayIn = Guid.NewGuid();
        await RegisterPlayInAsync("tagce1", b.Id, bPlayIn);
        Assert.Equal(0, (await ReportAsync("tagce1", b.Id, bPlayIn, ActiveSetId)).RecordedCount);

        var aPreview = await ListEntriesAsync("tagce1", a.Id);
        Assert.Equal([b.Id], aPreview.Entries.Single(e => e.SevenTvEmoteId == x).HeldByActiveTags.Select(t => t.Id));
        var aRemoval = Guid.NewGuid();
        await RegisterRemovalAsync("tagce1", a.Id, aRemoval);
        var aResult = await ReportRemovalAsync("tagce1", a.Id,
            Removal(aRemoval, aPreview.ActivationOperationId, SnapshotOf(aPreview), removed: [y], kept: [x]));
        AssertCounts(aResult, deleted: 1, transferred: 1, dropped: 0, swept: 0, deactivated: true);
        await fixture.AssertPlacementInvariantsAsync(channel.Id);

        var bPreview = await ListEntriesAsync("tagce1", b.Id);
        var bEntry = Assert.Single(bPreview.Entries);
        Assert.Equal((true, (Guid?)aRemoval, 0), (bEntry.PlacedByThisTag, bEntry.PlacementOperationId, bEntry.HeldByActiveTags.Count));
        var bRemoval = Guid.NewGuid();
        await RegisterRemovalAsync("tagce1", b.Id, bRemoval);
        var bResult = await ReportRemovalAsync("tagce1", b.Id,
            Removal(bRemoval, bPreview.ActivationOperationId, SnapshotOf(bPreview), removed: [x], kept: []));

        AssertCounts(bResult, deleted: 1, transferred: 0, dropped: 0, swept: 0, deactivated: true);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == a.Id || p.TagId == b.Id));
        Assert.False(await verify.EmoteTagActivations.AnyAsync(act => act.TagId == a.Id || act.TagId == b.Id));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // Counterexample 2: A and B both {X, Y}; A played in, B's play-in was a no-op. Clearing A with
    // nothing checked is a report without a delete run: both placements wander to B. Clearing B then
    // removes both.
    [Fact]
    public async Task CounterExample2_ClearingAWithNothingChecked_HandsBothPlacementsToB_NoDeadEnd()
    {
        var channel = await SeedChannelAsync("tagce2");
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-2));
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-1));
        var x = NewSevenTvId();
        var y = NewSevenTvId();
        foreach (var tag in new[] { a, b })
        {
            await SeedEntryAsync(tag.Id, x, "X", T0);
            await SeedEntryAsync(tag.Id, y, "Y", T0);
        }

        var aPlayIn = Guid.NewGuid();
        await RegisterPlayInAsync("tagce2", a.Id, aPlayIn);
        await ReportAsync("tagce2", a.Id, aPlayIn, ActiveSetId, x, y);
        var bPlayIn = Guid.NewGuid();
        await RegisterPlayInAsync("tagce2", b.Id, bPlayIn);
        await ReportAsync("tagce2", b.Id, bPlayIn, ActiveSetId);

        var aPreview = await ListEntriesAsync("tagce2", a.Id);
        var aRemoval = Guid.NewGuid();
        await RegisterRemovalAsync("tagce2", a.Id, aRemoval);
        var aResult = await ReportRemovalAsync("tagce2", a.Id,
            Removal(aRemoval, aPreview.ActivationOperationId, SnapshotOf(aPreview), removed: [], kept: [x, y]));
        AssertCounts(aResult, deleted: 0, transferred: 2, dropped: 0, swept: 0, deactivated: true);
        Assert.Equal(new[] { x, y }.Order(StringComparer.Ordinal), (await LoadPlacementsAsync(b.Id)).Select(p => p.SevenTvEmoteId));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);

        var bPreview = await ListEntriesAsync("tagce2", b.Id);
        Assert.All(bPreview.Entries, e => Assert.Equal((true, 0), (e.PlacedByThisTag, e.HeldByActiveTags.Count)));
        var bRemoval = Guid.NewGuid();
        await RegisterRemovalAsync("tagce2", b.Id, bRemoval);
        var bResult = await ReportRemovalAsync("tagce2", b.Id,
            Removal(bRemoval, bPreview.ActivationOperationId, SnapshotOf(bPreview), removed: [x, y], kept: []));

        AssertCounts(bResult, deleted: 2, transferred: 0, dropped: 0, swept: 0, deactivated: true);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == a.Id || p.TagId == b.Id));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // Counterexample 3: a removal of A (snapshot {X, Y}) hangs in its retry; meanwhile A is played in
    // again and adds Z. The late report deletes only X and Y, and does not deactivate: the activation
    // carries the newer operation. Z stays placed, A stays active.
    [Fact]
    public async Task CounterExample3_ALateRemovalReport_TouchesOnlyItsSnapshot_AndLeavesTheNewerActivation()
    {
        var channel = await SeedChannelAsync("tagce3");
        var a = await SeedTagAsync(channel.Id, "A");
        var x = NewSevenTvId();
        var y = NewSevenTvId();
        var z = NewSevenTvId();
        foreach (var id in new[] { x, y, z })
        {
            await SeedEntryAsync(a.Id, id, id, T0);
        }

        var firstPlayIn = Guid.NewGuid();
        await RegisterPlayInAsync("tagce3", a.Id, firstPlayIn);
        await ReportAsync("tagce3", a.Id, firstPlayIn, ActiveSetId, x, y);
        var preview = await ListEntriesAsync("tagce3", a.Id);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync("tagce3", a.Id, removal);
        // The run removed X and Y from 7TV; before its report lands, A is played in again with Z.
        var secondPlayIn = Guid.NewGuid();
        await RegisterPlayInAsync("tagce3", a.Id, secondPlayIn);
        await ReportAsync("tagce3", a.Id, secondPlayIn, ActiveSetId, z);

        var result = await ReportRemovalAsync("tagce3", a.Id,
            Removal(removal, preview.ActivationOperationId, SnapshotOf(preview), removed: [x, y], kept: []));

        AssertCounts(result, deleted: 2, transferred: 0, dropped: 0, swept: 0, deactivated: false);
        var after = await ListEntriesAsync("tagce3", a.Id);
        Assert.Equal((Guid?)secondPlayIn, after.ActivationOperationId);
        Assert.Equal([z], after.Entries.Where(e => e.PlacedByThisTag).Select(e => e.SevenTvEmoteId));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // Counterexample 5: removal R₁ of A (snapshot X with revision P₁) hangs; meanwhile X leaves (the
    // sync observes it) and A is played in again with X (revision P₂). The late R₁ names X with P₁ —
    // no hit — and its activation P₁ — no deactivation. The new placement is untouched and holds.
    [Fact]
    public async Task CounterExample5_ALateRemovalReport_DoesNotTouchARePlacedEmoteOfAnotherRevision()
    {
        var channel = await SeedChannelAsync("tagce5");
        var a = await SeedTagAsync(channel.Id, "A");
        var x = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        var firstPlayIn = Guid.NewGuid();
        await RegisterPlayInAsync("tagce5", a.Id, firstPlayIn);
        await ReportAsync("tagce5", a.Id, firstPlayIn, ActiveSetId, x);
        var preview = await ListEntriesAsync("tagce5", a.Id);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync("tagce5", a.Id, removal);
        await SeedObservationAsync(channel.Id, x, ActiveSetId, DateTime.UtcNow);
        var secondPlayIn = Guid.NewGuid();
        await RegisterPlayInAsync("tagce5", a.Id, secondPlayIn);
        Assert.Equal(1, (await ReportAsync("tagce5", a.Id, secondPlayIn, ActiveSetId, x)).AlreadyRecordedCount);

        var result = await ReportRemovalAsync("tagce5", a.Id,
            Removal(removal, preview.ActivationOperationId, SnapshotOf(preview), removed: [x], kept: []));

        AssertCounts(result, deleted: 0, transferred: 0, dropped: 0, swept: 0, deactivated: false);
        var after = await ListEntriesAsync("tagce5", a.Id);
        var entry = Assert.Single(after.Entries);
        Assert.Equal(((Guid?)secondPlayIn, true, (Guid?)secondPlayIn), (after.ActivationOperationId, entry.PlacedByThisTag, entry.PlacementOperationId));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // Counterexample 5 under F33: an OLDER play-in report (P_old, registered first, reported last)
    // lands after the newer one and overwrites revision, anchor and activation with P_old. A removal
    // whose preview read P_new then hits nothing and does not deactivate — nothing is deleted, the
    // tag stays active with X placed. Fail-safe: the next preview reads P_old, the live read no
    // longer shows X (the run removed it), and a second clearing drops the row and deactivates.
    [Fact]
    public async Task CounterExample5_UnderF33_AnOlderPlayInLandingLate_MakesTheRemovalMissAndNotDeactivate_RecoveredByTheNextClearing()
    {
        var channel = await SeedChannelAsync("tagce5f33");
        var a = await SeedTagAsync(channel.Id, "A");
        var x = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        var oldPlayIn = Guid.NewGuid();
        var oldRegisteredAtUtc = await RegisterPlayInAsync("tagce5f33", a.Id, oldPlayIn);
        var newPlayIn = Guid.NewGuid();
        await RegisterPlayInAsync("tagce5f33", a.Id, newPlayIn);
        await ReportAsync("tagce5f33", a.Id, newPlayIn, ActiveSetId, x);
        var preview = await ListEntriesAsync("tagce5f33", a.Id);
        Assert.Equal((Guid?)newPlayIn, preview.ActivationOperationId);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync("tagce5f33", a.Id, removal);
        // The older play-in's report lands late (rule 10: the upsert overwrites).
        Assert.Equal(1, (await ReportAsync("tagce5f33", a.Id, oldPlayIn, ActiveSetId, x)).AlreadyRecordedCount);

        var result = await ReportRemovalAsync("tagce5f33", a.Id,
            Removal(removal, preview.ActivationOperationId, SnapshotOf(preview), removed: [x], kept: []));

        AssertCounts(result, deleted: 0, transferred: 0, dropped: 0, swept: 0, deactivated: false);
        var row = Assert.Single(await LoadPlacementsAsync(a.Id));
        Assert.Equal((x, oldPlayIn, oldRegisteredAtUtc), (row.SevenTvEmoteId, row.OperationId, row.RegisteredAtUtc));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);

        // Recovery: the next preview reads what is there; X is no longer in the set, so it is neither
        // removed nor kept.
        var secondPreview = await ListEntriesAsync("tagce5f33", a.Id);
        Assert.Equal((Guid?)oldPlayIn, secondPreview.ActivationOperationId);
        var secondRemoval = Guid.NewGuid();
        await RegisterRemovalAsync("tagce5f33", a.Id, secondRemoval);
        var recovery = await ReportRemovalAsync("tagce5f33", a.Id,
            Removal(secondRemoval, secondPreview.ActivationOperationId, SnapshotOf(secondPreview), removed: [], kept: []));

        AssertCounts(recovery, deleted: 0, transferred: 0, dropped: 1, swept: 0, deactivated: true);
        Assert.Empty(await LoadPlacementsAsync(a.Id));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // Counterexample 7: A and B active in S, both contain X, only A holds (A, X, S). Both previews
    // are read at the same time. A reports first: X kept → wanders to (B, X, S) with A's removal as
    // revision, A inactive. B reports with an empty snapshot and its activation: deactivated, and the
    // sweep finds (B, X, S) — no active holder is left, so it is deleted. Both inactive, no placement,
    // X stays entered in both tags. Concurrently, B's report queues on the channel lock behind A's.
    [Theory]
    [InlineData("tagce7seq", false)]
    [InlineData("tagce7race", true)]
    public async Task CounterExample7_OverlappingRemovals_LeaveNoPlacementWithAnInactiveHolder(string channelName, bool concurrently)
    {
        var channel = await SeedChannelAsync(channelName);
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-2));
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-1));
        var x = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        await SeedEntryAsync(b.Id, x, "X", T0);
        var aPlayIn = Guid.NewGuid();
        await RegisterPlayInAsync(channelName, a.Id, aPlayIn);
        await ReportAsync(channelName, a.Id, aPlayIn, ActiveSetId, x);
        var bPlayIn = Guid.NewGuid();
        await RegisterPlayInAsync(channelName, b.Id, bPlayIn);
        await ReportAsync(channelName, b.Id, bPlayIn, ActiveSetId);

        var aPreview = await ListEntriesAsync(channelName, a.Id);
        var bPreview = await ListEntriesAsync(channelName, b.Id);
        Assert.Equal([b.Id], aPreview.Entries.Single().HeldByActiveTags.Select(t => t.Id));
        Assert.False(bPreview.Entries.Single().PlacedByThisTag);
        Assert.Equal([a.Id], bPreview.Entries.Single().PlacedByOtherTags.Select(t => t.Id));
        var aRemoval = Guid.NewGuid();
        await RegisterRemovalAsync(channelName, a.Id, aRemoval);
        var bRemoval = Guid.NewGuid();
        await RegisterRemovalAsync(channelName, b.Id, bRemoval);
        var aBody = Removal(aRemoval, aPreview.ActivationOperationId, SnapshotOf(aPreview), removed: [], kept: [x]);
        var bBody = Removal(bRemoval, bPreview.ActivationOperationId, SnapshotOf(bPreview), removed: [], kept: []);

        TagRemovalReportResult aResult, bResult;
        if (concurrently)
        {
            (aResult, bResult) = await RaceAsync(
                channelName,
                "EmoteTagPlacements",
                service => service.ReportRemovalAsync(channelName, a.Id, aBody, Actor),
                service => service.ReportRemovalAsync(channelName, b.Id, bBody, Actor));
        }
        else
        {
            aResult = await ReportRemovalAsync(channelName, a.Id, aBody);
            bResult = await ReportRemovalAsync(channelName, b.Id, bBody);
        }

        AssertCounts(aResult, deleted: 0, transferred: 1, dropped: 0, swept: 0, deactivated: true);
        AssertCounts(bResult, deleted: 0, transferred: 0, dropped: 0, swept: 1, deactivated: true);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == a.Id || p.TagId == b.Id));
        Assert.False(await verify.EmoteTagActivations.AnyAsync(act => act.TagId == a.Id || act.TagId == b.Id));
        Assert.Equal(2, await verify.EmoteTagEntries.CountAsync(e => e.SevenTvEmoteId == x));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // Race (i): a play-in report holds the channel lock and has read the entry for X; RemoveEntries(X)
    // starts in parallel and waits. The report places X and commits; the removal goes through and
    // the FK cascade takes the placement with the entry.
    [Fact]
    public async Task Race_APlayInReportThenTheEntryRemoval_LeavesNeitherEntryNorPlacement()
    {
        var channel = await SeedChannelAsync("tagracereportremove");
        var tag = await SeedTagAsync(channel.Id, "A");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var playIn = Guid.NewGuid();
        await RegisterPlayInAsync("tagracereportremove", tag.Id, playIn);

        var (report, removal) = await RaceAsync(
            "tagracereportremove",
            "EmoteTagPlacements",
            service => service.ReportPlacementsAsync("tagracereportremove", tag.Id, new TagPlacementReport(playIn, ActiveSetId, [x]), Actor),
            service => service.RemoveEntriesAsync("tagracereportremove", tag.Id, [x]));

        Assert.Equal((1, 0), (report.RecordedCount, report.NotTaggedIds.Count));
        Assert.Equal(1, removal.RemovedCount);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagEntries.AnyAsync(e => e.TagId == tag.Id));
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == tag.Id));
        Assert.True(await verify.EmoteTagActivations.AnyAsync(a => a.TagId == tag.Id));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // Race (ii): the removal of the entry goes first; the report, queued on the channel lock, sees no
    // entry and reports X as not tagged.
    [Fact]
    public async Task Race_TheEntryRemovalThenAPlayInReport_ReportsTheEmoteAsNotTagged()
    {
        var channel = await SeedChannelAsync("tagraceremovereport");
        var tag = await SeedTagAsync(channel.Id, "A");
        var x = NewSevenTvId();
        await SeedEntryAsync(tag.Id, x, "X", T0);
        var playIn = Guid.NewGuid();
        await RegisterPlayInAsync("tagraceremovereport", tag.Id, playIn);

        var (removal, report) = await RaceAsync(
            "tagraceremovereport",
            "EmoteTagEntries",
            service => service.RemoveEntriesAsync("tagraceremovereport", tag.Id, [x]),
            service => service.ReportPlacementsAsync("tagraceremovereport", tag.Id, new TagPlacementReport(playIn, ActiveSetId, [x]), Actor));

        Assert.Equal(1, removal.RemovedCount);
        Assert.Equal(0, report.RecordedCount);
        Assert.Equal([x], report.NotTaggedIds);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == tag.Id));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // Race (iii): a removal report wants to hand (A, X, S) to B while B's entry for X is being taken
    // out. Serialized by the channel lock: removal first → B is no candidate any more → dropped;
    // report first → the transfer commits and the removal's cascade takes it away again. Either way
    // no placement without an entry.
    [Theory]
    [InlineData("tagraceiiireport", true)]
    [InlineData("tagraceiiiremove", false)]
    public async Task Race_ARemovalReportTransfer_AgainstTheTargetsEntryRemoval_EndsInCommitOrder(string channelName, bool reportFirst)
    {
        var channel = await SeedChannelAsync(channelName);
        var b = await SeedTagAsync(channel.Id, "B", T0.AddDays(-2));
        var a = await SeedTagAsync(channel.Id, "A", T0.AddDays(-1));
        var x = NewSevenTvId();
        await SeedEntryAsync(a.Id, x, "X", T0);
        await SeedEntryAsync(b.Id, x, "X", T0);
        await SeedPlayInAsync(b.Id, ActiveSetId, T0.AddDays(-2));
        var playIn = await SeedPlayInAsync(a.Id, ActiveSetId, T0.AddDays(-1), x);
        var removal = Guid.NewGuid();
        await RegisterRemovalAsync(channelName, a.Id, removal);
        var body = Removal(removal, playIn, Snapshot((x, playIn)), removed: [], kept: [x]);

        TagRemovalReportResult report;
        EmoteTagRemoveEntriesResult entryRemoval;
        if (reportFirst)
        {
            (report, entryRemoval) = await RaceAsync(
                channelName,
                "EmoteTagPlacements",
                service => service.ReportRemovalAsync(channelName, a.Id, body, Actor),
                service => service.RemoveEntriesAsync(channelName, b.Id, [x]));
        }
        else
        {
            (entryRemoval, report) = await RaceAsync(
                channelName,
                "EmoteTagEntries",
                service => service.RemoveEntriesAsync(channelName, b.Id, [x]),
                service => service.ReportRemovalAsync(channelName, a.Id, body, Actor));
        }

        Assert.Equal(1, entryRemoval.RemovedCount);
        AssertCounts(report, deleted: 0, transferred: reportFirst ? 1 : 0, dropped: reportFirst ? 0 : 1, swept: 0, deactivated: true);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.SevenTvEmoteId == x));
        Assert.False(await verify.EmoteTagEntries.AnyAsync(e => e.TagId == b.Id));
        await fixture.AssertPlacementInvariantsAsync(channel.Id);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static EmoteTagService CreateService(AppDbContext db) => new(db);

    private async Task<EmoteTagMutationResult> CreateAsync(string channelName, string? name)
    {
        await using var db = fixture.CreateDbContext();
        return await CreateService(db).CreateAsync(channelName, name, Actor);
    }

    private async Task<EmoteTagMutationResult> RenameAsync(string channelName, long tagId, string? name)
    {
        await using var db = fixture.CreateDbContext();
        return await CreateService(db).RenameAsync(channelName, tagId, name, Actor);
    }

    private async Task<EmoteTagMutationStatus> DeleteAsync(string channelName, long tagId)
    {
        await using var db = fixture.CreateDbContext();
        return await CreateService(db).DeleteAsync(channelName, tagId, Actor);
    }

    private async Task<EmoteTagAddEntriesResult> AddAsync(string channelName, long tagId, IReadOnlyList<string>? ids)
    {
        await using var db = fixture.CreateDbContext();
        return await CreateService(db).AddEntriesAsync(channelName, tagId, ids);
    }

    private async Task<EmoteTagRemoveEntriesResult> RemoveAsync(string channelName, long tagId, IReadOnlyList<string>? ids)
    {
        await using var db = fixture.CreateDbContext();
        return await CreateService(db).RemoveEntriesAsync(channelName, tagId, ids);
    }

    private async Task<TagOperationRegistrationResult> RegisterAsync(
        string channelName, long tagId, Guid operationId, string emoteSetId = ActiveSetId, string kind = EmoteTagOperationKind.PlayIn)
    {
        await using var db = fixture.CreateDbContext();
        return await CreateService(db).RegisterOperationAsync(
            channelName, tagId, new RegisterTagOperationRequest(operationId, kind, emoteSetId));
    }

    // A registered play-in, as the browser starts every run; returns the server's stamp.
    private async Task<DateTime> RegisterPlayInAsync(string channelName, long tagId, Guid operationId, string emoteSetId = ActiveSetId)
    {
        var registration = await RegisterAsync(channelName, tagId, operationId, emoteSetId);
        Assert.Equal(TagOperationRegistrationStatus.Ok, registration.Status);
        return registration.RegisteredAtUtc!.Value;
    }

    private async Task<TagPlacementReportResult> ReportAsync(
        string channelName, long tagId, Guid operationId, string emoteSetId, params string[] sevenTvEmoteIds)
    {
        await using var db = fixture.CreateDbContext();
        return await CreateService(db).ReportPlacementsAsync(
            channelName, tagId, new TagPlacementReport(operationId, emoteSetId, sevenTvEmoteIds), Actor);
    }

    // A registered removal, as the browser starts every clearing; returns the server's stamp.
    private async Task<DateTime> RegisterRemovalAsync(string channelName, long tagId, Guid operationId, string emoteSetId = ActiveSetId)
    {
        var registration = await RegisterAsync(channelName, tagId, operationId, emoteSetId, EmoteTagOperationKind.Removal);
        Assert.Equal(TagOperationRegistrationStatus.Ok, registration.Status);
        return registration.RegisteredAtUtc!.Value;
    }

    private async Task<TagRemovalReportResult> ReportRemovalAsync(string channelName, long tagId, TagRemovalReport report)
    {
        await using var db = fixture.CreateDbContext();
        return await CreateService(db).ReportRemovalAsync(channelName, tagId, report, Actor);
    }

    // The preview a removal run reads: the entry read of the active set.
    private async Task<EmoteTagEntriesResult> ListEntriesAsync(string channelName, long tagId)
    {
        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db).ListEntriesAsync(channelName, tagId, null);
        Assert.Equal(EmoteTagEntriesStatus.Ok, result.Status);
        return result;
    }

    private static TagRemovalReport Removal(
        Guid operationId,
        Guid? activationOperationId,
        IReadOnlyList<TagPlacementSnapshotEntry> snapshot,
        IReadOnlyList<string> removed,
        IReadOnlyList<string> kept,
        string emoteSetId = ActiveSetId) =>
        new(operationId, emoteSetId, activationOperationId, snapshot, removed, kept);

    private static List<TagPlacementSnapshotEntry> Snapshot(params (string SevenTvEmoteId, Guid Revision)[] entries) =>
        entries.Select(e => new TagPlacementSnapshotEntry(e.SevenTvEmoteId, e.Revision)).ToList();

    // What the browser puts into the snapshot: the preview's own valid placements with their revision.
    private static List<TagPlacementSnapshotEntry> SnapshotOf(EmoteTagEntriesResult preview) =>
        preview.Entries
            .Where(e => e.PlacedByThisTag)
            .Select(e => new TagPlacementSnapshotEntry(e.SevenTvEmoteId, e.PlacementOperationId!.Value))
            .ToList();

    private async Task<List<EmoteTagPlacement>> LoadPlacementsAsync(long tagId)
    {
        await using var db = fixture.CreateDbContext();
        return (await db.EmoteTagPlacements.AsNoTracking().Where(p => p.TagId == tagId).ToListAsync())
            .OrderBy(p => p.SevenTvEmoteId, StringComparer.Ordinal)
            .ToList();
    }

    private static void AssertCounts(
        TagRemovalReportResult result, int deleted, int transferred, int dropped, int swept, bool deactivated) =>
        Assert.Equal(
            new TagRemovalReportResult(TagReportStatus.Ok, false, deleted, transferred, dropped, swept, deactivated),
            result);

    // Everything a report can write for one tag, serialized for a before/after comparison.
    private async Task<string> TagTablesAsync(Channel channel, long tagId)
    {
        await using var db = fixture.CreateDbContext();
        var placements = await db.EmoteTagPlacements.AsNoTracking()
            .Where(p => p.TagId == tagId)
            .OrderBy(p => p.SevenTvEmoteSetId).ThenBy(p => p.SevenTvEmoteId)
            .Select(p => new { p.SevenTvEmoteId, p.SevenTvEmoteSetId, p.OperationId, p.PlacedAtUtc, p.RegisteredAtUtc })
            .ToListAsync();
        var activations = await db.EmoteTagActivations.AsNoTracking()
            .Where(a => a.TagId == tagId)
            .OrderBy(a => a.SevenTvEmoteSetId)
            .Select(a => new { a.SevenTvEmoteSetId, a.OperationId, a.ActivatedAtUtc })
            .ToListAsync();
        var operations = await db.EmoteTagOperations.AsNoTracking()
            .Where(o => o.TagId == tagId)
            .OrderBy(o => o.OperationId)
            .Select(o => new { o.OperationId, o.RegisteredAtUtc, o.AppliedAtUtc })
            .ToListAsync();
        var auditCount = await db.AuditLogEntries.CountAsync(e => e.ChannelName == channel.ChannelName);
        return JsonSerializer.Serialize(new { placements, activations, operations, auditCount });
    }

    private async Task AssertNoReportWrittenAsync(Channel channel, long tagId)
    {
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.EmoteTagPlacements.AnyAsync(p => p.TagId == tagId));
        Assert.False(await db.EmoteTagActivations.AnyAsync(a => a.TagId == tagId));
        Assert.False(await db.AuditLogEntries.AnyAsync(e => e.ChannelName == channel.ChannelName));
    }

    private async Task<(TFirst First, TSecond Second)> RaceAsync<TFirst, TSecond>(
        string name,
        string heldTable,
        Func<EmoteTagService, Task<TFirst>> first,
        Func<EmoteTagService, Task<TSecond>> second)
    {
        await using var hold = await HoldTableAsync(heldTable);

        var firstTag = $"{name}-1";
        await using var firstDb = fixture.CreateTaggedDbContext(firstTag);
        var firstCall = first(CreateService(firstDb));
        await fixture.WaitUntilBlockedOnLockAsync(firstTag, firstCall);

        var secondTag = $"{name}-2";
        await using var secondDb = fixture.CreateTaggedDbContext(secondTag);
        var secondCall = second(CreateService(secondDb));
        await fixture.WaitUntilBlockedOnLockAsync(secondTag, secondCall);

        await hold.ReleaseAsync();
        return (await firstCall, await secondCall);
    }

    private async Task<TableHold> HoldTableAsync(string table)
    {
        var db = fixture.CreateDbContext();
        await db.Database.BeginTransactionAsync();
        // Table names are the test's own constants, never input.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"""LOCK TABLE "{table}" IN SHARE MODE""");
#pragma warning restore EF1002
        return new TableHold(db);
    }

    private async Task<Channel> SeedChannelAsync(string name, string activeEmoteSetId = ActiveSetId)
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = name, IsBotActive = true, ActiveEmoteSetId = activeEmoteSetId };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private async Task<Emote> SeedEmoteAsync(string channelId, string name, bool isArchived = false)
    {
        await using var db = fixture.CreateDbContext();
        var id = NewSevenTvId();
        var emote = new Emote
        {
            ChannelId = channelId,
            SevenTvEmoteId = id,
            Name = name,
            ImageUrl = $"https://cdn.7tv.app/emote/{id}/2x.webp",
            IsArchived = isArchived,
            ArchivedAt = isArchived ? DateTime.UtcNow : null
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private async Task ArchiveAsync(string emoteId)
    {
        await using var db = fixture.CreateDbContext();
        await db.Emotes.Where(e => e.Id == emoteId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsArchived, true).SetProperty(e => e.ArchivedAt, DateTime.UtcNow));
    }

    private async Task RenameEmoteAsync(string emoteId, string name)
    {
        await using var db = fixture.CreateDbContext();
        await db.Emotes.Where(e => e.Id == emoteId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Name, name));
    }

    private async Task<EmoteTag> SeedTagAsync(string channelId, string name, DateTime? createdAtUtc = null)
    {
        await using var db = fixture.CreateDbContext();
        var tag = new EmoteTag
        {
            ChannelId = channelId,
            Name = name,
            NormalizedName = EmoteTagName.Normalize(name),
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow
        };
        db.EmoteTags.Add(tag);
        await db.SaveChangesAsync();
        return tag;
    }

    private async Task SeedTagsAsync(string channelId, int count)
    {
        await using var db = fixture.CreateDbContext();
        db.EmoteTags.AddRange(Enumerable.Range(0, count).Select(i => new EmoteTag
        {
            ChannelId = channelId,
            Name = $"Seeded {i}",
            NormalizedName = $"seeded {i}",
            CreatedAtUtc = DateTime.UtcNow
        }));
        await db.SaveChangesAsync();
    }

    // Entries whose emotes have no row in the channel — "not in the set", which is all the limit and
    // count cases need.
    private async Task SeedEntriesAsync(long tagId, int count)
    {
        await using var db = fixture.CreateDbContext();
        db.EmoteTagEntries.AddRange(Enumerable.Range(0, count).Select(_ => new EmoteTagEntry
        {
            TagId = tagId,
            SevenTvEmoteId = NewSevenTvId(),
            Alias = "Seeded",
            ImageUrl = "https://cdn.7tv.app/emote/seeded/2x.webp",
            AddedAtUtc = DateTime.UtcNow
        }));
        await db.SaveChangesAsync();
    }

    private async Task<EmoteTagEntry> SeedEntryAsync(long tagId, string sevenTvEmoteId, string alias, DateTime addedAtUtc)
    {
        await using var db = fixture.CreateDbContext();
        var entry = new EmoteTagEntry
        {
            TagId = tagId,
            SevenTvEmoteId = sevenTvEmoteId,
            Alias = alias,
            ImageUrl = $"https://cdn.7tv.app/emote/{sevenTvEmoteId}/2x.webp",
            AddedAtUtc = addedAtUtc
        };
        db.EmoteTagEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    private async Task<Guid> SeedOperationAsync(long tagId, string emoteSetId, DateTime registeredAtUtc)
    {
        await using var db = fixture.CreateDbContext();
        var operationId = Guid.NewGuid();
        db.EmoteTagOperations.Add(new EmoteTagOperation
        {
            OperationId = operationId,
            TagId = tagId,
            Kind = EmoteTagOperationKind.PlayIn,
            SevenTvEmoteSetId = emoteSetId,
            RegisteredAtUtc = registeredAtUtc
        });
        await db.SaveChangesAsync();
        return operationId;
    }

    private async Task SeedPlacementAsync(
        long tagId, string sevenTvEmoteId, string emoteSetId, Guid operationId, DateTime placedAtUtc, DateTime registeredAtUtc)
    {
        await using var db = fixture.CreateDbContext();
        db.EmoteTagPlacements.Add(new EmoteTagPlacement
        {
            TagId = tagId,
            SevenTvEmoteId = sevenTvEmoteId,
            SevenTvEmoteSetId = emoteSetId,
            PlacedAtUtc = placedAtUtc,
            OperationId = operationId,
            RegisteredAtUtc = registeredAtUtc
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedActivationAsync(long tagId, string emoteSetId, Guid operationId, DateTime activatedAtUtc)
    {
        await using var db = fixture.CreateDbContext();
        db.EmoteTagActivations.Add(new EmoteTagActivation
        {
            TagId = tagId,
            SevenTvEmoteSetId = emoteSetId,
            ActivatedAtUtc = activatedAtUtc,
            OperationId = operationId
        });
        await db.SaveChangesAsync();
    }

    // What an applied play-in leaves behind: its operation, a placement per emote (the entries must
    // exist — FK) placed at the registration instant, and the activation.
    private async Task<Guid> SeedPlayInAsync(long tagId, string emoteSetId, DateTime registeredAtUtc, params string[] sevenTvEmoteIds)
    {
        var operationId = await SeedOperationAsync(tagId, emoteSetId, registeredAtUtc);
        foreach (var sevenTvEmoteId in sevenTvEmoteIds)
        {
            await SeedPlacementAsync(tagId, sevenTvEmoteId, emoteSetId, operationId, registeredAtUtc, registeredAtUtc);
        }

        await SeedActivationAsync(tagId, emoteSetId, operationId, registeredAtUtc);
        return operationId;
    }

    private async Task SeedObservationAsync(string channelId, string sevenTvEmoteId, string emoteSetId, DateTime observedAtUtc)
    {
        await using var db = fixture.CreateDbContext();
        await EmoteSetLeaveObservations.RecordAsync(db, channelId, emoteSetId, [sevenTvEmoteId], observedAtUtc, CancellationToken.None);
    }

    private async Task<List<AuditLogEntry>> LoadAuditAsync(string channelName)
    {
        await using var db = fixture.CreateDbContext();
        return await db.AuditLogEntries.AsNoTracking()
            .Where(e => e.ChannelName == channelName)
            .OrderBy(e => e.Id)
            .ToListAsync();
    }

    // The jsonb column reformats what was written, so details are compared parsed — which also pins
    // the exact key set: nothing besides the ids and counts may ride along.
    private static Dictionary<string, long> ParseDetails(string? json) =>
        JsonSerializer.Deserialize<Dictionary<string, long>>(json ?? "null") ?? [];

    private static Dictionary<string, long> Details(params (string Key, long Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    // 26 characters from [0-9A-Z], the shape of a real 7TV ULID.
    private static string NewSevenTvId() => Guid.NewGuid().ToString("N")[..26].ToUpperInvariant();

    // Holds its context's first commit until released — the report has taken the channel lock and
    // sent its writes, and nothing is committed yet.
    private sealed class PauseBeforeCommit : DbTransactionInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Reached.TrySetResult())
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class TableHold(AppDbContext db) : IAsyncDisposable
    {
        public async Task ReleaseAsync() => await db.Database.RollbackTransactionAsync();

        public async ValueTask DisposeAsync()
        {
            // Idempotent: a test that failed before ReleaseAsync must not leave the table locked for
            // every test after it.
            if (db.Database.CurrentTransaction is not null)
            {
                await db.Database.RollbackTransactionAsync();
            }

            await db.DisposeAsync();
        }
    }
}
