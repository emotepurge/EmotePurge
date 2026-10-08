namespace EmotePurge.Core.Entities;

// Membership row of a session's explicit ballot. A session with no rows covers all non-archived
// channel emotes dynamically (pre-subset behavior, also the "whole set" mode); a session with rows
// is a fixed ballot chosen at creation and never edited afterwards. EmoteId is the local Emote
// guid, not the 7TV id — same convention as Vote.EmoteId.
public class VoteSessionEmote
{
    public long VoteSessionId { get; set; }
    public string EmoteId { get; set; } = string.Empty;

    // Frozen at ballot creation, for set-sessions only (VoteSession.EmoteSetId != null). A
    // set-session's ballot can outlive the exact name/image 7TV reports today — and, for a foreign
    // channel (K3), can reference an emote with no local Emote row to fall back on at all. Null for
    // a null-session's ballot row, where the live Emote is always the answer. Enforced as a service
    // invariant (VoteSessionService), not a DB constraint — see spec section 4.1.
    public string? NameAtCreation { get; set; }
    public string? ImageUrlAtCreation { get; set; }

    public VoteSession VoteSession { get; set; } = null!;
    public Emote Emote { get; set; } = null!;
}
