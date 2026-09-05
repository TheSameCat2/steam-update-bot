using Microsoft.EntityFrameworkCore;
using SteamUpdateBot.Persistence;

namespace SteamUpdateBot.Tests.Persistence;

public sealed class BotDatabaseInitializerTests
{
    [Fact]
    public async Task InitializeAsyncCreatesMissingDirectoryAndAppliesWal()
    {
        var tempSubdir = Path.Combine(Path.GetTempPath(), $"steam-bot-dir-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempSubdir, "nested", "steam-update-bot.db");

        try
        {
            var optionsBuilder = new DbContextOptionsBuilder<BotDbContext>();
            BotSqliteConnection.Configure(optionsBuilder, $"Data Source={databasePath}");
            var factory = new TestDbContextFactory(optionsBuilder.Options);
            var initializer = new BotDatabaseInitializer(factory);

            await initializer.InitializeAsync(TestContext.Current.CancellationToken);

            Assert.True(File.Exists(databasePath));

            await using var context = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
            await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            try
            {
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA journal_mode;";
                var journalMode = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                Assert.Equal("wal", journalMode, ignoreCase: true);
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        }
        finally
        {
            if (Directory.Exists(tempSubdir))
            {
                Directory.Delete(tempSubdir, recursive: true);
            }
        }
    }
}
