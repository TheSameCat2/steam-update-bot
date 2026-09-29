using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SteamUpdateBot.App.Discord;
using SteamUpdateBot.App.Health;
using SteamUpdateBot.Tests.Core;

namespace SteamUpdateBot.Tests.Discord;

public sealed class DiscordGatewayConnectionMonitorTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FastWatchInterval = TimeSpan.FromMilliseconds(40);

    [Fact]
    public async Task DisconnectThenResumeWithoutReadyStaysConnectedAndLive()
    {
        var time = new MutableTimeProvider(TestNow);
        var state = new DiscordRuntimeState(time);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var monitor = CreateMonitor(state, time, lifetime);

        state.SetConnected(true);
        time.NowUtc = TestNow.AddMinutes(1);
        monitor.MarkDisconnected();

        Assert.True(monitor.TryRestoreAfterResume(commandsRegistered: true, ReadyResources()));
        Assert.True(state.DiscordConnected);
        Assert.Null(state.DiscordDisconnectedSinceUtc);

        time.NowUtc = TestNow.AddMinutes(BotLivenessHealthCheck.DisconnectGraceMinutes + 2);
        var liveness = await CheckLivenessAsync(state, time);

        Assert.Equal(HealthStatus.Healthy, liveness.Status);
        await AssertWatchdogDoesNotStopAsync(monitor, lifetime);
    }

    [Fact]
    public async Task ProlongedDisconnectStopsTheProcessAndFailsLiveness()
    {
        var time = new MutableTimeProvider(TestNow);
        var state = new DiscordRuntimeState(time);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var monitor = CreateMonitor(state, time, lifetime);

        state.SetConnected(true);
        monitor.MarkDisconnected();
        time.NowUtc = TestNow.AddMinutes(BotLivenessHealthCheck.DisconnectGraceMinutes + 1);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            TestContext.Current.CancellationToken);
        await monitor.WatchAsync(linked.Token);

        var liveness = await CheckLivenessAsync(state, time);

        lifetime.Received(1).StopApplication();
        Assert.Equal(HealthStatus.Unhealthy, liveness.Status);
        Assert.Equal("The Discord gateway has been disconnected too long.", liveness.Description);
    }

    [Fact]
    public void ConnectedBeforeReadyDoesNotRestoreConnectedState()
    {
        var time = new MutableTimeProvider(TestNow);
        var state = new DiscordRuntimeState(time);
        var monitor = CreateMonitor(state, time);

        Assert.False(monitor.TryRestoreAfterResume(commandsRegistered: false, ReadyResources()));
        Assert.False(state.DiscordConnected);
        Assert.Equal(TestNow, state.DiscordDisconnectedSinceUtc);
    }

    [Fact]
    public void ResumeDoesNotRestoreWhenGuildChecksFail()
    {
        var time = new MutableTimeProvider(TestNow);
        var state = new DiscordRuntimeState(time);
        var monitor = CreateMonitor(state, time);

        state.SetConnected(true);
        monitor.MarkDisconnected();

        Assert.False(monitor.TryRestoreAfterResume(
            commandsRegistered: true,
            ReadyResources() with { GuildFound = false, GuildAvailable = false }));
        Assert.False(state.DiscordConnected);
        Assert.Equal(TestNow, state.DiscordDisconnectedSinceUtc);
    }

    private static DiscordGatewayConnectionMonitor CreateMonitor(
        DiscordRuntimeState state,
        TimeProvider time,
        IHostApplicationLifetime? lifetime = null) =>
        new(
            state,
            time,
            lifetime ?? Substitute.For<IHostApplicationLifetime>(),
            NullLogger.Instance,
            FastWatchInterval);

    private static Task<HealthCheckResult> CheckLivenessAsync(
        DiscordRuntimeState state,
        TimeProvider time) =>
        new BotLivenessHealthCheck(state, time)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    private static async Task AssertWatchdogDoesNotStopAsync(
        DiscordGatewayConnectionMonitor monitor,
        IHostApplicationLifetime lifetime)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var watchTask = monitor.WatchAsync(cts.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await watchTask);
        lifetime.DidNotReceive().StopApplication();
    }

    private static DiscordResourceCheck ReadyResources() => new(
        GuildFound: true,
        GuildAvailable: true,
        RoleFound: true,
        ChannelFound: true,
        ChannelCanReceiveMessages: true,
        CanViewChannel: true,
        CanSendMessages: true,
        CanEmbedLinks: true);
}
