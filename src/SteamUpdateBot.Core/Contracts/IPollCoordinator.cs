using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Core.Contracts;

public interface IPollCoordinator
{
    Task<PollCycleResult> RunOnceAsync(CancellationToken cancellationToken);
}
