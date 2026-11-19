namespace EmotePurge.Core.Entities;

/// <summary>
/// The capture's single state row (K6; CHECK <c>Id = 1</c>). It appears with the first write; while it is
/// missing the capture has never been alive, so a start opens no restart gap.
/// </summary>
public class WishCaptureState
{
    /// <summary>The only allowed key.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>"Last surely captured": written only while the capture is effective and no connection gap is open.</summary>
    public DateTime LastAliveAtUtc { get; set; }
}
