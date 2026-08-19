namespace SteamUpdateBot.Core.Domain;

public sealed record SteamAppMetadata(
    uint AppId,
    string Name,
    string StoreUrl,
    string? HeaderImageUrl);
