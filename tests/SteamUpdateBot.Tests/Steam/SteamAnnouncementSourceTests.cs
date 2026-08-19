using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SteamUpdateBot.Core.Configuration;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Steam;

namespace SteamUpdateBot.Tests.Steam;

public sealed class SteamAnnouncementSourceTests
{
    [Fact]
    public void AddSteamUpdateBotSteamRegistersTheTypedAnnouncementSource()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        services.AddSteamUpdateBotSteam(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<SteamOptions>(provider.GetRequiredService<SteamOptions>());
        Assert.IsType<SteamAnnouncementSource>(provider.GetRequiredService<ISteamAnnouncementSource>());
    }

    [Fact]
    public void HttpClientTimeoutCoversTheFullRetryBudget()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(50),
            SteamServiceCollectionExtensions.GetHttpClientTimeout(new SteamOptions()));
    }

    [Fact]
    public async Task GetGameAsyncReturnsMatchingGameMetadataAndUsesEnglishStoreRequest()
    {
        var handler = new FakeHttpMessageHandler(
            JsonResponse(
                """
                {
                  "570": {
                    "success": true,
                    "data": {
                      "type": "game",
                      "name": "  Dota 2  ",
                      "header_image": "https://cdn.example/header.jpg"
                    }
                  }
                }
                """));
        using var client = new HttpClient(handler);
        var source = new SteamAnnouncementSource(client);

        var game = await source.GetGameAsync(570, TestContext.Current.CancellationToken);

        Assert.NotNull(game);
        Assert.Equal((uint)570, game.AppId);
        Assert.Equal("Dota 2", game.Name);
        Assert.Equal("https://store.steampowered.com/app/570/", game.StoreUrl);
        Assert.Equal("https://cdn.example/header.jpg", game.HeaderImageUrl);
        Assert.Single(handler.RequestUris);
        Assert.Equal("store.steampowered.com", handler.RequestUris[0].Host);
        Assert.Equal("570", GetQueryValue(handler.RequestUris[0], "appids"));
        Assert.Equal("english", GetQueryValue(handler.RequestUris[0], "l"));
    }

    [Theory]
    [InlineData("{\"570\":{\"success\":false}}")]
    [InlineData("{\"570\":{\"success\":true,\"data\":{\"type\":\"dlc\",\"name\":\"Extra Content\"}}}")]
    [InlineData("{\"570\":{\"success\":true,\"data\":{\"type\":\"game\",\"name\":\"   \"}}}")]
    [InlineData("{\"999\":{\"success\":true,\"data\":{\"type\":\"game\",\"name\":\"Different Game\"}}}")]
    public async Task GetGameAsyncReturnsNullWhenStorePayloadDoesNotDescribeTheRequestedGame(string payload)
    {
        var handler = new FakeHttpMessageHandler(JsonResponse(payload));
        using var client = new HttpClient(handler);
        var source = new SteamAnnouncementSource(client);

        var game = await source.GetGameAsync(570, TestContext.Current.CancellationToken);

        Assert.Null(game);
    }

    [Fact]
    public async Task GetGameAsyncWrapsTransportFailuresAsSourceUnavailable()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var source = new SteamAnnouncementSource(client);

        SteamSourceUnavailableException exception = await Assert.ThrowsAsync<SteamSourceUnavailableException>(
            () => source.GetGameAsync(570, TestContext.Current.CancellationToken));

        Assert.Contains("Store metadata request failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOfficialAnnouncementsSinceAsyncFiltersNonCommunityItemsDeduplicatesAndSortsOldestFirst()
    {
        var handler = new FakeHttpMessageHandler(
            JsonResponse(
                """
                {
                  "appnews": {
                    "newsitems": [
                      { "gid": "new", "title": "New", "url": "https://example/new", "contents": "new body", "author": "Author", "date": 300, "feedname": "steam_community_announcements" },
                      { "gid": "old", "title": "Old", "url": "https://example/old", "contents": "old body", "date": 200, "feedlabel": "Community Announcements" },
                      { "gid": "new", "title": "Duplicate", "url": "https://example/new", "contents": "duplicate", "date": 300, "feedname": "steam_community_announcements" },
                      { "gid": "third-party", "title": "Third party", "url": "https://example/third-party", "contents": "ignored", "date": 250, "feedname": "press" },
                      { "gid": "label-mismatch", "title": "Wrong label", "url": "https://example/wrong", "contents": "ignored", "date": 240, "feedlabel": "Steam News" }
                    ]
                  }
                }
                """));
        using var client = new HttpClient(handler);
        var source = new SteamAnnouncementSource(client);

        IReadOnlyList<SteamUpdateBot.Core.Domain.SteamAnnouncement> announcements = await source
            .GetOfficialAnnouncementsSinceAsync(570, DateTimeOffset.FromUnixTimeSeconds(100), TestContext.Current.CancellationToken);

        Assert.Collection(
            announcements,
            first =>
            {
                Assert.Equal("old", first.Gid);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(200), first.PublishedAtUtc);
                Assert.Null(first.Author);
            },
            second =>
            {
                Assert.Equal("new", second.Gid);
                Assert.Equal("New", second.Title);
                Assert.Equal("Author", second.Author);
            });

        Assert.Single(handler.RequestUris);
        Uri requestUri = handler.RequestUris[0];
        Assert.Equal("api.steampowered.com", requestUri.Host);
        Assert.Equal("570", GetQueryValue(requestUri, "appid"));
        Assert.Equal("50", GetQueryValue(requestUri, "count"));
        Assert.Equal("4096", GetQueryValue(requestUri, "maxlength"));
        Assert.Equal("steam_community_announcements", GetQueryValue(requestUri, "feeds"));
        Assert.NotNull(GetQueryValue(requestUri, "enddate"));
    }

    [Fact]
    public async Task GetOfficialAnnouncementsSinceAsyncPagesBackToTheRequestedBoundary()
    {
        object[] newestPage = Enumerable.Range(151, 50)
            .Reverse()
            .Select(date => NewsItem($"gid-{date}", date))
            .ToArray();
        object[] olderPage =
        [
            NewsItem("gid-150", 150),
            NewsItem("at-boundary", 100),
        ];
        var handler = new FakeHttpMessageHandler(
            JsonResponse(NewsResponse(newestPage)),
            JsonResponse(NewsResponse(olderPage)));
        using var client = new HttpClient(handler);
        var source = new SteamAnnouncementSource(client);

        IReadOnlyList<SteamUpdateBot.Core.Domain.SteamAnnouncement> announcements = await source
            .GetOfficialAnnouncementsSinceAsync(570, DateTimeOffset.FromUnixTimeSeconds(100), TestContext.Current.CancellationToken);

        Assert.Equal(51, announcements.Count);
        Assert.Equal("gid-150", announcements[0].Gid);
        Assert.Equal("gid-200", announcements[^1].Gid);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal("151", GetQueryValue(handler.RequestUris[1], "enddate"));
    }

    [Fact]
    public async Task GetOfficialAnnouncementsSinceAsyncIncludesAnnouncementsAtThePageBoundarySecond()
    {
        object[] newestPage = Enumerable.Range(151, 50)
            .Reverse()
            .Select(date => NewsItem($"gid-{date}", date))
            .ToArray();
        object[] boundaryPage =
        [
            NewsItem("gid-151", 151),
            NewsItem("same-second", 151),
            NewsItem("gid-150", 150),
        ];
        var handler = new FakeHttpMessageHandler(
            JsonResponse(NewsResponse(newestPage)),
            JsonResponse(NewsResponse(boundaryPage)));
        using var client = new HttpClient(handler);
        var source = new SteamAnnouncementSource(client);

        IReadOnlyList<SteamUpdateBot.Core.Domain.SteamAnnouncement> announcements = await source
            .GetOfficialAnnouncementsSinceAsync(570, DateTimeOffset.FromUnixTimeSeconds(100), TestContext.Current.CancellationToken);

        Assert.Equal(52, announcements.Count);
        Assert.Contains(announcements, announcement => announcement.Gid == "same-second");
        Assert.Equal("151", GetQueryValue(handler.RequestUris[1], "enddate"));
    }

    [Fact]
    public async Task GetOfficialAnnouncementsSinceAsyncFailsClosedWhenTheInclusiveCursorCannotAdvance()
    {
        object[] repeatedPage = Enumerable.Range(151, 50)
            .Reverse()
            .Select(date => NewsItem($"gid-{date}", date))
            .ToArray();
        var handler = new FakeHttpMessageHandler(
            JsonResponse(NewsResponse(repeatedPage)),
            JsonResponse(NewsResponse(repeatedPage)));
        using var client = new HttpClient(handler);
        var source = new SteamAnnouncementSource(client);

        SteamSourceUnavailableException exception = await Assert.ThrowsAsync<SteamSourceUnavailableException>(
            () => source.GetOfficialAnnouncementsSinceAsync(570, DateTimeOffset.FromUnixTimeSeconds(100), TestContext.Current.CancellationToken));

        Assert.Contains("could not safely advance", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal("151", GetQueryValue(handler.RequestUris[1], "enddate"));
    }

    [Fact]
    public async Task GetOfficialAnnouncementsSinceAsyncReturnsAnEmptyListForAnEmptyFeed()
    {
        var handler = new FakeHttpMessageHandler(JsonResponse("{\"appnews\":{\"newsitems\":[]}}"));
        using var client = new HttpClient(handler);
        var source = new SteamAnnouncementSource(client);

        IReadOnlyList<SteamUpdateBot.Core.Domain.SteamAnnouncement> announcements = await source
            .GetOfficialAnnouncementsSinceAsync(570, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

        Assert.Empty(announcements);
    }

    [Fact]
    public async Task GetOfficialAnnouncementsSinceAsyncWrapsInvalidJsonAsSourceUnavailable()
    {
        var handler = new FakeHttpMessageHandler(JsonResponse("not-json"));
        using var client = new HttpClient(handler);
        var source = new SteamAnnouncementSource(client);

        SteamSourceUnavailableException exception = await Assert.ThrowsAsync<SteamSourceUnavailableException>(
            () => source.GetOfficialAnnouncementsSinceAsync(570, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken));

        Assert.Contains("News response was not valid JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGameAsyncRejectsAResponseWhoseContentLengthExceedsTheStoreCap()
    {
        var response = JsonResponse("""{"570":{"success":true,"data":{"type":"game","name":"Dota 2"}}}""");
        response.Content.Headers.ContentLength = SteamAnnouncementSource.StoreResponseMaxBytes + 1;
        var handler = new FakeHttpMessageHandler(response);
        using var client = new HttpClient(handler);
        var source = new SteamAnnouncementSource(client);

        SteamSourceUnavailableException exception = await Assert.ThrowsAsync<SteamSourceUnavailableException>(
            () => source.GetGameAsync(570, TestContext.Current.CancellationToken));

        Assert.Contains("maximum allowed size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOfficialAnnouncementsSinceAsyncRejectsAStreamedBodyOverTheNewsCap()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new OversizedStreamContent(SteamAnnouncementSource.NewsResponseMaxBytes + 1),
        });
        using var client = new HttpClient(handler);
        var source = new SteamAnnouncementSource(client);

        SteamSourceUnavailableException exception = await Assert.ThrowsAsync<SteamSourceUnavailableException>(
            () => source.GetOfficialAnnouncementsSinceAsync(570, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken));

        Assert.Contains("maximum allowed size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOfficialAnnouncementsSinceAsyncFailsClosedAfterTheReducedPageCeiling()
    {
        HttpResponseMessage[] pages = Enumerable.Range(0, SteamAnnouncementSource.MaximumPageCount)
            .Select(page =>
            {
                var newest = 10_000 - (page * 50);
                object[] items = Enumerable.Range(0, 50)
                    .Select(offset => NewsItem($"gid-{newest - offset}", newest - offset))
                    .ToArray();
                return JsonResponse(NewsResponse(items));
            })
            .ToArray();
        var handler = new FakeHttpMessageHandler(pages);
        using var client = new HttpClient(handler);
        var source = new SteamAnnouncementSource(client);

        SteamSourceUnavailableException exception = await Assert.ThrowsAsync<SteamSourceUnavailableException>(
            () => source.GetOfficialAnnouncementsSinceAsync(570, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken));

        Assert.Contains("safety limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SteamAnnouncementSource.MaximumPageCount, handler.RequestUris.Count);
    }

    private static object NewsItem(string gid, long date) => new
    {
        gid,
        title = gid,
        url = $"https://example/{gid}",
        contents = $"{gid} contents",
        date,
        feedname = "steam_community_announcements",
    };

    private static string NewsResponse(object[] newsItems) => JsonSerializer.Serialize(new
    {
        appnews = new
        {
            newsitems = newsItems,
        },
    });

    private static HttpResponseMessage JsonResponse(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
    };

    private static string? GetQueryValue(Uri uri, string key)
    {
        foreach (string segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separatorIndex = segment.IndexOf('=');
            string segmentKey = separatorIndex < 0 ? segment : segment[..separatorIndex];
            if (!string.Equals(segmentKey, key, StringComparison.Ordinal))
            {
                continue;
            }

            return Uri.UnescapeDataString(separatorIndex < 0 ? string.Empty : segment[(separatorIndex + 1)..]);
        }

        return null;
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public FakeHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request.RequestUri);
            RequestUris.Add(request.RequestUri);

            if (!_responses.TryDequeue(out HttpResponseMessage? response))
            {
                throw new InvalidOperationException("The fake Steam handler received more requests than configured.");
            }

            return Task.FromResult(response);
        }
    }

    private sealed class OversizedStreamContent(int byteCount) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            // Valid JSON prefix so the serializer keeps reading until the byte ceiling is hit.
            await stream.WriteAsync("{"u8.ToArray()).ConfigureAwait(false);
            var buffer = new byte[8192];
            Array.Fill(buffer, (byte)' ');
            var remaining = byteCount;
            while (remaining > 0)
            {
                var chunk = Math.Min(buffer.Length, remaining);
                await stream.WriteAsync(buffer.AsMemory(0, chunk)).ConfigureAwait(false);
                remaining -= chunk;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
