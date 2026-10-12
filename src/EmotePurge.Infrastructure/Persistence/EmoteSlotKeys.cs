using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// The authoritative slot identity (emote, 7TV date, alias) as the database enforces it, and the
/// recognition of its violation: two writers creating the same authoritative slot at the same time
/// (slot plan #372, spec §6.5).
/// </summary>
internal static class EmoteSlotKeys
{
    /// <summary>
    /// Name of the partial unique index on <c>(EmoteId, AddedAt, Name)</c> where
    /// <c>AddedAtFromRest</c>. A compile-time constant shared with <see cref="AppDbContext"/> so the
    /// model and the conflict detection cannot drift apart (the same string leaves the model snapshot
    /// unchanged).
    /// </summary>
    public const string AuthoritativeIndexName = "IX_EmoteSlots_EmoteId_AddedAt_Name_Authoritative";

    /// <summary>
    /// True only for a unique violation on <see cref="AuthoritativeIndexName"/> — the one conflict a
    /// concurrent slot writer can cause once the row locks of <see cref="RowLocks"/> are taken. Every
    /// other failure propagates unchanged, like the emote-key check in the sync.
    /// </summary>
    public static bool IsSlotKeyConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AuthoritativeIndexName,
        };
}
