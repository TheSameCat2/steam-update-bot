using global::Discord;
using global::Discord.Interactions;
using Microsoft.Extensions.Options;
using SteamUpdateBot.Core.Configuration;

namespace SteamUpdateBot.App.Discord;

/// <summary>
/// Restricts slash commands to the single configured guild.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireConfiguredGuildAttribute : PreconditionAttribute
{
    public override Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context,
        ICommandInfo commandInfo,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commandInfo);
        ArgumentNullException.ThrowIfNull(services);

        var options = services.GetService(typeof(IOptions<DiscordOptions>)) as IOptions<DiscordOptions>;
        if (options is null)
        {
            return Task.FromResult(PreconditionResult.FromError("Discord configuration is unavailable."));
        }

        var result = DiscordAuthorization.CheckConfiguredGuild(context.Guild?.Id, options.Value.GuildId);
        return Task.FromResult(result.IsAuthorized
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError(result.FailureMessage!));
    }
}

/// <summary>
/// Allows commands only for the configured manager role or a guild administrator.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireSteamManagerAttribute : PreconditionAttribute
{
    public override Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context,
        ICommandInfo commandInfo,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commandInfo);
        ArgumentNullException.ThrowIfNull(services);

        var options = services.GetService(typeof(IOptions<DiscordOptions>)) as IOptions<DiscordOptions>;
        if (options is null)
        {
            return Task.FromResult(PreconditionResult.FromError("Discord configuration is unavailable."));
        }

        var guildUser = context.User as IGuildUser;
        var result = DiscordAuthorization.CheckManagerAccess(
            context.Guild?.Id,
            options.Value.GuildId,
            guildUser?.GuildPermissions.Administrator == true,
            guildUser?.RoleIds,
            options.Value.ManagerRoleId);

        return Task.FromResult(result.IsAuthorized
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError(result.FailureMessage!));
    }
}
