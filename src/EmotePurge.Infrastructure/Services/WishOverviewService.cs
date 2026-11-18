using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The cross-channel wish overview and the header counter (plan T6, spec E18). Both reads start with one
/// index-backed query over <c>Wishes</c> for the channels that have a wish needing action (or, for the list, a
/// wish in probation) and ask <see cref="IChannelAccessService.CanManageChannelAsync"/> for those channels
/// only, one after the other: the moderated-channel list behind it is cached per user, so the first check may
/// paginate Helix and every later one, for this call and for the next minutes, reads Redis. This service never
/// touches <c>IMyChannelsService</c> and keeps no cache of its own.
/// </summary>
public sealed class WishOverviewService(
    AppDbContext db,
    IChannelAccessService channelAccess,
    TimeProvider timeProvider) : IWishOverviewService
{
    private static readonly string[] ProbationStatuses = [WishStatus.Active];

    public async Task<IReadOnlyList<WishOverviewItemDto>> ListMineAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var candidates = await LoadCandidatesAsync(includeProbation: true, cancellationToken);
        var manageable = await ManageableChannelsAsync(principal, candidates, cancellationToken);
        if (manageable.Count == 0)
        {
            return [];
        }

        // The reload keeps the authorized channel scope: a channel merge that commits during the role checks
        // moves a selected wish to a channel the caller was never checked for, and that wish drops out.
        var ids = candidates.Where(c => manageable.ContainsKey(c.ChannelId)).Select(c => c.WishId).ToList();
        var channelIds = manageable.Keys.ToList();
        var rows = await db.Wishes.AsNoTracking()
            .Where(w => ids.Contains(w.Id) && channelIds.Contains(w.ChannelId))
            .ToListAsync(cancellationToken);
        return rows
            .OrderBy(w => w.ExpiresAtUtc is null)
            .ThenBy(w => w.ExpiresAtUtc)
            .ThenBy(w => w.CreatedAtUtc)
            .ThenBy(w => w.Id)
            .Select(w =>
            {
                var channel = manageable[w.ChannelId];
                return new WishOverviewItemDto(channel.Name, WishService.ToDto(w, channel.Settings));
            })
            .ToList();
    }

    public async Task<int> CountActionAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // Probation alone is no action, so a channel with nothing else needs no role check for the counter.
        var candidates = await LoadCandidatesAsync(includeProbation: false, cancellationToken);
        var manageable = await ManageableChannelsAsync(principal, candidates, cancellationToken);
        return candidates.Count(c => manageable.ContainsKey(c.ChannelId));
    }

    // One query on the wish table narrows to what could qualify (a status that may need action, or an active
    // wish that has started and expires within the longest possible lead); the per-channel settings then decide
    // in memory: "approved" needs action only while the channel runs manually, and the probation start depends
    // on the channel's lead. No candidate: no further query and no role check.
    private async Task<List<Candidate>> LoadCandidatesAsync(bool includeProbation, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var maxLead = now.AddDays(WishLimits.ProbationLeadDaysMax);
        string[] actionStatuses = [WishStatus.Pending, WishStatus.Failed, WishStatus.Presented, WishStatus.Approved];

        var rows = await db.Wishes.AsNoTracking()
            .Where(w => actionStatuses.Contains(w.Status)
                || (includeProbation && w.Status == WishStatus.Active && w.StartsAtUtc != null && w.StartsAtUtc <= now
                    && w.ExpiresAtUtc != null && w.ExpiresAtUtc <= maxLead))
            .Select(w => new { w.Id, w.ChannelId, w.Status, w.StartsAtUtc, w.ExpiresAtUtc })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var channelIds = rows.Select(r => r.ChannelId).Distinct().ToList();
        var settings = await db.ChannelWishSettings.AsNoTracking()
            .Where(s => channelIds.Contains(s.ChannelId))
            .ToDictionaryAsync(s => s.ChannelId, WishService.ToSettings, StringComparer.Ordinal, cancellationToken);

        var candidates = new List<Candidate>();
        foreach (var row in rows)
        {
            var effective = settings.GetValueOrDefault(row.ChannelId) ?? WishService.EffectiveSettings.Defaults;
            var needsAction = WishService.StatusesOf(WishListView.Open, effective.IsBotMode).Contains(row.Status);
            var inProbation = includeProbation
                && ProbationStatuses.Contains(row.Status)
                && row.StartsAtUtc is { } start
                && row.ExpiresAtUtc is { } expires
                && now >= WishSchedule.ProbationStartsAt(start, expires, effective.ProbationLeadDays);
            if (needsAction || inProbation)
            {
                candidates.Add(new Candidate(row.Id, row.ChannelId, needsAction, effective));
            }
        }

        return includeProbation ? candidates : [.. candidates.Where(c => c.NeedsAction)];
    }

    private async Task<Dictionary<string, ManageableChannel>> ManageableChannelsAsync(
        TwitchPrincipalInfo principal, List<Candidate> candidates, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ManageableChannel>(StringComparer.Ordinal);
        if (candidates.Count == 0)
        {
            return result;
        }

        var channelIds = candidates.Select(c => c.ChannelId).Distinct().ToList();
        var names = await db.Channels.AsNoTracking()
            .Where(c => channelIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ChannelName })
            .ToListAsync(cancellationToken);

        foreach (var channel in names.OrderBy(n => n.ChannelName, StringComparer.Ordinal))
        {
            if (await channelAccess.CanManageChannelAsync(principal, channel.ChannelName, cancellationToken))
            {
                result[channel.Id] = new ManageableChannel(channel.ChannelName, candidates.First(c => c.ChannelId == channel.Id).Settings);
            }
        }

        return result;
    }

    private sealed record Candidate(long WishId, string ChannelId, bool NeedsAction, WishService.EffectiveSettings Settings);

    private sealed record ManageableChannel(string Name, WishService.EffectiveSettings Settings);
}
