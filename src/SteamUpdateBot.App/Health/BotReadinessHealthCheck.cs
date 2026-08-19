using Microsoft.Extensions.Diagnostics.HealthChecks;
using SteamUpdateBot.Core.Configuration;
using SteamUpdateBot.Core.Contracts;

namespace SteamUpdateBot.App.Health;

public sealed class BotReadinessHealthCheck : IHealthCheck
{
    private readonly IBotRepository _repository;
    private readonly IBotRuntimeState _runtimeState;
    private readonly SteamOptions _steamOptions;
    private readonly TimeProvider _timeProvider;

    public BotReadinessHealthCheck(
        IBotRepository repository,
        IBotRuntimeState runtimeState,
        SteamOptions steamOptions,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _runtimeState = runtimeState;
        _steamOptions = steamOptions;
        _timeProvider = timeProvider;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var status = await _repository
            .GetStatusAsync(_runtimeState.DiscordConnected, cancellationToken)
            .ConfigureAwait(false);
        var data = new Dictionary<string, object>
        {
            ["discordConnected"] = status.DiscordConnected,
            ["monitoredGameCount"] = status.MonitoredGameCount,
            ["pendingDeliveryCount"] = status.PendingDeliveryCount,
            ["failedDeliveryCount"] = status.FailedDeliveryCount,
            ["abandonedDeliveryCount"] = status.AbandonedDeliveryCount,
            ["oldestSuccessfulPollUtc"] = status.OldestSuccessfulPollUtc?.ToString("O") ?? "never",
        };

        if (!status.DatabaseReady)
        {
            return HealthCheckResult.Unhealthy("The SQLite database is unavailable.", data: data);
        }

        if (!status.DiscordConnected)
        {
            return HealthCheckResult.Unhealthy("The Discord gateway is disconnected.", data: data);
        }

        var pollingIsStale = false;
        if (status.MonitoredGameCount > 0)
        {
            var maximumPollAge = TimeSpan.FromMinutes(_steamOptions.PollIntervalMinutes * 3);
            var oldestPollUtc = status.OldestSuccessfulPollUtc;
            pollingIsStale = oldestPollUtc is null
                || _timeProvider.GetUtcNow() - oldestPollUtc > maximumPollAge;
        }

        var deliveryIsRetrying = status.FailedDeliveryCount > 0;
        if (pollingIsStale && deliveryIsRetrying)
        {
            return HealthCheckResult.Unhealthy(
                "Steam polling is stale. Discord announcement delivery is retrying.",
                data: data);
        }

        if (pollingIsStale)
        {
            return HealthCheckResult.Unhealthy("Steam polling is stale.", data: data);
        }

        if (deliveryIsRetrying)
        {
            return HealthCheckResult.Degraded("Discord announcement delivery is retrying.", data: data);
        }

        return HealthCheckResult.Healthy("The bot is ready.", data);
    }
}
