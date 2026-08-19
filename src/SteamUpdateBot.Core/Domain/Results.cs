namespace SteamUpdateBot.Core.Domain;

public enum AddGameOutcome
{
    Added,
    AlreadyMonitored,
    LimitReached,
    InvalidApp,
    SourceUnavailable,
}

public sealed record AddGameResult(AddGameOutcome Outcome, string Message, MonitoredGame? Game = null);

public enum RemoveGameOutcome
{
    Removed,
    NotMonitored,
}

public sealed record RemoveGameResult(RemoveGameOutcome Outcome, string Message);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record PollCycleResult(int GamesPolled, int AnnouncementsQueued, int Failures);

public sealed record DeliveryCycleResult(int Delivered, int Failed);

public enum PublishClaimOutcome
{
    Claimed,
    AlreadyPublishing,
    AlreadyComplete,
    NotFound,
}

public sealed record PublishClaimResult(PublishClaimOutcome Outcome, string? DiscordMessageId = null);

public sealed record BotStatusSnapshot(
    bool DiscordConnected,
    bool DatabaseReady,
    int MonitoredGameCount,
    int PendingDeliveryCount,
    int FailedDeliveryCount,
    DateTimeOffset? LastSuccessfulPollUtc,
    DateTimeOffset? OldestSuccessfulPollUtc,
    string? LastError,
    int AbandonedDeliveryCount = 0);
