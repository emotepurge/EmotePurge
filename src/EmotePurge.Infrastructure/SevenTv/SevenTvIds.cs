namespace EmotePurge.Infrastructure.SevenTv;

internal static class SevenTvIds
{
    // 7TV represents "no id" two different ways depending on the endpoint: sometimes a genuine
    // absence (null/empty), sometimes a placeholder sentinel of all-zero characters — proven live for
    // a different lookup on this same client (ResolveSevenTvIdentityAsync's
    // "00000000000000000000000000" placeholder account id, measured 2026-08-31). Both must read as
    // "not present" here, or a sentinel would be mistaken for a real emote-set id and forwarded to
    // GET emote-sets/{id}. Checking "every character is '0'" rather than a fixed-length literal
    // survives 7TV changing the sentinel's length or format.
    //
    // For this particular field the sentinel has not been observed: 62 accounts without an active
    // set, sampled live 2026-09-01, all answered with a plain null emote_set_id (top level and in
    // connections[]). The all-zero branch is therefore unproven defence, not a fix for a known
    // behaviour — the null case, which keeps NoActiveEmoteSet reachable after the rollout, is the
    // measured one.
    internal static bool IsUsable(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Any(c => c != '0');
}
