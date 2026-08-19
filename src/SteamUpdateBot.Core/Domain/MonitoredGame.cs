namespace SteamUpdateBot.Core.Domain;

public sealed class MonitoredGame
{
    public uint AppId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string StoreUrl { get; init; } = string.Empty;

    public string? HeaderImageUrl { get; init; }

    public string AddedByDiscordUserId { get; init; } = string.Empty;

    public DateTimeOffset AddedAtUtc { get; init; }

    public DateTimeOffset ScanWatermarkUtc { get; set; }

    public DateTimeOffset? LastPollAttemptUtc { get; set; }

    public DateTimeOffset? LastPollSuccessUtc { get; set; }

    public DateTimeOffset NextPollUtc { get; set; }

    public int ConsecutiveFailures { get; set; }

    public string? LastError { get; set; }
}
