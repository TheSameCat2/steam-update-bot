using Microsoft.EntityFrameworkCore;
using SteamUpdateBot.Persistence;

namespace SteamUpdateBot.Tests.Persistence;

internal sealed class TestSqliteDatabase : IAsyncDisposable
{
    private readonly string _databasePath;

    private TestSqliteDatabase(string databasePath, DbContextOptions<BotDbContext> options)
    {
        _databasePath = databasePath;
        Factory = new TestDbContextFactory(options);
        Repository = new EfBotRepository(Factory);
    }

    public TestDbContextFactory Factory { get; }

    public EfBotRepository Repository { get; }

    public static async Task<TestSqliteDatabase> CreateAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"steam-update-bot-{Guid.NewGuid():N}.db");
        var optionsBuilder = new DbContextOptionsBuilder<BotDbContext>();
        BotSqliteConnection.Configure(optionsBuilder, $"Data Source={databasePath}");
        var options = optionsBuilder.Options;
        var database = new TestSqliteDatabase(databasePath, options);
        await using var context = await database.Factory.CreateDbContextAsync().ConfigureAwait(false);
        await context.Database.MigrateAsync().ConfigureAwait(false);
        return database;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        var walPath = $"{_databasePath}-wal";
        if (File.Exists(walPath))
        {
            File.Delete(walPath);
        }

        var shmPath = $"{_databasePath}-shm";
        if (File.Exists(shmPath))
        {
            File.Delete(shmPath);
        }
    }
}

internal sealed class TestDbContextFactory(DbContextOptions<BotDbContext> options) : IDbContextFactory<BotDbContext>
{
    public BotDbContext CreateDbContext() => new(options);

    public Task<BotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
