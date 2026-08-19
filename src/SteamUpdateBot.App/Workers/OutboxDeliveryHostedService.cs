using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.App.Workers;

public sealed partial class OutboxDeliveryHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private readonly IOutboxDeliveryService _deliveryService;
    private readonly IBotRuntimeState _runtimeState;
    private readonly ILogger<OutboxDeliveryHostedService> _logger;

    public OutboxDeliveryHostedService(
        IOutboxDeliveryService deliveryService,
        IBotRuntimeState runtimeState,
        ILogger<OutboxDeliveryHostedService> logger)
    {
        _deliveryService = deliveryService;
        _runtimeState = runtimeState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunCycleSafelyAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunCycleSafelyAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunCycleSafelyAsync(CancellationToken cancellationToken)
    {
        if (!_runtimeState.DiscordConnected)
        {
            return;
        }

        try
        {
            DeliveryCycleResult result = await _deliveryService.RunOnceAsync(cancellationToken).ConfigureAwait(false);
            if (result.Delivered > 0 || result.Failed > 0)
            {
                LogDeliveryCycle(_logger, result.Delivered, result.Failed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogDeliveryFailure(_logger, exception);
        }
    }

    [LoggerMessage(EventId = 110, Level = LogLevel.Information, Message = "Discord delivery cycle completed: {Delivered} delivered, {Failed} failed.")]
    private static partial void LogDeliveryCycle(ILogger logger, int delivered, int failed);

    [LoggerMessage(EventId = 111, Level = LogLevel.Error, Message = "Discord delivery cycle failed unexpectedly.")]
    private static partial void LogDeliveryFailure(ILogger logger, Exception exception);
}
