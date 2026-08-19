namespace SteamUpdateBot.Steam;

/// <summary>
/// Represents a failure to obtain a usable response from a Steam HTTP endpoint.
/// </summary>
public sealed class SteamSourceUnavailableException : Exception
{
    public SteamSourceUnavailableException(string message)
        : base(message)
    {
    }

    public SteamSourceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
