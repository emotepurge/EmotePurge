using System.Text;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The pass behind the real runner (#487 T8b): lock, epoch, token upkeep and session are the runner's; steps 3 and 4
// are the real WishBotPass. Covers the Unauthorized over two passes (Revision 6b K6b-3, 6c K6c-5) and the kill switch
// (AK81).
public partial class WishBotPassTests
{
    // K6b-3, two passes. Pass 1: a call with the token answered Unauthorized (reported by the wish part): the token is
    // unusable at once, the unauthenticated editor read still runs, nothing is suspended, the row stays valid. Pass 2:
    // the verification decides: 401 → invalid with botAccount.tokenState, the account is unavailable and the fallback
    // hands the open actions over; Ok → no fallback, everything goes on.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUnauthorized_TheNextPassVerificationDecides(bool secondVerificationUnauthorized)
    {
        using var sealer = new RsaBotAccountTokenSealer(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));
        var token = Jwt(Now.AddDays(30));
        await SeedSealedAccountAsync(sealer.Seal(token));
        var channel = await SeedChannelAsync();
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));
        var unconfirmed = await SeedWishAsync(channel, WishStatus.Unconfirmed, w => w.BotWriteAtUtc = Now.AddMinutes(-3));
        var queued = await SeedWishAsync(channel, WishStatus.Active, Queue);
        var client = Substitute.For<ISevenTvWriteClient>();
        client.VerifyAsync(token, Arg.Any<CancellationToken>()).Returns(
            SevenTvWriteResult<string>.Ok(BotA),
            secondVerificationUnauthorized
                ? SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Unauthorized())
                : SevenTvWriteResult<string>.Ok(BotA));
        var (runner, pass) = CreateRunner(client);

        pass.ReportUnauthorizedFirst = true;
        Assert.Equal(BotAccountPassResult.Completed, await runner.RunPassAsync());

        Assert.Equal([false], pass.TokenUsableAfterReport);
        await client.Received(1).VerifyAsync(token, Arg.Any<CancellationToken>());
        await _sevenTv.Received(1).GetEditorForAsync(BotA, Arg.Any<CancellationToken>());
        Assert.Null((await SettingsAsync(channel)).BotSuspendedReason);
        Assert.Empty(await SuspendAuditsAsync());
        Assert.Equal(BotAccountStatus.Valid, (await CredentialAsync()).Status);
        Assert.Equal(WishStatus.Unconfirmed, (await LoadAsync(unconfirmed)).Status);

        pass.ReportUnauthorizedFirst = false;
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(BotAccountPassResult.Completed, await runner.RunPassAsync());

        await client.Received(2).VerifyAsync(token, Arg.Any<CancellationToken>());
        if (secondVerificationUnauthorized)
        {
            Assert.Equal(BotAccountStatus.Invalid, (await CredentialAsync()).Status);
            Assert.Equal(1, await CountAuditsAsync(AuditActions.BotAccountTokenState));
            Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(unconfirmed)));
            Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(await LoadAsync(queued)));
            Assert.Null((await SettingsAsync(channel)).BotSuspendedReason);
        }
        else
        {
            Assert.Equal(BotAccountStatus.Valid, (await CredentialAsync()).Status);
            Assert.True(pass.Sessions[^1].TokenUsable);
            Assert.Equal(WishStatus.Unconfirmed, (await LoadAsync(unconfirmed)).Status);
            Assert.Equal(WishStatus.Active, (await LoadAsync(queued)).Status);
        }
    }

    // AK81: the kill switch, set between two passes, makes the next pass hand every open action over (the state of AK79)
    // without a verification and without an editor read; a second instance without the lock runs only the deadline step.
    [Fact]
    public async Task TheKillSwitch_FallsBackEveryBotChannel_AndOnlyTheLockHolderRuns()
    {
        using var sealer = new RsaBotAccountTokenSealer(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));
        var token = Jwt(Now.AddDays(30));
        await SeedSealedAccountAsync(sealer.Seal(token));
        var channel = await SeedChannelAsync();
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));
        var approved = await SeedWishAsync(channel, WishStatus.Approved);
        var unconfirmed = await SeedWishAsync(channel, WishStatus.Unconfirmed, w => w.BotWriteAtUtc = Now.AddMinutes(-3));
        var queued = await SeedWishAsync(channel, WishStatus.Active, Queue);
        var client = Substitute.For<ISevenTvWriteClient>();
        client.VerifyAsync(token, Arg.Any<CancellationToken>()).Returns(SevenTvWriteResult<string>.Ok(BotA));
        var (runner, _) = CreateRunner(client);
        var (second, secondPass) = CreateRunner(client);

        await runner.RunPassAsync();
        Assert.Equal(BotAccountPassResult.NoLock, await second.RunPassAsync());
        Assert.Empty(secondPass.Sessions);
        Assert.Equal(WishStatus.Unconfirmed, (await LoadAsync(unconfirmed)).Status);

        await using (var db = CreateDb())
        {
            await db.BotAccountCredentials.ExecuteUpdateAsync(u => u.SetProperty(c => c.KillSwitch, true));
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        await runner.RunPassAsync();

        await client.Received(1).VerifyAsync(token, Arg.Any<CancellationToken>());
        await _sevenTv.Received(1).GetEditorForAsync(BotA, Arg.Any<CancellationToken>());
        Assert.Equal((WishStatus.Approved, null), StatusOf(await LoadAsync(approved)));
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(unconfirmed)));
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(await LoadAsync(queued)));
        Assert.Null((await SettingsAsync(channel)).BotSuspendedReason);
    }

    private (BotAccountRunner Runner, ReportingPass Pass) CreateRunner(ISevenTvWriteClient client, AppDbContext? db = null)
    {
        db ??= CreateDb();
        _disposables.Add(db);
        var opener = new RsaBotAccountTokenOpener(BotAccountTokenKeys.ImportPrivateKey(BotAccountTestKeys.PrivateKey));
        _disposables.Add(new Disposal(opener));
        var pass = new ReportingPass(Pass(db));
        var runner = new BotAccountRunner(
            db, opener, client, pass, new BotAccountOptions { Enabled = true, WriteDelaySeconds = 0 }, _clock, NullLogger<BotAccountRunner>.Instance);
        _disposables.Add(runner);
        return (runner, pass);
    }

    // A valid token of bot A, verified a minute ago; the runner's own verification after taking the lock follows.
    private async Task SeedSealedAccountAsync(string seal)
    {
        await using var db = CreateDb();
        db.BotAccountCredentials.Add(new BotAccountCredential
        {
            SealedToken = seal,
            Status = BotAccountStatus.Valid,
            ExpiresAtUtc = Now.AddDays(30),
            BotSevenTvUserId = BotA,
            IdentityVersion = Identity,
            CredentialVersion = 1,
            LastVerifiedAtUtc = Now.AddMinutes(-1),
            LastVerifiedCredentialVersion = 1,
        });
        await db.SaveChangesAsync();
    }

    private async Task<BotAccountCredential> CredentialAsync()
    {
        await using var db = CreateDb();
        return await db.BotAccountCredentials.AsNoTracking().SingleAsync();
    }

    private async Task<int> CountAuditsAsync(string action)
    {
        await using var db = CreateDb();
        return await db.AuditLogEntries.CountAsync(a => a.Action == action);
    }

    private static string Jwt(DateTime expiresAt)
    {
        var exp = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();
        return $"{B64("""{"alg":"HS256","typ":"JWT"}""")}.{B64($$"""{"sub":"a","exp":{{exp}}}""")}.c2lnbmF0dXJl";
    }

    private static string B64(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class Disposal(IDisposable inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // The real pass behind a wish part that can report an Unauthorized first, as a call with the token would.
    private sealed class ReportingPass(IWishBotPass inner) : IWishBotPass
    {
        public bool ReportUnauthorizedFirst { get; set; }
        public List<bool> TokenUsableAfterReport { get; } = [];
        public List<IBotAccountSession> Sessions { get; } = [];

        public Task RunDeadlineStepAsync(CancellationToken cancellationToken = default) => inner.RunDeadlineStepAsync(cancellationToken);

        public Task RunAsync(IBotAccountSession session, CancellationToken cancellationToken = default)
        {
            Sessions.Add(session);
            if (ReportUnauthorizedFirst)
            {
                session.ReportUnauthorized();
                TokenUsableAfterReport.Add(session.TokenUsable);
            }

            return inner.RunAsync(session, cancellationToken);
        }
    }
}
