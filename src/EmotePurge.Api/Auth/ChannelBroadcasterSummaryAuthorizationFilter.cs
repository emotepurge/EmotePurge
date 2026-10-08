namespace EmotePurge.Api.Auth;

/// <summary>
/// The data summary's audience (#245): the same people the purge would serve, decided without a
/// Twitch call. Same 400 → 401 → 404 as <see cref="ChannelBroadcasterAuthorizationFilter"/>; the 403
/// differs for a row without a stored id. The purge may let such a row through because its service
/// proves the login live against Helix; the summary's service proves nothing, so this filter admits
/// an id-less row only when its name is the caller's current login (<see cref="BroadcasterOwnership.CanPurge"/>,
/// the rule that already decides whether the purge button is shown). A stored id is compared ordinally.
/// </summary>
public class ChannelBroadcasterSummaryAuthorizationFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        ChannelBroadcasterAuthorizationFilter.AuthorizeAsync(context, next, BroadcasterOwnership.CanPurge);
}
