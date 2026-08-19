using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SteamUpdateBot.App.Health;

public static class HealthCheckJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = new Dictionary<string, object?>
        {
            ["status"] = report.Status.ToString(),
            ["entries"] = report.Entries.ToDictionary(
                static entry => entry.Key,
                static entry => new Dictionary<string, object?>
                {
                    ["status"] = entry.Value.Status.ToString(),
                    ["description"] = entry.Value.Description,
                    ["data"] = entry.Value.Data,
                }),
        };
        return context.Response.WriteAsJsonAsync(payload, SerializerOptions);
    }
}
