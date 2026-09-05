using Microsoft.EntityFrameworkCore;

namespace SteamUpdateBot.Persistence;

/// <summary>
/// Applies checked-in migrations and persistent SQLite pragmas such as WAL.
/// Per-connection pragmas such as busy_timeout are applied by <see cref="BotSqliteConnection"/>.
/// Invoke once during application startup before accepting Discord work.
/// </summary>
public sealed class BotDatabaseInitializer(IDbContextFactory<BotDbContext> contextFactory)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        EnsureDatabaseDirectoryExists(context);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static void EnsureDatabaseDirectoryExists(BotDbContext context)
    {
        var dataSource = context.Database.GetDbConnection().DataSource;
        if (!string.IsNullOrWhiteSpace(dataSource)
            && !string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }
}
