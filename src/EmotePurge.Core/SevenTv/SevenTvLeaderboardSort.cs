namespace EmotePurge.Core.SevenTv;

/// <summary>
/// The two network-wide 7TV rankings the leaderboard import source (spec 2026-09-13) exposes:
/// today's movers and the all-time ranking. A strict subset of 7TV's own v4 <c>Sort</c> enum —
/// <c>TRENDING_DAILY</c> and <c>TOP_ALL_TIME</c> are the exact wire codes 7TV's
/// <c>EmoteQuery.search(sort: Sort!)</c> argument expects, confirmed live against a value from this
/// same vocabulary (docs/designs/Fremde-Kanaele-als-Import-Quelle-2026-09-09.md, "Trending-Katalog
/// funktioniert": <c>EmoteQuery.search(query, tags, sort, filters, page, perPage)</c> with
/// <c>SortBy = TRENDING_DAILY|WEEKLY|MONTHLY, TOP_DAILY|WEEKLY|MONTHLY|ALL_TIME, …</c>, live results
/// read back for <c>TRENDING_DAILY</c>).
/// </summary>
public enum SevenTvLeaderboardSort
{
    TrendingDaily,
    TopAllTime
}

/// <summary>
/// The bidirectional mapping between <see cref="SevenTvLeaderboardSort"/> and 7TV's own wire
/// vocabulary — used both to build the GraphQL <c>sort</c> variable
/// (<see cref="ISevenTvApiClient.SearchEmotesAsync"/>) and to parse the <c>sortBy</c> query
/// parameter of <c>GET /api/seventv/leaderboard</c> (<c>LeaderboardSortValidationFilter</c>).
/// </summary>
/// <remarks>
/// <see cref="TryParse"/> is deliberately strict-ordinal: a plain <c>switch</c> over a string
/// compares case-sensitively by default, so <c>trending_daily</c> (or any other casing 7TV itself
/// never sends) stays outside the allowlist rather than being accepted as a lookalike.
/// </remarks>
public static class SevenTvLeaderboardSortWireCode
{
    public const string TrendingDailyWireCode = "TRENDING_DAILY";

    public const string TopAllTimeWireCode = "TOP_ALL_TIME";

    public static string ToWireCode(this SevenTvLeaderboardSort sort) => sort switch
    {
        SevenTvLeaderboardSort.TrendingDaily => TrendingDailyWireCode,
        SevenTvLeaderboardSort.TopAllTime => TopAllTimeWireCode,
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Unknown SevenTvLeaderboardSort.")
    };

    public static bool TryParse(string? wireCode, out SevenTvLeaderboardSort sort)
    {
        switch (wireCode)
        {
            case TrendingDailyWireCode:
                sort = SevenTvLeaderboardSort.TrendingDaily;
                return true;
            case TopAllTimeWireCode:
                sort = SevenTvLeaderboardSort.TopAllTime;
                return true;
            default:
                sort = default;
                return false;
        }
    }
}
