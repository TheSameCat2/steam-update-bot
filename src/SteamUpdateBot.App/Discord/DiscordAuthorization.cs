namespace SteamUpdateBot.App.Discord;

/// <summary>
/// Keeps authorization policy independent from Discord.Net objects so it can be tested without a gateway connection.
/// </summary>
public static class DiscordAuthorization
{
    public static DiscordAuthorizationResult CheckConfiguredGuild(ulong? interactionGuildId, ulong configuredGuildId)
    {
        if (interactionGuildId is null)
        {
            return DiscordAuthorizationResult.Denied("This command can only be used in the configured Discord server.");
        }

        if (configuredGuildId == 0 || interactionGuildId.Value != configuredGuildId)
        {
            return DiscordAuthorizationResult.Denied("This command is not available in this Discord server.");
        }

        return DiscordAuthorizationResult.Allowed;
    }

    public static DiscordAuthorizationResult CheckManagerAccess(
        ulong? interactionGuildId,
        ulong configuredGuildId,
        bool isAdministrator,
        IEnumerable<ulong>? roleIds,
        ulong managerRoleId)
    {
        var guildResult = CheckConfiguredGuild(interactionGuildId, configuredGuildId);
        if (!guildResult.IsAuthorized)
        {
            return guildResult;
        }

        if (isAdministrator || (managerRoleId != 0 && roleIds?.Contains(managerRoleId) == true))
        {
            return DiscordAuthorizationResult.Allowed;
        }

        return DiscordAuthorizationResult.Denied("You need the configured Steam manager role or Discord Administrator permission.");
    }
}

public sealed record DiscordAuthorizationResult(bool IsAuthorized, string? FailureMessage)
{
    public static DiscordAuthorizationResult Allowed { get; } = new(true, null);

    public static DiscordAuthorizationResult Denied(string message) => new(false, message);
}
