using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Staleness;
using StreamDeckPilot.Infrastructure.Supervision;

namespace StreamDeckPilot.Tests.Api;

public sealed class HostedServiceOrderTests
{
    [Fact]
    public async Task Supervisor_IsRegisteredBeforeEveryRenderingService_SoItStopsLast()
    {
        // The host stops hosted services in reverse registration order. DeviceSupervisorService.StopAsync
        // releases the boards (#69), which is only safe once the services that render to them stopped.
        await using var factory = new StreamDeckApiFactory();
        var services = factory.Services.GetServices<IHostedService>().ToList();

        var supervisor = services.FindIndex(s => s is DeviceSupervisorService);
        var mqtt = services.FindIndex(s => s is MqttClientService);
        var staleness = services.FindIndex(s => s is StalenessMonitor);

        Assert.True(supervisor >= 0 && mqtt >= 0 && staleness >= 0, "all three hosted services are registered");
        Assert.True(supervisor < mqtt, "the supervisor must be registered before the MQTT client");
        Assert.True(supervisor < staleness, "the supervisor must be registered before the staleness monitor");
    }
}
