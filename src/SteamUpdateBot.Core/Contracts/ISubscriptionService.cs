using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Core.Contracts;

public interface ISubscriptionService
{
    Task<AddGameResult> AddGameAsync(uint appId, string actorDiscordUserId, CancellationToken cancellationToken);

    Task<RemoveGameResult> RemoveGameAsync(uint appId, CancellationToken cancellationToken);

    Task<PagedResult<MonitoredGame>> ListGamesAsync(int page, int pageSize, CancellationToken cancellationToken);

    Task<BotStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken);
}
