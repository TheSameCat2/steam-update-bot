using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Core.Contracts;

public interface IAnnouncementPublisher
{
    Task<string> PublishAsync(AnnouncementRecord announcement, CancellationToken cancellationToken);
}
