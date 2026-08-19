namespace SteamUpdateBot.App.Discord;

public static class DiscordHttpStatus
{
    public static bool IsTransient(int statusCode) => statusCode is 429 or >= 500;

    public static bool IsPermanentLoginFailure(int statusCode) => statusCode is 401 or 403;
}
