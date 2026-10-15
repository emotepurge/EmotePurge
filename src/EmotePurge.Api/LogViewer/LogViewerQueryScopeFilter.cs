using System.Diagnostics;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Logging;

namespace EmotePurge.Api.LogViewer;

/// <summary>
/// Wraps every log viewer handler in <see cref="LogCaptureSuppression"/> (recursion layer 4): the query and
/// filter services go through EF, which logs a cancelled or failed command at Error, and capturing that would
/// feed the viewer its own reads. The viewer's errors are mapped here, inside the scope, so no exception
/// reaches the exception-handler middleware (which would log it) either. One console line per minute, in a
/// category the capture excludes, is the only trace of a viewer failure.
/// </summary>
internal sealed class LogViewerQueryScopeFilter(ILoggerFactory loggerFactory) : IEndpointFilter
{
    // Not "EmotePurge.Infrastructure.Logging.*" by accident: the capture drops that whole namespace.
    private const string LogCategory = "EmotePurge.Infrastructure.Logging.ViewerQueries";
    private static readonly long ThrottleTicks = Stopwatch.Frequency * 60;
    private static long _lastLoggedTimestamp;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        using var scope = LogCaptureSuppression.Enter();
        try
        {
            return await next(context);
        }
        catch (LogFilterSyntaxException ex)
        {
            return Results.BadRequest(new
            {
                errorCode = ApiErrorCodes.InvalidLogFilter,
                reason = ex.Reason,
                position = ex.Position,
                length = ex.Length,
            });
        }
        catch (LogRangeException)
        {
            return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidLogRange });
        }
        catch (LogQueryTimeoutException ex)
        {
            LogThrottled(ex, "A log viewer query exceeded its time limit.");
            return Results.Json(new { errorCode = ApiErrorCodes.LogQueryTimeout }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        // A client that hung up is not a failure: let the cancellation pass like on every other route.
        catch (Exception ex) when (ex is not OperationCanceledException || !context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            LogThrottled(ex, "A log viewer request failed.");
            return Results.Json(new { errorCode = ApiErrorCodes.UnexpectedError }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private void LogThrottled(Exception ex, string message)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastLoggedTimestamp);
        if (last != 0 && now - last < ThrottleTicks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastLoggedTimestamp, now, last) == last)
        {
            loggerFactory.CreateLogger(LogCategory).LogWarning(ex, "{Message}", message);
        }
    }
}
