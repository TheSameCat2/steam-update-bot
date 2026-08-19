using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Core.Formatting;

/// <summary>
/// Converts Steam announcement markup into a bounded, Discord-safe presentation model.
/// </summary>
public sealed partial class SteamAnnouncementFormatter
{
    public const int MaxTitleLength = 256;
    public const int MaxDescriptionLength = 3_500;
    public const int MaxGameNameLength = 256;
    public const int MaxFooterLength = 256;

    private const string FallbackDescription = "No patch notes were provided. Read the full Steam announcement.";
    private const string FullAnnouncementPrefix = "Read the full Steam announcement: ";
    private readonly HtmlParser _htmlParser = new();

    public FormattedSteamAnnouncement Format(AnnouncementRecord announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);

        var gameName = TruncateUnicode(
            NormalizeText(announcement.GameName),
            MaxGameNameLength,
            $"Steam App {announcement.AppId}");
        var title = TruncateUnicode(
            NormalizeText(announcement.Title),
            MaxTitleLength,
            $"Steam update for {gameName}");
        var url = NormalizeHttpUrl(
            announcement.Url,
            $"https://store.steampowered.com/app/{announcement.AppId}/");
        var link = FullAnnouncementPrefix + "<" + url + ">";
        var contentBudget = Math.Max(0, MaxDescriptionLength - link.Length - 2);
        var notes = TruncateUnicode(NormalizeText(announcement.Content), contentBudget, FallbackDescription);
        var description = notes.Length == 0
            ? TruncateUnicode(FallbackDescription, contentBudget, string.Empty)
            : notes;

        if (link.Length + description.Length + 2 <= MaxDescriptionLength)
        {
            description += "\n\n" + link;
        }

        var footer = $"Steam • App ID {announcement.AppId}";
        var author = NormalizeText(announcement.Author);
        if (author.Length > 0)
        {
            footer += " • " + author;
        }

        return new FormattedSteamAnnouncement(
            title,
            description,
            url,
            gameName,
            TruncateUnicode(footer, MaxFooterLength, $"Steam • App ID {announcement.AppId}"),
            NormalizeOptionalHttpUrl(announcement.HeaderImageUrl),
            announcement.PublishedAtUtc);
    }

    /// <summary>
    /// Truncates by Unicode scalar rather than by UTF-16 code unit, avoiding split surrogate pairs.
    /// </summary>
    public static string TruncateUnicode(string? value, int maximumLength, string fallback)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);

        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (source.Length <= maximumLength)
        {
            return source;
        }

        if (maximumLength == 0)
        {
            return string.Empty;
        }

        const string ellipsis = "…";
        var contentLength = Math.Max(0, maximumLength - ellipsis.Length);
        var builder = new StringBuilder(contentLength + ellipsis.Length);
        var writtenLength = 0;

        foreach (var rune in source.EnumerateRunes())
        {
            if (writtenLength + rune.Utf16SequenceLength > contentLength)
            {
                break;
            }

            builder.Append(rune.ToString());
            writtenLength += rune.Utf16SequenceLength;
        }

        builder.Append('…');
        return builder.ToString();
    }

    private string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutSteamMarkup = StripSteamBbCode(value);
        var document = _htmlParser.ParseDocument(HtmlBlockSeparatorRegex().Replace(withoutSteamMarkup, " "));

        foreach (var element in document.QuerySelectorAll("script, style, noscript"))
        {
            element.Remove();
        }

        var text = document.Body?.TextContent ?? document.DocumentElement?.TextContent ?? string.Empty;
        return EscapePotentialMentions(WhitespaceRegex().Replace(text, " ").Trim());
    }

    private static string StripSteamBbCode(string value)
    {
        var withoutMedia = MediaBbCodeRegex().Replace(value, string.Empty);
        var withoutUrls = UrlWithValueBbCodeRegex().Replace(withoutMedia, static match => match.Groups["text"].Value);
        withoutUrls = UrlBbCodeRegex().Replace(withoutUrls, static match => match.Groups["text"].Value);
        return FormattingBbCodeRegex().Replace(withoutUrls, " ");
    }

    private static string EscapePotentialMentions(string value)
    {
        var withoutBroadcastMentions = BroadcastMentionRegex().Replace(value, "@\u200B$1");
        return UserOrRoleMentionRegex().Replace(withoutBroadcastMentions, "<@\u200B$1>");
    }

    private static string NormalizeHttpUrl(string? value, string fallback)
    {
        var candidate = NormalizeOptionalHttpUrl(value);
        return candidate ?? fallback;
    }

    private static string? NormalizeOptionalHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2_000
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    [GeneratedRegex(@"(?is)\[(?:img|video|youtube)[^\]]*\].*?\[/(?:img|video|youtube)\]", RegexOptions.CultureInvariant)]
    private static partial Regex MediaBbCodeRegex();

    [GeneratedRegex(@"(?is)\[url=[^\]]+\](?<text>.*?)\[/url\]", RegexOptions.CultureInvariant)]
    private static partial Regex UrlWithValueBbCodeRegex();

    [GeneratedRegex(@"(?is)\[url\](?<text>.*?)\[/url\]", RegexOptions.CultureInvariant)]
    private static partial Regex UrlBbCodeRegex();

    [GeneratedRegex(@"(?is)\[/?(?:b|i|u|s|strike|h[1-6]|p|list|quote|code|spoiler|table|tr|td|th|hr|center|left|right|noparse|color|size|font)(?:=[^\]]+)?\]|\[\*\]", RegexOptions.CultureInvariant)]
    private static partial Regex FormattingBbCodeRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?is)<\s*/?\s*(?:br|p|div|li|h[1-6]|tr|td|th|blockquote|pre|hr)\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlBlockSeparatorRegex();

    [GeneratedRegex(@"(?i)@(everyone|here)\b", RegexOptions.CultureInvariant)]
    private static partial Regex BroadcastMentionRegex();

    [GeneratedRegex(@"<@(&?\d+)>", RegexOptions.CultureInvariant)]
    private static partial Regex UserOrRoleMentionRegex();
}
