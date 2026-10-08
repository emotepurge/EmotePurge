using EmotePurge.Api.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// Pins the rate-limit policy each emotes-group route ends up carrying, read straight off the
/// running endpoint metadata rather than re-deriving it from <c>EmoteEndpoints.cs</c> by eye.
/// </summary>
/// <remarks>
/// Nothing in the repo tested this before (R7 in the #71 import plan): <c>sync-deleted</c> and
/// <c>sync-restored</c> override the group's <see cref="RateLimitPolicyNames.InteractiveRead"/>
/// policy with <see cref="RateLimitPolicyNames.Bookkeeping"/> — the last <c>RequireRateLimiting</c>
/// call on a route wins — and a silently dropped override would only surface in production, as a
/// bookkeeping call that 429s on a spent read budget. The two bookkeeping cases (sync-restored,
/// sync-imported) double as the proof that the matching approach below actually finds the right
/// endpoint metadata, before trusting it for the new route.
/// </remarks>
public class EmoteRoutePolicyTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public EmoteRoutePolicyTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("POST", "/api/channels/{channelName}/emotes/sync-restored", RateLimitPolicyNames.Bookkeeping)]
    [InlineData("POST", "/api/channels/{channelName}/emotes/sync-imported", RateLimitPolicyNames.Bookkeeping)]
    [InlineData("GET", "/api/channels/{channelName}/emotes", RateLimitPolicyNames.InteractiveRead)]
    // AK 46 (spec 2026-09-20, 6.10): the five K2 routes this task adds or extends, each on the policy
    // its group already carries — none of them needed a new policy.
    [InlineData("GET", "/api/channels/{channelName}/emote-sets", RateLimitPolicyNames.InteractiveRead)]
    [InlineData("GET", "/api/channels/{channelName}/emotes/set-warning", RateLimitPolicyNames.InteractiveRead)]
    // #220: a tracked channel's set preview has a bucket of its own, not the ForeignEmoteLookup one
    // its former route shared with the foreign-channel import.
    [InlineData("GET", "/api/channels/{channelName}/emote-sets/{emoteSetId}/emotes", RateLimitPolicyNames.TrackedEmoteSetPreview)]
    [InlineData("GET", "/api/channels/{channelName}/usage-stats/totals", RateLimitPolicyNames.InteractiveRead)]
    [InlineData("GET", "/api/seventv/channels/{channelName}/emotes", RateLimitPolicyNames.ForeignEmoteLookup)]
    // K3 (spec 6.3/6.10, T3.1): the source-set picker's list route, same policy as its /emotes sibling.
    [InlineData("GET", "/api/seventv/channels/{channelName}/emote-sets", RateLimitPolicyNames.ForeignEmoteLookup)]
    [InlineData("GET", "/api/seventv/me/emote-set-targets", RateLimitPolicyNames.ForeignEmoteLookup)]
    // The editable pre-check for one set (owner-hint design 3.4): a sibling in the same /me group, so
    // the same ForeignEmoteLookup permit per call as opening the picker — not Bookkeeping, because
    // this call runs before any 7TV mutation and pulls set-list requests of its own.
    [InlineData("GET", "/api/seventv/me/emote-set-targets/{emoteSetId}", RateLimitPolicyNames.ForeignEmoteLookup)]
    // The set-centric import (spec 6.7/6.10, T2.4): the 7TV mutation already happened by the time this
    // call runs, same reasoning as its channel-scoped sibling two lines up — Bookkeeping, not
    // ForeignEmoteLookup, so a spent read budget cannot drop the paper trail.
    [InlineData("POST", "/api/seventv/emote-sets/{emoteSetId}/sync-imported", RateLimitPolicyNames.Bookkeeping)]
    // The set-centric delete/restore reports (restore-per-set spec 5.1, AK 9): the same reasoning —
    // the mutation already happened, so a spent read budget must not cost the paper trail.
    [InlineData("POST", "/api/seventv/emote-sets/{emoteSetId}/sync-deleted", RateLimitPolicyNames.Bookkeeping)]
    [InlineData("POST", "/api/seventv/emote-sets/{emoteSetId}/sync-restored", RateLimitPolicyNames.Bookkeeping)]
    // K6 whole-branch review, Fable A (spec 6.10): a set-session's branch of this route reads the
    // set's live 7TV membership (paginated), same provider-budget shape as the ForeignEmoteLookup
    // routes above — its own group otherwise defaults to Bookkeeping (end/delete keep it, unaffected).
    [InlineData("POST", "/api/channels/{channelName}/vote-sessions", RateLimitPolicyNames.ForeignEmoteLookup)]
    // #201: tag reads are navigation, the five maintenance routes are bookkeeping — no new policy.
    [InlineData("GET", "/api/channels/{channelName}/tags", RateLimitPolicyNames.InteractiveRead)]
    [InlineData("GET", "/api/channels/{channelName}/tags/{tagId:long}/entries", RateLimitPolicyNames.InteractiveRead)]
    [InlineData("POST", "/api/channels/{channelName}/tags", RateLimitPolicyNames.Bookkeeping)]
    [InlineData("PATCH", "/api/channels/{channelName}/tags/{tagId:long}", RateLimitPolicyNames.Bookkeeping)]
    [InlineData("DELETE", "/api/channels/{channelName}/tags/{tagId:long}", RateLimitPolicyNames.Bookkeeping)]
    [InlineData("POST", "/api/channels/{channelName}/tags/{tagId:long}/entries", RateLimitPolicyNames.Bookkeeping)]
    [InlineData("POST", "/api/channels/{channelName}/tags/{tagId:long}/entries/remove", RateLimitPolicyNames.Bookkeeping)]
    // #201 T-C: the registration and the two reports of a tag run — bookkeeping about a run already begun.
    [InlineData("POST", "/api/channels/{channelName}/tags/{tagId:long}/operations", RateLimitPolicyNames.Bookkeeping)]
    [InlineData("POST", "/api/channels/{channelName}/tags/{tagId:long}/placements", RateLimitPolicyNames.Bookkeeping)]
    [InlineData("POST", "/api/channels/{channelName}/tags/{tagId:long}/placements/removed", RateLimitPolicyNames.Bookkeeping)]
    public void EmoteGroupRoute_CarriesTheExpectedRateLimitPolicy(string method, string routePattern, string expectedPolicy)
    {
        // Resolving from Services boots the host; the endpoints exist only afterwards (same as
        // LiveRouteStructureTests).
        var endpointDataSource = _factory.Services.GetRequiredService<EndpointDataSource>();

        var endpoint = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                // TrimEnd('/'): group.MapGet("") combines with the group prefix into a trailing
                // slash ("…/emotes/") that the sub-routes below it do not carry — cosmetic, and not
                // what this test is pinning.
                string.Equals(candidate.RoutePattern.RawText?.TrimEnd('/'), routePattern.TrimEnd('/'), StringComparison.Ordinal)
                && (candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));

        var policy = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();

        Assert.NotNull(policy);
        Assert.Equal(expectedPolicy, policy!.PolicyName);
    }
}
