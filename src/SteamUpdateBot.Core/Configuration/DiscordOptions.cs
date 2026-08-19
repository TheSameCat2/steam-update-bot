using System.ComponentModel.DataAnnotations;

namespace SteamUpdateBot.Core.Configuration;

public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    [Required]
    public string Token { get; init; } = string.Empty;

    [Range(1, ulong.MaxValue)]
    public ulong GuildId { get; init; }

    [Range(1, ulong.MaxValue)]
    public ulong AnnouncementChannelId { get; init; }

    [Range(1, ulong.MaxValue)]
    public ulong ManagerRoleId { get; init; }
}
