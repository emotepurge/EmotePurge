using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Steps 3 and 4 of the bot pass (#487 plan 2.5, 2.8, 2.5b; T8b) against real Postgres in a database of its own: the
// channel states with suspension and resume (not_listed, not_editor, bot_account_changed), the editor proof bound to
// identity and bot id, and the fallback run. The editor read is the real BotAccountEditorCheck over a substituted 7TV
// client; the session is a hand-made one whose epoch matches the row (the real runner is in WishBotPassTests.Runner.cs).
// The clock shows 2026-11-25 12:00 UTC, a whole second, so stamped times read back exactly.
[Collection("Postgres")]
public partial class WishBotPassTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "wish_bot_pass_tests";
    private const string BotA = "01BOTACCOUNTA";
    private const string BotB = "01BOTACCOUNTB";
    private const int Identity = 4;
    private const long Epoch = 3;
    private const int SeededVersion = 3;
    private const string Activator = "4242";

    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly IRedisPublisher _publisher = Substitute.For<IRedisPublisher>();
    private readonly ISevenTvApiClient _sevenTv = Substitute.For<ISevenTvApiClient>();
    private readonly ISevenTvWriteClient _writes = Substitute.For<ISevenTvWriteClient>();
    private readonly IGlobalAdminAllowlist _admins = Substitute.For<IGlobalAdminAllowlist>();
    private readonly List<IAsyncDisposable> _disposables = [];

    // The editor read's answer per bot id: Twitch channel id → (state, manage); a bot id without an entry is Unavailable.
    // A held read (HoldNextRead) answers only when the test releases it.
    private readonly Dictionary<string, Dictionary<string, (string State, bool Manage)>> _editors = new(StringComparer.Ordinal);
    private TaskCompletionSource<SevenTvEditorForResult>? _heldRead;
    private TaskCompletionSource? _heldReadEntered;
    private BotAccountEditorCheck? _editorCheck;
    private int _visibilitySeconds = 300;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private BotAccountEditorCheck EditorCheck => _editorCheck ??= new BotAccountEditorCheck(_sevenTv, _clock);

    public async Task InitializeAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (!_databaseReady)
            {
                await using (var admin = fixture.CreateDbContext())
                {
                    // A constant name, not user input; CREATE DATABASE cannot be parameterised.
#pragma warning disable EF1002
                    await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {DatabaseName}");
#pragma warning restore EF1002
                }

                await using var migrate = CreateDb();
                await migrate.Database.MigrateAsync();
                _databaseReady = true;
            }
        }
        finally
        {
            DatabaseGate.Release();
        }

        await using var reset = CreateDb();
        await reset.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"Wishes\", \"ChannelWishSettings\", \"AuditLogEntries\", \"Channels\", \"BotAccountCredentials\" CASCADE;");

        _sevenTv.GetEditorForAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => AnswerEditorRead(call.Arg<string>()));
        _writes.AcceptEditorInvitationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => AnswerAcceptAsync(call.ArgAt<string>(1), call.ArgAt<string>(2)));
        _writes.AddEmoteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => AnswerAddAsync(call.ArgAt<string>(1), call.ArgAt<string>(2), call.ArgAt<string>(3)));
        _writes.RemoveEmoteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => AnswerRemoveAsync(call.ArgAt<string>(1), call.ArgAt<string>(2), call.ArgAt<string>(3)));
        _writes.ReadSetEntriesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Func<TimeSpan>?>(), Arg.Any<CancellationToken>())
            .Returns(call => AnswerSetReadAsync(call.ArgAt<string>(1), call.ArgAt<Func<TimeSpan>?>(2)));
    }

    public async Task DisposeAsync()
    {
        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            await _disposables[i].DisposeAsync();
        }
    }

    // ------------------------------------------------------------------------------------ AK80: per-channel fallback

    // A negative read suspends that channel as not_editor with one audit; the fallback run hands its open actions over;
    // the other channel, an editor, runs on with a fresh proof. The same read again changes nothing.
    [Fact]
    public async Task ANegativeRead_SuspendsNotEditor_WithOneAudit_OtherChannelsRunOn()
    {
        await SeedAccountAsync(BotA);
        var lost = await SeedChannelAsync();
        var kept = await SeedChannelAsync();
        Editors(BotA, (kept.TwitchId, "ACCEPTED", true), (lost.TwitchId, "REJECTED", true));
        var lostWish = await SeedWishAsync(lost, WishStatus.Unconfirmed, w => w.BotWriteAtUtc = Now.AddMinutes(-3));
        var keptWish = await SeedWishAsync(kept, WishStatus.Unconfirmed, w => w.BotWriteAtUtc = Now.AddMinutes(-3));

        await RunPassAsync();

        var lostSettings = await SettingsAsync(lost);
        Assert.Equal(WishBotChannelReasons.NotEditor, lostSettings.BotSuspendedReason);
        Assert.True(lostSettings.BotModeEnabled);
        Assert.Equal(Activator, lostSettings.BotModeActivatedByTwitchUserId);
        Assert.Equal(new[] { (lost.Name, "reason", WishBotChannelReasons.NotEditor) }, await SuspendAuditsAsync());
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(lostWish)));

        var keptSettings = await SettingsAsync(kept);
        Assert.Null(keptSettings.BotSuspendedReason);
        Assert.Equal((Now, Identity), (keptSettings.BotEditorCheckedAtUtc, keptSettings.BotEditorCheckedIdentityVersion));
        Assert.Equal(WishStatus.Unconfirmed, (await LoadAsync(keptWish)).Status);

        // One event for the changed state, one after the fallback's last commit; none for the channel that stays bot.
        Assert.Equal(2, EventsFor(lost));
        Assert.Equal(0, EventsFor(kept));

        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();

        Assert.Single(await SuspendAuditsAsync());
        Assert.Equal(2, EventsFor(lost));
        await _sevenTv.Received(1).GetEditorForAsync(BotA, Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------------------------ AK85: the pilot list in operation

    // A bot channel drops out of the list: not_listed, one audit, the open bot actions go to the managers (approved stays
    // and counts, unconfirmed and the queued ones are presented or lose their order), the activation stays stored. Back
    // on the list, the next read resumes it without a new activation, and only approved runs on with the bot.
    [Fact]
    public async Task DroppedFromTheList_IsNotListed_TheFallbackHandsOver_BackOnTheListResumesOnlyApproved()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedChannelAsync();
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));
        var approved = await SeedWishAsync(channel, WishStatus.Approved);
        var unconfirmed = await SeedWishAsync(channel, WishStatus.Unconfirmed, w => w.BotWriteAtUtc = Now.AddMinutes(-3));
        var queued = await SeedWishAsync(channel, WishStatus.Active, Queue);
        var ordered = await SeedWishAsync(channel, WishStatus.Presented, w =>
        {
            w.StatusReason = WishStatusReason.Protected;
            Queue(w);
        });

        await RunPassAsync(pilot: "999");

        var settings = await SettingsAsync(channel);
        Assert.Equal(WishBotChannelReasons.NotListed, settings.BotSuspendedReason);
        Assert.True(settings.BotModeEnabled);
        Assert.Equal(Activator, settings.BotModeActivatedByTwitchUserId);
        Assert.Equal(new[] { (channel.Name, "reason", WishBotChannelReasons.NotListed) }, await SuspendAuditsAsync());
        Assert.Equal((WishStatus.Approved, null), StatusOf(await LoadAsync(approved)));
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(unconfirmed)));
        var queuedWish = await LoadAsync(queued);
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(queuedWish));
        Assert.Null(queuedWish.RemovalQueuedAtUtc);
        var orderedWish = await LoadAsync(ordered);
        Assert.Equal((WishStatus.Presented, WishStatusReason.Protected), StatusOf(orderedWish));
        Assert.Null(orderedWish.RemovalQueuedAtUtc);
        await _sevenTv.DidNotReceiveWithAnyArgs().GetEditorForAsync(default!, default);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await RunPassAsync(pilot: "*");

        settings = await SettingsAsync(channel);
        Assert.Null(settings.BotSuspendedReason);
        Assert.Equal(Now, settings.BotEditorCheckedAtUtc);
        Assert.Equal(WishBotChannelMode.Bot, ModeOf(settings, listed: true));
        Assert.Equal((channel.Name, "resumed", "True"), (await SuspendAuditsAsync())[^1]);
        Assert.Equal((WishStatus.Approved, null), StatusOf(await LoadAsync(approved)));
        Assert.Equal(WishStatus.Presented, (await LoadAsync(unconfirmed)).Status);
        Assert.Equal(WishStatus.Presented, (await LoadAsync(queued)).Status);
    }

    // E8, [B] F2: an activation by a global admin (by id) covers an unlisted channel; once the admin is removed from the
    // allowlist the channel is not_listed.
    [Fact]
    public async Task AnAdminActivationOutsideTheList_IsCovered_UntilTheAdminIsRemoved()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedChannelAsync();
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));
        _admins.IsAdminById(Activator).Returns(true);

        await RunPassAsync(pilot: "");
        Assert.Null((await SettingsAsync(channel)).BotSuspendedReason);
        Assert.Empty(await SuspendAuditsAsync());

        _admins.IsAdminById(Activator).Returns(false);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync(pilot: "");

        Assert.Equal(WishBotChannelReasons.NotListed, (await SettingsAsync(channel)).BotSuspendedReason);
        Assert.Single(await SuspendAuditsAsync());
    }

    // ------------------------------------------------------------------------------------ 7TV outage (Sol-P2-7)

    // An unavailable read suspends nothing. Held for 15 minutes, the stale proof alone puts the channel into the
    // fallback (approved counts as needing a human, the open actions are handed over) without any wish.botSuspend; the
    // first successful read makes it bot again.
    [Fact]
    public async Task APermanentlyUnavailableRead_SuspendsNothing_FallsBackAfter15Minutes_AndTheFirstGoodReadResumes()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedChannelAsync();
        var unconfirmed = await SeedWishAsync(channel, WishStatus.Unconfirmed, w => w.BotWriteAtUtc = Now.AddMinutes(-3));
        var approved = await SeedWishAsync(channel, WishStatus.Approved);

        await RunPassAsync();
        Assert.Null((await SettingsAsync(channel)).BotSuspendedReason);
        Assert.Equal(WishStatus.Unconfirmed, (await LoadAsync(unconfirmed)).Status);

        _clock.Advance(WishBotChannelState.EditorCheckFreshness);
        await RunPassAsync();
        var settings = await SettingsAsync(channel);
        Assert.Null(settings.BotSuspendedReason);
        Assert.Equal(WishBotChannelMode.Fallback, ModeOf(settings, listed: true));
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(unconfirmed)));
        Assert.Equal(WishStatus.Approved, (await LoadAsync(approved)).Status);

        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();

        settings = await SettingsAsync(channel);
        Assert.Equal(WishBotChannelMode.Bot, ModeOf(settings, listed: true));
        Assert.Equal(Now, settings.BotEditorCheckedAtUtc);
        Assert.Empty(await SuspendAuditsAsync());
        Assert.Equal(WishStatus.Approved, (await LoadAsync(approved)).Status);
    }

    // ------------------------------------------------------------------------------------ AK79: global fallback

    // The token is invalid: approved stays, unconfirmed and the queued removal are presented, no channel gets a reason.
    // A new valid token (a hand PUT, the next identity) with its own read makes the channel bot again; only approved runs
    // on. The activation request of another channel stays untouched while the account is unavailable.
    [Fact]
    public async Task AnInvalidToken_FallsBackEveryBotChannel_ANewTokenResumesOnlyApproved()
    {
        await SeedAccountAsync(BotA, status: BotAccountStatus.Invalid);
        var channel = await SeedChannelAsync();
        var requested = await SeedChannelAsync(botMode: false, configure: s =>
        {
            s.BotActivationRequestedAtUtc = Now.AddMinutes(-1);
            s.BotActivationRequestedByTwitchUserId = Activator;
            s.BotConsentSevenTvUserId = BotA;
        });
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true), (requested.TwitchId, "ACCEPTED", true));
        var approved = await SeedWishAsync(channel, WishStatus.Approved);
        var unconfirmed = await SeedWishAsync(channel, WishStatus.Unconfirmed, w => w.BotWriteAtUtc = Now.AddMinutes(-3));
        var queued = await SeedWishAsync(channel, WishStatus.Active, Queue);

        await RunPassAsync();

        Assert.Equal((WishStatus.Approved, null), StatusOf(await LoadAsync(approved)));
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(unconfirmed)));
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(await LoadAsync(queued)));
        Assert.Null((await SettingsAsync(channel)).BotSuspendedReason);
        Assert.Empty(await SuspendAuditsAsync());
        var request = await SettingsAsync(requested);
        Assert.Equal((false, Now.AddMinutes(-1), (string?)null), (request.BotModeEnabled, request.BotActivationRequestedAtUtc, request.BotActivationError));
        await _writes.DidNotReceiveWithAnyArgs().AcceptEditorInvitationAsync(default!, default!, default!, default);
        await _sevenTv.DidNotReceiveWithAnyArgs().GetEditorForAsync(default!, default);

        await SeedAccountAsync(BotA, identity: Identity + 1);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await RunPassAsync();

        var settings = await SettingsAsync(channel);
        Assert.Equal((Now, Identity + 1), (settings.BotEditorCheckedAtUtc, settings.BotEditorCheckedIdentityVersion));
        Assert.Equal(WishBotChannelMode.Bot, ModeOf(settings, listed: true, identity: Identity + 1));
        Assert.Equal(WishStatus.Approved, (await LoadAsync(approved)).Status);
        Assert.Equal(WishStatus.Presented, (await LoadAsync(unconfirmed)).Status);
        Assert.Equal(WishStatus.Presented, (await LoadAsync(queued)).Status);

        // T8c: with the account available again, the waiting request is confirmed from the new identity's read.
        request = await SettingsAsync(requested);
        Assert.True(request.BotModeEnabled);
        Assert.Equal((Now, Identity + 1), (request.BotEditorCheckedAtUtc, request.BotEditorCheckedIdentityVersion));
    }

    // The fallback twice in a row changes nothing the second time: no field, no event.
    [Fact]
    public async Task TheFallback_TwiceInARow_ChangesNothingTheSecondTime()
    {
        await SeedAccountAsync(BotA, status: BotAccountStatus.Expired);
        var channel = await SeedChannelAsync();
        var ids = new[]
        {
            await SeedWishAsync(channel, WishStatus.Unconfirmed, w => w.BotWriteAtUtc = Now.AddMinutes(-3)),
            await SeedWishAsync(channel, WishStatus.Active, Queue),
            await SeedWishAsync(channel, WishStatus.Approved, w => w.BotWriteAtUtc = Now.AddMinutes(-1)),
        };

        await RunPassAsync();
        var first = await Task.WhenAll(ids.Select(SnapshotAsync));
        Assert.Equal(1, EventsFor(channel));

        _clock.Advance(TimeSpan.FromMinutes(1));
        await RunPassAsync();

        Assert.Equal(first, await Task.WhenAll(ids.Select(SnapshotAsync)));
        Assert.Equal(1, EventsFor(channel));
    }

    // ------------------------------------------------------------------------------------ S6-2: consent bound to the bot id

    // Activated for A, a hand PUT sets B, and B is an editor there already: bot_account_changed (no read needed, so no
    // 7TV call at all), one audit, the fallback hands over. With A's token back the channel resumes without a new
    // activation once A's read proves it.
    [Fact]
    public async Task ConsentToA_TokenForB_EvenWhereBIsEditor_IsBotAccountChanged_UntilAIsBack()
    {
        await SeedAccountAsync(BotB, identity: Identity + 1);
        var channel = await SeedChannelAsync();
        Editors(BotB, (channel.TwitchId, "ACCEPTED", true));
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));
        var unconfirmed = await SeedWishAsync(channel, WishStatus.Unconfirmed, w => w.BotWriteAtUtc = Now.AddMinutes(-3));
        var queued = await SeedWishAsync(channel, WishStatus.Active, Queue);

        await RunPassAsync();

        Assert.Equal(WishBotChannelReasons.BotAccountChanged, (await SettingsAsync(channel)).BotSuspendedReason);
        Assert.Equal(new[] { (channel.Name, "reason", WishBotChannelReasons.BotAccountChanged) }, await SuspendAuditsAsync());
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(unconfirmed)));
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(await LoadAsync(queued)));
        await _sevenTv.DidNotReceiveWithAnyArgs().GetEditorForAsync(default!, default);

        await SeedAccountAsync(BotA, identity: Identity + 2);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();

        var settings = await SettingsAsync(channel);
        Assert.Null(settings.BotSuspendedReason);
        Assert.True(settings.BotModeEnabled);
        Assert.Equal(BotA, settings.BotConsentSevenTvUserId);
        Assert.Equal((channel.Name, "resumed", "True"), (await SuspendAuditsAsync())[^1]);
        Assert.Equal(WishBotChannelMode.Bot, ModeOf(settings, listed: true, identity: Identity + 2));
    }

    // ------------------------------------------------------------------------------------ The proof's identity (R2-5)

    // The unauthenticated read for A is held; a renewal swap (same bot id, same identity, new credential version)
    // commits meanwhile: the booking still counts.
    [Fact]
    public async Task AHeldRead_ARenewalSwapBeforeTheBooking_TheBookingCounts()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedChannelAsync(proofAt: Now.AddMinutes(-2));
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));
        HoldNextRead();

        var pass = RunPassAsync();
        await _heldReadEntered!.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await using (var db = CreateDb())
        {
            await db.BotAccountCredentials.ExecuteUpdateAsync(u => u.SetProperty(c => c.CredentialVersion, c => c.CredentialVersion + 1));
        }

        ReleaseHeldRead();
        await pass;

        var settings = await SettingsAsync(channel);
        Assert.Equal((Now, Identity), (settings.BotEditorCheckedAtUtc, settings.BotEditorCheckedIdentityVersion));
    }

    // The read for A is held and negative; a hand PUT sets B meanwhile (next identity, not yet verified): the result for
    // A is discarded, nothing is suspended (the new token proves the channel itself later).
    [Fact]
    public async Task AHeldNegativeRead_AHandPutBeforeTheBooking_SuspendsNothing()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedChannelAsync();
        Editors(BotA);
        HoldNextRead();

        var pass = RunPassAsync();
        await _heldReadEntered!.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await using (var db = CreateDb())
        {
            await db.BotAccountCredentials.ExecuteUpdateAsync(u => u
                .SetProperty(c => c.IdentityVersion, Identity + 1)
                .SetProperty(c => c.CredentialVersion, c => c.CredentialVersion + 1)
                .SetProperty(c => c.Status, BotAccountStatus.Unverified)
                .SetProperty(c => c.BotSevenTvUserId, (string?)null));
        }

        ReleaseHeldRead();
        await pass;

        Assert.Null((await SettingsAsync(channel)).BotSuspendedReason);
        Assert.Empty(await SuspendAuditsAsync());
    }

    // The fence: with the runner epoch moved on, the pass books nothing and reports the lock as lost; a lost lock before
    // the read means no 7TV call at all.
    [Fact]
    public async Task AMovedEpochOrALostLock_BooksNothing_AndEndsThePass()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedChannelAsync();
        Editors(BotA);
        var unconfirmed = await SeedWishAsync(channel, WishStatus.Unconfirmed, w => w.BotWriteAtUtc = Now.AddMinutes(-3));

        var noLock = await SessionAsync();
        noLock.HoldsLock = false;
        await RunPassAsync(session: noLock);
        await _sevenTv.DidNotReceiveWithAnyArgs().GetEditorForAsync(default!, default);

        await using (var db = CreateDb())
        {
            await db.BotAccountCredentials.ExecuteUpdateAsync(u => u.SetProperty(c => c.RunnerEpoch, Epoch + 1));
        }

        var fenced = await SessionAsync(epoch: Epoch);
        await RunPassAsync(session: fenced);

        Assert.True(fenced.LockLostReported);
        Assert.Null((await SettingsAsync(channel)).BotSuspendedReason);
        Assert.Empty(await SuspendAuditsAsync());
        Assert.Equal(WishStatus.Unconfirmed, (await LoadAsync(unconfirmed)).Status);
    }

    // ------------------------------------------------------------------------------------ K6b-17

    // The fallback run selects without a lock: a queued active wish that gets keep and is deleted (K7a) between the
    // selection and its row lock is skipped without an error, the channel's other wishes are handed over.
    [Fact]
    public async Task TheFallback_SkipsAWishDeletedBetweenSelectionAndLock_AndHandsOverTheRest()
    {
        await SeedAccountAsync(BotA, status: BotAccountStatus.Invalid);
        var channel = await SeedChannelAsync();
        var first = await SeedWishAsync(channel, WishStatus.Unconfirmed, w => w.BotWriteAtUtc = Now.AddMinutes(-3));
        var deleted = await SeedWishAsync(channel, WishStatus.Active, Queue);
        var last = await SeedWishAsync(channel, WishStatus.Presented, w =>
        {
            w.StatusReason = WishStatusReason.Protected;
            Queue(w);
        });
        var hook = new CommandHook(IsWishLock, async occurrence =>
        {
            if (occurrence == 1)
            {
                await using var other = CreateDb();
                await other.Wishes.Where(w => w.Id == deleted).ExecuteUpdateAsync(u => u
                    .SetProperty(w => w.Status, WishStatus.Kept)
                    .SetProperty(w => w.RemovalQueuedAtUtc, (DateTime?)null)
                    .SetProperty(w => w.EndedAtUtc, Now));
                await other.Wishes.Where(w => w.Id == deleted).ExecuteDeleteAsync();
            }
        });
        await using var db = fixture.CreateTaggedDbContext("wish-bot-fallback-k6b17", [hook], databaseName: DatabaseName);

        await RunPassAsync(db: db);

        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(first)));
        Assert.Null(await FindAsync(deleted));
        Assert.Null((await LoadAsync(last)).RemovalQueuedAtUtc);
        Assert.Equal(3, hook.Count(IsWishLock));
        Assert.Equal(1, EventsFor(channel));
    }

    // ------------------------------------------------------------------------------------ Helpers

    private AppDbContext CreateDb() => fixture.CreateDbContext(DatabaseName);

    // One pass of steps 3 and 4 on its own context (or the given one), with the session of the account row as it is.
    private async Task RunPassAsync(
        string pilot = "*", AppDbContext? db = null, FakeSession? session = null, bool botAccountEnabled = true)
    {
        var context = db ?? CreateDb();
        try
        {
            session ??= await SessionAsync();
            session.Db = context;
            await Pass(context, pilot, botAccountEnabled).RunAsync(session);
            Assert.False(context.ChangeTracker.HasChanges());
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally
        {
            if (db is null)
            {
                await context.DisposeAsync();
            }
        }
    }

    private WishBotPass Pass(AppDbContext db, string pilot = "*", bool botAccountEnabled = true)
    {
        var wishOptions = new WishOptions { Enabled = true, PilotChannelIds = pilot };
        var botAccountOptions = new BotAccountOptions { Enabled = botAccountEnabled, EditorVisibilitySeconds = _visibilitySeconds };
        var publisher = new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance);
        var fallback = new WishBotFallback(db, wishOptions, botAccountOptions, _admins, publisher, _clock, NullLogger<WishBotFallback>.Instance);
        return new WishBotPass(
            db, EditorCheck, _writes, fallback, wishOptions, botAccountOptions, _admins, publisher, _clock, NullLogger<WishBotPass>.Instance);
    }

    private async Task<FakeSession> SessionAsync(long epoch = Epoch)
    {
        await using var db = CreateDb();
        var row = await db.BotAccountCredentials.AsNoTracking().SingleAsync();
        _lastSession = new FakeSession(
            new BotAccountSnapshot(
                row.Status, row.ExpiresAtUtc, row.BotSevenTvUserId, row.KillSwitch, row.PausedUntilUtc, row.CredentialVersion, row.IdentityVersion),
            epoch,
            _clock);
        return _lastSession;
    }

    // The credential row as the token upkeep leaves it: an upsert, so a later call is a hand PUT (or a token back).
    private async Task SeedAccountAsync(string botId, string status = BotAccountStatus.Valid, int identity = Identity)
    {
        await using var db = CreateDb();
        var row = await db.BotAccountCredentials.SingleOrDefaultAsync();
        if (row is null)
        {
            row = new BotAccountCredential { RunnerEpoch = Epoch, CredentialVersion = 1 };
            db.BotAccountCredentials.Add(row);
        }
        else
        {
            row.CredentialVersion++;
        }

        row.SealedToken = "v1.x";
        row.Status = status;
        row.ExpiresAtUtc = Now.AddDays(30);
        row.BotSevenTvUserId = botId;
        row.IdentityVersion = identity;
        await db.SaveChangesAsync();
    }

    // An activated channel, consented to A, with an editor proof of A's identity 10 seconds old (or as given).
    private async Task<Seeded> SeedChannelAsync(
        bool botMode = true, DateTime? proofAt = null, Action<ChannelWishSettings>? configure = null)
    {
        var name = $"x{Guid.NewGuid():N}"[..18];
        var twitchId = Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var db = CreateDb();
        var channel = new Channel
        {
            ChannelName = name,
            TwitchChannelId = twitchId,
            IsBotActive = true,
            CreatedAt = Now.AddDays(-400),
        };
        db.Channels.Add(channel);
        var settings = new ChannelWishSettings
        {
            Channel = channel,
            BotModeEnabled = botMode,
            BotModeActivatedByTwitchUserId = botMode ? Activator : null,
            BotModeActivatedAtUtc = botMode ? Now.AddDays(-2) : null,
            BotConsentSevenTvUserId = botMode ? BotA : null,
            BotEditorCheckedAtUtc = botMode ? proofAt ?? Now.AddSeconds(-10) : null,
            BotEditorCheckedIdentityVersion = botMode ? Identity : null,
        };
        configure?.Invoke(settings);
        db.ChannelWishSettings.Add(settings);
        await db.SaveChangesAsync();
        return new Seeded(channel.Id, name, twitchId);
    }

    private async Task<long> SeedWishAsync(Seeded channel, string status, Action<Wish>? configure = null)
    {
        await using var db = CreateDb();
        var wish = new Wish
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = $"01EMOTE{Guid.NewGuid():N}"[..26].ToUpperInvariant(),
            EmoteName = "Emote",
            Alias = "Alias",
            Source = WishSource.Manual,
            Status = status,
            StatusChangedAtUtc = Now.AddDays(-1),
            CreatedAtUtc = Now.AddDays(-2),
            StartsAtUtc = status == WishStatus.Approved ? null : Now.AddDays(-1),
            StartsAtAuthoritative = status is WishStatus.Active or WishStatus.Presented,
            ExpiresAtUtc = status == WishStatus.Approved ? null : Now.AddDays(6),
            DecisionVersion = SeededVersion,
        };
        configure?.Invoke(wish);
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }

    private void Queue(Wish wish)
    {
        wish.RemovalQueuedAtUtc = Now.AddHours(-1);
        wish.RemovalQueuedVersion = SeededVersion;
    }

    private void Editors(string botId, params (string TwitchId, string State, bool Manage)[] entries) =>
        _editors[botId] = entries.ToDictionary(e => e.TwitchId, e => (e.State, e.Manage), StringComparer.Ordinal);

    private void HoldNextRead()
    {
        _heldRead = new TaskCompletionSource<SevenTvEditorForResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _heldReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ReleaseHeldRead()
    {
        var held = _heldRead!;
        _heldRead = null;
        held.SetResult(AnswerNow(BotA));
    }

    private Task<SevenTvEditorForResult> AnswerEditorRead(string botId)
    {
        if (_heldRead is { } held)
        {
            _heldReadEntered!.TrySetResult();
            return held.Task;
        }

        return Task.FromResult(AnswerNow(botId));
    }

    private SevenTvEditorForResult AnswerNow(string botId) =>
        _editors.TryGetValue(botId, out var entries)
            ? SevenTvEditorForResult.Ok(entries
                .Select(e => new SevenTvEditorForEntry($"01OWNER{e.Key}", e.Key, e.Value.State, e.Value.Manage, null))
                .ToList())
            : SevenTvEditorForResult.Failed(SevenTvLookupStatus.Unavailable);

    private async Task<ChannelWishSettings> SettingsAsync(Seeded channel)
    {
        await using var db = CreateDb();
        return await db.ChannelWishSettings.AsNoTracking().SingleAsync(s => s.ChannelId == channel.Id);
    }

    private WishBotChannelMode ModeOf(ChannelWishSettings settings, bool listed, int identity = Identity, string botId = BotA) =>
        WishBotChannelState.Of(settings, new BotAccountAvailabilityState(true, botId, identity), listed, false, Now);

    private async Task<Wish> LoadAsync(long id) => (await FindAsync(id))!;

    private async Task<Wish?> FindAsync(long id)
    {
        await using var db = CreateDb();
        return await db.Wishes.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id);
    }

    private async Task<string> SnapshotAsync(long id) => JsonSerializer.Serialize(await LoadAsync(id));

    private async Task<List<(string Channel, string Key, string Value)>> SuspendAuditsAsync()
    {
        await using var db = CreateDb();
        var entries = await db.AuditLogEntries.AsNoTracking()
            .Where(a => a.Action == AuditActions.WishBotSuspend)
            .OrderBy(a => a.Id)
            .ToListAsync();
        Assert.All(entries, e => Assert.Equal(AuditActor.System.TwitchUserId, e.ActorTwitchUserId));
        return entries.Select(e =>
        {
            using var details = JsonDocument.Parse(e.DetailsJson!);
            var property = Assert.Single(details.RootElement.EnumerateObject());
            return (e.ChannelName!, property.Name, property.Value.ToString());
        }).ToList();
    }

    private int EventsFor(Seeded channel) =>
        _publisher.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IRedisPublisher.PublishAsync))
            .Select(c => LiveEvent.TryParse((string?)c.GetArguments()[1]))
            .Count(e => e == new LiveEvent(LiveEvents.WishChanged, channel.Name));

    private static (string Status, string? Reason) StatusOf(Wish wish) => (wish.Status, wish.StatusReason);

    private static bool IsWishLock(string command) =>
        command.Contains("FROM \"Wishes\"", StringComparison.Ordinal) && command.Contains("FOR UPDATE", StringComparison.Ordinal);

    private sealed record Seeded(string Id, string Name, string TwitchId);

    // A session as the runner hands it over, with the epoch the row was seeded with; the lock probe is a switch (or a
    // hook per call), the write spacing is simulated on the test clock (a wait advances it), and every session call is
    // recorded in order.
    private sealed class FakeSession(BotAccountSnapshot account, long epoch, FakeTimeProvider clock) : IBotAccountSession
    {
        private DateTime? _lastWrite;
        private int _probes;
        private int _waits;

        public BotAccountSnapshot Account { get; set; } = account;
        public string? Token => Account.Status == BotAccountStatus.Valid ? "token" : null;
        public bool TokenUsable { get; set; } = true;
        public long RunnerEpoch { get; } = epoch;
        public int RemainingActions { get; set; } = 50;
        public bool HoldsLock { get; set; } = true;
        public bool LockLostReported { get; private set; }
        public TimeSpan WriteDelay { get; set; }
        public List<string> Calls { get; } = [];
        public List<TimeSpan> Pauses { get; } = [];
        public Func<int, Task<bool>>? OnProbe { get; set; }

        // Runs inside WaitForWriteSlotAsync (before the intent of an add), with the number of the wait.
        public Func<int, Task>? OnWait { get; set; }

        // The pass's context: the probe must never run while it has a transaction (with row locks) open (K6f-1).
        public AppDbContext? Db { get; set; }

        public async Task<bool> HoldsLockAsync(CancellationToken cancellationToken = default)
        {
            if (Db?.Database.CurrentTransaction is not null)
            {
                throw new InvalidOperationException("The lock probe ran under a row lock.");
            }

            Calls.Add("probe");
            return OnProbe is { } hook ? await hook(++_probes) : HoldsLock;
        }

        public bool TryTakeAction()
        {
            Calls.Add("action");
            if (RemainingActions <= 0)
            {
                return false;
            }

            RemainingActions--;
            return true;
        }

        public async Task WaitForWriteSlotAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("wait");
            if (_lastWrite is { } last && last + WriteDelay > clock.GetUtcNow().UtcDateTime)
            {
                clock.Advance(last + WriteDelay - clock.GetUtcNow().UtcDateTime);
            }

            if (OnWait is { } hook)
            {
                await hook(++_waits);
            }
        }

        public void MarkWriteSent()
        {
            Calls.Add("sent");
            _lastWrite = clock.GetUtcNow().UtcDateTime;
        }

        public void ReportUnauthorized()
        {
            Calls.Add("unauthorized");
            TokenUsable = false;
        }

        public void ReportLockLost() => LockLostReported = true;

        public Task<bool> PauseAsync(TimeSpan retryAfter, CancellationToken cancellationToken = default)
        {
            // "pause" joined to an open transaction of the pass (K6c-3), "pause-alone" without one.
            Calls.Add(Db?.Database.CurrentTransaction is null ? "pause-alone" : "pause");
            Pauses.Add(retryAfter);
            TokenUsable = false;
            return Task.FromResult(false);
        }
    }
}
