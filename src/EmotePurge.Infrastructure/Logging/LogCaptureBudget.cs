using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// Daily entry budget of one process (admission step 4). Information is admitted only while less than
/// 80 % of the budget is used; Warning and above until 100 %. The count starts over at UTC midnight and
/// after a restart. Day and count share one 64-bit word updated by compare-and-swap, so admission is
/// atomic and lock-free, and the stored day never moves backwards. An admitted entry that the buffer
/// then rejects (queue full, by count or by bytes) gets its budget back on the same day via
/// <see cref="Refund"/>; an entry that fails while being built stays charged.
/// </summary>
internal sealed class LogCaptureBudget
{
    private const int InformationSharePercent = 80;

    private readonly TimeProvider _time;
    private readonly long _limit;
    private readonly long _informationLimit;

    // High 32 bits: UTC day number; low 32 bits: entries admitted on that day.
    private long _state;

    public LogCaptureBudget(int dailyEntryBudget, TimeProvider time)
    {
        _time = time;
        _limit = Math.Max(0, dailyEntryBudget);
        _informationLimit = _limit * InformationSharePercent / 100;
    }

    public bool TryAdmit(LogEntryLevel level) => TryAdmit(level, out _);

    /// <summary>Admits like <see cref="TryAdmit(LogEntryLevel)"/> and reports the day that was charged.</summary>
    public bool TryAdmit(LogEntryLevel level, out long chargedDay)
    {
        var clockDay = _time.GetUtcNow().UtcDateTime.Ticks / TimeSpan.TicksPerDay;
        var limit = level <= LogEntryLevel.Information ? _informationLimit : _limit;

        var current = Interlocked.Read(ref _state);
        while (true)
        {
            // A thread that read the clock before another moved the state to the next day must not write
            // its older day back; it is charged against the newer one.
            var storedDay = current >> 32;
            var day = Math.Max(clockDay, storedDay);
            var used = storedDay == day ? current & uint.MaxValue : 0;
            if (used >= limit)
            {
                chargedDay = day;
                return false;
            }

            var seen = Interlocked.CompareExchange(ref _state, (day << 32) | (used + 1), current);
            if (seen == current)
            {
                chargedDay = day;
                return true;
            }

            current = seen;
        }
    }

    /// <summary>
    /// Gives back one admission charged on <paramref name="chargedDay"/>. A no-op once the state has moved
    /// to another day (the old day's count no longer matters) or if nothing is left to give back.
    /// </summary>
    public void Refund(long chargedDay)
    {
        var current = Interlocked.Read(ref _state);
        while ((current >> 32) == chargedDay && (current & uint.MaxValue) > 0)
        {
            var seen = Interlocked.CompareExchange(ref _state, current - 1, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
