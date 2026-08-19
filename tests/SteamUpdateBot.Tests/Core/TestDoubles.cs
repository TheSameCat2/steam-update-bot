using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Tests.Core;

internal sealed class MutableTimeProvider(DateTimeOffset nowUtc) : TimeProvider
{
    public DateTimeOffset NowUtc { get; set; } = nowUtc;

    public override DateTimeOffset GetUtcNow() => NowUtc;
}

internal sealed class FakeSteamAnnouncementSource : ISteamAnnouncementSource
{
    public SteamAppMetadata? Metadata { get; set; }

    public IReadOnlyList<SteamAnnouncement> Announcements { get; set; } = [];

    public Exception? GameException { get; set; }

    public Exception? AnnouncementsException { get; set; }

    public int GameRequests { get; private set; }

    public int AnnouncementRequests { get; private set; }

    public Task<SteamAppMetadata?> GetGameAsync(uint appId, CancellationToken cancellationToken)
    {
        GameRequests++;
        if (GameException is not null)
        {
            throw GameException;
        }

        return Task.FromResult(Metadata);
    }

    public Task<IReadOnlyList<SteamAnnouncement>> GetOfficialAnnouncementsSinceAsync(
        uint appId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        AnnouncementRequests++;
        if (AnnouncementsException is not null)
        {
            throw AnnouncementsException;
        }

        return Task.FromResult(Announcements);
    }
}

internal sealed class FakeAnnouncementPublisher : IAnnouncementPublisher
{
    public Exception? Exception { get; set; }

    public string MessageId { get; set; } = "discord-message";

    public Queue<Exception?> Results { get; } = new();

    public List<string> AttemptedGids { get; } = [];

    public List<string> PublishedGids { get; } = [];

    public Task<string> PublishAsync(AnnouncementRecord announcement, CancellationToken cancellationToken)
    {
        AttemptedGids.Add(announcement.SteamGid);
        if (Results.TryDequeue(out Exception? result) && result is not null)
        {
            throw result;
        }

        if (Exception is not null)
        {
            throw Exception;
        }

        PublishedGids.Add(announcement.SteamGid);
        return Task.FromResult(MessageId);
    }
}

internal sealed class FailOnceMarkDeliveredRepository(IBotRepository inner) : IBotRepository
{
    private int _markDeliveredCalls;

    public int MarkDeliveredCalls => _markDeliveredCalls;

    public Task<MonitoredGame?> GetGameAsync(uint appId, CancellationToken cancellationToken) =>
        inner.GetGameAsync(appId, cancellationToken);

    public Task<int> GetGameCountAsync(CancellationToken cancellationToken) =>
        inner.GetGameCountAsync(cancellationToken);

    public Task<bool> TryAddGameAsync(
        MonitoredGame game,
        IReadOnlyList<SteamAnnouncement> suppressedAnnouncements,
        CancellationToken cancellationToken) =>
        inner.TryAddGameAsync(game, suppressedAnnouncements, cancellationToken);

    public Task<bool> RemoveGameAsync(uint appId, CancellationToken cancellationToken) =>
        inner.RemoveGameAsync(appId, cancellationToken);

    public Task<PagedResult<MonitoredGame>> ListGamesAsync(int page, int pageSize, CancellationToken cancellationToken) =>
        inner.ListGamesAsync(page, pageSize, cancellationToken);

    public Task<IReadOnlyList<MonitoredGame>> GetDueGamesAsync(
        DateTimeOffset nowUtc,
        int maxGames,
        CancellationToken cancellationToken) =>
        inner.GetDueGamesAsync(nowUtc, maxGames, cancellationToken);

    public Task<PollCommitResult> CommitPollAsync(
        uint appId,
        DateTimeOffset pollStartedUtc,
        DateTimeOffset nextPollUtc,
        IReadOnlyList<SteamAnnouncement> announcements,
        CancellationToken cancellationToken) =>
        inner.CommitPollAsync(appId, pollStartedUtc, nextPollUtc, announcements, cancellationToken);

    public Task RecordPollFailureAsync(
        uint appId,
        DateTimeOffset attemptedAtUtc,
        DateTimeOffset nextPollUtc,
        string failureMessage,
        CancellationToken cancellationToken) =>
        inner.RecordPollFailureAsync(appId, attemptedAtUtc, nextPollUtc, failureMessage, cancellationToken);

    public Task<IReadOnlyList<AnnouncementRecord>> GetDueAnnouncementsAsync(
        DateTimeOffset nowUtc,
        int maxCount,
        CancellationToken cancellationToken) =>
        inner.GetDueAnnouncementsAsync(nowUtc, maxCount, cancellationToken);

    public Task<PublishClaimResult> TryClaimForPublishAsync(
        uint appId,
        string steamGid,
        CancellationToken cancellationToken) =>
        inner.TryClaimForPublishAsync(appId, steamGid, cancellationToken);

    public Task RecordPublishedMessageIdAsync(
        uint appId,
        string steamGid,
        string discordMessageId,
        CancellationToken cancellationToken) =>
        inner.RecordPublishedMessageIdAsync(appId, steamGid, discordMessageId, cancellationToken);

    public Task MarkDeliveredAsync(
        uint appId,
        string steamGid,
        string discordMessageId,
        DateTimeOffset deliveredAtUtc,
        CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _markDeliveredCalls);
        if (call == 1)
        {
            throw new InvalidOperationException("The delivery mark failed after Discord accepted the message.");
        }

        return inner.MarkDeliveredAsync(appId, steamGid, discordMessageId, deliveredAtUtc, cancellationToken);
    }

    public Task RecordDeliveryFailureAsync(
        uint appId,
        string steamGid,
        DateTimeOffset nextAttemptUtc,
        string failureMessage,
        CancellationToken cancellationToken,
        bool incrementAttempt = true) =>
        inner.RecordDeliveryFailureAsync(
            appId,
            steamGid,
            nextAttemptUtc,
            failureMessage,
            cancellationToken,
            incrementAttempt);

    public Task MarkDeliveryAbandonedAsync(
        uint appId,
        string steamGid,
        string failureMessage,
        CancellationToken cancellationToken) =>
        inner.MarkDeliveryAbandonedAsync(appId, steamGid, failureMessage, cancellationToken);

    public Task<BotStatusSnapshot> GetStatusAsync(bool discordConnected, CancellationToken cancellationToken) =>
        inner.GetStatusAsync(discordConnected, cancellationToken);
}

internal sealed class ConnectedRuntimeState : IBotRuntimeState
{
    public bool DiscordConnected => true;

    public DateTimeOffset? DiscordDisconnectedSinceUtc => null;
}
