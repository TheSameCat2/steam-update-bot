using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SteamUpdateBot.App.Discord;
using SteamUpdateBot.App.Health;
using SteamUpdateBot.App.Workers;
using SteamUpdateBot.Core.Configuration;
using SteamUpdateBot.Persistence;
using SteamUpdateBot.Steam;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services
    .AddOptions<DiscordOptions>()
    .BindConfiguration(DiscordOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services
    .AddOptions<SteamOptions>()
    .BindConfiguration(SteamOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("BotDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:BotDb must be configured.");
}

builder.Services.AddSteamUpdateBotSteam();
builder.Services.AddSteamUpdateBotPersistence(connectionString);
builder.Services.AddSteamUpdateBotCoreServices();
builder.Services.AddSteamUpdateBotDiscord();
builder.Services.AddHostedService<PollingHostedService>();
builder.Services.AddHostedService<OutboxDeliveryHostedService>();
builder.Services
    .AddHealthChecks()
    .AddCheck<BotLivenessHealthCheck>("live", tags: ["live"])
    .AddCheck<BotReadinessHealthCheck>("ready", tags: ["ready"]);

var app = builder.Build();

await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
{
    _ = scope.ServiceProvider.GetRequiredService<IOptions<SteamOptions>>().Value;
    _ = scope.ServiceProvider.GetRequiredService<IOptions<DiscordOptions>>().Value;
    await scope.ServiceProvider.GetRequiredService<BotDatabaseInitializer>().InitializeAsync();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live", StringComparer.Ordinal),
    ResponseWriter = HealthCheckJson.WriteAsync,
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready", StringComparer.Ordinal),
    ResponseWriter = HealthCheckJson.WriteAsync,
});

await app.RunAsync();
