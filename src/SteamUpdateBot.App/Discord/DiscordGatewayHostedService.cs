using global::Discord;
using global::Discord.Interactions;
using global::Discord.Net;
using global::Discord.WebSocket;
using Microsoft.Extensions.Options;
using SteamUpdateBot.Core.Configuration;

namespace SteamUpdateBot.App.Discord;

/// <summary>
/// Owns the Discord gateway lifecycle and validates the configured guild when it becomes ready.
/// </summary>
public sealed partial class DiscordGatewayHostedService : BackgroundService
{
    private static readonly TimeSpan[] LoginRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly DiscordInteractionHandler _interactionHandler;
    private readonly DiscordRuntimeState _runtimeState;
    private readonly DiscordGatewayConnectionMonitor _connectionMonitor;
    private readonly DiscordOptions _options;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<DiscordGatewayHostedService> _logger;
    private readonly SemaphoreSlim _readyLock = new(1, 1);
    private bool _commandsRegistered;
    private bool _disposed;

    public DiscordGatewayHostedService(
        DiscordSocketClient client,
        InteractionService interactions,
        DiscordInteractionHandler interactionHandler,
        DiscordRuntimeState runtimeState,
        IOptions<DiscordOptions> options,
        IHostApplicationLifetime applicationLifetime,
        TimeProvider timeProvider,
        ILogger<DiscordGatewayHostedService> logger)
    {
        _client = client;
        _interactions = interactions;
        _interactionHandler = interactionHandler;
        _runtimeState = runtimeState;
        _connectionMonitor = new DiscordGatewayConnectionMonitor(
            runtimeState,
            timeProvider,
            applicationLifetime,
            logger);
        _options = options.Value;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            ValidateStaticOptions();
        }
        catch (Exception exception)
        {
            LogGatewayConfigurationFailure(_logger, exception);
            _applicationLifetime.StopApplication();
            return;
        }

        _client.Ready += HandleReadyAsync;
        _client.Connected += HandleConnectedAsync;
        _client.Disconnected += HandleDisconnectedAsync;
        _client.Log += HandleGatewayLogAsync;

        await _interactionHandler.InitializeAsync().ConfigureAwait(false);
        if (!await LoginWithRetryAsync(stoppingToken).ConfigureAwait(false))
        {
            return;
        }

        await _connectionMonitor.WatchAsync(stoppingToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _connectionMonitor.MarkDisconnected();

        try
        {
            await _client.StopAsync().ConfigureAwait(false);
            await _client.LogoutAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogGatewayShutdownError(_logger, exception);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _client.Ready -= HandleReadyAsync;
            _client.Connected -= HandleConnectedAsync;
            _client.Disconnected -= HandleDisconnectedAsync;
            _client.Log -= HandleGatewayLogAsync;
            _readyLock.Dispose();
        }

        base.Dispose();
    }

