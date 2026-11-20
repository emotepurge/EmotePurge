using EmotePurge.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Emote> Emotes => Set<Emote>();
    public DbSet<EmoteSlot> EmoteSlots => Set<EmoteSlot>();
    public DbSet<UsageStat> UsageStats => Set<UsageStat>();
    public DbSet<ChannelLiveDay> ChannelLiveDays => Set<ChannelLiveDay>();
    public DbSet<BroadcasterChannelLock> BroadcasterChannelLocks => Set<BroadcasterChannelLock>();
    public DbSet<ChannelEmoteSetObservation> ChannelEmoteSetObservations => Set<ChannelEmoteSetObservation>();
    public DbSet<EmoteTag> EmoteTags => Set<EmoteTag>();
    public DbSet<EmoteTagEntry> EmoteTagEntries => Set<EmoteTagEntry>();
    public DbSet<EmoteTagPlacement> EmoteTagPlacements => Set<EmoteTagPlacement>();
    public DbSet<EmoteTagActivation> EmoteTagActivations => Set<EmoteTagActivation>();
    public DbSet<EmoteTagOperation> EmoteTagOperations => Set<EmoteTagOperation>();
    public DbSet<EmoteSetLeaveObservation> EmoteSetLeaveObservations => Set<EmoteSetLeaveObservation>();
    public DbSet<User> Users => Set<User>();
    public DbSet<VoteSession> VoteSessions => Set<VoteSession>();
    public DbSet<VoteSessionEmote> VoteSessionEmotes => Set<VoteSessionEmote>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();
    public DbSet<ChatLogBackfillRun> ChatLogBackfillRuns => Set<ChatLogBackfillRun>();
    public DbSet<ChatLogBackfillRunEmote> ChatLogBackfillRunEmotes => Set<ChatLogBackfillRunEmote>();
    public DbSet<ChatLogBackfillCoverageDay> ChatLogBackfillCoverage => Set<ChatLogBackfillCoverageDay>();
    public DbSet<ChatLogBackfillProviderState> ChatLogBackfillProviderState => Set<ChatLogBackfillProviderState>();
    public DbSet<SystemLoadSample> SystemLoadSamples => Set<SystemLoadSample>();
    public DbSet<SystemLoadHourly> SystemLoadHourlies => Set<SystemLoadHourly>();
    public DbSet<LogEntry> LogEntries => Set<LogEntry>();
    public DbSet<LogCaptureStatus> LogCaptureStatuses => Set<LogCaptureStatus>();
    public DbSet<LogFilter> LogFilters => Set<LogFilter>();
    public DbSet<Wish> Wishes => Set<Wish>();
    public DbSet<ChannelWishSettings> ChannelWishSettings => Set<ChannelWishSettings>();
    public DbSet<ChannelWishReward> ChannelWishRewards => Set<ChannelWishReward>();
    public DbSet<WishRewardLearning> WishRewardLearnings => Set<WishRewardLearning>();
    public DbSet<WishCaptureGap> WishCaptureGaps => Set<WishCaptureGap>();
    public DbSet<WishCaptureState> WishCaptureState => Set<WishCaptureState>();
    public DbSet<WishSlotExclusion> WishSlotExclusions => Set<WishSlotExclusion>();
    public DbSet<WishCaptureReceipt> WishCaptureReceipts => Set<WishCaptureReceipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Channel>(entity =>
        {
            entity.HasIndex(c => c.ChannelName).IsUnique();
            entity.HasIndex(c => c.TwitchChannelId).IsUnique();
        });

        modelBuilder.Entity<Emote>(entity =>
        {
            // The same 7TV emote can be active in more than one channel, so
            // uniqueness only holds per channel, not globally.
            entity.HasIndex(e => new { e.ChannelId, e.SevenTvEmoteId }).IsUnique();

            // A database default as well as the CLR one: an image older than this column keeps
            // inserting emote rows without naming it while the migration is already applied (the
            // window between a manual prod migration and the redeploy), and those must land on false.
            entity.Property(e => e.IsPlaceholder).HasDefaultValue(false);

            entity.HasOne(e => e.Channel)
                .WithMany(c => c.Emotes)
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmoteSlot>(entity =>
        {
            // Identity of an authoritative slot (Name only breaks a tie on the same date). The slot
            // sync's conflict detection refers to this index by name.
            entity.HasIndex(s => new { s.EmoteId, s.AddedAt, s.Name })
                .HasDatabaseName(EmoteSlotKeys.AuthoritativeIndexName)
                .IsUnique()
                .HasFilter("\"AddedAtFromRest\"");

            // FK index, and the name binding of provisional slots. Deliberately not unique: 7TV
            // swaps two aliases within one reconcile.
            entity.HasIndex(s => new { s.EmoteId, s.Name })
                .HasDatabaseName("IX_EmoteSlots_EmoteId_Name");

            // Database defaults like Emote.IsPlaceholder: an image that does not name the column
            // lands on false.
            entity.Property(s => s.AddedAtFromRest).HasDefaultValue(false);
            entity.Property(s => s.IsArchived).HasDefaultValue(false);
            entity.Property(s => s.SeedUnsettled).HasDefaultValue(false);

            entity.HasOne(s => s.Emote)
                .WithMany(e => e.Slots)
                .HasForeignKey(s => s.EmoteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UsageStat>(entity =>
        {
            // Emote-wide covering index (UseCount included), so range-sum queries over (EmoteId,
            // EmoteSetId, Date) — and, with the EmoteId-only prefix, over (EmoteId, Date) for
            // set-agnostic reads — can still be answered as an index-only scan. Unique from #200 until
            // M2 (SwitchUsageStatsToSlotKey, #373); since then two slots of one emote can each have a
            // row on the same day, and the key is the slot index below (D18). Kept, non-unique, for
            // the emote readers and the seed constant's plans.
            entity.HasIndex(u => new { u.EmoteId, u.EmoteSetId, u.Date })
                .IncludeProperties(u => u.UseCount);

            // Provenance of the row (#346). The default is what the live flush gets without naming
            // the column; the backfill writes ChatLogArchive explicitly.
            entity.Property(u => u.Source).HasDefaultValue(UsageStatSource.Live);

            entity.HasOne(u => u.Emote)
                .WithMany(e => e.UsageStats)
                .HasForeignKey(u => u.EmoteId)
                .OnDelete(DeleteBehavior.Cascade);

            // The row's key since M2 (D18): one row per slot per emote set per UTC day, and the
            // flush's ON CONFLICT target. Covering like the emote index.
            entity.HasIndex(u => new { u.SlotId, u.EmoteSetId, u.Date })
                .IsUnique()
                .IncludeProperties(u => u.UseCount);

            // No navigation on either side: nothing reads one yet.
            entity.HasOne<EmoteSlot>()
                .WithMany()
                .HasForeignKey(u => u.SlotId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChannelLiveDay>(entity =>
        {
            // One coverage row per channel per UTC day; covering index like UsageStat's, so the
            // live-day range query of the drilldown series is an index-only scan.
            entity.HasIndex(d => new { d.ChannelId, d.Date })
                .IsUnique()
                .IncludeProperties(d => d.LiveMinutes);

            // No inverse collection on Channel — nothing navigates from a channel to its coverage
            // rows; both consumers query by (ChannelId, Date) directly.
            entity.HasOne(d => d.Channel)
                .WithMany()
                .HasForeignKey(d => d.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BroadcasterChannelLock>(entity =>
        {
            // Keyed by the immutable Twitch broadcaster id; no FK to Channel, because the purge that
            // writes the lock deletes the channel row.
            entity.HasKey(l => l.TwitchChannelId);
        });

        modelBuilder.Entity<ChannelEmoteSetObservation>(entity =>
        {
            // Access pattern is "this channel's intervals, newest first" (open-interval lookup,
            // history for the "observed during this set" preset in spec 8.5).
            entity.HasIndex(o => new { o.ChannelId, o.ObservedFromUtc });

            // Invariant of the observation log (spec section 4.3): at most one open interval per
            // channel at a time. A partial index — rather than application-level locking — makes a
            // second concurrent "open" a unique-violation instead of a race two writers could both
            // win.
            entity.HasIndex(o => o.ChannelId)
                .IsUnique()
                .HasFilter("\"ObservedToUtc\" IS NULL");

            // No inverse collection on Channel, same as ChannelLiveDay above — nothing navigates
            // from a channel to its observations; every reader queries this table directly.
            entity.HasOne(o => o.Channel)
                .WithMany()
                .HasForeignKey(o => o.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmoteTag>(entity =>
        {
            entity.Property(t => t.Name).HasMaxLength(EmoteTagName.MaxLength);
            entity.Property(t => t.NormalizedName).HasMaxLength(EmoteTagName.MaxLength);

            // "Funny" and "funny" are one tag per channel.
            entity.HasIndex(t => new { t.ChannelId, t.NormalizedName }).IsUnique();
            // The tag list reads "this channel's tags in creation order".
            entity.HasIndex(t => new { t.ChannelId, t.CreatedAtUtc });

            // No inverse collection on Channel, same as ChannelEmoteSetObservation above.
            entity.HasOne(t => t.Channel)
                .WithMany()
                .HasForeignKey(t => t.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmoteTagEntry>(entity =>
        {
            // Pure join-style table: the natural key is the primary key.
            entity.HasKey(e => new { e.TagId, e.SevenTvEmoteId });
            entity.Property(e => e.SevenTvEmoteId).HasMaxLength(32);

            // Deliberately no FK to Emote: a tag outlives the emote row (E22, rule 8).
            entity.HasOne(e => e.Tag)
                .WithMany()
                .HasForeignKey(e => e.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmoteTagPlacement>(entity =>
        {
            entity.HasKey(p => new { p.TagId, p.SevenTvEmoteId, p.SevenTvEmoteSetId });
            // 7TV ids (emote and set) are 26-character ULIDs; 32 matches SevenTvEmoteIdValidation.
            entity.Property(p => p.SevenTvEmoteId).HasMaxLength(32);
            entity.Property(p => p.SevenTvEmoteSetId).HasMaxLength(32);

            // "Who holds X in S" and the read-time rule against the leave observations.
            entity.HasIndex(p => new { p.SevenTvEmoteSetId, p.SevenTvEmoteId });

            entity.HasOne(p => p.Tag)
                .WithMany()
                .HasForeignKey(p => p.TagId)
                .OnDelete(DeleteBehavior.Cascade);

            // The database upholds "no placement without an entry" (and takes the placements in every
            // set along when an entry is removed) independent of any caller's locking. Two cascade
            // paths from the tag (direct, and via the entry) are fine in Postgres. No navigation from
            // the entry. Deliberately no FK to EmoteTagOperation: it would force a delete order the
            // sweep does not need. OperationId is provenance only; validity is anchored on the
            // placement's own RegisteredAtUtc, because operations cascade with their tag while a
            // transfer points another tag's placement at the removed tag's operation.
            entity.HasOne<EmoteTagEntry>()
                .WithMany()
                .HasForeignKey(p => new { p.TagId, p.SevenTvEmoteId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmoteTagActivation>(entity =>
        {
            entity.HasKey(a => new { a.TagId, a.SevenTvEmoteSetId });
            entity.Property(a => a.SevenTvEmoteSetId).HasMaxLength(32);

            entity.HasOne(a => a.Tag)
                .WithMany()
                .HasForeignKey(a => a.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmoteTagOperation>(entity =>
        {
            entity.HasKey(o => o.OperationId);
            // The id comes from the client; a Guid.Empty must not be silently replaced by a generated one.
            entity.Property(o => o.OperationId).ValueGeneratedNever();
            entity.Property(o => o.Kind).HasMaxLength(16);
            entity.Property(o => o.SevenTvEmoteSetId).HasMaxLength(32);

            entity.HasIndex(o => new { o.TagId, o.SevenTvEmoteSetId });

            entity.HasOne(o => o.Tag)
                .WithMany()
                .HasForeignKey(o => o.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmoteSetLeaveObservation>(entity =>
        {
            entity.HasKey(o => new { o.ChannelId, o.SevenTvEmoteId, o.SevenTvEmoteSetId });
            entity.Property(o => o.SevenTvEmoteId).HasMaxLength(32);
            entity.Property(o => o.SevenTvEmoteSetId).HasMaxLength(32);

            // A channel-level fact, not a tag table: it must exist before any tag report arrives and
            // falls with the channel's purge. No inverse collection on Channel, and no FK to Emote.
            entity.HasOne(o => o.Channel)
                .WithMany()
                .HasForeignKey(o => o.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.TwitchUsername).IsUnique();
        });

        modelBuilder.Entity<VoteSession>(entity =>
        {
            entity.HasIndex(s => new { s.ChannelId, s.IsActive });

            entity.HasOne(s => s.Channel)
                .WithMany()
                .HasForeignKey(s => s.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VoteSessionEmote>(entity =>
        {
            // Pure join table, so the natural key is the primary key — no surrogate id.
            entity.HasKey(se => new { se.VoteSessionId, se.EmoteId });

            entity.HasOne(se => se.VoteSession)
                .WithMany(s => s.SessionEmotes)
                .HasForeignKey(se => se.VoteSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Cascade like Vote→Emote: emotes are only soft-archived today, so this fires solely on
            // a hard delete, where dangling ballot rows would be meaningless anyway.
            entity.HasOne(se => se.Emote)
                .WithMany()
                .HasForeignKey(se => se.EmoteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Vote>(entity =>
        {
            // One vote per user per emote per session — a repeat vote updates the existing row.
            entity.HasIndex(v => new { v.VoteSessionId, v.EmoteId, v.UserId }).IsUnique();

            entity.HasOne(v => v.VoteSession)
                .WithMany(s => s.Votes)
                .HasForeignKey(v => v.VoteSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(v => v.Emote)
                .WithMany()
                .HasForeignKey(v => v.EmoteId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not Cascade: a user row must never take votes with it as a side effect. The one
            // path that deletes users (AccountDeletionService) deletes their votes explicitly first.
            entity.HasOne(v => v.User)
                .WithMany()
                .HasForeignKey(v => v.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            // No relationships at all, on purpose: actor and channel are snapshot strings, never FKs
            // (see AuditLogEntry's remarks — a purge deletes its own channel row in the transaction
            // that records the purge). Lengths are declared here because the columns hold external
            // identifiers with known bounds (a Twitch login is at most 25 characters), unlike the
            // free-text columns elsewhere in this model.
            entity.Property(e => e.Action).HasMaxLength(64);
            entity.Property(e => e.ActorTwitchUserId).HasMaxLength(64);
            entity.Property(e => e.ActorLogin).HasMaxLength(64);
            entity.Property(e => e.ChannelName).HasMaxLength(25);
            entity.Property(e => e.TargetType).HasMaxLength(32);
            entity.Property(e => e.TargetId).HasMaxLength(64);

            // jsonb rather than text: the payload is JSON, and storing it as such keeps a later
            // filter on a details field ("which purge removed more than 500 emotes?") a query
            // instead of a migration.
            entity.Property(e => e.DetailsJson).HasColumnType("jsonb");

            // The unfiltered access pattern is "newest first, paged" — a descending index answers
            // that ordering directly.
            entity.HasIndex(e => e.OccurredAtUtc).IsDescending();

            // The channel filter of the admin audit-log UI matches exactly on the normalized name,
            // so this compound index serves both the filter and its "newest first" ordering. The
            // action and actor filters deliberately have no index: action has a handful of distinct
            // values, and the actor filter is a substring match no btree could serve anyway.
            entity.HasIndex(e => new { e.ChannelName, e.OccurredAtUtc }).IsDescending(false, true);
        });

        modelBuilder.Entity<ChatLogBackfillRun>(entity =>
        {
            // Stored as the lowercase name; the CHECK keeps a typo from ever becoming a state.
            entity.Property(r => r.Status).HasConversion(
                status => status.ToString().ToLowerInvariant(),
                text => Enum.Parse<ChatLogBackfillRunStatus>(text, true));

            entity.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_ChatLogBackfillRuns_Status",
                    "\"Status\" IN ('queued', 'running', 'paused', 'completed', 'failed', 'cancelled')");
                t.HasCheckConstraint("CK_ChatLogBackfillRuns_RequestedMonths", "\"RequestedMonths\" IN (1, 3, 6)");
                t.HasCheckConstraint("CK_ChatLogBackfillRuns_Window", "\"WindowFrom\" < \"WindowTo\"");
            });

            // The counters start at 0 in the database as well as in C# (spec section 1).
            entity.Property(r => r.WeeksDone).HasDefaultValue(0);
            entity.Property(r => r.PauseCount).HasDefaultValue(0);
            entity.Property(r => r.BlockAttempts).HasDefaultValue(0);
            entity.Property(r => r.BytesReceived).HasDefaultValue(0L);
            entity.Property(r => r.MessagesRead).HasDefaultValue(0L);

            // At most one active run per channel, enforced by the database rather than only by the
            // Api's pre-check (the index is the last word in a race).
            entity.HasIndex(r => r.ChannelId)
                .IsUnique()
                .HasDatabaseName("IX_ChatLogBackfillRuns_ChannelId_Active")
                .HasFilter("\"Status\" IN ('queued', 'running', 'paused')");

            // The worker's "next queued" and "paused whose timer passed" scans.
            entity.HasIndex(r => new { r.Status, r.Id });

            // Account-deletion pseudonymisation looks runs up by requester.
            entity.HasIndex(r => r.RequestedByTwitchUserId);

            entity.HasOne(r => r.Channel)
                .WithMany()
                .HasForeignKey(r => r.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatLogBackfillRunEmote>(entity =>
        {
            // One row per slot since M3 (#375); EmoteId may repeat within a run.
            entity.HasKey(e => new { e.RunId, e.SlotId });
            entity.Property(e => e.CreatedRow).HasDefaultValue(false);
            entity.Property(e => e.CreatedSlot).HasDefaultValue(false);
            entity.HasIndex(e => new { e.RunId, e.SevenTvEmoteId });

            // No FK to Emotes on purpose (see the entity's remarks); only the run owns these rows.
            entity.HasOne(e => e.Run)
                .WithMany(r => r.Emotes)
                .HasForeignKey(e => e.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatLogBackfillCoverageDay>(entity =>
        {
            entity.ToTable("ChatLogBackfillCoverage");
            entity.HasKey(d => new { d.ChannelId, d.Day });

            entity.HasOne(d => d.Channel)
                .WithMany()
                .HasForeignKey(d => d.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            // The coverage outlives the run row's retention deletion.
            entity.HasOne<ChatLogBackfillRun>()
                .WithMany()
                .HasForeignKey(d => d.RunId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ChatLogBackfillProviderState>(entity =>
        {
            // Exactly one row; the migration seeds it. Never generated: the id is the constant 1.
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.ToTable(t => t.HasCheckConstraint("CK_ChatLogBackfillProviderState_SingleRow", "\"Id\" = 1"));
        });

        modelBuilder.Entity<SystemLoadSample>(entity =>
        {
            entity.ToTable("SystemLoadSamples");
            entity.Property(sample => sample.Source).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(sample => new { sample.Source, sample.SampledAtUtc });
            entity.HasIndex(sample => new { sample.Source, sample.ProcessInstanceId, sample.SampledAtUtc });
        });

        modelBuilder.Entity<SystemLoadHourly>(entity =>
        {
            entity.ToTable("SystemLoadHourly");
            entity.HasKey(hourly => new { hourly.Source, hourly.HourUtc });
            entity.Property(hourly => hourly.Source).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<LogEntry>(entity =>
        {
            entity.ToTable("LogEntries");
            entity.Property(e => e.Source).HasConversion<short>();
            entity.Property(e => e.Level).HasConversion<short>();
            entity.Property(e => e.InstanceId).HasMaxLength(8);
            entity.Property(e => e.Category).HasMaxLength(256);
            entity.Property(e => e.EventName).HasMaxLength(128);
            entity.Property(e => e.ExceptionType).HasMaxLength(256);
            entity.Property(e => e.Properties).HasColumnType("jsonb");
            entity.HasIndex(e => new { e.TimestampUtc, e.Id }).IsDescending(true, true);
            entity.HasIndex(e => e.CaptureId).IsUnique();
            entity.HasIndex(e => e.TimestampUtc).HasFilter("\"Level\" >= 4").HasDatabaseName("IX_LogEntries_TimestampUtc_Errors");
            entity.HasIndex(e => new { e.Source, e.ProcessStartedUtc }).IsDescending(false, true);
        });

        modelBuilder.Entity<LogCaptureStatus>(entity =>
        {
            entity.ToTable("LogCaptureStatus");
            entity.HasKey(s => new { s.Source, s.InstanceId });
            entity.Property(s => s.Source).HasConversion<short>();
            entity.Property(s => s.InstanceId).HasMaxLength(8);
        });

        modelBuilder.Entity<LogFilter>(entity =>
        {
            entity.ToTable("LogFilters");
            entity.Property(f => f.Name).HasMaxLength(60);
            entity.Property(f => f.NameKey).HasMaxLength(60);
            entity.Property(f => f.Filter).HasMaxLength(512);
            entity.HasIndex(f => f.NameKey).IsUnique();
        });

        modelBuilder.Entity<Wish>(entity =>
        {
            entity.Property(w => w.SevenTvEmoteId).HasMaxLength(WishLimits.SevenTvEmoteIdMaxLength);
            entity.Property(w => w.EmoteName).HasMaxLength(WishLimits.EmoteNameMaxLength);
            entity.Property(w => w.Alias).HasMaxLength(WishLimits.AliasMaxLength);
            entity.Property(w => w.RequesterLogin).HasMaxLength(WishLimits.RequesterLoginMaxLength);
            entity.Property(w => w.IrcMessageId).HasMaxLength(WishLimits.IrcMessageIdMaxLength);
            entity.Property(w => w.RequestText).HasMaxLength(WishLimits.RequestTextMaxLength);
            entity.Property(w => w.RequestedName).HasMaxLength(WishLimits.RequestedNameMaxLength);

            entity.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_Wishes_DurationDays",
                    $"\"DurationDays\" BETWEEN {WishLimits.DurationDaysMin} AND {WishLimits.DurationDaysMax}");

                // K6 (plan #486 2.8a): an open wish without an emote keeps the three columns NOT NULL and carries
                // the empty string instead, so an image older than K6 never reads a null there. Only the states
                // that may lack an emote may hold the placeholder; every other state has all three.
                t.HasCheckConstraint(
                    "CK_Wishes_EmotePlaceholder",
                    $"\"Status\" IN ({SqlList(WishStatus.All.Where(WishStatus.EmotePlaceholderAllowed.Contains))}) "
                    + "OR (\"SevenTvEmoteId\" <> '' AND \"EmoteName\" <> '' AND \"Alias\" <> '')");
                t.HasCheckConstraint(
                    "CK_Wishes_MatchKind",
                    $"\"MatchKind\" IS NULL OR \"MatchKind\" IN ({SqlList(WishMatchKind.All)})");
            });

            entity.HasIndex(w => new { w.ChannelId, w.Status });
            entity.HasIndex(w => new { w.Status, w.ExpiresAtUtc });

            // E13: at most one running wish per channel and 7TV emote id; the index is the last word in a race.
            entity.HasIndex(w => new { w.ChannelId, w.SevenTvEmoteId })
                .IsUnique()
                .HasDatabaseName("IX_Wishes_ChannelId_SevenTvEmoteId_Running")
                .HasFilter(
                    $"\"Status\" IN ('{WishStatus.Approved}', '{WishStatus.Unconfirmed}', '{WishStatus.Active}', '{WishStatus.Presented}')");

            // One slot-holding wish per slot: a live one, or a kept one, which keeps its entry for good
            // (operator ruling 2026-11-18). Other ended wishes keep their SlotId without competing for it.
            entity.HasIndex(w => w.SlotId)
                .IsUnique()
                .HasDatabaseName("IX_Wishes_SlotId_Live")
                .HasFilter($"\"SlotId\" IS NOT NULL AND \"Status\" IN ('{WishStatus.Active}', '{WishStatus.Presented}', '{WishStatus.Kept}')");

            // Retention sweeps only rows that carry requester data.
            entity.HasIndex(w => w.EndedAtUtc)
                .HasDatabaseName("IX_Wishes_EndedAtUtc_WithRequester")
                .HasFilter("\"RequesterLogin\" IS NOT NULL OR \"RequesterTwitchUserId\" IS NOT NULL");

            entity.HasOne(w => w.Channel)
                .WithMany()
                .HasForeignKey(w => w.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(w => w.Slot)
                .WithMany()
                .HasForeignKey(w => w.SlotId)
                .OnDelete(DeleteBehavior.SetNull);

            // The reference survives only while the target does.
            entity.HasOne<Wish>()
                .WithMany()
                .HasForeignKey(w => w.MergedIntoWishId)
                .OnDelete(DeleteBehavior.SetNull);

            // K6: one wish per IRC message and channel; manual and adopted wishes carry none.
            entity.HasIndex(w => new { w.ChannelId, w.IrcMessageId })
                .IsUnique()
                .HasDatabaseName("IX_Wishes_ChannelId_IrcMessageId")
                .HasFilter("\"IrcMessageId\" IS NOT NULL");

            // Forgetting a reward keeps its wishes.
            entity.HasOne(w => w.Reward)
                .WithMany()
                .HasForeignKey(w => w.RewardId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ChannelWishSettings>(entity =>
        {
            entity.HasKey(s => s.ChannelId);
            entity.Property(s => s.ProtectionThresholdPerDay).HasPrecision(10, 2);

            entity.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_ChannelWishSettings_DefaultDurationDays",
                    $"\"DefaultDurationDays\" BETWEEN {WishLimits.DurationDaysMin} AND {WishLimits.DurationDaysMax}");
                t.HasCheckConstraint(
                    "CK_ChannelWishSettings_ProbationLeadDays",
                    $"\"ProbationLeadDays\" BETWEEN {WishLimits.ProbationLeadDaysMin} AND {WishLimits.ProbationLeadDaysMax}");
                t.HasCheckConstraint(
                    "CK_ChannelWishSettings_ProtectionThreshold",
                    "\"ProtectionThresholdPerDay\" IS NULL OR \"ProtectionThresholdPerDay\" > 0");
                t.HasCheckConstraint(
                    "CK_ChannelWishSettings_PendingExpiryDays",
                    $"\"PendingExpiryDays\" BETWEEN {WishLimits.PendingExpiryDaysMin} AND {WishLimits.PendingExpiryDaysMax}");
            });

            // A database default as well as the CLR one: an image older than K6 keeps inserting settings rows
            // without naming the column while AddWishCapture is already applied.
            entity.Property(s => s.PendingExpiryDays).HasDefaultValue(WishLimits.DefaultPendingExpiryDays);

            entity.HasOne(s => s.Channel)
                .WithOne()
                .HasForeignKey<ChannelWishSettings>(s => s.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Chat capture (K6, migration AddWishCapture). Every table goes with its channel (purge, merge loser).
        modelBuilder.Entity<ChannelWishReward>(entity =>
        {
            entity.Property(r => r.RewardId).HasMaxLength(WishLimits.RewardIdMaxLength);
            entity.Property(r => r.Label).HasMaxLength(WishLimits.RewardLabelMaxLength);
            entity.HasIndex(r => new { r.ChannelId, r.RewardId }).IsUnique();
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_ChannelWishRewards_DefaultDurationDays",
                $"\"DefaultDurationDays\" BETWEEN {WishLimits.DurationDaysMin} AND {WishLimits.DurationDaysMax}"));

            entity.HasOne(r => r.Channel)
                .WithMany()
                .HasForeignKey(r => r.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WishRewardLearning>(entity =>
        {
            entity.HasKey(l => l.ChannelId);
            entity.Property(l => l.CandidateRewardId).HasMaxLength(WishLimits.RewardIdMaxLength);
            entity.Property(l => l.CandidateText).HasMaxLength(WishLimits.RequestTextMaxLength);
            entity.Property(l => l.CandidateLogin).HasMaxLength(WishLimits.RequesterLoginMaxLength);

            // A candidate is all five fields or none: "has a candidate" is one question, asked on CandidateId.
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_WishRewardLearnings_Candidate",
                "(\"CandidateId\" IS NULL AND \"CandidateRewardId\" IS NULL AND \"CandidateText\" IS NULL "
                + "AND \"CandidateLogin\" IS NULL AND \"CandidateSeenAtUtc\" IS NULL) "
                + "OR (\"CandidateId\" IS NOT NULL AND \"CandidateRewardId\" IS NOT NULL AND \"CandidateText\" IS NOT NULL "
                + "AND \"CandidateLogin\" IS NOT NULL AND \"CandidateSeenAtUtc\" IS NOT NULL)"));

            entity.HasOne(l => l.Channel)
                .WithOne()
                .HasForeignKey<WishRewardLearning>(l => l.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WishCaptureGap>(entity =>
        {
            entity.HasIndex(g => new { g.ChannelId, g.FromUtc });
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_WishCaptureGaps_Reason",
                $"\"Reason\" IN ({SqlList(WishCaptureGapReason.All)})"));

            entity.HasOne(g => g.Channel)
                .WithMany()
                .HasForeignKey(g => g.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WishCaptureState>(entity =>
        {
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.ToTable(t => t.HasCheckConstraint("CK_WishCaptureState_Singleton", $"\"Id\" = {(global::EmotePurge.Core.Entities.WishCaptureState.SingletonId)}"));
        });

        modelBuilder.Entity<WishSlotExclusion>(entity =>
        {
            entity.HasKey(x => new { x.WishId, x.SlotId });

            // No foreign key to the slot: the exclusion outlives an archived or deleted slot.
            entity.HasOne(x => x.Wish)
                .WithMany()
                .HasForeignKey(x => x.WishId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WishCaptureReceipt>(entity =>
        {
            entity.HasKey(r => new { r.ChannelId, r.IrcMessageId });
            entity.Property(r => r.IrcMessageId).HasMaxLength(WishLimits.IrcMessageIdMaxLength);
            entity.HasIndex(r => r.SeenAtUtc);

            entity.HasOne(r => r.Channel)
                .WithMany()
                .HasForeignKey(r => r.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    // A SQL list of string literals for a CHECK. Only ever fed with the code's own constants.
    private static string SqlList(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}
