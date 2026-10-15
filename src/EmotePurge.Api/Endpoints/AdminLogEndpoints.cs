using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmotePurge.Api.LogViewer;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// The admin log viewer routes (#383, spec section 8). Mapped onto the <c>/api/admin</c> group, so the
/// group's authorization and <c>GlobalAdminAuthorizationFilter</c> run first; each sub-group then enters the
/// suppression scope (<see cref="LogViewerQueryScopeFilter"/>) around the whole handler, and only the three
/// query routes are admitted through the concurrency gate. Handlers stay thin: they parse the query string
/// and shape the wire format, everything else is <see cref="ILogQueryService"/> / <see cref="ILogFilterService"/>.
/// </summary>
public static class AdminLogEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxIds = 200;

    public static void MapAdminLogEndpoints(this RouteGroupBuilder group)
    {
        var logs = group.MapGroup("/logs").AddEndpointFilter<LogViewerQueryScopeFilter>();
        var filters = group.MapGroup("/log-filters").AddEndpointFilter<LogViewerQueryScopeFilter>();

        logs.MapGet("", GetLogsAsync).AddEndpointFilter<LogQueryConcurrencyFilter>();

        logs.MapGet("/summary", async (string? q, string? range, string? from, string? to, ILogQueryService queries, CancellationToken ct) =>
        {
            var summary = await queries.GetSummaryAsync(q, ParseRange(range, from, to), ct);
            return Results.Ok(ToWire(summary));
        }).AddEndpointFilter<LogQueryConcurrencyFilter>();

        logs.MapGet("/{id:long}", async (long id, ILogQueryService queries, CancellationToken ct) =>
        {
            var detail = await queries.GetEntryAsync(id, ct);
            return detail is null
                ? Results.NotFound(new { errorCode = ApiErrorCodes.LogEntryNotFound })
                : Results.Ok(ToWire(detail));
        }).AddEndpointFilter<LogQueryConcurrencyFilter>();

        // Not gated: cached for 30 s with single-flight inside the reader.
        logs.MapGet("/status", async (ILogStatusReader status, CancellationToken ct) =>
            Results.Ok(ToWire(await status.ReadAsync(ct))));

        filters.MapGet("", async (ILogFilterService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(ct)).Select(ToWire)));

        filters.MapPost("", async (LogFilterRequest body, ILogFilterService service, CancellationToken ct) =>
            MapWrite(await service.CreateAsync(body.Name ?? string.Empty, body.Filter ?? string.Empty, ct), created: true));

        filters.MapPut("/{id:int}", async (int id, LogFilterRequest body, ILogFilterService service, CancellationToken ct) =>
            MapWrite(await service.UpdateAsync(id, body.Name ?? string.Empty, body.Filter ?? string.Empty, ct), created: false));

        filters.MapDelete("/{id:int}", async (int id, ILogFilterService service, CancellationToken ct) =>
            await service.DeleteAsync(id, ct)
                ? Results.NoContent()
                : Results.NotFound(new { errorCode = ApiErrorCodes.LogFilterNotFound }));
    }

    private static async Task<IResult> GetLogsAsync(
        HttpContext http,
        string? q,
        string? range,
        string? from,
        string? to,
        string? before,
        int? limit,
        bool? keysOnly,
        ILogQueryService queries,
        CancellationToken ct)
    {
        if (http.Request.Query.ContainsKey("ids"))
        {
            var ids = ParseIds(http.Request.Query["ids"].ToString());
            var entries = await queries.GetByIdsAsync(ids, ct);

            // Targeted reload: no range applies, so there are no bounds to name.
            return Results.Ok(new
            {
                entries = entries.Select(ToWire),
                nextCursor = (string?)null,
                rangeFromUtc = (DateTime?)null,
                rangeToUtc = (DateTime?)null,
                truncated = false,
            });
        }

        if (keysOnly == true)
        {
            // The reconcile sends the frozen start of the loaded interval; `to` is optional (request time).
            var fromUtc = ParseInstant(from) ?? throw new LogRangeException("keysOnly needs a from.");
            var keys = await queries.GetKeysAsync(q, fromUtc, ParseInstant(to), ct);
            return Results.Ok(new
            {
                entries = keys.Keys.Select(k => new { id = k.Id, timestampUtc = Utc(k.TimestampUtc) }),
                nextCursor = (string?)null,
                rangeFromUtc = Utc(keys.RangeFromUtc),
                rangeToUtc = Utc(keys.RangeToUtc),
                truncated = keys.Truncated,
            });
        }

        var page = await queries.GetPageAsync(q, ParseRange(range, from, to), before, limit ?? DefaultPageSize, ct);
        return Results.Ok(new
        {
            entries = page.Entries.Select(ToWire),
            nextCursor = page.NextCursor,
            rangeFromUtc = Utc(page.RangeFromUtc),
            rangeToUtc = Utc(page.RangeToUtc),
            truncated = page.Truncated,
        });
    }

    private static IResult MapWrite(LogFilterWriteResult result, bool created) => result.Status switch
    {
        LogFilterWriteStatus.Ok when created => Results.Json(ToWire(result.Filter!), statusCode: StatusCodes.Status201Created),
        LogFilterWriteStatus.Ok => Results.Ok(ToWire(result.Filter!)),
        LogFilterWriteStatus.InvalidName => Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidLogFilterName }),
        LogFilterWriteStatus.InvalidFilter => Results.BadRequest(new
        {
            errorCode = ApiErrorCodes.InvalidLogFilter,
            reason = result.Reason,
            position = result.Position,
            length = result.Length,
        }),
        LogFilterWriteStatus.NameTaken => Results.Conflict(new { errorCode = ApiErrorCodes.LogFilterNameTaken }),
        LogFilterWriteStatus.LimitReached => Results.Conflict(new { errorCode = ApiErrorCodes.LogFilterLimitReached }),
        LogFilterWriteStatus.NotFound => Results.NotFound(new { errorCode = ApiErrorCodes.LogFilterNotFound }),
        _ => throw new InvalidOperationException($"Unhandled log filter write status {result.Status}."),
    };

    private static LogRangeRequest ParseRange(string? range, string? from, string? to) =>
        new(string.IsNullOrWhiteSpace(range) ? null : range, ParseInstant(from), ParseInstant(to));

    // Absolute instants arrive as ISO-8601 UTC. Parsed here rather than by model binding, which would
    // convert a trailing Z to local time; anything unparseable is the same 400 as a bad range.
    private static DateTime? ParseInstant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            throw new LogRangeException($"Not a valid instant: {value}.");
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    private static List<long> ParseIds(string raw)
    {
        var tokens = raw.Split(',');
        if (tokens.Length > MaxIds)
        {
            throw new LogRangeException("Too many ids.");
        }

        var ids = new List<long>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                throw new LogRangeException("Invalid id token.");
            }

            ids.Add(id);
        }

        return ids;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static DateTime? Utc(DateTime? value) => value is null ? null : Utc(value.Value);

    private static object ToWire(LogFilterView filter) =>
        new { id = filter.Id, name = filter.Name, filter = filter.Filter, sortOrder = filter.SortOrder };

    private static Dictionary<string, object?> ToWire(LogEntryView entry)
    {
        var wire = new Dictionary<string, object?>
        {
            ["id"] = entry.Id,
            ["timestampUtc"] = Utc(entry.TimestampUtc),
            ["level"] = LogViewerTokens.Level(entry.Level),
            ["source"] = LogViewerTokens.Source(entry.Source),
            ["instanceId"] = entry.InstanceId,
            ["category"] = entry.Category,
            ["eventId"] = entry.EventId,
            ["eventName"] = entry.EventName,
            ["message"] = entry.Message,
            ["hasException"] = entry.HasException,
            ["exceptionType"] = entry.ExceptionType,
            ["properties"] = ParseProperties(entry.PropertiesJson),
            ["truncated"] = entry.Truncated,
        };
        return wire;
    }

    private static Dictionary<string, object?> ToWire(LogEntryDetailView detail)
    {
        var wire = ToWire(detail.Entry);
        wire["exception"] = detail.Exception;
        wire["processStartedUtc"] = Utc(detail.ProcessStartedUtc);
        wire["uptimeSeconds"] = detail.UptimeSeconds;
        return wire;
    }

    private static JsonNode ParseProperties(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) is JsonObject parsed ? parsed : new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static object ToWire(LogSummary s) => new
    {
        rangeFromUtc = Utc(s.RangeFromUtc),
        rangeToUtc = Utc(s.RangeToUtc),
        bucketFromUtc = Utc(s.BucketFromUtc),
        bucketToUtc = Utc(s.BucketToUtc),
        bucketSeconds = s.BucketSeconds,
        bucketStartsUtc = s.BucketStartsUtc.Select(Utc),
        counts = new { info = s.Info, warn = s.Warn, error = s.Error },
        total = s.Total,
        facets = new
        {
            source = s.Facets.Source.Select(f => new { value = f.Value, count = f.Count }),
            level = s.Facets.Level.Select(f => new { value = f.Value, count = f.Count }),
            category = s.Facets.Category.Select(f => new { value = f.Value, @short = f.Short, count = f.Count }),
            categoryOther = s.Facets.CategoryOther,
        },
        dropped = new
        {
            total = s.Dropped.Total,
            queueFull = s.Dropped.QueueFull,
            budget = s.Dropped.Budget,
            writeUnconfirmed = s.Dropped.WriteUnconfirmed,
            captureFailed = s.Dropped.CaptureFailed,
            reentrant = s.Dropped.Reentrant,
            rejected = s.Dropped.Rejected,
        },
        processStarts = s.ProcessStarts.Select(p => new
        {
            source = LogViewerTokens.Source(p.Source),
            instanceId = p.InstanceId,
            startedUtc = Utc(p.StartedUtc),
        }),
    };

    private static object ToWire(LogCaptureStatusView s) => new
    {
        generatedAtUtc = Utc(s.GeneratedAtUtc),
        sources = s.Sources.Select(x => new
        {
            source = LogViewerTokens.Source(x.Source),
            state = x.State switch
            {
                LogSourceState.Healthy => "healthy",
                LogSourceState.Stale => "stale",
                LogSourceState.Disabled => "disabled",
                LogSourceState.Missing => "missing",
                _ => throw new InvalidOperationException($"Unhandled log source state {x.State}."),
            },
            instanceId = x.InstanceId,
            startedUtc = Utc(x.StartedUtc),
            lastFlushUtc = Utc(x.LastFlushUtc),
        }),
        coverage24h = new
        {
            complete = s.Coverage24h.Complete,
            recordedErrors = s.Coverage24h.RecordedErrors,
            incompleteSince = s.Coverage24h.IncompleteSince.Select(i => new
            {
                source = LogViewerTokens.Source(i.Source),
                lastIncompleteAtUtc = Utc(i.LastIncompleteAtUtc),
            }),
        },
        retention = new { oldestEntryUtc = Utc(s.Retention.OldestEntryUtc), overdue = s.Retention.Overdue },
    };
}

/// <summary>Body of <c>POST</c> and <c>PUT /api/admin/log-filters</c>; missing members are treated as empty.</summary>
internal sealed record LogFilterRequest(string? Name, string? Filter);
