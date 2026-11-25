using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// The confirmation schedule E11 of a bot add (#487 plan 2.6 point 5, Sol-P2-7, R2-7), pure. Reads at 0 s, 30 s, 2 min,
/// 5 min and 15 min after the anchor (the write, <c>BotWriteAtUtc</c>; after <c>recheck</c> the recheck). The next read is
/// computed from the <b>planned</b> instant of the one just made, not from when it actually ran, and a failed read counts as
/// an attempt like one without a match. The deadline is the anchor plus <see cref="WishLimits.BotConfirmWindow"/> (16 min,
/// one minute of slack for the fifth read); from then on only the deadline step decides (<c>presented</c>,
/// <c>unconfirmed</c>), whether or not a read could run.
/// </summary>
public static class WishBotConfirmSchedule
{
    /// <summary>The planned reads after the anchor, in order (E11).</summary>
    public static readonly IReadOnlyList<TimeSpan> Offsets =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    ];

    /// <summary>The first read, at the anchor itself (the add's own answer stands in for it where it carries the entry).</summary>
    public static DateTime First(DateTime anchorUtc) => anchorUtc;

    /// <summary>The confirmation deadline of an attempt anchored at <paramref name="anchorUtc"/>.</summary>
    public static DateTime Deadline(DateTime anchorUtc) => anchorUtc + WishLimits.BotConfirmWindow;

    /// <summary>
    /// The planned instant of the read after read number <paramref name="attempt"/> (0-based, as stored in
    /// <c>BotAttemptCount</c> before it is raised), which was planned for <paramref name="plannedAtUtc"/>; null after the
    /// last read: then the deadline step ends the schedule.
    /// </summary>
    public static DateTime? Next(DateTime plannedAtUtc, int attempt)
    {
        if (attempt < 0 || attempt >= Offsets.Count - 1)
        {
            return null;
        }

        var anchor = plannedAtUtc - Offsets[attempt];
        return anchor + Offsets[attempt + 1];
    }

    /// <summary>
    /// When the open add of <paramref name="wish"/> is due for the deadline step (plan 2.5 step 0): the stored
    /// <c>BotConfirmDeadlineAtUtc</c> of an <c>unconfirmed</c> wish, or, for an intent that was never resolved
    /// (<c>approved</c> with <c>BotWriteAtUtc</c>, a crash remnant) and an <c>unconfirmed</c> wish without a stored
    /// deadline, the deadline of its write. Null without an open add.
    /// </summary>
    public static DateTime? DeadlineOf(Wish wish)
    {
        ArgumentNullException.ThrowIfNull(wish);
        if (!WishBotAttemptResolution.HasOpenAdd(wish))
        {
            return null;
        }

        if (string.Equals(wish.Status, WishStatus.Unconfirmed, StringComparison.Ordinal) && wish.BotConfirmDeadlineAtUtc is { } stored)
        {
            return stored;
        }

        return wish.BotWriteAtUtc is { } written ? Deadline(written) : null;
    }
}
