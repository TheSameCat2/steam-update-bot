using Microsoft.EntityFrameworkCore;
using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Tests.Persistence;

public sealed class EfBotRepositoryTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BaselineIsSuppressedAndOnlyNewAnnouncementsAreQueuedInPublicationOrder()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var game = CreateGame(100, now);
        var oldAnnouncement = CreateAnnouncement(100, "old", now.AddMinutes(-10));
        var newerAnnouncement = CreateAnnouncement(100, "newer", now.AddMinutes(2));
        var newAnnouncement = CreateAnnouncement(100, "new", now.AddMinutes(1));

        var added = await database.Repository.TryAddGameAsync(game, [oldAnnouncement], TestContext.Current.CancellationToken);
        var commit = await database.Repository.CommitPollAsync(
            game.AppId,
            now.AddMinutes(5),
            now.AddMinutes(10),
            [oldAnnouncement, newerAnnouncement, newAnnouncement, newAnnouncement],
            TestContext.Current.CancellationToken);

        var due = await database.Repository.GetDueAnnouncementsAsync(now.AddMinutes(5), 10, TestContext.Current.CancellationToken);

        Assert.True(added);
        Assert.Equal(2, commit.QueuedCount);
        Assert.Equal(["new", "newer"], due.Select(announcement => announcement.SteamGid));
        Assert.All(due, announcement => Assert.Equal(AnnouncementDeliveryState.Pending, announcement.DeliveryState));
    }

    [Fact]
    public async Task RemovingAGameCascadesItsOutboxRecords()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var game = CreateGame(200, now);
        await database.Repository.TryAddGameAsync(game, [], TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            game.AppId,
            now,
            now.AddMinutes(5),
            [CreateAnnouncement(game.AppId, "queued", now)],
            TestContext.Current.CancellationToken);

        var removed = await database.Repository.RemoveGameAsync(game.AppId, TestContext.Current.CancellationToken);
        var due = await database.Repository.GetDueAnnouncementsAsync(now.AddDays(1), 10, TestContext.Current.CancellationToken);

        Assert.True(removed);
        Assert.Empty(due);
        Assert.Null(await database.Repository.GetGameAsync(game.AppId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RetryingAnnouncementBlocksLaterAnnouncementsUntilItIsDue()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var game = CreateGame(250, now);
        var first = CreateAnnouncement(game.AppId, "first", now);
        var second = CreateAnnouncement(game.AppId, "second", now.AddMinutes(1));
        await database.Repository.TryAddGameAsync(game, [], TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            game.AppId,
            now,
            now.AddMinutes(5),
            [first, second],
            TestContext.Current.CancellationToken);
        await database.Repository.RecordDeliveryFailureAsync(
            game.AppId,
            first.Gid,
            now.AddMinutes(5),
            "Discord rejected the message",
            TestContext.Current.CancellationToken);

        var blocked = await database.Repository.GetDueAnnouncementsAsync(now, 10, TestContext.Current.CancellationToken);
        var due = await database.Repository.GetDueAnnouncementsAsync(now.AddMinutes(5), 10, TestContext.Current.CancellationToken);

        Assert.Empty(blocked);
        Assert.Equal(["first", "second"], due.Select(announcement => announcement.SteamGid));
    }

    [Fact]
    public async Task RetryingAnnouncementDoesNotBlockADifferentGame()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var blockedGame = CreateGame(251, now);
        var otherGame = CreateGame(252, now);
        await database.Repository.TryAddGameAsync(blockedGame, [], TestContext.Current.CancellationToken);
        await database.Repository.TryAddGameAsync(otherGame, [], TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            blockedGame.AppId,
            now,
            now.AddMinutes(5),
            [CreateAnnouncement(blockedGame.AppId, "blocked-head", now)],
            TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            otherGame.AppId,
            now,
            now.AddMinutes(5),
            [CreateAnnouncement(otherGame.AppId, "other-due", now.AddMinutes(1))],
            TestContext.Current.CancellationToken);
        await database.Repository.RecordDeliveryFailureAsync(
            blockedGame.AppId,
            "blocked-head",
            now.AddMinutes(5),
            "Discord rejected the message",
            TestContext.Current.CancellationToken);

        var due = await database.Repository.GetDueAnnouncementsAsync(now, 10, TestContext.Current.CancellationToken);

        Assert.Equal(["other-due"], due.Select(announcement => announcement.SteamGid));
    }

    [Fact]
    public async Task AbandonedAnnouncementUnblocksLaterAnnouncementsForTheSameGame()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var game = CreateGame(253, now);
        var first = CreateAnnouncement(game.AppId, "first", now);
        var second = CreateAnnouncement(game.AppId, "second", now.AddMinutes(1));
        await database.Repository.TryAddGameAsync(game, [], TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            game.AppId,
            now,
            now.AddMinutes(5),
            [first, second],
            TestContext.Current.CancellationToken);
        await database.Repository.MarkDeliveryAbandonedAsync(
            game.AppId,
            first.Gid,
            "Discord rejected the message",
            TestContext.Current.CancellationToken);

        var due = await database.Repository.GetDueAnnouncementsAsync(now, 10, TestContext.Current.CancellationToken);
        await using var context = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var abandoned = await context.Announcements.SingleAsync(
            announcement => announcement.SteamGid == first.Gid,
            TestContext.Current.CancellationToken);

        Assert.Equal(AnnouncementDeliveryState.Abandoned, abandoned.DeliveryState);
        Assert.Equal(["second"], due.Select(announcement => announcement.SteamGid));
    }

    [Fact]
    public async Task PublishingClaimBlocksLaterAnnouncementsAndIsDueImmediately()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var game = CreateGame(254, now);
        var first = CreateAnnouncement(game.AppId, "first", now);
        var second = CreateAnnouncement(game.AppId, "second", now.AddMinutes(1));
        await database.Repository.TryAddGameAsync(game, [], TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            game.AppId,
            now,
            now.AddMinutes(5),
            [first, second],
            TestContext.Current.CancellationToken);

        var claimed = await database.Repository.TryClaimForPublishAsync(
            game.AppId,
            first.Gid,
            TestContext.Current.CancellationToken);
        var due = await database.Repository.GetDueAnnouncementsAsync(now, 10, TestContext.Current.CancellationToken);
        var replay = await database.Repository.TryClaimForPublishAsync(
            game.AppId,
            first.Gid,
            TestContext.Current.CancellationToken);

        Assert.Equal(PublishClaimOutcome.Claimed, claimed.Outcome);
        Assert.Equal(["first"], due.Select(announcement => announcement.SteamGid));
        Assert.Equal(AnnouncementDeliveryState.Publishing, due[0].DeliveryState);
        Assert.Equal(PublishClaimOutcome.AlreadyPublishing, replay.Outcome);
    }

    [Fact]
    public async Task StatusReportsRetryingDelivery()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var game = CreateGame(275, now);
        var announcement = CreateAnnouncement(game.AppId, "retry", now);
        await database.Repository.TryAddGameAsync(game, [], TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            game.AppId,
            now,
            now.AddMinutes(5),
            [announcement],
            TestContext.Current.CancellationToken);
        await database.Repository.RecordDeliveryFailureAsync(
            game.AppId,
            announcement.Gid,
            now.AddMinutes(1),
            "Discord rejected the message",
            TestContext.Current.CancellationToken);

        var status = await database.Repository.GetStatusAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(1, status.FailedDeliveryCount);
        Assert.Equal(0, status.AbandonedDeliveryCount);
        Assert.Equal("One or more Discord announcement deliveries are retrying.", status.LastError);
    }

    [Fact]
    public async Task StatusReportsAbandonedDelivery()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var game = CreateGame(276, now);
        var announcement = CreateAnnouncement(game.AppId, "abandoned", now);
        await database.Repository.TryAddGameAsync(game, [], TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            game.AppId,
            now,
            now.AddMinutes(5),
            [announcement],
            TestContext.Current.CancellationToken);
        await database.Repository.MarkDeliveryAbandonedAsync(
            game.AppId,
            announcement.Gid,
            "Discord rejected the message",
            TestContext.Current.CancellationToken);

        var status = await database.Repository.GetStatusAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(0, status.FailedDeliveryCount);
        Assert.Equal(1, status.AbandonedDeliveryCount);
        Assert.Equal(0, status.PendingDeliveryCount);
        Assert.Equal("One or more Discord announcement deliveries were abandoned after repeated failures.", status.LastError);
    }

    [Fact]
    public async Task StatusReportsATransientlyStuckDeliveryThatNeverIncrementsAttemptCount()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var game = CreateGame(277, now);
        var announcement = CreateAnnouncement(game.AppId, "stuck", now);
        await database.Repository.TryAddGameAsync(game, [], TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            game.AppId,
            now,
            now.AddMinutes(5),
            [announcement],
            TestContext.Current.CancellationToken);
        await database.Repository.RecordDeliveryFailureAsync(
            game.AppId,
            announcement.Gid,
            now.AddMinutes(1),
            "Configured announcement channel is unavailable",
            TestContext.Current.CancellationToken,
            incrementAttempt: false);

        var status = await database.Repository.GetStatusAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(1, status.FailedDeliveryCount);
        Assert.Equal(1, status.PendingDeliveryCount);
        Assert.Equal(now, status.OldestUndeliveredDetectedAtUtc);
        Assert.Equal("One or more Discord announcement deliveries are retrying.", status.LastError);
    }

    [Fact]
    public async Task StatusReportsNoUndeliveredAnnouncementOnceEverythingIsDelivered()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var game = CreateGame(278, now);
        var announcement = CreateAnnouncement(game.AppId, "done", now);
        await database.Repository.TryAddGameAsync(game, [], TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            game.AppId,
            now,
            now.AddMinutes(5),
            [announcement],
            TestContext.Current.CancellationToken);
        await database.Repository.MarkDeliveredAsync(
            game.AppId,
            announcement.Gid,
            "42",
            now,
            TestContext.Current.CancellationToken);

        var status = await database.Repository.GetStatusAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(0, status.FailedDeliveryCount);
        Assert.Equal(0, status.PendingDeliveryCount);
        Assert.Null(status.OldestUndeliveredDetectedAtUtc);
    }

    [Fact]
    public async Task TransientDeliveryFailureDoesNotIncrementAttemptCount()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var game = CreateGame(255, now);
        var announcement = CreateAnnouncement(game.AppId, "retry", now);
        await database.Repository.TryAddGameAsync(game, [], TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            game.AppId,
            now,
            now.AddMinutes(5),
            [announcement],
            TestContext.Current.CancellationToken);

        await database.Repository.RecordDeliveryFailureAsync(
            game.AppId,
            announcement.Gid,
            now.AddMinutes(1),
            "Discord 503",
            TestContext.Current.CancellationToken,
            incrementAttempt: false);

        var due = await database.Repository.GetDueAnnouncementsAsync(now.AddMinutes(1), 10, TestContext.Current.CancellationToken);

        Assert.Equal(AnnouncementDeliveryState.Pending, due[0].DeliveryState);
        Assert.Equal(0, due[0].AttemptCount);
        Assert.Equal("Discord 503", due[0].LastError);
    }

    [Fact]
    public async Task StoredTitleDoesNotSplitATrailingSurrogatePair()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var game = CreateGame(256, now);
        var title = new string('a', 511) + "😀";
        var announcement = new SteamAnnouncement(
            game.AppId,
            "emoji",
            title,
            $"https://steamcommunity.com/games/{game.AppId}/announcements/detail/emoji",
            "content",
            "developer",
            now);
        await database.Repository.TryAddGameAsync(game, [], TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            game.AppId,
            now,
            now.AddMinutes(5),
            [announcement],
            TestContext.Current.CancellationToken);

        var due = await database.Repository.GetDueAnnouncementsAsync(now, 10, TestContext.Current.CancellationToken);

        Assert.Equal(new string('a', 511), due[0].Title);
    }

    [Fact]
    public async Task StatusTracksTheOldestSuccessfulPollAcrossAllGames()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var firstGame = CreateGame(280, now);
        var secondGame = CreateGame(281, now);
        await database.Repository.TryAddGameAsync(firstGame, [], TestContext.Current.CancellationToken);
        await database.Repository.TryAddGameAsync(secondGame, [], TestContext.Current.CancellationToken);
        await database.Repository.CommitPollAsync(
            firstGame.AppId,
            now,
            now.AddMinutes(5),
            [],
            TestContext.Current.CancellationToken);

        var withUnpolledGame = await database.Repository.GetStatusAsync(true, TestContext.Current.CancellationToken);

        await database.Repository.CommitPollAsync(
            secondGame.AppId,
            now.AddMinutes(-2),
            now.AddMinutes(3),
            [],
            TestContext.Current.CancellationToken);
        var withAllGamesPolled = await database.Repository.GetStatusAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(now, withUnpolledGame.LastSuccessfulPollUtc);
        Assert.Equal(now, withUnpolledGame.OldestSuccessfulPollUtc);
        Assert.Equal(now, withAllGamesPolled.LastSuccessfulPollUtc);
        Assert.Equal(now.AddMinutes(-2), withAllGamesPolled.OldestSuccessfulPollUtc);
    }

    [Fact]
    public async Task PollFailureTracksFailureCountAndStatus()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var now = TestNow;
        var game = CreateGame(300, now);
        await database.Repository.TryAddGameAsync(game, [], TestContext.Current.CancellationToken);

        await database.Repository.RecordPollFailureAsync(
            game.AppId,
            now,
            now.AddMinutes(5),
            "network failed",
            TestContext.Current.CancellationToken);

        var saved = await database.Repository.GetGameAsync(game.AppId, TestContext.Current.CancellationToken);
        var status = await database.Repository.GetStatusAsync(true, TestContext.Current.CancellationToken);

        Assert.NotNull(saved);
        Assert.Equal(1, saved.ConsecutiveFailures);
        Assert.Equal("network failed", saved.LastError);
        Assert.True(status.DatabaseReady);
        Assert.Equal("network failed", status.LastError);
    }

    private static MonitoredGame CreateGame(uint appId, DateTimeOffset now) =>
        new()
        {
            AppId = appId,
            Name = $"Game {appId}",
            StoreUrl = $"https://store.steampowered.com/app/{appId}",
            AddedByDiscordUserId = "42",
            AddedAtUtc = now,
            ScanWatermarkUtc = now,
            NextPollUtc = now,
        };

    private static SteamAnnouncement CreateAnnouncement(uint appId, string gid, DateTimeOffset publishedAtUtc) =>
        new(
            appId,
            gid,
            $"Title {gid}",
            $"https://steamcommunity.com/games/{appId}/announcements/detail/{gid}",
            "content",
            "developer",
            publishedAtUtc);
}
