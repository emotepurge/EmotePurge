using EmotePurge.Infrastructure.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="ForeignSevenTvBreakerPolicy"/> against E4/AK 8: the two triggers stay apart (a
/// confirmed 429 opens immediately, everything else needs five consecutive failures), the open
/// duration honors <c>Retry-After</c> over the 60 s default, exactly one probe travels once the open
/// duration elapses, and a straggler from an older generation can neither close the breaker nor
/// stretch its open window. Pure and clock-controlled — no container, same shape as
/// <c>TwitchReconnectBackoffPolicyTests</c>/<c>SevenTvBackoffPolicyTests</c> in
/// <c>EmotePurge.Worker.Tests</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every report goes through <see cref="Admit"/> first, exactly as production does: the policy hands
/// out the state a caller was admitted against, and the caller hands it back with the outcome. Tests
/// that reported an outcome without ever asking for permission would be describing a caller that
/// cannot exist.
/// </para>
/// <para>
/// Every case below names <see cref="Preview"/> as its operation, and that is the point: with
/// exactly one operation in play, the operation parameter added on 2026-09-20 (spec 6.1) must
/// change nothing at all. What it does change — a second operation's reach — is pinned separately
/// in <see cref="ForeignSevenTvBreakerPolicyOperationScopeTests"/>.
/// </para>
/// </remarks>
public class ForeignSevenTvBreakerPolicyTests
{
    private const string Preview = ForeignSevenTvBreakerOperations.ForeignPreview;

    [Fact]
    public void ClosedByDefault_AllowsEveryRequest()
    {
        var policy = new ForeignSevenTvBreakerPolicy(NewClock().Provider);

        var decision = policy.TryAcquire(Preview);

        Assert.True(decision.Allowed);
    }

    /// <summary>E4a: no threshold to accumulate — a single confirmed 429 opens the breaker outright.</summary>
    [Fact]
    public void ConfirmedRateLimit_OpensImmediately_WithoutFiveFailures()
    {
        var policy = new ForeignSevenTvBreakerPolicy(NewClock().Provider);

        var transition = policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.RateLimited, null, Admit(policy));

        Assert.Equal(ForeignSevenTvBreakerTransition.Opened, transition);
        var decision = policy.TryAcquire(Preview);
        Assert.False(decision.Allowed);
        Assert.True(decision.OpenedByRateLimit);
    }

    /// <summary>E4b: an ordinary failure needs the fifth consecutive occurrence, not the first.</summary>
    [Fact]
    public void OtherFailures_OnlyOpenAfterFiveConsecutive_NotBefore()
    {
        var policy = new ForeignSevenTvBreakerPolicy(NewClock().Provider);

        for (var i = 0; i < 4; i++)
        {
            var transition = policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.OtherFailure, null, Admit(policy));
            Assert.Equal(ForeignSevenTvBreakerTransition.None, transition);
            Assert.True(policy.TryAcquire(Preview).Allowed);
        }

        var fifth = policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.OtherFailure, null, Admit(policy));

        Assert.Equal(ForeignSevenTvBreakerTransition.Opened, fifth);
        var decision = policy.TryAcquire(Preview);
        Assert.False(decision.Allowed);
        Assert.False(decision.OpenedByRateLimit);
    }

    /// <summary>A success anywhere in the streak resets it — the fifth failure after one success is
    /// only the first of a new streak, not the fifth overall.</summary>
    [Fact]
    public void ASuccess_ResetsTheConsecutiveFailureStreak()
    {
        var policy = new ForeignSevenTvBreakerPolicy(NewClock().Provider);

        policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.OtherFailure, null, Admit(policy));
        policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.OtherFailure, null, Admit(policy));
        policy.RecordSuccess(Preview, Admit(policy));

        for (var i = 0; i < 4; i++)
        {
            var transition = policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.OtherFailure, null, Admit(policy));
            Assert.Equal(ForeignSevenTvBreakerTransition.None, transition);
        }

        Assert.True(policy.TryAcquire(Preview).Allowed);
    }

    [Fact]
    public void RetryAfter_OverridesTheSixtySecondDefault()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);

        policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), Admit(policy));

        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(policy.TryAcquire(Preview).Allowed);

        clock.Advance(TimeSpan.FromSeconds(2)); // total 11s > the 10s Retry-After
        Assert.True(policy.TryAcquire(Preview).Allowed);
    }

    [Fact]
    public void NoRetryAfter_FallsBackToTheSixtySecondDefault()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);

        policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.RateLimited, null, Admit(policy));

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(policy.TryAcquire(Preview).Allowed);

        clock.Advance(TimeSpan.FromSeconds(2)); // total 61s > the 60s default
        Assert.True(policy.TryAcquire(Preview).Allowed);
    }

    /// <summary>Once the open duration elapses, exactly one caller gets through as a probe; a
    /// concurrent second caller is still rejected until the probe reports back.</summary>
    [Fact]
    public void OnceOpenDurationElapses_ExactlyOneProbeIsAllowedThrough()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), Admit(policy));
        clock.Advance(TimeSpan.FromSeconds(11));

        var probe = policy.TryAcquire(Preview);
        var concurrentSecondCaller = policy.TryAcquire(Preview);

        Assert.True(probe.Allowed);
        Assert.False(concurrentSecondCaller.Allowed);
    }

    /// <summary>Schließen: a successful probe closes the breaker and clears the failure streak.</summary>
    [Fact]
    public void ASuccessfulProbe_ClosesTheBreaker_AndResetsTheStreak()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), Admit(policy));
        clock.Advance(TimeSpan.FromSeconds(11));
        var probe = policy.TryAcquire(Preview);
        Assert.True(probe.Allowed); // claims the probe

        var transition = policy.RecordSuccess(Preview, probe.Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.Closed, transition);
        Assert.True(policy.TryAcquire(Preview).Allowed);
        // The streak really did reset: four more ordinary failures alone must not reopen it.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(
                ForeignSevenTvBreakerTransition.None,
                policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.OtherFailure, null, Admit(policy)));
        }
    }

    /// <summary>
    /// Rückfall: a failed probe keeps the breaker open (with a freshly computed duration) rather than
    /// leaving it stuck half-open forever. The transition is reported as <c>None</c>, not
    /// <c>Opened</c> — the breaker was never visibly closed in between, so this is not the
    /// closed-to-open edge the "log once" contract cares about, only a continuation of the same
    /// open period under a new clock.
    /// </summary>
    [Fact]
    public void AFailedProbe_ReopensTheBreaker()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), Admit(policy));
        clock.Advance(TimeSpan.FromSeconds(11));
        var probe = policy.TryAcquire(Preview);
        Assert.True(probe.Allowed); // claims the probe

        var transition = policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.OtherFailure, null, probe.Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.None, transition);
        Assert.False(policy.TryAcquire(Preview).Allowed);

        // And the new open window uses the default duration again — no leftover Retry-After from the
        // first opening leaks into the second.
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(policy.TryAcquire(Preview).Allowed);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(policy.TryAcquire(Preview).Allowed);
    }

    /// <summary>
    /// The interleaving E4 is actually about: two lookups run side by side, the second gets a 429 and
    /// opens the breaker for the hour 7TV asked for, and only then does the first — admitted while the
    /// breaker was still closed, and knowing nothing about the incident — come back successful. Its
    /// success belongs to a state that no longer exists and must not reopen the gates: doing so would
    /// discard the Retry-After and put the feature straight back into a live lockout, which is exactly
    /// what E4 exists to prevent.
    /// </summary>
    [Fact]
    public void ALateSuccess_FromBeforeAnIntervening429_DoesNotCloseTheBreaker()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);

        var slowCaller = policy.TryAcquire(Preview);
        var fastCaller = policy.TryAcquire(Preview);
        Assert.True(slowCaller.Allowed);
        Assert.True(fastCaller.Allowed);

        // The fast caller finishes first, with a 429 carrying 7TV's ~1 h search-bucket reset.
        var opened = policy.RecordFailure(Preview,
            ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromHours(1), fastCaller.Generation);
        Assert.Equal(ForeignSevenTvBreakerTransition.Opened, opened);

        // …and only now does the slow caller succeed.
        var lateTransition = policy.RecordSuccess(Preview, slowCaller.Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.None, lateTransition);
        var afterwards = policy.TryAcquire(Preview);
        Assert.False(afterwards.Allowed);
        Assert.True(afterwards.OpenedByRateLimit);

        // The full Retry-After still stands — it was neither discarded nor shortened to the default.
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.False(policy.TryAcquire(Preview).Allowed);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(policy.TryAcquire(Preview).Allowed);
    }

    /// <summary>
    /// The same rule in the other direction: a straggler's *failure* from before the opening does not
    /// stretch the open window either. Both callers saw one incident; only the report that opened the
    /// breaker gets to say how long it lasts, or a burst of parallel failures would multiply one
    /// outage into an ever-receding one.
    /// </summary>
    [Fact]
    public void ALateFailure_FromAnOlderGeneration_DoesNotStretchTheOpenWindow()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);

        var slowCaller = policy.TryAcquire(Preview);
        var fastCaller = policy.TryAcquire(Preview);
        policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), fastCaller.Generation);

        clock.Advance(TimeSpan.FromSeconds(5));
        var lateTransition = policy.RecordFailure(Preview,
            ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromHours(1), slowCaller.Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.None, lateTransition);
        // Still the original 10 s window, not the straggler's hour.
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(policy.TryAcquire(Preview).Allowed);
    }

    /// <summary>
    /// A probe that never reached 7TV releases its slot, but a straggler calling the same method must
    /// not release a probe it does not own — otherwise two callers could be in flight as "the one
    /// probe".
    /// </summary>
    [Fact]
    public void ReleasingAProbe_FromAnOlderGeneration_DoesNotFreeTheCurrentProbeSlot()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        var straggler = policy.TryAcquire(Preview);
        policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), Admit(policy));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(policy.TryAcquire(Preview).Allowed); // the real probe claims the slot

        policy.ReleaseProbeWithoutOutcome(Preview, straggler.Generation);

        Assert.False(policy.TryAcquire(Preview).Allowed);
    }

    // The generation a caller would be admitted against right now — production reads it off the
    // decision that admitted the call, and so does every test here.
    private static long Admit(ForeignSevenTvBreakerPolicy policy) => policy.TryAcquire(Preview).Generation;

    private static FakeClock NewClock() => new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public TimeProvider Provider => this;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}

