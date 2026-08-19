using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.App.Workers;

public sealed partial class PollingHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private readonly IPollCoordinator _pollCoordinator;
    private readonly ILogger<PollingHostedService> _logger;

    public PollingHostedService(IPollCoordinator pollCoordinator, ILogger<PollingHostedService> logger)
    {
        _pollCoordinator = pollCoordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunCycleSafelyAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunCycleSafelyAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunCycleSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            PollCycleResult result = await _pollCoordinator.RunOnceAsync(cancellationToken).ConfigureAwait(false);
            if (result.GamesPolled > 0 || result.Failures > 0)
            {
                LogPollCycle(_logger, result.GamesPolled, result.AnnouncementsQueued, result.Failures);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogPollFailure(_logger, exception);
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Steam poll cycle completed: {GamesPolled} games, {AnnouncementsQueued} announcements queued, {Failures} failures.")]
    private static partial void LogPollCycle(ILogger logger, int gamesPolled, int announcementsQueued, int failures);

    [LoggerMessage(EventId = 101, Level = LogLevel.Error, Message = "Steam poll cycle failed unexpectedly.")]
    private static partial void LogPollFailure(ILogger logger, Exception exception);
}
