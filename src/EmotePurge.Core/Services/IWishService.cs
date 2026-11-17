namespace EmotePurge.Core.Services;

/// <summary>
/// The manager's side of wish emotes (#481): writes that create a wish or change the channel's wish
/// settings, and the reads of the list and of one wish. Every method takes the channel name as the
/// caller spelled it and normalizes it through <see cref="Entities.ChannelName.Normalize"/>.
/// <para>
/// Every write is one transaction that takes the channel row lock first (plan 2.3), checks, writes the
/// row and its audit entry together, and publishes <c>wish.changed</c> after the commit. A refusal
/// writes nothing, audits nothing and publishes nothing. The handler maps
/// <see cref="WishCommandResult.Outcome"/> to a status and an error code and decides nothing itself.
/// </para>
/// </summary>
public interface IWishService
{
    /// <summary>The channel's settings, or the defaults while no row exists; null when the channel is unknown.</summary>
    Task<WishSettingsDto?> GetSettingsAsync(string channelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the settings (audited as <c>wish.settings</c>). Refusals: <see cref="WishOutcome.SettingsOutOfRange"/>,
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
    /// <see cref="WishOutcome.ChannelNotFound"/>, <see cref="WishOutcome.EmoteAlreadyInSet"/> (with slot ids,
    /// checked before) and <see cref="WishOutcome.AlreadyRunning"/> (with the running wish's id).
    /// </summary>
    Task<WishCommandResult> CreateAsync(
        string channelName, CreateWishRequest request, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// "Take over from the set": links a slot that is already in the set, either to a new wish (<c>active</c>,
    /// source <c>adopted</c>) or, with <see cref="AdoptWishRequest.WishId"/>, to an existing one. Refusals:
    /// <see cref="WishOutcome.SlotNotFound"/>, <see cref="WishOutcome.SlotAlreadyLinked"/>,
    /// <see cref="WishOutcome.WishNotFound"/>, <see cref="WishOutcome.StateConflict"/>,
    /// <see cref="WishOutcome.AlreadyRunning"/>, <see cref="WishOutcome.StartsAtInvalid"/>,
    /// <see cref="WishOutcome.DurationOutOfRange"/>, <see cref="WishOutcome.ChannelNotFound"/>.
    /// </summary>
    Task<WishCommandResult> AdoptAsync(
        string channelName, AdoptWishRequest request, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>One page of the channel's wishes in a view; null when the channel is unknown.</summary>
    Task<PagedResult<WishDto>?> ListAsync(
        string channelName, WishListView view, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>One wish of the channel; null when the channel is unknown or the wish belongs to another channel.</summary>
    Task<WishDto?> GetAsync(string channelName, long wishId, CancellationToken cancellationToken = default);
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

    /// <summary>Another live wish (<c>active</c> or <c>presented</c>) holds the slot.</summary>
    SlotAlreadyLinked,

    /// <summary>The wish is not in a state the command accepts, or changed under it.</summary>
    StateConflict
}

/// <summary>
/// The result of a command: the outcome plus the payload that belongs to it. <see cref="Wish"/> or
/// <see cref="Settings"/> on success, <see cref="SlotIds"/> and <see cref="ConflictingWishId"/> on the
/// matching conflicts, <see cref="SetFull"/> as the hint of a manual entry.
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
/// </summary>
public sealed record WishDto(
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
    string? LinkOrigin,
    int ExtensionCount,
    bool Permanent,
    bool BotOwned,
    bool BotWritePending,
    DateTime? BotNextAttemptAtUtc,
    DateTime? RemovalQueuedAtUtc,
    string Mode);

/// <summary>The channel's wish settings; the defaults of <see cref="Entities.WishLimits"/> while no row exists.</summary>
public sealed record WishSettingsDto(
    int DefaultDurationDays,
    int ProbationLeadDays,
    decimal? ProtectionThresholdPerDay,
    bool BotModeEnabled,
    string? BotSuspendedReason);

/// <summary>Body of the manual entry; a missing duration takes the channel's default.</summary>
public sealed record CreateWishRequest(
    string SevenTvEmoteId,
    string? EmoteName,
    string? Alias,
    int? DurationDays,
    string? RequesterLogin);

/// <summary>Body of "take over from the set"; without a start date the slot's <c>AddedAt</c> counts.</summary>
public sealed record AdoptWishRequest(
    string SlotId,
    long? WishId,
    int? DurationDays,
    DateTime? StartsAtUtc);

/// <summary>Body of the settings write: all three values, a null threshold switches protection off.</summary>
public sealed record UpdateWishSettingsRequest(
    int DefaultDurationDays,
    int ProbationLeadDays,
    decimal? ProtectionThresholdPerDay);
