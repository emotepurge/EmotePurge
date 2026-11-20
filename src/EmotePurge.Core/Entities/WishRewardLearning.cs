namespace EmotePurge.Core.Entities;

/// <summary>
/// A running "learn a reward" session (K6): at most one per channel (key = <see cref="ChannelId"/>), valid
/// for <see cref="WishLimits.LearningWindow"/>. The first redemption of an unknown reward becomes the
/// candidate; the manager confirms or cancels it. Past <see cref="ExpiresAtUtc"/> the row counts as absent
/// everywhere. The candidate is the only place text of an unlearned reward is ever stored.
/// </summary>
public class WishRewardLearning
{
    public string ChannelId { get; set; } = string.Empty;
    public Channel? Channel { get; set; }

    /// <summary>Drawn at the start; confirm and cancel name it, so a dialog behind a restart gets a conflict.</summary>
    public Guid SessionId { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }

    // The candidate: either all five fields are set or none (CHECK CK_WishRewardLearnings_Candidate).

    /// <summary>Drawn with the candidate; confirm names it, so it confirms exactly the candidate the manager saw.</summary>
    public Guid? CandidateId { get; set; }

    public string? CandidateRewardId { get; set; }

    /// <summary>The redemption's text, possibly empty, never null while a candidate is set.</summary>
    public string? CandidateText { get; set; }

    public string? CandidateLogin { get; set; }
    public DateTime? CandidateSeenAtUtc { get; set; }
}
