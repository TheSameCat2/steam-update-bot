using System.Globalization;
using System.Text;
using global::Discord;
using global::Discord.Interactions;
using global::Discord.WebSocket;
using SteamUpdateBot.Core.Configuration;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Domain;
using SteamUpdateBot.Core.Formatting;
using SteamUpdateBot.Steam;

namespace SteamUpdateBot.App.Discord;

[Group("steam", "Manage monitored Steam games.")]
[RequireConfiguredGuild]
public sealed class SteamCommandsModule : InteractionModuleBase<SocketInteractionContext>
{
    private const int ListPageSize = 20;
    private const int MaxResponseLength = 1_900;
    private readonly ISubscriptionService _subscriptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly SteamOptions _steamOptions;

    public SteamCommandsModule(
        ISubscriptionService subscriptions,
        IHostApplicationLifetime lifetime,
        SteamOptions steamOptions)
    {
        _subscriptions = subscriptions;
        _lifetime = lifetime;
        _steamOptions = steamOptions;
    }

    [SlashCommand("add", "Monitor a Steam game by its app ID.")]
    [RequireSteamManager]
    public async Task AddAsync([Summary("app-id", "The Steam application ID.")] long appId)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        if (!TryGetAppId(appId, out var parsedAppId))
        {
            await RespondEphemeralAsync("Steam App ID must be between 1 and 4,294,967,295.").ConfigureAwait(false);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
        timeout.CancelAfter(SteamServiceCollectionExtensions.GetHttpClientTimeout(_steamOptions));
        var result = await _subscriptions.AddGameAsync(
            parsedAppId,
            Context.User.Id.ToString(CultureInfo.InvariantCulture),
            timeout.Token).ConfigureAwait(false);
        await RespondEphemeralAsync(result.Message).ConfigureAwait(false);
    }

    [SlashCommand("remove", "Stop monitoring a Steam game by its app ID.")]
    [RequireSteamManager]
    public async Task RemoveAsync([Summary("app-id", "The Steam application ID.")] long appId)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        if (!TryGetAppId(appId, out var parsedAppId))
        {
            await RespondEphemeralAsync("Steam App ID must be between 1 and 4,294,967,295.").ConfigureAwait(false);
            return;
        }

        var result = await _subscriptions.RemoveGameAsync(parsedAppId, _lifetime.ApplicationStopping).ConfigureAwait(false);
        await RespondEphemeralAsync(result.Message).ConfigureAwait(false);
    }

    [SlashCommand("list", "List monitored Steam games.")]
    public async Task ListAsync([Summary("page", "Page number, starting at 1.")] int page = 1)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        if (page < 1)
        {
            await RespondEphemeralAsync("Page must be at least 1.").ConfigureAwait(false);
            return;
        }

        var games = await _subscriptions.ListGamesAsync(page, ListPageSize, _lifetime.ApplicationStopping).ConfigureAwait(false);
        var response = new StringBuilder();
        var totalPages = Math.Max(1, (int)Math.Ceiling(games.TotalCount / (double)ListPageSize));
        response.Append("Monitored games — page ")
            .Append(games.Page)
            .Append(" of ")
            .Append(totalPages)
            .Append(" (")
            .Append(games.TotalCount)
            .AppendLine(")");

        if (games.Items.Count == 0)
        {
            response.Append("No monitored games on this page.");
        }
        else
        {
            foreach (var game in games.Items)
            {
                response.Append("• `")
                    .Append(game.AppId)
                    .Append("` — ")
                    .AppendLine(Shorten(game.Name, 120));
            }
        }

        await RespondEphemeralAsync(response.ToString()).ConfigureAwait(false);
    }

    [SlashCommand("status", "Show bot status, optionally for one Steam app ID.")]
    public async Task StatusAsync([Summary("app-id", "The optional Steam application ID.")] long? appId = null)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        uint? parsedAppId = null;
        if (appId.HasValue)
        {
            if (!TryGetAppId(appId.Value, out var parsed))
            {
                await RespondEphemeralAsync("Steam App ID must be between 1 and 4,294,967,295.").ConfigureAwait(false);
                return;
            }

            parsedAppId = parsed;
        }

        var status = await _subscriptions.GetStatusAsync(_lifetime.ApplicationStopping).ConfigureAwait(false);
        var response = new StringBuilder()
            .Append("Discord: ").AppendLine(status.DiscordConnected ? "connected" : "disconnected")
            .Append("Database: ").AppendLine(status.DatabaseReady ? "ready" : "unavailable")
            .Append("Monitored games: ").AppendLine(status.MonitoredGameCount.ToString(CultureInfo.InvariantCulture))
            .Append("Pending announcements: ").AppendLine(status.PendingDeliveryCount.ToString(CultureInfo.InvariantCulture))
            .Append("Delivery retries: ").AppendLine(status.FailedDeliveryCount.ToString(CultureInfo.InvariantCulture))
            .Append("Abandoned deliveries: ").AppendLine(status.AbandonedDeliveryCount.ToString(CultureInfo.InvariantCulture))
            .Append("Last successful poll: ").AppendLine(FormatTimestamp(status.LastSuccessfulPollUtc))
            .Append("Oldest successful poll: ").AppendLine(FormatTimestamp(status.OldestSuccessfulPollUtc));

        if (status.OldestUndeliveredDetectedAtUtc is not null)
        {
            response.Append("Oldest undelivered announcement: ")
                .AppendLine(FormatTimestamp(status.OldestUndeliveredDetectedAtUtc));
        }

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            response.Append("Last error: ").AppendLine(status.LastError);
        }

        if (parsedAppId.HasValue)
        {
            var games = await _subscriptions.ListGamesAsync(1, 50, _lifetime.ApplicationStopping).ConfigureAwait(false);
            var game = games.Items.FirstOrDefault(candidate => candidate.AppId == parsedAppId.Value);
            if (game is null)
            {
                response.Append("App ")
                    .Append(parsedAppId.Value.ToString(CultureInfo.InvariantCulture))
                    .Append(" is not monitored.");
            }
            else
            {
                response.AppendLine()
                    .Append("App ")
                    .Append(game.AppId.ToString(CultureInfo.InvariantCulture))
                    .Append(": ")
                    .AppendLine(Shorten(game.Name, 160))
                    .Append("Last poll: ")
                    .AppendLine(FormatTimestamp(game.LastPollSuccessUtc))
                    .Append("Next poll: ")
                    .Append(FormatTimestamp(game.NextPollUtc));
            }
        }

        await RespondEphemeralAsync(response.ToString()).ConfigureAwait(false);
    }

    private static bool TryGetAppId(long input, out uint appId)
    {
        if (input is < 1 or > uint.MaxValue)
        {
            appId = default;
            return false;
        }

        appId = (uint)input;
        return true;
    }

    private async Task RespondEphemeralAsync(string message)
    {
        var safeMessage = SteamAnnouncementFormatter.TruncateUnicode(
            (message ?? string.Empty).Replace("@", "@\u200B", StringComparison.Ordinal),
            MaxResponseLength,
            "The command completed without a response message.");
        await FollowupAsync(safeMessage, ephemeral: true, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
    }

    private static string Shorten(string? value, int maximumLength) => SteamAnnouncementFormatter.TruncateUnicode(
        (value ?? string.Empty).Replace("@", "@\u200B", StringComparison.Ordinal).ReplaceLineEndings(" "),
        maximumLength,
        "Unnamed game");

    private static string FormatTimestamp(DateTimeOffset? value) => value?.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture) ?? "never";
}
