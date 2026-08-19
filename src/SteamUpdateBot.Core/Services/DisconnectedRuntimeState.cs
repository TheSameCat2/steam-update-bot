using SteamUpdateBot.Core.Contracts;

namespace SteamUpdateBot.Core.Services;

/// <summary>
/// Default runtime state used until the Discord host replaces it with its live state.
/// </summary>
public sealed class DisconnectedRuntimeState : IBotRuntimeState
{
    public bool DiscordConnected => false;

    public DateTimeOffset? DiscordDisconnectedSinceUtc => null;
}
