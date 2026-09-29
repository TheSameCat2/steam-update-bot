using SteamUpdateBot.App.Health;

namespace SteamUpdateBot.App.Discord;

/// <summary>
/// Tracks Discord gateway availability for the disconnect watchdog and liveness check.
/// A resumed session raises <c>Connected</c> without <c>Ready</c>, so this type can restore
/// connected state when the previous Ready work still holds.
/// </summary>
internal sealed partial class DiscordGatewayConnectionMonitor
{
    internal static readonly TimeSpan DefaultWatchInterval = TimeSpan.FromSeconds(30);

    private readonly DiscordRuntimeState _runtimeState;
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger _logger;
    private readonly TimeSpan _watchInterval;

    public DiscordGatewayConnectionMonitor(
        DiscordRuntimeState runtimeState,
        TimeProvider timeProvider,
        IHostApplicationLifetime applicationLifetime,
        ILogger logger,
        TimeSpan? watchInterval = null)
    {
        _runtimeState = runtimeState;
        _timeProvider = timeProvider;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _watchInterval = watchInterval ?? DefaultWatchInterval;
    }

    public void MarkDisconnected() => _runtimeState.SetConnected(false);

    public bool TryRestoreAfterResume(bool commandsRegistered, DiscordResourceCheck resources)
    {
        if (!commandsRegistered)
        {
            return false;
        }

        if (DiscordResourceCheckEvaluator.Evaluate(resources).Kind != DiscordResourceCheckKind.Ready)
        {
            return false;
        }

        _runtimeState.SetConnected(true);
        return true;
    }

    public async Task WatchAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_watchInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (_runtimeState.DiscordConnected)
            {
                continue;
            }

            var disconnectedSinceUtc = _runtimeState.DiscordDisconnectedSinceUtc;
            if (disconnectedSinceUtc is not null
                && _timeProvider.GetUtcNow() - disconnectedSinceUtc.Value
                    > TimeSpan.FromMinutes(BotLivenessHealthCheck.DisconnectGraceMinutes))
            {
                LogGatewayDisconnectWatchdog(_logger, BotLivenessHealthCheck.DisconnectGraceMinutes);
                _applicationLifetime.StopApplication();
                return;
            }
        }
    }

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Critical,
        Message = "Discord gateway has been disconnected for more than {DisconnectGraceMinutes} minutes; stopping the process.")]
    private static partial void LogGatewayDisconnectWatchdog(ILogger logger, int disconnectGraceMinutes);
}
