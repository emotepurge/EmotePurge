namespace EmotePurge.Api.Validation;

/// <summary>
/// Stable, language-neutral error identifiers returned in API error bodies as
/// <c>{ errorCode = ApiErrorCodes.X }</c>. The frontend owns the actual display text (translated
/// per the active language) — the API never returns human-readable prose, so there is exactly one
/// place (the frontend's translation files) that needs updating per supported language, not two.
/// </summary>
internal static class ApiErrorCodes
{
    public const string InvalidChannelName = "invalid_channel_name";
    public const string InvalidChannelOrSessionId = "invalid_channel_or_session_id";
    public const string EmoteIdsEmpty = "emote_ids_empty";
    public const string EmoteIdsInvalid = "emote_ids_invalid";
    public const string InvalidDateFormat = "invalid_date_format";
    public const string FromAfterTo = "from_after_to";
    public const string RangeTooLarge = "range_too_large";
    public const string InvalidOAuthState = "invalid_oauth_state";
    public const string TwitchTokenExchangeFailed = "twitch_token_exchange_failed";
    public const string TwitchUserInfoUnavailable = "twitch_user_info_unavailable";
    public const string VoteSessionTitleEmpty = "vote_session_title_empty";
    public const string VoteSessionRolesEmpty = "vote_session_roles_empty";
    public const string VipsNotSupported = "vips_not_supported";
    public const string StartedAtInFuture = "started_at_in_future";
    public const string ChannelNotJoined = "channel_not_joined";
    public const string EmoteIdEmpty = "emote_id_empty";
    public const string InvalidVoteType = "invalid_vote_type";
    // sync-imported's SourceKind: a closed vocabulary ("channel"/"file"/"seventv-channel") the
    // frontend itself constructs, so an unrecognized value is a frontend bug to surface, not a case
    // to guess at. Also answers a body whose kind and source name contradict each other.
    public const string InvalidSourceKind = "invalid_source_kind";
    public const string ChannelNotFound = "channel_not_found";
    // 404 from the join endpoint, and distinct from ChannelNotFound above: that one means "we do not
    // track this channel", this one means Twitch itself knows no account under that login. Only a
    // definite Helix answer produces it — an unreachable Helix lets the join through unchanged.
    public const string ChannelNotOnTwitch = "channel_not_on_twitch";
    // 409 from the join endpoint: the configured active-channel cap (Channels:MaxActiveChannels) is
    // reached and the caller is not a global admin. Never returned for a join that would not itself
    // activate a channel — an already-active channel stays idempotent regardless of the cap.
    public const string ChannelCapacityReached = "channel_capacity_reached";
    // Four codes for GET /api/seventv/channels/{channelName}/emotes (foreign-channel-import spec,
    // section 5) — ChannelNotOnTwitch above covers the fifth state that row shares with the join
    // endpoint. All four carry a 503/404 body with no further detail: the caller cannot act on more
    // than "not now" or "nothing there".
    public const string ForeignChannelTwitchUnavailable = "foreign_channel_twitch_unavailable";
    public const string ForeignChannelNoSevenTvAccount = "foreign_channel_no_seventv_account";
    public const string ForeignChannelNoActiveEmoteSet = "foreign_channel_no_active_emote_set";
    // Covers both a generic 7TV outage and a confirmed 429 (HTTP 429, or HTTP 200 with
    // extensions.status: 429) — one code for both, because the caller cannot act on the two any
    // differently (spec section 5: "7TV nicht erreichbar / 429" is a single table row).
    public const string ForeignChannelSevenTvUnavailable = "foreign_channel_seventv_unavailable";
    // GET /api/seventv/leaderboard (7TV-leaderboard-as-import-source spec, section 5, E13): a
    // missing or unrecognized sortBy. The three failure states of ISevenTvLeaderboardService itself
    // (7TV unreachable, 429, our own window budget refused) deliberately reuse
    // ForeignChannelSevenTvUnavailable above rather than getting a code of their own — its text is
    // already source-neutral, and the caller cannot act on the three any differently.
    public const string InvalidLeaderboardSort = "invalid_leaderboard_sort";
    // 429 with a body, unlike the rate limiter's bare 429: the client can tell the two apart and
    // say "already running, try again shortly" instead of a generic throttling message.
    public const string ResyncCooldownActive = "resync_cooldown_active";
    public const string VoteSessionNotFound = "vote_session_not_found";
    public const string VoteSessionEnded = "vote_session_ended";
    public const string EmoteNotEligible = "emote_not_eligible";
    // The generic rate limiter's 429, distinct from ResyncCooldownActive above: that one names a
    // per-channel cooldown any moderator can trip, this one means the caller's own per-minute budget
    // is spent. Both carry retryAfterSeconds and a Retry-After header, so the frontend can say which
    // of the two happened instead of falling back to one message for every 429 in existence.
    public const string RateLimitExceeded = "rate_limit_exceeded";
    // 503 with a body: RedisLiveEventStream could not subscribe to the Redis channel at all — an
    // infrastructure failure, not the caller's fault (issue #42). Distinct from
    // LiveStreamQuotaExhausted below so a bare 503 no longer hides which of the two happened.
    public const string LiveStreamUnavailable = "live_stream_unavailable";
    // 429 with a body: either the process-wide or the per-login live-stream connection limit is
    // exhausted (RedisLiveEventStream.MaxSubscriptions / MaxPerSubscriber) — one code for both,
    // because a caller cannot act on the two any differently: no slot right now, try again shortly.
    // Carries retryAfterSeconds and a Retry-After header like ResyncCooldownActive/RateLimitExceeded
    // above, but — unlike those — the value is a fixed heuristic, not a measured remaining cooldown:
    // neither connection limit has a natural expiry to report.
    public const string LiveStreamQuotaExhausted = "live_stream_quota_exhausted";

    // Returned by the global exception handler — deliberately opaque, no exception detail.
    public const string UnexpectedError = "unexpected_error";
    public const string NoHealthData = "no_health_data";
    public const string HealthDataUnreadable = "health_data_unreadable";

    // 404 from GET /api/legal/{kind}/{language} (issue #247): the operator has not configured this
    // document (no ContentPath, or no German file for it — German is authoritative, see
    // ILegalContentService), or the route carried a kind/language outside the closed vocabulary
    // (imprint/privacy, de/en). One code for both, because a caller cannot act on the two any
    // differently — the availability endpoint is what tells the frontend which links to show at all,
    // so a caller only ever reaches this by an unconfigured deep link or a route typo.
    public const string LegalDocumentNotFound = "legal_document_not_found";
}
