namespace EmotePurge.Core.Services;

/// <summary>
/// The global saved log filters (spec section 6.3): at most 30, names unique without regard to case,
/// whitespace runs and Unicode case (<c>NameKey</c>), filter strings validated by the log filter parser and
/// stored exactly as submitted. Writes are not audited (A12).
/// </summary>
public interface ILogFilterService
{
    /// <summary>All filters ordered by <c>SortOrder</c>, then id.</summary>
    Task<IReadOnlyList<LogFilterView>> ListAsync(CancellationToken ct);

    Task<LogFilterWriteResult> CreateAsync(string name, string filter, CancellationToken ct);

    Task<LogFilterWriteResult> UpdateAsync(int id, string name, string filter, CancellationToken ct);

    /// <summary><c>false</c> when the filter does not exist.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct);
}

public sealed record LogFilterView(int Id, string Name, string Filter, int SortOrder);

public enum LogFilterWriteStatus
{
    Ok,
    InvalidName,
    InvalidFilter,
    NameTaken,
    LimitReached,
    NotFound,
}

/// <summary>
/// Outcome of a create or update. <see cref="Filter"/> is set on <see cref="LogFilterWriteStatus.Ok"/>;
/// <see cref="Reason"/>, <see cref="Position"/> and <see cref="Length"/> on <see cref="LogFilterWriteStatus.InvalidFilter"/>.
/// </summary>
public sealed record LogFilterWriteResult(
    LogFilterWriteStatus Status,
    LogFilterView? Filter = null,
    string? Reason = null,
    int? Position = null,
    int? Length = null);
