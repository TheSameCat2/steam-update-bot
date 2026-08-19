namespace SteamUpdateBot.Core.Formatting;

/// <summary>
/// A Discord-neutral representation of an announcement that is safe to render in an embed.
/// </summary>
public sealed record FormattedSteamAnnouncement(
    string Title,
    string Description,
    string Url,
    string GameName,
    string Footer,
    string? ImageUrl,
    DateTimeOffset PublishedAtUtc);