/// <summary>
/// The correction of 2026-09-20 (spec 6.1, AK 94): one policy instance serves more than one
/// operation, and the two signals it carries do not reach equally far. An ordinary failure streak
/// and the half-open probe belong to the operation that produced them — a malformed list query
/// (F17) is indistinguishable from a real outage and would otherwise drag the healthy preview into
/// 503 after five attempts, then burn the one probe the preview needed. A confirmed 429 is the
/// opposite: it describes 7TV's bucket, which both share, so it locks both with the same
/// <c>Retry-After</c>.
/// </summary>
public class ForeignSevenTvBreakerPolicyOperationScopeTests
{
    private const string Preview = ForeignSevenTvBreakerOperations.ForeignPreview;
    private const string List = ForeignSevenTvBreakerOperations.EmoteSetList;

    /// <summary>
    /// The failure counter is the list's own: five consecutive <c>Unavailable</c> of the list query
    /// open the list and nothing else, and the preview call that follows travels — it neither finds
    /// a closed gate nor resets anything of the list's by reporting its own success.
    /// </summary>
    [Fact]
    public void AnOperationsFailureStreak_LocksThatOperationAlone()
    {
        var policy = new ForeignSevenTvBreakerPolicy(NewClock().Provider);

        for (var i = 0; i < ForeignSevenTvBreakerPolicy.FailureThreshold; i++)
        {
            policy.RecordFailure(
                List, ForeignSevenTvBreakerOutcome.OtherFailure, null, policy.TryAcquire(List).Generation);
        }

        Assert.False(policy.TryAcquire(List).Allowed);

        var preview = policy.TryAcquire(Preview);
        Assert.True(preview.Allowed);

        // …and the preview's own success closes nothing of the list's: the list stays open for as
        // long as its own incident lasts.
        Assert.Equal(ForeignSevenTvBreakerTransition.None, policy.RecordSuccess(Preview, preview.Generation));
        Assert.False(policy.TryAcquire(List).Allowed);
    }

