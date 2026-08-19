using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Core.Contracts;

public interface IBotRepository
{
    Task<MonitoredGame?> GetGameAsync(uint appId, CancellationToken cancellationToken);

    Task<int> GetGameCountAsync(CancellationToken cancellationToken);

    Task<bool> TryAddGameAsync(
        MonitoredGame game,
        IReadOnlyList<SteamAnnouncement> suppressedAnnouncements,
        CancellationToken cancellationToken);

    Task<bool> RemoveGameAsync(uint appId, CancellationToken cancellationToken);

    Task<PagedResult<MonitoredGame>> ListGamesAsync(int page, int pageSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<MonitoredGame>> GetDueGamesAsync(
        DateTimeOffset nowUtc,
        int maxGames,
        CancellationToken cancellationToken);

    Task<PollCommitResult> CommitPollAsync(
        uint appId,
        DateTimeOffset pollStartedUtc,
        DateTimeOffset nextPollUtc,
        IReadOnlyList<SteamAnnouncement> announcements,
        CancellationToken cancellationToken);

    Task RecordPollFailureAsync(
        uint appId,
        DateTimeOffset attemptedAtUtc,
        DateTimeOffset nextPollUtc,
        string failureMessage,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AnnouncementRecord>> GetDueAnnouncementsAsync(
        DateTimeOffset nowUtc,
        int maxCount,
        CancellationToken cancellationToken);

    Task<PublishClaimResult> TryClaimForPublishAsync(
        uint appId,
        string steamGid,
        CancellationToken cancellationToken);

    Task RecordPublishedMessageIdAsync(
        uint appId,
        string steamGid,
        string discordMessageId,
        CancellationToken cancellationToken);

    Task MarkDeliveredAsync(
        uint appId,
        string steamGid,
        string discordMessageId,
        DateTimeOffset deliveredAtUtc,
        CancellationToken cancellationToken);

    Task RecordDeliveryFailureAsync(
        uint appId,
        string steamGid,
        DateTimeOffset nextAttemptUtc,
        string failureMessage,
        CancellationToken cancellationToken,
        bool incrementAttempt = true);

    Task MarkDeliveryAbandonedAsync(
        uint appId,
        string steamGid,
        string failureMessage,
        CancellationToken cancellationToken);

    Task<BotStatusSnapshot> GetStatusAsync(bool discordConnected, CancellationToken cancellationToken);
}

public sealed record PollCommitResult(int QueuedCount, bool GameStillMonitored);
