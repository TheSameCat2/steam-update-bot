using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SteamUpdateBot.App.Health;

namespace SteamUpdateBot.Tests.Health;

public sealed class HealthCheckJsonTests
{
    [Fact]
    public async Task WritesStatusDescriptionAndData()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["live"] = new HealthReportEntry(
                    HealthStatus.Unhealthy,
                    "The Discord gateway has been disconnected too long.",
                    TimeSpan.Zero,
                    null,
                    new Dictionary<string, object> { ["discordConnected"] = false }),
            },
            TimeSpan.Zero);

        await HealthCheckJson.WriteAsync(context, report);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal("Unhealthy", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "The Discord gateway has been disconnected too long.",
            document.RootElement.GetProperty("entries").GetProperty("live").GetProperty("description").GetString());
        Assert.False(
            document.RootElement.GetProperty("entries").GetProperty("live").GetProperty("data").GetProperty("discordConnected").GetBoolean());
    }
}