    /// <summary>
    /// The half-open probe is per operation too. Once the list's open duration elapses it may send
    /// exactly one probe — and a preview call arriving in the same moment travels on its own
    /// account, without consuming the slot the list is holding.
    /// </summary>
    [Fact]
    public void TheHalfOpenProbe_IsNotSharedBetweenOperations()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        for (var i = 0; i < ForeignSevenTvBreakerPolicy.FailureThreshold; i++)
        {
            policy.RecordFailure(
                List, ForeignSevenTvBreakerOutcome.OtherFailure, null, policy.TryAcquire(List).Generation);
        }

        clock.Advance(ForeignSevenTvBreakerPolicy.DefaultOpenDuration + TimeSpan.FromSeconds(1));

        Assert.True(policy.TryAcquire(List).Allowed); // the list claims its one probe
        Assert.False(policy.TryAcquire(List).Allowed);

        Assert.True(policy.TryAcquire(Preview).Allowed);

        // The list's probe is still the list's: the preview neither took it nor gave it back.
        Assert.False(policy.TryAcquire(List).Allowed);
    }

    /// <summary>
    /// The one signal that has to stay shared (6.1, "Reichweite"): 7TV limits the bucket, not the
    /// query, so a confirmed 429 on either path locks both — and tells both the same waiting time.
    /// This is precisely what a second, keyed policy instance would have got wrong.
    /// </summary>
    [Fact]
    public void AConfirmedRateLimit_LocksBothOperations_WithTheSameRetryAfter()
    {
        var policy = new ForeignSevenTvBreakerPolicy(NewClock().Provider);
        var retryAfter = TimeSpan.FromMinutes(30);

        var opened = policy.RecordFailure(
            List, ForeignSevenTvBreakerOutcome.RateLimited, retryAfter, policy.TryAcquire(List).Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.Opened, opened);

        var preview = policy.TryAcquire(Preview);
        Assert.False(preview.Allowed);
        Assert.True(preview.OpenedByRateLimit);
        Assert.Equal(retryAfter, preview.RemainingOpenTime);

        var list = policy.TryAcquire(List);
        Assert.False(list.Allowed);
        Assert.True(list.OpenedByRateLimit);
        Assert.Equal(preview.RemainingOpenTime, list.RemainingOpenTime);
    }

    /// <summary>
    /// The shared lock is shared, its probes are not: once 7TV's own waiting time has passed, each
    /// operation may try once. Sharing the probe here would mean the path that happened to ask
    /// second stays dark until the other one succeeds.
    /// </summary>
    [Fact]
    public void AfterASharedRateLimitElapses_EachOperationGetsItsOwnProbe()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        policy.RecordFailure(
            List,
            ForeignSevenTvBreakerOutcome.RateLimited,
            TimeSpan.FromSeconds(10),
            policy.TryAcquire(List).Generation);

        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.True(policy.TryAcquire(List).Allowed);
        Assert.False(policy.TryAcquire(List).Allowed);
        Assert.True(policy.TryAcquire(Preview).Allowed);
        Assert.False(policy.TryAcquire(Preview).Allowed);
    }

    private static FakeClock NewClock() => new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public TimeProvider Provider => this;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}

