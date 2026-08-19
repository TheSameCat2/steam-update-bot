namespace SteamUpdateBot.Core.Domain;

public sealed class PublisherTransientException : Exception
{
    public PublisherTransientException(string message)
        : base(message)
    {
    }

    public PublisherTransientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
