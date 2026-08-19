namespace SteamUpdateBot.Core.Contracts;

public interface IBotRuntimeState
{
    bool DiscordConnected { get; }

    DateTimeOffset? DiscordDisconnectedSinceUtc { get; }
}
