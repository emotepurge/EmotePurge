using System.Diagnostics;
using EmotePurge.Core.Entities;
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
/// (cache, single-flight, breaker, budget), and within their 60 s the picker's answers are reused
/// for free. That is not "no request at all": a report more than a minute after the picker reads
/// the lists again — one request when the actor owns the set, two in parallel with a valid owner
/// hint on a grant, up to <c>1 + k</c> serial ones without one. Only a set that is in no list, or
/// listed without an owner, is asked about directly by the report — once, under the provider
/// budget and its own breaker operation; the pre-check (<see cref="ResolveEditableAsync"/>) never
/// asks.
/// </para>
/// <para>
/// <b>The hint is an order, never a permission.</b> A hint resolves only against the actor and the
/// actor's grants, before any list is read; anything else is dropped. The actor's own list is always
/// read: with a hint on grant G, G's list is read in parallel with it, and G's evidence counts only
/// once the own list has not said <see cref="EmoteSetListStatus.NoSevenTvAccount"/> — the grant
/// cache outlives the actor's 7TV connection by up to ten minutes, and a stale positive grant must
/// never widen what is admissible.
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
        string actorTwitchUserId,
        string actorTwitchLogin,
        string emoteSetId,
        CancellationToken cancellationToken = default,
        EmoteSetOwnerHint? ownerHint = null)
    {
        var evidence = new OwnershipEvidence(emoteSetId);
        if (await WalkListsAsync(evidence, actorTwitchUserId, actorTwitchLogin, ownerHint, cancellationToken) is { } listMatch)
        {
            return listMatch;
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

    public async Task<SevenTvEmoteSetOwnershipCheckResult> ResolveEditableAsync(
        string actorTwitchUserId,
        string actorTwitchLogin,
        string emoteSetId,
        CancellationToken cancellationToken = default,
        EmoteSetOwnerHint? ownerHint = null)
    {
        var evidence = new OwnershipEvidence(emoteSetId);
        if (await WalkListsAsync(evidence, actorTwitchUserId, actorTwitchLogin, ownerHint, cancellationToken) is { } listMatch)
        {
            return listMatch;
        }

        // No owner lookup here (F16: never looser than the report's list rule): an unreadable
        // source may have been the owner's, a listing without an admissible owner id says no, and
        // a set no readable list knows is not one the actor can pick.
        if (evidence.AnyListUnreadable)
        {
            return SevenTvEmoteSetOwnershipCheckResult.Unavailable();
        }

        return evidence.ListedAnywhere
            ? SevenTvEmoteSetOwnershipCheckResult.Forbidden()
            : SevenTvEmoteSetOwnershipCheckResult.SetNotFound();
    }

    /// <summary>
    /// The list walk both modes share: the hint's resolution, the order of the reads, and the
    /// evidence they leave behind. Returns the admissible match, or <c>null</c> with
    /// <paramref name="evidence"/> filled for the caller's own verdict.
    /// </summary>
    private async Task<SevenTvEmoteSetOwnershipCheckResult?> WalkListsAsync(
        OwnershipEvidence evidence,
        string actorTwitchUserId,
        string actorTwitchLogin,
        EmoteSetOwnerHint? ownerHint,
        CancellationToken cancellationToken)
    {
        SevenTvEditorGrantsLookupResult? grants = null;
        if (NamesAnotherAccount(ownerHint, actorTwitchUserId, actorTwitchLogin))
        {
            // Only the grants can vouch for a hint on another account — and they are cached for ten
            // minutes, so this read is almost always free.
            grants = await grantsService.GetEditorGrantsAsync(actorTwitchUserId, cancellationToken);
            if (FindHintedGrant(ownerHint!, grants, actorTwitchUserId) is { } hintedGrant)
            {
                return await WalkFromHintedGrantAsync(
                    evidence, actorTwitchUserId, actorTwitchLogin, grants, hintedGrant, cancellationToken);
            }

            logger.LogDebug(
                "Owner hint for 7TV emote set {SetId} names neither the actor nor one of their editor grants; ignored.",
                evidence.EmoteSetId);
        }

        var ownList = await emoteSetListService.ListByTwitchIdAsync(actorTwitchUserId, cancellationToken);
        if (evidence.Inspect(ownList, actorTwitchLogin, actorTwitchUserId) is { } ownMatch)
        {
            return ownMatch;
        }

        // An actor 7TV knows no account for edits nothing either: editor_of hangs off the same
        // account. Skipping the grants here also spares the uncached identity request the grants
        // lookup would otherwise repeat on every call for such an actor.
        if (ownList.Status == EmoteSetListStatus.NoSevenTvAccount)
        {
            return null;
        }

        grants ??= await grantsService.GetEditorGrantsAsync(actorTwitchUserId, cancellationToken);
        return await InspectEditorAccountsAsync(evidence, grants, actorTwitchUserId, null, cancellationToken);
    }

    /// <summary>
    /// The hinted order: the actor's own list and the hinted grant's list in one round trip, then
    /// the remaining grants serially as without a hint.
    /// </summary>
    private async Task<SevenTvEmoteSetOwnershipCheckResult?> WalkFromHintedGrantAsync(
        OwnershipEvidence evidence,
        string actorTwitchUserId,
        string actorTwitchLogin,
        SevenTvEditorGrantsLookupResult grants,
        SevenTvEditorGrantEntry hintedGrant,
        CancellationToken cancellationToken)
    {
        var ownListRead = emoteSetListService.ListByTwitchIdAsync(actorTwitchUserId, cancellationToken);
        var hintedListRead = emoteSetListService.ListByTwitchIdAsync(hintedGrant.TwitchChannelId, cancellationToken);
        await Task.WhenAll(ownListRead, hintedListRead);
        var ownList = await ownListRead;
        var hintedList = await hintedListRead;

        // The grants may be a stale positive from before the actor lost their 7TV account. The
        // own list is the fresher word: the hinted list's evidence is dropped unread, and the walk
        // ends exactly where it ends without a hint.
        if (ownList.Status == EmoteSetListStatus.NoSevenTvAccount)
        {
            return null;
        }

        if (evidence.Inspect(ownList, actorTwitchLogin, actorTwitchUserId) is { } ownMatch)
        {
            return ownMatch;
        }

        if (evidence.Inspect(hintedList, hintedGrant.ChannelLogin, hintedGrant.TwitchChannelId) is { } hintedMatch)
        {
            return hintedMatch;
        }

        return await InspectEditorAccountsAsync(
            evidence, grants, actorTwitchUserId, hintedGrant.TwitchChannelId, cancellationToken);
    }

    private async Task<SevenTvEmoteSetOwnershipCheckResult?> InspectEditorAccountsAsync(
        OwnershipEvidence evidence,
        SevenTvEditorGrantsLookupResult grants,
        string actorTwitchUserId,
        string? alreadyReadTwitchUserId,
        CancellationToken cancellationToken)
    {
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
            if (string.Equals(entry.TwitchChannelId, actorTwitchUserId, StringComparison.Ordinal)
                || string.Equals(entry.TwitchChannelId, alreadyReadTwitchUserId, StringComparison.Ordinal))
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
    /// Whether the hint points past the actor — the only case that needs the grants to resolve it.
    /// A hint on the actor, or no usable hint at all, walks the lists exactly as without one.
    /// </summary>
    private static bool NamesAnotherAccount(EmoteSetOwnerHint? hint, string actorTwitchUserId, string actorTwitchLogin)
    {
        if (hint is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(hint.TwitchUserId))
        {
            return !string.Equals(hint.TwitchUserId, actorTwitchUserId, StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(hint.TwitchLogin)
            && !string.Equals(
                ChannelName.Normalize(hint.TwitchLogin), ChannelName.Normalize(actorTwitchLogin), StringComparison.Ordinal);
    }

    /// <summary>
    /// The grant a hint names — by Twitch id if it carries one, otherwise by normalised login — or
    /// <c>null</c>, which drops the hint. A grant on the actor's own id is never a hinted grant: that
    /// account is the actor, whose list is read anyway.
    /// </summary>
    private static SevenTvEditorGrantEntry? FindHintedGrant(
        EmoteSetOwnerHint hint, SevenTvEditorGrantsLookupResult grants, string actorTwitchUserId)
    {
        if (grants.Status != SevenTvLookupStatus.Ok)
        {
            return null;
        }

        var candidates = grants.Grants!.Entries
            .Where(entry => !string.Equals(entry.TwitchChannelId, actorTwitchUserId, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(hint.TwitchUserId))
        {
            return candidates.FirstOrDefault(
                entry => string.Equals(entry.TwitchChannelId, hint.TwitchUserId, StringComparison.Ordinal));
        }

        var login = ChannelName.Normalize(hint.TwitchLogin!);
        return candidates.FirstOrDefault(
            entry => string.Equals(ChannelName.Normalize(entry.ChannelLogin), login, StringComparison.Ordinal));
    }

    /// <summary>
    /// What the inspected lists have said about one set so far: which 7TV ids belong to checked
    /// accounts (with the Twitch login the paper trail records for each, the Twitch id the owner's
    /// tracked channel is resolved by, and what that account's own list said about the set), under
    /// which owner ids the set was listed, and whether any list could not be read.
    /// </summary>
    private sealed class OwnershipEvidence(string emoteSetId)
    {
        private readonly Dictionary<string, CheckedAccount> _accountBySevenTvId = new(StringComparer.Ordinal);
        private readonly List<EmoteSetSummary> _listingsWithOwner = [];
        private bool _listedWithoutOwner;

        public string EmoteSetId => emoteSetId;

        public bool AnyListUnreadable { get; private set; }

        /// <summary>The set turned up, every time with an owner id, and no such owner was a checked account.</summary>
        public bool ListedOnlyUnderForeignOwners => _listingsWithOwner.Count > 0 && !_listedWithoutOwner;

        /// <summary>The set turned up in at least one readable list, with or without an owner id.</summary>
        public bool ListedAnywhere => _listingsWithOwner.Count > 0 || _listedWithoutOwner;

        public void MarkUnreadable() => AnyListUnreadable = true;

        public CheckedAccount? AccountOf(string sevenTvUserId) =>
            _accountBySevenTvId.GetValueOrDefault(sevenTvUserId);

        /// <summary>Records one account's list and answers as soon as the set is admissible.</summary>
        public SevenTvEmoteSetOwnershipCheckResult? Inspect(EmoteSetListResult result, string accountTwitchLogin, string accountTwitchUserId)
        {
            switch (result.Status)
            {
                case EmoteSetListStatus.Ok when !string.IsNullOrEmpty(result.List!.SevenTvUserId):
                    EmoteSetSummary? ownListing = null;
                    foreach (var set in result.List.Sets.Where(set => string.Equals(set.Id, emoteSetId, StringComparison.Ordinal)))
                    {
                        ownListing ??= set;
                        if (string.IsNullOrEmpty(set.OwnerSevenTvUserId))
                        {
                            _listedWithoutOwner = true;
                        }
                        else
                        {
                            _listingsWithOwner.Add(set);
                        }
                    }

                    _accountBySevenTvId.TryAdd(
                        result.List.SevenTvUserId,
                        new CheckedAccount(accountTwitchLogin, accountTwitchUserId, ownListing, result.List.SevenTvActiveEmoteSetId));
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
            foreach (var listing in _listingsWithOwner)
            {
                var ownerId = listing.OwnerSevenTvUserId!;
                if (EmoteSetEditability.IsEditable(ownerId, _accountBySevenTvId.Keys))
                {
                    // Always the owner account's identity, never the listing account's; the active
                    // set only counts from the owner's own list (null if the set was not on it).
                    var owner = _accountBySevenTvId[ownerId];
                    return SevenTvEmoteSetOwnershipCheckResult.Owner(
                        ownerId,
                        owner.TwitchLogin,
                        owner.TwitchUserId,
                        owner.Listing ?? listing,
                        owner.Listing is null ? null : owner.SevenTvActiveEmoteSetId);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// One checked account's Twitch identity — the actor's own, or an <c>editor_of</c> grant's — plus
    /// what its own list said: the set as listed there (<c>null</c> if it was not), and its active set.
    /// </summary>
    private sealed record CheckedAccount(
        string TwitchLogin, string TwitchUserId, EmoteSetSummary? Listing, string? SevenTvActiveEmoteSetId);
}
