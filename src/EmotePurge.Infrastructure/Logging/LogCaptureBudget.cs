using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// Daily entry budget of one process (admission step 4). Information is admitted only while less than
/// 80 % of the budget is used; Warning and above until 100 %. The count starts over at UTC midnight and
/// after a restart. Day and count share one 64-bit word updated by compare-and-swap, so admission is
/// atomic and lock-free. An entry admitted here and then rejected at the byte limit does not get its
/// budget back (plan decision L13).
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

    public bool TryAdmit(LogEntryLevel level)
    {
        var today = _time.GetUtcNow().UtcDateTime.Ticks / TimeSpan.TicksPerDay;
        var limit = level <= LogEntryLevel.Information ? _informationLimit : _limit;

        var current = Interlocked.Read(ref _state);
        while (true)
        {
            var used = (current >> 32) == today ? current & uint.MaxValue : 0;
            if (used >= limit)
            {
                return false;
            }

            var next = (today << 32) | (used + 1);
            var seen = Interlocked.CompareExchange(ref _state, next, current);
            if (seen == current)
            {
                return true;
            }

            current = seen;
        }
    }
}
