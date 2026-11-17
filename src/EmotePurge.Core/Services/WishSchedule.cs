namespace EmotePurge.Core.Services;

/// <summary>A wish's start and expiry as one pair.</summary>
public readonly record struct WishTimeline(DateTime StartsAtUtc, DateTime ExpiresAtUtc);

/// <summary>The time rules of a wish (E2, E12, AK8), pure: every function takes its "now" as a parameter.</summary>
public static class WishSchedule
{
    /// <summary>Expiry from start and duration in whole days.</summary>
    public static DateTime ExpiresAt(DateTime startsAtUtc, int durationDays) => startsAtUtc.AddDays(durationDays);

    /// <summary>
    /// E2: the probation begins at the later of the start and (expiry minus lead). A duration shorter than the
    /// lead therefore yields the start.
    /// </summary>
    public static DateTime ProbationStartsAt(DateTime startsAtUtc, DateTime expiresAtUtc, int probationLeadDays)
    {
        var lead = expiresAtUtc.AddDays(-probationLeadDays);
        return lead > startsAtUtc ? lead : startsAtUtc;
    }

    /// <summary>E12: an extension counts from the current expiry, or from now when that has passed.</summary>
    public static DateTime ExtendedExpiresAt(DateTime currentExpiresAtUtc, DateTime nowUtc, int extensionDays)
    {
        var baseline = currentExpiresAtUtc > nowUtc ? currentExpiresAtUtc : nowUtc;
        return baseline.AddDays(extensionDays);
    }

    /// <summary>AK8: a corrected start moves start and expiry by the same difference.</summary>
    public static WishTimeline Shift(DateTime oldStartsAtUtc, DateTime oldExpiresAtUtc, DateTime newStartsAtUtc)
    {
        var delta = newStartsAtUtc - oldStartsAtUtc;
        return new WishTimeline(newStartsAtUtc, oldExpiresAtUtc + delta);
    }
}
