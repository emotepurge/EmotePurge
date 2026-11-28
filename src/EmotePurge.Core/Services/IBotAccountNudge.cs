namespace EmotePurge.Core.Services;

/// <summary>
/// Nudges the worker's bot account loop with <c>BOTACCOUNT:</c> (no payload) after a committed write the bot should act
/// on soon: an activation request, a queued bot removal, a recheck (#487 plan 2.9). An accelerator only: a Redis outage
/// never fails the caller, whose row is committed already; the worker's 30-second pass converges anyway.
/// </summary>
public interface IBotAccountNudge
{
    Task NudgeAsync(CancellationToken cancellationToken = default);
}
