using System.Text.Json;

using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class AuditLogQueryService(AppDbContext db) : IAuditLogQueryService
{
    /// <summary>
    /// Cap for <see cref="AuditLogDetail.Text"/>. Vote-session titles are user input and are only
    /// bounded by their own validation; a log row is not the place to render an essay.
    /// </summary>
    private const int MaxDetailTextLength = 200;

    // Property names in emotes.syncImported's DetailsJson payload. Deliberately not in
    // AuditLogDetail.Kinds: "sourceKind" is only the discriminator that picks between the import
    // Kinds, never a Kind itself, "sourceChannelName" feeds the Text of a channel-shaped one, and
    // "leaderboardSort" feeds the Text of the leaderboard one (leaderboard-import spec E9).
    private const string SourceKindProperty = "sourceKind";
    private const string SourceChannelNameProperty = "sourceChannelName";
    private const string LeaderboardSortProperty = "leaderboardSort";

    // The import ladder's target (spec 6.7): written by MarkImportedAsync when a set was reported
    // (E5) and always by MarkImportedToSetAsync. Not part of AuditLogDetail.Kinds — they never pick
    // a Kind, they annotate whichever import Kind was already chosen above with AuditLogTargetEmoteSet.
    private const string TargetEmoteSetIdProperty = "targetEmoteSetId";
    private const string TargetIsActiveSetOfChannelProperty = "targetIsActiveSetOfChannel";
    private const string TargetOwnerTwitchLoginProperty = "targetOwnerTwitchLogin";

    // The closed vocabulary the endpoint accepts for that discriminator (EmoteEndpoints, F5.1/F1).
    // Both channel-shaped kinds render as ImportedFromChannel: what the row has to preserve is that
    // the emotes came from a channel and which one, not through which of the two read paths we saw
    // that channel. Kept as a named set so the connection to the endpoint's list is visible — an
    // unlisted word here costs the provenance of every row written with it, permanently, because
    // audit rows are write-once (F5.3/F1 Station 5).
    private const string ChannelSourceKind = "channel";
    private const string ForeignChannelSourceKind = "seventv-channel";
    private const string FileSourceKind = "file";
    private const string LeaderboardSourceKind = "seventv-leaderboard";

    public async Task<PagedResult<AuditLogEntryDto>> ListAsync(int page, int pageSize, AuditLogFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var query = ApplyFilter(db.AuditLogEntries.AsNoTracking(), filter);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(e => e.OccurredAtUtc)
            // Id descending as the tiebreaker, not decoration: entries written inside one transaction
            // share a timestamp to the tick, and without a total order Skip/Take may return the same
            // row on two pages and drop another entirely.
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new
            {
                e.Id,
                e.OccurredAtUtc,
                e.ActorLogin,
                e.Action,
                e.ChannelName,
                e.TargetType,
                e.TargetId,
                e.DetailsJson,
            })
            .ToListAsync(cancellationToken);

        // Projected after materializing, never inside the Select: ProjectDetail parses JSON, which
        // EF Core cannot translate — putting it in the query would throw at runtime, not at build.
        var items = rows
            .Select(r => new AuditLogEntryDto(
                r.Id,
                r.OccurredAtUtc,
                r.ActorLogin,
                r.Action,
                r.ChannelName,
                r.TargetType,
                r.TargetId,
                ProjectDetail(r.DetailsJson)))
            .ToList();

        return new PagedResult<AuditLogEntryDto>(items, page, pageSize, totalCount);
    }

    private static IQueryable<AuditLogEntry> ApplyFilter(IQueryable<AuditLogEntry> query, AuditLogFilter? filter)
    {
        if (filter is null)
        {
            return query;
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            query = query.Where(e => e.Action == filter.Action);
        }

        if (!string.IsNullOrWhiteSpace(filter.ChannelName))
        {
            // Exact match on the normalized form (Regel 9) — this is what the
            // (ChannelName, OccurredAtUtc) index serves, unlike a substring scan.
            var normalized = ChannelName.Normalize(filter.ChannelName);
            query = query.Where(e => e.ChannelName == normalized);
        }

        if (!string.IsNullOrWhiteSpace(filter.ActorLogin))
        {
            // Substring on purpose: logins are free text to the admin. ILIKE cannot use the
            // btree index, but the actor-filtered set is small enough that this is fine.
            var pattern = $"%{filter.ActorLogin.Trim()}%";
            query = query.Where(e => EF.Functions.ILike(e.ActorLogin, pattern));
        }

        return query;
    }

    /// <summary>
    /// Reduces a free-form <c>DetailsJson</c> payload to the closed set of
    /// <see cref="AuditLogDetail.Kinds"/>. Every step degrades to <c>null</c> rather than throwing:
    /// the column is written by ten different call sites and read by two endpoints, so one malformed
    /// row must cost its own detail line and nothing else.
    /// <para>
    /// The precedence between kinds is fixed, because a payload could carry more than one known key.
    /// <c>login</c> is recognized and deliberately dropped: user-scoped actions already carry the
    /// target's login in their details, and rendering it would produce "by sensitron · handofblood"
    /// with nothing saying which of the two names is the target. The import kind is checked first
    /// (<see cref="TryProjectImportDetail"/>) and that order is load-bearing: an emotes.syncImported
    /// payload carries emoteCount too, and if the bare EmoteCount kind matched first it would win the
    /// precedence and silently drop the one thing that row can't be reconstructed from otherwise —
    /// where the emotes came from (R1 in the #71 import plan).
    /// </para>
    /// </summary>
    private static AuditLogDetail? ProjectDetail(string? detailsJson)
    {
        if (!TryParseDetailsObject(detailsJson, out var root))
        {
            return null;
        }

        if (TryProjectImportDetail(root, out var importDetail))
        {
            return importDetail;
        }

        if (TryReadCount(root, AuditLogDetail.Kinds.EmoteCount, out var emoteCount))
        {
            return new AuditLogDetail(AuditLogDetail.Kinds.EmoteCount, emoteCount, null);
        }

        if (TryReadCount(root, AuditLogDetail.Kinds.RemovedEntries, out var removedEntries))
        {
            return new AuditLogDetail(AuditLogDetail.Kinds.RemovedEntries, removedEntries, null);
        }

        return TryProjectTitleDetail(root, out var titleDetail) ? titleDetail : null;
    }

    /// <summary>Parses a details payload into an object root, degrading to false on any malformed shape.</summary>
    private static bool TryParseDetailsObject(string? detailsJson, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        return root.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// The emotes.syncImported shapes (channel/file/leaderboard origin). Degrades to false — not a
    /// throw — when <c>sourceKind</c> is missing, unrecognized, or its companion fields don't check
    /// out; the caller then falls through to the plainer kinds below.
    /// </summary>
    private static bool TryProjectImportDetail(JsonElement root, out AuditLogDetail? detail)
    {
        detail = null;
        if (!root.TryGetProperty(SourceKindProperty, out var sourceKindElement)
            || sourceKindElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var sourceKind = sourceKindElement.GetString();
        if (!(TryProjectLeaderboardDetail(root, sourceKind, out detail)
            || TryProjectChannelOrFileDetail(root, sourceKind, out detail)))
        {
            return false;
        }

        // AK 32/spec 6.7: annotates whichever import Kind was just chosen with the target set, if the
        // payload names one. A row written before targetEmoteSetId existed, or one from a client that
        // omitted it (E5), simply has no property here — ReadTargetEmoteSet returns null and the
        // detail stays exactly as the two methods above built it.
        var targetEmoteSet = ReadTargetEmoteSet(root);
        if (targetEmoteSet is not null)
        {
            detail = detail! with { TargetEmoteSet = targetEmoteSet };
        }

        return true;
    }

    /// <summary>
    /// Reads the import ladder's target (spec 6.7) off an already-parsed details payload. Returns
    /// null — not a throw — whenever <c>targetEmoteSetId</c> is missing, not a non-empty string, or
    /// simply absent (E5: a valid, complete row with no set reported at all).
    /// </summary>
    private static AuditLogTargetEmoteSet? ReadTargetEmoteSet(JsonElement root)
    {
        if (!root.TryGetProperty(TargetEmoteSetIdProperty, out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || idElement.GetString() is not { Length: > 0 } id)
        {
            return null;
        }

        // Three-valued (E5): missing or non-boolean reads as null ("not applicable"/"not reported"),
        // never coerced to false — the set-centric endpoint never writes this property at all, since
        // its target set has no channel of ours to compare against.
        bool? isActiveSetOfChannel = root.TryGetProperty(TargetIsActiveSetOfChannelProperty, out var activeElement)
            ? activeElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

        string? ownerLogin = root.TryGetProperty(TargetOwnerTwitchLoginProperty, out var ownerElement)
            && ownerElement.ValueKind == JsonValueKind.String
                ? ownerElement.GetString()
                : null;

        return new AuditLogTargetEmoteSet(id, isActiveSetOfChannel, ownerLogin);
    }

    /// <summary>
    /// Checked ahead of <see cref="TryProjectChannelOrFileDetail"/> (F1 Station 5): a network-wide 7TV
    /// ranking has no source channel to fall through to, so it needs its own kind rather than sharing
    /// the source-name check there. Degrades to false — not a throw — when leaderboardSort is missing
    /// or outside the allowlist, which also doubles as this feature's rollback behavior: if this PR
    /// were ever reverted, rows already written with "seventv-leaderboard" would simply read back as a
    /// plain count instead of the endpoint crashing on a Kind it no longer recognizes.
    /// </summary>
    private static bool TryProjectLeaderboardDetail(JsonElement root, string? sourceKind, out AuditLogDetail? detail)
    {
        detail = null;
        if (sourceKind != LeaderboardSourceKind
            || !TryReadCount(root, AuditLogDetail.Kinds.EmoteCount, out var leaderboardCount)
            || !root.TryGetProperty(LeaderboardSortProperty, out var sortElement)
            || sortElement.ValueKind != JsonValueKind.String
            || !SevenTvLeaderboardSortWireCode.TryParse(sortElement.GetString(), out _))
        {
            return false;
        }

        detail = new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromLeaderboard, leaderboardCount, sortElement.GetString());
        return true;
    }

    private static bool TryProjectChannelOrFileDetail(JsonElement root, string? sourceKind, out AuditLogDetail? detail)
    {
        detail = null;
        if (sourceKind is not (ChannelSourceKind or ForeignChannelSourceKind or FileSourceKind)
            || !TryReadCount(root, AuditLogDetail.Kinds.EmoteCount, out var importedCount))
        {
            return false;
        }

        // sourceKind decides the kind, never the mere presence of a name: a file import that somehow
        // carries a source channel is still a file import, and reading the name instead would file it
        // under a channel origin it never had. A stray name is dropped rather than shown.
        if (sourceKind == FileSourceKind)
        {
            detail = new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromFile, importedCount, null);
            return true;
        }

        // A channel origin that cannot name its channel falls through (returns false) to the bare
        // count kind instead of claiming an origin. The endpoint rejects that combination, so this
        // only covers rows that got in around it; saying "N emotes" is honest, while both import kinds
        // would not be. Holds for the foreign kind as well — it is name-carrying for exactly the same
        // reason.
        var source = ReadSourceChannelName(root);
        if (source is null)
        {
            return false;
        }

        detail = new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromChannel, importedCount, source);
        return true;
    }

    private static string? ReadSourceChannelName(JsonElement root)
    {
        if (!root.TryGetProperty(SourceChannelNameProperty, out var sourceChannelNameElement)
            || sourceChannelNameElement.ValueKind != JsonValueKind.String
            || sourceChannelNameElement.GetString() is not { Length: > 0 } sourceChannelName)
        {
            return null;
        }

        return sourceChannelName.Length > MaxDetailTextLength
            ? sourceChannelName[..MaxDetailTextLength]
            : sourceChannelName;
    }

    private static bool TryProjectTitleDetail(JsonElement root, out AuditLogDetail? detail)
    {
        detail = null;
        if (!root.TryGetProperty(AuditLogDetail.Kinds.Title, out var title)
            || title.ValueKind != JsonValueKind.String
            || title.GetString() is not { Length: > 0 } text)
        {
            return false;
        }

        detail = new AuditLogDetail(
            AuditLogDetail.Kinds.Title,
            null,
            text.Length > MaxDetailTextLength ? text[..MaxDetailTextLength] : text);
        return true;
    }

    private static bool TryReadCount(JsonElement root, string propertyName, out long value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value);
    }
}
