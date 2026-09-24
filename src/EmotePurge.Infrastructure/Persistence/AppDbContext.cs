using EmotePurge.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Emote> Emotes => Set<Emote>();
    public DbSet<UsageStat> UsageStats => Set<UsageStat>();
    public DbSet<ChannelLiveDay> ChannelLiveDays => Set<ChannelLiveDay>();
    public DbSet<ChannelEmoteSetObservation> ChannelEmoteSetObservations => Set<ChannelEmoteSetObservation>();
    public DbSet<User> Users => Set<User>();
    public DbSet<VoteSession> VoteSessions => Set<VoteSession>();
    public DbSet<VoteSessionEmote> VoteSessionEmotes => Set<VoteSessionEmote>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

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

            entity.HasOne(e => e.Channel)
                .WithMany(c => c.Emotes)
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UsageStat>(entity =>
        {
            // One aggregated row per emote per emote set per UTC day (#200, spec section 4.1): the
            // same emote can be counted under two set ids on the same day (a mid-day set switch), so
            // the old (EmoteId, Date) key stopped being able to hold one row per real count. Covering
            // index (UseCount included) so range-sum queries over (EmoteId, EmoteSetId, Date) — and,
            // with the EmoteId-only prefix, over (EmoteId, Date) for set-agnostic reads — can still
            // be answered as an index-only scan. Replaces the former
            // IX_UsageStats_EmoteId_Date, which the AddUsageStatEmoteSetId migration (T1.3b) drops.
            entity.HasIndex(u => new { u.EmoteId, u.EmoteSetId, u.Date })
                .IsUnique()
                .IncludeProperties(u => u.UseCount);

            entity.HasOne(u => u.Emote)
                .WithMany(e => e.UsageStats)
                .HasForeignKey(u => u.EmoteId)
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
    }
}
