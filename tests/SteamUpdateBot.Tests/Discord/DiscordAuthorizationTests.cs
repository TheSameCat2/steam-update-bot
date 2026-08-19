using SteamUpdateBot.App.Discord;

namespace SteamUpdateBot.Tests.Discord;

public sealed class DiscordAuthorizationTests
{
    private const ulong GuildId = 100;
    private const ulong ManagerRoleId = 200;

    [Fact]
    public void CheckManagerAccessRejectsDirectMessages()
    {
        var result = DiscordAuthorization.CheckManagerAccess(
            null,
            GuildId,
            isAdministrator: true,
            [ManagerRoleId],
            ManagerRoleId);

        Assert.False(result.IsAuthorized);
        Assert.Equal("This command can only be used in the configured Discord server.", result.FailureMessage);
    }

    [Fact]
    public void CheckManagerAccessRejectsOtherGuildsEvenForAdministrators()
    {
        var result = DiscordAuthorization.CheckManagerAccess(
            GuildId + 1,
            GuildId,
            isAdministrator: true,
            [ManagerRoleId],
            ManagerRoleId);

        Assert.False(result.IsAuthorized);
        Assert.Equal("This command is not available in this Discord server.", result.FailureMessage);
    }

    [Fact]
    public void CheckManagerAccessAllowsConfiguredManagerRole()
    {
        var result = DiscordAuthorization.CheckManagerAccess(
            GuildId,
            GuildId,
            isAdministrator: false,
            [1, ManagerRoleId, 3],
            ManagerRoleId);

        Assert.True(result.IsAuthorized);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public void CheckManagerAccessAllowsDiscordAdministratorWithoutManagerRole()
    {
        var result = DiscordAuthorization.CheckManagerAccess(
            GuildId,
            GuildId,
            isAdministrator: true,
            [1, 2, 3],
            ManagerRoleId);

        Assert.True(result.IsAuthorized);
    }

    [Fact]
    public void CheckManagerAccessRejectsMembersWithoutTheConfiguredRole()
    {
        var result = DiscordAuthorization.CheckManagerAccess(
            GuildId,
            GuildId,
            isAdministrator: false,
            [1, 2, 3],
            ManagerRoleId);

        Assert.False(result.IsAuthorized);
        Assert.Equal(
            "You need the configured Steam manager role or Discord Administrator permission.",
            result.FailureMessage);
    }
}
