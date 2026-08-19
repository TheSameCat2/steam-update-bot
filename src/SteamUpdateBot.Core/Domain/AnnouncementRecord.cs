namespace SteamUpdateBot.Core.Domain;

public sealed class AnnouncementRecord
{
    public uint AppId { get; init; }

    public string SteamGid { get; init; } = string.Empty;

    public string GameName { get; init; } = string.Empty;

    public string? HeaderImageUrl { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public string? Author { get; init; }

    public DateTimeOffset PublishedAtUtc { get; init; }

    public DateTimeOffset DetectedAtUtc { get; init; }

    public AnnouncementDeliveryState DeliveryState { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset NextAttemptUtc { get; set; }

    public DateTimeOffset? DeliveredAtUtc { get; set; }

    public string? DiscordMessageId { get; set; }

    public string? LastError { get; set; }
}
