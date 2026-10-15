namespace EmotePurge.Api.LogViewer;

/// <summary>
/// Admits a log query only while a gate slot is free: waits up to the gate's limit, then answers 503 with
/// <c>Retry-After: 5</c> and no body (the client keeps its last answer). Registered after the scope filter,
/// so a rejected request never opens a database command.
/// </summary>
internal sealed class LogQueryConcurrencyFilter(LogQueryConcurrencyGate gate) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        using var slot = await gate.TryEnterAsync(context.HttpContext.RequestAborted);
        if (slot is null)
        {
            context.HttpContext.Response.Headers.RetryAfter = "5";
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return await next(context);
    }
}
