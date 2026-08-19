namespace SteamUpdateBot.Core.Services;

/// <summary>
/// Coordinates operations for a single Steam application inside this process.
/// </summary>
public interface IAppIdLockProvider
{
    ValueTask<IAsyncDisposable> AcquireAsync(uint appId, CancellationToken cancellationToken);
}
