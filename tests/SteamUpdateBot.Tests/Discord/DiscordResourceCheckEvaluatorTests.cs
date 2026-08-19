using SteamUpdateBot.App.Discord;

namespace SteamUpdateBot.Tests.Discord;

public sealed class DiscordResourceCheckEvaluatorTests
{
    [Fact]
    public void MissingOrUnavailableGuildIsTransient()
    {
        var missing = DiscordResourceCheckEvaluator.Evaluate(new DiscordResourceCheck(
            GuildFound: false,
            GuildAvailable: false,
            RoleFound: false,
            ChannelFound: false,
            ChannelCanReceiveMessages: false,
            CanViewChannel: false,
            CanSendMessages: false,
            CanEmbedLinks: false));
        var unavailable = DiscordResourceCheckEvaluator.Evaluate(ReadyCheck() with { GuildAvailable = false });

        Assert.Equal(DiscordResourceCheckKind.Transient, missing.Kind);
        Assert.Equal(DiscordResourceCheckKind.Transient, unavailable.Kind);
    }

    [Fact]
    public void MissingRoleChannelOrPermissionsIsPermanent()
    {
        Assert.Equal(
            DiscordResourceCheckKind.Permanent,
            DiscordResourceCheckEvaluator.Evaluate(ReadyCheck() with { RoleFound = false }).Kind);
        Assert.Equal(
            DiscordResourceCheckKind.Permanent,
            DiscordResourceCheckEvaluator.Evaluate(ReadyCheck() with { ChannelFound = false }).Kind);
        Assert.Equal(
            DiscordResourceCheckKind.Permanent,
            DiscordResourceCheckEvaluator.Evaluate(ReadyCheck() with { ChannelCanReceiveMessages = false }).Kind);
        Assert.Equal(
            DiscordResourceCheckKind.Permanent,
            DiscordResourceCheckEvaluator.Evaluate(ReadyCheck() with { CanSendMessages = false }).Kind);
    }

    [Fact]
    public void CompleteResourcesAreReady()
    {
        var result = DiscordResourceCheckEvaluator.Evaluate(ReadyCheck());

        Assert.Equal(DiscordResourceCheckKind.Ready, result.Kind);
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(200, false)]
    public void PermanentLoginFailuresAreUnauthorizedOrForbidden(int statusCode, bool expected)
    {
        Assert.Equal(expected, DiscordHttpStatus.IsPermanentLoginFailure(statusCode));
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(401, false)]
    [InlineData(400, false)]
    public void TransientHttpStatusesAreRateLimitsAndServerErrors(int statusCode, bool expected)
    {
        Assert.Equal(expected, DiscordHttpStatus.IsTransient(statusCode));
    }

    private static DiscordResourceCheck ReadyCheck() => new(
        GuildFound: true,
        GuildAvailable: true,
        RoleFound: true,
        ChannelFound: true,
        ChannelCanReceiveMessages: true,
        CanViewChannel: true,
        CanSendMessages: true,
        CanEmbedLinks: true);
}
