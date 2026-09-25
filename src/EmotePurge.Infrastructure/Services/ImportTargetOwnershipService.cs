using System.Diagnostics;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// <see cref="IImportTargetOwnershipService"/> over the cached set lists (spec 2026-09-20, 6.7 step 4
/// as amended by section 32).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule.</b> A set is admissible when it appears in the set list of one of the checked
/// accounts — the actor, then every <c>editor_of</c> account — <b>and</b> its owner id is the 7TV id
/// of one of those accounts. Both halves come from the same list answers: each list carries its
/// account's 7TV id (<c>userByConnection.id</c>) and each set its owner's (<c>owner.id</c>), so the
/// semantics of E22 ("owner ∈ {actor} ∪ {editor_of}") hold exactly, including for a set 7TV lists
/// under an account that does not own it. Ids only, never names.
/// </para>
/// <para>
/// <b>Why lists and not a direct question.</b> The lists sit behind the full guard chain of 6.1
/// (cache, single-flight, breaker, budget) and are the ones the picker has just loaded, so the
/// ordinary report costs no upstream request at all. Only a set that is in no list, or listed
/// without an owner, is asked about directly — once, under the provider budget and its own breaker
/// operation.
/// </para>
/// <para>
/// <b>The grants take the guarded way (second review round).</b> Which accounts to check comes from
/// <see cref="IGuardedSevenTvEditorGrantsService"/>, not from the unguarded
/// <see cref="ISevenTvEditorService"/> the authorization path uses: a hit costs nothing either way,
/// but a miss is resolved behind the provider guards and a failure is held — without that, repeated
/// forged reports during a Redis or 7TV outage cost two unbudgeted requests each. A refused guard or
/// a held failure makes the grants unreadable, which is the partial outage below.
/// </para>
/// <para>
/// <b>Unknown is not forbidden.</b> When a list could not be read and the set was not found
/// admissible anywhere else, the answer is <see cref="SevenTvEmoteSetOwnershipStatus.Unavailable"/>:
/// the unreadable account may be the very one that owns the set.
/// </para>
/// </remarks>
public sealed class ImportTargetOwnershipService(
    ISevenTvEmoteSetListService emoteSetListService,
    IGuardedSevenTvEditorGrantsService grantsService,
    ISevenTvApiClient client,
    ForeignSevenTvBreakerPolicy breaker,
    ForeignEmoteSetProviderBudget budget,
    ILogger<ImportTargetOwnershipService> logger) : IImportTargetOwnershipService
{
    /// <summary>The same concurrency-slot wait the list and the preview use.</summary>
    private static readonly TimeSpan BudgetWaitTimeout = TimeSpan.FromSeconds(5);

    public async Task<SevenTvEmoteSetOwnershipCheckResult> CheckAsync(
        string actorTwitchUserId, string actorTwitchLogin, string emoteSetId, CancellationToken cancellationToken = default)
    {
        var evidence = new OwnershipEvidence(emoteSetId);

        var ownList = await emoteSetListService.ListByTwitchIdAsync(actorTwitchUserId, cancellationToken);
        if (evidence.Inspect(ownList, actorTwitchLogin, actorTwitchUserId) is { } ownMatch)
        {
            return ownMatch;
        }

        // An actor 7TV knows no account for edits nothing either: editor_of hangs off the same
        // account. Skipping the grants here also spares the uncached identity request the grants
        // lookup would otherwise repeat on every call for such an actor.
        if (ownList.Status != EmoteSetListStatus.NoSevenTvAccount
            && await InspectEditorAccountsAsync(evidence, actorTwitchUserId, cancellationToken) is { } grantMatch)
        {
            return grantMatch;
        }

        // Listed, but only ever under a foreign owner: no lookup needed to say no — unless an
        // unreadable list might have been the owner's.
        if (evidence.ListedOnlyUnderForeignOwners)
        {
            return evidence.AnyListUnreadable
                ? SevenTvEmoteSetOwnershipCheckResult.Unavailable()
                : SevenTvEmoteSetOwnershipCheckResult.Forbidden();
        }

        var lookup = await LookUpOwnerGuardedAsync(emoteSetId, cancellationToken);
        switch (lookup.Status)
        {
            case SevenTvEmoteSetOwnerLookupStatus.Ok:
                if (evidence.AccountOf(lookup.OwnerSevenTvUserId!) is { } owner)
                {
                    return SevenTvEmoteSetOwnershipCheckResult.Owner(lookup.OwnerSevenTvUserId!, owner.TwitchLogin, owner.TwitchUserId);
                }

                return evidence.AnyListUnreadable
                    ? SevenTvEmoteSetOwnershipCheckResult.Unavailable()
                    : SevenTvEmoteSetOwnershipCheckResult.Forbidden();
            case SevenTvEmoteSetOwnerLookupStatus.NotFound:
                return SevenTvEmoteSetOwnershipCheckResult.SetNotFound();
            case SevenTvEmoteSetOwnerLookupStatus.RateLimited:
            case SevenTvEmoteSetOwnerLookupStatus.Unavailable:
            case SevenTvEmoteSetOwnerLookupStatus.BudgetExhausted:
                return SevenTvEmoteSetOwnershipCheckResult.Unavailable();
            default:
                throw new UnreachableException($"Unexpected {nameof(SevenTvEmoteSetOwnerLookupStatus)} value: {lookup.Status}.");
        }
    }

    private async Task<SevenTvEmoteSetOwnershipCheckResult?> InspectEditorAccountsAsync(
        OwnershipEvidence evidence, string actorTwitchUserId, CancellationToken cancellationToken)
    {
        var grants = await grantsService.GetEditorGrantsAsync(actorTwitchUserId, cancellationToken);
        if (grants.Status == SevenTvLookupStatus.Unavailable)
        {
            evidence.MarkUnreadable();
            return null;
        }

        if (grants.Status != SevenTvLookupStatus.Ok)
        {
            // NoSevenTvAccount: a complete answer — the actor edits nothing.
            return null;
        }

        foreach (var entry in grants.Grants!.Entries)
        {
            if (string.Equals(entry.TwitchChannelId, actorTwitchUserId, StringComparison.Ordinal))
            {
                continue;
            }

            var list = await emoteSetListService.ListByTwitchIdAsync(entry.TwitchChannelId, cancellationToken);
            if (evidence.Inspect(list, entry.ChannelLogin, entry.TwitchChannelId) is { } match)
            {
                return match;
            }
        }

        // A set listed under account A but owned by account B can only be matched once B's own
        // list has contributed B's id — which may have been after A was inspected.
        return evidence.MatchAgainstAllKnownAccounts();
    }

    /// <summary>
    /// The fallback's guard chain, in the list service's order and with the same collaborators:
    /// breaker (own operation), concurrency slot, then the client, which charges the request permit
    /// itself. Exactly one outcome goes back to the breaker per allowed decision.
    /// </summary>
    private async Task<SevenTvEmoteSetOwnerLookupResult> LookUpOwnerGuardedAsync(
        string emoteSetId, CancellationToken cancellationToken)
    {
        var decision = breaker.TryAcquire(ForeignSevenTvBreakerOperations.EmoteSetOwner);
        if (!decision.Allowed)
        {
            logger.LogDebug(
                "Owner of 7TV emote set {SetId}: circuit breaker open, no upstream request.", emoteSetId);
            return SevenTvEmoteSetOwnerLookupResult.Failed(SevenTvEmoteSetOwnerLookupStatus.Unavailable);
        }

        var breakerResolved = false;
        IDisposable? slot = null;
        try
        {
            slot = await budget.TryAcquireConcurrencySlotAsync(BudgetWaitTimeout, cancellationToken);
            if (slot is null)
            {
                logger.LogWarning(
                    "Owner of 7TV emote set {SetId}: provider-wide budget unavailable after {TimeoutSeconds}s.",
                    emoteSetId, BudgetWaitTimeout.TotalSeconds);
                breaker.ReleaseProbeWithoutOutcome(ForeignSevenTvBreakerOperations.EmoteSetOwner, decision.Generation);
                breakerResolved = true;
                return SevenTvEmoteSetOwnerLookupResult.Failed(SevenTvEmoteSetOwnerLookupStatus.BudgetExhausted);
            }

            var upstream = await client.LookUpEmoteSetOwnerAsync(emoteSetId, cancellationToken);
            LogBreakerTransition(emoteSetId, upstream.Status, ApplyBreakerFeedback(upstream, decision.Generation));
            breakerResolved = true;
            return upstream;
        }
        finally
        {
            if (!breakerResolved)
            {
                // Backstop for an unexpected exception (cancellation included) — a plain failure,
                // never a rate limit: nothing here is something 7TV told us.
                breaker.RecordFailure(
                    ForeignSevenTvBreakerOperations.EmoteSetOwner,
                    ForeignSevenTvBreakerOutcome.OtherFailure,
                    null,
                    decision.Generation);
            }

            slot?.Dispose();
        }
    }

    /// <summary>
    /// <c>NotFound</c> needed a real 7TV answer, so it is health evidence like <c>Ok</c>;
    /// <c>BudgetExhausted</c> is our own throttle and releases the probe instead.
    /// </summary>
    private ForeignSevenTvBreakerTransition ApplyBreakerFeedback(SevenTvEmoteSetOwnerLookupResult upstream, long generation)
    {
        switch (upstream.Status)
        {
            case SevenTvEmoteSetOwnerLookupStatus.Ok:
            case SevenTvEmoteSetOwnerLookupStatus.NotFound:
                return breaker.RecordSuccess(ForeignSevenTvBreakerOperations.EmoteSetOwner, generation);
            case SevenTvEmoteSetOwnerLookupStatus.RateLimited:
                return breaker.RecordFailure(
                    ForeignSevenTvBreakerOperations.EmoteSetOwner,
                    ForeignSevenTvBreakerOutcome.RateLimited,
                    upstream.RetryAfter,
                    generation);
            case SevenTvEmoteSetOwnerLookupStatus.Unavailable:
                return breaker.RecordFailure(
                    ForeignSevenTvBreakerOperations.EmoteSetOwner,
                    ForeignSevenTvBreakerOutcome.OtherFailure,
                    null,
                    generation);
            case SevenTvEmoteSetOwnerLookupStatus.BudgetExhausted:
                breaker.ReleaseProbeWithoutOutcome(ForeignSevenTvBreakerOperations.EmoteSetOwner, generation);
                return ForeignSevenTvBreakerTransition.None;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(upstream), upstream.Status, "Unknown SevenTvEmoteSetOwnerLookupStatus.");
        }
    }

    private void LogBreakerTransition(
        string emoteSetId, SevenTvEmoteSetOwnerLookupStatus status, ForeignSevenTvBreakerTransition transition)
    {
        switch (transition)
        {
            case ForeignSevenTvBreakerTransition.Opened:
                logger.LogWarning(
                    "7TV circuit breaker opened for the emote-set owner lookup (triggered by set {SetId}, status {Status}).",
                    emoteSetId, status);
                break;
            case ForeignSevenTvBreakerTransition.Closed:
                logger.LogInformation("7TV circuit breaker closed again for the emote-set owner lookup.");
                break;
            case ForeignSevenTvBreakerTransition.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(transition), transition, "Unknown ForeignSevenTvBreakerTransition.");
        }
    }

    /// <summary>
    /// What the inspected lists have said about one set so far: which 7TV ids belong to checked
    /// accounts (with the Twitch login the paper trail records for each, and the Twitch id the
    /// owner's tracked channel is resolved by), under which owner ids the set was listed, and whether
    /// any list could not be read.
    /// </summary>
    private sealed class OwnershipEvidence(string emoteSetId)
    {
        private readonly Dictionary<string, CheckedAccount> _accountBySevenTvId = new(StringComparer.Ordinal);
        private readonly List<string> _listedOwnerIds = [];
        private bool _listedWithoutOwner;

        public bool AnyListUnreadable { get; private set; }

        /// <summary>The set turned up, every time with an owner id, and no such owner was a checked account.</summary>
        public bool ListedOnlyUnderForeignOwners => _listedOwnerIds.Count > 0 && !_listedWithoutOwner;

        public void MarkUnreadable() => AnyListUnreadable = true;

        public CheckedAccount? AccountOf(string sevenTvUserId) =>
            _accountBySevenTvId.GetValueOrDefault(sevenTvUserId);

        /// <summary>Records one account's list and answers as soon as the set is admissible.</summary>
        public SevenTvEmoteSetOwnershipCheckResult? Inspect(EmoteSetListResult result, string accountTwitchLogin, string accountTwitchUserId)
        {
            switch (result.Status)
            {
                case EmoteSetListStatus.Ok when !string.IsNullOrEmpty(result.List!.SevenTvUserId):
                    _accountBySevenTvId.TryAdd(result.List.SevenTvUserId, new CheckedAccount(accountTwitchLogin, accountTwitchUserId));
                    foreach (var set in result.List.Sets.Where(set => string.Equals(set.Id, emoteSetId, StringComparison.Ordinal)))
                    {
                        if (string.IsNullOrEmpty(set.OwnerSevenTvUserId))
                        {
                            _listedWithoutOwner = true;
                        }
                        else
                        {
                            _listedOwnerIds.Add(set.OwnerSevenTvUserId);
                        }
                    }

                    return MatchAgainstAllKnownAccounts();
                case EmoteSetListStatus.NoSevenTvAccount:
                    // An answer: this account owns nothing and cannot be anyone's owner.
                    return null;
                default:
                    // Unavailable, RateLimited, BudgetExhausted — and an Ok list that does not say
                    // whose it is, which cannot vouch for anything.
                    AnyListUnreadable = true;
                    return null;
            }
        }

        public SevenTvEmoteSetOwnershipCheckResult? MatchAgainstAllKnownAccounts()
        {
            // The shared rule (EmoteSetEditability, spec 5.8/AK 30): editable iff the owner id is
            // one of the readable accounts' ids — _accountBySevenTvId's keys are exactly that set.
            foreach (var ownerId in _listedOwnerIds)
            {
                if (EmoteSetEditability.IsEditable(ownerId, _accountBySevenTvId.Keys))
                {
                    var owner = _accountBySevenTvId[ownerId];
                    return SevenTvEmoteSetOwnershipCheckResult.Owner(ownerId, owner.TwitchLogin, owner.TwitchUserId);
                }
            }

            return null;
        }
    }

    /// <summary>One checked account's Twitch identity: the actor's own, or an <c>editor_of</c> grant's.</summary>
    private sealed record CheckedAccount(string TwitchLogin, string TwitchUserId);
}
