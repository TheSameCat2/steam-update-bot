using System.ComponentModel.DataAnnotations;

namespace SteamUpdateBot.Core.Configuration;

public sealed class SteamOptions
{
    public const string SectionName = "Steam";

    [Range(1, 60)]
    public int PollIntervalMinutes { get; init; } = 5;

    [Range(1, 8)]
    public int MaxConcurrentPolls { get; init; } = 4;

    [Range(1, 50)]
    public int MaxGames { get; init; } = 50;

    [Range(1, 48)]
    public int OverlapHours { get; init; } = 24;

    [Range(5, 60)]
    public int RequestTimeoutSeconds { get; init; } = 15;
}
