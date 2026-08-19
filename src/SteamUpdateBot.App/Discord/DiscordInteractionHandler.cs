using global::Discord;
using global::Discord.Interactions;
using global::Discord.WebSocket;

namespace SteamUpdateBot.App.Discord;

/// <summary>
/// Loads slash-command modules once and dispatches gateway interactions to them.
/// </summary>
public sealed partial class DiscordInteractionHandler
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _services;
    private readonly ILogger<DiscordInteractionHandler> _logger;
    private int _initialized;

    public DiscordInteractionHandler(
        DiscordSocketClient client,
        InteractionService interactions,
        IServiceProvider services,
        ILogger<DiscordInteractionHandler> logger)
    {
        _client = client;
        _interactions = interactions;
        _services = services;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _interactions.AddModulesAsync(typeof(SteamCommandsModule).Assembly, _services).ConfigureAwait(false);
            _client.InteractionCreated += HandleInteractionAsync;
            _interactions.InteractionExecuted += HandleInteractionExecutedAsync;
            _interactions.Log += HandleInteractionLogAsync;
        }
        catch
        {
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
    }

    private async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        try
        {
            var context = new SocketInteractionContext(_client, interaction);
            await _interactions.ExecuteCommandAsync(context, _services).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogUnhandledInteraction(_logger, exception);
            if (interaction.HasResponded)
            {
                await interaction.FollowupAsync(
                    "The command could not be completed.",
                    ephemeral: true,
                    allowedMentions: AllowedMentions.None).ConfigureAwait(false);
                return;
            }

            await interaction.RespondAsync(
                "The command could not be completed.",
                ephemeral: true,
                allowedMentions: AllowedMentions.None).ConfigureAwait(false);
        }
    }

    private async Task HandleInteractionExecutedAsync(
        ICommandInfo command,
        IInteractionContext context,
        global::Discord.Interactions.IResult result)
    {
        if (result.IsSuccess || context.Interaction is not SocketSlashCommand interaction)
        {
            return;
        }

        LogInteractionFailure(_logger, result.ErrorReason);
        try
        {
            await RespondToFailedInteractionAsync(interaction, result).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogUnhandledInteraction(_logger, exception);
        }
    }

    private static async Task RespondToFailedInteractionAsync(SocketInteraction interaction, global::Discord.Interactions.IResult result)
    {
        var message = result.Error == InteractionCommandError.UnmetPrecondition && !string.IsNullOrWhiteSpace(result.ErrorReason)
            ? result.ErrorReason
            : "The command could not be completed.";

        if (interaction.HasResponded)
        {
            await interaction.FollowupAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            return;
        }

        await interaction.RespondAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
    }

    private Task HandleInteractionLogAsync(LogMessage message)
    {
        if (message.Exception is null)
        {
            LogInteractionServiceMessage(_logger, message.Message);
        }
        else
        {
            LogInteractionServiceError(_logger, message.Exception, message.Message);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Discord interaction failed: {Reason}")]
    private static partial void LogInteractionFailure(ILogger logger, string? reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Unhandled error while executing a Discord interaction.")]
    private static partial void LogUnhandledInteraction(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Discord interaction service: {Message}")]
    private static partial void LogInteractionServiceMessage(ILogger logger, string? message);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Discord interaction service: {Message}")]
    private static partial void LogInteractionServiceError(ILogger logger, Exception exception, string? message);
}
