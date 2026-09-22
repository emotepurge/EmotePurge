using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class VoteSessionServiceTests(PostgresFixture fixture)
{
    private static readonly AuditActor Actor = new("4711", "sensitron");

    [Fact]
    public async Task DeleteAsync_RemovesSession_AndCascadesVotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "deletetest1");
        var emote = await SeedEmoteAsync(db, channel.Id, "Emote");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var voter = await SeedUserAsync(db, "deletetest1-voter");
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emote.Id, UserId = voter.Id, Type = VoteType.Keep });
        await db.SaveChangesAsync();

        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());
        var deleted = await service.DeleteAsync(channel.ChannelName, session.Id, Actor);

        Assert.True(deleted);
        Assert.Null(await db.VoteSessions.SingleOrDefaultAsync(s => s.Id == session.Id));
        Assert.Empty(await db.Votes.Where(v => v.VoteSessionId == session.Id).ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_ForUnknownSession_ReturnsFalse()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "deletetest2");

        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());
        var deleted = await service.DeleteAsync(channel.ChannelName, sessionId: 999_999, Actor);

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteAsync_ForUnknownChannel_ReturnsFalse()
    {
        await using var db = fixture.CreateDbContext();

        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());
        var deleted = await service.DeleteAsync("does-not-exist", sessionId: 1, Actor);

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotAffectOtherSessions_InSameChannel()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "deletetest3");
        var toDelete = await SeedActiveSessionAsync(db, channel.Id);
        var toKeep = await SeedActiveSessionAsync(db, channel.Id);

        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());
        var deleted = await service.DeleteAsync(channel.ChannelName, toDelete.Id, Actor);

        Assert.True(deleted);
        Assert.NotNull(await db.VoteSessions.SingleOrDefaultAsync(s => s.Id == toKeep.Id));
    }

    [Fact]
    public async Task CreateAsync_WritesAuditEntry_PointingAtTheNewSession()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesessionaudit1");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        var (result, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Sommer-Purge", AllowedRoles.Everyone), Actor);

        Assert.Equal(CreateVoteSessionResult.Success, result);
        var entry = Assert.Single(await LoadAuditEntriesAsync(db, "votesessionaudit1"));
        Assert.Equal(AuditActions.VoteSessionCreate, entry.Action);
        Assert.Equal(Actor.Login, entry.ActorLogin);
        // TargetId is only knowable after the insert — the service takes an explicit transaction for
        // exactly this, so the entry can name the session it created and still commit atomically.
        Assert.Equal("voteSession", entry.TargetType);
        Assert.Equal(session!.Id.ToString(), entry.TargetId);
        Assert.Equal("Sommer-Purge", ReadDetail(entry.DetailsJson, "title"));
    }

    [Fact]
    public async Task CreateAsync_RejectedByValidation_WritesNoAuditEntry()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesessionaudit2");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        var (result, _) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "   ", AllowedRoles.Everyone), Actor);

        Assert.Equal(CreateVoteSessionResult.TitleEmpty, result);
        Assert.Empty(await LoadAuditEntriesAsync(db, "votesessionaudit2"));
    }

    [Fact]
    public async Task CreateAsync_WithNoAllowedRoles_ReturnsRolesEmpty_AndWritesNothing()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "rolesempty1");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        var (result, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Ohne Rollen", (AllowedRoles)0), Actor);

        Assert.Equal(CreateVoteSessionResult.RolesEmpty, result);
        Assert.Null(session);
        Assert.Empty(await LoadAuditEntriesAsync(db, "rolesempty1"));
    }

    [Fact]
    public async Task CreateAsync_WithVipsAmongAllowedRoles_ReturnsVipsNotSupported_AndWritesNothing()
    {
        // Twitch has no self-report endpoint for VIP status, so a voter cannot prove their own — see
        // the decision log. VIPs combined with another role must still be rejected.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "vipsnotsupported1");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        var (result, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Mit VIPs", AllowedRoles.Everyone | AllowedRoles.VIPs), Actor);

        Assert.Equal(CreateVoteSessionResult.VipsNotSupported, result);
        Assert.Null(session);
        Assert.Empty(await LoadAuditEntriesAsync(db, "vipsnotsupported1"));
    }

    [Fact]
    public async Task CreateAsync_WithStartedAtInTheFuture_ReturnsStartedAtInFuture_AndWritesNothing()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "startedatfuture1");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        var (result, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Zukunft", AllowedRoles.Everyone, StartedAt: DateTime.UtcNow.AddDays(1)),
            Actor);

        Assert.Equal(CreateVoteSessionResult.StartedAtInFuture, result);
        Assert.Null(session);
        Assert.Empty(await LoadAuditEntriesAsync(db, "startedatfuture1"));
    }

    [Fact]
    public async Task CreateAsync_WithStartedAtBeyondMaxBackdateDays_ReturnsStartedAtTooFarBack_AndWritesNothing()
    {
        // Comfortably past the MaxBackdateDays boundary (not just over it) so the assertion doesn't
        // depend on the exact millisecond the test happens to run at.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "startedattoofarback1");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());
        var wayTooFarBack = DateTime.UtcNow.AddDays(-(VoteSessionLimits.MaxBackdateDays + 30));

        var (result, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Uralt", AllowedRoles.Everyone, StartedAt: wayTooFarBack), Actor);

        Assert.Equal(CreateVoteSessionResult.StartedAtTooFarBack, result);
        Assert.Null(session);
        Assert.Empty(await LoadAuditEntriesAsync(db, "startedattoofarback1"));
    }

    [Fact]
    public async Task CreateAsync_WithEmoteIds_PersistsDedupedBallotRows()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballotcreate1");
        var emoteA = await SeedEmoteAsync(db, channel.Id, "EmoteA");
        var emoteB = await SeedEmoteAsync(db, channel.Id, "EmoteB");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        // Duplicate and whitespace-padded ids collapse to one clean row each.
        var (result, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(
                channel.ChannelName, "Kuratiert", AllowedRoles.Everyone,
                EmoteIds: [emoteA.Id, $" {emoteA.Id} ", emoteB.Id]),
            Actor);

        Assert.Equal(CreateVoteSessionResult.Success, result);
        var ballot = await db.VoteSessionEmotes.Where(se => se.VoteSessionId == session!.Id).ToListAsync();
        Assert.Equal(2, ballot.Count);
        Assert.Contains(ballot, se => se.EmoteId == emoteA.Id);
        Assert.Contains(ballot, se => se.EmoteId == emoteB.Id);
    }

    [Fact]
    public async Task CreateAsync_PersistsHideResultsUntilEnd_DefaultingToVisible()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "hidecreate1");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        var (_, hidden) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Geheim", AllowedRoles.Everyone, HideResultsUntilEnd: true), Actor);
        var (_, open) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Offen", AllowedRoles.Everyone), Actor);

        Assert.True(hidden!.HideResultsUntilEnd);
        // Not passing the flag must keep producing the sessions every existing caller creates today.
        Assert.False(open!.HideResultsUntilEnd);

        // The secret ballot is a governance decision, so it has to be readable from the audit row.
        var audit = await LoadAuditEntriesAsync(db, "hidecreate1");
        Assert.True(ReadDetailBool(audit.Single(e => e.TargetId == hidden.Id.ToString()).DetailsJson, "hideResults"));
        Assert.False(ReadDetailBool(audit.Single(e => e.TargetId == open.Id.ToString()).DetailsJson, "hideResults"));
    }

    [Fact]
    public async Task CreateAsync_WithEmptyEmoteIds_ReturnsEmoteIdsEmpty_AndWritesNothing()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballotcreate2");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        // An explicit empty ballot (here: whitespace-only ids) is an error, not "all emotes".
        var (result, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Leer", AllowedRoles.Everyone, EmoteIds: ["   ", ""]), Actor);

        Assert.Equal(CreateVoteSessionResult.EmoteIdsEmpty, result);
        Assert.Null(session);
        Assert.Empty(await LoadAuditEntriesAsync(db, "ballotcreate2"));
    }

    [Fact]
    public async Task CreateAsync_WithForeignUnknownOrArchivedEmoteId_ReturnsEmoteIdsInvalid()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballotcreate3");
        var otherChannel = await SeedChannelAsync(db, "ballotcreate3-other");
        var own = await SeedEmoteAsync(db, channel.Id, "Own");
        var foreign = await SeedEmoteAsync(db, otherChannel.Id, "Foreign");
        var archived = await SeedEmoteAsync(db, channel.Id, "Archived");
        archived.IsArchived = true;
        await db.SaveChangesAsync();
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        var (foreignResult, _) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Fremd", AllowedRoles.Everyone, EmoteIds: [own.Id, foreign.Id]), Actor);
        Assert.Equal(CreateVoteSessionResult.EmoteIdsInvalid, foreignResult);

        var (unknownResult, _) = await service.CreateAsync(
            new VoteSessionCreateRequest(
                channel.ChannelName, "Unbekannt", AllowedRoles.Everyone, EmoteIds: [own.Id, Guid.NewGuid().ToString()]),
            Actor);
        Assert.Equal(CreateVoteSessionResult.EmoteIdsInvalid, unknownResult);

        var (archivedResult, _) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Archiviert", AllowedRoles.Everyone, EmoteIds: [own.Id, archived.Id]), Actor);
        Assert.Equal(CreateVoteSessionResult.EmoteIdsInvalid, archivedResult);

        Assert.Empty(await LoadAuditEntriesAsync(db, "ballotcreate3"));
    }

    [Fact]
    public async Task CreateAsync_SetSession_WithMemberNotOnLiveSet_ReturnsEmoteIdsInvalid_AndWritesNothing()
    {
        // AK 76: all-or-nothing on the 7TV identity — one id outside the live set rejects the whole
        // create, same "all or nothing" shape as the local-guid ballot's own EmoteIdsInvalid above.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "setballot1");
        var foreignEmoteSetService = SubstituteForeignEmoteSetService(
            channel.ChannelName, "set-1", ("7tv-live", "LiveOnly", "https://cdn.7tv.app/emote/live/2x.webp"));
        var service = new VoteSessionService(db, foreignEmoteSetService);

        var (result, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(
                channel.ChannelName, "Halloween-Set", AllowedRoles.Everyone,
                EmoteSetId: "set-1", SevenTvEmoteIds: ["7tv-live", "7tv-not-a-member"]),
            Actor);

        Assert.Equal(CreateVoteSessionResult.EmoteIdsInvalid, result);
        Assert.Null(session);
        Assert.Empty(await db.VoteSessions.Where(s => s.ChannelId == channel.Id).ToListAsync());
        Assert.Empty(await db.Emotes.Where(e => e.ChannelId == channel.Id).ToListAsync());
        Assert.Empty(await LoadAuditEntriesAsync(db, "setballot1"));
    }

    [Fact]
    public async Task CreateAsync_SetSession_ForNewMember_CreatesArchivedRow_AndFreezesNameAndImage()
    {
        // AK 77 (new-row half): a live member with no local row yet gets one, created archived
        // ("never active") — never granted the appearance of being live in our own database.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "setballot2");
        var foreignEmoteSetService = SubstituteForeignEmoteSetService(
            channel.ChannelName, "set-2", ("7tv-new", "NewMember", "https://cdn.7tv.app/emote/new/2x.webp"));
        var service = new VoteSessionService(db, foreignEmoteSetService);

        var (result, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(
                channel.ChannelName, "Halloween-Set", AllowedRoles.Everyone,
                EmoteSetId: "set-2", SevenTvEmoteIds: ["7tv-new"]),
            Actor);

        Assert.Equal(CreateVoteSessionResult.Success, result);
        var emote = Assert.Single(await db.Emotes.AsNoTracking().Where(e => e.ChannelId == channel.Id).ToListAsync());
        Assert.Equal("7tv-new", emote.SevenTvEmoteId);
        Assert.True(emote.IsArchived);
        Assert.Null(emote.ArchivedAt);

        var ballot = Assert.Single(
            await db.VoteSessionEmotes.AsNoTracking().Where(se => se.VoteSessionId == session!.Id).ToListAsync());
        Assert.Equal(emote.Id, ballot.EmoteId);
        Assert.Equal("NewMember", ballot.NameAtCreation);
        Assert.Equal("https://cdn.7tv.app/emote/new/2x.webp", ballot.ImageUrlAtCreation);
    }

    [Fact]
    public async Task CreateAsync_SetSession_TakesOverAnExistingActiveRow_WithoutModifyingIt()
    {
        // AK 77 (existing-row half): a live member that already has a local row — active, from an
        // ordinary sync — is taken over unmodified (ON CONFLICT DO NOTHING). NameAtCreation still
        // freezes the *live* 7TV value even though the stored row itself was left untouched.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "setballot3");
        var existing = await SeedEmoteAsync(db, channel.Id, "StaleName");
        existing.SevenTvEmoteId = "7tv-existing";
        existing.ImageUrl = "https://cdn.7tv.app/emote/stale/2x.webp";
        await db.SaveChangesAsync();
        var foreignEmoteSetService = SubstituteForeignEmoteSetService(
            channel.ChannelName, "set-3", ("7tv-existing", "LiveName", "https://cdn.7tv.app/emote/live/2x.webp"));
        var service = new VoteSessionService(db, foreignEmoteSetService);

        var (result, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(
                channel.ChannelName, "Halloween-Set", AllowedRoles.Everyone,
                EmoteSetId: "set-3", SevenTvEmoteIds: ["7tv-existing"]),
            Actor);

        Assert.Equal(CreateVoteSessionResult.Success, result);
        var stored = await db.Emotes.AsNoTracking().SingleAsync(e => e.Id == existing.Id);
        Assert.Equal("StaleName", stored.Name);
        Assert.False(stored.IsArchived);
        Assert.Equal("https://cdn.7tv.app/emote/stale/2x.webp", stored.ImageUrl);

        var ballot = Assert.Single(
            await db.VoteSessionEmotes.AsNoTracking().Where(se => se.VoteSessionId == session!.Id).ToListAsync());
        Assert.Equal(existing.Id, ballot.EmoteId);
        Assert.Equal("LiveName", ballot.NameAtCreation);
    }

    [Fact]
    public async Task CreateAsync_SetSession_WhenSevenTvIsUnreadable_ReturnsSevenTvUnavailable_AndWritesNothing()
    {
        // Spec section 9, step 1: an unreadable live membership list creates no session at all.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "setballot4");
        var foreignEmoteSetService = Substitute.For<IForeignEmoteSetService>();
        foreignEmoteSetService
            .GetForeignEmoteSetBySetIdAsync(channel.ChannelName, "set-4", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.SevenTvUnavailable));
        var service = new VoteSessionService(db, foreignEmoteSetService);

        var (result, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(
                channel.ChannelName, "Halloween-Set", AllowedRoles.Everyone,
                EmoteSetId: "set-4", SevenTvEmoteIds: ["7tv-x"]),
            Actor);

        Assert.Equal(CreateVoteSessionResult.SevenTvUnavailable, result);
        Assert.Null(session);
        Assert.Empty(await db.VoteSessions.Where(s => s.ChannelId == channel.Id).ToListAsync());
        Assert.Empty(await LoadAuditEntriesAsync(db, "setballot4"));
    }

    [Fact]
    public async Task CastVoteAsync_SubsetSession_AllowsBallotMembers_RejectsOutsiders()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballotcast1");
        var onBallot = await SeedEmoteAsync(db, channel.Id, "OnBallot");
        var offBallot = await SeedEmoteAsync(db, channel.Id, "OffBallot");
        var voter = await SeedUserAsync(db, "ballotcast1-voter");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());
        var (_, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Kuratiert", AllowedRoles.Everyone, EmoteIds: [onBallot.Id]), Actor);

        var (onResult, vote) = await service.CastVoteAsync(channel.ChannelName, session!.Id, onBallot.Id, voter.Id, VoteType.Keep);
        Assert.Equal(VoteCastResult.Success, onResult);
        Assert.NotNull(vote);

        // Off-ballot emote is a perfectly valid channel emote — but this session doesn't cover it.
        var (offResult, _) = await service.CastVoteAsync(channel.ChannelName, session.Id, offBallot.Id, voter.Id, VoteType.Delete);
        Assert.Equal(VoteCastResult.EmoteNotEligible, offResult);
    }

    [Fact]
    public async Task CastVoteAsync_OnArchivedEmote_ReturnsEmoteNotEligible_EvenOnItsOwnBallot()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballotcast2");
        var emote = await SeedEmoteAsync(db, channel.Id, "SoonGone");
        var voter = await SeedUserAsync(db, "ballotcast2-voter");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());
        var (_, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Kuratiert", AllowedRoles.Everyone, EmoteIds: [emote.Id]), Actor);

        // Archived mid-session: stays visible in the results (badged), but voting on it is closed.
        emote.IsArchived = true;
        await db.SaveChangesAsync();

        var (result, _) = await service.CastVoteAsync(channel.ChannelName, session!.Id, emote.Id, voter.Id, VoteType.Delete);
        Assert.Equal(VoteCastResult.EmoteNotEligible, result);
    }

    [Fact]
    public async Task CastVoteAsync_SetSession_AllowsVotingOnAnArchivedBallotMember()
    {
        // AK 79 (set-session half): a set-session's ballot rows are created archived on purpose (spec
        // section 9) — "steht auf dem Wahlzettel" is the only criterion here, unlike the null-session
        // case above where IsArchived still gates the vote.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "setballotcast1");
        var voter = await SeedUserAsync(db, "setballotcast1-voter");
        var foreignEmoteSetService = SubstituteForeignEmoteSetService(
            channel.ChannelName, "set-5", ("7tv-halloween", "PumpkinFace", "https://cdn.7tv.app/emote/pumpkin/2x.webp"));
        var service = new VoteSessionService(db, foreignEmoteSetService);
        var (_, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(
                channel.ChannelName, "Halloween-Set", AllowedRoles.Everyone,
                EmoteSetId: "set-5", SevenTvEmoteIds: ["7tv-halloween"]),
            Actor);
        var ballotEmoteId = (await db.VoteSessionEmotes.AsNoTracking()
            .SingleAsync(se => se.VoteSessionId == session!.Id)).EmoteId;

        var (result, vote) = await service.CastVoteAsync(channel.ChannelName, session!.Id, ballotEmoteId, voter.Id, VoteType.Keep);

        Assert.Equal(VoteCastResult.Success, result);
        Assert.NotNull(vote);
    }

    [Fact]
    public async Task CastVoteAsync_DynamicSession_StillAllowsAnyActiveChannelEmote()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballotcast3");
        var emote = await SeedEmoteAsync(db, channel.Id, "AnyEmote");
        var voter = await SeedUserAsync(db, "ballotcast3-voter");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());
        var (_, session) = await service.CreateAsync(
            new VoteSessionCreateRequest(channel.ChannelName, "Alle", AllowedRoles.Everyone), Actor);

        var (result, vote) = await service.CastVoteAsync(channel.ChannelName, session!.Id, emote.Id, voter.Id, VoteType.Keep);

        Assert.Equal(VoteCastResult.Success, result);
        Assert.NotNull(vote);
    }

    [Fact]
    public async Task CastVoteAsync_InsertsOnce_ThenUpdatesTheSameRow_WhenTheVoterChangesTheirMind()
    {
        // The (VoteSessionId, EmoteId, UserId) unique index is what makes this an update rather than
        // a second row, and the read-then-insert in front of it is the reason the method carries a
        // DbUpdateException fallback at all. One vote per user per emote is the invariant the whole
        // popularity score rests on.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "castupsert1");
        var emote = await SeedEmoteAsync(db, channel.Id, "Emote");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var voter = await SeedUserAsync(db, "castupsert1-voter");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        var (firstResult, firstVote) = await service.CastVoteAsync(channel.ChannelName, session.Id, emote.Id, voter.Id, VoteType.Keep);
        var (secondResult, secondVote) = await service.CastVoteAsync(channel.ChannelName, session.Id, emote.Id, voter.Id, VoteType.Delete);

        Assert.Equal(VoteCastResult.Success, firstResult);
        Assert.Equal(VoteCastResult.Success, secondResult);
        Assert.NotNull(firstVote);
        Assert.NotNull(secondVote);
        Assert.Equal(firstVote!.Id, secondVote!.Id);

        var stored = Assert.Single(await db.Votes.AsNoTracking().Where(v => v.VoteSessionId == session.Id).ToListAsync());
        Assert.Equal(VoteType.Delete, stored.Type);
    }

    [Fact]
    public async Task CastVoteAsync_ForAnEndedSession_ReturnsSessionEnded_AndStoresNothing()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "castended1");
        var emote = await SeedEmoteAsync(db, channel.Id, "Emote");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var voter = await SeedUserAsync(db, "castended1-voter");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());
        await service.EndAsync(channel.ChannelName, session.Id, Actor);

        var (result, vote) = await service.CastVoteAsync(channel.ChannelName, session.Id, emote.Id, voter.Id, VoteType.Keep);

        Assert.Equal(VoteCastResult.SessionEnded, result);
        Assert.Null(vote);
        Assert.Empty(await db.Votes.AsNoTracking().Where(v => v.VoteSessionId == session.Id).ToListAsync());
    }

    [Fact]
    public async Task CastVoteAsync_ForAnUnknownChannel_ReturnsChannelNotFound()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "castunknown1");
        var emote = await SeedEmoteAsync(db, channel.Id, "Emote");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var voter = await SeedUserAsync(db, "castunknown1-voter");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        var (result, vote) = await service.CastVoteAsync("doesnotexist", session.Id, emote.Id, voter.Id, VoteType.Keep);

        Assert.Equal(VoteCastResult.ChannelNotFound, result);
        Assert.Null(vote);
    }

    [Fact]
    public async Task CastVoteAsync_ForASessionOfAnotherChannel_ReturnsSessionNotFound()
    {
        // Multi-tenant isolation: the session id is globally unique, so without the ChannelId
        // predicate in LoadChannelSessionAsync a caller authorized for channel A could vote in
        // channel B's session simply by naming its id.
        await using var db = fixture.CreateDbContext();
        var channelA = await SeedChannelAsync(db, "castforeign1a");
        var channelB = await SeedChannelAsync(db, "castforeign1b");
        var emoteA = await SeedEmoteAsync(db, channelA.Id, "Emote");
        var sessionB = await SeedActiveSessionAsync(db, channelB.Id);
        var voter = await SeedUserAsync(db, "castforeign1-voter");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        var (result, vote) = await service.CastVoteAsync(channelA.ChannelName, sessionB.Id, emoteA.Id, voter.Id, VoteType.Keep);

        Assert.Equal(VoteCastResult.SessionNotFound, result);
        Assert.Null(vote);
        Assert.Empty(await db.Votes.AsNoTracking().Where(v => v.VoteSessionId == sessionB.Id).ToListAsync());
    }

    [Fact]
    public async Task CastVoteAsync_ForAnEmoteOfAnotherChannel_ReturnsEmoteNotEligible()
    {
        // The other half of the isolation: same session, but an emote id belonging to a foreign
        // channel. Emote.Id is a global Guid, so only the ChannelId predicate stops this.
        await using var db = fixture.CreateDbContext();
        var channelA = await SeedChannelAsync(db, "castforeign2a");
        var channelB = await SeedChannelAsync(db, "castforeign2b");
        var sessionA = await SeedActiveSessionAsync(db, channelA.Id);
        var emoteB = await SeedEmoteAsync(db, channelB.Id, "ForeignEmote");
        var voter = await SeedUserAsync(db, "castforeign2-voter");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        var (result, vote) = await service.CastVoteAsync(channelA.ChannelName, sessionA.Id, emoteB.Id, voter.Id, VoteType.Keep);

        Assert.Equal(VoteCastResult.EmoteNotEligible, result);
        Assert.Null(vote);
        Assert.Empty(await db.Votes.AsNoTracking().Where(v => v.EmoteId == emoteB.Id).ToListAsync());
    }

    [Fact]
    public async Task EndAsync_WritesAuditEntry_Once_EvenWhenCalledTwice()
    {
        // Ending an already-ended session is an idempotent no-op by contract, and a no-op is not an
        // event — the second call must not add a second entry.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesessionaudit3");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        await service.EndAsync(channel.ChannelName, session.Id, Actor);
        await service.EndAsync(channel.ChannelName, session.Id, Actor);

        var entry = Assert.Single(await LoadAuditEntriesAsync(db, "votesessionaudit3"));
        Assert.Equal(AuditActions.VoteSessionEnd, entry.Action);
        Assert.Equal(session.Id.ToString(), entry.TargetId);
    }

    [Fact]
    public async Task DeleteAsync_WritesAuditEntry_ThatOutlivesTheSession()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesessionaudit4");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        await service.DeleteAsync(channel.ChannelName, session.Id, Actor);

        Assert.Null(await db.VoteSessions.SingleOrDefaultAsync(s => s.Id == session.Id));
        var entry = Assert.Single(await LoadAuditEntriesAsync(db, "votesessionaudit4"));
        Assert.Equal(AuditActions.VoteSessionDelete, entry.Action);
        // The title is captured into the entry because after the delete there is nothing left to
        // look it up in.
        Assert.Equal("Test Session", ReadDetail(entry.DetailsJson, "title"));
    }

    [Fact]
    public async Task DeleteAsync_ForUnknownSession_WritesNoAuditEntry()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesessionaudit5");
        var service = new VoteSessionService(db, Substitute.For<IForeignEmoteSetService>());

        await service.DeleteAsync(channel.ChannelName, sessionId: 999_999, Actor);

        Assert.Empty(await LoadAuditEntriesAsync(db, "votesessionaudit5"));
    }

    /// <summary>
    /// Reads one string member out of an entry's <c>DetailsJson</c>. Parsed rather than string-
    /// compared, because the column is <c>jsonb</c>: Postgres stores a normalized form and hands back
    /// <c>{"title": "x"}</c> for the <c>{"title":"x"}</c> that was written. The value is the contract,
    /// the byte-for-byte formatting is not.
    /// </summary>
    private static string? ReadDetail(string? detailsJson, string property)
    {
        return detailsJson is null ? null : JsonDocument.Parse(detailsJson).RootElement.GetProperty(property).GetString();
    }

    private static bool ReadDetailBool(string? detailsJson, string property)
    {
        return detailsJson is not null && JsonDocument.Parse(detailsJson).RootElement.GetProperty(property).GetBoolean();
    }

    private static async Task<IReadOnlyList<AuditLogEntry>> LoadAuditEntriesAsync(AppDbContext db, string channelName)
    {
        return await db.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.ChannelName == channelName)
            .OrderBy(e => e.Id)
            .ToListAsync();
    }

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string channelName)
    {
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private static async Task<Emote> SeedEmoteAsync(AppDbContext db, string channelId, string name)
    {
        var emote = new Emote
        {
            ChannelId = channelId,
            Name = name,
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private static async Task<VoteSession> SeedActiveSessionAsync(AppDbContext db, string channelId)
    {
        var session = new VoteSession
        {
            ChannelId = channelId,
            Title = "Test Session",
            AllowedVoterRoles = AllowedRoles.Everyone,
            IsActive = true
        };
        db.VoteSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private static async Task<User> SeedUserAsync(AppDbContext db, string twitchUserId)
    {
        var user = new User { Id = twitchUserId, TwitchUsername = twitchUserId, DisplayName = twitchUserId };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// A substituted <see cref="IForeignEmoteSetService"/> whose set-ID lookup answers with exactly
    /// the given live members — the shape every set-session test needs, without a database or a real
    /// 7TV call. <c>DefaultName</c> mirrors <c>Name</c>; no test here depends on the two differing.
    /// </summary>
    private static IForeignEmoteSetService SubstituteForeignEmoteSetService(
        string channelName, string emoteSetId, params (string SevenTvEmoteId, string Name, string ImageUrl)[] members)
    {
        var foreignEmoteSetService = Substitute.For<IForeignEmoteSetService>();
        var emotes = members
            .Select(m => new ForeignEmoteRow(m.SevenTvEmoteId, m.Name, m.Name, m.ImageUrl, TopAllTime: null, Trending: null))
            .ToList();
        foreignEmoteSetService
            .GetForeignEmoteSetBySetIdAsync(channelName, emoteSetId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Ok(
                new ForeignEmoteSet(channelName, SevenTvUserId: null, emoteSetId, emotes.Count, Truncated: false, emotes)));
        return foreignEmoteSetService;
    }
}
