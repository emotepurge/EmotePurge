using System.Text.Json;
using EmotePurge.Core.SevenTv;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.SevenTv;

// Parses the d.body payload of 7TV EventAPI dispatch frames (op 0). Lives next to the REST DTOs
// because dispatch values share the emote JSON shape with the REST emote-set response, so the
// internal SevenTvEmoteJsonDto/SevenTvEmoteJsonMapper pair is reused instead of duplicated.
// Never throws: a malformed entry is logged and skipped — one broken change must not take down
// the receive loop that carries every other channel's updates.
public static class SevenTvDispatchParser
{
    // Change entries whose key is not "emotes" are set-level metadata (e.g. a rename of the set
    // itself) and carry no emote payload.
    private const string EmotesKey = "emotes";

    // The JSON property carrying a change entry's new value, on both the outer ChangeMap entries
    // (pushed/updated) and the nested ones inside a connections change.
    private const string ValueKey = "value";

    public static SevenTvEmoteSetDelta ParseEmoteSetUpdate(JsonElement body, ILogger logger)
    {
        var pushed = new List<SevenTvEmote>();
        var updated = new List<SevenTvEmote>();
        var pulledIds = new List<string>();

        // The wire uses pushed/pulled/updated (ChangeMap). added/removed do not exist in real
        // dispatches — reading them is the bug that sank the 2026-07 implementation.
        if (body.TryGetProperty("pushed", out var pushedArray) && pushedArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in pushedArray.EnumerateArray())
            {
                TryMapEmoteChange(change, ValueKey, pushed, logger);
            }
        }

        if (body.TryGetProperty("updated", out var updatedArray) && updatedArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in updatedArray.EnumerateArray())
            {
                TryMapEmoteChange(change, ValueKey, updated, logger);
            }
        }

        if (body.TryGetProperty("pulled", out var pulledArray) && pulledArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in pulledArray.EnumerateArray())
            {
                TryGetPulledEmoteId(change, pulledIds, logger);
            }
        }

        return new SevenTvEmoteSetDelta(pushed, updated, pulledIds);
    }

    // body of a user.update dispatch. The active-set switch arrives as a nested change on the
    // connections field: updated[key=="connections"].value[] holds inner changes, of which
    // key=="emote_set_id" carries the old/new set ids as plain strings (verified live 2026-07-30;
    // the sibling key=="emote_set" duplicates them as full objects and serves as fallback).
    public static SevenTvUserSetChange? ParseUserSetChange(JsonElement body, ILogger logger)
    {
        try
        {
            if (!body.TryGetProperty("id", out var idProp) || idProp.GetString() is not { Length: > 0 } sevenTvUserId)
            {
                return null;
            }

            if (!body.TryGetProperty("updated", out var updatedArray) || updatedArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var change in updatedArray.EnumerateArray())
            {
                if (!HasKey(change, "connections") ||
                    !change.TryGetProperty(ValueKey, out var inner) ||
                    inner.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                if (TryFindEmoteSetIdChange(inner, out var oldSetId, out var newSetId))
                {
                    return new SevenTvUserSetChange(sevenTvUserId, oldSetId, newSetId);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unerwartete user.update-Dispatch-Form übersprungen: {Body}", Truncate(body));
            return null;
        }
    }

    // The inner change list of one "connections" entry: emote_set_id (plain old/new set id strings)
    // takes priority and stops the scan immediately; emote_set (old/new as full objects) is the
    // fallback and keeps scanning, so a later emote_set_id entry still overrides it — same priority
    // as the original inline loop, just named.
    private static bool TryFindEmoteSetIdChange(JsonElement inner, out string? oldSetId, out string? newSetId)
    {
        oldSetId = null;
        newSetId = null;
        var found = false;

        foreach (var innerChange in inner.EnumerateArray())
        {
            if (HasKey(innerChange, "emote_set_id"))
            {
                oldSetId = GetNonEmptyString(innerChange, "old_value");
                newSetId = GetNonEmptyString(innerChange, ValueKey);
                return true;
            }

            if (HasKey(innerChange, "emote_set"))
            {
                oldSetId = GetNonEmptyObjectId(innerChange, "old_value");
                newSetId = GetNonEmptyObjectId(innerChange, ValueKey);
                found = true;
            }
        }

        return found;
    }

    private static void TryMapEmoteChange(JsonElement change, string valueProperty, List<SevenTvEmote> into, ILogger logger)
    {
        try
        {
            if (!HasKey(change, EmotesKey) ||
                !change.TryGetProperty(valueProperty, out var value) ||
                value.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var dto = value.Deserialize<SevenTvEmoteJsonDto>(SevenTvEmoteJsonMapper.JsonOptions);
            if (dto is null || dto.Id.Length == 0)
            {
                return;
            }

            into.Add(SevenTvEmoteJsonMapper.MapDto(dto));
        }
        catch (Exception ex)
        {
            // Warning, not Debug: the 2026-07 parser logged unknown shapes at Debug level and its
            // failures were invisible at production log levels.
            logger.LogWarning(ex, "Unerwartete emote_set.update-Dispatch-Änderung übersprungen: {Change}", Truncate(change));
        }
    }

    private static void TryGetPulledEmoteId(JsonElement change, List<string> into, ILogger logger)
    {
        try
        {
            if (!HasKey(change, EmotesKey))
            {
                return;
            }

            var id = GetNonEmptyObjectId(change, "old_value");
            if (id is not null)
            {
                into.Add(id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unerwartete emote_set.update-Dispatch-Entfernung übersprungen: {Change}", Truncate(change));
        }
    }

    private static bool HasKey(JsonElement change, string expectedKey) =>
        change.ValueKind == JsonValueKind.Object &&
        change.TryGetProperty("key", out var key) &&
        key.ValueKind == JsonValueKind.String &&
        key.GetString() == expectedKey;

    private static string? GetNonEmptyString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string? GetNonEmptyObjectId(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
        id.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string Truncate(JsonElement element)
    {
        var raw = element.GetRawText();
        return raw.Length <= 500 ? raw : raw[..500] + "…";
    }
}