/// <summary>
/// The correction of 2026-09-21 (spec section 32, second opinion on K2): the breaker keeps two
/// epochs, not one. The provider epoch moves only when the provider-wide rate-limit lock opens or
/// closes; each operation's epoch moves only when that operation's own breaker opens or closes. A
/// report is checked against the epoch of the state it wants to change — so an operation-local
/// transition on one path can no longer discard the confirmed 429 another path brings back, and a
/// late success on one path still cannot clear the lock another path caught (the reason the single
/// generation existed in the first place).
/// </summary>
public class ForeignSevenTvBreakerPolicyEpochTests
{
    private const string Preview = ForeignSevenTvBreakerOperations.ForeignPreview;
    private const string List = ForeignSevenTvBreakerOperations.EmoteSetList;

    /// <summary>
    /// Invariant 1, the finding itself: a preview call is admitted, the list then opens its own
    /// breaker after five bad queries, and only afterwards does the preview come back with a
    /// confirmed 429. The list's local opening says nothing about 7TV's bucket, so the 429 must still
    /// lock the provider — with its own <c>Retry-After</c>, for both paths.
    /// </summary>
    [Fact]
    public void AnOperationLocalOpening_DoesNotDiscardAnotherOperationsConfirmedRateLimit()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        var retryAfter = TimeSpan.FromMinutes(30);

