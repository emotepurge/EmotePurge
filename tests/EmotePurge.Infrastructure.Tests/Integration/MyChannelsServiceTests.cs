using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// In Integration/ rather than Unit/: MyChannelsService takes AppDbContext to resolve the tracked
// state of the channels it assembles. The moderated-channel provider, Helix and 7TV are substituted.
//
// The two properties worth pinning are the ones a user notices immediately when they break: the
// caller's own channel is always in the list (Helix's moderated-channels endpoint never returns it),
// and a degraded third party is reported as a degradation rather than as "you have no channels".
[Collection("Postgres")]
public class MyChannelsServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetMyChannelsAsync_AlwaysContainsTheCallersOwnChannel_AsBroadcaster()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);

        var result = await service.GetMyChannelsAsync(Principal("mychannels1_self"));

        var own = Assert.Single(result.Channels);
        Assert.Equal("mychannels1_self", own.ChannelName);
        Assert.True(own.IsBroadcaster);
        Assert.False(own.IsModerator);
        Assert.False(own.IsSevenTvEditor);
        // Never joined, so neither tracked nor active — but still listed, because this is the one
        // channel the user can always decide to add.
        Assert.False(own.IsTracked);
        Assert.False(own.IsBotActive);
    }

    [Fact]
    public async Task GetMyChannelsAsync_KeepsTheOwnChannel_EvenWhenHelixFailsAndSevenTvIsUnavailable()
    {
        await using var db = fixture.CreateDbContext();
        var editors = Substitute.For<ISevenTvEditorService>();
        editors.GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Failed(SevenTvLookupStatus.Unavailable));
        var service = CreateService(db, moderated: Undetermined(), editors: editors);

        var result = await service.GetMyChannelsAsync(Principal("mychannels2_self"));

        Assert.True(result.HelixUnavailable);
        Assert.True(result.SevenTvUnavailable);
        Assert.Equal("mychannels2_self", Assert.Single(result.Channels).ChannelName);
    }

    // Issue #37: NoSevenTvAccount used to collapse onto the very same null as Unavailable, so a user
    // without a 7TV account at all — the common case, not a degradation — saw the "7TV editor status
    // could not be fully checked" warning banner. It must not raise SevenTvUnavailable.
    [Fact]
    public async Task GetMyChannelsAsync_DoesNotReportSevenTvUnavailable_WhenTheUserSimplyHasNoSevenTvAccount()
    {
        await using var db = fixture.CreateDbContext();
        var editors = Substitute.For<ISevenTvEditorService>();
        editors.GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Failed(SevenTvLookupStatus.NoSevenTvAccount));
        var service = CreateService(db, moderated: Moderated(), editors: editors);

        var result = await service.GetMyChannelsAsync(Principal("mychannels2b_self"));

        Assert.False(result.SevenTvUnavailable);
        Assert.Equal("mychannels2b_self", Assert.Single(result.Channels).ChannelName);
    }

    [Fact]
    public async Task GetMyChannelsAsync_ReportsHelixUnavailableAndReauthRequired_WhenNoUsableTokenExists()
    {
        // The sharper of the two degradations: only a fresh Twitch login can fix this, so the
        // frontend must offer a re-login instead of a generic "Helix is down" note.
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, moderated: Undetermined(reauthRequired: true));

        var result = await service.GetMyChannelsAsync(Principal("mychannels3_self"));

        Assert.True(result.HelixUnavailable);
        Assert.True(result.ReauthRequired);
    }

    [Fact]
    public async Task GetMyChannelsAsync_ReportsHelixAvailable_WhenTheUserModeratesNothing()
    {
        // An empty list is a real answer, not a degradation. Reporting HelixUnavailable here would
        // put a permanent "Twitch is unreachable" warning in front of every user who happens to
        // moderate no channel at all.
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, moderated: Moderated());

        var result = await service.GetMyChannelsAsync(Principal("mychannels21_self"));

        Assert.False(result.HelixUnavailable);
        Assert.False(result.ReauthRequired);
        Assert.Equal("mychannels21_self", Assert.Single(result.Channels).ChannelName);
    }

    [Fact]
    public async Task GetMyChannelsAsync_ReportsHelixUnavailable_WhenTheModeratedListCouldNotBeDetermined()
    {
        // Same visible channel list as the empty case above — one own channel — but the flag has to
        // differ, because here the moderated channels are missing rather than absent.
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, moderated: Undetermined());

        var result = await service.GetMyChannelsAsync(Principal("mychannels22_self"));

        Assert.True(result.HelixUnavailable);
        Assert.False(result.ReauthRequired);
        Assert.Equal("mychannels22_self", Assert.Single(result.Channels).ChannelName);
    }

    [Fact]
    public async Task GetMyChannelsAsync_ReportsNoDegradation_WhenBothSourcesAnswered()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, moderated: Moderated(), grants: Grants());

        var result = await service.GetMyChannelsAsync(Principal("mychannels4_self"));

        Assert.False(result.HelixUnavailable);
        Assert.False(result.ReauthRequired);
        Assert.False(result.SevenTvUnavailable);
    }

    [Fact]
    public async Task GetMyChannelsAsync_AnnotatesModeratedChannels_AndNormalizesTheirLogins()
    {
        // Helix answers with display-cased logins; the database holds them lowercase. Without the
        // normalization the same channel would appear twice, once per casing.
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, moderated: Moderated("MyChannels5_Modded"));

        var result = await service.GetMyChannelsAsync(Principal("mychannels5_self"));

        var modded = Assert.Single(result.Channels, c => c.ChannelName == "mychannels5_modded");
        Assert.True(modded.IsModerator);
        Assert.False(modded.IsBroadcaster);
    }

    [Fact]
    public async Task GetMyChannelsAsync_AddsSevenTvEditorChannels_ThatHaveNoTwitchRelationshipAtAll()
    {
        // A 7TV editor grant is independent of the Twitch role axis, so it can introduce entirely
        // new channel keys rather than only annotating ones Helix already returned.
        await using var db = fixture.CreateDbContext();
        var twitchId = NewTwitchId();
        var service = CreateService(
            db,
            helix: HelixWithUsers([new TwitchUserIdentity(twitchId, "mychannels6_edited")]),
            appTokenProvider: AppTokenProvider("app-token"),
            moderated: Moderated(),
            grants: GrantsWithEntries(("mychannels6_edited", twitchId)));

        var result = await service.GetMyChannelsAsync(Principal("mychannels6_self"));

        var edited = Assert.Single(result.Channels, c => c.ChannelName == "mychannels6_edited");
        Assert.True(edited.IsSevenTvEditor);
        Assert.False(edited.IsBroadcaster);
        Assert.False(edited.IsModerator);
    }

    [Fact]
    public async Task GetMyChannelsAsync_CombinesFlags_WhenOneChannelCarriesSeveralRoles()
    {
        await using var db = fixture.CreateDbContext();
        var twitchId = NewTwitchId();
        var service = CreateService(
            db,
            helix: HelixWithUsers([new TwitchUserIdentity(twitchId, "mychannels7_both")]),
            appTokenProvider: AppTokenProvider("app-token"),
            moderated: Moderated("mychannels7_both"),
            grants: GrantsWithEntries(("mychannels7_both", twitchId)));

        var result = await service.GetMyChannelsAsync(Principal("mychannels7_self"));

        var both = Assert.Single(result.Channels, c => c.ChannelName == "mychannels7_both");
        Assert.True(both.IsModerator);
        Assert.True(both.IsSevenTvEditor);
    }

    [Fact]
    public async Task GetMyChannelsAsync_ReportsTrackedAndBotActiveState_FromTheDatabase()
    {
        await using var db = fixture.CreateDbContext();
        db.Channels.Add(new Channel { ChannelName = "mychannels8_active", IsBotActive = true, TwitchChannelId = NewTwitchId() });
        db.Channels.Add(new Channel { ChannelName = "mychannels8_left", IsBotActive = false, TwitchChannelId = NewTwitchId() });
        await db.SaveChangesAsync();
        var service = CreateService(db, moderated: Moderated("mychannels8_active", "mychannels8_left"));

        var result = await service.GetMyChannelsAsync(Principal("mychannels8_self"));

        var active = Assert.Single(result.Channels, c => c.ChannelName == "mychannels8_active");
        Assert.True(active.IsTracked);
        Assert.True(active.IsBotActive);

        // A channel that was left stays tracked — the row and its whole history survive a leave.
        var left = Assert.Single(result.Channels, c => c.ChannelName == "mychannels8_left");
        Assert.True(left.IsTracked);
        Assert.False(left.IsBotActive);
    }

    [Fact]
    public async Task GetMyChannelsAsync_SortsTheOwnChannelFirst_ThenAlphabetically()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, moderated: Moderated("mychannels9_c", "mychannels9_a", "mychannels9_b"));

        var result = await service.GetMyChannelsAsync(Principal("mychannels9_zzz_self"));

        Assert.Equal(
            ["mychannels9_zzz_self", "mychannels9_a", "mychannels9_b", "mychannels9_c"],
            result.Channels.Select(c => c.ChannelName));
    }

    [Fact]
    public async Task GetMyChannelsAsync_NormalizesTheCallersOwnLogin()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);

        var result = await service.GetMyChannelsAsync(Principal("MyChannels10_Self"));

        Assert.Equal("mychannels10_self", Assert.Single(result.Channels).ChannelName);
    }

    [Fact]
    public async Task GetMyChannelsAsync_DerivesLiveState_OnlyForPolledChannels()
    {
        await using var db = fixture.CreateDbContext();
        db.Channels.Add(new Channel { ChannelName = "mychannels11_live", IsBotActive = true, TwitchChannelId = NewTwitchId() });
        db.Channels.Add(new Channel { ChannelName = "mychannels11_off", IsBotActive = true, TwitchChannelId = NewTwitchId() });
        db.Channels.Add(new Channel { ChannelName = "mychannels11_left", IsBotActive = false, TwitchChannelId = NewTwitchId() });
        await db.SaveChangesAsync();
        var polledAt = new DateTime(2026, 8, 3, 18, 0, 0, DateTimeKind.Utc);
        var service = CreateService(
            db,
            moderated: Moderated("mychannels11_live", "mychannels11_off", "mychannels11_left"),
            liveStatus: new TwitchLiveStatusSnapshot(polledAt, ["mychannels11_live"]));

        var result = await service.GetMyChannelsAsync(Principal("mychannels11_self"));

        Assert.Equal(polledAt, result.LivePolledAtUtc);
        Assert.Equal(ChannelLiveStates.Live, Assert.Single(result.Channels, c => c.ChannelName == "mychannels11_live").LiveState);
        // Bot-active, therefore polled: absence from the live set is a real "offline".
        Assert.Equal(ChannelLiveStates.Offline, Assert.Single(result.Channels, c => c.ChannelName == "mychannels11_off").LiveState);
        // A left channel was never polled — absence proves nothing about it.
        Assert.Equal(ChannelLiveStates.Unknown, Assert.Single(result.Channels, c => c.ChannelName == "mychannels11_left").LiveState);
    }

    [Fact]
    public async Task GetMyChannelsAsync_ReportsLiveStateUnknown_WhenNoSnapshotExists()
    {
        // Worker down, poll disabled, or the key expired: no statement about anyone, including
        // bot-active channels — and no poll timestamp to report.
        await using var db = fixture.CreateDbContext();
        db.Channels.Add(new Channel { ChannelName = "mychannels12_active", IsBotActive = true, TwitchChannelId = NewTwitchId() });
        await db.SaveChangesAsync();
        var service = CreateService(db, moderated: Moderated("mychannels12_active"));

        var result = await service.GetMyChannelsAsync(Principal("mychannels12_self"));

        Assert.Null(result.LivePolledAtUtc);
        Assert.All(result.Channels, c => Assert.Equal(ChannelLiveStates.Unknown, c.LiveState));
    }

    // --- Issue #34: 7TV editor grants are resolved by Twitch id, not by 7TV's (possibly stale)
    // login copy. Each test below feeds a mixed-case login into the grant, like Twitch renders them
    // (Regel 9), to pin the normalization alongside the identity-resolution outcome.

    [Fact]
    public async Task GetMyChannelsAsync_DropsAGrant_WhenHelixConfirmsTheIdNoLongerExists()
    {
        // The exact issue #34 scenario: 7TV still reports the grant under a login Twitch no longer
        // knows, and Helix answers successfully but without that id — the account is gone, not
        // merely renamed.
        await using var db = fixture.CreateDbContext();
        var twitchId = NewTwitchId();
        var helix = HelixWithUsers([]);
        var service = CreateService(
            db,
            helix: helix,
            appTokenProvider: AppTokenProvider("app-token"),
            grants: GrantsWithEntries(("MyChannels13_Ghost", twitchId)));

        var result = await service.GetMyChannelsAsync(Principal("mychannels13_self"));

        Assert.DoesNotContain(result.Channels, c => c.ChannelName == "mychannels13_ghost");
        Assert.Single(result.Channels);
    }

    [Fact]
    public async Task GetMyChannelsAsync_ResolvesATrackedRenamedChannel_ByTwitchId_WithoutCallingHelix()
    {
        // The db already tracks this channel under its post-rename name; a Helix round trip would
        // be redundant and is asserted away.
        await using var db = fixture.CreateDbContext();
        var twitchId = NewTwitchId();
        db.Channels.Add(new Channel { ChannelName = "mychannels14_newname", TwitchChannelId = twitchId, IsBotActive = true });
        await db.SaveChangesAsync();
        var helix = HelixWithUsers([]);
        var service = CreateService(
            db,
            helix: helix,
            appTokenProvider: AppTokenProvider("app-token"),
            grants: GrantsWithEntries(("MyChannels14_Oldname", twitchId)));

        var result = await service.GetMyChannelsAsync(Principal("mychannels14_self"));

        var current = Assert.Single(result.Channels, c => c.ChannelName == "mychannels14_newname");
        Assert.True(current.IsSevenTvEditor);
        Assert.True(current.IsTracked);
        Assert.DoesNotContain(result.Channels, c => c.ChannelName == "mychannels14_oldname");
        await helix.DidNotReceive().GetUsersAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMyChannelsAsync_ResolvesAnUntrackedRenamedChannel_UnderItsNewHelixLogin()
    {
        // No db row at all — Helix is the only source of truth here, and it reports the account
        // under a new login.
        await using var db = fixture.CreateDbContext();
        var twitchId = NewTwitchId();
        var helix = HelixWithUsers([new TwitchUserIdentity(twitchId, "MyChannels15_Newlogin")]);
        var service = CreateService(
            db,
            helix: helix,
            appTokenProvider: AppTokenProvider("app-token"),
            grants: GrantsWithEntries(("MyChannels15_Oldlogin", twitchId)));

        var result = await service.GetMyChannelsAsync(Principal("mychannels15_self"));

        var resolved = Assert.Single(result.Channels, c => c.ChannelName == "mychannels15_newlogin");
        Assert.True(resolved.IsSevenTvEditor);
        Assert.DoesNotContain(result.Channels, c => c.ChannelName == "mychannels15_oldlogin");
    }

    [Fact]
    public async Task GetMyChannelsAsync_FallsBackToTheSevenTvLogin_WhenHelixFailsTransiently()
    {
        // The regression case: Helix itself is unreachable, so today's behavior (trust 7TV's login)
        // is the only safe degradation — dropping the grant here would be wrong, unlike the
        // confirmed-dead case above.
        await using var db = fixture.CreateDbContext();
        var twitchId = NewTwitchId();
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetUsersAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TwitchUserIdentity>?)null);
        var service = CreateService(
            db,
            helix: helix,
            appTokenProvider: AppTokenProvider("app-token"),
            grants: GrantsWithEntries(("MyChannels16_Oldlogin", twitchId)));

        var result = await service.GetMyChannelsAsync(Principal("mychannels16_self"));

        var fallback = Assert.Single(result.Channels, c => c.ChannelName == "mychannels16_oldlogin");
        Assert.True(fallback.IsSevenTvEditor);
    }

    [Fact]
    public async Task GetMyChannelsAsync_FallsBackToTheSevenTvLogin_WhenNoAppTokenIsAvailable()
    {
        await using var db = fixture.CreateDbContext();
        var twitchId = NewTwitchId();
        var helix = HelixWithUsers([]);
        var service = CreateService(
            db,
            helix: helix,
            appTokenProvider: AppTokenProvider(null),
            grants: GrantsWithEntries(("MyChannels17_Oldlogin", twitchId)));

        var result = await service.GetMyChannelsAsync(Principal("mychannels17_self"));

        var fallback = Assert.Single(result.Channels, c => c.ChannelName == "mychannels17_oldlogin");
        Assert.True(fallback.IsSevenTvEditor);
        await helix.DidNotReceive().GetUsersAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMyChannelsAsync_ReportsTrackedState_ForAGrantWhoseChannelRowHasNoTwitchIdYet()
    {
        // Regression: a db row predating the id backfill has TwitchChannelId == null. The grant's id
        // resolves via Helix to the very same (unchanged) login, so the row must still be found by
        // name — losing that match makes an actively tracked channel look untracked in the overview.
        await using var db = fixture.CreateDbContext();
        var twitchId = NewTwitchId();
        db.Channels.Add(new Channel { ChannelName = "mychannels20_notyetbackfilled", TwitchChannelId = null, IsBotActive = true });
        await db.SaveChangesAsync();
        var helix = HelixWithUsers([new TwitchUserIdentity(twitchId, "MyChannels20_NotYetBackfilled")]);
        var service = CreateService(
            db,
            helix: helix,
            appTokenProvider: AppTokenProvider("app-token"),
            grants: GrantsWithEntries(("MyChannels20_NotYetBackfilled", twitchId)));

        var result = await service.GetMyChannelsAsync(Principal("mychannels20_self"));

        var channel = Assert.Single(result.Channels, c => c.ChannelName == "mychannels20_notyetbackfilled");
        Assert.True(channel.IsSevenTvEditor);
        Assert.True(channel.IsTracked);
        Assert.True(channel.IsBotActive);
    }

    [Fact]
    public async Task GetMyChannelsAsync_MergesModeratorAndEditorFlags_WhenAGrantResolvesToAnAlreadyKnownChannel()
    {
        // The merge case (Falle a): the user moderates the channel's new name directly, and also
        // holds a 7TV grant whose id resolves to that very same new name — one row, both flags.
        await using var db = fixture.CreateDbContext();
        var twitchId = NewTwitchId();
        var helix = HelixWithUsers([new TwitchUserIdentity(twitchId, "MyChannels19_Newname")]);
        var service = CreateService(
            db,
            helix: helix,
            appTokenProvider: AppTokenProvider("app-token"),
            moderated: Moderated("mychannels19_newname"),
            grants: GrantsWithEntries(("MyChannels19_Oldlogin", twitchId)));

        var result = await service.GetMyChannelsAsync(Principal("mychannels19_self"));

        var merged = Assert.Single(result.Channels, c => c.ChannelName == "mychannels19_newname");
        Assert.True(merged.IsModerator);
        Assert.True(merged.IsSevenTvEditor);
        Assert.DoesNotContain(result.Channels, c => c.ChannelName == "mychannels19_oldlogin");
    }

    private static TwitchPrincipalInfo Principal(string login) => new("42", login, AccessToken: null);

    private static string NewTwitchId() => Guid.NewGuid().ToString("N")[..16];

    private static SevenTvEditorGrants Grants() => new(new HashSet<string>(), new HashSet<string>());

    private static SevenTvEditorGrants GrantsWithEntries(params (string Login, string TwitchId)[] pairs)
    {
        var entries = pairs.Select(p => new SevenTvEditorGrantEntry(ChannelName.Normalize(p.Login), p.TwitchId)).ToList();
        return new SevenTvEditorGrants(
            new HashSet<string>(entries.Select(e => e.ChannelLogin), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(entries.Select(e => e.TwitchChannelId), StringComparer.Ordinal),
            entries);
    }

    private static ITwitchHelixClient HelixWithUsers(IReadOnlyList<TwitchUserIdentity> identities)
    {
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetUsersAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(identities);
        return helix;
    }

    private static ITwitchAppTokenProvider AppTokenProvider(string? token)
    {
        var provider = Substitute.For<ITwitchAppTokenProvider>();
        provider.GetTokenAsync(Arg.Any<CancellationToken>()).Returns(token);
        return provider;
    }

    // Logins go in verbatim, not pre-normalized: the service must keep normalizing them itself,
    // which is what GetMyChannelsAsync_AnnotatesModeratedChannels_AndNormalizesTheirLogins pins.
    private static ModeratedChannelsLookup Moderated(params string[] logins) =>
        new([.. logins.Select((login, index) => new TwitchModeratedChannelInfo(login, $"moderated-{index}"))], ReauthRequired: false);

    private static ModeratedChannelsLookup Undetermined(bool reauthRequired = false) => new(null, reauthRequired);

    private static MyChannelsService CreateService(
        AppDbContext db,
        ITwitchHelixClient? helix = null,
        ISevenTvEditorService? editors = null,
        ITwitchAppTokenProvider? appTokenProvider = null,
        ModeratedChannelsLookup? moderated = null,
        SevenTvEditorGrants? grants = null,
        TwitchLiveStatusSnapshot? liveStatus = null)
    {
        // Default is "answered, moderates nothing" — the degraded variants are opt-in, so no test
        // accidentally asserts against a HelixUnavailable it never meant to configure.
        var moderatedChannelsProvider = Substitute.For<IModeratedChannelsProvider>();
        moderatedChannelsProvider.GetModeratedChannelsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>())
            .Returns(moderated ?? Moderated());

        if (editors is null)
        {
            editors = Substitute.For<ISevenTvEditorService>();
            editors.GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(SevenTvEditorGrantsLookupResult.Ok(grants ?? Grants()));
        }

        // Default is "no snapshot": the live-status axis stays out of every test that is not
        // explicitly about it.
        var liveStatusReader = Substitute.For<ITwitchLiveStatusReader>();
        liveStatusReader.ReadAsync(Arg.Any<CancellationToken>()).Returns(liveStatus);

        return new MyChannelsService(
            db,
            helix ?? Substitute.For<ITwitchHelixClient>(),
            editors,
            moderatedChannelsProvider,
            liveStatusReader,
            appTokenProvider ?? Substitute.For<ITwitchAppTokenProvider>(),
            NullLogger<MyChannelsService>.Instance);
    }
}
