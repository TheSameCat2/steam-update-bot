using System.Globalization;
using System.Net;
using System.Text.Json;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Steam;

/// <summary>
/// Retrieves Steam store metadata and official Steam community announcements.
/// </summary>
public sealed class SteamAnnouncementSource : ISteamAnnouncementSource
{
    public const int StoreResponseMaxBytes = 256 * 1024;
    public const int NewsResponseMaxBytes = 1024 * 1024;
    public const int MaximumPageCount = 20;

    private const int AnnouncementPageSize = 50;
    private const int AnnouncementMaximumLength = 4096;
    private const string CommunityAnnouncementsFeed = "steam_community_announcements";
    private const string CommunityAnnouncementsLabel = "Community Announcements";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public SteamAnnouncementSource(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<SteamAppMetadata?> GetGameAsync(uint appId, CancellationToken cancellationToken)
    {
        if (appId == 0)
        {
            return null;
        }

        var requestUri = new Uri(
            $"https://store.steampowered.com/api/appdetails?appids={appId.ToString(CultureInfo.InvariantCulture)}&l=english",
            UriKind.Absolute);

        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            Dictionary<string, SteamStoreAppResponse>? payload = await DeserializeBoundedJsonAsync<Dictionary<string, SteamStoreAppResponse>>(
                    response,
                    StoreResponseMaxBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            if (payload is null ||
                !payload.TryGetValue(appId.ToString(CultureInfo.InvariantCulture), out SteamStoreAppResponse? appResponse) ||
                !appResponse.Success ||
                appResponse.Data is null ||
                !string.Equals(appResponse.Data.Type, "game", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(appResponse.Data.Name))
            {
                return null;
            }

            return new SteamAppMetadata(
                appId,
                appResponse.Data.Name.Trim(),
                $"https://store.steampowered.com/app/{appId.ToString(CultureInfo.InvariantCulture)}/",
                NormalizeOptionalText(appResponse.Data.HeaderImage));
        }
        catch (SteamSourceUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SteamSourceUnavailableException("Steam Store metadata request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new SteamSourceUnavailableException("Steam Store metadata request failed.", exception);
        }
        catch (JsonException exception)
        {
            throw new SteamSourceUnavailableException("Steam Store metadata response was not valid JSON.", exception);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SteamAnnouncement>> GetOfficialAnnouncementsSinceAsync(
        uint appId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        if (appId == 0)
        {
            return Array.Empty<SteamAnnouncement>();
        }

        DateTimeOffset normalizedSinceUtc = sinceUtc.ToUniversalTime();
        long endDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var announcementsByGid = new Dictionary<string, SteamAnnouncement>(StringComparer.Ordinal);
        var reachedRequestedBoundary = false;

        for (var pageNumber = 0; pageNumber < MaximumPageCount; pageNumber++)
        {
            SteamNewsResponse newsResponse = await GetNewsPageAsync(appId, endDate, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<SteamNewsItem> newsItems = newsResponse.AppNews?.NewsItems ?? [];

            if (newsItems.Count == 0)
            {
                reachedRequestedBoundary = true;
                break;
            }

            long? oldestItemDate = null;

            foreach (SteamNewsItem newsItem in newsItems)
            {
                if (newsItem.Date <= 0)
                {
                    continue;
                }

                oldestItemDate = oldestItemDate is null
                    ? newsItem.Date
                    : Math.Min(oldestItemDate.Value, newsItem.Date);

                if (!IsCommunityAnnouncement(newsItem) ||
                    !TryCreateAnnouncement(appId, newsItem, out SteamAnnouncement announcement) ||
                    announcement.PublishedAtUtc <= normalizedSinceUtc)
                {
                    continue;
                }

                if (announcementsByGid.TryGetValue(announcement.Gid, out SteamAnnouncement? current))
                {
                    announcementsByGid[announcement.Gid] = SelectMostRecent(current, announcement);
                }
                else
                {
                    announcementsByGid.Add(announcement.Gid, announcement);
                }
            }

            if (oldestItemDate is null ||
                oldestItemDate.Value <= normalizedSinceUtc.ToUnixTimeSeconds() ||
                newsItems.Count < AnnouncementPageSize ||
                oldestItemDate.Value <= 0)
            {
                reachedRequestedBoundary = true;
                break;
            }

            // Steam's cursor is second-granular and inclusive. Reuse the boundary second so
            // announcements sharing it are deduplicated instead of silently being skipped.
            long nextEndDate = oldestItemDate.Value;
            if (nextEndDate >= endDate)
            {
                throw new SteamSourceUnavailableException(
                    "Steam News pagination could not safely advance past an announcement timestamp boundary.");
            }

            endDate = nextEndDate;
        }

        if (!reachedRequestedBoundary)
        {
            throw new SteamSourceUnavailableException(
                "Steam News pagination reached its safety limit before reaching the requested time boundary.");
        }

        return announcementsByGid.Values
            .OrderBy(announcement => announcement.PublishedAtUtc)
            .ThenBy(announcement => announcement.Gid, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<SteamNewsResponse> GetNewsPageAsync(
        uint appId,
        long endDate,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri(
            string.Create(
                CultureInfo.InvariantCulture,
                $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={appId}&count={AnnouncementPageSize}&maxlength={AnnouncementMaximumLength}&feeds={CommunityAnnouncementsFeed}&enddate={endDate}"),
            UriKind.Absolute);

        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            SteamNewsResponse? payload = await DeserializeBoundedJsonAsync<SteamNewsResponse>(
                    response,
                    NewsResponseMaxBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            return payload ?? throw new SteamSourceUnavailableException("Steam News response was empty.");
        }
        catch (SteamSourceUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SteamSourceUnavailableException("Steam News request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new SteamSourceUnavailableException("Steam News request failed.", exception);
        }
        catch (JsonException exception)
        {
            throw new SteamSourceUnavailableException("Steam News response was not valid JSON.", exception);
        }
    }

    private static async Task<T?> DeserializeBoundedJsonAsync<T>(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            throw new SteamSourceUnavailableException("Steam response exceeded the maximum allowed size.");
        }

        await using var bounded = new BoundedReadStream(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            maxBytes);
        return await JsonSerializer.DeserializeAsync<T>(bounded, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsCommunityAnnouncement(SteamNewsItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.FeedName))
        {
            return string.Equals(item.FeedName, CommunityAnnouncementsFeed, StringComparison.OrdinalIgnoreCase);
        }

        return string.IsNullOrWhiteSpace(item.FeedLabel) ||
            string.Equals(item.FeedLabel, CommunityAnnouncementsLabel, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateAnnouncement(uint appId, SteamNewsItem item, out SteamAnnouncement announcement)
    {
        announcement = null!;

        if (string.IsNullOrWhiteSpace(item.Gid) || item.Date <= 0)
        {
            return false;
        }

        try
        {
            announcement = new SteamAnnouncement(
                appId,
                item.Gid.Trim(),
                item.Title?.Trim() ?? string.Empty,
                item.Url?.Trim() ?? string.Empty,
                item.Contents ?? string.Empty,
                NormalizeOptionalText(item.Author),
                DateTimeOffset.FromUnixTimeSeconds(item.Date));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static SteamAnnouncement SelectMostRecent(
        SteamAnnouncement current,
        SteamAnnouncement candidate)
    {
        if (candidate.PublishedAtUtc > current.PublishedAtUtc)
        {
            return candidate;
        }

        if (candidate.PublishedAtUtc < current.PublishedAtUtc)
        {
            return current;
        }

        return string.CompareOrdinal(candidate.Url, current.Url) > 0 ? candidate : current;
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
