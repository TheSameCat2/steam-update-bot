using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using SteamUpdateBot.App.Health;
using SteamUpdateBot.Core.Configuration;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Domain;
using SteamUpdateBot.Tests.Core;

namespace SteamUpdateBot.Tests.Health;

public sealed class BotReadinessHealthCheckTests
{
    [Fact]
    public async Task MissingSuccessfulPollForOneGameMakesReadinessUnhealthy()
    {
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var repository = Substitute.For<IBotRepository>();
        repository.GetStatusAsync(true, Arg.Any<CancellationToken>()).Returns(Task.FromResult(
            new BotStatusSnapshot(
                true,
                true,
                2,
                0,
                0,
                now,
                null,
                null)));
        var healthCheck = new BotReadinessHealthCheck(
            repository,
            new ConnectedRuntimeState(),
            new SteamOptions { PollIntervalMinutes = 5 },
            new MutableTimeProvider(now));

        HealthCheckResult result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Steam polling is stale.", result.Description);
    }

    [Fact]
    public async Task RetryingDeliveryMakesReadinessDegraded()
    {
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var repository = Substitute.For<IBotRepository>();
        repository.GetStatusAsync(true, Arg.Any<CancellationToken>()).Returns(Task.FromResult(
            new BotStatusSnapshot(
                true,
                true,
                1,
                1,
                1,
                now,
                now,
                "One or more Discord announcement deliveries are retrying.")));
        var healthCheck = new BotReadinessHealthCheck(
            repository,
            new ConnectedRuntimeState(),
            new SteamOptions { PollIntervalMinutes = 5 },
            new MutableTimeProvider(now));

        HealthCheckResult result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("Discord announcement delivery is retrying.", result.Description);
    }

    [Fact]
    public async Task NewlyAddedGameWithinGraceIsReady()
    {
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var repository = Substitute.For<IBotRepository>();
        repository.GetStatusAsync(true, Arg.Any<CancellationToken>()).Returns(Task.FromResult(
            new BotStatusSnapshot(
                true,
                true,
                1,
                0,
                0,
                null,
                now,
                null)));
        var healthCheck = new BotReadinessHealthCheck(
            repository,
            new ConnectedRuntimeState(),
            new SteamOptions { PollIntervalMinutes = 5 },
            new MutableTimeProvider(now));

        HealthCheckResult result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task RetryingDeliveryDoesNotHideStalePolling()
    {
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var repository = Substitute.For<IBotRepository>();
        repository.GetStatusAsync(true, Arg.Any<CancellationToken>()).Returns(Task.FromResult(
            new BotStatusSnapshot(
                true,
                true,
                1,
                1,
                1,
                now.AddHours(-2),
                now.AddHours(-2),
                "One or more Discord announcement deliveries are retrying.")));
        var healthCheck = new BotReadinessHealthCheck(
            repository,
            new ConnectedRuntimeState(),
            new SteamOptions { PollIntervalMinutes = 5 },
            new MutableTimeProvider(now));

        HealthCheckResult result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Steam polling is stale. Discord announcement delivery is not draining.", result.Description);
    }

    [Fact]
    public async Task OutboxStuckBeyondTheGraceMakesReadinessUnhealthyWithoutAnyAttemptCount()
    {
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var repository = Substitute.For<IBotRepository>();
        repository.GetStatusAsync(true, Arg.Any<CancellationToken>()).Returns(Task.FromResult(
            new BotStatusSnapshot(
                true,
                true,
                1,
                PendingDeliveryCount: 3,
                FailedDeliveryCount: 0,
                now,
                now,
                null,
                AbandonedDeliveryCount: 0,
                OldestUndeliveredDetectedAtUtc: now.AddMinutes(-(BotReadinessHealthCheck.StuckDeliveryGraceMinutes + 1)))));
        var healthCheck = new BotReadinessHealthCheck(
            repository,
            new ConnectedRuntimeState(),
            new SteamOptions { PollIntervalMinutes = 5 },
            new MutableTimeProvider(now));

        HealthCheckResult result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("has not drained", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecentlyQueuedAnnouncementWithinTheGraceStaysReady()
    {
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var repository = Substitute.For<IBotRepository>();
        repository.GetStatusAsync(true, Arg.Any<CancellationToken>()).Returns(Task.FromResult(
            new BotStatusSnapshot(
                true,
                true,
                1,
                PendingDeliveryCount: 1,
                FailedDeliveryCount: 0,
                now,
                now,
                null,
                AbandonedDeliveryCount: 0,
                OldestUndeliveredDetectedAtUtc: now.AddMinutes(-1))));
        var healthCheck = new BotReadinessHealthCheck(
            repository,
            new ConnectedRuntimeState(),
            new SteamOptions { PollIntervalMinutes = 5 },
            new MutableTimeProvider(now));

        HealthCheckResult result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private sealed class ConnectedRuntimeState : IBotRuntimeState
    {
        public bool DiscordConnected => true;

        public DateTimeOffset? DiscordDisconnectedSinceUtc => null;
    }
}
