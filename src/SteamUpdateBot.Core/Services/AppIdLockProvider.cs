using System.Collections.Concurrent;

namespace SteamUpdateBot.Core.Services;

public sealed class AppIdLockProvider : IAppIdLockProvider, IDisposable
{
    private readonly ConcurrentDictionary<uint, SemaphoreSlim> _locks = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(uint appId, CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(appId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    public void Dispose()
    {
        foreach (var semaphore in _locks.Values)
        {
            semaphore.Dispose();
        }
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
