namespace EmotePurge.Core.Services;

/// <summary>
/// Since when a channel's usage data can be trusted. Extracted out of
/// <c>EmoteSetStatusService</c> (which uses it for <c>EmoteSetStatusDto.TrackedSince</c>) so the
/// rule lives in one place and can be applied without rebuilding it — there is deliberately only
/// one place this one-line decision is made.
/// </summary>
public static class TrackingCoverage
{
    /// <summary>
    /// The last join that reactivated the channel, or its creation if it was never left and
    /// rejoined. Older than this, nothing was being counted. Does not judge plausibility — a
    /// <paramref name="trackingResumedAt"/> earlier than <paramref name="createdAt"/> is a data
    /// question for the caller, not a case this function special-cases.
    /// </summary>
    public static DateTime TrackedSince(DateTime? trackingResumedAt, DateTime createdAt) =>
        trackingResumedAt ?? createdAt;
}
