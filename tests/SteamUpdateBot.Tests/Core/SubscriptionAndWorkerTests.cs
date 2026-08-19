using Microsoft.EntityFrameworkCore;
using SteamUpdateBot.Core.Configuration;
using SteamUpdateBot.Core.Domain;
using SteamUpdateBot.Core.Services;
using SteamUpdateBot.Tests.Persistence;

namespace SteamUpdateBot.Tests.Core;

public sealed class SubscriptionAndWorkerTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AddSilentlyBaselinesExistingAnnouncementsAndRejectsDuplicate()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        using var locks = new AppIdLockProvider();
        var time = new MutableTimeProvider(TestNow);
        var source = new FakeSteamAnnouncementSource
        {
            Metadata = CreateMetadata(400),
            Announcements = [CreateAnnouncement(400, "already-published", TestNow.AddMinutes(-2))],
        };
        using var service = new SubscriptionService(
            database.Repository,
            source,
            CreateOptions(),
            time,
            locks,
            new DisconnectedRuntimeState());

        var added = await service.AddGameAsync(400, "actor", CancellationToken.None);
        var duplicate = await service.AddGameAsync(400, "actor", CancellationToken.None);
        var due = await database.Repository.GetDueAnnouncementsAsync(TestNow.AddDays(1), 10, CancellationToken.None);
        await using var context = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await context.Announcements.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AddGameOutcome.Added, added.Outcome);
        Assert.Equal(AddGameOutcome.AlreadyMonitored, duplicate.Outcome);
        Assert.Empty(due);
        Assert.Equal(AnnouncementDeliveryState.Suppressed, stored.DeliveryState);
        Assert.Equal(1, source.GameRequests);
    }

    [Fact]
    public async Task AddEnforcesTheConfiguredGameCapBeforeCallingSteam()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var game = CreateGame(401, TestNow);
        await database.Repository.TryAddGameAsync(game, [], CancellationToken.None);
        using var locks = new AppIdLockProvider();
        using var service = new SubscriptionService(
            database.Repository,
            new FakeSteamAnnouncementSource { Metadata = CreateMetadata(402) },
            CreateOptions(maxGames: 1),
            new MutableTimeProvider(TestNow),
            locks,
            new DisconnectedRuntimeState());

        var result = await service.AddGameAsync(402, "actor", CancellationToken.None);

        Assert.Equal(AddGameOutcome.LimitReached, result.Outcome);
    }

    [Fact]
    public async Task PollFailuresUseFiveThenTenMinuteBackoff()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var game = CreateGame(500, TestNow);
        await database.Repository.TryAddGameAsync(game, [], CancellationToken.None);
        using var locks = new AppIdLockProvider();
        var time = new MutableTimeProvider(TestNow);
        var source = new FakeSteamAnnouncementSource { AnnouncementsException = new InvalidOperationException("steam down") };
        var coordinator = new PollCoordinator(database.Repository, source, CreateOptions(), time, locks);

        var first = await coordinator.RunOnceAsync(CancellationToken.None);
        var afterFirst = await database.Repository.GetGameAsync(game.AppId, CancellationToken.None);
        Assert.NotNull(afterFirst);
        Assert.Equal(TestNow.AddMinutes(5), afterFirst.NextPollUtc);
        Assert.Equal(TestNow, afterFirst.ScanWatermarkUtc);
        Assert.Null(afterFirst.LastPollSuccessUtc);

        time.NowUtc = afterFirst.NextPollUtc;
        var second = await coordinator.RunOnceAsync(CancellationToken.None);
        var afterSecond = await database.Repository.GetGameAsync(game.AppId, CancellationToken.None);

        Assert.Equal(1, first.Failures);
        Assert.Equal(1, second.Failures);
        Assert.NotNull(afterSecond);
        Assert.Equal(2, afterSecond.ConsecutiveFailures);
        Assert.Equal(time.NowUtc.AddMinutes(10), afterSecond.NextPollUtc);
    }

    [Fact]
    public async Task DeliveryOutboxRetriesAfterRestartAndMarksOnlySuccessfulPublicationDelivered()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var game = CreateGame(600, TestNow);
        await database.Repository.TryAddGameAsync(game, [], CancellationToken.None);
        await database.Repository.CommitPollAsync(
            game.AppId,
            TestNow,
            TestNow.AddMinutes(5),
            [CreateAnnouncement(game.AppId, "deliver-me", TestNow)],
            CancellationToken.None);

        using var locks = new AppIdLockProvider();
        var time = new MutableTimeProvider(TestNow);
        var failingPublisher = new FakeAnnouncementPublisher { Exception = new InvalidOperationException("Discord down") };
        using (var firstWorker = new OutboxDeliveryService(database.Repository, failingPublisher, time, locks))
        {
            var firstCycle = await firstWorker.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, firstCycle.Failed);
        }

        var retry = (await database.Repository.GetDueAnnouncementsAsync(TestNow.AddHours(1), 10, CancellationToken.None)).Single();
        Assert.Equal(1, retry.AttemptCount);
        Assert.Equal(TestNow.AddMinutes(1), retry.NextAttemptUtc);

        time.NowUtc = retry.NextAttemptUtc;
        var successfulPublisher = new FakeAnnouncementPublisher { MessageId = "123456" };
        using (var restartedWorker = new OutboxDeliveryService(database.Repository, successfulPublisher, time, locks))
        {
            var secondCycle = await restartedWorker.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, secondCycle.Delivered);
        }

        await using var context = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await context.Announcements.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AnnouncementDeliveryState.Delivered, stored.DeliveryState);
        Assert.Equal("123456", stored.DiscordMessageId);
        Assert.Equal(["deliver-me"], successfulPublisher.PublishedGids);
    }

    [Fact]
    public async Task DeliveryFailureDoesNotAllowALaterAnnouncementToOvertakeItInTheSameCycle()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var game = CreateGame(650, TestNow);
        await database.Repository.TryAddGameAsync(game, [], CancellationToken.None);
        await database.Repository.CommitPollAsync(
            game.AppId,
            TestNow,
            TestNow.AddMinutes(5),
            [
                CreateAnnouncement(game.AppId, "first", TestNow),
                CreateAnnouncement(game.AppId, "second", TestNow.AddMinutes(1)),
            ],
            CancellationToken.None);

        using var locks = new AppIdLockProvider();
        var time = new MutableTimeProvider(TestNow);
        var publisher = new FakeAnnouncementPublisher();
        publisher.Results.Enqueue(new InvalidOperationException("Discord down"));
        publisher.Results.Enqueue(null);
        using var worker = new OutboxDeliveryService(database.Repository, publisher, time, locks);

        var firstCycle = await worker.RunOnceAsync(CancellationToken.None);
        var blocked = await database.Repository.GetDueAnnouncementsAsync(TestNow, 10, CancellationToken.None);

        time.NowUtc = TestNow.AddMinutes(1);
        var retryCycle = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, firstCycle.Delivered);
        Assert.Equal(1, firstCycle.Failed);
        Assert.Empty(blocked);
        Assert.Equal(2, retryCycle.Delivered);
        Assert.Equal(["first", "first", "second"], publisher.AttemptedGids);
    }

    [Fact]
    public async Task DeliveryFailureDoesNotBlockADifferentGameInTheSameCycle()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var firstGame = CreateGame(651, TestNow);
        var secondGame = CreateGame(652, TestNow);
        await database.Repository.TryAddGameAsync(firstGame, [], CancellationToken.None);
        await database.Repository.TryAddGameAsync(secondGame, [], CancellationToken.None);
        await database.Repository.CommitPollAsync(
            firstGame.AppId,
            TestNow,
            TestNow.AddMinutes(5),
            [CreateAnnouncement(firstGame.AppId, "first-game", TestNow)],
            CancellationToken.None);
        await database.Repository.CommitPollAsync(
            secondGame.AppId,
            TestNow,
            TestNow.AddMinutes(5),
            [CreateAnnouncement(secondGame.AppId, "second-game", TestNow.AddMinutes(1))],
            CancellationToken.None);

        using var locks = new AppIdLockProvider();
        var publisher = new FakeAnnouncementPublisher();
        publisher.Results.Enqueue(new InvalidOperationException("Discord down"));
        using var worker = new OutboxDeliveryService(
            database.Repository,
            publisher,
            new MutableTimeProvider(TestNow),
            locks);

        var cycle = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, cycle.Delivered);
        Assert.Equal(1, cycle.Failed);
        Assert.Equal(["first-game", "second-game"], publisher.AttemptedGids);
        Assert.Equal(["second-game"], publisher.PublishedGids);
    }

    [Fact]
    public async Task DeliveryIsAbandonedAfterTheMaximumAttemptsAndUnblocksTheSameGame()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var game = CreateGame(653, TestNow);
        await database.Repository.TryAddGameAsync(game, [], CancellationToken.None);
        await database.Repository.CommitPollAsync(
            game.AppId,
            TestNow,
            TestNow.AddMinutes(5),
            [
                CreateAnnouncement(game.AppId, "poison", TestNow),
                CreateAnnouncement(game.AppId, "later", TestNow.AddMinutes(1)),
            ],
            CancellationToken.None);

        using var locks = new AppIdLockProvider();
        var time = new MutableTimeProvider(TestNow);
        var publisher = new FakeAnnouncementPublisher { Exception = new InvalidOperationException("Discord down") };
        using var worker = new OutboxDeliveryService(database.Repository, publisher, time, locks);

        for (var attempt = 0; attempt < OutboxDeliveryService.MaxDeliveryAttempts; attempt++)
        {
            var cycle = await worker.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, cycle.Failed);
            if (attempt == OutboxDeliveryService.MaxDeliveryAttempts - 1)
            {
                break;
            }

            var retry = (await database.Repository.GetDueAnnouncementsAsync(
                    time.NowUtc.AddDays(1),
                    10,
                    CancellationToken.None))
                .Single(announcement => announcement.SteamGid == "poison");
            time.NowUtc = retry.NextAttemptUtc;
        }

        publisher.Exception = null;
        var unblocked = await worker.RunOnceAsync(CancellationToken.None);
        await using var context = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var poison = await context.Announcements.SingleAsync(
            announcement => announcement.SteamGid == "poison",
            TestContext.Current.CancellationToken);
        var later = await context.Announcements.SingleAsync(
            announcement => announcement.SteamGid == "later",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, unblocked.Delivered);
        Assert.Equal(AnnouncementDeliveryState.Abandoned, poison.DeliveryState);
        Assert.Equal(OutboxDeliveryService.MaxDeliveryAttempts, poison.AttemptCount);
        Assert.Equal(AnnouncementDeliveryState.Delivered, later.DeliveryState);
        Assert.Equal(["later"], publisher.PublishedGids);
    }

    [Fact]
    public async Task InterruptedMarkAfterPublishDoesNotSendTheAnnouncementAgain()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var game = CreateGame(654, TestNow);
        await database.Repository.TryAddGameAsync(game, [], CancellationToken.None);
        await database.Repository.CommitPollAsync(
            game.AppId,
            TestNow,
            TestNow.AddMinutes(5),
            [CreateAnnouncement(game.AppId, "once-only", TestNow)],
            CancellationToken.None);

        var repository = new FailOnceMarkDeliveredRepository(database.Repository);
        using var locks = new AppIdLockProvider();
        var publisher = new FakeAnnouncementPublisher { MessageId = "789" };
        using var worker = new OutboxDeliveryService(
            repository,
            publisher,
            new MutableTimeProvider(TestNow),
            locks);

        var firstCycle = await worker.RunOnceAsync(CancellationToken.None);
        var secondCycle = await worker.RunOnceAsync(CancellationToken.None);
        await using var context = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await context.Announcements.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, firstCycle.Delivered);
        Assert.Equal(1, firstCycle.Failed);
        Assert.Equal(1, secondCycle.Delivered);
        Assert.Equal(["once-only"], publisher.PublishedGids);
        Assert.Equal(2, repository.MarkDeliveredCalls);
        Assert.Equal(AnnouncementDeliveryState.Delivered, stored.DeliveryState);
        Assert.Equal("789", stored.DiscordMessageId);
    }

    [Fact]
    public async Task OrphanedPublishingClaimWithoutMessageIdIsSentAgain()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var game = CreateGame(655, TestNow);
        await database.Repository.TryAddGameAsync(game, [], CancellationToken.None);
        await database.Repository.CommitPollAsync(
            game.AppId,
            TestNow,
            TestNow.AddMinutes(5),
            [CreateAnnouncement(game.AppId, "claimed", TestNow)],
            CancellationToken.None);
        await database.Repository.TryClaimForPublishAsync(game.AppId, "claimed", CancellationToken.None);

        using var locks = new AppIdLockProvider();
        var publisher = new FakeAnnouncementPublisher { MessageId = "123456" };
        using var worker = new OutboxDeliveryService(
            database.Repository,
            publisher,
            new MutableTimeProvider(TestNow),
            locks);

        var cycle = await worker.RunOnceAsync(CancellationToken.None);
        await using var context = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await context.Announcements.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, cycle.Delivered);
        Assert.Equal(["claimed"], publisher.AttemptedGids);
        Assert.Equal(["claimed"], publisher.PublishedGids);
        Assert.Equal(AnnouncementDeliveryState.Delivered, stored.DeliveryState);
        Assert.Equal("123456", stored.DiscordMessageId);
    }

    [Fact]
    public async Task TransientPublishFailureDoesNotCountTowardThePoisonBudget()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();
        var game = CreateGame(656, TestNow);
        await database.Repository.TryAddGameAsync(game, [], CancellationToken.None);
        await database.Repository.CommitPollAsync(
            game.AppId,
            TestNow,
            TestNow.AddMinutes(5),
            [CreateAnnouncement(game.AppId, "transient", TestNow)],
            CancellationToken.None);

        using var locks = new AppIdLockProvider();
        var time = new MutableTimeProvider(TestNow);
        var publisher = new FakeAnnouncementPublisher
        {
            Exception = new PublisherTransientException("Discord 503"),
        };
        using var worker = new OutboxDeliveryService(database.Repository, publisher, time, locks);

        for (var attempt = 0; attempt < OutboxDeliveryService.MaxDeliveryAttempts + 1; attempt++)
        {
            var cycle = await worker.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, cycle.Failed);
            var retry = (await database.Repository.GetDueAnnouncementsAsync(
                    time.NowUtc.AddDays(1),
                    10,
                    CancellationToken.None))
                .Single();
            Assert.Equal(0, retry.AttemptCount);
            Assert.Equal(AnnouncementDeliveryState.Pending, retry.DeliveryState);
            time.NowUtc = retry.NextAttemptUtc;
        }

        publisher.Exception = null;
        var delivered = await worker.RunOnceAsync(CancellationToken.None);
        await using var context = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await context.Announcements.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, delivered.Delivered);
        Assert.Equal(AnnouncementDeliveryState.Delivered, stored.DeliveryState);
        Assert.Equal(0, stored.AttemptCount);
    }

    private static SteamOptions CreateOptions(int maxGames = 50) =>
        new()
        {
            PollIntervalMinutes = 5,
            MaxConcurrentPolls = 4,
            MaxGames = maxGames,
            OverlapHours = 24,
            RequestTimeoutSeconds = 15,
        };

    private static SteamAppMetadata CreateMetadata(uint appId) =>
        new(appId, $"Game {appId}", $"https://store.steampowered.com/app/{appId}", null);

    private static MonitoredGame CreateGame(uint appId, DateTimeOffset now) =>
        new()
        {
            AppId = appId,
            Name = $"Game {appId}",
            StoreUrl = $"https://store.steampowered.com/app/{appId}",
            AddedByDiscordUserId = "actor",
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
