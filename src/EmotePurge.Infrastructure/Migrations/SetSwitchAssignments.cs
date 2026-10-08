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
            // HandOfBlood's switch was detected by the dev worker on 2026-10-07 at 19:57:33 UTC
            // (7TV itself may have switched up to ~60 s earlier), not on 2026-10-01 as planned.
            // Rows strictly before this date go to the old set; the boundary day itself goes to
            // the new one. The switch fell late in the UTC day, so 2026-10-07 lands wholly on one
            // side either way; counted on the dev stack, that day holds 42 uses for the old set
            // and 9 for the new one. Naming 2026-10-08 misattributes 9 uses, naming 2026-10-07
            // would misattribute 42. The operator named 2026-10-08 on 2026-10-08 — deliberately
            // the day after the switch, not "the day of the switch" as the spec words it.
            BoundaryUtc: new DateOnly(2026, 10, 8)),
    ];
}
