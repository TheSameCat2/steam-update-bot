using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SteamUpdateBot.Persistence;

/// <summary>
/// Applies <c>PRAGMA busy_timeout</c> on every opened SQLite connection.
/// The pragma is per-connection, so a startup-only statement does not cover later EF connections.
/// </summary>
internal sealed class SqliteBusyTimeoutInterceptor : DbConnectionInterceptor
{
    public static SqliteBusyTimeoutInterceptor Instance { get; } = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {BotSqliteConnection.BusyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {BotSqliteConnection.BusyTimeoutMilliseconds};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
