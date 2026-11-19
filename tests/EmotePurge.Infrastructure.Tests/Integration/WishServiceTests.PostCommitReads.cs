using System.Data.Common;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The image behind a response is read after the commit. Its failure must not turn the committed command into an
// error: a manager would repeat it ("extend" twice) and book it twice.
public partial class WishServiceTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("extend")]
    public async Task ACommandThatCommitted_StillSucceeds_WhenTheImageReadAfterTheCommitFails(string command)
    {
        var channel = await SeedChannelAsync();
        var interceptor = new FailImageReadInterceptor();
        var emoteId = EmoteId();
        long? wishId = command == "extend"
            ? await SeedRowAsync(channel, WishStatus.Active, w => w.ExpiresAtUtc = Now.AddDays(3))
            : null;
        var sut = Sut(fixture.CreateDbContext([interceptor]));

        var result = command == "create"
            ? await sut.CreateAsync(channel.Name, new CreateWishRequest(emoteId, "Emote", "Alias", null, null), Actor)
            : await sut.ExtendAsync(channel.Name, wishId!.Value, Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Null(result.Wish!.ImageUrl);
        Assert.True(interceptor.Failures >= 1);

        await using var db = fixture.CreateDbContext();
        var wish = await db.Wishes.SingleAsync(w => w.ChannelId == channel.Id);
        Assert.Equal(command == "create" ? WishStatus.Approved : WishStatus.Active, wish.Status);
        Assert.Equal(command == "extend" ? 1 : 0, wish.ExtensionCount);
        var audit = await db.AuditLogEntries.SingleAsync(a => a.ChannelName == channel.Name);
        Assert.Equal(command == "create" ? AuditActions.WishCreate : AuditActions.WishExtend, audit.Action);
        AssertPublishedOnce(channel);
    }

    // Fails the wish image query (the only read of Emotes without the slot join) once the command has run.
    private sealed class FailImageReadInterceptor : DbCommandInterceptor
    {
        private int _failures;

        public int Failures => _failures;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Emotes\"", StringComparison.Ordinal)
                && !command.CommandText.Contains("EmoteSlots", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _failures);
                throw new Npgsql.NpgsqlException("injected image read failure", innerException: null);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
