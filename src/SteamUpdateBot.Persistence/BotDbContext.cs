using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Persistence;

public sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMilliseconds = new(
        value => value.ToUnixTimeMilliseconds(),
        value => DateTimeOffset.FromUnixTimeMilliseconds(value));

    public DbSet<MonitoredGame> MonitoredGames => Set<MonitoredGame>();

    public DbSet<AnnouncementRecord> Announcements => Set<AnnouncementRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureMonitoredGame(modelBuilder.Entity<MonitoredGame>());
        ConfigureAnnouncement(modelBuilder.Entity<AnnouncementRecord>());
    }

    private static void ConfigureMonitoredGame(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<MonitoredGame> game)
    {
        game.ToTable("MonitoredGames");
        game.HasKey(entity => entity.AppId);
        game.Property(entity => entity.AppId).HasConversion<long>().ValueGeneratedNever();
        game.Property(entity => entity.Name).HasMaxLength(256).IsRequired();
        game.Property(entity => entity.StoreUrl).HasMaxLength(2_048).IsRequired();
        game.Property(entity => entity.HeaderImageUrl).HasMaxLength(2_048);
        game.Property(entity => entity.AddedByDiscordUserId).HasMaxLength(64).IsRequired();
        game.Property(entity => entity.AddedAtUtc).HasConversion(DateTimeOffsetToUnixMilliseconds).IsRequired();
        game.Property(entity => entity.ScanWatermarkUtc).HasConversion(DateTimeOffsetToUnixMilliseconds).IsRequired();
        game.Property(entity => entity.LastPollAttemptUtc).HasConversion(DateTimeOffsetToUnixMilliseconds);
        game.Property(entity => entity.LastPollSuccessUtc).HasConversion(DateTimeOffsetToUnixMilliseconds);
        game.Property(entity => entity.NextPollUtc).HasConversion(DateTimeOffsetToUnixMilliseconds).IsRequired();
        game.Property(entity => entity.ConsecutiveFailures).IsRequired();
        game.Property(entity => entity.LastError).HasMaxLength(1_000);
        game.HasIndex(entity => entity.NextPollUtc);
    }

    private static void ConfigureAnnouncement(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<AnnouncementRecord> announcement)
    {
        announcement.ToTable("Announcements");
        announcement.HasKey(entity => new { entity.AppId, entity.SteamGid });
        announcement.Property(entity => entity.AppId).HasConversion<long>();
        announcement.Property(entity => entity.SteamGid).HasMaxLength(128).IsRequired();
        announcement.Property(entity => entity.GameName).HasMaxLength(256).IsRequired();
        announcement.Property(entity => entity.HeaderImageUrl).HasMaxLength(2_048);
        announcement.Property(entity => entity.Title).HasMaxLength(512).IsRequired();
        announcement.Property(entity => entity.Url).HasMaxLength(2_048).IsRequired();
        announcement.Property(entity => entity.Content).HasMaxLength(4_096).IsRequired();
        announcement.Property(entity => entity.Author).HasMaxLength(256);
        announcement.Property(entity => entity.PublishedAtUtc).HasConversion(DateTimeOffsetToUnixMilliseconds).IsRequired();
        announcement.Property(entity => entity.DetectedAtUtc).HasConversion(DateTimeOffsetToUnixMilliseconds).IsRequired();
        announcement.Property(entity => entity.DeliveryState).HasConversion<int>().IsRequired();
        announcement.Property(entity => entity.AttemptCount).IsRequired();
        announcement.Property(entity => entity.NextAttemptUtc).HasConversion(DateTimeOffsetToUnixMilliseconds).IsRequired();
        announcement.Property(entity => entity.DeliveredAtUtc).HasConversion(DateTimeOffsetToUnixMilliseconds);
        announcement.Property(entity => entity.DiscordMessageId).HasMaxLength(64);
        announcement.Property(entity => entity.LastError).HasMaxLength(1_000);
        announcement.HasIndex(entity => new { entity.DeliveryState, entity.NextAttemptUtc, entity.PublishedAtUtc });
        announcement.HasOne<MonitoredGame>()
            .WithMany()
            .HasForeignKey(entity => entity.AppId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
