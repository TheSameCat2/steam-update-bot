using SteamUpdateBot.Core.Configuration;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Core.Services;

public sealed class SubscriptionService : ISubscriptionService, IDisposable
{
    private readonly SemaphoreSlim _addLock = new(1, 1);
    private readonly IBotRepository _repository;
    private readonly ISteamAnnouncementSource _steamSource;
    private readonly SteamOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IAppIdLockProvider _appLocks;
    private readonly IBotRuntimeState _runtimeState;

    public SubscriptionService(
        IBotRepository repository,
        ISteamAnnouncementSource steamSource,
        SteamOptions options,
        TimeProvider timeProvider,
        IAppIdLockProvider appLocks,
        IBotRuntimeState runtimeState)
    {
        _repository = repository;
        _steamSource = steamSource;
        _options = options;
        _timeProvider = timeProvider;
        _appLocks = appLocks;
        _runtimeState = runtimeState;
    }

    public async Task<AddGameResult> AddGameAsync(
        uint appId,
        string actorDiscordUserId,
        CancellationToken cancellationToken)
    {
        if (appId == 0)
        {
            return new AddGameResult(AddGameOutcome.InvalidApp, "Steam App ID must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(actorDiscordUserId))
        {
            return new AddGameResult(AddGameOutcome.InvalidApp, "The Discord user ID is required.");
        }

        await _addLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var appLock = await _appLocks.AcquireAsync(appId, cancellationToken).ConfigureAwait(false);

            if (await _repository.GetGameAsync(appId, cancellationToken).ConfigureAwait(false) is not null)
            {
                return new AddGameResult(AddGameOutcome.AlreadyMonitored, "That Steam game is already monitored.");
            }

            if (await _repository.GetGameCountAsync(cancellationToken).ConfigureAwait(false) >= _options.MaxGames)
            {
                return new AddGameResult(AddGameOutcome.LimitReached, $"The monitor limit of {_options.MaxGames} games has been reached.");
            }

            SteamAppMetadata? metadata;
            IReadOnlyList<SteamAnnouncement> baseline;
            var nowUtc = _timeProvider.GetUtcNow();

            try
            {
                metadata = await _steamSource.GetGameAsync(appId, cancellationToken).ConfigureAwait(false);
                if (metadata is null || metadata.AppId != appId || string.IsNullOrWhiteSpace(metadata.Name))
                {
                    return new AddGameResult(AddGameOutcome.InvalidApp, "Steam could not find a monitorable game for that App ID.");
                }

                baseline = await _steamSource.GetOfficialAnnouncementsSinceAsync(
                        appId,
                        SubtractClamped(nowUtc, TimeSpan.FromHours(_options.OverlapHours)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return new AddGameResult(AddGameOutcome.SourceUnavailable, "Steam is unavailable; no game was added.");
            }

            var game = new MonitoredGame
            {
                AppId = metadata.AppId,
                Name = metadata.Name,
                StoreUrl = metadata.StoreUrl,
                HeaderImageUrl = metadata.HeaderImageUrl,
                AddedByDiscordUserId = actorDiscordUserId,
                AddedAtUtc = nowUtc,
                ScanWatermarkUtc = nowUtc,
                NextPollUtc = nowUtc.Add(GetInitialPollOffset(appId)),
            };

            if (!await _repository.TryAddGameAsync(game, baseline, cancellationToken).ConfigureAwait(false))
            {
                return new AddGameResult(AddGameOutcome.AlreadyMonitored, "That Steam game is already monitored.");
            }

            return new AddGameResult(AddGameOutcome.Added, $"Now monitoring {game.Name} ({game.AppId}).", game);
        }
        finally
        {
            _addLock.Release();
        }
    }

    public async Task<RemoveGameResult> RemoveGameAsync(uint appId, CancellationToken cancellationToken)
    {
        if (appId == 0)
        {
            return new RemoveGameResult(RemoveGameOutcome.NotMonitored, "That Steam game is not monitored.");
        }

        await using var appLock = await _appLocks.AcquireAsync(appId, cancellationToken).ConfigureAwait(false);
        var removed = await _repository.RemoveGameAsync(appId, cancellationToken).ConfigureAwait(false);
        return removed
            ? new RemoveGameResult(RemoveGameOutcome.Removed, "Stopped monitoring that Steam game.")
            : new RemoveGameResult(RemoveGameOutcome.NotMonitored, "That Steam game is not monitored.");
    }

    public Task<PagedResult<MonitoredGame>> ListGamesAsync(int page, int pageSize, CancellationToken cancellationToken) =>
        _repository.ListGamesAsync(page, pageSize, cancellationToken);

    public Task<BotStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken) =>
        _repository.GetStatusAsync(_runtimeState.DiscordConnected, cancellationToken);

    public void Dispose() => _addLock.Dispose();

    private TimeSpan GetInitialPollOffset(uint appId)
    {
        var interval = TimeSpan.FromMinutes(_options.PollIntervalMinutes);
        var slots = Math.Max(_options.MaxGames, 1);
        var slot = (appId % (uint)slots) + 1;
        return TimeSpan.FromTicks(interval.Ticks * slot / slots);
    }

    private static DateTimeOffset SubtractClamped(DateTimeOffset timestamp, TimeSpan duration) =>
        timestamp <= DateTimeOffset.MinValue.Add(duration) ? DateTimeOffset.MinValue : timestamp - duration;
}
