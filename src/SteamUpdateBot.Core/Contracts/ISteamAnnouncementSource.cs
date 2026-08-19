using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Core.Contracts;

public interface ISteamAnnouncementSource
{
    Task<SteamAppMetadata?> GetGameAsync(uint appId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SteamAnnouncement>> GetOfficialAnnouncementsSinceAsync(
        uint appId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);
}
