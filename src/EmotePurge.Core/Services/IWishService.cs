namespace EmotePurge.Core.Services;

/// <summary>
/// The manager's side of wish emotes (#481): writes that create a wish or change the channel's wish
/// settings, and the reads of the list and of one wish. Every method takes the channel name as the
/// caller spelled it and normalizes it through <see cref="Entities.ChannelName.Normalize"/>.
/// <para>
/// Every write is one transaction that takes its row locks first (plan 2.3: the channel row before an insert or
/// a <c>SlotId</c> write; a command that only changes an existing wish takes just that wish's row), checks,
/// writes the row and its audit entry together, and publishes <c>wish.changed</c> after the commit. A refusal
/// writes nothing, audits nothing and publishes nothing. The handler maps
/// <see cref="WishCommandResult.Outcome"/> to a status and an error code and decides nothing itself.
/// </para>
/// </summary>
public interface IWishService
{
    /// <summary>The channel's settings, or the defaults while no row exists; null when the channel is unknown.</summary>
    Task<WishSettingsDto?> GetSettingsAsync(string channelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the settings (audited as <c>wish.settings</c>). With <see cref="UpdateWishSettingsRequest.KeepProtectionThreshold"/>
    /// the stored threshold stays as it is and no decision is superseded (plan #485 2.1a); otherwise the sent value,
    /// null switching protection off. Refusals: <see cref="WishOutcome.SettingsOutOfRange"/>,
    /// <see cref="WishOutcome.ChannelNotFound"/>.
    /// </summary>
    Task<WishCommandResult> UpdateSettingsAsync(
        string channelName, UpdateWishSettingsRequest request, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// The decision behind the settings write filter: may this caller change the settings? A channel in manual
    /// mode answers yes (the caller is a manager already); in bot mode only the broadcaster, the manager who
    /// activated the bot and a global admin. An unknown channel answers yes: the handler's 404 is the better answer.
    /// </summary>
    Task<bool> CanChangeSettingsAsync(
        string channelName, string callerTwitchUserId, bool isGlobalAdmin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Manual entry: creates a wish in <c>approved</c> (source <c>manual</c>). Refusals:
    /// <see cref="WishOutcome.EmoteIdInvalid"/>, <see cref="WishOutcome.DurationOutOfRange"/>,
    /// <see cref="WishOutcome.AliasInvalid"/>, <see cref="WishOutcome.RequesterInvalid"/>,
    /// <see cref="WishOutcome.ChannelNotFound"/>, <see cref="WishOutcome.EmoteAlreadyInSet"/> (with slot ids and the id
    /// of a running wish of the emote or null, checked before) and <see cref="WishOutcome.AlreadyRunning"/> (with the
    /// running wish's id).
    /// </summary>
    Task<WishCommandResult> CreateAsync(
        string channelName, CreateWishRequest request, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// "Take over from the set": links a slot that is already in the set, either to a new wish (<c>active</c>,
    /// source <c>adopted</c>) or, with <see cref="AdoptWishRequest.WishId"/>, to an existing one. Bound to the entry the
    /// manager picked (plan #485 2.1a): under the channel lock, right after loading the slot, a slot whose alias or
    /// <c>AddedAt</c> (as an instant at database precision, null only matching null) differs from
    /// <see cref="AdoptWishRequest.ExpectedAlias"/>/<see cref="AdoptWishRequest.ExpectedAddedAtUtc"/> is
    /// <see cref="WishOutcome.SlotNotFound"/>. Refusals: <see cref="WishOutcome.RequestInvalid"/> (no expected alias),
    /// <see cref="WishOutcome.SlotNotFound"/>, <see cref="WishOutcome.SlotAlreadyLinked"/> (with the holder's id),
    /// <see cref="WishOutcome.WishNotFound"/>, <see cref="WishOutcome.StateConflict"/>,
    /// <see cref="WishOutcome.AlreadyRunning"/>, <see cref="WishOutcome.StartsAtInvalid"/>,
    /// <see cref="WishOutcome.DurationOutOfRange"/>, <see cref="WishOutcome.ChannelNotFound"/>.
    /// </summary>
    Task<WishCommandResult> AdoptAsync(
        string channelName, AdoptWishRequest request, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// PATCH: changes alias and/or duration while the wish may still be edited (<c>approved</c>, <c>failed</c>;
    /// <c>pending</c> from K6 on). Audited as <c>wish.update</c>. Refusals: <see cref="WishOutcome.DurationOutOfRange"/>,
    /// <see cref="WishOutcome.AliasInvalid"/> and the common ones of the transition commands (see <see cref="ExtendAsync"/>).
    /// </summary>
    Task<WishCommandResult> UpdateAsync(
        string channelName, long wishId, UpdateWishRequest request, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends an <c>active</c> or <c>presented</c> wish by the channel's default duration (E12: from the current
    /// expiry, or from now when that has passed); a <c>presented</c> wish returns to <c>active</c>. Audited as
    /// <c>wish.extend</c> with the extension length as <c>durationDays</c>.
    /// <para>
    /// The transition commands (<see cref="UpdateAsync"/> to <see cref="RecheckAsync"/>) share their refusals:
    /// <see cref="WishOutcome.ChannelNotFound"/>, <see cref="WishOutcome.WishNotFound"/> (also for a wish of another
    /// channel) and <see cref="WishOutcome.StateConflict"/> (the state does not allow the command, also when it
    /// changed while the command waited for the row lock). A refusal writes, audits and publishes nothing; a
    /// success counts the decision (E25) and publishes <c>wish.changed</c> once.
    /// </para>
    /// </summary>
    Task<WishCommandResult> ExtendAsync(string channelName, long wishId, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary><c>active</c> or <c>presented</c> to <c>kept</c>; bot ownership ends. Audited as <c>wish.keep</c>.</summary>
    Task<WishCommandResult> KeepAsync(string channelName, long wishId, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>approved</c>, <c>failed</c>, and <c>presented</c> without a linked slot, to <c>closed</c>. Audited as
    /// <c>wish.close</c>.
    /// </summary>
    Task<WishCommandResult> CloseAsync(string channelName, long wishId, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary><c>failed</c> to <c>approved</c> with a fresh approval time. Audited as <c>wish.retry</c>.</summary>
    Task<WishCommandResult> RetryAsync(string channelName, long wishId, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>removed</c> or <c>removed_externally</c> back to <c>approved</c>, timed (duration or the channel default) or
    /// permanent; start, expiry and slot generation are cleared, <c>SlotId</c> and <c>FirstActivatedAtUtc</c> stay.
    /// Audited as <c>wish.readd</c>. Further refusals: <see cref="WishOutcome.ModeInvalid"/>,
    /// <see cref="WishOutcome.DurationOutOfRange"/>, <see cref="WishOutcome.AlreadyRunning"/> (another wish for the
    /// emote runs again, with its id).
    /// </summary>
    Task<WishCommandResult> ReAddAsync(
        string channelName, long wishId, ReAddWishRequest request, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// The browser engine reports a removal of the entry named in <paramref name="request"/> (plan #485 2.1a, the
    /// ten-row table, the first row that applies wins). Locks the channel row, then the wish row, and reads only then,
    /// so the report stands in line with every slot writer. A request without slot, generation or set, or with an
    /// unknown evidence, is <see cref="WishOutcome.RequestInvalid"/>; a set that is not the channel's active one, or a
    /// generation that is not the wish's, is <see cref="WishOutcome.StateConflict"/>; a wish already <c>removed</c>
    /// answers success without any write (a repeated report); a replacement visible in the slot inventory is a
    /// conflict; otherwise <c>active</c>/<c>presented</c> becomes <c>removed</c> (ended now) and
    /// <c>removed_externally</c> within the adoption window becomes <c>removed</c> keeping its <c>EndedAtUtc</c>.
    /// Audited as <c>wish.remove</c> with slot, generation and set. Further refusals as for the transition commands.
    /// </summary>
    Task<WishCommandResult> CompleteRemovalAsync(
        string channelName, long wishId, CompleteWishRemovalRequest request, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// "Check again" (<c>presented</c> with reason <c>unconfirmed</c>). Its only transition needs the bot, which K1
    /// does not have (plan finding 8): in K1 every state answers <see cref="WishOutcome.StateConflict"/>; K4 fills it.
    /// </summary>
    Task<WishCommandResult> RecheckAsync(string channelName, long wishId, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>One page of the channel's wishes in a view; null when the channel is unknown.</summary>
    Task<PagedResult<WishDto>?> ListAsync(
        string channelName, WishListView view, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// One wish of the channel with its usage (E4, E5, E16), evaluated now; null when the channel is unknown or the
    /// wish belongs to another channel. A wish without a start has no usage (<see cref="WishDetailDto.Usage"/> null).
    /// </summary>
    Task<WishDetailDto?> GetAsync(string channelName, long wishId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The entries "take over from the set" may pick (plan #485 2.1a): the channel's slots that
    /// <see cref="AdoptAsync"/> accepts, under the one predicate it and the slot ids of
    /// <see cref="WishOutcome.EmoteAlreadyInSet"/> use, each with the conflict <see cref="AdoptAsync"/> would decide
    /// on. Optionally narrowed to one 7TV emote id; unpaginated, ordered by alias (ordinal). Null when the
    /// channel is unknown; a channel without entries answers an empty list.
    /// </summary>
    Task<IReadOnlyList<WishAdoptableSlotDto>?> ListAdoptableSlotsAsync(
        string channelName, string? sevenTvEmoteId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishes <c>wish.changed</c> for a channel (plan 2.5): thin, no payload, after the commit that changed a
/// wish or the settings, one event per channel and commit. A failure is logged and swallowed; the change is
/// committed and clients converge on their next fetch. Wish writes and the reconcile share this one path.
/// </summary>
public interface IWishChangedPublisher
{
    Task PublishAsync(string channelName, CancellationToken cancellationToken = default);
}

/// <summary>The views of the wish list (plan finding 17).</summary>
public enum WishListView
{
    /// <summary>Needs action: <c>pending</c>, <c>failed</c>, <c>presented</c>, and <c>approved</c> while the channel runs manually.</summary>
    Open,

    /// <summary><c>active</c>, <c>unconfirmed</c> and an <c>approved</c> wish that needs no action (the bot's turn).</summary>
    Running,

    /// <summary>Every ended state.</summary>
    Ended
}

/// <summary>
/// Every way a wish command can end. The handler's mapping covers each value; a test enforces that
/// (plan 2.4). Later tasks add the outcomes of their own commands here.
/// </summary>
public enum WishOutcome
{
    Success,
    ChannelNotFound,
    WishNotFound,

    /// <summary>The 7TV emote id is empty, too long or not alphanumeric.</summary>
    EmoteIdInvalid,
    DurationOutOfRange,
    AliasInvalid,
    RequesterInvalid,
    SettingsOutOfRange,

    /// <summary>The start date of an adoption lies in the future or before the slot's <c>AddedAt</c>.</summary>
    StartsAtInvalid,

    /// <summary>The emote is already active in the set; <see cref="WishCommandResult.SlotIds"/> names the slots.</summary>
    EmoteAlreadyInSet,

    /// <summary>A wish for this emote is running; <see cref="WishCommandResult.ConflictingWishId"/> names it.</summary>
    AlreadyRunning,

    /// <summary>The slot does not exist, is not active, belongs to another channel or does not fit the wish.</summary>
    SlotNotFound,

    /// <summary>
    /// Another wish (<c>active</c>, <c>presented</c> or <c>kept</c>) holds the slot;
    /// <see cref="WishCommandResult.ConflictingWishId"/> names it.
    /// </summary>
    SlotAlreadyLinked,

    /// <summary>The wish is not in a state the command accepts, or changed under it.</summary>
    StateConflict,

    /// <summary>The <c>mode</c> of a re-add is neither <c>timed</c> nor <c>permanent</c>.</summary>
    ModeInvalid,

    /// <summary>
    /// A required field of the body is missing or one has a value outside its set: the report of a removal without
    /// slot, generation or set, or with an unknown evidence; an adoption without the expected alias.
    /// </summary>
    RequestInvalid
}

/// <summary>
/// The result of a command: the outcome plus the payload that belongs to it. <see cref="Wish"/> or
/// <see cref="Settings"/> on success, <see cref="SlotIds"/> and <see cref="ConflictingWishId"/> on the
/// matching conflicts (the running wish of <see cref="WishOutcome.AlreadyRunning"/> and
/// <see cref="WishOutcome.EmoteAlreadyInSet"/>, the holder of <see cref="WishOutcome.SlotAlreadyLinked"/>),
/// <see cref="SetFull"/> as the hint of a manual entry.
/// </summary>
public sealed record WishCommandResult(
    WishOutcome Outcome,
    WishDto? Wish = null,
    WishSettingsDto? Settings = null,
    IReadOnlyList<string>? SlotIds = null,
    long? ConflictingWishId = null,
    bool SetFull = false)
{
    public bool IsSuccess => Outcome == WishOutcome.Success;

    public static WishCommandResult Of(WishOutcome outcome) => new(outcome);
}

/// <summary>
/// The wish as the Api serves it. The requester's Twitch id is never part of it (spec 6.4); the bot and queue
/// fields exist for K4 and stay empty until then.
/// <para>
/// <see cref="SlotAddedAtUtc"/> is the slot generation the wish is bound to (<c>Wishes.SlotAddedAtUtc</c>), null
/// without a slot or while the generation is unknown; it is confirmed exactly when
/// <see cref="StartsAtAuthoritative"/> is true (plan #485 2.1a, held by a test over every way a generation is
/// bound). <see cref="ImageUrl"/> is the channel's stored image of the same 7TV emote, null when the channel never
/// had the emote in its set or the row carries no image.
/// </para>
/// </summary>
public record WishDto(
    long Id,
    string SevenTvEmoteId,
    string EmoteName,
    string Alias,
    string Source,
    string Status,
    string? StatusReason,
    string? RequesterLogin,
    int DurationDays,
    DateTime CreatedAtUtc,
    DateTime? ApprovedAtUtc,
    DateTime? StartsAtUtc,
    bool StartsAtAuthoritative,
    DateTime? ProbationStartsAtUtc,
    DateTime? ExpiresAtUtc,
    DateTime? EndedAtUtc,
    string? SlotId,
    DateTime? SlotAddedAtUtc,
    string? ImageUrl,
    string? LinkOrigin,
    int ExtensionCount,
    bool Permanent,
    bool BotOwned,
    bool BotWritePending,
    DateTime? BotNextAttemptAtUtc,
    DateTime? RemovalQueuedAtUtc,
    string Mode);

/// <summary>
/// The detail view: the <see cref="WishDto"/> plus the usage of its window. Serialized flat, the usage as one
/// extra property (spec K1 "Formen").
/// </summary>
public sealed record WishDetailDto : WishDto
{
    public WishDetailDto(WishDto wish, WishUsageDto? usage)
        : base(wish)
    {
        Usage = usage;
    }

    /// <summary>Null for a wish without a start: there is no window to measure.</summary>
    public WishUsageDto? Usage { get; init; }
}

/// <summary>
/// Usage over the window of E4: <see cref="Total"/> is the sum over all slots of the emote on the UTC days of the
/// window, <see cref="PerDayMean"/> that sum over the window length in fractional days. <see cref="Rank"/> of
/// <see cref="RankOf"/> places the total among the active slots of the channel (E16, ties share the better
/// rank); both are null while the emote has no active slot to rank (an ended wish). <see cref="CoverageGap"/>
/// is the E5 rule: counting did not cover the whole window. <see cref="Daily"/> is the same sum per UTC day,
/// from the window's first day to the evaluation day in order, days without use as 0; it sums to
/// <see cref="Total"/> and has at most eight entries (plan #485 2.1a).
/// </summary>
public sealed record WishUsageDto(
    DateTime WindowFromUtc,
    DateTime WindowToUtc,
    long Total,
    decimal PerDayMean,
    int? Rank,
    int? RankOf,
    bool CoverageGap,
    IReadOnlyList<WishUsageDayDto> Daily);

/// <summary>One UTC day of <see cref="WishUsageDto.Daily"/>.</summary>
public sealed record WishUsageDayDto(DateOnly Date, long UseCount);

/// <summary>
/// One entry of <see cref="IWishService.ListAdoptableSlotsAsync"/>. <see cref="AddedAtUtc"/> is the slot's
/// <c>AddedAt</c> (null = undated), <see cref="AddedAtAuthoritative"/> whether 7TV confirmed it. The conflict, as
/// <see cref="IWishService.AdoptAsync"/> would decide it: <see cref="HeldByWishId"/> is the wish that holds this
/// slot (<c>active</c>, <c>presented</c>, <c>kept</c>); <see cref="RunningWishId"/> a running wish of the same
/// emote that does not hold it; <see cref="RunningWishAdoptable"/> whether that wish accepts "adopt with its id"
/// (the same <c>WishTransitions</c> row adopt asks), false without one.
/// </summary>
public sealed record WishAdoptableSlotDto(
    string SlotId,
    string SevenTvEmoteId,
    string Alias,
    DateTime? AddedAtUtc,
    bool AddedAtAuthoritative,
    string? ImageUrl,
    long? HeldByWishId,
    long? RunningWishId,
    bool RunningWishAdoptable);

/// <summary>
/// The channel's wish settings; the defaults of <see cref="Entities.WishLimits"/> while no row exists.
/// <see cref="PendingExpiryDays"/> (K6) is how long a captured wish may stay <c>pending</c>.
/// </summary>
public sealed record WishSettingsDto(
    int DefaultDurationDays,
    int ProbationLeadDays,
    decimal? ProtectionThresholdPerDay,
    bool BotModeEnabled,
    string? BotSuspendedReason,
    int PendingExpiryDays = Entities.WishLimits.DefaultPendingExpiryDays);

/// <summary>Body of the manual entry; a missing duration takes the channel's default.</summary>
public sealed record CreateWishRequest(
    string SevenTvEmoteId,
    string? EmoteName,
    string? Alias,
    int? DurationDays,
    string? RequesterLogin);

/// <summary>
/// Body of "take over from the set"; without a start date the slot's <c>AddedAt</c> counts.
/// <see cref="ExpectedAlias"/> (required) and <see cref="ExpectedAddedAtUtc"/> are the alias and the
/// <c>addedAtUtc</c> of the entry the manager picked from <c>GET …/wishes/slots</c>, returned verbatim; a null
/// <see cref="ExpectedAddedAtUtc"/> means an undated entry was picked (plan #485 2.1a).
/// </summary>
public sealed record AdoptWishRequest(
    string SlotId,
    string? ExpectedAlias,
    DateTime? ExpectedAddedAtUtc,
    long? WishId,
    int? DurationDays,
    DateTime? StartsAtUtc);

/// <summary>
/// Body of <c>complete-removal</c> (plan #485 2.1a): what the client's run removed. <see cref="SlotId"/> and
/// <see cref="SlotAddedAtUtc"/> are the wish's slot and generation verbatim from the DTO, <see cref="EmoteSetId"/>
/// the set the run wrote against; all three are required. <see cref="Evidence"/> is optional and goes into the
/// audit entry only: one of <see cref="WishRemovalEvidence"/>.
/// </summary>
public sealed record CompleteWishRemovalRequest(
    string? SlotId,
    DateTime? SlotAddedAtUtc,
    string? EmoteSetId,
    string? Evidence = null);

/// <summary>The values of <see cref="CompleteWishRemovalRequest.Evidence"/>, as on the wire.</summary>
public static class WishRemovalEvidence
{
    /// <summary>7TV acknowledged the REMOVE.</summary>
    public const string Acknowledged = "acknowledged";

    /// <summary>Only the re-read found the entry missing (plan #485 R20).</summary>
    public const string Absent = "absent";
}

/// <summary>Body of the PATCH: a null field stays as it is.</summary>
public sealed record UpdateWishRequest(string? Alias, int? DurationDays);

/// <summary>
/// Body of the re-add: <see cref="Mode"/> is one of <see cref="WishReAddMode"/>; a timed re-add without a
/// duration takes the channel's default, a permanent one keeps the wish's duration.
/// </summary>
public sealed record ReAddWishRequest(string? Mode, int? DurationDays);

/// <summary>The values of <see cref="ReAddWishRequest.Mode"/>, as on the wire.</summary>
public static class WishReAddMode
{
    public const string Timed = "timed";
    public const string Permanent = "permanent";
}

/// <summary>
/// Body of the settings write: a null threshold switches protection off. With
/// <see cref="KeepProtectionThreshold"/> (the threshold was not sent, plan #485 2.1a) the stored threshold stays and
/// <see cref="ProtectionThresholdPerDay"/> is ignored. A null <see cref="PendingExpiryDays"/> (not sent, K6 plan #486
/// 2.8) keeps the stored value; a value must lie in 1 to 90.
/// </summary>
public sealed record UpdateWishSettingsRequest(
    int DefaultDurationDays,
    int ProbationLeadDays,
    decimal? ProtectionThresholdPerDay,
    bool KeepProtectionThreshold = false,
    int? PendingExpiryDays = null);
