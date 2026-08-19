using global::Discord;
using global::Discord.Interactions;
using global::Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Formatting;

namespace SteamUpdateBot.App.Discord;

public static class DiscordServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Discord gateway, slash commands, notification publisher, and runtime-state services.
    /// </summary>
    public static IServiceCollection AddSteamUpdateBotDiscord(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(static _ => new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            AlwaysDownloadUsers = false,
            LogLevel = LogSeverity.Info,
        }));
        services.AddSingleton(static provider => new InteractionService(
            provider.GetRequiredService<DiscordSocketClient>(),
            new InteractionServiceConfig
            {
                DefaultRunMode = RunMode.Async,
                LogLevel = LogSeverity.Info,
            }));
        services.AddSingleton<DiscordRuntimeState>();
        services.AddSingleton<IBotRuntimeState>(static provider => provider.GetRequiredService<DiscordRuntimeState>());
        services.AddSingleton<SteamAnnouncementFormatter>();
        services.AddSingleton<IAnnouncementPublisher, DiscordAnnouncementPublisher>();
        services.AddSingleton<DiscordInteractionHandler>();
        services.AddHostedService<DiscordGatewayHostedService>();

        return services;
    }
}
