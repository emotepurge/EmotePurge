using System.Globalization;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
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
    public async Task Delete_RemovesTheTagAndItsEntries_LeavesASiblingTag_AndAuditsIdAndEntryCount()
    {
        var channel = await SeedChannelAsync("tagdelete");
        var tag = await SeedTagAsync(channel.Id, "Funny");
        var sibling = await SeedTagAsync(channel.Id, "Sad");
        await SeedEntriesAsync(tag.Id, 3);
        await SeedEntriesAsync(sibling.Id, 2);

        var status = await DeleteAsync("tagdelete", tag.Id);

        Assert.Equal(EmoteTagMutationStatus.Ok, status);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTags.AnyAsync(t => t.Id == tag.Id));
        Assert.False(await verify.EmoteTagEntries.AnyAsync(e => e.TagId == tag.Id));
        Assert.True(await verify.EmoteTags.AnyAsync(t => t.Id == sibling.Id));
        Assert.Equal(2, await verify.EmoteTagEntries.CountAsync(e => e.TagId == sibling.Id));

        var audit = Assert.Single(await LoadAuditAsync("tagdelete"));
        Assert.Equal(AuditActions.TagDelete, audit.Action);
        Assert.Equal("emoteTag", audit.TargetType);
        Assert.Equal(tag.Id.ToString(CultureInfo.InvariantCulture), audit.TargetId);
        Assert.Equal(Details(("tagId", tag.Id), ("entryCount", 3)), ParseDetails(audit.DetailsJson));
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
