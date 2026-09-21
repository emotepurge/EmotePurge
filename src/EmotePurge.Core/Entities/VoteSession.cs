namespace EmotePurge.Core.Entities;

[Flags]
public enum AllowedRoles
{
    Everyone = 1,
    Subs = 2,
    VIPs = 4,
    Mods = 8,
    Broadcaster = 16
}

public class VoteSession
{
    public long Id { get; set; }
    public string ChannelId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public AllowedRoles AllowedVoterRoles { get; set; } = AllowedRoles.Everyone;
    public bool IsActive { get; set; } = true;
    // Opt-in secret ballot: while the session is active, non-managers get no tallies at all (see
    // IVoteSessionQueryService.GetResultsAsync). Fixed at creation, like the ballot itself.
    public bool HideResultsUntilEnd { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }

    // The 7TV emote set this session's ballot is scoped to. Null means today's behavior: a
    // channel-wide ("null") session whose ballot is either dynamic (all non-archived channel
    // emotes) or a fixed set of local Emote guids (spec section 9). Non-null marks a set-session,
    // whose SessionEmotes carry 7TV emote ids instead and freeze NameAtCreation/ImageUrlAtCreation.
    public string? EmoteSetId { get; set; }

    public Channel Channel { get; set; } = null!;
    public ICollection<Vote> Votes { get; set; } = new List<Vote>();
    // Empty = dynamic "all non-archived channel emotes"; non-empty = fixed explicit ballot.
    public ICollection<VoteSessionEmote> SessionEmotes { get; set; } = new List<VoteSessionEmote>();
}
