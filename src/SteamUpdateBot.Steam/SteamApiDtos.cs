using System.Text.Json.Serialization;

namespace SteamUpdateBot.Steam;

internal sealed class SteamStoreAppResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("data")]
    public SteamStoreAppData? Data { get; init; }
}

internal sealed class SteamStoreAppData
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("header_image")]
    public string? HeaderImage { get; init; }
}

internal sealed class SteamNewsResponse
{
    [JsonPropertyName("appnews")]
    public SteamAppNews? AppNews { get; init; }
}

internal sealed class SteamAppNews
{
    [JsonPropertyName("newsitems")]
    public IReadOnlyList<SteamNewsItem>? NewsItems { get; init; }
}

internal sealed class SteamNewsItem
{
    [JsonPropertyName("gid")]
    public string? Gid { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("contents")]
    public string? Contents { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("feedlabel")]
    public string? FeedLabel { get; init; }

    [JsonPropertyName("feedname")]
    public string? FeedName { get; init; }

    [JsonPropertyName("date")]
    public long Date { get; init; }
}