        var inFlightPreview = policy.TryAcquire(Preview);
        Assert.True(inFlightPreview.Allowed);

        OpenLocally(policy, List);
        Assert.False(policy.TryAcquire(List).Allowed);

        var transition = policy.RecordFailure(
            Preview, ForeignSevenTvBreakerOutcome.RateLimited, retryAfter, inFlightPreview.Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.Opened, transition);
        AssertLockedByRateLimit(policy, Preview, retryAfter);
        AssertLockedByRateLimit(policy, List, retryAfter);

        // The whole Retry-After stands, not the list's 60 s and not nothing.
        clock.Advance(retryAfter - TimeSpan.FromSeconds(1));
        Assert.False(policy.TryAcquire(Preview).Allowed);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(policy.TryAcquire(Preview).Allowed);
    }

    /// <summary>
    /// Invariant 1, the other direction of an operation-local transition: the list's probe closes
    /// the list's own breaker while a preview call is in flight. That closing is just as local, and
    /// the preview's 429 still locks the provider.
    /// </summary>
    [Fact]
    public void AnOperationLocalClosing_DoesNotDiscardAnotherOperationsConfirmedRateLimit()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        OpenLocally(policy, List);
        clock.Advance(ForeignSevenTvBreakerPolicy.DefaultOpenDuration + TimeSpan.FromSeconds(1));

        var inFlightPreview = policy.TryAcquire(Preview);
        var listProbe = policy.TryAcquire(List);
        Assert.True(inFlightPreview.Allowed);
        Assert.True(listProbe.Allowed);
        Assert.Equal(ForeignSevenTvBreakerTransition.Closed, policy.RecordSuccess(List, listProbe.Generation));

        var transition = policy.RecordFailure(
            Preview, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromMinutes(20), inFlightPreview.Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.Opened, transition);
        AssertLockedByRateLimit(policy, Preview, TimeSpan.FromMinutes(20));
        AssertLockedByRateLimit(policy, List, TimeSpan.FromMinutes(20));
    }

    /// <summary>
    /// Invariant 2, what the single generation was introduced for and must not come back: the
    /// preview is admitted while everything is closed, the list catches a 429 for an hour, and the
    /// preview's success arrives only afterwards. It proves nothing about the incident the list
    /// reported, so the lock stays — for both paths, for the full hour.
    /// </summary>
    [Fact]
    public void ALateSuccessOnOneOperation_DoesNotClearTheRateLimitAnotherOperationCaught()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);

        var slowPreview = policy.TryAcquire(Preview);
        var fastList = policy.TryAcquire(List);
        Assert.Equal(
            ForeignSevenTvBreakerTransition.Opened,
            policy.RecordFailure(List, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromHours(1), fastList.Generation));

        Assert.Equal(ForeignSevenTvBreakerTransition.None, policy.RecordSuccess(Preview, slowPreview.Generation));

        AssertLockedByRateLimit(policy, Preview, TimeSpan.FromHours(1));
        AssertLockedByRateLimit(policy, List, TimeSpan.FromHours(1));
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.False(policy.TryAcquire(Preview).Allowed);
        Assert.False(policy.TryAcquire(List).Allowed);
    }

    /// <summary>
    /// Invariant 2 where the two epochs part ways: the late preview's own operation epoch is still
    /// current (the preview never transitioned), only the provider epoch moved. The success may
    /// touch what belongs to the preview, but not the lock.
    /// </summary>
    [Fact]
    public void ALateSuccess_WithACurrentOperationEpoch_StillCannotClearAMovedProviderLock()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        var slowPreview = policy.TryAcquire(Preview);

        // The list opens locally first (its own epoch), then catches the 429 (the provider epoch).
        OpenLocally(policy, List);
        clock.Advance(ForeignSevenTvBreakerPolicy.DefaultOpenDuration + TimeSpan.FromSeconds(1));
        var listProbe = policy.TryAcquire(List);
        Assert.True(listProbe.Allowed);
        policy.RecordFailure(List, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromMinutes(10), listProbe.Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.None, policy.RecordSuccess(Preview, slowPreview.Generation));

        AssertLockedByRateLimit(policy, Preview, TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// Invariant 3, the failure streak: late reports on the preview — a success and an ordinary
    /// failure — neither reset nor extend the list's streak. Four list failures stay four, and the
    /// fifth is the one that opens.
    /// </summary>
    [Fact]
    public void LateReportsOnOneOperation_LeaveTheOtherOperationsStreakAlone()
    {
        var policy = new ForeignSevenTvBreakerPolicy(NewClock().Provider);
        var lateSuccess = policy.TryAcquire(Preview);
        var lateFailure = policy.TryAcquire(Preview);

        for (var i = 0; i < ForeignSevenTvBreakerPolicy.FailureThreshold - 1; i++)
        {
            policy.RecordFailure(List, ForeignSevenTvBreakerOutcome.OtherFailure, null, policy.TryAcquire(List).Generation);
        }

        policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.OtherFailure, null, lateFailure.Generation);
        Assert.True(policy.TryAcquire(List).Allowed); // the preview's failure did not count as the list's fifth
        policy.RecordSuccess(Preview, lateSuccess.Generation);

        var fifth = policy.RecordFailure(
            List, ForeignSevenTvBreakerOutcome.OtherFailure, null, policy.TryAcquire(List).Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.Opened, fifth); // …and its success did not reset the list's four
    }

    /// <summary>
    /// Invariant 3, the probe: while the list's probe is out, the preview opens its own breaker and
    /// a straggler of the preview reports late. Neither frees the list's slot for a second probe,
    /// and neither makes the list's own probe report stale — it still closes the list.
    /// </summary>
    [Fact]
    public void AnotherOperationsTransition_NeitherFreesNorInvalidatesAProbe()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        var previewStraggler = policy.TryAcquire(Preview);
        OpenLocally(policy, List);
        clock.Advance(ForeignSevenTvBreakerPolicy.DefaultOpenDuration + TimeSpan.FromSeconds(1));

        var listProbe = policy.TryAcquire(List);
        Assert.True(listProbe.Allowed);

        OpenLocally(policy, Preview);
        policy.RecordSuccess(Preview, previewStraggler.Generation);

        Assert.False(policy.TryAcquire(List).Allowed); // still exactly one probe out
        Assert.Equal(ForeignSevenTvBreakerTransition.Closed, policy.RecordSuccess(List, listProbe.Generation));
        Assert.True(policy.TryAcquire(List).Allowed);
        Assert.False(policy.TryAcquire(Preview).Allowed); // the preview's own incident is untouched
    }

    /// <summary>
    /// Invariant 4: the provider epoch overtakes an operation's probe. The list's probe is out when
    /// the preview's 429 locks the provider; the probe then fails late. Nothing may stay held — once
    /// every wait is over, both operations get a probe.
    /// </summary>
    [Fact]
    public void ProviderOpeningOvertakingAnOperationProbe_LeavesNoSlotHeld()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        OpenLocally(policy, List);
        clock.Advance(ForeignSevenTvBreakerPolicy.DefaultOpenDuration + TimeSpan.FromSeconds(1));
        var listProbe = policy.TryAcquire(List);
        Assert.True(listProbe.Allowed);

        policy.RecordFailure(
            Preview, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), policy.TryAcquire(Preview).Generation);
        policy.RecordFailure(List, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromHours(1), listProbe.Generation);

        // The list's late 429 belonged to a provider state that no longer exists: it neither
        // stretched the preview's ten seconds to an hour …
        clock.Advance(TimeSpan.FromSeconds(11));
        var previewProbe = policy.TryAcquire(Preview);
        Assert.True(previewProbe.Allowed);
        policy.RecordSuccess(Preview, previewProbe.Generation);

        // … nor kept the list's slot: its own late report handed it back.
        AssertEveryOperationCanProbeEventually(policy, clock);
    }

    /// <summary>
    /// Invariant 4: an operation epoch moves while another operation's provider probe is out. The
    /// preview's probe stays the one probe, and its success still closes the provider.
    /// </summary>
    [Fact]
    public void OperationOpeningDuringAProviderProbe_LeavesNoSlotHeld()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        policy.RecordFailure(
            List, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), policy.TryAcquire(List).Generation);
        clock.Advance(TimeSpan.FromSeconds(11));

        var previewProbe = policy.TryAcquire(Preview);
        var listProbe = policy.TryAcquire(List);
        Assert.True(previewProbe.Allowed);
        Assert.True(listProbe.Allowed);

        // The list's probe fails in an ordinary way: the list reopens locally, the provider stays.
        policy.RecordFailure(List, ForeignSevenTvBreakerOutcome.OtherFailure, null, listProbe.Generation);
        Assert.False(policy.TryAcquire(Preview).Allowed); // the preview's probe is still out

        Assert.Equal(ForeignSevenTvBreakerTransition.Closed, policy.RecordSuccess(Preview, previewProbe.Generation));
        Assert.True(policy.TryAcquire(Preview).Allowed);
        Assert.False(policy.TryAcquire(List).Allowed); // its own 60 s

        AssertEveryOperationCanProbeEventually(policy, clock);
    }

    /// <summary>
    /// Invariant 4: the provider closes while another operation's probe is out, and that probe then
    /// reports a 429 against the lock that no longer exists. Its report is stale for the provider,
    /// and its slot went back with the transition — traffic resumes on both paths.
    /// </summary>
    [Fact]
    public void ProviderClosingOvertakingAnotherProbe_LeavesNoSlotHeld()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        policy.RecordFailure(
            Preview, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), policy.TryAcquire(Preview).Generation);
        clock.Advance(TimeSpan.FromSeconds(11));
        var previewProbe = policy.TryAcquire(Preview);
        var listProbe = policy.TryAcquire(List);

        Assert.Equal(ForeignSevenTvBreakerTransition.Closed, policy.RecordSuccess(List, listProbe.Generation));
        Assert.Equal(
            ForeignSevenTvBreakerTransition.None,
            policy.RecordFailure(Preview, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromHours(1), previewProbe.Generation));

        Assert.True(policy.TryAcquire(Preview).Allowed);
        Assert.True(policy.TryAcquire(List).Allowed);
    }

    /// <summary>
    /// Invariant 4: a probe that never reached 7TV gives its slot back even after another
    /// operation's epoch has moved in the meantime.
    /// </summary>
    [Fact]
    public void ReleasingAProbe_AfterAnotherOperationsTransition_StillFreesTheSlot()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        OpenLocally(policy, List);
        clock.Advance(ForeignSevenTvBreakerPolicy.DefaultOpenDuration + TimeSpan.FromSeconds(1));
        var listProbe = policy.TryAcquire(List);
        Assert.True(listProbe.Allowed);

        OpenLocally(policy, Preview);
        policy.ReleaseProbeWithoutOutcome(List, listProbe.Generation);

        Assert.True(policy.TryAcquire(List).Allowed);
    }

    /// <summary>
    /// Invariant 4, for any sequence: random admissions, reports (in any order, late or not), probe
    /// releases and clock steps over both operations. Once every admitted call has reported — the
    /// contract every caller keeps — and every wait is over, each operation gets a call through.
    /// Seeded, so a failure names a reproducible sequence.
    /// </summary>
    [Fact]
    public void AfterAnySequenceOfTransitions_EveryOperationGetsAProbeOnceAllCallsReported()
    {
        string[] operations = [Preview, List];

        for (var seed = 0; seed < 2000; seed++)
        {
            var random = new Random(seed);
            var clock = NewClock();
            var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
            var inFlight = new List<(string Operation, long Generation)>();

            for (var step = 0; step < 60; step++)
            {
                switch (random.Next(4))
                {
                    case 0:
                    case 1:
                        var operation = operations[random.Next(operations.Length)];
                        var decision = policy.TryAcquire(operation);
                        if (decision.Allowed)
                        {
                            inFlight.Add((operation, decision.Generation));
                        }

                        break;
                    case 2 when inFlight.Count > 0:
                        var index = random.Next(inFlight.Count);
                        Report(policy, random, inFlight[index]);
                        inFlight.RemoveAt(index);
                        break;
                    default:
                        clock.Advance(TimeSpan.FromSeconds(random.Next(0, 90)));
                        break;
                }
            }

            while (inFlight.Count > 0)
            {
                var index = random.Next(inFlight.Count);
                Report(policy, random, inFlight[index]);
                inFlight.RemoveAt(index);
            }

            clock.Advance(TimeSpan.FromHours(2));
            foreach (var operation in operations)
            {
                Assert.True(policy.TryAcquire(operation).Allowed, $"seed {seed}: {operation} stayed locked");
            }
        }
    }

    /// <summary>
    /// Invariant 5 (AK 94), under the new epochs: far more than <c>FailureThreshold</c> list
    /// failures — including failed probes that reopen the list — never lock the preview; a confirmed
    /// 429 on the preview then locks both with the same <c>Retry-After</c>.
    /// </summary>
    [Fact]
    public void ManyListFailures_NeverLockThePreview_AndAPreview429LocksBoth()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);

        for (var round = 0; round < 3 * ForeignSevenTvBreakerPolicy.FailureThreshold; round++)
        {
            var list = policy.TryAcquire(List);
            if (list.Allowed)
            {
                policy.RecordFailure(List, ForeignSevenTvBreakerOutcome.OtherFailure, null, list.Generation);
            }
            else
            {
                clock.Advance(ForeignSevenTvBreakerPolicy.DefaultOpenDuration + TimeSpan.FromSeconds(1));
            }

            var preview = policy.TryAcquire(Preview);
            Assert.True(preview.Allowed);
            policy.RecordSuccess(Preview, preview.Generation);
        }

        var retryAfter = TimeSpan.FromMinutes(20);
        policy.RecordFailure(
            Preview, ForeignSevenTvBreakerOutcome.RateLimited, retryAfter, policy.TryAcquire(Preview).Generation);

        AssertLockedByRateLimit(policy, Preview, retryAfter);
        AssertLockedByRateLimit(policy, List, retryAfter);
    }

    private static void OpenLocally(ForeignSevenTvBreakerPolicy policy, string operation)
    {
        for (var i = 0; i < ForeignSevenTvBreakerPolicy.FailureThreshold; i++)
        {
            policy.RecordFailure(
                operation, ForeignSevenTvBreakerOutcome.OtherFailure, null, policy.TryAcquire(operation).Generation);
        }
    }

    private static void AssertLockedByRateLimit(ForeignSevenTvBreakerPolicy policy, string operation, TimeSpan remaining)
    {
        var decision = policy.TryAcquire(operation);
        Assert.False(decision.Allowed);
        Assert.True(decision.OpenedByRateLimit);
        Assert.Equal(remaining, decision.RemainingOpenTime);
    }

    private static void AssertEveryOperationCanProbeEventually(ForeignSevenTvBreakerPolicy policy, FakeClock clock)
    {
        clock.Advance(TimeSpan.FromHours(2));
        Assert.True(policy.TryAcquire(Preview).Allowed);
        Assert.True(policy.TryAcquire(List).Allowed);
    }

    private static void Report(ForeignSevenTvBreakerPolicy policy, Random random, (string Operation, long Generation) call)
    {
        switch (random.Next(4))
        {
            case 0:
                policy.RecordSuccess(call.Operation, call.Generation);
                break;
            case 1:
                policy.RecordFailure(
                    call.Operation,
                    ForeignSevenTvBreakerOutcome.RateLimited,
                    TimeSpan.FromSeconds(random.Next(1, 900)),
                    call.Generation);
                break;
            case 2:
                policy.RecordFailure(call.Operation, ForeignSevenTvBreakerOutcome.OtherFailure, null, call.Generation);
                break;
            default:
                policy.ReleaseProbeWithoutOutcome(call.Operation, call.Generation);
                break;
        }
    }

    private static FakeClock NewClock() => new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public TimeProvider Provider => this;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
