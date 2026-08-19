namespace SteamUpdateBot.Core.Domain;

public enum AnnouncementDeliveryState
{
    Suppressed = 0,
    Pending = 1,
    Delivered = 2,
    Abandoned = 3,
    Publishing = 4,
}
