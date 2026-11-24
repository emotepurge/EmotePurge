using System.Security.Claims;
using EmotePurge.Api.Auth;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// The bot account's admin routes (#487, plan 2.10, spec K4), mapped onto the <c>/api/admin</c> group, so the group's
/// authorization and <c>GlobalAdminAuthorizationFilter</c> run first and no rate limit applies. With
/// <c>BotAccount:Enabled</c> off only <c>PUT /token</c> answers 404 <c>bot_account_disabled</c> (after the filter); the
/// read, the token delete and the kill switch stay reachable, because they are the emergency stop and the way back
/// (plan 2.10, Revision 4). No route ever returns the token or the seal: the read is built field by field below.
/// </summary>
public static class AdminBotAccountEndpoints
{
    public static void MapAdminBotAccountEndpoints(this RouteGroupBuilder group)
    {
        var botAccount = group.MapGroup("/bot-account");

        botAccount.MapGet("", async (IBotAccountService accounts, IWishBotService wishBot, CancellationToken ct) =>
        {
            var state = await accounts.GetAsync(ct);
            var wishes = await wishBot.GetAdminSummaryAsync(ct);
            return Results.Ok(new
            {
                tokenPresent = state.TokenPresent,
                status = state.Status,
                expiresAtUtc = state.ExpiresAtUtc,
                expiresInDays = state.ExpiresInDays,
                botSevenTvUserId = state.BotSevenTvUserId,
                lastVerifiedAtUtc = state.LastVerifiedAtUtc,
                lastErrorCode = state.LastErrorCode,
                killSwitch = state.KillSwitch,
                pausedUntilUtc = state.PausedUntilUtc,
                wishes = new
                {
                    queue = new
                    {
                        adds = wishes.Queue.Adds,
                        confirms = wishes.Queue.Confirms,
                        removals = wishes.Queue.Removals,
                    },
                    botChannels = wishes.BotChannels,
                    suspendedChannels = wishes.SuspendedChannels,
                    openRepairChecks = wishes.OpenRepairChecks,
                },
            });
        });

        // Nullable bodies, so the switch is decided before a missing body: with the bot account off every PUT /token is
        // the same 404, whatever it carried.
        botAccount.MapPut("/token", async (
            BotAccountTokenRequest? body,
            ClaimsPrincipal principal,
            BotAccountOptions options,
            IBotAccountService accounts,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound(new { errorCode = ApiErrorCodes.BotAccountDisabled });
            }

            if (body is null)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidRequestBody });
            }

            var actor = principal.TryBuildAuditActor();
            if (actor is null)
            {
                // Unreachable behind RequireAuthorization + the admin filter; guard, not a case.
                return Results.Unauthorized();
            }

            return await accounts.SetTokenAsync(body.Token, actor, ct) switch
            {
                BotAccountTokenSetOutcome.Set => Results.NoContent(),
                BotAccountTokenSetOutcome.Disabled => Results.NotFound(new { errorCode = ApiErrorCodes.BotAccountDisabled }),
                BotAccountTokenSetOutcome.Malformed => Results.BadRequest(new { errorCode = ApiErrorCodes.BotAccountTokenMalformed }),
                BotAccountTokenSetOutcome.Expired => Results.BadRequest(new { errorCode = ApiErrorCodes.BotAccountTokenExpired }),
                _ => Results.Problem(),
            };
        });

        botAccount.MapDelete("/token", async (ClaimsPrincipal principal, IBotAccountService accounts, CancellationToken ct) =>
        {
            var actor = principal.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            // Idempotent: no stored token is a 204 as well, without an audit entry.
            await accounts.DeleteTokenAsync(actor, ct);
            return Results.NoContent();
        });

        botAccount.MapPut("/kill-switch", async (
            BotAccountKillSwitchRequest? body,
            ClaimsPrincipal principal,
            IBotAccountService accounts,
            CancellationToken ct) =>
        {
            if (body?.Enabled is not { } enabled)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidRequestBody });
            }

            var actor = principal.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            // The same value again is a 204 too; the service writes and audits only a change.
            await accounts.SetKillSwitchAsync(enabled, actor, ct);
            return Results.NoContent();
        });
    }
}

/// <summary>
/// Body of <c>PUT /api/admin/bot-account/token</c>. A class rather than a record, so there is no generated
/// <c>ToString</c> that would print the token if the model ever ended up in a log line.
/// </summary>
internal sealed class BotAccountTokenRequest
{
    public string? Token { get; init; }

    public override string ToString() => nameof(BotAccountTokenRequest);
}

/// <summary>Body of <c>PUT /api/admin/bot-account/kill-switch</c>; a missing <c>enabled</c> is a 400.</summary>
internal sealed record BotAccountKillSwitchRequest(bool? Enabled);
