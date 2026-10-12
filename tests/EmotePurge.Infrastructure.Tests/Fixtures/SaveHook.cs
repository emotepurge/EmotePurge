using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

/// <summary>Runs <c>beforeSave</c> at the start of every SaveChanges (1-based call number).</summary>
public sealed class SaveHook(Func<DbContext, int, Task> beforeSave) : SaveChangesInterceptor
{
    public int Calls { get; private set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Calls++;
        await beforeSave(eventData.Context!, Calls);
        return result;
    }
}
