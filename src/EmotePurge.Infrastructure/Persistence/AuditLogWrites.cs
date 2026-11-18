using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// The one way an audit entry gets written. It is an <see cref="AppDbContext"/> extension rather
/// than a service of its own precisely so it cannot be called with a *different* context than the
/// action it records: the entry is staged in the same change tracker and committed by the same
/// <c>SaveChangesAsync</c>, which is what makes "the action happened" and "the action is recorded"
/// a single atomic fact instead of two things that can drift apart when the second one fails.
/// </summary>
internal static class AuditLogWrites
{
    /// <summary>
    /// Stages an entry — it is not persisted until the caller saves. <paramref name="details"/> is
    /// serialized to the jsonb column; pass an anonymous object with camelCase members
    /// (e.g. <c>new { emoteCount = 12 }</c>), which System.Text.Json writes through unchanged.
    /// <paramref name="occurredAtUtc"/> defaults to the wall clock; a writer whose own reads depend on the stamp
    /// (the wish expiry run, which searches its earlier entries from the expiry on) passes its clock's time.
    /// </summary>
    public static void AddAuditEntry(
        this AppDbContext db,
        AuditActor actor,
        string action,
        string? channelName = null,
        string? targetType = null,
        string? targetId = null,
        object? details = null,
        DateTime? occurredAtUtc = null)
    {
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = occurredAtUtc ?? DateTime.UtcNow,
            ActorTwitchUserId = actor.TwitchUserId,
            ActorLogin = actor.Login,
            Action = action,
            ChannelName = channelName,
            TargetType = targetType,
            TargetId = targetId,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details)
        });
    }
}
