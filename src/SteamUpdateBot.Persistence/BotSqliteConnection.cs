using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SteamUpdateBot.Persistence;

public static class BotSqliteConnection
{
    public const int BusyTimeoutMilliseconds = 5_000;

    public static void Configure(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var connectionStringBuilder = new SqliteConnectionStringBuilder(connectionString)
        {
            ForeignKeys = true,
            DefaultTimeout = 5,
        };

        options
            .UseSqlite(
                connectionStringBuilder.ToString(),
                sqlite => sqlite.MigrationsAssembly(typeof(BotDbContext).Assembly.FullName))
            .AddInterceptors(SqliteBusyTimeoutInterceptor.Instance);
    }
}
