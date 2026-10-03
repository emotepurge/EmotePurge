using System.Globalization;

namespace EmotePurge.Api.Validation;

/// <summary>
/// The paging contract every list endpoint shares. Out-of-range paging is corrected silently rather
/// than rejected, so it introduces no <see cref="ApiErrorCodes"/> entry — and none has to be
/// mirrored into both locale files for something a client can only hit by constructing the URL by
/// hand.
/// <para>
/// Extracted after the fourth verbatim copy of the same two lines. The query services document
/// their arguments as "expected to be pre-validated by the endpoint"; this is that validation, in
/// one place, so the cap cannot drift between endpoints.
/// </para>
/// </summary>
internal static class PagingQuery
{
    private const int DefaultPageSize = 20;

    /// <summary>
    /// Clamps a 1-based page and its size. <paramref name="maxPageSize"/> is the ceiling a caller
    /// may ask for — it exists so one client cannot turn a paged endpoint into a full-table read.
    /// <para>
    /// Both values arrive as raw strings on purpose. A handler parameter typed <see cref="int"/>
    /// fails to bind when the value is absent or not a number, and binding runs before the endpoint
    /// filters: the failure would answer 400 (500 in Development) even to a caller the authorization
    /// filter is about to refuse with 403. A missing or unparsable value is corrected like an
    /// out-of-range one, so binding can never fail.
    /// </para>
    /// </summary>
    public static (int Page, int PageSize) Clamp(string? page, string? pageSize, int maxPageSize = 100)
    {
        var parsedPage = Parse(page);
        var parsedPageSize = Parse(pageSize);
        return (parsedPage <= 0 ? 1 : parsedPage,
            parsedPageSize <= 0 ? DefaultPageSize : Math.Min(parsedPageSize, maxPageSize));
    }

    /// <summary>
    /// Trims a free-text filter and treats blank as absent, so `?actor=` and a missing `actor` mean
    /// the same thing to the query services.
    /// </summary>
    public static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Anything that is not an int32 — absent, text, or beyond the range — counts as 0, which Clamp
    // then replaces with the default.
    private static int Parse(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
