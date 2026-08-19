using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SteamUpdateBot.App.Workers;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Domain;
using SteamUpdateBot.Core.Services;
using SteamUpdateBot.Tests.Core;

namespace SteamUpdateBot.Tests.Workers;

public sealed class OutboxDeliveryHostedServiceTests
{
    [Fact]
    public async Task SkipsDeliveryWhileDiscordIsDisconnected()
    {
        var delivery = Substitute.For<IOutboxDeliveryService>();
        var service = new OutboxDeliveryHostedService(
            delivery,
            new DisconnectedRuntimeState(),
            NullLogger<OutboxDeliveryHostedService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        await delivery.DidNotReceive().RunOnceAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunsDeliveryOnceDiscordIsConnected()
    {
        var delivery = Substitute.For<IOutboxDeliveryService>();
        delivery.RunOnceAsync(Arg.Any<CancellationToken>()).Returns(new DeliveryCycleResult(0, 0));
        var service = new OutboxDeliveryHostedService(
            delivery,
            new ConnectedRuntimeState(),
            NullLogger<OutboxDeliveryHostedService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        await delivery.Received().RunOnceAsync(Arg.Any<CancellationToken>());
    }
}
