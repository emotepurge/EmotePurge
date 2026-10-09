namespace EmotePurge.Core.Entities;

/// <summary>
/// One UTC day of a channel's successfully processed import coverage. Written only inside a committed
/// block transaction, so a cancelled or failed run can never claim a week it did not process, and
/// kept apart from the usage rows on purpose: a block without any matched usage (404, or chat without
/// set emotes) still counts as covered. The imported range shown to users derives from here, never
/// from <c>MIN(Date)</c> over usage rows. Survives the run row's retention deletion (<c>RunId</c> is
/// set to null) and falls with the channel.
/// </summary>
public class ChatLogBackfillCoverageDay
{
    public string ChannelId { get; set; } = string.Empty;

    public DateOnly Day { get; set; }

    // The set the run matched this day against.
    public string EmoteSetId { get; set; } = string.Empty;

    // Host of the run's ArchiveBaseUrl (e.g. logs.cyex.app), copied at commit: the attribution the
    // caption shows, independent of the current configuration.
    public string ArchiveHost { get; set; } = string.Empty;

    public long? RunId { get; set; }

    public DateTime CompletedAtUtc { get; set; }

    public Channel Channel { get; set; } = null!;
}
