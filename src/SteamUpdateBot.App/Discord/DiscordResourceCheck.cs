namespace SteamUpdateBot.App.Discord;

public enum DiscordResourceCheckKind
{
    Ready,
    Transient,
    Permanent,
}

public readonly record struct DiscordResourceCheck(
    bool GuildFound,
    bool GuildAvailable,
    bool RoleFound,
    bool ChannelFound,
    bool ChannelCanReceiveMessages,
    bool CanViewChannel,
    bool CanSendMessages,
    bool CanEmbedLinks);

public sealed record DiscordResourceCheckResult(DiscordResourceCheckKind Kind, string Message);

public static class DiscordResourceCheckEvaluator
{
    public static DiscordResourceCheckResult Evaluate(DiscordResourceCheck check)
    {
        if (!check.GuildFound || !check.GuildAvailable)
        {
            return new DiscordResourceCheckResult(
                DiscordResourceCheckKind.Transient,
                "The configured Discord guild is unavailable.");
        }

        if (!check.RoleFound)
        {
            return new DiscordResourceCheckResult(
                DiscordResourceCheckKind.Permanent,
                "The configured Discord manager role does not exist in the configured guild.");
        }

        if (!check.ChannelFound)
        {
            return new DiscordResourceCheckResult(
                DiscordResourceCheckKind.Permanent,
                "The configured announcement channel does not exist in the configured guild.");
        }

        if (!check.ChannelCanReceiveMessages)
        {
            return new DiscordResourceCheckResult(
                DiscordResourceCheckKind.Permanent,
                "The configured announcement channel cannot receive messages.");
        }

        if (!check.CanViewChannel || !check.CanSendMessages || !check.CanEmbedLinks)
        {
            return new DiscordResourceCheckResult(
                DiscordResourceCheckKind.Permanent,
                "The Discord bot needs View Channel, Send Messages, and Embed Links permissions in the configured announcement channel.");
        }

        return new DiscordResourceCheckResult(DiscordResourceCheckKind.Ready, "The configured Discord resources are available.");
    }
}
