using System.Globalization;
using System.Net.Http;
using global::Discord;
using global::Discord.Net;
using global::Discord.WebSocket;
using Microsoft.Extensions.Options;
using SteamUpdateBot.Core.Configuration;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Domain;
using SteamUpdateBot.Core.Formatting;

namespace SteamUpdateBot.App.Discord;

/// <summary>
/// Publishes the durable announcement outbox into the configured Discord channel.
/// </summary>
public sealed class DiscordAnnouncementPublisher : IAnnouncementPublisher
{
    private readonly DiscordSocketClient _client;
    private readonly DiscordOptions _options;
    private readonly SteamAnnouncementFormatter _formatter;

    public DiscordAnnouncementPublisher(
        DiscordSocketClient client,
        IOptions<DiscordOptions> options,
        SteamAnnouncementFormatter formatter)
    {
        _client = client;
        _options = options.Value;
        _formatter = formatter;
    }

    public async Task<string> PublishAsync(AnnouncementRecord announcement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(announcement);

        try
        {
            var channel = _client.GetChannel(_options.AnnouncementChannelId) as IMessageChannel;
            if (channel is null)
            {
                channel = await _client.GetChannelAsync(
                    _options.AnnouncementChannelId,
                    new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false) as IMessageChannel;
            }

            // The REST fallback above is authoritative, so a still-unresolved channel means it
            // was deleted or the bot lost access. Treating that as transient would retry forever
            // without ever incrementing the attempt count, so let it reach the poison budget.
            if (channel is null)
            {
                throw new InvalidOperationException(
                    $"Configured announcement channel {_options.AnnouncementChannelId} is unavailable or cannot receive messages.");
            }

            var formatted = _formatter.Format(announcement);
            var embed = CreateEmbed(formatted);
            var message = await channel.SendMessageAsync(
                embed: embed,
                options: new RequestOptions { CancelToken = cancellationToken },
                allowedMentions: AllowedMentions.None).ConfigureAwait(false);

            return message.Id.ToString(CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PublisherTransientException)
        {
            throw;
        }
        catch (HttpException exception) when (IsTransient(exception))
        {
            throw new PublisherTransientException("Discord rejected the announcement with a transient HTTP error.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PublisherTransientException("The Discord HTTP request failed.", exception);
        }
        catch (TimeoutException exception)
        {
            throw new PublisherTransientException("The Discord HTTP request timed out.", exception);
        }
    }

    private static bool IsTransient(HttpException exception) =>
        DiscordHttpStatus.IsTransient((int)exception.HttpCode);

    private static Embed CreateEmbed(FormattedSteamAnnouncement formatted)
    {
        var builder = new EmbedBuilder()
            .WithTitle(formatted.Title)
            .WithUrl(formatted.Url)
            .WithDescription(formatted.Description)
            .WithAuthor(formatted.GameName)
            .WithFooter(formatted.Footer)
            .WithTimestamp(formatted.PublishedAtUtc)
            .WithColor(new Color(28, 98, 177));

        if (formatted.ImageUrl is not null)
        {
            builder.WithImageUrl(formatted.ImageUrl);
        }

        return builder.Build();
    }
}
