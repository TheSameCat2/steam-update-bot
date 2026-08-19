using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Core.Contracts;

public interface IOutboxDeliveryService
{
    Task<DeliveryCycleResult> RunOnceAsync(CancellationToken cancellationToken);
}
