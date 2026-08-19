using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamUpdateBot.Core.Configuration;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Core.Services;

public sealed partial class PollCoordinator : IPollCoordinator
{
    private readonly IBotRepository _repository;
    private readonly ISteamAnnouncementSource _steamSource;
    private readonly SteamOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IAppIdLockProvider _appLocks;
    private readonly ILogger<PollCoordinator> _logger;

    public PollCoordinator(
        IBotRepository repository,
        ISteamAnnouncementSource steamSource,
        SteamOptions options,
        TimeProvider timeProvider,
        IAppIdLockProvider appLocks,
        ILogger<PollCoordinator>? logger = null)
    {
        _repository = repository;
        _steamSource = steamSource;
        _options = options;
        _timeProvider = timeProvider;
        _appLocks = appLocks;
        _logger = logger ?? NullLogger<PollCoordinator>.Instance;
    }

    public async Task<PollCycleResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var dueGames = await _repository.GetDueGamesAsync(nowUtc, _options.MaxGames, cancellationToken).ConfigureAwait(false);
        if (dueGames.Count == 0)
        {
            return new PollCycleResult(0, 0, 0);
        }

        using var concurrency = new SemaphoreSlim(_options.MaxConcurrentPolls, _options.MaxConcurrentPolls);
        var tasks = dueGames.Select(async game =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await PollGameAsync(game.AppId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new PollCycleResult(
            results.Length,
            results.Sum(static result => result.QueuedCount),
            results.Count(static result => result.Failed));
    }

    private async Task<PollResult> PollGameAsync(uint appId, CancellationToken cancellationToken)
    {
        await using var appLock = await _appLocks.AcquireAsync(appId, cancellationToken).ConfigureAwait(false);
        var game = await _repository.GetGameAsync(appId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return PollResult.Noop;
        }

        var pollStartedUtc = _timeProvider.GetUtcNow();
        try
        {
            var announcements = await _steamSource.GetOfficialAnnouncementsSinceAsync(
                    appId,
                    SubtractClamped(game.ScanWatermarkUtc, TimeSpan.FromHours(_options.OverlapHours)),
                    cancellationToken)
                .ConfigureAwait(false);

            var commit = await _repository.CommitPollAsync(
                    appId,
                    pollStartedUtc,
                    pollStartedUtc.AddMinutes(_options.PollIntervalMinutes),
                    announcements,
                    cancellationToken)
                .ConfigureAwait(false);

            return commit.GameStillMonitored ? new PollResult(commit.QueuedCount, false) : PollResult.Noop;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogPollGameFailure(_logger, exception, appId);
            var attemptedAtUtc = _timeProvider.GetUtcNow();
            var failureNumber = game.ConsecutiveFailures + 1;
            await _repository.RecordPollFailureAsync(
                    appId,
                    attemptedAtUtc,
                    attemptedAtUtc.Add(GetPollRetryDelay(failureNumber)),
                    exception.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return new PollResult(0, true);
        }
    }

    private static TimeSpan GetPollRetryDelay(int failureNumber)
    {
        var minutes = failureNumber switch
        {
            <= 1 => 5,
            2 => 10,
            3 => 20,
            4 => 40,
            _ => 60,
        };
        return TimeSpan.FromMinutes(minutes);
    }

    private static DateTimeOffset SubtractClamped(DateTimeOffset timestamp, TimeSpan duration) =>
        timestamp <= DateTimeOffset.MinValue.Add(duration) ? DateTimeOffset.MinValue : timestamp - duration;

    private readonly record struct PollResult(int QueuedCount, bool Failed)
    {
        public static PollResult Noop => new(0, false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Steam poll failed for app {AppId}.")]
    private static partial void LogPollGameFailure(ILogger logger, Exception exception, uint appId);
}
