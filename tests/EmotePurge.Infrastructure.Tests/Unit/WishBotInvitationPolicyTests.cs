using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.BotAccount;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The pure rule of step 2 (#487 plan 2.5b, 2.9; T8c): resolving an open acceptance attempt, a request (case (a)) and an
// invitation of an activated channel (case (b)).
public class WishBotInvitationPolicyTests
{
    private const string BotA = "01BOTACCOUNTA";
    private const string BotB = "01BOTACCOUNTB";
    private const string Twitch = "111";
    private const string Owner = "01OWNER111";
    private const int Identity = 4;
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(300);
    private static readonly BotAccountAvailabilityState AccountA = new(true, BotA, Identity);

    // ------------------------------------------------------------------------------------ constants

    // K6c-4: the latest end of the wait is the stamp + 30 s + the client's 10 s; the policy's constant is the client's.
    [Fact]
    public void TheNegativeWindow_StartsAfterSendWindowClientTimeLimitAndVisibility()
    {
        Assert.Equal(BotAccountRegistration.WriteClientTimeout, WishBotInvitationPolicy.ClientTimeLimit);
        Assert.Equal(Now.AddSeconds(340), WishBotInvitationPolicy.NegativeResolutionFrom(Now, Window));
    }

    // ------------------------------------------------------------------------------------ an open attempt

    // K6d-2: ACCEPTED resolves positively at once, with or without "manage"; consent stands while a request or an
    // activation does, gone after a DELETE (binding kept, nothing standing).
    [Theory]
    [InlineData("ACCEPTED", true, false, true, false)]
    [InlineData("ACCEPTED", false, false, true, false)]
    [InlineData("ACCEPTED", true, true, false, false)]
    [InlineData("ACCEPTED", true, false, false, true)]
    public void AnAcceptedEntry_ResolvesTheAttemptAtOnce(string state, bool manage, bool enabled, bool request, bool consentGone)
    {
        var settings = Settings(enabled: enabled, requestedAt: request ? Now.AddMinutes(-1) : null, attemptAt: Now.AddSeconds(-1));

        var decision = Decide(settings, Read(BotA, Now, (state, manage)));

        Assert.Equal(new WishBotInvitationDecision(WishBotInvitationAction.ResolveAccepted, ConsentGone: consentGone), decision);
    }

    // S6-1, K6c-4: no ACCEPTED decides only from a read that began at or after the stamp + 40 s + window. Before: nothing,
    // or a fresh read once the window has passed but the read in hand began before it.
    [Theory]
    [InlineData("PENDING", true)]
    [InlineData("REJECTED", true)]
    [InlineData(null, false)]
    public void NoAcceptance_ResolvesOnlyFromAReadAfterTheWindow(string? state, bool manage)
    {
        var attemptAt = Now.AddMinutes(-10);
        var from = WishBotInvitationPolicy.NegativeResolutionFrom(attemptAt, Window);
        var settings = Settings(requestedAt: attemptAt, attemptAt: attemptAt);
        var entry = state is null ? Array.Empty<(string, bool)>() : [(state, manage)];

        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(settings, Read(BotA, from.AddSeconds(-1), entry), now: from.AddSeconds(-1)));
        Assert.Equal(
            new WishBotInvitationDecision(WishBotInvitationAction.ReadAgain, ReadStartedAfterUtc: from),
            Decide(settings, Read(BotA, from.AddSeconds(-1), entry), now: from.AddSeconds(59)));
        Assert.Equal(
            new WishBotInvitationDecision(WishBotInvitationAction.ResolveNotAccepted),
            Decide(settings, Read(BotA, from, entry), now: from));
    }

    // K6b-1: a retry stamp later than the attempt marks "ended in Rejected", which books "not accepted"; one before it is
    // an older wait.
    [Theory]
    [InlineData(15, true)]
    [InlineData(-1, false)]
    public void ARetryStampAfterTheAttempt_EndsAsNotAccepted(int retryMinutesAfterAttempt, bool endsAsNotAccepted)
    {
        var attemptAt = Now.AddMinutes(-10);
        var settings = Settings(enabled: true, attemptAt: attemptAt, retryAt: attemptAt.AddMinutes(retryMinutesAfterAttempt));

        var decision = Decide(settings, Read(BotA, Now, ("PENDING", true)));

        Assert.Equal(WishBotInvitationAction.ResolveNotAccepted, decision.Action);
        Assert.Equal(endsAsNotAccepted, decision.EndsAsNotAccepted);
    }

    // Plan 2.9 (Sol-P2-6) for the attempt's Rejected: a read that began before the request (made anew while the read ran)
    // resolves the attempt alone and does not end the request; one that began at or after its stamp ends it.
    [Fact]
    public void ARejectedAttempt_EndsOnlyARequestTheReadMayDecide()
    {
        var attemptAt = Now.AddMinutes(-10);
        var requestedAt = Now.AddSeconds(5);
        var settings = Settings(requestedAt: requestedAt, attemptAt: attemptAt, retryAt: attemptAt.AddMinutes(15));

        Assert.Equal(
            new WishBotInvitationDecision(WishBotInvitationAction.ResolveNotAccepted),
            Decide(settings, Read(BotA, Now, ("PENDING", true)), now: Now.AddSeconds(10)));
        Assert.Equal(
            new WishBotInvitationDecision(WishBotInvitationAction.ResolveNotAccepted, EndsAsNotAccepted: true),
            Decide(settings, Read(BotA, requestedAt, ("PENDING", true)), now: Now.AddSeconds(10)));
    }

    // Only a read for the bound id resolves; an unavailable read leaves the attempt open whatever its age, and an open
    // attempt decides nothing else (no acceptance, no confirmation, no end of the request).
    [Fact]
    public void AnOpenAttempt_OnlyAReadForTheBoundIdResolves_AndBlocksEverythingElse()
    {
        var settings = Settings(requestedAt: Now.AddHours(-1), attemptAt: Now.AddHours(-1));

        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(settings, Read(BotB, Now, ("ACCEPTED", true)), account: new(true, BotB, Identity + 1)));
        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(settings, BotAccountEditorRead.Unavailable(BotA, Identity, Now)));
        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(settings, read: null));
    }

    // ------------------------------------------------------------------------------------ case (a): a request

    [Fact]
    public void ARequest_WaitsWhileTheAccountIsUnavailable() =>
        Assert.Equal(
            WishBotInvitationDecision.Nothing,
            Decide(Settings(requestedAt: Now), Read(BotA, Now, ("ACCEPTED", true)), account: new(false, BotA, Identity)));

    // S6-2: bound to another account, the request ends at once, whatever a read for the current one says.
    [Fact]
    public void ARequestBoundToAnotherAccount_EndsBotAccountChanged()
    {
        var decision = Decide(Settings(requestedAt: Now, consent: BotA), Read(BotB, Now, ("ACCEPTED", true)), account: new(true, BotB, Identity + 1));

        Assert.Equal(new WishBotInvitationDecision(WishBotInvitationAction.EndRequest, Error: WishBotChannelReasons.BotAccountChanged), decision);
    }

    [Fact]
    public void ARequest_IsConfirmedByEditor_AndAcceptsAnInvitationWithItsOwner()
    {
        Assert.Equal(WishBotInvitationAction.Confirm, Decide(Settings(requestedAt: Now), Read(BotA, Now, ("ACCEPTED", true))).Action);
        Assert.Equal(
            new WishBotInvitationDecision(WishBotInvitationAction.Accept, OwnerSevenTvUserId: Owner),
            Decide(Settings(requestedAt: Now), Read(BotA, Now, ("PENDING", true))));
    }

    // Not covered: nothing (the request waits); a read of another identity: nothing (plan 2.3).
    [Fact]
    public void ARequest_NotCoveredOrReadForAnotherIdentity_DecidesNothing()
    {
        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(Settings(requestedAt: Now), Read(BotA, Now, ("ACCEPTED", true)), covered: false));
        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(Settings(requestedAt: Now), Read(BotA, Now, Identity - 1, ("ACCEPTED", true))));
    }

    // S6-6: a wait not reached holds the acceptance back; reached, it accepts.
    [Fact]
    public void TheRetryStamp_HoldsTheAcceptanceBackUntilReached()
    {
        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(Settings(requestedAt: Now, retryAt: Now.AddSeconds(1)), Read(BotA, Now, ("PENDING", true))));
        Assert.Equal(WishBotInvitationAction.Accept, Decide(Settings(requestedAt: Now, retryAt: Now), Read(BotA, Now, ("PENDING", true))).Action);
    }

    // Plan 2.9 (Sol-P2-6): a read that began before the request (cached, or from a pass before) decides nothing about it,
    // neither a confirmation nor an acceptance nor not_editor; it asks for a fresh read that begins after the stamp, or
    // waits while the clock has not reached the stamp yet.
    [Theory]
    [InlineData("ACCEPTED", true)]
    [InlineData("PENDING", true)]
    [InlineData("REJECTED", true)]
    public void ARequest_IsDecidedOnlyByAReadThatBeganAtOrAfterIt(string state, bool manage)
    {
        var settings = Settings(requestedAt: Now);
        var early = Read(BotA, Now.AddTicks(-1), (state, manage));

        Assert.Equal(new WishBotInvitationDecision(WishBotInvitationAction.ReadAgain, ReadStartedAfterUtc: Now), Decide(settings, early, window: TimeSpan.Zero));
        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(settings, early, now: Now.AddTicks(-1), window: TimeSpan.Zero));
        Assert.NotEqual(WishBotInvitationAction.ReadAgain, Decide(settings, Read(BotA, Now, (state, manage)), window: TimeSpan.Zero).Action);
    }

    // S6-3, two windows: a negative read that began a second before request + window decides nothing (also from a later
    // clock: then a fresh read), the first one from request + window on ends the request with not_editor. ACCEPTED without
    // "manage" and an invitation without it count as not_editor (no acceptance).
    [Theory]
    [InlineData(60, "REJECTED", true)]
    [InlineData(1800, "REJECTED", true)]
    [InlineData(300, "ACCEPTED", false)]
    [InlineData(300, "PENDING", false)]
    public void ANegativeRead_EndsTheRequestOnlyFromTheVisibilityWindowOn(int windowSeconds, string state, bool manage)
    {
        var window = TimeSpan.FromSeconds(windowSeconds);
        var requestedAt = Now;
        var visibleFrom = requestedAt + window;
        var settings = Settings(requestedAt: requestedAt);

        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(settings, Read(BotA, visibleFrom.AddSeconds(-1), (state, manage)), now: visibleFrom.AddSeconds(-1), window: window));
        Assert.Equal(
            new WishBotInvitationDecision(WishBotInvitationAction.ReadAgain, ReadStartedAfterUtc: visibleFrom),
            Decide(settings, Read(BotA, visibleFrom.AddSeconds(-1), (state, manage)), now: visibleFrom.AddSeconds(59), window: window));
        Assert.Equal(
            new WishBotInvitationDecision(WishBotInvitationAction.EndRequest, Error: WishBotChannelReasons.NotEditor),
            Decide(settings, Read(BotA, visibleFrom, (state, manage)), now: visibleFrom, window: window));
    }

    // ------------------------------------------------------------------------------------ case (b): an activated channel

    [Fact]
    public void AnActivatedChannel_AcceptsAnInvitation_OnlyCoveredAvailableBoundAndAfterTheWait()
    {
        var read = Read(BotA, Now, ("PENDING", true));

        Assert.Equal(new WishBotInvitationDecision(WishBotInvitationAction.Accept, OwnerSevenTvUserId: Owner), Decide(Settings(enabled: true), read));
        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(Settings(enabled: true), read, covered: false));
        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(Settings(enabled: true), read, account: new(false, BotA, Identity)));
        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(Settings(enabled: true, consent: BotB), read));
        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(Settings(enabled: true, retryAt: Now.AddMinutes(1)), read));
        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(Settings(enabled: true), Read(BotA, Now, ("ACCEPTED", true))));
    }

    // [F7]: an invitation without a request or an activation is never answered.
    [Fact]
    public void AnInvitationWithoutRequestOrActivation_IsNeverAnswered() =>
        Assert.Equal(WishBotInvitationDecision.Nothing, Decide(Settings(consent: null), Read(BotA, Now, ("PENDING", true))));

    // ------------------------------------------------------------------------------------ helpers

    private static WishBotInvitationDecision Decide(
        ChannelWishSettings settings,
        BotAccountEditorRead? read,
        BotAccountAvailabilityState? account = null,
        bool covered = true,
        DateTime? now = null,
        TimeSpan? window = null) =>
        WishBotInvitationPolicy.Decide(settings, Twitch, account ?? AccountA, covered, read, window ?? Window, now ?? Now);

    private static ChannelWishSettings Settings(
        bool enabled = false,
        DateTime? requestedAt = null,
        DateTime? attemptAt = null,
        DateTime? retryAt = null,
        string? consent = BotA) => new()
        {
            ChannelId = "c1",
            BotModeEnabled = enabled,
            BotModeActivatedByTwitchUserId = enabled ? "4242" : null,
            BotActivationRequestedAtUtc = requestedAt,
            BotActivationRequestedByTwitchUserId = requestedAt is null ? null : "4242",
            BotConsentSevenTvUserId = consent,
            BotInvitationAttemptAtUtc = attemptAt,
            BotInvitationRetryAtUtc = retryAt,
        };

    private static BotAccountEditorRead Read(string botId, DateTime startedAt, params (string State, bool Manage)[] entries) =>
        Read(botId, startedAt, Identity, entries);

    private static BotAccountEditorRead Read(string botId, DateTime startedAt, int identity, params (string State, bool Manage)[] entries) =>
        BotAccountEditorRead.Available(
            botId,
            identity,
            startedAt,
            entries.Length == 0
                ? new Dictionary<string, BotAccountEditorEntry>()
                : new Dictionary<string, BotAccountEditorEntry> { [Twitch] = BotAccountEditorEntry.Of(entries[0].State, entries[0].Manage, Owner) });
}
