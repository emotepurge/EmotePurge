using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// <see cref="IWishBotPass"/>, the wishes' steps of the bot account's pass (plan #487 2.5). Registered scoped by the
/// worker's <c>AddBotAccountWriter</c> next to the runner, so it shares the runner's loop-long scope and with it the
/// runner's <see cref="AppDbContext"/>: every step leaves nothing tracked behind (plan K6b-14).
/// <list type="bullet">
/// <item>Step 0, the deadline step: T9 and T10.</item>
/// <item>Step 2, activation requests and invitations: open acceptance attempts resolved by a read, requests confirmed or
/// ended, invitations accepted after [F7] (<c>WishBotPass.Invitations.cs</c>, T8c).</item>
/// <item>Step 3, the channel states: coverage, consent and the editor read per activated channel, with suspension and
/// resume (<c>WishBotPass.ChannelStates.cs</c>, T8b).</item>
/// <item>Step 4, the fallback run (<see cref="IWishBotFallback"/>, T8b), the channels' states from the session's account.</item>
/// <item>Step 5, the derived queue: T9 and T10.</item>
/// </list>
/// <see cref="RunAsync"/> runs whatever the token state (<see cref="IBotAccountSession.Token"/> is only set while
/// <c>valid</c>); a lost lock or epoch ends it without booking anything after that, and so does a credential that changed
/// between the preflight and a send (plan 2.5 revision 3: no fallback, the next pass opens the new seal).
/// </summary>
public sealed partial class WishBotPass(
    AppDbContext db,
    IBotAccountEditorCheck editorCheck,
    ISevenTvWriteClient writeClient,
    IWishBotFallback fallback,
    WishOptions wishOptions,
    BotAccountOptions botAccountOptions,
    IGlobalAdminAllowlist admins,
    IWishChangedPublisher changedPublisher,
    TimeProvider timeProvider,
    ILogger<WishBotPass> logger) : IWishBotPass
{
    public Task RunDeadlineStepAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task RunAsync(IBotAccountSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        // The account as the session saw it after the token upkeep; a pause or an unusable token is no unavailability
        // (plan 2.3, 2.5), only the account-wide rule counts.
        var account = AccountOf(session.Account);

        if (await RunInvitationsAsync(session, account, cancellationToken) == PassStep.Stop)
        {
            return;
        }

        if (!await ReviewChannelsAsync(session, account, cancellationToken))
        {
            return;
        }

        await fallback.ApplyAsync(account, cancellationToken);
    }

    private BotAccountAvailabilityState AccountOf(BotAccountSnapshot snapshot) =>
        BotAccountAvailabilityState.Of(
            wishOptions.Enabled && botAccountOptions.Enabled,
            snapshot.Status,
            snapshot.ExpiresAtUtc,
            snapshot.KillSwitch,
            snapshot.BotSevenTvUserId,
            snapshot.IdentityVersion,
            Now());

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
}
