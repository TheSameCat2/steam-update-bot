using Microsoft.Extensions.Diagnostics.HealthChecks;
using SteamUpdateBot.App.Discord;
using SteamUpdateBot.App.Health;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Tests.Core;

namespace SteamUpdateBot.Tests.Health;

public sealed class BotLivenessHealthCheckTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConnectedGatewayIsLive()
    {
        var result = await CheckAsync(new FixedRuntimeState(true, null), TestNow);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("The process is live.", result.Description);
    }

    [Fact]
    public async Task BriefDisconnectStaysLive()
    {
        var result = await CheckAsync(
            new FixedRuntimeState(false, TestNow.AddMinutes(-1)),
            TestNow);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task LongDisconnectMakesLivenessUnhealthy()
    {
        var result = await CheckAsync(
            new FixedRuntimeState(false, TestNow.AddMinutes(-(BotLivenessHealthCheck.DisconnectGraceMinutes + 1))),
            TestNow);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("The Discord gateway has been disconnected too long.", result.Description);
    }

    [Fact]
    public void DisconnectTimestampIsPreservedUntilReconnect()
    {
        var time = new MutableTimeProvider(TestNow);
        var state = new DiscordRuntimeState(time);

        Assert.False(state.DiscordConnected);
        Assert.Equal(TestNow, state.DiscordDisconnectedSinceUtc);

        time.NowUtc = TestNow.AddMinutes(1);
        state.SetConnected(false);
        Assert.Equal(TestNow, state.DiscordDisconnectedSinceUtc);

        state.SetConnected(true);
        Assert.True(state.DiscordConnected);
        Assert.Null(state.DiscordDisconnectedSinceUtc);

        time.NowUtc = TestNow.AddMinutes(2);
        state.SetConnected(false);
        Assert.Equal(TestNow.AddMinutes(2), state.DiscordDisconnectedSinceUtc);
    }

    [Fact]
    public void ResetDisconnectedClockStartsANewGraceWindow()
    {
        var time = new MutableTimeProvider(TestNow);
        var state = new DiscordRuntimeState(time);

        time.NowUtc = TestNow.AddMinutes(3);
        state.ResetDisconnectedClock();

        Assert.False(state.DiscordConnected);
        Assert.Equal(TestNow.AddMinutes(3), state.DiscordDisconnectedSinceUtc);
    }

    private static Task<HealthCheckResult> CheckAsync(IBotRuntimeState runtimeState, DateTimeOffset now) =>
        new BotLivenessHealthCheck(runtimeState, new MutableTimeProvider(now))
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    private sealed class FixedRuntimeState(bool connected, DateTimeOffset? disconnectedSinceUtc) : IBotRuntimeState
    {
        public bool DiscordConnected { get; } = connected;

        public DateTimeOffset? DiscordDisconnectedSinceUtc { get; } = disconnectedSinceUtc;
    }
}
