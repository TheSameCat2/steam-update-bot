using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SteamUpdateBot.Persistence;

namespace SteamUpdateBot.Tests.Persistence;

public sealed class SqliteBusyTimeoutTests
{
    [Fact]
    public async Task BusyTimeoutIsAppliedOnEveryOpenedConnection()
    {
        await using var database = await TestSqliteDatabase.CreateAsync();

        var first = await ReadBusyTimeoutAsync(database);
        var second = await ReadBusyTimeoutAsync(database);

        Assert.Equal(BotSqliteConnection.BusyTimeoutMilliseconds, first);
        Assert.Equal(BotSqliteConnection.BusyTimeoutMilliseconds, second);
    }

    private static async Task<long> ReadBusyTimeoutAsync(TestSqliteDatabase database)
    {
        await using var context = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        try
        {
            var connection = context.Database.GetDbConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout;";
            return Convert.ToInt64(
                await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
                CultureInfo.InvariantCulture);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
