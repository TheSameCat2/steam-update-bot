using SteamUpdateBot.Core.Contracts;

namespace SteamUpdateBot.App.Discord;

/// <summary>
/// Thread-safe gateway availability state consumed by polling and health-check services.
/// </summary>
public sealed class DiscordRuntimeState : IBotRuntimeState
{
    private readonly TimeProvider _timeProvider;
    private int _discordConnected;
    private long _disconnectedSinceTicks;

    public DiscordRuntimeState(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _disconnectedSinceTicks = timeProvider.GetUtcNow().UtcTicks;
    }

    public bool DiscordConnected => Volatile.Read(ref _discordConnected) != 0;

    public DateTimeOffset? DiscordDisconnectedSinceUtc
    {
        get
        {
            if (DiscordConnected)
            {
                return null;
            }

            var ticks = Volatile.Read(ref _disconnectedSinceTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void SetConnected(bool connected)
    {
        if (connected)
        {
            Volatile.Write(ref _discordConnected, 1);
            Volatile.Write(ref _disconnectedSinceTicks, 0);
            return;
        }

        if (Interlocked.Exchange(ref _discordConnected, 0) != 0
            || Volatile.Read(ref _disconnectedSinceTicks) == 0)
        {
            Volatile.Write(ref _disconnectedSinceTicks, _timeProvider.GetUtcNow().UtcTicks);
        }
    }

    public void ResetDisconnectedClock()
    {
        if (DiscordConnected)
        {
            return;
        }

        Volatile.Write(ref _disconnectedSinceTicks, _timeProvider.GetUtcNow().UtcTicks);
    }
}
