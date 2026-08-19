namespace SteamUpdateBot.Core.Domain;

public sealed record SteamAnnouncement(
    uint AppId,
    string Gid,
    string Title,
    string Url,
    string Content,
    string? Author,
    DateTimeOffset PublishedAtUtc);
