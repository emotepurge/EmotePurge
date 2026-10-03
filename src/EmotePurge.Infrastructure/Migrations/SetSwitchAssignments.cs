namespace EmotePurge.Infrastructure.Migrations;

// The input of the AddUsageStatEmoteSetId migration (#200, spec 4.2 and E1): a list of *switches*,
// not a directory of channels. A channel that does not appear here gets no entry at all, and its
// UsageStats rows are backfilled with its current Channels."ActiveEmoteSetId". That is a default,
// not a statement by the operator — spec 4.2 names in plain words what it leaves unprotected, and
// nothing here is allowed to grow a safety net that section deliberately cut.
//
// Ordinary committed source code: no partial method, no loader, no marker, no .example, no
// .gitignore or .dockerignore entry. The file travels with every clone, worktree and CI checkout,
// a typo in it is either a compile error or an abort at one of the migration's three checks, and
// the one hand-written line is readable in the PR diff before it costs anything.
//
// The migration reads this list and nothing else. The pure check/assignment functions in
// UsageStatMigrationChecks take the list as a parameter, and the migration tests build their cases
// through the database (a channel carrying the entry's TwitchChannelId) — no test changes this
// constant.
internal static class SetSwitchAssignments
{
    // ---------------------------------------------------------------------------------------
    // PLACEHOLDER. THIS IS NOT THE REAL SWITCH DATE AND MUST NOT BE MIGRATED WITH.
    //
    // HandOfBlood switches his active emote set on 2026-10-01; the operator names the actual day
    // once it has happened, and a separate chore: commit replaces this value — before the
    // migration probe (T1.10) and before the maintenance window. Until then the entry below reads
    // "BoundaryUtc: BoundaryDateNotYetNamedByTheOperator", which is the whole point: the line
    // cannot be mistaken for a date anybody measured.
    //
    // The value is 1900-01-01 rather than something plausible so that a run against real data
    // cannot quietly do the wrong thing: it lies outside any UsageStats range this project will
    // ever hold, so the migration's third check aborts and names the channel, the boundary and the
    // range. That is the existing check doing its job, not an extra safeguard.
    // ---------------------------------------------------------------------------------------
    internal static readonly DateOnly BoundaryDateNotYetNamedByTheOperator = new(1900, 1, 1);

    // Exactly one entry, per spec 4.2 / E1 / Nachtrag 31. Both set ids are HandOfBlood's own and
    // publicly visible on 7TV; they were measured on 2026-09-20 (Sonde 1, spec section 11):
    // "HandOfBlood's Emotes" (active until the switch) and "Halloween Set" (active after it).
    //
    // A second entry for the *same* channel is an inadmissible input, not a supported case: the
    // two-stage backfill in 4.2 models exactly one boundary per channel and would silently lose the
    // middle set. The migration's first check rejects it rather than trusting this comment. Whoever
    // ever extends this list changes the backfill rule first.
    internal static readonly IReadOnlyList<SetSwitchAssignment> Entries =
    [
        new SetSwitchAssignment(
            TwitchChannelId: "49140130",
            OldEmoteSetId: "01GV88A38G0006FW5TVZVMG507",
            NewEmoteSetId: "01J94NYQR0000D15QN0BDGN85E",
            BoundaryUtc: BoundaryDateNotYetNamedByTheOperator),
    ];
}