    private async Task<bool> LoginWithRetryAsync(CancellationToken stoppingToken)
    {
        var delayIndex = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _client.LoginAsync(TokenType.Bot, _options.Token).ConfigureAwait(false);
                await _client.StartAsync().ConfigureAwait(false);
                _runtimeState.ResetDisconnectedClock();
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            catch (HttpException exception) when (DiscordHttpStatus.IsPermanentLoginFailure((int)exception.HttpCode))
            {
                LogGatewayConfigurationFailure(_logger, exception);
                _applicationLifetime.StopApplication();
                return false;
            }
            catch (Exception exception)
            {
                LogGatewayLoginRetry(_logger, exception);
                await TryStopClientAsync().ConfigureAwait(false);
                var delay = LoginRetryDelays[Math.Min(delayIndex, LoginRetryDelays.Length - 1)];
                delayIndex++;
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private async Task HandleReadyAsync()
    {
        await _readyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _runtimeState.SetConnected(false);
            var resources = DiscordResourceCheckEvaluator.Evaluate(InspectConfiguredResources());
            if (resources.Kind == DiscordResourceCheckKind.Transient)
            {
                LogGatewayResourcesUnavailable(_logger, resources.Message);
                return;
            }

            if (resources.Kind == DiscordResourceCheckKind.Permanent)
            {
                LogGatewayMisconfiguration(_logger, resources.Message);
                _applicationLifetime.StopApplication();
                return;
            }

            if (!_commandsRegistered)
            {
                try
                {
                    await _interactions.RegisterCommandsToGuildAsync(_options.GuildId, deleteMissing: true)
                        .ConfigureAwait(false);
                    _commandsRegistered = true;
                }
                catch (HttpException exception) when (DiscordHttpStatus.IsTransient((int)exception.HttpCode))
                {
                    LogGatewayCommandRegistrationRetry(_logger, exception);
                    return;
                }
            }

            _runtimeState.SetConnected(true);
            LogGatewayReady(_logger, _options.GuildId);
        }
        catch (Exception exception)
        {
            _runtimeState.SetConnected(false);
            if (exception is HttpException httpException
                && DiscordHttpStatus.IsTransient((int)httpException.HttpCode))
            {
                LogGatewayCommandRegistrationRetry(_logger, httpException);
                return;
            }

            if (exception is HttpRequestException or TimeoutException)
            {
                LogGatewayResourcesUnavailable(_logger, exception.Message);
                return;
            }

            LogGatewayConfigurationFailure(_logger, exception);
            _applicationLifetime.StopApplication();
        }
        finally
        {
            _readyLock.Release();
        }
    }

    private async Task HandleConnectedAsync()
    {
        // Discord.Net raises Connected after a RESUME, but Ready only after a fresh IDENTIFY.
        await _readyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connectionMonitor.TryRestoreAfterResume(_commandsRegistered, InspectConfiguredResources()))
            {
                LogGatewayResumeRestored(_logger, _options.GuildId);
            }
        }
        finally
        {
            _readyLock.Release();
        }
    }

    private Task HandleDisconnectedAsync(Exception exception)
    {
        _connectionMonitor.MarkDisconnected();
        if (exception is null)
        {
            LogGatewayDisconnected(_logger);
        }
        else
        {
            LogGatewayDisconnectedWithError(_logger, exception);
        }

        return Task.CompletedTask;
    }

    private Task HandleGatewayLogAsync(LogMessage message)
    {
        if (message.Exception is null)
        {
            LogGatewayMessage(_logger, message.Message);
        }
        else
        {
            LogGatewayError(_logger, message.Exception, message.Message);
        }

        return Task.CompletedTask;
    }

    private void ValidateStaticOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.Token)
            || _options.GuildId == 0
            || _options.AnnouncementChannelId == 0
            || _options.ManagerRoleId == 0)
        {
            throw new InvalidOperationException("Discord token, guild ID, announcement channel ID, and manager role ID must be configured.");
        }
    }

    private DiscordResourceCheck InspectConfiguredResources()
    {
        var guild = _client.GetGuild(_options.GuildId);
        if (guild is null)
        {
            return new DiscordResourceCheck(
                GuildFound: false,
                GuildAvailable: false,
                RoleFound: false,
                ChannelFound: false,
                ChannelCanReceiveMessages: false,
                CanViewChannel: false,
                CanSendMessages: false,
                CanEmbedLinks: false);
        }

        var channel = guild.GetChannel(_options.AnnouncementChannelId);
        var permissions = channel is null ? default : guild.CurrentUser.GetPermissions(channel);
        return new DiscordResourceCheck(
            GuildFound: true,
            GuildAvailable: ((IGuild)guild).Available,
            RoleFound: guild.GetRole(_options.ManagerRoleId) is not null,
            ChannelFound: channel is not null,
            ChannelCanReceiveMessages: channel is IMessageChannel,
            CanViewChannel: permissions.ViewChannel,
            CanSendMessages: permissions.SendMessages,
            CanEmbedLinks: permissions.EmbedLinks);
    }

    private async Task TryStopClientAsync()
    {
        try
        {
            await _client.StopAsync().ConfigureAwait(false);
            await _client.LogoutAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogGatewayShutdownError(_logger, exception);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Discord gateway shutdown did not complete cleanly.")]
    private static partial void LogGatewayShutdownError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Discord gateway ready for guild {GuildId}.")]
    private static partial void LogGatewayReady(ILogger logger, ulong guildId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Critical, Message = "Discord configuration validation or command registration failed.")]
    private static partial void LogGatewayConfigurationFailure(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 12, Level = LogLevel.Critical, Message = "Discord configuration is invalid: {Reason}")]
    private static partial void LogGatewayMisconfiguration(ILogger logger, string reason);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Discord gateway disconnected.")]
    private static partial void LogGatewayDisconnected(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Discord gateway disconnected.")]
    private static partial void LogGatewayDisconnectedWithError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Discord gateway: {Message}")]
    private static partial void LogGatewayMessage(ILogger logger, string? message);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Discord gateway: {Message}")]
    private static partial void LogGatewayError(ILogger logger, Exception exception, string? message);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Discord login failed; retrying.")]
    private static partial void LogGatewayLoginRetry(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Discord guild or command registration is not ready: {Reason}")]
    private static partial void LogGatewayResourcesUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Discord command registration failed with a transient error.")]
    private static partial void LogGatewayCommandRegistrationRetry(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Information,
        Message = "Discord gateway connection restored for guild {GuildId} after a resumed session.")]
    private static partial void LogGatewayResumeRestored(ILogger logger, ulong guildId);
}
