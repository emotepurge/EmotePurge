namespace EmotePurge.Core.Entities;

/// <summary>
/// "Tag T counts as played in to set S". The row's existence is the state; deleting it means the tag
/// was cleared out of that set again.
/// </summary>
public class EmoteTagActivation
{
    public long TagId { get; set; }

    /// <summary>The 7TV emote set id (a 26-character ULID in practice; the column allows 32).</summary>
    public string SevenTvEmoteSetId { get; set; } = string.Empty;

    public DateTime ActivatedAtUtc { get; set; }

    /// <summary>The operation of the most recent play-in.</summary>
    public Guid OperationId { get; set; }

    public EmoteTag Tag { get; set; } = null!;
}
