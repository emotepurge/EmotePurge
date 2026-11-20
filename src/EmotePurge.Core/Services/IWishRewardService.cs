namespace EmotePurge.Core.Services;

/// <summary>
/// The manager's side of the K6 chat capture (plan #486 2.4): learning a channel-point reward and keeping the learned
/// ones. Only the Api calls it; the worker's half is the capture service.
/// <para>
/// <b>Access and binding live here, not only in the filter.</b> Every method takes the <see cref="WishCaptureAccess"/>
/// of <see cref="IChannelAccessService.ResolveWishCaptureAccessAsync"/> and works on its <c>ChannelId</c>, never on a
/// name. A write locks the channel row and only then reads the row's <c>TwitchChannelId</c> again: none →
/// <see cref="WishRewardOutcome.ChannelUnbound"/>; one other than the confirmed id, or an access that was not
/// confirmed → <see cref="WishRewardOutcome.AccessDenied"/>. The read applies the same two rules to its one read of
/// the row and gives out no K6 data for an unbound row (<see cref="WishRewardsDto.ChannelBound"/> false).
/// </para>
/// <para>
/// <b>The learning row changes only through conditional statements</b> (Sol-R2-P2-5): no method reads the row,
/// decides in memory and writes by key. Each statement names the row it expects (<c>SessionId</c>, and for confirm
/// also <c>CandidateId</c> and "not expired"), and the number of rows it hit is the answer. A dialog behind a restart
/// therefore gets <see cref="WishRewardOutcome.LearningStale"/>, and a read that saw an expired session A never deletes
/// a session B started meanwhile. A row past its expiry counts as absent everywhere.
/// </para>
/// <para>
/// Every write is one transaction, publishes <c>wish.changed</c> after the commit and sends no command to the worker
/// (the worker decides each observation on the database). A refusal writes, audits and publishes nothing, except that
/// a confirm which finds an expired row deletes it.
/// </para>
/// </summary>
public interface IWishRewardService
{
    /// <summary>
    /// The read model of <c>GET …/wishes/rewards</c>; deletes an expired learning row in the same call (conditional on
    /// its session and on the expiry). <paramref name="callerTwitchUserId"/> and <paramref name="isGlobalAdmin"/> feed
    /// <see cref="WishRewardsDto.CanEdit"/>, the question of the settings write filter. Outcomes:
    /// <see cref="WishRewardOutcome.Success"/> with <see cref="WishRewardResult.Rewards"/>,
    /// <see cref="WishRewardOutcome.ChannelNotFound"/>, <see cref="WishRewardOutcome.AccessDenied"/>.
    /// </summary>
    Task<WishRewardResult> GetAsync(
        WishCaptureAccess access, string callerTwitchUserId, bool isGlobalAdmin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a learning session of <see cref="Entities.WishLimits.LearningWindow"/> with a fresh session id and
    /// answers it in <see cref="WishRewardResult.Learning"/>. An expired row is replaced; an unexpired one is
    /// <see cref="WishRewardOutcome.LearningActive"/> (decided by the insert's key conflict). A channel holding
    /// <see cref="Entities.WishLimits.MaxRewardsPerChannel"/> rewards is <see cref="WishRewardOutcome.RewardLimit"/>
    /// already here. Not audited.
    /// </summary>
    Task<WishRewardResult> StartLearningAsync(WishCaptureAccess access, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns the candidate the manager saw into a reward (<c>wish.rewardAdd</c>) and deletes the learning row in the
    /// same transaction. The delete names session, candidate and "not expired"; when it hits nothing, what lies there
    /// decides: no row, an expired one (then deleted) or one without a candidate →
    /// <see cref="WishRewardOutcome.NoCandidate"/>; another session or candidate →
    /// <see cref="WishRewardOutcome.LearningStale"/>. Further refusals: <see cref="WishRewardOutcome.RequestInvalid"/>
    /// (an id missing, a label empty after trimming, longer than <see cref="Entities.WishLimits.RewardLabelMaxLength"/>
    /// code points or with control characters), <see cref="WishRewardOutcome.DurationOutOfRange"/>,
    /// <see cref="WishRewardOutcome.AlreadyKnown"/>, <see cref="WishRewardOutcome.RewardLimit"/>. A missing duration
    /// takes the channel's default. The redemption the reward was learned with never becomes a wish.
    /// </summary>
    Task<WishRewardResult> ConfirmLearningAsync(
        WishCaptureAccess access, ConfirmWishRewardLearningRequest request, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the session <paramref name="sessionId"/> (delete conditional on it). Nothing lying, or only an expired
    /// row of another session, is success (idempotent); an unexpired row of another session is
    /// <see cref="WishRewardOutcome.LearningStale"/> and stays. A missing id is
    /// <see cref="WishRewardOutcome.RequestInvalid"/>. Not audited.
    /// </summary>
    Task<WishRewardResult> CancelLearningAsync(
        WishCaptureAccess access, Guid? sessionId, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the sent fields of the reward with row id <paramref name="rewardRowId"/> (<c>wish.rewardChange</c>
    /// with the names of the fields that changed; nothing changed writes nothing). Refusals:
    /// <see cref="WishRewardOutcome.RewardNotFound"/> (also for a reward of another channel),
    /// <see cref="WishRewardOutcome.RequestInvalid"/> (label), <see cref="WishRewardOutcome.DurationOutOfRange"/>.
    /// </summary>
    Task<WishRewardResult> UpdateRewardAsync(
        WishCaptureAccess access, long rewardRowId, UpdateWishRewardRequest request, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets the reward (<c>wish.rewardRemove</c>); its wishes stay and lose their <c>RewardId</c> through the
    /// foreign key's <c>SET NULL</c>. Refusal: <see cref="WishRewardOutcome.RewardNotFound"/>.
    /// </summary>
    Task<WishRewardResult> DeleteRewardAsync(
        WishCaptureAccess access, long rewardRowId, AuditActor actor, CancellationToken cancellationToken = default);
}

/// <summary>Every way a reward command or the read can end; the Api maps each value to a status and a code.</summary>
public enum WishRewardOutcome
{
    Success,

    /// <summary>The channel row is gone (merged or purged), also when it went while the command waited for its lock.</summary>
    ChannelNotFound,

    /// <summary>The row carries no <c>TwitchChannelId</c> (checked behind the channel lock): <c>wish_channel_unbound</c>.</summary>
    ChannelUnbound,

    /// <summary>
    /// The access was not confirmed on the id, or the row's <c>TwitchChannelId</c> is no longer the one it was
    /// confirmed for (403 in the form of the manager filter).
    /// </summary>
    AccessDenied,

    /// <summary>An unexpired learning row exists: <c>wish_learning_active</c>.</summary>
    LearningActive,

    /// <summary>The lying row carries another session or candidate: <c>wish_learning_stale</c>.</summary>
    LearningStale,

    /// <summary>No row, an expired one, or one without a candidate: <c>wish_reward_no_candidate</c>.</summary>
    NoCandidate,

    /// <summary>The candidate's reward is already learned in the channel: <c>wish_reward_already_known</c>.</summary>
    AlreadyKnown,

    /// <summary>The channel holds <see cref="Entities.WishLimits.MaxRewardsPerChannel"/> rewards: <c>wish_reward_limit</c>.</summary>
    RewardLimit,

    /// <summary>No reward with this row id in the channel: <c>wish_reward_not_found</c>.</summary>
    RewardNotFound,

    /// <summary>A required id is missing or the label is invalid: <c>invalid_request_body</c>.</summary>
    RequestInvalid,

    /// <summary>The default duration lies outside 1 to 365: <c>wish_duration_out_of_range</c>.</summary>
    DurationOutOfRange
}

/// <summary>
/// The result of a reward call: the outcome plus its payload — <see cref="Rewards"/> for the read,
/// <see cref="Learning"/> for a start, <see cref="Reward"/> for confirm and update.
/// </summary>
public sealed record WishRewardResult(
    WishRewardOutcome Outcome,
    WishRewardsDto? Rewards = null,
    WishRewardLearningDto? Learning = null,
    WishRewardDto? Reward = null)
{
    public bool IsSuccess => Outcome == WishRewardOutcome.Success;

    public static WishRewardResult Of(WishRewardOutcome outcome) => new(outcome);
}

/// <summary>
/// The read model of <c>GET …/wishes/rewards</c>. For an unbound row (<see cref="ChannelBound"/> false)
/// <see cref="Rewards"/> and <see cref="Gaps"/> are empty and <see cref="Learning"/> is null whatever the tables hold.
/// <see cref="Limit"/> is <see cref="Entities.WishLimits.MaxRewardsPerChannel"/>; <see cref="Gaps"/> are those that
/// end within <see cref="Entities.WishLimits.CaptureGapDisplayWindow"/>, newest first; <see cref="CanEdit"/> is the
/// question of the settings write filter; <see cref="CaptureAliveAtUtc"/> the capture's last life stamp, null while
/// it has never written one.
/// </summary>
public sealed record WishRewardsDto(
    bool ChannelBound,
    IReadOnlyList<WishRewardDto> Rewards,
    int Limit,
    WishRewardLearningDto? Learning,
    IReadOnlyList<WishCaptureGapDto> Gaps,
    bool CanEdit,
    int PendingExpiryDays,
    DateTime? CaptureAliveAtUtc);

/// <summary>A learned reward. <see cref="Id"/> is the row id (the route's <c>{rewardId}</c>); Twitch's tag value never leaves the Api.</summary>
public sealed record WishRewardDto(
    long Id,
    string Label,
    int DefaultDurationDays,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? LastRedemptionSeenAtUtc);

/// <summary>A running learning session; <see cref="Candidate"/> is null until the first redemption of an unknown reward.</summary>
public sealed record WishRewardLearningDto(
    Guid SessionId,
    DateTime StartedAtUtc,
    DateTime ExpiresAtUtc,
    WishRewardCandidateDto? Candidate);

/// <summary>The candidate the manager confirms: the opaque id it is confirmed by, the redemption's text and login.</summary>
public sealed record WishRewardCandidateDto(Guid CandidateId, string Text, string Login, DateTime SeenAtUtc);

/// <summary>A capture gap; <see cref="Reason"/> is one of <see cref="Entities.WishCaptureGapReason"/>.</summary>
public sealed record WishCaptureGapDto(DateTime FromUtc, DateTime ToUtc, string Reason);

/// <summary>
/// Body of <c>POST …/rewards/learning/confirm</c>: the session and candidate ids exactly as the dialog showed them,
/// the manager's label and an optional default duration (null = the channel's default).
/// </summary>
public sealed record ConfirmWishRewardLearningRequest(
    Guid? SessionId,
    Guid? CandidateId,
    string? Label,
    int? DefaultDurationDays);

/// <summary>Body of <c>PATCH …/rewards/{rewardId}</c>: a null field stays as it is.</summary>
public sealed record UpdateWishRewardRequest(string? Label, int? DefaultDurationDays, bool? IsActive);
