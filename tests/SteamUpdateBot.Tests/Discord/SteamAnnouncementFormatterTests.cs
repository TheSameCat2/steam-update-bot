using SteamUpdateBot.Core.Domain;
using SteamUpdateBot.Core.Formatting;

namespace SteamUpdateBot.Tests.Discord;

public sealed class SteamAnnouncementFormatterTests
{
    private readonly SteamAnnouncementFormatter _formatter = new();

    [Fact]
    public void FormatConvertsSteamMarkupNormalizesWhitespaceAndProtectsMentionLikeText()
    {
        var announcement = CreateAnnouncement(
            title: "<b>Big&nbsp;Patch</b>",
            content: """
                [h1]Notes[/h1]
                <p>Fix <b>everything</b> &amp; enjoy @everyone.</p>
                [url=https://example.test]More details[/url]<br/>Final note
                <@123456789> [img]https://example.test/image.png[/img]
                <script>this must not appear</script>
                """);

        var formatted = _formatter.Format(announcement);

        Assert.Equal("Big Patch", formatted.Title);
        Assert.Contains("Notes Fix everything & enjoy @\u200Beveryone.", formatted.Description, StringComparison.Ordinal);
        Assert.Contains("More details", formatted.Description, StringComparison.Ordinal);
        Assert.Contains("More details Final note", formatted.Description, StringComparison.Ordinal);
        Assert.Contains("<@\u200B123456789>", formatted.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("this must not appear", formatted.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("image.png", formatted.Description, StringComparison.Ordinal);
        Assert.Contains("<https://steamcommunity.com/games/570/announcements/detail/123>", formatted.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatKeepsEveryEmbedTextFieldWithinDiscordLimitsWithoutSplittingUnicode()
    {
        var repeatedEmoji = string.Concat(Enumerable.Repeat("😀", 5_000));
        var announcement = CreateAnnouncement(
            title: repeatedEmoji,
            content: repeatedEmoji,
            gameName: repeatedEmoji,
            author: repeatedEmoji);

        var formatted = _formatter.Format(announcement);
        var aggregateLength = formatted.Title.Length
            + formatted.Description.Length
            + formatted.GameName.Length
            + formatted.Footer.Length;

        Assert.True(formatted.Title.Length <= SteamAnnouncementFormatter.MaxTitleLength);
        Assert.True(formatted.Description.Length <= SteamAnnouncementFormatter.MaxDescriptionLength);
        Assert.True(formatted.GameName.Length <= SteamAnnouncementFormatter.MaxGameNameLength);
        Assert.True(formatted.Footer.Length <= SteamAnnouncementFormatter.MaxFooterLength);
        Assert.True(aggregateLength <= 6_000);
        Assert.Equal('…', formatted.Title[^1]);
        Assert.False(char.IsHighSurrogate(formatted.Title[^2]));
        Assert.Equal('…', formatted.Description[^("Read the full Steam announcement: <https://steamcommunity.com/games/570/announcements/detail/123>".Length + 3)]);
    }

    [Fact]
    public void FormatUsesSafeStoreUrlWhenAnnouncementUrlIsNotHttp()
    {
        var announcement = CreateAnnouncement(url: "javascript:alert('no')", headerImageUrl: "ftp://example.test/image.png");

        var formatted = _formatter.Format(announcement);

        Assert.Equal("https://store.steampowered.com/app/570/", formatted.Url);
        Assert.Null(formatted.ImageUrl);
        Assert.Contains("<https://store.steampowered.com/app/570/>", formatted.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateUnicodeReturnsAnEmptyStringForZeroLength()
    {
        Assert.Equal(string.Empty, SteamAnnouncementFormatter.TruncateUnicode("😀", 0, "fallback"));
    }

    private static AnnouncementRecord CreateAnnouncement(
        string title = "Update",
        string content = "Patch notes",
        string gameName = "Dota 2",
        string? author = "Valve",
        string url = "https://steamcommunity.com/games/570/announcements/detail/123",
        string? headerImageUrl = "https://cdn.example.test/header.png") =>
        new()
        {
            AppId = 570,
            SteamGid = "123",
            GameName = gameName,
            HeaderImageUrl = headerImageUrl,
            Title = title,
            Url = url,
            Content = content,
            Author = author,
            PublishedAtUtc = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
            DetectedAtUtc = new DateTimeOffset(2026, 8, 16, 12, 1, 0, TimeSpan.Zero),
            DeliveryState = AnnouncementDeliveryState.Pending,
            NextAttemptUtc = new DateTimeOffset(2026, 8, 16, 12, 1, 0, TimeSpan.Zero),
        };
}
