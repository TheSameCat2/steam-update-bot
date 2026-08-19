using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using SteamUpdateBot.Core.Configuration;
using SteamUpdateBot.Core.Contracts;

namespace SteamUpdateBot.Steam;

public static class SteamServiceCollectionExtensions
{
    /// <summary>
    /// Registers Steam settings and the Steam announcement source.
    /// </summary>
    public static IServiceCollection AddSteamUpdateBotSteam(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SteamOptions>(configuration.GetSection(SteamOptions.SectionName));
        return services.AddSteamUpdateBotSteam();
    }

    /// <summary>
    /// Registers the Steam announcement source using already-configured <see cref="SteamOptions"/>.
    /// </summary>
    public static IServiceCollection AddSteamUpdateBotSteam(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SteamOptions>();
        services.TryAddSingleton<SteamOptions>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<SteamOptions>>().Value);

        IHttpClientBuilder clientBuilder = services
            .AddHttpClient<ISteamAnnouncementSource, SteamAnnouncementSource>((serviceProvider, client) =>
            {
                SteamOptions options = serviceProvider.GetRequiredService<IOptions<SteamOptions>>().Value;
                if (options.RequestTimeoutSeconds <= 0)
                {
                    throw new InvalidOperationException("Steam request timeout must be greater than zero.");
                }

                client.Timeout = GetHttpClientTimeout(options);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamUpdateBot/1.0");
            });

        clientBuilder
            // Core services are singleton-scoped, so keep their captured typed client from
            // pinning a connection or DNS result for the lifetime of the process.
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            })
            .AddStandardResilienceHandler()
            .Configure((options, serviceProvider) =>
            {
                SteamOptions steamOptions = serviceProvider.GetRequiredService<IOptions<SteamOptions>>().Value;
                TimeSpan attemptTimeout = TimeSpan.FromSeconds(steamOptions.RequestTimeoutSeconds);

                options.Retry.MaxRetryAttempts = RetryAttempts;
                options.AttemptTimeout.Timeout = attemptTimeout;
                options.TotalRequestTimeout.Timeout = attemptTimeout * (RetryAttempts + 1);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(
                    Math.Max(30, steamOptions.RequestTimeoutSeconds * 2));
            });

        return services;
    }

    public const int RetryAttempts = 2;
    public const int HttpClientTimeoutBufferSeconds = 5;

    public static TimeSpan GetHttpClientTimeout(SteamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TimeSpan.FromSeconds((options.RequestTimeoutSeconds * (RetryAttempts + 1)) + HttpClientTimeoutBufferSeconds);
    }
}
