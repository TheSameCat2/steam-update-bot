using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Services;

namespace SteamUpdateBot.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSteamUpdateBotPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContextFactory<BotDbContext>(options =>
            BotSqliteConnection.Configure(options, connectionString));
        services.TryAddSingleton<IBotRepository, EfBotRepository>();
        services.TryAddSingleton<BotDatabaseInitializer>();
        return services;
    }

    /// <summary>
    /// Registers storage-agnostic application services. A concrete <c>SteamOptions</c>
    /// and an <see cref="ISteamAnnouncementSource"/> must also be registered by the host.
    /// The host may replace the default <see cref="IBotRuntimeState"/> with Discord's live state.
    /// </summary>
    public static IServiceCollection AddSteamUpdateBotCoreServices(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IAppIdLockProvider, AppIdLockProvider>();
        services.TryAddSingleton<IBotRuntimeState, DisconnectedRuntimeState>();
        services.TryAddSingleton<ISubscriptionService, SubscriptionService>();
        services.TryAddSingleton<IPollCoordinator, PollCoordinator>();
        services.TryAddSingleton<IOutboxDeliveryService, OutboxDeliveryService>();
        return services;
    }
}
