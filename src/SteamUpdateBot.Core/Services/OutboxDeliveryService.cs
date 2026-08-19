using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Core.Services;

public sealed partial class OutboxDeliveryService : IOutboxDeliveryService, IDisposable
{
    public const int MaxDeliveryAttempts = 5;

    private const int DeliveryBatchSize = 100;

    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private readonly IBotRepository _repository;
    private readonly IAnnouncementPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly IAppIdLockProvider _appLocks;
    private readonly ILogger<OutboxDeliveryService> _logger;

    public OutboxDeliveryService(
        IBotRepository repository,
        IAnnouncementPublisher publisher,
        TimeProvider timeProvider,
        IAppIdLockProvider appLocks,
        ILogger<OutboxDeliveryService>? logger = null)
    {
        _repository = repository;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _appLocks = appLocks;
        _logger = logger ?? NullLogger<OutboxDeliveryService>.Instance;
    }

    public async Task<DeliveryCycleResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        await _cycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var due = await _repository.GetDueAnnouncementsAsync(
                    _timeProvider.GetUtcNow(),
                    DeliveryBatchSize,
                    cancellationToken)
                .ConfigureAwait(false);

            var delivered = 0;
            var failed = 0;
            var blockedAppIds = new HashSet<uint>();
            foreach (var announcement in due)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (blockedAppIds.Contains(announcement.AppId))
                {
                    continue;
                }

                var result = await DeliverAsync(announcement, cancellationToken).ConfigureAwait(false);
                delivered += result.Delivered;
                failed += result.Failed;
                if (result.Failed > 0)
                {
                    // Keep later announcements for this game from overtaking a just-failed one.
                    blockedAppIds.Add(announcement.AppId);
                }
            }

            return new DeliveryCycleResult(delivered, failed);
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    public void Dispose() => _cycleLock.Dispose();

    private async Task<DeliveryResult> DeliverAsync(AnnouncementRecord announcement, CancellationToken cancellationToken)
    {
        await using var appLock = await _appLocks.AcquireAsync(announcement.AppId, cancellationToken).ConfigureAwait(false);
        if (await _repository.GetGameAsync(announcement.AppId, cancellationToken).ConfigureAwait(false) is null)
        {
            return DeliveryResult.Noop;
        }

        var claim = await _repository.TryClaimForPublishAsync(
                announcement.AppId,
                announcement.SteamGid,
                cancellationToken)
            .ConfigureAwait(false);

        switch (claim.Outcome)
        {
            case PublishClaimOutcome.NotFound:
            case PublishClaimOutcome.AlreadyComplete:
                return DeliveryResult.Noop;
            case PublishClaimOutcome.AlreadyPublishing:
                if (!string.IsNullOrWhiteSpace(claim.DiscordMessageId))
                {
                    return await FinalizeClaimAsync(announcement, claim.DiscordMessageId, cancellationToken)
                        .ConfigureAwait(false);
                }

                break;
            case PublishClaimOutcome.Claimed:
                break;
            default:
                return DeliveryResult.Noop;
        }

        string messageId;
        try
        {
            messageId = await _publisher.PublishAsync(announcement, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(messageId))
            {
                throw new InvalidOperationException("Discord did not return a message ID.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leave the row in Publishing. A restart retries PublishAsync when no message ID was stored.
            throw;
        }
        catch (Exception exception)
        {
            LogPublishFailure(_logger, exception, announcement.AppId, announcement.SteamGid);
            return await RecordPublishFailureAsync(announcement, exception, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _repository.RecordPublishedMessageIdAsync(
                    announcement.AppId,
                    announcement.SteamGid,
                    messageId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new DeliveryResult(0, 1);
        }

        return await FinalizeClaimAsync(announcement, messageId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeliveryResult> FinalizeClaimAsync(
        AnnouncementRecord announcement,
        string? discordMessageId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(discordMessageId))
            {
                return new DeliveryResult(0, 1);
            }

            await _repository.MarkDeliveredAsync(
                    announcement.AppId,
                    announcement.SteamGid,
                    discordMessageId,
                    _timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            return new DeliveryResult(1, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Publish already happened or was claimed. Stay in Publishing and retry the mark only.
            return new DeliveryResult(0, 1);
        }
    }

    private async Task<DeliveryResult> RecordPublishFailureAsync(
        AnnouncementRecord announcement,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var incrementAttempt = exception is not PublisherTransientException;
        var retryNumber = incrementAttempt
            ? announcement.AttemptCount + 1
            : Math.Max(1, announcement.AttemptCount);
        if (incrementAttempt && retryNumber >= MaxDeliveryAttempts)
        {
            await _repository.MarkDeliveryAbandonedAsync(
                    announcement.AppId,
                    announcement.SteamGid,
                    exception.Message,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _repository.RecordDeliveryFailureAsync(
                    announcement.AppId,
                    announcement.SteamGid,
                    _timeProvider.GetUtcNow().Add(GetDeliveryRetryDelay(retryNumber)),
                    exception.Message,
                    cancellationToken,
                    incrementAttempt)
                .ConfigureAwait(false);
        }

        return new DeliveryResult(0, 1);
    }

    private static TimeSpan GetDeliveryRetryDelay(int retryNumber)
    {
        var minutes = retryNumber switch
        {
            <= 1 => 1,
            2 => 5,
            3 => 15,
            4 => 60,
            _ => 360,
        };
        return TimeSpan.FromMinutes(minutes);
    }

    private readonly record struct DeliveryResult(int Delivered, int Failed)
    {
        public static DeliveryResult Noop => new(0, 0);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Discord publish failed for app {AppId} announcement {SteamGid}.")]
    private static partial void LogPublishFailure(ILogger logger, Exception exception, uint appId, string steamGid);
}
