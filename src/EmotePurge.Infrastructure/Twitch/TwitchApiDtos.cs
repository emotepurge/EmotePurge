namespace EmotePurge.Infrastructure.Twitch;

// POST id.twitch.tv/oauth2/token (form-urlencoded request, JSON response)
internal sealed class TwitchTokenResponseDto
{
    public string AccessToken { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public string? RefreshToken { get; set; }
    public List<string>? Scope { get; set; }
}

// GET api.twitch.tv/helix/users
internal sealed class TwitchGetUsersResponseDto
{
    public List<TwitchUserDto> Data { get; set; } = [];
}

internal sealed class TwitchUserDto
{
    public string Id { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    // Twitch sends "profile_image_url"; the SnakeCase naming policy in TwitchJsonOptions maps it.
    public string ProfileImageUrl { get; set; } = string.Empty;
}

// GET api.twitch.tv/helix/users, batched id/login identity lookup — only id/login are needed
// here, unlike TwitchGetUsersResponseDto/TwitchUserDto above which back the self-lookup and also
// carry display name/avatar.
internal sealed class TwitchGetUserIdentitiesResponseDto
{
    public List<TwitchUserIdentityDto> Data { get; set; } = [];
}

internal sealed class TwitchUserIdentityDto
{
    public string Id { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
}

// GET api.twitch.tv/helix/moderation/channels
internal sealed class TwitchGetModeratedChannelsResponseDto
{
    public List<TwitchModeratedChannelDto> Data { get; set; } = [];
    public TwitchPaginationDto? Pagination { get; set; }
}

internal sealed class TwitchModeratedChannelDto
{
    public string BroadcasterLogin { get; set; } = string.Empty;
    public string BroadcasterId { get; set; } = string.Empty;
}

internal sealed class TwitchPaginationDto
{
    public string? Cursor { get; set; }
}

// GET api.twitch.tv/helix/subscriptions/user
internal sealed class TwitchGetUserSubscriptionResponseDto
{
    public List<TwitchUserSubscriptionDto> Data { get; set; } = [];
}

internal sealed class TwitchUserSubscriptionDto
{
    public string BroadcasterId { get; set; } = string.Empty;
}

// GET api.twitch.tv/helix/streams
internal sealed class TwitchGetStreamsResponseDto
{
    public List<TwitchStreamDto> Data { get; set; } = [];
}

internal sealed class TwitchStreamDto
{
    public string UserId { get; set; } = string.Empty;
    public string UserLogin { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
}
