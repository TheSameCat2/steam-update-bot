using Microsoft.Extensions.Diagnostics.HealthChecks;
using SteamUpdateBot.Core.Contracts;

namespace SteamUpdateBot.App.Health;

/// <summary>
/// Process liveness for Docker. Fails only when Discord has been down long enough
/// that a container restart is more useful than waiting for a reconnect.
/// </summary>
public sealed class BotLivenessHealthCheck : IHealthCheck
{
    public const int DisconnectGraceMinutes = 10;

    private readonly IBotRuntimeState _runtimeState;
    private readonly TimeProvider _timeProvider;

    public BotLivenessHealthCheck(IBotRuntimeState runtimeState, TimeProvider timeProvider)
    {
        _runtimeState = runtimeState;
        _timeProvider = timeProvider;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["discordConnected"] = _runtimeState.DiscordConnected,
            ["discordDisconnectedSinceUtc"] = _runtimeState.DiscordDisconnectedSinceUtc?.ToString("O") ?? "n/a",
        };

        if (_runtimeState.DiscordConnected)
        {
            return Task.FromResult(HealthCheckResult.Healthy("The process is live.", data));
        }

        var disconnectedSinceUtc = _runtimeState.DiscordDisconnectedSinceUtc;
        if (disconnectedSinceUtc is not null
            && _timeProvider.GetUtcNow() - disconnectedSinceUtc.Value > TimeSpan.FromMinutes(DisconnectGraceMinutes))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "The Discord gateway has been disconnected too long.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("The process is live.", data));
    }
}
